using PunchIO.Framing;
using Xunit;

namespace PunchIO.Core.Tests.Framing;

public class FixedBlockFramerTests
{
    private static byte[] Bytes(int count, byte seed = 0)
    {
        var b = new byte[count];
        for (int i = 0; i < count; i++) b[i] = (byte)(seed + i);
        return b;
    }

    [Fact]
    public void FramesOneWholeRecord()
    {
        var framer = new FixedBlockFramer(80);

        var status = framer.TryFrame(Bytes(240), isFinalBlock: false,
            out int consumed, out int start, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(80, consumed);
        Assert.Equal(0, start);
        Assert.Equal(80, length);
    }

    [Fact]
    public void RequestsMoreDataWhenShortOfAWholeRecord()
    {
        var framer = new FixedBlockFramer(80);

        var status = framer.TryFrame(Bytes(79), isFinalBlock: false,
            out int consumed, out _, out _);

        Assert.Equal(FrameStatus.NeedMoreData, status);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void ReportsEndOfDataOnEmptyFinalInput()
    {
        var framer = new FixedBlockFramer(80);

        var status = framer.TryFrame(ReadOnlySpan<byte>.Empty, isFinalBlock: true,
            out _, out _, out _);

        Assert.Equal(FrameStatus.EndOfData, status);
    }

    [Theory]
    [InlineData(TrailingPartialRecord.Strict, FrameStatus.Invalid, 0, 0)]
    [InlineData(TrailingPartialRecord.Lenient, FrameStatus.Ok, 37, 37)]
    [InlineData(TrailingPartialRecord.Ignore, FrameStatus.EndOfData, 37, 0)]
    public void HandlesATrailingPartialRecordPerPolicy(
        TrailingPartialRecord policy, FrameStatus expected, int expectedConsumed, int expectedLength)
    {
        var framer = new FixedBlockFramer(80, policy);

        var status = framer.TryFrame(Bytes(37), isFinalBlock: true,
            out int consumed, out _, out int length);

        Assert.Equal(expected, status);
        Assert.Equal(expectedConsumed, consumed);
        Assert.Equal(expectedLength, length);
    }

    [Fact]
    public void NeverReturnsNeedMoreDataOnTheFinalBlock()
    {
        // The universal framer contract: on the final block there are no more
        // bytes coming, so the framer must commit to a decision.
        var framer = new FixedBlockFramer(80);

        for (int available = 0; available < 80; available++)
        {
            var status = framer.TryFrame(Bytes(available), isFinalBlock: true,
                out _, out _, out _);

            Assert.NotEqual(FrameStatus.NeedMoreData, status);
        }
    }

    [Fact]
    public void MinimumLookaheadIsTheRecordLength()
    {
        Assert.Equal(512, new FixedBlockFramer(512).MinimumLookahead);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsANonPositiveRecordLength(int recordLength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedBlockFramer(recordLength));
    }
}
