namespace PunchIO;

/// <summary>
/// A configured file profile is invalid. Raised when the profile is resolved,
/// not when a record fails to frame, so a mistake in configuration is reported
/// against the key that caused it rather than against a byte offset deep in a
/// file.
/// </summary>
public sealed class FileProfileException : PunchIoException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="profileName">The name of the offending file profile.</param>
    /// <param name="key">The configuration key at fault, if a single one is to blame.</param>
    /// <param name="message">What is wrong with it.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public FileProfileException(
        string profileName, string? key, string message, Exception? innerException = null)
        : base(
            key is null
                ? $"File profile '{profileName}': {message}"
                : $"File profile '{profileName}', key '{key}': {message}",
            FileStatus.AttributeMismatch,
            innerException)
    {
        ProfileName = profileName;
        Key = key;
    }

    /// <summary>The name of the offending file profile.</summary>
    public string ProfileName { get; }

    /// <summary>The configuration key at fault, when a single one is to blame.</summary>
    public string? Key { get; }
}
