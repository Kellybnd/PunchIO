using System.Runtime.CompilerServices;
using PunchIO.Framing;

namespace PunchIO.Readers;

/// <summary>
/// Reads line-sequential records, applying the content transforms the byte
/// reader deliberately leaves alone: tab expansion and null unescaping.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SequentialReader{TFramer}"/> always hands back the file's own
/// bytes, because framing and rewriting are separate concerns and most callers
/// want neither cost nor surprise. This facade adds the rewriting for callers
/// who configured it — and when nothing is configured it passes records straight
/// through, so the zero-copy path is preserved.
/// </para>
/// <para>
/// The lifetime contract is unchanged: a record is valid only until the next call
/// to <see cref="MoveNextAsync"/>.
/// </para>
/// </remarks>
public sealed class LineSequentialReader : IRecordReader
{
    private readonly SequentialReader<LineSequentialFramer> _inner;
    private readonly LineRecordTransform _transform;
    private byte[] _scratch = [];

    internal LineSequentialReader(
        SequentialReader<LineSequentialFramer> inner, LineSequentialOptions options)
    {
        _inner = inner;
        _transform = new LineRecordTransform(options);
    }

    /// <summary>
    /// The record most recently read. Valid only until the next call to
    /// <see cref="MoveNextAsync"/>.
    /// </summary>
    public ReadOnlyMemory<byte> Current { get; private set; }

    /// <summary>The one-based ordinal of <see cref="Current"/> within the file.</summary>
    public long RecordNumber => _inner.RecordNumber;

    /// <summary>The absolute file offset at which <see cref="Current"/> begins.</summary>
    public long RecordOffset => _inner.RecordOffset;

    /// <summary>Advances to the next record.</summary>
    /// <param name="cancellationToken">Cancels before the next block is awaited.</param>
    /// <returns><see langword="true"/> if a record was read; otherwise <see langword="false"/>.</returns>
    public async ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken = default)
    {
        if (!await _inner.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            Current = default;
            return false;
        }

        if (_transform.IsIdentity)
        {
            Current = _inner.Current;
            return true;
        }

        var raw = _inner.Current.Span;
        EnsureScratch(_transform.MaxExpansion(raw.Length));

        if (!_transform.TryDecode(raw, _scratch, out int written))
        {
            throw new RecordFormatException(
                "The record could not be decoded into the available buffer",
                _inner.RecordOffset);
        }

        Current = _scratch.AsMemory(0, written);
        return true;
    }

    /// <summary>Enumerates every record in the file.</summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>
    /// A sequence of records, each valid only until the enumerator advances.
    /// </returns>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await MoveNextAsync(cancellationToken).ConfigureAwait(false))
            yield return Current;
    }

    private void EnsureScratch(int required)
    {
        if (_scratch.Length >= required) return;

        _scratch = new byte[Math.Max(required, Math.Max(4096, _scratch.Length * 2))];
    }

    /// <summary>Disposes the reader and everything beneath it.</summary>
    /// <returns>A task that completes once the pump has drained.</returns>
    public async ValueTask DisposeAsync()
    {
        Current = default;

        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
