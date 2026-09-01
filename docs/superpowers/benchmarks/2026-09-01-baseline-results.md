# PunchIO Performance Report

**Version:** 1.0.0
**Date:** 2026-09-01

## Test environment

| | |
|---|---|
| CPU | Intel Core i9-13900K, 24 physical / 32 logical cores |
| Memory | 127.7 GB |
| OS | Windows 11 |
| Runtime | .NET 10.0.11, Release |
| Storage | Local fixed NTFS volume, 512-byte logical sector |
| Harness | BenchmarkDotNet, short job (3 warm-up, 3 iterations), in-process toolchain |
| Data set | 1.5 M records of 200 bytes — a 312 MB file, resident in the file system cache |

---

## 1. Framing throughput

Per-record framing cost with no I/O, isolating the record-parsing hot path.

| Format | 80-byte records | 400-byte records | Allocated |
|---|---:|---:|---:|
| Fixed block | **0.38 ns** | 0.38 ns | 0 B |
| Variable, Micro Focus | 4.26 ns | 4.46 ns | 0 B |
| Variable, Fujitsu | 4.14 ns | 8.04 ns | 0 B |
| Line sequential | 5.31 ns | 8.56 ns | 0 B |

**Target: under 20 ns per record. Met on every format with a wide margin.**

Fixed-block framing is one comparison and one addition, so it is flat across
record size and effectively free. Line-sequential cost scales with record length
because terminator scanning is a vectorised search across the record body.

Fujitsu costs roughly twice Micro Focus at 400 bytes because it validates the
trailing length field against the leading one, which touches a second cache
line. That check detects record-level corruption on every read. Callers who
prefer the nanoseconds can disable it:

```csharp
VariableRecordDescriptor.Fujitsu with { ValidateSuffix = false }
```

---

## 2. Allocation

Measured with `GC.GetTotalAllocatedBytes(precise: true)` across 50,000 records
after a warm-up pass. Figures are identical on `net8.0` and `net10.0`.

| Path | Total | Per record |
|---|---:|---:|
| Write, variable Fujitsu | 4,408 B | **0.09 B** |
| Read, variable Fujitsu | 73,272 B | **1.47 B** |
| Read, fixed block | 3,104 B | **0.06 B** |

**Target: no per-record allocation. Met.** What remains is per-file setup — the
buffer slab and, for variable records, the stitch buffer that joins records
spanning a block boundary.

These thresholds are asserted by the test suite (`AllocationTests`), so a
regression fails the build rather than waiting for someone to re-run a
benchmark.

Fixed-block reading allocates twenty times less than variable because
`ResolveBlockSize` rounds the block size to a multiple of the record length.
Records then never straddle a boundary and no stitch buffer is required.

---

## 3. Queue depth and block size

Sequential read of the 312 MB data set, portable backend, block size pinned.
Times are the mean of three iterations.

| Queue depth | 64 KiB | 256 KiB | 1 MiB | 4 MiB |
|---|---:|---:|---:|---:|
| 1 | 82.8 ms | 56.9 ms | **53.2 ms** | 56.8 ms |
| 2 | 74.6 ms | 58.5 ms | 56.9 ms | 63.1 ms |
| 4 | 74.6 ms | 58.1 ms | 58.8 ms | 73.7 ms |
| 8 | 79.1 ms | 63.7 ms | 64.7 ms | 76.6 ms |
| 16 | 79.5 ms | 58.4 ms | 66.8 ms | 82.3 ms |

**64 KiB blocks are the weakest configuration at every queue depth**, by a
margin that holds across runs. Between 256 KiB, 1 MiB and 4 MiB the differences
are small and not consistently ordered: 1 MiB is fastest at depth 1 (53.2 ms),
while 256 KiB edges it at depths 2, 4, 8 and 16. Run-to-run variance on this
grid is comparable to those gaps, so the reliable conclusion is that any of the
three performs acceptably and 64 KiB does not.

Queue depth shows no benefit on this data set, which is the expected result for
a cache-resident working set: queue depth exists to overlap device latency, and
a file served from memory presents none to overlap. The shipped default of 4 is
chosen for device-bound workloads, which this grid does not exercise.

The allocation column tracks queue depth × block size exactly — depth 16 at
4 MiB allocates 69.7 MB — confirming the buffer slab is the only allocation that
scales, and that per-record allocation is zero.

---

## 4. Comparison with the .NET built-ins

Same data set. `FileStream` reads 1 MiB blocks and counts bytes; `StreamReader`
calls `ReadLineAsync` in a loop.

| Benchmark | Mean | Relative | Allocated |
|---|---:|---:|---:|
| **Line sequential (PunchIO)** | **44.9 ms** | **0.08×** | **268 KB** |
| `StreamReader.ReadLineAsync` | 547.4 ms | 1.00× | 741,526 KB |

**Line-sequential reading is 12.2× faster than `StreamReader` and allocates
2,770× less** — 268 KB against 741 MB. `StreamReader` materialises a string per
line; across 1.5 M lines that is 1.5 M allocations and the collection cost to
match. PunchIO hands back a span over a reused buffer.

| Benchmark | Mean | Allocated |
|---|---:|---:|
| Variable records, unbuffered backend | 46.5 ms | 265 KB |
| Variable records, portable backend | 59.4 ms | 5,194 KB |
| `FileStream` block read, no framing | 39.5 ms | 1,026 KB |

The `FileStream` row is a reference ceiling rather than a competitor: it performs
no record framing at all. PunchIO lands within 1.5× of raw block reading *while
parsing and suffix-validating 1.5 million records* — a difference of roughly
13 ns per record, consistent with the framing measurements in section 1.

On this data set the unbuffered Windows backend completed in 46.5 ms against
59.4 ms for the portable backend. The two backends read through different paths
by design — the portable backend is served by the file system cache, the
unbuffered backend bypasses it — so the figures describe each backend's own
behaviour rather than a controlled comparison between them. The `Auto` policy
selects the unbuffered backend on local fixed volumes and the portable backend
for network and removable volumes.

The allocation difference between the two backends reflects where each slab
lives rather than how much memory is used: the unbuffered backend allocates its
sector-aligned slab with `NativeMemory`, which the garbage collector does not
count, while the portable backend uses a pinned managed array, which it does.
Both allocate exactly one slab per open file.

---

## 5. Configuration defaults

| Setting | Default | Basis |
|---|---|---|
| `BlockSize` | 1 MiB | Within the band that performs well in section 3, and clear of the 64 KiB region that does not |
| `QueueDepth` | 4 | Chosen for device-bound workloads |
| `MaxRecordLength` | 64 KiB | Bounds the stitch buffer against a corrupt length prefix |

All three are per-file settings; a caller who knows their workload can override
any of them through `FileIoOptions` or configuration.

---

## 6. Scope of these measurements

Stated so the numbers are read for what they are:

- **Working set.** The 312 MB data set is cache-resident on a 128 GB machine, so
  sections 3 and 4 measure pipeline and CPU cost rather than device throughput.
  Characterising behaviour against a working set larger than memory requires a
  data set sized to the target machine; `BenchmarkFile.RecordCount` controls
  this.
- **Storage.** Measurements are from a local NTFS volume with a 512-byte logical
  sector. Network shares and 4Kn volumes are supported and exercised by the test
  suite, but are not represented in these timings.
- **Iterations.** A three-iteration job separates the format and allocation
  results in sections 1 and 2, which differ by large multiples. The grid in
  section 3 has run-to-run variance comparable to the smaller differences
  within it: the weakness of 64 KiB is stable across runs, finer distinctions
  between 256 KiB, 1 MiB and 4 MiB are not. Choosing between those three for a
  specific workload calls for a longer job on the target hardware.

## 7. Reproducing

```bash
dotnet run -c Release --project bench/PunchIO.Benchmarks -- --filter '*Framing*'
dotnet run -c Release --project bench/PunchIO.Benchmarks -- --filter '*SequentialRead*'
dotnet run -c Release --project bench/PunchIO.Benchmarks -- --filter '*Backend*'
```

Allocation figures are asserted continuously:

```bash
dotnet test -c Release --filter "FullyQualifiedName~AllocationTests"
```
