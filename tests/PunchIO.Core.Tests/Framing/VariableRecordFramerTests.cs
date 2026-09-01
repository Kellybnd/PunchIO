using PunchIO.Framing;
using Xunit;

namespace PunchIO.Core.Tests.Framing;

public class VariableRecordFramerTests
{
    /// <summary>A Fujitsu record: four-byte big-endian length, data, the same length again.</summary>
    private static byte[] Fujitsu(params byte[] data) =>
    [
        0, 0, (byte)(data.Length >> 8), (byte)data.Length,
        .. data,
        0, 0, (byte)(data.Length >> 8), (byte)data.Length,
    ];

    /// <summary>A Micro Focus record: two-byte big-endian length, flags, reserved, data.</summary>
    private static byte[] MicroFocus(byte flags, params byte[] data) =>
    [
        (byte)(data.Length >> 8), (byte)data.Length, flags, 0,
        .. data,
    ];

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
        var framer = new VariableRecordFramer(VariableRecordDescriptor.MicroFocus);

        var status = framer.TryFrame(MicroFocus(flags: 0, 9, 8, 7), isFinalBlock: false,
            out int consumed, out int start, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(7, consumed);    // 4 header + 3 data
        Assert.Equal(4, start);
        Assert.Equal(3, length);
    }

    [Fact]
    public void IgnoresTheMicroFocusFlagByteByDefault()
    {
        var framer = new VariableRecordFramer(VariableRecordDescriptor.MicroFocus);

        var status = framer.TryFrame(MicroFocus(flags: 0x40, 1, 2), isFinalBlock: false,
            out _, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(2, length);
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
    public void ReadsLittleEndianLengthsWhenConfigured()
    {
        var descriptor = VariableRecordDescriptor.Fujitsu with { Endianness = Endianness.LittleEndian };
        var framer = new VariableRecordFramer(descriptor);
        byte[] input = [5, 0, 0, 0, 1, 2, 3, 4, 5, 5, 0, 0, 0];

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
        byte[] input = [0, 0, 0, 13, 1, 2, 3, 4, 5, 0, 0, 0, 13];

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

        byte[] input = [0, 0, 0, 2, 1, 2, 3, 4, 0, 0, 0, 2];   // 2 - 8 is negative

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
        var descriptor = VariableRecordDescriptor.MicroFocus with { ValidateReservedBytes = true };
        var framer = new VariableRecordFramer(descriptor);
        var input = MicroFocus(flags: 0x40, 1, 2);

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
        var descriptor = VariableRecordDescriptor.MicroFocus;
        byte[] data = [4, 5, 6, 7];

        var buffer = new byte[VariableRecordFramer.FramedLength(data.Length, descriptor)];
        VariableRecordFramer.WriteFraming(buffer, data.Length, descriptor);
        data.CopyTo(buffer, descriptor.PrefixBytes);

        Assert.Equal(8, buffer.Length);   // 4 header + 4 data, no suffix

        var status = new VariableRecordFramer(descriptor).TryFrame(buffer, isFinalBlock: true,
            out int consumed, out int start, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(8, consumed);
        Assert.Equal<byte[]>(data, buffer.AsSpan(start, length).ToArray());
    }
}
