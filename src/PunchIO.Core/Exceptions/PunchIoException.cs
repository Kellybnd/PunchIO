namespace PunchIO;

/// <summary>
/// The base type for failures raised by PunchIO. Carries the COBOL file status
/// an external file handler should report for this failure, so the EXFH boundary
/// reads a property rather than maintaining a second translation table that
/// could disagree with this one.
/// </summary>
public class PunchIoException : IOException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="message">A description of the failure.</param>
    /// <param name="status">The COBOL file status for this failure.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public PunchIoException(string message, FileStatus status, Exception? innerException = null)
        : base(message, innerException) => Status = status;

    /// <summary>The COBOL file status corresponding to this failure.</summary>
    public FileStatus Status { get; }
}
