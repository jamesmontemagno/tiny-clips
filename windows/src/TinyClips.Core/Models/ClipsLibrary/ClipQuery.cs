namespace TinyClips.Core.Models.ClipsLibrary;

/// <summary>
/// All inputs that shape which clips the library shows and in what order.
/// </summary>
public sealed record ClipQuery(
    string SearchText = "",
    ClipTypeFilter TypeFilter = ClipTypeFilter.All,
    ClipDateFilter DateFilter = ClipDateFilter.Any,
    SmartCollection SmartCollection = SmartCollection.AllClips,
    string? Tag = null,
    string? Collection = null,
    ClipSortOption Sort = ClipSortOption.NewestFirst)
{
    public static ClipQuery Default { get; } = new();

    /// <summary>True when any narrowing filter (not sort) is active.</summary>
    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || TypeFilter != ClipTypeFilter.All
        || DateFilter != ClipDateFilter.Any
        || SmartCollection != SmartCollection.AllClips
        || !string.IsNullOrWhiteSpace(Tag)
        || !string.IsNullOrWhiteSpace(Collection);

    /// <summary>Resets narrowing filters but keeps the sort order.</summary>
    public ClipQuery ClearFilters() => new(Sort: Sort);
}

/// <summary>
/// A clip file joined with its user metadata — the unit the query engine and UI work with.
/// </summary>
public sealed record LibraryClip(ClipEntry Entry, ClipMetadata Metadata)
{
    public string Path => Entry.Path;

    public CaptureType Type => Entry.Type;

    /// <summary>User-chosen name, falling back to the file name without extension.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Metadata.DisplayName)
        ? System.IO.Path.GetFileNameWithoutExtension(Entry.FileName)
        : Metadata.DisplayName;
}
