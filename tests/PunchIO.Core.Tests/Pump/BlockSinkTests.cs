using PunchIO.Pump;
using Xunit;

namespace PunchIO.Core.Tests.Pump;

public class BlockSinkTests
{
    private const int BlockSize = 64;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Pattern(int length, int seed = 0)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++) b[i] = (byte)(i * 31 + 7 + seed);
        return b;
    }

    [Fact]
    public async Task WritesBytesThroughToTheDevice()
    {
        var device = new FakeBlockDevice();
        var content = Pattern(BlockSize * 3);

        await using (var sink = BlockSink.Create(device, queueDepth: 2, BlockSize))
        {
            await sink.WriteAsync(content, Ct);
            await sink.CompleteAsync(Ct);
        }

        Assert.Equal<byte[]>(content, device.Content);
    }

    [Fact]
    public async Task WritesAPartialFinalBlockOnComplete()
    {
        var device = new FakeBlockDevice();
        var content = Pattern(BlockSize * 2 + 17);

        await using (var sink = BlockSink.Create(device, queueDepth: 4, BlockSize))
        {
            await sink.WriteAsync(content, Ct);
            await sink.CompleteAsync(Ct);
        }

        Assert.Equal(BlockSize * 2 + 17, device.Content.Length);
        Assert.Equal<byte[]>(content, device.Content);
    }

    [Fact]
    public async Task EveryBlockWriteLandsOnABlockSizeBoundary()
    {
        // The invariant an unbuffered device depends on. Flushing a partly filled
        // block mid-stream would leave the next offset unaligned.
        var device = new FakeBlockDevice();

        await using (var sink = BlockSink.Create(device, queueDepth: 3, BlockSize))
        {
            // Deliberately awkward record sizes that never align with the block.
            for (int i = 0; i < 40; i++)
                await sink.WriteAsync(Pattern(23, i), Ct);

            await sink.CompleteAsync(Ct);
        }

        // Every write except the final tail is a whole block at a block boundary.
        foreach (var (offset, length) in device.Writes.SkipLast(1))
        {
            Assert.Equal(0, offset % BlockSize);
            Assert.Equal(BlockSize, length);
        }
    }

    [Fact]
    public async Task SplitsARecordLargerThanOneBlockAcrossBlocks()
    {
        var device = new FakeBlockDevice();
        var huge = Pattern(BlockSize * 5 + 13);

        await using (var sink = BlockSink.Create(device, queueDepth: 2, BlockSize))
        {
            await sink.WriteAsync(huge, Ct);
            await sink.CompleteAsync(Ct);
        }

        Assert.Equal<byte[]>(huge, device.Content);
    }

    [Fact]
    public async Task ConcatenatesManySmallRecordsExactly()
    {
        var device = new FakeBlockDevice();
        var expected = new List<byte>();

        await using (var sink = BlockSink.Create(device, queueDepth: 4, BlockSize))
        {
            for (int i = 0; i < 200; i++)
            {
                var record = Pattern(1 + (i % 37), i);
                expected.AddRange(record);
                await sink.WriteAsync(record, Ct);
            }

            await sink.CompleteAsync(Ct);
        }

        Assert.Equal<byte[]>(expected.ToArray(), device.Content);
    }

    [Fact]
    public async Task KeepsTheConfiguredNumberOfWritesInFlight()
    {
        // Proved by barrier rather than sampled: the gate opens only once four
        // writes are in flight together, so a sink that serialised would time out.
        var device = new FakeBlockDevice { GateAt = 4 };

        await using (var sink = BlockSink.Create(device, queueDepth: 4, BlockSize))
        {
            await sink.WriteAsync(Pattern(BlockSize * 30), Ct);
            await sink.CompleteAsync(Ct);
        }

        Assert.True(device.PeakOutstanding <= 5,
            $"expected at most 5 concurrent writes, saw {device.PeakOutstanding}");
    }

    [Fact]
    public async Task ReportsLogicalLengthAsBytesAccepted()
    {
        var device = new FakeBlockDevice();
        await using var sink = BlockSink.Create(device, queueDepth: 2, BlockSize);

        await sink.WriteAsync(Pattern(100), Ct);
        Assert.Equal(100, sink.Length);

        await sink.WriteAsync(Pattern(50), Ct);
        Assert.Equal(150, sink.Length);

        await sink.CompleteAsync(Ct);
        Assert.Equal(150, sink.Length);
    }

    [Fact]
    public async Task PadsTheTailAndTruncatesOnADeviceThatRequiresIt()
    {
        // The most commonly botched part of unbuffered I/O: the final write must
        // be a whole sector, then the file is truncated back to its true length.
        var device = new FakeBlockDevice
        {
            Alignment = 512,
            RequiresTailPadding = true,
        };
        var content = Pattern(512 * 2 + 100);

        await using (var sink = BlockSink.Create(device, queueDepth: 2, blockSize: 512 * 4))
        {
            await sink.WriteAsync(content, Ct);
            await sink.CompleteAsync(Ct);
        }

        // The tail write was rounded up to a sector...
        var tail = device.Writes[^1];
        Assert.Equal(0, tail.Length % 512);

        // ...and the file was then truncated to the logical length.
        Assert.Equal(content.Length, Assert.Single(device.SetLengths));
        Assert.Equal<byte[]>(content, device.Content);
    }

    [Fact]
    public async Task DoesNotTruncateWhenTheTailAlreadyFillsASector()
    {
        var device = new FakeBlockDevice
        {
            Alignment = 512,
            RequiresTailPadding = true,
        };

        await using (var sink = BlockSink.Create(device, queueDepth: 2, blockSize: 512 * 4))
        {
            await sink.WriteAsync(Pattern(512 * 3), Ct);
            await sink.CompleteAsync(Ct);
        }

        Assert.Empty(device.SetLengths);
    }

    [Fact]
    public async Task DisposeCompletesTheSinkSoTheTailIsNotLost()
    {
        var device = new FakeBlockDevice();
        var content = Pattern(BlockSize + 5);

        await using (var sink = BlockSink.Create(device, queueDepth: 2, BlockSize))
        {
            await sink.WriteAsync(content, Ct);
            // No explicit CompleteAsync: disposal must not silently drop the tail.
        }

        Assert.Equal<byte[]>(content, device.Content);
    }

    [Fact]
    public async Task DisposeDrainsOutstandingWritesAndReleasesTheDevice()
    {
        var device = new FakeBlockDevice();
        var sink = BlockSink.Create(device, queueDepth: 8, BlockSize);

        await sink.WriteAsync(Pattern(BlockSize * 40), Ct);
        await sink.DisposeAsync();

        Assert.Equal(0, device.Outstanding);
        Assert.True(device.IsDisposed);
    }

    [Fact]
    public async Task DisposeDrainsEvenWhenWritesAreFailing()
    {
        var device = new FakeBlockDevice { FaultAtWriteOffset = BlockSize * 2 };
        var sink = BlockSink.Create(device, queueDepth: 4, BlockSize);

        try
        {
            await sink.WriteAsync(Pattern(BlockSize * 30), Ct);
            await sink.CompleteAsync(Ct);
        }
        catch (IOException)
        {
            // Expected: the injected fault surfaces somewhere in here.
        }

        await sink.DisposeAsync();

        Assert.Equal(0, device.Outstanding);
    }

    [Fact]
    public async Task AFaultIsLatchedSoLaterCallsReportIt()
    {
        var device = new FakeBlockDevice { FaultAtWriteOffset = 0 };
        await using var sink = BlockSink.Create(device, queueDepth: 2, BlockSize);

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await sink.WriteAsync(Pattern(BlockSize * 10), Ct);
            await sink.CompleteAsync(Ct);
        });

        await Assert.ThrowsAsync<IOException>(async () => await sink.WriteAsync(Pattern(8), Ct));
    }

    [Fact]
    public async Task RejectsWritesAfterCompletion()
    {
        var device = new FakeBlockDevice();
        await using var sink = BlockSink.Create(device, queueDepth: 2, BlockSize);

        await sink.WriteAsync(Pattern(10), Ct);
        await sink.CompleteAsync(Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sink.WriteAsync(Pattern(10), Ct));
    }

    [Fact]
    public async Task CompleteIsIdempotent()
    {
        var device = new FakeBlockDevice();
        await using var sink = BlockSink.Create(device, queueDepth: 2, BlockSize);

        await sink.WriteAsync(Pattern(10), Ct);
        await sink.CompleteAsync(Ct);
        await sink.CompleteAsync(Ct);

        Assert.Equal(10, device.Content.Length);
    }

    [Fact]
    public async Task RejectsUseAfterDisposal()
    {
        var device = new FakeBlockDevice();
        var sink = BlockSink.Create(device, queueDepth: 2, BlockSize);
        await sink.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await sink.WriteAsync(Pattern(10), Ct));
    }

    [Fact]
    public void RejectsABlockSizeThatViolatesDeviceAlignment()
    {
        var device = new FakeBlockDevice { Alignment = 4096 };

        Assert.Throws<ArgumentException>(
            () => BlockSink.Create(device, queueDepth: 2, blockSize: 4095));
    }

    [Fact]
    public async Task RoundTripsThroughBlockSource()
    {
        // The two halves of the pump agree: what the sink writes, the source reads.
        var expected = new List<byte>();
        var device = new FakeBlockDevice();

        await using (var sink = BlockSink.Create(device, queueDepth: 3, BlockSize))
        {
            for (int i = 0; i < 150; i++)
            {
                var record = Pattern(1 + (i % 51), i);
                expected.AddRange(record);
                await sink.WriteAsync(record, Ct);
            }

            await sink.CompleteAsync(Ct);
        }

        var readBack = new FakeBlockDevice(device.Content);
        var actual = new List<byte>();

        await using (var source = BlockSource.Create(readBack, queueDepth: 3, BlockSize))
        {
            while (true)
            {
                var block = await source.NextBlockAsync(Ct);
                if (block.IsEmpty) break;

                actual.AddRange(block.ToArray());
            }
        }

        Assert.Equal<byte[]>(expected.ToArray(), actual.ToArray());
    }
}
