namespace PunchIO.Framing;

/// <summary>The byte order of a multi-byte length field.</summary>
public enum Endianness
{
    /// <summary>Most significant byte first.</summary>
    BigEndian,

    /// <summary>Least significant byte first.</summary>
    LittleEndian,
}

/// <summary>What a record's stored length field counts.</summary>
public enum LengthBasis
{
    /// <summary>The record data only, excluding all framing bytes.</summary>
    DataOnly,

    /// <summary>The record data plus the prefix.</summary>
    WithPrefix,

    /// <summary>The record data plus both the prefix and the suffix.</summary>
    WithPrefixAndSuffix,
}

/// <summary>
/// The on-disk layout of a variable-length record: where the length lives, how
/// wide it is, what it counts, and whether a trailing copy of it follows the data.
/// </summary>
/// <remarks>
/// Record layouts vary between COBOL runtimes and compiler directives. Every
/// layout constant is confined to the <see cref="MicroFocus"/> and
/// <see cref="Fujitsu"/> presets below, and the type is a record struct, so
/// adapting to a variant is a single <c>with</c> expression rather than a
/// rewrite.
/// </remarks>
public readonly record struct VariableRecordDescriptor
{
    /// <summary>Total width of the header preceding each record, in bytes.</summary>
    public int PrefixBytes { get; init; }

    /// <summary>
    /// Width of the trailing length field following each record, in bytes; zero
    /// when the format has no suffix.
    /// </summary>
    public int SuffixBytes { get; init; }

    /// <summary>Offset of the length field within the prefix, in bytes.</summary>
    public int LengthFieldOffset { get; init; }

    /// <summary>Width of the length field, in bytes. At most 4.</summary>
    public int LengthFieldWidth { get; init; }

    /// <summary>
    /// Offset of a flag byte within the prefix that carries no length
    /// information, or <c>-1</c> when the format has none.
    /// </summary>
    public int FlagByteOffset { get; init; }

    /// <summary>Byte order of the length fields.</summary>
    public Endianness Endianness { get; init; }

    /// <summary>What the stored length counts.</summary>
    public LengthBasis LengthIncludes { get; init; }

    /// <summary>
    /// Compares the suffix against the prefix on read as an integrity check.
    /// Requires <see cref="SuffixBytes"/> to be non-zero.
    /// </summary>
    public bool ValidateSuffix { get; init; }

    /// <summary>
    /// Rejects a record whose prefix contains a non-zero byte outside the length
    /// field and the flag byte.
    /// </summary>
    public bool ValidateReservedBytes { get; init; }

    /// <summary>
    /// Byte boundary each record is padded up to. <c>1</c> means packed. Must be
    /// a power of two.
    /// </summary>
    public int Alignment { get; init; }

    /// <summary>
    /// The Micro Focus variable-length sequential layout: a four-byte header
    /// carrying a big-endian length in its first two bytes, flags in byte 2, and
    /// a reserved zero in byte 3. No suffix.
    /// </summary>
    public static VariableRecordDescriptor MicroFocus => new()
    {
        PrefixBytes = 4,
        SuffixBytes = 0,
        LengthFieldOffset = 0,
        LengthFieldWidth = 2,
        FlagByteOffset = 2,
        Endianness = Endianness.BigEndian,
        LengthIncludes = LengthBasis.DataOnly,
        ValidateSuffix = false,
        ValidateReservedBytes = false,
        Alignment = 1,
    };

    /// <summary>
    /// The Fujitsu variable-length sequential layout: a four-byte length prefix
    /// and a matching four-byte length suffix around each record. Both carry the
    /// caller-visible data length, excluding the eight framing bytes, so a record
    /// of <c>n</c> data bytes occupies <c>n + 8</c> bytes on disk and reports <c>n</c>.
    /// </summary>
    public static VariableRecordDescriptor Fujitsu => new()
    {
        PrefixBytes = 4,
        SuffixBytes = 4,
        LengthFieldOffset = 0,
        LengthFieldWidth = 4,
        FlagByteOffset = -1,
        Endianness = Endianness.BigEndian,
        LengthIncludes = LengthBasis.DataOnly,
        ValidateSuffix = true,
        ValidateReservedBytes = false,
        Alignment = 1,
    };

    /// <summary>
    /// The largest record this layout can describe, in bytes, limited by what
    /// the length field can represent.
    /// </summary>
    /// <remarks>
    /// A two-byte length field tops out at 65,535, so the Micro Focus preset
    /// cannot represent a larger record. Exceeding this silently truncates the
    /// stored length and produces a file that reframes into garbage, so the
    /// encoder refuses instead.
    /// </remarks>
    public long MaxDataLength
    {
        get
        {
            long capacity = (1L << (8 * LengthFieldWidth)) - 1;

            return LengthIncludes switch
            {
                LengthBasis.WithPrefix => capacity - PrefixBytes,
                LengthBasis.WithPrefixAndSuffix => capacity - PrefixBytes - SuffixBytes,
                _ => capacity,
            };
        }
    }

    /// <summary>Throws when the descriptor's fields are mutually inconsistent.</summary>
    /// <exception cref="ArgumentException">The layout cannot be satisfied.</exception>
    public void Validate()
    {
        if (LengthFieldWidth is < 1 or > 4)
        {
            throw new ArgumentException(
                $"{nameof(LengthFieldWidth)} must be between 1 and 4; got {LengthFieldWidth}.");
        }

        if (LengthFieldOffset < 0)
        {
            throw new ArgumentException(
                $"{nameof(LengthFieldOffset)} cannot be negative; got {LengthFieldOffset}.");
        }

        if (LengthFieldOffset + LengthFieldWidth > PrefixBytes)
        {
            throw new ArgumentException(
                $"The length field ({LengthFieldOffset}..{LengthFieldOffset + LengthFieldWidth}) " +
                $"does not fit within a {PrefixBytes}-byte prefix.");
        }

        if (SuffixBytes is < 0 or > 4)
        {
            throw new ArgumentException(
                $"{nameof(SuffixBytes)} must be between 0 and 4; got {SuffixBytes}.");
        }

        if (ValidateSuffix && SuffixBytes == 0)
        {
            throw new ArgumentException(
                $"{nameof(ValidateSuffix)} requires a non-zero {nameof(SuffixBytes)}.");
        }

        if (FlagByteOffset >= PrefixBytes)
        {
            throw new ArgumentException(
                $"{nameof(FlagByteOffset)} {FlagByteOffset} lies outside a {PrefixBytes}-byte prefix.");
        }

        if (Alignment < 1 || (Alignment & (Alignment - 1)) != 0)
        {
            throw new ArgumentException(
                $"{nameof(Alignment)} must be a positive power of two; got {Alignment}.");
        }
    }
}
