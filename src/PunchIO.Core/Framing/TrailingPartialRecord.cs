namespace PunchIO.Framing;

/// <summary>How a final record shorter than the configured length is handled.</summary>
public enum TrailingPartialRecord
{
    /// <summary>Treat it as a format error.</summary>
    Strict,

    /// <summary>Return it as a short record.</summary>
    Lenient,

    /// <summary>Discard it silently.</summary>
    Ignore,
}
