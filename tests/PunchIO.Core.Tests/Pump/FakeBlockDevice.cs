using PunchIO.Buffers;
using PunchIO.Devices;

namespace PunchIO.Core.Tests.Pump;

/// <summary>
/// A block device that can misbehave on demand: short reads, genuinely
/// interleaved completions, injected faults, and alignment requirements. This is
/// how the pump's hard cases get tested without needing exotic storage.
/// </summary>
internal sealed class FakeBlockDevice : IBlockDevice
{
    // The pump issues genuinely concurrent operations and Task.Yield puts their
    // continuations on the thread pool, so this double has to be thread-safe.
    // An unsynchronised Array.Resize here loses whole blocks.
    private readonly object _gate = new();

    private readonly TaskCompletionSource _gateOpened =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private byte[] _store;
    private long _length;
    private int _outstanding;
    private int _waitingAtGate;

    public FakeBlockDevice(byte[] initialContent)
    {
        _store = (byte[])initialContent.Clone();
        _length = initialContent.Length;
    }

    public FakeBlockDevice()
        : this([])
    {
    }

    /// <summary>Caps every read, simulating the short reads real network storage returns.</summary>
    public int MaxReadLength { get; set; } = int.MaxValue;

    /// <summary>A read starting at this offset throws. Negative disables the fault.</summary>
    public long FaultAtReadOffset { get; set; } = -1;

    /// <summary>A write starting at this offset throws. Negative disables the fault.</summary>
    public long FaultAtWriteOffset { get; set; } = -1;

    /// <summary>The highest number of operations ever in flight simultaneously.</summary>
    /// <remarks>
    /// Safe to assert as an upper bound: a maximum can under-report if
    /// completions interleave with submissions, but it can never over-report.
    /// To assert a <em>lower</em> bound on concurrency use <see cref="GateAt"/>,
    /// which is deterministic.
    /// </remarks>
    public int PeakOutstanding { get; private set; }

    /// <summary>
    /// Holds every operation at a barrier until this many are waiting at it
    /// simultaneously, then releases them all. Zero disables the barrier.
    /// </summary>
    /// <remarks>
    /// This is how a lower bound on concurrency is proved rather than sampled:
    /// if the pump ever serialised its I/O the barrier would never open and the
    /// operation would fail on the timeout below.
    /// </remarks>
    public int GateAt { get; set; }

    /// <summary>The number of operations currently in flight.</summary>
    public int Outstanding => _outstanding;

    /// <summary>Every write issued, in the order it was submitted.</summary>
    public List<(long Offset, int Length)> Writes { get; } = [];

    /// <summary>Every logical length set through <see cref="SetLengthAsync"/>.</summary>
    public List<long> SetLengths { get; } = [];

    /// <summary><see langword="true"/> once the device has been disposed.</summary>
    public bool IsDisposed { get; private set; }

    public long Length
    {
        get { lock (_gate) return _length; }
    }

    public int Alignment { get; set; } = 1;

    public bool RequiresTailPadding { get; set; }

    /// <summary>The device's current contents, truncated to its logical length.</summary>
    public byte[] Content
    {
        get { lock (_gate) return _store.AsSpan(0, (int)_length).ToArray(); }
    }

    public IBlockSlab AllocateSlab(int blockCount, int blockSize) =>
        Alignment > 1
            ? new AlignedNativeSlab(blockCount, blockSize, Alignment)
            : new PinnedArraySlab(blockCount, blockSize);

    public async ValueTask<int> ReadAsync(
        Memory<byte> destination, long fileOffset, CancellationToken cancellationToken)
    {
        Enter();

        try
        {
            await ReachGateAsync();

            // Vary the number of yields per block so completions genuinely
            // interleave. If the pump ever delivered in completion order rather
            // than file order, the content assertions would catch it.
            await YieldSeveralTimes(fileOffset);

            cancellationToken.ThrowIfCancellationRequested();

            if (FaultAtReadOffset >= 0 && fileOffset == FaultAtReadOffset)
                throw new IOException($"Injected read fault at offset {fileOffset}.");

            lock (_gate)
            {
                if (fileOffset >= _length) return 0;

                int available = (int)Math.Min(_length - fileOffset, destination.Length);
                int count = Math.Min(available, MaxReadLength);

                _store.AsSpan((int)fileOffset, count).CopyTo(destination.Span);
                return count;
            }
        }
        finally
        {
            Exit();
        }
    }

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> source, long fileOffset, CancellationToken cancellationToken)
    {
        Enter();

        lock (_gate)
        {
            Writes.Add((fileOffset, source.Length));
        }

        try
        {
            await ReachGateAsync();
            await YieldSeveralTimes(fileOffset);

            cancellationToken.ThrowIfCancellationRequested();

            if (FaultAtWriteOffset >= 0 && fileOffset == FaultAtWriteOffset)
                throw new IOException($"Injected write fault at offset {fileOffset}.");

            lock (_gate)
            {
                long end = fileOffset + source.Length;

                if (end > _store.Length)
                    Array.Resize(ref _store, (int)Math.Max(end, _store.Length * 2L));

                source.Span.CopyTo(_store.AsSpan((int)fileOffset));

                if (end > _length) _length = end;
            }
        }
        finally
        {
            Exit();
        }
    }

    public ValueTask FlushAsync(bool toDisk, CancellationToken cancellationToken) => default;

    public ValueTask SetLengthAsync(long length, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            SetLengths.Add(length);
            _length = length;
        }

        return default;
    }

    public void Dispose() => IsDisposed = true;

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return default;
    }

    private Task ReachGateAsync()
    {
        if (GateAt <= 0 || _gateOpened.Task.IsCompleted) return Task.CompletedTask;

        lock (_gate)
        {
            if (++_waitingAtGate >= GateAt) _gateOpened.TrySetResult();
        }

        // A timeout rather than an indefinite wait, so a pump that failed to
        // pipeline fails the test with a message instead of hanging the run.
        return _gateOpened.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task YieldSeveralTimes(long fileOffset)
    {
        int yields = (int)(Math.Abs(fileOffset / 64) % 4);

        for (int i = 0; i <= yields; i++)
            await Task.Yield();
    }

    private void Enter()
    {
        lock (_gate)
        {
            _outstanding++;
            if (_outstanding > PeakOutstanding) PeakOutstanding = _outstanding;
        }
    }

    private void Exit()
    {
        lock (_gate)
        {
            _outstanding--;
        }
    }
}
