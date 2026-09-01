using PunchIO.Framing;
using PunchIO.Readers;
using PunchIO.Writers;

namespace PunchIO;

/// <summary>The record format a file profile describes.</summary>
public enum RecordFormat
{
    /// <summary>Records terminated by a line terminator.</summary>
    LineSequential,

    /// <summary>Records of a single fixed length, packed with no delimiters.</summary>
    FixedBlock,

    /// <summary>Records carrying their own length in a prefix, and sometimes a suffix.</summary>
    VariableBlock,
}

/// <summary>
/// A validated file profile: everything needed to open one named file, resolved
/// from configuration and checked once.
/// </summary>
/// <remarks>
/// Profiles are equally constructible in code. Configuration is a convenience
/// over the same options objects, never a required path.
/// </remarks>
public sealed class FileProfile
{
    /// <summary>Initializes a profile.</summary>
    /// <param name="name">The name callers resolve this profile with.</param>
    /// <param name="format">The record format.</param>
    /// <param name="io">I/O tuning.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public FileProfile(string name, RecordFormat format, FileIoOptions io)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(io);

        Name = name;
        Format = format;
        Io = io;
    }

    /// <summary>The name callers resolve this profile with.</summary>
    public string Name { get; }

    /// <summary>The record format.</summary>
    public RecordFormat Format { get; }

    /// <summary>I/O tuning.</summary>
    public FileIoOptions Io { get; }

    /// <summary>Line-sequential behavior, when <see cref="Format"/> is line sequential.</summary>
    public LineSequentialOptions? Line { get; init; }

    /// <summary>The record length, when <see cref="Format"/> is fixed block.</summary>
    public int RecordLength { get; init; }

    /// <summary>How a short final record is treated, when the format is fixed block.</summary>
    public TrailingPartialRecord TrailingPartialRecord { get; init; }

    /// <summary>The byte used to pad a short record on write, when the format is fixed block.</summary>
    public byte PadByte { get; init; } = 0x20;

    /// <summary>The on-disk layout, when <see cref="Format"/> is variable block.</summary>
    public VariableRecordDescriptor Variable { get; init; }

    /// <summary>Opens the file for reading in this profile's format.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>A reader positioned before the first record.</returns>
    /// <exception cref="PunchIoException">The file could not be opened.</exception>
    public IRecordReader OpenRead(string path) => Format switch
    {
        RecordFormat.LineSequential =>
            RecordFile.OpenLineSequentialRead(path, Line, Io),

        RecordFormat.FixedBlock =>
            RecordFile.OpenFixedBlockRead(path, RecordLength, Io, TrailingPartialRecord),

        _ => RecordFile.OpenVariableRead(path, Variable, Io),
    };

    /// <summary>Creates or truncates the file for writing in this profile's format.</summary>
    /// <param name="path">The file to write.</param>
    /// <returns>A writer ready to accept records.</returns>
    /// <exception cref="PunchIoException">The file could not be opened.</exception>
    public IRecordWriter CreateWrite(string path) => Format switch
    {
        RecordFormat.LineSequential =>
            RecordFile.CreateLineSequentialWrite(path, Line, Io),

        RecordFormat.FixedBlock =>
            RecordFile.CreateFixedBlockWrite(path, RecordLength, Io, PadByte),

        _ => RecordFile.CreateVariableWrite(path, Variable, Io),
    };
}

/// <summary>Resolves file profiles by name.</summary>
public interface IFileProfileProvider
{
    /// <summary>Gets a profile.</summary>
    /// <param name="name">The profile's name, matched case-insensitively.</param>
    /// <returns>The resolved profile.</returns>
    /// <exception cref="FileProfileException">No profile of that name is configured.</exception>
    FileProfile Get(string name);

    /// <summary>Gets a profile, or <see langword="null"/> when none is configured.</summary>
    /// <param name="name">The profile's name, matched case-insensitively.</param>
    /// <returns>The resolved profile, or <see langword="null"/>.</returns>
    FileProfile? Find(string name);

    /// <summary>The names of every configured profile.</summary>
    IReadOnlyCollection<string> Names { get; }
}
