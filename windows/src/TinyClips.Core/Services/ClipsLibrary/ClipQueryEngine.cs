using TinyClips.Core.Models;
using TinyClips.Core.Models.ClipsLibrary;

namespace TinyClips.Core.Services.ClipsLibrary;

/// <summary>
/// Pure, deterministic filtering/sorting for the Clips Library. No I/O; fully unit-testable.
/// </summary>
public static class ClipQueryEngine
{
    /// <summary>Files at or above this size count as "Large" for the smart collection.</summary>
    public const long LargeFileThresholdBytes = 50L * 1024 * 1024;

    /// <summary>Clips captured within this window count as "Recent".</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(24);

    public static IReadOnlyList<LibraryClip> Apply(IEnumerable<LibraryClip> clips, ClipQuery query, DateTimeOffset now)
    {
        var filtered = clips
            .Where(clip => MatchesSmartCollection(clip, query.SmartCollection, now))
            .Where(clip => MatchesType(clip, query.TypeFilter))
            .Where(clip => MatchesDate(clip, query.DateFilter, now))
            .Where(clip => MatchesTag(clip, query.Tag))
            .Where(clip => MatchesCollection(clip, query.Collection))
            .Where(clip => MatchesSearch(clip, query.SearchText));

        return Sort(filtered, query.Sort).ToList();
    }

    public static IReadOnlyList<string> CollectTags(IEnumerable<LibraryClip> clips) =>
        clips.SelectMany(clip => clip.Metadata.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IReadOnlyList<string> CollectCollections(IEnumerable<LibraryClip> clips) =>
        clips.Select(clip => clip.Metadata.Collection)
            .Where(collection => !string.IsNullOrWhiteSpace(collection))
            .Select(collection => collection!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(collection => collection, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IReadOnlyDictionary<SmartCollection, int> CountSmartCollections(IReadOnlyCollection<LibraryClip> clips, DateTimeOffset now)
    {
        var counts = new Dictionary<SmartCollection, int>();
        foreach (var collection in Enum.GetValues<SmartCollection>())
        {
            counts[collection] = clips.Count(clip => MatchesSmartCollection(clip, collection, now));
        }

        return counts;
    }

    public static bool MatchesSmartCollection(LibraryClip clip, SmartCollection collection, DateTimeOffset now) => collection switch
    {
        SmartCollection.AllClips    => true,
        SmartCollection.Recent      => clip.Entry.CapturedAt >= now - RecentWindow,
        SmartCollection.ThisWeek    => clip.Entry.CapturedAt >= StartOfWeek(now),
        SmartCollection.ThisMonth   => clip.Entry.CapturedAt >= StartOfMonth(now),
        SmartCollection.LargeFiles  => clip.Entry.FileSizeBytes >= LargeFileThresholdBytes,
        SmartCollection.Favorites   => clip.Metadata.IsFavorite,
        SmartCollection.Screenshots => clip.Type == CaptureType.Screenshot,
        SmartCollection.Videos      => clip.Type == CaptureType.Video,
        SmartCollection.Gifs        => clip.Type == CaptureType.Gif,
        _                           => true,
    };

    public static bool MatchesType(LibraryClip clip, ClipTypeFilter filter) => filter switch
    {
        ClipTypeFilter.All         => true,
        ClipTypeFilter.Screenshots => clip.Type == CaptureType.Screenshot,
        ClipTypeFilter.Videos      => clip.Type == CaptureType.Video,
        ClipTypeFilter.Gifs        => clip.Type == CaptureType.Gif,
        ClipTypeFilter.Favorites   => clip.Metadata.IsFavorite,
        _                          => true,
    };

    public static bool MatchesDate(LibraryClip clip, ClipDateFilter filter, DateTimeOffset now)
    {
        var localNow = now.ToLocalTime();
        var startOfToday = new DateTimeOffset(localNow.Date, localNow.Offset);
        var captured = clip.Entry.CapturedAt;
        return filter switch
        {
            ClipDateFilter.Any        => true,
            ClipDateFilter.Today      => captured >= startOfToday,
            ClipDateFilter.Last7Days  => captured >= startOfToday.AddDays(-6),
            ClipDateFilter.Last30Days => captured >= startOfToday.AddDays(-29),
            _                         => true,
        };
    }

    public static bool MatchesTag(LibraryClip clip, string? tag) =>
        string.IsNullOrWhiteSpace(tag)
        || clip.Metadata.Tags.Any(existing => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase));

    public static bool MatchesCollection(LibraryClip clip, string? collection) =>
        string.IsNullOrWhiteSpace(collection)
        || string.Equals(clip.Metadata.Collection, collection, StringComparison.OrdinalIgnoreCase);

    public static bool MatchesSearch(LibraryClip clip, string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        var terms = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term =>
            Contains(clip.DisplayName, term)
            || Contains(clip.Entry.FileName, term)
            || Contains(clip.Metadata.Notes, term)
            || Contains(clip.Metadata.Collection, term)
            || clip.Metadata.Tags.Any(tag => Contains(tag, term)));
    }

    public static IEnumerable<LibraryClip> Sort(IEnumerable<LibraryClip> clips, ClipSortOption sort) => sort switch
    {
        ClipSortOption.OldestFirst    => clips.OrderBy(clip => clip.Entry.CapturedAt),
        ClipSortOption.Largest        => clips.OrderByDescending(clip => clip.Entry.FileSizeBytes).ThenByDescending(clip => clip.Entry.CapturedAt),
        ClipSortOption.Name           => clips.OrderBy(clip => clip.DisplayName, StringComparer.OrdinalIgnoreCase).ThenByDescending(clip => clip.Entry.CapturedAt),
        ClipSortOption.FavoritesFirst => clips.OrderByDescending(clip => clip.Metadata.IsFavorite).ThenByDescending(clip => clip.Entry.CapturedAt),
        _                             => clips.OrderByDescending(clip => clip.Entry.CapturedAt),
    };

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset StartOfWeek(DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var daysSinceMonday = ((int)local.DayOfWeek + 6) % 7;
        return new DateTimeOffset(local.Date.AddDays(-daysSinceMonday), local.Offset);
    }

    private static DateTimeOffset StartOfMonth(DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        return new DateTimeOffset(new DateTime(local.Year, local.Month, 1), local.Offset);
    }
}
