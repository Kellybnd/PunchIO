using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PunchIO.Devices.Interop;

/// <summary>
/// The two file operations with no portable managed equivalent reachable from a
/// bare handle: forcing data to stable media, and setting a file's length.
/// </summary>
/// <remarks>
/// Uses source-generated interop so the library stays NativeAOT-safe.
/// </remarks>
internal static partial class NativeFileOps
{
    private const int FileEndOfFileInfo = 6;

    /// <summary>Forces the file's data to stable media.</summary>
    /// <param name="handle">An open file handle with write access.</param>
    /// <exception cref="PunchIoException">The platform call failed.</exception>
    public static void FlushToDisk(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!FlushFileBuffers(handle))
                throw Failure("Failed to flush file buffers to disk.");
        }
        else
        {
            if (Fsync((int)handle.DangerousGetHandle()) != 0)
                throw Failure("Failed to fsync the file.");
        }
    }

    /// <summary>Sets the file's logical length.</summary>
    /// <param name="handle">An open file handle with write access.</param>
    /// <param name="length">The new length in bytes.</param>
    /// <exception cref="PunchIoException">The platform call failed.</exception>
    public static void SetLength(SafeFileHandle handle, long length)
    {
        if (OperatingSystem.IsWindows())
        {
            // FileEndOfFileInfo takes an explicit offset, unlike SetEndOfFile,
            // which truncates at the handle's file pointer -- a meaningless
            // notion on an overlapped handle.
            long endOfFile = length;

            if (!SetFileInformationByHandle(handle, FileEndOfFileInfo, ref endOfFile, sizeof(long)))
                throw Failure($"Failed to set the file length to {length} bytes.");
        }
        else
        {
            if (Ftruncate((int)handle.DangerousGetHandle(), length) != 0)
                throw Failure($"Failed to truncate the file to {length} bytes.");
        }
    }

    private static PunchIoException Failure(string message) =>
        new(message,
            FileStatus.PermanentError,
            Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeFileHandle handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(
        SafeFileHandle handle, int fileInformationClass, ref long fileInformation, uint bufferSize);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fileDescriptor);

    [LibraryImport("libc", EntryPoint = "ftruncate", SetLastError = true)]
    private static partial int Ftruncate(int fileDescriptor, long length);
}
