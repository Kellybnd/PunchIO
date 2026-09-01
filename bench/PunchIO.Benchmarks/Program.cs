using BenchmarkDotNet.Running;

// Run everything with `dotnet run -c Release`, or a subset with
// `dotnet run -c Release -- --filter *Framing*`.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>Entry point marker for <see cref="BenchmarkSwitcher"/>.</summary>
public partial class Program;
