namespace PunchIO.Framing;

/// <summary>
/// The byte values a line-sequential framer treats as structural. Supplying these
/// rather than assuming ASCII is what lets one framer handle EBCDIC files, which
/// relocate every one of them.
/// </summary>
public readonly struct LineSyntax
{
    /// <summary>The line-feed (record terminator) byte.</summary>
    public byte LineFeed { get; init; }

    /// <summary>The carriage-return byte.</summary>
    public byte CarriageReturn { get; init; }

    /// <summary>The space byte, used for trailing-space trimming and padding.</summary>
    public byte Space { get; init; }

    /// <summary>The horizontal tab byte.</summary>
    public byte Tab { get; init; }

    /// <summary>The null byte, used as the escape prefix when null escaping is enabled.</summary>
    public byte Null { get; init; }

    /// <summary>Byte values for ASCII and ASCII-compatible encodings such as UTF-8.</summary>
    public static LineSyntax Ascii => new()
    {
        LineFeed = 0x0A,
        CarriageReturn = 0x0D,
        Space = 0x20,
        Tab = 0x09,
        Null = 0x00,
    };

    /// <summary>Byte values for EBCDIC code pages.</summary>
    public static LineSyntax Ebcdic => new()
    {
        LineFeed = 0x15,          // NL
        CarriageReturn = 0x0D,
        Space = 0x40,
        Tab = 0x05,
        Null = 0x00,
    };
}
