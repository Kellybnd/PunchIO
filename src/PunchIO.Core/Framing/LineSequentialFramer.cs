namespace PunchIO.Framing;

/// <summary>
/// Frames records terminated by a line terminator, in the file's own byte encoding.
/// </summary>
/// <remarks>
/// A block ending on a carriage return with its line feed in the next block needs
/// no special case: the terminator search fails, the framer reports
/// <see cref="FrameStatus.NeedMoreData"/>, and the reader stitches the next block
/// on before reframing.
/// </remarks>
public readonly struct LineSequentialFramer : IRecordFramer
{
    private readonly byte _terminator;
    private readonly byte _carriageReturn;
    private readonly byte _space;
    private readonly bool _stripPrecedingCr;
    private readonly bool _trimTrailingSpaces;

    /// <summary>Initializes the framer from line-sequential options.</summary>
    /// <param name="options">The behavior switches to apply.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public LineSequentialFramer(LineSequentialOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _terminator = options.Terminator == LineTerminator.Cr
            ? options.Syntax.CarriageReturn
            : options.Syntax.LineFeed;

        _carriageReturn = options.Syntax.CarriageReturn;
        _space = options.Syntax.Space;

        // A carriage return only forms part of the terminator when the
        // terminator itself is line-feed based.
        _stripPrecedingCr = options.Terminator != LineTerminator.Cr
            && (options.AcceptEitherOnRead || options.Terminator == LineTerminator.CrLf);

        _trimTrailingSpaces = options.TrimTrailingSpaces;
    }

    /// <inheritdoc />
    public int MinimumLookahead => 1;

    /// <inheritdoc />
    public FrameStatus TryFrame(
        ReadOnlySpan<byte> input,
        bool isFinalBlock,
        out int consumed,
        out int recordStart,
        out int recordLength)
    {
        recordStart = 0;
        consumed = 0;
        recordLength = 0;

        int index = input.IndexOf(_terminator);
        int end;

        if (index >= 0)
        {
            consumed = index + 1;
            end = index;

            if (_stripPrecedingCr && end > 0 && input[end - 1] == _carriageReturn)
                end--;
        }
        else
        {
            if (!isFinalBlock)
                return FrameStatus.NeedMoreData;

            if (input.Length == 0)
                return FrameStatus.EndOfData;

            // A final record with no terminator is still a record.
            consumed = input.Length;
            end = input.Length;
        }

        if (_trimTrailingSpaces)
        {
            while (end > 0 && input[end - 1] == _space)
                end--;
        }

        recordLength = end;
        return FrameStatus.Ok;
    }
}
