namespace PunchIO.Framing;

/// <summary>
/// Splits a contiguous run of bytes into records. Implementations are pure span
/// logic: no I/O, no allocation, and no knowledge of absolute file offsets.
/// </summary>
/// <remarks>
/// <para>
/// Implement this as a <see langword="readonly struct"/> and pass it as a generic
/// type argument so the framing call is inlined rather than dispatched once per
/// record.
/// </para>
/// <para>
/// The contract every implementation honours:
/// <list type="bullet">
/// <item><description>
/// <see cref="FrameStatus.Ok"/> sets <c>consumed</c> to the bytes to advance
/// past, including framing overhead and padding, and sets the record offsets to
/// the record body alone.
/// </description></item>
/// <item><description>
/// <see cref="FrameStatus.NeedMoreData"/> must never be returned when
/// <c>isFinalBlock</c> is <see langword="true"/>: at that point there are no
/// more bytes coming and the framer must commit to a decision.
/// </description></item>
/// <item><description>
/// <see cref="FrameStatus.EndOfData"/> means the input ended on a record
/// boundary. A truncated record must report <see cref="FrameStatus.Invalid"/>
/// instead, so data loss is never reported as a clean ending.
/// </description></item>
/// </list>
/// </para>
/// </remarks>
public interface IRecordFramer
{
    /// <summary>
    /// The smallest number of bytes that could allow <see cref="TryFrame"/> to
    /// reach a decision other than <see cref="FrameStatus.NeedMoreData"/>.
    /// </summary>
    int MinimumLookahead { get; }

    /// <summary>Attempts to frame exactly one record from the front of <paramref name="input"/>.</summary>
    /// <param name="input">The bytes available, starting at a record boundary.</param>
    /// <param name="isFinalBlock">
    /// <see langword="true"/> when no further bytes will ever be supplied.
    /// </param>
    /// <param name="consumed">
    /// Bytes to advance past, including any framing overhead and padding.
    /// </param>
    /// <param name="recordStart">
    /// Offset of the record body within <paramref name="input"/>.
    /// </param>
    /// <param name="recordLength">Length of the record body, in bytes.</param>
    /// <returns>The framing outcome.</returns>
    FrameStatus TryFrame(
        ReadOnlySpan<byte> input,
        bool isFinalBlock,
        out int consumed,
        out int recordStart,
        out int recordLength);
}
