using System.Buffers;

namespace PunchIO.Buffers;

/// <summary>
/// Presents a region of unmanaged memory as <see cref="Memory{T}"/>, so native
/// and managed slabs are interchangeable above the device layer.
/// </summary>
internal sealed unsafe class PointerMemoryManager(byte* pointer, int length) : MemoryManager<byte>
{
    public override Span<byte> GetSpan() => new(pointer, length);

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(elementIndex, length);

        return new MemoryHandle(pointer + elementIndex);
    }

    // The memory is unmanaged and permanently fixed; there is nothing to release.
    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
    }
}
