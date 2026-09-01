using System.Buffers.Binary;

namespace PunchIO.Framing;

/// <summary>
/// The Micro Focus standard variable-structure file header: the 128-byte block
/// that opens every file of variable-length records apart from line-sequential
/// and print files.
/// </summary>
/// <remarks>
/// <para>
/// The header is not a special case bolted onto the format — it is itself the
/// file's first record, a system record whose control field carries status
/// <see cref="SystemRecord"/> and a length of 126 (short control field) or 124
/// (long), either way totalling 128 bytes with its header. Readers therefore need
/// no special handling: the ordinary record framer skips it along with any other
/// non-data record.
/// </para>
/// <para>
/// Every multi-byte field is big-endian, and every field not listed in
/// <see cref="Write"/> is zero.
/// </para>
/// </remarks>
public static class MicroFocusFileHeader
{
    /// <summary>The size of the header block in bytes.</summary>
    public const int Length = 128;

    /// <summary>Control-field status for a deleted record, available for reuse.</summary>
    public const int DeletedRecord = 2;

    /// <summary>Control-field status for a system record, such as this header.</summary>
    public const int SystemRecord = 3;

    /// <summary>Control-field status for a user data record.</summary>
    public const int UserDataRecord = 4;

    /// <summary>The largest record length the header's two-byte field can record.</summary>
    public const int MaxDeclarableRecordLength = ushort.MaxValue;

    private const int OrganizationSequential = 1;
    private const int RecordingModeVariable = 1;

    // Offsets within the header, from the start of the file.
    private const int OrganizationOffset = 39;
    private const int ReservedSixtyTwoOffset = 36;
    private const int RecordingModeOffset = 48;
    private const int MaxRecordLengthOffset = 56;
    private const int MinRecordLengthOffset = 60;

    /// <summary>
    /// Writes the header for a variable-length sequential file.
    /// </summary>
    /// <param name="destination">A buffer at least <see cref="Length"/> bytes long.</param>
    /// <param name="descriptor">
    /// The layout whose control-field width and declared record lengths the header
    /// records.
    /// </param>
    /// <returns><see cref="Length"/>.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="destination"/> is too small, or the descriptor declares a
    /// record length the header cannot represent.
    /// </exception>
    public static int Write(Span<byte> destination, in VariableRecordDescriptor descriptor)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException(
                $"The Micro Focus file header needs {Length} bytes; got {destination.Length}.",
                nameof(destination));
        }

        if (descriptor.MaxRecordLength > MaxDeclarableRecordLength)
        {
            throw new ArgumentException(
                $"A maximum record length of {descriptor.MaxRecordLength} cannot be " +
                $"recorded in the header's two-byte field, which tops out at " +
                $"{MaxDeclarableRecordLength}.",
                nameof(descriptor));
        }

        var header = destination[..Length];
        header.Clear();

        // Offset 0: the header's own control field. Status 3 over the length of
        // the bytes that follow it, so that control field plus data is exactly
        // 128 either way: 2 + 126 with a short field, 4 + 124 with a long one.
        WriteControlField(header, descriptor, Length - descriptor.PrefixBytes, SystemRecord);

        // Offset 36: reserved, documented as always holding 62.
        BinaryPrimitives.WriteUInt16BigEndian(header[ReservedSixtyTwoOffset..], 62);

        header[OrganizationOffset] = OrganizationSequential;
        header[RecordingModeOffset] = RecordingModeVariable;

        BinaryPrimitives.WriteUInt16BigEndian(
            header[MaxRecordLengthOffset..], (ushort)descriptor.MaxRecordLength);

        BinaryPrimitives.WriteUInt16BigEndian(
            header[MinRecordLengthOffset..], (ushort)descriptor.MinRecordLength);

        return Length;
    }

    private static void WriteControlField(
        Span<byte> destination, in VariableRecordDescriptor descriptor, int length, int status)
    {
        long field = ((long)status << descriptor.LengthBits) | (uint)length;

        for (int i = descriptor.LengthFieldWidth - 1; i >= 0; i--, field >>= 8)
            destination[i] = (byte)field;
    }
}
