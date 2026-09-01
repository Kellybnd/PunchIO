using PunchIO.Files;
using PunchIO.Framing;

namespace PunchIO.Tools;

/// <summary>How well a candidate layout explained the bytes it was tried against.</summary>
public enum ProbeConfidence
{
    /// <summary>The layout did not frame the bytes at all.</summary>
    None,

    /// <summary>
    /// The layout framed something, but too little or too uniform to be
    /// distinguished from coincidence.
    /// </summary>
    Low,

    /// <summary>The layout framed the sample, but every record was the same length.</summary>
    Medium,

    /// <summary>
    /// The layout framed the whole sample into records of varying length, which
    /// is very hard to achieve by accident.
    /// </summary>
    High,
}

/// <summary>A named layout to try against a file.</summary>
/// <param name="Name">A human-readable description of the layout.</param>
/// <param name="Descriptor">The layout itself.</param>
public sealed record VariableFormatCandidate(string Name, VariableRecordDescriptor Descriptor);

/// <summary>What one candidate layout made of a sample.</summary>
public sealed record VariableFormatProbeResult
{
    /// <summary>The candidate's name.</summary>
    public required string Name { get; init; }

    /// <summary>The layout that was tried.</summary>
    public required VariableRecordDescriptor Descriptor { get; init; }

    /// <summary>How many consecutive records framed successfully.</summary>
    public required int RecordsFramed { get; init; }

    /// <summary>How many bytes of the sample those records accounted for.</summary>
    public required long BytesConsumed { get; init; }

    /// <summary>Whether framing ran cleanly to the end of the sample.</summary>
    public required bool ReachedEndOfSample { get; init; }

    /// <summary>
    /// How many distinct record lengths were seen, capped at a small number.
    /// A variable-length file with only one length is suspicious.
    /// </summary>
    public required int DistinctRecordLengths { get; init; }

    /// <summary>
    /// How many of the framed records had no body. A layout dominated by empty
    /// records is almost certainly reading a run of zero bytes as headers.
    /// </summary>
    public required int ZeroLengthRecords { get; init; }

    /// <summary>How much this result should be believed.</summary>
    public required ProbeConfidence Confidence { get; init; }

    /// <summary>Why the layout was rejected, when it was.</summary>
    public string? Rejection { get; init; }
}

/// <summary>
/// Works out which variable-record layout a file actually uses, by trying
/// candidate layouts against its opening bytes and reporting which ones frame it
/// self-consistently.
/// </summary>
/// <remarks>
/// <para>
/// Byte order and length basis are the two aspects of a variable-record layout
/// that vary most between COBOL runtimes. Rather than discover a mismatch
/// against a 400 GB production file, point this at a real one and it reports
/// which interpretation the bytes support.
/// </para>
/// <para>
/// A layout that frames the whole sample into records of varying length is very
/// unlikely to be doing so by accident: every record's declared length has to
/// land exactly on the next record's header, hundreds of times in a row, and for
/// a format with a suffix the trailing length has to agree with the leading one
/// every time.
/// </para>
/// </remarks>
public static class VariableFormatProbe
{
    /// <summary>The default sample size read from the front of a file, in bytes.</summary>
    public const int DefaultSampleBytes = 1024 * 1024;

    /// <summary>The default number of records to frame before stopping.</summary>
    public const int DefaultMaxRecords = 500;

    /// <summary>
    /// The layouts tried by default: the two shipped presets, plus the variations
    /// on endianness and length basis that are the open questions about them.
    /// </summary>
    public static IReadOnlyList<VariableFormatCandidate> DefaultCandidates { get; } =
    [
        new("Fujitsu (4-byte prefix and suffix, little-endian, data-only length)",
            VariableRecordDescriptor.Fujitsu),

        new("Fujitsu, big-endian",
            VariableRecordDescriptor.Fujitsu with { Endianness = Endianness.BigEndian }),

        new("Fujitsu, length includes framing",
            VariableRecordDescriptor.Fujitsu with
            {
                LengthIncludes = LengthBasis.WithPrefixAndSuffix,
            }),

        new("Fujitsu, big-endian, length includes framing",
            VariableRecordDescriptor.Fujitsu with
            {
                Endianness = Endianness.BigEndian,
                LengthIncludes = LengthBasis.WithPrefixAndSuffix,
            }),

        new("Micro Focus (128-byte file header, 2-byte control field)",
            VariableRecordDescriptor.MicroFocus()),

        new("Micro Focus, 4-byte control field (records over 4,095 bytes)",
            VariableRecordDescriptor.MicroFocus(
                MicroFocusFileHeader.MaxDeclarableRecordLength)),

        new("2-byte big-endian prefix, no suffix", Plain(2, Endianness.BigEndian)),

        new("2-byte little-endian prefix, no suffix", Plain(2, Endianness.LittleEndian)),

        new("4-byte big-endian prefix, no suffix", Plain(4, Endianness.BigEndian)),

        new("4-byte little-endian prefix, no suffix", Plain(4, Endianness.LittleEndian)),
    ];

    /// <summary>
    /// A bare length-prefixed layout: no suffix, no status bits, no file header
    /// and no padding. These catch the many in-house formats that are simply a
    /// length followed by its bytes.
    /// </summary>
    private static VariableRecordDescriptor Plain(int prefixBytes, Endianness endianness) => new()
    {
        PrefixBytes = prefixBytes,
        SuffixBytes = 0,
        LengthFieldOffset = 0,
        LengthFieldWidth = prefixBytes,
        StatusBits = 0,
        DataRecordStatus = 0,
        FlagByteOffset = -1,
        Endianness = endianness,
        LengthIncludes = LengthBasis.DataOnly,
        ValidateSuffix = false,
        ValidateReservedBytes = false,
        Alignment = 1,
        FileHeader = VariableFileHeader.None,
    };

    /// <summary>Reads the front of a file and reports what each candidate made of it.</summary>
    /// <param name="path">The file to inspect.</param>
    /// <param name="candidates">
    /// The layouts to try, or <see langword="null"/> for <see cref="DefaultCandidates"/>.
    /// </param>
    /// <param name="sampleBytes">How many bytes to read from the front of the file.</param>
    /// <param name="maxRecords">How many records to frame before stopping.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// Every candidate's result, best first: highest confidence, then most records
    /// framed.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="PunchIoException">The file could not be read.</exception>
    public static async ValueTask<IReadOnlyList<VariableFormatProbeResult>> ProbeFileAsync(
        string path,
        IEnumerable<VariableFormatCandidate>? candidates = null,
        int sampleBytes = DefaultSampleBytes,
        int maxRecords = DefaultMaxRecords,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecords);

        await using var file = RandomAccessFile.Open(path, FileAccess.Read);

        int wanted = (int)Math.Min(sampleBytes, file.Length);
        var sample = new byte[wanted];
        int filled = 0;

        while (filled < wanted)
        {
            int read = await file.ReadAsync(sample.AsMemory(filled), filled, cancellationToken)
                .ConfigureAwait(false);

            if (read == 0) break;

            filled += read;
        }

        return Probe(sample.AsSpan(0, filled), candidates, maxRecords);
    }

    /// <summary>Reports what each candidate made of a sample already in memory.</summary>
    /// <param name="sample">The opening bytes of a file, starting at a record boundary.</param>
    /// <param name="candidates">
    /// The layouts to try, or <see langword="null"/> for <see cref="DefaultCandidates"/>.
    /// </param>
    /// <param name="maxRecords">How many records to frame before stopping.</param>
    /// <returns>Every candidate's result, best first.</returns>
    public static IReadOnlyList<VariableFormatProbeResult> Probe(
        ReadOnlySpan<byte> sample,
        IEnumerable<VariableFormatCandidate>? candidates = null,
        int maxRecords = DefaultMaxRecords)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecords);

        var results = new List<VariableFormatProbeResult>();

        foreach (var candidate in candidates ?? DefaultCandidates)
            results.Add(Evaluate(sample, candidate, maxRecords));

        // The candidates all explain the same bytes, so they can be judged
        // against each other rather than only against an absolute bar. A layout
        // that finds a quarter of the structure the leader found is not a rival
        // reading of the file; it is noise that happened not to crash.
        // Only credible candidates set the bar. A layout already discounted for
        // being dominated by empty records can rack up hundreds of phantom
        // records against a zero-filled file, and letting that set the leader
        // would demote the correct answer.
        int leader = 0;
        foreach (var result in results)
        {
            if (result.Confidence > ProbeConfidence.Low && result.RecordsFramed > leader)
                leader = result.RecordsFramed;
        }

        for (int i = 0; i < results.Count; i++)
        {
            var result = results[i];

            if (result.Confidence > ProbeConfidence.Low && result.RecordsFramed * 4 < leader)
                results[i] = result with { Confidence = ProbeConfidence.Low };
        }

        results.Sort(static (a, b) =>
        {
            int byConfidence = b.Confidence.CompareTo(a.Confidence);
            if (byConfidence != 0) return byConfidence;

            int byRecords = b.RecordsFramed.CompareTo(a.RecordsFramed);
            return byRecords != 0 ? byRecords : b.BytesConsumed.CompareTo(a.BytesConsumed);
        });

        return results;
    }

    /// <summary>Tries one candidate against a sample.</summary>
    /// <param name="sample">The opening bytes of a file.</param>
    /// <param name="candidate">The layout to try.</param>
    /// <param name="maxRecords">How many records to frame before stopping.</param>
    /// <returns>What the candidate made of the sample.</returns>
    public static VariableFormatProbeResult Evaluate(
        ReadOnlySpan<byte> sample, VariableFormatCandidate candidate, int maxRecords)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        VariableRecordFramer framer;

        try
        {
            framer = new VariableRecordFramer(candidate.Descriptor);
        }
        catch (ArgumentException ex)
        {
            return Rejected(candidate, $"the layout is not self-consistent: {ex.Message}");
        }

        var lengths = new HashSet<int>();
        int zeroLengthRecords = 0;
        int largestRecord = 0;
        int records = 0;
        int offset = 0;
        bool reachedEnd = false;
        string? rejection = null;

        while (records < maxRecords)
        {
            // isFinalBlock is false throughout: the sample is a prefix of the
            // file, so running out of bytes mid-record means the sample ended,
            // not that the layout is wrong.
            var status = framer.TryFrame(
                sample[offset..], isFinalBlock: false,
                out int consumed, out _, out int length);

            if (status == FrameStatus.Skip)
            {
                // A file header or a deleted record: real structure, but not a
                // record the caller would see, so it does not count as one.
                offset += consumed;
                continue;
            }

            if (status == FrameStatus.Ok)
            {
                records++;
                offset += consumed;

                if (length == 0) zeroLengthRecords++;
                if (length > largestRecord) largestRecord = length;
                if (lengths.Count < 64) lengths.Add(length);
                continue;
            }

            if (status == FrameStatus.Invalid)
            {
                rejection = records == 0
                    ? "the first record's header does not describe a valid record"
                    : $"framing broke after {records} record(s), at byte {offset}";
            }
            else
            {
                // Out of bytes mid-record. That is the expected way a sample
                // ends, so it counts as having reached the end when what is
                // left over is no larger than one more record would be.
                int leftover = sample.Length - offset;
                int plausibleRecord =
                    largestRecord + candidate.Descriptor.PrefixBytes + candidate.Descriptor.SuffixBytes;

                reachedEnd = leftover <= plausibleRecord;
            }

            break;
        }

        return new VariableFormatProbeResult
        {
            Name = candidate.Name,
            Descriptor = candidate.Descriptor,
            RecordsFramed = records,
            BytesConsumed = offset,
            ReachedEndOfSample = reachedEnd,
            DistinctRecordLengths = lengths.Count,
            ZeroLengthRecords = zeroLengthRecords,
            Rejection = rejection,
            Confidence = Score(records, maxRecords, lengths, zeroLengthRecords, reachedEnd, rejection),
        };
    }

    private static ProbeConfidence Score(
        int records,
        int maxRecords,
        HashSet<int> lengths,
        int zeroLengthRecords,
        bool reachedEnd,
        string? rejection)
    {
        if (rejection is not null || records == 0) return ProbeConfidence.None;

        // A run of zero bytes frames perfectly under any data-only layout: every
        // header reads as length zero and the walk marches straight through. This
        // is the probe's one real false positive, and it does not only occur in
        // files that are entirely zeros -- a wrong layout applied to records with
        // zero-filled bodies produces a mixture of real-looking lengths and a
        // great many empty records. Being dominated by empty records is the
        // signal, so that is what is tested rather than the pure case alone.
        if (zeroLengthRecords * 2 > records) return ProbeConfidence.Low;

        if (records < 4) return ProbeConfidence.Low;

        // A wrong layout wanders: it frames plausible-looking garbage until it
        // trips over a byte it cannot explain or the sample runs out. Merely not
        // crashing is not evidence, so accounting for the sample -- either by
        // framing every record asked for, or by reaching its end -- is a
        // prerequisite for any confidence above Low.
        if (records < maxRecords && !reachedEnd) return ProbeConfidence.Low;

        // Every record the same length is what a fixed-length file looks like
        // when read as variable, so it is not evidence for this layout either.
        return lengths.Count == 1 ? ProbeConfidence.Medium : ProbeConfidence.High;
    }

    private static VariableFormatProbeResult Rejected(
        VariableFormatCandidate candidate, string reason) =>
        new()
        {
            Name = candidate.Name,
            Descriptor = candidate.Descriptor,
            RecordsFramed = 0,
            BytesConsumed = 0,
            ReachedEndOfSample = false,
            DistinctRecordLengths = 0,
            ZeroLengthRecords = 0,
            Confidence = ProbeConfidence.None,
            Rejection = reason,
        };
}
