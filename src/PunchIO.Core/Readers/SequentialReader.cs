using System.Runtime.CompilerServices;
using PunchIO.Framing;
using PunchIO.Pump;

#if DEBUG
using System.Runtime.InteropServices;
#endif

namespace PunchIO.Readers;

/// <summary>
/// Reads records from a file by driving a <see cref="BlockSource"/> through a
/// record framer, joining records that straddle block boundaries.
/// </summary>
/// <typeparam name="TFramer">
/// The framer to use. Constrained to a struct so the framing call is inlined
/// into the read loop rather than dispatched once per record.
/// </typeparam>
/// <remarks>
/// <para>
/// <strong>A record returned by this reader is valid only until the next call to
/// <see cref="MoveNextAsync"/>.</strong> Records are slices of the pump's
/// buffers, which are reused; a caller that needs to retain a record must copy
/// it. Debug builds detect a stale access and throw.
/// </para>
/// <para>
/// When a record spans a block boundary its bytes are copied into a stitch
/// buffer and joined with the following block. That costs one copy per block
/// boundary, not per record, and the buffer grows only as far as
/// <see cref="MaxRecordLength"/> allows — so a corrupt length prefix fails
/// cleanly instead of attempting an enormous allocation.
/// </para>
/// </remarks>
public sealed class SequentialReader<TFramer> : IRecordReader
    where TFramer : struct, IRecordFramer
{
    private readonly BlockSource _source;
    private readonly TFramer _framer;
    private readonly int _maxRecordLength;

    private ReadOnlyMemory<byte> _current;
    private long _blockOffset;
    private int _position;

    private byte[] _stitch = [];
    private int _stitchLength;
    private long _stitchFileOffset;
    private long _recordOffset;
    private long _recordNumber;
    private bool _final;
    private bool _completed;
    private bool _disposed;

#if DEBUG
    private RecordGuard? _guard;
#endif

    private SequentialReader(BlockSource source, TFramer framer, int maxRecordLength)
    {
        _source = source;
        _framer = framer;
        _maxRecordLength = maxRecordLength;
    }

    /// <summary>Creates a reader over a block pump.</summary>
    /// <param name="source">The pump to read. The reader takes ownership and disposes it.</param>
    /// <param name="framer">The framer that splits blocks into records.</param>
    /// <param name="maxRecordLength">
    /// The largest record the reader will accept, in bytes. Framing overhead
    /// counts toward the limit, since it is the number of bytes the reader is
    /// willing to accumulate for one record.
    /// </param>
    /// <returns>A reader positioned before the first record.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxRecordLength"/> is not positive.
    /// </exception>
    public static SequentialReader<TFramer> Create(
        BlockSource source, TFramer framer, int maxRecordLength)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecordLength);

        return new SequentialReader<TFramer>(source, framer, maxRecordLength);
    }

    /// <summary>
    /// The record most recently read. Valid only until the next call to
    /// <see cref="MoveNextAsync"/>.
    /// </summary>
    public ReadOnlyMemory<byte> Current { get; private set; }

    /// <summary>The one-based ordinal of <see cref="Current"/> within the file.</summary>
    public long RecordNumber => _recordNumber;

    /// <summary>
    /// The absolute file offset at which <see cref="Current"/> begins, including
    /// any framing bytes that precede the record body.
    /// </summary>
    public long RecordOffset => _recordOffset;

    /// <summary>The largest record this reader will accept, in bytes.</summary>
    public int MaxRecordLength => _maxRecordLength;

    /// <summary>Advances to the next record.</summary>
    /// <param name="cancellationToken">Cancels before the next block is awaited.</param>
    /// <returns><see langword="true"/> if a record was read; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ObjectDisposedException">The reader has been disposed.</exception>
    /// <exception cref="RecordFormatException">The bytes do not form a valid record.</exception>
    /// <exception cref="RecordTooLargeException">
    /// A record exceeded <see cref="MaxRecordLength"/>.
    /// </exception>
    public async ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_completed) return false;

        while (true)
        {
            // Nothing stitched and the block is used up: pull the next one.
            if (_stitchLength == 0 && _position >= _current.Length)
            {
                if (_final)
                {
                    Complete();
                    return false;
                }

                if (!await AdvanceBlockAsync(cancellationToken).ConfigureAwait(false))
                    _final = true;

                continue;
            }

            FrameStatus status = _stitchLength > 0 ? FrameFromStitch() : FrameFromBlock();

            switch (status)
            {
                case FrameStatus.Ok:
                    return true;

                case FrameStatus.EndOfData:
                    Complete();
                    return false;

                case FrameStatus.Invalid:
                    throw new RecordFormatException(
                        "The bytes here do not form a valid record for the configured format",
                        _recordOffset);

                default:
                    // NeedMoreData. Carry the unconsumed tail into the stitch
                    // buffer and join what follows onto it.
                    if (_stitchLength == 0) BeginStitch();

                    // Take more of the block already in hand before pulling
                    // another. Swallowing whole blocks would make the stitch
                    // buffer's size a function of the block size rather than the
                    // record size, which would break any configuration whose
                    // MaxRecordLength is smaller than its BlockSize.
                    if (ExtendStitchFromCurrentBlock()) continue;

                    if (await AdvanceBlockAsync(cancellationToken).ConfigureAwait(false))
                        ExtendStitchFromCurrentBlock();
                    else
                        _final = true;

                    continue;
            }
        }
    }

    /// <summary>Enumerates every record in the file.</summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>
    /// A sequence of records, each valid only until the enumerator advances.
    /// </returns>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await MoveNextAsync(cancellationToken).ConfigureAwait(false))
            yield return Current;
    }

    private FrameStatus FrameFromBlock()
    {
        _recordOffset = _blockOffset + _position;

        var status = _framer.TryFrame(
            _current.Span[_position..], _final,
            out int consumed, out int recordStart, out int recordLength);

        if (status != FrameStatus.Ok) return status;

        CheckRecordLength(recordLength);
        SetCurrent(_current.Slice(_position + recordStart, recordLength));

        _position += consumed;
        _recordNumber++;

        return FrameStatus.Ok;
    }

    private FrameStatus FrameFromStitch()
    {
        _recordOffset = _stitchFileOffset;

        var status = _framer.TryFrame(
            _stitch.AsSpan(0, _stitchLength), _final,
            out int consumed, out int recordStart, out int recordLength);

        if (status != FrameStatus.Ok) return status;

        CheckRecordLength(recordLength);
        SetCurrent(_stitch.AsMemory(recordStart, recordLength));

        // Map back into the current block. Stitch offset zero sits at
        // (_position - _stitchLength) within it -- a negative index once the
        // record began in an earlier block -- so this resolves correctly however
        // many blocks the record spanned, and equally when end of file arrived
        // before any further block did.
        _position = _position - _stitchLength + consumed;
        _stitchLength = 0;
        _recordNumber++;

        return FrameStatus.Ok;
    }

    private async ValueTask<bool> AdvanceBlockAsync(CancellationToken cancellationToken)
    {
        var block = await _source.NextBlockAsync(cancellationToken).ConfigureAwait(false);

        if (block.IsEmpty) return false;

        _current = block;
        _blockOffset = _source.BlockOffset;
        _position = 0;

        return true;
    }

    private void BeginStitch()
    {
        int tail = _current.Length - _position;

        // Recorded before the capacity check so an oversized record reports the
        // offset it actually starts at.
        _stitchFileOffset = _blockOffset + _position;

        EnsureStitchCapacity(tail);
        _current.Span[_position..].CopyTo(_stitch);

        _stitchLength = tail;
        _position = _current.Length;
    }

    /// <summary>
    /// Copies as much of the unconsumed part of the current block into the stitch
    /// buffer as the record-length limit allows.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the current block is already spent, so the
    /// caller must fetch another.
    /// </returns>
    private bool ExtendStitchFromCurrentBlock()
    {
        int available = _current.Length - _position;
        if (available == 0) return false;

        int room = _maxRecordLength - _stitchLength;

        if (room <= 0)
            throw new RecordTooLargeException(_stitchFileOffset, _maxRecordLength);

        int take = Math.Min(available, room);

        EnsureStitchCapacity(_stitchLength + take);
        _current.Span.Slice(_position, take).CopyTo(_stitch.AsSpan(_stitchLength));

        _stitchLength += take;
        _position += take;

        return true;
    }

    private void EnsureStitchCapacity(int required)
    {
        if (required > _maxRecordLength)
            throw new RecordTooLargeException(_stitchFileOffset, _maxRecordLength);

        if (_stitch.Length >= required) return;

        int capacity = Math.Max(_stitch.Length == 0 ? 4096 : _stitch.Length * 2, required);
        Array.Resize(ref _stitch, Math.Min(capacity, _maxRecordLength));
    }

    private void CheckRecordLength(int recordLength)
    {
        if (recordLength > _maxRecordLength)
            throw new RecordTooLargeException(_recordOffset, _maxRecordLength);
    }

    private void SetCurrent(ReadOnlyMemory<byte> record)
    {
#if DEBUG
        _guard?.Invalidate();
        _guard = new RecordGuard(MemoryMarshal.AsMemory(record));
        Current = _guard.Memory;
#else
        Current = record;
#endif
    }

    private void Complete()
    {
        _completed = true;

#if DEBUG
        _guard?.Invalidate();
        _guard = null;
#endif

        Current = default;
    }

    /// <summary>Disposes the reader and the pump beneath it.</summary>
    /// <returns>A task that completes once the pump has drained.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _disposed = true;
        Current = default;

        await _source.DisposeAsync().ConfigureAwait(false);
    }
}
