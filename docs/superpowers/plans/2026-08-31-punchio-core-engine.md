# PunchIO Core Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `PunchIO.Core` — a zero-dependency .NET library that reads and writes line-sequential, fixed-block, and variable-block (Micro Focus / Fujitsu) record files at high throughput using a configurable-depth asynchronous block pump, with a Windows unbuffered fast path.

**Architecture:** Four layers, dependencies pointing one way only. A *block pump* (`BlockSource`/`BlockSink`) owns the file handle, the queue depth, and the buffer slab, delivering fixed-size blocks in file order. A *framing* layer of pure `struct` framers splits blocks into records using span arithmetic and no I/O. *Readers/writers* compose the two and handle records that straddle block boundaries via a stitch buffer. An `IBlockDevice` seam selects between a portable `RandomAccess` backend and a Windows `FILE_FLAG_NO_BUFFERING` backend.

**Tech Stack:** .NET 10 + .NET 8 LTS, C# 13, xUnit v3, BenchmarkDotNet. No runtime package dependencies in `PunchIO.Core`.

**Spec:** `docs/superpowers/specs/2026-08-31-punchio-file-io-library-design.md`

**Scope:** This plan covers `PunchIO.Core` only, and delivers a complete, shippable library on its own. Four follow-on plans complete the product and will be written after this one lands:

- **Plan B** — `RelativeFile`, `RandomAccessFile`, `VariableFormatProbe`
- **Plan C** — packaging, public API baselines, samples, published benchmarks
- **Plan D** — `PunchIO.Configuration` (IConfiguration profiles, DI, validation)
- **Plan E** — `PunchIO.Cobol` + `PunchIO.Exfh.Native` (EXFH boundary)

---

## Global Constraints

Every task's requirements implicitly include this section.

- **Target frameworks:** `net10.0;net8.0`. Any API newer than net8.0 goes behind `#if NET10_0_OR_GREATER`.
- **`PunchIO.Core` takes zero runtime package dependencies.** Not `Microsoft.Extensions.*`, not anything. A `PackageReference` in `PunchIO.Core.csproj` other than build-time analyzers is a defect.
- **AOT-safe:** no reflection, no dynamic code generation, no `Type.GetType`, no reflection-based serialization. The native EXFH host (Plan E) publishes with NativeAOT and this library must survive it.
- **`<Nullable>enable</Nullable>`**, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` (needed for aligned native slabs).
- **XML documentation is required on every public member.** `<GenerateDocumentationFile>true</GenerateDocumentationFile>` plus CS1591 as an error.
- **Zero allocations per record in steady state.** No `byte[]` allocation, no boxing, no LINQ, no `async` state machine per record on the hot path.
- **Verified platform facts** (measured 2026-08-31, do not re-litigate):
  - `File.OpenHandle` accepts `(FileOptions)0x20000000` (`FILE_FLAG_NO_BUFFERING`) and the resulting handle reports `IsAsync == true`.
  - `RandomAccess.ReadAsync` works on that handle.
  - A read whose length is rounded **up** to a sector multiple past EOF is legal and returns the true remaining byte count.
  - Unaligned length, unaligned offset, **and unaligned buffer address** are each rejected with `IOException`. All three must be enforced.
- **Record memory lifetime:** a record handed to a caller is valid only until the next `MoveNextAsync`. This is the documented contract; never copy defensively to work around it.
- **Naming:** root namespace `PunchIO`, sub-namespaces matching folders (`PunchIO.Framing`, `PunchIO.Devices`, `PunchIO.Buffers`, `PunchIO.Pump`).

---

## File Structure

```
PunchIO.sln
Directory.Build.props                     shared MSBuild settings for all projects
Directory.Packages.props                  central package management
.editorconfig

src/PunchIO.Core/PunchIO.Core.csproj
  Exceptions/FileStatus.cs                COBOL 2-char status codes
  Exceptions/PunchIoException.cs          base; carries FileStatus
  Exceptions/RecordFormatException.cs     malformed framing; carries byte offset
  Exceptions/RecordTooLargeException.cs   record exceeded MaxRecordLength

  Framing/FrameStatus.cs                  Ok | NeedMoreData | EndOfData | Invalid
  Framing/IRecordFramer.cs                the framing contract
  Framing/TrailingPartialRecord.cs        Strict | Lenient | Ignore
  Framing/FixedBlockFramer.cs             fixed-length arithmetic framer
  Framing/LineSyntax.cs                   terminator/space/tab/NUL byte constants
  Framing/LineSequentialOptions.cs        line-sequential behavior switches
  Framing/LineSequentialFramer.cs         terminator scanning + trailing spaces
  Framing/LineRecordTransform.cs          tab expansion + NUL unescape (content rewrite)
  Framing/Endianness.cs                   BigEndian | LittleEndian
  Framing/LengthBasis.cs                  DataOnly | WithPrefix | WithPrefixAndSuffix
  Framing/VariableRecordDescriptor.cs     parameterized layout + MF/Fujitsu presets
  Framing/VariableRecordFramer.cs         prefix/suffix framing

  Buffers/IBlockSlab.cs                   N equally sized blocks, backend-owned
  Buffers/PinnedArraySlab.cs              GC pinned array slab (managed backend)
  Buffers/AlignedNativeSlab.cs            NativeMemory.AlignedAlloc slab (native backend)
  Buffers/PointerMemoryManager.cs         Memory<byte> over a native pointer

  Devices/IBlockDevice.cs                 open/read/write/flush/length/extend/slab
  Devices/ManagedBlockDevice.cs           RandomAccess, all platforms
  Devices/WindowsBlockDevice.cs           NO_BUFFERING + alignment rules
  Devices/BlockDevicePolicy.cs            Auto | ForceNative | ForceManaged
  Devices/BlockDeviceFactory.cs           policy resolution + validation errors
  Devices/Interop/WindowsNative.cs        sector size, FlushFileBuffers, SetEndOfFile
  Devices/Interop/SectorInfoCache.cs      per-volume sector size cache

  Pump/BlockSource.cs                     read ring: in-order delivery, re-issue, EOF
  Pump/BlockSink.cs                       write ring: monotonic offsets, tail handling

  Options/FileIoOptions.cs                QueueDepth, BlockSize, MaxRecordLength, Backend
  Readers/SequentialReader.cs             MoveNextAsync + stitch algorithm
  Readers/SequentialReaderEnumerable.cs   IAsyncEnumerable facade
  Readers/RecordGuard.cs                  Debug-only stale-record detection
  Writers/SequentialWriter.cs             record append + flush
  RecordFile.cs                      public open/create factories

tests/PunchIO.Core.Tests/PunchIO.Core.Tests.csproj
  Framing/FixedBlockFramerTests.cs
  Framing/LineSequentialFramerTests.cs
  Framing/LineRecordTransformTests.cs
  Framing/VariableRecordFramerTests.cs
  Buffers/SlabTests.cs
  Devices/ManagedBlockDeviceTests.cs
  Devices/WindowsBlockDeviceTests.cs
  Devices/BlockDeviceFactoryTests.cs
  Pump/FakeBlockDevice.cs                 test double: short reads, reordering, faults
  Pump/BlockSourceTests.cs
  Pump/BlockSinkTests.cs
  Readers/SequentialReaderTests.cs
  RoundTripTests.cs                       all formats x both backends
  PropertyMatrixTests.cs                  record size x block size x depth x backend
```

---

### Task 1: Repository scaffolding and build configuration

**Files:**
- Create: `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `PunchIO.sln`
- Create: `src/PunchIO.Core/PunchIO.Core.csproj`
- Test: `tests/PunchIO.Core.Tests/PunchIO.Core.Tests.csproj`, `tests/PunchIO.Core.Tests/ScaffoldingTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: a solution where `dotnet build` and `dotnet test` both succeed on `net10.0` and `net8.0`, with `TreatWarningsAsErrors` active. Every later task depends on this.

- [ ] **Step 1: Create the solution and project skeletons**

```bash
cd E:/source/punchio
dotnet new sln -n PunchIO
dotnet new classlib -o src/PunchIO.Core -f net10.0
rm src/PunchIO.Core/Class1.cs
dotnet new classlib -o tests/PunchIO.Core.Tests -f net10.0
rm tests/PunchIO.Core.Tests/Class1.cs
dotnet sln add src/PunchIO.Core tests/PunchIO.Core.Tests
dotnet add tests/PunchIO.Core.Tests reference src/PunchIO.Core
```

- [ ] **Step 2: Write `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <TargetFrameworks>net10.0;net8.0</TargetFrameworks>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <InvariantGlobalization>true</InvariantGlobalization>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <Deterministic>true</Deterministic>
    <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
  </PropertyGroup>

  <!-- Public API quality: required on the shipping library, not on tests. -->
  <PropertyGroup Condition="'$(IsTestProject)' != 'true'">
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <IsAotCompatible>true</IsAotCompatible>
    <IsTrimmable>true</IsTrimmable>
    <EnablePackageValidation>true</EnablePackageValidation>
  </PropertyGroup>

  <PropertyGroup Condition="'$(IsTestProject)' == 'true'">
    <IsPackable>false</IsPackable>
    <OutputType>Exe</OutputType>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>
</Project>
```

**Why `IsAotCompatible` is set here and not in Plan E:** it turns on the trim and AOT analyzers *now*, so reflection creeps in as a build error on the day it is written rather than as a NativeAOT publish failure months later.

- [ ] **Step 3: Mark the test project and add test packages**

Add to `tests/PunchIO.Core.Tests/PunchIO.Core.Tests.csproj` inside the first `<PropertyGroup>`:

```xml
<IsTestProject>true</IsTestProject>
```

Then add the packages (no explicit versions — central package management records the resolved latest):

```bash
dotnet add tests/PunchIO.Core.Tests package xunit.v3
dotnet add tests/PunchIO.Core.Tests package Microsoft.NET.Test.Sdk
```

**Do not add `xunit.runner.visualstudio`.** xUnit v3 runs on Microsoft.Testing
Platform, not VSTest, and the .NET 10 SDK refuses the VSTest path outright:
`Testing with VSTest target is no longer supported by Microsoft.Testing.Platform
on .NET 10 SDK and later`. The opt-in is a **`global.json`** key — not an MSBuild
property and not `dotnet.config`, both of which leave the error in place. Create
`global.json` at the repository root:

```json
{
  "sdk": {
    "version": "10.0.400",
    "rollForward": "latestFeature"
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

Also add these to the test-project `PropertyGroup` in `Directory.Build.props`:

```xml
<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
```

If `Directory.Packages.props` does not exist yet, create it before running the above:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup />
</Project>
```

- [ ] **Step 4: Write the scaffolding test**

`tests/PunchIO.Core.Tests/ScaffoldingTests.cs`:

```csharp
using Xunit;

namespace PunchIO.Core.Tests;

public class ScaffoldingTests
{
    [Fact]
    public void TargetFrameworkIsOneOfTheSupportedOnes()
    {
        // Guards the multi-targeting itself: if a TFM is dropped from
        // Directory.Build.props, this stops reporting the missing one.
        var description = System.Runtime.InteropServices
            .RuntimeInformation.FrameworkDescription;

        Assert.StartsWith(".NET", description);
    }

    [Fact]
    public void UnsafeCodeIsEnabled()
    {
        // AlignedNativeSlab (Task 8) cannot compile without this.
        unsafe
        {
            int value = 42;
            int* p = &value;
            Assert.Equal(42, *p);
        }
    }
}
```

- [ ] **Step 5: Run the build and tests on both frameworks**

```bash
dotnet build
dotnet test
```

Expected: build succeeds with zero warnings; both tests pass on `net10.0` and `net8.0` (4 test results total).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "build: scaffold PunchIO solution targeting net10.0 and net8.0"
```

---

### Task 2: Exceptions and COBOL file status codes

**Files:**
- Create: `src/PunchIO.Core/Exceptions/FileStatus.cs`, `PunchIoException.cs`, `RecordFormatException.cs`, `RecordTooLargeException.cs`
- Test: `tests/PunchIO.Core.Tests/ExceptionTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `FileStatus` (readonly struct, `FileStatus.Ok/EndOfFile/RecordNotFound/FileNotFound/AttributeMismatch/PermanentError`, property `string Code`); `PunchIoException(string message, FileStatus status, Exception? inner = null)` with `FileStatus Status { get; }`; `RecordFormatException(string message, long byteOffset)` with `long ByteOffset { get; }`; `RecordTooLargeException(long byteOffset, int maxRecordLength)`. Every later task throws these and never bare `InvalidOperationException` for file-shaped failures.

**Why this comes second:** Plan E maps these to COBOL status codes by reading `Status` off the exception. Building the hierarchy before any code that throws avoids a later sweep to retrofit it.

- [ ] **Step 1: Write the failing test**

`tests/PunchIO.Core.Tests/ExceptionTests.cs`:

```csharp
using Xunit;

namespace PunchIO.Core.Tests;

public class ExceptionTests
{
    [Theory]
    [InlineData("00")] // success
    [InlineData("10")] // end of file
    [InlineData("23")] // record not found
    [InlineData("35")] // file not found on open
    [InlineData("39")] // attribute mismatch
    public void KnownStatusCodesAreExactlyTwoCharacters(string code)
    {
        var status = new FileStatus(code);
        Assert.Equal(code, status.Code);
        Assert.Equal(2, status.Code.Length);
    }

    [Fact]
    public void StatusCodeMustBeTwoCharacters()
    {
        Assert.Throws<ArgumentException>(() => new FileStatus("9"));
        Assert.Throws<ArgumentException>(() => new FileStatus("000"));
    }

    [Fact]
    public void WellKnownStatusesHaveTheirSpecCodes()
    {
        Assert.Equal("00", FileStatus.Ok.Code);
        Assert.Equal("10", FileStatus.EndOfFile.Code);
        Assert.Equal("23", FileStatus.RecordNotFound.Code);
        Assert.Equal("35", FileStatus.FileNotFound.Code);
        Assert.Equal("39", FileStatus.AttributeMismatch.Code);
        Assert.Equal("90", FileStatus.PermanentError.Code);
    }

    [Fact]
    public void RecordFormatExceptionCarriesTheByteOffset()
    {
        // The offset is the entire support interaction when one record is bad
        // 200 GB into a file, so it is part of the type, not the message text.
        var ex = new RecordFormatException("prefix length 4 exceeds remaining data", 200_000_000_017);

        Assert.Equal(200_000_000_017, ex.ByteOffset);
        Assert.Equal(FileStatus.PermanentError, ex.Status);
        Assert.Contains("200000000017", ex.Message);
    }

    [Fact]
    public void RecordTooLargeExceptionReportsBothTheLimitAndTheOffset()
    {
        var ex = new RecordTooLargeException(4096, 65536);

        Assert.Equal(4096, ex.ByteOffset);
        Assert.Equal(65536, ex.MaxRecordLength);
        Assert.IsAssignableFrom<PunchIoException>(ex);
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

```bash
dotnet test --filter "FullyQualifiedName~ExceptionTests"
```

Expected: FAIL — `The type or namespace name 'FileStatus' could not be found`.

- [ ] **Step 3: Implement `FileStatus`**

`src/PunchIO.Core/Exceptions/FileStatus.cs`:

```csharp
namespace PunchIO;

/// <summary>
/// A two-character COBOL file status code, as reported through an external
/// file handler interface.
/// </summary>
public readonly struct FileStatus : IEquatable<FileStatus>
{
    /// <summary>Initializes a status from its two-character code.</summary>
    /// <param name="code">Exactly two characters, for example <c>"00"</c>.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="code"/> is not exactly two characters long.
    /// </exception>
    public FileStatus(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.Length != 2)
            throw new ArgumentException("A COBOL file status is exactly two characters.", nameof(code));
        Code = code;
    }

    /// <summary>The two-character status code.</summary>
    public string Code { get; }

    /// <summary>Successful completion.</summary>
    public static FileStatus Ok => new("00");

    /// <summary>End of file reached on a sequential read.</summary>
    public static FileStatus EndOfFile => new("10");

    /// <summary>The requested record does not exist.</summary>
    public static FileStatus RecordNotFound => new("23");

    /// <summary>The file was not found when opening.</summary>
    public static FileStatus FileNotFound => new("35");

    /// <summary>The file's attributes conflict with those requested.</summary>
    public static FileStatus AttributeMismatch => new("39");

    /// <summary>A permanent input/output error.</summary>
    public static FileStatus PermanentError => new("90");

    /// <inheritdoc />
    public bool Equals(FileStatus other) => string.Equals(Code, other.Code, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is FileStatus other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Code?.GetHashCode(StringComparison.Ordinal) ?? 0;

    /// <inheritdoc />
    public override string ToString() => Code;

    /// <summary>Compares two statuses for equality.</summary>
    public static bool operator ==(FileStatus left, FileStatus right) => left.Equals(right);

    /// <summary>Compares two statuses for inequality.</summary>
    public static bool operator !=(FileStatus left, FileStatus right) => !left.Equals(right);
}
```

> `ArgumentNullException.ThrowIfNull` exists on net8.0; no `#if` needed.

- [ ] **Step 4: Implement the exception hierarchy**

`src/PunchIO.Core/Exceptions/PunchIoException.cs`:

```csharp
namespace PunchIO;

/// <summary>
/// The base type for failures raised by PunchIO. Carries the COBOL file status
/// an external file handler should report for this failure.
/// </summary>
public class PunchIoException : IOException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="message">A description of the failure.</param>
    /// <param name="status">The COBOL file status for this failure.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public PunchIoException(string message, FileStatus status, Exception? innerException = null)
        : base(message, innerException) => Status = status;

    /// <summary>The COBOL file status corresponding to this failure.</summary>
    public FileStatus Status { get; }
}
```

`src/PunchIO.Core/Exceptions/RecordFormatException.cs`:

```csharp
namespace PunchIO;

/// <summary>
/// The bytes at a given offset do not form a valid record for the configured format.
/// </summary>
public sealed class RecordFormatException : PunchIoException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="message">What was expected and what was found.</param>
    /// <param name="byteOffset">The absolute file offset where framing failed.</param>
    public RecordFormatException(string message, long byteOffset)
        : base($"{message} (at byte offset {byteOffset})", FileStatus.PermanentError)
        => ByteOffset = byteOffset;

    /// <summary>The absolute file offset at which framing failed.</summary>
    public long ByteOffset { get; }
}
```

`src/PunchIO.Core/Exceptions/RecordTooLargeException.cs`:

```csharp
namespace PunchIO;

/// <summary>
/// A record's declared length exceeded the configured maximum. Raised instead of
/// attempting the allocation, so a corrupt length prefix cannot exhaust memory.
/// </summary>
public sealed class RecordTooLargeException : PunchIoException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="byteOffset">The absolute file offset of the oversized record.</param>
    /// <param name="maxRecordLength">The configured limit, in bytes.</param>
    public RecordTooLargeException(long byteOffset, int maxRecordLength)
        : base($"Record at byte offset {byteOffset} exceeds the configured " +
               $"maximum record length of {maxRecordLength} bytes.",
               FileStatus.PermanentError)
    {
        ByteOffset = byteOffset;
        MaxRecordLength = maxRecordLength;
    }

    /// <summary>The absolute file offset of the oversized record.</summary>
    public long ByteOffset { get; }

    /// <summary>The configured maximum record length, in bytes.</summary>
    public int MaxRecordLength { get; }
}
```

- [ ] **Step 5: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~ExceptionTests"
```

Expected: PASS, all cases, both frameworks.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add COBOL file status codes and PunchIO exception hierarchy"
```

---

### Task 3: The framing contract and the fixed-block framer

**Files:**
- Create: `src/PunchIO.Core/Framing/FrameStatus.cs`, `IRecordFramer.cs`, `TrailingPartialRecord.cs`, `FixedBlockFramer.cs`
- Test: `tests/PunchIO.Core.Tests/Framing/FixedBlockFramerTests.cs`

**Interfaces:**
- Consumes: Task 2 exceptions (not thrown here — framers report `FrameStatus.Invalid` and the *reader* raises the exception, because only the reader knows the absolute file offset).
- Produces: `FrameStatus { Ok, NeedMoreData, EndOfData, Invalid }`; `IRecordFramer` with `int MinimumLookahead { get; }` and `FrameStatus TryFrame(ReadOnlySpan<byte> input, bool isFinalBlock, out int consumed, out int recordStart, out int recordLength)`; `FixedBlockFramer(int recordLength, TrailingPartialRecord trailing)`. Tasks 4, 5, 7 implement the same interface; Task 16 consumes it.

**The contract, stated once — every framer in Tasks 4–7 must honour it:**

| Return | Meaning | `consumed` | `recordStart` / `recordLength` |
|---|---|---|---|
| `Ok` | One record framed | bytes to advance past, including framing overhead and padding | offsets **relative to `input`**, naming the record body only |
| `NeedMoreData` | Cannot decide yet; caller must supply more bytes | 0 | 0 |
| `EndOfData` | Input is exhausted cleanly; no more records | may be > 0 to discard trailing padding | 0 |
| `Invalid` | The bytes are malformed | 0 | 0 |

`TryFrame` returns **offsets, not spans**, so the reader can hand out a `ReadOnlyMemory<byte>` slice of the owning block without a copy. `NeedMoreData` must never be returned when `isFinalBlock` is true — at that point the framer has all the bytes there will ever be and must decide.

- [ ] **Step 1: Write the failing tests**

`tests/PunchIO.Core.Tests/Framing/FixedBlockFramerTests.cs`:

```csharp
using PunchIO.Framing;
using Xunit;

namespace PunchIO.Core.Tests.Framing;

public class FixedBlockFramerTests
{
    private static byte[] Bytes(int count, byte seed = 0)
    {
        var b = new byte[count];
        for (int i = 0; i < count; i++) b[i] = (byte)(seed + i);
        return b;
    }

    [Fact]
    public void FramesOneWholeRecord()
    {
        var framer = new FixedBlockFramer(80);
        var input = Bytes(240);

        var status = framer.TryFrame(input, isFinalBlock: false,
            out int consumed, out int start, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(80, consumed);
        Assert.Equal(0, start);
        Assert.Equal(80, length);
    }

    [Fact]
    public void RequestsMoreDataWhenShortOfAWholeRecord()
    {
        var framer = new FixedBlockFramer(80);

        var status = framer.TryFrame(Bytes(79), isFinalBlock: false,
            out int consumed, out _, out _);

        Assert.Equal(FrameStatus.NeedMoreData, status);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void ReportsEndOfDataOnEmptyFinalInput()
    {
        var framer = new FixedBlockFramer(80);

        var status = framer.TryFrame(ReadOnlySpan<byte>.Empty, isFinalBlock: true,
            out _, out _, out _);

        Assert.Equal(FrameStatus.EndOfData, status);
    }

    [Theory]
    [InlineData(TrailingPartialRecord.Strict,  FrameStatus.Invalid, 0,  0)]
    [InlineData(TrailingPartialRecord.Lenient, FrameStatus.Ok,      37, 37)]
    [InlineData(TrailingPartialRecord.Ignore,  FrameStatus.EndOfData, 37, 0)]
    public void HandlesATrailingPartialRecordPerPolicy(
        TrailingPartialRecord policy, FrameStatus expected, int expectedConsumed, int expectedLength)
    {
        var framer = new FixedBlockFramer(80, policy);

        var status = framer.TryFrame(Bytes(37), isFinalBlock: true,
            out int consumed, out _, out int length);

        Assert.Equal(expected, status);
        Assert.Equal(expectedConsumed, consumed);
        Assert.Equal(expectedLength, length);
    }

    [Fact]
    public void NeverReturnsNeedMoreDataOnTheFinalBlock()
    {
        // The universal framer contract: on the final block there are no more
        // bytes coming, so the framer must commit to a decision.
        var framer = new FixedBlockFramer(80);

        for (int available = 0; available < 80; available++)
        {
            var status = framer.TryFrame(Bytes(available), isFinalBlock: true,
                out _, out _, out _);
            Assert.NotEqual(FrameStatus.NeedMoreData, status);
        }
    }

    [Fact]
    public void MinimumLookaheadIsTheRecordLength()
    {
        Assert.Equal(512, new FixedBlockFramer(512).MinimumLookahead);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsANonPositiveRecordLength(int recordLength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedBlockFramer(recordLength));
    }
}
```

- [ ] **Step 2: Run and confirm failure**

```bash
dotnet test --filter "FullyQualifiedName~FixedBlockFramerTests"
```

Expected: FAIL — `FrameStatus` / `IRecordFramer` / `FixedBlockFramer` not found.

- [ ] **Step 3: Implement the contract types**

`src/PunchIO.Core/Framing/FrameStatus.cs`:

```csharp
namespace PunchIO.Framing;

/// <summary>The outcome of an attempt to frame one record.</summary>
public enum FrameStatus
{
    /// <summary>A record was framed.</summary>
    Ok,

    /// <summary>More bytes are required before a decision can be made.</summary>
    NeedMoreData,

    /// <summary>The input is cleanly exhausted; there are no further records.</summary>
    EndOfData,

    /// <summary>The bytes are malformed for the configured format.</summary>
    Invalid,
}
```

`src/PunchIO.Core/Framing/TrailingPartialRecord.cs`:

```csharp
namespace PunchIO.Framing;

/// <summary>How a final record shorter than the configured length is handled.</summary>
public enum TrailingPartialRecord
{
    /// <summary>Treat it as a format error.</summary>
    Strict,

    /// <summary>Return it as a short record.</summary>
    Lenient,

    /// <summary>Discard it silently.</summary>
    Ignore,
}
```

`src/PunchIO.Core/Framing/IRecordFramer.cs`:

```csharp
namespace PunchIO.Framing;

/// <summary>
/// Splits a contiguous run of bytes into records. Implementations are pure span
/// logic: no I/O, no allocation, and no knowledge of file offsets.
/// </summary>
/// <remarks>
/// Implement this as a <see langword="readonly struct"/> and pass it as a generic
/// type argument so the framing call is inlined rather than dispatched per record.
/// </remarks>
public interface IRecordFramer
{
    /// <summary>
    /// The smallest number of bytes that could allow <see cref="TryFrame"/> to
    /// reach a decision other than <see cref="FrameStatus.NeedMoreData"/>.
    /// </summary>
    int MinimumLookahead { get; }

    /// <summary>Attempts to frame exactly one record from the front of <paramref name="input"/>.</summary>
    /// <param name="input">The bytes available, starting at a record boundary.</param>
    /// <param name="isFinalBlock">
    /// <see langword="true"/> when no further bytes will ever be supplied. An
    /// implementation must not return <see cref="FrameStatus.NeedMoreData"/> in
    /// that case.
    /// </param>
    /// <param name="consumed">
    /// Bytes to advance past, including any framing overhead and padding.
    /// </param>
    /// <param name="recordStart">
    /// Offset of the record body within <paramref name="input"/>.
    /// </param>
    /// <param name="recordLength">Length of the record body, in bytes.</param>
    /// <returns>The framing outcome.</returns>
    FrameStatus TryFrame(
        ReadOnlySpan<byte> input,
        bool isFinalBlock,
        out int consumed,
        out int recordStart,
        out int recordLength);
}
```

- [ ] **Step 4: Implement `FixedBlockFramer`**

`src/PunchIO.Core/Framing/FixedBlockFramer.cs`:

```csharp
namespace PunchIO.Framing;

/// <summary>
/// Frames records of a single fixed length, packed with no delimiters.
/// </summary>
public readonly struct FixedBlockFramer : IRecordFramer
{
    private readonly int _recordLength;
    private readonly TrailingPartialRecord _trailing;

    /// <summary>Initializes the framer.</summary>
    /// <param name="recordLength">The record length in bytes; must be positive.</param>
    /// <param name="trailing">How to treat a short final record.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="recordLength"/> is not positive.
    /// </exception>
    public FixedBlockFramer(int recordLength,
        TrailingPartialRecord trailing = TrailingPartialRecord.Strict)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recordLength);
        _recordLength = recordLength;
        _trailing = trailing;
    }

    /// <summary>The configured record length in bytes.</summary>
    public int RecordLength => _recordLength;

    /// <inheritdoc />
    public int MinimumLookahead => _recordLength;

    /// <inheritdoc />
    public FrameStatus TryFrame(
        ReadOnlySpan<byte> input,
        bool isFinalBlock,
        out int consumed,
        out int recordStart,
        out int recordLength)
    {
        consumed = 0;
        recordStart = 0;
        recordLength = 0;

        if (input.Length >= _recordLength)
        {
            consumed = _recordLength;
            recordLength = _recordLength;
            return FrameStatus.Ok;
        }

        if (!isFinalBlock)
            return FrameStatus.NeedMoreData;

        if (input.Length == 0)
            return FrameStatus.EndOfData;

        switch (_trailing)
        {
            case TrailingPartialRecord.Lenient:
                consumed = input.Length;
                recordLength = input.Length;
                return FrameStatus.Ok;

            case TrailingPartialRecord.Ignore:
                consumed = input.Length;
                return FrameStatus.EndOfData;

            default:
                return FrameStatus.Invalid;
        }
    }
}
```

- [ ] **Step 5: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~FixedBlockFramerTests"
```

Expected: PASS — 13 test results per framework.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add IRecordFramer contract and fixed-block framer"
```

---

### Task 4: Line syntax and the line-sequential framer

**Files:**
- Create: `src/PunchIO.Core/Framing/LineSyntax.cs`, `LineTerminator.cs`, `LineSequentialOptions.cs`, `LineSequentialFramer.cs`
- Test: `tests/PunchIO.Core.Tests/Framing/LineSequentialFramerTests.cs`

**Interfaces:**
- Consumes: `IRecordFramer`, `FrameStatus`, `TrailingPartialRecord` (Task 3)
- Produces: `LineSyntax` (readonly struct; `LineFeed`, `CarriageReturn`, `Space`, `Tab`, `Null` byte properties; `LineSyntax.Ascii`, `LineSyntax.Ebcdic`); `LineTerminator { Lf, CrLf, Cr }`; `LineSequentialOptions` (class with `init` properties, see below); `LineSequentialFramer(LineSequentialOptions options)`. Task 5 consumes `LineSyntax` and `LineSequentialOptions`; Task 19 constructs the framer.

**The rule this task exists to enforce:** every byte constant comes from `LineSyntax`, never from a literal. EBCDIC relocates all of them — newline is `0x15`, space is `0x40`, tab is `0x05` — so a hardcoded `0x0A` silently corrupts every EBCDIC file. Framing happens in the file's byte encoding; transcoding is a later, separate step (Task 5 and the reader).

- [ ] **Step 1: Write the failing tests**

`tests/PunchIO.Core.Tests/Framing/LineSequentialFramerTests.cs`:

```csharp
using System.Text;
using PunchIO.Framing;
using Xunit;

namespace PunchIO.Core.Tests.Framing;

public class LineSequentialFramerTests
{
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static LineSequentialFramer Framer(
        LineTerminator terminator = LineTerminator.Lf,
        bool acceptEither = true,
        bool trimTrailingSpaces = false) =>
        new(new LineSequentialOptions
        {
            Terminator = terminator,
            AcceptEitherOnRead = acceptEither,
            TrimTrailingSpaces = trimTrailingSpaces,
        });

    [Fact]
    public void FramesALineFeedTerminatedRecord()
    {
        var status = Framer().TryFrame(Ascii("HELLO\nWORLD\n"), isFinalBlock: false,
            out int consumed, out int start, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(6, consumed);   // record + terminator
        Assert.Equal(0, start);
        Assert.Equal(5, length);     // terminator excluded from the record
    }

    [Fact]
    public void StripsTheCarriageReturnOfACrLfPair()
    {
        var status = Framer().TryFrame(Ascii("HELLO\r\nWORLD\r\n"), isFinalBlock: false,
            out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(7, consumed);
        Assert.Equal(5, length);
    }

    [Fact]
    public void KeepsALoneCarriageReturnWhenNotPartOfAPair()
    {
        // A CR in the middle of data is data, not a terminator.
        var status = Framer().TryFrame(Ascii("A\rB\n"), isFinalBlock: false,
            out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(4, consumed);
        Assert.Equal(3, length);
    }

    [Fact]
    public void FramesAnEmptyRecord()
    {
        var status = Framer().TryFrame(Ascii("\n\n"), isFinalBlock: false,
            out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(1, consumed);
        Assert.Equal(0, length);
    }

    [Fact]
    public void RequestsMoreDataWhenNoTerminatorIsPresentYet()
    {
        var status = Framer().TryFrame(Ascii("PARTIAL"), isFinalBlock: false,
            out int consumed, out _, out _);

        Assert.Equal(FrameStatus.NeedMoreData, status);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void RequestsMoreDataWhenABlockEndsOnACarriageReturn()
    {
        // The straddle case that motivates the stitch buffer: CR closes one
        // block and LF opens the next. Deciding here would emit a bogus record.
        var status = Framer().TryFrame(Ascii("HELLO\r"), isFinalBlock: false,
            out _, out _, out _);

        Assert.Equal(FrameStatus.NeedMoreData, status);
    }

    [Fact]
    public void AcceptsAFinalRecordWithNoTerminator()
    {
        var status = Framer().TryFrame(Ascii("LAST"), isFinalBlock: true,
            out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(4, consumed);
        Assert.Equal(4, length);
    }

    [Fact]
    public void ReportsEndOfDataOnEmptyFinalInput()
    {
        var status = Framer().TryFrame(ReadOnlySpan<byte>.Empty, isFinalBlock: true,
            out _, out _, out _);

        Assert.Equal(FrameStatus.EndOfData, status);
    }

    [Fact]
    public void TrimsTrailingSpacesWhenConfigured()
    {
        var status = Framer(trimTrailingSpaces: true)
            .TryFrame(Ascii("DATA    \n"), isFinalBlock: false,
                out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(9, consumed);   // consumed is unaffected by trimming
        Assert.Equal(4, length);
    }

    [Fact]
    public void TrimmingAnAllSpaceRecordYieldsAnEmptyRecord()
    {
        Framer(trimTrailingSpaces: true)
            .TryFrame(Ascii("    \n"), isFinalBlock: false, out int consumed, out _, out int length);

        Assert.Equal(5, consumed);
        Assert.Equal(0, length);
    }

    [Fact]
    public void HonoursACarriageReturnOnlyTerminator()
    {
        var framer = Framer(LineTerminator.Cr, acceptEither: false);

        var status = framer.TryFrame(Ascii("HELLO\rWORLD\r"), isFinalBlock: false,
            out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(6, consumed);
        Assert.Equal(5, length);
    }

    [Fact]
    public void FramesEbcdicUsingEbcdicByteConstants()
    {
        // EBCDIC newline is 0x15, space is 0x40. An ASCII-hardcoded framer
        // would find no terminator at all here.
        var options = new LineSequentialOptions
        {
            Syntax = LineSyntax.Ebcdic,
            TrimTrailingSpaces = true,
        };
        byte[] input = [0xC8, 0xC9, 0x40, 0x40, 0x15, 0xC1, 0x15];

        var status = new LineSequentialFramer(options).TryFrame(input, isFinalBlock: false,
            out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(5, consumed);
        Assert.Equal(2, length);   // 0x40 0x40 trimmed as trailing spaces
    }

    [Fact]
    public void NeverReturnsNeedMoreDataOnTheFinalBlock()
    {
        var framer = Framer();
        foreach (var text in new[] { "", "A", "A\r", "ABC", "A\rB" })
        {
            var status = framer.TryFrame(Ascii(text), isFinalBlock: true, out _, out _, out _);
            Assert.NotEqual(FrameStatus.NeedMoreData, status);
        }
    }
}
```

- [ ] **Step 2: Run and confirm failure**

```bash
dotnet test --filter "FullyQualifiedName~LineSequentialFramerTests"
```

Expected: FAIL — `LineSyntax` / `LineSequentialFramer` not found.

- [ ] **Step 3: Implement `LineSyntax` and `LineTerminator`**

`src/PunchIO.Core/Framing/LineSyntax.cs`:

```csharp
namespace PunchIO.Framing;

/// <summary>
/// The byte values a line-sequential framer treats as structural. Supplying these
/// rather than assuming ASCII is what lets the same framer handle EBCDIC files.
/// </summary>
public readonly struct LineSyntax
{
    /// <summary>The line-feed (record terminator) byte.</summary>
    public byte LineFeed { get; init; }

    /// <summary>The carriage-return byte.</summary>
    public byte CarriageReturn { get; init; }

    /// <summary>The space byte, used for trailing-space trimming and padding.</summary>
    public byte Space { get; init; }

    /// <summary>The horizontal tab byte.</summary>
    public byte Tab { get; init; }

    /// <summary>The null byte, used as the escape prefix when null escaping is enabled.</summary>
    public byte Null { get; init; }

    /// <summary>Byte values for ASCII and ASCII-compatible encodings such as UTF-8.</summary>
    public static LineSyntax Ascii => new()
    {
        LineFeed = 0x0A,
        CarriageReturn = 0x0D,
        Space = 0x20,
        Tab = 0x09,
        Null = 0x00,
    };

    /// <summary>Byte values for EBCDIC code pages.</summary>
    public static LineSyntax Ebcdic => new()
    {
        LineFeed = 0x15,          // NL
        CarriageReturn = 0x0D,
        Space = 0x40,
        Tab = 0x05,
        Null = 0x00,
    };
}
```

`src/PunchIO.Core/Framing/LineTerminator.cs`:

```csharp
namespace PunchIO.Framing;

/// <summary>The terminator written after each record.</summary>
public enum LineTerminator
{
    /// <summary>A single line feed.</summary>
    Lf,

    /// <summary>A carriage return followed by a line feed.</summary>
    CrLf,

    /// <summary>A single carriage return.</summary>
    Cr,
}
```

- [ ] **Step 4: Implement `LineSequentialOptions`**

`src/PunchIO.Core/Framing/LineSequentialOptions.cs`:

```csharp
namespace PunchIO.Framing;

/// <summary>Behavior switches for line-sequential reading and writing.</summary>
public sealed class LineSequentialOptions
{
    /// <summary>The structural byte values. Defaults to <see cref="LineSyntax.Ascii"/>.</summary>
    public LineSyntax Syntax { get; init; } = LineSyntax.Ascii;

    /// <summary>The terminator written after each record. Defaults to <see cref="LineTerminator.Lf"/>.</summary>
    public LineTerminator Terminator { get; init; } = LineTerminator.Lf;

    /// <summary>
    /// When <see langword="true"/> (the default), a carriage return immediately
    /// preceding a line feed is treated as part of the terminator on read,
    /// regardless of <see cref="Terminator"/>.
    /// </summary>
    public bool AcceptEitherOnRead { get; init; } = true;

    /// <summary>Strips trailing spaces from each record on read. Defaults to <see langword="false"/>.</summary>
    public bool TrimTrailingSpaces { get; init; }

    /// <summary>
    /// Strips trailing spaces from each record on write. Defaults to
    /// <see langword="true"/>, matching COBOL line-sequential behavior.
    /// </summary>
    public bool StripTrailingSpaces { get; init; } = true;

    /// <summary>Expands tabs to the next tab stop on read. Defaults to <see langword="false"/>.</summary>
    public bool ExpandTabs { get; init; }

    /// <summary>The tab stop width used by <see cref="ExpandTabs"/>. Defaults to 8.</summary>
    public int TabStopWidth { get; init; } = 8;

    /// <summary>
    /// Escapes control bytes with a preceding null on write and removes those
    /// escapes on read, following the Micro Focus <c>INSERTNULL</c> convention.
    /// </summary>
    public bool NullEscape { get; init; }
}
```

- [ ] **Step 5: Implement `LineSequentialFramer`**

`src/PunchIO.Core/Framing/LineSequentialFramer.cs`:

```csharp
namespace PunchIO.Framing;

/// <summary>
/// Frames records terminated by a line terminator, in the file's own byte encoding.
/// </summary>
public readonly struct LineSequentialFramer : IRecordFramer
{
    private readonly byte _terminator;
    private readonly byte _carriageReturn;
    private readonly byte _space;
    private readonly bool _stripPrecedingCr;
    private readonly bool _trimTrailingSpaces;

    /// <summary>Initializes the framer from line-sequential options.</summary>
    /// <param name="options">The behavior switches to apply.</param>
    public LineSequentialFramer(LineSequentialOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _terminator = options.Terminator == LineTerminator.Cr
            ? options.Syntax.CarriageReturn
            : options.Syntax.LineFeed;

        _carriageReturn = options.Syntax.CarriageReturn;
        _space = options.Syntax.Space;

        // A CR only forms part of the terminator when the terminator is LF-based.
        _stripPrecedingCr = options.Terminator != LineTerminator.Cr
            && (options.AcceptEitherOnRead || options.Terminator == LineTerminator.CrLf);

        _trimTrailingSpaces = options.TrimTrailingSpaces;
    }

    /// <inheritdoc />
    public int MinimumLookahead => 1;

    /// <inheritdoc />
    public FrameStatus TryFrame(
        ReadOnlySpan<byte> input,
        bool isFinalBlock,
        out int consumed,
        out int recordStart,
        out int recordLength)
    {
        recordStart = 0;
        consumed = 0;
        recordLength = 0;

        int index = input.IndexOf(_terminator);
        int end;

        if (index >= 0)
        {
            consumed = index + 1;
            end = index;

            if (_stripPrecedingCr && end > 0 && input[end - 1] == _carriageReturn)
                end--;
        }
        else
        {
            if (!isFinalBlock)
                return FrameStatus.NeedMoreData;

            if (input.Length == 0)
                return FrameStatus.EndOfData;

            // A final record with no terminator is still a record.
            consumed = input.Length;
            end = input.Length;
        }

        if (_trimTrailingSpaces)
            while (end > 0 && input[end - 1] == _space)
                end--;

        recordLength = end;
        return FrameStatus.Ok;
    }
}
```

**Note on the CR-at-block-end case:** when a block ends with CR and no LF, `IndexOf` returns -1 and the framer returns `NeedMoreData`, so the reader stitches the next block on and reframes. No special case is needed — the general path already handles it, which is why the test above asserts it explicitly.

- [ ] **Step 6: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~LineSequentialFramerTests"
```

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add line-sequential framer with encoding-agnostic line syntax"
```

---

### Task 5: Tab expansion and null-escape transforms

**Files:**
- Create: `src/PunchIO.Core/Framing/LineRecordTransform.cs`
- Test: `tests/PunchIO.Core.Tests/Framing/LineRecordTransformTests.cs`

**Interfaces:**
- Consumes: `LineSyntax`, `LineSequentialOptions` (Task 4)
- Produces: `LineRecordTransform(LineSequentialOptions options)` with `bool IsIdentity { get; }`, `int MaxExpansion(int sourceLength)`, `bool TryDecode(ReadOnlySpan<byte> source, Span<byte> destination, out int written)`, `bool TryEncode(ReadOnlySpan<byte> source, Span<byte> destination, out int written)`. Task 16 (reader) calls `TryDecode`; Task 18 (writer) calls `TryEncode`.

**Why this is separate from the framer:** tab expansion and null unescaping *rewrite record content*, and a framer only reports offsets into bytes it was given. Keeping the rewrite in its own type means the framer stays a pure boundary-finder and the zero-copy path stays intact whenever `IsIdentity` is true — which is the common case. The reader only allocates and copies into a scratch buffer when a transform is actually configured.

- [ ] **Step 1: Write the failing tests**

`tests/PunchIO.Core.Tests/Framing/LineRecordTransformTests.cs`:

```csharp
using System.Text;
using PunchIO.Framing;
using Xunit;

namespace PunchIO.Core.Tests.Framing;

public class LineRecordTransformTests
{
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static string Decode(LineSequentialOptions options, string input)
    {
        var transform = new LineRecordTransform(options);
        var source = Ascii(input);
        var destination = new byte[transform.MaxExpansion(source.Length)];

        Assert.True(transform.TryDecode(source, destination, out int written));
        return Encoding.ASCII.GetString(destination, 0, written);
    }

    [Fact]
    public void IdentityWhenNoContentRewritingIsConfigured()
    {
        var transform = new LineRecordTransform(new LineSequentialOptions());
        Assert.True(transform.IsIdentity);
    }

    [Fact]
    public void NotIdentityWhenTabExpansionIsOn()
    {
        var transform = new LineRecordTransform(new LineSequentialOptions { ExpandTabs = true });
        Assert.False(transform.IsIdentity);
    }

    [Theory]
    [InlineData("\tX",       "        X")]   // tab at column 0 -> 8 spaces
    [InlineData("AB\tX",     "AB      X")]   // advances to column 8
    [InlineData("ABCDEFG\tX","ABCDEFG X")]   // one space to reach column 8
    [InlineData("ABCDEFGH\tX", "ABCDEFGH        X")] // already at a stop -> full tab
    [InlineData("\t\tX",     "                X")]   // consecutive tabs
    public void ExpandsTabsToTheNextTabStop(string input, string expected)
    {
        var options = new LineSequentialOptions { ExpandTabs = true, TabStopWidth = 8 };
        Assert.Equal(expected, Decode(options, input));
    }

    [Fact]
    public void HonoursANonDefaultTabStopWidth()
    {
        var options = new LineSequentialOptions { ExpandTabs = true, TabStopWidth = 4 };
        Assert.Equal("AB  X", Decode(options, "AB\tX"));
    }

    [Fact]
    public void DecodeRemovesNullEscapes()
    {
        // NUL is the escape prefix: the byte after it is literal data.
        var options = new LineSequentialOptions { NullEscape = true };
        var transform = new LineRecordTransform(options);

        byte[] source = [0x41, 0x00, 0x09, 0x42];   // A, escape, TAB, B
        var destination = new byte[transform.MaxExpansion(source.Length)];

        Assert.True(transform.TryDecode(source, destination, out int written));
        Assert.Equal([0x41, 0x09, 0x42], destination[..written]);
    }

    [Fact]
    public void EncodeInsertsNullEscapesBeforeControlBytes()
    {
        var options = new LineSequentialOptions { NullEscape = true };
        var transform = new LineRecordTransform(options);

        byte[] source = [0x41, 0x09, 0x42];         // A, TAB, B
        var destination = new byte[transform.MaxExpansion(source.Length)];

        Assert.True(transform.TryEncode(source, destination, out int written));
        Assert.Equal([0x41, 0x00, 0x09, 0x42], destination[..written]);
    }

    [Fact]
    public void EncodeEscapesTheEscapeByteItself()
    {
        var options = new LineSequentialOptions { NullEscape = true };
        var transform = new LineRecordTransform(options);

        byte[] source = [0x41, 0x00, 0x42];
        var destination = new byte[transform.MaxExpansion(source.Length)];

        Assert.True(transform.TryEncode(source, destination, out int written));
        Assert.Equal([0x41, 0x00, 0x00, 0x42], destination[..written]);
    }

    [Fact]
    public void EncodeAndDecodeRoundTripEveryByteValue()
    {
        var options = new LineSequentialOptions { NullEscape = true };
        var transform = new LineRecordTransform(options);

        var source = new byte[256];
        for (int i = 0; i < 256; i++) source[i] = (byte)i;

        var encoded = new byte[transform.MaxExpansion(source.Length)];
        Assert.True(transform.TryEncode(source, encoded, out int encodedLength));

        var decoded = new byte[transform.MaxExpansion(encodedLength)];
        Assert.True(transform.TryDecode(encoded.AsSpan(0, encodedLength), decoded, out int decodedLength));

        Assert.Equal(source, decoded[..decodedLength]);
    }

    [Fact]
    public void ReportsFailureRatherThanOverrunningASmallDestination()
    {
        var options = new LineSequentialOptions { ExpandTabs = true };
        var transform = new LineRecordTransform(options);

        var destination = new byte[4];
        Assert.False(transform.TryDecode(Ascii("\tX"), destination, out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void MaxExpansionCoversTheWorstCase()
    {
        var options = new LineSequentialOptions
        {
            ExpandTabs = true, TabStopWidth = 8, NullEscape = true,
        };
        var transform = new LineRecordTransform(options);

        // Worst case for decode is every byte a tab at a stop boundary.
        Assert.True(transform.MaxExpansion(10) >= 80);
    }
}
```

- [ ] **Step 2: Run and confirm failure**

```bash
dotnet test --filter "FullyQualifiedName~LineRecordTransformTests"
```

Expected: FAIL — `LineRecordTransform` not found.

- [ ] **Step 3: Implement `LineRecordTransform`**

`src/PunchIO.Core/Framing/LineRecordTransform.cs`:

```csharp
namespace PunchIO.Framing;

/// <summary>
/// Rewrites record content for line-sequential files: tab expansion and the
/// Micro Focus null-escape convention.
/// </summary>
/// <remarks>
/// Separate from <see cref="LineSequentialFramer"/> because these operations
/// change bytes rather than locate boundaries. When <see cref="IsIdentity"/> is
/// <see langword="true"/> the reader keeps its zero-copy path and never invokes
/// this type.
/// </remarks>
public readonly struct LineRecordTransform
{
    private readonly byte _tab;
    private readonly byte _space;
    private readonly byte _null;
    private readonly int _tabStopWidth;
    private readonly bool _expandTabs;
    private readonly bool _nullEscape;

    /// <summary>Initializes the transform from line-sequential options.</summary>
    /// <param name="options">The behavior switches to apply.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="LineSequentialOptions.TabStopWidth"/> is not positive.
    /// </exception>
    public LineRecordTransform(LineSequentialOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.TabStopWidth);

        _tab = options.Syntax.Tab;
        _space = options.Syntax.Space;
        _null = options.Syntax.Null;
        _tabStopWidth = options.TabStopWidth;
        _expandTabs = options.ExpandTabs;
        _nullEscape = options.NullEscape;
    }

    /// <summary>
    /// <see langword="true"/> when no rewriting is configured, allowing callers
    /// to skip this type entirely and hand out record bytes unchanged.
    /// </summary>
    public bool IsIdentity => !_expandTabs && !_nullEscape;

    /// <summary>
    /// The largest output length either direction can produce for an input of
    /// <paramref name="sourceLength"/> bytes.
    /// </summary>
    /// <param name="sourceLength">The input length in bytes.</param>
    /// <returns>A destination size guaranteed to be sufficient.</returns>
    public int MaxExpansion(int sourceLength)
    {
        if (IsIdentity) return sourceLength;

        // Tab expansion is the wider of the two: each byte can become TabStopWidth
        // spaces. Null escaping at worst doubles. They cannot compound, because a
        // tab expands to spaces and spaces are never escaped.
        int factor = _expandTabs ? _tabStopWidth : 2;
        return sourceLength * factor;
    }

    /// <summary>Applies the read-side transform: expand tabs, remove null escapes.</summary>
    /// <param name="source">The framed record bytes.</param>
    /// <param name="destination">The buffer to write into.</param>
    /// <param name="written">The number of bytes written.</param>
    /// <returns><see langword="false"/> if <paramref name="destination"/> is too small.</returns>
    public bool TryDecode(ReadOnlySpan<byte> source, Span<byte> destination, out int written)
    {
        written = 0;
        int output = 0;

        for (int i = 0; i < source.Length; i++)
        {
            byte value = source[i];

            if (_nullEscape && value == _null)
            {
                // The escape prefix; the next byte is literal data.
                if (++i >= source.Length) break;
                if (output >= destination.Length) return false;
                destination[output++] = source[i];
                continue;
            }

            if (_expandTabs && value == _tab)
            {
                int spaces = _tabStopWidth - (output % _tabStopWidth);
                if (output + spaces > destination.Length) return false;
                destination.Slice(output, spaces).Fill(_space);
                output += spaces;
                continue;
            }

            if (output >= destination.Length) return false;
            destination[output++] = value;
        }

        written = output;
        return true;
    }

    /// <summary>Applies the write-side transform: insert null escapes before control bytes.</summary>
    /// <param name="source">The caller's record bytes.</param>
    /// <param name="destination">The buffer to write into.</param>
    /// <param name="written">The number of bytes written.</param>
    /// <returns><see langword="false"/> if <paramref name="destination"/> is too small.</returns>
    public bool TryEncode(ReadOnlySpan<byte> source, Span<byte> destination, out int written)
    {
        written = 0;

        if (!_nullEscape)
        {
            if (source.Length > destination.Length) return false;
            source.CopyTo(destination);
            written = source.Length;
            return true;
        }

        int output = 0;
        for (int i = 0; i < source.Length; i++)
        {
            byte value = source[i];

            // Control bytes -- and the escape byte itself -- must be escaped so
            // they cannot be mistaken for a terminator by a reader.
            if (value < 0x20)
            {
                if (output + 2 > destination.Length) return false;
                destination[output++] = _null;
                destination[output++] = value;
                continue;
            }

            if (output >= destination.Length) return false;
            destination[output++] = value;
        }

        written = output;
        return true;
    }
}
```

**Note on `MaxExpansion` and the round-trip test:** encode escapes every byte below `0x20`, which includes the escape byte itself, so the 256-value round-trip exercises both the escape and the double-escape path. Decode is the exact inverse only when tab expansion is off; with both enabled, decode is deliberately lossy in the same way Micro Focus is — an escaped tab survives, a bare tab expands.

- [ ] **Step 4: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~LineRecordTransformTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add tab expansion and null-escape record transforms"
```

---

### Task 6: The variable-record descriptor and format presets

**Files:**
- Create: `src/PunchIO.Core/Framing/Endianness.cs`, `LengthBasis.cs`, `VariableRecordDescriptor.cs`
- Test: `tests/PunchIO.Core.Tests/Framing/VariableRecordDescriptorTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `Endianness { BigEndian, LittleEndian }`; `LengthBasis { DataOnly, WithPrefix, WithPrefixAndSuffix }`; `VariableRecordDescriptor` (readonly struct with `init` properties `PrefixBytes`, `SuffixBytes`, `Endianness`, `LengthIncludes`, `ValidateSuffix`, `Alignment`, `LengthFieldOffset`, `LengthFieldWidth`, `FlagByteOffset`, `ValidateReservedBytes`; statics `MicroFocus` and `Fujitsu`; method `void Validate()`). Task 7 consumes it.

**This task is the containment strategy from spec section 14.** Vendor headers are not available. Every uncertain fact about both formats lives in exactly two struct literals in this one file, so a correction later is a one-line change and a regenerated golden file — never a rewrite. Do not spread these constants into the framer.

- [ ] **Step 1: Write the failing tests**

`tests/PunchIO.Core.Tests/Framing/VariableRecordDescriptorTests.cs`:

```csharp
using PunchIO.Framing;
using Xunit;

namespace PunchIO.Core.Tests.Framing;

public class VariableRecordDescriptorTests
{
    [Fact]
    public void MicroFocusPresetMatchesTheSpecifiedLayout()
    {
        var d = VariableRecordDescriptor.MicroFocus;

        Assert.Equal(4, d.PrefixBytes);          // 4-byte record header
        Assert.Equal(0, d.SuffixBytes);          // no suffix
        Assert.Equal(0, d.LengthFieldOffset);    // length in bytes 0-1
        Assert.Equal(2, d.LengthFieldWidth);
        Assert.Equal(2, d.FlagByteOffset);       // byte 2 carries flags
        Assert.Equal(Endianness.BigEndian, d.Endianness);
        Assert.Equal(LengthBasis.DataOnly, d.LengthIncludes);
        Assert.Equal(1, d.Alignment);
    }

    [Fact]
    public void FujitsuPresetHasAPrefixAndAMatchingSuffix()
    {
        var d = VariableRecordDescriptor.Fujitsu;

        Assert.Equal(4, d.PrefixBytes);
        Assert.Equal(4, d.SuffixBytes);          // prefix AND postfix
        Assert.Equal(0, d.LengthFieldOffset);
        Assert.Equal(4, d.LengthFieldWidth);
        Assert.Equal(-1, d.FlagByteOffset);      // no flag byte
        Assert.True(d.ValidateSuffix);           // free integrity check
        Assert.Equal(LengthBasis.DataOnly, d.LengthIncludes);
    }

    [Fact]
    public void FujitsuLengthCountsOnlyTheCallerVisibleData()
    {
        // Confirmed by the customer: a record with n data bytes occupies n + 8
        // bytes on disk and reports n. This is the one thing about the Fujitsu
        // layout that is NOT a verification item.
        Assert.Equal(LengthBasis.DataOnly, VariableRecordDescriptor.Fujitsu.LengthIncludes);
    }

    [Fact]
    public void PresetsValidateCleanly()
    {
        VariableRecordDescriptor.MicroFocus.Validate();
        VariableRecordDescriptor.Fujitsu.Validate();
    }

    [Theory]
    [InlineData(0, 2, 0, 1)]    // prefix too small to hold the length field
    [InlineData(4, 0, 0, 1)]    // zero-width length field
    [InlineData(4, 5, 0, 1)]    // length field wider than 4 bytes
    [InlineData(4, 4, 1, 1)]    // length field runs past the end of the prefix
    [InlineData(4, 2, 0, 0)]    // alignment must be at least 1
    [InlineData(4, 2, 0, 3)]    // alignment must be a power of two
    public void RejectsInternallyInconsistentDescriptors(
        int prefixBytes, int lengthFieldWidth, int lengthFieldOffset, int alignment)
    {
        var d = new VariableRecordDescriptor
        {
            PrefixBytes = prefixBytes,
            LengthFieldWidth = lengthFieldWidth,
            LengthFieldOffset = lengthFieldOffset,
            Alignment = alignment,
        };

        Assert.Throws<ArgumentException>(d.Validate);
    }

    [Fact]
    public void RejectsSuffixValidationWhenThereIsNoSuffix()
    {
        var d = VariableRecordDescriptor.MicroFocus with { ValidateSuffix = true };
        Assert.Throws<ArgumentException>(d.Validate);
    }

    [Fact]
    public void SupportsWithExpressionsForOneLineCustomisation()
    {
        // The customisation path a customer uses when a preset is close but not exact.
        var d = VariableRecordDescriptor.Fujitsu with { Endianness = Endianness.LittleEndian };

        Assert.Equal(Endianness.LittleEndian, d.Endianness);
        Assert.Equal(4, d.SuffixBytes);   // everything else preserved
        d.Validate();
    }
}
```

- [ ] **Step 2: Run and confirm failure**

```bash
dotnet test --filter "FullyQualifiedName~VariableRecordDescriptorTests"
```

Expected: FAIL — `VariableRecordDescriptor` not found.

- [ ] **Step 3: Implement the enums**

`src/PunchIO.Core/Framing/Endianness.cs`:

```csharp
namespace PunchIO.Framing;

/// <summary>The byte order of a multi-byte length field.</summary>
public enum Endianness
{
    /// <summary>Most significant byte first.</summary>
    BigEndian,

    /// <summary>Least significant byte first.</summary>
    LittleEndian,
}
```

`src/PunchIO.Core/Framing/LengthBasis.cs`:

```csharp
namespace PunchIO.Framing;

/// <summary>What a record's stored length field counts.</summary>
public enum LengthBasis
{
    /// <summary>The record data only, excluding all framing bytes.</summary>
    DataOnly,

    /// <summary>The record data plus the prefix.</summary>
    WithPrefix,

    /// <summary>The record data plus both the prefix and the suffix.</summary>
    WithPrefixAndSuffix,
}
```

- [ ] **Step 4: Implement `VariableRecordDescriptor`**

`src/PunchIO.Core/Framing/VariableRecordDescriptor.cs`:

```csharp
namespace PunchIO.Framing;

/// <summary>
/// The on-disk layout of a variable-length record: where the length lives, how
/// wide it is, what it counts, and whether a trailing copy of it follows the data.
/// </summary>
/// <remarks>
/// Vendor documentation was not available when the presets were written. Every
/// uncertain constant is confined to <see cref="MicroFocus"/> and
/// <see cref="Fujitsu"/> below, so correcting one is a single-line change.
/// </remarks>
public readonly record struct VariableRecordDescriptor
{
    /// <summary>Total width of the header preceding each record, in bytes.</summary>
    public int PrefixBytes { get; init; }

    /// <summary>
    /// Width of the trailing length field following each record, in bytes;
    /// zero when the format has no suffix.
    /// </summary>
    public int SuffixBytes { get; init; }

    /// <summary>Offset of the length field within the prefix, in bytes.</summary>
    public int LengthFieldOffset { get; init; }

    /// <summary>Width of the length field, in bytes. At most 4.</summary>
    public int LengthFieldWidth { get; init; }

    /// <summary>
    /// Offset of a flag byte within the prefix that carries no length
    /// information, or <c>-1</c> when the format has none.
    /// </summary>
    public int FlagByteOffset { get; init; }

    /// <summary>Byte order of the length fields.</summary>
    public Endianness Endianness { get; init; }

    /// <summary>What the stored length counts.</summary>
    public LengthBasis LengthIncludes { get; init; }

    /// <summary>
    /// Compares the suffix against the prefix on read as an integrity check.
    /// Requires <see cref="SuffixBytes"/> to be non-zero.
    /// </summary>
    public bool ValidateSuffix { get; init; }

    /// <summary>
    /// Rejects a record whose prefix contains a non-zero byte outside the length
    /// field and the flag byte.
    /// </summary>
    public bool ValidateReservedBytes { get; init; }

    /// <summary>
    /// Byte boundary each record is padded up to. <c>1</c> means packed.
    /// Must be a power of two.
    /// </summary>
    public int Alignment { get; init; }

    /// <summary>
    /// The Micro Focus variable-length sequential layout: a four-byte header
    /// carrying a big-endian length in its first two bytes, flags in byte 2,
    /// and a reserved zero in byte 3. No suffix.
    /// </summary>
    public static VariableRecordDescriptor MicroFocus => new()
    {
        PrefixBytes = 4,
        SuffixBytes = 0,
        LengthFieldOffset = 0,
        LengthFieldWidth = 2,
        FlagByteOffset = 2,
        Endianness = Endianness.BigEndian,
        LengthIncludes = LengthBasis.DataOnly,
        ValidateSuffix = false,
        ValidateReservedBytes = false,
        Alignment = 1,
    };

    /// <summary>
    /// The Fujitsu variable-length sequential layout: a four-byte length prefix
    /// and a matching four-byte length suffix around each record. Both carry the
    /// caller-visible data length, excluding the eight framing bytes.
    /// </summary>
    public static VariableRecordDescriptor Fujitsu => new()
    {
        PrefixBytes = 4,
        SuffixBytes = 4,
        LengthFieldOffset = 0,
        LengthFieldWidth = 4,
        FlagByteOffset = -1,
        Endianness = Endianness.BigEndian,
        LengthIncludes = LengthBasis.DataOnly,
        ValidateSuffix = true,
        ValidateReservedBytes = false,
        Alignment = 1,
    };

    /// <summary>Throws when the descriptor's fields are mutually inconsistent.</summary>
    /// <exception cref="ArgumentException">The layout cannot be satisfied.</exception>
    public void Validate()
    {
        if (LengthFieldWidth is < 1 or > 4)
            throw new ArgumentException(
                $"{nameof(LengthFieldWidth)} must be between 1 and 4; got {LengthFieldWidth}.");

        if (LengthFieldOffset < 0)
            throw new ArgumentException(
                $"{nameof(LengthFieldOffset)} cannot be negative; got {LengthFieldOffset}.");

        if (LengthFieldOffset + LengthFieldWidth > PrefixBytes)
            throw new ArgumentException(
                $"The length field ({LengthFieldOffset}..{LengthFieldOffset + LengthFieldWidth}) " +
                $"does not fit within a {PrefixBytes}-byte prefix.");

        if (SuffixBytes is < 0 or > 4)
            throw new ArgumentException(
                $"{nameof(SuffixBytes)} must be between 0 and 4; got {SuffixBytes}.");

        if (ValidateSuffix && SuffixBytes == 0)
            throw new ArgumentException(
                $"{nameof(ValidateSuffix)} requires a non-zero {nameof(SuffixBytes)}.");

        if (FlagByteOffset >= PrefixBytes)
            throw new ArgumentException(
                $"{nameof(FlagByteOffset)} {FlagByteOffset} lies outside a {PrefixBytes}-byte prefix.");

        if (Alignment < 1 || (Alignment & (Alignment - 1)) != 0)
            throw new ArgumentException(
                $"{nameof(Alignment)} must be a positive power of two; got {Alignment}.");
    }
}
```

> Declared as a `readonly record struct` specifically so `with` expressions work — that is the one-line customisation path a customer uses when a preset is nearly right, and it is asserted in the tests.

- [ ] **Step 5: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~VariableRecordDescriptorTests"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add variable-record descriptor with Micro Focus and Fujitsu presets"
```

---

### Task 7: The variable-record framer

**Files:**
- Create: `src/PunchIO.Core/Framing/VariableRecordFramer.cs`
- Test: `tests/PunchIO.Core.Tests/Framing/VariableRecordFramerTests.cs`

**Interfaces:**
- Consumes: `IRecordFramer`, `FrameStatus` (Task 3); `VariableRecordDescriptor`, `Endianness`, `LengthBasis` (Task 6)
- Produces: `VariableRecordFramer(VariableRecordDescriptor descriptor)` implementing `IRecordFramer`, plus `static int WriteFraming(Span<byte> destination, int dataLength, in VariableRecordDescriptor descriptor)` used by the writer in Task 18 to emit prefix and suffix. Task 19 constructs the framer.

- [ ] **Step 1: Write the failing tests**

`tests/PunchIO.Core.Tests/Framing/VariableRecordFramerTests.cs`:

```csharp
using PunchIO.Framing;
using Xunit;

namespace PunchIO.Core.Tests.Framing;

public class VariableRecordFramerTests
{
    /// <summary>Builds a Fujitsu record: 4-byte big-endian length, data, same length again.</summary>
    private static byte[] Fujitsu(params byte[] data) =>
    [
        0, 0, (byte)(data.Length >> 8), (byte)data.Length,
        .. data,
        0, 0, (byte)(data.Length >> 8), (byte)data.Length,
    ];

    /// <summary>Builds a Micro Focus record: 2-byte big-endian length, flags, reserved, data.</summary>
    private static byte[] MicroFocus(byte flags, params byte[] data) =>
    [
        (byte)(data.Length >> 8), (byte)data.Length, flags, 0,
        .. data,
    ];

    [Fact]
    public void FramesAFujitsuRecordAndReportsOnlyTheData()
    {
        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);
        var input = Fujitsu(1, 2, 3, 4, 5);

        var status = framer.TryFrame(input, isFinalBlock: false,
            out int consumed, out int start, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(13, consumed);   // 4 prefix + 5 data + 4 suffix
        Assert.Equal(4, start);       // data begins after the prefix
        Assert.Equal(5, length);      // the caller sees only the data
    }

    [Fact]
    public void FramesAMicroFocusRecord()
    {
        var framer = new VariableRecordFramer(VariableRecordDescriptor.MicroFocus);
        var input = MicroFocus(flags: 0, 9, 8, 7);

        var status = framer.TryFrame(input, isFinalBlock: false,
            out int consumed, out int start, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(7, consumed);    // 4 header + 3 data
        Assert.Equal(4, start);
        Assert.Equal(3, length);
    }

    [Fact]
    public void IgnoresTheMicroFocusFlagByteByDefault()
    {
        var framer = new VariableRecordFramer(VariableRecordDescriptor.MicroFocus);

        var status = framer.TryFrame(MicroFocus(flags: 0x40, 1, 2), isFinalBlock: false,
            out _, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(2, length);
    }

    [Fact]
    public void RejectsAFujitsuRecordWhoseSuffixDisagreesWithItsPrefix()
    {
        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);
        var input = Fujitsu(1, 2, 3, 4, 5);
        input[^1] = 99;   // corrupt the trailing length

        var status = framer.TryFrame(input, isFinalBlock: false, out _, out _, out _);

        Assert.Equal(FrameStatus.Invalid, status);
    }

    [Fact]
    public void FramesAZeroLengthRecord()
    {
        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);

        var status = framer.TryFrame(Fujitsu(), isFinalBlock: false,
            out int consumed, out int start, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(8, consumed);
        Assert.Equal(4, start);
        Assert.Equal(0, length);
    }

    [Theory]
    [InlineData(0)]   // nothing at all
    [InlineData(3)]   // truncated prefix
    [InlineData(8)]   // prefix and some data, but no suffix
    public void RequestsMoreDataWhenTheRecordIsIncomplete(int available)
    {
        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);
        var input = Fujitsu(1, 2, 3, 4, 5).AsSpan(0, available).ToArray();

        var status = framer.TryFrame(input, isFinalBlock: false, out int consumed, out _, out _);

        Assert.Equal(FrameStatus.NeedMoreData, status);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void ReportsEndOfDataOnEmptyFinalInput()
    {
        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);

        var status = framer.TryFrame(ReadOnlySpan<byte>.Empty, isFinalBlock: true,
            out _, out _, out _);

        Assert.Equal(FrameStatus.EndOfData, status);
    }

    [Theory]
    [InlineData(3)]   // truncated prefix at end of file
    [InlineData(8)]   // missing suffix at end of file
    public void ReportsInvalidWhenTheFileEndsMidRecord(int available)
    {
        var framer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);
        var input = Fujitsu(1, 2, 3, 4, 5).AsSpan(0, available).ToArray();

        var status = framer.TryFrame(input, isFinalBlock: true, out _, out _, out _);

        Assert.Equal(FrameStatus.Invalid, status);
    }

    [Fact]
    public void ReadsLittleEndianLengthsWhenConfigured()
    {
        var descriptor = VariableRecordDescriptor.Fujitsu with { Endianness = Endianness.LittleEndian };
        var framer = new VariableRecordFramer(descriptor);
        byte[] input = [5, 0, 0, 0, 1, 2, 3, 4, 5, 5, 0, 0, 0];

        var status = framer.TryFrame(input, isFinalBlock: false,
            out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(13, consumed);
        Assert.Equal(5, length);
    }

    [Fact]
    public void SubtractsFramingBytesWhenTheLengthCountsThem()
    {
        var descriptor = VariableRecordDescriptor.Fujitsu
            with { LengthIncludes = LengthBasis.WithPrefixAndSuffix, ValidateSuffix = false };
        var framer = new VariableRecordFramer(descriptor);

        // Stored length 13 = 4 prefix + 5 data + 4 suffix.
        byte[] input = [0, 0, 0, 13, 1, 2, 3, 4, 5, 0, 0, 0, 13];

        var status = framer.TryFrame(input, isFinalBlock: false,
            out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(13, consumed);
        Assert.Equal(5, length);
    }

    [Fact]
    public void RejectsALengthThatUnderflowsAfterSubtractingFraming()
    {
        var descriptor = VariableRecordDescriptor.Fujitsu
            with { LengthIncludes = LengthBasis.WithPrefixAndSuffix, ValidateSuffix = false };
        var framer = new VariableRecordFramer(descriptor);

        byte[] input = [0, 0, 0, 2, 1, 2, 3, 4, 0, 0, 0, 2];   // 2 - 8 is negative

        Assert.Equal(FrameStatus.Invalid,
            framer.TryFrame(input, isFinalBlock: false, out _, out _, out _));
    }

    [Fact]
    public void PadsToTheConfiguredAlignment()
    {
        var descriptor = VariableRecordDescriptor.Fujitsu with { Alignment = 4, ValidateSuffix = false };
        var framer = new VariableRecordFramer(descriptor);

        // 4 prefix + 5 data + 4 suffix = 13, padded up to 16.
        var input = new byte[16];
        Fujitsu(1, 2, 3, 4, 5).CopyTo(input, 0);

        var status = framer.TryFrame(input, isFinalBlock: false, out int consumed, out _, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(16, consumed);
        Assert.Equal(5, length);
    }

    [Fact]
    public void ValidatesReservedPrefixBytesWhenAsked()
    {
        var descriptor = VariableRecordDescriptor.MicroFocus with { ValidateReservedBytes = true };
        var framer = new VariableRecordFramer(descriptor);

        var input = MicroFocus(flags: 0x40, 1, 2);
        Assert.Equal(FrameStatus.Ok, framer.TryFrame(input, false, out _, out _, out _));

        input[3] = 0x01;   // reserved byte 3 must be zero
        Assert.Equal(FrameStatus.Invalid, framer.TryFrame(input, false, out _, out _, out _));
    }

    [Fact]
    public void WritesFramingThatItsOwnFramerCanRead()
    {
        var descriptor = VariableRecordDescriptor.Fujitsu;
        byte[] data = [7, 7, 7];
        var buffer = new byte[descriptor.PrefixBytes + data.Length + descriptor.SuffixBytes];

        int total = VariableRecordFramer.WriteFraming(buffer, data.Length, descriptor);
        data.CopyTo(buffer, descriptor.PrefixBytes);

        Assert.Equal(buffer.Length, total);

        var status = new VariableRecordFramer(descriptor)
            .TryFrame(buffer, isFinalBlock: true, out int consumed, out int start, out int length);

        Assert.Equal(FrameStatus.Ok, status);
        Assert.Equal(buffer.Length, consumed);
        Assert.Equal(data, buffer.AsSpan(start, length).ToArray());
    }
}
```

- [ ] **Step 2: Run and confirm failure**

```bash
dotnet test --filter "FullyQualifiedName~VariableRecordFramerTests"
```

Expected: FAIL — `VariableRecordFramer` not found.

- [ ] **Step 3: Implement `VariableRecordFramer`**

`src/PunchIO.Core/Framing/VariableRecordFramer.cs`:

```csharp
namespace PunchIO.Framing;

/// <summary>
/// Frames variable-length records described by a <see cref="VariableRecordDescriptor"/>,
/// covering both the Micro Focus and Fujitsu layouts.
/// </summary>
public readonly struct VariableRecordFramer : IRecordFramer
{
    private readonly VariableRecordDescriptor _descriptor;

    /// <summary>Initializes the framer.</summary>
    /// <param name="descriptor">The on-disk layout to read.</param>
    /// <exception cref="ArgumentException">The descriptor is internally inconsistent.</exception>
    public VariableRecordFramer(VariableRecordDescriptor descriptor)
    {
        descriptor.Validate();
        _descriptor = descriptor;
    }

    /// <inheritdoc />
    public int MinimumLookahead => _descriptor.PrefixBytes;

    /// <inheritdoc />
    public FrameStatus TryFrame(
        ReadOnlySpan<byte> input,
        bool isFinalBlock,
        out int consumed,
        out int recordStart,
        out int recordLength)
    {
        consumed = 0;
        recordStart = 0;
        recordLength = 0;

        var d = _descriptor;

        if (input.Length < d.PrefixBytes)
        {
            if (input.Length == 0)
                return isFinalBlock ? FrameStatus.EndOfData : FrameStatus.NeedMoreData;

            // A partial prefix at end of file is corruption, not a clean ending.
            return isFinalBlock ? FrameStatus.Invalid : FrameStatus.NeedMoreData;
        }

        if (d.ValidateReservedBytes && !ReservedBytesAreZero(input, d))
            return FrameStatus.Invalid;

        long stored = ReadLength(input.Slice(d.LengthFieldOffset, d.LengthFieldWidth), d.Endianness);

        long dataLength = d.LengthIncludes switch
        {
            LengthBasis.WithPrefix => stored - d.PrefixBytes,
            LengthBasis.WithPrefixAndSuffix => stored - d.PrefixBytes - d.SuffixBytes,
            _ => stored,
        };

        if (dataLength < 0 || dataLength > int.MaxValue - d.PrefixBytes - d.SuffixBytes)
            return FrameStatus.Invalid;

        int total = d.PrefixBytes + (int)dataLength + d.SuffixBytes;
        int padded = d.Alignment > 1 ? RoundUp(total, d.Alignment) : total;

        if (input.Length < padded)
            return isFinalBlock ? FrameStatus.Invalid : FrameStatus.NeedMoreData;

        if (d.ValidateSuffix)
        {
            long suffix = ReadLength(
                input.Slice(d.PrefixBytes + (int)dataLength, d.SuffixBytes), d.Endianness);

            if (suffix != stored)
                return FrameStatus.Invalid;
        }

        recordStart = d.PrefixBytes;
        recordLength = (int)dataLength;
        consumed = padded;
        return FrameStatus.Ok;
    }

    /// <summary>
    /// Writes the prefix and suffix for a record of <paramref name="dataLength"/>
    /// bytes, leaving the data region between them for the caller to fill.
    /// </summary>
    /// <param name="destination">
    /// A buffer at least <see cref="FramedLength"/> bytes long.
    /// </param>
    /// <param name="dataLength">The record's data length in bytes.</param>
    /// <param name="descriptor">The layout to write.</param>
    /// <returns>The total number of bytes the framed record occupies, including padding.</returns>
    public static int WriteFraming(
        Span<byte> destination, int dataLength, in VariableRecordDescriptor descriptor)
    {
        int total = FramedLength(dataLength, descriptor);

        long stored = descriptor.LengthIncludes switch
        {
            LengthBasis.WithPrefix => dataLength + descriptor.PrefixBytes,
            LengthBasis.WithPrefixAndSuffix => dataLength + descriptor.PrefixBytes + descriptor.SuffixBytes,
            _ => dataLength,
        };

        destination[..total].Clear();

        WriteLength(
            destination.Slice(descriptor.LengthFieldOffset, descriptor.LengthFieldWidth),
            stored, descriptor.Endianness);

        if (descriptor.SuffixBytes > 0)
        {
            WriteLength(
                destination.Slice(descriptor.PrefixBytes + dataLength, descriptor.SuffixBytes),
                stored, descriptor.Endianness);
        }

        return total;
    }

    /// <summary>
    /// The total on-disk size of a record carrying <paramref name="dataLength"/>
    /// data bytes, including framing and alignment padding.
    /// </summary>
    /// <param name="dataLength">The record's data length in bytes.</param>
    /// <param name="descriptor">The layout in use.</param>
    /// <returns>The framed size in bytes.</returns>
    public static int FramedLength(int dataLength, in VariableRecordDescriptor descriptor)
    {
        int total = descriptor.PrefixBytes + dataLength + descriptor.SuffixBytes;
        return descriptor.Alignment > 1 ? RoundUp(total, descriptor.Alignment) : total;
    }

    private static bool ReservedBytesAreZero(ReadOnlySpan<byte> input, in VariableRecordDescriptor d)
    {
        for (int i = 0; i < d.PrefixBytes; i++)
        {
            bool isLength = i >= d.LengthFieldOffset && i < d.LengthFieldOffset + d.LengthFieldWidth;
            if (isLength || i == d.FlagByteOffset) continue;
            if (input[i] != 0) return false;
        }

        return true;
    }

    private static long ReadLength(ReadOnlySpan<byte> field, Endianness endianness)
    {
        long value = 0;

        if (endianness == Endianness.BigEndian)
            for (int i = 0; i < field.Length; i++)
                value = (value << 8) | field[i];
        else
            for (int i = field.Length - 1; i >= 0; i--)
                value = (value << 8) | field[i];

        return value;
    }

    private static void WriteLength(Span<byte> field, long value, Endianness endianness)
    {
        if (endianness == Endianness.BigEndian)
            for (int i = field.Length - 1; i >= 0; i--, value >>= 8)
                field[i] = (byte)value;
        else
            for (int i = 0; i < field.Length; i++, value >>= 8)
                field[i] = (byte)value;
    }

    private static int RoundUp(int value, int alignment) =>
        (value + alignment - 1) & ~(alignment - 1);
}
```

**Two decisions worth understanding rather than just copying:**

*Lengths are read into a `long`, not an `int`.* A four-byte big-endian field with the high bit set is a perfectly ordinary bit pattern in a corrupt file. Reading into `int` makes it negative and the arithmetic below it becomes nonsense; reading into `long` keeps it positive so the range check rejects it cleanly.

*A partial prefix at end of file is `Invalid`, not `EndOfData`.* `EndOfData` means the file ended on a record boundary. Three stray bytes after the last record mean the file is truncated, and reporting that as a clean ending would silently swallow data loss.

- [ ] **Step 4: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~VariableRecordFramerTests"
```

Expected: PASS.

- [ ] **Step 5: Run the whole framing suite together**

```bash
dotnet test --filter "FullyQualifiedName~Framing"
```

Expected: PASS — all four framers, both frameworks. This is the point at which every record format is fully implemented and provable without touching a disk.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add variable-record framer for Micro Focus and Fujitsu layouts"
```

---

### Task 8: Buffer slabs

**Files:**
- Create: `src/PunchIO.Core/Buffers/IBlockSlab.cs`, `PointerMemoryManager.cs`, `PinnedArraySlab.cs`, `AlignedNativeSlab.cs`
- Test: `tests/PunchIO.Core.Tests/Buffers/SlabTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `IBlockSlab : IDisposable` with `int BlockCount { get; }`, `int BlockSize { get; }`, `Memory<byte> Block(int index)`; `PinnedArraySlab(int blockCount, int blockSize)`; `AlignedNativeSlab(int blockCount, int blockSize, int alignment)`. Task 9 and Task 11 allocate these; Tasks 13–15 consume `Block(i)`.

**Why slabs exist as a type at all:** the measured platform facts say an unbuffered read rejects an unaligned *buffer address*, not just an unaligned offset and length. That makes buffer allocation a property of the device, not of the pump — and a pooled `byte[]` cannot promise an address. One slab per open file, carved into blocks, is also a single allocation instead of `QueueDepth` pooled rentals with their pinning churn.

**Slot count, and why it is `QueueDepth + 1`:** while the caller holds a block, the pump must not reissue a read into it — that would overwrite the record the caller is reading, mid-iteration. So the slab holds one more block than the configured depth: `QueueDepth` blocks in flight plus one checked out. `QueueDepth` therefore means what the option says it means — outstanding I/O requests.

- [ ] **Step 1: Write the failing tests**

`tests/PunchIO.Core.Tests/Buffers/SlabTests.cs`:

```csharp
using System.Runtime.InteropServices;
using PunchIO.Buffers;
using Xunit;

namespace PunchIO.Core.Tests.Buffers;

public class SlabTests
{
    public static TheoryData<Func<int, int, IBlockSlab>> Factories => new()
    {
        (count, size) => new PinnedArraySlab(count, size),
        (count, size) => new AlignedNativeSlab(count, size, alignment: 4096),
    };

    [Theory]
    [MemberData(nameof(Factories))]
    public void ExposesTheRequestedGeometry(Func<int, int, IBlockSlab> create)
    {
        using var slab = create(5, 4096);

        Assert.Equal(5, slab.BlockCount);
        Assert.Equal(4096, slab.BlockSize);
    }

    [Theory]
    [MemberData(nameof(Factories))]
    public void BlocksAreDistinctAndDoNotOverlap(Func<int, int, IBlockSlab> create)
    {
        using var slab = create(4, 4096);

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
    [MemberData(nameof(Factories))]
    public void RejectsAnOutOfRangeBlockIndex(Func<int, int, IBlockSlab> create)
    {
        using var slab = create(2, 4096);

        Assert.Throws<ArgumentOutOfRangeException>(() => slab.Block(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => slab.Block(2));
    }

    [Theory]
    [MemberData(nameof(Factories))]
    public void BlocksSurviveAGarbageCollection(Func<int, int, IBlockSlab> create)
    {
        // The kernel writes into these buffers while I/O is outstanding. A block
        // that the GC can relocate is a correctness bug, not a performance one.
        using var slab = create(3, 4096);
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
        // Every block start must be aligned, not merely the slab start --
        // an unbuffered read from block 1 is rejected otherwise.
        using var slab = new AlignedNativeSlab(4, 4096, alignment: 4096);

        for (int i = 0; i < slab.BlockCount; i++)
            Assert.Equal(0, AddressOf(slab.Block(i)) % 4096);
    }

    [Fact]
    public void NativeSlabRejectsABlockSizeThatIsNotAnAlignmentMultiple()
    {
        // Block 1 would start unaligned, so the geometry is refused up front.
        Assert.Throws<ArgumentException>(() => new AlignedNativeSlab(2, 4095, alignment: 4096));
    }

    [Fact]
    public void NativeSlabTolerationOfRepeatedDisposal()
    {
        var slab = new AlignedNativeSlab(2, 4096, alignment: 4096);
        slab.Dispose();
        slab.Dispose();   // double free would corrupt the heap
    }

    [Fact]
    public void NativeSlabRejectsUseAfterDisposal()
    {
        var slab = new AlignedNativeSlab(2, 4096, alignment: 4096);
        slab.Dispose();

        Assert.Throws<ObjectDisposedException>(() => slab.Block(0));
    }

    [Theory]
    [MemberData(nameof(Factories))]
    public void RejectsNonPositiveGeometry(Func<int, int, IBlockSlab> create)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => create(0, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() => create(2, 0));
    }

    private static nint AddressOf(Memory<byte> memory)
    {
        using var handle = memory.Pin();
        unsafe { return (nint)handle.Pointer; }
    }
}
```

- [ ] **Step 2: Run and confirm failure**

```bash
dotnet test --filter "FullyQualifiedName~SlabTests"
```

Expected: FAIL — `IBlockSlab` not found.

- [ ] **Step 3: Implement `IBlockSlab` and `PointerMemoryManager`**

`src/PunchIO.Core/Buffers/IBlockSlab.cs`:

```csharp
namespace PunchIO.Buffers;

/// <summary>
/// A fixed set of equally sized I/O buffers backed by one allocation whose
/// address is stable for the lifetime of the slab.
/// </summary>
/// <remarks>
/// Allocated by the block device rather than the pump, because buffer alignment
/// is a device requirement: an unbuffered Windows read rejects a buffer whose
/// address is not sector-aligned.
/// </remarks>
public interface IBlockSlab : IDisposable
{
    /// <summary>The number of blocks in the slab.</summary>
    int BlockCount { get; }

    /// <summary>The size of each block, in bytes.</summary>
    int BlockSize { get; }

    /// <summary>Returns the block at <paramref name="index"/>.</summary>
    /// <param name="index">A zero-based block index.</param>
    /// <returns>Memory covering exactly one block.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> is outside the slab.
    /// </exception>
    Memory<byte> Block(int index);
}
```

`src/PunchIO.Core/Buffers/PointerMemoryManager.cs`:

```csharp
using System.Buffers;

namespace PunchIO.Buffers;

/// <summary>
/// Presents a region of unmanaged memory as <see cref="Memory{T}"/> so native
/// and managed slabs are interchangeable above the device layer.
/// </summary>
internal sealed unsafe class PointerMemoryManager(byte* pointer, int length) : MemoryManager<byte>
{
    public override Span<byte> GetSpan() => new(pointer, length);

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(elementIndex, length);
        return new MemoryHandle(pointer + elementIndex);
    }

    // The memory is unmanaged and permanently fixed; there is nothing to release.
    public override void Unpin() { }

    protected override void Dispose(bool disposing) { }
}
```

- [ ] **Step 4: Implement `PinnedArraySlab`**

`src/PunchIO.Core/Buffers/PinnedArraySlab.cs`:

```csharp
namespace PunchIO.Buffers;

/// <summary>
/// A slab backed by a single pinned managed array. Used by the portable block
/// device, where only address stability matters and alignment does not.
/// </summary>
public sealed class PinnedArraySlab : IBlockSlab
{
    private readonly byte[] _buffer;

    /// <summary>Allocates the slab.</summary>
    /// <param name="blockCount">The number of blocks; must be positive.</param>
    /// <param name="blockSize">The size of each block in bytes; must be positive.</param>
    public PinnedArraySlab(int blockCount, int blockSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);

        BlockCount = blockCount;
        BlockSize = blockSize;

        // Pinned so the kernel can write into it while I/O is outstanding.
        _buffer = GC.AllocateArray<byte>(blockCount * blockSize, pinned: true);
    }

    /// <inheritdoc />
    public int BlockCount { get; }

    /// <inheritdoc />
    public int BlockSize { get; }

    /// <inheritdoc />
    public Memory<byte> Block(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, BlockCount);
        return _buffer.AsMemory(index * BlockSize, BlockSize);
    }

    /// <summary>Releases the slab. The pinned array becomes collectable.</summary>
    public void Dispose() { }
}
```

- [ ] **Step 5: Implement `AlignedNativeSlab`**

`src/PunchIO.Core/Buffers/AlignedNativeSlab.cs`:

```csharp
using System.Runtime.InteropServices;

namespace PunchIO.Buffers;

/// <summary>
/// A slab backed by aligned unmanaged memory. Required by the Windows
/// unbuffered device, which rejects reads and writes whose buffer address is
/// not a multiple of the volume's sector size.
/// </summary>
public sealed unsafe class AlignedNativeSlab : IBlockSlab
{
    private readonly PointerMemoryManager _manager;
    private byte* _pointer;

    /// <summary>Allocates the slab.</summary>
    /// <param name="blockCount">The number of blocks; must be positive.</param>
    /// <param name="blockSize">
    /// The size of each block in bytes; must be positive and a multiple of
    /// <paramref name="alignment"/> so that every block start is aligned.
    /// </param>
    /// <param name="alignment">The required address alignment, in bytes.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="blockSize"/> is not a multiple of <paramref name="alignment"/>.
    /// </exception>
    public AlignedNativeSlab(int blockCount, int blockSize, int alignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);

        if (blockSize % alignment != 0)
        {
            throw new ArgumentException(
                $"Block size {blockSize} must be a multiple of the {alignment}-byte alignment, " +
                "otherwise blocks after the first would start unaligned.",
                nameof(blockSize));
        }

        BlockCount = blockCount;
        BlockSize = blockSize;

        nuint total = (nuint)blockCount * (nuint)blockSize;
        _pointer = (byte*)NativeMemory.AlignedAlloc(total, (nuint)alignment);
        _manager = new PointerMemoryManager(_pointer, blockCount * blockSize);
    }

    /// <inheritdoc />
    public int BlockCount { get; }

    /// <inheritdoc />
    public int BlockSize { get; }

    /// <inheritdoc />
    public Memory<byte> Block(int index)
    {
        ObjectDisposedException.ThrowIf(_pointer is null, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, BlockCount);
        return _manager.Memory.Slice(index * BlockSize, BlockSize);
    }

    /// <summary>Frees the unmanaged allocation. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_pointer is null) return;

        NativeMemory.AlignedFree(_pointer);
        _pointer = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>Frees the allocation if <see cref="Dispose"/> was never called.</summary>
    ~AlignedNativeSlab() => Dispose();
}
```

**On the finalizer:** the slab holds unmanaged memory whose lifetime must outlive any outstanding I/O. The pump guarantees that by draining before disposal (Task 14), but a finalizer is still correct insurance against a leaked slab — and `Dispose` nulls the pointer first, so the double-dispose test above proves the free happens exactly once.

- [ ] **Step 6: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~SlabTests"
```

Expected: PASS — the aligned slab reports every block start at a 4096-byte boundary, and the pinned slab survives a compacting collection.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add pinned and aligned buffer slabs"
```

---

### Task 9: The block device seam and the portable backend

**Files:**
- Create: `src/PunchIO.Core/Devices/IBlockDevice.cs`, `src/PunchIO.Core/Devices/Interop/NativeFileOps.cs`, `src/PunchIO.Core/Devices/ManagedBlockDevice.cs`
- Test: `tests/PunchIO.Core.Tests/Devices/ManagedBlockDeviceTests.cs`

**Interfaces:**
- Consumes: `IBlockSlab`, `PinnedArraySlab` (Task 8); `PunchIoException` (Task 2)
- Produces:
  - `IBlockDevice : IAsyncDisposable` with `long Length { get; }`, `int Alignment { get; }`, `bool RequiresTailPadding { get; }`, `IBlockSlab AllocateSlab(int blockCount, int blockSize)`, `ValueTask<int> ReadAsync(Memory<byte> destination, long fileOffset, CancellationToken ct)`, `ValueTask WriteAsync(ReadOnlyMemory<byte> source, long fileOffset, CancellationToken ct)`, `ValueTask FlushAsync(bool toDisk, CancellationToken ct)`, `ValueTask SetLengthAsync(long length, CancellationToken ct)`
  - `ManagedBlockDevice.Open(string path, FileAccess access, FileShare share)` returning `ManagedBlockDevice`
  - `NativeFileOps.FlushToDisk(SafeFileHandle)` and `NativeFileOps.SetLength(SafeFileHandle, long)`

Tasks 11–15 consume `IBlockDevice`. Task 11 implements it a second time.

**The `ReadAsync` contract, which Task 11 must match exactly:** return the number of bytes *logically* available at `fileOffset`, never more than `destination.Length`, and `0` only at true end of file. The portable backend gets this for free. The unbuffered backend has to round its request up past EOF and clamp the result — and hiding that here is the entire point of the seam, because nothing above the device should know about sectors.

- [ ] **Step 1: Write the failing tests**

`tests/PunchIO.Core.Tests/Devices/ManagedBlockDeviceTests.cs`:

```csharp
using PunchIO.Devices;
using Xunit;

namespace PunchIO.Core.Tests.Devices;

public sealed class ManagedBlockDeviceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"punchio-{Guid.NewGuid():N}.bin");

    public void Dispose() => File.Delete(_path);

    private static byte[] Pattern(int length)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++) b[i] = (byte)(i * 31 + 7);
        return b;
    }

    [Fact]
    public async Task ReportsTheFileLength()
    {
        File.WriteAllBytes(_path, Pattern(1234));

        await using var device = ManagedBlockDevice.Open(_path, FileAccess.Read, FileShare.Read);

        Assert.Equal(1234, device.Length);
    }

    [Fact]
    public async Task ReadsAtAnArbitraryOffset()
    {
        var content = Pattern(4096);
        File.WriteAllBytes(_path, content);

        await using var device = ManagedBlockDevice.Open(_path, FileAccess.Read, FileShare.Read);
        var buffer = new byte[100];

        int read = await device.ReadAsync(buffer, 1000, TestContext.Current.CancellationToken);

        Assert.Equal(100, read);
        Assert.Equal(content.AsSpan(1000, 100).ToArray(), buffer);
    }

    [Fact]
    public async Task ReadAtTheTailReturnsOnlyWhatRemains()
    {
        File.WriteAllBytes(_path, Pattern(100));

        await using var device = ManagedBlockDevice.Open(_path, FileAccess.Read, FileShare.Read);
        var buffer = new byte[4096];

        int read = await device.ReadAsync(buffer, 60, TestContext.Current.CancellationToken);

        Assert.Equal(40, read);
    }

    [Fact]
    public async Task ReadPastTheEndReturnsZero()
    {
        File.WriteAllBytes(_path, Pattern(100));

        await using var device = ManagedBlockDevice.Open(_path, FileAccess.Read, FileShare.Read);

        int read = await device.ReadAsync(new byte[4096], 100, TestContext.Current.CancellationToken);

        Assert.Equal(0, read);
    }

    [Fact]
    public async Task WritesAndReadsBackAtExplicitOffsets()
    {
        await using (var device = ManagedBlockDevice.Open(_path, FileAccess.ReadWrite, FileShare.None))
        {
            // Written out of order on purpose: offsets are explicit, so completion
            // order must not affect file content.
            await device.WriteAsync(Pattern(512), 512, TestContext.Current.CancellationToken);
            await device.WriteAsync(Pattern(512), 0, TestContext.Current.CancellationToken);
            await device.FlushAsync(toDisk: false, TestContext.Current.CancellationToken);
        }

        var written = File.ReadAllBytes(_path);
        Assert.Equal(1024, written.Length);
        Assert.Equal(Pattern(512), written[..512]);
        Assert.Equal(Pattern(512), written[512..]);
    }

    [Fact]
    public async Task FlushToDiskSucceeds()
    {
        await using var device = ManagedBlockDevice.Open(_path, FileAccess.ReadWrite, FileShare.None);
        await device.WriteAsync(Pattern(64), 0, TestContext.Current.CancellationToken);

        await device.FlushAsync(toDisk: true, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SetLengthTruncatesTheFile()
    {
        await using (var device = ManagedBlockDevice.Open(_path, FileAccess.ReadWrite, FileShare.None))
        {
            await device.WriteAsync(Pattern(4096), 0, TestContext.Current.CancellationToken);
            await device.SetLengthAsync(1000, TestContext.Current.CancellationToken);
            Assert.Equal(1000, device.Length);
        }

        Assert.Equal(1000, new FileInfo(_path).Length);
    }

    [Fact]
    public void PortableBackendNeedsNoAlignmentOrTailPadding()
    {
        File.WriteAllBytes(_path, Pattern(16));
        using var device = ManagedBlockDevice.Open(_path, FileAccess.Read, FileShare.Read);

        Assert.Equal(1, device.Alignment);
        Assert.False(device.RequiresTailPadding);
    }

    [Fact]
    public async Task AllocatesASlabWithTheRequestedGeometry()
    {
        File.WriteAllBytes(_path, Pattern(16));
        await using var device = ManagedBlockDevice.Open(_path, FileAccess.Read, FileShare.Read);

        using var slab = device.AllocateSlab(3, 4096);

        Assert.Equal(3, slab.BlockCount);
        Assert.Equal(4096, slab.BlockSize);
    }

    [Fact]
    public void MissingFileRaisesAnPunchIoExceptionWithStatus35()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.bin");

        var ex = Assert.Throws<PunchIoException>(
            () => ManagedBlockDevice.Open(missing, FileAccess.Read, FileShare.Read));

        Assert.Equal(FileStatus.FileNotFound, ex.Status);
    }
}
```

> `TestContext.Current.CancellationToken` is the xUnit v3 idiom for a test-scoped token; use it rather than `CancellationToken.None` so a hung I/O test fails instead of stalling the run.

- [ ] **Step 2: Run and confirm failure**

```bash
dotnet test --filter "FullyQualifiedName~ManagedBlockDeviceTests"
```

Expected: FAIL — `ManagedBlockDevice` not found.

- [ ] **Step 3: Implement `IBlockDevice`**

`src/PunchIO.Core/Devices/IBlockDevice.cs`:

```csharp
using PunchIO.Buffers;

namespace PunchIO.Devices;

/// <summary>
/// The storage seam beneath the block pump: opens a file, allocates buffers for
/// it, and moves blocks to and from explicit offsets.
/// </summary>
/// <remarks>
/// Implementations hide every platform-specific constraint. Nothing above this
/// interface knows about sector alignment or unbuffered I/O.
/// </remarks>
public interface IBlockDevice : IAsyncDisposable, IDisposable
{
    /// <summary>The file's current logical length in bytes.</summary>
    long Length { get; }

    /// <summary>
    /// The byte boundary that offsets, lengths, and buffer addresses must be
    /// multiples of. <c>1</c> when the device imposes no alignment.
    /// </summary>
    int Alignment { get; }

    /// <summary>
    /// <see langword="true"/> when a final short block must be padded up to
    /// <see cref="Alignment"/> on write and the file then truncated to its true
    /// length with <see cref="SetLengthAsync"/>.
    /// </summary>
    bool RequiresTailPadding { get; }

    /// <summary>Allocates buffers meeting this device's alignment requirement.</summary>
    /// <param name="blockCount">The number of blocks.</param>
    /// <param name="blockSize">The size of each block in bytes.</param>
    /// <returns>The allocated slab.</returns>
    IBlockSlab AllocateSlab(int blockCount, int blockSize);

    /// <summary>Reads bytes available at <paramref name="fileOffset"/>.</summary>
    /// <param name="destination">The buffer to fill.</param>
    /// <param name="fileOffset">The absolute file offset to read from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// The number of bytes read, never exceeding the logical bytes remaining at
    /// <paramref name="fileOffset"/>, and <c>0</c> only at end of file.
    /// </returns>
    ValueTask<int> ReadAsync(Memory<byte> destination, long fileOffset, CancellationToken cancellationToken);

    /// <summary>Writes bytes at <paramref name="fileOffset"/>.</summary>
    /// <param name="source">The bytes to write.</param>
    /// <param name="fileOffset">The absolute file offset to write at.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the write has been accepted.</returns>
    ValueTask WriteAsync(ReadOnlyMemory<byte> source, long fileOffset, CancellationToken cancellationToken);

    /// <summary>Flushes buffered data.</summary>
    /// <param name="toDisk">
    /// When <see langword="true"/>, forces data to stable media rather than
    /// merely handing it to the operating system.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the flush has finished.</returns>
    ValueTask FlushAsync(bool toDisk, CancellationToken cancellationToken);

    /// <summary>Sets the file's logical length.</summary>
    /// <param name="length">The new length in bytes.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the length has been set.</returns>
    ValueTask SetLengthAsync(long length, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Implement `NativeFileOps`**

`src/PunchIO.Core/Devices/Interop/NativeFileOps.cs`:

```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PunchIO.Devices.Interop;

/// <summary>
/// The two file operations with no portable managed equivalent: forcing data to
/// stable media, and setting a file's length through a handle.
/// </summary>
/// <remarks>
/// Uses source-generated interop so the library remains NativeAOT-safe.
/// </remarks>
internal static partial class NativeFileOps
{
    /// <summary>Forces the file's data to stable media.</summary>
    /// <param name="handle">An open file handle with write access.</param>
    /// <exception cref="PunchIoException">The platform call failed.</exception>
    public static void FlushToDisk(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!FlushFileBuffers(handle))
                throw Failure("Failed to flush file buffers to disk.");
        }
        else
        {
            if (Fsync((int)handle.DangerousGetHandle()) != 0)
                throw Failure("Failed to fsync the file.");
        }
    }

    /// <summary>Sets the file's logical length.</summary>
    /// <param name="handle">An open file handle with write access.</param>
    /// <param name="length">The new length in bytes.</param>
    /// <exception cref="PunchIoException">The platform call failed.</exception>
    public static void SetLength(SafeFileHandle handle, long length)
    {
        if (OperatingSystem.IsWindows())
        {
            // FileEndOfFileInfo takes an explicit offset, unlike SetEndOfFile,
            // which truncates at the handle's file pointer -- meaningless on an
            // overlapped handle.
            long endOfFile = length;
            if (!SetFileInformationByHandle(handle, FileEndOfFileInfo, ref endOfFile, sizeof(long)))
                throw Failure($"Failed to set the file length to {length} bytes.");
        }
        else
        {
            if (Ftruncate((int)handle.DangerousGetHandle(), length) != 0)
                throw Failure($"Failed to truncate the file to {length} bytes.");
        }
    }

    private const int FileEndOfFileInfo = 6;

    private static PunchIoException Failure(string message) =>
        new(message, FileStatus.PermanentError, Marshal.GetExceptionForHR(
            Marshal.GetHRForLastWin32Error()));

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeFileHandle handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(
        SafeFileHandle handle, int fileInformationClass, ref long fileInformation, uint bufferSize);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fileDescriptor);

    [LibraryImport("libc", EntryPoint = "ftruncate", SetLastError = true)]
    private static partial int Ftruncate(int fileDescriptor, long length);
}
```

- [ ] **Step 5: Implement `ManagedBlockDevice`**

`src/PunchIO.Core/Devices/ManagedBlockDevice.cs`:

```csharp
using PunchIO.Buffers;
using PunchIO.Devices.Interop;
using Microsoft.Win32.SafeHandles;

namespace PunchIO.Devices;

/// <summary>
/// The portable block device. Issues genuine overlapped reads and writes through
/// <see cref="RandomAccess"/> on a handle bound to the thread pool's completion
/// port, on every supported platform.
/// </summary>
public sealed class ManagedBlockDevice : IBlockDevice
{
    private readonly SafeFileHandle _handle;
    private long _length;

    private ManagedBlockDevice(SafeFileHandle handle)
    {
        _handle = handle;
        _length = RandomAccess.GetLength(handle);
    }

    /// <summary>Opens a file.</summary>
    /// <param name="path">The file to open.</param>
    /// <param name="access">The access required.</param>
    /// <param name="share">The sharing mode.</param>
    /// <returns>An open device.</returns>
    /// <exception cref="PunchIoException">The file could not be opened.</exception>
    public static ManagedBlockDevice Open(string path, FileAccess access, FileShare share)
    {
        ArgumentNullException.ThrowIfNull(path);

        FileMode mode = access == FileAccess.Read ? FileMode.Open : FileMode.OpenOrCreate;

        try
        {
            var handle = File.OpenHandle(
                path, mode, access, share,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            return new ManagedBlockDevice(handle);
        }
        catch (FileNotFoundException ex)
        {
            throw new PunchIoException($"File not found: {path}", FileStatus.FileNotFound, ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new PunchIoException($"Directory not found for: {path}", FileStatus.FileNotFound, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new PunchIoException($"Access denied opening: {path}", FileStatus.AttributeMismatch, ex);
        }
    }

    /// <inheritdoc />
    public long Length => _length;

    /// <inheritdoc />
    public int Alignment => 1;

    /// <inheritdoc />
    public bool RequiresTailPadding => false;

    /// <inheritdoc />
    public IBlockSlab AllocateSlab(int blockCount, int blockSize) =>
        new PinnedArraySlab(blockCount, blockSize);

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(
        Memory<byte> destination, long fileOffset, CancellationToken cancellationToken) =>
        RandomAccess.ReadAsync(_handle, destination, fileOffset, cancellationToken);

    /// <inheritdoc />
    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> source, long fileOffset, CancellationToken cancellationToken)
    {
        await RandomAccess.WriteAsync(_handle, source, fileOffset, cancellationToken)
            .ConfigureAwait(false);

        long end = fileOffset + source.Length;
        if (end > _length) _length = end;
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(bool toDisk, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Writes went straight to the handle, so there is nothing of ours to
        // flush; only the media-durability request needs a platform call.
        if (toDisk) NativeFileOps.FlushToDisk(_handle);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask SetLengthAsync(long length, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NativeFileOps.SetLength(_handle, length);
        _length = length;
        return ValueTask.CompletedTask;
    }

    /// <summary>Closes the file handle.</summary>
    public void Dispose() => _handle.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

**Why `ReadAsync` forwards directly with no loop:** a short read is legitimate here and the *pump* owns re-issuing for the remainder (Task 13). Putting a fill loop in the device would hide short reads from the pump's tests, which is precisely the behavior the fake device in Task 13 exists to simulate.

- [ ] **Step 6: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~ManagedBlockDeviceTests"
```

Expected: PASS on Windows and Linux.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add block device seam and portable RandomAccess backend"
```
