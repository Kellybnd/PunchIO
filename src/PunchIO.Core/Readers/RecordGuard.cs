#if DEBUG
using System.Buffers;

namespace PunchIO.Readers;

/// <summary>
/// Wraps a record so that touching it after the reader has moved on fails loudly
/// instead of silently returning another record's bytes.
/// </summary>
/// <remarks>
/// Debug builds only. Records are slices of pooled I/O buffers that the pump
/// reuses, so retaining one past the next <c>MoveNextAsync</c> is a caller bug
/// that would otherwise surface as inexplicable data corruption in production.
/// </remarks>
internal sealed class RecordGuard(Memory<byte> target) : MemoryManager<byte>
{
    private bool _valid = true;

    public void Invalidate() => _valid = false;

    public override Span<byte> GetSpan()
    {
        if (!_valid)
        {
            throw new InvalidOperationException(
                "This record's memory was only valid until the next MoveNextAsync call. " +
                "Copy the bytes if you need to retain them beyond the current iteration.");
        }

        return target.Span;
    }

    public override MemoryHandle Pin(int elementIndex = 0) => target[elementIndex..].Pin();

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
    }
}
#endif
