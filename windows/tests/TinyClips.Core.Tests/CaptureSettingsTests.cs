using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class CaptureSettingsTests
{
    [Fact]
    public void Defaults_ReturnDocumentedValues()
    {
        var settings = CreateSettings();

        Assert.True(settings.CopyScreenshotToClipboard);
        Assert.Equal(10.0, settings.GifFrameRate);
        Assert.Equal(30, settings.VideoFrameRate);
        Assert.Equal(100, settings.ScreenshotScale);
        Assert.Equal("TinyClips {date} at {time}", settings.FileNameTemplate);
        Assert.True(settings.ShowTrimmer);
        Assert.False(settings.WebcamEnabled);
        Assert.Equal(string.Empty, settings.SelectedWebcamId);
        Assert.Equal(WebcamShape.Circle, settings.WebcamShape);
        Assert.Equal(WebcamSizePreset.Medium, settings.WebcamSizePreset);
        Assert.Equal(WebcamCornerPosition.BottomRight, settings.WebcamCornerPosition);
        Assert.Null(settings.WebcamCornerRadius);
        Assert.Equal(MultiMonitorCaptureMode.Picker, settings.MultiMonitorCaptureMode);
        Assert.False(settings.UploadcareEnabled);
        Assert.Equal(string.Empty, settings.UploadcarePublicKey);
        Assert.False(settings.UploadcareAutoUpload);
        Assert.False(settings.UploadcareCopyUrl);
        Assert.False(settings.ShouldShowCapturePickerAfterCapture(CaptureType.Screenshot));
        Assert.False(settings.ShouldShowCapturePickerAfterCapture(CaptureType.Video));
        Assert.False(settings.ShouldShowCapturePickerAfterCapture(CaptureType.Gif));
    }

    [Fact]
    public void RoundTrip_StoresExpectedValues()
    {
        var settings = CreateSettings();

        settings.CopyScreenshotToClipboard = false;
        settings.GifFrameRate = 24.5;
        settings.VideoFrameRate = 60;
        settings.FileNameTemplate = "Custom {date}";
        settings.MultiMonitorCaptureMode = MultiMonitorCaptureMode.UnderCursor;
        settings.WebcamEnabled = true;
        settings.SelectedWebcamId = "webcam-1";
        settings.WebcamShape = WebcamShape.RoundedRectangle;
        settings.WebcamSizePreset = WebcamSizePreset.Large;
        settings.WebcamCornerPosition = WebcamCornerPosition.TopLeft;
        settings.WebcamCornerRadius = 16.0;
        settings.UploadcareEnabled = true;
        settings.UploadcarePublicKey = "public-key";
        settings.UploadcareAutoUpload = true;
        settings.UploadcareCopyUrl = true;

        Assert.False(settings.CopyScreenshotToClipboard);
        Assert.Equal(24.5, settings.GifFrameRate);
        Assert.Equal(60, settings.VideoFrameRate);
        Assert.Equal("Custom {date}", settings.FileNameTemplate);
        Assert.Equal(MultiMonitorCaptureMode.UnderCursor, settings.MultiMonitorCaptureMode);
        Assert.True(settings.WebcamEnabled);
        Assert.Equal("webcam-1", settings.SelectedWebcamId);
        Assert.Equal(WebcamShape.RoundedRectangle, settings.WebcamShape);
        Assert.Equal(WebcamSizePreset.Large, settings.WebcamSizePreset);
        Assert.Equal(WebcamCornerPosition.TopLeft, settings.WebcamCornerPosition);
        Assert.Equal(16.0, settings.WebcamCornerRadius);
        Assert.True(settings.UploadcareEnabled);
        Assert.Equal("public-key", settings.UploadcarePublicKey);
        Assert.True(settings.UploadcareAutoUpload);
        Assert.True(settings.UploadcareCopyUrl);
    }

    [Fact]
    public void WebcamSettings_ParsePersistedStringsAndCornerRadiusSentinel()
    {
        var settingsService = new TestSettingsService();
        settingsService.Set("webcamShape", "rectangle");
        settingsService.Set("webcamSize", "small");
        settingsService.Set("webcamCorner", "topRight");
        settingsService.Set("webcamCornerRadius", -1.0);
        var settings = new CaptureSettings(settingsService);

        Assert.Equal(WebcamShape.Rectangle, settings.WebcamShape);
        Assert.Equal(WebcamSizePreset.Small, settings.WebcamSizePreset);
        Assert.Equal(WebcamCornerPosition.TopRight, settings.WebcamCornerPosition);
        Assert.Null(settings.WebcamCornerRadius);

        settings.WebcamShape = WebcamShape.Circle;
        settings.WebcamSizePreset = WebcamSizePreset.Medium;
        settings.WebcamCornerPosition = WebcamCornerPosition.BottomLeft;
        settings.WebcamCornerRadius = null;

        Assert.Equal(WebcamShape.Circle, settings.WebcamShape);
        Assert.Equal(WebcamSizePreset.Medium, settings.WebcamSizePreset);
        Assert.Equal(WebcamCornerPosition.BottomLeft, settings.WebcamCornerPosition);
        Assert.Null(settings.WebcamCornerRadius);
    }

    [Fact]
    public void MultiMonitorCaptureMode_UsesLegacyAlwaysCaptureMainDisplay_WhenModeIsMissing()
    {
        var mainDisplaySettingsService = new TestSettingsService();
        mainDisplaySettingsService.Set("alwaysCaptureMainDisplay", true);
        var mainDisplaySettings = new CaptureSettings(mainDisplaySettingsService);

        Assert.Equal(MultiMonitorCaptureMode.MainDisplay, mainDisplaySettings.MultiMonitorCaptureMode);

        var pickerSettingsService = new TestSettingsService();
        pickerSettingsService.Set("alwaysCaptureMainDisplay", false);
        var pickerSettings = new CaptureSettings(pickerSettingsService);

        Assert.Equal(MultiMonitorCaptureMode.Picker, pickerSettings.MultiMonitorCaptureMode);
    }

    [Fact]
    public void ImageFormat_RoundTripsThroughScreenshotFormat()
    {
        var settings = CreateSettings();

        settings.ImageFormat = ImageFormat.Png;

        Assert.Equal("png", settings.ScreenshotFormat);
        Assert.Equal(ImageFormat.Png, settings.ImageFormat);

        settings.ImageFormat = ImageFormat.Jpeg;

        Assert.Equal("jpg", settings.ScreenshotFormat);
        Assert.Equal(ImageFormat.Jpeg, settings.ImageFormat);
    }

    [Fact]
    public void ShouldCopyToClipboard_And_ShouldShowCapturePicker_ReturnPerTypeValues()
    {
        var settings = CreateSettings();

        settings.CopyScreenshotToClipboard = true;
        settings.CopyVideoToClipboard = false;
        settings.CopyGifToClipboard = true;
        settings.ShowScreenshotCapturePicker = true;
        settings.ShowVideoCapturePicker = false;
        settings.ShowGifCapturePicker = true;

        Assert.True(settings.ShouldCopyToClipboard(CaptureType.Screenshot));
        Assert.False(settings.ShouldCopyToClipboard(CaptureType.Video));
        Assert.True(settings.ShouldCopyToClipboard(CaptureType.Gif));

        Assert.True(settings.ShouldShowCapturePicker(CaptureType.Screenshot));
        Assert.False(settings.ShouldShowCapturePicker(CaptureType.Video));
        Assert.True(settings.ShouldShowCapturePicker(CaptureType.Gif));
    }

    [Fact]
    public void CapturePickerAfterCapture_IsConfiguredPerTypeAndRequiresTheBeforePicker()
    {
        var settings = CreateSettings();

        settings.ShowScreenshotCapturePickerAfterCapture = true;
        settings.ShowVideoCapturePickerAfterCapture = true;
        settings.ShowGifCapturePickerAfterCapture = true;

        Assert.True(settings.ShouldShowCapturePickerAfterCapture(CaptureType.Screenshot));
        Assert.True(settings.ShouldShowCapturePickerAfterCapture(CaptureType.Video));
        Assert.True(settings.ShouldShowCapturePickerAfterCapture(CaptureType.Gif));

        settings.ShowVideoCapturePicker = false;

        Assert.False(settings.ShowVideoCapturePickerAfterCapture);
        Assert.False(settings.ShouldShowCapturePickerAfterCapture(CaptureType.Video));
        Assert.True(settings.ShouldShowCapturePickerAfterCapture(CaptureType.Screenshot));
        Assert.True(settings.ShouldShowCapturePickerAfterCapture(CaptureType.Gif));
    }

    [Fact]
    public void MouseClickStyleFor_GifAndScreenshot_UsesExpectedValues()
    {
        var settings = CreateSettings();

        settings.GifMouseClicksUseVideoSettings = true;
        settings.VideoMouseClickColorHex = "#112233";
        settings.VideoMouseClickSize = 12.5;
        settings.VideoMouseClickStrokeWidth = 4.5;
        settings.VideoMouseClickOpacity = 0.2;
        settings.VideoMouseClickDuration = 1.25;

        var gifStyle = settings.MouseClickOverlayStyleFor(CaptureType.Gif);

        Assert.Equal("#112233", gifStyle.ColorHex);
        Assert.Equal(12.5, gifStyle.Size);
        Assert.Equal(4.5, gifStyle.StrokeWidth);
        Assert.Equal(0.2, gifStyle.Opacity);
        Assert.Equal(1.25, gifStyle.DurationSeconds);

        var screenshotStyle = settings.MouseClickOverlayStyleFor(CaptureType.Screenshot);

        Assert.Equal("#FFFFFF", screenshotStyle.ColorHex);
        Assert.Equal(32.0, screenshotStyle.Size);
        Assert.Equal(3.0, screenshotStyle.StrokeWidth);
        Assert.Equal(0.85, screenshotStyle.Opacity);
        Assert.Equal(0.45, screenshotStyle.DurationSeconds);
    }

    [Fact]
    public void ShouldShowMouseClickVisuals_And_SetShowMouseClickVisuals_HonorsGifMode()
    {
        var settings = CreateSettings();

        settings.GifMouseClicksUseVideoSettings = true;
        settings.ShowMouseClickVisualsInVideo = true;
        settings.ShowMouseClickVisualsInGif = false;

        Assert.True(settings.ShouldShowMouseClickVisuals(CaptureType.Gif));

        settings.SetShowMouseClickVisuals(false, CaptureType.Gif);

        Assert.False(settings.ShowMouseClickVisualsInVideo);

        settings.GifMouseClicksUseVideoSettings = false;
        settings.ShowMouseClickVisualsInGif = true;

        Assert.True(settings.ShouldShowMouseClickVisuals(CaptureType.Gif));

        settings.SetShowMouseClickVisuals(false, CaptureType.Gif);

        Assert.False(settings.ShowMouseClickVisualsInGif);
    }

    [Fact]
    public void ResetToDefaults_ClearsAnalyticsCacheSoStaleDataCannotReappear()
    {
        var settingsService = new TestSettingsService();
        var analytics = new ClipAnalyticsService(settingsService);
        var settings = new CaptureSettings(settingsService, analytics);

        settings.FileNameTemplate = "Changed";
        settings.CopyScreenshotToClipboard = false;
        settings.GifFrameRate = 99;
        analytics.RecordCapture(CaptureType.Screenshot);
        analytics.RecordCapture(CaptureType.Video);

        settings.ResetToDefaults();

        Assert.Equal("TinyClips {date} at {time}", settings.FileNameTemplate);
        Assert.True(settings.CopyScreenshotToClipboard);
        Assert.Equal(10.0, settings.GifFrameRate);
        Assert.Equal(string.Empty, settingsService.Get("captureAnalyticsHistoryV1", string.Empty));

        // Regression check: recording after reset must not resurrect the cleared history by
        // re-serializing a stale in-memory cache that ResetToDefaults() didn't know about.
        analytics.RecordCapture(CaptureType.Gif);
        var today = Assert.Single(analytics.GetDailyCounts(1));
        Assert.Equal(0, today.ScreenshotCount);
        Assert.Equal(0, today.VideoCount);
        Assert.Equal(1, today.GifCount);
    }

    [Fact]
    public void TeleprompterSettings_DefaultToHiddenOverlayWithNoTranscript()
    {
        var settings = CreateSettings();

        Assert.False(settings.TeleprompterEnabled);
        Assert.Equal(string.Empty, settings.TeleprompterTranscript);
        Assert.Equal(50.0, settings.TeleprompterScrollSpeed);
        Assert.Equal(-1.0, settings.TeleprompterPosX);
        Assert.Equal(-1.0, settings.TeleprompterPosY);
        Assert.Equal(string.Empty, settings.TeleprompterMonitorDeviceName);
    }

    [Fact]
    public void TeleprompterSettings_RoundTripThroughPersistedKeys()
    {
        var settingsService = new TestSettingsService();
        var settings = new CaptureSettings(settingsService);

        settings.TeleprompterEnabled = true;
        settings.TeleprompterTranscript = "Hello\nworld";
        settings.TeleprompterScrollSpeed = 120.5;
        settings.TeleprompterPosX = 640.0;
        settings.TeleprompterPosY = 42.25;
        settings.TeleprompterMonitorDeviceName = @"\\.\DISPLAY2";

        Assert.True(settings.TeleprompterEnabled);
        Assert.Equal("Hello\nworld", settings.TeleprompterTranscript);
        Assert.Equal(120.5, settings.TeleprompterScrollSpeed);
        Assert.Equal(640.0, settings.TeleprompterPosX);
        Assert.Equal(42.25, settings.TeleprompterPosY);
        Assert.Equal(@"\\.\DISPLAY2", settings.TeleprompterMonitorDeviceName);

        Assert.True(settingsService.Get("teleprompterEnabled", false));
        Assert.Equal("Hello\nworld", settingsService.Get("teleprompterTranscript", string.Empty));
        Assert.Equal(120.5, settingsService.Get("teleprompterScrollSpeed", 50.0));
        Assert.Equal(640.0, settingsService.Get("teleprompterPosX", -1.0));
        Assert.Equal(42.25, settingsService.Get("teleprompterPosY", -1.0));
        Assert.Equal(@"\\.\DISPLAY2", settingsService.Get("teleprompterMonitorDeviceName", string.Empty));
    }

    [Fact]
    public void ResetToDefaults_RestoresTeleprompterDefaults()
    {
        var settings = CreateSettings();

        settings.TeleprompterEnabled = true;
        settings.TeleprompterTranscript = "Script";
        settings.TeleprompterScrollSpeed = 180.0;
        settings.TeleprompterPosX = 100.0;
        settings.TeleprompterPosY = 200.0;
        settings.TeleprompterMonitorDeviceName = @"\\.\DISPLAY2";

        settings.ResetToDefaults();

        Assert.False(settings.TeleprompterEnabled);
        Assert.Equal(string.Empty, settings.TeleprompterTranscript);
        Assert.Equal(50.0, settings.TeleprompterScrollSpeed);
        Assert.Equal(-1.0, settings.TeleprompterPosX);
        Assert.Equal(-1.0, settings.TeleprompterPosY);
        Assert.Equal(string.Empty, settings.TeleprompterMonitorDeviceName);
    }

    private static ICaptureSettings CreateSettings() => new CaptureSettings(new TestSettingsService());

    private sealed class TestSettingsService : ISettingsService, ILargeTextSettingsService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

        public AppTheme Theme { get; set; }

        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue)
        {
            if (_values.TryGetValue(key, out var value))
            {
                if (value is T typedValue)
                {
                    return typedValue;
                }

                if (value is string stringValue && typeof(T).IsEnum)
                {
                    return (T)Enum.Parse(typeof(T), stringValue, true);
                }
            }

            return defaultValue;
        }

        public void Set<T>(string key, T value)
        {
            _values[key] = value is null ? string.Empty : value;
        }

        public string GetLargeText(string key, string defaultValue) => Get(key, defaultValue);

        public void SetLargeText(string key, string value) => Set(key, value);
    }
}
