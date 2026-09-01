namespace PunchIO.Writers;

/// <summary>
/// Writes records to a file, without exposing which format they are in.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="Readers.IRecordReader"/>, and carrying the same
/// trade: one virtual call per record in exchange for not needing to know the
/// format at compile time.
/// </remarks>
public interface IRecordWriter : IAsyncDisposable
{
    /// <summary>The number of records written so far.</summary>
    long RecordNumber { get; }

    /// <summary>The number of bytes written so far, including framing.</summary>
    long Length { get; }

    /// <summary>Appends one record.</summary>
    /// <param name="record">The record's bytes, without any framing.</param>
    /// <param name="cancellationToken">Cancels before the next block is awaited.</param>
    /// <returns>A task that completes when the record has been buffered.</returns>
    ValueTask WriteAsync(ReadOnlyMemory<byte> record, CancellationToken cancellationToken = default);

    /// <summary>Waits for outstanding writes to finish, optionally forcing them to media.</summary>
    /// <param name="toDisk">Forces data to stable media rather than to the operating system.</param>
    /// <param name="cancellationToken">Cancels the device flush.</param>
    /// <returns>A task that completes when the flush has finished.</returns>
    ValueTask FlushAsync(bool toDisk = false, CancellationToken cancellationToken = default);

    /// <summary>Writes the final partial block and finishes the file.</summary>
    /// <param name="cancellationToken">Cancels the final write.</param>
    /// <returns>A task that completes once the file is whole on disk.</returns>
    ValueTask CompleteAsync(CancellationToken cancellationToken = default);
}
