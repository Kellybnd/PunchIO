namespace PunchIO.Framing;

/// <summary>
/// Frames variable-length records described by a <see cref="VariableRecordDescriptor"/>,
/// covering both the Micro Focus and Fujitsu layouts.
/// </summary>
public readonly struct VariableRecordFramer : IRecordFramer
{
    private readonly VariableRecordDescriptor _descriptor;

    /// <summary>Initializes the framer.</summary>
    /// <param name="descriptor">The on-disk layout to read.</param>
    /// <exception cref="ArgumentException">The descriptor is internally inconsistent.</exception>
    public VariableRecordFramer(VariableRecordDescriptor descriptor)
    {
        descriptor.Validate();
        _descriptor = descriptor;
    }

    /// <summary>The layout this framer reads.</summary>
    public VariableRecordDescriptor Descriptor => _descriptor;

    /// <inheritdoc />
    public int MinimumLookahead => _descriptor.PrefixBytes;

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

        var d = _descriptor;

        if (input.Length < d.PrefixBytes)
        {
            if (input.Length == 0)
                return isFinalBlock ? FrameStatus.EndOfData : FrameStatus.NeedMoreData;

            // A partial prefix at end of file is truncation, not a clean ending.
            return isFinalBlock ? FrameStatus.Invalid : FrameStatus.NeedMoreData;
        }

        if (d.ValidateReservedBytes && !ReservedBytesAreZero(input, d))
            return FrameStatus.Invalid;

        // Read into a long: a four-byte field with its high bit set is an ordinary
        // bit pattern in a corrupt file, and an int would make it negative.
        long control = ReadLength(input.Slice(d.LengthFieldOffset, d.LengthFieldWidth), d.Endianness);

        // Micro Focus packs a record status into the top bits of the same field.
        int status = d.StatusBits > 0 ? (int)(control >> d.LengthBits) : 0;
        long stored = d.StatusBits > 0 ? control & ((1L << d.LengthBits) - 1) : control;

        long dataLength = d.LengthIncludes switch
        {
            LengthBasis.WithPrefix => stored - d.PrefixBytes,
            LengthBasis.WithPrefixAndSuffix => stored - d.PrefixBytes - d.SuffixBytes,
            _ => stored,
        };

        if (dataLength < 0 || dataLength > int.MaxValue - d.PrefixBytes - d.SuffixBytes)
            return FrameStatus.Invalid;

        int total = d.PrefixBytes + (int)dataLength + d.SuffixBytes;
        int padded = d.Alignment > 1 ? RoundUp(total, d.Alignment) : total;

        if (input.Length < padded)
            return isFinalBlock ? FrameStatus.Invalid : FrameStatus.NeedMoreData;

        // Not a user data record: the file header, a deleted slot, or one the
        // runtime keeps for itself. Consume it and let the caller frame on.
        if (d.StatusBits > 0 && status != d.DataRecordStatus)
        {
            consumed = padded;
            return FrameStatus.Skip;
        }

        if (d.ValidateSuffix)
        {
            long suffix = ReadLength(
                input.Slice(d.PrefixBytes + (int)dataLength, d.SuffixBytes), d.Endianness);

            if (suffix != stored)
                return FrameStatus.Invalid;
        }

        recordStart = d.PrefixBytes;
        recordLength = (int)dataLength;
        consumed = padded;
        return FrameStatus.Ok;
    }

    /// <summary>
    /// Writes the prefix and suffix for a record of <paramref name="dataLength"/>
    /// bytes, leaving the data region between them for the caller to fill.
    /// </summary>
    /// <param name="destination">
    /// A buffer at least <see cref="FramedLength"/> bytes long.
    /// </param>
    /// <param name="dataLength">The record's data length in bytes.</param>
    /// <param name="descriptor">The layout to write.</param>
    /// <returns>
    /// The total number of bytes the framed record occupies, including padding.
    /// </returns>
    public static int WriteFraming(
        Span<byte> destination, int dataLength, in VariableRecordDescriptor descriptor)
    {
        WritePrefix(destination, dataLength, descriptor);
        WriteSuffix(destination[(descriptor.PrefixBytes + dataLength)..], dataLength, descriptor);

        return FramedLength(dataLength, descriptor);
    }

    /// <summary>Writes the header that precedes a record's data.</summary>
    /// <param name="destination">
    /// A buffer at least <see cref="VariableRecordDescriptor.PrefixBytes"/> long.
    /// </param>
    /// <param name="dataLength">The record's data length in bytes.</param>
    /// <param name="descriptor">The layout to write.</param>
    /// <returns>The number of bytes written.</returns>
    public static int WritePrefix(
        Span<byte> destination, int dataLength, in VariableRecordDescriptor descriptor)
    {
        destination[..descriptor.PrefixBytes].Clear();

        WriteLength(
            destination.Slice(descriptor.LengthFieldOffset, descriptor.LengthFieldWidth),
            ControlValue(dataLength, descriptor),
            descriptor.Endianness);

        return descriptor.PrefixBytes;
    }

    /// <summary>
    /// Writes the trailer that follows a record's data: the suffix length field,
    /// if the format has one, plus any alignment padding.
    /// </summary>
    /// <param name="destination">A buffer long enough for the suffix and padding.</param>
    /// <param name="dataLength">The record's data length in bytes.</param>
    /// <param name="descriptor">The layout to write.</param>
    /// <returns>The number of bytes written.</returns>
    public static int WriteSuffix(
        Span<byte> destination, int dataLength, in VariableRecordDescriptor descriptor)
    {
        int trailer = FramedLength(dataLength, descriptor) - descriptor.PrefixBytes - dataLength;

        destination[..trailer].Clear();

        if (descriptor.SuffixBytes > 0)
        {
            WriteLength(
                destination[..descriptor.SuffixBytes],
                StoredLength(dataLength, descriptor),
                descriptor.Endianness);
        }

        return trailer;
    }

    /// <summary>
    /// The value the prefix's field carries: the stored length, with the user
    /// data status folded into its top bits for formats that pack the two
    /// together.
    /// </summary>
    private static long ControlValue(int dataLength, in VariableRecordDescriptor descriptor)
    {
        long stored = StoredLength(dataLength, descriptor);

        return descriptor.StatusBits > 0
            ? stored | ((long)descriptor.DataRecordStatus << descriptor.LengthBits)
            : stored;
    }

    private static long StoredLength(int dataLength, in VariableRecordDescriptor descriptor) =>
        descriptor.LengthIncludes switch
        {
            LengthBasis.WithPrefix => dataLength + descriptor.PrefixBytes,
            LengthBasis.WithPrefixAndSuffix =>
                dataLength + descriptor.PrefixBytes + descriptor.SuffixBytes,
            _ => dataLength,
        };

    /// <summary>
    /// The total on-disk size of a record carrying <paramref name="dataLength"/>
    /// data bytes, including framing and alignment padding.
    /// </summary>
    /// <param name="dataLength">The record's data length in bytes.</param>
    /// <param name="descriptor">The layout in use.</param>
    /// <returns>The framed size in bytes.</returns>
    public static int FramedLength(int dataLength, in VariableRecordDescriptor descriptor)
    {
        int total = descriptor.PrefixBytes + dataLength + descriptor.SuffixBytes;
        return descriptor.Alignment > 1 ? RoundUp(total, descriptor.Alignment) : total;
    }

    private static bool ReservedBytesAreZero(ReadOnlySpan<byte> input, in VariableRecordDescriptor d)
    {
        for (int i = 0; i < d.PrefixBytes; i++)
        {
            bool isLength = i >= d.LengthFieldOffset && i < d.LengthFieldOffset + d.LengthFieldWidth;
            if (isLength || i == d.FlagByteOffset) continue;
            if (input[i] != 0) return false;
        }

        return true;
    }

    private static long ReadLength(ReadOnlySpan<byte> field, Endianness endianness)
    {
        long value = 0;

        if (endianness == Endianness.BigEndian)
        {
            for (int i = 0; i < field.Length; i++)
                value = (value << 8) | field[i];
        }
        else
        {
            for (int i = field.Length - 1; i >= 0; i--)
                value = (value << 8) | field[i];
        }

        return value;
    }

    private static void WriteLength(Span<byte> field, long value, Endianness endianness)
    {
        if (endianness == Endianness.BigEndian)
        {
            for (int i = field.Length - 1; i >= 0; i--, value >>= 8)
                field[i] = (byte)value;
        }
        else
        {
            for (int i = 0; i < field.Length; i++, value >>= 8)
                field[i] = (byte)value;
        }
    }

    private static int RoundUp(int value, int alignment) =>
        (value + alignment - 1) & ~(alignment - 1);
}
