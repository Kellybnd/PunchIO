using System.Buffers.Binary;
using System.Text;

namespace PunchIO.Cobol;

/// <summary>
/// Where each field sits inside a Micro Focus file control description.
/// </summary>
/// <remarks>
/// <para>
/// Control-block layouts differ between COBOL runtimes and product generations.
/// Every offset is gathered here — all of them, in a single object with no other
/// logic — so that matching a specific runtime means editing this one place and
/// rebuilding, without touching the dispatcher, the handle table, or anything
/// else. Check these against your runtime's header when integrating.
/// </para>
/// <para>
/// Nothing above <see cref="FcdView"/> reads a raw offset, so a correction here
/// is the whole correction.
/// </para>
/// </remarks>
public sealed record FcdLayout
{
    /// <summary>Offset of the two-character file status.</summary>
    public int FileStatusOffset { get; init; }

    /// <summary>Offset of the control block's own length, as a big-endian 16-bit value.</summary>
    public int LengthOffset { get; init; } = 2;

    /// <summary>Offset of the version byte: <c>0</c> for FCD2, <c>1</c> for FCD3.</summary>
    public int VersionOffset { get; init; } = 4;

    /// <summary>Offset of the organization byte.</summary>
    public int OrganizationOffset { get; init; } = 5;

    /// <summary>Offset of the current record length, as a big-endian 32-bit value.</summary>
    public int CurrentRecordLengthOffset { get; init; } = 24;

    /// <summary>Offset of the maximum record length, as a big-endian 32-bit value.</summary>
    public int MaxRecordLengthOffset { get; init; } = 32;

    /// <summary>Offset of the relative key, as a big-endian 64-bit value.</summary>
    public int RelativeKeyOffset { get; init; } = 52;

    /// <summary>
    /// Offset of the field this library stores its open-file identifier in, as a
    /// big-endian 32-bit value.
    /// </summary>
    public int UserHandleOffset { get; init; } = 60;

    /// <summary>
    /// Offset of the pointer to the program's record area, as a big-endian
    /// 64-bit address. Only the native host follows it.
    /// </summary>
    public int RecordAddressOffset { get; init; } = 36;

    /// <summary>Offset of the file name's length, as a big-endian 16-bit value.</summary>
    public int NameLengthOffset { get; init; } = 64;

    /// <summary>Offset of the file name's bytes, stored inline.</summary>
    public int NameOffset { get; init; } = 66;

    /// <summary>The smallest control block this layout can be read from.</summary>
    public int MinimumLength { get; init; } = 66;

    /// <summary>The older, 92-byte control block.</summary>
    public static FcdLayout Fcd2 { get; } = new();

    /// <summary>
    /// The newer control block. The fields this library reads sit at the same
    /// offsets as <see cref="Fcd2"/>, so the two are defined identically; the
    /// distinction exists so that adapting one without the other is a one-line
    /// change.
    /// </summary>
    public static FcdLayout Fcd3 { get; } = new();

    /// <summary>Picks the layout matching a control block's version byte.</summary>
    /// <param name="fcd">The control block.</param>
    /// <returns>The layout to read it with.</returns>
    public static FcdLayout For(ReadOnlySpan<byte> fcd)
    {
        var probe = Fcd2;

        if (fcd.Length <= probe.VersionOffset) return probe;

        return fcd[probe.VersionOffset] == 0 ? Fcd2 : Fcd3;
    }
}

/// <summary>
/// Reads and writes the fields of a COBOL file control description.
/// </summary>
/// <remarks>
/// A <see langword="ref struct"/> over the caller's own bytes: nothing is copied,
/// and the control block is updated in place. Every offset comes from
/// <see cref="FcdLayout"/>; none appear here.
/// </remarks>
public readonly ref struct FcdView
{
    private readonly Span<byte> _fcd;
    private readonly FcdLayout _layout;

    /// <summary>Wraps a control block.</summary>
    /// <param name="fcd">The control block's bytes.</param>
    /// <param name="layout">
    /// The layout to read it with, or <see langword="null"/> to detect it from the
    /// version byte.
    /// </param>
    public FcdView(Span<byte> fcd, FcdLayout? layout = null)
    {
        _fcd = fcd;
        _layout = layout ?? FcdLayout.For(fcd);
    }

    /// <summary>The layout in use.</summary>
    public FcdLayout Layout => _layout;

    /// <summary><see langword="true"/> when the block is long enough to read.</summary>
    public bool IsUsable => _fcd.Length >= _layout.MinimumLength;

    /// <summary>The control block's declared length.</summary>
    public int DeclaredLength => ReadUInt16(_layout.LengthOffset);

    /// <summary>The version byte.</summary>
    public byte Version => _fcd[_layout.VersionOffset];

    /// <summary>The length of the record the program is presenting or expecting.</summary>
    public int CurrentRecordLength
    {
        get => (int)ReadUInt32(_layout.CurrentRecordLengthOffset);
        set => WriteUInt32(_layout.CurrentRecordLengthOffset, (uint)value);
    }

    /// <summary>The largest record this file can hold.</summary>
    public int MaxRecordLength => (int)ReadUInt32(_layout.MaxRecordLengthOffset);

    /// <summary>The record number, for record-addressed operations.</summary>
    public long RelativeKey
    {
        get => ReadInt64(_layout.RelativeKeyOffset);
        set => WriteInt64(_layout.RelativeKeyOffset, value);
    }

    /// <summary>This library's identifier for the open file.</summary>
    public int HandleId
    {
        get => (int)ReadUInt32(_layout.UserHandleOffset);
        set => WriteUInt32(_layout.UserHandleOffset, (uint)value);
    }

    /// <summary>The file name, stored inline and length-prefixed.</summary>
    public string FileName
    {
        get
        {
            int length = ReadUInt16(_layout.NameLengthOffset);
            int available = _fcd.Length - _layout.NameOffset;

            if (length <= 0 || available <= 0) return string.Empty;

            return Encoding.ASCII
                .GetString(_fcd.Slice(_layout.NameOffset, Math.Min(length, available)))
                .TrimEnd();
        }
    }

    /// <summary>Writes the two-character COBOL file status.</summary>
    /// <param name="status">The status to report.</param>
    public void SetStatus(FileStatus status)
    {
        var code = status.Code;

        _fcd[_layout.FileStatusOffset] = (byte)code[0];
        _fcd[_layout.FileStatusOffset + 1] = (byte)code[1];
    }

    /// <summary>Reads the two-character COBOL file status.</summary>
    /// <returns>The status currently in the control block.</returns>
    public FileStatus GetStatus() =>
        new(Encoding.ASCII.GetString(_fcd.Slice(_layout.FileStatusOffset, 2)));

    private ushort ReadUInt16(int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(_fcd.Slice(offset, 2));

    private uint ReadUInt32(int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(_fcd.Slice(offset, 4));

    private long ReadInt64(int offset) =>
        BinaryPrimitives.ReadInt64BigEndian(_fcd.Slice(offset, 8));

    private void WriteUInt32(int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(_fcd.Slice(offset, 4), value);

    private void WriteInt64(int offset, long value) =>
        BinaryPrimitives.WriteInt64BigEndian(_fcd.Slice(offset, 8), value);
}
