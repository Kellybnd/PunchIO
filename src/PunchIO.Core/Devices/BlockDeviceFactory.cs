using PunchIO.Devices.Interop;

namespace PunchIO.Devices;

/// <summary>Which block device backend to use for a file.</summary>
public enum BlockDevicePolicy
{
    /// <summary>
    /// Choose per file: the unbuffered Windows backend on a local fixed volume,
    /// the portable backend everywhere else.
    /// </summary>
    Auto,

    /// <summary>
    /// Always use the unbuffered Windows backend. Fails rather than degrading
    /// silently where it is unavailable.
    /// </summary>
    ForceNative,

    /// <summary>Always use the portable backend.</summary>
    ForceManaged,
}

/// <summary>Opens the block device appropriate for a path and policy.</summary>
public static class BlockDeviceFactory
{
    /// <summary>Opens a file with the configured backend.</summary>
    /// <param name="path">The file to open.</param>
    /// <param name="access">The access required.</param>
    /// <param name="share">The sharing mode.</param>
    /// <param name="policy">Which backend to use.</param>
    /// <returns>An open device.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="PunchIoException">
    /// <see cref="BlockDevicePolicy.ForceNative"/> was requested where the
    /// unbuffered backend is unavailable, or the file could not be opened.
    /// </exception>
    public static IBlockDevice Open(
        string path,
        FileAccess access,
        FileShare share,
        BlockDevicePolicy policy = BlockDevicePolicy.Auto) =>
        Open(
            path,
            access == FileAccess.Read ? FileMode.Open : FileMode.OpenOrCreate,
            access,
            share,
            policy);

    /// <summary>Opens a file with the configured backend and an explicit creation mode.</summary>
    /// <param name="path">The file to open.</param>
    /// <param name="mode">How the file should be opened or created.</param>
    /// <param name="access">The access required.</param>
    /// <param name="share">The sharing mode.</param>
    /// <param name="policy">Which backend to use.</param>
    /// <returns>An open device.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="PunchIoException">
    /// <see cref="BlockDevicePolicy.ForceNative"/> was requested where the
    /// unbuffered backend is unavailable, or the file could not be opened.
    /// </exception>
    public static IBlockDevice Open(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        BlockDevicePolicy policy = BlockDevicePolicy.Auto)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (policy == BlockDevicePolicy.ForceManaged)
            return ManagedBlockDevice.Open(path, mode, access, share);

        // Written as a visible OperatingSystem.IsWindows() guard rather than
        // hidden behind a helper, so the platform-compatibility analyzer can see
        // that the Windows-only backend is unreachable elsewhere.
        if (OperatingSystem.IsWindows())
        {
            return policy == BlockDevicePolicy.ForceNative || WindowsVolumeInfo.IsLocalFixedVolume(path)
                ? WindowsBlockDevice.Open(path, mode, access, share)
                : ManagedBlockDevice.Open(path, mode, access, share);
        }

        if (policy == BlockDevicePolicy.ForceNative)
        {
            // A customer who asked for the fast path deserves to be told it is
            // not available, rather than left wondering why the benchmark did
            // not move.
            throw new PunchIoException(
                $"{nameof(BlockDevicePolicy)}.{nameof(BlockDevicePolicy.ForceNative)} requires " +
                "Windows; the unbuffered backend is not implemented for this platform.",
                FileStatus.AttributeMismatch);
        }

        return ManagedBlockDevice.Open(path, mode, access, share);
    }

    /// <summary>
    /// Whether <see cref="BlockDevicePolicy.Auto"/> would select the unbuffered
    /// backend for a path.
    /// </summary>
    /// <param name="path">The path to classify.</param>
    /// <returns><see langword="true"/> when the unbuffered backend applies.</returns>
    public static bool UseNativeFor(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!OperatingSystem.IsWindows()) return false;

        return WindowsVolumeInfo.IsLocalFixedVolume(path);
    }
}
