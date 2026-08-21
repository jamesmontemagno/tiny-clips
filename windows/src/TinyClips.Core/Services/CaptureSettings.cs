using TinyClips.Core.Models;

namespace TinyClips.Core.Services;

public sealed class CaptureSettings : ICaptureSettings
{
    private readonly ISettingsService _settings;
    private readonly IClipAnalyticsService? _analytics;

    public CaptureSettings(ISettingsService settings, IClipAnalyticsService? analytics = null)
    {
        _settings = settings;
        _analytics = analytics;
        MigrateLegacySaveDirectory();
    }

    public string SaveDirectory
    {
        get => _settings.SaveDirectory;
        set => _settings.SaveDirectory = value;
    }

    public bool UseDefaultSaveDirectories
    {
        get => _settings.Get("useDefaultSaveDirectories", true);
        set => _settings.Set("useDefaultSaveDirectories", value);
    }

    public string ScreenshotSaveDirectory
    {
        get => _settings.Get("screenshotSaveDirectory", DefaultSaveDirectory(CaptureType.Screenshot));
        set => _settings.Set("screenshotSaveDirectory", value);
    }

    public string VideoSaveDirectory
    {
        get => _settings.Get("videoSaveDirectory", DefaultSaveDirectory(CaptureType.Video));
        set => _settings.Set("videoSaveDirectory", value);
    }

    public string GifSaveDirectory
    {
        get => _settings.Get("gifSaveDirectory", DefaultSaveDirectory(CaptureType.Gif));
        set => _settings.Set("gifSaveDirectory", value);
    }

    public static string DefaultSaveDirectory(CaptureType type)
    {
        var folder = type == CaptureType.Screenshot
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = AppContext.BaseDirectory;
        }

        return Path.Combine(folder, "TinyClips");
    }

    public AppTheme Theme
    {
        get => _settings.Theme;
        set => _settings.Theme = value;
    }

    public bool CopyScreenshotToClipboard
    {
        get => _settings.Get("copyScreenshotToClipboard", true);
        set => _settings.Set("copyScreenshotToClipboard", value);
    }

    public bool CopyVideoToClipboard
    {
        get => _settings.Get("copyVideoToClipboard", false);
        set => _settings.Set("copyVideoToClipboard", value);
    }

    public bool CopyGifToClipboard
    {
        get => _settings.Get("copyGifToClipboard", false);
        set => _settings.Set("copyGifToClipboard", value);
    }

    public bool ShowInExplorer
    {
        get => _settings.Get("showInExplorer", false);
        set => _settings.Set("showInExplorer", value);
    }

    public bool ShowSaveNotifications
    {
        get => _settings.Get("showSaveNotifications", false);
        set => _settings.Set("showSaveNotifications", value);
    }

    public bool UploadcareEnabled
    {
        get => _settings.Get("uploadcareEnabled", false);
        set => _settings.Set("uploadcareEnabled", value);
    }

    public string UploadcarePublicKey
    {
        get => _settings.Get("uploadcarePublicKey", string.Empty);
        set => _settings.Set("uploadcarePublicKey", value);
    }

    public bool UploadcareAutoUpload
    {
        get => _settings.Get("uploadcareAutoUpload", false);
        set => _settings.Set("uploadcareAutoUpload", value);
    }

    public bool UploadcareCopyUrl
    {
        get => _settings.Get("uploadcareCopyUrl", false);
        set => _settings.Set("uploadcareCopyUrl", value);
    }

    public bool LaunchAtLogin
    {
        get => _settings.Get("launchAtLogin", false);
        set => _settings.Set("launchAtLogin", value);
    }

    public bool ReopenPickerAfterCapture
    {
        get => _settings.Get("reopenPickerAfterCapture", false);
        set => _settings.Set("reopenPickerAfterCapture", value);
    }

    public string FileNameTemplate
    {
        get => _settings.Get("fileNameTemplate", "TinyClips {date} at {time}");
        set => _settings.Set("fileNameTemplate", value);
    }

    public double GifFrameRate
    {
        get => _settings.Get("gifFrameRate", 10.0);
        set => _settings.Set("gifFrameRate", value);
    }

    public int GifMaxWidth
    {
        get => _settings.Get("gifMaxWidth", 640);
        set => _settings.Set("gifMaxWidth", value);
    }

    public int VideoFrameRate
    {
        get => _settings.Get("videoFrameRate", 30);
        set => _settings.Set("videoFrameRate", value);
    }

    public bool KeepDisplayAwakeWhileRecording
    {
        get => _settings.Get("keepDisplayAwakeWhileRecording", true);
        set => _settings.Set("keepDisplayAwakeWhileRecording", value);
    }

    public bool ShowMouseClickVisualsInVideo
    {
        get => _settings.Get("showMouseClickVisualsInVideo", false);
        set => _settings.Set("showMouseClickVisualsInVideo", value);
    }

    public bool ShowMouseClickVisualsInGif
    {
        get => _settings.Get("showMouseClickVisualsInGif", false);
        set => _settings.Set("showMouseClickVisualsInGif", value);
    }

    public bool GifMouseClicksUseVideoSettings
    {
        get => _settings.Get("gifMouseClicksUseVideoSettings", false);
        set => _settings.Set("gifMouseClicksUseVideoSettings", value);
    }

    public string VideoMouseClickColorHex
    {
        get => _settings.Get("videoMouseClickColorHex", "#0A84FF");
        set => _settings.Set("videoMouseClickColorHex", value);
    }

    public double VideoMouseClickSize
    {
        get => _settings.Get("videoMouseClickSize", 40.0);
        set => _settings.Set("videoMouseClickSize", value);
    }

    public double VideoMouseClickStrokeWidth
    {
        get => _settings.Get("videoMouseClickStrokeWidth", 3.0);
        set => _settings.Set("videoMouseClickStrokeWidth", value);
    }

    public double VideoMouseClickOpacity
    {
        get => _settings.Get("videoMouseClickOpacity", 0.85);
        set => _settings.Set("videoMouseClickOpacity", value);
    }

    public double VideoMouseClickDuration
    {
        get => _settings.Get("videoMouseClickDuration", 0.45);
        set => _settings.Set("videoMouseClickDuration", value);
    }

    public string GifMouseClickColorHex
    {
        get => _settings.Get("gifMouseClickColorHex", "#0A84FF");
        set => _settings.Set("gifMouseClickColorHex", value);
    }

    public double GifMouseClickSize
    {
        get => _settings.Get("gifMouseClickSize", 40.0);
        set => _settings.Set("gifMouseClickSize", value);
    }

    public double GifMouseClickStrokeWidth
    {
        get => _settings.Get("gifMouseClickStrokeWidth", 3.0);
        set => _settings.Set("gifMouseClickStrokeWidth", value);
    }

    public double GifMouseClickOpacity
    {
        get => _settings.Get("gifMouseClickOpacity", 0.85);
        set => _settings.Set("gifMouseClickOpacity", value);
    }

    public double GifMouseClickDuration
    {
        get => _settings.Get("gifMouseClickDuration", 0.45);
        set => _settings.Set("gifMouseClickDuration", value);
    }

    public bool ShowTrimmer
    {
        get => _settings.Get("showTrimmer", true);
        set => _settings.Set("showTrimmer", value);
    }

    public bool RecordAudio
    {
        get => _settings.Get("recordAudio", false);
        set => _settings.Set("recordAudio", value);
    }

    public bool RecordMicrophone
    {
        get => _settings.Get("recordMicrophone", false);
        set => _settings.Set("recordMicrophone", value);
    }

    public string SelectedMicrophoneId
    {
        get => _settings.Get("selectedMicrophoneID", string.Empty);
        set => _settings.Set("selectedMicrophoneID", value);
    }

    public bool MicrophoneLimiterEnabled
    {
        get => _settings.Get("microphoneLimiterEnabled", true);
        set => _settings.Set("microphoneLimiterEnabled", value);
    }

    public bool WebcamEnabled
    {
        get => _settings.Get("webcamEnabled", false);
        set => _settings.Set("webcamEnabled", value);
    }

    public string SelectedWebcamId
    {
        get => _settings.Get("selectedWebcamID", string.Empty);
        set => _settings.Set("selectedWebcamID", value);
    }

    public WebcamShape WebcamShape
    {
        get => ParseWebcamShape(_settings.Get("webcamShape", "circle"));
        set => _settings.Set("webcamShape", ToPersistedWebcamShape(value));
    }

    public WebcamSizePreset WebcamSizePreset
    {
        get => ParseWebcamSizePreset(_settings.Get("webcamSize", "medium"));
        set => _settings.Set("webcamSize", ToPersistedWebcamSizePreset(value));
    }

    public WebcamCornerPosition WebcamCornerPosition
    {
        get => ParseWebcamCornerPosition(_settings.Get("webcamCorner", "bottomRight"));
        set => _settings.Set("webcamCorner", ToPersistedWebcamCornerPosition(value));
    }

    public double? WebcamCornerRadius
    {
        get
        {
            var persisted = _settings.Get("webcamCornerRadius", -1.0);
            return persisted < 0 ? null : persisted;
        }
        set => _settings.Set("webcamCornerRadius", value ?? -1.0);
    }

    public bool ShowScreenshotEditor
    {
        get => _settings.Get("showScreenshotEditor", true);
        set => _settings.Set("showScreenshotEditor", value);
    }

    public bool ScreenshotUsesLiveCapture
    {
        get => _settings.Get("screenshotUsesLiveCapture", false);
        set => _settings.Set("screenshotUsesLiveCapture", value);
    }

    public bool ShowGifTrimmer
    {
        get => _settings.Get("showGifTrimmer", true);
        set => _settings.Set("showGifTrimmer", value);
    }

    public bool SaveImmediatelyScreenshot
    {
        get => _settings.Get("saveImmediatelyScreenshot", true);
        set => _settings.Set("saveImmediatelyScreenshot", value);
    }

    public bool SaveImmediatelyVideo
    {
        get => _settings.Get("saveImmediatelyVideo", true);
        set => _settings.Set("saveImmediatelyVideo", value);
    }

    public bool SaveImmediatelyGif
    {
        get => _settings.Get("saveImmediatelyGif", true);
        set => _settings.Set("saveImmediatelyGif", value);
    }

    public bool ShowScreenshotCapturePicker
    {
        get => _settings.Get("showScreenshotCapturePicker", true);
        set
        {
            _settings.Set("showScreenshotCapturePicker", value);
            if (!value)
            {
                ShowScreenshotCapturePickerAfterCapture = false;
            }
        }
    }

    public bool ShowScreenshotCapturePickerAfterCapture
    {
        get => _settings.Get("showScreenshotCapturePickerAfterCapture", false);
        set => _settings.Set("showScreenshotCapturePickerAfterCapture", value && ShowScreenshotCapturePicker);
    }

    public bool ShowVideoCapturePicker
    {
        get => _settings.Get("showVideoCapturePicker", true);
        set
        {
            _settings.Set("showVideoCapturePicker", value);
            if (!value)
            {
                ShowVideoCapturePickerAfterCapture = false;
            }
        }
    }

    public bool ShowVideoCapturePickerAfterCapture
    {
        get => _settings.Get("showVideoCapturePickerAfterCapture", false);
        set => _settings.Set("showVideoCapturePickerAfterCapture", value && ShowVideoCapturePicker);
    }

    public bool ShowGifCapturePicker
    {
        get => _settings.Get("showGifCapturePicker", true);
        set
        {
            _settings.Set("showGifCapturePicker", value);
            if (!value)
            {
                ShowGifCapturePickerAfterCapture = false;
            }
        }
    }

    public bool ShowGifCapturePickerAfterCapture
    {
        get => _settings.Get("showGifCapturePickerAfterCapture", false);
        set => _settings.Set("showGifCapturePickerAfterCapture", value && ShowGifCapturePicker);
    }

    public string ScreenshotFormat
    {
        get => _settings.Get("screenshotFormat", "jpg");
        set => _settings.Set("screenshotFormat", value);
    }

    public int ScreenshotScale
    {
        get => _settings.Get("screenshotScale", 100);
        set => _settings.Set("screenshotScale", value);
    }

    public double JpegQuality
    {
        get => _settings.Get("jpegQuality", 0.85);
        set => _settings.Set("jpegQuality", value);
    }

    public bool VideoCountdownEnabled
    {
        get => _settings.Get("videoCountdownEnabled", true);
        set => _settings.Set("videoCountdownEnabled", value);
    }

    public int VideoCountdownDuration
    {
        get => _settings.Get("videoCountdownDuration", 3);
        set => _settings.Set("videoCountdownDuration", value);
    }

    public int VideoRecordingTimeLimitMinutes
    {
        get => _settings.Get("videoRecordingTimeLimitMinutes", 0);
        set => _settings.Set("videoRecordingTimeLimitMinutes", value);
    }

    public bool GifCountdownEnabled
    {
        get => _settings.Get("gifCountdownEnabled", true);
        set => _settings.Set("gifCountdownEnabled", value);
    }

    public int GifCountdownDuration
    {
        get => _settings.Get("gifCountdownDuration", 3);
        set => _settings.Set("gifCountdownDuration", value);
    }

    public bool ScreenshotCountdownEnabled
    {
        get => _settings.Get("screenshotCountdownEnabled", false);
        set => _settings.Set("screenshotCountdownEnabled", value);
    }

    public int ScreenshotCountdownDuration
    {
        get => _settings.Get("screenshotCountdownDuration", 3);
        set => _settings.Set("screenshotCountdownDuration", value);
    }

    public bool HasCompletedOnboarding
    {
        get => _settings.Get("hasCompletedOnboarding", false);
        set => _settings.Set("hasCompletedOnboarding", value);
    }

    public MultiMonitorCaptureMode MultiMonitorCaptureMode
    {
        get
        {
            var persisted = _settings.Get("multiMonitorCaptureMode", string.Empty);
            if (string.IsNullOrWhiteSpace(persisted))
            {
                // Back-compat with the previous boolean toggle.
                return _settings.Get("alwaysCaptureMainDisplay", false)
                    ? MultiMonitorCaptureMode.MainDisplay
                    : MultiMonitorCaptureMode.Picker;
            }

            return Enum.TryParse<MultiMonitorCaptureMode>(persisted, ignoreCase: true, out var parsed)
                ? parsed
                : MultiMonitorCaptureMode.Picker;
        }
        set => _settings.Set("multiMonitorCaptureMode", value.ToString());
    }

    public bool ShowRegionIndicator
    {
        get => _settings.Get("showRegionIndicator", true);
        set => _settings.Set("showRegionIndicator", value);
    }

    public bool IncludeTinyClipsInCapture
    {
        get => _settings.Get("includeTinyClipsInCapture", false);
        set => _settings.Set("includeTinyClipsInCapture", value);
    }

    public bool ShowBrandingOverlay
    {
        get => _settings.Get("showBrandingOverlay", false);
        set => _settings.Set("showBrandingOverlay", value);
    }

    public bool TeleprompterEnabled
    {
        get => _settings.Get("teleprompterEnabled", false);
        set => _settings.Set("teleprompterEnabled", value);
    }

    public string TeleprompterTranscript
    {
        get => _settings is ILargeTextSettingsService largeTextSettings
            ? largeTextSettings.GetLargeText("teleprompterTranscript", string.Empty)
            : _settings.Get("teleprompterTranscript", string.Empty);
        set
        {
            if (_settings is ILargeTextSettingsService largeTextSettings)
            {
                largeTextSettings.SetLargeText("teleprompterTranscript", value);
            }
            else
            {
                _settings.Set("teleprompterTranscript", value);
            }
        }
    }

    public double TeleprompterScrollSpeed
    {
        get => _settings.Get("teleprompterScrollSpeed", 50.0);
        set => _settings.Set("teleprompterScrollSpeed", value);
    }

    public double TeleprompterPosX
    {
        get => _settings.Get("teleprompterPosX", -1.0);
        set => _settings.Set("teleprompterPosX", value);
    }

    public double TeleprompterPosY
    {
        get => _settings.Get("teleprompterPosY", -1.0);
        set => _settings.Set("teleprompterPosY", value);
    }

    public string TeleprompterMonitorDeviceName
    {
        get => _settings.Get("teleprompterMonitorDeviceName", string.Empty);
        set => _settings.Set("teleprompterMonitorDeviceName", value);
    }

    public int ScreenshotHotKeyCode
    {
        get => _settings.Get("screenshotHotKeyCode", 53);
        set => _settings.Set("screenshotHotKeyCode", value);
    }

    public int ScreenshotHotKeyModifiers
    {
        get => _settings.Get("screenshotHotKeyModifiers", 6);
        set => _settings.Set("screenshotHotKeyModifiers", value);
    }

    public int VideoHotKeyCode
    {
        get => _settings.Get("videoHotKeyCode", 54);
        set => _settings.Set("videoHotKeyCode", value);
    }

    public int VideoHotKeyModifiers
    {
        get => _settings.Get("videoHotKeyModifiers", 6);
        set => _settings.Set("videoHotKeyModifiers", value);
    }

    public int GifHotKeyCode
    {
        get => _settings.Get("gifHotKeyCode", 55);
        set => _settings.Set("gifHotKeyCode", value);
    }

    public int GifHotKeyModifiers
    {
        get => _settings.Get("gifHotKeyModifiers", 6);
        set => _settings.Set("gifHotKeyModifiers", value);
    }

    public int OcrHotKeyCode
    {
        get => _settings.Get("ocrHotKeyCode", 84);
        set => _settings.Set("ocrHotKeyCode", value);
    }

    public int OcrHotKeyModifiers
    {
        get => _settings.Get("ocrHotKeyModifiers", 6);
        set => _settings.Set("ocrHotKeyModifiers", value);
    }

    public ImageFormat ImageFormat
    {
        get => string.Equals(ScreenshotFormat, "png", StringComparison.OrdinalIgnoreCase) ? Models.ImageFormat.Png : Models.ImageFormat.Jpeg;
        set => ScreenshotFormat = value == Models.ImageFormat.Png ? "png" : "jpg";
    }

    public bool ShouldCopyToClipboard(CaptureType type) => type switch
    {
        CaptureType.Screenshot => CopyScreenshotToClipboard,
        CaptureType.Video => CopyVideoToClipboard,
        CaptureType.Gif => CopyGifToClipboard,
        _ => false,
    };

    public bool ShouldShowCapturePicker(CaptureType type) => type switch
    {
        CaptureType.Screenshot => ShowScreenshotCapturePicker,
        CaptureType.Video => ShowVideoCapturePicker,
        CaptureType.Gif => ShowGifCapturePicker,
        _ => false,
    };

    public bool ShouldShowCapturePickerAfterCapture(CaptureType type) => type switch
    {
        CaptureType.Screenshot => ShowScreenshotCapturePicker && ShowScreenshotCapturePickerAfterCapture,
        CaptureType.Video => ShowVideoCapturePicker && ShowVideoCapturePickerAfterCapture,
        CaptureType.Gif => ShowGifCapturePicker && ShowGifCapturePickerAfterCapture,
        _ => false,
    };

    public MouseClickOverlayStyle MouseClickOverlayStyleFor(CaptureType type) => type switch
    {
        CaptureType.Video => new MouseClickOverlayStyle(VideoMouseClickColorHex, VideoMouseClickSize, VideoMouseClickStrokeWidth, VideoMouseClickOpacity, VideoMouseClickDuration),
        CaptureType.Gif when GifMouseClicksUseVideoSettings => new MouseClickOverlayStyle(VideoMouseClickColorHex, VideoMouseClickSize, VideoMouseClickStrokeWidth, VideoMouseClickOpacity, VideoMouseClickDuration),
        CaptureType.Gif => new MouseClickOverlayStyle(GifMouseClickColorHex, GifMouseClickSize, GifMouseClickStrokeWidth, GifMouseClickOpacity, GifMouseClickDuration),
        CaptureType.Screenshot => new MouseClickOverlayStyle("#FFFFFF", 32, 3, 0.85, 0.45),
        _ => new MouseClickOverlayStyle("#FFFFFF", 32, 3, 0.85, 0.45),
    };

    public bool ShouldShowMouseClickVisuals(CaptureType type) => type switch
    {
        CaptureType.Video => ShowMouseClickVisualsInVideo,
        CaptureType.Gif when GifMouseClicksUseVideoSettings => ShowMouseClickVisualsInVideo,
        CaptureType.Gif => ShowMouseClickVisualsInGif,
        _ => false,
    };

    public void SetShowMouseClickVisuals(bool enabled, CaptureType type)
    {
        switch (type)
        {
            case CaptureType.Video:
                ShowMouseClickVisualsInVideo = enabled;
                break;
            case CaptureType.Gif:
                if (GifMouseClicksUseVideoSettings)
                {
                    ShowMouseClickVisualsInVideo = enabled;
                }
                else
                {
                    ShowMouseClickVisualsInGif = enabled;
                }

                break;
            case CaptureType.Screenshot:
                break;
        }
    }

    public void ResetToDefaults()
    {
        SaveDirectory = string.Empty;
        UseDefaultSaveDirectories = true;
        ScreenshotSaveDirectory = DefaultSaveDirectory(CaptureType.Screenshot);
        VideoSaveDirectory = DefaultSaveDirectory(CaptureType.Video);
        GifSaveDirectory = DefaultSaveDirectory(CaptureType.Gif);
        Theme = AppTheme.Default;
        CopyScreenshotToClipboard = true;
        CopyVideoToClipboard = false;
        CopyGifToClipboard = false;
        ShowInExplorer = false;
        ShowSaveNotifications = false;
        LaunchAtLogin = false;
        ReopenPickerAfterCapture = false;
        FileNameTemplate = "TinyClips {date} at {time}";
        GifFrameRate = 10.0;
        GifMaxWidth = 640;
        VideoFrameRate = 30;
        KeepDisplayAwakeWhileRecording = true;
        ShowMouseClickVisualsInVideo = false;
        ShowMouseClickVisualsInGif = false;
        GifMouseClicksUseVideoSettings = false;
        VideoMouseClickColorHex = "#0A84FF";
        VideoMouseClickSize = 40.0;
        VideoMouseClickStrokeWidth = 3.0;
        VideoMouseClickOpacity = 0.85;
        VideoMouseClickDuration = 0.45;
        GifMouseClickColorHex = "#0A84FF";
        GifMouseClickSize = 40.0;
        GifMouseClickStrokeWidth = 3.0;
        GifMouseClickOpacity = 0.85;
        GifMouseClickDuration = 0.45;
        ShowTrimmer = true;
        RecordAudio = false;
        RecordMicrophone = false;
        SelectedMicrophoneId = string.Empty;
        MicrophoneLimiterEnabled = true;
        WebcamEnabled = false;
        SelectedWebcamId = string.Empty;
        WebcamShape = WebcamShape.Circle;
        WebcamSizePreset = WebcamSizePreset.Medium;
        WebcamCornerPosition = WebcamCornerPosition.BottomRight;
        WebcamCornerRadius = null;
        ShowScreenshotEditor = true;
        ScreenshotUsesLiveCapture = false;
        ShowGifTrimmer = true;
        SaveImmediatelyScreenshot = true;
        SaveImmediatelyVideo = true;
        SaveImmediatelyGif = true;
        ShowScreenshotCapturePicker = true;
        ShowScreenshotCapturePickerAfterCapture = false;
        ShowVideoCapturePicker = true;
        ShowVideoCapturePickerAfterCapture = false;
        ShowGifCapturePicker = true;
        ShowGifCapturePickerAfterCapture = false;
        ScreenshotFormat = "jpg";
        ScreenshotScale = 100;
        JpegQuality = 0.85;
        VideoCountdownEnabled = true;
        VideoCountdownDuration = 3;
        VideoRecordingTimeLimitMinutes = 0;
        GifCountdownEnabled = true;
        GifCountdownDuration = 3;
        ScreenshotCountdownEnabled = false;
        ScreenshotCountdownDuration = 3;
        HasCompletedOnboarding = false;
        MultiMonitorCaptureMode = MultiMonitorCaptureMode.Picker;
        ShowRegionIndicator = true;
        IncludeTinyClipsInCapture = false;
        ShowBrandingOverlay = false;
        TeleprompterEnabled = false;
        TeleprompterTranscript = string.Empty;
        TeleprompterScrollSpeed = 50.0;
        TeleprompterPosX = -1.0;
        TeleprompterPosY = -1.0;
        TeleprompterMonitorDeviceName = string.Empty;
        UploadcareEnabled = false;
        UploadcarePublicKey = string.Empty;
        UploadcareAutoUpload = false;
        UploadcareCopyUrl = false;
        ScreenshotHotKeyCode = 53;
        ScreenshotHotKeyModifiers = 6;
        VideoHotKeyCode = 54;
        VideoHotKeyModifiers = 6;
        GifHotKeyCode = 55;
        GifHotKeyModifiers = 6;
        OcrHotKeyCode = 84;
        OcrHotKeyModifiers = 6;
        _analytics?.Clear();
    }

    private void MigrateLegacySaveDirectory()
    {
        if (_settings.Get("saveDirectoryFoldersMigrated", false))
        {
            EnsureSaveDirectoryDefaults();
            return;
        }

        var legacyDirectory = SaveDirectory.Trim();
        if (!string.IsNullOrWhiteSpace(legacyDirectory))
        {
            UseDefaultSaveDirectories = false;
            ScreenshotSaveDirectory = legacyDirectory;
            VideoSaveDirectory = legacyDirectory;
            GifSaveDirectory = legacyDirectory;
        }

        EnsureSaveDirectoryDefaults();
        _settings.Set("saveDirectoryFoldersMigrated", true);
    }

    private void EnsureSaveDirectoryDefaults()
    {
        if (string.IsNullOrWhiteSpace(_settings.Get("screenshotSaveDirectory", string.Empty)))
        {
            ScreenshotSaveDirectory = DefaultSaveDirectory(CaptureType.Screenshot);
        }

        if (string.IsNullOrWhiteSpace(_settings.Get("videoSaveDirectory", string.Empty)))
        {
            VideoSaveDirectory = DefaultSaveDirectory(CaptureType.Video);
        }

        if (string.IsNullOrWhiteSpace(_settings.Get("gifSaveDirectory", string.Empty)))
        {
            GifSaveDirectory = DefaultSaveDirectory(CaptureType.Gif);
        }
    }

    private static WebcamShape ParseWebcamShape(string value) =>        (value ?? string.Empty).ToLowerInvariant() switch
        {
            "rectangle" => WebcamShape.Rectangle,
            "rounded" or "roundedrectangle" => WebcamShape.RoundedRectangle,
            "circle" => WebcamShape.Circle,
            _ => WebcamShape.Circle,
        };

    private static string ToPersistedWebcamShape(WebcamShape value) => value switch
    {
        WebcamShape.Rectangle => "rectangle",
        WebcamShape.RoundedRectangle => "rounded",
        WebcamShape.Circle => "circle",
        _ => "circle",
    };

    private static WebcamSizePreset ParseWebcamSizePreset(string value) =>
        (value ?? string.Empty).ToLowerInvariant() switch
        {
            "small" => WebcamSizePreset.Small,
            "medium" => WebcamSizePreset.Medium,
            "large" => WebcamSizePreset.Large,
            _ => WebcamSizePreset.Medium,
        };

    private static string ToPersistedWebcamSizePreset(WebcamSizePreset value) => value switch
    {
        WebcamSizePreset.Small => "small",
        WebcamSizePreset.Medium => "medium",
        WebcamSizePreset.Large => "large",
        _ => "medium",
    };

    private static WebcamCornerPosition ParseWebcamCornerPosition(string value) =>
        (value ?? string.Empty).ToLowerInvariant() switch
        {
            "topleft" => WebcamCornerPosition.TopLeft,
            "topright" => WebcamCornerPosition.TopRight,
            "bottomleft" => WebcamCornerPosition.BottomLeft,
            "bottomright" => WebcamCornerPosition.BottomRight,
            _ => WebcamCornerPosition.BottomRight,
        };

    private static string ToPersistedWebcamCornerPosition(WebcamCornerPosition value) => value switch
    {
        WebcamCornerPosition.TopLeft => "topLeft",
        WebcamCornerPosition.TopRight => "topRight",
        WebcamCornerPosition.BottomLeft => "bottomLeft",
        WebcamCornerPosition.BottomRight => "bottomRight",
        _ => "bottomRight",
    };
}
