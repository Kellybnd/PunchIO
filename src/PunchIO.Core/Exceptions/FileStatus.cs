namespace PunchIO;

/// <summary>
/// A two-character COBOL file status code, as reported through an external
/// file handler interface.
/// </summary>
public readonly struct FileStatus : IEquatable<FileStatus>
{
    /// <summary>Initializes a status from its two-character code.</summary>
    /// <param name="code">Exactly two characters, for example <c>"00"</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="code"/> is not exactly two characters long.
    /// </exception>
    public FileStatus(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (code.Length != 2)
            throw new ArgumentException("A COBOL file status is exactly two characters.", nameof(code));

        Code = code;
    }

    /// <summary>The two-character status code.</summary>
    public string Code { get; }

    /// <summary>Successful completion.</summary>
    public static FileStatus Ok => new("00");

    /// <summary>End of file reached on a sequential read.</summary>
    public static FileStatus EndOfFile => new("10");

    /// <summary>The requested record does not exist.</summary>
    public static FileStatus RecordNotFound => new("23");

    /// <summary>The file was not found when opening.</summary>
    public static FileStatus FileNotFound => new("35");

    /// <summary>The file's attributes conflict with those requested.</summary>
    public static FileStatus AttributeMismatch => new("39");

    /// <summary>A permanent input/output error.</summary>
    public static FileStatus PermanentError => new("90");

    /// <inheritdoc />
    public bool Equals(FileStatus other) => string.Equals(Code, other.Code, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is FileStatus other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Code?.GetHashCode(StringComparison.Ordinal) ?? 0;

    /// <inheritdoc />
    public override string ToString() => Code ?? string.Empty;

    /// <summary>Compares two statuses for equality.</summary>
    /// <param name="left">The first status.</param>
    /// <param name="right">The second status.</param>
    /// <returns><see langword="true"/> when the codes are identical.</returns>
    public static bool operator ==(FileStatus left, FileStatus right) => left.Equals(right);

    /// <summary>Compares two statuses for inequality.</summary>
    /// <param name="left">The first status.</param>
    /// <param name="right">The second status.</param>
    /// <returns><see langword="true"/> when the codes differ.</returns>
    public static bool operator !=(FileStatus left, FileStatus right) => !left.Equals(right);
}
