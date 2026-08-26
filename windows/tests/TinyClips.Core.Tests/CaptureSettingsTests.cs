using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class CaptureSettingsTests
{
    [Fact]
    public void Defaults_ReturnDocumentedValues()
    {
        var settingsService = new TestSettingsService();
        var settings = new CaptureSettings(settingsService);

        Assert.True(settings.CopyScreenshotToClipboard);
        Assert.True(settings.UseDefaultSaveDirectories);
        Assert.Equal(CaptureSettings.DefaultSaveDirectory(CaptureType.Screenshot), settings.ScreenshotSaveDirectory);
        Assert.Equal(CaptureSettings.DefaultSaveDirectory(CaptureType.Video), settings.VideoSaveDirectory);
        Assert.Equal(CaptureSettings.DefaultSaveDirectory(CaptureType.Gif), settings.GifSaveDirectory);
        Assert.Equal(
            CaptureSettings.DefaultSaveDirectory(CaptureType.Screenshot),
            settingsService.Get("screenshotSaveDirectory", string.Empty));
        Assert.Equal(
            CaptureSettings.DefaultSaveDirectory(CaptureType.Video),
            settingsService.Get("videoSaveDirectory", string.Empty));
        Assert.Equal(
            CaptureSettings.DefaultSaveDirectory(CaptureType.Gif),
            settingsService.Get("gifSaveDirectory", string.Empty));
        Assert.Equal(10.0, settings.GifFrameRate);
        Assert.Equal(30, settings.VideoFrameRate);
        Assert.False(settings.UseGpuRecordingPipeline);
        Assert.True(settings.KeepDisplayAwakeWhileRecording);
        Assert.Equal(100, settings.ScreenshotScale);
        Assert.Equal("TinyClips {date} at {time}", settings.FileNameTemplate);
        Assert.True(settings.ShowTrimmer);
        Assert.True(settings.MicrophoneLimiterEnabled);
        Assert.Equal(0, settings.AudioOffsetMilliseconds);
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
    }

    [Fact]
    public void SaveDirectoryDefaults_ArePersistedForExistingSettings()
    {
        var settingsService = new TestSettingsService();
        settingsService.Set("saveDirectoryFoldersMigrated", true);

        _ = new CaptureSettings(settingsService);

        Assert.Equal(
            CaptureSettings.DefaultSaveDirectory(CaptureType.Screenshot),
            settingsService.Get("screenshotSaveDirectory", string.Empty));
        Assert.Equal(
            CaptureSettings.DefaultSaveDirectory(CaptureType.Video),
            settingsService.Get("videoSaveDirectory", string.Empty));
        Assert.Equal(
            CaptureSettings.DefaultSaveDirectory(CaptureType.Gif),
            settingsService.Get("gifSaveDirectory", string.Empty));
    }

    [Fact]
    public void SaveDirectoryMigration_PreservesHotkeys_AndResetRestoresDefaults()
    {
        var settingsService = new TestSettingsService { SaveDirectory = @"C:\Captures" };
        settingsService.Set("screenshotHotKeyCode", 80);
        settingsService.Set("screenshotHotKeyModifiers", 4);
        settingsService.Set("videoHotKeyCode", 81);
        settingsService.Set("videoHotKeyModifiers", 5);
        settingsService.Set("gifHotKeyCode", 82);
        settingsService.Set("gifHotKeyModifiers", 7);

        var settings = new CaptureSettings(settingsService);

        Assert.Equal(80, settings.ScreenshotHotKeyCode);
        Assert.Equal(4, settings.ScreenshotHotKeyModifiers);
        Assert.Equal(81, settings.VideoHotKeyCode);
        Assert.Equal(5, settings.VideoHotKeyModifiers);
        Assert.Equal(82, settings.GifHotKeyCode);
        Assert.Equal(7, settings.GifHotKeyModifiers);

        settings.ResetToDefaults();

        Assert.Equal(53, settings.ScreenshotHotKeyCode);
        Assert.Equal(6, settings.ScreenshotHotKeyModifiers);
        Assert.Equal(54, settings.VideoHotKeyCode);
        Assert.Equal(6, settings.VideoHotKeyModifiers);
        Assert.Equal(55, settings.GifHotKeyCode);
        Assert.Equal(6, settings.GifHotKeyModifiers);
        Assert.Equal(CaptureSettings.DefaultSaveDirectory(CaptureType.Screenshot), settings.ScreenshotSaveDirectory);
        Assert.Equal(CaptureSettings.DefaultSaveDirectory(CaptureType.Video), settings.VideoSaveDirectory);
        Assert.Equal(CaptureSettings.DefaultSaveDirectory(CaptureType.Gif), settings.GifSaveDirectory);
        Assert.False(settings.ShouldShowCapturePickerAfterCapture(CaptureType.Screenshot));
        Assert.False(settings.ShouldShowCapturePickerAfterCapture(CaptureType.Video));
        Assert.False(settings.ShouldShowCapturePickerAfterCapture(CaptureType.Gif));
    }

    [Fact]
    public void MicrophoneLimiterEnabled_RoundTripsAndResetsToTrue()
    {
        var settingsService = new TestSettingsService();
        var settings = new CaptureSettings(settingsService);

        settings.MicrophoneLimiterEnabled = false;

        Assert.False(settings.MicrophoneLimiterEnabled);
        Assert.False(settingsService.Get("microphoneLimiterEnabled", true));

        settings.ResetToDefaults();

        Assert.True(settings.MicrophoneLimiterEnabled);
    }

    [Fact]
    public void AudioOffsetMilliseconds_RoundTripsAndResetsToZero()
    {
        var settingsService = new TestSettingsService();
        var settings = new CaptureSettings(settingsService);

        settings.AudioOffsetMilliseconds = -150;

        Assert.Equal(-150, settings.AudioOffsetMilliseconds);
        Assert.Equal(-150, settingsService.Get("audioOffsetMilliseconds", 0));

        settings.ResetToDefaults();

        Assert.Equal(0, settings.AudioOffsetMilliseconds);
    }

    [Theory]
    [InlineData(600, 500)]
    [InlineData(500, 500)]
    [InlineData(-500, -500)]
    [InlineData(-9999, -500)]
    public void AudioOffsetMilliseconds_ClampsToSupportedRange(int requested, int expected)
    {
        var settingsService = new TestSettingsService();
        var settings = new CaptureSettings(settingsService);

        settings.AudioOffsetMilliseconds = requested;

        Assert.Equal(expected, settings.AudioOffsetMilliseconds);
        Assert.Equal(expected, settingsService.Get("audioOffsetMilliseconds", 0));
    }

    [Fact]
    public void AudioOffsetMilliseconds_ClampsOutOfRangeStoredValueOnRead()
    {
        // A value written by an older/newer build or edited by hand must never leak outside the range.
        var settingsService = new TestSettingsService();
        settingsService.Set("audioOffsetMilliseconds", 2000);
        var settings = new CaptureSettings(settingsService);

        Assert.Equal(CaptureSettings.MaxAudioOffsetMilliseconds, settings.AudioOffsetMilliseconds);
    }

    [Fact]
    public void TeleprompterDisplaySizes_DefaultToMediumAndRoundTrip()
    {
        var settingsService = new TestSettingsService();
        var settings = new CaptureSettings(settingsService);

        Assert.Equal(TeleprompterDisplaySize.Medium, settings.TeleprompterFontSize);
        Assert.Equal(TeleprompterDisplaySize.Medium, settings.TeleprompterPanelHeight);

        settings.TeleprompterFontSize = TeleprompterDisplaySize.Large;
        settings.TeleprompterPanelHeight = TeleprompterDisplaySize.Small;

        Assert.Equal(TeleprompterDisplaySize.Large, settings.TeleprompterFontSize);
        Assert.Equal(TeleprompterDisplaySize.Small, settings.TeleprompterPanelHeight);
        // Persisted as the same lowercase tokens the macOS app uses.
        Assert.Equal("large", settingsService.Get("teleprompterFontSize", string.Empty));
        Assert.Equal("small", settingsService.Get("teleprompterPanelHeight", string.Empty));

        settings.ResetToDefaults();

        Assert.Equal(TeleprompterDisplaySize.Medium, settings.TeleprompterFontSize);
        Assert.Equal(TeleprompterDisplaySize.Medium, settings.TeleprompterPanelHeight);
    }

    [Fact]
    public void TeleprompterDisplaySizes_UnknownPersistedValueFallsBackToMedium()
    {
        var settingsService = new TestSettingsService();
        settingsService.Set("teleprompterFontSize", "gigantic");
        var settings = new CaptureSettings(settingsService);

        Assert.Equal(TeleprompterDisplaySize.Medium, settings.TeleprompterFontSize);
    }

    [Theory]
    [InlineData(TeleprompterDisplaySize.Small, 20, 120)]
    [InlineData(TeleprompterDisplaySize.Medium, 24, 140)]
    [InlineData(TeleprompterDisplaySize.Large, 30, 220)]
    public void TeleprompterDisplaySize_PresetsMatchMacParity(TeleprompterDisplaySize size, double fontSize, double panelHeight)
    {
        Assert.Equal(fontSize, size.FontSize());
        Assert.Equal(panelHeight, size.PanelHeight());
        Assert.Equal(panelHeight - TeleprompterDisplaySizeExtensions.PanelVerticalPaddingDip, size.ViewportHeight());
    }

    [Fact]
    public void RoundTrip_StoresExpectedValues()
    {
        var settings = CreateSettings();

        settings.CopyScreenshotToClipboard = false;
        settings.UseDefaultSaveDirectories = false;
        settings.ScreenshotSaveDirectory = @"C:\Captures\Screenshots";
        settings.VideoSaveDirectory = @"C:\Captures\Videos";
        settings.GifSaveDirectory = @"C:\Captures\Gifs";
        settings.GifFrameRate = 24.5;
        settings.VideoFrameRate = 60;
        settings.KeepDisplayAwakeWhileRecording = false;
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
        Assert.False(settings.UseDefaultSaveDirectories);
        Assert.Equal(@"C:\Captures\Screenshots", settings.ScreenshotSaveDirectory);
        Assert.Equal(@"C:\Captures\Videos", settings.VideoSaveDirectory);
        Assert.Equal(@"C:\Captures\Gifs", settings.GifSaveDirectory);
        Assert.Equal(24.5, settings.GifFrameRate);
        Assert.Equal(60, settings.VideoFrameRate);
        Assert.False(settings.KeepDisplayAwakeWhileRecording);
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
        settings.KeepDisplayAwakeWhileRecording = false;
        analytics.RecordCapture(CaptureType.Screenshot);
        analytics.RecordCapture(CaptureType.Video);

        settings.ResetToDefaults();

        Assert.Equal("TinyClips {date} at {time}", settings.FileNameTemplate);
        Assert.True(settings.CopyScreenshotToClipboard);
        Assert.Equal(10.0, settings.GifFrameRate);
        Assert.True(settings.KeepDisplayAwakeWhileRecording);
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
    public void ScreenshotUsesLiveCapture_DefaultsFalse_RoundTrips_AndResetRestoresDefault()
    {
        var settingsService = new TestSettingsService();
        var settings = new CaptureSettings(settingsService);

        Assert.False(settings.ScreenshotUsesLiveCapture);

        settings.ScreenshotUsesLiveCapture = true;
        Assert.True(settings.ScreenshotUsesLiveCapture);
        Assert.True(settingsService.Get("screenshotUsesLiveCapture", false));

        settings.ResetToDefaults();
        Assert.False(settings.ScreenshotUsesLiveCapture);
        Assert.False(settingsService.Get("screenshotUsesLiveCapture", true));
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
