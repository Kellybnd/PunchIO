namespace PunchIO.Framing;

/// <summary>
/// Writes fixed-length records, padding a short record out to the configured
/// length.
/// </summary>
public readonly struct FixedBlockEncoder : IRecordEncoder
{
    private readonly int _recordLength;
    private readonly byte _padByte;

    /// <summary>Initializes the encoder.</summary>
    /// <param name="recordLength">The record length in bytes; must be positive.</param>
    /// <param name="padByte">
    /// The byte used to pad a short record. Defaults to an ASCII space, matching
    /// COBOL behavior; pass zero for binary files.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="recordLength"/> is not positive.
    /// </exception>
    public FixedBlockEncoder(int recordLength, byte padByte = 0x20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recordLength);

        _recordLength = recordLength;
        _padByte = padByte;
    }

    /// <summary>The configured record length in bytes.</summary>
    public int RecordLength => _recordLength;

    /// <inheritdoc />
    public int MaxHeaderLength => 0;

    /// <inheritdoc />
    public int MaxTrailerLength => _recordLength;

    /// <inheritdoc />
    public bool RewritesBody => false;

    /// <inheritdoc />
    public int MaxBodyLength(int recordLength) => recordLength;

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// The record is longer than the configured record length. Truncating would
    /// discard data silently, so it is refused instead.
    /// </exception>
    public int BodyLength(ReadOnlySpan<byte> record)
    {
        if (record.Length > _recordLength)
        {
            throw new ArgumentException(
                $"A record of {record.Length} bytes cannot be written to a " +
                $"fixed-length file of {_recordLength}-byte records.",
                nameof(record));
        }

        return record.Length;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always; this encoder writes the record through.</exception>
    public int WriteBody(ReadOnlySpan<byte> record, Span<byte> destination) =>
        throw new NotSupportedException($"{nameof(FixedBlockEncoder)} does not rewrite record bodies.");

    /// <inheritdoc />
    public int WriteHeader(Span<byte> destination, int bodyLength) => 0;

    /// <inheritdoc />
    public int WriteTrailer(Span<byte> destination, int bodyLength)
    {
        int padding = _recordLength - bodyLength;
        destination[..padding].Fill(_padByte);

        return padding;
    }
}

/// <summary>
/// Writes line-sequential records: optional trailing-space stripping, optional
/// null escaping, then the configured terminator.
/// </summary>
public readonly struct LineSequentialEncoder : IRecordEncoder
{
    private readonly LineRecordTransform _transform;
    private readonly LineTerminator _terminator;
    private readonly byte _space;
    private readonly byte _carriageReturn;
    private readonly byte _lineFeed;
    private readonly bool _stripTrailingSpaces;
    private readonly bool _nullEscape;

    /// <summary>Initializes the encoder from line-sequential options.</summary>
    /// <param name="options">The behavior switches to apply.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public LineSequentialEncoder(LineSequentialOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _transform = new LineRecordTransform(options);
        _terminator = options.Terminator;
        _space = options.Syntax.Space;
        _carriageReturn = options.Syntax.CarriageReturn;
        _lineFeed = options.Syntax.LineFeed;
        _stripTrailingSpaces = options.StripTrailingSpaces;
        _nullEscape = options.NullEscape;
    }

    /// <inheritdoc />
    public int MaxHeaderLength => 0;

    /// <inheritdoc />
    public int MaxTrailerLength => 2;

    /// <inheritdoc />
    /// <remarks>
    /// Only null escaping rewrites bytes. Trailing-space stripping merely
    /// shortens the slice, so it stays on the zero-copy path.
    /// </remarks>
    public bool RewritesBody => _nullEscape;

    /// <inheritdoc />
    public int MaxBodyLength(int recordLength) => _transform.MaxExpansion(recordLength);

    /// <inheritdoc />
    public int BodyLength(ReadOnlySpan<byte> record) =>
        _stripTrailingSpaces ? TrimTrailingSpaces(record) : record.Length;

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
    public int WriteBody(ReadOnlySpan<byte> record, Span<byte> destination)
    {
        var body = record[..BodyLength(record)];

        if (!_transform.TryEncode(body, destination, out int written))
        {
            throw new ArgumentException(
                "The destination is too small for the encoded record.", nameof(destination));
        }

        return written;
    }

    /// <inheritdoc />
    public int WriteHeader(Span<byte> destination, int bodyLength) => 0;

    /// <inheritdoc />
    public int WriteTrailer(Span<byte> destination, int bodyLength)
    {
        switch (_terminator)
        {
            case LineTerminator.CrLf:
                destination[0] = _carriageReturn;
                destination[1] = _lineFeed;
                return 2;

            case LineTerminator.Cr:
                destination[0] = _carriageReturn;
                return 1;

            default:
                destination[0] = _lineFeed;
                return 1;
        }
    }

    private int TrimTrailingSpaces(ReadOnlySpan<byte> record)
    {
        int end = record.Length;
        while (end > 0 && record[end - 1] == _space) end--;

        return end;
    }
}

/// <summary>
/// Writes variable-length records described by a <see cref="VariableRecordDescriptor"/>,
/// bracketing the record with its length prefix and, where the format has one,
/// its length suffix.
/// </summary>
public readonly struct VariableRecordEncoder : IRecordEncoder
{
    private readonly VariableRecordDescriptor _descriptor;

    /// <summary>Initializes the encoder.</summary>
    /// <param name="descriptor">The on-disk layout to write.</param>
    /// <exception cref="ArgumentException">The descriptor is internally inconsistent.</exception>
    public VariableRecordEncoder(VariableRecordDescriptor descriptor)
    {
        descriptor.Validate();
        _descriptor = descriptor;
    }

    /// <summary>The layout this encoder writes.</summary>
    public VariableRecordDescriptor Descriptor => _descriptor;

    /// <inheritdoc />
    public int MaxHeaderLength => _descriptor.PrefixBytes;

    /// <inheritdoc />
    public int MaxTrailerLength => _descriptor.SuffixBytes + _descriptor.Alignment;

    /// <inheritdoc />
    public bool RewritesBody => false;

    /// <inheritdoc />
    public int MaxBodyLength(int recordLength) => recordLength;

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// The record is longer than the layout's length field can represent.
    /// Storing it would silently truncate the length and produce a file that
    /// reframes into garbage, so it is refused instead.
    /// </exception>
    public int BodyLength(ReadOnlySpan<byte> record)
    {
        if (record.Length > _descriptor.MaxDataLength)
        {
            throw new ArgumentException(
                $"A record of {record.Length} bytes exceeds the " +
                $"{_descriptor.MaxDataLength} bytes a {_descriptor.LengthFieldWidth}-byte " +
                "length field can represent. Use a layout with a wider length field.",
                nameof(record));
        }

        return record.Length;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always; this encoder writes the record through.</exception>
    public int WriteBody(ReadOnlySpan<byte> record, Span<byte> destination) =>
        throw new NotSupportedException(
            $"{nameof(VariableRecordEncoder)} does not rewrite record bodies.");

    /// <inheritdoc />
    public int WriteHeader(Span<byte> destination, int bodyLength) =>
        VariableRecordFramer.WritePrefix(destination, bodyLength, _descriptor);

    /// <inheritdoc />
    public int WriteTrailer(Span<byte> destination, int bodyLength) =>
        VariableRecordFramer.WriteSuffix(destination, bodyLength, _descriptor);
}
