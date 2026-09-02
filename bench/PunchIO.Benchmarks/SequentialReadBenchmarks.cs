using System.Buffers.Binary;
using System.Text;
using BenchmarkDotNet.Attributes;
using PunchIO.Devices;
using PunchIO.Framing;

namespace PunchIO.Benchmarks;

/// <summary>
/// End-to-end sequential read throughput across queue depths, block sizes and
/// backends, from a cold cache. This is the sweep that sets the shipped defaults
/// in <see cref="FileIoOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// The file is evicted from the operating system's cache before every pass, so
/// each pass reads the whole multi-gigabyte file from the device. That is what
/// makes queue depth meaningful here: it exists to overlap device latency, and
/// a cache-resident file presents none to overlap.
/// </para>
/// <para>
/// The cells at queue depth 4 with 1 MiB blocks are the shipped defaults, and so
/// are the same measurement as <see cref="ReadComparisonBenchmarks.PunchIoPortable"/>
/// and <see cref="ReadComparisonBenchmarks.PunchIoUnbuffered"/>. A sweep that
/// disagrees with the comparison class by more than its own error bars was
/// disturbed by something else on the machine while it ran, and should be
/// repeated rather than interpreted.
/// </para>
/// </remarks>
[Config(typeof(LargeFileConfig))]
public class SequentialReadBenchmarks
{
    private string _path = string.Empty;

    /// <summary>The number of reads to keep outstanding.</summary>
    [Params(1, 2, 4, 8, 16)]
    public int QueueDepth { get; set; }

    /// <summary>The size of each read in bytes.</summary>
    [Params(64 * 1024, 256 * 1024, 1024 * 1024, 4 * 1024 * 1024)]
    public int BlockSize { get; set; }

    /// <summary>Which backend to read through.</summary>
    [ParamsSource(nameof(Backends))]
    public BlockDevicePolicy Backend { get; set; }

    /// <summary>The backends available on this machine.</summary>
    public static IReadOnlyList<BlockDevicePolicy> Backends => BenchmarkFile.Backends;

    [GlobalSetup]
    public void Setup() => _path = BenchmarkFile.EnsureVariableFile();

    [IterationSetup]
    public void EvictFromCache() => PageCache.Evict(_path);

    [Benchmark]
    [Transfers(BenchmarkData.VariableFile)]
    public async Task<long> Read()
    {
        var options = new FileIoOptions
        {
            QueueDepth = QueueDepth,
            BlockSize = BlockSize,
            PinBlockSize = true,
            Backend = Backend,
        };

        await using var reader = RecordFile.OpenVariableRead(
            _path, VariableRecordDescriptor.Fujitsu, options);

        long total = 0;

        while (await reader.MoveNextAsync())
            total += reader.Current.Length;

        return total;
    }
}

/// <summary>
/// PunchIO against what a .NET developer would write instead, reading the same
/// multi-gigabyte files from a cold cache.
/// </summary>
/// <remarks>
/// <para>
/// Each format has its own baseline. For variable records it is a buffered
/// <see cref="FileStream"/> framing Fujitsu records by hand, which is the
/// like-for-like comparison; the raw 1 MiB block read alongside it does no
/// framing and is the ceiling for buffered .NET I/O. For lines it is
/// <see cref="StreamReader.ReadLineAsync()"/>.
/// </para>
/// <para>
/// The .NET baselines read synchronously where a developer would, because three
/// asynchronous reads per record would be a strawman. PunchIO is measured
/// through its asynchronous API, which is the only one it has.
/// </para>
/// </remarks>
[Config(typeof(LargeFileConfig))]
public class ReadComparisonBenchmarks
{
    private const string VariableRecords = "Variable records";
    private const string LineSequential = "Line sequential";
    private const int BufferSize = 1024 * 1024;

    private string _variablePath = string.Empty;
    private string _linePath = string.Empty;
    private byte[] _block = [];
    private byte[] _record = [];

    [GlobalSetup]
    public void Setup()
    {
        _variablePath = BenchmarkFile.EnsureVariableFile();
        _linePath = BenchmarkFile.EnsureLineFile();
        _block = new byte[BufferSize];
        _record = new byte[FileIoOptions.Default.MaxRecordLength];
    }

    [IterationSetup]
    public void EvictFromCache()
    {
        PageCache.Evict(_variablePath);
        PageCache.Evict(_linePath);
    }

    /// <summary>The ceiling: 1 MiB block reads with no framing at all.</summary>
    [Benchmark]
    [BenchmarkCategory(VariableRecords)]
    [Transfers(BenchmarkData.VariableFile)]
    public long FileStreamBlocks()
    {
        using var stream = new FileStream(
            _variablePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1, FileOptions.SequentialScan);

        long total = 0;
        int read;

        while ((read = stream.Read(_block)) > 0)
            total += read;

        return total;
    }

    /// <summary>
    /// The like-for-like baseline: a buffered stream framing each record by hand.
    /// </summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory(VariableRecords)]
    [Transfers(BenchmarkData.VariableFile)]
    public long FileStreamRecords()
    {
        using var stream = new FileStream(
            _variablePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.SequentialScan);

        Span<byte> field = stackalloc byte[4];
        long total = 0;

        while (true)
        {
            int read = stream.ReadAtLeast(field, field.Length, throwOnEndOfStream: false);

            if (read == 0) break;

            if (read < field.Length)
                throw new InvalidDataException("Truncated record prefix.");

            int length = BinaryPrimitives.ReadInt32LittleEndian(field);

            if ((uint)length > (uint)_record.Length)
                throw new InvalidDataException($"Record length {length} is out of range.");

            stream.ReadExactly(_record, 0, length);
            stream.ReadExactly(field);

            if (BinaryPrimitives.ReadInt32LittleEndian(field) != length)
                throw new InvalidDataException("Record suffix does not match its prefix.");

            total += length;
        }

        return total;
    }

    [Benchmark]
    [BenchmarkCategory(VariableRecords)]
    [Transfers(BenchmarkData.VariableFile)]
    public Task<long> PunchIoPortable() => ReadVariableAsync(BlockDevicePolicy.ForceManaged);

    [Benchmark]
    [BenchmarkCategory(VariableRecords)]
    [Transfers(BenchmarkData.VariableFile)]
    public Task<long> PunchIoUnbuffered() => ReadVariableAsync(BlockDevicePolicy.ForceNative);

    /// <summary>What a .NET developer reaching for lines would write instead.</summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory(LineSequential)]
    [Transfers(BenchmarkData.LineFile)]
    public async Task<long> StreamReaderLines()
    {
        using var reader = new StreamReader(
            new FileStream(
                _linePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.SequentialScan),
            Encoding.ASCII, detectEncodingFromByteOrderMarks: false, BufferSize);

        long total = 0;
        string? line;

        while ((line = await reader.ReadLineAsync()) is not null)
            total += line.Length;

        return total;
    }

    [Benchmark]
    [BenchmarkCategory(LineSequential)]
    [Transfers(BenchmarkData.LineFile)]
    public async Task<long> PunchIoLines()
    {
        await using var reader = RecordFile.OpenLineSequentialRead(_linePath);

        long total = 0;

        while (await reader.MoveNextAsync())
            total += reader.Current.Length;

        return total;
    }

    private async Task<long> ReadVariableAsync(BlockDevicePolicy policy)
    {
        var options = new FileIoOptions { Backend = policy };

        await using var reader = RecordFile.OpenVariableRead(
            _variablePath, VariableRecordDescriptor.Fujitsu, options);

        long total = 0;

        while (await reader.MoveNextAsync())
            total += reader.Current.Length;

        return total;
    }
}
