using System.Text;
using PunchIO.Devices;
using PunchIO.Framing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PunchIO.Configuration.Tests;

public class SizeValueTests
{
    [Theory]
    [InlineData("1024", 1024)]
    [InlineData("4KiB", 4096)]
    [InlineData("1MiB", 1024 * 1024)]
    [InlineData("2GiB", 2L * 1024 * 1024 * 1024)]
    [InlineData("512KB", 512 * 1024)]
    [InlineData("8M", 8 * 1024 * 1024)]
    [InlineData("64B", 64)]
    [InlineData("  1MiB  ", 1024 * 1024)]
    [InlineData("1 MiB", 1024 * 1024)]
    [InlineData("1mib", 1024 * 1024)]
    [InlineData("0", 0)]
    public void ParsesSizes(string text, long expected)
    {
        Assert.True(SizeValue.TryParse(text, out long value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void BinaryAndDecimalSpellingsAgree()
    {
        // KB means 1024 here, like KiB. A buffer size differing by 2.4% because of
        // spelling would be worse than not offering the spelling at all.
        Assert.Equal(SizeValue.Parse("1KiB"), SizeValue.Parse("1KB"));
        Assert.Equal(SizeValue.Parse("1MiB"), SizeValue.Parse("1MB"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1TiB")]      // not a supported unit
    [InlineData("-4KiB")]
    [InlineData("1.5MiB")]    // fractional sizes are not accepted
    [InlineData("MiB")]
    public void RejectsWhatItCannotParse(string? text)
    {
        Assert.False(SizeValue.TryParse(text, out _));
    }

    [Fact]
    public void RejectsRatherThanOverflowing()
    {
        Assert.False(SizeValue.TryParse("9223372036854775807GiB", out _));
    }

    [Fact]
    public void ParseThrowsWithAHelpfulMessage()
    {
        var ex = Assert.Throws<FormatException>(() => SizeValue.Parse("banana"));

        Assert.Contains("4KiB", ex.Message, StringComparison.Ordinal);
    }
}

public class FileProfileFactoryTests
{
    private static FileProfile Build(string json, string name = "Test")
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        return new FileProfileProvider(ServiceCollectionExtensions.Bind(configuration)).Get(name);
    }

    private static FileProfileException BuildError(string json, string name = "Test") =>
        Assert.Throws<FileProfileException>(() => Build(json, name));

    // ---- format selection -------------------------------------------------

    [Fact]
    public void BuildsAVariableProfileFromAPreset()
    {
        var profile = Build("""
            { "PunchIO": { "Files": { "Test": {
                "Format": "VariableBlock", "Preset": "Fujitsu" } } } }
            """);

        Assert.Equal(RecordFormat.VariableBlock, profile.Format);
        Assert.Equal(VariableRecordDescriptor.Fujitsu, profile.Variable);
    }

    [Fact]
    public void PresetSeedsAndExplicitKeysOverride()
    {
        // The one-line customisation path: everything Fujitsu except endianness.
        var profile = Build("""
            { "PunchIO": { "Files": { "Test": {
                "Format": "VariableBlock",
                "Preset": "Fujitsu",
                "Variable": { "Endianness": "LittleEndian" } } } } }
            """);

        Assert.Equal(Endianness.LittleEndian, profile.Variable.Endianness);
        Assert.Equal(4, profile.Variable.SuffixBytes);          // preserved from the preset
        Assert.True(profile.Variable.ValidateSuffix);           // preserved from the preset
    }

    [Fact]
    public void BuildsAFixedProfile()
    {
        var profile = Build("""
            { "PunchIO": { "Files": { "Test": {
                "Format": "FixedBlock",
                "Fixed": { "RecordLength": 80, "TrailingPartialRecord": "Lenient", "PadByte": 0 } } } } }
            """);

        Assert.Equal(RecordFormat.FixedBlock, profile.Format);
        Assert.Equal(80, profile.RecordLength);
        Assert.Equal(TrailingPartialRecord.Lenient, profile.TrailingPartialRecord);
        Assert.Equal(0, profile.PadByte);
    }

    [Fact]
    public void BuildsALineProfileWithEbcdicSyntax()
    {
        var profile = Build("""
            { "PunchIO": { "Files": { "Test": {
                "Format": "LineSequential",
                "Line": { "Terminator": "CrLf", "Encoding": "ebcdic", "TrimTrailingSpaces": true } } } } }
            """);

        Assert.NotNull(profile.Line);
        Assert.Equal(LineTerminator.CrLf, profile.Line.Terminator);
        Assert.Equal(0x15, profile.Line.Syntax.LineFeed);   // EBCDIC NL, not 0x0A
        Assert.True(profile.Line.TrimTrailingSpaces);
    }

    [Fact]
    public void TheMicroFocusLinePresetTurnsOnItsDistinguishingBehaviours()
    {
        var profile = Build("""
            { "PunchIO": { "Files": { "Test": {
                "Format": "LineSequential", "Preset": "MicroFocus" } } } }
            """);

        Assert.True(profile.Line!.ExpandTabs);
        Assert.True(profile.Line.NullEscape);
    }

    [Fact]
    public void AnExplicitKeyCanTurnOffWhatThePresetTurnedOn()
    {
        var profile = Build("""
            { "PunchIO": { "Files": { "Test": {
                "Format": "LineSequential",
                "Preset": "MicroFocus",
                "Line": { "ExpandTabs": false } } } } }
            """);

        Assert.False(profile.Line!.ExpandTabs);
        Assert.True(profile.Line.NullEscape);   // the rest of the preset survives
    }

    // ---- I/O options ------------------------------------------------------

    [Fact]
    public void ParsesSizeSuffixesInIoOptions()
    {
        var profile = Build("""
            { "PunchIO": { "Files": { "Test": {
                "Format": "VariableBlock",
                "Preset": "Fujitsu",
                "Io": { "QueueDepth": 8, "BlockSize": "2MiB", "MaxRecordLength": "64KiB",
                        "Backend": "ForceManaged", "PinBlockSize": true } } } } }
            """);

        Assert.Equal(8, profile.Io.QueueDepth);
        Assert.Equal(2 * 1024 * 1024, profile.Io.BlockSize);
        Assert.Equal(64 * 1024, profile.Io.MaxRecordLength);
        Assert.Equal(BlockDevicePolicy.ForceManaged, profile.Io.Backend);
        Assert.True(profile.Io.PinBlockSize);
    }

    [Fact]
    public void FallsBackToDefaultsWhenIoIsAbsent()
    {
        var profile = Build("""
            { "PunchIO": { "Files": { "Test": {
                "Format": "VariableBlock", "Preset": "Fujitsu" } } } }
            """);

        Assert.Equal(FileIoOptions.Default.QueueDepth, profile.Io.QueueDepth);
        Assert.Equal(FileIoOptions.Default.BlockSize, profile.Io.BlockSize);
    }

    // ---- failures name the profile and the key ----------------------------

    [Fact]
    public void AMissingFormatIsReportedAgainstTheKey()
    {
        var ex = BuildError("""{ "PunchIO": { "Files": { "Test": { } } } }""");

        Assert.Equal("Test", ex.ProfileName);
        Assert.Equal("Format", ex.Key);
        Assert.Contains("LineSequential", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownEnumValueListsTheValidOnes()
    {
        var ex = BuildError("""
            { "PunchIO": { "Files": { "Test": {
                "Format": "VariableBlock", "Preset": "Fujitsu",
                "Variable": { "Endianness": "Sideways" } } } } }
            """);

        Assert.Equal("Variable:Endianness", ex.Key);
        Assert.Contains("BigEndian", ex.Message, StringComparison.Ordinal);
        Assert.Contains("LittleEndian", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnparsableSizeSaysWhatAValidOneLooksLike()
    {
        var ex = BuildError("""
            { "PunchIO": { "Files": { "Test": {
                "Format": "VariableBlock", "Preset": "Fujitsu",
                "Io": { "BlockSize": "quite big" } } } } }
            """);

        Assert.Equal("Io:BlockSize", ex.Key);
        Assert.Contains("1MiB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOutOfRangeIoValueIsReportedAtResolutionTime()
    {
        // Not at byte 40 of a 400 GB file, which is the entire point.
        var ex = BuildError("""
            { "PunchIO": { "Files": { "Test": {
                "Format": "VariableBlock", "Preset": "Fujitsu",
                "Io": { "QueueDepth": 9999 } } } } }
            """);

        Assert.Equal("Test", ex.ProfileName);
        Assert.Contains("QueueDepth", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFixedProfileWithoutARecordLengthIsRefused()
    {
        var ex = BuildError("""
            { "PunchIO": { "Files": { "Test": { "Format": "FixedBlock" } } } }
            """);

        Assert.Equal("Fixed:RecordLength", ex.Key);
    }

    [Fact]
    public void AnInconsistentVariableLayoutIsRefused()
    {
        var ex = BuildError("""
            { "PunchIO": { "Files": { "Test": {
                "Format": "VariableBlock",
                "Preset": "Fujitsu",
                "Variable": { "PrefixBytes": 2 } } } } }
            """);

        // A 4-byte length field does not fit in a 2-byte prefix.
        Assert.Equal("Variable", ex.Key);
        Assert.Contains("prefix", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AVariableProfileWithNeitherPresetNorLayoutIsRefused()
    {
        var ex = BuildError("""
            { "PunchIO": { "Files": { "Test": { "Format": "VariableBlock" } } } }
            """);

        Assert.Equal("Preset", ex.Key);
    }

    [Fact]
    public void AnUnknownPresetIsRefused()
    {
        var ex = BuildError("""
            { "PunchIO": { "Files": { "Test": {
                "Format": "VariableBlock", "Preset": "Acme" } } } }
            """);

        Assert.Equal("Preset", ex.Key);
        Assert.Contains("Fujitsu", ex.Message, StringComparison.Ordinal);
    }
}

public class ProviderAndRegistrationTests
{
    private const string TwoProfiles = """
        {
          "PunchIO": {
            "Files": {
              "CustomerMaster": {
                "Format": "VariableBlock",
                "Preset": "Fujitsu",
                "Io": { "QueueDepth": 8, "BlockSize": "1MiB" }
              },
              "AuditLog": {
                "Format": "LineSequential",
                "Line": { "Terminator": "CrLf" }
              }
            }
          }
        }
        """;

    private static IConfiguration Configuration(string json) =>
        new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

    [Fact]
    public void ResolvesProfilesByName()
    {
        var provider = new FileProfileProvider(
            ServiceCollectionExtensions.Bind(Configuration(TwoProfiles)));

        Assert.Equal(2, provider.Names.Count);
        Assert.Equal(RecordFormat.VariableBlock, provider.Get("CustomerMaster").Format);
        Assert.Equal(RecordFormat.LineSequential, provider.Get("AuditLog").Format);
    }

    [Fact]
    public void ProfileNamesAreCaseInsensitive()
    {
        var provider = new FileProfileProvider(
            ServiceCollectionExtensions.Bind(Configuration(TwoProfiles)));

        Assert.NotNull(provider.Find("customermaster"));
    }

    [Fact]
    public void AnUnknownProfileListsTheKnownOnes()
    {
        var provider = new FileProfileProvider(
            ServiceCollectionExtensions.Bind(Configuration(TwoProfiles)));

        var ex = Assert.Throws<FileProfileException>(() => provider.Get("Nope"));

        Assert.Contains("CustomerMaster", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AuditLog", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindReturnsNullRatherThanThrowing()
    {
        var provider = new FileProfileProvider(
            ServiceCollectionExtensions.Bind(Configuration(TwoProfiles)));

        Assert.Null(provider.Find("Nope"));
    }

    [Fact]
    public void EveryProfileIsValidatedAtRegistrationNotOnFirstUse()
    {
        // A typo in a profile nobody has opened yet must still fail at startup.
        var configuration = Configuration("""
            { "PunchIO": { "Files": {
                "Good": { "Format": "VariableBlock", "Preset": "Fujitsu" },
                "Broken": { "Format": "VariableBlock", "Preset": "Fujitsu",
                            "Io": { "QueueDepth": -1 } } } } }
            """);

        var services = new ServiceCollection();

        var ex = Assert.Throws<FileProfileException>(() => services.AddPunchIO(configuration));

        Assert.Equal("Broken", ex.ProfileName);
    }

    [Fact]
    public void RegistersTheProviderForInjection()
    {
        var services = new ServiceCollection();
        services.AddPunchIO(Configuration(TwoProfiles));

        using var container = services.BuildServiceProvider();
        var provider = container.GetRequiredService<IFileProfileProvider>();

        Assert.Equal(8, provider.Get("CustomerMaster").Io.QueueDepth);
    }

    [Fact]
    public void AcceptsEitherTheWholeConfigurationOrTheSectionItself()
    {
        var whole = Configuration(TwoProfiles);
        var section = whole.GetSection("PunchIO");

        Assert.Equal(2, ServiceCollectionExtensions.Bind(whole).Files.Count);
        Assert.Equal(2, ServiceCollectionExtensions.Bind(section).Files.Count);
    }

    [Fact]
    public void AnEmptyConfigurationYieldsNoProfilesRatherThanFailing()
    {
        var provider = new FileProfileProvider(
            ServiceCollectionExtensions.Bind(Configuration("{ }")));

        Assert.Empty(provider.Names);

        var ex = Assert.Throws<FileProfileException>(() => provider.Get("Anything"));

        Assert.Contains("no file profiles are configured", ex.Message, StringComparison.Ordinal);
    }
}

public sealed class ProfileRoundTripTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"punchio-profile-{Guid.NewGuid():N}");

    public ProfileRoundTripTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string NewPath() => Path.Combine(_directory, $"{Guid.NewGuid():N}.dat");

    private static FileProfile Profile(string json, string name) =>
        new FileProfileProvider(ServiceCollectionExtensions.Bind(
            new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
                .Build())).Get(name);

    [Theory]
    [InlineData("""
        { "PunchIO": { "Files": { "P": {
            "Format": "VariableBlock", "Preset": "Fujitsu" } } } }
        """)]
    [InlineData("""
        { "PunchIO": { "Files": { "P": {
            "Format": "VariableBlock", "Preset": "MicroFocus" } } } }
        """)]
    [InlineData("""
        { "PunchIO": { "Files": { "P": {
            "Format": "VariableBlock", "Preset": "Fujitsu",
            "Variable": { "Endianness": "LittleEndian" } } } } }
        """)]
    [InlineData("""
        { "PunchIO": { "Files": { "P": {
            "Format": "LineSequential", "Line": { "Terminator": "CrLf" } } } } }
        """)]
    [InlineData("""
        { "PunchIO": { "Files": { "P": {
            "Format": "FixedBlock", "Fixed": { "RecordLength": 24 } } } } }
        """)]
    public async Task AConfiguredProfileReadsBackWhatItWrote(string json)
    {
        var profile = Profile(json, "P");
        var path = NewPath();

        var records = Enumerable.Range(0, 200)
            .Select(i => Encoding.ASCII.GetBytes($"record-{i:D6}"))
            .ToList();

        await using (var writer = profile.CreateWrite(path))
        {
            foreach (var record in records)
                await writer.WriteAsync(record, Ct);
        }

        var read = new List<string>();

        await using (var reader = profile.OpenRead(path))
        {
            await foreach (var record in reader.ReadAllAsync(Ct))
                read.Add(Encoding.ASCII.GetString(record.Span).TrimEnd());
        }

        Assert.Equal(records.Select(r => Encoding.ASCII.GetString(r)), read);
    }
}
