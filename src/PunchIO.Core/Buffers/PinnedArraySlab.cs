namespace PunchIO.Buffers;

/// <summary>
/// A slab backed by a single pinned managed array. Used by the portable block
/// device, where address stability matters but alignment does not.
/// </summary>
public sealed class PinnedArraySlab : IBlockSlab
{
    private readonly byte[] _buffer;

    /// <summary>Allocates the slab.</summary>
    /// <param name="blockCount">The number of blocks; must be positive.</param>
    /// <param name="blockSize">The size of each block in bytes; must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    public PinnedArraySlab(int blockCount, int blockSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);

        BlockCount = blockCount;
        BlockSize = blockSize;

        // Pinned so the kernel can write into it while I/O is outstanding.
        _buffer = GC.AllocateArray<byte>(blockCount * blockSize, pinned: true);
    }

    /// <inheritdoc />
    public int BlockCount { get; }

    /// <inheritdoc />
    public int BlockSize { get; }

    /// <inheritdoc />
    public Memory<byte> Block(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, BlockCount);

        return _buffer.AsMemory(index * BlockSize, BlockSize);
    }

    /// <summary>Releases the slab. The pinned array becomes collectable.</summary>
    public void Dispose()
    {
    }
}
