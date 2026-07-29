using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
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
    private readonly IWebcamDeviceEnumerator _webcamDevices;
    private readonly IClipStorageService _storage;
    private readonly IClipAnalyticsService _analytics;
    private readonly DispatcherQueue? _dispatcherQueue;
    private bool _loading;
    private string _savedMicrophoneId = string.Empty;
    private string _savedWebcamId = string.Empty;

    // Persistence stays suppressed while one or more Settings sections are realizing their
    // visual tree for the first time. WinUI TwoWay x:Bind targets (ComboBox.SelectedIndex,
    // TextBox.Text, ToggleSwitch.IsOn) push their transient initial values back into the
    // source as their controls are realized — which happens *after* this constructor's
    // Load() call. Without this gate those write-backs overwrite the loaded values (blanking
    // ComboBoxes to -1, emptying text boxes) and persist the garbage.
    //
    // Because sections are now created lazily (one per first navigation, cached afterward),
    // this can no longer be a single one-shot bool: General realizes when the window opens,
    // but Analytics/Video/etc. may realize much later, or several sections may be mid-realization
    // at once if the user navigates quickly before an earlier section's Loaded has fired. This
    // counter is incremented when a section begins realizing (see <see cref="BeginSectionRealization"/>)
    // and decremented once that section's first layout pass has completed and its values have been
    // rehydrated (see <see cref="CompleteSectionRealization"/>); persistence stays suppressed as long
    // as the count is above zero, regardless of how many sections are overlapping.
    private int _pendingSectionRealizations;

    // Set once the owning SettingsWindow has closed, so in-flight async continuations (media
    // device enumeration, permission prompts) stop touching view-model state.
    private bool _closed;

    private Task? _analyticsInitialization;
    private Task? _mediaDeviceInitialization;

    /// <summary>Raised when the selected theme changes so the window can re-apply it live.</summary>
    public event Action? ThemeChanged;

    public SettingsViewModel(
        ICaptureSettings settings,
        IHotKeyService hotKeys,
        ILaunchAtLoginService launchAtLogin,
        IAudioDeviceService audioDevices,
        IWebcamDeviceEnumerator webcamDevices,
        IClipStorageService storage,
        IClipAnalyticsService analytics)
    {
        _settings = settings;
        _hotKeys = hotKeys;
        _launchAtLoginService = launchAtLogin;
        _audioDevices = audioDevices;
        _webcamDevices = webcamDevices;
        _storage = storage;
        _analytics = analytics;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        Load();

        // Analytics history and microphone/webcam enumeration are deferred until their
        // sections are first selected (see EnsureAnalyticsInitializedAsync /
        // EnsureMediaDevicesInitializedAsync), since General is always the first section shown
        // and neither is needed to render it.

        // Reconcile the toggle with the OS-owned launch-at-login (StartupTask) state. General
        // is always the first section realized, so this stays eager.
        _ = RefreshLaunchAtLoginAsync();
    }

    /// <summary>
    /// Marks the start of a settings section's first visual-tree realization. Callers must pass
    /// the returned token to <see cref="CompleteSectionRealization"/> once that section's root
    /// element has raised its first <c>Loaded</c> event. Reference-counted so multiple sections
    /// can be mid-realization at once (e.g. rapid navigation) without prematurely re-enabling
    /// persistence.
    /// </summary>
    public IDisposable BeginSectionRealization()
    {
        _pendingSectionRealizations++;
        return new SectionRealizationScope(this);
    }

    /// <summary>
    /// Completes a section's first realization: re-reads the (still-intact) persisted values
    /// into the bound properties to overwrite anything the section's initial TwoWay binding
    /// write-backs may have corrupted, then releases this section's persistence suppression.
    /// Safe to call even if the window has since closed.
    /// </summary>
    public void CompleteSectionRealization(IDisposable realizationScope)
    {
        if (!_closed)
        {
            Load();
        }

        realizationScope.Dispose();

        if (!_closed)
        {
            ThemeChanged?.Invoke();
        }
    }

    private void EndSectionRealization()
    {
        if (_pendingSectionRealizations > 0)
        {
            _pendingSectionRealizations--;
        }
    }

    /// <summary>Stops async continuations (media enumeration, permission prompts) from touching
    /// this view model once the owning window has closed.</summary>
    public void NotifyClosed() => _closed = true;

    private sealed class SectionRealizationScope : IDisposable
    {
        private SettingsViewModel? _owner;

        public SectionRealizationScope(SettingsViewModel owner) => _owner = owner;

        public void Dispose()
        {
            _owner?.EndSectionRealization();
            _owner = null;
        }
    }

    public string ScreenshotSaveLocationDisplay => $"Screenshot: {ResolveEffectiveSaveLocation(CaptureType.Screenshot)}";

    public string VideoGifSaveLocationDisplay => $"Video/GIF: {ResolveEffectiveSaveLocation(CaptureType.Video)}";

    public string SaveLocationModeDisplay => string.IsNullOrWhiteSpace(SaveDirectory)
        ? "Using defaults by capture type."
        : "Custom save location override applies to all capture types.";

    private string ResolveEffectiveSaveLocation(CaptureType type) => string.IsNullOrWhiteSpace(SaveDirectory)
        ? $"{_storage.OutputDirectory(type)} (default)"
        : SaveDirectory;

    // General
    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScreenshotSaveLocationDisplay))]
    [NotifyPropertyChangedFor(nameof(VideoGifSaveLocationDisplay))]
    [NotifyPropertyChangedFor(nameof(SaveLocationModeDisplay))]
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
    [NotifyPropertyChangedFor(nameof(MicrophoneSelectorEnabled))]
    private bool _recordMicrophone;

    /// <summary>Microphone devices for the picker (first entry is the system default).</summary>
    public System.Collections.ObjectModel.ObservableCollection<AudioInputDevice> Microphones { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicrophoneLoadingVisibility))]
    [NotifyPropertyChangedFor(nameof(MicrophoneSelectorVisibility))]
    [NotifyPropertyChangedFor(nameof(MicrophoneSelectorEnabled))]
    private bool _isMicrophonesLoading = true;

    [ObservableProperty]
    private AudioInputDevice? _selectedMicrophone;

    public Microsoft.UI.Xaml.Visibility MicrophoneLoadingVisibility => IsMicrophonesLoading
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility MicrophoneSelectorVisibility => IsMicrophonesLoading
        ? Microsoft.UI.Xaml.Visibility.Collapsed
        : Microsoft.UI.Xaml.Visibility.Visible;

    public bool MicrophoneSelectorEnabled => RecordMicrophone && !IsMicrophonesLoading;

    [ObservableProperty]
    private bool _webcamEnabled;

    /// <summary>Webcam devices for the picker (first entry is the system default).</summary>
    public System.Collections.ObjectModel.ObservableCollection<WebcamDeviceInfo> Webcams { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WebcamLoadingVisibility))]
    [NotifyPropertyChangedFor(nameof(WebcamSelectorVisibility))]
    [NotifyPropertyChangedFor(nameof(WebcamDeviceSelectorEnabled))]
    private bool _isWebcamsLoading = true;

    public Microsoft.UI.Xaml.Visibility WebcamLoadingVisibility => IsWebcamsLoading
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility WebcamSelectorVisibility => IsWebcamsLoading
        ? Microsoft.UI.Xaml.Visibility.Collapsed
        : Microsoft.UI.Xaml.Visibility.Visible;

    public bool WebcamDeviceSelectorEnabled => !IsWebcamsLoading;

    /// <summary>Set when microphone or webcam enumeration fails on first Video activation; null when there's no error.</summary>
    [ObservableProperty]
    private string? _mediaDevicesLoadError;

    [ObservableProperty]
    private WebcamDeviceInfo? _selectedWebcam;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WebcamCornerRadiusEnabled))]
    private int _webcamShapeIndex;

    [ObservableProperty]
    private int _webcamSizePresetIndex;

    [ObservableProperty]
    private int _webcamCornerPositionIndex;

    [ObservableProperty]
    private double _webcamCornerRadius = -1;

    public bool WebcamCornerRadiusEnabled => WebcamShapeIndex == 1;

    [ObservableProperty]
    private double _videoRecordingTimeLimitMinutes;

    [ObservableProperty]
    private int _videoEncoderProfileIndex;

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

    // Analytics
    public System.Collections.ObjectModel.ObservableCollection<CaptureAnalyticsDayViewModel> AnalyticsDays { get; } = new();

    /// <summary>Set when loading capture history fails on first Analytics activation; null when there's no error.</summary>
    [ObservableProperty]
    private string? _analyticsLoadError;

    [ObservableProperty]
    private int _analyticsRangeIndex;

    [ObservableProperty]
    private int _analyticsScreenshotTotal;

    [ObservableProperty]
    private int _analyticsVideoTotal;

    [ObservableProperty]
    private int _analyticsGifTotal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnalyticsEmptyStateVisibility))]
    private int _analyticsCaptureTotal;

    public Microsoft.UI.Xaml.Visibility AnalyticsEmptyStateVisibility => AnalyticsCaptureTotal == 0
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>Whether the screenshot series is currently shown in the daily chart (does not affect totals).</summary>
    [ObservableProperty]
    private bool _showScreenshotsInChart = true;

    [ObservableProperty]
    private bool _showVideosInChart = true;

    [ObservableProperty]
    private bool _showGifsInChart = true;

    // Lifetime totals (never pruned by the rolling day window)
    [ObservableProperty]
    private int _lifetimeScreenshotTotal;

    [ObservableProperty]
    private int _lifetimeVideoTotal;

    [ObservableProperty]
    private int _lifetimeGifTotal;

    [ObservableProperty]
    private int _lifetimeCaptureTotal;

    // Insights: busiest weekday / most active hour
    public System.Collections.ObjectModel.ObservableCollection<WeekdayBreakdownViewModel> WeekdayBreakdown { get; } = new();

    public System.Collections.ObjectModel.ObservableCollection<HourBreakdownViewModel> HourlyBreakdown { get; } = new();

    [ObservableProperty]
    private string _busiestWeekdayLabel = "No captures yet for this range.";

    [ObservableProperty]
    private string _mostActiveHourLabel = "No captures yet.";

    public string ScreenshotHotKeyDisplay => _hotKeys.GetBinding(CaptureType.Screenshot).DisplayString;

    public string VideoHotKeyDisplay => _hotKeys.GetBinding(CaptureType.Video).DisplayString;

    public string GifHotKeyDisplay => _hotKeys.GetBinding(CaptureType.Gif).DisplayString;

    public HotKeyDefinition GetHotKey(CaptureType type) => _hotKeys.GetBinding(type);

    public HotKeyDefinition GetDefaultHotKey(CaptureType type) => _hotKeys.DefaultFor(type);

    public HotKeyValidationResult ValidateHotKey(CaptureType type, HotKeyDefinition binding)
        => _hotKeys.ValidateBinding(type, binding);

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

    /// <summary>
    /// Loads capture analytics the first time the Analytics section is selected. Idempotent — the
    /// first call kicks off the (synchronous, but wrapped for future-proofing and error isolation)
    /// load and caches the task; later calls just await the same completed task instead of
    /// re-querying the analytics store.
    /// </summary>
    public Task EnsureAnalyticsInitializedAsync() => _analyticsInitialization ??= InitializeAnalyticsAsync();

    private Task InitializeAnalyticsAsync()
    {
        try
        {
            RefreshAnalytics();
            AnalyticsLoadError = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to load capture analytics: {ex}");
            AnalyticsLoadError = "Couldn't load capture analytics. Reopen Settings to try again.";
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Enumerates microphones and webcams the first time the Video section is selected.
    /// Idempotent — the first call kicks off enumeration and caches the task; later calls just
    /// await the same in-flight/completed task instead of re-enumerating devices.
    /// </summary>
    public Task EnsureMediaDevicesInitializedAsync() => _mediaDeviceInitialization ??= InitializeMediaDevicesAsync();

    private async Task InitializeMediaDevicesAsync()
    {
        MediaDevicesLoadError = null;

        var errors = await Task.WhenAll(LoadMicrophonesAsync(), LoadWebcamsAsync());
        if (!_closed)
        {
            MediaDevicesLoadError = string.Join(
                " ",
                errors
                    .Where(error => !string.IsNullOrWhiteSpace(error))
                    .Distinct());
        }
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
            FileNameTemplate = string.IsNullOrWhiteSpace(_settings.FileNameTemplate)
                ? "TinyClips {date} at {time}"
                : _settings.FileNameTemplate;
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

            _savedMicrophoneId = _settings.SelectedMicrophoneId ?? string.Empty;
            WebcamEnabled = _settings.WebcamEnabled;
            _savedWebcamId = _settings.SelectedWebcamId ?? string.Empty;
            WebcamShapeIndex = _settings.WebcamShape switch
            {
                WebcamShape.Rectangle => 0,
                WebcamShape.RoundedRectangle => 1,
                _ => 2,
            };
            WebcamSizePresetIndex = _settings.WebcamSizePreset switch
            {
                WebcamSizePreset.Small => 0,
                WebcamSizePreset.Large => 2,
                _ => 1,
            };
            WebcamCornerPositionIndex = _settings.WebcamCornerPosition switch
            {
                WebcamCornerPosition.TopLeft => 0,
                WebcamCornerPosition.TopRight => 1,
                WebcamCornerPosition.BottomLeft => 2,
                _ => 3,
            };
            WebcamCornerRadius = _settings.WebcamCornerRadius ?? -1;

            VideoRecordingTimeLimitMinutes = _settings.VideoRecordingTimeLimitMinutes;
            VideoEncoderProfileIndex = _settings.VideoEncoderProfile == VideoEncoderProfile.Baseline ? 1 : 0;
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

    private async Task<string?> LoadMicrophonesAsync()
    {
        IsMicrophonesLoading = true;
        try
        {
            var microphones = await Task.Run(() => _audioDevices.GetMicrophones());
            if (_closed)
            {
                return null;
            }

            await ApplyMicrophonesAsync(microphones);
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to enumerate microphones: {ex}");
            return _closed
                ? null
                : "Couldn't load microphones. Reopen Settings to try again.";
        }
        finally
        {
            IsMicrophonesLoading = false;
        }
    }

    private async Task<string?> LoadWebcamsAsync()
    {
        IsWebcamsLoading = true;
        try
        {
            var webcams = await _webcamDevices.GetWebcamDevicesAsync();
            if (_closed)
            {
                return null;
            }

            await ApplyWebcamsAsync(webcams);
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to enumerate webcams: {ex}");
            return _closed
                ? null
                : "Couldn't load webcams. Reopen Settings to try again.";
        }
        finally
        {
            IsWebcamsLoading = false;
        }
    }

    private async Task ApplyMicrophonesAsync(IReadOnlyList<AudioInputDevice> microphones)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            ApplyMicrophones(microphones);
            return;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    ApplyMicrophones(microphones);
                    completion.SetResult(true);
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }))
        {
            throw new InvalidOperationException("Unable to update microphone list on the UI thread.");
        }

        await completion.Task;
    }

    private void ApplyMicrophones(IReadOnlyList<AudioInputDevice> microphones)
    {
        _loading = true;
        try
        {
            SelectedMicrophone = null;
            Microphones.Clear();

            if (microphones.Count == 0)
            {
                Microphones.Add(new AudioInputDevice(string.Empty, "System default"));
            }
            else
            {
                foreach (var mic in microphones)
                {
                    Microphones.Add(mic);
                }
            }

            SelectedMicrophone = Microphones.FirstOrDefault(m => m.Id == _savedMicrophoneId) ?? Microphones[0];
        }
        finally
        {
            _loading = false;
            IsMicrophonesLoading = false;
        }
    }

    private async Task ApplyWebcamsAsync(IReadOnlyList<WebcamDeviceInfo> webcams)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            ApplyWebcams(webcams);
            return;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    ApplyWebcams(webcams);
                    completion.SetResult(true);
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }))
        {
            throw new InvalidOperationException("Unable to update webcam list on the UI thread.");
        }

        await completion.Task;
    }

    private void ApplyWebcams(IReadOnlyList<WebcamDeviceInfo> webcams)
    {
        _loading = true;
        try
        {
            Webcams.Clear();
            Webcams.Add(new WebcamDeviceInfo(string.Empty, "System default"));

            foreach (var webcam in webcams)
            {
                Webcams.Add(webcam);
            }

            SelectedWebcam = Webcams.FirstOrDefault(device => device.Id == _savedWebcamId) ?? Webcams[0];
        }
        finally
        {
            _loading = false;
            IsWebcamsLoading = false;
        }
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (_loading || _pendingSectionRealizations > 0)
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
    }

    partial void OnFileNameTemplateChanged(string value) => Persist(() => _settings.FileNameTemplate = value);

    partial void OnShowInExplorerChanged(bool value) => Persist(() => _settings.ShowInExplorer = value);

    partial void OnShowSaveNotificationsChanged(bool value) => Persist(() => _settings.ShowSaveNotifications = value);

    partial void OnLaunchAtLoginChanged(bool value)
    {
        if (_loading || _pendingSectionRealizations > 0 || _suppressLaunchAtLogin)
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

    partial void OnSelectedMicrophoneChanged(AudioInputDevice? value) =>
        Persist(() => _settings.SelectedMicrophoneId = value?.Id ?? string.Empty);

    partial void OnWebcamEnabledChanged(bool value) => Persist(() => _settings.WebcamEnabled = value);

    partial void OnSelectedWebcamChanged(WebcamDeviceInfo? value) =>
        Persist(() => _settings.SelectedWebcamId = value?.Id ?? string.Empty);

    partial void OnWebcamShapeIndexChanged(int value) => Persist(() => _settings.WebcamShape = value switch
    {
        0 => WebcamShape.Rectangle,
        1 => WebcamShape.RoundedRectangle,
        _ => WebcamShape.Circle,
    });

    partial void OnWebcamSizePresetIndexChanged(int value) => Persist(() => _settings.WebcamSizePreset = value switch
    {
        0 => WebcamSizePreset.Small,
        2 => WebcamSizePreset.Large,
        _ => WebcamSizePreset.Medium,
    });

    partial void OnWebcamCornerPositionIndexChanged(int value) => Persist(() => _settings.WebcamCornerPosition = value switch
    {
        0 => WebcamCornerPosition.TopLeft,
        1 => WebcamCornerPosition.TopRight,
        2 => WebcamCornerPosition.BottomLeft,
        _ => WebcamCornerPosition.BottomRight,
    });

    partial void OnWebcamCornerRadiusChanged(double value) =>
        Persist(() => _settings.WebcamCornerRadius = value < 0 ? null : value);

    partial void OnVideoRecordingTimeLimitMinutesChanged(double value) =>
        Persist(() => _settings.VideoRecordingTimeLimitMinutes = (int)Math.Round(value));

    partial void OnVideoEncoderProfileIndexChanged(int value) =>
        Persist(() => _settings.VideoEncoderProfile = value == 1 ? VideoEncoderProfile.Baseline : VideoEncoderProfile.High);

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

    partial void OnAnalyticsRangeIndexChanged(int value)
    {
        if (_loading)
        {
            return;
        }

        RefreshAnalytics();
    }

    partial void OnShowScreenshotsInChartChanged(bool value) =>
        RefreshAnalyticsChartOrKeepSeriesSelected(value, () => ShowScreenshotsInChart = true);

    partial void OnShowVideosInChartChanged(bool value) =>
        RefreshAnalyticsChartOrKeepSeriesSelected(value, () => ShowVideosInChart = true);

    partial void OnShowGifsInChartChanged(bool value) =>
        RefreshAnalyticsChartOrKeepSeriesSelected(value, () => ShowGifsInChart = true);

    private void Persist(Action apply)
    {
        if (_loading || _pendingSectionRealizations > 0)
        {
            return;
        }

        apply();
    }

    /// <summary>
    /// Resets every setting to its default value and reloads all bound properties so the
    /// Settings window immediately reflects the restored state.
    /// </summary>
    public void ResetAllSettings()
    {
        _settings.ResetToDefaults();
        Load();
        ThemeChanged?.Invoke();
    }

    public void ResetAnalytics()
    {
        _analytics.Clear();
        RefreshAnalytics();
    }

    /// <summary>Builds a shareable plain-text summary of capture activity for the selected range.</summary>
    public string BuildAnalyticsSummaryText()
    {
        var rangeDays = AnalyticsRangeIndex == 1 ? 30 : 7;
        var rangeLabel = AnalyticsRangeIndex == 1 ? "the last 30 days" : "the last 7 days";

        var lines = new List<string>
        {
            $"📊 Tiny Clips capture activity — {rangeLabel}",
            $"📸 {AnalyticsScreenshotTotal} screenshot{(AnalyticsScreenshotTotal == 1 ? string.Empty : "s")}",
            $"🎥 {AnalyticsVideoTotal} video{(AnalyticsVideoTotal == 1 ? string.Empty : "s")}",
            $"🎞️ {AnalyticsGifTotal} GIF{(AnalyticsGifTotal == 1 ? string.Empty : "s")}",
        };

        var busiestWeekday = _analytics.GetBusiestWeekday(rangeDays);
        if (busiestWeekday is not null)
        {
            lines.Add($"Busiest day: {busiestWeekday.Weekday} ({FormatCount(busiestWeekday.Count, "capture")})");
        }

        var mostActiveHour = _analytics.GetMostActiveHour();
        if (mostActiveHour is not null)
        {
            lines.Add($"Most active hour (all-time): {FormatHourLabel(mostActiveHour.Hour)}");
        }

        lines.Add($"Lifetime total: {FormatCount(LifetimeCaptureTotal, "capture")}");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Copies the current analytics summary text to the clipboard.</summary>
    public Task CopyAnalyticsSummaryAsync() => ClipboardService.CopyTextAsync(BuildAnalyticsSummaryText());

    private void RefreshAnalyticsChartOrKeepSeriesSelected(bool value, Action keepSeriesSelected)
    {
        if (!value && !ShowScreenshotsInChart && !ShowVideosInChart && !ShowGifsInChart)
        {
            keepSeriesSelected();
            return;
        }

        RefreshAnalyticsChartOnly();
    }

    /// <summary>Re-renders just the chart bar heights/visibility for the active range
    /// without updating totals, lifetime counts, or insight summaries.</summary>
    private void RefreshAnalyticsChartOnly()
    {
        var rangeDays = AnalyticsRangeIndex == 1 ? 30 : 7;
        ApplyChartDays(_analytics.GetDailyCounts(rangeDays), rangeDays);
    }

    private void RefreshAnalytics()
    {
        var rangeDays = AnalyticsRangeIndex == 1 ? 30 : 7;
        var dailyCounts = _analytics.GetDailyCounts(rangeDays);

        ApplyChartDays(dailyCounts, rangeDays);

        AnalyticsScreenshotTotal = dailyCounts.Sum(day => day.ScreenshotCount);
        AnalyticsVideoTotal = dailyCounts.Sum(day => day.VideoCount);
        AnalyticsGifTotal = dailyCounts.Sum(day => day.GifCount);
        AnalyticsCaptureTotal = AnalyticsScreenshotTotal + AnalyticsVideoTotal + AnalyticsGifTotal;

        var lifetime = _analytics.GetLifetimeTotals();
        LifetimeScreenshotTotal = lifetime.ScreenshotCount;
        LifetimeVideoTotal = lifetime.VideoCount;
        LifetimeGifTotal = lifetime.GifCount;
        LifetimeCaptureTotal = lifetime.TotalCount;

        RefreshInsights(rangeDays);
    }

    private void ApplyChartDays(IReadOnlyList<DailyCaptureAnalytics> dailyCounts, int rangeDays)
    {
        const double chartHeight = 160.0;

        int VisibleCount(DailyCaptureAnalytics day) =>
            (ShowScreenshotsInChart ? day.ScreenshotCount : 0) +
            (ShowVideosInChart ? day.VideoCount : 0) +
            (ShowGifsInChart ? day.GifCount : 0);

        var maxTotal = Math.Max(1, dailyCounts.Count == 0 ? 0 : dailyCounts.Max(VisibleCount));

        AnalyticsDays.Clear();
        foreach (var day in dailyCounts)
        {
            AnalyticsDays.Add(new CaptureAnalyticsDayViewModel(
                dateLabel: rangeDays == 7
                    ? day.Date.ToString("ddd", CultureInfo.InvariantCulture)[..2]
                    : day.Date.ToString("%d", CultureInfo.InvariantCulture),
                fullDateLabel: day.Date.ToString("ddd, MMM d", CultureInfo.InvariantCulture),
                screenshotCount: day.ScreenshotCount,
                videoCount: day.VideoCount,
                gifCount: day.GifCount,
                screenshotHeight: ShowScreenshotsInChart ? chartHeight * day.ScreenshotCount / maxTotal : 0,
                videoHeight: ShowVideosInChart ? chartHeight * day.VideoCount / maxTotal : 0,
                gifHeight: ShowGifsInChart ? chartHeight * day.GifCount / maxTotal : 0));
        }
    }

    private void RefreshInsights(int rangeDays)
    {
        const double breakdownHeight = 60.0;

        var weekdayTotals = _analytics.GetWeekdayTotals(rangeDays);
        var maxWeekdayCount = Math.Max(1, weekdayTotals.Count == 0 ? 0 : weekdayTotals.Max(w => w.Count));
        var busiestWeekday = _analytics.GetBusiestWeekday(rangeDays);

        WeekdayBreakdown.Clear();
        foreach (var weekday in weekdayTotals)
        {
            WeekdayBreakdown.Add(new WeekdayBreakdownViewModel(
                dayLabel: weekday.Weekday.ToString()[..3],
                fullDayLabel: weekday.Weekday.ToString(),
                count: weekday.Count,
                height: breakdownHeight * weekday.Count / maxWeekdayCount,
                isBusiest: busiestWeekday is not null && weekday.Weekday == busiestWeekday.Weekday));
        }

        BusiestWeekdayLabel = busiestWeekday is null
            ? "No captures yet for this range."
            : $"{busiestWeekday.Weekday} · {busiestWeekday.Count} capture{(busiestWeekday.Count == 1 ? string.Empty : "s")}";

        var hourlyTotals = _analytics.GetHourlyTotals();
        var maxHourCount = Math.Max(1, hourlyTotals.Count == 0 ? 0 : hourlyTotals.Max(h => h.Count));
        var mostActiveHour = _analytics.GetMostActiveHour();

        HourlyBreakdown.Clear();
        foreach (var hour in hourlyTotals)
        {
            HourlyBreakdown.Add(new HourBreakdownViewModel(
                hourLabel: FormatHourLabel(hour.Hour),
                count: hour.Count,
                height: breakdownHeight * hour.Count / maxHourCount,
                isBusiest: mostActiveHour is not null && hour.Hour == mostActiveHour.Hour));
        }

        MostActiveHourLabel = mostActiveHour is null
            ? "No captures yet."
            : $"{FormatHourLabel(mostActiveHour.Hour)} · {mostActiveHour.Count} capture{(mostActiveHour.Count == 1 ? string.Empty : "s")}";
    }

    private static string FormatHourLabel(int hour)
    {
        var date = DateTime.Today.AddHours(hour);
        return date.ToString("h tt", CultureInfo.InvariantCulture);
    }

    private static string FormatCount(int count, string singular, string? plural = null) =>
        $"{count} {(count == 1 ? singular : plural ?? $"{singular}s")}";
}

public sealed class CaptureAnalyticsDayViewModel
{
    public CaptureAnalyticsDayViewModel(
        string dateLabel,
        string fullDateLabel,
        int screenshotCount,
        int videoCount,
        int gifCount,
        double screenshotHeight,
        double videoHeight,
        double gifHeight)
    {
        DateLabel = dateLabel;
        FullDateLabel = fullDateLabel;
        ScreenshotCount = screenshotCount;
        VideoCount = videoCount;
        GifCount = gifCount;
        ScreenshotHeight = screenshotHeight;
        VideoHeight = videoHeight;
        GifHeight = gifHeight;
    }

    public string DateLabel { get; }
    public string FullDateLabel { get; }
    public int ScreenshotCount { get; }
    public int VideoCount { get; }
    public int GifCount { get; }
    public double ScreenshotHeight { get; }
    public double VideoHeight { get; }
    public double GifHeight { get; }

    public string AccessibilitySummary =>
        $"{FullDateLabel}: {FormatCount(ScreenshotCount, "screenshot")}, {FormatCount(VideoCount, "video")}, {FormatCount(GifCount, "GIF", "GIFs")}.";

    private static string FormatCount(int count, string singular, string? plural = null) =>
        $"{count} {(count == 1 ? singular : plural ?? $"{singular}s")}";
}

/// <summary>A single day-of-week bar in the "busiest day" insights breakdown.</summary>
public sealed class WeekdayBreakdownViewModel
{
    public WeekdayBreakdownViewModel(string dayLabel, string fullDayLabel, int count, double height, bool isBusiest)
    {
        DayLabel = dayLabel;
        FullDayLabel = fullDayLabel;
        Count = count;
        Height = height;
        IsBusiest = isBusiest;
    }

    public string DayLabel { get; }
    public string FullDayLabel { get; }
    public int Count { get; }
    public double Height { get; }
    public bool IsBusiest { get; }

    /// <summary>Full opacity for the busiest bar, dimmed for all others.</summary>
    public double BarOpacity => IsBusiest ? 1.0 : 0.35;

    public string AccessibilitySummary => $"{FullDayLabel}: {Count} capture{(Count == 1 ? string.Empty : "s")}.";
}

/// <summary>A single hour-of-day bar in the "most active hour" insights breakdown (all-time).</summary>
public sealed class HourBreakdownViewModel
{
    public HourBreakdownViewModel(string hourLabel, int count, double height, bool isBusiest)
    {
        HourLabel = hourLabel;
        Count = count;
        Height = height;
        IsBusiest = isBusiest;
    }

    public string HourLabel { get; }
    public int Count { get; }
    public double Height { get; }
    public bool IsBusiest { get; }

    /// <summary>Full opacity for the busiest bar, dimmed for all others.</summary>
    public double BarOpacity => IsBusiest ? 1.0 : 0.35;

    public string AccessibilitySummary => $"{HourLabel}: {Count} capture{(Count == 1 ? string.Empty : "s")} all-time.";
}
