# PunchIO — Asynchronous Record-Oriented File I/O Library

**Status:** Implemented and released as 1.0.0
**Date:** 2026-08-31
**Target:** .NET 10 (`net10.0`) and .NET 8 LTS (`net8.0`)

---

## 1. Purpose

A commercial .NET library for high-throughput sequential file I/O over
record-structured files, usable both directly by .NET developers and through an
EXFH (external file handler) boundary by COBOL programs.

The optimization target is sequential read and write of large files. Random
access is supported and correct, but is not what the architecture is tuned for.

### Goals

- Saturate modern storage on sequential reads and writes by keeping multiple
  I/O requests outstanding, with both the request count and request size under
  caller control.
- Support four record formats: line sequential, fixed-length block, and
  variable-length block in Micro Focus and Fujitsu layouts.
- Support relative (record-number addressed) files and byte-offset random access.
- Expose every framing constant as configuration, so a customer with a format
  variant can adapt without a code change.
- Present a first-class API to general .NET callers, independent of COBOL.
- Ship a Windows native fast path in v1 that bypasses the OS cache manager for
  large sequential scans, behind a backend seam that keeps the managed path
  authoritative for correctness.
- Be callable from both native and managed COBOL runtimes through EXFH.
- Ship as a supported commercial product: documented, packaged, API-stable.

### Non-goals

- Indexed / keyed (ISAM, KSDS) organization. That is a separate subsystem with
  its own B-tree, key files, and locking model, and would be its own project.
- Record-level locking and transaction semantics beyond what EXFH opcodes
  require as no-ops.
- Backward sequential traversal. The Fujitsu suffix makes it possible later;
  it is not built now.
- .NET Framework / `netstandard2.0` support.
- A native fast path on Linux (`O_DIRECT`, `io_uring`). The backend seam makes
  it addable; it is not in v1.

---

## 2. Architecture

Four layers. The dependency arrow only ever points down.

```
+- PunchIO.Cobol / PunchIO.Exfh.Native -- control-block parse + opcode dispatch -+
+- Facades ------ LineSequentialReader / VariableRecordWriter / RelativeFile     |
+- Framing ------ IRecordFramer structs: pure span logic, no I/O, no async       |
+- Block pump --- BlockSource / BlockSink: owns the handle, the depth, buffers   |
```

Framers never see a file handle. The pump never knows what a record is. This is
what makes the format logic testable against byte arrays and the async logic
testable against a fake device.

### Package split

| Package | Depends on | Purpose |
|---|---|---|
| `PunchIO.Core` | *nothing* | Engine, framers, options POCOs. AOT- and trim-compatible. |
| `PunchIO.Configuration` | `Microsoft.Extensions.Configuration.Abstractions`, `.Options` | `IConfiguration` binding, DI registration, profile validation. |
| `PunchIO.Cobol` | `PunchIO.Core` | Dialect-neutral EXFH dispatch, FCD and Fujitsu adapters. Managed-callable. |
| `PunchIO.Exfh.Native` | `PunchIO.Cobol` | NativeAOT shared library exporting `EXTFH`. |

`PunchIO.Core` takes no dependencies so that a customer buying it purely for
throughput is not forced to take `Microsoft.Extensions.*`. Core and COBOL
packages are separately shippable SKUs; nothing in Core references COBOL.

---

## 3. The block pump

### Read path

Open with `File.OpenHandle(path, FileMode.Open, FileAccess.Read, share,
FileOptions.Asynchronous | FileOptions.SequentialScan)`.

Buffers are carved from a single slab rather than from `ArrayPool<byte>` — one
allocation, no per-operation pin churn, and explicit control over the buffer
address. How the slab is allocated depends on the backend:

- **Managed backend** — `GC.AllocateArray<byte>(QueueDepth * BlockSize, pinned: true)`.
- **Windows native backend** — `NativeMemory.AlignedAlloc`, aligned to the
  volume's physical sector size, because unbuffered I/O constrains the buffer
  *address* and not just the offset and length. The slab is exposed as
  `Memory<byte>` through a `MemoryManager<byte>` over the pointer, so every
  layer above the device sees the same type either way.

`IBlockDevice` owns slab allocation for exactly this reason: alignment is a
property of the device, not of the pump.

The pump is a ring of `QueueDepth` slots:

1. At start, issue `RandomAccess.ReadAsync` on every slot `i` at offset
   `base + i * BlockSize`.
2. Consume slots **in ring order** — 0, 1, 2, ..., N-1, 0, ... Completions may
   land out of order; delivery to the framer is always in file order.
3. The instant slot `k` is consumed, re-issue it at the next unread offset.

Depth stays pegged at `QueueDepth` with no bookkeeping beyond a head index and
a next-offset counter.

### Read-path correctness rules

These are the failure modes this kind of code gets wrong, and they are
requirements, not optimizations:

- **Short reads.** `RandomAccess.ReadAsync` may return fewer bytes than
  requested before EOF; this is common on SMB and network storage. A slot is
  not complete until it is filled or a zero-length read occurs at its next
  offset. A short read issues a fill-completion read for the slot remainder.
  This is off the hot path but must exist.
- **EOF.** File length is captured at open as a hint, so reads are not issued
  wholly past the end. A zero-length read is still the authoritative EOF
  signal; the length hint is never trusted for correctness.
- **Dispose must drain.** Buffers are pinned and shared with the kernel.
  `DisposeAsync` awaits every outstanding operation — including on the failure
  path — before releasing the slab. Releasing a buffer the kernel still owns is
  memory corruption, not a leak.
- **Deterministic fault ordering.** A faulted slot's exception is captured and
  rethrown at its position in file order, so a given corrupt file fails
  identically on every run regardless of how completions raced.

### Write path

The same ring, inverted. The framer appends into the current block; when the
block is full it is issued with `RandomAccess.WriteAsync` at a monotonically
computed offset and the writer advances to the next free slot, awaiting the
oldest outstanding write if all slots are busy.

Offsets are computed explicitly, so completion order cannot corrupt file
content. `FlushAsync(bool toDisk = false)` drains the ring and writes the
partial tail block; `toDisk: true` forces to media (`FlushFileBuffers` on
Windows, `fsync` on Unix) via the backend.

Durability is caller-driven: writes are OS-buffered and are guaranteed on
stable media only after an awaited `FlushAsync(toDisk: true)`. On a write
failure, file content past the last successful flush is documented as
undefined.

### Backend seam

`IBlockDevice` abstracts open, read, write, flush, length, extend, and slab
allocation. Two implementations ship in v1:

- **`ManagedBlockDevice`** — `RandomAccess` over a handle opened with
  `FileOptions.Asynchronous | FileOptions.SequentialScan`. All platforms.
- **`WindowsBlockDevice`** — the same `RandomAccess` submission path, but on a
  handle additionally opened with `FILE_FLAG_NO_BUFFERING`, with sector-aligned
  buffers and sector-aligned request geometry.

### The Windows native fast path

**The overlapped I/O is not the differentiator.** `FileOptions.Asynchronous`
already gives a handle bound to the thread pool's I/O completion port, and
`RandomAccess.ReadAsync` already issues genuine overlapped reads against it.
The managed backend is not doing synchronous I/O on a worker thread. What the
native backend adds is **`FILE_FLAG_NO_BUFFERING`**: bypassing the Windows
cache manager, which on a sequential scan of a file far larger than RAM is pure
overhead — every block is copied kernel-to-user for a cache entry that will
never be read a second time, and the pages evicted to make room are someone
else's working set.

The flag is reached by OR-ing `(FileOptions)0x20000000` into `File.OpenHandle`.
The runtime's options validation mask explicitly permits that bit for this
purpose, so the handle is still created and IOCP-bound by the runtime and
`RandomAccess` continues to work unchanged. Native interop is therefore
confined to three small P/Invokes — sector-size discovery, `FlushFileBuffers`,
and `SetEndOfFile` — rather than a reimplementation of the I/O submission path.
**This is deliberate: we buy the cache-bypass win without owning an overlapped
I/O engine.**

#### The constraints unbuffered I/O imposes

With `FILE_FLAG_NO_BUFFERING`, every request must have a sector-aligned file
offset, a sector-multiple length, and a sector-aligned buffer address. Three
consequences, each a hard correctness requirement:

- **Sector size must be discovered, not assumed.** `IOCTL_STORAGE_QUERY_PROPERTY`
  gives the physical sector size (4096 on modern drives, 512 on older ones,
  and larger on some SAN LUNs); `GetDiskFreeSpaceW` is the fallback. Results
  are cached per volume. `BlockSize` is rounded up to a multiple of it.
- **Reading the file tail.** A file's length is rarely a sector multiple. The
  final read must request `RoundUpToSector(remaining)` bytes — reading past
  EOF within the final sector is legal and returns a short count — and the
  delivered byte count is then clamped to the true file length. The framer
  must never see the padding.
- **Writing the file tail.** Unbuffered writes *cannot* be a non-sector
  multiple, so the final partial block is written padded up to a sector
  boundary, and the file is then truncated to its true logical length with
  `SetEndOfFile` before the handle closes. This is the single most commonly
  botched part of unbuffered I/O; it gets a dedicated test (section 10).

`FILE_FLAG_NO_BUFFERING` is **not** a durability guarantee — data may still sit
in the drive's own cache. `FlushAsync(toDisk: true)` calls `FlushFileBuffers`
on both backends. The write semantics from earlier in this section are
unchanged by the backend choice.

#### Selection policy

Unbuffered I/O is a clear win on local fixed volumes and frequently a *loss*
over SMB, where the redirector's caching is doing useful work and the alignment
constraints add round trips. Selection is therefore a policy, not a boolean:

```csharp
public enum BlockDevicePolicy { Auto, ForceNative, ForceManaged }
```

`Auto` (the default) resolves the path's volume via `GetDriveType` and picks the
native backend for `DRIVE_FIXED`, the managed backend for network, removable,
and unknown volumes, and the managed backend on every non-Windows platform.
`ForceNative` on a non-Windows platform is a configuration error and fails
validation rather than silently degrading — a customer who asked for the fast
path deserves to be told it is not available, not to wonder why their
benchmark did not move.

Both backends are interchangeable behind `IBlockDevice` and produce
byte-identical results for every format. The benchmark sweep treats the backend
as a dimension (section 11), so the `Auto` policy's thresholds are set from
measurement.

### I/O options

```csharp
public sealed class FileIoOptions
{
    public int  QueueDepth      { get; init; } = 4;
    public int  BlockSize       { get; init; } = 1 << 20;   // 1 MiB
    public int  MaxRecordLength { get; init; } = 64 * 1024;
    public FileShare Share      { get; init; } = FileShare.Read;
    public BlockDevicePolicy Backend { get; init; } = BlockDevicePolicy.Auto;
}
```

Starting defaults are `QueueDepth = 4`, `BlockSize = 1 MiB` — 4 MiB in flight,
a reasonable middle for local NVMe and network storage. **Final defaults are
set by the benchmark sweep (section 11), not by assumption.**

Validation: `QueueDepth` in [1, 256]; `BlockSize` in [4 KiB, 256 MiB];
`MaxRecordLength` in [1, `int.MaxValue`]. `MaxRecordLength` is deliberately
*not* coupled to `BlockSize * QueueDepth` — the stitch buffer accumulates
across as many consecutive blocks as a record needs, so a record may legally
exceed the total bytes in flight.

`BlockSize` is subject to two independent rounding rules that interact: the
fixed-block reader wants a multiple of `RecordLength` to eliminate straddling,
and the native backend requires a multiple of the volume sector size. When both
apply, `BlockSize` is rounded up to a common multiple of the two. If that
product is unreasonable — a 4096-byte sector against a 4095-byte record length
— the straddle-elimination rounding is dropped, sector alignment is kept
(it is a correctness requirement, not an optimization), and the reader uses the
normal stitch path. A caller who pins `BlockSize` explicitly gets sector
rounding only.

---

## 4. Framing contract

```csharp
public enum FrameStatus { Ok, NeedMoreData, EndOfData, Invalid }

public interface IRecordFramer
{
    /// Minimum bytes that must be available before TryFrame can decide.
    int MinimumLookahead { get; }

    FrameStatus TryFrame(
        ReadOnlySpan<byte> input,
        bool isFinalBlock,
        out int consumed,
        out int recordStart,
        out int recordLength);
}
```

`TryFrame` returns **offsets, not spans**, so the reader can hand the caller a
`ReadOnlyMemory<byte>` slice over the owning block without a copy. Readers are
generic over the framer:

```csharp
public sealed class SequentialReader<TFramer> where TFramer : struct, IRecordFramer
```

The struct constraint lets the JIT devirtualize and inline `TryFrame` into the
read loop, so there is no interface dispatch per record.

### Straddle handling

When `TryFrame` returns `NeedMoreData` at the tail of a block, the reader copies
the unconsumed tail into a stitch buffer, appends the head of the next block,
and frames from the stitch buffer. The returned memory then points into the
stitch buffer.

One copy per **block boundary**, not per record. The stitch buffer grows on
demand and is capped by `MaxRecordLength`, so a corrupt length prefix produces
`RecordTooLargeException` rather than an attempted multi-gigabyte allocation.

### Record memory lifetime

**A record's `ReadOnlyMemory<byte>` is valid only until the next
`MoveNextAsync`.** Callers that retain data must copy it.

In Debug builds each record is wrapped in a `MemoryManager<byte>` that throws
`InvalidOperationException` once invalidated, so a caller who stashes a record
fails loudly in Debug rather than silently reading another record's bytes in
Release. Release builds hand out a direct slice with no wrapper.

---

## 5. Record formats

### 5.1 Line sequential

Records are terminated by a line terminator. Scanning uses vectorized
`IndexOf` on the terminator byte, then trims a preceding CR when present.

All byte constants come from a `LineSyntax` struct rather than being hardcoded
ASCII, because EBCDIC relocates every one of them:

```csharp
public readonly struct LineSyntax
{
    public byte LineFeed { get; init; }        // ASCII 0x0A, EBCDIC 0x15 (NL)
    public byte CarriageReturn { get; init; }  // ASCII 0x0D, EBCDIC 0x0D
    public byte Space { get; init; }           // ASCII 0x20, EBCDIC 0x40
    public byte Tab { get; init; }             // ASCII 0x09, EBCDIC 0x05
    public byte Null { get; init; }            // 0x00 both

    public static LineSyntax Ascii { get; }
    public static LineSyntax Ebcdic { get; }
}
```

**Order of operations is: frame in the file's byte encoding, then transcode the
record.** Framing must never assume the record body is ASCII.

Options:

| Option | Default | Meaning |
|---|---|---|
| `Terminator` | `Lf` | `Lf`, `CrLf`, or `Cr` — used on write |
| `AcceptEitherOnRead` | `true` | Accept LF and CRLF on read regardless of `Terminator` |
| `TrimTrailingSpaces` | `false` | Strip trailing spaces from records on read |
| `StripTrailingSpaces` | `true` | Strip trailing spaces on write (COBOL behavior) |
| `TabExpansion.Enabled` | `false` | Expand tabs to the next stop on read |
| `TabExpansion.StopWidth` | `8` | Tab stop width |
| `TabCompression` | `false` | Compress space runs to tabs on write |
| `NullEscape` | `None` | `None` or `MicroFocus` — NUL escaping of sub-0x20 bytes |
| `Encoding` | `null` (raw) | Selects the `LineSyntax` byte constants and enables the text facade |

**What `Encoding` does and does not do.** `SequentialReader<T>` always returns
the file's raw bytes, untouched, regardless of `Encoding`. The setting has two
effects: it selects the `LineSyntax` byte constants used for framing (so an
EBCDIC file is split on `0x15`, not `0x0A`), and it enables an opt-in
`SequentialTextReader` facade that decodes each framed record into a scratch
buffer and yields `ReadOnlyMemory<char>` under the same
valid-until-next-`MoveNextAsync` lifetime rule. Callers who want bytes never
pay for a decode; callers who want text never hand-roll one. Nothing implicitly
rewrites record bytes in place.

### 5.2 Fixed block

Records are exactly `RecordLength` bytes, packed with no delimiters. Framing is
arithmetic.

Fast path: when `BlockSize % RecordLength == 0`, records can never straddle a
block boundary, and the stitch path is compiled out of the loop. The reader
rounds `BlockSize` to a multiple of `RecordLength` to obtain this whenever the
caller has not pinned `BlockSize`, subject to the sector-alignment interaction
described in section 3. The stitch path therefore remains reachable for fixed
block and must stay correct; it is an optimization that may not apply, not a
guarantee.

A trailing partial record is handled per `TrailingPartialRecord`:
`Strict` (throw, default), `Lenient` (return the short record), `Ignore` (drop).

### 5.3 Variable block

One framer, parameterized by a descriptor, rather than two hand-written framers.

```csharp
public enum Endianness  { BigEndian, LittleEndian }
public enum LengthBasis { DataOnly, WithPrefix, WithPrefixAndSuffix }

public readonly struct VariableRecordDescriptor
{
    public int  PrefixBytes    { get; init; }   // 2 or 4
    public int  SuffixBytes    { get; init; }   // 0, or equal to PrefixBytes
    public Endianness  Endianness     { get; init; }
    public LengthBasis LengthIncludes { get; init; }
    public bool ValidateSuffix { get; init; }
    public int  Alignment      { get; init; }   // 1 = packed
    public int  LengthFieldOffset { get; init; }  // within the prefix
    public int  LengthFieldWidth  { get; init; }  // bytes of actual length

    public int  StatusBits       { get; init; }  // high bits carrying a status
    public int  DataRecordStatus { get; init; }
    public VariableFileHeader FileHeader { get; init; }
    public int  MaxRecordLength  { get; init; }
    public int  MinRecordLength  { get; init; }

    public static VariableRecordDescriptor MicroFocus(
        int maxRecordLength = 4095, int minRecordLength = 0);
    public static VariableRecordDescriptor Fujitsu { get; }
}
```

#### Presets

| | File header | Prefix | Suffix | Length field | Endian | Length basis | Alignment |
|---|---|---|---|---|---|---|---|
| **MicroFocus** | 128 bytes | 2 or 4 bytes | none | 4 status bits over 12 or 28 length bits | big-endian | `DataOnly` | 4 bytes |
| **Fujitsu** | none | 4 bytes | 4 bytes | full 4 bytes | little-endian | `DataOnly` | packed |

**Micro Focus layout.** A file of variable-length records opens with a 128-byte
header, which is itself the file's first record: a system record whose control
field carries status `3` over a length of 126 (short control field) or 124
(long), so that field plus data comes to exactly 128 either way. The documented
first four bytes are `x"307E0000"` and `x"3000007C"` respectively.

Each record that follows carries a control field whose top four bits are its
status — `4` for a user data record, `3` system, `2` deleted — over its length.
An 80-byte record therefore begins `x"4050"`. The field is 2 bytes while the
file's maximum record length is under 4,096 and 4 bytes otherwise, which is why
`MicroFocus` takes that maximum as a parameter: it is not inferable per record,
and a reader must be given the same value the writer used. Records are padded
out to the next four-byte boundary, and the padding is not counted in the
length.

Records whose status is not `DataRecordStatus` are skipped rather than returned,
which is what makes the file header and any deleted records invisible to a
caller without any special-casing in the reader.

**Fujitsu length field.** The length in both the prefix and the suffix is the
length of the data returned to the caller. It excludes the 8 bytes of framing,
so a record with `n` data bytes occupies `n + 8` bytes on disk and reports `n`.
It is stored little-endian: the runtime reads and writes it as a native x86
word with no byte swapping. When `ValidateSuffix` is on (the default for this
preset), the suffix is compared to the prefix on read as an integrity check.

This layout is confirmed against a working implementation rather than inferred,
and the byte order is asserted in the test suite so it cannot drift.

**Prefix bytes outside the length field.** For the Micro Focus preset, prefix
byte 2 (flags) and byte 3 are not part of the length. On read they are ignored
by default; setting `ValidateReservedBytes` makes a non-zero byte 3 a
`RecordFormatException`, which is the stricter behavior wanted when validating a
migration. On write both are emitted as zero.

Byte 2 carries runtime-specific flags. This library does not interpret them and
does not carry them across a read-modify-write: a record read from one file and
written to another is emitted with a zero flag byte. A migration that must
preserve those flags should read them from the source bytes directly.

The presets are struct literals in a single file, so adapting a field to a
runtime variant is a one-line change, not a rewrite (section 14).

**Record size ceiling.** The length field bounds the record: Micro Focus's
two-byte field tops out at 65,535 bytes, Fujitsu's four-byte field at 4 GiB.
`VariableRecordDescriptor.MaxDataLength` reports the limit, and the encoder
refuses a record that exceeds it rather than storing a truncated length and
producing a file that reframes into garbage. A Micro Focus variant carrying
larger records needs a wider `LengthFieldWidth`.

#### Format probe

Endianness and length basis are exactly the fields most likely to be wrong
against real-world files, and discovering that against a 400 GB production file
is expensive. `VariableFormatProbe` is a supported utility that takes a path,
tries every candidate descriptor against the first N records, and reports which
descriptors frame self-consistently — the criteria being that prefix matches
suffix (where a suffix exists) and each record's length chains to a plausible
next record header. It ships as a public API and as a CLI verb.

### 5.4 Relative files

Fixed-length records addressed by 1-based record number:

```
offset = (recordNumber - 1) * (SlotHeaderLength + RecordLength)
```

`SlotHeaderLength` defaults to 0 and exists so a record-present marker can be
enabled without changing call sites. Operations: `ReadAsync(n)`,
`WriteAsync(n, data)`, `RewriteAsync(n, data)`, `DeleteAsync(n)`, and a
sequential traversal that skips deleted slots. **The traversal runs through the
block pump**, so reading a relative file front to back is as fast as a
fixed-block read.

### 5.5 Random byte access

`RandomAccessFile` is deliberately thin: direct `RandomAccess.Read/WriteAsync`
at caller-supplied offsets, sharing handle and options plumbing but **bypassing
the pump entirely**. Readahead is worthless when access is unpredictable and
would waste device bandwidth on blocks nobody asked for.

---

## 6. Configuration

### Schema

Named per-file profiles bound from `IConfiguration`:

```json
{
  "PunchIO": {
    "Files": {
      "CustomerMaster": {
        "Format": "VariableBlock",
        "Preset": "Fujitsu",
        "Variable": {
          "PrefixBytes": 4,
          "SuffixBytes": 4,
          "Endianness": "BigEndian",
          "LengthIncludes": "DataOnly",
          "ValidateSuffix": true
        },
        "Io": { "QueueDepth": 8, "BlockSize": "1MiB", "MaxRecordLength": "64KiB",
                "Backend": "Auto" }
      },
      "AuditLog": {
        "Format": "LineSequential",
        "Preset": "MicroFocus",
        "Line": {
          "Terminator": "CrLf",
          "AcceptEitherOnRead": true,
          "StripTrailingSpaces": true,
          "TabExpansion": { "Enabled": true, "StopWidth": 8 },
          "NullEscape": "MicroFocus",
          "Encoding": "ibm037"
        }
      },
      "Ledger": {
        "Format": "FixedBlock",
        "Fixed": { "RecordLength": 512, "TrailingPartialRecord": "Strict" }
      }
    }
  }
}
```

### Rules

- **Preset-then-override layering.** `Preset` seeds every field; explicit keys
  win. Changing one field of a known format is a one-line customization.
- **Validation at profile construction.** An invalid or contradictory profile
  fails when the profile is resolved, with a message naming both the file
  profile and the offending key — never at byte 40 of a 400 GB file.
- **Unit suffixes.** Size-valued keys accept `"1MiB"`, `"512KiB"`, `"4096"`.
  They bind as strings and are parsed during validation, which keeps the
  binder AOT-safe.
- **Source-generated binding.** `PunchIO.Configuration` sets
  `EnableConfigurationBindingGenerator`. No reflection-based binding anywhere,
  because the native EXFH host is published with NativeAOT.

### Registration

```csharp
services.AddPunchIO(configuration);
// then
var profile = provider.GetRequiredService<IFileProfileProvider>().Get("CustomerMaster");
await using var reader = profile.OpenRead(path);
```

Profiles are equally constructible in code; configuration is a convenience
layer over the same options objects, never a required path.

---

## 7. Public API sketch

```csharp
namespace PunchIO;

public static class RecordFile
{
    public static SequentialReader<LineSequentialFramer> OpenLineSequentialRead(
        string path, LineSequentialOptions? options = null, FileIoOptions? io = null);

    public static SequentialReader<FixedBlockFramer> OpenFixedBlockRead(
        string path, int recordLength, FileIoOptions? io = null);

    public static SequentialReader<VariableRecordFramer> OpenVariableRead(
        string path, VariableRecordDescriptor descriptor, FileIoOptions? io = null);

    // matching Open*Write factories
}

public sealed class SequentialReader<TFramer> : IAsyncDisposable
    where TFramer : struct, IRecordFramer
{
    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken ct = default);

    public ValueTask<bool> MoveNextAsync(CancellationToken ct = default);
    public ReadOnlyMemory<byte> Current { get; }

    public long RecordNumber { get; }
    public long BytePosition { get; }
}

public sealed class SequentialWriter<TFramer> : IAsyncDisposable
    where TFramer : struct, IRecordFramer
{
    public ValueTask WriteAsync(ReadOnlyMemory<byte> record, CancellationToken ct = default);
    public ValueTask FlushAsync(bool toDisk = false, CancellationToken ct = default);
}
```

`IRecordFramer`, `IBlockDevice`, `BlockSource`, and `BlockSink` are **public and
documented**. A customer with a proprietary record format plugs their own framer
into the pump. This is a product feature, not an implementation detail.

Usage:

```csharp
await using var reader = RecordFile.OpenVariableRead(
    path, VariableRecordDescriptor.Fujitsu, new FileIoOptions { QueueDepth = 8 });

await foreach (var record in reader.ReadAllAsync(ct))
    Process(record.Span);   // valid until the next iteration
```

---

## 8. The EXFH boundary

### Dialect-neutral dispatch

```
MF EXTFH  --> FcdView (FCD2/FCD3)     --+
                                        +--> FileOperationRequest --> handle table --> PunchIO.Core
Fujitsu   --> Fujitsu control block    --+
```

Both runtimes want the same thing: an opcode plus a control block. The middle
layer speaks one `FileOperationRequest` / `FileOperationResult` pair; each
dialect gets a thin adapter.

`FcdView` is a `ref struct` over `Span<byte>` exposing named, big-endian-aware
field accessors, with FCD2 / FCD3 detection from the version byte. Open files
live in a handle table keyed by an integer stored in the control block, so the
COBOL side holds only a plain number.

### Opcodes

`OPEN` (input / output / i-o / extend), `CLOSE`, `READ` sequential, `READ`
random, `WRITE`, `REWRITE`, `DELETE`, `START`, `UNLOCK`. `COMMIT` and
`ROLLBACK` are accepted as no-ops returning success.

### Hosts

- **`PunchIO.Cobol`** — `public static int Extfh(ReadOnlySpan<byte> opcode, Span<byte> controlBlock)`,
  called directly from managed COBOL.
- **`PunchIO.Exfh.Native`** — `[UnmanagedCallersOnly(EntryPoint = "EXTFH")]`,
  published with `PublishAot` as a shared library for native runtimes.

### Two load-bearing rules

**No managed exception may cross into native COBOL.** The boundary catches
everything, maps it to a COBOL file status, and returns normally. An escaping
exception through an `[UnmanagedCallersOnly]` frame terminates the process. The
status mapping table is shared by both hosts so managed and native COBOL cannot
drift apart.

**The synchronous bridge does not cost the async advantage.** EXFH is
synchronous — a COBOL `READ` must return a record before it returns. But the
throughput here comes from *readahead depth*, not from the caller being
asynchronous. With N blocks in flight, a COBOL `READ` almost always finds its
record in an already-filled buffer and returns without suspending; the
`ValueTask.IsCompletedSuccessfully` fast path never touches the thread pool.
The pipeline runs ahead of a synchronous caller exactly as it does for an
asynchronous one. Blocking occurs only when the program has genuinely outrun
the device — which is when it should.

The native host's NativeAOT requirement pushes AOT-safety down through the
entire core: no reflection-driven binding anywhere.

---

## 9. Error handling

```
PunchIoException                 (base; carries FileStatus)
+-- RecordFormatException        (byte offset, expected vs. actual)
+-- RecordTooLargeException      (length exceeded MaxRecordLength)
+-- FileProfileException         (invalid configuration; names file + key)
```

`PunchIoException` carries the two-character COBOL `FileStatus`, so the EXFH
mapping is a property read rather than a second translation table that can
disagree with the first.

| Condition | Status |
|---|---|
| Success | `00` |
| End of file | `10` |
| Record not found (relative) | `23` |
| File not found on open | `35` |
| File attribute mismatch | `39` |
| Permanent I/O error / other | `9x` |

`RecordFormatException` reports the byte offset and what was expected. When a
400 GB file has one bad record at 200 GB, that offset is the entire support
interaction.

Pump faults rethrow in file order (section 3). Write failures leave content
past the last successful flush undefined. `DisposeAsync` drains outstanding I/O
on every path, including failure.

---

## 10. Testing strategy

**Framer unit tests** — pure byte arrays, no disk. The boundary matrix:
records spanning two *and three* blocks; a terminator landing exactly on a
block boundary; CR ending one block with LF opening the next; empty records;
truncated prefixes; Fujitsu prefix/suffix mismatch; a record exactly
`MaxRecordLength`; a record one byte over.

**Pump tests** — against a fake `IBlockDevice` that returns short reads,
completes out of order, and injects faults at chosen offsets. Asserts in-order
delivery, deterministic fault ordering, and that dispose drains.

**Backend equivalence** — every framer, round-trip, and property test runs
against both `ManagedBlockDevice` and `WindowsBlockDevice` (the latter skipped
off Windows and off fixed volumes) and must produce byte-identical results.
Two backends that can disagree are two products to support.

**Unbuffered-specific tests**, which are where this path will actually break:

- File lengths spanning every residue class mod sector size — including exactly
  one byte, exactly one sector, and one byte over a sector — read back
  byte-exact with no padding visible to the framer.
- Written tails padded to a sector and then truncated: the resulting file
  length on disk is the true logical length, and reopening it yields the same
  records. Asserted against the byte count, not just the record count.
- Sector-size discovery falling back from `IOCTL_STORAGE_QUERY_PROPERTY` to
  `GetDiskFreeSpaceW`, and a 4096-byte-sector volume exercised explicitly
  rather than assuming the 512 the developer's machine happens to report.
- `ForceNative` on a network path and on a non-Windows platform fails
  validation with a clear message instead of silently degrading.
- An unaligned `BlockSize` supplied by the caller is corrected, and the
  `RecordLength`/sector rounding conflict resolves as specified in section 3.

**Property matrix** — random record sizes crossed with random block sizes and
queue depths, with fixed seeds. This is what actually finds straddle bugs.

**Golden files** — byte-exact reference files per format, checked in. Any
change to a layout appears as a visible diff in review rather than as silent
behavior change in a customer's data.

**Round-trip** — write N records, read them back, assert identical, for every
format and a range of record-size distributions.

**EXFH tests** — drive the dispatcher with synthetic control blocks; assert
returned data and file-status codes for every opcode, including error paths.
Assert that no exception escapes the boundary under injected faults.

**CI** — Windows and Linux. Cross-platform is a product claim, not an
aspiration.

---

## 11. Performance targets

Validated by the BenchmarkDotNet suite, which also sets the shipped defaults:

- Zero allocations per record in steady state for all four formats
  (`MemoryDiagnoser`, asserted in tests).
- Fixed-block sequential read reaches >= 80% of raw device sequential
  throughput on local NVMe.
- Per-record CPU overhead for fixed-block framing under 20 ns.
- Measured baseline comparison against `FileStream` + `StreamReader` for line
  sequential, and against `FileStream` block reads for fixed block. These
  numbers are publishable marketing material.

- The native backend beats the managed backend on a local NVMe sequential scan
  of a file larger than physical RAM. **If it does not, that is a finding, not
  a bug to hide** — the `Auto` policy's thresholds are set from the measurement
  either way, and a negative result gets written down here.

The sweep covers `QueueDepth` x `BlockSize` x `RecordLength` x backend, on a
local fixed volume and an SMB share, and its output determines both the final
default values in section 3 and the `Auto` selection policy.

---

## 12. Productization

- **Packaging** — deterministic builds, SourceLink, `.snupkg` symbol packages,
  package README and metadata, license file.
- **API stability** — `Microsoft.CodeAnalysis.PublicApiAnalyzers` with
  checked-in baseline files; any unintended public surface change fails the
  build.
- **Documentation** — XML doc comments required on every public member, with
  missing-doc warnings as errors for public surface.
- **Samples** — one runnable sample project per format, plus an EXFH sample.
- **Benchmarks** — published results as above.

---

## 13. Repository layout

```
PunchIO.sln
Directory.Build.props          net10.0;net8.0, nullable, AOT analyzers,
                               deterministic, SourceLink, symbols, docs required
Directory.Packages.props       central package management
src/
  PunchIO.Core/                zero-dependency engine; public framer + pump seams
  PunchIO.Configuration/       IConfiguration binding, DI, profile validation
  PunchIO.Cobol/               dialect-neutral dispatch, FCD + Fujitsu adapters
  PunchIO.Exfh.Native/         NativeAOT shared library exporting EXTFH
tests/
  PunchIO.Core.Tests/          xUnit v3
  PunchIO.Configuration.Tests/
  PunchIO.Cobol.Tests/
bench/
  PunchIO.Benchmarks/          BenchmarkDotNet
samples/                       one runnable sample per format, plus EXFH
docs/superpowers/specs/
```

`net8.0` compatibility note: `RandomAccess`, `GC.AllocateArray(pinned:)`,
`IAsyncEnumerable`, and the `Span` overloads used here all exist on `net8.0`.
Divergence is expected to be limited and handled with `#if NET10_0_OR_GREATER`.

---

## 14. Format compatibility and adaptation points

COBOL file layouts vary between vendors, between product generations, and with
compiler directives. The design's answer is that **every layout constant is a
configuration value, not a hard-coded assumption**, and each one is declared in
exactly one place. Adapting to a variant is a single-line change to a
descriptor, with no effect on the framing, pump, or dispatch layers above it.

The table below is the integration reference: what the shipped defaults
implement, and which declaration to change for a variant.

| Layout aspect | Shipped default | Adaptation point |
|---|---|---|
| Fujitsu record framing | 4-byte prefix and 4-byte suffix, little-endian, length counts data only | `VariableRecordDescriptor.Fujitsu` |
| Micro Focus record framing | 128-byte file header; 2- or 4-byte control field, big-endian, 4 status bits over the length; 4-byte alignment | `VariableRecordDescriptor.MicroFocus(int, int)` |
| Length byte order | Big-endian | `Endianness` |
| What the length counts | Data only | `LengthIncludes` |
| Record status bits | Top 4 bits of the control field, `4` meaning user data | `StatusBits`, `DataRecordStatus` |
| File header | 128 bytes for Micro Focus, none for Fujitsu | `FileHeader`, `MicroFocusFileHeader` |
| Record alignment | 4 bytes for Micro Focus, packed for Fujitsu | `Alignment` |
| Relative-file slot header | None. A header is required for deletion to be representable | `RelativeFileOptions.SlotHeaderLength`, `PresentMarker` |
| Line-sequential null escaping | Off. Enabled by the `MicroFocus` profile preset | `LineSequentialOptions.NullEscape` |
| Largest record | 4,095 bytes for Micro Focus with a short control field, 65,535 with a wide one; 4 GiB for Fujitsu | `LengthFieldWidth`, `MaxRecordLength` |
| FCD field offsets | FCD2 and FCD3 layouts | `FcdLayout` |
| EXTFH operation codes | Micro Focus opcode set | `ExfhOpcodes` |

Two properties of this design matter for integration:

**Nothing above `FcdView` reads a raw offset**, and nothing above the framers
reads a raw layout constant. A correction to `FcdLayout` or a
`VariableRecordDescriptor` preset is therefore complete in itself — the
dispatcher, handle table, pump and readers require no corresponding change.

**Layouts can be identified from the data.** `VariableFormatProbe`
(section 5.3) reads a file's opening bytes, tries each candidate layout, and
reports which ones frame it self-consistently. Confirming that a target file
matches the shipped defaults — or discovering which variant it uses — is a
single command:

```
punchio-probe customers.dat
```

Sites integrating against a specific COBOL runtime should run the probe against
representative production files as part of acceptance, and should confirm the
FCD layout and opcode set against the runtime's own header before deploying the
external file handler. Both are configuration, and both have a single place to
change.
