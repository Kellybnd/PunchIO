using System.Text;
using PunchIO.Framing;
using Xunit;

namespace PunchIO.Core.Tests;

/// <summary>
/// Asserts that reading and writing do not allocate per record.
/// </summary>
/// <remarks>
/// <para>
/// Measured as a marginal cost: the same operation is run over N records and
/// over 2N, and the difference is divided by N. Opening a file allocates a
/// buffer slab of <c>BlockSize × (QueueDepth + 1)</c>, several megabytes at the
/// defaults, and that cost is identical in both runs so it cancels out.
/// </para>
/// <para>
/// Dividing a single run's total by its record count does not work. The slab
/// dominates, it is charged to the garbage collector only on backends that use
/// managed memory, and the resulting figure varies by platform while telling you
/// nothing about per-record behaviour.
/// </para>
/// </remarks>
public sealed class AllocationTests : IDisposable
{
    private const int Baseline = 50_000;
    private const int Extra = 50_000;

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
    /// Bytes allocated per record, isolated from the fixed cost of opening a file.
    /// </summary>
    /// <param name="run">Runs the operation over the given number of records.</param>
    /// <returns>The marginal allocation per record, in bytes.</returns>
    private static async Task<double> MeasurePerRecordAsync(Func<int, Task> run)
    {
        // Warm up so first-call costs are not attributed to either measurement.
        await run(Baseline);

        long small = await MeasureAsync(() => run(Baseline));
        long large = await MeasureAsync(() => run(Baseline + Extra));

        return (large - small) / (double)Extra;
    }

    private static async Task<long> MeasureAsync(Func<Task> action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Process-wide and precise: async continuations resume on arbitrary pool
        // threads, so a per-thread counter would simply lose them.
        long before = GC.GetTotalAllocatedBytes(precise: true);
        await action();

        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    private static void AssertPerRecord(double perRecord, double ceiling, string what)
    {
        Assert.True(
            perRecord < ceiling,
            $"{what} allocated {perRecord:N2} bytes per record, ceiling {ceiling:N2}");
    }

    [Fact]
    public async Task ReadingVariableRecordsAllocatesNothingPerRecord()
    {
        RequireReleaseBuild();

        var ct = Ct;
        var record = new byte[80];
        string path = NewPath();

        await using (var writer = RecordFile.CreateVariableWrite(
            path, VariableRecordDescriptor.Fujitsu))
        {
            for (int i = 0; i < Baseline + Extra; i++)
                await writer.WriteAsync(record, ct);
        }

        double perRecord = await MeasurePerRecordAsync(async count =>
        {
            await using var reader = RecordFile.OpenVariableRead(
                path, VariableRecordDescriptor.Fujitsu);

            long read = 0;
            while (read < count && await reader.MoveNextAsync(ct)) read++;

            Assert.Equal(count, read);
        });

        AssertPerRecord(perRecord, ceiling: 1.0, "reading variable records");
    }

    [Fact]
    public async Task ReadingFixedBlockRecordsAllocatesNothingPerRecord()
    {
        RequireReleaseBuild();

        var ct = Ct;
        var record = Encoding.ASCII.GetBytes(new string('X', 80));
        string path = NewPath();

        await using (var writer = RecordFile.CreateFixedBlockWrite(path, recordLength: 80))
        {
            for (int i = 0; i < Baseline + Extra; i++)
                await writer.WriteAsync(record, ct);
        }

        double perRecord = await MeasurePerRecordAsync(async count =>
        {
            await using var reader = RecordFile.OpenFixedBlockRead(path, 80);

            long read = 0;
            while (read < count && await reader.MoveNextAsync(ct)) read++;

            Assert.Equal(count, read);
        });

        AssertPerRecord(perRecord, ceiling: 1.0, "reading fixed records");
    }

    [Fact]
    public async Task WritingAllocatesNothingPerRecord()
    {
        RequireReleaseBuild();

        var ct = Ct;
        var record = new byte[80];

        double perRecord = await MeasurePerRecordAsync(async count =>
        {
            await using var writer = RecordFile.CreateVariableWrite(
                NewPath(), VariableRecordDescriptor.Fujitsu);

            for (int i = 0; i < count; i++)
                await writer.WriteAsync(record, ct);
        });

        AssertPerRecord(perRecord, ceiling: 1.0, "writing records");
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
