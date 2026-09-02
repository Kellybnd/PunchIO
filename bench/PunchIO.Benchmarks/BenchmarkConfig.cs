using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace PunchIO.Benchmarks;

/// <summary>
/// A short in-process job for the benchmarks that do no I/O.
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
/// slightly noisier than an isolated run. That is acceptable for numbers that
/// differ by large multiples.
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

/// <summary>
/// The job for benchmarks that move a multi-gigabyte file each invocation.
/// </summary>
/// <remarks>
/// <para>
/// One invocation per iteration, because an iteration setup evicts the file
/// from the cache or deletes the previous output and that has to happen before
/// every single pass. One warm-up pass is enough to tier up a loop that runs for
/// seconds, and five measured passes give a usable spread without turning the
/// full suite into an afternoon: at 4 GiB a pass is one to several seconds.
/// </para>
/// <para>
/// Benchmarks are grouped by category so that each format can carry its own
/// .NET baseline, and the <see cref="ThroughputColumn"/> turns the mean into
/// bytes per second, which is the figure a disk benchmark is read for.
/// </para>
/// </remarks>
public sealed class LargeFileConfig : ManualConfig
{
    public LargeFileConfig()
    {
        AddJob(Job.Default
            .WithToolchain(InProcessEmitToolchain.Instance)
            .WithWarmupCount(1)
            .WithIterationCount(5)
            .WithInvocationCount(1)
            .WithUnrollFactor(1)
            .WithId("LargeFile"));
        AddDiagnoser(MemoryDiagnoser.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddColumn(ThroughputColumn.Default);
        AddLogicalGroupRules(BenchmarkLogicalGroupRule.ByCategory);
        AddLogger(ConsoleLogger.Default);
        AddExporter(MarkdownExporter.GitHub);
        WithOptions(ConfigOptions.DisableLogFile);
    }
}
