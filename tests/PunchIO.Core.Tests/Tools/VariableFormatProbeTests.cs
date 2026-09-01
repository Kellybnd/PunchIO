using PunchIO.Framing;
using PunchIO.Tools;
using Xunit;

namespace PunchIO.Core.Tests.Tools;

public sealed class VariableFormatProbeTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"punchio-probe-{Guid.NewGuid():N}");

    public VariableFormatProbeTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string NewPath() => Path.Combine(_directory, $"{Guid.NewGuid():N}.dat");

    /// <summary>Builds a file in the given layout with deliberately varied record lengths.</summary>
    private static byte[] Build(VariableRecordDescriptor descriptor, int count = 60)
    {
        var output = new List<byte>();

        for (int i = 0; i < count; i++)
        {
            int length = 1 + (i * 13 % 97);
            var framed = new byte[VariableRecordFramer.FramedLength(length, descriptor)];

            VariableRecordFramer.WriteFraming(framed, length, descriptor);

            for (int b = 0; b < length; b++)
                framed[descriptor.PrefixBytes + b] = (byte)('A' + (b % 26));

            output.AddRange(framed);
        }

        return output.ToArray();
    }

    private static VariableFormatProbeResult Best(IReadOnlyList<VariableFormatProbeResult> results) =>
        results[0];

    [Fact]
    public void IdentifiesAFujitsuFile()
    {
        var results = VariableFormatProbe.Probe(Build(VariableRecordDescriptor.Fujitsu));
        var best = Best(results);

        Assert.Equal(ProbeConfidence.High, best.Confidence);
        Assert.Equal(VariableRecordDescriptor.Fujitsu, best.Descriptor);
        Assert.Contains("Fujitsu", best.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentifiesAMicroFocusFile()
    {
        var results = VariableFormatProbe.Probe(Build(VariableRecordDescriptor.MicroFocus()));
        var best = Best(results);

        Assert.Equal(ProbeConfidence.High, best.Confidence);
        Assert.Equal(VariableRecordDescriptor.MicroFocus(), best.Descriptor);
    }

    [Fact]
    public void IdentifiesAMicroFocusFileCompleteWithItsHeader()
    {
        // The shape a real file has: the 128-byte header, then the records.
        var descriptor = VariableRecordDescriptor.MicroFocus();
        var header = new byte[MicroFocusFileHeader.Length];

        MicroFocusFileHeader.Write(header, descriptor);

        byte[] file = [.. header, .. Build(descriptor)];
        var best = Best(VariableFormatProbe.Probe(file));

        Assert.Equal(ProbeConfidence.High, best.Confidence);
        Assert.Equal(descriptor, best.Descriptor);
    }

    [Fact]
    public void DistinguishesEndiannessWhichIsTheWholePoint()
    {
        // A big-endian file must not be explained by the little-endian preset.
        var bigEndian = VariableRecordDescriptor.Fujitsu with
        {
            Endianness = Endianness.BigEndian,
        };

        var best = Best(VariableFormatProbe.Probe(Build(bigEndian)));

        Assert.Equal(ProbeConfidence.High, best.Confidence);
        Assert.Equal(Endianness.BigEndian, best.Descriptor.Endianness);
    }

    [Fact]
    public void DistinguishesTheLengthBasis()
    {
        var withFraming = VariableRecordDescriptor.Fujitsu with
        {
            LengthIncludes = LengthBasis.WithPrefixAndSuffix,
        };

        var best = Best(VariableFormatProbe.Probe(Build(withFraming)));

        Assert.Equal(ProbeConfidence.High, best.Confidence);
        Assert.Equal(LengthBasis.WithPrefixAndSuffix, best.Descriptor.LengthIncludes);
    }

    [Fact]
    public void TheCorrectLayoutOutranksEveryOtherCandidate()
    {
        var results = VariableFormatProbe.Probe(Build(VariableRecordDescriptor.Fujitsu));

        var correct = results.Single(r => r.Descriptor == VariableRecordDescriptor.Fujitsu);

        Assert.Equal(results[0], correct);
        Assert.All(
            results.Where(r => r.Descriptor != VariableRecordDescriptor.Fujitsu),
            r => Assert.True(
                r.Confidence < ProbeConfidence.High,
                $"'{r.Name}' also claimed high confidence on a Fujitsu file"));
    }

    [Fact]
    public void ReportsNoConfidenceForRandomBytes()
    {
        var random = new byte[8192];
        new Random(42).NextBytes(random);

        var results = VariableFormatProbe.Probe(random);

        Assert.All(results, r => Assert.True(
            r.Confidence <= ProbeConfidence.Low,
            $"'{r.Name}' claimed {r.Confidence} on random bytes"));
    }

    [Fact]
    public void CallsOutTheAllZerosFalsePositive()
    {
        // A run of zeros frames perfectly under every data-only layout: each
        // header reads as length zero and the walk marches straight through. It
        // is the probe's one genuine false positive, so it must not score well.
        var results = VariableFormatProbe.Probe(new byte[8192]);

        Assert.All(results, r => Assert.True(
            r.Confidence <= ProbeConfidence.Low,
            $"'{r.Name}' claimed {r.Confidence} on a file of zeros"));

        var framed = results.Where(r => r.RecordsFramed > 0).ToList();

        Assert.NotEmpty(framed);
        Assert.All(framed, r => Assert.Equal(1, r.DistinctRecordLengths));
    }

    [Fact]
    public void ReportsOnlyMediumConfidenceWhenEveryRecordIsTheSameLength()
    {
        // Uniform lengths are what a fixed-length file looks like read as
        // variable, so they are not evidence for a variable layout.
        var descriptor = VariableRecordDescriptor.Fujitsu;
        var output = new List<byte>();

        for (int i = 0; i < 50; i++)
        {
            var framed = new byte[VariableRecordFramer.FramedLength(40, descriptor)];
            VariableRecordFramer.WriteFraming(framed, 40, descriptor);

            // Real bodies, so the only degenerate thing about this file is that
            // every record is the same length. Leaving them zero-filled would
            // also trip the zeros trap and stop the test isolating anything.
            for (int b = 0; b < 40; b++)
                framed[descriptor.PrefixBytes + b] = (byte)('A' + (b % 26));

            output.AddRange(framed);
        }

        var best = Best(VariableFormatProbe.Probe(output.ToArray()));

        Assert.Equal(descriptor, best.Descriptor);
        Assert.Equal(ProbeConfidence.Medium, best.Confidence);
    }

    [Fact]
    public void ExplainsWhyACandidateWasRejected()
    {
        var results = VariableFormatProbe.Probe(Build(VariableRecordDescriptor.Fujitsu));
        var rejected = results.Where(r => r.Rejection is not null).ToList();

        Assert.NotEmpty(rejected);
        Assert.All(rejected, r => Assert.False(string.IsNullOrWhiteSpace(r.Rejection)));
    }

    [Fact]
    public void RunningOutOfSampleMidRecordIsNotARejection()
    {
        // The sample is a prefix of the file, so a truncated final record says
        // nothing about whether the layout is right.
        var full = Build(VariableRecordDescriptor.Fujitsu);
        var truncated = full.AsSpan(0, full.Length - 20).ToArray();

        var best = Best(VariableFormatProbe.Probe(truncated));

        Assert.Null(best.Rejection);
        Assert.Equal(VariableRecordDescriptor.Fujitsu, best.Descriptor);
    }

    [Fact]
    public void StopsAtTheRecordLimit()
    {
        var results = VariableFormatProbe.Probe(
            Build(VariableRecordDescriptor.Fujitsu, count: 200), maxRecords: 10);

        Assert.Equal(10, Best(results).RecordsFramed);
    }

    [Fact]
    public void AcceptsCustomCandidates()
    {
        // Narrowing the prefix means narrowing the length field with it, or the
        // layout is not self-consistent and the probe rejects it outright.
        var descriptor = VariableRecordDescriptor.Fujitsu with
        {
            PrefixBytes = 2,
            SuffixBytes = 2,
            LengthFieldWidth = 2,
        };

        var results = VariableFormatProbe.Probe(
            Build(descriptor),
            [new VariableFormatCandidate("2-byte prefix and suffix", descriptor)]);

        var only = Assert.Single(results);

        Assert.Equal(ProbeConfidence.High, only.Confidence);
    }

    [Fact]
    public async Task ProbesARealFileOnDisk()
    {
        var path = NewPath();

        await using (var writer = RecordFile.CreateVariableWrite(
            path, VariableRecordDescriptor.Fujitsu))
        {
            for (int i = 0; i < 300; i++)
                await writer.WriteAsync(new byte[1 + (i * 7 % 130)], Ct);
        }

        var results = await VariableFormatProbe.ProbeFileAsync(path, cancellationToken: Ct);
        var best = Best(results);

        Assert.Equal(ProbeConfidence.High, best.Confidence);
        Assert.Equal(VariableRecordDescriptor.Fujitsu, best.Descriptor);
    }

    [Fact]
    public async Task ProbingAnEmptyFileClaimsNothing()
    {
        var path = NewPath();
        await File.WriteAllBytesAsync(path, [], Ct);

        var results = await VariableFormatProbe.ProbeFileAsync(path, cancellationToken: Ct);

        Assert.All(results, r => Assert.Equal(ProbeConfidence.None, r.Confidence));
    }
}
