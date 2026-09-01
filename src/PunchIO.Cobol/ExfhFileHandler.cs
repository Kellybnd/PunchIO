using PunchIO.Files;
using PunchIO.Readers;
using PunchIO.Writers;

namespace PunchIO.Cobol;

/// <summary>Resolves the format of a file the COBOL program named.</summary>
public interface IExfhProfileResolver
{
    /// <summary>Finds the profile for a file.</summary>
    /// <param name="fileName">The name the COBOL program used.</param>
    /// <returns>The profile, or <see langword="null"/> when none is configured.</returns>
    FileProfile? Resolve(string fileName);
}

/// <summary>
/// Executes normalised file operations against PunchIO, holding open files in a
/// table keyed by an integer the COBOL program carries in its control block.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The synchronous bridge does not cost the asynchronous advantage.</strong>
/// A COBOL <c>READ</c> must return a record before it returns, so this blocks.
/// But the throughput comes from readahead depth, not from the caller being
/// asynchronous: with several blocks already in flight, a read almost always
/// finds its record in a filled buffer and completes without ever suspending.
/// Blocking happens only when the program has genuinely outrun the device, which
/// is when it should.
/// </para>
/// <para>
/// The file's format comes from a configured profile resolved by name, not from
/// the control block. A file's record format is a deployment fact that belongs
/// in configuration, and resolving it by name keeps the handler independent of
/// control-block fields whose encoding varies between runtimes.
/// </para>
/// </remarks>
public sealed class ExfhFileHandler : IDisposable
{
    private readonly IExfhProfileResolver _resolver;
    private readonly Dictionary<int, OpenFile> _files = [];
    private int _nextHandle = 1;

    /// <summary>Initializes the handler.</summary>
    /// <param name="resolver">Resolves the format of each file by name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resolver"/> is <see langword="null"/>.</exception>
    public ExfhFileHandler(IExfhProfileResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        _resolver = resolver;
    }

    /// <summary>The number of files currently open.</summary>
    public int OpenFileCount => _files.Count;

    /// <summary>Executes one operation.</summary>
    /// <param name="request">What the COBOL program asked for.</param>
    /// <param name="recordArea">
    /// The program's record area: written into on a read, read from on a write.
    /// </param>
    /// <param name="result">What to write back into the control block.</param>
    /// <remarks>
    /// Never throws. Every failure becomes a COBOL file status, because this is
    /// the last managed frame before a native COBOL runtime and an escaping
    /// exception there terminates the process.
    /// </remarks>
    public void Execute(
        FileOperationRequest request, Span<byte> recordArea, FileOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        result.Reset();

        try
        {
            Dispatch(request, recordArea, result);
        }
        catch (PunchIoException ex)
        {
            result.Status = ex.Status;
        }
        catch (Exception)
        {
            // Anything unmapped is a permanent I/O error rather than an escape
            // route. There is no caller above this that can handle a .NET
            // exception.
            result.Status = FileStatus.PermanentError;
        }
    }

    private void Dispatch(
        FileOperationRequest request, Span<byte> recordArea, FileOperationResult result)
    {
        switch (request.Operation)
        {
            case FileOperation.OpenInput:
            case FileOperation.OpenOutput:
            case FileOperation.OpenIo:
            case FileOperation.OpenExtend:
                Open(request, result);
                return;

            case FileOperation.Close:
                Close(request, result);
                return;

            case FileOperation.ReadNext:
                ReadNext(request, recordArea, result);
                return;

            case FileOperation.ReadRandom:
                ReadRandom(request, recordArea, result);
                return;

            case FileOperation.Write:
                Write(request, recordArea, result);
                return;

            case FileOperation.Rewrite:
                Rewrite(request, recordArea, result);
                return;

            case FileOperation.Delete:
                Delete(request, result);
                return;

            case FileOperation.Start:
                Start(request, result);
                return;

            // Accepted and ignored: this library holds no locks and runs no
            // transactions, so reporting failure would be wrong.
            case FileOperation.Unlock:
            case FileOperation.Commit:
            case FileOperation.Rollback:
                result.Status = FileStatus.Ok;
                return;

            default:
                result.Status = FileStatus.AttributeMismatch;
                return;
        }
    }

    private void Open(FileOperationRequest request, FileOperationResult result)
    {
        var profile = _resolver.Resolve(request.FileName)
            ?? throw new FileProfileException(
                request.FileName, null,
                "no profile is configured for this file, so its record format is unknown.");

        var file = OpenFile.Create(request.Operation, request.FileName, profile);

        int handle = _nextHandle++;
        _files[handle] = file;

        result.HandleId = handle;
        result.Status = FileStatus.Ok;
    }

    private void Close(FileOperationRequest request, FileOperationResult result)
    {
        if (!_files.Remove(request.HandleId, out var file))
        {
            result.Status = FileStatus.AttributeMismatch;
            return;
        }

        file.Dispose();
        result.Status = FileStatus.Ok;
    }

    private void ReadNext(
        FileOperationRequest request, Span<byte> recordArea, FileOperationResult result)
    {
        var file = Require(request.HandleId);

        if (file.Reader is null)
        {
            result.Status = FileStatus.AttributeMismatch;
            return;
        }

        // Await synchronously. In the common case the record is already in a
        // filled buffer and this never suspends.
        if (!Block(file.Reader.MoveNextAsync()))
        {
            result.Status = FileStatus.EndOfFile;
            return;
        }

        result.RecordLength = Deliver(file.Reader.Current.Span, recordArea);
        result.RecordNumber = file.Reader.RecordNumber;
        result.Status = FileStatus.Ok;
    }

    private void ReadRandom(
        FileOperationRequest request, Span<byte> recordArea, FileOperationResult result)
    {
        var file = Require(request.HandleId);

        if (file.Relative is null)
        {
            result.Status = FileStatus.AttributeMismatch;
            return;
        }

        var buffer = file.RentRecordBuffer();

        if (!Block(file.Relative.TryReadAsync(request.RecordNumber, buffer)))
        {
            result.Status = FileStatus.RecordNotFound;
            return;
        }

        result.RecordLength = Deliver(buffer.Span, recordArea);
        result.RecordNumber = request.RecordNumber;
        result.Status = FileStatus.Ok;
    }

    private void Write(
        FileOperationRequest request, Span<byte> recordArea, FileOperationResult result)
    {
        var file = Require(request.HandleId);
        var record = Presented(request, recordArea);

        if (file.Relative is not null)
        {
            Block(file.Relative.WriteAsync(request.RecordNumber, file.Stage(record)));
            result.Status = FileStatus.Ok;
            return;
        }

        if (file.Writer is null)
        {
            result.Status = FileStatus.AttributeMismatch;
            return;
        }

        Block(file.Writer.WriteAsync(file.Stage(record)));
        result.RecordNumber = file.Writer.RecordNumber;
        result.Status = FileStatus.Ok;
    }

    private void Rewrite(
        FileOperationRequest request, Span<byte> recordArea, FileOperationResult result)
    {
        var file = Require(request.HandleId);

        if (file.Relative is null)
        {
            // Rewriting a sequential record in place would mean overwriting a
            // record whose length may differ from the one being replaced.
            result.Status = FileStatus.AttributeMismatch;
            return;
        }

        var record = Presented(request, recordArea);

        Block(file.Relative.RewriteAsync(request.RecordNumber, file.Stage(record)));
        result.Status = FileStatus.Ok;
    }

    private void Delete(FileOperationRequest request, FileOperationResult result)
    {
        var file = Require(request.HandleId);

        if (file.Relative is null)
        {
            result.Status = FileStatus.AttributeMismatch;
            return;
        }

        result.Status = Block(file.Relative.DeleteAsync(request.RecordNumber))
            ? FileStatus.Ok
            : FileStatus.RecordNotFound;
    }

    private void Start(FileOperationRequest request, FileOperationResult result)
    {
        var file = Require(request.HandleId);

        // Positioning a sequential reader means reopening it, which this handler
        // does not do implicitly; a relative file needs no positioning because
        // every read names its record.
        result.Status = file.Relative is null
            ? FileStatus.AttributeMismatch
            : FileStatus.Ok;
    }

    private OpenFile Require(int handleId) =>
        _files.TryGetValue(handleId, out var file)
            ? file
            : throw new PunchIoException(
                $"File handle {handleId} is not open.", FileStatus.AttributeMismatch);

    /// <summary>Copies a record into the program's record area, space-padding the remainder.</summary>
    private static int Deliver(ReadOnlySpan<byte> record, Span<byte> recordArea)
    {
        int copied = Math.Min(record.Length, recordArea.Length);

        record[..copied].CopyTo(recordArea);

        // COBOL record areas are blank-filled, not left holding the previous
        // record's tail.
        if (copied < recordArea.Length) recordArea[copied..].Fill(0x20);

        return copied;
    }

    private static ReadOnlySpan<byte> Presented(FileOperationRequest request, Span<byte> recordArea)
    {
        int length = request.RecordLength > 0
            ? Math.Min(request.RecordLength, recordArea.Length)
            : recordArea.Length;

        return recordArea[..length];
    }

    /// <summary>
    /// Waits for an operation that has almost always already completed.
    /// </summary>
    /// <remarks>
    /// The fast path matters: when the pump has run ahead, the value task is
    /// already complete and this costs a branch rather than a thread-pool
    /// round trip.
    /// </remarks>
    private static void Block(ValueTask task)
    {
        if (task.IsCompletedSuccessfully) return;

        task.AsTask().GetAwaiter().GetResult();
    }

    private static T Block<T>(ValueTask<T> task) =>
        task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();

    /// <summary>Closes every open file.</summary>
    public void Dispose()
    {
        foreach (var file in _files.Values)
            file.Dispose();

        _files.Clear();
    }

    /// <summary>One entry in the handle table.</summary>
    private sealed class OpenFile
    {
        private byte[] _staging = [];

        private OpenFile(FileProfile profile) => Profile = profile;

        public FileProfile Profile { get; }

        public IRecordReader? Reader { get; private init; }

        public IRecordWriter? Writer { get; private init; }

        public RelativeFile? Relative { get; private init; }

        public static OpenFile Create(FileOperation operation, string path, FileProfile profile)
        {
            bool reading = operation is FileOperation.OpenInput;

            return reading
                ? new OpenFile(profile) { Reader = profile.OpenRead(path) }
                : new OpenFile(profile) { Writer = profile.CreateWrite(path) };
        }

        /// <summary>
        /// Copies a record out of the caller's record area so it can outlive the
        /// span across an await.
        /// </summary>
        public ReadOnlyMemory<byte> Stage(ReadOnlySpan<byte> record)
        {
            if (_staging.Length < record.Length)
                _staging = new byte[Math.Max(record.Length, 4096)];

            record.CopyTo(_staging);

            return _staging.AsMemory(0, record.Length);
        }

        public Memory<byte> RentRecordBuffer()
        {
            int length = Math.Max(Profile.RecordLength, 1);

            if (_staging.Length < length) _staging = new byte[Math.Max(length, 4096)];

            return _staging.AsMemory(0, length);
        }

        public void Dispose()
        {
            Reader?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Writer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Relative?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
