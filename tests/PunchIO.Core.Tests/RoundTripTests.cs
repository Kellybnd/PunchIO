using System.Text;
using PunchIO.Devices;
using PunchIO.Framing;
using PunchIO.Pump;
using PunchIO.Readers;
using PunchIO.Writers;
using Xunit;

namespace PunchIO.Core.Tests;

/// <summary>
/// Write then read back through real files on the real device. Everything below
/// this point has been proved against a fake; these tests close the loop.
/// </summary>
public sealed class RoundTripTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"punchio-roundtrip-{Guid.NewGuid():N}");

    public RoundTripTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string NewPath() => Path.Combine(_directory, $"{Guid.NewGuid():N}.dat");

    private static async Task WriteAsync<TEncoder>(
        string path,
        TEncoder encoder,
        IEnumerable<byte[]> records,
        int blockSize,
        int queueDepth,
        BlockDevicePolicy policy = BlockDevicePolicy.ForceManaged)
        where TEncoder : struct, IRecordEncoder
    {
        var device = BlockDeviceFactory.Open(path, FileAccess.ReadWrite, FileShare.None, policy);
        var sink = BlockSink.Create(device, queueDepth, blockSize);

        await using var writer = SequentialWriter<TEncoder>.Create(sink, encoder);

        foreach (var record in records)
            await writer.WriteAsync(record, Ct);

        await writer.CompleteAsync(Ct);
    }

    private static async Task<List<byte[]>> ReadAsync<TFramer>(
        string path,
        TFramer framer,
        int blockSize,
        int queueDepth,
        BlockDevicePolicy policy = BlockDevicePolicy.ForceManaged)
        where TFramer : struct, IRecordFramer
    {
        var device = BlockDeviceFactory.Open(path, FileAccess.Read, FileShare.Read, policy);
        var source = BlockSource.Create(device, queueDepth, blockSize);

        await using var reader = SequentialReader<TFramer>.Create(source, framer, 1 << 20);

        var records = new List<byte[]>();

        while (await reader.MoveNextAsync(Ct))
            records.Add(reader.Current.ToArray());

        return records;
    }

    private static List<byte[]> RandomRecords(int seed, int count, int minLength, int maxLength)
    {
        var rng = new Random(seed);

        return Enumerable.Range(0, count)
            .Select(_ =>
            {
                var record = new byte[rng.Next(minLength, maxLength + 1)];
                rng.NextBytes(record);
                return record;
            })
            .ToList();
    }

    public static TheoryData<int, int> Geometries => new()
    {
        { 16, 1 },
        { 64, 2 },
        { 4096, 4 },
        { 65536, 8 },
    };

    [Theory]
    [MemberData(nameof(Geometries))]
    public async Task FujitsuVariableRecordsSurviveARoundTrip(int blockSize, int queueDepth)
    {
        var records = RandomRecords(seed: 11, count: 300, minLength: 0, maxLength: 250);
        var path = NewPath();

        await WriteAsync(
            path, new VariableRecordEncoder(VariableRecordDescriptor.Fujitsu),
            records, blockSize, queueDepth);

        var actual = await ReadAsync(
            path, new VariableRecordFramer(VariableRecordDescriptor.Fujitsu), blockSize, queueDepth);

        Assert.Equal(records.Count, actual.Count);

        for (int i = 0; i < records.Count; i++)
            Assert.True(records[i].AsSpan().SequenceEqual(actual[i]), $"record {i} differed");
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public async Task MicroFocusVariableRecordsSurviveARoundTrip(int blockSize, int queueDepth)
    {
        var records = RandomRecords(seed: 23, count: 300, minLength: 0, maxLength: 250);
        var path = NewPath();

        await WriteAsync(
            path, new VariableRecordEncoder(VariableRecordDescriptor.MicroFocus),
            records, blockSize, queueDepth);

        var actual = await ReadAsync(
            path, new VariableRecordFramer(VariableRecordDescriptor.MicroFocus), blockSize, queueDepth);

        Assert.Equal(records.Count, actual.Count);

        for (int i = 0; i < records.Count; i++)
            Assert.True(records[i].AsSpan().SequenceEqual(actual[i]), $"record {i} differed");
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public async Task FixedLengthRecordsSurviveARoundTrip(int blockSize, int queueDepth)
    {
        const int recordLength = 80;

        var records = RandomRecords(seed: 37, count: 500, minLength: recordLength, maxLength: recordLength);
        var path = NewPath();

        await WriteAsync(path, new FixedBlockEncoder(recordLength), records, blockSize, queueDepth);

        var actual = await ReadAsync(path, new FixedBlockFramer(recordLength), blockSize, queueDepth);

        Assert.Equal(records.Count, actual.Count);

        for (int i = 0; i < records.Count; i++)
            Assert.True(records[i].AsSpan().SequenceEqual(actual[i]), $"record {i} differed");
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public async Task LineSequentialRecordsSurviveARoundTrip(int blockSize, int queueDepth)
    {
        var rng = new Random(53);

        var lines = Enumerable.Range(0, 400)
            .Select(_ => new string((char)('A' + rng.Next(26)), rng.Next(0, 90)))
            .ToList();

        var records = lines.Select(Encoding.ASCII.GetBytes).ToList();
        var path = NewPath();

        var options = new LineSequentialOptions { Terminator = LineTerminator.CrLf };

        await WriteAsync(path, new LineSequentialEncoder(options), records, blockSize, queueDepth);

        var actual = await ReadAsync(path, new LineSequentialFramer(options), blockSize, queueDepth);

        Assert.Equal(lines, actual.Select(r => Encoding.ASCII.GetString(r)).ToList());
    }

    [Fact]
    public async Task FixedLengthPaddingIsVisibleWhenReadingBack()
    {
        // A short record is padded on write, so it comes back at full length.
        // Stating this explicitly beats discovering it in production.
        const int recordLength = 8;
        var path = NewPath();

        await WriteAsync(
            path, new FixedBlockEncoder(recordLength),
            [Encoding.ASCII.GetBytes("AB")], blockSize: 64, queueDepth: 2);

        var actual = await ReadAsync(path, new FixedBlockFramer(recordLength), 64, 2);

        Assert.Equal("AB      ", Encoding.ASCII.GetString(Assert.Single(actual)));
    }

    [Fact]
    public async Task TheFileOnDiskIsExactlyAsLongAsTheRecordsRequire()
    {
        var records = RandomRecords(seed: 71, count: 100, minLength: 1, maxLength: 60);
        var path = NewPath();

        await WriteAsync(
            path, new VariableRecordEncoder(VariableRecordDescriptor.Fujitsu),
            records, blockSize: 4096, queueDepth: 4);

        long expected = records.Sum(r => (long)r.Length + 8);   // 4-byte prefix and suffix

        Assert.Equal(expected, new FileInfo(path).Length);
    }

    [Fact]
    public async Task AnEmptyFileRoundTrips()
    {
        var path = NewPath();

        await WriteAsync(
            path, new VariableRecordEncoder(VariableRecordDescriptor.Fujitsu),
            [], blockSize: 4096, queueDepth: 2);

        Assert.Equal(0, new FileInfo(path).Length);
        Assert.Empty(await ReadAsync(path, new VariableRecordFramer(VariableRecordDescriptor.Fujitsu), 4096, 2));
    }

    [Fact]
    public async Task WritingWithOneBlockSizeAndReadingWithAnotherStillWorks()
    {
        // Block size is a tuning knob, not part of the file format.
        var records = RandomRecords(seed: 97, count: 200, minLength: 0, maxLength: 300);
        var path = NewPath();

        await WriteAsync(
            path, new VariableRecordEncoder(VariableRecordDescriptor.Fujitsu),
            records, blockSize: 8192, queueDepth: 4);

        var actual = await ReadAsync(
            path, new VariableRecordFramer(VariableRecordDescriptor.Fujitsu),
            blockSize: 13, queueDepth: 1);

        Assert.Equal(records.Count, actual.Count);

        for (int i = 0; i < records.Count; i++)
            Assert.True(records[i].AsSpan().SequenceEqual(actual[i]), $"record {i} differed");
    }

    // ---- the unbuffered backend, end to end ------------------------------

    private void RequireUnbufferedBackend()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The unbuffered backend is Windows-only.");
        Assert.SkipUnless(
            BlockDeviceFactory.UseNativeFor(_directory),
            "The temp directory is not on a local fixed volume.");
    }

    [Fact]
    public async Task RecordsSurviveARoundTripThroughTheUnbufferedBackend()
    {
        RequireUnbufferedBackend();

        var records = RandomRecords(seed: 151, count: 5_000, minLength: 0, maxLength: 200);
        var path = NewPath();

        await WriteAsync(
            path, new VariableRecordEncoder(VariableRecordDescriptor.Fujitsu),
            records, blockSize: 65536, queueDepth: 4, BlockDevicePolicy.ForceNative);

        // The file length is the proof that the padded tail was truncated back.
        // Skip that step and the file ends in a run of zeros.
        long expected = records.Sum(r => (long)r.Length + 8);
        Assert.Equal(expected, new FileInfo(path).Length);

        var actual = await ReadAsync(
            path, new VariableRecordFramer(VariableRecordDescriptor.Fujitsu),
            blockSize: 65536, queueDepth: 4, BlockDevicePolicy.ForceNative);

        Assert.Equal(records.Count, actual.Count);

        for (int i = 0; i < records.Count; i++)
            Assert.True(records[i].AsSpan().SequenceEqual(actual[i]), $"record {i} differed");
    }

    [Fact]
    public async Task TheTwoBackendsProduceAndConsumeIdenticalFiles()
    {
        // Two backends that can disagree are two products to support. A file
        // written unbuffered must be byte-identical to one written portably, and
        // either must be readable by either.
        RequireUnbufferedBackend();

        var records = RandomRecords(seed: 173, count: 2_000, minLength: 0, maxLength: 180);

        var nativePath = NewPath();
        var managedPath = NewPath();

        await WriteAsync(
            nativePath, new VariableRecordEncoder(VariableRecordDescriptor.Fujitsu),
            records, blockSize: 65536, queueDepth: 4, BlockDevicePolicy.ForceNative);

        await WriteAsync(
            managedPath, new VariableRecordEncoder(VariableRecordDescriptor.Fujitsu),
            records, blockSize: 65536, queueDepth: 4, BlockDevicePolicy.ForceManaged);

        Assert.Equal<byte[]>(
            await File.ReadAllBytesAsync(nativePath, Ct),
            await File.ReadAllBytesAsync(managedPath, Ct));

        // Written unbuffered, read portably.
        var crossRead = await ReadAsync(
            nativePath, new VariableRecordFramer(VariableRecordDescriptor.Fujitsu),
            blockSize: 4096, queueDepth: 2, BlockDevicePolicy.ForceManaged);

        Assert.Equal(records.Count, crossRead.Count);

        for (int i = 0; i < records.Count; i++)
            Assert.True(records[i].AsSpan().SequenceEqual(crossRead[i]), $"record {i} differed");
    }

    [Fact]
    public async Task LineSequentialSurvivesTheUnbufferedBackend()
    {
        RequireUnbufferedBackend();

        var rng = new Random(191);

        var lines = Enumerable.Range(0, 3_000)
            .Select(_ => new string((char)('A' + rng.Next(26)), rng.Next(0, 120)))
            .ToList();

        var path = NewPath();
        var options = new LineSequentialOptions { Terminator = LineTerminator.CrLf };

        await WriteAsync(
            path, new LineSequentialEncoder(options), lines.Select(Encoding.ASCII.GetBytes),
            blockSize: 65536, queueDepth: 4, BlockDevicePolicy.ForceNative);

        var actual = await ReadAsync(
            path, new LineSequentialFramer(options),
            blockSize: 65536, queueDepth: 4, BlockDevicePolicy.ForceNative);

        Assert.Equal(lines, actual.Select(r => Encoding.ASCII.GetString(r)).ToList());
    }

    [Fact]
    public async Task ALargeFileOfSmallRecordsRoundTripsIntact()
    {
        // Enough records to exercise the ring wrapping many times over.
        var records = RandomRecords(seed: 131, count: 20_000, minLength: 1, maxLength: 40);
        var path = NewPath();

        await WriteAsync(
            path, new VariableRecordEncoder(VariableRecordDescriptor.Fujitsu),
            records, blockSize: 65536, queueDepth: 4);

        var actual = await ReadAsync(
            path, new VariableRecordFramer(VariableRecordDescriptor.Fujitsu), 65536, 4);

        Assert.Equal(records.Count, actual.Count);

        for (int i = 0; i < records.Count; i++)
            Assert.True(records[i].AsSpan().SequenceEqual(actual[i]), $"record {i} differed");
    }
}
