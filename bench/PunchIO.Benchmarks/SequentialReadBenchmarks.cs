using System.Text;
using PunchIO.Devices;
using PunchIO.Framing;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace PunchIO.Benchmarks;

/// <summary>
/// End-to-end sequential read throughput across queue depths, block sizes and
/// backends. This is the sweep that sets the shipped defaults in
/// <see cref="FileIoOptions"/>.
/// </summary>
/// <remarks>
/// <strong>Read the caveat before quoting these numbers.</strong> The data file
/// is a few hundred megabytes and therefore sits entirely in the operating
/// system's cache after the first pass, so what is measured here is pipeline and
/// CPU overhead, not device throughput. That makes the queue-depth and block-size
/// comparisons meaningful and the unbuffered-versus-portable comparison
/// <em>not</em> meaningful: bypassing a cache that is serving every request for
/// free can only lose. Use <see cref="BenchmarkFile.SizeInBytes"/> larger than
/// physical memory on the target machine before drawing conclusions about the
/// unbuffered backend.
/// </remarks>
[Config(typeof(ShortInProcessConfig))]
public class SequentialReadBenchmarks
{
    private string _path = string.Empty;

    /// <summary>The number of reads to keep outstanding.</summary>
    [Params(1, 2, 4, 8, 16)]
    public int QueueDepth { get; set; }

    /// <summary>The size of each read in bytes.</summary>
    [Params(64 * 1024, 256 * 1024, 1024 * 1024, 4 * 1024 * 1024)]
    public int BlockSize { get; set; }

    [GlobalSetup]
    public void Setup() => _path = BenchmarkFile.EnsureVariableFile();

    [Benchmark]
    public async Task<long> Read()
    {
        var options = new FileIoOptions
        {
            QueueDepth = QueueDepth,
            BlockSize = BlockSize,
            PinBlockSize = true,
            Backend = BlockDevicePolicy.ForceManaged,
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
/// The portable backend against the unbuffered one, and both against a plain
/// <see cref="FileStream"/> baseline.
/// </summary>
/// <remarks>
/// Subject to the same cache caveat as <see cref="SequentialReadBenchmarks"/>.
/// A negative result for the unbuffered backend at this file size is expected
/// and is not evidence against it.
/// </remarks>
[Config(typeof(ShortInProcessConfig))]
public class BackendBenchmarks
{
    private string _variablePath = string.Empty;
    private string _linePath = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _variablePath = BenchmarkFile.EnsureVariableFile();
        _linePath = BenchmarkFile.EnsureLineFile();
    }

    [Benchmark(Baseline = true)]
    public async Task<long> VariableRecordsPortable() =>
        await ReadVariableAsync(BlockDevicePolicy.ForceManaged);

    [Benchmark]
    public async Task<long> VariableRecordsUnbuffered() =>
        await ReadVariableAsync(
            OperatingSystem.IsWindows() && BlockDeviceFactory.UseNativeFor(_variablePath)
                ? BlockDevicePolicy.ForceNative
                : BlockDevicePolicy.ForceManaged);

    /// <summary>The obvious thing a .NET developer would write instead.</summary>
    [Benchmark]
    public async Task<long> FileStreamBaseline()
    {
        await using var stream = new FileStream(
            _variablePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, useAsync: true);

        var buffer = new byte[1024 * 1024];
        long total = 0;
        int read;

        while ((read = await stream.ReadAsync(buffer)) > 0)
            total += read;

        return total;
    }

    [Benchmark]
    public async Task<long> LineSequential()
    {
        await using var reader = RecordFile.OpenLineSequentialRead(_linePath);

        long total = 0;

        while (await reader.MoveNextAsync())
            total += reader.Current.Length;

        return total;
    }

    /// <summary>What a .NET developer reaching for lines would write instead.</summary>
    [Benchmark]
    public async Task<long> StreamReaderBaseline()
    {
        using var reader = new StreamReader(_linePath, Encoding.ASCII);

        long total = 0;
        string? line;

        while ((line = await reader.ReadLineAsync()) is not null)
            total += line.Length;

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

/// <summary>Sequential write throughput across queue depths and block sizes.</summary>
[Config(typeof(ShortInProcessConfig))]
public class SequentialWriteBenchmarks
{
    private byte[] _record = [];
    private string _directory = string.Empty;

    /// <summary>The number of writes to keep outstanding.</summary>
    [Params(1, 4, 16)]
    public int QueueDepth { get; set; }

    /// <summary>The size of each write in bytes.</summary>
    [Params(256 * 1024, 1024 * 1024)]
    public int BlockSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _record = Encoding.ASCII.GetBytes(new string('X', BenchmarkFile.RecordLength));
        _directory = Path.Combine(Path.GetTempPath(), "punchio-bench-write");
        Directory.CreateDirectory(_directory);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Benchmark]
    public async Task<long> Write()
    {
        var options = new FileIoOptions
        {
            QueueDepth = QueueDepth,
            BlockSize = BlockSize,
            PinBlockSize = true,
            Backend = BlockDevicePolicy.ForceManaged,
        };

        string path = Path.Combine(_directory, "out.dat");

        await using var writer = RecordFile.CreateVariableWrite(
            path, VariableRecordDescriptor.Fujitsu, options);

        for (int i = 0; i < BenchmarkFile.RecordCount; i++)
            await writer.WriteAsync(_record);

        return writer.RecordNumber;
    }
}
