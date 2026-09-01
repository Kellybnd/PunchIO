namespace PunchIO.Framing;

/// <summary>The terminator written after each record.</summary>
public enum LineTerminator
{
    /// <summary>A single line feed.</summary>
    Lf,

    /// <summary>A carriage return followed by a line feed.</summary>
    CrLf,

    /// <summary>A single carriage return.</summary>
    Cr,
}

/// <summary>Behavior switches for line-sequential reading and writing.</summary>
public sealed class LineSequentialOptions
{
    /// <summary>The structural byte values. Defaults to <see cref="LineSyntax.Ascii"/>.</summary>
    public LineSyntax Syntax { get; init; } = LineSyntax.Ascii;

    /// <summary>
    /// The terminator written after each record. Defaults to <see cref="LineTerminator.Lf"/>.
    /// </summary>
    public LineTerminator Terminator { get; init; } = LineTerminator.Lf;

    /// <summary>
    /// When <see langword="true"/> (the default), a carriage return immediately
    /// preceding a line feed is treated as part of the terminator on read,
    /// regardless of <see cref="Terminator"/>.
    /// </summary>
    public bool AcceptEitherOnRead { get; init; } = true;

    /// <summary>
    /// Strips trailing spaces from each record on read. Defaults to <see langword="false"/>.
    /// </summary>
    public bool TrimTrailingSpaces { get; init; }

    /// <summary>
    /// Strips trailing spaces from each record on write. Defaults to
    /// <see langword="true"/>, matching COBOL line-sequential behavior.
    /// </summary>
    public bool StripTrailingSpaces { get; init; } = true;

    /// <summary>Expands tabs to the next tab stop on read. Defaults to <see langword="false"/>.</summary>
    public bool ExpandTabs { get; init; }

    /// <summary>The tab stop width used by <see cref="ExpandTabs"/>. Defaults to 8.</summary>
    public int TabStopWidth { get; init; } = 8;

    /// <summary>
    /// Escapes control bytes with a preceding null on write and removes those
    /// escapes on read, following the Micro Focus <c>INSERTNULL</c> convention.
    /// </summary>
    public bool NullEscape { get; init; }
}
