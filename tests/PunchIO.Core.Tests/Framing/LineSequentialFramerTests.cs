using System.Text;
using PunchIO.Framing;
using Xunit;

namespace PunchIO.Core.Tests.Framing;

public class LineSequentialFramerTests
{
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static LineSequentialFramer Framer(
        LineTerminator terminator = LineTerminator.Lf,
        bool acceptEither = true,
        bool trimTrailingSpaces = false) =>
        new(new LineSequentialOptions
        {
            Terminator = terminator,
            AcceptEitherOnRead = acceptEither,
            TrimTrailingSpaces = trimTrailingSpaces,
        });

    [Fact]
    public void FramesALineFeedTerminatedRecord()
    {
        var status = Framer().TryFrame(Ascii("HELLO\nWORLD\n"), isFinalBlock: false,
            out int consumed, out int start, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(6, consumed);   // record plus terminator
        Assert.Equal(0, start);
        Assert.Equal(5, length);     // terminator excluded from the record
    }

    [Fact]
    public void StripsTheCarriageReturnOfACrLfPair()
    {
        var status = Framer().TryFrame(Ascii("HELLO\r\nWORLD\r\n"), isFinalBlock: false,
            out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(7, consumed);
        Assert.Equal(5, length);
    }

    [Fact]
    public void KeepsALoneCarriageReturnWhenNotPartOfAPair()
    {
        // A carriage return in the middle of data is data, not a terminator.
        var status = Framer().TryFrame(Ascii("A\rB\n"), isFinalBlock: false,
            out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(4, consumed);
        Assert.Equal(3, length);
    }

    [Fact]
    public void FramesAnEmptyRecord()
    {
        var status = Framer().TryFrame(Ascii("\n\n"), isFinalBlock: false,
            out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(1, consumed);
        Assert.Equal(0, length);
    }

    [Fact]
    public void RequestsMoreDataWhenNoTerminatorIsPresentYet()
    {
        var status = Framer().TryFrame(Ascii("PARTIAL"), isFinalBlock: false,
            out int consumed, out _, out _);

        Assert.Equal(FrameStatus.NeedMoreData, status);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void RequestsMoreDataWhenABlockEndsOnACarriageReturn()
    {
        // The straddle case that motivates the stitch buffer: CR closes one block
        // and LF opens the next. Deciding here would emit a bogus record.
        var status = Framer().TryFrame(Ascii("HELLO\r"), isFinalBlock: false,
            out _, out _, out _);

        Assert.Equal(FrameStatus.NeedMoreData, status);
    }

    [Fact]
    public void AcceptsAFinalRecordWithNoTerminator()
    {
        var status = Framer().TryFrame(Ascii("LAST"), isFinalBlock: true,
            out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(4, consumed);
        Assert.Equal(4, length);
    }

    [Fact]
    public void ReportsEndOfDataOnEmptyFinalInput()
    {
        var status = Framer().TryFrame(ReadOnlySpan<byte>.Empty, isFinalBlock: true,
            out _, out _, out _);

        Assert.Equal(FrameStatus.EndOfData, status);
    }

    [Fact]
    public void TrimsTrailingSpacesWhenConfigured()
    {
        var status = Framer(trimTrailingSpaces: true)
            .TryFrame(Ascii("DATA    \n"), isFinalBlock: false,
                out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(9, consumed);   // consumed is unaffected by trimming
        Assert.Equal(4, length);
    }

    [Fact]
    public void TrimmingAnAllSpaceRecordYieldsAnEmptyRecord()
    {
        Framer(trimTrailingSpaces: true).TryFrame(Ascii("    \n"), isFinalBlock: false,
            out int consumed, out _, out int length);

        Assert.Equal(5, consumed);
        Assert.Equal(0, length);
    }

    [Fact]
    public void HonoursACarriageReturnOnlyTerminator()
    {
        var status = Framer(LineTerminator.Cr, acceptEither: false)
            .TryFrame(Ascii("HELLO\rWORLD\r"), isFinalBlock: false,
                out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(6, consumed);
        Assert.Equal(5, length);
    }

    [Fact]
    public void FramesEbcdicUsingEbcdicByteConstants()
    {
        // EBCDIC newline is 0x15 and space is 0x40. An ASCII-hardcoded framer
        // would find no terminator here at all.
        var options = new LineSequentialOptions
        {
            Syntax = LineSyntax.Ebcdic,
            TrimTrailingSpaces = true,
        };
        byte[] input = [0xC8, 0xC9, 0x40, 0x40, 0x15, 0xC1, 0x15];

        var status = new LineSequentialFramer(options).TryFrame(input, isFinalBlock: false,
            out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(5, consumed);
        Assert.Equal(2, length);   // the two 0x40 bytes trimmed as trailing spaces
    }

    [Fact]
    public void NeverReturnsNeedMoreDataOnTheFinalBlock()
    {
        var framer = Framer();

        foreach (var text in new[] { "", "A", "A\r", "ABC", "A\rB" })
        {
            var status = framer.TryFrame(Ascii(text), isFinalBlock: true, out _, out _, out _);
            Assert.NotEqual(FrameStatus.NeedMoreData, status);
        }
    }
}
