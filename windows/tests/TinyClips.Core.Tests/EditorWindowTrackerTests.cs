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

        Assert.Equal(windows, closed);
        Assert.Equal(0, tracker.Count);
        Assert.Null(tracker.ActiveWindow);
    }
}
