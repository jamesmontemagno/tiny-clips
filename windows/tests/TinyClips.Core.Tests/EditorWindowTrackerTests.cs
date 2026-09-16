using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class EditorWindowTrackerTests
{
    [Fact]
    public void CloseOne_ReopensPickerOnlyWhenLastWindowCloses()
    {
        var tracker = new EditorWindowTracker<string>();
        var first = "first";
        var second = "second";

        tracker.Track(first);
        tracker.Track(second);

        Assert.Equal(2, tracker.Count);
        Assert.Equal(second, tracker.ActiveWindow);

        Assert.False(tracker.CloseOne(first, reopenPickerAfterClose: true));
        Assert.Equal(1, tracker.Count);
        Assert.Equal(second, tracker.ActiveWindow);

        Assert.True(tracker.CloseOne(second, reopenPickerAfterClose: true));
        Assert.Equal(0, tracker.Count);
        Assert.Null(tracker.ActiveWindow);
    }

    [Fact]
    public void ActiveWindow_ReflectsNewestRemainingWindowWhenTheNewestClosesFirst()
    {
        var tracker = new EditorWindowTracker<string>();
        var first = "first";
        var second = "second";
        var third = "third";

        tracker.Track(first);
        tracker.Track(second);
        tracker.Track(third);
        Assert.Equal(third, tracker.ActiveWindow);

        // Close the newest window first; the newest *remaining* window should become active,
        // not an arbitrary one.
        Assert.False(tracker.CloseOne(third, reopenPickerAfterClose: false));
        Assert.Equal(second, tracker.ActiveWindow);

        Assert.False(tracker.CloseOne(first, reopenPickerAfterClose: false));
        Assert.Equal(second, tracker.ActiveWindow);

        Assert.False(tracker.CloseOne(second, reopenPickerAfterClose: false));
        Assert.Null(tracker.ActiveWindow);
    }

    [Fact]
    public void CloseOne_AccumulatesReopenRequestAcrossMixedFlagCloses()
    {
        var tracker = new EditorWindowTracker<string>();
        var pickerInitiated = "picker-initiated";
        var recentCaptures = "recent-captures";

        tracker.Track(pickerInitiated);
        tracker.Track(recentCaptures);

        // The picker-initiated editor (reopen requested) closes first, while an editor opened
        // from Recent Captures (no reopen requested) is still open. The request must not be
        // lost: once the last window closes, the picker should still reopen.
        Assert.False(tracker.CloseOne(pickerInitiated, reopenPickerAfterClose: true));
        Assert.True(tracker.CloseOne(recentCaptures, reopenPickerAfterClose: false));
    }

    [Fact]
    public void CloseAll_ClosesEveryTrackedWindowAndClearsState()
    {
        var tracker = new EditorWindowTracker<string>();
        var windows = new[] { "one", "two", "three" };
        foreach (var window in windows)
        {
            tracker.Track(window);
        }

        var closed = new List<string>();
        tracker.CloseAll(closeWindow => closed.Add(closeWindow));

        Assert.Equal(windows.ToHashSet(), closed.ToHashSet());
        Assert.Equal(0, tracker.Count);
        Assert.Null(tracker.ActiveWindow);
    }
}
