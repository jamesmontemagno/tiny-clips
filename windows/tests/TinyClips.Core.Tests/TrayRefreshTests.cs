using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using TinyClips.App;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class TrayRefreshTests
{
    [Fact]
    public void ClickOnlyShowsRetainedContentAndCachedDimensions()
    {
        using var stream = typeof(TrayRefreshTests).Assembly.GetManifestResourceStream("Tray.App.cs")!;
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();
        var start = source.IndexOf("private void ShowTrayPopup()", StringComparison.Ordinal);
        var end = source.IndexOf("private void RefreshTrayTiles()", start, StringComparison.Ordinal);
        var body = Regex.Replace(source[start..end], @"//[^\r\n]*", "");
        var calls = Regex.Matches(body, @"\b(\w+)\s*\(").Select(match => match.Groups[1].Value);
        Assert.Equal(["ShowTrayPopup", "if", "ShowNearCursor", "ResumeTrayRefresh"], calls);
        Assert.Contains("_trayPopup.ShowNearCursor(_trayPopupWidth, TrayPopupHeight)", body);

        // Resuming the poll only touches the timer; its catch-up probe is queued, not run inline.
        var resumeStart = source.IndexOf("private void ResumeTrayRefresh()", StringComparison.Ordinal);
        var resume = source[resumeStart..source.IndexOf("private void OnTrayDraftsChanged", resumeStart, StringComparison.Ordinal)];
        Assert.Contains("_dispatcher?.TryEnqueue(DispatcherQueuePriority.Low", resume);
        Assert.DoesNotContain("_trayRefreshTimer.Start();", source[..resumeStart]);
        Assert.Contains("sender.Stop();", source);

        // The popup has one construction site, and an open Recent menu is never mutated.
        Assert.Equal(2, Regex.Matches(source, @"\bBuildTrayPopupContent\(").Count);
        Assert.Contains("|| flyout.IsOpen", source);
        Assert.Contains("flyout.Closed += OnTrayRecentClosed", source);
        // The Recent menu shows type icons only: the tray never reads or decodes thumbnails.
        Assert.DoesNotContain("GetThumbnailPathAsync", source);
        Assert.DoesNotContain("ImageIcon", source);
        Assert.DoesNotContain("PosterOf", source);
    }

    [Fact]
    public void BurstIsSingleFlightAndPublishesOnlyLatestResultOnOwnerThread()
    {
        using var context = new OwnerContext();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var secondRelease = new ManualResetEventSlim();
        var owner = Environment.CurrentManagedThreadId;
        var reads = 0;
        var active = 0;
        var published = new List<int>();
        using var refresh = new CoalescedRefresh<int>(() =>
        {
            Assert.NotEqual(owner, Environment.CurrentManagedThreadId);
            Assert.Equal(1, Interlocked.Increment(ref active));
            var result = Interlocked.Increment(ref reads);
            entered.Set();
            Assert.True((result == 1 ? release : secondRelease).Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Interlocked.Decrement(ref active);
            return result;
        }, value =>
        {
            Assert.Equal(owner, Environment.CurrentManagedThreadId);
            published.Add(value);
        }, ex => throw new InvalidOperationException("Refresh failed", ex));

        refresh.Request();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        for (var index = 0; index < 1000; index++)
        {
            refresh.Request();
        }

        Assert.Equal(1, Volatile.Read(ref reads));
        release.Set();
        context.RunOne(); // Discards read 1 and starts exactly one follow-up.
        secondRelease.Set();
        context.RunOne(); // Publishes read 2.
        Assert.Equal(2, reads);
        Assert.Equal([2], published);
    }

    [Fact]
    public void DisposalSuppressesLateResultsAndPendingReads()
    {
        using var context = new OwnerContext();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reads = 0;
        var publications = 0;
        using var refresh = new CoalescedRefresh<int>(() =>
        {
            Interlocked.Increment(ref reads);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            return 1;
        }, _ => publications++, _ => publications++);
        refresh.Request();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        refresh.Request();
        refresh.Dispose();
        release.Set();
        context.RunOne();
        refresh.Request();
        Assert.Equal(1, reads);
        Assert.Equal(0, publications);
    }

    [Fact]
    public void PeriodicProbesReuseSlowReadInsteadOfStarvingPublication()
    {
        using var context = new OwnerContext();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reads = 0;
        var published = 0;
        using var refresh = new CoalescedRefresh<int>(() =>
        {
            Interlocked.Increment(ref reads);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            return 42;
        }, value => published = value, _ => throw new InvalidOperationException());
        refresh.Request();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        for (var index = 0; index < 100; index++)
        {
            refresh.Request(invalidate: false);
        }

        release.Set();
        context.RunOne();
        Assert.Equal(1, reads);
        Assert.Equal(42, published);
    }

    [Fact]
    public void FailedReadCanBeRetriedWithoutLosingPendingRequest()
    {
        using var context = new OwnerContext();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var secondRelease = new ManualResetEventSlim();
        var reads = 0;
        var failures = 0;
        var published = 0;
        using var refresh = new CoalescedRefresh<int>(() =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                throw new IOException("Folder temporarily unavailable");
            }

            Assert.True(secondRelease.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            return 2;
        }, value => published = value, _ => failures++);
        refresh.Request();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        refresh.Request();
        release.Set();
        context.RunOne();
        secondRelease.Set();
        context.RunOne();
        Assert.Equal(1, failures);
        Assert.Equal(2, reads);
        Assert.Equal(2, published);
    }

    [Fact]
    public void UnchangedAndReorderedEntriesRetainItems()
    {
        var menu = new TrayRecentItems<object>();
        var a = Entry("a.png");
        var b = Entry("b.png");
        var creates = 0;
        object Create(RecentMenuEntry _) { creates++; return new object(); }
        menu.Update([a, b], Create);
        var first = menu.Items[0];
        var second = menu.Items[1];

        for (var index = 0; index < 100; index++)
        {
            menu.Update([b, a], Create);
        }

        Assert.Same(second, menu.Items[0]);
        Assert.Same(first, menu.Items[1]);
        Assert.Equal(2, creates);
        menu.Update([], _ => throw new InvalidOperationException());
        Assert.Empty(menu.Items);
    }

    [Fact]
    public void ChangedCaptureTypeRecreatesItsItemForTheNewIcon()
    {
        var menu = new TrayRecentItems<object>();
        var first = Entry("capture.mp4");
        menu.Update([first], _ => new object());
        var old = Assert.Single(menu.Items);
        var second = RecentMenuEntry.ForCapture(first.Capture! with { Type = CaptureType.Video });
        menu.Update([second], _ => new object());
        Assert.Equal(first.Id, second.Id);
        Assert.NotSame(old, Assert.Single(menu.Items));
    }

    [Fact]
    public void DisplayOnlyChangesUpdateTextButRetainItem()
    {
        var menu = new TrayRecentItems<object>();
        var entry = Entry("capture.png");
        menu.Update([entry], _ => new object());
        var item = Assert.Single(menu.Items);
        var next = RecentMenuEntry.ForCapture(entry.Capture! with { CapturedAt = DateTimeOffset.UnixEpoch.AddDays(1) });
        RecentMenuEntry? displayed = null;
        menu.Update([next], _ => throw new InvalidOperationException(), (updated, shown) =>
        {
            Assert.Same(item, updated);
            displayed = shown;
        });
        Assert.Equal(next, displayed);
        Assert.Same(item, Assert.Single(menu.Items));

        // Unchanged afterwards: no further text update.
        menu.Update([next], _ => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException());
    }

    private static RecentMenuEntry Entry(string path) =>
        RecentMenuEntry.ForCapture(new RecentCapture(path, CaptureType.Screenshot, DateTimeOffset.UnixEpoch));

    private sealed class OwnerContext : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? _previous = Current;
        private readonly BlockingCollection<Action> _callbacks = new();

        public OwnerContext() => SetSynchronizationContext(this);
        public override void Post(SendOrPostCallback callback, object? state) =>
            _callbacks.Add(() => callback(state));
        public void RunOne()
        {
            Assert.True(_callbacks.TryTake(out var callback, TimeSpan.FromSeconds(10)));
            callback();
        }

        public void Dispose()
        {
            SetSynchronizationContext(_previous);
            _callbacks.Dispose();
        }
    }
}
