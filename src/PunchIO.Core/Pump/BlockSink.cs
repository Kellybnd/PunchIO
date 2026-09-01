using System.Runtime.ExceptionServices;
using PunchIO.Buffers;
using PunchIO.Devices;

namespace PunchIO.Pump;

/// <summary>
/// Writes a file as a stream of fixed-size blocks, keeping several writes
/// outstanding at once.
/// </summary>
/// <remarks>
/// <para>
/// The caller appends bytes; the sink issues a write only when a block is
/// <em>exactly</em> full. That invariant is what keeps every write offset a
/// multiple of the block size, which an unbuffered device requires — flushing a
/// partly filled block mid-stream would leave the next write unaligned and the
/// device would reject it.
/// </para>
/// <para>
/// A record larger than one block is therefore split across blocks rather than
/// refused: correctness of the offset sequence matters more than avoiding a
/// second copy.
/// </para>
/// <para>
/// The partially filled final block is written by <see cref="CompleteAsync"/>,
/// not by <see cref="FlushAsync"/>. On a device that requires tail padding it is
/// padded up to an alignment boundary and the file is then truncated back to its
/// logical length.
/// </para>
/// </remarks>
public sealed class BlockSink : IAsyncDisposable
{
    private readonly IBlockDevice _device;
    private readonly IBlockSlab _slab;
    private readonly ValueTask[] _pending;
    private readonly bool[] _issued;
    private readonly int _blockSize;
    private readonly int _slotCount;

    private int _current;
    private int _used;
    private long _nextOffset;
    private long _flushed;
    private bool _padded;
    private bool _completed;
    private bool _disposed;
    private ExceptionDispatchInfo? _fault;

    private BlockSink(IBlockDevice device, int queueDepth, int blockSize)
    {
        _device = device;
        _blockSize = blockSize;
        _slotCount = queueDepth + 1;
        _slab = device.AllocateSlab(_slotCount, blockSize);
        _pending = new ValueTask[_slotCount];
        _issued = new bool[_slotCount];
    }

    /// <summary>Creates a write pump over a device.</summary>
    /// <param name="device">The device to write. The sink takes ownership and disposes it.</param>
    /// <param name="queueDepth">
    /// The number of writes to keep outstanding while a block is being filled;
    /// must be positive.
    /// </param>
    /// <param name="blockSize">
    /// The size of each write in bytes; must be positive and a multiple of the
    /// device's alignment.
    /// </param>
    /// <returns>A sink ready to accept bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="blockSize"/> is not a multiple of the device's alignment.
    /// </exception>
    public static BlockSink Create(IBlockDevice device, int queueDepth, int blockSize)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);

        if (blockSize % device.Alignment != 0)
        {
            throw new ArgumentException(
                $"Block size {blockSize} must be a multiple of the device's " +
                $"{device.Alignment}-byte alignment.",
                nameof(blockSize));
        }

        return new BlockSink(device, queueDepth, blockSize);
    }

    /// <summary>The number of logical bytes accepted so far.</summary>
    public long Length => _flushed + _used;

    /// <summary>The size of each block in bytes.</summary>
    public int BlockSize => _blockSize;

    /// <summary>Appends bytes, issuing a write each time a block fills exactly.</summary>
    /// <param name="source">The bytes to append. May be larger than one block.</param>
    /// <param name="cancellationToken">Cancels before the next block is awaited.</param>
    /// <returns>A task that completes when the bytes have been buffered.</returns>
    /// <exception cref="ObjectDisposedException">The sink has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The sink has already been completed.</exception>
    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_completed)
            throw new InvalidOperationException("The sink has already been completed.");

        _fault?.Throw();

        while (!source.IsEmpty)
        {
            if (_used == _blockSize)
                await RotateAsync(cancellationToken).ConfigureAwait(false);

            int take = Math.Min(_blockSize - _used, source.Length);
            source.Span[..take].CopyTo(_slab.Block(_current).Span[_used..]);

            _used += take;
            source = source[take..];
        }
    }

    /// <summary>Waits for outstanding writes to finish, optionally forcing them to media.</summary>
    /// <param name="toDisk">Forces data to stable media rather than to the operating system.</param>
    /// <param name="cancellationToken">Cancels the device flush.</param>
    /// <returns>A task that completes when the flush has finished.</returns>
    /// <remarks>
    /// The partially filled block is <em>not</em> written here. Writing it would
    /// leave the next write offset unaligned on an unbuffered device, so the tail
    /// is the exclusive business of <see cref="CompleteAsync"/>.
    /// </remarks>
    public async ValueTask FlushAsync(bool toDisk = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await DrainAsync(rethrow: true).ConfigureAwait(false);
        await _device.FlushAsync(toDisk, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the final partial block, drains every outstanding write, and
    /// restores the file's logical length if the tail had to be padded.
    /// </summary>
    /// <param name="cancellationToken">Cancels the final write.</param>
    /// <returns>A task that completes once the file is whole on disk.</returns>
    /// <exception cref="ObjectDisposedException">The sink has been disposed.</exception>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_completed) return;

        _fault?.Throw();

        long logicalLength = _flushed + _used;

        if (_used > 0)
        {
            int count = _used;

            if (_device.RequiresTailPadding && count % _device.Alignment != 0)
            {
                // An unbuffered write cannot be a partial sector, so the tail is
                // rounded up with zeros and the file truncated below.
                int padded = RoundUp(count, _device.Alignment);
                _slab.Block(_current).Span[count..padded].Clear();
                count = padded;
                _padded = true;
            }

            _used = 0;

            await _device
                .WriteAsync(_slab.Block(_current)[..count], _nextOffset, cancellationToken)
                .ConfigureAwait(false);
        }

        await DrainAsync(rethrow: true).ConfigureAwait(false);

        if (_padded)
            await _device.SetLengthAsync(logicalLength, cancellationToken).ConfigureAwait(false);

        _flushed = logicalLength;
        _completed = true;
    }

    private async ValueTask RotateAsync(CancellationToken cancellationToken)
    {
        int slot = _current;
        int count = _used;

        _pending[slot] = _device.WriteAsync(
            _slab.Block(slot)[..count], _nextOffset, CancellationToken.None);
        _issued[slot] = true;

        _nextOffset += count;
        _flushed += count;
        _used = 0;
        _current = (_current + 1) % _slotCount;

        // The slot about to be filled may still be owned by the kernel.
        if (!_issued[_current]) return;

        _issued[_current] = false;

        try
        {
            await _pending[_current].ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _fault = ExceptionDispatchInfo.Capture(ex);
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async ValueTask DrainAsync(bool rethrow)
    {
        ExceptionDispatchInfo? first = null;

        // Start at the slot being filled and wrap, so slots are visited oldest
        // first -- which is file order, so a failure reports deterministically.
        for (int i = 0; i < _slotCount; i++)
        {
            int slot = (_current + i) % _slotCount;

            if (!_issued[slot]) continue;

            _issued[slot] = false;

            try
            {
                await _pending[slot].ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                first ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        if (first is null) return;

        _fault ??= first;

        if (rethrow) first.Throw();
    }

    private static int RoundUp(int value, int alignment) =>
        (value + alignment - 1) / alignment * alignment;

    /// <summary>
    /// Completes the sink if it has not already been completed, then drains and
    /// releases the buffers and the device.
    /// </summary>
    /// <returns>A task that completes once the sink is fully quiesced.</returns>
    /// <remarks>
    /// The drain in the cleanup path is not optional: each outstanding write owns
    /// a pinned or unmanaged buffer inside the slab, and releasing the slab while
    /// the kernel still owns one corrupts memory.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        try
        {
            if (!_completed && _fault is null)
                await CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _disposed = true;

            await DrainAsync(rethrow: false).ConfigureAwait(false);

            _slab.Dispose();
            await _device.DisposeAsync().ConfigureAwait(false);
        }
    }
}
