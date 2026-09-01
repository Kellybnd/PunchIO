using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace PunchIO.Benchmarks;

/// <summary>
/// A short in-process job.
/// </summary>
/// <remarks>
/// <para>
/// In-process rather than the default isolated toolchain because the repository
/// multi-targets: BenchmarkDotNet's auto-generated boilerplate project inherits
/// <c>Directory.Build.props</c>, picks up <c>net8.0</c> alongside <c>net10.0</c>,
/// and then cannot reference this single-framework project. Running in process
/// sidesteps generating that project at all.
/// </para>
/// <para>
/// The trade-off is that measurements share a process with the host, so they are
/// slightly noisier than an isolated run. That is acceptable for choosing between
/// queue depths and block sizes, which differ by far more than the noise.
/// </para>
/// </remarks>
public sealed class ShortInProcessConfig : ManualConfig
{
    public ShortInProcessConfig()
    {
        AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
        AddDiagnoser(MemoryDiagnoser.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddLogger(ConsoleLogger.Default);
        AddExporter(MarkdownExporter.GitHub);
        WithOptions(ConfigOptions.DisableLogFile);
    }
}
