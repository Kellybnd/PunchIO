using System.Text;
using PunchIO.Core.Tests.Pump;
using PunchIO.Framing;
using PunchIO.Pump;
using PunchIO.Writers;
using Xunit;

namespace PunchIO.Core.Tests.Writers;

public class SequentialWriterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<byte[]> WriteAllAsync<TEncoder>(
        TEncoder encoder,
        IEnumerable<byte[]> records,
        int blockSize = 64,
        int queueDepth = 2)
        where TEncoder : struct, IRecordEncoder
    {
        var device = new FakeBlockDevice();
        var sink = BlockSink.Create(device, queueDepth, blockSize);

        await using (var writer = SequentialWriter<TEncoder>.Create(sink, encoder))
        {
            foreach (var record in records)
                await writer.WriteAsync(record, Ct);

            await writer.CompleteAsync(Ct);
        }

        return device.Content;
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    // ---- fixed block ----------------------------------------------------

    [Fact]
    public async Task WritesFixedLengthRecordsBackToBack()
    {
        var content = await WriteAllAsync(
            new FixedBlockEncoder(4),
            [Ascii("ABCD"), Ascii("EFGH"), Ascii("IJKL")]);

        Assert.Equal("ABCDEFGHIJKL", Encoding.ASCII.GetString(content));
    }

    [Fact]
    public async Task PadsAShortFixedRecordWithSpacesByDefault()
    {
        var content = await WriteAllAsync(new FixedBlockEncoder(6), [Ascii("AB"), Ascii("CDEF")]);

        Assert.Equal("AB    CDEF  ", Encoding.ASCII.GetString(content));
    }

    [Fact]
    public async Task PadsWithTheConfiguredByteForBinaryFiles()
    {
        var content = await WriteAllAsync(new FixedBlockEncoder(4, padByte: 0), [Ascii("AB")]);

        Assert.Equal<byte[]>([0x41, 0x42, 0x00, 0x00], content);
    }

    [Fact]
    public async Task RefusesARecordLongerThanTheFixedLength()
    {
        // Truncating would discard data silently, which is worse than failing.
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await WriteAllAsync(new FixedBlockEncoder(4), [Ascii("TOOLONG")]));
    }

    [Fact]
    public async Task WritesAnEmptyFixedRecordAsAFullPaddedSlot()
    {
        var content = await WriteAllAsync(new FixedBlockEncoder(3), [[], Ascii("XY")]);

        Assert.Equal("   XY ", Encoding.ASCII.GetString(content));
    }

    // ---- line sequential ------------------------------------------------

    [Fact]
    public async Task WritesLineFeedTerminatedRecords()
    {
        var content = await WriteAllAsync(
            new LineSequentialEncoder(new LineSequentialOptions()),
            [Ascii("ONE"), Ascii("TWO")]);

        Assert.Equal("ONE\nTWO\n", Encoding.ASCII.GetString(content));
    }

    [Fact]
    public async Task WritesCarriageReturnLineFeedWhenConfigured()
    {
        var options = new LineSequentialOptions { Terminator = LineTerminator.CrLf };

        var content = await WriteAllAsync(new LineSequentialEncoder(options), [Ascii("A"), Ascii("B")]);

        Assert.Equal("A\r\nB\r\n", Encoding.ASCII.GetString(content));
    }

    [Fact]
    public async Task StripsTrailingSpacesByDefault()
    {
        // COBOL line-sequential behavior: the record area is blank-filled, and
        // those blanks are not part of the file.
        var content = await WriteAllAsync(
            new LineSequentialEncoder(new LineSequentialOptions()),
            [Ascii("DATA      ")]);

        Assert.Equal("DATA\n", Encoding.ASCII.GetString(content));
    }

    [Fact]
    public async Task KeepsTrailingSpacesWhenStrippingIsDisabled()
    {
        var options = new LineSequentialOptions { StripTrailingSpaces = false };

        var content = await WriteAllAsync(new LineSequentialEncoder(options), [Ascii("DATA  ")]);

        Assert.Equal("DATA  \n", Encoding.ASCII.GetString(content));
    }

    [Fact]
    public async Task AnAllSpaceRecordBecomesAnEmptyLine()
    {
        var content = await WriteAllAsync(
            new LineSequentialEncoder(new LineSequentialOptions()),
            [Ascii("     ")]);

        Assert.Equal("\n", Encoding.ASCII.GetString(content));
    }

    [Fact]
    public async Task EscapesControlBytesWhenNullEscapingIsOn()
    {
        var options = new LineSequentialOptions { NullEscape = true, StripTrailingSpaces = false };

        var content = await WriteAllAsync(new LineSequentialEncoder(options), [[0x41, 0x09, 0x42]]);

        // A, escaped TAB, B, terminator.
        Assert.Equal<byte[]>([0x41, 0x00, 0x09, 0x42, 0x0A], content);
    }

    [Fact]
    public async Task WritesEbcdicTerminatorsAndTrimsEbcdicSpaces()
    {
        var options = new LineSequentialOptions { Syntax = LineSyntax.Ebcdic };

        var content = await WriteAllAsync(
            new LineSequentialEncoder(options),
            [[0xC8, 0xC9, 0x40, 0x40]]);

        Assert.Equal<byte[]>([0xC8, 0xC9, 0x15], content);
    }

    // ---- variable block -------------------------------------------------

    [Fact]
    public async Task WritesFujitsuRecordsWithAMatchingPrefixAndSuffix()
    {
        var content = await WriteAllAsync(
            new VariableRecordEncoder(VariableRecordDescriptor.Fujitsu),
            [Ascii("ABC")]);

        Assert.Equal<byte[]>(
            [0, 0, 0, 3, 0x41, 0x42, 0x43, 0, 0, 0, 3],
            content);
    }

    [Fact]
    public async Task WritesMicroFocusRecordsWithAFourByteHeaderAndNoSuffix()
    {
        var content = await WriteAllAsync(
            new VariableRecordEncoder(VariableRecordDescriptor.MicroFocus),
            [Ascii("ABC")]);

        Assert.Equal<byte[]>([0, 3, 0, 0, 0x41, 0x42, 0x43], content);
    }

    [Fact]
    public async Task WritesAZeroLengthVariableRecord()
    {
        var content = await WriteAllAsync(
            new VariableRecordEncoder(VariableRecordDescriptor.Fujitsu),
            [[]]);

        Assert.Equal<byte[]>([0, 0, 0, 0, 0, 0, 0, 0], content);
    }

    [Fact]
    public async Task PadsVariableRecordsToTheConfiguredAlignment()
    {
        var descriptor = VariableRecordDescriptor.Fujitsu with { Alignment = 4 };

        var content = await WriteAllAsync(new VariableRecordEncoder(descriptor), [Ascii("ABC")]);

        // 4 prefix + 3 data + 4 suffix = 11, padded up to 12.
        Assert.Equal(12, content.Length);
        Assert.Equal<byte[]>([0, 0, 0, 3, 0x41, 0x42, 0x43, 0, 0, 0, 3, 0], content);
    }

    // ---- bookkeeping ----------------------------------------------------

    [Fact]
    public async Task CountsRecordsAndBytes()
    {
        var device = new FakeBlockDevice();
        var sink = BlockSink.Create(device, queueDepth: 2, blockSize: 64);

        await using var writer = SequentialWriter<FixedBlockEncoder>.Create(
            sink, new FixedBlockEncoder(10));

        Assert.Equal(0, writer.RecordNumber);

        await writer.WriteAsync(Ascii("ABCDEFGHIJ"), Ct);
        await writer.WriteAsync(Ascii("KLMNOPQRST"), Ct);

        Assert.Equal(2, writer.RecordNumber);
        Assert.Equal(20, writer.Length);
    }

    [Fact]
    public async Task RejectsUseAfterDisposal()
    {
        var sink = BlockSink.Create(new FakeBlockDevice(), queueDepth: 2, blockSize: 64);
        var writer = SequentialWriter<FixedBlockEncoder>.Create(sink, new FixedBlockEncoder(4));

        await writer.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await writer.WriteAsync(Ascii("ABCD"), Ct));
    }

    [Fact]
    public async Task DisposeWithoutCompleteStillWritesEveryRecord()
    {
        var device = new FakeBlockDevice();
        var sink = BlockSink.Create(device, queueDepth: 2, blockSize: 64);

        await using (var writer = SequentialWriter<FixedBlockEncoder>.Create(
            sink, new FixedBlockEncoder(4)))
        {
            await writer.WriteAsync(Ascii("ABCD"), Ct);
            await writer.WriteAsync(Ascii("EFGH"), Ct);
        }

        Assert.Equal("ABCDEFGH", Encoding.ASCII.GetString(device.Content));
    }

    [Theory]
    [InlineData(65_535, true)]    // the largest a 2-byte length field can hold
    [InlineData(65_536, false)]   // one byte too many
    [InlineData(80_000, false)]
    public async Task RefusesARecordTheLengthFieldCannotRepresent(int length, bool shouldSucceed)
    {
        // Micro Focus stores the length in two bytes. Writing more than 65,535
        // bytes would store the low 16 bits and produce a file that reframes
        // into garbage on read, so it is refused rather than truncated.
        var encoder = new VariableRecordEncoder(VariableRecordDescriptor.MicroFocus);
        var record = new byte[length];

        if (shouldSucceed)
        {
            var content = await WriteAllAsync(encoder, [record], blockSize: 1 << 20);
            Assert.Equal(length + 4, content.Length);
            return;
        }

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            async () => await WriteAllAsync(encoder, [record], blockSize: 1 << 20));

        Assert.Contains("length field", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsTheLargestRecordEachLayoutCanDescribe()
    {
        Assert.Equal(65_535, VariableRecordDescriptor.MicroFocus.MaxDataLength);
        Assert.Equal(4_294_967_295, VariableRecordDescriptor.Fujitsu.MaxDataLength);

        // When the stored length counts the framing, that framing comes out of
        // the same budget.
        var withFraming = VariableRecordDescriptor.Fujitsu with
        {
            LengthIncludes = LengthBasis.WithPrefixAndSuffix,
        };

        Assert.Equal(4_294_967_295 - 8, withFraming.MaxDataLength);
    }

    [Fact]
    public async Task AFujitsuRecordFarBeyondTheMicroFocusLimitIsAccepted()
    {
        // Four-byte length field, so the same record that Micro Focus refuses
        // is ordinary here.
        var content = await WriteAllAsync(
            new VariableRecordEncoder(VariableRecordDescriptor.Fujitsu),
            [new byte[80_000]],
            blockSize: 1 << 20);

        Assert.Equal(80_000 + 8, content.Length);
    }

    [Fact]
    public async Task WritesRecordsThatSpanManyBlocks()
    {
        // A tiny block size forces the sink to split records; the file must still
        // come out byte-identical.
        var record = Enumerable.Range(0, 500).Select(i => (byte)i).ToArray();

        var content = await WriteAllAsync(
            new VariableRecordEncoder(VariableRecordDescriptor.Fujitsu),
            [record],
            blockSize: 8,
            queueDepth: 3);

        Assert.Equal(508, content.Length);
        Assert.Equal<byte[]>(record, content.AsSpan(4, 500).ToArray());
    }
}
