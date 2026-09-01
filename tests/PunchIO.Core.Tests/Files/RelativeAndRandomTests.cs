using System.Text;
using PunchIO.Files;
using Xunit;

namespace PunchIO.Core.Tests.Files;

public sealed class RandomAccessFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"punchio-random-{Guid.NewGuid():N}");

    public RandomAccessFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string NewPath() => Path.Combine(_directory, $"{Guid.NewGuid():N}.dat");

    private static byte[] Pattern(int length, int seed = 0)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++) b[i] = (byte)(i * 31 + 7 + seed);
        return b;
    }

    [Fact]
    public async Task WritesAndReadsAtArbitraryOffsets()
    {
        var path = NewPath();

        await using (var file = RecordFile.OpenRandomAccess(path, FileAccess.ReadWrite))
        {
            await file.WriteAsync(Pattern(100, 1), 0, Ct);
            await file.WriteAsync(Pattern(100, 2), 1000, Ct);
        }

        await using var reader = RecordFile.OpenRandomAccess(path, FileAccess.Read);
        var buffer = new byte[100];

        Assert.Equal(100, await reader.ReadAsync(buffer, 1000, Ct));
        Assert.Equal<byte[]>(Pattern(100, 2), buffer);

        Assert.Equal(100, await reader.ReadAsync(buffer, 0, Ct));
        Assert.Equal<byte[]>(Pattern(100, 1), buffer);
    }

    [Fact]
    public async Task ReadsAreUnalignedByDesign()
    {
        // A byte-offset API cannot promise sector alignment, so Auto must not
        // resolve to the unbuffered backend however local the volume is.
        var path = NewPath();
        await File.WriteAllBytesAsync(path, Pattern(4096), Ct);

        await using var file = RecordFile.OpenRandomAccess(path, FileAccess.Read);

        Assert.Equal(1, file.Alignment);

        var buffer = new byte[17];
        Assert.Equal(17, await file.ReadAsync(buffer, 13, Ct));
        Assert.Equal<byte[]>(Pattern(4096).AsSpan(13, 17).ToArray(), buffer);
    }

    [Fact]
    public async Task ReportsLengthAndReadsShortAtTheTail()
    {
        var path = NewPath();
        await File.WriteAllBytesAsync(path, Pattern(100), Ct);

        await using var file = RecordFile.OpenRandomAccess(path, FileAccess.Read);

        Assert.Equal(100, file.Length);
        Assert.Equal(40, await file.ReadAsync(new byte[500], 60, Ct));
        Assert.Equal(0, await file.ReadAsync(new byte[500], 100, Ct));
    }

    [Fact]
    public async Task TruncatesAndExtends()
    {
        var path = NewPath();

        await using var file = RecordFile.OpenRandomAccess(path, FileAccess.ReadWrite);
        await file.WriteAsync(Pattern(1000), 0, Ct);

        await file.SetLengthAsync(500, Ct);
        Assert.Equal(500, file.Length);

        await file.SetLengthAsync(2000, Ct);
        Assert.Equal(2000, file.Length);
    }

    [Fact]
    public async Task RejectsANegativeOffset()
    {
        var path = NewPath();
        await File.WriteAllBytesAsync(path, Pattern(16), Ct);

        await using var file = RecordFile.OpenRandomAccess(path, FileAccess.Read);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await file.ReadAsync(new byte[4], -1, Ct));
    }

    [Fact]
    public async Task RejectsUseAfterDisposal()
    {
        var path = NewPath();
        await File.WriteAllBytesAsync(path, Pattern(16), Ct);

        var file = RecordFile.OpenRandomAccess(path, FileAccess.Read);
        await file.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await file.ReadAsync(new byte[4], 0, Ct));
    }
}

public sealed class RelativeFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"punchio-relative-{Guid.NewGuid():N}");

    public RelativeFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string NewPath() => Path.Combine(_directory, $"{Guid.NewGuid():N}.dat");

    private static RelativeFileOptions Layout(int recordLength = 10, int headerLength = 1) =>
        new() { RecordLength = recordLength, SlotHeaderLength = headerLength };

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static string Text(byte[] b) => Encoding.ASCII.GetString(b);

    [Fact]
    public async Task WritesAndReadsByRecordNumber()
    {
        var path = NewPath();

        await using var file = RecordFile.OpenRelative(path, Layout(), FileAccess.ReadWrite);

        await file.WriteAsync(1, Ascii("first"), Ct);
        await file.WriteAsync(2, Ascii("second"), Ct);
        await file.WriteAsync(3, Ascii("third"), Ct);

        var buffer = new byte[10];

        await file.ReadAsync(2, buffer, Ct);
        Assert.Equal("second    ", Text(buffer));

        await file.ReadAsync(1, buffer, Ct);
        Assert.Equal("first     ", Text(buffer));
    }

    [Fact]
    public async Task RecordNumbersAreOneBasedAndMapToExactOffsets()
    {
        var path = NewPath();
        var layout = Layout(recordLength: 10, headerLength: 1);

        await using (var file = RecordFile.OpenRelative(path, layout, FileAccess.ReadWrite))
            await file.WriteAsync(1, Ascii("A"), Ct);

        // One slot, not two: record 1 lives at offset zero.
        Assert.Equal(layout.SlotSize, new FileInfo(path).Length);
    }

    [Fact]
    public async Task WritingBeyondTheEndLeavesTheGapAbsentRatherThanCorrupt()
    {
        // The gap is whatever the filesystem leaves behind, which is zero -- and
        // zero is not the present marker, so no explicit gap filling is needed.
        var path = NewPath();

        await using var file = RecordFile.OpenRelative(path, Layout(), FileAccess.ReadWrite);

        await file.WriteAsync(1, Ascii("one"), Ct);
        await file.WriteAsync(100, Ascii("hundred"), Ct);

        Assert.Equal(100, file.SlotCount);

        var buffer = new byte[10];

        Assert.True(await file.TryReadAsync(1, buffer, Ct));
        Assert.True(await file.TryReadAsync(100, buffer, Ct));

        for (long n = 2; n < 100; n++)
            Assert.False(await file.TryReadAsync(n, buffer, Ct), $"slot {n} should be absent");
    }

    [Fact]
    public async Task ReadingAnAbsentRecordReportsStatus23()
    {
        var path = NewPath();

        await using var file = RecordFile.OpenRelative(path, Layout(), FileAccess.ReadWrite);
        await file.WriteAsync(1, Ascii("one"), Ct);

        var ex = await Assert.ThrowsAsync<PunchIoException>(
            async () => await file.ReadAsync(5, new byte[10], Ct));

        Assert.Equal(FileStatus.RecordNotFound, ex.Status);
    }

    [Fact]
    public async Task DeletesARecordAndLeavesItsNeighboursAlone()
    {
        var path = NewPath();

        await using var file = RecordFile.OpenRelative(path, Layout(), FileAccess.ReadWrite);

        for (int i = 1; i <= 5; i++)
            await file.WriteAsync(i, Ascii($"rec{i}"), Ct);

        Assert.True(await file.DeleteAsync(3, Ct));
        Assert.False(await file.DeleteAsync(3, Ct));   // already gone

        var buffer = new byte[10];

        Assert.False(await file.TryReadAsync(3, buffer, Ct));
        Assert.True(await file.TryReadAsync(2, buffer, Ct));
        Assert.Equal("rec2      ", Text(buffer));
        Assert.True(await file.TryReadAsync(4, buffer, Ct));
        Assert.Equal("rec4      ", Text(buffer));
    }

    [Fact]
    public async Task RewritingAnAbsentRecordIsRefused()
    {
        var path = NewPath();

        await using var file = RecordFile.OpenRelative(path, Layout(), FileAccess.ReadWrite);
        await file.WriteAsync(1, Ascii("one"), Ct);

        var ex = await Assert.ThrowsAsync<PunchIoException>(
            async () => await file.RewriteAsync(9, Ascii("nope"), Ct));

        Assert.Equal(FileStatus.RecordNotFound, ex.Status);
    }

    [Fact]
    public async Task RewritingALiveRecordReplacesIt()
    {
        var path = NewPath();

        await using var file = RecordFile.OpenRelative(path, Layout(), FileAccess.ReadWrite);
        await file.WriteAsync(1, Ascii("before"), Ct);
        await file.RewriteAsync(1, Ascii("after"), Ct);

        var buffer = new byte[10];
        await file.ReadAsync(1, buffer, Ct);

        Assert.Equal("after     ", Text(buffer));
    }

    [Fact]
    public async Task RefusesARecordLongerThanTheSlot()
    {
        var path = NewPath();

        await using var file = RecordFile.OpenRelative(path, Layout(), FileAccess.ReadWrite);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await file.WriteAsync(1, Ascii("far too long for ten bytes"), Ct));
    }

    [Fact]
    public async Task TraversalSkipsAbsentSlotsAndReportsRecordNumbers()
    {
        var path = NewPath();

        await using var file = RecordFile.OpenRelative(path, Layout(), FileAccess.ReadWrite);

        for (int i = 1; i <= 20; i++)
            await file.WriteAsync(i, Ascii($"r{i}"), Ct);

        await file.DeleteAsync(5, Ct);
        await file.DeleteAsync(6, Ct);
        await file.DeleteAsync(20, Ct);

        var seen = new List<long>();

        await foreach (var record in file.ReadAllAsync(Ct))
            seen.Add(record.RecordNumber);

        Assert.Equal(Enumerable.Range(1, 19).Select(i => (long)i).Except([5L, 6L]), seen);
    }

    [Fact]
    public async Task TraversalReturnsRecordBodiesWithoutTheSlotHeader()
    {
        var path = NewPath();

        await using var file = RecordFile.OpenRelative(path, Layout(), FileAccess.ReadWrite);

        await file.WriteAsync(1, Ascii("alpha"), Ct);
        await file.WriteAsync(2, Ascii("beta"), Ct);

        var bodies = new List<string>();

        await foreach (var record in file.ReadAllAsync(Ct))
            bodies.Add(Text(record.Record.ToArray()));

        Assert.Equal(["alpha     ", "beta      "], bodies);
    }

    [Fact]
    public async Task TraversalOfALargeFileMatchesRandomReads()
    {
        // The traversal runs through the block pump while random reads go
        // straight to the device; the two must agree.
        var path = NewPath();
        var layout = Layout(recordLength: 40, headerLength: 1);

        await using var file = RecordFile.OpenRelative(path, layout, FileAccess.ReadWrite);

        const int count = 5_000;

        for (int i = 1; i <= count; i++)
            await file.WriteAsync(i, Ascii($"record-{i:D6}"), Ct);

        for (int i = 3; i <= count; i += 7)
            await file.DeleteAsync(i, Ct);

        var buffer = new byte[layout.RecordLength];
        long seen = 0;

        await foreach (var record in file.ReadAllAsync(Ct))
        {
            seen++;

            Assert.True(await file.TryReadAsync(record.RecordNumber, buffer, Ct));
            Assert.Equal<byte[]>(buffer, record.Record.ToArray());
        }

        long expectedDeleted = 0;
        for (int i = 3; i <= count; i += 7) expectedDeleted++;

        Assert.Equal(count - expectedDeleted, seen);
    }

    [Fact]
    public async Task WorksWithoutASlotHeaderButCannotDelete()
    {
        var path = NewPath();
        var layout = Layout(recordLength: 8, headerLength: 0);

        await using var file = RecordFile.OpenRelative(path, layout, FileAccess.ReadWrite);

        await file.WriteAsync(1, Ascii("AB"), Ct);
        await file.WriteAsync(2, Ascii("CD"), Ct);

        var buffer = new byte[8];
        await file.ReadAsync(2, buffer, Ct);
        Assert.Equal("CD      ", Text(buffer));

        await Assert.ThrowsAsync<NotSupportedException>(
            async () => await file.DeleteAsync(1, Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RejectsARecordNumberBelowOne(long recordNumber)
    {
        var path = NewPath();

        await using var file = RecordFile.OpenRelative(path, Layout(), FileAccess.ReadWrite);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await file.TryReadAsync(recordNumber, new byte[10], Ct));
    }

    [Fact]
    public void RejectsALayoutWhereTheMarkersCollide()
    {
        var layout = new RelativeFileOptions
        {
            RecordLength = 10,
            SlotHeaderLength = 1,
            PresentMarker = 0x01,
            DeletedMarker = 0x01,
        };

        Assert.Throws<ArgumentException>(layout.Validate);
    }

    [Fact]
    public void RejectsANonPositiveRecordLength()
    {
        Assert.Throws<ArgumentException>(new RelativeFileOptions { RecordLength = 0 }.Validate);
    }
}
