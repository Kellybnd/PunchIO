using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PunchIO.Devices.Interop;

/// <summary>
/// Volume facts the unbuffered device needs: the sector size its requests must
/// align to, and whether the volume is local enough for unbuffered I/O to be
/// worth using.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsVolumeInfo
{
    private const uint DriveFixed = 3;

    /// <summary>
    /// A safe alignment when the volume cannot be interrogated. Over-aligning is
    /// always valid — the requirement is that requests be a multiple of the
    /// sector size, and 4096 is a multiple of every sector size in use.
    /// </summary>
    private const int FallbackSectorSize = 4096;

    private static readonly ConcurrentDictionary<string, int> SectorSizes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The sector size that unbuffered requests on this path must align to.</summary>
    /// <param name="path">Any path on the volume.</param>
    /// <returns>The sector size in bytes.</returns>
    public static int GetSectorSize(string path)
    {
        string root = RootOf(path);

        return SectorSizes.GetOrAdd(root, static r =>
        {
            if (GetDiskFreeSpace(r, out _, out uint bytesPerSector, out _, out _) && bytesPerSector > 0)
                return (int)bytesPerSector;

            return FallbackSectorSize;
        });
    }

    /// <summary>
    /// <see langword="true"/> when the path lives on a local fixed volume, where
    /// bypassing the cache manager is a win. Network shares are excluded: the
    /// redirector's caching is doing useful work there and the alignment
    /// constraints only add round trips.
    /// </summary>
    /// <param name="path">The path to classify.</param>
    /// <returns>Whether unbuffered I/O is appropriate for this path.</returns>
    public static bool IsLocalFixedVolume(string path)
    {
        string full = Path.GetFullPath(path);

        // A UNC path is remote regardless of what GetDriveType makes of its root.
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return false;

        return GetDriveType(RootOf(path)) == DriveFixed;
    }

    private static string RootOf(string path)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));

        // GetDiskFreeSpaceW and GetDriveTypeW both want a trailing separator.
        return string.IsNullOrEmpty(root)
            ? Path.GetFullPath(path)
            : root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpace(
        string rootPathName,
        out uint sectorsPerCluster,
        out uint bytesPerSector,
        out uint numberOfFreeClusters,
        out uint totalNumberOfClusters);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveType(string rootPathName);
}
