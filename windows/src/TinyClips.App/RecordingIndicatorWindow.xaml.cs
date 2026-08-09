using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using TinyClips.Core.Capture;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace TinyClips.App;

/// <summary>
/// Small always-on-top panel shown while video or GIF recording is active.
/// </summary>
public sealed partial class RecordingIndicatorWindow : Window
{
    private const int WidthDip = 520;
    private const int HeightDip = 64;
    private const int TopOffsetDip = 24;
    private const int RegionOutsideOffsetDip = 12;

    private const uint WdaExcludeFromCapture = 0x11;

    private bool _finishRequested;
    private string _stopHintText = "Stop from tray";
    private bool _closed;

    private bool _dragging;
    private POINT _dragCursorStart;
    private PointInt32 _dragWindowStart;

    public RecordingIndicatorWindow(string stopHint)
    {
        InitializeComponent();

        _stopHintText = string.IsNullOrWhiteSpace(stopHint)
            ? "Stop from tray"
            : $"Stop: {stopHint}";
        HotKeyText.Text = _stopHintText;

        ConfigurePresenter();
        Closed += OnClosed;
    }

    public Action? StopRequested { get; set; }
    public Action? PauseRequested { get; set; }
    public Action? ResumeRequested { get; set; }
    public Action? RestartRequested { get; set; }
    public Action? DiscardRequested { get; set; }
    public Action<bool>? SystemAudioMuteChanged { get; set; }
    public Action<bool>? MicrophoneMuteChanged { get; set; }

    private bool _systemAudioMuted;
    private bool _microphoneMuted;

    public void ShowNear()
    {
        ShowNear(null, null);
    }

    public void ShowNear(MonitorInfo? monitor, PixelRect? regionInVirtualDesktop)
    {
        PositionNearMonitorWorkArea(monitor, regionInVirtualDesktop);
        AppWindow.Show(false);

        // Exclude the floating panel from screen capture so it never appears in the
        // recorded video/GIF.
        var hwnd = WindowNative.GetWindowHandle(this);
        SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
    }

    public void UpdateElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var totalMinutes = (int)Math.Min(99, elapsed.TotalMinutes);
        ElapsedText.Text = $"{totalMinutes:00}:{elapsed.Seconds:00}";
    }

    public void SetStopEnabled(bool enabled)
    {
        SetControlsEnabled(enabled && !_finishRequested);
    }

    public void SetPaused(bool paused)
    {
        PauseButton.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
        ResumeButton.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        HotKeyText.Text = paused ? "Paused" : _stopHintText;
    }

    public void ConfigureAudioControls(
        bool canMuteSystemAudio,
        bool systemAudioMuted,
        bool canMuteMicrophone,
        bool microphoneMuted)
    {
        SystemAudioButton.Visibility = canMuteSystemAudio ? Visibility.Visible : Visibility.Collapsed;
        MicrophoneButton.Visibility = canMuteMicrophone ? Visibility.Visible : Visibility.Collapsed;
        _systemAudioMuted = systemAudioMuted;
        _microphoneMuted = microphoneMuted;
        SystemAudioButton.IsChecked = systemAudioMuted;
        MicrophoneButton.IsChecked = microphoneMuted;
        UpdateAudioControlVisuals();
    }

    public void ClosePanel()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        StopRequested = null;
        PauseRequested = null;
        ResumeRequested = null;
        RestartRequested = null;
        DiscardRequested = null;
        SystemAudioMuteChanged = null;
        MicrophoneMuteChanged = null;
        Close();
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        PauseRequested?.Invoke();
    }

    private void OnResumeClick(object sender, RoutedEventArgs e)
    {
        ResumeRequested?.Invoke();
    }

    private void OnRestartClick(object sender, RoutedEventArgs e)
    {
        CompleteWith(RestartRequested);
    }

    private void OnSystemAudioClick(object sender, RoutedEventArgs e)
    {
        _systemAudioMuted = SystemAudioButton.IsChecked == true;
        SystemAudioMuteChanged?.Invoke(_systemAudioMuted);
        UpdateAudioControlVisuals();
    }

    private void OnMicrophoneClick(object sender, RoutedEventArgs e)
    {
        _microphoneMuted = MicrophoneButton.IsChecked == true;
        MicrophoneMuteChanged?.Invoke(_microphoneMuted);
        UpdateAudioControlVisuals();
    }

    private void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        CompleteWith(DiscardRequested);
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        CompleteWith(StopRequested);
    }

    private void CompleteWith(Action? callback)
    {
        if (_finishRequested)
        {
            return;
        }

        _finishRequested = true;
        SetControlsEnabled(false);
        StopRequested = null;
        PauseRequested = null;
        ResumeRequested = null;
        RestartRequested = null;
        DiscardRequested = null;
        SystemAudioMuteChanged = null;
        MicrophoneMuteChanged = null;
        callback?.Invoke();
    }

    private void SetControlsEnabled(bool enabled)
    {
        PauseButton.IsEnabled = enabled;
        ResumeButton.IsEnabled = enabled;
        RestartButton.IsEnabled = enabled;
        DiscardButton.IsEnabled = enabled;
        StopButton.IsEnabled = enabled;
        SystemAudioButton.IsEnabled = enabled;
        MicrophoneButton.IsEnabled = enabled;
    }

    private void UpdateAudioControlVisuals()
    {
        SystemAudioIcon.Glyph = _systemAudioMuted ? "\uE74F" : "\uE767";
        var systemAudioState = _systemAudioMuted ? "muted" : "recording";
        ToolTipService.SetToolTip(SystemAudioButton, $"System audio {systemAudioState}");
        AutomationProperties.SetName(SystemAudioButton, $"System audio {systemAudioState}");

        MicrophoneIcon.Glyph = _microphoneMuted ? "\uE74F" : "\uE720";
        var microphoneState = _microphoneMuted ? "muted" : "recording";
        ToolTipService.SetToolTip(MicrophoneButton, $"Microphone {microphoneState}");
        AutomationProperties.SetName(MicrophoneButton, $"Microphone {microphoneState}");
    }

    // Drag-anywhere support: pressing the Stop button is handled by the Button itself
    // (it marks the pointer event handled), so dragging only begins on the panel surface.
    // Anchored to absolute cursor position to avoid feedback jitter as the window moves.
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        GetCursorPos(out _dragCursorStart);
        _dragWindowStart = AppWindow.Position;
        _dragging = element.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        GetCursorPos(out var current);
        var dx = current.X - _dragCursorStart.X;
        var dy = current.Y - _dragCursorStart.Y;

        if (dx == 0 && dy == 0)
        {
            return;
        }

        AppWindow.Move(new PointInt32(_dragWindowStart.X + dx, _dragWindowStart.Y + dy));
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        _dragging = false;
        element.ReleasePointerCapture(e.Pointer);
    }

    private void ConfigurePresenter()
    {
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        AppWindow.IsShownInSwitchers = false;
    }

    private void PositionNearMonitorWorkArea(MonitorInfo? monitor, PixelRect? regionInVirtualDesktop)
    {
        var scale = GetScale();
        var width = (int)Math.Round(WidthDip * scale);
        var height = (int)Math.Round(HeightDip * scale);
        var topOffset = (int)Math.Round(TopOffsetDip * scale);
        var regionOutsideOffset = (int)Math.Round(RegionOutsideOffsetDip * scale);

        AppWindow.Resize(new SizeInt32(width, height));

        if (GetWorkArea(monitor) is { } work)
        {
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

            x = Math.Clamp(x, work.X, work.X + Math.Max(0, work.Width - width));
            y = Math.Clamp(y, work.Y, work.Y + Math.Max(0, work.Height - height));
            AppWindow.Move(new PointInt32(x, y));
        }
    }

    private static RectInt32? GetWorkArea(MonitorInfo? monitor)
    {
        if (monitor is { WorkAreaWidth: > 0, WorkAreaHeight: > 0 })
        {
            return new RectInt32(monitor.WorkAreaX, monitor.WorkAreaY, monitor.WorkAreaWidth, monitor.WorkAreaHeight);
        }

        return DisplayArea.Primary?.WorkArea;
    }

    private double GetScale()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        return dpi <= 0 ? 1.0 : dpi / 96.0;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        StopRequested = null;
        PauseRequested = null;
        ResumeRequested = null;
        RestartRequested = null;
        DiscardRequested = null;
        SystemAudioMuteChanged = null;
        MicrophoneMuteChanged = null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);
}
