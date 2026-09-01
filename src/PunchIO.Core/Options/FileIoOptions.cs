using PunchIO.Devices;

namespace PunchIO;

/// <summary>
/// How much I/O to keep in flight for a file, how large each request should be,
/// and which backend to use.
/// </summary>
public sealed class FileIoOptions
{
    private const int MinimumBlockSize = 4 * 1024;
    private const int MaximumBlockSize = 256 * 1024 * 1024;
    private const int MaximumQueueDepth = 256;

    /// <summary>The default options.</summary>
    public static FileIoOptions Default { get; } = new();

    /// <summary>
    /// The number of I/O requests to keep outstanding while a block is checked
    /// out. Defaults to 4.
    /// </summary>
    public int QueueDepth { get; init; } = 4;

    /// <summary>
    /// The size of each request in bytes. Defaults to 1 MiB. Treated as a hint
    /// and adjusted upward as described on <see cref="PinBlockSize"/> unless that
    /// is set.
    /// </summary>
    public int BlockSize { get; init; } = 1024 * 1024;

    /// <summary>
    /// Prevents <see cref="BlockSize"/> from being rounded up to eliminate
    /// record straddling. Sector alignment is still applied, because that is a
    /// correctness requirement rather than an optimization.
    /// </summary>
    public bool PinBlockSize { get; init; }

    /// <summary>
    /// The largest record a reader will accept, in bytes. Framing overhead counts
    /// toward the limit. Defaults to 64 KiB.
    /// </summary>
    public int MaxRecordLength { get; init; } = 64 * 1024;

    /// <summary>The sharing mode used when opening the file. Defaults to <see cref="FileShare.Read"/>.</summary>
    public FileShare Share { get; init; } = FileShare.Read;

    /// <summary>Which block device backend to use. Defaults to <see cref="BlockDevicePolicy.Auto"/>.</summary>
    public BlockDevicePolicy Backend { get; init; } = BlockDevicePolicy.Auto;

    /// <summary>Throws when the options are out of range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its permitted range.</exception>
    public void Validate()
    {
        if (QueueDepth is < 1 or > MaximumQueueDepth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(QueueDepth), QueueDepth,
                $"{nameof(QueueDepth)} must be between 1 and {MaximumQueueDepth}.");
        }

        if (BlockSize is < MinimumBlockSize or > MaximumBlockSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BlockSize), BlockSize,
                $"{nameof(BlockSize)} must be between {MinimumBlockSize} and {MaximumBlockSize} bytes.");
        }

        if (MaxRecordLength < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRecordLength), MaxRecordLength,
                $"{nameof(MaxRecordLength)} must be positive.");
        }
    }

    /// <summary>
    /// Works out the block size to actually use for a device and record layout.
    /// </summary>
    /// <param name="alignment">The device's required alignment in bytes.</param>
    /// <param name="recordLength">
    /// The fixed record length, when the format has one. Supplying it lets the
    /// block size be chosen so records never straddle a block boundary.
    /// </param>
    /// <returns>The block size to use.</returns>
    /// <remarks>
    /// Two rounding rules can apply and they can conflict. Sector alignment is a
    /// correctness requirement and always wins; rounding to a multiple of the
    /// record length is only an optimization, and it is dropped when the smallest
    /// size satisfying both would be wildly larger than what was asked for — a
    /// 4096-byte sector against a 4095-byte record, for instance, whose least
    /// common multiple is over sixteen megabytes.
    /// </remarks>
    public int ResolveBlockSize(int alignment, int? recordLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);

        int resolved = RoundUp(BlockSize, alignment);

        if (PinBlockSize || recordLength is not int length || length <= 0)
            return resolved;

        long common = Lcm(alignment, length);

        // Only worth doing when the result stays in the same ballpark as the
        // requested size; otherwise keep sector alignment and let the reader
        // stitch across boundaries as usual.
        long budget = Math.Max((long)BlockSize, alignment) * 4;

        if (common > budget || common > MaximumBlockSize)
            return resolved;

        return (int)RoundUp(BlockSize, (int)common);
    }

    private static int RoundUp(int value, int multiple) =>
        (value + multiple - 1) / multiple * multiple;

    private static long Lcm(int a, int b) => (long)a / Gcd(a, b) * b;

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
