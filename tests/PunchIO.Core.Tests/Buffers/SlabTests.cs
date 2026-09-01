using PunchIO.Buffers;
using Xunit;

namespace PunchIO.Core.Tests.Buffers;

public class SlabTests
{
    private const int Alignment = 4096;

    private static IBlockSlab Create(bool aligned, int blockCount, int blockSize) =>
        aligned
            ? new AlignedNativeSlab(blockCount, blockSize, Alignment)
            : new PinnedArraySlab(blockCount, blockSize);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExposesTheRequestedGeometry(bool aligned)
    {
        using var slab = Create(aligned, 5, 4096);

        Assert.Equal(5, slab.BlockCount);
        Assert.Equal(4096, slab.BlockSize);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlocksAreDistinctAndDoNotOverlap(bool aligned)
    {
        using var slab = Create(aligned, 4, 4096);

        for (int i = 0; i < slab.BlockCount; i++)
            slab.Block(i).Span.Fill((byte)(i + 1));

        for (int i = 0; i < slab.BlockCount; i++)
        {
            var span = slab.Block(i).Span;

            Assert.Equal(4096, span.Length);
            Assert.True(span.IndexOfAnyExcept((byte)(i + 1)) < 0,
                $"block {i} was overwritten by a neighbouring block");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsAnOutOfRangeBlockIndex(bool aligned)
    {
        using var slab = Create(aligned, 2, 4096);

        Assert.Throws<ArgumentOutOfRangeException>(() => slab.Block(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => slab.Block(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsNonPositiveGeometry(bool aligned)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(aligned, 0, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(aligned, 2, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlocksSurviveACompactingGarbageCollection(bool aligned)
    {
        // The kernel writes into these buffers while I/O is outstanding, so a
        // block the GC can relocate is a correctness bug, not a slow path.
        using var slab = Create(aligned, 3, 4096);
        slab.Block(1).Span.Fill(0xAB);

        nint before = AddressOf(slab.Block(1));
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        nint after = AddressOf(slab.Block(1));

        Assert.Equal(before, after);
        Assert.True(slab.Block(1).Span.IndexOfAnyExcept((byte)0xAB) < 0);
    }

    [Fact]
    public void NativeSlabAlignsEveryBlockToTheRequestedBoundary()
    {
        // Every block start must be aligned, not merely the slab start: an
        // unbuffered read out of block 1 is rejected otherwise.
        using var slab = new AlignedNativeSlab(4, 4096, Alignment);

        for (int i = 0; i < slab.BlockCount; i++)
            Assert.Equal(0, AddressOf(slab.Block(i)) % Alignment);
    }

    [Fact]
    public void NativeSlabRejectsABlockSizeThatIsNotAnAlignmentMultiple()
    {
        // Block 1 would start unaligned, so the geometry is refused up front.
        Assert.Throws<ArgumentException>(() => new AlignedNativeSlab(2, 4095, Alignment));
    }

    [Fact]
    public void NativeSlabToleratesRepeatedDisposal()
    {
        var slab = new AlignedNativeSlab(2, 4096, Alignment);

        slab.Dispose();
        slab.Dispose();   // a double free would corrupt the process heap
    }

    [Fact]
    public void NativeSlabRejectsUseAfterDisposal()
    {
        var slab = new AlignedNativeSlab(2, 4096, Alignment);
        slab.Dispose();

        Assert.Throws<ObjectDisposedException>(() => slab.Block(0));
    }

    private static nint AddressOf(Memory<byte> memory)
    {
        using var handle = memory.Pin();

        unsafe
        {
            return (nint)handle.Pointer;
        }
    }
}
