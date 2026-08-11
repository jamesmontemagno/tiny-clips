using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TinyClips.Core.Capture;
using TinyClips.Core.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace TinyClips.App;

/// <summary>
/// Small always-on-top teleprompter overlay shown during video recordings. The window is
/// excluded from screen capture, so its transcript never appears in the recorded video.
/// The panel is draggable; its last position is persisted in device-independent pixels.
/// </summary>
public sealed partial class TeleprompterWindow : Window
{
    private const int WidthDip = 600;
    private const int HeightDip = 140;
    private const int TopOffsetDip = 24;

    private const uint WdaExcludeFromCapture = 0x11;

    // The capture-exclusion style makes the window read back as fully transparent to XAML
    // hit-testing; a ~1% alpha background keeps the panel draggable without showing a fill.
    private static readonly SolidColorBrush SizingSurfaceBrush = new(Microsoft.UI.ColorHelper.FromArgb(1, 0, 0, 0));

    private static readonly TimeSpan ScrollInterval = TimeSpan.FromMilliseconds(16);

    private readonly ICaptureSettings _settings;
    private readonly double _scrollSpeedDipPerSecond;
    private readonly Microsoft.UI.Dispatching.DispatcherTimer _scrollTimer;

    private bool _closed;

    private bool _dragging;
    private POINT _dragCursorStart;
    private PointInt32 _dragWindowStart;

    public TeleprompterWindow(ICaptureSettings settings, MonitorInfo? monitor)
    {
        InitializeComponent();

        _settings = settings;
        TranscriptText.Text = settings.TeleprompterTranscript;
        _scrollSpeedDipPerSecond = Math.Max(1.0, settings.TeleprompterScrollSpeed);

        RootGrid.Background = SizingSurfaceBrush;
        SizeChanged += OnSizeChanged;

        _scrollTimer = new Microsoft.UI.Dispatching.DispatcherTimer { Interval = ScrollInterval };
        _scrollTimer.Tick += OnScrollTick;

        ConfigurePresenter();
        PositionWindow(monitor);
        Closed += OnClosed;
    }

    public void Show()
    {
        AppWindow.Show(false);

        // Exclude the overlay from screen capture so it never appears in the recorded video.
        var hwnd = WindowNative.GetWindowHandle(this);
        SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
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

    private void OnSizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        if (_scrollTimer.IsEnabled || Scroller.ExtentHeight <= 0)
        {
            return;
        }

        // Text starts at the top edge (offset 0) and scrolls down from there.
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        _scrollTimer.Start();
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
            (_scrollSpeedDipPerSecond * ScrollInterval.TotalSeconds * GetScale());
        Scroller.ChangeView(null, Math.Min(next, maxOffset), null, disableAnimation: true);
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
        // Store the position in DIPs (window positions are physical pixels).
        var scale = GetScale();
        _settings.TeleprompterPosX = AppWindow.Position.X / scale;
        _settings.TeleprompterPosY = AppWindow.Position.Y / scale;
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
        var scale = GetScale();
        var width = (int)Math.Round(WidthDip * scale);
        var height = (int)Math.Round(HeightDip * scale);

        AppWindow.Resize(new SizeInt32(width, height));

        // Saved positions are stored in DIPs; monitor work areas are physical pixels.
        var savedXDip = _settings.TeleprompterPosX;
        var savedYDip = _settings.TeleprompterPosY;
        if (savedXDip >= 0 && savedYDip >= 0)
        {
            AppWindow.Move(new PointInt32(
                (int)Math.Round(savedXDip * scale),
                (int)Math.Round(savedYDip * scale)));
            return;
        }

        // Default: top-center of the target monitor's work area.
        var work = GetWorkArea(monitor);
        var x = work.X + Math.Max(0, (work.Width - width) / 2);
        var y = work.Y + (int)Math.Round(TopOffsetDip * scale);
        AppWindow.Move(new PointInt32(x, y));
    }

    private static RectInt32 GetWorkArea(MonitorInfo? monitor)
    {
        if (monitor is { WorkAreaWidth: > 0, WorkAreaHeight: > 0 })
        {
            return new RectInt32(monitor.WorkAreaX, monitor.WorkAreaY, monitor.WorkAreaWidth, monitor.WorkAreaHeight);
        }

        return DisplayArea.Primary?.WorkArea ?? new RectInt32(0, 0, 1920, 1080);
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
        _scrollTimer.Stop();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);
}
