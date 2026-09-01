# PunchIO.Cobol

An external file handler (EXFH) boundary over
[PunchIO.Core](https://www.nuget.org/packages/PunchIO.Core), callable from
managed COBOL.

```csharp
using var exfh = new Exfh(profileResolver);

int rc = exfh.Execute(opcode, fcd, recordArea);
```

## How it works

Micro Focus and Fujitsu opcodes are normalised into one `FileOperation` enum, so
a single dispatcher serves both and each dialect contributes only a thin adapter.
Open files live in a handle table keyed by an integer the COBOL program carries
in its control block.

A file's record format comes from a **configured profile resolved by name**, not
from the control block. A file's format is a deployment fact, and this keeps the
handler independent of control-block fields that vary between runtimes.

## Two things that matter

**Nothing throws across the boundary.** Every failure becomes a COBOL file
status. This is the last managed frame before a native COBOL runtime, where an
escaping exception ends the process rather than being caught.

**The synchronous bridge does not cost the asynchronous advantage.** A COBOL
`READ` must return a record before it returns, so it blocks — but the throughput
comes from readahead depth, not from the caller being asynchronous. With blocks
already in flight, a read almost always finds its record in a filled buffer and
never suspends.

## Adapting to your runtime

Control-block layouts and opcode values differ between COBOL runtimes and
product generations. `FcdLayout` and `ExfhOpcodes` hold every byte offset and
opcode value in one place each, with no logic attached, so matching a specific
runtime is a change to those two declarations and nothing else — nothing above
`FcdView` reads a raw offset. Check both against your runtime's header when
integrating.

## Licence

MIT. Micro Focus, Fujitsu and NetCOBOL are trademarks of their respective
owners; this project is not affiliated with or endorsed by any of them, and
those names appear only to describe the formats and interfaces it supports.
