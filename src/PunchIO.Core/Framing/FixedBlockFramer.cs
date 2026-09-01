namespace PunchIO.Framing;

/// <summary>
/// Frames records of a single fixed length, packed with no delimiters.
/// </summary>
public readonly struct FixedBlockFramer : IRecordFramer
{
    private readonly int _recordLength;
    private readonly TrailingPartialRecord _trailing;

    /// <summary>Initializes the framer.</summary>
    /// <param name="recordLength">The record length in bytes; must be positive.</param>
    /// <param name="trailing">How to treat a short final record.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="recordLength"/> is not positive.
    /// </exception>
    public FixedBlockFramer(
        int recordLength,
        TrailingPartialRecord trailing = TrailingPartialRecord.Strict)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recordLength);

        _recordLength = recordLength;
        _trailing = trailing;
    }

    /// <summary>The configured record length in bytes.</summary>
    public int RecordLength => _recordLength;

    /// <inheritdoc />
    public int MinimumLookahead => _recordLength;

    /// <inheritdoc />
    public FrameStatus TryFrame(
        ReadOnlySpan<byte> input,
        bool isFinalBlock,
        out int consumed,
        out int recordStart,
        out int recordLength)
    {
        consumed = 0;
        recordStart = 0;
        recordLength = 0;

        if (input.Length >= _recordLength)
        {
            consumed = _recordLength;
            recordLength = _recordLength;
            return FrameStatus.Ok;
        }

        if (!isFinalBlock)
            return FrameStatus.NeedMoreData;

        if (input.Length == 0)
            return FrameStatus.EndOfData;

        switch (_trailing)
        {
            case TrailingPartialRecord.Lenient:
                consumed = input.Length;
                recordLength = input.Length;
                return FrameStatus.Ok;

            case TrailingPartialRecord.Ignore:
                consumed = input.Length;
                return FrameStatus.EndOfData;

            default:
                return FrameStatus.Invalid;
        }
    }
}
