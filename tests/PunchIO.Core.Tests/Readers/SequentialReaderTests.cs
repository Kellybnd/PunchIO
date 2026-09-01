using System.Text;
using PunchIO.Core.Tests.Pump;
using PunchIO.Framing;
using PunchIO.Pump;
using PunchIO.Readers;
using Xunit;

namespace PunchIO.Core.Tests.Readers;

public class SequentialReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SequentialReader<TFramer> Open<TFramer>(
        byte[] content,
        TFramer framer,
        int blockSize,
        int queueDepth = 2,
        int maxRecordLength = 65536)
        where TFramer : struct, IRecordFramer
    {
        var device = new FakeBlockDevice(content);
        var source = BlockSource.Create(device, queueDepth, blockSize);

        return SequentialReader<TFramer>.Create(source, framer, maxRecordLength);
    }

    private static async Task<List<byte[]>> ReadAllAsync<TFramer>(SequentialReader<TFramer> reader)
        where TFramer : struct, IRecordFramer
    {
        var records = new List<byte[]>();

        while (await reader.MoveNextAsync(Ct))
            records.Add(reader.Current.ToArray());

        return records;
    }

    private static byte[] Record(int length, int seed)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++) b[i] = (byte)(i * 31 + seed + 1);
        return b;
    }

    private static byte[] FujitsuFile(IEnumerable<byte[]> records)
    {
        var descriptor = VariableRecordDescriptor.Fujitsu;
        var output = new List<byte>();

        foreach (var record in records)
        {
            var framed = new byte[VariableRecordFramer.FramedLength(record.Length, descriptor)];
            VariableRecordFramer.WriteFraming(framed, record.Length, descriptor);
            record.CopyTo(framed, descriptor.PrefixBytes);
            output.AddRange(framed);
        }

        return output.ToArray();
    }

    // ---- fixed block ----------------------------------------------------

    [Theory]
    [InlineData(64)]    // records straddle blocks
    [InlineData(13)]    // block exactly one record
    [InlineData(26)]    // block exactly two records
    [InlineData(1)]     // pathological: every record spans thirteen blocks
    [InlineData(1000)]  // one block holds everything
    public async Task ReadsFixedLengthRecordsAtAnyBlockSize(int blockSize)
    {
        var records = Enumerable.Range(0, 40).Select(i => Record(13, i)).ToList();
        var content = records.SelectMany(r => r).ToArray();

        await using var reader = Open(content, new FixedBlockFramer(13), blockSize);
        var actual = await ReadAllAsync(reader);

        Assert.Equal(40, actual.Count);
        Assert.All(actual.Select((r, i) => (r, i)), t => Assert.Equal<byte[]>(records[t.i], t.r));
    }

    [Fact]
    public async Task ReportsRecordNumberAndOffsetForFixedRecords()
    {
        var content = Enumerable.Range(0, 10).SelectMany(i => Record(20, i)).ToArray();

        await using var reader = Open(content, new FixedBlockFramer(20), blockSize: 32);

        for (int i = 0; i < 10; i++)
        {
            Assert.True(await reader.MoveNextAsync(Ct));
            Assert.Equal(i + 1, reader.RecordNumber);
            Assert.Equal(i * 20, reader.RecordOffset);
        }

        Assert.False(await reader.MoveNextAsync(Ct));
    }

    // ---- line sequential ------------------------------------------------

    [Fact]
    public async Task JoinsARecordWhoseCarriageReturnAndLineFeedAreInDifferentBlocks()
    {
        // Block size 4 against 5-byte records puts the CR at the end of a block
        // and its LF at the head of the next, every single time.
        var content = Encoding.ASCII.GetBytes("AAA\r\nBBB\r\nCCC\r\n");
        var framer = new LineSequentialFramer(new LineSequentialOptions());

        await using var reader = Open(content, framer, blockSize: 4);
        var records = await ReadAllAsync(reader);

        Assert.Equal(
            new[] { "AAA", "BBB", "CCC" },
            records.Select(r => Encoding.ASCII.GetString(r)));
    }

    [Fact]
    public async Task ReportsOffsetsForLineSequentialRecords()
    {
        var content = Encoding.ASCII.GetBytes("AAA\r\nBBB\r\nCCC\r\n");
        var framer = new LineSequentialFramer(new LineSequentialOptions());

        await using var reader = Open(content, framer, blockSize: 4);
        var offsets = new List<long>();

        while (await reader.MoveNextAsync(Ct))
            offsets.Add(reader.RecordOffset);

        Assert.Equal(new long[] { 0, 5, 10 }, offsets);
    }

    [Fact]
    public async Task ReadsAFinalLineWithNoTerminator()
    {
        var content = Encoding.ASCII.GetBytes("ONE\nTWO\nTHREE");
        var framer = new LineSequentialFramer(new LineSequentialOptions());

        await using var reader = Open(content, framer, blockSize: 5);
        var records = await ReadAllAsync(reader);

        Assert.Equal(
            new[] { "ONE", "TWO", "THREE" },
            records.Select(r => Encoding.ASCII.GetString(r)));
    }

    [Fact]
    public async Task ReadsEmptyLines()
    {
        var content = Encoding.ASCII.GetBytes("A\n\n\nB\n");
        var framer = new LineSequentialFramer(new LineSequentialOptions());

        await using var reader = Open(content, framer, blockSize: 2);
        var records = await ReadAllAsync(reader);

        Assert.Equal(new[] { "A", "", "", "B" }, records.Select(r => Encoding.ASCII.GetString(r)));
    }

    // ---- variable block -------------------------------------------------

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(4096)]
    public async Task ReadsFujitsuVariableRecordsAtAnyBlockSize(int blockSize)
    {
        var records = Enumerable.Range(0, 30).Select(i => Record(1 + (i * 7 % 61), i)).ToList();
        var content = FujitsuFile(records);
        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);

        await using var reader = Open(content, framer, blockSize);
        var actual = await ReadAllAsync(reader);

        Assert.Equal(records.Count, actual.Count);
        Assert.All(actual.Select((r, i) => (r, i)), t => Assert.Equal<byte[]>(records[t.i], t.r));
    }

    [Fact]
    public async Task JoinsARecordThatSpansMoreThanTwoBlocks()
    {
        // 200 data bytes plus 8 framing bytes against a 32-byte block: the record
        // needs seven blocks stitched together before it can be returned.
        var records = new List<byte[]> { Record(200, 1), Record(5, 2), Record(200, 3) };
        var content = FujitsuFile(records);
        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);

        await using var reader = Open(content, framer, blockSize: 32);
        var actual = await ReadAllAsync(reader);

        Assert.Equal(3, actual.Count);
        Assert.Equal<byte[]>(records[0], actual[0]);
        Assert.Equal<byte[]>(records[1], actual[1]);
        Assert.Equal<byte[]>(records[2], actual[2]);
    }

    [Fact]
    public async Task ReadsMicroFocusVariableRecords()
    {
        var descriptor = VariableRecordDescriptor.MicroFocus();
        var records = Enumerable.Range(0, 25).Select(i => Record(1 + (i * 5 % 43), i)).ToList();

        var output = new List<byte>();
        foreach (var record in records)
        {
            var framed = new byte[VariableRecordFramer.FramedLength(record.Length, descriptor)];
            VariableRecordFramer.WriteFraming(framed, record.Length, descriptor);
            record.CopyTo(framed, descriptor.PrefixBytes);
            output.AddRange(framed);
        }

        await using var reader = Open(
            output.ToArray(), new VariableRecordFramer(descriptor), blockSize: 16);

        var actual = await ReadAllAsync(reader);

        Assert.Equal(records.Count, actual.Count);
        Assert.All(actual.Select((r, i) => (r, i)), t => Assert.Equal<byte[]>(records[t.i], t.r));
    }

    // ---- failure modes --------------------------------------------------

    [Fact]
    public async Task AnEmptyFileYieldsNoRecords()
    {
        await using var reader = Open([], new FixedBlockFramer(10), blockSize: 64);

        Assert.False(await reader.MoveNextAsync(Ct));
        Assert.True(reader.Current.IsEmpty);
    }

    [Fact]
    public async Task CompletionIsSticky()
    {
        await using var reader = Open(Record(10, 0), new FixedBlockFramer(10), blockSize: 64);

        Assert.True(await reader.MoveNextAsync(Ct));
        Assert.False(await reader.MoveNextAsync(Ct));
        Assert.False(await reader.MoveNextAsync(Ct));
    }

    [Fact]
    public async Task ReportsAFormatErrorWithTheOffendingByteOffset()
    {
        // Three good records, then a suffix that disagrees with its prefix.
        var records = new List<byte[]> { Record(10, 1), Record(10, 2), Record(10, 3) };
        var content = FujitsuFile(records).ToArray();

        int badRecordOffset = 18 * 2;   // two whole records of 10 + 8 framing bytes
        content[badRecordOffset + 4 + 10 + 3] = 0xFF;   // corrupt the trailing length

        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);
        await using var reader = Open(content, framer, blockSize: 16);

        Assert.True(await reader.MoveNextAsync(Ct));
        Assert.True(await reader.MoveNextAsync(Ct));

        var ex = await Assert.ThrowsAsync<RecordFormatException>(
            async () => await reader.MoveNextAsync(Ct));

        Assert.Equal(badRecordOffset, ex.ByteOffset);
        Assert.Equal(FileStatus.PermanentError, ex.Status);
    }

    [Fact]
    public async Task RefusesToBufferARecordBeyondTheConfiguredLimit()
    {
        // A corrupt length prefix must fail cleanly rather than trying to
        // allocate whatever the file claims.
        var content = FujitsuFile([Record(4000, 1)]);
        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);

        await using var reader = Open(content, framer, blockSize: 64, maxRecordLength: 256);

        var ex = await Assert.ThrowsAsync<RecordTooLargeException>(
            async () => await reader.MoveNextAsync(Ct));

        Assert.Equal(256, ex.MaxRecordLength);
        Assert.Equal(0, ex.ByteOffset);
    }

    [Theory]
    [InlineData(64, 4096)]
    [InlineData(128, 8192)]
    [InlineData(256, 65536)]
    public async Task ASmallMaxRecordLengthWorksAgainstALargeBlockSize(
        int maxRecordLength, int blockSize)
    {
        // The stitch buffer must be sized by the record, not by the block. An
        // implementation that swallows a whole block per extension makes every
        // configuration with MaxRecordLength below BlockSize fail on the first
        // straddling record, however small the records actually are.
        var records = Enumerable.Range(0, 200)
            .Select(i => Record(1 + (i * 13 % 40), i))
            .ToList();

        var content = FujitsuFile(records);
        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);

        await using var reader = Open(content, framer, blockSize, queueDepth: 2, maxRecordLength);
        var actual = await ReadAllAsync(reader);

        Assert.Equal(records.Count, actual.Count);

        for (int i = 0; i < records.Count; i++)
            Assert.True(records[i].AsSpan().SequenceEqual(actual[i]), $"record {i} differed");
    }

    [Fact]
    public async Task ARecordSpanningManyBlocksStillHonoursTheRecordLimit()
    {
        // The limit counts bytes buffered for one record, so a record that needs
        // several extensions is still rejected at exactly the configured size.
        var content = FujitsuFile([Record(300, 1)]);
        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);

        await using var reader = Open(content, framer, blockSize: 16, maxRecordLength: 100);

        var ex = await Assert.ThrowsAsync<RecordTooLargeException>(
            async () => await reader.MoveNextAsync(Ct));

        Assert.Equal(100, ex.MaxRecordLength);
    }

    [Fact]
    public async Task RejectsUseAfterDisposal()
    {
        var reader = Open(Record(10, 0), new FixedBlockFramer(10), blockSize: 64);
        await reader.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await reader.MoveNextAsync(Ct));
    }

    [Fact]
    public async Task DisposeIsIdempotent()
    {
        var reader = Open(Record(10, 0), new FixedBlockFramer(10), blockSize: 64);

        await reader.DisposeAsync();
        await reader.DisposeAsync();
    }

    // ---- enumeration ----------------------------------------------------

    [Fact]
    public async Task ReadAllAsyncEnumeratesEveryRecord()
    {
        var records = Enumerable.Range(0, 25).Select(i => Record(17, i)).ToList();
        var content = records.SelectMany(r => r).ToArray();

        await using var reader = Open(content, new FixedBlockFramer(17), blockSize: 40);

        var actual = new List<byte[]>();
        await foreach (var record in reader.ReadAllAsync(Ct))
            actual.Add(record.ToArray());

        Assert.Equal(records.Count, actual.Count);
        Assert.All(actual.Select((r, i) => (r, i)), t => Assert.Equal<byte[]>(records[t.i], t.r));
    }

#if DEBUG
    [Fact]
    public async Task RetainingARecordPastTheNextMoveFailsLoudlyInDebug()
    {
        // The zero-copy contract, enforced. In Release this record would quietly
        // become another record's bytes, which is exactly the bug worth catching.
        var content = Enumerable.Range(0, 5).SelectMany(i => Record(10, i)).ToArray();

        await using var reader = Open(content, new FixedBlockFramer(10), blockSize: 64);

        Assert.True(await reader.MoveNextAsync(Ct));
        var stale = reader.Current;

        Assert.True(await reader.MoveNextAsync(Ct));

        Assert.Throws<InvalidOperationException>(() => stale.Span[0]);
    }
#endif

    // ---- property matrix ------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1337)]
    public async Task VariableRecordsSurviveRandomGeometries(int seed)
    {
        // Random record sizes crossed with awkward block sizes and queue depths.
        // This is the combination that actually shakes out stitch-buffer bugs.
        var rng = new Random(seed);

        var records = Enumerable.Range(0, 60)
            .Select(i => Record(rng.Next(0, 120), i))
            .ToList();

        var content = FujitsuFile(records);

        foreach (int blockSize in new[] { 8, 13, 31, 64, 127, 512 })
        {
            foreach (int queueDepth in new[] { 1, 3, 8 })
            {
                await using var reader = Open(
                    content,
                    new VariableRecordFramer(VariableRecordDescriptor.Fujitsu),
                    blockSize,
                    queueDepth);

                var actual = await ReadAllAsync(reader);

                Assert.Equal(records.Count, actual.Count);

                for (int i = 0; i < records.Count; i++)
                {
                    Assert.True(records[i].AsSpan().SequenceEqual(actual[i]),
                        $"record {i} differed at blockSize={blockSize}, queueDepth={queueDepth}");
                }
            }
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(99)]
    public async Task LineSequentialSurvivesRandomGeometries(int seed)
    {
        var rng = new Random(seed);

        var lines = Enumerable.Range(0, 80)
            .Select(_ => new string((char)('A' + rng.Next(26)), rng.Next(0, 40)))
            .ToList();

        var content = Encoding.ASCII.GetBytes(string.Concat(lines.Select(l => l + "\r\n")));

        foreach (int blockSize in new[] { 1, 2, 3, 16, 61, 1024 })
        {
            await using var reader = Open(
                content,
                new LineSequentialFramer(new LineSequentialOptions()),
                blockSize,
                queueDepth: 2);

            var actual = await ReadAllAsync(reader);

            Assert.Equal(
                lines,
                actual.Select(r => Encoding.ASCII.GetString(r)).ToList());
        }
    }
}
