namespace PunchIO.Framing;

/// <summary>The outcome of an attempt to frame one record.</summary>
public enum FrameStatus
{
    /// <summary>A record was framed.</summary>
    Ok,

    /// <summary>More bytes are required before a decision can be made.</summary>
    NeedMoreData,

    /// <summary>The input is cleanly exhausted; there are no further records.</summary>
    EndOfData,

    /// <summary>The bytes are malformed for the configured format.</summary>
    Invalid,
}
