using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class RecentCaptureServiceTests
{
    [Fact]
    public void Record_PersistsNewestTenAcrossCaptureTypes()
    {
        var settings = new TestSettingsService();
        var files = new FakeFileSystem();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));
        var service = new RecentCaptureService(settings, files, time);

        for (var index = 0; index < 12; index++)
        {
            var path = $@"C:\Captures\capture-{index}.png";
            files.ExistingPaths.Add(path);
            service.Record(path, (CaptureType)(index % 3));
            time.Advance(TimeSpan.FromMinutes(1));
        }

        var reloaded = new RecentCaptureService(settings, files, time).GetRecentCaptures();

        Assert.Equal(10, reloaded.Count);
        Assert.Equal(@"C:\Captures\capture-11.png", reloaded[0].Path);
        Assert.Equal(CaptureType.Gif, reloaded[0].Type);
        Assert.DoesNotContain(reloaded, capture => capture.Path.EndsWith("capture-0.png", StringComparison.Ordinal));
        Assert.DoesNotContain(reloaded, capture => capture.Path.EndsWith("capture-1.png", StringComparison.Ordinal));
    }

    [Fact]
    public void Record_MovesAnExistingPathToTheFrontWithoutDuplicatingIt()
    {
        var settings = new TestSettingsService();
        var files = new FakeFileSystem();
        files.ExistingPaths.UnionWith([@"C:\Captures\first.png", @"C:\Captures\second.mp4"]);
        var service = new RecentCaptureService(settings, files, TimeProvider.System);

        service.Record(@"C:\Captures\first.png", CaptureType.Screenshot);
        service.Record(@"C:\Captures\second.mp4", CaptureType.Video);
        service.Record(@"c:\captures\FIRST.png", CaptureType.Screenshot);

        var captures = service.GetRecentCaptures();
        Assert.Equal(2, captures.Count);
        Assert.Equal(@"c:\captures\FIRST.png", captures[0].Path);
    }

    [Fact]
    public void GetRecentCaptures_PrunesMissingFilesAndPersistsTheResult()
    {
        var settings = new TestSettingsService();
        var files = new FakeFileSystem();
        files.ExistingPaths.Add(@"C:\Captures\kept.gif");
        var service = new RecentCaptureService(settings, files, TimeProvider.System);
        service.Record(@"C:\Captures\missing.mp4", CaptureType.Video);
        service.Record(@"C:\Captures\kept.gif", CaptureType.Gif);

        var captures = service.GetRecentCaptures();
        var reloaded = new RecentCaptureService(settings, files, TimeProvider.System).GetRecentCaptures();

        Assert.Equal(@"C:\Captures\kept.gif", Assert.Single(captures).Path);
        Assert.Equal(@"C:\Captures\kept.gif", Assert.Single(reloaded).Path);
    }

    [Fact]
    public async Task SlowMissingFileProbeDoesNotBlockRecordOrPruneANewerRecordingAtTheSamePath()
    {
        var files = new FakeFileSystem();
        var service = new RecentCaptureService(new TestSettingsService(), files, TimeProvider.System);
        service.Record("capture.mp4", CaptureType.Video);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        files.ExistsOverride = _ =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            return false; // The old file was absent when the probe started.
        };
        var reading = Task.Run(service.GetRecentCaptures, TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        try
        {
            await Task.Run(() => service.Record("capture.mp4", CaptureType.Gif), TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            release.Set();
        }

        var result = await reading.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(CaptureType.Gif, Assert.Single(result).Type);
        files.ExistsOverride = _ => true;
        Assert.Equal(CaptureType.Gif, Assert.Single(service.GetRecentCaptures()).Type);
    }

    [Fact]
    public async Task SlowPersistenceCoalescesMutationsWithoutHoldingTheStateLockOrWritingOlderStateLast()
    {
        var settings = new TestSettingsService();
        var files = new FakeFileSystem();
        var service = new RecentCaptureService(settings, files, TimeProvider.System);
        files.ExistsOverride = _ => true;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writes = 0;
        settings.BeforeSet = () =>
        {
            if (Interlocked.Increment(ref writes) == 1)
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            }
        };

        var writing = Task.Run(() => service.Record("old.png", CaptureType.Screenshot), TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        try
        {
            await Task.Run(() =>
            {
                service.Record("new.mp4", CaptureType.Video);
                service.Remove("old.png");
                Assert.Equal("new.mp4", Assert.Single(service.GetRecentCaptures()).Path);
            }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            release.Set();
        }

        await writing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(2, writes);
        Assert.Equal("new.mp4", Assert.Single(new RecentCaptureService(settings, files, TimeProvider.System).GetRecentCaptures()).Path);
    }

    [Fact]
    public void ChangesNotifyForRecordRemoveAndExternalDeletionButNotUnchangedReads()
    {
        var files = new FakeFileSystem();
        var service = new RecentCaptureService(new TestSettingsService(), files, TimeProvider.System);
        var changes = 0;
        service.Changed += (_, _) => changes++;
        files.ExistingPaths.Add("capture.png");
        service.Record("capture.png", CaptureType.Screenshot);
        Assert.Single(service.GetRecentCaptures());
        Assert.Equal(1, changes);
        files.ExistingPaths.Clear();
        Assert.Empty(service.GetRecentCaptures());
        Assert.Equal(2, changes);
        service.Remove("capture.png"); // Already gone.
        Assert.Equal(2, changes);
        service.Record("new.mp4", CaptureType.Video);
        service.Remove("new.mp4");
        Assert.Equal(4, changes);
    }

    private sealed class TestSettingsService : ISettingsService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);
        public Action? BeforeSet { get; set; }

        public AppTheme Theme { get; set; }
        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typedValue ? typedValue : defaultValue;

        public void Set<T>(string key, T value)
        {
            BeforeSet?.Invoke();
            _values[key] = value is null ? string.Empty : value;
        }
    }

    private sealed class FakeFileSystem : IFileSystem
    {
        public HashSet<string> ExistingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Func<string, bool>? ExistsOverride { get; set; }

        public bool FileExists(string path) => ExistsOverride?.Invoke(path) ?? ExistingPaths.Contains(path);
        public bool DirectoryExists(string path) => false;
        public void CreateDirectory(string path) { }
        public string GetFolderPath(Environment.SpecialFolder folder) => string.Empty;
        public IEnumerable<string> EnumerateFiles(string directory) => [];
        public DateTimeOffset GetFileLastWriteTime(string path) => DateTimeOffset.MinValue;
        public long GetFileSizeBytes(string path) => 0;
        public void DeleteFile(string path) { }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
