namespace PunchIO.Configuration;

/// <summary>The root configuration section, bound from <c>PunchIO</c>.</summary>
public sealed class PunchIoConfiguration
{
    /// <summary>File profiles, keyed by the name callers resolve them with.</summary>
    public Dictionary<string, FileProfileConfiguration> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One file profile as written in configuration, before validation.</summary>
/// <remarks>
/// Every property is nullable and every size is a string. Binding never fails
/// here; it fails in <see cref="FileProfileFactory"/>, which can name the profile
/// and the key responsible.
/// </remarks>
public sealed class FileProfileConfiguration
{
    /// <summary>
    /// <c>LineSequential</c>, <c>FixedBlock</c> or <c>VariableBlock</c>. Required.
    /// </summary>
    public string? Format { get; set; }

    /// <summary>
    /// A named starting point whose values seed the rest: <c>Fujitsu</c> or
    /// <c>MicroFocus</c> for variable-block, <c>MicroFocus</c> for line
    /// sequential. Explicit keys override whatever the preset supplied.
    /// </summary>
    public string? Preset { get; set; }

    /// <summary>Queue depth, block size and backend.</summary>
    public IoConfiguration? Io { get; set; }

    /// <summary>Line-sequential behavior, when <see cref="Format"/> says so.</summary>
    public LineConfiguration? Line { get; set; }

    /// <summary>Fixed-length behavior, when <see cref="Format"/> says so.</summary>
    public FixedConfiguration? Fixed { get; set; }

    /// <summary>Variable-length layout, when <see cref="Format"/> says so.</summary>
    public VariableConfiguration? Variable { get; set; }
}

/// <summary>I/O tuning, as written in configuration.</summary>
public sealed class IoConfiguration
{
    /// <summary>The number of requests to keep outstanding.</summary>
    public int? QueueDepth { get; set; }

    /// <summary>The size of each request. Accepts a plain number or a unit suffix such as <c>"1MiB"</c>.</summary>
    public string? BlockSize { get; set; }

    /// <summary>The largest record a reader will accept. Accepts a unit suffix.</summary>
    public string? MaxRecordLength { get; set; }

    /// <summary>Prevents the block size being rounded up to eliminate record straddling.</summary>
    public bool? PinBlockSize { get; set; }

    /// <summary><c>Auto</c>, <c>ForceNative</c> or <c>ForceManaged</c>.</summary>
    public string? Backend { get; set; }

    /// <summary><c>None</c>, <c>Read</c>, <c>Write</c>, <c>ReadWrite</c> or <c>Delete</c>.</summary>
    public string? Share { get; set; }
}

/// <summary>Line-sequential behavior, as written in configuration.</summary>
public sealed class LineConfiguration
{
    /// <summary><c>Lf</c>, <c>CrLf</c> or <c>Cr</c>.</summary>
    public string? Terminator { get; set; }

    /// <summary>Accept either terminator on read regardless of <see cref="Terminator"/>.</summary>
    public bool? AcceptEitherOnRead { get; set; }

    /// <summary>Strip trailing spaces from each record on read.</summary>
    public bool? TrimTrailingSpaces { get; set; }

    /// <summary>Strip trailing spaces from each record on write.</summary>
    public bool? StripTrailingSpaces { get; set; }

    /// <summary>Expand tabs to the next stop on read.</summary>
    public bool? ExpandTabs { get; set; }

    /// <summary>The tab stop width.</summary>
    public int? TabStopWidth { get; set; }

    /// <summary>Apply the Micro Focus null-escape convention.</summary>
    public bool? NullEscape { get; set; }

    /// <summary>
    /// <c>ascii</c> or <c>ebcdic</c>. Selects the structural byte values framing
    /// uses; it does not transcode record content.
    /// </summary>
    public string? Encoding { get; set; }
}

/// <summary>Fixed-length behavior, as written in configuration.</summary>
public sealed class FixedConfiguration
{
    /// <summary>The record length in bytes. Required for this format.</summary>
    public int? RecordLength { get; set; }

    /// <summary><c>Strict</c>, <c>Lenient</c> or <c>Ignore</c>.</summary>
    public string? TrailingPartialRecord { get; set; }

    /// <summary>The byte used to pad a short record on write, as a number.</summary>
    public int? PadByte { get; set; }
}

/// <summary>Variable-length layout, as written in configuration.</summary>
public sealed class VariableConfiguration
{
    /// <summary>Total width of the header preceding each record.</summary>
    public int? PrefixBytes { get; set; }

    /// <summary>Width of the trailing length field, or zero when there is none.</summary>
    public int? SuffixBytes { get; set; }

    /// <summary>Offset of the length field within the prefix.</summary>
    public int? LengthFieldOffset { get; set; }

    /// <summary>Width of the length field.</summary>
    public int? LengthFieldWidth { get; set; }

    /// <summary>Offset of a flag byte within the prefix, or <c>-1</c> for none.</summary>
    public int? FlagByteOffset { get; set; }

    /// <summary><c>BigEndian</c> or <c>LittleEndian</c>.</summary>
    public string? Endianness { get; set; }

    /// <summary><c>DataOnly</c>, <c>WithPrefix</c> or <c>WithPrefixAndSuffix</c>.</summary>
    public string? LengthIncludes { get; set; }

    /// <summary>Compare the suffix against the prefix on read.</summary>
    public bool? ValidateSuffix { get; set; }

    /// <summary>Reject a non-zero byte in the prefix outside the length and flag fields.</summary>
    public bool? ValidateReservedBytes { get; set; }

    /// <summary>The byte boundary each record is padded up to.</summary>
    public int? Alignment { get; set; }
}
