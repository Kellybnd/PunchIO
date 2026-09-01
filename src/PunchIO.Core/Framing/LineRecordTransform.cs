namespace PunchIO.Framing;

/// <summary>
/// Rewrites record content for line-sequential files: tab expansion and the
/// Micro Focus null-escape convention.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="LineSequentialFramer"/> because these operations
/// change bytes rather than locate boundaries. When <see cref="IsIdentity"/> is
/// <see langword="true"/> a reader keeps its zero-copy path and never invokes
/// this type at all, which is the common case.
/// </remarks>
public readonly struct LineRecordTransform
{
    private readonly byte _tab;
    private readonly byte _space;
    private readonly byte _null;
    private readonly int _tabStopWidth;
    private readonly bool _expandTabs;
    private readonly bool _nullEscape;

    /// <summary>Initializes the transform from line-sequential options.</summary>
    /// <param name="options">The behavior switches to apply.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="LineSequentialOptions.TabStopWidth"/> is not positive.
    /// </exception>
    public LineRecordTransform(LineSequentialOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.TabStopWidth);

        _tab = options.Syntax.Tab;
        _space = options.Syntax.Space;
        _null = options.Syntax.Null;
        _tabStopWidth = options.TabStopWidth;
        _expandTabs = options.ExpandTabs;
        _nullEscape = options.NullEscape;
    }

    /// <summary>
    /// <see langword="true"/> when no rewriting is configured, allowing callers to
    /// skip this type entirely and hand out record bytes unchanged.
    /// </summary>
    public bool IsIdentity => !_expandTabs && !_nullEscape;

    /// <summary>
    /// The largest output length either direction can produce for an input of
    /// <paramref name="sourceLength"/> bytes.
    /// </summary>
    /// <param name="sourceLength">The input length in bytes.</param>
    /// <returns>A destination size guaranteed to be sufficient.</returns>
    public int MaxExpansion(int sourceLength)
    {
        if (IsIdentity) return sourceLength;

        // Tab expansion is the wider of the two: each byte can become TabStopWidth
        // spaces. Null escaping at worst doubles. They cannot compound, because a
        // tab expands into spaces and spaces are never escaped.
        int factor = _expandTabs ? _tabStopWidth : 2;
        return sourceLength * factor;
    }

    /// <summary>Applies the read-side transform: expand tabs, remove null escapes.</summary>
    /// <param name="source">The framed record bytes.</param>
    /// <param name="destination">The buffer to write into.</param>
    /// <param name="written">The number of bytes written.</param>
    /// <returns><see langword="false"/> when <paramref name="destination"/> is too small.</returns>
    public bool TryDecode(ReadOnlySpan<byte> source, Span<byte> destination, out int written)
    {
        written = 0;
        int output = 0;

        for (int i = 0; i < source.Length; i++)
        {
            byte value = source[i];

            if (_nullEscape && value == _null)
            {
                // The escape prefix; the byte that follows is literal data.
                if (++i >= source.Length) break;
                if (output >= destination.Length) return false;
                destination[output++] = source[i];
                continue;
            }

            if (_expandTabs && value == _tab)
            {
                int spaces = _tabStopWidth - (output % _tabStopWidth);
                if (output + spaces > destination.Length) return false;
                destination.Slice(output, spaces).Fill(_space);
                output += spaces;
                continue;
            }

            if (output >= destination.Length) return false;
            destination[output++] = value;
        }

        written = output;
        return true;
    }

    /// <summary>Applies the write-side transform: insert null escapes before control bytes.</summary>
    /// <param name="source">The caller's record bytes.</param>
    /// <param name="destination">The buffer to write into.</param>
    /// <param name="written">The number of bytes written.</param>
    /// <returns><see langword="false"/> when <paramref name="destination"/> is too small.</returns>
    public bool TryEncode(ReadOnlySpan<byte> source, Span<byte> destination, out int written)
    {
        written = 0;

        if (!_nullEscape)
        {
            if (source.Length > destination.Length) return false;
            source.CopyTo(destination);
            written = source.Length;
            return true;
        }

        int output = 0;

        for (int i = 0; i < source.Length; i++)
        {
            byte value = source[i];

            // Control bytes -- and the escape byte itself -- are escaped so a
            // reader cannot mistake them for a terminator.
            if (value < 0x20)
            {
                if (output + 2 > destination.Length) return false;
                destination[output++] = _null;
                destination[output++] = value;
                continue;
            }

            if (output >= destination.Length) return false;
            destination[output++] = value;
        }

        written = output;
        return true;
    }
}
