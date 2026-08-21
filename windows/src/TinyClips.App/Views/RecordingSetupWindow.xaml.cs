using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.System;
using WinRT.Interop;

namespace TinyClips.App;

public sealed record RecordingSetupResult(
    bool RecordSystemAudio,
    bool RecordMicrophone,
    string SelectedMicrophoneId,
    bool WebcamEnabled,
    string SelectedWebcamId,
    WebcamShape WebcamShape,
    WebcamSizePreset WebcamSizePreset,
    WebcamCornerPosition WebcamCornerPosition,
    double? WebcamCornerRadius,
    bool ShowMouseClicks);

/// <summary>
/// Pre-recording setup panel shown after target selection and before countdown.
/// </summary>
/// <remarks>
/// This window owns placement (DPI-safe positioning near the capture target/monitor), drag
/// movement, key handling, completion/double-fire guarding, and top-level capture result
/// coordination. Device selection UI (microphone/webcam toggles, device pickers, and webcam
/// option flyouts) is delegated to <see cref="RecordingSetup.AudioDeviceControl"/> and
/// <see cref="RecordingSetup.WebcamOptionsControl"/>, which cache their flyout menu items so
/// ordinary selection changes update in place instead of rebuilding the flyouts. Permission
/// decisions (which need this window's close guard and cross-control coordination — e.g. enabling
/// the webcam also requests microphone access) stay here.
/// </remarks>
public sealed partial class RecordingSetupWindow : Window
{
    private const int TopOffsetDip = 24;
    private const int RegionOutsideOffsetDip = 12;

    private readonly TaskCompletionSource<RecordingSetupResult?> _result = new();
    private readonly CaptureType _captureType;
    private readonly IAudioDeviceService _audioDevices;
    private readonly IWebcamDeviceEnumerator _webcamDevices;
    private readonly IMediaDevicePermissionService _mediaPermissions;
    private readonly IWebcamCaptureService _previewCapture = new WebcamCaptureService();
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private CancellationTokenSource _previewCts = new();

    private bool _completed;
    private bool _closed;
    private RecordingSetupResult? _pendingResult;
    private bool _suppressEvents;
    private bool _showMouseClicks;
    private bool _microphonePermissionPending;
    private bool _webcamPermissionPending;

    private readonly FloatingWindowDragger _dragger;

    private RecordingSetupWindow(
        CaptureType captureType,
        ICaptureSettings settings,
        IAudioDeviceService audioDevices,
        IWebcamDeviceEnumerator webcamDevices,
        IMediaDevicePermissionService mediaPermissions)
    {
        InitializeComponent();

        _captureType = captureType;
        _audioDevices = audioDevices;
        _webcamDevices = webcamDevices;
        _mediaPermissions = mediaPermissions;
        _showMouseClicks = settings.ShouldShowMouseClickVisuals(captureType);

        AudioDevices.MicrophoneToggleRequested += OnMicrophoneToggleRequested;
        WebcamOptions.WebcamToggleRequested += OnWebcamToggleRequested;
        WebcamOptions.PreviewSourceChanged += OnPreviewSourceChanged;
        AudioDevices.ReadinessChanged += OnSelectionReadinessChanged;
        WebcamOptions.ReadinessChanged += OnSelectionReadinessChanged;

        AudioDevices.Initialize(settings.RecordAudio, settings.RecordMicrophone, settings.SelectedMicrophoneId);
        WebcamOptions.Initialize(
            captureType,
            settings.WebcamEnabled,
            settings.SelectedWebcamId,
            settings.WebcamShape,
            settings.WebcamSizePreset,
            settings.WebcamCornerPosition,
            settings.WebcamCornerRadius);

        ConfigurePresenter();
        _dragger = new FloatingWindowDragger(AppWindow);
        ConfigureForCaptureType();
        UpdateMouseClicksVisual();
        UpdateStartButtonEnabled();

        Closed += OnClosed;
    }

    public static Task<RecordingSetupResult?> RunAsync(
        CaptureType captureType,
        ICaptureSettings settings,
        IAudioDeviceService audioDevices,
        IWebcamDeviceEnumerator webcamDevices,
        IMediaDevicePermissionService mediaPermissions,
        MonitorInfo? monitor,
        PixelRect? regionInVirtualDesktop)
    {
        var window = new RecordingSetupWindow(
            captureType,
            settings,
            audioDevices,
            webcamDevices,
            mediaPermissions);
        window.ShowNear(monitor, regionInVirtualDesktop);
        CaptureFlowTrace.Mark("setup: panel shown");
        if (captureType == CaptureType.Video)
        {
            _ = window.LoadMicrophonesAsync();
            _ = window.LoadWebcamsAsync();
        }

        return window._result.Task;
    }

    private void ConfigureForCaptureType()
    {
        var isVideo = _captureType != CaptureType.Gif;
        AudioDevices.SetVisibleForVideo(isVideo);
        WebcamOptions.SetVisibleForVideo(isVideo);
    }

    private async Task LoadMicrophonesAsync()
    {
        AudioDevices.SetMicrophoneLoading(true);
        try
        {
            var microphones = await Task.Run(() => _audioDevices.GetMicrophones());
            if (_closed)
            {
                return;
            }

            AudioDevices.SetMicrophones(microphones);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Microphone enumeration failed: {ex}");
            if (_closed)
            {
                return;
            }

            AudioDevices.SetMicrophones(Array.Empty<AudioInputDevice>());
        }
        finally
        {
            if (!_closed)
            {
                AudioDevices.SetMicrophoneLoading(false);
            }
        }
    }

    private async Task LoadWebcamsAsync()
    {
        WebcamOptions.SetWebcamsLoading(true);
        try
        {
            var webcams = await _webcamDevices.GetWebcamDevicesAsync();
            if (_closed)
            {
                return;
            }

            WebcamOptions.SetWebcams(webcams);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Webcam enumeration failed: {ex}");
            if (_closed)
            {
                return;
            }

            WebcamOptions.SetWebcams(Array.Empty<WebcamDeviceInfo>());
        }
        finally
        {
            if (!_closed)
            {
                WebcamOptions.SetWebcamsLoading(false);
            }
        }
    }

    private void ShowNear(MonitorInfo? monitor, PixelRect? regionInVirtualDesktop)
    {
        PositionNearMonitorWorkArea(monitor, regionInVirtualDesktop);
        Activate();
        RootGrid.Focus(FocusState.Programmatic);

        var hwnd = WindowNative.GetWindowHandle(this);
        OverlayWindowHelpers.ExcludeFromCapture(hwnd);
    }

    private void PositionNearMonitorWorkArea(MonitorInfo? monitor, PixelRect? regionInVirtualDesktop)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var target = AppWindowPlacement.PrepareForTargetMonitor(AppWindow, hwnd, monitor);
        var work = target.WorkArea;
        var scale = target.Scale;
        RootGrid.UpdateLayout();
        RootGrid.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

        var width = (int)Math.Ceiling(RootGrid.DesiredSize.Width * scale) + 2;
        var height = (int)Math.Ceiling(RootGrid.DesiredSize.Height * scale) + 2;
        var topOffset = AppWindowPlacement.DipToPixels(TopOffsetDip, scale);
        var regionOutsideOffset = AppWindowPlacement.DipToPixels(RegionOutsideOffsetDip, scale);

        var x = work.X + Math.Max(0, (work.Width - width) / 2);
        var y = work.Y + topOffset;

        if (regionInVirtualDesktop is { Width: > 0, Height: > 0 } region)
        {
            x = region.X + Math.Max(0, (region.Width - width) / 2);
            var preferredAbove = region.Y - height - regionOutsideOffset;
            var preferredBelow = region.Y + region.Height + regionOutsideOffset;
            if (preferredAbove >= work.Y)
            {
                y = preferredAbove;
            }
            else if (preferredBelow <= work.Y + Math.Max(0, work.Height - height))
            {
                y = preferredBelow;
            }
            else
            {
                y = region.Y + topOffset;
            }
        }

        AppWindow.MoveAndResize(AppWindowPlacement.ClampToWorkArea(work, x, y, width, height));
    }

    private void ConfigurePresenter()
    {
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
    }

    private async void OnMicrophoneToggleRequested(object? sender, EventArgs e)
    {
        if (_microphonePermissionPending)
        {
            return;
        }

        _microphonePermissionPending = true;
        AudioDevices.IsMicrophoneToggleEnabled = false;
        UpdateStartButtonEnabled();
        try
        {
            var allowed = await _mediaPermissions.RequestMicrophoneAccessAsync();
            if (!_closed)
            {
                AudioDevices.SetMicrophoneAllowed(allowed);
            }
        }
        finally
        {
            _microphonePermissionPending = false;
            if (!_closed)
            {
                AudioDevices.IsMicrophoneToggleEnabled = !_webcamPermissionPending;
                UpdateStartButtonEnabled();
            }
        }
    }

    private void OnWebcamToggleRequested(object? sender, EventArgs e) => _ = HandleWebcamToggleRequestAsync();

    private async Task HandleWebcamToggleRequestAsync()
    {
        if (_webcamPermissionPending)
        {
            return;
        }

        _webcamPermissionPending = true;
        WebcamOptions.IsWebcamToggleEnabled = false;
        AudioDevices.IsMicrophoneToggleEnabled = false;
        UpdateStartButtonEnabled();
        try
        {
            var isCameraAllowed = await _mediaPermissions.RequestCameraAccessAsync();
            if (_closed)
            {
                return;
            }

            var isMicrophoneAllowed = await _mediaPermissions.RequestMicrophoneAccessAsync();
            if (_closed)
            {
                return;
            }

            WebcamOptions.SetWebcamAllowed(isCameraAllowed);
            AudioDevices.SetMicrophoneAllowed(isMicrophoneAllowed);
        }
        finally
        {
            _webcamPermissionPending = false;
            if (!_closed)
            {
                WebcamOptions.IsWebcamToggleEnabled = true;
                AudioDevices.IsMicrophoneToggleEnabled = !_microphonePermissionPending;
                UpdateStartButtonEnabled();
            }
        }
    }

    private void OnMouseClicksToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        _showMouseClicks = MouseClicksToggle.IsChecked == true;
        UpdateMouseClicksVisual();
    }

    private void OnSelectionReadinessChanged(object? sender, EventArgs e) => UpdateStartButtonEnabled();

    private void OnPreviewSourceChanged(object? sender, EventArgs e) => _ = RefreshSetupPreviewAsync();

    private async Task RefreshSetupPreviewAsync()
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _previewCts, cancellation);
        previous.Cancel();
        previous.Dispose();

        await _previewGate.WaitAsync();
        try
        {
            if (_previewCapture.IsRunning)
            {
                await _previewCapture.StopAsync();
            }

            if (_closed || cancellation.IsCancellationRequested ||
                !WebcamOptions.WebcamEnabled ||
                !WebcamOptions.IsWebcamSelectionReady)
            {
                if (!_closed)
                {
                    WebcamOptions.HidePreview();
                    ResizeToContent();
                }
                return;
            }

            // Avoid starting MediaCapture after a setup panel that was immediately dismissed.
            await Task.Delay(150, cancellation.Token);
            await _previewCapture.StartAsync(
                WebcamOptions.SelectedWebcamId,
                ResolvePreviewSize(WebcamOptions.WebcamSizePreset),
                cancellation.Token);

            if (_closed || cancellation.IsCancellationRequested)
            {
                await _previewCapture.StopAsync();
                return;
            }

            WebcamOptions.ShowPreview(_previewCapture);
            ResizeToContent();
        }
        catch (OperationCanceledException)
        {
            // A newer selection or window dismissal superseded this start.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Webcam setup preview failed: {ex}");
            if (!_closed)
            {
                WebcamOptions.HidePreview();
                ResizeToContent();
            }
        }
        finally
        {
            _previewGate.Release();
        }
    }

    private static BitmapSize ResolvePreviewSize(WebcamSizePreset preset) => preset switch
    {
        WebcamSizePreset.Small => new BitmapSize { Width = 640, Height = 360 },
        WebcamSizePreset.Large => new BitmapSize { Width = 1280, Height = 720 },
        _ => new BitmapSize { Width = 960, Height = 540 },
    };

    private void ResizeToContent()
    {
        RootGrid.UpdateLayout();
        RootGrid.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = AppWindowPlacement.GetScaleForWindow(hwnd);
        AppWindow.Resize(new SizeInt32(
            (int)Math.Ceiling(RootGrid.DesiredSize.Width * scale) + 2,
            (int)Math.Ceiling(RootGrid.DesiredSize.Height * scale) + 2));
    }

    /// <summary>
    /// GIF setup never gates on device readiness (its device controls are hidden and forced off in
    /// <see cref="OnStart"/>). For video, Start stays disabled only while an *enabled* audio or
    /// webcam source's selection is still unresolved/loading — a disabled source never blocks
    /// Start.
    /// </summary>
    private bool IsReadyToStart =>
        _captureType == CaptureType.Gif
        || (!_microphonePermissionPending
            && !_webcamPermissionPending
            && AudioDevices.IsMicrophoneSelectionReady
            && WebcamOptions.IsWebcamSelectionReady);

    private void UpdateStartButtonEnabled()
    {
        var isReady = IsReadyToStart;
        StartButton.IsEnabled = isReady;

        if (isReady)
        {
            ToolTipService.SetToolTip(StartButton, "Start recording (Enter)");
            AutomationProperties.SetName(StartButton, "Start recording");
        }
        else
        {
            const string reason = "Start recording (waiting for microphone/webcam device setup to finish)";
            ToolTipService.SetToolTip(StartButton, reason);
            AutomationProperties.SetName(StartButton, reason);
        }
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        // Defensive: keeps Enter (see OnKeyDown) from bypassing a disabled Start button if
        // readiness changed in the same tick a key/click was already in flight.
        if (!IsReadyToStart)
        {
            return;
        }

        Complete(new RecordingSetupResult(
            _captureType == CaptureType.Video && AudioDevices.RecordSystemAudio,
            _captureType == CaptureType.Video && AudioDevices.RecordMicrophone,
            AudioDevices.SelectedMicrophoneId,
            _captureType == CaptureType.Video && WebcamOptions.WebcamEnabled,
            WebcamOptions.SelectedWebcamId,
            WebcamOptions.WebcamShape,
            WebcamOptions.WebcamSizePreset,
            WebcamOptions.WebcamCornerPosition,
            WebcamOptions.WebcamCornerRadiusOrNull,
            _showMouseClicks));
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Complete(null);

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                OnStart(sender, e);
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                Complete(null);
                e.Handled = true;
                break;
        }
    }

    private void Complete(RecordingSetupResult? result)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _pendingResult = result;
        ClosePanel();
    }

    private void ClosePanel()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        AudioDevices.MicrophoneToggleRequested -= OnMicrophoneToggleRequested;
        WebcamOptions.WebcamToggleRequested -= OnWebcamToggleRequested;
        WebcamOptions.PreviewSourceChanged -= OnPreviewSourceChanged;
        AudioDevices.ReadinessChanged -= OnSelectionReadinessChanged;
        WebcamOptions.ReadinessChanged -= OnSelectionReadinessChanged;
        _previewCts.Cancel();
        WebcamOptions.HidePreview();
        if (!_completed)
        {
            _completed = true;
        }

        _ = CompleteAfterPreviewCleanupAsync();
    }

    private async Task CompleteAfterPreviewCleanupAsync()
    {
        try
        {
            await _previewGate.WaitAsync();
            try
            {
                await _previewCapture.DisposeAsync();
            }
            finally
            {
                _previewGate.Release();
            }

            _previewCts.Dispose();
            _result.TrySetResult(_pendingResult);
        }
        catch (Exception ex)
        {
            _result.TrySetException(ex);
        }
    }

    private void UpdateMouseClicksVisual()
    {
        _suppressEvents = true;
        try
        {
            MouseClicksToggle.IsChecked = _showMouseClicks;
        }
        finally
        {
            _suppressEvents = false;
        }

        var state = _showMouseClicks ? "On" : "Off";
        ToolTipService.SetToolTip(MouseClicksToggle, $"Mouse click visuals: {state}");
        AutomationProperties.SetName(MouseClicksToggle, $"Mouse click visuals {state}");
    }

    // Drag-anywhere support: interactive controls mark pointer events handled; dragging
    // only begins on the setup panel background.
    // Delegates to FloatingWindowDragger which anchors movement to absolute cursor position.
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e) => _dragger.OnPointerPressed(sender, e);

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e) => _dragger.OnPointerMoved(sender, e);

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) => _dragger.OnPointerReleased(sender, e);

    private void OnPointerCaptureEnded(object sender, PointerRoutedEventArgs e) => _dragger.OnPointerCaptureEnded(sender, e);
}
