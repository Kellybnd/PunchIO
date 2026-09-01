namespace PunchIO.Framing;

/// <summary>
/// The write-side counterpart to <see cref="IRecordFramer"/>: turns a caller's
/// record into the bytes that appear on disk.
/// </summary>
/// <remarks>
/// <para>
/// Split into header, body, and trailer so the common formats never copy the
/// record. A fixed-length record is written straight from the caller's buffer
/// and padded by the trailer; a variable-length record is bracketed by a header
/// and trailer of a few bytes each. Only a format that must genuinely rewrite
/// record bytes — line sequential with null escaping — sets
/// <see cref="RewritesBody"/> and pays for a copy.
/// </para>
/// <para>
/// Implement as a <see langword="readonly struct"/> and pass as a generic type
/// argument so the calls inline rather than dispatching per record.
/// </para>
/// </remarks>
public interface IRecordEncoder
{
    /// <summary>The largest header <see cref="WriteHeader"/> can produce.</summary>
    int MaxHeaderLength { get; }

    /// <summary>The largest trailer <see cref="WriteTrailer"/> can produce.</summary>
    int MaxTrailerLength { get; }

    /// <summary>
    /// <see langword="true"/> when the record's bytes must be rewritten rather
    /// than written through unchanged.
    /// </summary>
    bool RewritesBody { get; }

    /// <summary>
    /// The largest body <see cref="WriteBody"/> can produce for a record of
    /// <paramref name="recordLength"/> bytes.
    /// </summary>
    /// <param name="recordLength">The caller's record length in bytes.</param>
    /// <returns>A scratch size guaranteed to be sufficient.</returns>
    int MaxBodyLength(int recordLength);

    /// <summary>
    /// How many of the record's own bytes to write, used when
    /// <see cref="RewritesBody"/> is <see langword="false"/>. This is what lets
    /// trailing-space stripping cost nothing: the body is simply a shorter slice
    /// of the caller's buffer.
    /// </summary>
    /// <param name="record">The caller's record.</param>
    /// <returns>The number of leading bytes of <paramref name="record"/> to write.</returns>
    int BodyLength(ReadOnlySpan<byte> record);

    /// <summary>
    /// Writes the rewritten body. Called only when <see cref="RewritesBody"/> is
    /// <see langword="true"/>.
    /// </summary>
    /// <param name="record">The caller's record.</param>
    /// <param name="destination">
    /// A buffer at least <see cref="MaxBodyLength"/> bytes long.
    /// </param>
    /// <returns>The number of bytes written.</returns>
    int WriteBody(ReadOnlySpan<byte> record, Span<byte> destination);

    /// <summary>Writes the bytes that precede the body.</summary>
    /// <param name="destination">A buffer at least <see cref="MaxHeaderLength"/> bytes long.</param>
    /// <param name="bodyLength">The body length that will follow, in bytes.</param>
    /// <returns>The number of bytes written.</returns>
    int WriteHeader(Span<byte> destination, int bodyLength);

    /// <summary>Writes the bytes that follow the body, including any padding.</summary>
    /// <param name="destination">A buffer at least <see cref="MaxTrailerLength"/> bytes long.</param>
    /// <param name="bodyLength">The body length just written, in bytes.</param>
    /// <returns>The number of bytes written.</returns>
    int WriteTrailer(Span<byte> destination, int bodyLength);
}
