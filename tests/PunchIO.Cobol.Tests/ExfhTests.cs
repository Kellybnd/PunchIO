using System.Buffers.Binary;
using System.Text;
using PunchIO.Framing;
using Xunit;

namespace PunchIO.Cobol.Tests;

/// <summary>Resolves every file to one profile, which is all these tests need.</summary>
internal sealed class SingleProfileResolver(FileProfile profile) : IExfhProfileResolver
{
    public string? LastRequested { get; private set; }

    public bool ReturnNull { get; set; }

    public FileProfile? Resolve(string fileName)
    {
        LastRequested = fileName;
        return ReturnNull ? null : profile;
    }
}

public sealed class ExfhTests : IDisposable
{
    private const int FcdLength = 256;
    private const int RecordLength = 32;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"punchio-exfh-{Guid.NewGuid():N}");

    public ExfhTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string NewPath() => Path.Combine(_directory, $"{Guid.NewGuid():N}.dat");

    private static FileProfile VariableProfile() =>
        new("Test", RecordFormat.VariableBlock, FileIoOptions.Default)
        {
            Variable = VariableRecordDescriptor.Fujitsu,
        };

    private static FileProfile FixedProfile() =>
        new("Test", RecordFormat.FixedBlock, FileIoOptions.Default) { RecordLength = RecordLength };

    /// <summary>Builds a control block naming a file, using the shipped layout.</summary>
    private static byte[] Fcd(string fileName, long relativeKey = 0, int recordLength = 0)
    {
        var fcd = new byte[FcdLength];
        var layout = FcdLayout.Fcd2;

        fcd[layout.VersionOffset] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(fcd.AsSpan(layout.LengthOffset, 2), FcdLength);

        var name = Encoding.ASCII.GetBytes(fileName);
        BinaryPrimitives.WriteUInt16BigEndian(fcd.AsSpan(layout.NameLengthOffset, 2), (ushort)name.Length);
        name.CopyTo(fcd.AsSpan(layout.NameOffset));

        BinaryPrimitives.WriteInt64BigEndian(fcd.AsSpan(layout.RelativeKeyOffset, 8), relativeKey);
        BinaryPrimitives.WriteUInt32BigEndian(
            fcd.AsSpan(layout.CurrentRecordLengthOffset, 4), (uint)recordLength);

        return fcd;
    }

    private static byte[] Opcode(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    private static FileStatus StatusOf(byte[] fcd) => new FcdView(fcd).GetStatus();

    private static int HandleOf(byte[] fcd) => new FcdView(fcd).HandleId;

    // ---- the control block ------------------------------------------------

    [Fact]
    public void TheViewRoundTripsEveryFieldItWrites()
    {
        var fcd = Fcd("CUSTOMER.DAT", relativeKey: 42, recordLength: 80);
        var view = new FcdView(fcd);

        Assert.Equal("CUSTOMER.DAT", view.FileName);
        Assert.Equal(42, view.RelativeKey);
        Assert.Equal(80, view.CurrentRecordLength);

        view.HandleId = 7;
        view.SetStatus(FileStatus.EndOfFile);

        Assert.Equal(7, new FcdView(fcd).HandleId);
        Assert.Equal(FileStatus.EndOfFile, new FcdView(fcd).GetStatus());
    }

    [Fact]
    public void DetectsTheControlBlockVersion()
    {
        var fcd = Fcd("X");

        Assert.Equal(FcdLayout.Fcd2, FcdLayout.For(fcd));

        fcd[FcdLayout.Fcd2.VersionOffset] = 1;

        Assert.Equal(FcdLayout.Fcd3, FcdLayout.For(fcd));
    }

    [Theory]
    [InlineData(ExfhOpcodes.OpenInput, FileOperation.OpenInput)]
    [InlineData(ExfhOpcodes.OpenOutput, FileOperation.OpenOutput)]
    [InlineData(ExfhOpcodes.Close, FileOperation.Close)]
    [InlineData(ExfhOpcodes.ReadNext, FileOperation.ReadNext)]
    [InlineData(ExfhOpcodes.Write, FileOperation.Write)]
    [InlineData(0xFFFF, FileOperation.Unknown)]
    public void TranslatesOperationCodes(ushort opcode, FileOperation expected)
    {
        Assert.Equal(expected, ExfhOpcodes.ToOperation(opcode));
    }

    // ---- a complete COBOL session ------------------------------------------

    [Fact]
    public void WritesThenReadsAFileTheWayACobolProgramWould()
    {
        var path = NewPath();
        var resolver = new SingleProfileResolver(VariableProfile());

        var records = new[] { "FIRST", "SECOND", "THIRD" };

        using (var exfh = new Exfh(resolver))
        {
            var fcd = Fcd(path);
            var recordArea = new byte[RecordLength];

            Assert.Equal(0, exfh.Execute(Opcode(ExfhOpcodes.OpenOutput), fcd, recordArea));
            Assert.Equal(FileStatus.Ok, StatusOf(fcd));
            Assert.NotEqual(0, HandleOf(fcd));

            foreach (var record in records)
            {
                recordArea.AsSpan().Fill(0x20);
                Encoding.ASCII.GetBytes(record).CopyTo(recordArea.AsSpan());

                new FcdView(fcd).CurrentRecordLength = record.Length;

                Assert.Equal(0, exfh.Execute(Opcode(ExfhOpcodes.Write), fcd, recordArea));
                Assert.Equal(FileStatus.Ok, StatusOf(fcd));
            }

            Assert.Equal(0, exfh.Execute(Opcode(ExfhOpcodes.Close), fcd, recordArea));
            Assert.Equal(0, exfh.OpenFileCount);
        }

        using (var exfh = new Exfh(resolver))
        {
            var fcd = Fcd(path);
            var recordArea = new byte[RecordLength];
            var read = new List<string>();

            Assert.Equal(0, exfh.Execute(Opcode(ExfhOpcodes.OpenInput), fcd, recordArea));

            while (exfh.Execute(Opcode(ExfhOpcodes.ReadNext), fcd, recordArea) == 0)
            {
                int length = new FcdView(fcd).CurrentRecordLength;
                read.Add(Encoding.ASCII.GetString(recordArea, 0, length));
            }

            Assert.Equal(FileStatus.EndOfFile, StatusOf(fcd));
            Assert.Equal(records, read);

            exfh.Execute(Opcode(ExfhOpcodes.Close), fcd, recordArea);
        }
    }

    [Fact]
    public void ReadingPastTheEndReportsStatus10()
    {
        var path = NewPath();
        File.WriteAllBytes(path, []);

        using var exfh = new Exfh(new SingleProfileResolver(VariableProfile()));
        var fcd = Fcd(path);
        var recordArea = new byte[RecordLength];

        exfh.Execute(Opcode(ExfhOpcodes.OpenInput), fcd, recordArea);

        Assert.Equal(1, exfh.Execute(Opcode(ExfhOpcodes.ReadNext), fcd, recordArea));
        Assert.Equal(FileStatus.EndOfFile, StatusOf(fcd));
    }

    [Fact]
    public void TheRecordAreaIsBlankFilledBeyondTheRecord()
    {
        // COBOL record areas are blank-filled, not left holding the previous
        // record's tail.
        var path = NewPath();
        var resolver = new SingleProfileResolver(VariableProfile());

        using (var exfh = new Exfh(resolver))
        {
            var fcd = Fcd(path);
            var area = new byte[RecordLength];

            exfh.Execute(Opcode(ExfhOpcodes.OpenOutput), fcd, area);

            Encoding.ASCII.GetBytes("LONGRECORDVALUE").CopyTo(area.AsSpan());
            new FcdView(fcd).CurrentRecordLength = 15;
            exfh.Execute(Opcode(ExfhOpcodes.Write), fcd, area);

            Encoding.ASCII.GetBytes("SHORT").CopyTo(area.AsSpan());
            new FcdView(fcd).CurrentRecordLength = 5;
            exfh.Execute(Opcode(ExfhOpcodes.Write), fcd, area);

            exfh.Execute(Opcode(ExfhOpcodes.Close), fcd, area);
        }

        using (var exfh = new Exfh(resolver))
        {
            var fcd = Fcd(path);
            var area = new byte[RecordLength];

            exfh.Execute(Opcode(ExfhOpcodes.OpenInput), fcd, area);
            exfh.Execute(Opcode(ExfhOpcodes.ReadNext), fcd, area);
            exfh.Execute(Opcode(ExfhOpcodes.ReadNext), fcd, area);

            Assert.Equal("SHORT", Encoding.ASCII.GetString(area, 0, 5));
            Assert.All(area[5..], b => Assert.Equal(0x20, b));
        }
    }

    // ---- failures become statuses, never exceptions ------------------------

    [Fact]
    public void AnUnconfiguredFileReportsAStatusRatherThanThrowing()
    {
        var resolver = new SingleProfileResolver(VariableProfile()) { ReturnNull = true };

        using var exfh = new Exfh(resolver);
        var fcd = Fcd(NewPath());

        Assert.Equal(1, exfh.Execute(Opcode(ExfhOpcodes.OpenInput), fcd, new byte[RecordLength]));
        Assert.Equal(FileStatus.AttributeMismatch, StatusOf(fcd));
    }

    [Fact]
    public void AMissingFileReportsStatus35()
    {
        using var exfh = new Exfh(new SingleProfileResolver(VariableProfile()));
        var fcd = Fcd(Path.Combine(_directory, "absent.dat"));

        Assert.Equal(1, exfh.Execute(Opcode(ExfhOpcodes.OpenInput), fcd, new byte[RecordLength]));
        Assert.Equal(FileStatus.FileNotFound, StatusOf(fcd));
    }

    [Fact]
    public void AnUnknownOpcodeReportsAStatus()
    {
        using var exfh = new Exfh(new SingleProfileResolver(VariableProfile()));
        var fcd = Fcd(NewPath());

        Assert.Equal(1, exfh.Execute(Opcode(0xFFFF), fcd, new byte[RecordLength]));
        Assert.Equal(FileStatus.AttributeMismatch, StatusOf(fcd));
    }

    [Fact]
    public void OperatingOnAnUnopenedHandleReportsAStatus()
    {
        using var exfh = new Exfh(new SingleProfileResolver(VariableProfile()));
        var fcd = Fcd(NewPath());

        new FcdView(fcd).HandleId = 999;

        Assert.Equal(1, exfh.Execute(Opcode(ExfhOpcodes.ReadNext), fcd, new byte[RecordLength]));
        Assert.Equal(FileStatus.AttributeMismatch, StatusOf(fcd));
    }

    [Theory]
    [InlineData(0)]    // no control block at all
    [InlineData(2)]    // room for a status and nothing else
    [InlineData(20)]   // truncated part-way through
    public void AControlBlockTooShortToReadStillGetsAStatus(int length)
    {
        using var exfh = new Exfh(new SingleProfileResolver(VariableProfile()));
        var fcd = new byte[length];

        Assert.Equal(1, exfh.Execute(Opcode(ExfhOpcodes.OpenInput), fcd, new byte[RecordLength]));

        if (length >= 2) Assert.Equal(FileStatus.AttributeMismatch, StatusOf(fcd));
    }

    [Fact]
    public void AnEmptyOpcodeIsRefusedRatherThanRead()
    {
        using var exfh = new Exfh(new SingleProfileResolver(VariableProfile()));
        var fcd = Fcd(NewPath());

        Assert.Equal(1, exfh.Execute([], fcd, new byte[RecordLength]));
        Assert.Equal(FileStatus.AttributeMismatch, StatusOf(fcd));
    }

    [Fact]
    public void NoOperationEverThrows()
    {
        // The contract that matters most: this is the last managed frame before a
        // native COBOL runtime, where an escaping exception ends the process.
        using var exfh = new Exfh(new SingleProfileResolver(VariableProfile()));

        ushort[] opcodes =
        [
            ExfhOpcodes.OpenInput, ExfhOpcodes.OpenOutput, ExfhOpcodes.OpenIo,
            ExfhOpcodes.OpenExtend, ExfhOpcodes.Close, ExfhOpcodes.ReadNext,
            ExfhOpcodes.ReadRandom, ExfhOpcodes.Write, ExfhOpcodes.Rewrite,
            ExfhOpcodes.Delete, ExfhOpcodes.Start, ExfhOpcodes.Unlock,
            ExfhOpcodes.Commit, ExfhOpcodes.Rollback, 0x1234, 0x0000,
        ];

        foreach (var opcode in opcodes)
        {
            foreach (var fcd in new[] { new byte[0], new byte[8], Fcd("nonexistent-file.dat") })
            {
                var exception = Record.Exception(
                    () => exfh.Execute(Opcode(opcode), fcd, new byte[RecordLength]));

                Assert.Null(exception);
            }
        }
    }

    [Fact]
    public void TransactionOpcodesAreAcceptedAndIgnored()
    {
        // Reporting failure would be wrong: this library holds no locks and runs
        // no transactions, so there is nothing to fail.
        using var exfh = new Exfh(new SingleProfileResolver(VariableProfile()));
        var fcd = Fcd(NewPath());

        foreach (var opcode in new[] { ExfhOpcodes.Commit, ExfhOpcodes.Rollback, ExfhOpcodes.Unlock })
        {
            Assert.Equal(0, exfh.Execute(Opcode(opcode), fcd, new byte[RecordLength]));
            Assert.Equal(FileStatus.Ok, StatusOf(fcd));
        }
    }

    // ---- fixed-length records ----------------------------------------------

    [Fact]
    public void HandlesFixedLengthRecords()
    {
        var path = NewPath();
        var resolver = new SingleProfileResolver(FixedProfile());

        using (var exfh = new Exfh(resolver))
        {
            var fcd = Fcd(path);
            var area = new byte[RecordLength];

            exfh.Execute(Opcode(ExfhOpcodes.OpenOutput), fcd, area);

            area.AsSpan().Fill(0x20);
            Encoding.ASCII.GetBytes("ROW-ONE").CopyTo(area.AsSpan());
            new FcdView(fcd).CurrentRecordLength = RecordLength;
            exfh.Execute(Opcode(ExfhOpcodes.Write), fcd, area);

            exfh.Execute(Opcode(ExfhOpcodes.Close), fcd, area);
        }

        Assert.Equal(RecordLength, new FileInfo(path).Length);

        using (var exfh = new Exfh(resolver))
        {
            var fcd = Fcd(path);
            var area = new byte[RecordLength];

            exfh.Execute(Opcode(ExfhOpcodes.OpenInput), fcd, area);

            Assert.Equal(0, exfh.Execute(Opcode(ExfhOpcodes.ReadNext), fcd, area));
            Assert.Equal("ROW-ONE", Encoding.ASCII.GetString(area).TrimEnd());
        }
    }

    [Fact]
    public void ClosingReleasesTheHandle()
    {
        var path = NewPath();

        using var exfh = new Exfh(new SingleProfileResolver(VariableProfile()));
        var fcd = Fcd(path);
        var area = new byte[RecordLength];

        exfh.Execute(Opcode(ExfhOpcodes.OpenOutput), fcd, area);
        Assert.Equal(1, exfh.OpenFileCount);

        exfh.Execute(Opcode(ExfhOpcodes.Close), fcd, area);
        Assert.Equal(0, exfh.OpenFileCount);

        // Closing twice is a program error, not a crash.
        Assert.Equal(1, exfh.Execute(Opcode(ExfhOpcodes.Close), fcd, area));
    }

    [Fact]
    public void DisposingClosesEveryOpenFile()
    {
        var resolver = new SingleProfileResolver(VariableProfile());
        var exfh = new Exfh(resolver);

        for (int i = 0; i < 5; i++)
            exfh.Execute(Opcode(ExfhOpcodes.OpenOutput), Fcd(NewPath()), new byte[RecordLength]);

        Assert.Equal(5, exfh.OpenFileCount);

        exfh.Dispose();

        Assert.Equal(0, exfh.OpenFileCount);
    }

    [Fact]
    public void ManyRecordsSurviveTheSynchronousBridge()
    {
        // The readahead pump runs ahead of a synchronous caller just as it does
        // for an asynchronous one; this is the assertion that it actually works.
        var path = NewPath();
        var resolver = new SingleProfileResolver(VariableProfile());
        const int count = 20_000;

        using (var exfh = new Exfh(resolver))
        {
            var fcd = Fcd(path);
            var area = new byte[RecordLength];

            exfh.Execute(Opcode(ExfhOpcodes.OpenOutput), fcd, area);

            for (int i = 0; i < count; i++)
            {
                area.AsSpan().Fill(0x20);
                Encoding.ASCII.GetBytes($"REC{i:D8}").CopyTo(area.AsSpan());
                new FcdView(fcd).CurrentRecordLength = 11;

                Assert.Equal(0, exfh.Execute(Opcode(ExfhOpcodes.Write), fcd, area));
            }

            exfh.Execute(Opcode(ExfhOpcodes.Close), fcd, area);
        }

        using (var exfh = new Exfh(resolver))
        {
            var fcd = Fcd(path);
            var area = new byte[RecordLength];
            int read = 0;

            exfh.Execute(Opcode(ExfhOpcodes.OpenInput), fcd, area);

            while (exfh.Execute(Opcode(ExfhOpcodes.ReadNext), fcd, area) == 0)
            {
                Assert.Equal($"REC{read:D8}", Encoding.ASCII.GetString(area, 0, 11));
                read++;
            }

            Assert.Equal(count, read);
        }
    }
}
