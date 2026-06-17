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
    private const int WidthDip = 320;
    private const int HeightDip = 64;
    private const int TopOffsetDip = 24;
    private const int RegionOutsideOffsetDip = 12;

    private const uint WdaExcludeFromCapture = 0x11;

    private const string MicGlyph = "\uE720";
    private const string SystemAudioOnGlyph = "\uE767";
    private const string SystemAudioOffGlyph = "\uE74F";

    private bool _stopRequested;
    private bool _closed;
    private bool _suppressAudioEvents;

    private bool _dragging;
    private POINT _dragCursorStart;
    private PointInt32 _dragWindowStart;

    public RecordingIndicatorWindow(string stopHint, bool showAudioControls, bool micOn, bool systemAudioOn)
    {
        InitializeComponent();

        HotKeyText.Text = string.IsNullOrWhiteSpace(stopHint)
            ? "Stop from tray"
            : $"Stop: {stopHint}";

        if (showAudioControls)
        {
            _suppressAudioEvents = true;
            MicToggle.IsChecked = micOn;
            SystemAudioToggle.IsChecked = systemAudioOn;
            _suppressAudioEvents = false;
            UpdateMicVisual(micOn);
            UpdateSystemAudioVisual(systemAudioOn);
        }
        else
        {
            MicToggle.Visibility = Visibility.Collapsed;
            SystemAudioToggle.Visibility = Visibility.Collapsed;
        }

        ConfigurePresenter();
        Closed += OnClosed;
    }

    public Action? StopRequested { get; set; }

    /// <summary>Raised when the microphone toggle changes; persists the default for the next recording.</summary>
    public Action<bool>? MicrophoneToggled { get; set; }

    /// <summary>Raised when the system-audio toggle changes; persists the default for the next recording.</summary>
    public Action<bool>? SystemAudioToggled { get; set; }

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
        StopButton.IsEnabled = enabled && !_stopRequested;
    }

    public void ClosePanel()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        StopRequested = null;
        Close();
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (_stopRequested)
        {
            return;
        }

        _stopRequested = true;
        StopButton.IsEnabled = false;

        var callback = StopRequested;
        StopRequested = null;
        callback?.Invoke();
    }

    private void OnMicToggled(object sender, RoutedEventArgs e)
    {
        var on = MicToggle.IsChecked == true;
        UpdateMicVisual(on);

        if (_suppressAudioEvents)
        {
            return;
        }

        MicrophoneToggled?.Invoke(on);
    }

    private void OnSystemAudioToggled(object sender, RoutedEventArgs e)
    {
        var on = SystemAudioToggle.IsChecked == true;
        UpdateSystemAudioVisual(on);

        if (_suppressAudioEvents)
        {
            return;
        }

        SystemAudioToggled?.Invoke(on);
    }

    private void UpdateMicVisual(bool on)
    {
        MicIcon.Glyph = MicGlyph;
        var state = on ? "On" : "Off";
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(MicToggle, $"Microphone: {state} (for the next recording)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MicToggle, $"Microphone {state}");
    }

    private void UpdateSystemAudioVisual(bool on)
    {
        SystemAudioIcon.Glyph = on ? SystemAudioOnGlyph : SystemAudioOffGlyph;
        var state = on ? "On" : "Off";
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(SystemAudioToggle, $"System audio: {state} (for the next recording)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SystemAudioToggle, $"System audio {state}");
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
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
        }

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
