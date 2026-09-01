using PunchIO.Buffers;

namespace PunchIO.Devices;

/// <summary>
/// The storage seam beneath the block pump: opens a file, allocates buffers for
/// it, and moves blocks to and from explicit offsets.
/// </summary>
/// <remarks>
/// Implementations hide every platform-specific constraint. Nothing above this
/// interface knows about sector alignment or unbuffered I/O.
/// </remarks>
public interface IBlockDevice : IAsyncDisposable, IDisposable
{
    /// <summary>The file's current logical length in bytes.</summary>
    long Length { get; }

    /// <summary>
    /// The byte boundary that offsets, lengths, and buffer addresses must be
    /// multiples of. <c>1</c> when the device imposes no alignment.
    /// </summary>
    int Alignment { get; }

    /// <summary>
    /// <see langword="true"/> when a final short block must be padded up to
    /// <see cref="Alignment"/> on write and the file then truncated to its true
    /// length with <see cref="SetLengthAsync"/>.
    /// </summary>
    bool RequiresTailPadding { get; }

    /// <summary>Allocates buffers meeting this device's alignment requirement.</summary>
    /// <param name="blockCount">The number of blocks.</param>
    /// <param name="blockSize">The size of each block in bytes.</param>
    /// <returns>The allocated slab.</returns>
    IBlockSlab AllocateSlab(int blockCount, int blockSize);

    /// <summary>Reads the bytes available at <paramref name="fileOffset"/>.</summary>
    /// <param name="destination">The buffer to fill.</param>
    /// <param name="fileOffset">The absolute file offset to read from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// The number of bytes read, never exceeding the logical bytes remaining at
    /// <paramref name="fileOffset"/>, and <c>0</c> only at true end of file. A
    /// short result before end of file is legal and the caller re-issues for the
    /// remainder.
    /// </returns>
    ValueTask<int> ReadAsync(
        Memory<byte> destination, long fileOffset, CancellationToken cancellationToken);

    /// <summary>Writes bytes at <paramref name="fileOffset"/>.</summary>
    /// <param name="source">The bytes to write.</param>
    /// <param name="fileOffset">The absolute file offset to write at.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the write has been accepted.</returns>
    ValueTask WriteAsync(
        ReadOnlyMemory<byte> source, long fileOffset, CancellationToken cancellationToken);

    /// <summary>Flushes buffered data.</summary>
    /// <param name="toDisk">
    /// When <see langword="true"/>, forces data to stable media rather than
    /// merely handing it to the operating system.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the flush has finished.</returns>
    ValueTask FlushAsync(bool toDisk, CancellationToken cancellationToken);

    /// <summary>Sets the file's logical length.</summary>
    /// <param name="length">The new length in bytes.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the length has been set.</returns>
    ValueTask SetLengthAsync(long length, CancellationToken cancellationToken);
}
