using TinyClips.Core.Services;

namespace TinyClips.App;

/// <summary>
/// The bounded, retained Recent menu. Call Update only while the native flyout is closed.
/// Unchanged entries keep their item; a display-only change (date, title) updates its text in
/// place; a change of capture type recreates it, since its icon differs.
/// </summary>
internal sealed class TrayRecentItems<TItem>
{
    private sealed record Slot(RecentMenuEntry Entry, TItem Item);

    private Dictionary<string, Slot> _byId = new(StringComparer.Ordinal);
    public IReadOnlyList<TItem> Items { get; private set; } = [];

    public void Update(
        IEnumerable<RecentMenuEntry> entries,
        Func<RecentMenuEntry, TItem> create,
        Action<TItem, RecentMenuEntry>? updateText = null)
    {
        var next = new Dictionary<string, Slot>(StringComparer.Ordinal);
        var ordered = new List<TItem>();
        foreach (var entry in entries)
        {
            if (!_byId.TryGetValue(entry.Id, out var slot) || slot.Entry.Capture?.Type != entry.Capture?.Type)
            {
                slot = new Slot(entry, create(entry));
            }
            else if (slot.Entry != entry)
            {
                updateText?.Invoke(slot.Item, entry);
                slot = slot with { Entry = entry };
            }

            next.Add(entry.Id, slot);
            ordered.Add(slot.Item);
        }

        _byId = next;
        Items = ordered;
    }
}
