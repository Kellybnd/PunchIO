using System.Runtime.InteropServices;

namespace PunchIO.Buffers;

/// <summary>
/// A slab backed by aligned unmanaged memory. Required by the Windows unbuffered
/// device, which rejects reads and writes whose buffer address is not a multiple
/// of the volume's sector size.
/// </summary>
public sealed unsafe class AlignedNativeSlab : IBlockSlab
{
    private readonly PointerMemoryManager _manager;
    private byte* _pointer;

    /// <summary>Allocates the slab.</summary>
    /// <param name="blockCount">The number of blocks; must be positive.</param>
    /// <param name="blockSize">
    /// The size of each block in bytes; must be positive and a multiple of
    /// <paramref name="alignment"/> so that every block start is aligned, not
    /// merely the first.
    /// </param>
    /// <param name="alignment">The required address alignment, in bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="blockSize"/> is not a multiple of <paramref name="alignment"/>.
    /// </exception>
    public AlignedNativeSlab(int blockCount, int blockSize, int alignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);

        if (blockSize % alignment != 0)
        {
            throw new ArgumentException(
                $"Block size {blockSize} must be a multiple of the {alignment}-byte alignment, " +
                "otherwise blocks after the first would start unaligned.",
                nameof(blockSize));
        }

        BlockCount = blockCount;
        BlockSize = blockSize;

        nuint total = (nuint)blockCount * (nuint)blockSize;
        _pointer = (byte*)NativeMemory.AlignedAlloc(total, (nuint)alignment);
        _manager = new PointerMemoryManager(_pointer, blockCount * blockSize);
    }

    /// <inheritdoc />
    public int BlockCount { get; }

    /// <inheritdoc />
    public int BlockSize { get; }

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">The slab has been disposed.</exception>
    public Memory<byte> Block(int index)
    {
        ObjectDisposedException.ThrowIf(_pointer is null, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, BlockCount);

        return _manager.Memory.Slice(index * BlockSize, BlockSize);
    }

    /// <summary>Frees the unmanaged allocation. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_pointer is null) return;

        NativeMemory.AlignedFree(_pointer);
        _pointer = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>Frees the allocation if <see cref="Dispose"/> was never called.</summary>
    ~AlignedNativeSlab() => Dispose();
}
