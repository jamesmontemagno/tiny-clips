using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace TinyClips.App;

/// <summary>
/// Small always-on-top teleprompter overlay shown during video recordings. The window is
/// excluded from screen capture, so its transcript never appears in the recorded video.
/// The panel is draggable; its last position is persisted in target-monitor-relative DIPs.
/// </summary>
public sealed partial class TeleprompterWindow : Window
{
    private const int WidthDip = 600;
    private const int TopOffsetDip = 24;

    /// <summary>Current panel height preset, in DIPs (Settings → Teleprompter → Panel height).</summary>
    private int HeightDip => (int)Math.Round(_settings.TeleprompterPanelHeight.PanelHeight());

    // The capture-exclusion style makes the window read back as fully transparent to XAML
    // hit-testing; a ~1% alpha background keeps the panel draggable without showing a fill.
    private static readonly SolidColorBrush SizingSurfaceBrush = new(Microsoft.UI.ColorHelper.FromArgb(1, 0, 0, 0));

    private static readonly TimeSpan ScrollInterval = TimeSpan.FromMilliseconds(16);

    private readonly ICaptureSettings _settings;
    private readonly IMonitorService _monitorService;
    private readonly double _scrollSpeedDipPerSecond;
    private readonly DispatcherTimer _scrollTimer;

    private bool _closed;
    private bool _scrollingPaused;

    private bool _dragging;
    private POINT _dragCursorStart;
    private PointInt32 _dragWindowStart;

    public TeleprompterWindow(
        ICaptureSettings settings,
        IMonitorService monitorService,
        MonitorInfo? monitor)
    {
        InitializeComponent();

        _settings = settings;
        _monitorService = monitorService;
        TranscriptText.Text = settings.TeleprompterTranscript;
        TranscriptText.FontSize = settings.TeleprompterFontSize.FontSize();
        _scrollSpeedDipPerSecond = Math.Max(1.0, settings.TeleprompterScrollSpeed);

        RootGrid.Background = SizingSurfaceBrush;
        SizeChanged += OnSizeChanged;

        _scrollTimer = new DispatcherTimer { Interval = ScrollInterval };
        _scrollTimer.Tick += OnScrollTick;

        ConfigurePresenter();
        PositionWindow(monitor);
        Closed += OnClosed;
    }

    public bool TryShow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (!OverlayWindowHelpers.ExcludeFromCapture(hwnd))
        {
            var error = Marshal.GetLastWin32Error();
            Debug.WriteLine($"Teleprompter capture exclusion failed (Win32 error {error}); overlay will remain hidden.");
            ClosePanel();
            return false;
        }

        AppWindow.Show(false);
        return true;
    }

    public void PauseScrolling()
    {
        _scrollingPaused = true;
        _scrollTimer.Stop();
    }

    public void ResumeScrolling()
    {
        _scrollingPaused = false;
        StartScrollingIfPossible();
    }

    public void ClosePanel()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _scrollTimer.Stop();
        Close();
    }

    /// <summary>
    /// Re-reads the text-size and panel-height presets from settings and applies them to the live
    /// overlay, keeping the panel's top-left position and the current scroll offset so a change
    /// made mid-recording is reflected without restarting.
    /// </summary>
    public void ApplyDisplaySettings()
    {
        if (_closed)
        {
            return;
        }

        TranscriptText.FontSize = _settings.TeleprompterFontSize.FontSize();

        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = AppWindowPlacement.GetScaleForWindow(hwnd);
        var height = AppWindowPlacement.DipToPixels(HeightDip, scale);
        if (AppWindow.Size.Height != height)
        {
            AppWindow.Resize(new SizeInt32(AppWindow.Size.Width, height));
        }

        // ExtentHeight/ViewportHeight still reflect the previous font and window size until layout
        // has run, so defer the clamp/restart to a low-priority pass after the resize and
        // re-measure have been processed. Otherwise a font-only change that turns fitting text
        // into overflowing text would be evaluated against the stale extent and never start.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_closed)
            {
                return;
            }

            var maxOffset = Math.Max(0, Scroller.ExtentHeight - Scroller.ViewportHeight);
            if (Scroller.VerticalOffset > maxOffset)
            {
                Scroller.ChangeView(null, maxOffset, null, disableAnimation: true);
            }

            StartScrollingIfPossible();
        });
    }

    private void OnSizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        if (_scrollTimer.IsEnabled || _scrollingPaused || Scroller.ExtentHeight <= 0)
        {
            return;
        }

        // Text starts at the top edge (offset 0) and scrolls down from there.
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        StartScrollingIfPossible();
    }

    private void OnScrollTick(object? sender, object e)
    {
        var maxOffset = Scroller.ExtentHeight - Scroller.ViewportHeight;
        if (Scroller.VerticalOffset >= maxOffset)
        {
            _scrollTimer.Stop();
            return;
        }

        var next = Scroller.VerticalOffset +
            (_scrollSpeedDipPerSecond * ScrollInterval.TotalSeconds);
        Scroller.ChangeView(null, Math.Min(next, maxOffset), null, disableAnimation: true);
    }

    private void StartScrollingIfPossible()
    {
        if (!_closed &&
            !_scrollingPaused &&
            Scroller.ExtentHeight - Scroller.ViewportHeight > Scroller.VerticalOffset)
        {
            _scrollTimer.Start();
        }
    }

    // Drag-anywhere support, anchored to the absolute cursor position to avoid feedback
    // jitter as the window moves. Same pattern as RecordingIndicatorWindow.
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
        PersistPosition();
    }

    private void PersistPosition()
    {
        var monitors = _monitorService.GetMonitors();
        var monitor = FindMonitorForWindow(monitors, AppWindow.Position, AppWindow.Size);
        if (monitor is null)
        {
            return;
        }

        var position = TeleprompterPlacement.ToMonitorRelativeDips(
            monitor,
            AppWindow.Position.X,
            AppWindow.Position.Y);
        _settings.TeleprompterPosX = position.X;
        _settings.TeleprompterPosY = position.Y;
        _settings.TeleprompterMonitorDeviceName = monitor.DeviceName;
    }

    private void ConfigurePresenter()
    {
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);

        AppWindow.IsShownInSwitchers = false;
    }

    private void PositionWindow(MonitorInfo? monitor)
    {
        var monitors = _monitorService.GetMonitors();
        var savedXDip = _settings.TeleprompterPosX;
        var savedYDip = _settings.TeleprompterPosY;
        var savedMonitorName = _settings.TeleprompterMonitorDeviceName;
        var placementMonitor = monitors.FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(savedMonitorName) &&
                string.Equals(candidate.DeviceName, savedMonitorName, StringComparison.OrdinalIgnoreCase))
            ?? monitors.FirstOrDefault(candidate =>
                monitor is not null &&
                string.Equals(candidate.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
            ?? monitors.FirstOrDefault(candidate => candidate.IsPrimary)
            ?? monitors.FirstOrDefault()
            ?? monitor;

        if (placementMonitor is null)
        {
            var work = DisplayArea.Primary?.WorkArea ?? new RectInt32(0, 0, 1920, 1080);
            var hwnd = WindowNative.GetWindowHandle(this);
            var target = AppWindowPlacement.PrepareForTargetWorkArea(AppWindow, hwnd, work);
            var width = Math.Min(AppWindowPlacement.DipToPixels(WidthDip, target.Scale), work.Width);
            var height = Math.Min(AppWindowPlacement.DipToPixels(HeightDip, target.Scale), work.Height);
            var topOffset = AppWindowPlacement.DipToPixels(TopOffsetDip, target.Scale);
            AppWindow.MoveAndResize(AppWindowPlacement.ClampToWorkArea(
                work,
                work.X + Math.Max(0, (work.Width - width) / 2),
                work.Y + topOffset,
                width,
                height));
            return;
        }

        var placement = TeleprompterPlacement.Calculate(
            placementMonitor,
            WidthDip,
            HeightDip,
            TopOffsetDip,
            savedXDip,
            savedYDip,
            savedPositionIsMonitorRelative: !string.IsNullOrWhiteSpace(savedMonitorName));
        // Move the hidden window onto the target monitor before sizing so WinUI processes
        // the DPI transition before we apply the final physical-pixel dimensions.
        AppWindow.Move(new PointInt32(placementMonitor.WorkAreaX, placementMonitor.WorkAreaY));
        AppWindow.Resize(new SizeInt32(placement.Width, placement.Height));
        AppWindow.Move(new PointInt32(placement.X, placement.Y));
    }

    private static MonitorInfo? FindMonitorForWindow(
        IReadOnlyList<MonitorInfo> monitors,
        PointInt32 position,
        SizeInt32 size)
    {
        var centerX = position.X + (size.Width / 2);
        var centerY = position.Y + (size.Height / 2);
        return monitors.FirstOrDefault(monitor =>
                centerX >= monitor.X &&
                centerX < monitor.X + monitor.Width &&
                centerY >= monitor.Y &&
                centerY < monitor.Y + monitor.Height)
            ?? monitors.FirstOrDefault(monitor => monitor.IsPrimary)
            ?? monitors.FirstOrDefault();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _scrollTimer.Stop();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
