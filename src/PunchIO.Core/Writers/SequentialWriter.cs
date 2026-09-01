using PunchIO.Framing;
using PunchIO.Pump;

namespace PunchIO.Writers;

/// <summary>
/// Writes records to a file by driving a <see cref="BlockSink"/> through a record
/// encoder.
/// </summary>
/// <typeparam name="TEncoder">
/// The encoder to use. Constrained to a struct so the encoding calls are inlined
/// rather than dispatched once per record.
/// </typeparam>
/// <remarks>
/// The caller's record buffer is handed to the sink directly for every format
/// that does not have to rewrite bytes, which is all of them except line
/// sequential with null escaping. Headers and trailers are a few bytes written
/// from a small reusable buffer, so a record costs one copy into the block — the
/// one the sink has to make anyway.
/// </remarks>
public sealed class SequentialWriter<TEncoder> : IRecordWriter
    where TEncoder : struct, IRecordEncoder
{
    private readonly BlockSink _sink;
    private readonly TEncoder _encoder;
    private readonly byte[] _framing;

    private byte[] _scratch = [];
    private long _recordNumber;
    private bool _fileHeaderWritten;
    private bool _disposed;

    private SequentialWriter(BlockSink sink, TEncoder encoder)
    {
        _sink = sink;
        _encoder = encoder;
        _framing = new byte[Math.Max(
            1,
            Math.Max(
                encoder.MaxFileHeaderLength,
                Math.Max(encoder.MaxHeaderLength, encoder.MaxTrailerLength)))];
    }

    /// <summary>Creates a writer over a block sink.</summary>
    /// <param name="sink">The sink to write. The writer takes ownership and disposes it.</param>
    /// <param name="encoder">The encoder that turns records into on-disk bytes.</param>
    /// <returns>A writer ready to accept records.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    public static SequentialWriter<TEncoder> Create(BlockSink sink, TEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(sink);

        return new SequentialWriter<TEncoder>(sink, encoder);
    }

    /// <summary>The number of records written so far.</summary>
    public long RecordNumber => _recordNumber;

    /// <summary>The number of bytes written so far, including framing.</summary>
    public long Length => _sink.Length;

    /// <summary>Appends one record.</summary>
    /// <param name="record">The record's bytes, without any framing.</param>
    /// <param name="cancellationToken">Cancels before the next block is awaited.</param>
    /// <returns>A task that completes when the record has been buffered.</returns>
    /// <exception cref="ObjectDisposedException">The writer has been disposed.</exception>
    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> record, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureFileHeaderAsync(cancellationToken).ConfigureAwait(false);

        ReadOnlyMemory<byte> body;

        if (_encoder.RewritesBody)
        {
            EnsureScratch(_encoder.MaxBodyLength(record.Length));
            body = _scratch.AsMemory(0, _encoder.WriteBody(record.Span, _scratch));
        }
        else
        {
            body = record[.._encoder.BodyLength(record.Span)];
        }

        int header = _encoder.WriteHeader(_framing, body.Length);

        if (header > 0)
            await _sink.WriteAsync(_framing.AsMemory(0, header), cancellationToken).ConfigureAwait(false);

        if (!body.IsEmpty)
            await _sink.WriteAsync(body, cancellationToken).ConfigureAwait(false);

        // Safe to reuse the framing buffer: the sink copies into its block before
        // its WriteAsync completes, so the header bytes are already gone.
        int trailer = _encoder.WriteTrailer(_framing, body.Length);

        if (trailer > 0)
            await _sink.WriteAsync(_framing.AsMemory(0, trailer), cancellationToken).ConfigureAwait(false);

        _recordNumber++;
    }

    /// <summary>Waits for outstanding writes to finish, optionally forcing them to media.</summary>
    /// <param name="toDisk">Forces data to stable media rather than to the operating system.</param>
    /// <param name="cancellationToken">Cancels the device flush.</param>
    /// <returns>A task that completes when the flush has finished.</returns>
    /// <remarks>
    /// The partially filled block reaches disk at <see cref="CompleteAsync"/>, not
    /// here; writing it early would leave the next write offset unaligned on an
    /// unbuffered device.
    /// </remarks>
    public async ValueTask FlushAsync(
        bool toDisk = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureFileHeaderAsync(cancellationToken).ConfigureAwait(false);
        await _sink.FlushAsync(toDisk, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the final partial block and finishes the file.</summary>
    /// <param name="cancellationToken">Cancels the final write.</param>
    /// <returns>A task that completes once the file is whole on disk.</returns>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Even a file that never received a record still gets its header, so an
        // empty Micro Focus file is a valid one rather than a zero-byte stub.
        await EnsureFileHeaderAsync(cancellationToken).ConfigureAwait(false);
        await _sink.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the file header, once, before any record reaches the sink.</summary>
    private async ValueTask EnsureFileHeaderAsync(CancellationToken cancellationToken)
    {
        if (_fileHeaderWritten) return;

        _fileHeaderWritten = true;

        if (_encoder.MaxFileHeaderLength == 0) return;

        int written = _encoder.WriteFileHeader(_framing);

        if (written > 0)
        {
            await _sink.WriteAsync(_framing.AsMemory(0, written), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void EnsureScratch(int required)
    {
        if (_scratch.Length >= required) return;

        _scratch = new byte[Math.Max(required, Math.Max(4096, _scratch.Length * 2))];
    }

    /// <summary>Completes the file if it has not been completed, then releases the sink.</summary>
    /// <returns>A task that completes once the writer is fully quiesced.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        // Before the sink, not after: disposing it finishes the file, and a
        // format with a header needs that header in it even when the caller
        // wrote no records and never called CompleteAsync.
        await EnsureFileHeaderAsync(CancellationToken.None).ConfigureAwait(false);

        _disposed = true;

        await _sink.DisposeAsync().ConfigureAwait(false);
    }
}
