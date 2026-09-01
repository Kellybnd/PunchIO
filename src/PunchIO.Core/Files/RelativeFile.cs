using System.Runtime.CompilerServices;
using PunchIO.Devices;
using PunchIO.Framing;
using PunchIO.Pump;
using PunchIO.Readers;

namespace PunchIO.Files;

/// <summary>The layout of a relative file's slots.</summary>
public sealed class RelativeFileOptions
{
    /// <summary>The record length in bytes. Required.</summary>
    public int RecordLength { get; init; }

    /// <summary>
    /// The width of the per-slot header in bytes, or zero when the format has
    /// none. A header is what makes deletion representable.
    /// </summary>
    /// <remarks>
    /// Parameterised because the presence and width of a record marker vary
    /// between COBOL runtimes. Changing it here changes nothing at any call
    /// site.
    /// </remarks>
    public int SlotHeaderLength { get; init; }

    /// <summary>
    /// The first header byte's value for a slot that holds a record. Any other
    /// value means the slot is absent — including the zero a filesystem leaves in
    /// the gap when a record is written past the end of the file, so a gap needs
    /// no explicit filling.
    /// </summary>
    public byte PresentMarker { get; init; } = 0xFF;

    /// <summary>The first header byte's value written when a record is deleted.</summary>
    public byte DeletedMarker { get; init; }

    /// <summary>The byte used to pad a record shorter than <see cref="RecordLength"/>.</summary>
    public byte PadByte { get; init; } = 0x20;

    /// <summary>The total size of one slot in bytes.</summary>
    public int SlotSize => SlotHeaderLength + RecordLength;

    /// <summary>Throws when the layout is unusable.</summary>
    /// <exception cref="ArgumentException">The layout is inconsistent.</exception>
    public void Validate()
    {
        if (RecordLength < 1)
            throw new ArgumentException($"{nameof(RecordLength)} must be positive.");

        if (SlotHeaderLength < 0)
            throw new ArgumentException($"{nameof(SlotHeaderLength)} cannot be negative.");

        if (SlotHeaderLength > 0 && PresentMarker == DeletedMarker)
        {
            throw new ArgumentException(
                $"{nameof(PresentMarker)} and {nameof(DeletedMarker)} must differ, " +
                "otherwise a deleted slot is indistinguishable from a live one.");
        }
    }
}

/// <summary>One record returned by a relative file's sequential traversal.</summary>
/// <param name="RecordNumber">The one-based record number.</param>
/// <param name="Record">
/// The record's bytes, valid only until the enumerator advances.
/// </param>
public readonly record struct RelativeRecord(long RecordNumber, ReadOnlyMemory<byte> Record);

/// <summary>
/// A file of fixed-length records addressed by a one-based record number.
/// </summary>
/// <remarks>
/// Random operations go straight to the device, since a record number gives an
/// exact offset and there is nothing to read ahead for. Sequential traversal, by
/// contrast, runs through the block pump — so walking a relative file front to
/// back is as fast as reading a fixed-block file, and only the per-slot presence
/// check separates them.
/// </remarks>
public sealed class RelativeFile : IAsyncDisposable
{
    private readonly IBlockDevice _device;
    private readonly RelativeFileOptions _layout;
    private readonly FileIoOptions _options;
    private readonly byte[] _slot;
    private bool _disposed;

    private RelativeFile(IBlockDevice device, RelativeFileOptions layout, FileIoOptions options)
    {
        _device = device;
        _layout = layout;
        _options = options;
        _slot = new byte[layout.SlotSize];
    }

    /// <summary>Opens a relative file.</summary>
    /// <param name="path">The file to open.</param>
    /// <param name="layout">The slot layout.</param>
    /// <param name="access">The access required.</param>
    /// <param name="options">I/O options, or <see langword="null"/> for the defaults.</param>
    /// <returns>An open file.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The layout is inconsistent.</exception>
    /// <exception cref="PunchIoException">The file could not be opened.</exception>
    public static RelativeFile Open(
        string path,
        RelativeFileOptions layout,
        FileAccess access,
        FileIoOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(layout);

        layout.Validate();

        options ??= FileIoOptions.Default;
        options.Validate();

        // Record numbers give exact byte offsets that owe nothing to sector
        // boundaries, so the unbuffered backend cannot serve them.
        var policy = options.Backend == BlockDevicePolicy.ForceNative
            ? BlockDevicePolicy.ForceNative
            : BlockDevicePolicy.ForceManaged;

        var mode = access == FileAccess.Read ? FileMode.Open : FileMode.OpenOrCreate;

        return new RelativeFile(
            BlockDeviceFactory.Open(path, mode, access, options.Share, policy), layout, options);
    }

    /// <summary>The record length in bytes.</summary>
    public int RecordLength => _layout.RecordLength;

    /// <summary>
    /// The number of slots the file currently spans, whether or not each holds a
    /// record.
    /// </summary>
    public long SlotCount => _device.Length / _layout.SlotSize;

    /// <summary>Reads a record, reporting whether the slot holds one.</summary>
    /// <param name="recordNumber">The one-based record number.</param>
    /// <param name="destination">
    /// A buffer at least <see cref="RecordLength"/> bytes long.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the slot held a record, which has been copied
    /// into <paramref name="destination"/>; otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="recordNumber"/> is less than one, or the destination is
    /// too small.
    /// </exception>
    public async ValueTask<bool> TryReadAsync(
        long recordNumber, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireValidRecordNumber(recordNumber);
        RequireCapacity(destination.Length);

        long offset = OffsetOf(recordNumber);
        if (offset + _layout.SlotSize > _device.Length) return false;

        int read = await ReadWholeSlotAsync(offset, cancellationToken).ConfigureAwait(false);
        if (read < _layout.SlotSize) return false;

        if (!SlotIsPresent(_slot)) return false;

        _slot.AsSpan(_layout.SlotHeaderLength, _layout.RecordLength)
            .CopyTo(destination.Span);

        return true;
    }

    /// <summary>Reads a record that must exist.</summary>
    /// <param name="recordNumber">The one-based record number.</param>
    /// <param name="destination">A buffer at least <see cref="RecordLength"/> bytes long.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the record has been copied.</returns>
    /// <exception cref="PunchIoException">
    /// The slot holds no record. The exception carries file status <c>23</c>.
    /// </exception>
    public async ValueTask ReadAsync(
        long recordNumber, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (await TryReadAsync(recordNumber, destination, cancellationToken).ConfigureAwait(false))
            return;

        throw new PunchIoException(
            $"Relative record {recordNumber} does not exist.", FileStatus.RecordNotFound);
    }

    /// <summary>
    /// Writes a record, creating the slot if it does not yet exist and extending
    /// the file if the record number is beyond its end.
    /// </summary>
    /// <param name="recordNumber">The one-based record number.</param>
    /// <param name="record">
    /// The record's bytes. Shorter records are padded; longer ones are refused.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the record has been written.</returns>
    /// <exception cref="ArgumentException">The record is longer than <see cref="RecordLength"/>.</exception>
    public ValueTask WriteAsync(
        long recordNumber, ReadOnlyMemory<byte> record, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireValidRecordNumber(recordNumber);

        if (record.Length > _layout.RecordLength)
        {
            throw new ArgumentException(
                $"A record of {record.Length} bytes cannot be written to a relative file of " +
                $"{_layout.RecordLength}-byte records.",
                nameof(record));
        }

        if (_layout.SlotHeaderLength > 0)
        {
            _slot.AsSpan(0, _layout.SlotHeaderLength).Clear();
            _slot[0] = _layout.PresentMarker;
        }

        var body = _slot.AsSpan(_layout.SlotHeaderLength, _layout.RecordLength);
        record.Span.CopyTo(body);
        body[record.Length..].Fill(_layout.PadByte);

        // Any gap left between the old end of file and this slot reads back as
        // zero, which is not the present marker, so it is already "absent".
        return _device.WriteAsync(_slot, OffsetOf(recordNumber), cancellationToken);
    }

    /// <summary>Overwrites a record that must already exist.</summary>
    /// <param name="recordNumber">The one-based record number.</param>
    /// <param name="record">The replacement bytes.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the record has been written.</returns>
    /// <exception cref="PunchIoException">
    /// The slot holds no record. The exception carries file status <c>23</c>.
    /// </exception>
    public async ValueTask RewriteAsync(
        long recordNumber, ReadOnlyMemory<byte> record, CancellationToken cancellationToken = default)
    {
        if (!await ExistsAsync(recordNumber, cancellationToken).ConfigureAwait(false))
        {
            throw new PunchIoException(
                $"Relative record {recordNumber} does not exist and cannot be rewritten.",
                FileStatus.RecordNotFound);
        }

        await WriteAsync(recordNumber, record, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Marks a slot as holding no record.</summary>
    /// <param name="recordNumber">The one-based record number.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> when a record was deleted; <see langword="false"/>
    /// when the slot was already empty.
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// The layout has no slot header, so deletion cannot be represented.
    /// </exception>
    public async ValueTask<bool> DeleteAsync(
        long recordNumber, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireValidRecordNumber(recordNumber);

        if (_layout.SlotHeaderLength == 0)
        {
            throw new NotSupportedException(
                "Deletion requires a slot header; this layout has none, so an empty slot " +
                "cannot be distinguished from a live one.");
        }

        if (!await ExistsAsync(recordNumber, cancellationToken).ConfigureAwait(false))
            return false;

        var header = new byte[_layout.SlotHeaderLength];
        header[0] = _layout.DeletedMarker;

        await _device.WriteAsync(header, OffsetOf(recordNumber), cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>Reports whether a slot currently holds a record.</summary>
    /// <param name="recordNumber">The one-based record number.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether the slot holds a record.</returns>
    public async ValueTask<bool> ExistsAsync(
        long recordNumber, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireValidRecordNumber(recordNumber);

        long offset = OffsetOf(recordNumber);
        if (offset + _layout.SlotSize > _device.Length) return false;

        if (_layout.SlotHeaderLength == 0) return true;

        int read = await ReadWholeSlotAsync(offset, cancellationToken).ConfigureAwait(false);

        return read >= _layout.SlotSize && SlotIsPresent(_slot);
    }

    /// <summary>
    /// Walks the file front to back through the block pump, skipping slots that
    /// hold no record.
    /// </summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>
    /// Every live record with its number. Each record is valid only until the
    /// enumerator advances.
    /// </returns>
    /// <remarks>
    /// Do not write to the file while enumerating it: the traversal reads through
    /// the same device and would see a partially updated slot.
    /// </remarks>
    public async IAsyncEnumerable<RelativeRecord> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int blockSize = _options.ResolveBlockSize(_device.Alignment, _layout.SlotSize);

        // The device outlives the pump here, so the pump must not dispose it.
        var source = BlockSource.Create(_device, _options.QueueDepth, blockSize, ownsDevice: false);

        await using var reader = SequentialReader<FixedBlockFramer>.Create(
            source,
            new FixedBlockFramer(_layout.SlotSize, TrailingPartialRecord.Ignore),
            Math.Max(_options.MaxRecordLength, _layout.SlotSize));

        long recordNumber = 0;

        while (await reader.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            recordNumber++;

            var slot = reader.Current;
            if (!SlotIsPresent(slot.Span)) continue;

            yield return new RelativeRecord(
                recordNumber, slot[_layout.SlotHeaderLength..]);
        }
    }

    /// <summary>Flushes buffered data.</summary>
    /// <param name="toDisk">Forces data to stable media rather than to the operating system.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the flush has finished.</returns>
    public ValueTask FlushAsync(bool toDisk = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _device.FlushAsync(toDisk, cancellationToken);
    }

    private bool SlotIsPresent(ReadOnlySpan<byte> slot) =>
        _layout.SlotHeaderLength == 0 || slot[0] == _layout.PresentMarker;

    private long OffsetOf(long recordNumber) => (recordNumber - 1) * _layout.SlotSize;

    private async ValueTask<int> ReadWholeSlotAsync(long offset, CancellationToken cancellationToken)
    {
        int total = 0;

        while (total < _layout.SlotSize)
        {
            int read = await _device
                .ReadAsync(_slot.AsMemory(total), offset + total, cancellationToken)
                .ConfigureAwait(false);

            if (read == 0) break;

            total += read;
        }

        return total;
    }

    private static void RequireValidRecordNumber(long recordNumber)
    {
        if (recordNumber < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recordNumber), recordNumber, "Record numbers are one-based.");
        }
    }

    private void RequireCapacity(int destinationLength)
    {
        if (destinationLength < _layout.RecordLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destinationLength), destinationLength,
                $"The destination must be at least {_layout.RecordLength} bytes.");
        }
    }

    /// <summary>Closes the file.</summary>
    /// <returns>A task that completes once the file is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _disposed = true;

        await _device.DisposeAsync().ConfigureAwait(false);
    }
}
