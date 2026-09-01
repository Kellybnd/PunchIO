using PunchIO.Devices;
using PunchIO.Framing;

namespace PunchIO.Configuration;

/// <summary>
/// Turns bound configuration into validated <see cref="FileProfile"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// Every failure here names the profile and the key responsible. That is the
/// whole point of validating at resolution time: a misconfigured profile should
/// fail when the application starts, saying which key is wrong, rather than at
/// byte 40 of a 400 GB file.
/// </para>
/// <para>
/// A <c>Preset</c> seeds every field and explicit keys override it, so adjusting
/// one aspect of a known format is a single line of configuration.
/// </para>
/// </remarks>
public static class FileProfileFactory
{
    /// <summary>Builds and validates one profile.</summary>
    /// <param name="name">The profile's name.</param>
    /// <param name="configuration">The profile as written in configuration.</param>
    /// <returns>The resolved profile.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="FileProfileException">The configuration is invalid.</exception>
    public static FileProfile Create(string name, FileProfileConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configuration);

        var format = ParseEnum<RecordFormat>(name, "Format", configuration.Format)
            ?? throw new FileProfileException(name, "Format",
                "is required; use LineSequential, FixedBlock or VariableBlock.");

        var io = BuildIo(name, configuration.Io);

        return format switch
        {
            RecordFormat.LineSequential => new FileProfile(name, format, io)
            {
                Line = BuildLine(name, configuration),
            },

            RecordFormat.FixedBlock => BuildFixed(name, format, io, configuration),

            _ => new FileProfile(name, format, io)
            {
                Variable = BuildVariable(name, configuration),
            },
        };
    }

    private static FileIoOptions BuildIo(string name, IoConfiguration? io)
    {
        var defaults = FileIoOptions.Default;

        var options = new FileIoOptions
        {
            QueueDepth = io?.QueueDepth ?? defaults.QueueDepth,
            BlockSize = (int)Size(name, "Io:BlockSize", io?.BlockSize, defaults.BlockSize),
            MaxRecordLength = (int)Size(name, "Io:MaxRecordLength", io?.MaxRecordLength, defaults.MaxRecordLength),
            PinBlockSize = io?.PinBlockSize ?? defaults.PinBlockSize,
            Backend = ParseEnum<BlockDevicePolicy>(name, "Io:Backend", io?.Backend) ?? defaults.Backend,
            Share = ParseEnum<FileShare>(name, "Io:Share", io?.Share) ?? defaults.Share,
        };

        try
        {
            options.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new FileProfileException(name, $"Io:{ex.ParamName}", ex.Message, ex);
        }

        return options;
    }

    private static LineSequentialOptions BuildLine(string name, FileProfileConfiguration configuration)
    {
        // The only line-sequential preset is Micro Focus, whose distinguishing
        // behaviours are tab expansion and null escaping.
        var seed = configuration.Preset is null
            ? new LineSequentialOptions()
            : configuration.Preset.Equals("MicroFocus", StringComparison.OrdinalIgnoreCase)
                ? new LineSequentialOptions { ExpandTabs = true, NullEscape = true }
                : throw new FileProfileException(name, "Preset",
                    $"'{configuration.Preset}' is not a line-sequential preset; use MicroFocus.");

        var line = configuration.Line;

        var syntax = line?.Encoding is null
            ? seed.Syntax
            : line.Encoding.Equals("ebcdic", StringComparison.OrdinalIgnoreCase)
                ? LineSyntax.Ebcdic
                : line.Encoding.Equals("ascii", StringComparison.OrdinalIgnoreCase)
                    || line.Encoding.StartsWith("utf-8", StringComparison.OrdinalIgnoreCase)
                    ? LineSyntax.Ascii
                    : throw new FileProfileException(name, "Line:Encoding",
                        $"'{line.Encoding}' is not recognised; use ascii, utf-8 or ebcdic.");

        var options = new LineSequentialOptions
        {
            Syntax = syntax,
            Terminator = ParseEnum<LineTerminator>(name, "Line:Terminator", line?.Terminator) ?? seed.Terminator,
            AcceptEitherOnRead = line?.AcceptEitherOnRead ?? seed.AcceptEitherOnRead,
            TrimTrailingSpaces = line?.TrimTrailingSpaces ?? seed.TrimTrailingSpaces,
            StripTrailingSpaces = line?.StripTrailingSpaces ?? seed.StripTrailingSpaces,
            ExpandTabs = line?.ExpandTabs ?? seed.ExpandTabs,
            TabStopWidth = line?.TabStopWidth ?? seed.TabStopWidth,
            NullEscape = line?.NullEscape ?? seed.NullEscape,
        };

        if (options.TabStopWidth < 1)
        {
            throw new FileProfileException(
                name, "Line:TabStopWidth", $"must be positive; got {options.TabStopWidth}.");
        }

        return options;
    }

    private static FileProfile BuildFixed(
        string name, RecordFormat format, FileIoOptions io, FileProfileConfiguration configuration)
    {
        int recordLength = configuration.Fixed?.RecordLength
            ?? throw new FileProfileException(
                name, "Fixed:RecordLength", "is required for a FixedBlock profile.");

        if (recordLength < 1)
        {
            throw new FileProfileException(
                name, "Fixed:RecordLength", $"must be positive; got {recordLength}.");
        }

        int? padByte = configuration.Fixed.PadByte;

        if (padByte is < 0 or > 255)
        {
            throw new FileProfileException(
                name, "Fixed:PadByte", $"must be a byte value between 0 and 255; got {padByte}.");
        }

        return new FileProfile(name, format, io)
        {
            RecordLength = recordLength,
            PadByte = (byte)(padByte ?? 0x20),
            TrailingPartialRecord =
                ParseEnum<TrailingPartialRecord>(
                    name, "Fixed:TrailingPartialRecord", configuration.Fixed.TrailingPartialRecord)
                ?? TrailingPartialRecord.Strict,
        };
    }

    private static VariableRecordDescriptor BuildVariable(
        string name, FileProfileConfiguration configuration)
    {
        var seed = configuration.Preset switch
        {
            null => VariableRecordDescriptor.Fujitsu,
            var p when p.Equals("Fujitsu", StringComparison.OrdinalIgnoreCase) =>
                VariableRecordDescriptor.Fujitsu,
            var p when p.Equals("MicroFocus", StringComparison.OrdinalIgnoreCase) =>
                VariableRecordDescriptor.MicroFocus,
            var p => throw new FileProfileException(
                name, "Preset", $"'{p}' is not a variable-record preset; use Fujitsu or MicroFocus."),
        };

        if (configuration.Preset is null && configuration.Variable is null)
        {
            throw new FileProfileException(
                name, "Preset",
                "a VariableBlock profile needs either a Preset or an explicit Variable section.");
        }

        var v = configuration.Variable;

        var descriptor = new VariableRecordDescriptor
        {
            PrefixBytes = v?.PrefixBytes ?? seed.PrefixBytes,
            SuffixBytes = v?.SuffixBytes ?? seed.SuffixBytes,
            LengthFieldOffset = v?.LengthFieldOffset ?? seed.LengthFieldOffset,
            LengthFieldWidth = v?.LengthFieldWidth ?? seed.LengthFieldWidth,
            FlagByteOffset = v?.FlagByteOffset ?? seed.FlagByteOffset,
            Endianness = ParseEnum<Endianness>(name, "Variable:Endianness", v?.Endianness) ?? seed.Endianness,
            LengthIncludes =
                ParseEnum<LengthBasis>(name, "Variable:LengthIncludes", v?.LengthIncludes) ?? seed.LengthIncludes,
            ValidateSuffix = v?.ValidateSuffix ?? seed.ValidateSuffix,
            ValidateReservedBytes = v?.ValidateReservedBytes ?? seed.ValidateReservedBytes,
            Alignment = v?.Alignment ?? seed.Alignment,
        };

        try
        {
            descriptor.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new FileProfileException(name, "Variable", ex.Message, ex);
        }

        return descriptor;
    }

    private static long Size(string name, string key, string? text, long fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;

        if (!SizeValue.TryParse(text, out long value))
        {
            throw new FileProfileException(
                name, key,
                $"'{text}' is not a byte size. Write a number, optionally with a suffix: " +
                "1024, 4KiB, 1MiB, 2GiB.");
        }

        if (value is < 1 or > int.MaxValue)
        {
            throw new FileProfileException(
                name, key, $"must be between 1 and {int.MaxValue} bytes; got {value}.");
        }

        return value;
    }

    private static TEnum? ParseEnum<TEnum>(string name, string key, string? text)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (Enum.TryParse<TEnum>(text, ignoreCase: true, out var value) && Enum.IsDefined(value))
            return value;

        throw new FileProfileException(
            name, key,
            $"'{text}' is not valid. Expected one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }
}
