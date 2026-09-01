using System.Text;
using PunchIO;
using PunchIO.Cobol;
using PunchIO.Configuration;
using PunchIO.Files;
using PunchIO.Framing;
using PunchIO.Tools;
using Microsoft.Extensions.Configuration;

// Every sample writes into a scratch directory and cleans up after itself.
string workspace = Path.Combine(Path.GetTempPath(), "punchio-samples-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspace);

try
{
    await VariableRecordsAsync();
    await FixedLengthRecordsAsync();
    await LineSequentialAsync();
    await RelativeRecordsAsync();
    await ConfiguredProfileAsync();
    await CobolThroughExfhAsync();
    await IdentifyAnUnknownFormatAsync();
}
finally
{
    Directory.Delete(workspace, recursive: true);
}

string Path_(string name) => Path.Combine(workspace, name);

void Heading(string title)
{
    Console.WriteLine();
    Console.WriteLine(title);
    Console.WriteLine(new string('-', title.Length));
}

// ---------------------------------------------------------------------------

async Task VariableRecordsAsync()
{
    Heading("Variable-length records, Fujitsu layout");

    string path = Path_("customers.dat");

    await using (var writer = RecordFile.CreateVariableWrite(
        path, VariableRecordDescriptor.Fujitsu))
    {
        foreach (var name in new[] { "ACME LIMITED", "BOREALIS PLC", "CYGNUS GMBH" })
            await writer.WriteAsync(Encoding.ASCII.GetBytes(name));
    }

    await using var reader = RecordFile.OpenVariableRead(
        path, VariableRecordDescriptor.Fujitsu);

    await foreach (var record in reader.ReadAllAsync())
    {
        // The record is a slice of the pump's buffer and is valid only until the
        // next iteration. Copy anything you intend to keep.
        Console.WriteLine($"  {reader.RecordNumber,3}  {Encoding.ASCII.GetString(record.Span)}");
    }

    Console.WriteLine($"  {new FileInfo(path).Length} bytes on disk (each record costs 8 bytes of framing)");
}

async Task FixedLengthRecordsAsync()
{
    Heading("Fixed-length records");

    string path = Path_("ledger.dat");
    const int recordLength = 20;

    await using (var writer = RecordFile.CreateFixedBlockWrite(path, recordLength))
    {
        // Short records are padded out; a record that is too long is refused
        // rather than silently truncated.
        await writer.WriteAsync(Encoding.ASCII.GetBytes("OPENING BALANCE"));
        await writer.WriteAsync(Encoding.ASCII.GetBytes("INVOICE 4471"));
    }

    await using var reader = RecordFile.OpenFixedBlockRead(path, recordLength);

    await foreach (var record in reader.ReadAllAsync())
        Console.WriteLine($"  [{Encoding.ASCII.GetString(record.Span)}]");
}

async Task LineSequentialAsync()
{
    Heading("Line sequential, with tab expansion on read");

    string path = Path_("audit.log");

    await File.WriteAllTextAsync(path, "STARTED\tOK\nSTOPPED\tOK\n");

    var options = new LineSequentialOptions { ExpandTabs = true, TabStopWidth = 12 };

    await using var reader = RecordFile.OpenLineSequentialRead(path, options);

    await foreach (var record in reader.ReadAllAsync())
        Console.WriteLine($"  [{Encoding.ASCII.GetString(record.Span)}]");
}

async Task RelativeRecordsAsync()
{
    Heading("Relative file, addressed by record number");

    string path = Path_("slots.dat");
    var layout = new RelativeFileOptions { RecordLength = 12, SlotHeaderLength = 1 };

    await using var file = RecordFile.OpenRelative(path, layout, FileAccess.ReadWrite);

    await file.WriteAsync(1, Encoding.ASCII.GetBytes("FIRST"));
    await file.WriteAsync(2, Encoding.ASCII.GetBytes("SECOND"));

    // Writing far past the end leaves the gap absent rather than corrupt: the
    // filesystem fills it with zeros, and zero is not the present marker.
    await file.WriteAsync(9, Encoding.ASCII.GetBytes("NINTH"));
    await file.DeleteAsync(2);

    await foreach (var record in file.ReadAllAsync())
        Console.WriteLine($"  {record.RecordNumber,3}  {Encoding.ASCII.GetString(record.Record.Span)}");

    Console.WriteLine($"  {file.SlotCount} slots span the file; only the live ones are listed");
}

async Task ConfiguredProfileAsync()
{
    Heading("A file profile resolved from configuration");

    string configPath = Path_("punchio.json");

    await File.WriteAllTextAsync(configPath, """
        {
          "PunchIO": {
            "Files": {
              "CustomerMaster": {
                "Format": "VariableBlock",
                "Preset": "Fujitsu",
                "Io": { "QueueDepth": 8, "BlockSize": "1MiB" }
              }
            }
          }
        }
        """);

    var configuration = new ConfigurationBuilder().AddJsonFile(configPath).Build();
    var profiles = new FileProfileProvider(ServiceCollectionExtensions.Bind(configuration));

    var profile = profiles.Get("CustomerMaster");
    string path = Path_("configured.dat");

    await using (var writer = profile.CreateWrite(path))
        await writer.WriteAsync(Encoding.ASCII.GetBytes("WRITTEN THROUGH A PROFILE"));

    await using var reader = profile.OpenRead(path);
    await reader.MoveNextAsync();

    Console.WriteLine($"  format     {profile.Format}");
    Console.WriteLine($"  queueDepth {profile.Io.QueueDepth}, blockSize {profile.Io.BlockSize:N0}");
    Console.WriteLine($"  record     {Encoding.ASCII.GetString(reader.Current.Span)}");
}

async Task CobolThroughExfhAsync()
{
    Heading("The same file through the COBOL external file handler");

    string path = Path_("cobol.dat");

    var profile = new FileProfile("Sample", RecordFormat.VariableBlock, FileIoOptions.Default)
    {
        Variable = VariableRecordDescriptor.Fujitsu,
    };

    using var exfh = new Exfh(new SampleResolver(profile));

    var fcd = BuildFcd(path);
    var recordArea = new byte[32];

    Execute(ExfhOpcodes.OpenOutput, "OPEN OUTPUT");

    foreach (var text in new[] { "LEDGER ROW ONE", "LEDGER ROW TWO" })
    {
        recordArea.AsSpan().Fill(0x20);
        Encoding.ASCII.GetBytes(text).CopyTo(recordArea.AsSpan());
        new FcdView(fcd).CurrentRecordLength = text.Length;

        Execute(ExfhOpcodes.Write, $"WRITE  {text}");
    }

    Execute(ExfhOpcodes.Close, "CLOSE");
    Execute(ExfhOpcodes.OpenInput, "OPEN INPUT");

    while (exfh.Execute(Opcode(ExfhOpcodes.ReadNext), fcd, recordArea) == 0)
    {
        int length = new FcdView(fcd).CurrentRecordLength;
        Console.WriteLine($"  READ   {Encoding.ASCII.GetString(recordArea, 0, length)}");
    }

    Console.WriteLine($"  status {new FcdView(fcd).GetStatus()} at end of file");

    Execute(ExfhOpcodes.Close, "CLOSE");

    void Execute(ushort opcode, string label)
    {
        exfh.Execute(Opcode(opcode), fcd, recordArea);
        Console.WriteLine($"  {label,-22} status {new FcdView(fcd).GetStatus()}");
    }

    await Task.CompletedTask;
}

async Task IdentifyAnUnknownFormatAsync()
{
    Heading("Identifying the layout of an unfamiliar file");

    string path = Path_("unknown.dat");

    // Written little-endian, which is not the shipped Fujitsu default: the point
    // is that the probe works this out from the bytes rather than being told.
    var actual = VariableRecordDescriptor.Fujitsu with { Endianness = Endianness.LittleEndian };

    await using (var writer = RecordFile.CreateVariableWrite(path, actual))
    {
        for (int i = 0; i < 200; i++)
            await writer.WriteAsync(new byte[1 + (i * 11 % 90)]);
    }

    foreach (var result in (await VariableFormatProbe.ProbeFileAsync(path)).Take(3))
    {
        Console.WriteLine(
            $"  {result.Confidence,-6} {result.RecordsFramed,4} records  {result.Name}");
    }
}

static byte[] Opcode(ushort value) => [(byte)(value >> 8), (byte)value];

static byte[] BuildFcd(string fileName)
{
    var layout = FcdLayout.Fcd2;
    var fcd = new byte[256];

    fcd[layout.VersionOffset] = 0;
    fcd[layout.LengthOffset] = (byte)(fcd.Length >> 8);
    fcd[layout.LengthOffset + 1] = (byte)fcd.Length;

    var name = Encoding.ASCII.GetBytes(fileName);
    fcd[layout.NameLengthOffset] = (byte)(name.Length >> 8);
    fcd[layout.NameLengthOffset + 1] = (byte)name.Length;
    name.CopyTo(fcd.AsSpan(layout.NameOffset));

    return fcd;
}

/// <summary>Resolves every file to one profile, which is all a sample needs.</summary>
file sealed class SampleResolver(FileProfile profile) : IExfhProfileResolver
{
    public FileProfile? Resolve(string fileName) => profile;
}
