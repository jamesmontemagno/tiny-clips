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

    private sealed class TestSettingsService : ISettingsService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

        public AppTheme Theme { get; set; }
        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typedValue ? typedValue : defaultValue;

        public void Set<T>(string key, T value) => _values[key] = value is null ? string.Empty : value;
    }

    private sealed class FakeFileSystem : IFileSystem
    {
        public HashSet<string> ExistingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => ExistingPaths.Contains(path);
        public void CreateDirectory(string path) { }
        public string GetFolderPath(Environment.SpecialFolder folder) => string.Empty;
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
