using PunchIO;
using PunchIO.Tools;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        punchio-probe — identify the variable-record layout of a file

          punchio-probe <file> [--records N] [--sample-bytes N] [--all]

          --records N        records to frame before stopping (default 500)
          --sample-bytes N   bytes to read from the front of the file (default 1048576)
          --all              list every candidate, not just those that framed something

        Reports which layouts explain the file's opening bytes. A layout that frames
        the whole sample into records of varying length is very unlikely to be doing
        so by accident.
        """);

    return 0;
}

string path = args[0];

if (!File.Exists(path))
{
    Console.Error.WriteLine($"punchio-probe: file not found: {path}");
    return 2;
}

int maxRecords = ReadIntOption("--records", VariableFormatProbe.DefaultMaxRecords);
int sampleBytes = ReadIntOption("--sample-bytes", VariableFormatProbe.DefaultSampleBytes);
bool showAll = args.Contains("--all", StringComparer.Ordinal);

IReadOnlyList<VariableFormatProbeResult> results;

try
{
    results = await VariableFormatProbe.ProbeFileAsync(
        path, candidates: null, sampleBytes, maxRecords);
}
catch (PunchIoException ex)
{
    Console.Error.WriteLine($"punchio-probe: {ex.Message}");
    return 2;
}

var shown = showAll ? results : [.. results.Where(r => r.RecordsFramed > 0)];

Console.WriteLine($"{path}  ({new FileInfo(path).Length:N0} bytes)");
Console.WriteLine();

if (shown.Count == 0)
{
    Console.WriteLine("  No candidate layout framed this file.");
    Console.WriteLine("  It may not be a variable-record file, or it may use a layout not tried here.");
    Console.WriteLine("  Re-run with --all to see why each candidate was rejected.");
    return 1;
}

Console.WriteLine($"  {"Confidence",-10}  {"Records",7}  {"Lengths",7}  {"Empty",6}  Layout");
Console.WriteLine($"  {new string('-', 10)}  {new string('-', 7)}  {new string('-', 7)}  {new string('-', 6)}  {new string('-', 50)}");

foreach (var result in shown)
{
    Console.WriteLine(
        $"  {result.Confidence,-10}  {result.RecordsFramed,7:N0}  " +
        $"{result.DistinctRecordLengths,7:N0}  {result.ZeroLengthRecords,6:N0}  {result.Name}");

    if (result.Rejection is not null)
        Console.WriteLine($"  {"",-10}  {"",7}  {"",7}  {"",6}  ({result.Rejection})");
}

Console.WriteLine();

var best = shown[0];

switch (best.Confidence)
{
    case ProbeConfidence.High:
        Console.WriteLine($"  Best match: {best.Name}");
        Console.WriteLine();
        Console.WriteLine("  " + Descriptor(best));
        return 0;

    case ProbeConfidence.Medium:
        Console.WriteLine($"  Likely: {best.Name}");
        Console.WriteLine("  Every record was the same length, which is also what a fixed-length");
        Console.WriteLine("  file looks like read as variable. Check against a wider sample.");
        return 0;

    default:
        Console.WriteLine("  No layout matched convincingly.");
        Console.WriteLine("  A file dominated by zero bytes frames under any data-only layout, so a");
        Console.WriteLine("  large 'Empty' count means the match is coincidence rather than evidence.");
        return 1;
}

int ReadIntOption(string name, int fallback)
{
    int index = Array.IndexOf(args, name);

    return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int value)
        ? value
        : fallback;
}

static string Descriptor(VariableFormatProbeResult result)
{
    var d = result.Descriptor;

    return $"VariableRecordDescriptor {{ PrefixBytes = {d.PrefixBytes}, " +
           $"SuffixBytes = {d.SuffixBytes}, LengthFieldOffset = {d.LengthFieldOffset}, " +
           $"LengthFieldWidth = {d.LengthFieldWidth}, Endianness = {d.Endianness}, " +
           $"LengthIncludes = {d.LengthIncludes} }}";
}
