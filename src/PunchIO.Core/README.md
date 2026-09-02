# PunchIO.Core

High-throughput asynchronous file I/O for record-structured files, with no
package dependencies.

```csharp
await using var reader = RecordFile.OpenVariableRead(
    path, VariableRecordDescriptor.Fujitsu);

await foreach (var record in reader.ReadAllAsync(cancellationToken))
    Process(record.Span);   // valid until the next iteration
```

## What it does

- **Four record formats**, read and write: line sequential, fixed-length block,
  and variable-length block in Micro Focus and Fujitsu layouts.
- **Configurable pipelining** — the number of I/O requests kept outstanding and
  the size of each are yours to set.
- **Relative files** addressed by record number, and byte-offset random access.
- **A Windows fast path** that bypasses the OS cache manager for large
  sequential scans, selected automatically on local volumes.

## Measured

On an i9-13900K with a Samsung 980 PRO NVMe drive, .NET 10:

| | Result |
|---|---|
| Sequential read, 4 GiB file, cold cache | **5.8 GB/s: 1.9x a `FileStream` record loop** |
| Line sequential read vs `StreamReader.ReadLineAsync` | **2.3x faster, 3,500x less allocated** |
| Sequential write, 4 GiB file, flushed to disk | **1.8 GB/s: 1.3x a `FileStream` record loop** |
| Framing cost per record | 0.38 ns fixed block, 4–9 ns for the other formats |
| Allocation per record | 0.06–1.47 bytes; asserted in the test suite |

Full results, including what has *not* been measured, are in
`docs/superpowers/benchmarks/`.

## The one contract to know

A record handed to you is valid **only until the next `MoveNextAsync`**. Records
are slices of the pump's reused buffers; copy anything you need to keep. Debug
builds throw if you touch a stale one.

## Extending it

`IRecordFramer`, `IRecordEncoder`, `IBlockDevice`, `BlockSource` and `BlockSink`
are public and documented. A proprietary record format plugs into the same pump
the built-in formats use.

## Licence

MIT. Micro Focus, Fujitsu and NetCOBOL are trademarks of their respective
owners; this project is not affiliated with or endorsed by any of them, and
those names appear only to describe the formats and interfaces it supports.
