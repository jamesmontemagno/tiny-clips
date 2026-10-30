using TinyClips.Core.Services;

namespace TinyClips.App;

internal sealed record TrayRecentItemKey(RecentMenuEntry Entry, long Size, DateTimeOffset Modified, string? Poster)
{
    public bool HasSameImage(TrayRecentItemKey other) =>
        Entry.Capture?.Type == other.Entry.Capture?.Type
        && Size == other.Size && Modified == other.Modified && Poster == other.Poster;
}

/// <summary>
/// The bounded, retained Recent menu. Call Update only while the native flyout is closed.
/// Identity includes metadata/poster revisions, not just paths, so replacing a file invalidates
/// its image. Removed or superseded slots cannot accept a late image completion.
/// </summary>
internal sealed class TrayRecentItems<TItem>
{
    internal sealed class Slot(TrayRecentItemKey key, TItem item)
    {
        public TrayRecentItemKey Key { get; set; } = key;
        public TItem Item { get; } = item;
        public bool IsImageStarted { get; set; }
        public bool HasImageFailed { get; set; }
    }

    private Dictionary<string, Slot> _byId = new(StringComparer.Ordinal);
    public IReadOnlyList<Slot> Items { get; private set; } = [];

    public void Update(
        IEnumerable<TrayRecentItemKey> keys,
        Func<RecentMenuEntry, TItem> create,
        Action<TItem, RecentMenuEntry>? updateText = null)
    {
        var next = new Dictionary<string, Slot>(StringComparer.Ordinal);
        var ordered = new List<Slot>();
        foreach (var key in keys)
        {
            if (!_byId.TryGetValue(key.Entry.Id, out var slot) || !slot.Key.HasSameImage(key))
            {
                slot = new Slot(key, create(key.Entry));
            }
            else
            {
                if (slot.Key.Entry != key.Entry)
                {
                    updateText?.Invoke(slot.Item, key.Entry);
                }

                slot.Key = key;
                if (slot.HasImageFailed)
                {
                    // Retry a transient read/decode failure on a later refresh, not in a tight loop.
                    slot.IsImageStarted = false;
                    slot.HasImageFailed = false;
                }
            }

            next.Add(key.Entry.Id, slot);
            ordered.Add(slot);
        }

        _byId = next;
        Items = ordered;
    }

    public Slot? StartNextImage()
    {
        var slot = Items.FirstOrDefault(static item => !item.IsImageStarted);
        if (slot is not null)
        {
            slot.IsImageStarted = true;
        }

        return slot;
    }

    public bool IsCurrent(Slot slot) =>
        _byId.TryGetValue(slot.Key.Entry.Id, out var current) && ReferenceEquals(slot, current);
}
