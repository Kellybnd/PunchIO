namespace PunchIO.Buffers;

/// <summary>
/// A fixed set of equally sized I/O buffers backed by one allocation whose
/// address is stable for the lifetime of the slab.
/// </summary>
/// <remarks>
/// Allocated by the block device rather than by the pump, because buffer
/// alignment is a device requirement: an unbuffered Windows read rejects a
/// buffer whose address is not sector-aligned, so a pooled array cannot serve.
/// </remarks>
public interface IBlockSlab : IDisposable
{
    /// <summary>The number of blocks in the slab.</summary>
    int BlockCount { get; }

    /// <summary>The size of each block, in bytes.</summary>
    int BlockSize { get; }

    /// <summary>Returns the block at <paramref name="index"/>.</summary>
    /// <param name="index">A zero-based block index.</param>
    /// <returns>Memory covering exactly one block.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> is outside the slab.
    /// </exception>
    Memory<byte> Block(int index);
}
