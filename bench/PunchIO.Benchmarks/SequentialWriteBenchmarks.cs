using System.Buffers.Binary;
using System.Text;
using BenchmarkDotNet.Attributes;
using PunchIO.Devices;
using PunchIO.Framing;

namespace PunchIO.Benchmarks;

/// <summary>
/// Sequential write throughput across queue depths, block sizes and backends,
/// measured to durability.
/// </summary>
/// <remarks>
/// Every pass writes a fresh multi-gigabyte file and ends with a flush to disk,
/// so the time covers getting the bytes onto the device rather than into the
/// operating system's write-back cache. The previous output is deleted before
/// each pass, so no pass is measured against a file that already owns its
/// clusters.
/// </remarks>
[Config(typeof(LargeFileConfig))]
public class SequentialWriteBenchmarks
{
    private byte[] _record = [];
    private string _path = string.Empty;

    /// <summary>The number of writes to keep outstanding.</summary>
    [Params(1, 4, 16)]
    public int QueueDepth { get; set; }

    /// <summary>The size of each write in bytes.</summary>
    [Params(256 * 1024, 1024 * 1024)]
    public int BlockSize { get; set; }

    /// <summary>Which backend to write through.</summary>
    [ParamsSource(nameof(Backends))]
    public BlockDevicePolicy Backend { get; set; }

    /// <summary>The backends available on this machine.</summary>
    public static IReadOnlyList<BlockDevicePolicy> Backends => BenchmarkFile.Backends;

    [GlobalSetup]
    public void Setup()
    {
        _record = BenchmarkFile.Record();

        Directory.CreateDirectory(BenchmarkFile.WriteDirectory);
        _path = Path.Combine(BenchmarkFile.WriteDirectory, "sweep.dat");
    }

    [IterationSetup]
    public void DeletePreviousOutput() => File.Delete(_path);

    [GlobalCleanup]
    public void Cleanup() => File.Delete(_path);

    [Benchmark]
    [Transfers(BenchmarkData.VariableFile)]
    public async Task<long> Write()
    {
        var options = new FileIoOptions
        {
            QueueDepth = QueueDepth,
            BlockSize = BlockSize,
            PinBlockSize = true,
            Backend = Backend,
        };

        await using var writer = RecordFile.CreateVariableWrite(
            _path, VariableRecordDescriptor.Fujitsu, options);

        for (long i = 0; i < BenchmarkFile.RecordCount; i++)
            await writer.WriteAsync(_record);

        await writer.CompleteAsync();
        await writer.FlushAsync(toDisk: true);

        return writer.Length;
    }
}

/// <summary>
/// PunchIO against what a .NET developer would write instead, producing the
/// same multi-gigabyte files and flushing them to disk.
/// </summary>
/// <remarks>
/// <para>
/// Each format has its own baseline. For variable records it is a buffered
/// <see cref="FileStream"/> writing the Fujitsu prefix, body and suffix of each
/// record, which is the like-for-like comparison; the raw 1 MiB block write
/// alongside it does no framing and is the ceiling for buffered .NET I/O. For
/// lines it is <see cref="StreamWriter.WriteLine(string)"/>.
/// </para>
/// <para>
/// Every benchmark ends with a flush to disk. Without it the buffered .NET
/// paths would report the time to fill the write-back cache, and the unbuffered
/// backend, which has no cache to fill, would be measured against a number that
/// does not include the device at all.
/// </para>
/// </remarks>
[Config(typeof(LargeFileConfig))]
public class WriteComparisonBenchmarks
{
    private const string VariableRecords = "Variable records";
    private const string LineSequential = "Line sequential";
    private const int BufferSize = 1024 * 1024;

    private byte[] _record = [];
    private string _line = string.Empty;
    private byte[] _block = [];
    private string _path = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _record = BenchmarkFile.Record();
        _line = new string('X', BenchmarkFile.RecordLength);
        _block = new byte[BufferSize];
        Array.Fill(_block, (byte)'X');

        Directory.CreateDirectory(BenchmarkFile.WriteDirectory);
        _path = Path.Combine(BenchmarkFile.WriteDirectory, "compare.dat");
    }

    [IterationSetup]
    public void DeletePreviousOutput() => File.Delete(_path);

    [GlobalCleanup]
    public void Cleanup() => File.Delete(_path);

    /// <summary>The ceiling: 1 MiB block writes with no framing at all.</summary>
    [Benchmark]
    [BenchmarkCategory(VariableRecords)]
    [Transfers(BenchmarkData.VariableFile)]
    public long FileStreamBlocks()
    {
        using var stream = new FileStream(
            _path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1, FileOptions.None);

        long remaining = BenchmarkFile.VariableFileBytes;

        while (remaining > 0)
        {
            int count = (int)Math.Min(remaining, _block.Length);
            stream.Write(_block, 0, count);
            remaining -= count;
        }

        stream.Flush(flushToDisk: true);

        return stream.Position;
    }

    /// <summary>
    /// The like-for-like baseline: a buffered stream writing each record's
    /// prefix, body and suffix.
    /// </summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory(VariableRecords)]
    [Transfers(BenchmarkData.VariableFile)]
    public long FileStreamRecords()
    {
        using var stream = new FileStream(
            _path, FileMode.Create, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.None);

        Span<byte> field = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(field, _record.Length);

        for (long i = 0; i < BenchmarkFile.RecordCount; i++)
        {
            stream.Write(field);
            stream.Write(_record);
            stream.Write(field);
        }

        stream.Flush(flushToDisk: true);

        return stream.Position;
    }

    [Benchmark]
    [BenchmarkCategory(VariableRecords)]
    [Transfers(BenchmarkData.VariableFile)]
    public Task<long> PunchIoPortable() => WriteVariableAsync(BlockDevicePolicy.ForceManaged);

    [Benchmark]
    [BenchmarkCategory(VariableRecords)]
    [Transfers(BenchmarkData.VariableFile)]
    public Task<long> PunchIoUnbuffered() => WriteVariableAsync(BlockDevicePolicy.ForceNative);

    /// <summary>What a .NET developer writing lines would write instead.</summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory(LineSequential)]
    [Transfers(BenchmarkData.LineFile)]
    public long StreamWriterLines()
    {
        using var stream = new FileStream(
            _path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1, FileOptions.None);

        using var writer = new StreamWriter(stream, Encoding.ASCII, BufferSize) { NewLine = "\n" };

        for (long i = 0; i < BenchmarkFile.RecordCount; i++)
            writer.WriteLine(_line);

        writer.Flush();
        stream.Flush(flushToDisk: true);

        return stream.Position;
    }

    [Benchmark]
    [BenchmarkCategory(LineSequential)]
    [Transfers(BenchmarkData.LineFile)]
    public async Task<long> PunchIoLines()
    {
        await using var writer = RecordFile.CreateLineSequentialWrite(_path);

        for (long i = 0; i < BenchmarkFile.RecordCount; i++)
            await writer.WriteAsync(_record);

        await writer.CompleteAsync();
        await writer.FlushAsync(toDisk: true);

        return writer.Length;
    }

    private async Task<long> WriteVariableAsync(BlockDevicePolicy policy)
    {
        var options = new FileIoOptions { Backend = policy };

        await using var writer = RecordFile.CreateVariableWrite(
            _path, VariableRecordDescriptor.Fujitsu, options);

        for (long i = 0; i < BenchmarkFile.RecordCount; i++)
            await writer.WriteAsync(_record);

        await writer.CompleteAsync();
        await writer.FlushAsync(toDisk: true);

        return writer.Length;
    }
}
