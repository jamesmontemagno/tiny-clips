using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using TinyClips.App.Settings;
using TinyClips.Core.Capture;
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
    private readonly IUploadcareCredentialStore _uploadcareCredentials;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly ITeleprompterTranscriptSaveScheduler? _teleprompterTranscriptSaveScheduler;
    private bool _loading;
    private string? _pendingTeleprompterTranscript;
    private string _savedMicrophoneId = string.Empty;
    private string _savedWebcamId = string.Empty;
    private bool _uploadcareCredentialsInitialized;
    private string? _savedTeleprompterTranscript;
    private bool _teleprompterTranscriptInitialized;

    // Persistence stays suppressed while one or more Settings sections are realizing their
    // visual tree for the first time. WinUI TwoWay x:Bind targets (ComboBox.SelectedIndex,
    // TextBox.Text, ToggleSwitch.IsOn) push their transient initial values back into the
    // source as their controls are realized — which happens *after* this constructor's
    // scalar restoration. Without this gate those write-backs overwrite the loaded values (blanking
    // ComboBoxes to -1, emptying text boxes) and persist the garbage.
    //
    // Because sections are now created lazily (one per first navigation, cached afterward),
    // this can no longer be a single one-shot bool: General realizes when the window opens,
    // but Analytics/Video/etc. may realize much later, or several sections may be mid-realization
    // at once if the user navigates quickly before an earlier section's Loaded has fired. This
    // counter is incremented when a section begins realizing (see <see cref="BeginSectionRealization"/>)
    // and decremented once that section's first layout pass has completed and its values have been
    // rehydrated (see <see cref="CompleteSectionRealization"/>). Each realizing section remains
    // suppressed independently, so edits in an already-realized section are not dropped while an
    // unrelated section is still waiting for its Loaded event or dispatcher fallback.
    private int _pendingSectionRealizations;
    private readonly Dictionary<SettingsSectionKind, int> _realizingSections = new();

    // Set once the owning SettingsWindow has closed, so in-flight async continuations (media
    // device enumeration, permission prompts) stop touching view-model state.
    private bool _closed;

    private Task? _analyticsInitialization;
    private Task? _mediaDeviceInitialization;
    private int _launchAtLoginRequestVersion;
    private Task _launchAtLoginMutation = Task.CompletedTask;
    internal Task LaunchAtLoginInitialization { get; }
    internal Task LaunchAtLoginMutation => _launchAtLoginMutation;

    /// <summary>Raised when the selected theme changes so the window can re-apply it live.</summary>
    public event Action? ThemeChanged;

    /// <summary>Raised when any Clips Library preference changes so an open Library window can re-read them.</summary>
    public event Action? ClipsLibrarySettingsChanged;

    public void NotifyClipsLibrarySettingsChanged() => ClipsLibrarySettingsChanged?.Invoke();

    public SettingsViewModel(
        ICaptureSettings settings,
        IHotKeyService hotKeys,
        ILaunchAtLoginService launchAtLogin,
        IAudioDeviceService audioDevices,
        IWebcamDeviceEnumerator webcamDevices,
        IClipStorageService storage,
        IClipAnalyticsService analytics,
        IUploadcareCredentialStore uploadcareCredentials)
        : this(settings, hotKeys, launchAtLogin, audioDevices, webcamDevices, storage, analytics,
            uploadcareCredentials, DispatcherQueue.GetForCurrentThread())
    {
    }

    internal SettingsViewModel(
        ICaptureSettings settings,
        IHotKeyService hotKeys,
        ILaunchAtLoginService launchAtLogin,
        IAudioDeviceService audioDevices,
        IWebcamDeviceEnumerator webcamDevices,
        IClipStorageService storage,
        IClipAnalyticsService analytics,
        IUploadcareCredentialStore uploadcareCredentials,
        DispatcherQueue? dispatcherQueue,
        ITeleprompterTranscriptSaveScheduler? transcriptSaveScheduler = null)
    {
        _settings = settings;
        _hotKeys = hotKeys;
        _launchAtLoginService = launchAtLogin;
        _audioDevices = audioDevices;
        _webcamDevices = webcamDevices;
        _storage = storage;
        _analytics = analytics;
        _uploadcareCredentials = uploadcareCredentials;
        _dispatcherQueue = dispatcherQueue;
        _teleprompterTranscriptSaveScheduler = transcriptSaveScheduler ??
            (_dispatcherQueue is null ? null : new DispatcherTranscriptSaveScheduler(_dispatcherQueue));
        RestoreScalarSettings();

        // Analytics history and microphone/webcam enumeration are deferred until their
        // sections are first selected (see EnsureAnalyticsInitializedAsync /
        // EnsureMediaDevicesInitializedAsync), since General is always the first section shown
        // and neither is needed to render it.

        // Reconcile the toggle with the OS-owned launch-at-login (StartupTask) state. General
        // is always the first section realized, so this stays eager.
        LaunchAtLoginInitialization = RefreshLaunchAtLoginAsync();
    }

    /// <summary>
    /// Marks the start of a settings section's first visual-tree realization. Callers must pass
    /// the returned token to <see cref="CompleteSectionRealization"/> once that section's root
    /// element has raised its first <c>Loaded</c> event. Reference-counted so multiple sections
    /// can be mid-realization at once (e.g. rapid navigation) without prematurely re-enabling
    /// persistence.
    /// </summary>
    public IDisposable BeginSectionRealization(SettingsSectionKind kind)
    {
        if (_closed)
        {
            throw new InvalidOperationException("Cannot realize a section after Settings has closed.");
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Settings section.");
        }

        _pendingSectionRealizations++;
        _realizingSections[kind] = _realizingSections.GetValueOrDefault(kind) + 1;
        return new SectionRealizationScope(this, kind);
    }

    /// <summary>
    /// Completes a section's first realization: restores only that section's persisted scalars
    /// into the bound properties to overwrite anything the section's initial TwoWay binding
    /// write-backs may have corrupted, then releases this section's persistence suppression.
    /// Safe to call even if the window has since closed.
    /// </summary>
    public void CompleteSectionRealization(IDisposable realizationScope)
    {
        if (realizationScope is not SectionRealizationScope scope)
        {
            throw new ArgumentException("Expected a Settings section realization scope.", nameof(realizationScope));
        }

        if (scope.IsCompleted)
        {
            return;
        }

        if (!scope.IsOwnedBy(this))
        {
            throw new ArgumentException("The realization scope belongs to another Settings window.", nameof(realizationScope));
        }

        try
        {
            if (!_closed)
            {
                RestoreScalarSettings(scope.Kind);
                InitializeSectionState(scope.Kind);
            }
        }
        finally
        {
            scope.Dispose();
        }

        if (!_closed && scope.Kind == SettingsSectionKind.General)
        {
            ThemeChanged?.Invoke();
        }
    }

    private void EndSectionRealization(SettingsSectionKind kind)
    {
        if (_pendingSectionRealizations > 0)
        {
            _pendingSectionRealizations--;
            if (--_realizingSections[kind] == 0)
            {
                _realizingSections.Remove(kind);
            }
        }
    }

    private bool IsPersistenceSuppressed(SettingsSectionKind kind) =>
        _closed || _loading || (_pendingSectionRealizations > 0 && _realizingSections.ContainsKey(kind));

    /// <summary>Stops async continuations (media enumeration, permission prompts) from touching
    /// this view model once the owning window has closed.</summary>
    public void NotifyClosed()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _teleprompterTranscriptSaveScheduler?.Stop();
        PersistPendingTeleprompterTranscript();
        ReleaseStudio();
    }

    // Tiny Clips Studio's part of this view model is in SettingsViewModel.Studio.cs. A build that
    // leaves that file out has no Studio settings, and these two calls are then not there.
    partial void RestoreStudioSettings(SettingsSectionKind? kind);

    partial void ReleaseStudio();

    private void PersistPendingTeleprompterTranscript()
    {
        if (_pendingTeleprompterTranscript is not { } transcript)
        {
            return;
        }

        _settings.TeleprompterTranscript = transcript;
        _pendingTeleprompterTranscript = null;
    }

    private sealed class SectionRealizationScope : IDisposable
    {
        private SettingsViewModel? _owner;

        public SettingsSectionKind Kind { get; }
        public bool IsCompleted => _owner is null;

        public SectionRealizationScope(SettingsViewModel owner, SettingsSectionKind kind)
        {
            _owner = owner;
            Kind = kind;
        }

        public bool IsOwnedBy(SettingsViewModel owner) => ReferenceEquals(_owner, owner);

        public void Dispose()
        {
            _owner?.EndSectionRealization(Kind);
            _owner = null;
        }
    }

    public string ScreenshotSaveLocationDisplay => ResolveSaveLocation(CaptureType.Screenshot);

    public string VideoSaveLocationDisplay => ResolveSaveLocation(CaptureType.Video);

    public string GifSaveLocationDisplay => ResolveSaveLocation(CaptureType.Gif);

    public string SaveLocationModeDisplay => UseDefaultSaveDirectories
        ? "Screenshots save to Pictures/TinyClips; videos and GIFs save to Videos/TinyClips."
        : "Choose a folder for each capture type.";

    public Microsoft.UI.Xaml.Visibility CustomSaveLocationsVisibility => UseDefaultSaveDirectories
        ? Microsoft.UI.Xaml.Visibility.Collapsed
        : Microsoft.UI.Xaml.Visibility.Visible;

    public string TempFolderSummary => FormatTemporaryFilesSummary(TinyClipsTemporaryFiles.GetSummary());

    private string ResolveSaveLocation(CaptureType type)
    {
        if (UseDefaultSaveDirectories)
        {
            return $"{_storage.OutputDirectory(type)} (default)";
        }

        var customDirectory = type switch
        {
            CaptureType.Screenshot => ScreenshotSaveDirectory,
            CaptureType.Video => VideoSaveDirectory,
            CaptureType.Gif => GifSaveDirectory,
            _ => string.Empty,
        };

        return string.IsNullOrWhiteSpace(customDirectory) ? "No folder selected" : customDirectory;
    }

    public void OpenTempFolder()
    {
        var directory = TinyClipsTemporaryFiles.EnsureDirectoryExists();
        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true,
        });
    }

    public TemporaryFilesPurgeResult PurgeTempFiles()
    {
        var result = TinyClipsTemporaryFiles.Purge(WebcamDiagnostics.ActiveFilePaths);
        OnPropertyChanged(nameof(TempFolderSummary));
        return result;
    }

    private static string FormatTemporaryFilesSummary(TemporaryFilesSummary summary)
    {
        var fileLabel = summary.FileCount == 1 ? "file" : "files";
        return $"{summary.FileCount:N0} {fileLabel}, {FormatFileSize(summary.TotalSize)}";
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:N0} {units[unitIndex]}"
            : $"{value:N1} {units[unitIndex]}";
    }

    // General
    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScreenshotSaveLocationDisplay))]
    [NotifyPropertyChangedFor(nameof(VideoSaveLocationDisplay))]
    [NotifyPropertyChangedFor(nameof(GifSaveLocationDisplay))]
    [NotifyPropertyChangedFor(nameof(SaveLocationModeDisplay))]
    [NotifyPropertyChangedFor(nameof(CustomSaveLocationsVisibility))]
    private bool _useDefaultSaveDirectories;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScreenshotSaveLocationDisplay))]
    private string _screenshotSaveDirectory = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VideoSaveLocationDisplay))]
    private string _videoSaveDirectory = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GifSaveLocationDisplay))]
    private string _gifSaveDirectory = string.Empty;

    [ObservableProperty]
    private string _fileNameTemplate = string.Empty;

    [ObservableProperty]
    private bool _showInExplorer;

    [ObservableProperty]
    private bool _showSaveNotifications;

    [ObservableProperty]
    private bool _confirmEditorEscape;

    // Uploadcare
    [ObservableProperty]
    private bool _uploadcareEnabled;

    [ObservableProperty]
    private string _uploadcarePublicKey = string.Empty;

    [ObservableProperty]
    private bool _uploadcareAutoUpload;

    [ObservableProperty]
    private bool _uploadcareCopyUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UploadcareSecretKeyStatus))]
    private bool _hasUploadcareSecretKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UploadcareSecretKeyStatus))]
    private string? _uploadcareLoadError;

    public string UploadcareSecretKeyStatus => UploadcareLoadError ?? (HasUploadcareSecretKey
        ? "A secret key is stored securely in Windows Credential Locker."
        : "No secret key is stored. It is only required for signed Uploadcare uploads.");

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

    [ObservableProperty]
    private bool _screenshotUsesLiveCapture;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScreenshotCapturePickerAfterCaptureEnabled))]
    private bool _showScreenshotCapturePicker;

    [ObservableProperty]
    private bool _showScreenshotCapturePickerAfterCapture;

    public bool ScreenshotCapturePickerAfterCaptureEnabled => ShowScreenshotCapturePicker;

    // Video
    [ObservableProperty]
    private double _videoFrameRate;

    [ObservableProperty]
    private bool _keepDisplayAwakeWhileRecording;

    [ObservableProperty]
    private bool _useGpuRecordingPipeline;

    [ObservableProperty]
    private int _videoEncoderBackendIndex;

    [ObservableProperty]
    private int _videoCodecIndex;

    [ObservableProperty]
    private bool _recordAudio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicrophoneSelectorEnabled))]
    private bool _recordMicrophone;

    /// <summary>Soft-knee limiter on the microphone path so hot input rounds off instead of clipping.</summary>
    [ObservableProperty]
    private bool _microphoneLimiterEnabled = true;

    /// <summary>Manual A/V offset in ms; positive delays audio, negative plays it earlier.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioOffsetIsNonZero))]
    private double _audioOffsetMilliseconds;

    public bool AudioOffsetIsNonZero => Math.Abs(AudioOffsetMilliseconds) >= 0.5;

    [RelayCommand]
    private void ResetAudioOffset() => AudioOffsetMilliseconds = 0;

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
    private bool _videoCountdownEnabled;

    [ObservableProperty]
    private double _videoCountdownDuration;

    [ObservableProperty]
    private bool _showTrimmer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VideoCapturePickerAfterCaptureEnabled))]
    private bool _showVideoCapturePicker;

    [ObservableProperty]
    private bool _showVideoCapturePickerAfterCapture;

    public bool VideoCapturePickerAfterCaptureEnabled => ShowVideoCapturePicker;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GifCapturePickerAfterCaptureEnabled))]
    private bool _showGifCapturePicker;

    [ObservableProperty]
    private bool _showGifCapturePickerAfterCapture;

    public bool GifCapturePickerAfterCaptureEnabled => ShowGifCapturePicker;

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

    // Teleprompter
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TeleprompterTranscriptEditorEnabled))]
    private bool _teleprompterEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TeleprompterTranscriptEditorEnabled))]
    private bool _isTeleprompterTranscriptLoaded;

    [ObservableProperty]
    private string? _teleprompterTranscriptLoadError;

    public bool TeleprompterTranscriptEditorEnabled => TeleprompterEnabled && IsTeleprompterTranscriptLoaded;

    [ObservableProperty]
    private string _teleprompterTranscript = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TeleprompterScrollSpeedDisplay))]
    private double _teleprompterScrollSpeed = 50;

    public string TeleprompterScrollSpeedDisplay => $"{TeleprompterScrollSpeed:N0} DIPs/s";

    /// <summary>0 = Small, 1 = Medium, 2 = Large (matches <see cref="TeleprompterDisplaySize"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TeleprompterPreviewFontSize))]
    private int _teleprompterFontSizeIndex = (int)TeleprompterDisplaySize.Medium;

    /// <summary>0 = Small, 1 = Medium, 2 = Large (matches <see cref="TeleprompterDisplaySize"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TeleprompterPreviewPanelHeight))]
    private int _teleprompterPanelHeightIndex = (int)TeleprompterDisplaySize.Medium;

    public double TeleprompterPreviewFontSize => ToTeleprompterDisplaySize(TeleprompterFontSizeIndex).FontSize();

    public double TeleprompterPreviewPanelHeight => ToTeleprompterDisplaySize(TeleprompterPanelHeightIndex).PanelHeight();

    /// <summary>Raised when the overlay text size or panel height preset changes, so a live overlay can re-apply it.</summary>
    public event Action? TeleprompterDisplayChanged;

    private static TeleprompterDisplaySize ToTeleprompterDisplaySize(int index) => index switch
    {
        0 => TeleprompterDisplaySize.Small,
        2 => TeleprompterDisplaySize.Large,
        _ => TeleprompterDisplaySize.Medium,
    };

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

    public string ScreenshotHotKeyDisplay => _hotKeys.GetBinding(HotKeyAction.Screenshot).DisplayString;

    public string VideoHotKeyDisplay => _hotKeys.GetBinding(HotKeyAction.RecordVideo).DisplayString;

    public string GifHotKeyDisplay => _hotKeys.GetBinding(HotKeyAction.RecordGif).DisplayString;

    public string OcrHotKeyDisplay => _hotKeys.GetBinding(HotKeyAction.RecognizeText).DisplayString;

    public string ScreenshotRegionHotKeyDisplay => _hotKeys.GetBinding(HotKeyAction.ScreenshotRegion).DisplayString;

    public string ScreenshotWindowHotKeyDisplay => _hotKeys.GetBinding(HotKeyAction.ScreenshotWindow).DisplayString;

    public HotKeyDefinition GetHotKey(HotKeyAction action) => _hotKeys.GetBinding(action);

    public HotKeyDefinition GetDefaultHotKey(HotKeyAction action) => _hotKeys.DefaultFor(action);

    public HotKeyValidationResult ValidateHotKey(HotKeyAction action, HotKeyDefinition binding)
        => _hotKeys.ValidateBinding(action, binding);

    /// <summary>Persists a new global shortcut for the given action and refreshes the display.</summary>
    public void SetHotKey(HotKeyAction action, HotKeyModifiers modifiers, uint virtualKey)
    {
        _hotKeys.SetBinding(action, new HotKeyDefinition(modifiers, virtualKey));
        RaiseHotKeyDisplays();
    }

    /// <summary>Restores the default shortcut for the given action.</summary>
    public void ResetHotKey(HotKeyAction action)
    {
        _hotKeys.SetBinding(action, _hotKeys.DefaultFor(action));
        RaiseHotKeyDisplays();
    }

    private void RaiseHotKeyDisplays()
    {
        OnPropertyChanged(nameof(ScreenshotHotKeyDisplay));
        OnPropertyChanged(nameof(VideoHotKeyDisplay));
        OnPropertyChanged(nameof(GifHotKeyDisplay));
        OnPropertyChanged(nameof(OcrHotKeyDisplay));
        OnPropertyChanged(nameof(ScreenshotRegionHotKeyDisplay));
        OnPropertyChanged(nameof(ScreenshotWindowHotKeyDisplay));
    }

    /// <summary>
    /// Loads capture analytics the first time the Analytics section is selected. Idempotent — the
    /// first call kicks off the (synchronous, but wrapped for future-proofing and error isolation)
    /// load and caches the task; later calls just await the same completed task instead of
    /// re-querying the analytics store.
    /// </summary>
    public Task EnsureAnalyticsInitializedAsync() => _closed
        ? Task.CompletedTask
        : _analyticsInitialization ??= InitializeAnalyticsAsync();

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
    public Task EnsureMediaDevicesInitializedAsync() => _closed
        ? Task.CompletedTask
        : _mediaDeviceInitialization ??= InitializeMediaDevicesAsync();

    private async Task InitializeMediaDevicesAsync()
    {
        MediaDevicesLoadError = null;

        var errors = await Task.WhenAll(LoadMicrophonesAsync(), LoadWebcamsAsync());
        await RunOnOwnerThreadAsync(() =>
        {
            MediaDevicesLoadError = string.Join(
                " ",
                errors
                    .Where(error => !string.IsNullOrWhiteSpace(error))
                    .Distinct());
        });
    }

    private void RestoreScalarSettings(SettingsSectionKind? kind = null)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            if (kind is null or SettingsSectionKind.General)
            {
                ThemeIndex = _settings.Theme switch
                {
                    AppTheme.Light => 1,
                    AppTheme.Dark => 2,
                    _ => 0,
                };
                UseDefaultSaveDirectories = _settings.UseDefaultSaveDirectories;
                ScreenshotSaveDirectory = _settings.ScreenshotSaveDirectory;
                VideoSaveDirectory = _settings.VideoSaveDirectory;
                GifSaveDirectory = _settings.GifSaveDirectory;
                FileNameTemplate = string.IsNullOrWhiteSpace(_settings.FileNameTemplate)
                    ? "TinyClips {date} at {time}"
                    : _settings.FileNameTemplate;
                ShowInExplorer = _settings.ShowInExplorer;
                ShowSaveNotifications = _settings.ShowSaveNotifications;
                ConfirmEditorEscape = _settings.ConfirmEditorEscape;
                LaunchAtLogin = _settings.LaunchAtLogin;
                ShowBrandingOverlay = _settings.ShowBrandingOverlay;
                MultiMonitorCaptureModeIndex = _settings.MultiMonitorCaptureMode switch
                {
                    MultiMonitorCaptureMode.UnderCursor => 1,
                    MultiMonitorCaptureMode.MainDisplay => 2,
                    _ => 0,
                };
            }

            if (kind is null or SettingsSectionKind.Uploadcare)
            {
                UploadcareEnabled = _settings.UploadcareEnabled;
                UploadcarePublicKey = _settings.UploadcarePublicKey;
                UploadcareAutoUpload = _settings.UploadcareAutoUpload;
                UploadcareCopyUrl = _settings.UploadcareCopyUrl;
            }

            if (kind is null or SettingsSectionKind.Screenshot)
            {
                CopyScreenshotToClipboard = _settings.CopyScreenshotToClipboard;
                ScreenshotFormatIndex = _settings.ImageFormat switch
                {
                    ImageFormat.Png => 0,
                    ImageFormat.Webp => 2,
                    _ => 1,
                };
                ScreenshotScale = _settings.ScreenshotScale;
                JpegQuality = _settings.JpegQuality;
                ScreenshotCountdownEnabled = _settings.ScreenshotCountdownEnabled;
                ScreenshotCountdownDuration = _settings.ScreenshotCountdownDuration;
                ShowScreenshotEditor = _settings.ShowScreenshotEditor;
                ScreenshotUsesLiveCapture = _settings.ScreenshotUsesLiveCapture;
                ShowScreenshotCapturePicker = _settings.ShowScreenshotCapturePicker;
                ShowScreenshotCapturePickerAfterCapture = _settings.ShowScreenshotCapturePickerAfterCapture;
            }

            if (kind is null or SettingsSectionKind.Video)
            {
                CopyVideoToClipboard = _settings.CopyVideoToClipboard;
                VideoFrameRate = _settings.VideoFrameRate;
                KeepDisplayAwakeWhileRecording = _settings.KeepDisplayAwakeWhileRecording;
                UseGpuRecordingPipeline = _settings.UseGpuRecordingPipeline;
                VideoEncoderBackendIndex = _settings.VideoEncoderBackend == VideoEncoderBackend.SinkWriter ? 1 : 0;
                VideoCodecIndex = _settings.VideoCodec == VideoCodec.Hevc ? 1 : 0;
                RecordAudio = _settings.RecordAudio;
                RecordMicrophone = _settings.RecordMicrophone;
                MicrophoneLimiterEnabled = _settings.MicrophoneLimiterEnabled;
                AudioOffsetMilliseconds = _settings.AudioOffsetMilliseconds;

                _savedMicrophoneId = _settings.SelectedMicrophoneId ?? string.Empty;
                if (Microphones.Count > 0)
                {
                    SelectedMicrophone = Microphones.FirstOrDefault(device => device.Id == _savedMicrophoneId) ?? Microphones[0];
                }
                WebcamEnabled = _settings.WebcamEnabled;
                _savedWebcamId = _settings.SelectedWebcamId ?? string.Empty;
                if (Webcams.Count > 0)
                {
                    SelectedWebcam = Webcams.FirstOrDefault(device => device.Id == _savedWebcamId) ?? Webcams[0];
                }
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
                VideoCountdownEnabled = _settings.VideoCountdownEnabled;
                VideoCountdownDuration = _settings.VideoCountdownDuration;
                ShowTrimmer = _settings.ShowTrimmer;
                ShowVideoCapturePicker = _settings.ShowVideoCapturePicker;
                ShowVideoCapturePickerAfterCapture = _settings.ShowVideoCapturePickerAfterCapture;
            }

            if (kind is null or SettingsSectionKind.Gif)
            {
                CopyGifToClipboard = _settings.CopyGifToClipboard;
                GifFrameRate = _settings.GifFrameRate;
                GifMaxWidth = _settings.GifMaxWidth;
                GifCountdownEnabled = _settings.GifCountdownEnabled;
                GifCountdownDuration = _settings.GifCountdownDuration;
                ShowGifTrimmer = _settings.ShowGifTrimmer;
                ShowGifCapturePicker = _settings.ShowGifCapturePicker;
                ShowGifCapturePickerAfterCapture = _settings.ShowGifCapturePickerAfterCapture;
            }

            if (kind is null or SettingsSectionKind.MouseClicks)
            {
                ShowMouseClicksInVideo = _settings.ShowMouseClickVisualsInVideo;
                ShowMouseClicksInGif = _settings.ShowMouseClickVisualsInGif;
                GifMouseClicksUseVideoSettings = _settings.GifMouseClicksUseVideoSettings;
                VideoMouseClickSize = _settings.VideoMouseClickSize;
                VideoMouseClickOpacity = _settings.VideoMouseClickOpacity;
                VideoMouseClickColorHex = _settings.VideoMouseClickColorHex;
                GifMouseClickSize = _settings.GifMouseClickSize;
                GifMouseClickOpacity = _settings.GifMouseClickOpacity;
                GifMouseClickColorHex = _settings.GifMouseClickColorHex;
            }

            if (kind is null or SettingsSectionKind.Teleprompter)
            {
                TeleprompterEnabled = _settings.TeleprompterEnabled;
                TeleprompterScrollSpeed = Math.Clamp(_settings.TeleprompterScrollSpeed, 10.0, 200.0);
                TeleprompterFontSizeIndex = (int)_settings.TeleprompterFontSize;
                TeleprompterPanelHeightIndex = (int)_settings.TeleprompterPanelHeight;
            }

            RestoreStudioSettings(kind);
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void InitializeSectionState(SettingsSectionKind kind)
    {
        if (_closed)
        {
            return;
        }

        var wasLoading = _loading;
        _loading = true;
        try
        {
            if (kind == SettingsSectionKind.Uploadcare && !_uploadcareCredentialsInitialized)
            {
                _uploadcareCredentialsInitialized = true;
                try
                {
                    HasUploadcareSecretKey = _uploadcareCredentials.HasSecretKey();
                    UploadcareLoadError = null;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Unable to read Uploadcare credential status ({ex.GetType().Name}).");
                    UploadcareLoadError = "Couldn't read the stored secret key status. Reopen Settings to try again.";
                }
            }

            if (kind == SettingsSectionKind.Teleprompter)
            {
                if (!_teleprompterTranscriptInitialized)
                {
                    _teleprompterTranscriptInitialized = true;
                    try
                    {
                        _savedTeleprompterTranscript = _settings.GetTeleprompterTranscriptForEditing();
                        IsTeleprompterTranscriptLoaded = true;
                        TeleprompterTranscriptLoadError = null;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Unable to load the teleprompter transcript ({ex.GetType().Name}).");
                        TeleprompterTranscriptLoadError = "Couldn't load the transcript. Reopen Settings to try again.";
                    }
                }

                // An edit awaiting the debounce timer is newer than the persisted text.
                if (_savedTeleprompterTranscript is { } transcript)
                {
                    TeleprompterTranscript = _pendingTeleprompterTranscript ?? transcript;
                }
            }
        }
        finally
        {
            _loading = wasLoading;
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

            await RunOnOwnerThreadAsync(() => ApplyMicrophones(microphones));
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
            await RunOnOwnerThreadAsync(() => IsMicrophonesLoading = false);
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

            await RunOnOwnerThreadAsync(() => ApplyWebcams(webcams));
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
            await RunOnOwnerThreadAsync(() => IsWebcamsLoading = false);
        }
    }

    private async Task RunOnOwnerThreadAsync(Action apply)
    {
        if (_closed)
        {
            return;
        }

        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            apply();
            return;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (!_closed)
                    {
                        apply();
                    }
                    completion.SetResult(true);
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }))
        {
            throw new InvalidOperationException("Unable to update Settings on the UI thread.");
        }

        await completion.Task;
    }

    private void ApplyMicrophones(IReadOnlyList<AudioInputDevice> microphones)
    {
        var wasLoading = _loading;
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
            IsMicrophonesLoading = false;
            _loading = wasLoading;
        }
    }

    private void ApplyWebcams(IReadOnlyList<WebcamDeviceInfo> webcams)
    {
        var wasLoading = _loading;
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
            IsWebcamsLoading = false;
            _loading = wasLoading;
        }
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (IsPersistenceSuppressed(SettingsSectionKind.General))
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

    partial void OnUseDefaultSaveDirectoriesChanged(bool value) =>
        Persist(SettingsSectionKind.General, () => _settings.UseDefaultSaveDirectories = value);

    partial void OnScreenshotSaveDirectoryChanged(string value) =>
        Persist(SettingsSectionKind.General, () => _settings.ScreenshotSaveDirectory = value);

    partial void OnVideoSaveDirectoryChanged(string value) =>
        Persist(SettingsSectionKind.General, () => _settings.VideoSaveDirectory = value);

    partial void OnGifSaveDirectoryChanged(string value) =>
        Persist(SettingsSectionKind.General, () => _settings.GifSaveDirectory = value);

    partial void OnFileNameTemplateChanged(string value) => Persist(SettingsSectionKind.General, () => _settings.FileNameTemplate = value);

    partial void OnShowInExplorerChanged(bool value) => Persist(SettingsSectionKind.General, () => _settings.ShowInExplorer = value);

    partial void OnShowSaveNotificationsChanged(bool value) => Persist(SettingsSectionKind.General, () => _settings.ShowSaveNotifications = value);

    partial void OnConfirmEditorEscapeChanged(bool value) => Persist(SettingsSectionKind.General, () => _settings.ConfirmEditorEscape = value);

    partial void OnUploadcareEnabledChanged(bool value) => Persist(SettingsSectionKind.Uploadcare, () => _settings.UploadcareEnabled = value);

    partial void OnUploadcarePublicKeyChanged(string value) => Persist(SettingsSectionKind.Uploadcare, () => _settings.UploadcarePublicKey = value);

    partial void OnUploadcareAutoUploadChanged(bool value) => Persist(SettingsSectionKind.Uploadcare, () => _settings.UploadcareAutoUpload = value);

    partial void OnUploadcareCopyUrlChanged(bool value) => Persist(SettingsSectionKind.Uploadcare, () => _settings.UploadcareCopyUrl = value);

    public void SaveUploadcareSecretKey(string secretKey)
    {
        if (_closed)
        {
            return;
        }

        _uploadcareCredentials.SaveSecretKey(secretKey);
        _uploadcareCredentialsInitialized = true;
        UploadcareLoadError = null;
        HasUploadcareSecretKey = true;
    }

    public void ClearUploadcareSecretKey()
    {
        if (_closed)
        {
            return;
        }

        _uploadcareCredentials.RemoveSecretKey();
        _uploadcareCredentialsInitialized = true;
        UploadcareLoadError = null;
        HasUploadcareSecretKey = false;
    }

    partial void OnLaunchAtLoginChanged(bool value)
    {
        if (IsPersistenceSuppressed(SettingsSectionKind.General) || _suppressLaunchAtLogin)
        {
            return;
        }

        _ = ApplyLaunchAtLoginAsync(value);
    }

    private Task ApplyLaunchAtLoginAsync(bool value)
    {
        var version = ++_launchAtLoginRequestVersion;
        return _launchAtLoginMutation = ApplyLaunchAtLoginAfterAsync(_launchAtLoginMutation, value, version);
    }

    private async Task ApplyLaunchAtLoginAfterAsync(Task previousMutation, bool value, int version)
    {
        // Consent may complete after a later toggle edit. Serialize OS mutations so the last
        // requested state is applied last, not merely displayed last.
        await previousMutation;
        if (_closed)
        {
            return;
        }

        await ReconcileLaunchAtLoginAsync(() => _launchAtLoginService.SetEnabledAsync(value), version);
    }

    private async Task RefreshLaunchAtLoginAsync()
    {
        var version = ++_launchAtLoginRequestVersion;
        await ReconcileLaunchAtLoginAsync(_launchAtLoginService.GetStateAsync, version);
    }

    private async Task ReconcileLaunchAtLoginAsync(Func<Task<LaunchAtLoginState>> readState, int version)
    {
        try
        {
            var state = await readState();
            await RunOnOwnerThreadAsync(() =>
            {
                if (version == _launchAtLoginRequestVersion)
                {
                    ApplyLaunchAtLoginState(state);
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to reconcile launch at login ({ex.GetType().Name}).");
            await RunOnOwnerThreadAsync(() =>
            {
                if (version == _launchAtLoginRequestVersion)
                {
                    LaunchAtLoginNote = "Couldn't update launch at login. Reopen Settings to try again.";
                }
            });
        }
    }

    private void ApplyLaunchAtLoginState(LaunchAtLoginState state)
    {
        var enabled = state is LaunchAtLoginState.Enabled or LaunchAtLoginState.EnabledByPolicy;

        // Reflect OS truth without re-triggering the change handler.
        _suppressLaunchAtLogin = true;
        try
        {
            LaunchAtLogin = enabled;
        }
        finally
        {
            _suppressLaunchAtLogin = false;
        }

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

    partial void OnCopyScreenshotToClipboardChanged(bool value) => Persist(SettingsSectionKind.Screenshot, () => _settings.CopyScreenshotToClipboard = value);

    partial void OnCopyVideoToClipboardChanged(bool value) => Persist(SettingsSectionKind.Video, () => _settings.CopyVideoToClipboard = value);

    partial void OnCopyGifToClipboardChanged(bool value) => Persist(SettingsSectionKind.Gif, () => _settings.CopyGifToClipboard = value);

    partial void OnMultiMonitorCaptureModeIndexChanged(int value) => Persist(SettingsSectionKind.General, () => _settings.MultiMonitorCaptureMode = value switch
    {
        1 => MultiMonitorCaptureMode.UnderCursor,
        2 => MultiMonitorCaptureMode.MainDisplay,
        _ => MultiMonitorCaptureMode.Picker,
    });

    partial void OnScreenshotFormatIndexChanged(int value) =>
        Persist(SettingsSectionKind.Screenshot, () => _settings.ImageFormat = value switch
        {
            0 => ImageFormat.Png,
            2 => ImageFormat.Webp,
            _ => ImageFormat.Jpeg,
        });

    partial void OnScreenshotScaleChanged(double value) => Persist(SettingsSectionKind.Screenshot, () => _settings.ScreenshotScale = (int)Math.Round(value));

    partial void OnJpegQualityChanged(double value) => Persist(SettingsSectionKind.Screenshot, () => _settings.JpegQuality = value);

    partial void OnScreenshotCountdownEnabledChanged(bool value) => Persist(SettingsSectionKind.Screenshot, () => _settings.ScreenshotCountdownEnabled = value);

    partial void OnScreenshotCountdownDurationChanged(double value) =>
        Persist(SettingsSectionKind.Screenshot, () => _settings.ScreenshotCountdownDuration = (int)Math.Round(value));

    partial void OnShowScreenshotEditorChanged(bool value) => Persist(SettingsSectionKind.Screenshot, () => _settings.ShowScreenshotEditor = value);

    partial void OnScreenshotUsesLiveCaptureChanged(bool value) => Persist(SettingsSectionKind.Screenshot, () => _settings.ScreenshotUsesLiveCapture = value);

    partial void OnShowScreenshotCapturePickerChanged(bool value) =>
        Persist(SettingsSectionKind.Screenshot, () =>
        {
            _settings.ShowScreenshotCapturePicker = value;
            if (!value)
            {
                ShowScreenshotCapturePickerAfterCapture = false;
            }
        });

    partial void OnShowScreenshotCapturePickerAfterCaptureChanged(bool value)
    {
        if (value && !ShowScreenshotCapturePicker)
        {
            ShowScreenshotCapturePickerAfterCapture = false;
            return;
        }

        Persist(SettingsSectionKind.Screenshot, () => _settings.ShowScreenshotCapturePickerAfterCapture = value);
    }

    partial void OnVideoFrameRateChanged(double value) => Persist(SettingsSectionKind.Video, () => _settings.VideoFrameRate = (int)Math.Round(value));

    partial void OnKeepDisplayAwakeWhileRecordingChanged(bool value) =>
        Persist(SettingsSectionKind.Video, () => _settings.KeepDisplayAwakeWhileRecording = value);

    partial void OnUseGpuRecordingPipelineChanged(bool value) =>
        Persist(SettingsSectionKind.Video, () => _settings.UseGpuRecordingPipeline = value);

    partial void OnVideoEncoderBackendIndexChanged(int value) =>
        Persist(SettingsSectionKind.Video, () => _settings.VideoEncoderBackend = value == 1 ? VideoEncoderBackend.SinkWriter : VideoEncoderBackend.Transcoder);

    partial void OnVideoCodecIndexChanged(int value) =>
        Persist(SettingsSectionKind.Video, () => _settings.VideoCodec = value == 1 ? VideoCodec.Hevc : VideoCodec.H264);

    partial void OnRecordAudioChanged(bool value) => Persist(SettingsSectionKind.Video, () => _settings.RecordAudio = value);

    partial void OnRecordMicrophoneChanged(bool value) => Persist(SettingsSectionKind.Video, () => _settings.RecordMicrophone = value);

    partial void OnMicrophoneLimiterEnabledChanged(bool value) => Persist(SettingsSectionKind.Video, () => _settings.MicrophoneLimiterEnabled = value);

    partial void OnAudioOffsetMillisecondsChanged(double value)
    {
        // NumberBox reports NaN when its text is cleared; treat that as "no offset".
        if (double.IsNaN(value))
        {
            AudioOffsetMilliseconds = 0;
            return;
        }

        Persist(SettingsSectionKind.Video, () => _settings.AudioOffsetMilliseconds = (int)Math.Round(value));
    }

    partial void OnSelectedMicrophoneChanged(AudioInputDevice? value) =>
        Persist(SettingsSectionKind.Video, () =>
        {
            _savedMicrophoneId = value?.Id ?? string.Empty;
            _settings.SelectedMicrophoneId = _savedMicrophoneId;
        });

    partial void OnWebcamEnabledChanged(bool value) => Persist(SettingsSectionKind.Video, () => _settings.WebcamEnabled = value);

    partial void OnSelectedWebcamChanged(WebcamDeviceInfo? value) =>
        Persist(SettingsSectionKind.Video, () =>
        {
            _savedWebcamId = value?.Id ?? string.Empty;
            _settings.SelectedWebcamId = _savedWebcamId;
        });

    partial void OnWebcamShapeIndexChanged(int value) => Persist(SettingsSectionKind.Video, () => _settings.WebcamShape = value switch
    {
        0 => WebcamShape.Rectangle,
        1 => WebcamShape.RoundedRectangle,
        _ => WebcamShape.Circle,
    });

    partial void OnWebcamSizePresetIndexChanged(int value) => Persist(SettingsSectionKind.Video, () => _settings.WebcamSizePreset = value switch
    {
        0 => WebcamSizePreset.Small,
        2 => WebcamSizePreset.Large,
        _ => WebcamSizePreset.Medium,
    });

    partial void OnWebcamCornerPositionIndexChanged(int value) => Persist(SettingsSectionKind.Video, () => _settings.WebcamCornerPosition = value switch
    {
        0 => WebcamCornerPosition.TopLeft,
        1 => WebcamCornerPosition.TopRight,
        2 => WebcamCornerPosition.BottomLeft,
        _ => WebcamCornerPosition.BottomRight,
    });

    partial void OnWebcamCornerRadiusChanged(double value) =>
        Persist(SettingsSectionKind.Video, () => _settings.WebcamCornerRadius = value < 0 ? null : value);

    partial void OnVideoRecordingTimeLimitMinutesChanged(double value) =>
        Persist(SettingsSectionKind.Video, () => _settings.VideoRecordingTimeLimitMinutes = (int)Math.Round(value));

    partial void OnVideoCountdownEnabledChanged(bool value) => Persist(SettingsSectionKind.Video, () => _settings.VideoCountdownEnabled = value);

    partial void OnVideoCountdownDurationChanged(double value) =>
        Persist(SettingsSectionKind.Video, () => _settings.VideoCountdownDuration = (int)Math.Round(value));

    partial void OnShowTrimmerChanged(bool value) => Persist(SettingsSectionKind.Video, () => _settings.ShowTrimmer = value);

    partial void OnShowVideoCapturePickerChanged(bool value) =>
        Persist(SettingsSectionKind.Video, () =>
        {
            _settings.ShowVideoCapturePicker = value;
            if (!value)
            {
                ShowVideoCapturePickerAfterCapture = false;
            }
        });

    partial void OnShowVideoCapturePickerAfterCaptureChanged(bool value)
    {
        if (value && !ShowVideoCapturePicker)
        {
            ShowVideoCapturePickerAfterCapture = false;
            return;
        }

        Persist(SettingsSectionKind.Video, () => _settings.ShowVideoCapturePickerAfterCapture = value);
    }

    partial void OnGifFrameRateChanged(double value) => Persist(SettingsSectionKind.Gif, () => _settings.GifFrameRate = value);

    partial void OnGifMaxWidthChanged(double value) => Persist(SettingsSectionKind.Gif, () => _settings.GifMaxWidth = (int)Math.Round(value));

    partial void OnGifCountdownEnabledChanged(bool value) => Persist(SettingsSectionKind.Gif, () => _settings.GifCountdownEnabled = value);

    partial void OnGifCountdownDurationChanged(double value) =>
        Persist(SettingsSectionKind.Gif, () => _settings.GifCountdownDuration = (int)Math.Round(value));

    partial void OnShowGifTrimmerChanged(bool value) => Persist(SettingsSectionKind.Gif, () => _settings.ShowGifTrimmer = value);

    partial void OnShowGifCapturePickerChanged(bool value) =>
        Persist(SettingsSectionKind.Gif, () =>
        {
            _settings.ShowGifCapturePicker = value;
            if (!value)
            {
                ShowGifCapturePickerAfterCapture = false;
            }
        });

    partial void OnShowGifCapturePickerAfterCaptureChanged(bool value)
    {
        if (value && !ShowGifCapturePicker)
        {
            ShowGifCapturePickerAfterCapture = false;
            return;
        }

        Persist(SettingsSectionKind.Gif, () => _settings.ShowGifCapturePickerAfterCapture = value);
    }

    partial void OnShowMouseClicksInVideoChanged(bool value) => Persist(SettingsSectionKind.MouseClicks, () => _settings.ShowMouseClickVisualsInVideo = value);

    partial void OnShowMouseClicksInGifChanged(bool value) => Persist(SettingsSectionKind.MouseClicks, () => _settings.ShowMouseClickVisualsInGif = value);

    partial void OnGifMouseClicksUseVideoSettingsChanged(bool value) => Persist(SettingsSectionKind.MouseClicks, () =>
    {
        _settings.GifMouseClicksUseVideoSettings = value;
        OnPropertyChanged(nameof(GifMouseClickPreviewColorHex));
    });

    partial void OnVideoMouseClickSizeChanged(double value) => Persist(SettingsSectionKind.MouseClicks, () => _settings.VideoMouseClickSize = value);

    partial void OnVideoMouseClickOpacityChanged(double value) => Persist(SettingsSectionKind.MouseClicks, () => _settings.VideoMouseClickOpacity = value);

    partial void OnVideoMouseClickColorHexChanged(string value) => Persist(SettingsSectionKind.MouseClicks, () =>
    {
        _settings.VideoMouseClickColorHex = value;
        if (_settings.GifMouseClicksUseVideoSettings)
        {
            _settings.GifMouseClickColorHex = value;
            OnPropertyChanged(nameof(GifMouseClickPreviewColorHex));
        }
    });

    partial void OnGifMouseClickSizeChanged(double value) => Persist(SettingsSectionKind.MouseClicks, () => _settings.GifMouseClickSize = value);

    partial void OnGifMouseClickOpacityChanged(double value) => Persist(SettingsSectionKind.MouseClicks, () => _settings.GifMouseClickOpacity = value);

    partial void OnGifMouseClickColorHexChanged(string value) => Persist(SettingsSectionKind.MouseClicks, () => _settings.GifMouseClickColorHex = value);

    partial void OnShowBrandingOverlayChanged(bool value) => Persist(SettingsSectionKind.General, () => _settings.ShowBrandingOverlay = value);

    partial void OnTeleprompterEnabledChanged(bool value) => Persist(SettingsSectionKind.Teleprompter, () => _settings.TeleprompterEnabled = value);

    partial void OnTeleprompterTranscriptChanged(string value)
    {
        if (IsPersistenceSuppressed(SettingsSectionKind.Teleprompter))
        {
            return;
        }

        AcceptTeleprompterTranscript(value);
    }

    /// <summary>Applies an imported transcript as an intentional mutation, not a binding write-back.</summary>
    public void ImportTeleprompterTranscript(string transcript)
    {
        if (_closed)
        {
            return;
        }

        var wasLoading = _loading;
        _loading = true;
        try
        {
            TeleprompterTranscript = transcript;
            AcceptTeleprompterTranscript(transcript);
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void AcceptTeleprompterTranscript(string value)
    {
        _savedTeleprompterTranscript = value;
        _teleprompterTranscriptInitialized = true;
        IsTeleprompterTranscriptLoaded = true;
        TeleprompterTranscriptLoadError = null;
        _pendingTeleprompterTranscript = value;
        if (_teleprompterTranscriptSaveScheduler is null)
        {
            PersistPendingTeleprompterTranscript();
            return;
        }

        _teleprompterTranscriptSaveScheduler.Restart(PersistPendingTeleprompterTranscript);
    }

    partial void OnTeleprompterScrollSpeedChanged(double value) => Persist(SettingsSectionKind.Teleprompter, () => _settings.TeleprompterScrollSpeed = value);

    partial void OnTeleprompterFontSizeIndexChanged(int value) => Persist(SettingsSectionKind.Teleprompter, () =>
    {
        _settings.TeleprompterFontSize = ToTeleprompterDisplaySize(value);
        TeleprompterDisplayChanged?.Invoke();
    });

    partial void OnTeleprompterPanelHeightIndexChanged(int value) => Persist(SettingsSectionKind.Teleprompter, () =>
    {
        _settings.TeleprompterPanelHeight = ToTeleprompterDisplaySize(value);
        TeleprompterDisplayChanged?.Invoke();
    });

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

    private void Persist(SettingsSectionKind kind, Action apply)
    {
        if (IsPersistenceSuppressed(kind))
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
        if (_closed)
        {
            return;
        }

        _uploadcareCredentials.RemoveSecretKey();
        _settings.ResetToDefaults();
        _teleprompterTranscriptSaveScheduler?.Stop();
        _pendingTeleprompterTranscript = null;
        _savedTeleprompterTranscript = string.Empty;
        _teleprompterTranscriptInitialized = true;
        _uploadcareCredentialsInitialized = true;
        UploadcareLoadError = null;
        HasUploadcareSecretKey = false;
        TeleprompterTranscriptLoadError = null;
        IsTeleprompterTranscriptLoaded = true;
        RestoreScalarSettings();
        InitializeSectionState(SettingsSectionKind.Teleprompter);
        ThemeChanged?.Invoke();
        // Restoration runs under the _loading guard, so the per-property persistence callbacks (and
        // their live notifications) are suppressed; tell an active overlay explicitly.
        TeleprompterDisplayChanged?.Invoke();
        _ = ApplyLaunchAtLoginAsync(_settings.LaunchAtLogin);
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
