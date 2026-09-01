using System.Runtime.ExceptionServices;
using PunchIO.Buffers;
using PunchIO.Devices;

namespace PunchIO.Pump;

/// <summary>
/// Reads a file as a stream of fixed-size blocks, keeping several reads
/// outstanding at once and delivering the results strictly in file order.
/// </summary>
/// <remarks>
/// <para>
/// The pump is a ring of slots. Every slot is issued at construction; the caller
/// then consumes slots in ring order, so completions may land in any order while
/// delivery stays sequential. A consumed slot is re-issued at the tail of the
/// file at the start of the <em>next</em> call, never before the current block is
/// returned, because re-issuing immediately would overwrite the block the caller
/// is still reading.
/// </para>
/// <para>
/// The ring holds one more block than <c>queueDepth</c> for exactly that reason:
/// one block is checked out to the caller while <c>queueDepth</c> reads remain in
/// flight.
/// </para>
/// <para>
/// Reads are issued ahead of the call that consumes them, so a cancellation token
/// passed to <see cref="NextBlockAsync"/> cannot cancel work already submitted.
/// Cancellation takes effect at the next block boundary;
/// <see cref="DisposeAsync"/> always drains what is outstanding.
/// </para>
/// </remarks>
public sealed class BlockSource : IAsyncDisposable
{
    private readonly IBlockDevice _device;
    private readonly IBlockSlab _slab;
    private readonly ValueTask<int>[] _pending;
    private readonly long[] _offsets;
    private readonly bool[] _issued;
    private readonly int _blockSize;
    private readonly int _slotCount;
    private readonly long _length;

    private readonly bool _ownsDevice;

    private long _nextOffset;
    private int _head;
    private int _recycle = -1;
    private long _blockOffset;
    private bool _completed;
    private bool _disposed;
    private ExceptionDispatchInfo? _fault;

    private BlockSource(IBlockDevice device, int queueDepth, int blockSize, bool ownsDevice)
    {
        _device = device;
        _ownsDevice = ownsDevice;
        _blockSize = blockSize;
        _slotCount = queueDepth + 1;
        _slab = device.AllocateSlab(_slotCount, blockSize);
        _pending = new ValueTask<int>[_slotCount];
        _offsets = new long[_slotCount];
        _issued = new bool[_slotCount];

        // Captured once as a hint so reads are not issued wholly past the end.
        // A zero-length read remains the authoritative end-of-file signal.
        _length = device.Length;

        for (int slot = 0; slot < _slotCount; slot++)
            Issue(slot);
    }

    /// <summary>Creates a pump over a device and begins reading immediately.</summary>
    /// <param name="device">The device to read. The pump takes ownership and disposes it.</param>
    /// <param name="queueDepth">
    /// The number of reads to keep outstanding while a block is checked out;
    /// must be positive.
    /// </param>
    /// <param name="blockSize">
    /// The size of each read in bytes; must be positive and a multiple of the
    /// device's alignment.
    /// </param>
    /// <param name="ownsDevice">
    /// When <see langword="true"/> (the default) the pump disposes the device
    /// with itself. Pass <see langword="false"/> to read through a device whose
    /// lifetime the caller manages, such as one also being used for random access.
    /// </param>
    /// <returns>A pump with its first reads already in flight.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="blockSize"/> is not a multiple of the device's alignment.
    /// </exception>
    public static BlockSource Create(
        IBlockDevice device, int queueDepth, int blockSize, bool ownsDevice = true)
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

        return new BlockSource(device, queueDepth, blockSize, ownsDevice);
    }

    /// <summary>The absolute file offset of the block most recently returned.</summary>
    public long BlockOffset => _blockOffset;

    /// <summary>The size of each block in bytes.</summary>
    public int BlockSize => _blockSize;

    /// <summary><see langword="true"/> once end of file has been reached.</summary>
    public bool IsCompleted => _completed;

    /// <summary>Returns the next block in file order.</summary>
    /// <param name="cancellationToken">Cancels before the next block is awaited.</param>
    /// <returns>
    /// The block's bytes, valid until the next call, or an empty result at end of file.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The pump has been disposed.</exception>
    public async ValueTask<ReadOnlyMemory<byte>> NextBlockAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A fault is latched so every later call reports the same failure at the
        // same point in the file, however the completions happened to race.
        _fault?.Throw();

        if (_completed) return ReadOnlyMemory<byte>.Empty;

        cancellationToken.ThrowIfCancellationRequested();

        // Re-issue the slot the caller was holding, now that it has moved on.
        if (_recycle >= 0)
        {
            Issue(_recycle);
            _recycle = -1;
        }

        int slot = _head;

        if (!_issued[slot])
        {
            _completed = true;
            return ReadOnlyMemory<byte>.Empty;
        }

        int filled;

        try
        {
            filled = await FillAsync(slot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _issued[slot] = false;
            _fault = ExceptionDispatchInfo.Capture(ex);
            throw;
        }

        _issued[slot] = false;

        if (filled == 0)
        {
            _completed = true;
            return ReadOnlyMemory<byte>.Empty;
        }

        _blockOffset = _offsets[slot];
        _head = (_head + 1) % _slotCount;
        _recycle = slot;

        return _slab.Block(slot)[..filled];
    }

    private void Issue(int slot)
    {
        if (_nextOffset >= _length)
        {
            _issued[slot] = false;
            return;
        }

        _offsets[slot] = _nextOffset;
        _pending[slot] = _device.ReadAsync(_slab.Block(slot), _nextOffset, CancellationToken.None);
        _issued[slot] = true;
        _nextOffset += _blockSize;
    }

    private async ValueTask<int> FillAsync(int slot, CancellationToken cancellationToken)
    {
        int total = await _pending[slot].ConfigureAwait(false);
        if (total == 0) return 0;

        long baseOffset = _offsets[slot];
        var block = _slab.Block(slot);

        // A read may return fewer bytes than requested before end of file --
        // routine on network storage. Fill the remainder before handing the
        // block on, so the framer never sees a hole.
        while (total < _blockSize && baseOffset + total < _length)
        {
            int n = await _device
                .ReadAsync(block[total..], baseOffset + total, cancellationToken)
                .ConfigureAwait(false);

            if (n == 0) break;

            total += n;
        }

        return total;
    }

    /// <summary>
    /// Drains every outstanding read, then releases the buffers and the device.
    /// </summary>
    /// <returns>A task that completes once the pump is fully quiesced.</returns>
    /// <remarks>
    /// The drain is not optional. Each outstanding read owns a pinned or
    /// unmanaged buffer inside the slab, and releasing the slab while the kernel
    /// still owns one of them corrupts memory rather than merely leaking it.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        for (int slot = 0; slot < _slotCount; slot++)
        {
            if (!_issued[slot]) continue;

            _issued[slot] = false;

            try
            {
                _ = await _pending[slot].ConfigureAwait(false);
            }
            catch
            {
                // Draining: a read that failed still released its buffer, and
                // the caller is tearing down, so there is nobody to report to.
            }
        }

        _slab.Dispose();

        if (_ownsDevice)
            await _device.DisposeAsync().ConfigureAwait(false);
    }
}
