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

/// <summary>The block of header information a format places at the start of a file.</summary>
public enum VariableFileHeader
{
    /// <summary>The file has no header; the first record begins at byte zero.</summary>
    None,

    /// <summary>
    /// The Micro Focus standard variable-structure file header: a 128-byte system
    /// record describing the file's organization and record lengths.
    /// </summary>
    MicroFocusStandard,
}

/// <summary>
/// The on-disk layout of a variable-length record: where the length lives, how
/// wide it is, what it counts, whether a status field shares the same bits, and
/// whether a trailing copy of the length follows the data.
/// </summary>
/// <remarks>
/// Record layouts vary between COBOL runtimes and compiler directives. Every
/// layout constant is confined to the <see cref="MicroFocus(int, int)"/> and
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
    /// Number of high-order bits of the length field that carry a record status
    /// rather than length, or zero when the whole field is length.
    /// </summary>
    /// <remarks>
    /// Micro Focus packs both into one field: the top four bits say what kind of
    /// record follows and the rest is its length. A record whose status is not
    /// <see cref="DataRecordStatus"/> is skipped on read rather than returned,
    /// which is what makes the file header and any deleted records invisible to
    /// the caller.
    /// </remarks>
    public int StatusBits { get; init; }

    /// <summary>
    /// The status value marking a record as user data. Ignored when
    /// <see cref="StatusBits"/> is zero.
    /// </summary>
    public int DataRecordStatus { get; init; }

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

    /// <summary>The block of header information at the start of the file, if any.</summary>
    public VariableFileHeader FileHeader { get; init; }

    /// <summary>
    /// The longest record the file declares, in bytes, or zero when the format
    /// does not declare one.
    /// </summary>
    /// <remarks>
    /// For Micro Focus this is not merely advisory: it decides whether the record
    /// header is two bytes or four, and it is recorded in the file header, so a
    /// reader must be given the same value the writer used.
    /// </remarks>
    public int MaxRecordLength { get; init; }

    /// <summary>The shortest record the file declares, in bytes.</summary>
    public int MinRecordLength { get; init; }

    /// <summary>Size of the file header in bytes; zero when there is none.</summary>
    public int FileHeaderLength =>
        FileHeader == VariableFileHeader.MicroFocusStandard ? MicroFocusFileHeader.Length : 0;

    /// <summary>
    /// The longest record a two-byte Micro Focus control field can describe, its
    /// twelve length bits topping out here.
    /// </summary>
    public const int MicroFocusShortHeaderLimit = 4095;

    /// <summary>
    /// The Micro Focus variable-length sequential layout: a 128-byte file header
    /// followed by records that each carry a two- or four-byte big-endian control
    /// field holding four status bits over a length, padded out to the next
    /// four-byte boundary.
    /// </summary>
    /// <param name="maxRecordLength">
    /// The longest record the file may hold. At or below
    /// <see cref="MicroFocusShortHeaderLimit"/> the control field is two bytes;
    /// above it, four.
    /// </param>
    /// <param name="minRecordLength">The shortest record the file may hold.</param>
    /// <returns>The layout for a file with those record lengths.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A length is negative, or the minimum exceeds the maximum.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The control field packs a status into its top four bits: <c>4</c> is a user
    /// data record, <c>3</c> a system record such as the file header, <c>2</c> a
    /// deleted one. An 80-byte record in a short-header file therefore begins
    /// <c>x"4050"</c>. Records whose status is not <c>4</c> are skipped on read.
    /// </para>
    /// <para>
    /// The default maximum is <see cref="MicroFocusShortHeaderLimit"/>, giving the
    /// two-byte control field Micro Focus uses for the great majority of files. A
    /// reader must be given the same maximum the writer used, since it decides the
    /// field's width.
    /// </para>
    /// </remarks>
    public static VariableRecordDescriptor MicroFocus(
        int maxRecordLength = MicroFocusShortHeaderLimit, int minRecordLength = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecordLength);
        ArgumentOutOfRangeException.ThrowIfNegative(minRecordLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minRecordLength, maxRecordLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maxRecordLength, MicroFocusFileHeader.MaxDeclarableRecordLength);

        int width = maxRecordLength <= MicroFocusShortHeaderLimit ? 2 : 4;

        return new VariableRecordDescriptor
        {
            PrefixBytes = width,
            SuffixBytes = 0,
            LengthFieldOffset = 0,
            LengthFieldWidth = width,
            StatusBits = 4,
            DataRecordStatus = MicroFocusFileHeader.UserDataRecord,
            FlagByteOffset = -1,
            Endianness = Endianness.BigEndian,
            LengthIncludes = LengthBasis.DataOnly,
            ValidateSuffix = false,
            ValidateReservedBytes = false,
            Alignment = 4,
            FileHeader = VariableFileHeader.MicroFocusStandard,
            MaxRecordLength = maxRecordLength,
            MinRecordLength = minRecordLength,
        };
    }

    /// <summary>
    /// The Fujitsu variable-length sequential layout: a four-byte length prefix
    /// and a matching four-byte length suffix around each record. Both carry the
    /// caller-visible data length, excluding the eight framing bytes, so a record
    /// of <c>n</c> data bytes occupies <c>n + 8</c> bytes on disk and reports <c>n</c>.
    /// </summary>
    /// <remarks>
    /// The length is little-endian: the runtime reads and writes it as a native
    /// x86 word with no byte swapping, so the on-disk order is the machine's.
    /// </remarks>
    public static VariableRecordDescriptor Fujitsu => new()
    {
        PrefixBytes = 4,
        SuffixBytes = 4,
        LengthFieldOffset = 0,
        LengthFieldWidth = 4,
        StatusBits = 0,
        DataRecordStatus = 0,
        FlagByteOffset = -1,
        Endianness = Endianness.LittleEndian,
        LengthIncludes = LengthBasis.DataOnly,
        ValidateSuffix = true,
        ValidateReservedBytes = false,
        Alignment = 1,
        FileHeader = VariableFileHeader.None,
        MaxRecordLength = 0,
        MinRecordLength = 0,
    };

    /// <summary>The number of bits of the length field that carry length.</summary>
    public int LengthBits => (8 * LengthFieldWidth) - StatusBits;

    /// <summary>
    /// The largest record this layout can describe, in bytes, limited by what the
    /// length field can represent and by any maximum the file declares.
    /// </summary>
    /// <remarks>
    /// A short Micro Focus control field leaves twelve bits for the length, so it
    /// cannot describe a record longer than 4,095 bytes. Exceeding this silently
    /// truncates the stored length and produces a file that reframes into garbage,
    /// so the encoder refuses instead.
    /// </remarks>
    public long MaxDataLength =>
        MaxRecordLength > 0 ? Math.Min(FieldCapacity, MaxRecordLength) : FieldCapacity;

    /// <summary>
    /// The longest record the length field on its own could describe, before any
    /// maximum the file declares is taken into account.
    /// </summary>
    private long FieldCapacity
    {
        get
        {
            long capacity = (1L << LengthBits) - 1;

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

        // A status has to leave at least one bit for the length, and the value
        // stored in it has to fit the bits reserved for it.
        if (StatusBits < 0 || StatusBits >= 8 * LengthFieldWidth)
        {
            throw new ArgumentException(
                $"{nameof(StatusBits)} must leave at least one length bit in a " +
                $"{LengthFieldWidth}-byte field; got {StatusBits}.");
        }

        if (StatusBits > 0 && (DataRecordStatus < 0 || DataRecordStatus >= 1 << StatusBits))
        {
            throw new ArgumentException(
                $"{nameof(DataRecordStatus)} {DataRecordStatus} does not fit in " +
                $"{StatusBits} bits.");
        }

        if (MinRecordLength < 0 || MaxRecordLength < 0)
        {
            throw new ArgumentException("Record length bounds cannot be negative.");
        }

        if (MaxRecordLength > 0 && MinRecordLength > MaxRecordLength)
        {
            throw new ArgumentException(
                $"{nameof(MinRecordLength)} {MinRecordLength} exceeds " +
                $"{nameof(MaxRecordLength)} {MaxRecordLength}.");
        }

        // A declared maximum the length field cannot express would produce a file
        // whose own header promises records it has no way to frame.
        if (MaxRecordLength > FieldCapacity)
        {
            throw new ArgumentException(
                $"{nameof(MaxRecordLength)} {MaxRecordLength} exceeds the " +
                $"{FieldCapacity} bytes a {LengthBits}-bit length field can describe.");
        }

        // Records are aligned from the end of the file header, so a header that is
        // not itself aligned would leave every record in the file off by a byte.
        if (FileHeaderLength % Alignment != 0)
        {
            throw new ArgumentException(
                $"A {FileHeaderLength}-byte file header does not sit on a " +
                $"{Alignment}-byte boundary.");
        }
    }
}
