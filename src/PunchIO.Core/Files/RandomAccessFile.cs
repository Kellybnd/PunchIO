using PunchIO.Devices;

namespace PunchIO.Files;

/// <summary>
/// Byte-offset access to a file, without readahead.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately thin. Readahead is worthless when access is unpredictable and
/// would spend device bandwidth on blocks nobody asked for, so this bypasses the
/// block pump entirely and issues each request as the caller makes it.
/// </para>
/// <para>
/// <see cref="BlockDevicePolicy.Auto"/> resolves to the portable backend here
/// even on Windows, because unbuffered I/O would require every offset and length
/// the caller supplies to be sector-aligned — a promise a byte-offset API cannot
/// make. <see cref="BlockDevicePolicy.ForceNative"/> is still honoured for a
/// caller who wants it and can meet the alignment rules.
/// </para>
/// </remarks>
public sealed class RandomAccessFile : IAsyncDisposable
{
    private readonly IBlockDevice _device;
    private bool _disposed;

    private RandomAccessFile(IBlockDevice device) => _device = device;

    /// <summary>Opens a file for byte-offset access.</summary>
    /// <param name="path">The file to open.</param>
    /// <param name="access">The access required.</param>
    /// <param name="options">I/O options, or <see langword="null"/> for the defaults.</param>
    /// <returns>An open file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="PunchIoException">The file could not be opened.</exception>
    public static RandomAccessFile Open(
        string path, FileAccess access, FileIoOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        options ??= FileIoOptions.Default;
        options.Validate();

        // Auto means "what is appropriate here", and unbuffered is not.
        var policy = options.Backend == BlockDevicePolicy.ForceNative
            ? BlockDevicePolicy.ForceNative
            : BlockDevicePolicy.ForceManaged;

        var mode = access == FileAccess.Read ? FileMode.Open : FileMode.OpenOrCreate;

        return new RandomAccessFile(
            BlockDeviceFactory.Open(path, mode, access, options.Share, policy));
    }

    /// <summary>The file's current length in bytes.</summary>
    public long Length => _device.Length;

    /// <summary>
    /// The alignment that offsets and lengths must satisfy. <c>1</c> on the
    /// portable backend, which is what this type uses unless the caller forced
    /// the unbuffered one.
    /// </summary>
    public int Alignment => _device.Alignment;

    /// <summary>Reads at an absolute offset.</summary>
    /// <param name="destination">The buffer to fill.</param>
    /// <param name="offset">The absolute file offset to read from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// The number of bytes read, which may be fewer than requested near the end
    /// of the file, and <c>0</c> at or past the end.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The file has been disposed.</exception>
    public ValueTask<int> ReadAsync(
        Memory<byte> destination, long offset, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        return _device.ReadAsync(destination, offset, cancellationToken);
    }

    /// <summary>Writes at an absolute offset, extending the file if necessary.</summary>
    /// <param name="source">The bytes to write.</param>
    /// <param name="offset">The absolute file offset to write at.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the write has been accepted.</returns>
    /// <exception cref="ObjectDisposedException">The file has been disposed.</exception>
    public ValueTask WriteAsync(
        ReadOnlyMemory<byte> source, long offset, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        return _device.WriteAsync(source, offset, cancellationToken);
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

    /// <summary>Sets the file's length, truncating or extending it.</summary>
    /// <param name="length">The new length in bytes.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the length has been set.</returns>
    public ValueTask SetLengthAsync(long length, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        return _device.SetLengthAsync(length, cancellationToken);
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
