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
        Assert.Equal(["ShowTrayPopup", "if", "ShowNearCursor"], calls);
        Assert.Contains("_trayPopup.ShowNearCursor(_trayPopupWidth, TrayPopupHeight)", body);

        // The popup has one construction site, not a second click/menu-opening path.
        Assert.Equal(2, Regex.Matches(source, @"\bBuildTrayPopupContent\(").Count);
        Assert.Contains("flyout.Closed += OnTrayRecentClosed", source);
        Assert.Contains("_isTrayTilesRefreshPending = true", source);
        Assert.Contains("_isTrayWidthRefreshPending = true", source);
        Assert.Contains("_trayWidthInvalidation = new CoalescedAction", source);
        Assert.Contains("label.RegisterPropertyChangedCallback(property", source);
        Assert.Contains("TextBlock.FontFamilyProperty", source);
        Assert.Contains("TextBlock.FontSizeProperty", source);
        Assert.Contains("TextBlock.FontWeightProperty", source);
        Assert.Contains("DispatcherQueuePriority.Low", source);
        Assert.Contains("refreshTimer.Tick -= OnTrayRefreshTick", source);
        Assert.Contains("drafts.Changed -= OnTrayDraftsChanged", source);
        Assert.Contains("tracker.Changed -= OnTrayProjectTrackerChanged", source);
        Assert.Contains("settingsWindow.ViewModel.ThemeChanged -= ApplyTrayTheme", source);
        Assert.Contains("|| flyout.IsOpen", source);
        Assert.Contains("_trayRefreshTimer.Interval = TimeSpan.FromSeconds(30)", source);
        Assert.Contains("_trayRecentService.Changed += OnRecentCapturesChanged", source);
        // The cache API itself has a synchronous disk prefix, even though it returns a Task.
        Assert.Contains(
            "await Task.Run(() => Services.GetRequiredService<IThumbnailCache>().GetThumbnailPathAsync(entry))",
            source);
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
    public void UnchangedAndReorderedEntriesRetainItemsAndDoNotRestartImages()
    {
        var menu = new TrayRecentItems<object>();
        var a = Key("a.png");
        var b = Key("b.png");
        var creates = 0;
        object Create(RecentMenuEntry _) { creates++; return new object(); }
        menu.Update([a, b], Create);
        var first = menu.Items[0];
        var second = menu.Items[1];
        Assert.Same(first, menu.StartNextImage());
        Assert.Same(second, menu.StartNextImage());
        Assert.Null(menu.StartNextImage());

        for (var index = 0; index < 100; index++)
        {
            menu.Update([b, a], Create);
        }

        Assert.Same(second, menu.Items[0]);
        Assert.Same(first, menu.Items[1]);
        Assert.Equal(2, creates);
        Assert.Null(menu.StartNextImage());
    }

    [Fact]
    public void SamePathMetadataAndPosterChangesInvalidateLateImagesAndRemovalDropsPendingWork()
    {
        var menu = new TrayRecentItems<object>();
        var original = Key("a.png");
        menu.Update([original, Key("removed.mp4")], _ => new object());
        var old = menu.StartNextImage()!;
        menu.Update([original with { Modified = original.Modified.AddSeconds(1) }], _ => new object());
        Assert.False(menu.IsCurrent(old));
        var changed = Assert.Single(menu.Items);
        Assert.Same(changed, menu.StartNextImage());
        Assert.Null(menu.StartNextImage()); // Removed entry never starts.

        menu.Update([changed.Key with { Poster = "same-path|new-version" }], _ => new object());
        Assert.False(menu.IsCurrent(changed));
        Assert.NotSame(changed.Item, Assert.Single(menu.Items).Item);
        menu.Update([], _ => throw new InvalidOperationException());
        Assert.Empty(menu.Items);
        Assert.Null(menu.StartNextImage());
    }

    [Fact]
    public void TransientImageFailureRetriesOnlyOnLaterRefresh()
    {
        var menu = new TrayRecentItems<object>();
        var key = Key("a.png");
        menu.Update([key], _ => new object());
        var slot = menu.StartNextImage()!;
        slot.HasImageFailed = true;
        Assert.Null(menu.StartNextImage());
        menu.Update([key], _ => throw new InvalidOperationException());
        Assert.Same(slot, menu.StartNextImage());
    }

    [Fact]
    public void SameIdWithChangedDisplayMetadataIsNotMistakenForAnUnchangedEntry()
    {
        var menu = new TrayRecentItems<object>();
        var first = Key("capture.mp4");
        menu.Update([first], _ => new object());
        var old = Assert.Single(menu.Items);
        var capture = first.Entry.Capture!;
        var second = first with
        {
            Entry = RecentMenuEntry.ForCapture(capture with { Type = CaptureType.Video, CapturedAt = capture.CapturedAt.AddHours(1) }),
        };
        menu.Update([second], _ => new object());
        Assert.Equal(first.Entry.Id, second.Entry.Id);
        Assert.NotSame(old.Item, Assert.Single(menu.Items).Item);
        Assert.False(menu.IsCurrent(old));
    }

    [Fact]
    public void DisplayOnlyChangesUpdateTextButRetainItemAndInFlightOrCompletedImage()
    {
        var menu = new TrayRecentItems<object>();
        var key = Key("capture.png");
        menu.Update([key], _ => new object());
        var slot = menu.StartNextImage()!;
        var next = key with
        {
            Entry = RecentMenuEntry.ForCapture(key.Entry.Capture! with { CapturedAt = DateTimeOffset.UnixEpoch.AddDays(1) }),
        };
        RecentMenuEntry? displayed = null;
        menu.Update([next], _ => throw new InvalidOperationException(), (item, entry) =>
        {
            Assert.Same(slot.Item, item);
            displayed = entry;
        });
        Assert.Equal(next.Entry, displayed);
        Assert.Same(slot, Assert.Single(menu.Items));
        Assert.True(menu.IsCurrent(slot));
        Assert.Null(menu.StartNextImage());
    }

    private static TrayRecentItemKey Key(string path) =>
        new(RecentMenuEntry.ForCapture(new RecentCapture(path, CaptureType.Screenshot, DateTimeOffset.UnixEpoch)),
            123, DateTimeOffset.UnixEpoch, null);

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
