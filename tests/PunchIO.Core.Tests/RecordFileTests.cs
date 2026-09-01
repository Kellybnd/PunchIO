using System.Text;
using PunchIO.Devices;
using PunchIO.Framing;
using Xunit;

namespace PunchIO.Core.Tests;

public sealed class RecordFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"punchio-api-{Guid.NewGuid():N}");

    public RecordFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string NewPath() => Path.Combine(_directory, $"{Guid.NewGuid():N}.dat");

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    // ---- the shape of ordinary use ---------------------------------------

    [Fact]
    public async Task ReadsAndWritesVariableRecordsWithNoAssemblyRequired()
    {
        var path = NewPath();
        var records = new[] { Ascii("first"), Ascii("second"), Ascii("third") };

        await using (var writer = RecordFile.CreateVariableWrite(
            path, VariableRecordDescriptor.Fujitsu))
        {
            foreach (var record in records)
                await writer.WriteAsync(record, Ct);
        }

        var read = new List<string>();

        await using (var reader = RecordFile.OpenVariableRead(
            path, VariableRecordDescriptor.Fujitsu))
        {
            await foreach (var record in reader.ReadAllAsync(Ct))
                read.Add(Encoding.ASCII.GetString(record.Span));
        }

        Assert.Equal(["first", "second", "third"], read);
    }

    [Fact]
    public async Task ReadsAndWritesFixedLengthRecords()
    {
        var path = NewPath();

        await using (var writer = RecordFile.CreateFixedBlockWrite(path, recordLength: 8))
        {
            await writer.WriteAsync(Ascii("ABC"), Ct);
            await writer.WriteAsync(Ascii("DEFGHIJK"), Ct);
        }

        await using var reader = RecordFile.OpenFixedBlockRead(path, recordLength: 8);
        var records = new List<string>();

        await foreach (var record in reader.ReadAllAsync(Ct))
            records.Add(Encoding.ASCII.GetString(record.Span));

        Assert.Equal(["ABC     ", "DEFGHIJK"], records);
    }

    [Fact]
    public async Task ReadsAndWritesLineSequentialRecords()
    {
        var path = NewPath();

        await using (var writer = RecordFile.CreateLineSequentialWrite(path))
        {
            await writer.WriteAsync(Ascii("one"), Ct);
            await writer.WriteAsync(Ascii("two   "), Ct);   // trailing spaces stripped on write
        }

        Assert.Equal("one\ntwo\n", await File.ReadAllTextAsync(path, Ct));

        await using var reader = RecordFile.OpenLineSequentialRead(path);
        var records = new List<string>();

        await foreach (var record in reader.ReadAllAsync(Ct))
            records.Add(Encoding.ASCII.GetString(record.Span));

        Assert.Equal(["one", "two"], records);
    }

    // ---- the transforms the byte reader leaves alone ----------------------

    [Fact]
    public async Task ExpandsTabsOnReadWhenConfigured()
    {
        // SequentialReader hands back raw bytes by design; this is the facade
        // that applies the rewriting, and only when it was asked for.
        var path = NewPath();
        await File.WriteAllTextAsync(path, "AB\tX\n", Ct);

        var lineOptions = new LineSequentialOptions { ExpandTabs = true, TabStopWidth = 8 };

        await using var reader = RecordFile.OpenLineSequentialRead(path, lineOptions);

        Assert.True(await reader.MoveNextAsync(Ct));
        Assert.Equal("AB      X", Encoding.ASCII.GetString(reader.Current.Span));
    }

    [Fact]
    public async Task NullEscapedControlBytesRoundTrip()
    {
        var path = NewPath();
        var lineOptions = new LineSequentialOptions
        {
            NullEscape = true,
            StripTrailingSpaces = false,
        };

        byte[] record = [0x41, 0x09, 0x0D, 0x42];   // A, TAB, CR, B

        await using (var writer = RecordFile.CreateLineSequentialWrite(path, lineOptions))
            await writer.WriteAsync(record, Ct);

        await using var reader = RecordFile.OpenLineSequentialRead(path, lineOptions);

        Assert.True(await reader.MoveNextAsync(Ct));
        Assert.Equal<byte[]>(record, reader.Current.ToArray());
    }

    [Fact]
    public async Task LeavesRecordsUntouchedWhenNoTransformIsConfigured()
    {
        var path = NewPath();
        await File.WriteAllTextAsync(path, "A\tB\n", Ct);

        await using var reader = RecordFile.OpenLineSequentialRead(path);

        Assert.True(await reader.MoveNextAsync(Ct));
        Assert.Equal("A\tB", Encoding.ASCII.GetString(reader.Current.Span));
    }

    // ---- create semantics -------------------------------------------------

    [Fact]
    public async Task CreatingOverALongerFileDoesNotLeaveTheOldTailBehind()
    {
        var path = NewPath();

        await using (var writer = RecordFile.CreateVariableWrite(
            path, VariableRecordDescriptor.Fujitsu))
        {
            for (int i = 0; i < 50; i++)
                await writer.WriteAsync(Ascii($"record number {i}"), Ct);
        }

        long longLength = new FileInfo(path).Length;

        await using (var writer = RecordFile.CreateVariableWrite(
            path, VariableRecordDescriptor.Fujitsu))
        {
            await writer.WriteAsync(Ascii("just one"), Ct);
        }

        Assert.True(new FileInfo(path).Length < longLength);

        await using var reader = RecordFile.OpenVariableRead(
            path, VariableRecordDescriptor.Fujitsu);

        Assert.True(await reader.MoveNextAsync(Ct));
        Assert.Equal("just one", Encoding.ASCII.GetString(reader.Current.Span));
        Assert.False(await reader.MoveNextAsync(Ct));
    }

    [Fact]
    public void OpeningAMissingFileReportsStatus35()
    {
        var ex = Assert.Throws<PunchIoException>(
            () => RecordFile.OpenVariableRead(NewPath(), VariableRecordDescriptor.Fujitsu));

        Assert.Equal(FileStatus.FileNotFound, ex.Status);
    }

    // ---- options ----------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public void RejectsAnOutOfRangeQueueDepth(int queueDepth)
    {
        var options = new FileIoOptions { QueueDepth = queueDepth };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Theory]
    [InlineData(1024)]              // below the floor
    [InlineData(512 * 1024 * 1024)] // above the ceiling
    public void RejectsAnOutOfRangeBlockSize(int blockSize)
    {
        var options = new FileIoOptions { BlockSize = blockSize };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void DefaultsMatchTheDocumentedStartingPoint()
    {
        var options = FileIoOptions.Default;

        Assert.Equal(4, options.QueueDepth);
        Assert.Equal(1024 * 1024, options.BlockSize);
        Assert.Equal(64 * 1024, options.MaxRecordLength);
        Assert.Equal(BlockDevicePolicy.Auto, options.Backend);
    }

    [Fact]
    public void BlockSizeIsRoundedUpToTheRecordLengthToEliminateStraddling()
    {
        var options = new FileIoOptions { BlockSize = 4096 };

        // 4096 is not a multiple of 100; 4100 is, and records then never straddle.
        Assert.Equal(4100, options.ResolveBlockSize(alignment: 1, recordLength: 100));
    }

    [Fact]
    public void SectorAlignmentWinsWhenTheTwoRoundingRulesConflict()
    {
        // A 4096-byte sector against a 4095-byte record has a least common
        // multiple over sixteen megabytes. Alignment is correctness and is kept;
        // the straddle optimisation is dropped and the reader stitches instead.
        var options = new FileIoOptions { BlockSize = 8192 };

        int resolved = options.ResolveBlockSize(alignment: 4096, recordLength: 4095);

        Assert.Equal(8192, resolved);
        Assert.Equal(0, resolved % 4096);
    }

    [Fact]
    public void PinningTheBlockSizeKeepsItExceptForMandatoryAlignment()
    {
        var options = new FileIoOptions { BlockSize = 8192, PinBlockSize = true };

        Assert.Equal(8192, options.ResolveBlockSize(alignment: 1, recordLength: 100));
        Assert.Equal(8192, options.ResolveBlockSize(alignment: 4096, recordLength: 100));
    }

    [Fact]
    public async Task HonoursCustomOptionsEndToEnd()
    {
        var path = NewPath();
        var options = new FileIoOptions
        {
            QueueDepth = 8,
            BlockSize = 8192,
            MaxRecordLength = 4096,
            Backend = BlockDevicePolicy.ForceManaged,
        };

        var records = Enumerable.Range(0, 2_000)
            .Select(i => Ascii($"record {i} with some padding to make it interesting"))
            .ToList();

        await using (var writer = RecordFile.CreateVariableWrite(
            path, VariableRecordDescriptor.Fujitsu, options))
        {
            foreach (var record in records)
                await writer.WriteAsync(record, Ct);
        }

        await using var reader = RecordFile.OpenVariableRead(
            path, VariableRecordDescriptor.Fujitsu, options);

        var actual = new List<byte[]>();

        await foreach (var record in reader.ReadAllAsync(Ct))
            actual.Add(record.ToArray());

        Assert.Equal(records.Count, actual.Count);

        for (int i = 0; i < records.Count; i++)
            Assert.True(records[i].AsSpan().SequenceEqual(actual[i]), $"record {i} differed");
    }

    [Fact]
    public async Task ReportsAnOversizedRecordAgainstTheConfiguredLimit()
    {
        var path = NewPath();
        var writeOptions = new FileIoOptions { MaxRecordLength = 1 << 20 };

        await using (var writer = RecordFile.CreateVariableWrite(
            path, VariableRecordDescriptor.Fujitsu, writeOptions))
        {
            await writer.WriteAsync(new byte[5000], Ct);
        }

        var readOptions = new FileIoOptions { MaxRecordLength = 256 };

        await using var reader = RecordFile.OpenVariableRead(
            path, VariableRecordDescriptor.Fujitsu, readOptions);

        var ex = await Assert.ThrowsAsync<RecordTooLargeException>(
            async () => await reader.MoveNextAsync(Ct));

        Assert.Equal(256, ex.MaxRecordLength);
    }
}
