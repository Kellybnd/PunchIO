namespace PunchIO.Readers;

/// <summary>
/// Reads records from a file, without exposing which format they are in.
/// </summary>
/// <remarks>
/// The generic readers exist so the framing call inlines and costs nothing per
/// record. This interface exists for the callers that cannot know the format at
/// compile time — a configuration-driven profile, or an external file handler
/// dispatching on a control block — and trades one virtual call per record for
/// that. Reach for the concrete type when the format is known.
/// </remarks>
public interface IRecordReader : IAsyncDisposable
{
    /// <summary>
    /// The record most recently read. Valid only until the next call to
    /// <see cref="MoveNextAsync"/>.
    /// </summary>
    ReadOnlyMemory<byte> Current { get; }

    /// <summary>The one-based ordinal of <see cref="Current"/> within the file.</summary>
    long RecordNumber { get; }

    /// <summary>The absolute file offset at which <see cref="Current"/> begins.</summary>
    long RecordOffset { get; }

    /// <summary>Advances to the next record.</summary>
    /// <param name="cancellationToken">Cancels before the next block is awaited.</param>
    /// <returns><see langword="true"/> if a record was read; otherwise <see langword="false"/>.</returns>
    ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken = default);

    /// <summary>Enumerates every remaining record.</summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>
    /// A sequence of records, each valid only until the enumerator advances.
    /// </returns>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken cancellationToken = default);
}
