using System.Globalization;

namespace PunchIO.Configuration;

/// <summary>
/// Parses byte sizes written with a unit suffix, so configuration can say
/// <c>"1MiB"</c> instead of <c>1048576</c>.
/// </summary>
/// <remarks>
/// Sizes bind as strings and are parsed here rather than being bound as numbers,
/// which keeps the configuration binder source-generated and therefore
/// NativeAOT-safe. Suffixes are binary throughout: <c>KB</c> and <c>KiB</c> both
/// mean 1024 bytes. Decimal kilobytes have no place in a buffer size, and
/// silently differing by 2.4 % would be worse than not offering the spelling.
/// </remarks>
public static class SizeValue
{
    /// <summary>Attempts to parse a size.</summary>
    /// <param name="text">A number, optionally followed by a unit suffix.</param>
    /// <param name="value">The size in bytes.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> was understood.</returns>
    public static bool TryParse(string? text, out long value)
    {
        value = 0;

        if (string.IsNullOrWhiteSpace(text)) return false;

        var span = text.AsSpan().Trim();
        long multiplier = 1;

        foreach (var (suffix, factor) in Suffixes)
        {
            if (!span.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            multiplier = factor;
            span = span[..^suffix.Length].TrimEnd();
            break;
        }

        if (!long.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
            return false;

        if (number < 0) return false;

        // Reject rather than wrap: a size that overflows is a typo, not a request.
        if (number > long.MaxValue / multiplier) return false;

        value = number * multiplier;
        return true;
    }

    /// <summary>Parses a size, or throws.</summary>
    /// <param name="text">A number, optionally followed by a unit suffix.</param>
    /// <returns>The size in bytes.</returns>
    /// <exception cref="FormatException"><paramref name="text"/> was not understood.</exception>
    public static long Parse(string? text) =>
        TryParse(text, out long value)
            ? value
            : throw new FormatException(
                $"'{text}' is not a byte size. Write a number, optionally with a suffix: " +
                "1024, 4KiB, 1MiB, 2GiB.");

    // Longest first, so "KiB" is matched before "B". A static array rather than
    // a collection expression returning a span, which would rebuild it per call.
    private static readonly (string Suffix, long Factor)[] Suffixes =
    [
        ("KiB", 1024L),
        ("MiB", 1024L * 1024),
        ("GiB", 1024L * 1024 * 1024),
        ("KB", 1024L),
        ("MB", 1024L * 1024),
        ("GB", 1024L * 1024 * 1024),
        ("K", 1024L),
        ("M", 1024L * 1024),
        ("G", 1024L * 1024 * 1024),
        ("B", 1L),
    ];
}
