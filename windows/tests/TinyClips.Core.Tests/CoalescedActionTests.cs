using System.Collections.ObjectModel;
using TinyClips.App;

namespace TinyClips.Core.Tests;

public sealed class CoalescedActionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(10_000)]
    public void SyntheticNavigationBurst_RebuildsOnlyFinalTree(int entryCount)
    {
        var queue = new Queue<Action>();
        var collections = new ObservableCollection<string>();
        var tags = new ObservableCollection<string>();
        var rendered = Array.Empty<string>();
        var rebuilds = 0;
        long synchronousVisits = 0;
        using var action = new CoalescedAction(callback =>
        {
            queue.Enqueue(callback);
            return true;
        }, () =>
        {
            rendered = collections.Concat(tags).ToArray();
            rebuilds++;
        });

        void OnChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        {
            // The old shell rebuilt every currently known entry for every notification.
            synchronousVisits += collections.Count + tags.Count;
            Assert.True(action.Request());
        }

        collections.CollectionChanged += OnChanged;
        tags.CollectionChanged += OnChanged;
        for (var i = 0; i < entryCount; i++)
        {
            (i % 2 == 0 ? collections : tags).Add($"entry-{i}");
        }

        Assert.Equal((long)entryCount * (entryCount + 1) / 2, synchronousVisits);
        Assert.Equal(entryCount == 0 ? 0 : 1, queue.Count);
        Assert.Equal(0, rebuilds);
        while (queue.TryDequeue(out var callback))
        {
            callback();
        }

        Assert.Equal(entryCount == 0 ? 0 : 1, rebuilds);
        Assert.Equal(collections.Concat(tags), rendered);
        Assert.Equal(entryCount, rendered.Length);
    }

    [Fact]
    public void LaterBurst_RebuildsAgainWithLatestOrderAndRemovals()
    {
        var queue = new Queue<Action>();
        var entries = new ObservableCollection<string> { "a", "b", "c" };
        var rendered = Array.Empty<string>();
        using var action = new CoalescedAction(callback =>
        {
            queue.Enqueue(callback);
            return true;
        }, () => rendered = entries.ToArray());
        entries.CollectionChanged += (_, _) => Assert.True(action.Request());

        entries.Move(2, 0);
        entries.Remove("a");
        Assert.Single(queue);
        queue.Dequeue()();
        Assert.Equal(new[] { "c", "b" }, rendered);

        entries.Clear();
        entries.Add("d");
        Assert.Single(queue);
        queue.Dequeue()();
        Assert.Equal(new[] { "d" }, rendered);
    }

    [Fact]
    public void ClosedBeforeQueuedRebuild_DoesNotTouchUi()
    {
        Action? queued = null;
        var rebuilds = 0;
        var action = new CoalescedAction(callback =>
        {
            queued = callback;
            return true;
        }, () => rebuilds++);

        Assert.True(action.Request());
        action.Dispose();
        queued!();
        Assert.True(action.Request());
        Assert.Equal(0, rebuilds);
    }

    [Fact]
    public void RejectedEnqueue_IsReportedAndCanBeRetried()
    {
        var accepted = false;
        var attempts = 0;
        using var action = new CoalescedAction(_ =>
        {
            attempts++;
            return accepted;
        }, () => { });

        Assert.False(action.Request());
        accepted = true;
        Assert.True(action.Request());
        Assert.True(action.Request());
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void ReentrantNotification_IsNotLost()
    {
        var queue = new Queue<Action>();
        var rebuilds = 0;
        CoalescedAction? action = null;
        action = new CoalescedAction(callback =>
        {
            queue.Enqueue(callback);
            return true;
        }, () =>
        {
            if (++rebuilds == 1)
            {
                Assert.True(action!.Request());
            }
        });
        using (action)
        {
            Assert.True(action.Request());
            queue.Dequeue()();
            Assert.Single(queue);
            queue.Dequeue()();
            Assert.Equal(2, rebuilds);
        }
    }
}
