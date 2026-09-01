using System.Text;
using PunchIO.Framing;

namespace PunchIO.Benchmarks;

/// <summary>
/// Creates and caches the data files the benchmarks read, so the cost of
/// generating them is not attributed to anything being measured.
/// </summary>
public static class BenchmarkFile
{
    /// <summary>The body length of every generated record, in bytes.</summary>
    public const int RecordLength = 200;

    /// <summary>The number of records in a generated file.</summary>
    public const int RecordCount = 1_500_000;

    /// <summary>
    /// The approximate size of a generated variable-record file, in bytes.
    /// </summary>
    /// <remarks>
    /// About 312 MB, which fits comfortably in the operating system's cache on
    /// any modern machine. That is deliberate for a suite meant to run in
    /// minutes, and it is exactly why the unbuffered comparison here is not
    /// decisive: raise this above physical memory before drawing conclusions
    /// about cache bypass.
    /// </remarks>
    public static long SizeInBytes => (long)RecordCount * (RecordLength + 8);

    private static readonly string Directory =
        Path.Combine(Path.GetTempPath(), "punchio-benchmark-data");

    /// <summary>Ensures a variable-record file exists and returns its path.</summary>
    /// <returns>The path to the file.</returns>
    public static string EnsureVariableFile()
    {
        string path = Path.Combine(Directory, $"variable-{RecordCount}-{RecordLength}.dat");

        return Ensure(path, async () =>
        {
            var record = Body();

            await using var writer = RecordFile.CreateVariableWrite(
                path, VariableRecordDescriptor.Fujitsu);

            for (int i = 0; i < RecordCount; i++)
                await writer.WriteAsync(record);
        });
    }

    /// <summary>Ensures a line-sequential file exists and returns its path.</summary>
    /// <returns>The path to the file.</returns>
    public static string EnsureLineFile()
    {
        string path = Path.Combine(Directory, $"lines-{RecordCount}-{RecordLength}.txt");

        return Ensure(path, async () =>
        {
            var record = Body();

            await using var writer = RecordFile.CreateLineSequentialWrite(path);

            for (int i = 0; i < RecordCount; i++)
                await writer.WriteAsync(record);
        });
    }

    private static byte[] Body() => Encoding.ASCII.GetBytes(new string('X', RecordLength));

    private static string Ensure(string path, Func<Task> create)
    {
        System.IO.Directory.CreateDirectory(Directory);

        if (File.Exists(path)) return path;

        create().GetAwaiter().GetResult();

        return path;
    }
}
