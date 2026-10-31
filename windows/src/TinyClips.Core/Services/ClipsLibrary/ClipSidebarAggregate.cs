using TinyClips.Core.Models.ClipsLibrary;

namespace TinyClips.Core.Services.ClipsLibrary;

/// <summary>A fresh snapshot of the Library's tag and collection navigation data.</summary>
public sealed class ClipSidebarAggregate
{
    private readonly Dictionary<string, int> _tagCounts;
    private readonly Dictionary<string, int> _collectionCounts;
    private readonly int _clipCount;

    private ClipSidebarAggregate(
        HashSet<string> tags,
        Dictionary<string, int> tagCounts,
        Dictionary<string, int> collectionCounts,
        int clipCount)
    {
        Tags = tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToList();
        Collections = collectionCounts.Keys.OrderBy(collection => collection, StringComparer.OrdinalIgnoreCase).ToList();
        _tagCounts = tagCounts;
        _collectionCounts = collectionCounts;
        _clipCount = clipCount;
    }

    public IReadOnlyList<string> Tags { get; }

    public IReadOnlyList<string> Collections { get; }

    public int TagCount(string? tag) =>
        string.IsNullOrWhiteSpace(tag) ? _clipCount : _tagCounts.GetValueOrDefault(tag);

    public int CollectionCount(string? collection) =>
        string.IsNullOrWhiteSpace(collection) ? _clipCount : _collectionCounts.GetValueOrDefault(collection);

    public static ClipSidebarAggregate Create(IEnumerable<LibraryClip> clips)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenInClip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var collectionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var clipCount = 0;
        foreach (var clip in clips)
        {
            clipCount++;
            seenInClip.Clear();
            foreach (var tag in clip.Metadata.Tags)
            {
                // Loaded metadata can contain duplicates or blank tags. Keep its display
                // values unchanged; a blank filter matches every clip, not just tagged clips.
                tags.Add(tag);
                if (!string.IsNullOrWhiteSpace(tag) && seenInClip.Add(tag))
                {
                    tagCounts[tag] = tagCounts.GetValueOrDefault(tag) + 1;
                }
            }

            var collection = clip.Metadata.Collection;
            if (!string.IsNullOrWhiteSpace(collection))
            {
                collectionCounts[collection] = collectionCounts.GetValueOrDefault(collection) + 1;
            }
        }

        return new ClipSidebarAggregate(tags, tagCounts, collectionCounts, clipCount);
    }
}
