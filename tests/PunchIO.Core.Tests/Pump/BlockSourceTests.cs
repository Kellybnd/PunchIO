using PunchIO.Pump;
using Xunit;

namespace PunchIO.Core.Tests.Pump;

public class BlockSourceTests
{
    private const int BlockSize = 64;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Pattern(int length)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++) b[i] = (byte)(i * 31 + 7);
        return b;
    }

    private static async Task<List<byte[]>> DrainAsync(BlockSource source)
    {
        var blocks = new List<byte[]>();

        while (true)
        {
            var block = await source.NextBlockAsync(Ct);
            if (block.IsEmpty) break;

            blocks.Add(block.ToArray());
        }

        return blocks;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task DeliversEveryBlockInFileOrderAtAnyQueueDepth(int queueDepth)
    {
        // The device completes reads out of order on purpose. Delivery order is
        // the pump's job, so any slip shows up as corrupted content here.
        var content = Pattern(BlockSize * 20);
        var device = new FakeBlockDevice(content);
        await using var source = BlockSource.Create(device, queueDepth, BlockSize);

        var blocks = await DrainAsync(source);

        Assert.Equal(20, blocks.Count);
        Assert.Equal<byte[]>(content, blocks.SelectMany(b => b).ToArray());
    }

    [Fact]
    public async Task KeepsTheConfiguredNumberOfReadsInFlight()
    {
        // The lower bound is proved, not sampled: the barrier releases only once
        // four reads are waiting at it simultaneously, so a pump that serialised
        // its I/O would time out here rather than quietly pass.
        var device = new FakeBlockDevice(Pattern(BlockSize * 20)) { GateAt = 4 };
        await using var source = BlockSource.Create(device, queueDepth: 4, BlockSize);

        await DrainAsync(source);

        // The upper bound is safe to sample: a maximum can under-report when
        // completions interleave with submissions, but never over-report. Five
        // is queueDepth plus the block checked out to the caller.
        Assert.True(device.PeakOutstanding <= 5,
            $"expected at most 5 concurrent reads, saw {device.PeakOutstanding}");
    }

    [Fact]
    public async Task FillsABlockDespiteRepeatedShortReads()
    {
        // 7 bytes at a time against a 64-byte block: every block needs nine
        // fill-completion reads before it can be handed on.
        var content = Pattern(BlockSize * 5);
        var device = new FakeBlockDevice(content) { MaxReadLength = 7 };
        await using var source = BlockSource.Create(device, queueDepth: 2, BlockSize);

        var blocks = await DrainAsync(source);

        Assert.All(blocks, b => Assert.Equal(BlockSize, b.Length));
        Assert.Equal<byte[]>(content, blocks.SelectMany(b => b).ToArray());
    }

    [Fact]
    public async Task DeliversAShortFinalBlock()
    {
        var content = Pattern(BlockSize * 3 + 17);
        var device = new FakeBlockDevice(content);
        await using var source = BlockSource.Create(device, queueDepth: 4, BlockSize);

        var blocks = await DrainAsync(source);

        Assert.Equal(4, blocks.Count);
        Assert.Equal(17, blocks[^1].Length);
        Assert.Equal<byte[]>(content, blocks.SelectMany(b => b).ToArray());
    }

    [Fact]
    public async Task AnEmptyFileCompletesWithoutDeliveringAnything()
    {
        var device = new FakeBlockDevice();
        await using var source = BlockSource.Create(device, queueDepth: 4, BlockSize);

        Assert.True((await source.NextBlockAsync(Ct)).IsEmpty);
        Assert.True(source.IsCompleted);
    }

    [Fact]
    public async Task CompletionIsStickyOnceReached()
    {
        var device = new FakeBlockDevice(Pattern(BlockSize));
        await using var source = BlockSource.Create(device, queueDepth: 2, BlockSize);

        Assert.False((await source.NextBlockAsync(Ct)).IsEmpty);
        Assert.True((await source.NextBlockAsync(Ct)).IsEmpty);
        Assert.True((await source.NextBlockAsync(Ct)).IsEmpty);
    }

    [Fact]
    public async Task ReportsBlockOffsetOfTheBlockJustReturned()
    {
        var device = new FakeBlockDevice(Pattern(BlockSize * 4));
        await using var source = BlockSource.Create(device, queueDepth: 2, BlockSize);

        for (int i = 0; i < 4; i++)
        {
            await source.NextBlockAsync(Ct);
            Assert.Equal(i * BlockSize, source.BlockOffset);
        }
    }

    [Fact]
    public async Task AFaultSurfacesAtItsPlaceInFileOrder()
    {
        // The failing read is issued in the very first batch and may complete
        // before its predecessors. It must still be reported fourth.
        var content = Pattern(BlockSize * 10);
        var device = new FakeBlockDevice(content) { FaultAtReadOffset = BlockSize * 3 };
        await using var source = BlockSource.Create(device, queueDepth: 6, BlockSize);

        for (int i = 0; i < 3; i++)
        {
            var block = await source.NextBlockAsync(Ct);
            Assert.Equal<byte[]>(content.AsSpan(i * BlockSize, BlockSize).ToArray(), block.ToArray());
        }

        await Assert.ThrowsAsync<IOException>(async () => await source.NextBlockAsync(Ct));
    }

    [Fact]
    public async Task AFaultIsLatchedSoEveryLaterCallReportsIt()
    {
        // Deterministic error reporting: the same corrupt file must fail the same
        // way on every run, however the completions happened to race.
        var device = new FakeBlockDevice(Pattern(BlockSize * 10)) { FaultAtReadOffset = 0 };
        await using var source = BlockSource.Create(device, queueDepth: 4, BlockSize);

        var first = await Assert.ThrowsAsync<IOException>(async () => await source.NextBlockAsync(Ct));
        var second = await Assert.ThrowsAsync<IOException>(async () => await source.NextBlockAsync(Ct));

        Assert.Equal(first.Message, second.Message);
    }

    [Fact]
    public async Task DisposeDrainsEveryOutstandingReadBeforeReleasingBuffers()
    {
        // Releasing a slab while the kernel still owns one of its buffers is
        // memory corruption, so the drain is a correctness requirement.
        var device = new FakeBlockDevice(Pattern(BlockSize * 50));
        var source = BlockSource.Create(device, queueDepth: 8, BlockSize);

        await source.NextBlockAsync(Ct);
        await source.DisposeAsync();

        Assert.Equal(0, device.Outstanding);
        Assert.True(device.IsDisposed);
    }

    [Fact]
    public async Task DisposeDrainsEvenWhenReadsAreFailing()
    {
        var device = new FakeBlockDevice(Pattern(BlockSize * 50)) { FaultAtReadOffset = BlockSize * 2 };
        var source = BlockSource.Create(device, queueDepth: 8, BlockSize);

        await source.DisposeAsync();

        Assert.Equal(0, device.Outstanding);
    }

    [Fact]
    public async Task DisposeIsIdempotent()
    {
        var device = new FakeBlockDevice(Pattern(BlockSize * 4));
        var source = BlockSource.Create(device, queueDepth: 2, BlockSize);

        await source.DisposeAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task RejectsUseAfterDisposal()
    {
        var device = new FakeBlockDevice(Pattern(BlockSize * 4));
        var source = BlockSource.Create(device, queueDepth: 2, BlockSize);
        await source.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await source.NextBlockAsync(Ct));
    }

    [Fact]
    public void RejectsABlockSizeThatViolatesDeviceAlignment()
    {
        var device = new FakeBlockDevice(Pattern(4096)) { Alignment = 4096 };

        var ex = Assert.Throws<ArgumentException>(
            () => BlockSource.Create(device, queueDepth: 2, blockSize: 4095));

        Assert.Contains("alignment", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsANonPositiveQueueDepth(int queueDepth)
    {
        var device = new FakeBlockDevice(Pattern(64));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => BlockSource.Create(device, queueDepth, BlockSize));
    }

    [Fact]
    public async Task WorksWithAnAlignedDeviceAndAlignedBlocks()
    {
        var content = Pattern(4096 * 3);
        var device = new FakeBlockDevice(content) { Alignment = 4096 };
        await using var source = BlockSource.Create(device, queueDepth: 2, blockSize: 4096);

        var blocks = await DrainAsync(source);

        Assert.Equal(3, blocks.Count);
        Assert.Equal<byte[]>(content, blocks.SelectMany(b => b).ToArray());
    }
}
