using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class ScreenshotTemporaryFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-artifacts", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void CapturePlan_PreservesEveryValidCombination(int mask)
    {
        var selection = new ScreenshotOutputSelection((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
        var normal = ScreenshotCapturePlan.Create(selection, fitsEditor: true);
        var oversized = ScreenshotCapturePlan.Create(selection, fitsEditor: false);
        Assert.Equal(selection, normal.Selection);
        Assert.Equal(selection.OpenEditor, normal.OpenEditor);
        Assert.False(normal.EditorUnavailable);
        Assert.False(oversized.OpenEditor);
        Assert.Equal(selection.OpenEditor, oversized.EditorUnavailable);
        Assert.Equal(selection, oversized.Selection);
    }

    [Fact]
    public void CapturePlan_RejectsCorruptAllOffState()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ScreenshotCapturePlan.Create(new(false, false, false), fitsEditor: true));
    }

    [Fact]
    public async Task FinalLeaseDeletesFile_WhilePurgeSkipsActiveWork()
    {
        var store = new ScreenshotTemporaryFileStore(_root);
        var work = store.Reserve("png");
        var editor = work.Retain();
        await using (var output = store.OpenNewFile(work))
            await output.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
        work.Dispose();

        Assert.True(File.Exists(editor.Path));
        Assert.Equal(new TemporaryFilesPurgeResult(0, 1),
            store.Purge(TimeSpan.Zero, DateTimeOffset.UtcNow.AddSeconds(1)));
        editor.Dispose();
        editor.Dispose();
        Assert.False(File.Exists(editor.Path));
        Assert.Throws<ObjectDisposedException>(() => editor.Retain());
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task StartupSweepRemovesOnlyOldScreenshots_WithoutMarkersOrClipboardInspection()
    {
        var store = new ScreenshotTemporaryFileStore(_root);
        Directory.CreateDirectory(_root);
        var old = Path.Combine(_root, $"screenshot-{Guid.NewGuid():N}.png");
        var recent = Path.Combine(_root, $"screenshot-{Guid.NewGuid():N}.webp");
        var unrelated = Path.Combine(_root, "unrelated.png");
        foreach (var path in new[] { old, recent, unrelated })
            await File.WriteAllTextAsync(path, "synthetic", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-2));

        Assert.Equal(new TemporaryFilesPurgeResult(1, 1),
            store.Purge(TimeSpan.FromDays(1), DateTimeOffset.UtcNow));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void SaveLocationChangesAndResetAreAllowedDuringTemporaryWork()
    {
        var settings = new CaptureSettings(new MemorySettings());
        var store = new ScreenshotTemporaryFileStore(_root);
        using var lease = store.Reserve("png");
        settings.ScreenshotSaveDirectory = _root;
        settings.VideoSaveDirectory = @"\\server\captures";
        settings.GifSaveDirectory = _root;
        settings.UseDefaultSaveDirectories = false;
        Assert.Equal(_root, settings.ScreenshotSaveDirectory);
        Assert.Equal(@"\\server\captures", settings.VideoSaveDirectory);
        settings.ResetToDefaults();
        Assert.True(settings.UseDefaultSaveDirectories);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, object?> _values = new();
        public TinyClips.Core.Models.AppTheme Theme { get; set; }
        public string SaveDirectory { get; set; } = string.Empty;
        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;
        public void Set<T>(string key, T value) => _values[key] = value;
    }
}
