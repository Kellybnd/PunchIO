using PunchIO.Buffers;
using PunchIO.Devices.Interop;
using Microsoft.Win32.SafeHandles;

namespace PunchIO.Devices;

/// <summary>
/// The portable block device. Issues genuine overlapped reads and writes through
/// <see cref="RandomAccess"/> on a handle bound to the thread pool's completion
/// port, on every supported platform.
/// </summary>
public sealed class ManagedBlockDevice : IBlockDevice
{
    private readonly SafeFileHandle _handle;
    private long _length;

    private ManagedBlockDevice(SafeFileHandle handle)
    {
        _handle = handle;
        _length = RandomAccess.GetLength(handle);
    }

    /// <summary>Opens a file.</summary>
    /// <param name="path">The file to open.</param>
    /// <param name="access">The access required.</param>
    /// <param name="share">The sharing mode.</param>
    /// <returns>An open device.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="PunchIoException">The file could not be opened.</exception>
    public static ManagedBlockDevice Open(string path, FileAccess access, FileShare share) =>
        Open(path, access == FileAccess.Read ? FileMode.Open : FileMode.OpenOrCreate, access, share);

    /// <summary>Opens a file with an explicit creation mode.</summary>
    /// <param name="path">The file to open.</param>
    /// <param name="mode">How the file should be opened or created.</param>
    /// <param name="access">The access required.</param>
    /// <param name="share">The sharing mode.</param>
    /// <returns>An open device.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="PunchIoException">The file could not be opened.</exception>
    public static ManagedBlockDevice Open(
        string path, FileMode mode, FileAccess access, FileShare share)
    {
        ArgumentNullException.ThrowIfNull(path);

        try
        {
            var handle = File.OpenHandle(
                path, mode, access, share,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            return new ManagedBlockDevice(handle);
        }
        catch (FileNotFoundException ex)
        {
            throw new PunchIoException($"File not found: {path}", FileStatus.FileNotFound, ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new PunchIoException($"Directory not found for: {path}", FileStatus.FileNotFound, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new PunchIoException($"Access denied opening: {path}", FileStatus.AttributeMismatch, ex);
        }
    }

    /// <inheritdoc />
    public long Length => _length;

    /// <inheritdoc />
    public int Alignment => 1;

    /// <inheritdoc />
    public bool RequiresTailPadding => false;

    /// <inheritdoc />
    public IBlockSlab AllocateSlab(int blockCount, int blockSize) =>
        new PinnedArraySlab(blockCount, blockSize);

    /// <inheritdoc />
    /// <remarks>
    /// Forwards a single read with no fill loop. A short result is legitimate,
    /// and re-issuing for the remainder belongs to the pump, so that the pump's
    /// tests against a fake device exercise the same path production does.
    /// </remarks>
    public ValueTask<int> ReadAsync(
        Memory<byte> destination, long fileOffset, CancellationToken cancellationToken) =>
        RandomAccess.ReadAsync(_handle, destination, fileOffset, cancellationToken);

    /// <inheritdoc />
    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> source, long fileOffset, CancellationToken cancellationToken)
    {
        await RandomAccess.WriteAsync(_handle, source, fileOffset, cancellationToken)
            .ConfigureAwait(false);

        long end = fileOffset + source.Length;
        if (end > _length) _length = end;
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(bool toDisk, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Writes went straight to the handle, so there is nothing of ours to
        // flush; only the media-durability request needs a platform call.
        if (toDisk) NativeFileOps.FlushToDisk(_handle);

        return default;
    }

    /// <inheritdoc />
    public ValueTask SetLengthAsync(long length, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        NativeFileOps.SetLength(_handle, length);
        _length = length;

        return default;
    }

    /// <summary>Closes the file handle.</summary>
    public void Dispose() => _handle.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return default;
    }
}
