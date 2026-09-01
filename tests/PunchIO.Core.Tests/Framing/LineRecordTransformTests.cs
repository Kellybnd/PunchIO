using System.Text;
using PunchIO.Framing;
using Xunit;

namespace PunchIO.Core.Tests.Framing;

public class LineRecordTransformTests
{
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static string Decode(LineSequentialOptions options, string input)
    {
        var transform = new LineRecordTransform(options);
        var source = Ascii(input);
        var destination = new byte[transform.MaxExpansion(source.Length)];

        Assert.True(transform.TryDecode(source, destination, out int written));
        return Encoding.ASCII.GetString(destination, 0, written);
    }

    [Fact]
    public void IdentityWhenNoContentRewritingIsConfigured()
    {
        Assert.True(new LineRecordTransform(new LineSequentialOptions()).IsIdentity);
    }

    [Fact]
    public void NotIdentityWhenTabExpansionIsOn()
    {
        Assert.False(new LineRecordTransform(new LineSequentialOptions { ExpandTabs = true }).IsIdentity);
    }

    [Theory]
    [InlineData("\tX", "        X")]                    // tab at column 0 expands to 8 spaces
    [InlineData("AB\tX", "AB      X")]                  // advances to column 8
    [InlineData("ABCDEFG\tX", "ABCDEFG X")]             // one space reaches column 8
    [InlineData("ABCDEFGH\tX", "ABCDEFGH        X")]    // already at a stop, so a full tab
    [InlineData("\t\tX", "                X")]          // consecutive tabs
    public void ExpandsTabsToTheNextTabStop(string input, string expected)
    {
        var options = new LineSequentialOptions { ExpandTabs = true, TabStopWidth = 8 };
        Assert.Equal(expected, Decode(options, input));
    }

    [Fact]
    public void HonoursANonDefaultTabStopWidth()
    {
        var options = new LineSequentialOptions { ExpandTabs = true, TabStopWidth = 4 };
        Assert.Equal("AB  X", Decode(options, "AB\tX"));
    }

    [Fact]
    public void DecodeRemovesNullEscapes()
    {
        // NUL is the escape prefix: the byte after it is literal data.
        var transform = new LineRecordTransform(new LineSequentialOptions { NullEscape = true });
        byte[] source = [0x41, 0x00, 0x09, 0x42];   // A, escape, TAB, B
        var destination = new byte[transform.MaxExpansion(source.Length)];

        Assert.True(transform.TryDecode(source, destination, out int written));
        Assert.Equal<byte[]>([0x41, 0x09, 0x42], destination[..written]);
    }

    [Fact]
    public void EncodeInsertsNullEscapesBeforeControlBytes()
    {
        var transform = new LineRecordTransform(new LineSequentialOptions { NullEscape = true });
        byte[] source = [0x41, 0x09, 0x42];         // A, TAB, B
        var destination = new byte[transform.MaxExpansion(source.Length)];

        Assert.True(transform.TryEncode(source, destination, out int written));
        Assert.Equal<byte[]>([0x41, 0x00, 0x09, 0x42], destination[..written]);
    }

    [Fact]
    public void EncodeEscapesTheEscapeByteItself()
    {
        var transform = new LineRecordTransform(new LineSequentialOptions { NullEscape = true });
        byte[] source = [0x41, 0x00, 0x42];
        var destination = new byte[transform.MaxExpansion(source.Length)];

        Assert.True(transform.TryEncode(source, destination, out int written));
        Assert.Equal<byte[]>([0x41, 0x00, 0x00, 0x42], destination[..written]);
    }

    [Fact]
    public void EncodeAndDecodeRoundTripEveryByteValue()
    {
        var transform = new LineRecordTransform(new LineSequentialOptions { NullEscape = true });

        var source = new byte[256];
        for (int i = 0; i < 256; i++) source[i] = (byte)i;

        var encoded = new byte[transform.MaxExpansion(source.Length)];
        Assert.True(transform.TryEncode(source, encoded, out int encodedLength));

        var decoded = new byte[transform.MaxExpansion(encodedLength)];
        Assert.True(transform.TryDecode(encoded.AsSpan(0, encodedLength), decoded, out int decodedLength));

        Assert.Equal<byte[]>(source, decoded[..decodedLength]);
    }

    [Fact]
    public void ReportsFailureRatherThanOverrunningASmallDestination()
    {
        var transform = new LineRecordTransform(new LineSequentialOptions { ExpandTabs = true });
        var destination = new byte[4];

        Assert.False(transform.TryDecode(Ascii("\tX"), destination, out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void MaxExpansionCoversTheWorstCase()
    {
        var options = new LineSequentialOptions
        {
            ExpandTabs = true,
            TabStopWidth = 8,
            NullEscape = true,
        };

        // Worst case for decode is every byte a tab landing on a stop boundary.
        Assert.True(new LineRecordTransform(options).MaxExpansion(10) >= 80);
    }

    [Fact]
    public void IdentityTransformStillCopiesThroughEncode()
    {
        var transform = new LineRecordTransform(new LineSequentialOptions());
        byte[] source = [1, 2, 3];
        var destination = new byte[3];

        Assert.True(transform.TryEncode(source, destination, out int written));
        Assert.Equal(3, written);
        Assert.Equal<byte[]>(source, destination);
    }
}
