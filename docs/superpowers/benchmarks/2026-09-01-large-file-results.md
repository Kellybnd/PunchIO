# PunchIO Large-File Performance Report

**Version:** 1.0.0
**Date:** 2026-09-01

The [baseline report](2026-09-01-baseline-results.md) measured a 312 MB file
that sat in the file system cache, which made its I/O sections a measure of
pipeline overhead rather than of the device. This report answers the question
that one could not: on multi-gigabyte files that actually reach the disk, does
PunchIO move data faster than the I/O a .NET developer would otherwise write?

It does. Reads run 1.8× faster than a `FileStream` loop and sit at the drive's
ceiling; writes through the backend that `Auto` selects run 1.2× to 1.3× faster
than the buffered .NET paths.

## Test environment

| | |
|---|---|
| CPU | 13th Gen Intel Core i9-13900K, 24 physical / 32 logical cores |
| Memory | 128 GB |
| OS | Windows 11 24H2 (10.0.26100) |
| Runtime | .NET 10.0.11, Release, x64 |
| Storage | Samsung SSD 980 PRO 2 TB, NVMe; the system volume, NTFS, 512-byte logical sector |
| Harness | BenchmarkDotNet 0.15.8, in-process, 1 warm-up and 5 measured passes, one invocation per pass |
| Data set | 20,648,881 records of 200 bytes: a 4.00 GiB Fujitsu variable-record file (4,294,967,248 bytes) and a 3.87 GiB line-sequential file (4,150,425,081 bytes) |
| Machine state | Otherwise idle: under 4% CPU and under 1% disk activity before the run |

Throughput is quoted in decimal gigabytes per second, so 4.29 GB moved in one
second is 4.29 GB/s.

## Method

**Every read starts cold.** Before each pass the file is opened with
`FILE_FLAG_NO_BUFFERING` and closed again, which makes NTFS flush and purge its
cached pages for that file. This was verified directly: a 2 GiB file that read
at 6 to 8 GB/s from cache read at 3.4 to 3.9 GB/s after the purge, and the
unbuffered backend, which cannot be served from the cache at all, matches the
portable backend to within noise in every comparison below. Without this step a
128 GB machine would serve a 4 GiB file from memory on every pass after the
first, and file size alone would prove nothing.

**Every write ends on the disk.** The previous output is deleted before each
pass, and each benchmark ends with a flush to stable media, on the .NET side
and the PunchIO side alike. Without that the buffered .NET paths would report
the time to fill the write-back cache, and the unbuffered backend, which has no
cache to fill, would be compared against a number that leaves the device out.

**The baselines are what a developer would write.** For variable records the
like-for-like baseline is a buffered `FileStream` reading or writing each
record's four-byte prefix, body and four-byte suffix by hand; a raw 1 MiB block
loop with no framing at all sits beside it as the ceiling for buffered .NET I/O.
For lines the baselines are `StreamReader.ReadLineAsync` and
`StreamWriter.WriteLine`. The .NET record loops are synchronous, because three
asynchronous calls per record would be a strawman; PunchIO is measured through
its asynchronous API, which is the only one it has. Every baseline was checked
to produce or consume files byte-identical to PunchIO's before anything was
timed.

**The drive was checked, not assumed.** Five minutes of continuous 4 GiB reads
at the default configuration held a flat 5.8 GB/s, so the read figures are not
shaped by thermal throttling. Roughly 290 GiB had been written during the run
before the write comparison executed, so the drive's SLC cache was exhausted
and every write benchmark saw the same steady-state TLC write rate.

---

## 1. Reading against the .NET built-ins

| Benchmark | Mean | GB/s | Relative | Allocated |
|---|---:|---:|---:|---:|
| `FileStream`, 1 MiB blocks, no framing | 1,257.8 ms | 3.41 | 0.92× | 1 KB |
| `FileStream`, hand-framed records (baseline) | 1,364.6 ms | 3.15 | 1.00× | 1,028 KB |
| **PunchIO, portable backend** | **740.4 ms** | **5.80** | **0.54×** | 5,997 KB |
| **PunchIO, unbuffered backend** | **733.3 ms** | **5.86** | **0.54×** | 2,180 KB |

| Benchmark | Mean | GB/s | Relative | Allocated |
|---|---:|---:|---:|---:|
| `StreamReader.ReadLineAsync` (baseline) | 1,471.9 ms | 2.82 | 1.00× | 10,005,679 KB |
| **PunchIO, line sequential** | **629.7 ms** | **6.59** | **0.43×** | 2,875 KB |

**PunchIO reads variable records 1.86× faster than the `FileStream` record
loop, and 1.72× faster than raw `FileStream` block reads that do no framing at
all.** Line-sequential reading is 2.34× faster than `StreamReader` and
allocates 3,480× less: `StreamReader` materialises 20.6 million strings, close
to 10 GB, where PunchIO hands back a span over a reused buffer.

The gap is the device, not the CPU. A synchronous `FileStream` loop has one
request outstanding, so the drive idles between requests while the cache
manager copies the previous one; on this drive that path tops out around
3.4 GB/s. PunchIO keeps four 1 MiB reads in flight and runs at 5.8 GB/s, within
a few percent of the 6.0 to 6.4 GB/s ceiling that the sweep in section 3 finds
for this drive under any configuration.

The two backends tie at the default queue depth. What the unbuffered backend
buys at that depth is not speed but a clean cache: the portable backend leaves
4 GiB of standby pages behind after every file it reads, evicting whatever else
was resident; the unbuffered backend leaves none. Section 3 shows it also
matters for speed when the queue is shallow.

---

## 2. Writing against the .NET built-ins

| Benchmark | Mean | Std dev | GB/s | Relative | Allocated |
|---|---:|---:|---:|---:|---:|
| `FileStream`, 1 MiB blocks, no framing | 2.864 s | 0.143 s | 1.50 | 0.94× | 6 KB |
| `FileStream`, hand-framed records (baseline) | 3.054 s | 0.132 s | 1.41 | 1.00× | 1,030 KB |
| PunchIO, portable backend | 3.678 s | 0.325 s | 1.17 | 1.21× | 5,707 KB |
| **PunchIO, unbuffered backend** | **2.385 s** | 0.050 s | **1.80** | **0.78×** | 1,210 KB |

| Benchmark | Mean | Std dev | GB/s | Relative | Allocated |
|---|---:|---:|---:|---:|---:|
| `StreamWriter.WriteLine` (baseline) | 3.119 s | 0.214 s | 1.33 | 1.00× | 3,078 KB |
| **PunchIO, line sequential** | **2.170 s** | 0.070 s | **1.91** | **0.70×** | 1,419 KB |

**The unbuffered backend, which is what `Auto` selects on a local volume,
writes 1.28× faster than the `FileStream` record loop and 1.20× faster than raw
`FileStream` block writes.** Line-sequential writing through PunchIO is 1.44×
faster than `StreamWriter`. Both PunchIO paths also run with a third of the
buffered paths' variance.

The buffered .NET paths top out between 1.4 and 1.5 GB/s, and the reason is the
same one that limits their reads: every byte is copied into the cache manager
and written out again later by the lazy writer, on the file system's schedule
rather than the caller's. The unbuffered backend skips the copy and the lazy
writer both, and at 1.8 to 2.0 GB/s it is running at this drive's sustained
write rate once the SLC cache is spent.

The portable backend is the one path slower than its baseline, by 20%. It
issues overlapped writes through the same cache manager, and the extra
concurrency buys nothing when the lazy writer is the bottleneck. The write
sweep in section 4 shows the same gap at every queue depth and block size, so
it is a property of the path, not of one configuration. This is exactly why
`Auto` prefers the unbuffered backend wherever it is available.

---

## 3. Read sweep: queue depth, block size and backend

Sequential read of the 4.00 GiB variable-record file from a cold cache, in
GB/s. The cells at queue depth 4 with 1 MiB blocks are the shipped defaults and
match section 1 to within 6 ms, which is the check that the sweep was not
disturbed while it ran.

Unbuffered backend:

| Queue depth | 64 KiB | 256 KiB | 1 MiB | 4 MiB |
|---|---:|---:|---:|---:|
| 1 | 1.35 | 2.42 | 4.55 | 5.64 |
| 2 | 1.91 | 3.40 | 5.57 | 6.30 |
| 4 | 1.84 | 4.84 | **5.84** | 6.29 |
| 8 | 2.70 | 5.40 | 6.07 | 6.23 |
| 16 | 3.51 | 5.45 | 5.91 | 5.73 |

Portable backend:

| Queue depth | 64 KiB | 256 KiB | 1 MiB | 4 MiB |
|---|---:|---:|---:|---:|
| 1 | 1.00 | 1.96 | 3.15 | 3.80 |
| 2 | 1.43 | 2.68 | 4.57 | 4.96 |
| 4 | 1.98 | 3.95 | **5.85** | 6.22 |
| 8 | 2.26 | 5.26 | 6.40 | 5.61 |
| 16 | 3.17 | 5.88 | 6.03 | 5.37 |

Three things the cache-resident sweep could not show are plain here.

**Queue depth matters once the device is real.** At 1 MiB blocks, going from
one outstanding read to four lifts the unbuffered backend from 4.55 to
5.84 GB/s and the portable backend from 3.15 to 5.85 GB/s. Beyond four the
curve is flat: every cell at 1 MiB or 4 MiB with a queue depth of 4 or more
sits between 5.4 and 6.4 GB/s, and the spread among them is run-to-run noise.
The drive is saturated.

**Small blocks are expensive, and queue depth only partly rescues them.**
64 KiB blocks reach 3.5 GB/s only at depth 16, and cost around 40 MB of
allocation per file on the unbuffered backend, because 65,536 requests each
carry an asynchronous state machine. 256 KiB blocks need depth 8 to reach the
ceiling. 1 MiB reaches it at depth 4.

**The unbuffered backend forgives a shallow queue.** At depth 1 it is 44%
faster than the portable backend with 1 MiB blocks and 48% faster with 4 MiB,
because there is no kernel-to-user copy in series with each request. At depth
4 and above the copy is hidden behind the outstanding requests and the two tie.

---

## 4. Write sweep: queue depth, block size and backend

Sequential write of the 4.00 GiB variable-record file, flushed to disk, in
GB/s.

| Queue depth | 256 KiB unbuffered | 256 KiB portable | 1 MiB unbuffered | 1 MiB portable |
|---|---:|---:|---:|---:|
| 1 | 1.33 | 1.34 | 1.80 | 1.05 |
| 4 | 1.47 | 1.07 | **1.86** | 1.11 |
| 16 | 1.49 | 1.12 | 1.96 | 1.15 |

With 1 MiB blocks the unbuffered backend is 1.7× faster than the portable
backend at every depth; with 256 KiB it is 1.3× faster at depths 4 and 16, and
at depth 1 the portable backend's half-second standard deviation swallows the
difference. On the unbuffered backend 1 MiB beats 256 KiB in every cell, and
depth 1 gives up only 3% against depth 4 because a 1 MiB unbuffered write is
already close to the drive's rate on its own. Depth 16 edges depth 4 by 5%,
which is within the variance of the cells around it.

---

## 5. What this confirms about the defaults

| Setting | Default | Result |
|---|---|---|
| `BlockSize` | 1 MiB | Reaches the drive's ceiling at the default queue depth on reads; the fastest block size measured on writes |
| `QueueDepth` | 4 | The depth at which 1 MiB reads saturate the drive; deeper queues add memory and no speed |
| `Backend` | `Auto` | Selects the unbuffered backend on local volumes, which ties the portable backend on reads at depth 4, beats it by 44% at depth 1, beats it by 1.7× on 1 MiB writes, and leaves the file system cache untouched |

The default configuration reads at 5.8 GB/s against a measured ceiling of 6.0
to 6.4 GB/s on this drive. The cells that beat it do so by 8 to 10%, for four
times the buffer memory or twice the queue, which is not worth changing the
default for.

---

## 6. Scope of these measurements

- **One drive.** A consumer NVMe drive on a desktop. Its sustained write rate
  after the SLC cache is spent, roughly 1.8 to 2.0 GB/s, is the ceiling the
  unbuffered write path hits; the buffered paths stop short of it. Enterprise
  drives and RAID volumes will move the ceiling and may change the margins.
- **Writes were measured at steady state.** Roughly 290 GiB were written
  during the run before the write comparison executed, so the SLC cache played
  no part. A single 4 GiB write to an idle drive will be faster on every path.
- **Variance.** Read benchmarks repeat to within 3%. The buffered write paths
  vary by 5 to 10% between passes as the lazy writer and the drive's cache
  folding interact; the unbuffered backend varies by about 2%. Differences
  between write paths smaller than the variance should not be read as
  rankings.
- **Interference is detectable, and it is not even-handed.** An earlier run
  for this report overlapped with a container build on the same machine. Its
  read sweep fell to about 1.2 GB/s in every cell for thirteen minutes, while
  the same configuration read at 5.8 GB/s before and after; its write figures
  were 20 to 25% below the ones here, and the unbuffered path, which has no
  write-back cache to absorb contention, lost the most: 1.40 GB/s against the
  1.80 GB/s reported here, while the `FileStream` record loop went from 1.29 to
  1.41. The default-configuration cells of the read sweep are the same
  measurement as the comparison class, so a sweep that disagrees with the
  comparison by more than its own error bars should be repeated, not
  interpreted. The sweep reported here agrees to within 6 ms.
- **Cache eviction is Windows-only.** On other platforms the read benchmarks
  print a warning and measure a cache-resident file.
- **Data moved per full run.** About 1 TB read and 430 GiB written, which is
  a few hundredths of a percent of a consumer drive's rated endurance.

## 7. Reproducing

```bash
# PunchIO against FileStream, StreamReader and StreamWriter
dotnet run -c Release --project bench/PunchIO.Benchmarks -- --filter '*Comparison*'

# Queue depth x block size x backend
dotnet run -c Release --project bench/PunchIO.Benchmarks -- --filter '*SequentialReadBenchmarks*' '*SequentialWriteBenchmarks*'
```

The two data files are generated once under the temp directory and reused.
`PUNCHIO_BENCH_SIZE_MIB` overrides the 4 GiB default. The comparison classes
take about three and a half minutes together; the two sweeps take about
thirteen. Run them on an otherwise idle machine.
