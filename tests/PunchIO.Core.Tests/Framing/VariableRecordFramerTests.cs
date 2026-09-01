using PunchIO.Framing;
using Xunit;

namespace PunchIO.Core.Tests.Framing;

public class VariableRecordFramerTests
{
    /// <summary>A Fujitsu record: four-byte little-endian length, data, the same length again.</summary>
    private static byte[] Fujitsu(params byte[] data) =>
    [
        (byte)data.Length, (byte)(data.Length >> 8), 0, 0,
        .. data,
        (byte)data.Length, (byte)(data.Length >> 8), 0, 0,
    ];

    /// <summary>
    /// A Micro Focus record: a two-byte big-endian control field carrying the
    /// record status in its top four bits over a twelve-bit length, then the
    /// data, padded out to the next four-byte boundary.
    /// </summary>
    private static byte[] MicroFocus(int status, params byte[] data)
    {
        var buffer = new byte[(2 + data.Length + 3) & ~3];

        buffer[0] = (byte)((status << 4) | (data.Length >> 8));
        buffer[1] = (byte)data.Length;
        data.CopyTo(buffer, 2);

        return buffer;
    }

    [Fact]
    public void FramesAFujitsuRecordAndReportsOnlyTheData()
    {
        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);

        var status = framer.TryFrame(Fujitsu(1, 2, 3, 4, 5), isFinalBlock: false,
            out int consumed, out int start, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(13, consumed);   // 4 prefix + 5 data + 4 suffix
        Assert.Equal(4, start);       // data begins after the prefix
        Assert.Equal(5, length);      // the caller sees only the data
    }

    [Fact]
    public void FramesAMicroFocusRecord()
    {
        var framer = new VariableRecordFramer(VariableRecordDescriptor.MicroFocus());

        var status = framer.TryFrame(MicroFocus(status: 4, 9, 8, 7), isFinalBlock: false,
            out int consumed, out int start, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(8, consumed);    // 2 control + 3 data + 3 padding
        Assert.Equal(2, start);
        Assert.Equal(3, length);
    }

    [Fact]
    public void AnEightyByteMicroFocusRecordBeginsWith4050()
    {
        // The documented example: status 4 over a length of x"050".
        var record = MicroFocus(status: 4, new byte[80]);

        Assert.Equal(0x40, record[0]);
        Assert.Equal(0x50, record[1]);

        var framer = new VariableRecordFramer(VariableRecordDescriptor.MicroFocus());

        Assert.Equal(
            FrameStatus.Ok,
            framer.TryFrame(record, false, out int consumed, out _, out int length));

        Assert.Equal(80, length);
        Assert.Equal(82 + 2, consumed);   // 2 control + 80 data, padded to 84
    }

    [Theory]
    [InlineData(2)]   // deleted
    [InlineData(3)]   // a system record, which is what the file header is
    [InlineData(6)]   // a pointer record
    public void SkipsAnyRecordThatIsNotUserData(int status)
    {
        var framer = new VariableRecordFramer(VariableRecordDescriptor.MicroFocus());

        var result = framer.TryFrame(MicroFocus(status, 1, 2, 3), isFinalBlock: false,
            out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Skip, result);
        Assert.Equal(8, consumed);   // consumed in full, so framing continues after it
        Assert.Equal(0, length);
    }

    [Fact]
    public void SkipsTheStandardFileHeaderWholeAndInOnePiece()
    {
        // The header is itself a record: a system record whose control field and
        // data together come to exactly 128 bytes.
        var framer = new VariableRecordFramer(VariableRecordDescriptor.MicroFocus());
        var header = new byte[MicroFocusFileHeader.Length];

        MicroFocusFileHeader.Write(header, VariableRecordDescriptor.MicroFocus());

        Assert.Equal(0x30, header[0]);
        Assert.Equal(0x7E, header[1]);

        Assert.Equal(
            FrameStatus.Skip,
            framer.TryFrame(header, isFinalBlock: false, out int consumed, out _, out _));

        Assert.Equal(MicroFocusFileHeader.Length, consumed);
    }

    [Fact]
    public void TheLongFileHeaderCarriesTheDocumentedBytes()
    {
        var header = new byte[MicroFocusFileHeader.Length];

        MicroFocusFileHeader.Write(header, VariableRecordDescriptor.MicroFocus(65_535));

        // x"3000007C": a system record of 124 bytes, which with its four-byte
        // control field is again 128.
        Assert.Equal<byte[]>([0x30, 0x00, 0x00, 0x7C], header[..4]);
    }

    [Fact]
    public void RejectsAFujitsuRecordWhoseSuffixDisagreesWithItsPrefix()
    {
        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);
        var input = Fujitsu(1, 2, 3, 4, 5);
        input[^1] = 99;   // corrupt the trailing length

        Assert.Equal(FrameStatus.Invalid,
            framer.TryFrame(input, isFinalBlock: false, out _, out _, out _));
    }

    [Fact]
    public void FramesAZeroLengthRecord()
    {
        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);

        var status = framer.TryFrame(Fujitsu(), isFinalBlock: false,
            out int consumed, out int start, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(8, consumed);
        Assert.Equal(4, start);
        Assert.Equal(0, length);
    }

    [Theory]
    [InlineData(0)]   // nothing at all
    [InlineData(3)]   // truncated prefix
    [InlineData(8)]   // prefix and some data, but no suffix
    public void RequestsMoreDataWhenTheRecordIsIncomplete(int available)
    {
        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);
        var input = Fujitsu(1, 2, 3, 4, 5)[..available];

        var status = framer.TryFrame(input, isFinalBlock: false, out int consumed, out _, out _);

        Assert.Equal(FrameStatus.NeedMoreData, status);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void ReportsEndOfDataOnEmptyFinalInput()
    {
        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);

        Assert.Equal(FrameStatus.EndOfData,
            framer.TryFrame(ReadOnlySpan<byte>.Empty, isFinalBlock: true, out _, out _, out _));
    }

    [Theory]
    [InlineData(3)]   // truncated prefix at end of file
    [InlineData(8)]   // missing suffix at end of file
    public void ReportsInvalidWhenTheFileEndsMidRecord(int available)
    {
        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);
        var input = Fujitsu(1, 2, 3, 4, 5)[..available];

        Assert.Equal(FrameStatus.Invalid,
            framer.TryFrame(input, isFinalBlock: true, out _, out _, out _));
    }

    [Fact]
    public void ReadsBigEndianLengthsWhenConfigured()
    {
        var descriptor = VariableRecordDescriptor.Fujitsu with { Endianness = Endianness.BigEndian };
        var framer = new VariableRecordFramer(descriptor);
        byte[] input = [0, 0, 0, 5, 1, 2, 3, 4, 5, 0, 0, 0, 5];

        var status = framer.TryFrame(input, isFinalBlock: false,
            out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(13, consumed);
        Assert.Equal(5, length);
    }

    [Fact]
    public void SubtractsFramingBytesWhenTheLengthCountsThem()
    {
        var descriptor = VariableRecordDescriptor.Fujitsu with
        {
            LengthIncludes = LengthBasis.WithPrefixAndSuffix,
            ValidateSuffix = false,
        };
        var framer = new VariableRecordFramer(descriptor);

        // Stored length 13 = 4 prefix + 5 data + 4 suffix.
        byte[] input = [13, 0, 0, 0, 1, 2, 3, 4, 5, 13, 0, 0, 0];

        var status = framer.TryFrame(input, isFinalBlock: false,
            out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(13, consumed);
        Assert.Equal(5, length);
    }

    [Fact]
    public void RejectsALengthThatUnderflowsAfterSubtractingFraming()
    {
        var descriptor = VariableRecordDescriptor.Fujitsu with
        {
            LengthIncludes = LengthBasis.WithPrefixAndSuffix,
            ValidateSuffix = false,
        };
        var framer = new VariableRecordFramer(descriptor);

        byte[] input = [2, 0, 0, 0, 1, 2, 3, 4, 2, 0, 0, 0];   // 2 - 8 is negative

        Assert.Equal(FrameStatus.Invalid,
            framer.TryFrame(input, isFinalBlock: false, out _, out _, out _));
    }

    [Fact]
    public void RejectsAHugeLengthWithoutAttemptingToUseIt()
    {
        // A four-byte field with the high bit set: an ordinary bit pattern in a
        // corrupt file, and the reason lengths are read into a long.
        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);
        byte[] input = [0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3];

        Assert.Equal(FrameStatus.Invalid,
            framer.TryFrame(input, isFinalBlock: true, out _, out _, out _));
    }

    [Fact]
    public void PadsToTheConfiguredAlignment()
    {
        var descriptor = VariableRecordDescriptor.Fujitsu with { Alignment = 4, ValidateSuffix = false };
        var framer = new VariableRecordFramer(descriptor);

        // 4 prefix + 5 data + 4 suffix = 13, padded up to 16.
        var input = new byte[16];
        Fujitsu(1, 2, 3, 4, 5).CopyTo(input, 0);

        var status = framer.TryFrame(input, isFinalBlock: false,
            out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(16, consumed);
        Assert.Equal(5, length);
    }

    [Fact]
    public void ValidatesReservedPrefixBytesWhenAsked()
    {
        // A four-byte prefix carrying a two-byte length, a flag byte, and one
        // byte that must stay zero.
        var descriptor = new VariableRecordDescriptor
        {
            PrefixBytes = 4,
            SuffixBytes = 0,
            LengthFieldOffset = 0,
            LengthFieldWidth = 2,
            FlagByteOffset = 2,
            Endianness = Endianness.BigEndian,
            LengthIncludes = LengthBasis.DataOnly,
            Alignment = 1,
            ValidateReservedBytes = true,
        };

        var framer = new VariableRecordFramer(descriptor);
        byte[] input = [0, 2, 0x40, 0, 1, 2];

        Assert.Equal(FrameStatus.Ok, framer.TryFrame(input, false, out _, out _, out _));

        input[3] = 0x01;   // reserved byte 3 must be zero
        Assert.Equal(FrameStatus.Invalid, framer.TryFrame(input, false, out _, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(1000)]
    public void WritesFramingThatItsOwnFramerCanRead(int dataLength)
    {
        var descriptor = VariableRecordDescriptor.Fujitsu;
        var data = new byte[dataLength];
        for (int i = 0; i < dataLength; i++) data[i] = (byte)(i * 7 + 1);

        var buffer = new byte[VariableRecordFramer.FramedLength(dataLength, descriptor)];
        int total = VariableRecordFramer.WriteFraming(buffer, dataLength, descriptor);
        data.CopyTo(buffer, descriptor.PrefixBytes);

        Assert.Equal(buffer.Length, total);

        var status = new VariableRecordFramer(descriptor).TryFrame(buffer, isFinalBlock: true,
            out int consumed, out int start, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(buffer.Length, consumed);
        Assert.Equal<byte[]>(data, buffer.AsSpan(start, length).ToArray());
    }

    [Fact]
    public void MicroFocusFramingRoundTripsThroughTheWriter()
    {
        var descriptor = VariableRecordDescriptor.MicroFocus();
        byte[] data = [4, 5, 6, 7];

        var buffer = new byte[VariableRecordFramer.FramedLength(data.Length, descriptor)];
        VariableRecordFramer.WriteFraming(buffer, data.Length, descriptor);
        data.CopyTo(buffer, descriptor.PrefixBytes);

        Assert.Equal(8, buffer.Length);   // 2 control + 4 data + 2 padding
        Assert.Equal(0x40, buffer[0]);    // status 4 over a length of 4
        Assert.Equal(0x04, buffer[1]);

        var status = new VariableRecordFramer(descriptor).TryFrame(buffer, isFinalBlock: true,
            out int consumed, out int start, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(8, consumed);
        Assert.Equal<byte[]>(data, buffer.AsSpan(start, length).ToArray());
    }
}
