namespace PunchIO.Benchmarks;

/// <summary>
/// Drops a file from the operating system's cache, so a read benchmark starts
/// from the device rather than from memory.
/// </summary>
/// <remarks>
/// <para>
/// On Windows, opening a file with <c>FILE_FLAG_NO_BUFFERING</c> while no other
/// handle has it open cached makes NTFS flush and purge its cached pages. Opening
/// and immediately closing such a handle is therefore a per-file cache purge
/// that needs no privilege and touches nothing else on the machine. A 2 GiB
/// file that read at over 6 GB/s from cache reads at under 4 GB/s after this,
/// which is the device.
/// </para>
/// <para>
/// No other platform is handled. There the read benchmarks measure a
/// cache-resident file, and a warning says so once, so the numbers are not
/// mistaken for device throughput.
/// </para>
/// </remarks>
public static class PageCache
{
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    private static bool _warned;

    /// <summary>Whether <see cref="Evict"/> does anything on this platform.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Evicts a file's cached pages.</summary>
    /// <param name="path">The file to evict.</param>
    public static void Evict(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var handle = File.OpenHandle(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, NoBuffering);

            return;
        }

        if (_warned) return;

        _warned = true;

        Console.WriteLine(
            "// WARNING: cache eviction is only implemented on Windows. Read benchmarks " +
            "on this platform measure a cache-resident file, not the device.");
    }
}
