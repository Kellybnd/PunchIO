using System.Runtime.Versioning;
using PunchIO.Devices;
using Xunit;

namespace PunchIO.Core.Tests.Devices;

/// <summary>
/// The unbuffered backend against a real local volume. Every alignment rule
/// asserted here was measured against the operating system, not assumed.
/// </summary>
/// <remarks>
/// Annotated for the platform analyzer; each test additionally skips at runtime
/// when the volume is not a local fixed one, so the suite still runs elsewhere.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsBlockDeviceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"punchio-unbuffered-{Guid.NewGuid():N}");

    public WindowsBlockDeviceTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string NewPath() => Path.Combine(_directory, $"{Guid.NewGuid():N}.dat");

    private void RequireLocalWindowsVolume()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The unbuffered backend is Windows-only.");
        Assert.SkipUnless(
            BlockDeviceFactory.UseNativeFor(_directory),
            "The temp directory is not on a local fixed volume.");
    }

    private static byte[] Pattern(int length)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++) b[i] = (byte)(i * 31 + 7);
        return b;
    }

    [Fact]
    public void ReportsASectorAlignmentAndRequiresTailPadding()
    {
        RequireLocalWindowsVolume();

        var path = NewPath();
        File.WriteAllBytes(path, Pattern(16));

        using var device = WindowsBlockDevice.Open(path, FileAccess.Read, FileShare.Read);

        Assert.True(device.Alignment >= 512, $"implausible sector size {device.Alignment}");
        Assert.Equal(0, device.Alignment & (device.Alignment - 1));   // a power of two
        Assert.True(device.RequiresTailPadding);
    }

    [Fact]
    public void AllocatesSlabsWhoseBlocksAreSectorAligned()
    {
        RequireLocalWindowsVolume();

        var path = NewPath();
        File.WriteAllBytes(path, Pattern(16));

        using var device = WindowsBlockDevice.Open(path, FileAccess.Read, FileShare.Read);
        using var slab = device.AllocateSlab(3, device.Alignment * 2);

        for (int i = 0; i < slab.BlockCount; i++)
        {
            using var handle = slab.Block(i).Pin();

            unsafe
            {
                Assert.Equal(0, (nint)handle.Pointer % device.Alignment);
            }
        }
    }

    [Theory]
    [InlineData(1)]        // a single byte: the whole file is one partial sector
    [InlineData(511)]
    [InlineData(4095)]
    [InlineData(4096)]     // exactly one sector on a 4K volume
    [InlineData(4097)]
    [InlineData(12305)]    // three sectors plus a remainder
    public async Task ReadsAFileOfAnyLengthWithoutExposingSectorPadding(int fileLength)
    {
        // The tail rule. A file's length is rarely a sector multiple, so the
        // final read must round its request up past end of file and then clamp
        // the reported count back to the truth.
        RequireLocalWindowsVolume();

        var path = NewPath();
        var content = Pattern(fileLength);
        await File.WriteAllBytesAsync(path, content, Ct);

        await using var device = WindowsBlockDevice.Open(path, FileAccess.Read, FileShare.Read);
        using var slab = device.AllocateSlab(1, RoundUp(fileLength + 1, device.Alignment));

        int read = await device.ReadAsync(slab.Block(0), 0, Ct);

        Assert.Equal(fileLength, read);
        Assert.Equal<byte[]>(content, slab.Block(0)[..read].ToArray());
    }

    [Fact]
    public async Task ReadPastTheEndReturnsZero()
    {
        RequireLocalWindowsVolume();

        var path = NewPath();
        await File.WriteAllBytesAsync(path, Pattern(100), Ct);

        await using var device = WindowsBlockDevice.Open(path, FileAccess.Read, FileShare.Read);
        using var slab = device.AllocateSlab(1, device.Alignment);

        Assert.Equal(0, await device.ReadAsync(slab.Block(0), RoundUp(100, device.Alignment), Ct));
    }

    [Fact]
    public async Task RejectsAnUnalignedOffsetWithAnExplanation()
    {
        RequireLocalWindowsVolume();

        var path = NewPath();
        await File.WriteAllBytesAsync(path, Pattern(8192), Ct);

        await using var device = WindowsBlockDevice.Open(path, FileAccess.Read, FileShare.Read);
        using var slab = device.AllocateSlab(1, device.Alignment);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            async () => await device.ReadAsync(slab.Block(0), 17, Ct));

        Assert.Contains("sector size", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsAnUnalignedLengthWithAnExplanation()
    {
        RequireLocalWindowsVolume();

        var path = NewPath();
        await File.WriteAllBytesAsync(path, Pattern(8192), Ct);

        await using var device = WindowsBlockDevice.Open(path, FileAccess.Read, FileShare.Read);
        using var slab = device.AllocateSlab(1, device.Alignment);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await device.ReadAsync(slab.Block(0)[..17], 0, Ct));
    }

    [Fact]
    public async Task WritesAndReadsBackThroughAlignedRequests()
    {
        RequireLocalWindowsVolume();

        var path = NewPath();

        await using (var device = WindowsBlockDevice.Open(path, FileAccess.ReadWrite, FileShare.None))
        {
            using var slab = device.AllocateSlab(1, device.Alignment * 2);
            Pattern(device.Alignment * 2).CopyTo(slab.Block(0).Span);

            await device.WriteAsync(slab.Block(0), 0, Ct);
            await device.FlushAsync(toDisk: true, Ct);
        }

        var written = await File.ReadAllBytesAsync(path, Ct);

        Assert.Equal<byte[]>(Pattern(written.Length), written);
    }

    [Fact]
    public async Task TruncatesAPaddedTailBackToItsLogicalLength()
    {
        // Unbuffered writes cannot be a partial sector, so a short tail is padded
        // and the file is truncated afterwards. This is the step most commonly
        // left out, and it leaves trailing zeros in the file when it is.
        RequireLocalWindowsVolume();

        var path = NewPath();

        await using (var device = WindowsBlockDevice.Open(path, FileAccess.ReadWrite, FileShare.None))
        {
            using var slab = device.AllocateSlab(1, device.Alignment);
            slab.Block(0).Span.Clear();
            Pattern(100).CopyTo(slab.Block(0).Span);

            await device.WriteAsync(slab.Block(0), 0, Ct);
            await device.SetLengthAsync(100, Ct);

            Assert.Equal(100, device.Length);
        }

        Assert.Equal(100, new FileInfo(path).Length);
        Assert.Equal<byte[]>(Pattern(100), await File.ReadAllBytesAsync(path, Ct));
    }

    [Fact]
    public void MissingFileRaisesAnPunchIoExceptionWithStatus35()
    {
        RequireLocalWindowsVolume();

        var ex = Assert.Throws<PunchIoException>(
            () => WindowsBlockDevice.Open(NewPath(), FileAccess.Read, FileShare.Read));

        Assert.Equal(FileStatus.FileNotFound, ex.Status);
    }

    private static int RoundUp(int value, int alignment) =>
        (value + alignment - 1) / alignment * alignment;
}

public sealed class BlockDeviceFactoryTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"punchio-factory-{Guid.NewGuid():N}");

    public BlockDeviceFactoryTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string NewFile()
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.dat");
        File.WriteAllBytes(path, new byte[64]);
        return path;
    }

    [Fact]
    public void ForceManagedAlwaysYieldsThePortableBackend()
    {
        using var device = BlockDeviceFactory.Open(
            NewFile(), FileAccess.Read, FileShare.Read, BlockDevicePolicy.ForceManaged);

        Assert.IsType<ManagedBlockDevice>(device);
        Assert.Equal(1, device.Alignment);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AutoPicksTheUnbufferedBackendOnALocalFixedVolume()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The unbuffered backend is Windows-only.");
        Assert.SkipUnless(
            BlockDeviceFactory.UseNativeFor(_directory),
            "The temp directory is not on a local fixed volume.");

        using var device = BlockDeviceFactory.Open(
            NewFile(), FileAccess.Read, FileShare.Read, BlockDevicePolicy.Auto);

        Assert.IsType<WindowsBlockDevice>(device);
        Assert.True(device.Alignment > 1);
    }

    [Fact]
    public void AutoNeverPicksTheUnbufferedBackendOffWindows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "This asserts the non-Windows path.");

        using var device = BlockDeviceFactory.Open(
            NewFile(), FileAccess.Read, FileShare.Read, BlockDevicePolicy.Auto);

        Assert.IsType<ManagedBlockDevice>(device);
    }

    [Fact]
    public void ForceNativeFailsLoudlyOffWindowsRatherThanDegrading()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "This asserts the non-Windows path.");

        var ex = Assert.Throws<PunchIoException>(() => BlockDeviceFactory.Open(
            NewFile(), FileAccess.Read, FileShare.Read, BlockDevicePolicy.ForceNative));

        Assert.Equal(FileStatus.AttributeMismatch, ex.Status);
    }

    [Fact]
    public void UncPathsAreNeverTreatedAsLocal()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "UNC classification is a Windows concern.");

        Assert.False(BlockDeviceFactory.UseNativeFor(@"\\server\share\file.dat"));
    }
}
