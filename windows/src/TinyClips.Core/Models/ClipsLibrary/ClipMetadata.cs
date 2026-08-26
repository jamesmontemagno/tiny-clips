namespace TinyClips.Core.Models.ClipsLibrary;

/// <summary>
/// User-authored metadata attached to a clip file. Keyed by the clip's full path and persisted
/// separately from the file so the capture itself is never rewritten.
/// </summary>
public sealed record ClipMetadata(
    string Path,
    string? DisplayName = null,
    bool IsFavorite = false,
    IReadOnlyList<string>? Tags = null,
    string? Notes = null,
    string? Collection = null,
    string? UploadedUrl = null)
{
    public IReadOnlyList<string> Tags { get; init; } = Tags ?? [];

    public static ClipMetadata Empty(string path) => new(path);

    /// <summary>True when nothing user-visible is set; such records are dropped on save.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(DisplayName)
        && !IsFavorite
        && Tags.Count == 0
        && string.IsNullOrWhiteSpace(Notes)
        && string.IsNullOrWhiteSpace(Collection)
        && string.IsNullOrWhiteSpace(UploadedUrl);

    public ClipMetadata WithTags(IEnumerable<string> tags) => this with { Tags = NormalizeTags(tags) };

    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags) =>
        tags.Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
