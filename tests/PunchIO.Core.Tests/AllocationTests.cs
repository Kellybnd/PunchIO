using System.Text;
using PunchIO.Framing;
using Xunit;

namespace PunchIO.Core.Tests;

/// <summary>
/// The zero-allocation claim, asserted rather than measured in a benchmark
/// report nobody re-runs. Steady state means after the pump's slab and the
/// reader's stitch buffer exist; those are per-file, not per-record.
/// </summary>
public sealed class AllocationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"punchio-alloc-{Guid.NewGuid():N}");

    public AllocationTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif

    /// <summary>
    /// Allocation figures are only meaningful in Release. Debug emits async state
    /// machines as classes rather than structs, so every await allocates, and
    /// RecordGuard deliberately wraps each record to catch stale access.
    /// </summary>
    private static void RequireReleaseBuild() =>
        Assert.SkipWhen(IsDebugBuild, "Allocation is only meaningful in a Release build.");

    private string NewPath() => Path.Combine(_directory, $"{Guid.NewGuid():N}.dat");

    /// <summary>
    /// Asserts bytes allocated per record, which is the figure the documentation
    /// quotes. A total-byte ceiling generous enough to absorb per-file setup is
    /// also generous enough to hide a small per-record regression.
    /// </summary>
    private static void AssertPerRecord(long allocated, int records, double ceiling, string what)
    {
        double perRecord = allocated / (double)records;

        Assert.True(
            perRecord < ceiling,
            $"{what} allocated {perRecord:N2} bytes per record " +
            $"({allocated:N0} across {records:N0}), ceiling {ceiling:N2}");
    }

    /// <summary>
    /// Bytes allocated on this thread while draining <paramref name="records"/>
    /// records, after a warm-up pass that settles the buffers.
    /// </summary>
    private static async Task<long> MeasureDrainAsync(Func<Task> warmUp, Func<Task<long>> drain)
    {
        await warmUp();

        // Settle anything the warm-up left pending so it is not attributed below.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Process-wide, not per-thread: an async continuation can resume on any
        // pool thread, and a per-thread counter would simply lose those bytes.
        long before = GC.GetTotalAllocatedBytes(precise: true);
        long records = await drain();
        long after = GC.GetTotalAllocatedBytes(precise: true);

        Assert.True(records > 0, "the drain read no records");

        return after - before;
    }

    [Fact]
    public async Task ReadingVariableRecordsAllocatesNothingPerRecord()
    {
        RequireReleaseBuild();

        // Hoisted: TestContext.Current is an AsyncLocal lookup, and resolving
        // it inside a 50,000-iteration loop measures the harness, not the library.
        var ct = Ct;

        var path = NewPath();

        await using (var writer = RecordFile.CreateVariableWrite(
            path, VariableRecordDescriptor.Fujitsu))
        {
            for (int i = 0; i < 50_000; i++)
                await writer.WriteAsync(new byte[80], ct);
        }

        long allocated = await MeasureDrainAsync(
            warmUp: async () =>
            {
                await using var reader = RecordFile.OpenVariableRead(
                    path, VariableRecordDescriptor.Fujitsu);

                while (await reader.MoveNextAsync(ct)) { }
            },
            drain: async () =>
            {
                await using var reader = RecordFile.OpenVariableRead(
                    path, VariableRecordDescriptor.Fujitsu);

                long count = 0;
                long checksum = 0;

                while (await reader.MoveNextAsync(ct))
                {
                    count++;
                    checksum += reader.Current.Length;
                }

                Assert.Equal(50_000 * 80, checksum);
                return count;
            });

        // Measured at 1.47 bytes per record: the stitch buffer growing at block
        // boundaries, which is per-file rather than per-record. The ceiling is
        // set close enough to catch a regression that reintroduced genuine
        // per-record allocation, which is what the documentation claims.
        AssertPerRecord(allocated, records: 50_000, ceiling: 3.0, "reading variable records");
    }

    [Fact]
    public async Task ReadingFixedBlockRecordsAllocatesNothingPerRecord()
    {
        RequireReleaseBuild();

        // Hoisted: TestContext.Current is an AsyncLocal lookup, and resolving
        // it inside a 50,000-iteration loop measures the harness, not the library.
        var ct = Ct;

        var path = NewPath();
        var record = Encoding.ASCII.GetBytes(new string('X', 80));

        await using (var writer = RecordFile.CreateFixedBlockWrite(path, recordLength: 80))
        {
            for (int i = 0; i < 50_000; i++)
                await writer.WriteAsync(record, ct);
        }

        long allocated = await MeasureDrainAsync(
            warmUp: async () =>
            {
                await using var reader = RecordFile.OpenFixedBlockRead(path, 80);
                while (await reader.MoveNextAsync(ct)) { }
            },
            drain: async () =>
            {
                await using var reader = RecordFile.OpenFixedBlockRead(path, 80);

                long count = 0;
                while (await reader.MoveNextAsync(ct)) count++;

                return count;
            });

        // Measured at 0.06 bytes per record. Fixed block is the cleanest path
        // because ResolveBlockSize picks a multiple of the record length, so
        // records never straddle and no stitch buffer is ever needed.
        AssertPerRecord(allocated, records: 50_000, ceiling: 0.5, "reading fixed records");
    }

    [Fact]
    public async Task WritingAllocatesNothingPerRecord()
    {
        RequireReleaseBuild();

        // Hoisted: TestContext.Current is an AsyncLocal lookup, and resolving
        // it inside a 50,000-iteration loop measures the harness, not the library.
        var ct = Ct;

        var record = new byte[80];

        long allocated = await MeasureDrainAsync(
            warmUp: async () =>
            {
                await using var writer = RecordFile.CreateVariableWrite(
                    NewPath(), VariableRecordDescriptor.Fujitsu);

                for (int i = 0; i < 50_000; i++)
                    await writer.WriteAsync(record, ct);
            },
            drain: async () =>
            {
                await using var writer = RecordFile.CreateVariableWrite(
                    NewPath(), VariableRecordDescriptor.Fujitsu);

                for (int i = 0; i < 50_000; i++)
                    await writer.WriteAsync(record, ct);

                return 50_000;
            });

        // Measured at 0.09 bytes per record. The encoder writes framing around
        // the caller's buffer rather than staging a copy, so nothing here scales
        // with record count.
        AssertPerRecord(allocated, records: 50_000, ceiling: 0.5, "writing records");
    }

    [Fact]
    public void FramingItselfAllocatesNothing()
    {
        // The framers are the hot loop's inner core; anything allocating here
        // would be a defect no buffering could hide.
        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);
        var buffer = new byte[8 + 80];

        VariableRecordFramer.WriteFraming(buffer, 80, VariableRecordDescriptor.Fujitsu);

        // Warm up the JIT before measuring.
        for (int i = 0; i < 1000; i++)
            framer.TryFrame(buffer, false, out _, out _, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 1_000_000; i++)
            framer.TryFrame(buffer, false, out _, out _, out _);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
