namespace PunchIO;

/// <summary>
/// The bytes at a given offset do not form a valid record for the configured format.
/// </summary>
public sealed class RecordFormatException : PunchIoException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="message">What was expected and what was found.</param>
    /// <param name="byteOffset">The absolute file offset at which framing failed.</param>
    public RecordFormatException(string message, long byteOffset)
        : base($"{message} (at byte offset {byteOffset})", FileStatus.PermanentError)
        => ByteOffset = byteOffset;

    /// <summary>
    /// The absolute file offset at which framing failed. Part of the type rather
    /// than only the message text, because locating one bad record in a very
    /// large file is the whole of the support interaction.
    /// </summary>
    public long ByteOffset { get; }
}
