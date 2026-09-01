namespace PunchIO.Cobol;

/// <summary>
/// What a COBOL program asked for, independent of which runtime asked.
/// </summary>
/// <remarks>
/// Both Micro Focus and Fujitsu ultimately request the same handful of things.
/// Normalising to this enum is what lets one dispatcher serve both, with each
/// dialect contributing only a thin adapter that translates its own control
/// block.
/// </remarks>
public enum FileOperation
{
    /// <summary>The opcode was not recognised.</summary>
    Unknown,

    /// <summary>Open for reading.</summary>
    OpenInput,

    /// <summary>Open for writing, replacing any existing contents.</summary>
    OpenOutput,

    /// <summary>Open for reading and writing.</summary>
    OpenIo,

    /// <summary>Open for appending.</summary>
    OpenExtend,

    /// <summary>Close the file.</summary>
    Close,

    /// <summary>Read the next record in sequence.</summary>
    ReadNext,

    /// <summary>Read the record identified by the control block's record number.</summary>
    ReadRandom,

    /// <summary>Append a record, or write one at a given record number.</summary>
    Write,

    /// <summary>Replace the record last read, or the one at a given record number.</summary>
    Rewrite,

    /// <summary>Delete the record at a given record number.</summary>
    Delete,

    /// <summary>Position for subsequent sequential reads.</summary>
    Start,

    /// <summary>Release any record lock.</summary>
    Unlock,

    /// <summary>Commit a transaction. Accepted and ignored.</summary>
    Commit,

    /// <summary>Roll back a transaction. Accepted and ignored.</summary>
    Rollback,
}

/// <summary>What a dialect adapter extracted from a control block.</summary>
/// <remarks>
/// A mutable class reused across calls rather than a struct passed by value, so
/// a busy COBOL program does not allocate one of these per file operation. The
/// record area travels separately as a span, since it points into the caller's
/// memory.
/// </remarks>
public sealed class FileOperationRequest
{
    /// <summary>What was asked for.</summary>
    public FileOperation Operation { get; set; }

    /// <summary>The file name, as the COBOL program supplied it.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>The open file this operation refers to, or zero when opening.</summary>
    public int HandleId { get; set; }

    /// <summary>The one-based record number, for record-addressed operations.</summary>
    public long RecordNumber { get; set; }

    /// <summary>The length of the record the program is presenting, for writes.</summary>
    public int RecordLength { get; set; }

    /// <summary>Resets every field, so the instance can be reused.</summary>
    public void Reset()
    {
        Operation = FileOperation.Unknown;
        FileName = string.Empty;
        HandleId = 0;
        RecordNumber = 0;
        RecordLength = 0;
    }
}

/// <summary>What the dispatcher wants written back into the control block.</summary>
public sealed class FileOperationResult
{
    /// <summary>The COBOL file status to report.</summary>
    public FileStatus Status { get; set; } = FileStatus.Ok;

    /// <summary>The open file's identifier, set when a file is opened.</summary>
    public int HandleId { get; set; }

    /// <summary>The length of the record delivered, for reads.</summary>
    public int RecordLength { get; set; }

    /// <summary>The record number of the record delivered, for sequential reads.</summary>
    public long RecordNumber { get; set; }

    /// <summary>Resets every field, so the instance can be reused.</summary>
    public void Reset()
    {
        Status = FileStatus.Ok;
        HandleId = 0;
        RecordLength = 0;
        RecordNumber = 0;
    }
}
