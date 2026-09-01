using System.Runtime.Versioning;
using PunchIO.Buffers;
using PunchIO.Devices.Interop;
using Microsoft.Win32.SafeHandles;

namespace PunchIO.Devices;

/// <summary>
/// The Windows fast path: the same overlapped submission as the portable device,
/// on a handle opened with <c>FILE_FLAG_NO_BUFFERING</c>.
/// </summary>
/// <remarks>
/// <para>
/// The overlapped I/O is not what this buys. <see cref="FileOptions.Asynchronous"/>
/// already binds the handle to the thread pool's completion port and
/// <see cref="RandomAccess"/> already issues genuine overlapped reads on it. What
/// this adds is bypassing the cache manager, which on a sequential scan of a file
/// larger than memory is pure overhead: every block is copied kernel-to-user for
/// a cache entry nothing will read again, evicting someone else's working set to
/// make room.
/// </para>
/// <para>
/// The cost is alignment. Every request must have a sector-aligned offset, a
/// sector-multiple length, and a sector-aligned buffer address — all three are
/// enforced by the operating system, which is why slab allocation lives on the
/// device. Reads near end of file round their length up past the end, which is
/// legal, and clamp the result back to the file's true length so nothing above
/// this class ever sees the padding.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsBlockDevice : IBlockDevice
{
    /// <summary>
    /// <c>FILE_FLAG_NO_BUFFERING</c>. The runtime's options validation permits
    /// this bit specifically so it can be requested, and the handle is still
    /// created and completion-port bound by the runtime.
    /// </summary>
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    private readonly SafeFileHandle _handle;
    private readonly int _sectorSize;
    private long _length;

    private WindowsBlockDevice(SafeFileHandle handle, int sectorSize)
    {
        _handle = handle;
        _sectorSize = sectorSize;
        _length = RandomAccess.GetLength(handle);
    }

    /// <summary>Opens a file for unbuffered access.</summary>
    /// <param name="path">The file to open.</param>
    /// <param name="access">The access required.</param>
    /// <param name="share">The sharing mode.</param>
    /// <returns>An open device.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="PunchIoException">The file could not be opened.</exception>
    public static WindowsBlockDevice Open(string path, FileAccess access, FileShare share) =>
        Open(path, access == FileAccess.Read ? FileMode.Open : FileMode.OpenOrCreate, access, share);

    /// <summary>Opens a file for unbuffered access with an explicit creation mode.</summary>
    /// <param name="path">The file to open.</param>
    /// <param name="mode">How the file should be opened or created.</param>
    /// <param name="access">The access required.</param>
    /// <param name="share">The sharing mode.</param>
    /// <returns>An open device.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="PunchIoException">The file could not be opened.</exception>
    public static WindowsBlockDevice Open(
        string path, FileMode mode, FileAccess access, FileShare share)
    {
        ArgumentNullException.ThrowIfNull(path);

        int sectorSize = WindowsVolumeInfo.GetSectorSize(path);

        try
        {
            var handle = File.OpenHandle(
                path, mode, access, share,
                FileOptions.Asynchronous | FileOptions.SequentialScan | NoBuffering);

            return new WindowsBlockDevice(handle, sectorSize);
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
    public int Alignment => _sectorSize;

    /// <inheritdoc />
    public bool RequiresTailPadding => true;

    /// <inheritdoc />
    public IBlockSlab AllocateSlab(int blockCount, int blockSize) =>
        new AlignedNativeSlab(blockCount, blockSize, _sectorSize);

    /// <inheritdoc />
    /// <remarks>
    /// Fills the destination internally rather than returning a short count, so
    /// the pump never has to issue a fill-completion read at an unaligned offset
    /// — which the operating system would reject.
    /// </remarks>
    public async ValueTask<int> ReadAsync(
        Memory<byte> destination, long fileOffset, CancellationToken cancellationToken)
    {
        RequireAligned(fileOffset, nameof(fileOffset));
        RequireAligned(destination.Length, nameof(destination));

        if (fileOffset >= _length) return 0;

        long remaining = _length - fileOffset;

        // Rounding the request up past end of file is legal on an unbuffered
        // handle and returns the true remaining count; a request rounded *down*
        // would silently drop the file's final partial sector.
        int wanted = (int)Math.Min(destination.Length, RoundUpToSector(remaining));
        int total = 0;

        while (total < wanted)
        {
            int read = await RandomAccess
                .ReadAsync(_handle, destination[total..wanted], fileOffset + total, cancellationToken)
                .ConfigureAwait(false);

            if (read == 0) break;

            total += read;

            // Reaching the logical end of the file is the ordinary way a read
            // comes back short, and the count there is a partial sector by
            // definition. Nothing is left to request.
            if (fileOffset + total >= _length) break;

            if (total % _sectorSize != 0)
            {
                // Only now is a short count a problem: continuing would issue an
                // unaligned request. This should be unreachable on a local
                // volume, so say so plainly rather than letting the next call
                // fail with a bare IOException.
                throw new PunchIoException(
                    $"An unbuffered read returned {total} bytes short of end of file, which is " +
                    $"not a multiple of the {_sectorSize}-byte sector size, so the remainder " +
                    "cannot be requested.",
                    FileStatus.PermanentError);
            }
        }

        // Never report the padding read past the logical end of the file.
        return (int)Math.Min(total, remaining);
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> source, long fileOffset, CancellationToken cancellationToken)
    {
        RequireAligned(fileOffset, nameof(fileOffset));
        RequireAligned(source.Length, nameof(source));

        await RandomAccess.WriteAsync(_handle, source, fileOffset, cancellationToken)
            .ConfigureAwait(false);

        long end = fileOffset + source.Length;
        if (end > _length) _length = end;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>FILE_FLAG_NO_BUFFERING</c> bypasses the operating system's cache but
    /// not the drive's, so durability still needs an explicit flush.
    /// </remarks>
    public ValueTask FlushAsync(bool toDisk, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

    private void RequireAligned(long value, string name)
    {
        if (value % _sectorSize == 0) return;

        throw new ArgumentException(
            $"Unbuffered I/O requires {name} to be a multiple of the volume's " +
            $"{_sectorSize}-byte sector size; got {value}.",
            name);
    }

    private long RoundUpToSector(long value) =>
        (value + _sectorSize - 1) / _sectorSize * _sectorSize;

    /// <summary>Closes the file handle.</summary>
    public void Dispose() => _handle.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return default;
    }
}
