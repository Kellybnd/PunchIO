# PunchIO

[![CI](https://github.com/Kellybnd/PunchIO/actions/workflows/ci.yml/badge.svg)](https://github.com/Kellybnd/PunchIO/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/PunchIO.Core.svg)](https://www.nuget.org/packages/PunchIO.Core)
[![MIT](https://img.shields.io/badge/licence-MIT-blue.svg)](LICENSE.txt)
[![.NET](https://img.shields.io/badge/.NET-10.0%20%7C%208.0-512BD4.svg)](https://dotnet.microsoft.com/)

Fast asynchronous file I/O for record-structured data on .NET, plus an external
file handler (EXFH) for COBOL.

PunchIO reads and writes line sequential, fixed-length, and variable-length
record files, including the Micro Focus and Fujitsu variable-record layouts. It
keeps several I/O requests in flight, hands you one record at a time as a span
over a reused buffer, and does not allocate per record.

## Install

```
dotnet add package PunchIO.Core
```

`PunchIO.Core` has no package dependencies. Two optional packages build on it:

```
dotnet add package PunchIO.Configuration   # file layouts from IConfiguration
dotnet add package PunchIO.Cobol           # EXFH entry point for COBOL
```

Both `net10.0` and `net8.0` are supported. The assemblies are strong-named with
public key token `b8f132504d26042a`, for consumers whose own assemblies require
strong-named references.

## Getting started

Write some records, then read them back:

```csharp
using PunchIO;
using PunchIO.Framing;

await using (var writer = RecordFile.CreateVariableWrite(
    "customers.dat", VariableRecordDescriptor.Fujitsu))
{
    await writer.WriteAsync(Encoding.ASCII.GetBytes("ACME LIMITED"));
    await writer.WriteAsync(Encoding.ASCII.GetBytes("BOREALIS PLC"));
}

await using var reader = RecordFile.OpenVariableRead(
    "customers.dat", VariableRecordDescriptor.Fujitsu);

await foreach (var record in reader.ReadAllAsync())
{
    Console.WriteLine(Encoding.ASCII.GetString(record.Span));
}
```

Use `MoveNextAsync` directly when you want the record number or file offset:

```csharp
while (await reader.MoveNextAsync())
{
    Console.WriteLine($"{reader.RecordNumber} at byte {reader.RecordOffset}: " +
                      $"{reader.Current.Length} bytes");
}
```

### Record lifetime

A record is a slice of the buffer the reader is filling, and stays valid only
until the next `MoveNextAsync`. Copy anything you need to keep:

```csharp
var saved = reader.Current.ToArray();
```

Debug builds throw if you read a record after advancing, so this surfaces during
development rather than as corrupt output later.

## Formats

### Fixed length

```csharp
await using var writer = RecordFile.CreateFixedBlockWrite("ledger.dat", recordLength: 80);
await writer.WriteAsync(Encoding.ASCII.GetBytes("OPENING BALANCE"));  // padded to 80

await using var reader = RecordFile.OpenFixedBlockRead("ledger.dat", recordLength: 80);
```

Short records are space-padded on write; pass `padByte: 0` for binary data. A
record longer than the fixed length is rejected rather than truncated.

### Line sequential

```csharp
var options = new LineSequentialOptions
{
    Terminator = LineTerminator.CrLf,
    ExpandTabs = true,
    TabStopWidth = 8,
};

await using var reader = RecordFile.OpenLineSequentialRead("audit.log", options);
```

Trailing spaces are stripped on write, matching COBOL. Set `Syntax =
LineSyntax.Ebcdic` for EBCDIC files, which moves the terminator, space and tab
byte values rather than assuming ASCII.

### Variable length

`VariableRecordDescriptor.Fujitsu` and `VariableRecordDescriptor.MicroFocus()`
cover the two common layouts. Adjust any field with a `with` expression:

```csharp
var layout = VariableRecordDescriptor.Fujitsu with
{
    Endianness = Endianness.LittleEndian,
};

await using var reader = RecordFile.OpenVariableRead("customers.dat", layout);
```

| | Fujitsu | Micro Focus |
|---|---|---|
| File header | none | 128 bytes |
| Record header | 4-byte length | 2- or 4-byte control field |
| Suffix | 4-byte length | none |
| Byte order | little-endian | big-endian |
| Alignment | packed | 4 bytes |
| Largest record | 4 GiB | 4,095 bytes, or 65,535 with a wide control field |

A Fujitsu record on disk is a four-byte little-endian length, the record bytes,
then the same length again, so `n` bytes of data occupy `n + 8`:

```
[ length ][ ........ record ........ ][ length ]
  4 bytes            n bytes            4 bytes
```

The trailing length is checked against the leading one on every read, which
catches corruption at record granularity. Disable it with
`ValidateSuffix = false` if you would rather have the throughput.

A Micro Focus file opens with a 128-byte header, and every record carries a
control field packing a four-bit status over its length. Status `4` marks a user
data record; the file header is itself a record with status `3`, and deleted
records are `2`, so anything that is not user data is skipped on read. Records
are padded out to the next four-byte boundary:

```
[ 128-byte file header ][ 40 50 ][ ....... record ....... ][ pad ]
                         2 bytes          80 bytes          0-3
```

The control field is two bytes while the file's longest record is 4,095 bytes or
fewer, and four bytes above that. The width is part of the on-disk layout rather
than a detail the reader can infer per record, so declare it whenever the file
holds records longer than that:

```csharp
var layout = VariableRecordDescriptor.MicroFocus(maxRecordLength: 8000);
```

A reader has to be given the same maximum the writer used.

### Relative files

Records addressed by a one-based record number:

```csharp
var layout = new RelativeFileOptions { RecordLength = 80, SlotHeaderLength = 1 };

await using var file = RecordFile.OpenRelative("slots.dat", layout, FileAccess.ReadWrite);

await file.WriteAsync(1, Encoding.ASCII.GetBytes("FIRST"));
await file.WriteAsync(500, Encoding.ASCII.GetBytes("FIVE HUNDREDTH"));
await file.DeleteAsync(1);

await foreach (var record in file.ReadAllAsync())
{
    Console.WriteLine($"{record.RecordNumber}: {record.Record.Length} bytes");
}
```

Deletion needs a slot header, since without one an empty slot cannot be told
from a live one. `SlotHeaderLength` defaults to `0`; set it to `1` if you need
`DeleteAsync`.

Sequential traversal runs through the same pipeline as a fixed-length read, so
walking a relative file front to back is as fast as reading a flat one.

### Random access

```csharp
await using var file = RecordFile.OpenRandomAccess("data.bin", FileAccess.ReadWrite);

await file.WriteAsync(buffer, offset: 4096);
int read = await file.ReadAsync(buffer, offset: 8192);
```

## Tuning

```csharp
var options = new FileIoOptions
{
    QueueDepth = 8,          // I/O requests kept in flight
    BlockSize = 1024 * 1024, // bytes per request
    MaxRecordLength = 64 * 1024,
};

await using var reader = RecordFile.OpenVariableRead(path, layout, options);
```

On Windows, PunchIO opens local fixed volumes with `FILE_FLAG_NO_BUFFERING` to
bypass the cache manager on large sequential scans, and falls back to buffered
I/O for network and removable volumes. Override with
`Backend = BlockDevicePolicy.ForceManaged`.

## Configuration

`PunchIO.Configuration` reads file layouts from `IConfiguration`, so formats
live alongside connection strings rather than in code:

```json
{
  "PunchIO": {
    "Files": {
      "CustomerMaster": {
        "Format": "VariableBlock",
        "Preset": "Fujitsu",
        "Variable": { "Endianness": "LittleEndian" },
        "Io": { "QueueDepth": 8, "BlockSize": "1MiB" }
      }
    }
  }
}
```

```csharp
services.AddPunchIO(configuration);

var profiles = provider.GetRequiredService<IFileProfileProvider>();
await using var reader = profiles.Get("CustomerMaster").OpenRead(path);
```

`Preset` supplies a complete layout and any key you also set overrides it, so
adapting a known format is usually one line. Profiles are built and validated
during registration, and an invalid one names both the profile and the key:

```
File profile 'CustomerMaster', key 'Variable:Endianness':
  'Sideways' is not valid. Expected one of: BigEndian, LittleEndian.
```

## COBOL

`PunchIO.Cobol` exposes an EXFH entry point taking an operation code and a file
control description:

```csharp
using var exfh = new Exfh(profileResolver);

int rc = exfh.Execute(opcode, fcd, recordArea);
```

Micro Focus and Fujitsu operations are normalised into a single dispatcher.
Failures are reported as COBOL file statuses; nothing throws across the
boundary, since an exception reaching a native COBOL runtime would end the
process.

For native runtimes, `PunchIO.Exfh.Native` publishes as a self-contained shared
library exporting `EXTFH`, with no .NET runtime required on the target machine:

```bash
dotnet publish src/PunchIO.Exfh.Native -c Release -r win-x64 -f net10.0
```

It reads file layouts from `punchio.json` beside the library, or from the path
in `PUNCHIO_CONFIG`.

## Identifying an unknown layout

```
$ punchio-probe customers.dat

  Confidence  Records  Lengths   Empty  Layout
  ----------  -------  -------  ------  --------------------------------
  High            500       47       0  Fujitsu, little-endian
  Low              52        3       2  2-byte big-endian prefix, no suffix
```

The probe tries candidate layouts against the opening bytes of a file and
reports which ones frame it consistently. It is also available as an API:

```csharp
var results = await VariableFormatProbe.ProbeFileAsync("customers.dat");
var best = results[0];
```

## Compatibility

COBOL layouts vary between vendors, product versions and compiler directives.
Record layouts are runtime settings, so a variant needs no rebuild:

| Aspect | Set through |
|---|---|
| Framing, byte order, length basis, alignment | `VariableRecordDescriptor` |
| Slot headers and record markers | `RelativeFileOptions` |
| Terminators, tabs, null escaping, encoding | `LineSequentialOptions` |

The COBOL control-block layout and opcode values are compile-time constants in
`FcdLayout` and `ExfhOpcodes`. Each holds its values in one place with no logic
attached, so matching a runtime that differs means editing those declarations
and rebuilding `PunchIO.Cobol`.

When integrating with a specific runtime, run `punchio-probe` over
representative files and check the FCD layout and opcodes against that runtime's
header. Section 14 of the [design specification][spec] is the full reference.

## Performance

Measured on an i9-13900K, .NET 10, against a 312 MB file of 1.5 M records:

| | |
|---|---|
| Framing per record | 0.38 ns fixed length, 4–9 ns other formats |
| Allocation per record | 0.06–1.47 bytes |
| Line sequential vs `StreamReader.ReadLineAsync` | 12.2× faster, 2,770× less allocated |
| Variable records vs a raw `FileStream` loop | within 1.5×, while framing 1.5 M records |

The [performance report][perf] has the full results, methodology and test
conditions. Per-record allocation is asserted by the test suite.

## Building

```bash
dotnet build
dotnet test
dotnet run -c Release --project samples/PunchIO.Samples
```

Benchmarks:

```bash
dotnet run -c Release --project bench/PunchIO.Benchmarks -- --filter '*Framing*'
```

Publishing the native EXFH library needs `vswhere.exe` on `PATH`
(`C:\Program Files (x86)\Microsoft Visual Studio\Installer`). Without it the AOT
compilation succeeds and the linker step fails with an unhelpful error.

### Layout

```
src/PunchIO.Core           engine: formats, pipeline, devices, readers, writers
src/PunchIO.Configuration  file layouts from IConfiguration
src/PunchIO.Cobol          EXFH dispatch for managed COBOL
src/PunchIO.Exfh.Native    NativeAOT shared library exporting EXTFH
tools/PunchIO.Probe        punchio-probe
samples/                   runnable example of every format
bench/                     BenchmarkDotNet suite
docs/                      design specification and performance report
```

`IRecordFramer`, `IRecordEncoder`, `IBlockDevice`, `BlockSource` and `BlockSink`
are public, so a proprietary record format can use the same pipeline as the
built-in ones.

## Licence

MIT. See [LICENSE.txt](LICENSE.txt).

Micro Focus, Fujitsu and NetCOBOL are trademarks of their respective owners.
This project is not affiliated with or endorsed by any of them, and those names
appear only to describe the formats and interfaces it supports.

[spec]: docs/superpowers/specs/2026-08-31-punchio-file-io-library-design.md
[perf]: docs/superpowers/benchmarks/2026-09-01-baseline-results.md
