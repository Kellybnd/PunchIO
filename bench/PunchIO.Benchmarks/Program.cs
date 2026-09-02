using BenchmarkDotNet.Running;

// Run everything with `dotnet run -c Release`, or a subset with
// `dotnet run -c Release -- --filter *Framing*`.
//
// Every I/O benchmark moves a 4 GiB file per pass; set PUNCHIO_BENCH_SIZE_MIB to
// change that. The data files are generated once into the temp directory and
// reused across runs.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>Entry point marker for <see cref="BenchmarkSwitcher"/>.</summary>
public partial class Program;
