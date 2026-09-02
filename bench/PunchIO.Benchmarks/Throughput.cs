using System.Reflection;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace PunchIO.Benchmarks;

/// <summary>
/// Declares which data set a benchmark moves in one invocation, so
/// <see cref="ThroughputColumn"/> can report it in bytes per second.
/// </summary>
/// <param name="data">The data set moved.</param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TransfersAttribute(BenchmarkData data) : Attribute
{
    /// <summary>The data set moved by one invocation.</summary>
    public BenchmarkData Data { get; } = data;
}

/// <summary>
/// Reports each benchmark's mean time as throughput, in decimal gigabytes per
/// second, for benchmarks that carry a <see cref="TransfersAttribute"/>.
/// </summary>
public sealed class ThroughputColumn : IColumn
{
    /// <summary>The shared instance.</summary>
    public static readonly ThroughputColumn Default = new();

    /// <inheritdoc />
    public string Id => nameof(ThroughputColumn);

    /// <inheritdoc />
    public string ColumnName => "GB/s";

    /// <inheritdoc />
    public bool AlwaysShow => true;

    /// <inheritdoc />
    public ColumnCategory Category => ColumnCategory.Custom;

    /// <inheritdoc />
    public int PriorityInCategory => 0;

    /// <inheritdoc />
    public bool IsNumeric => true;

    /// <inheritdoc />
    public UnitType UnitType => UnitType.Dimensionless;

    /// <inheritdoc />
    public string Legend => "Bytes moved per second of mean time, in decimal gigabytes";

    /// <inheritdoc />
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) =>
        GetValue(summary, benchmarkCase, SummaryStyle.Default);

    /// <inheritdoc />
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        var transfers = benchmarkCase.Descriptor.WorkloadMethod.GetCustomAttribute<TransfersAttribute>();
        double? mean = summary[benchmarkCase]?.ResultStatistics?.Mean;

        if (transfers is null || mean is not > 0) return "-";

        // The mean is in nanoseconds, so bytes per nanosecond is exactly decimal
        // gigabytes per second.
        double gigabytesPerSecond = BenchmarkFile.BytesOf(transfers.Data) / mean.Value;

        return gigabytesPerSecond.ToString("F2", style.CultureInfo);
    }

    /// <inheritdoc />
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    /// <inheritdoc />
    public bool IsAvailable(Summary summary) => true;

    /// <inheritdoc />
    public override string ToString() => ColumnName;
}
