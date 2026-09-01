using System.Buffers.Binary;

namespace PunchIO.Cobol;

/// <summary>
/// The Micro Focus EXTFH operation codes this library recognises.
/// </summary>
/// <remarks>
/// Operation codes differ between COBOL runtimes and product generations. Like
/// <see cref="FcdLayout"/>, they are gathered in one table with no logic
/// attached, so matching a specific runtime is a change here and nowhere else.
/// Check these against your runtime's header when integrating.
/// </remarks>
public static class ExfhOpcodes
{
    /// <summary>Open for reading.</summary>
    public const ushort OpenInput = 0x0000;

    /// <summary>Open for writing, replacing any existing contents.</summary>
    public const ushort OpenOutput = 0x0001;

    /// <summary>Open for reading and writing.</summary>
    public const ushort OpenIo = 0x0002;

    /// <summary>Open for appending.</summary>
    public const ushort OpenExtend = 0x0003;

    /// <summary>Close the file.</summary>
    public const ushort Close = 0x0080;

    /// <summary>Position for subsequent sequential reads.</summary>
    public const ushort Start = 0x00F5;

    /// <summary>Read the record identified by the relative key.</summary>
    public const ushort ReadRandom = 0x00F6;

    /// <summary>Append a record, or write one at a given record number.</summary>
    public const ushort Write = 0x00F7;

    /// <summary>Replace a record.</summary>
    public const ushort Rewrite = 0x00F8;

    /// <summary>Delete a record.</summary>
    public const ushort Delete = 0x00F9;

    /// <summary>Read the next record in sequence.</summary>
    public const ushort ReadNext = 0x00FA;

    /// <summary>Release any record lock.</summary>
    public const ushort Unlock = 0x00FD;

    /// <summary>Commit a transaction.</summary>
    public const ushort Commit = 0x00E0;

    /// <summary>Roll back a transaction.</summary>
    public const ushort Rollback = 0x00E1;

    /// <summary>Translates an operation code into a dialect-neutral operation.</summary>
    /// <param name="opcode">The two-byte operation code, big-endian.</param>
    /// <returns>
    /// The operation, or <see cref="FileOperation.Unknown"/> when unrecognised.
    /// </returns>
    public static FileOperation ToOperation(ushort opcode) => opcode switch
    {
        OpenInput => FileOperation.OpenInput,
        OpenOutput => FileOperation.OpenOutput,
        OpenIo => FileOperation.OpenIo,
        OpenExtend => FileOperation.OpenExtend,
        Close => FileOperation.Close,
        Start => FileOperation.Start,
        ReadRandom => FileOperation.ReadRandom,
        Write => FileOperation.Write,
        Rewrite => FileOperation.Rewrite,
        Delete => FileOperation.Delete,
        ReadNext => FileOperation.ReadNext,
        Unlock => FileOperation.Unlock,
        Commit => FileOperation.Commit,
        Rollback => FileOperation.Rollback,
        _ => FileOperation.Unknown,
    };
}

/// <summary>
/// The external file handler entry point, callable directly from managed COBOL.
/// </summary>
/// <remarks>
/// <para>
/// A COBOL program calls this with an operation code and a file control
/// description; everything else — which file, in what format, how deep to
/// pipeline — comes from the control block and from configuration.
/// </para>
/// <para>
/// <strong>This method never throws.</strong> Every failure becomes a COBOL file
/// status written into the control block. The native host wraps this same class,
/// and an exception escaping into a native COBOL runtime terminates the process
/// rather than being caught anywhere.
/// </para>
/// </remarks>
public sealed class Exfh : IDisposable
{
    private readonly ExfhFileHandler _handler;
    private readonly FileOperationRequest _request = new();
    private readonly FileOperationResult _result = new();

    /// <summary>Initializes the entry point.</summary>
    /// <param name="resolver">Resolves the format of each file by name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resolver"/> is <see langword="null"/>.</exception>
    public Exfh(IExfhProfileResolver resolver) => _handler = new ExfhFileHandler(resolver);

    /// <summary>The number of files currently open.</summary>
    public int OpenFileCount => _handler.OpenFileCount;

    /// <summary>Executes one COBOL file operation.</summary>
    /// <param name="opcode">The two-byte operation code, big-endian.</param>
    /// <param name="fcd">The file control description, updated in place.</param>
    /// <param name="recordArea">
    /// The program's record area: written into on a read, read from on a write.
    /// </param>
    /// <returns>
    /// Zero on success, non-zero otherwise. The authoritative result is the file
    /// status written into <paramref name="fcd"/>.
    /// </returns>
    public int Execute(ReadOnlySpan<byte> opcode, Span<byte> fcd, Span<byte> recordArea)
    {
        try
        {
            if (opcode.Length < 2) return Fail(fcd, FileStatus.AttributeMismatch);

            var view = new FcdView(fcd);

            if (!view.IsUsable) return Fail(fcd, FileStatus.AttributeMismatch);

            _request.Reset();
            _request.Operation = ExfhOpcodes.ToOperation(BinaryPrimitives.ReadUInt16BigEndian(opcode));
            _request.FileName = view.FileName;
            _request.HandleId = view.HandleId;
            _request.RecordNumber = view.RelativeKey;
            _request.RecordLength = view.CurrentRecordLength;

            _handler.Execute(_request, recordArea, _result);

            view.SetStatus(_result.Status);

            if (_result.HandleId != 0) view.HandleId = _result.HandleId;
            if (_result.RecordLength != 0) view.CurrentRecordLength = _result.RecordLength;
            if (_result.RecordNumber != 0) view.RelativeKey = _result.RecordNumber;

            return _result.Status == FileStatus.Ok ? 0 : 1;
        }
        catch (Exception)
        {
            // The last managed frame before a native COBOL runtime. Nothing above
            // can handle a .NET exception, so nothing is allowed to escape.
            return Fail(fcd, FileStatus.PermanentError);
        }
    }

    private static int Fail(Span<byte> fcd, FileStatus status)
    {
        // Written directly rather than through FcdView: the block may be too
        // short or too malformed for the view to be constructed at all, and a
        // status is more useful than silence.
        if (fcd.Length >= 2)
        {
            fcd[0] = (byte)status.Code[0];
            fcd[1] = (byte)status.Code[1];
        }

        return 1;
    }

    /// <summary>Closes every file this entry point has open.</summary>
    public void Dispose() => _handler.Dispose();
}
