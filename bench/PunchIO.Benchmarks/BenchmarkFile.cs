using System.Text;
using PunchIO.Devices;
using PunchIO.Framing;

namespace PunchIO.Benchmarks;

/// <summary>
/// Which generated data set a benchmark moves, so the throughput column can
/// turn its mean time into bytes per second.
/// </summary>
public enum BenchmarkData
{
    /// <summary>The Fujitsu variable-record file, or output of the same size.</summary>
    VariableFile,

    /// <summary>The line-sequential file, or output of the same size.</summary>
    LineFile,
}

/// <summary>
/// Creates and caches the multi-gigabyte data files the benchmarks read, so
/// the cost of generating them is not attributed to anything being measured.
/// </summary>
/// <remarks>
/// <para>
/// The data set defaults to 4 GiB per file. That is large enough that a single
/// pass runs for seconds rather than milliseconds, so device throughput rather
/// than call overhead dominates, and small enough that a full run of the suite
/// finishes in minutes. Set <see cref="SizeVariable"/> to override it, in MiB.
/// </para>
/// <para>
/// Size alone does not defeat the operating system's cache on a machine with
/// tens of gigabytes of memory; the read benchmarks also evict the file before
/// every iteration with <see cref="PageCache.Evict"/>.
/// </para>
/// </remarks>
public static class BenchmarkFile
{
    /// <summary>The body length of every generated record, in bytes.</summary>
    public const int RecordLength = 200;

    /// <summary>
    /// The environment variable that overrides the data set size, in MiB.
    /// </summary>
    public const string SizeVariable = "PUNCHIO_BENCH_SIZE_MIB";

    private const long DefaultSizeMiB = 4096;

    /// <summary>The Fujitsu prefix and suffix around each record.</summary>
    private const int VariableFraming = 8;

    /// <summary>The line feed after each line.</summary>
    private const int LineFraming = 1;

    /// <summary>The requested data set size in bytes.</summary>
    public static long TargetSizeInBytes { get; } = ReadTargetSize();

    /// <summary>
    /// The number of records in every generated file: however many framed
    /// variable records fit in <see cref="TargetSizeInBytes"/>.
    /// </summary>
    public static long RecordCount { get; } = TargetSizeInBytes / (RecordLength + VariableFraming);

    /// <summary>The exact size of the variable-record file, in bytes.</summary>
    public static long VariableFileBytes => RecordCount * (RecordLength + VariableFraming);

    /// <summary>The exact size of the line-sequential file, in bytes.</summary>
    public static long LineFileBytes => RecordCount * (RecordLength + LineFraming);

    /// <summary>Where the generated input files live.</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "punchio-benchmark-data");

    /// <summary>Where the write benchmarks put their output.</summary>
    public static string WriteDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "punchio-benchmark-write");

    /// <summary>
    /// Whether the unbuffered backend is available where the files live, which
    /// decides whether the benchmarks include it.
    /// </summary>
    public static bool SupportsUnbuffered => BlockDeviceFactory.UseNativeFor(DataDirectory);

    /// <summary>The backends worth measuring on this machine.</summary>
    public static IReadOnlyList<BlockDevicePolicy> Backends { get; } =
        SupportsUnbuffered
            ? [BlockDevicePolicy.ForceManaged, BlockDevicePolicy.ForceNative]
            : [BlockDevicePolicy.ForceManaged];

    /// <summary>The size of a data set in bytes.</summary>
    /// <param name="data">The data set.</param>
    /// <returns>Its size in bytes.</returns>
    public static long BytesOf(BenchmarkData data) => data switch
    {
        BenchmarkData.VariableFile => VariableFileBytes,
        BenchmarkData.LineFile => LineFileBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(data), data, null),
    };

    /// <summary>A fresh copy of the record body every generated file repeats.</summary>
    /// <returns>The record body.</returns>
    public static byte[] Record() => Encoding.ASCII.GetBytes(new string('X', RecordLength));

    /// <summary>Ensures a variable-record file exists and returns its path.</summary>
    /// <returns>The path to the file.</returns>
    public static string EnsureVariableFile() =>
        Ensure($"variable-{RecordCount}-{RecordLength}.dat", VariableFileBytes, async path =>
        {
            var record = Record();

            await using var writer = RecordFile.CreateVariableWrite(
                path, VariableRecordDescriptor.Fujitsu);

            for (long i = 0; i < RecordCount; i++)
                await writer.WriteAsync(record);

            await writer.CompleteAsync();
            await writer.FlushAsync(toDisk: true);
        });

    /// <summary>Ensures a line-sequential file exists and returns its path.</summary>
    /// <returns>The path to the file.</returns>
    public static string EnsureLineFile() =>
        Ensure($"lines-{RecordCount}-{RecordLength}.txt", LineFileBytes, async path =>
        {
            var record = Record();

            await using var writer = RecordFile.CreateLineSequentialWrite(path);

            for (long i = 0; i < RecordCount; i++)
                await writer.WriteAsync(record);

            await writer.CompleteAsync();
            await writer.FlushAsync(toDisk: true);
        });

    /// <summary>Formats a byte count for a progress message.</summary>
    /// <param name="bytes">The count.</param>
    /// <returns>The count in GiB to one decimal place.</returns>
    public static string Describe(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):F1} GiB";

    private static string Ensure(string fileName, long expectedBytes, Func<string, Task> create)
    {
        Directory.CreateDirectory(DataDirectory);

        string path = Path.Combine(DataDirectory, fileName);

        if (File.Exists(path)) return path;

        // Generate under a temporary name and rename on success, so a run that
        // is interrupted part-way cannot leave a truncated file that the next
        // run mistakes for a complete one.
        string temp = path + ".tmp";

        Console.WriteLine($"// Generating {Describe(expectedBytes)} data file: {path}");

        create(temp).GetAwaiter().GetResult();
        File.Move(temp, path, overwrite: true);

        return path;
    }

    private static long ReadTargetSize()
    {
        string? configured = Environment.GetEnvironmentVariable(SizeVariable);

        long mib = long.TryParse(configured, out long parsed) && parsed > 0
            ? parsed
            : DefaultSizeMiB;

        return mib * 1024 * 1024;
    }
}
