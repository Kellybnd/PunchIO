namespace PunchIO;

/// <summary>
/// A record's declared length exceeded the configured maximum. Raised instead of
/// attempting the allocation, so a corrupt length prefix cannot exhaust memory.
/// </summary>
public sealed class RecordTooLargeException : PunchIoException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="byteOffset">The absolute file offset of the oversized record.</param>
    /// <param name="maxRecordLength">The configured limit, in bytes.</param>
    public RecordTooLargeException(long byteOffset, int maxRecordLength)
        : base($"Record at byte offset {byteOffset} exceeds the configured " +
               $"maximum record length of {maxRecordLength} bytes.",
               FileStatus.PermanentError)
    {
        ByteOffset = byteOffset;
        MaxRecordLength = maxRecordLength;
    }

    /// <summary>The absolute file offset of the oversized record.</summary>
    public long ByteOffset { get; }

    /// <summary>The configured maximum record length, in bytes.</summary>
    public int MaxRecordLength { get; }
}
