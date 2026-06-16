using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.App;

/// <summary>
/// View model for the Settings window. Each property mirrors a value on
/// <see cref="ICaptureSettings"/>; the generated change handlers persist edits
/// immediately so there is no explicit Save step.
/// </summary>
/// <remarks>
/// Field-based <c>[ObservableProperty]</c> is used intentionally. The MVVM Toolkit
/// source generator does not emit implementations for partial-property syntax in this
/// project configuration, and this view model is only consumed through compiled
/// <c>x:Bind</c> in C#; it never crosses the WinRT ABI, so the AOT-marshalling hint
/// (MVVMTK0045) does not apply.
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ICaptureSettings _settings;
    private readonly IHotKeyService _hotKeys;
    private readonly ILaunchAtLoginService _launchAtLoginService;
    private readonly IAudioDeviceService _audioDevices;
    private readonly IClipStorageService _storage;
    private bool _loading;

    /// <summary>Raised when the selected theme changes so the window can re-apply it live.</summary>
    public event Action? ThemeChanged;

    public SettingsViewModel(ICaptureSettings settings, IHotKeyService hotKeys, ILaunchAtLoginService launchAtLogin, IAudioDeviceService audioDevices, IClipStorageService storage)
    {
        _settings = settings;
        _hotKeys = hotKeys;
        _launchAtLoginService = launchAtLogin;
        _audioDevices = audioDevices;
        _storage = storage;
        Load();

        // Reconcile the toggle with the OS-owned launch-at-login (StartupTask) state.
        _ = RefreshLaunchAtLoginAsync();
    }

    /// <summary>
    /// The folder clips are actually written to. When the user has not picked a
    /// custom location this resolves to the default Pictures\TinyClips folder so
    /// the Settings UI always shows a real path instead of a blank line.
    /// </summary>
    public string SaveLocationDisplay => string.IsNullOrWhiteSpace(SaveDirectory)
        ? $"{_storage.OutputDirectory(CaptureType.Screenshot)} (default)"
        : SaveDirectory;

    // General
    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    private string _saveDirectory = string.Empty;

    [ObservableProperty]
    private string _fileNameTemplate = string.Empty;

    [ObservableProperty]
    private bool _showInExplorer;

    [ObservableProperty]
    private bool _showSaveNotifications;

    [ObservableProperty]
    private bool _launchAtLogin;

    /// <summary>False when Windows owns the toggle (policy-locked), so the UI disables it.</summary>
    [ObservableProperty]
    private bool _launchAtLoginToggleEnabled = true;

    /// <summary>Explains OS-imposed launch-at-login states (e.g. disabled by the user in Windows Settings).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LaunchAtLoginNoteVisibility))]
    private string _launchAtLoginNote = string.Empty;

    public Microsoft.UI.Xaml.Visibility LaunchAtLoginNoteVisibility =>
        string.IsNullOrEmpty(LaunchAtLoginNote)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

    // Guards the toggle's change handler while we set LaunchAtLogin to reflect OS truth.
    private bool _suppressLaunchAtLogin;

    [ObservableProperty]
    private bool _copyScreenshotToClipboard;

    [ObservableProperty]
    private bool _copyVideoToClipboard;

    [ObservableProperty]
    private bool _copyGifToClipboard;

    [ObservableProperty]
    private bool _reopenPickerAfterCapture;

    [ObservableProperty]
    private int _multiMonitorCaptureModeIndex;

    // Screenshot
    [ObservableProperty]
    private int _screenshotFormatIndex;

    [ObservableProperty]
    private double _screenshotScale;

    [ObservableProperty]
    private double _jpegQuality;

    [ObservableProperty]
    private bool _screenshotCountdownEnabled;

    [ObservableProperty]
    private double _screenshotCountdownDuration;

    [ObservableProperty]
    private bool _showScreenshotEditor;

    // Video
    [ObservableProperty]
    private double _videoFrameRate;

    [ObservableProperty]
    private bool _recordAudio;

    [ObservableProperty]
    private bool _recordMicrophone;

    /// <summary>Microphone devices for the picker (first entry is the system default).</summary>
    public System.Collections.ObjectModel.ObservableCollection<AudioInputDevice> Microphones { get; } = new();

    [ObservableProperty]
    private AudioInputDevice _selectedMicrophone;

    [ObservableProperty]
    private double _videoRecordingTimeLimitMinutes;

    [ObservableProperty]
    private bool _videoCountdownEnabled;

    [ObservableProperty]
    private double _videoCountdownDuration;

    [ObservableProperty]
    private bool _showTrimmer;

    // GIF
    [ObservableProperty]
    private double _gifFrameRate;

    [ObservableProperty]
    private double _gifMaxWidth;

    [ObservableProperty]
    private bool _gifCountdownEnabled;

    [ObservableProperty]
    private double _gifCountdownDuration;

    [ObservableProperty]
    private bool _showGifTrimmer;

    // Mouse clicks
    [ObservableProperty]
    private bool _showMouseClicksInVideo;

    [ObservableProperty]
    private bool _showMouseClicksInGif;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GifClicksEditable))]
    private bool _gifMouseClicksUseVideoSettings;

    [ObservableProperty]
    private double _videoMouseClickSize;

    [ObservableProperty]
    private double _videoMouseClickOpacity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MouseClickPreviewColorHex))]
    private string _videoMouseClickColorHex = "#FFD60A";

    [ObservableProperty]
    private double _gifMouseClickSize;

    [ObservableProperty]
    private double _gifMouseClickOpacity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GifMouseClickPreviewColorHex))]
    private string _gifMouseClickColorHex = "#FFD60A";

    /// <summary>Hex color surfaced to the settings preview swatch.</summary>
    public string MouseClickPreviewColorHex => VideoMouseClickColorHex;

    /// <summary>Hex color surfaced to the GIF click preview swatch.</summary>
    public string GifMouseClickPreviewColorHex => GifMouseClicksUseVideoSettings
        ? VideoMouseClickColorHex
        : GifMouseClickColorHex;

    /// <summary>True when the GIF click controls should be editable (i.e. not mirroring the video settings).</summary>
    public bool GifClicksEditable => !GifMouseClicksUseVideoSettings;

    // Branding
    [ObservableProperty]
    private bool _showBrandingOverlay;
    public string ScreenshotHotKeyDisplay => _hotKeys.GetBinding(CaptureType.Screenshot).DisplayString;

    public string VideoHotKeyDisplay => _hotKeys.GetBinding(CaptureType.Video).DisplayString;

    public string GifHotKeyDisplay => _hotKeys.GetBinding(CaptureType.Gif).DisplayString;

    /// <summary>Persists a new global shortcut for the given capture type and refreshes the display.</summary>
    public void SetHotKey(CaptureType type, HotKeyModifiers modifiers, uint virtualKey)
    {
        _hotKeys.SetBinding(type, new HotKeyDefinition(modifiers, virtualKey));
        RaiseHotKeyDisplays();
    }

    /// <summary>Restores the default shortcut for the given capture type.</summary>
    public void ResetHotKey(CaptureType type)
    {
        _hotKeys.SetBinding(type, _hotKeys.DefaultFor(type));
        RaiseHotKeyDisplays();
    }

    private void RaiseHotKeyDisplays()
    {
        OnPropertyChanged(nameof(ScreenshotHotKeyDisplay));
        OnPropertyChanged(nameof(VideoHotKeyDisplay));
        OnPropertyChanged(nameof(GifHotKeyDisplay));
    }

    private void Load()
    {
        _loading = true;
        try
        {
            ThemeIndex = _settings.Theme switch
            {
                AppTheme.Light => 1,
                AppTheme.Dark => 2,
                _ => 0,
            };
            SaveDirectory = _settings.SaveDirectory;
            FileNameTemplate = _settings.FileNameTemplate;
            ShowInExplorer = _settings.ShowInExplorer;
            ShowSaveNotifications = _settings.ShowSaveNotifications;
            LaunchAtLogin = _settings.LaunchAtLogin;
            CopyScreenshotToClipboard = _settings.CopyScreenshotToClipboard;
            CopyVideoToClipboard = _settings.CopyVideoToClipboard;
            CopyGifToClipboard = _settings.CopyGifToClipboard;
            ReopenPickerAfterCapture = _settings.ReopenPickerAfterCapture;
            MultiMonitorCaptureModeIndex = _settings.MultiMonitorCaptureMode switch
            {
                MultiMonitorCaptureMode.UnderCursor => 1,
                MultiMonitorCaptureMode.MainDisplay => 2,
                _ => 0,
            };

            ScreenshotFormatIndex = _settings.ImageFormat == ImageFormat.Png ? 0 : 1;
            ScreenshotScale = _settings.ScreenshotScale;
            JpegQuality = _settings.JpegQuality;
            ScreenshotCountdownEnabled = _settings.ScreenshotCountdownEnabled;
            ScreenshotCountdownDuration = _settings.ScreenshotCountdownDuration;
            ShowScreenshotEditor = _settings.ShowScreenshotEditor;

            VideoFrameRate = _settings.VideoFrameRate;
            RecordAudio = _settings.RecordAudio;
            RecordMicrophone = _settings.RecordMicrophone;

            Microphones.Clear();
            foreach (var mic in _audioDevices.GetMicrophones())
            {
                Microphones.Add(mic);
            }

            var savedMicId = _settings.SelectedMicrophoneId ?? string.Empty;
            SelectedMicrophone = Microphones.FirstOrDefault(m => m.Id == savedMicId, Microphones[0]);

            VideoRecordingTimeLimitMinutes = _settings.VideoRecordingTimeLimitMinutes;
            VideoCountdownEnabled = _settings.VideoCountdownEnabled;
            VideoCountdownDuration = _settings.VideoCountdownDuration;
            ShowTrimmer = _settings.ShowTrimmer;

            GifFrameRate = _settings.GifFrameRate;
            GifMaxWidth = _settings.GifMaxWidth;
            GifCountdownEnabled = _settings.GifCountdownEnabled;
            GifCountdownDuration = _settings.GifCountdownDuration;
            ShowGifTrimmer = _settings.ShowGifTrimmer;

            ShowMouseClicksInVideo = _settings.ShowMouseClickVisualsInVideo;
            ShowMouseClicksInGif = _settings.ShowMouseClickVisualsInGif;
            GifMouseClicksUseVideoSettings = _settings.GifMouseClicksUseVideoSettings;
            VideoMouseClickSize = _settings.VideoMouseClickSize;
            VideoMouseClickOpacity = _settings.VideoMouseClickOpacity;
            VideoMouseClickColorHex = _settings.VideoMouseClickColorHex;
            GifMouseClickSize = _settings.GifMouseClickSize;
            GifMouseClickOpacity = _settings.GifMouseClickOpacity;
            GifMouseClickColorHex = _settings.GifMouseClickColorHex;
            ShowBrandingOverlay = _settings.ShowBrandingOverlay;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (_loading)
        {
            return;
        }

        _settings.Theme = value switch
        {
            1 => AppTheme.Light,
            2 => AppTheme.Dark,
            _ => AppTheme.Default,
        };
        ThemeChanged?.Invoke();
    }

    partial void OnSaveDirectoryChanged(string value)
    {
        Persist(() => _settings.SaveDirectory = value);
        OnPropertyChanged(nameof(SaveLocationDisplay));
    }

    partial void OnFileNameTemplateChanged(string value) => Persist(() => _settings.FileNameTemplate = value);

    partial void OnShowInExplorerChanged(bool value) => Persist(() => _settings.ShowInExplorer = value);

    partial void OnShowSaveNotificationsChanged(bool value) => Persist(() => _settings.ShowSaveNotifications = value);

    partial void OnLaunchAtLoginChanged(bool value)
    {
        if (_loading || _suppressLaunchAtLogin)
        {
            return;
        }

        _ = ApplyLaunchAtLoginAsync(value);
    }

    private async Task ApplyLaunchAtLoginAsync(bool value)
    {
        var state = await _launchAtLoginService.SetEnabledAsync(value);
        ApplyLaunchAtLoginState(state);
    }

    private async Task RefreshLaunchAtLoginAsync()
    {
        var state = await _launchAtLoginService.GetStateAsync();
        ApplyLaunchAtLoginState(state);
    }

    private void ApplyLaunchAtLoginState(LaunchAtLoginState state)
    {
        var enabled = state is LaunchAtLoginState.Enabled or LaunchAtLoginState.EnabledByPolicy;

        // Reflect OS truth without re-triggering the change handler.
        _suppressLaunchAtLogin = true;
        LaunchAtLogin = enabled;
        _suppressLaunchAtLogin = false;

        // The app can only flip the toggle when Windows hasn't locked it.
        LaunchAtLoginToggleEnabled = state is LaunchAtLoginState.Enabled or LaunchAtLoginState.Disabled;

        LaunchAtLoginNote = state switch
        {
            LaunchAtLoginState.DisabledByUser =>
                "Turned off in Windows Settings \u2192 Apps \u2192 Startup. Re-enable Tiny Clips there to allow launch at login.",
            LaunchAtLoginState.DisabledByPolicy => "Launch at login is disabled by your organization's policy.",
            LaunchAtLoginState.EnabledByPolicy => "Launch at login is enabled by your organization's policy.",
            LaunchAtLoginState.Unavailable => "Launch at login isn't available for this installation.",
            _ => string.Empty,
        };

        // Keep the persisted mirror aligned with the real state.
        _settings.LaunchAtLogin = enabled;
    }

    partial void OnCopyScreenshotToClipboardChanged(bool value) => Persist(() => _settings.CopyScreenshotToClipboard = value);

    partial void OnCopyVideoToClipboardChanged(bool value) => Persist(() => _settings.CopyVideoToClipboard = value);

    partial void OnCopyGifToClipboardChanged(bool value) => Persist(() => _settings.CopyGifToClipboard = value);

    partial void OnReopenPickerAfterCaptureChanged(bool value) => Persist(() => _settings.ReopenPickerAfterCapture = value);

    partial void OnMultiMonitorCaptureModeIndexChanged(int value) => Persist(() => _settings.MultiMonitorCaptureMode = value switch
    {
        1 => MultiMonitorCaptureMode.UnderCursor,
        2 => MultiMonitorCaptureMode.MainDisplay,
        _ => MultiMonitorCaptureMode.Picker,
    });

    partial void OnScreenshotFormatIndexChanged(int value) =>
        Persist(() => _settings.ImageFormat = value == 0 ? ImageFormat.Png : ImageFormat.Jpeg);

    partial void OnScreenshotScaleChanged(double value) => Persist(() => _settings.ScreenshotScale = (int)Math.Round(value));

    partial void OnJpegQualityChanged(double value) => Persist(() => _settings.JpegQuality = value);

    partial void OnScreenshotCountdownEnabledChanged(bool value) => Persist(() => _settings.ScreenshotCountdownEnabled = value);

    partial void OnScreenshotCountdownDurationChanged(double value) =>
        Persist(() => _settings.ScreenshotCountdownDuration = (int)Math.Round(value));

    partial void OnShowScreenshotEditorChanged(bool value) => Persist(() => _settings.ShowScreenshotEditor = value);

    partial void OnVideoFrameRateChanged(double value) => Persist(() => _settings.VideoFrameRate = (int)Math.Round(value));

    partial void OnRecordAudioChanged(bool value) => Persist(() => _settings.RecordAudio = value);

    partial void OnRecordMicrophoneChanged(bool value) => Persist(() => _settings.RecordMicrophone = value);

    partial void OnSelectedMicrophoneChanged(AudioInputDevice value) =>
        Persist(() => _settings.SelectedMicrophoneId = value.Id ?? string.Empty);

    partial void OnVideoRecordingTimeLimitMinutesChanged(double value) =>
        Persist(() => _settings.VideoRecordingTimeLimitMinutes = (int)Math.Round(value));

    partial void OnVideoCountdownEnabledChanged(bool value) => Persist(() => _settings.VideoCountdownEnabled = value);

    partial void OnVideoCountdownDurationChanged(double value) =>
        Persist(() => _settings.VideoCountdownDuration = (int)Math.Round(value));

    partial void OnShowTrimmerChanged(bool value) => Persist(() => _settings.ShowTrimmer = value);

    partial void OnGifFrameRateChanged(double value) => Persist(() => _settings.GifFrameRate = value);

    partial void OnGifMaxWidthChanged(double value) => Persist(() => _settings.GifMaxWidth = (int)Math.Round(value));

    partial void OnGifCountdownEnabledChanged(bool value) => Persist(() => _settings.GifCountdownEnabled = value);

    partial void OnGifCountdownDurationChanged(double value) =>
        Persist(() => _settings.GifCountdownDuration = (int)Math.Round(value));

    partial void OnShowGifTrimmerChanged(bool value) => Persist(() => _settings.ShowGifTrimmer = value);

    partial void OnShowMouseClicksInVideoChanged(bool value) => Persist(() => _settings.ShowMouseClickVisualsInVideo = value);

    partial void OnShowMouseClicksInGifChanged(bool value) => Persist(() => _settings.ShowMouseClickVisualsInGif = value);

    partial void OnGifMouseClicksUseVideoSettingsChanged(bool value) => Persist(() =>
    {
        _settings.GifMouseClicksUseVideoSettings = value;
        OnPropertyChanged(nameof(GifMouseClickPreviewColorHex));
    });

    partial void OnVideoMouseClickSizeChanged(double value) => Persist(() => _settings.VideoMouseClickSize = value);

    partial void OnVideoMouseClickOpacityChanged(double value) => Persist(() => _settings.VideoMouseClickOpacity = value);

    partial void OnVideoMouseClickColorHexChanged(string value) => Persist(() =>
    {
        _settings.VideoMouseClickColorHex = value;
        if (_settings.GifMouseClicksUseVideoSettings)
        {
            _settings.GifMouseClickColorHex = value;
            OnPropertyChanged(nameof(GifMouseClickPreviewColorHex));
        }
    });

    partial void OnGifMouseClickSizeChanged(double value) => Persist(() => _settings.GifMouseClickSize = value);

    partial void OnGifMouseClickOpacityChanged(double value) => Persist(() => _settings.GifMouseClickOpacity = value);

    partial void OnGifMouseClickColorHexChanged(string value) => Persist(() => _settings.GifMouseClickColorHex = value);

    partial void OnShowBrandingOverlayChanged(bool value) => Persist(() => _settings.ShowBrandingOverlay = value);

    private void Persist(Action apply)
    {
        if (_loading)
        {
            return;
        }

        apply();
    }
}
