using TinyClips.Core.Capture;
using TinyClips.Core.Editing;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class ScreenshotTemporaryEncodingTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-artifacts", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(ImageFormat.Png, "png")]
    [InlineData(ImageFormat.Jpeg, "jpg")]
    [InlineData(ImageFormat.Webp, "webp")]
    public async Task WriteFrame_UsesConfiguredFormatAndScaleWithoutCreatingCaptureFolder(ImageFormat format, string extension)
    {
        var memory = new MemorySettings();
        var captureFolder = Path.Combine(_root, "capture");
        var settings = new CaptureSettings(memory)
        {
            UseDefaultSaveDirectories = false,
            ScreenshotSaveDirectory = captureFolder,
            SaveScreenshotToDisk = false,
            ImageFormat = format,
            ScreenshotScale = 50,
        };
        var storage = new ClipStorageService(settings, new FileNameService(settings), new TinyClips.Core.Services.FileSystem());
        var analytics = new ClipAnalyticsService(memory);
        var service = new ScreenshotService(new ScreenCaptureService(), new MonitorService(), storage, settings, analytics);
        var temporary = new ScreenshotTemporaryFileStore(Path.Combine(_root, "temp"));
        using var lease = temporary.Reserve(extension);

        await using (var stream = temporary.OpenNewFile(lease))
        {
            await service.WriteFrameAsync(new CapturedFrame(new byte[13 * 9 * 4], 13, 9), stream,
                applyBranding: false, TestContext.Current.CancellationToken);
        }

        Assert.False(Directory.Exists(captureFolder));
        Assert.True(File.Exists(lease.Path));
        Assert.Equal((6, 4), ScreenshotBackgroundDecoder.ReadDimensions(lease.Path, TestContext.Current.CancellationToken));
        Assert.Equal(0, analytics.GetLifetimeTotals().ScreenshotCount);
    }

    [Fact]
    public async Task WriteFrame_OverwritesExistingFileDirectly()
    {
        var memory = new MemorySettings();
        var settings = new CaptureSettings(memory);
        var storage = new ClipStorageService(settings, new FileNameService(settings), new TinyClips.Core.Services.FileSystem());
        var service = new ScreenshotService(new ScreenCaptureService(), new MonitorService(), storage, settings, new ClipAnalyticsService(memory));
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "existing.png");
        await File.WriteAllTextAsync(path, "keep", TestContext.Current.CancellationToken);

        await service.WriteFrameAsync(
            new CapturedFrame(new byte[4 * 4 * 4], 4, 4), path, applyBranding: false, TestContext.Current.CancellationToken);
        Assert.Equal((4, 4), ScreenshotBackgroundDecoder.ReadDimensions(path, TestContext.Current.CancellationToken));
        Assert.Equal(path, Assert.Single(Directory.GetFiles(_root)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);
        public AppTheme Theme { get; set; }
        public string SaveDirectory { get; set; } = string.Empty;
        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;
        public void Set<T>(string key, T value) => _values[key] = value is null ? string.Empty : value;
    }
}
