using System.Globalization;

namespace TinyClips.Core.Studio;

/// <summary>A part of a text: where it starts, and how many characters it has.</summary>
public readonly record struct StudioTextRange(int Start, int Length);

/// <summary>
/// Finding words in captions, as the search box of the Captions panel does: ignoring case,
/// accents, and the width of East Asian letters, in the user's language. The same as on the Mac.
/// </summary>
public static class StudioCaptionSearch
{
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth;

    /// <summary>What is searched for: what was typed, without spaces around it.</summary>
    public static string Normalize(string? query) => query?.Trim() ?? string.Empty;

    /// <summary>Where <paramref name="query"/> is in <paramref name="text"/>, from the start, without overlaps.</summary>
    public static StudioTextRange[] GetRanges(string text, string query)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0)
        {
            return [];
        }

        var compare = CultureInfo.CurrentCulture.CompareInfo;
        var found = new List<StudioTextRange>();
        var from = 0;
        while (from < text.Length)
        {
            var at = compare.IndexOf(text.AsSpan(from), query, Options, out var length);
            if (at < 0)
            {
                break;
            }

            if (length > 0)
            {
                found.Add(new StudioTextRange(from + at, length));
            }

            from += at + Math.Max(length, 1);
        }

        return [.. found];
    }
}
