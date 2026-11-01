using TinyClips.Core.Capture;
using TinyClips.Core.Editing;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class ScreenshotAutomationDimensionsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TinyClipsDimensionsTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(ImageFormat.Png)]
    [InlineData(ImageFormat.Jpeg)]
    [InlineData(ImageFormat.Webp)]
    public async Task SavedScreenshotDimensions_ReflectConfiguredScale(ImageFormat format)
    {
        var storage = new MemorySettings();
        var settings = new CaptureSettings(storage)
        {
            UseDefaultSaveDirectories = false,
            ScreenshotSaveDirectory = _directory,
            ImageFormat = format,
            ScreenshotScale = 50,
        };
        var files = new ClipStorageService(settings, new FileNameService(settings), new TinyClips.Core.Services.FileSystem());
        var service = new ScreenshotService(
            new ScreenCaptureService(), new MonitorService(), files, settings, new ClipAnalyticsService(storage));
        var frame = new CapturedFrame(new byte[13 * 9 * 4], 13, 9);

        var path = await service.SaveFrameAsync(frame, applyBranding: false, TestContext.Current.CancellationToken);

        Assert.Equal((6, 4), ScreenshotBackgroundDecoder.ReadDimensions(path, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
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
