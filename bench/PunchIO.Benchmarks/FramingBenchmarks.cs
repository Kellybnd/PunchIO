using System.Text;
using PunchIO.Framing;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace PunchIO.Benchmarks;

/// <summary>
/// Per-record framing cost with no I/O involved. These are the numbers the
/// spec's "under 20 ns per record" target refers to, and the only ones in the
/// suite that are unaffected by the operating system's cache.
/// </summary>
[Config(typeof(ShortInProcessConfig))]
public class FramingBenchmarks
{
    private const int Records = 10_000;

    private byte[] _fixedBlock = [];
    private byte[] _lineSequential = [];
    private byte[] _fujitsu = [];
    private byte[] _microFocus = [];

    private FixedBlockFramer _fixedFramer;
    private LineSequentialFramer _lineFramer;
    private VariableRecordFramer _fujitsuFramer;
    private VariableRecordFramer _microFocusFramer;

    /// <summary>The record body length in bytes.</summary>
    [Params(80, 400)]
    public int RecordLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var body = Encoding.ASCII.GetBytes(new string('X', RecordLength));

        _fixedFramer = new FixedBlockFramer(RecordLength);
        _lineFramer = new LineSequentialFramer(new LineSequentialOptions());
        _fujitsuFramer = new VariableRecordFramer(VariableRecordDescriptor.Fujitsu);
        _microFocusFramer = new VariableRecordFramer(VariableRecordDescriptor.MicroFocus());

        _fixedBlock = Repeat(body, Records);
        _lineSequential = Repeat([.. body, (byte)'\n'], Records);
        _fujitsu = RepeatFramed(body, Records, VariableRecordDescriptor.Fujitsu);
        _microFocus = RepeatFramed(body, Records, VariableRecordDescriptor.MicroFocus());
    }

    [Benchmark(OperationsPerInvoke = Records, Baseline = true)]
    public long FixedBlock() => FrameAll(_fixedBlock, _fixedFramer);

    [Benchmark(OperationsPerInvoke = Records)]
    public long LineSequential() => FrameAll(_lineSequential, _lineFramer);

    [Benchmark(OperationsPerInvoke = Records)]
    public long VariableFujitsu() => FrameAll(_fujitsu, _fujitsuFramer);

    [Benchmark(OperationsPerInvoke = Records)]
    public long VariableMicroFocus() => FrameAll(_microFocus, _microFocusFramer);

    /// <summary>
    /// Frames every record in <paramref name="data"/>, returning the total body
    /// length so nothing can be optimised away.
    /// </summary>
    private static long FrameAll<TFramer>(byte[] data, TFramer framer)
        where TFramer : struct, IRecordFramer
    {
        var input = data.AsSpan();
        long total = 0;

        while (true)
        {
            var status = framer.TryFrame(
                input, isFinalBlock: true, out int consumed, out _, out int length);

            if (status != FrameStatus.Ok) break;

            total += length;
            input = input[consumed..];
        }

        return total;
    }

    private static byte[] Repeat(byte[] unit, int count)
    {
        var result = new byte[unit.Length * count];

        for (int i = 0; i < count; i++)
            unit.CopyTo(result, i * unit.Length);

        return result;
    }

    private static byte[] RepeatFramed(byte[] body, int count, VariableRecordDescriptor descriptor)
    {
        int framed = VariableRecordFramer.FramedLength(body.Length, descriptor);
        var unit = new byte[framed];

        VariableRecordFramer.WriteFraming(unit, body.Length, descriptor);
        body.CopyTo(unit, descriptor.PrefixBytes);

        return Repeat(unit, count);
    }
}
