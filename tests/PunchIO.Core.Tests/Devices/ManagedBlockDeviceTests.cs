using PunchIO.Devices;
using Xunit;

namespace PunchIO.Core.Tests.Devices;

public sealed class ManagedBlockDeviceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"punchio-{Guid.NewGuid():N}.bin");

    public void Dispose() => File.Delete(_path);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Pattern(int length)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++) b[i] = (byte)(i * 31 + 7);
        return b;
    }

    [Fact]
    public async Task ReportsTheFileLength()
    {
        await File.WriteAllBytesAsync(_path, Pattern(1234), Ct);

        await using var device = ManagedBlockDevice.Open(_path, FileAccess.Read, FileShare.Read);

        Assert.Equal(1234, device.Length);
    }

    [Fact]
    public async Task ReadsAtAnArbitraryOffset()
    {
        var content = Pattern(4096);
        await File.WriteAllBytesAsync(_path, content, Ct);

        await using var device = ManagedBlockDevice.Open(_path, FileAccess.Read, FileShare.Read);
        var buffer = new byte[100];

        int read = await device.ReadAsync(buffer, 1000, Ct);

        Assert.Equal(100, read);
        Assert.Equal<byte[]>(content.AsSpan(1000, 100).ToArray(), buffer);
    }

    [Fact]
    public async Task ReadAtTheTailReturnsOnlyWhatRemains()
    {
        await File.WriteAllBytesAsync(_path, Pattern(100), Ct);

        await using var device = ManagedBlockDevice.Open(_path, FileAccess.Read, FileShare.Read);

        int read = await device.ReadAsync(new byte[4096], 60, Ct);

        Assert.Equal(40, read);
    }

    [Fact]
    public async Task ReadPastTheEndReturnsZero()
    {
        await File.WriteAllBytesAsync(_path, Pattern(100), Ct);

        await using var device = ManagedBlockDevice.Open(_path, FileAccess.Read, FileShare.Read);

        Assert.Equal(0, await device.ReadAsync(new byte[4096], 100, Ct));
    }

    [Fact]
    public async Task WritesAndReadsBackAtExplicitOffsets()
    {
        await using (var device = ManagedBlockDevice.Open(_path, FileAccess.ReadWrite, FileShare.None))
        {
            // Written out of order deliberately: offsets are explicit, so
            // completion order must not affect file content.
            await device.WriteAsync(Pattern(512), 512, Ct);
            await device.WriteAsync(Pattern(512), 0, Ct);
            await device.FlushAsync(toDisk: false, Ct);
        }

        var written = await File.ReadAllBytesAsync(_path, Ct);

        Assert.Equal(1024, written.Length);
        Assert.Equal<byte[]>(Pattern(512), written[..512]);
        Assert.Equal<byte[]>(Pattern(512), written[512..]);
    }

    [Fact]
    public async Task FlushToDiskSucceeds()
    {
        await using var device = ManagedBlockDevice.Open(_path, FileAccess.ReadWrite, FileShare.None);
        await device.WriteAsync(Pattern(64), 0, Ct);

        await device.FlushAsync(toDisk: true, Ct);
    }

    [Fact]
    public async Task SetLengthTruncatesTheFile()
    {
        await using (var device = ManagedBlockDevice.Open(_path, FileAccess.ReadWrite, FileShare.None))
        {
            await device.WriteAsync(Pattern(4096), 0, Ct);
            await device.SetLengthAsync(1000, Ct);

            Assert.Equal(1000, device.Length);
        }

        Assert.Equal(1000, new FileInfo(_path).Length);
    }

    [Fact]
    public async Task PortableBackendNeedsNoAlignmentOrTailPadding()
    {
        await File.WriteAllBytesAsync(_path, Pattern(16), Ct);

        await using var device = ManagedBlockDevice.Open(_path, FileAccess.Read, FileShare.Read);

        Assert.Equal(1, device.Alignment);
        Assert.False(device.RequiresTailPadding);
    }

    [Fact]
    public async Task AllocatesASlabWithTheRequestedGeometry()
    {
        await File.WriteAllBytesAsync(_path, Pattern(16), Ct);

        await using var device = ManagedBlockDevice.Open(_path, FileAccess.Read, FileShare.Read);
        using var slab = device.AllocateSlab(3, 4096);

        Assert.Equal(3, slab.BlockCount);
        Assert.Equal(4096, slab.BlockSize);
    }

    [Fact]
    public void MissingFileRaisesAnPunchIoExceptionWithStatus35()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.bin");

        var ex = Assert.Throws<PunchIoException>(
            () => ManagedBlockDevice.Open(missing, FileAccess.Read, FileShare.Read));

        Assert.Equal(FileStatus.FileNotFound, ex.Status);
    }
}
