using PunchIO.Devices;
using PunchIO.Files;
using PunchIO.Framing;
using PunchIO.Pump;
using PunchIO.Readers;
using PunchIO.Writers;

namespace PunchIO;

/// <summary>
/// Opens record files for reading and writing. This is the entry point for
/// ordinary use; the device, pump, framing and reader layers underneath are
/// public so they can be composed directly or extended with a custom format, but
/// nothing here requires that.
/// </summary>
/// <example>
/// <code>
/// await using var reader = RecordFile.OpenVariableRead(
///     path, VariableRecordDescriptor.Fujitsu);
///
/// await foreach (var record in reader.ReadAllAsync(cancellationToken))
///     Process(record.Span);   // valid until the next iteration
/// </code>
/// </example>
public static class RecordFile
{
    // ---- fixed-length records -------------------------------------------

    /// <summary>Opens a file of fixed-length records for reading.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="recordLength">The record length in bytes.</param>
    /// <param name="options">I/O options, or <see langword="null"/> for the defaults.</param>
    /// <param name="trailing">How to treat a short final record.</param>
    /// <returns>A reader positioned before the first record.</returns>
    public static SequentialReader<FixedBlockFramer> OpenFixedBlockRead(
        string path,
        int recordLength,
        FileIoOptions? options = null,
        TrailingPartialRecord trailing = TrailingPartialRecord.Strict)
    {
        options ??= FileIoOptions.Default;
        options.Validate();

        return OpenRead(
            path, options, recordLength, new FixedBlockFramer(recordLength, trailing));
    }

    /// <summary>Creates or truncates a file of fixed-length records for writing.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="recordLength">The record length in bytes.</param>
    /// <param name="options">I/O options, or <see langword="null"/> for the defaults.</param>
    /// <param name="padByte">
    /// The byte used to pad a short record. Defaults to an ASCII space, matching
    /// COBOL behavior; pass zero for binary files.
    /// </param>
    /// <returns>A writer ready to accept records.</returns>
    public static SequentialWriter<FixedBlockEncoder> CreateFixedBlockWrite(
        string path,
        int recordLength,
        FileIoOptions? options = null,
        byte padByte = 0x20)
    {
        options ??= FileIoOptions.Default;
        options.Validate();

        return CreateWrite(
            path, options, recordLength, new FixedBlockEncoder(recordLength, padByte));
    }

    // ---- line sequential -------------------------------------------------

    /// <summary>Opens a line-sequential file for reading.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="lineOptions">
    /// Line-sequential behavior, or <see langword="null"/> for the defaults.
    /// </param>
    /// <param name="options">I/O options, or <see langword="null"/> for the defaults.</param>
    /// <returns>A reader positioned before the first record.</returns>
    public static LineSequentialReader OpenLineSequentialRead(
        string path,
        LineSequentialOptions? lineOptions = null,
        FileIoOptions? options = null)
    {
        lineOptions ??= new LineSequentialOptions();
        options ??= FileIoOptions.Default;
        options.Validate();

        var inner = OpenRead(path, options, recordLength: null, new LineSequentialFramer(lineOptions));

        return new LineSequentialReader(inner, lineOptions);
    }

    /// <summary>Creates or truncates a line-sequential file for writing.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="lineOptions">
    /// Line-sequential behavior, or <see langword="null"/> for the defaults.
    /// </param>
    /// <param name="options">I/O options, or <see langword="null"/> for the defaults.</param>
    /// <returns>A writer ready to accept records.</returns>
    public static SequentialWriter<LineSequentialEncoder> CreateLineSequentialWrite(
        string path,
        LineSequentialOptions? lineOptions = null,
        FileIoOptions? options = null)
    {
        lineOptions ??= new LineSequentialOptions();
        options ??= FileIoOptions.Default;
        options.Validate();

        return CreateWrite(
            path, options, recordLength: null, new LineSequentialEncoder(lineOptions));
    }

    // ---- variable-length records -----------------------------------------

    /// <summary>Opens a file of variable-length records for reading.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="descriptor">
    /// The on-disk layout. Use <see cref="VariableRecordDescriptor.Fujitsu"/> or
    /// <see cref="VariableRecordDescriptor.MicroFocus"/>, optionally customised
    /// with a <c>with</c> expression.
    /// </param>
    /// <param name="options">I/O options, or <see langword="null"/> for the defaults.</param>
    /// <returns>A reader positioned before the first record.</returns>
    public static SequentialReader<VariableRecordFramer> OpenVariableRead(
        string path,
        VariableRecordDescriptor descriptor,
        FileIoOptions? options = null)
    {
        options ??= FileIoOptions.Default;
        options.Validate();

        return OpenRead(
            path, options, recordLength: null, new VariableRecordFramer(descriptor));
    }

    /// <summary>Creates or truncates a file of variable-length records for writing.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="descriptor">The on-disk layout to write.</param>
    /// <param name="options">I/O options, or <see langword="null"/> for the defaults.</param>
    /// <returns>A writer ready to accept records.</returns>
    public static SequentialWriter<VariableRecordEncoder> CreateVariableWrite(
        string path,
        VariableRecordDescriptor descriptor,
        FileIoOptions? options = null)
    {
        options ??= FileIoOptions.Default;
        options.Validate();

        return CreateWrite(
            path, options, recordLength: null, new VariableRecordEncoder(descriptor));
    }

    // ---- relative and random access ---------------------------------------

    /// <summary>Opens a file of fixed-length records addressed by record number.</summary>
    /// <param name="path">The file to open.</param>
    /// <param name="layout">The slot layout.</param>
    /// <param name="access">The access required.</param>
    /// <param name="options">I/O options, or <see langword="null"/> for the defaults.</param>
    /// <returns>An open relative file.</returns>
    public static RelativeFile OpenRelative(
        string path,
        RelativeFileOptions layout,
        FileAccess access,
        FileIoOptions? options = null) =>
        RelativeFile.Open(path, layout, access, options);

    /// <summary>Opens a file for byte-offset access, without readahead.</summary>
    /// <param name="path">The file to open.</param>
    /// <param name="access">The access required.</param>
    /// <param name="options">I/O options, or <see langword="null"/> for the defaults.</param>
    /// <returns>An open file.</returns>
    public static RandomAccessFile OpenRandomAccess(
        string path, FileAccess access, FileIoOptions? options = null) =>
        RandomAccessFile.Open(path, access, options);

    // ---- composition ------------------------------------------------------

    private static SequentialReader<TFramer> OpenRead<TFramer>(
        string path, FileIoOptions options, int? recordLength, TFramer framer)
        where TFramer : struct, IRecordFramer
    {
        var device = BlockDeviceFactory.Open(
            path, FileMode.Open, FileAccess.Read, options.Share, options.Backend);

        try
        {
            var source = BlockSource.Create(
                device, options.QueueDepth, options.ResolveBlockSize(device.Alignment, recordLength));

            return SequentialReader<TFramer>.Create(source, framer, options.MaxRecordLength);
        }
        catch
        {
            // The pump owns the device once it exists; until then this does.
            device.Dispose();
            throw;
        }
    }

    private static SequentialWriter<TEncoder> CreateWrite<TEncoder>(
        string path, FileIoOptions options, int? recordLength, TEncoder encoder)
        where TEncoder : struct, IRecordEncoder
    {
        // Create rather than OpenOrCreate: writing a shorter file over a longer
        // one would otherwise leave the old tail in place.
        var device = BlockDeviceFactory.Open(
            path, FileMode.Create, FileAccess.ReadWrite, options.Share, options.Backend);

        try
        {
            var sink = BlockSink.Create(
                device, options.QueueDepth, options.ResolveBlockSize(device.Alignment, recordLength));

            return SequentialWriter<TEncoder>.Create(sink, encoder);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }
}
