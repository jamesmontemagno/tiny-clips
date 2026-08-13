using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using TinyClips.Core.Capture;
using WinRT.Interop;

namespace TinyClips.App;

/// <summary>
/// A full-screen, borderless overlay that lets the user rubber-band a rectangle on one
/// monitor and reports a monitor-relative region in physical pixels.
/// </summary>
public sealed partial class RegionSelectWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;

    private readonly MonitorInfo _monitor;
    private readonly CapturedFrame? _backdropFrame;
    private readonly Action<RegionSelectResult?> _onComplete;
    private readonly nint _hwnd;
    private Point _start;
    private bool _dragging;
    private bool _completed;
    private bool _closedByController;
    private bool _rootLoaded;
    private bool _backdropReady;
    private bool _revealQueued;
    private bool _revealed;

    internal RegionSelectWindow(MonitorInfo monitor, CapturedFrame? backdropFrame, Action<RegionSelectResult?> onComplete)
    {
        _monitor = monitor;
        _backdropFrame = backdropFrame;
        _onComplete = onComplete;

        InitializeComponent();

        ConfigurePresenter();
        _hwnd = WindowNative.GetWindowHandle(this);
        SetWindowAlpha(_hwnd, 0);
        AppWindow.Move(new PointInt32(monitor.X, monitor.Y));
        AppWindow.Resize(new SizeInt32(monitor.Width, monitor.Height));

        if (_backdropFrame is not null)
        {
            Backdrop.ImageOpened += OnBackdropReady;
            Backdrop.ImageFailed += OnBackdropReady;
        }

        ShowBackdrop();
        RootGrid.Loaded += OnRootGridLoaded;
        Activated += OnActivated;
        Closed += OnClosed;
    }

    /// <summary>Shows the overlay on the given monitor and resolves with the chosen region.</summary>
    public static async Task<PixelRect?> RunAsync(MonitorInfo monitor)
    {
        var result = await RegionSelectController.RunAsync(new[] { monitor });
        return result?.Region;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnActivated;
        RootGrid.Focus(FocusState.Programmatic);
    }

    private async void OnRootGridLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnRootGridLoaded;
        _rootLoaded = true;
        TryQueueReveal();

        await Task.Delay(250);
        _backdropReady = true;
        TryQueueReveal();
    }

    private void OnBackdropReady(object sender, RoutedEventArgs e)
    {
        Backdrop.ImageOpened -= OnBackdropReady;
        Backdrop.ImageFailed -= OnBackdropReady;
        _backdropReady = true;
        TryQueueReveal();
    }

    private async void TryQueueReveal()
    {
        if (_revealQueued || !_rootLoaded || !_backdropReady)
        {
            return;
        }

        _revealQueued = true;
        await Task.Delay(50);
        RevealWindow();
    }

    private void RevealWindow()
    {
        if (_revealed || _completed || _closedByController)
        {
            return;
        }

        _revealed = true;
        SetWindowAlpha(_hwnd, 255);
    }

    /// <summary>
    /// Paints the pre-captured monitor snapshot behind the dim overlay so the user sees a true
    /// view of the screen, with only the area outside the selection darkened.
    /// </summary>
    private void ShowBackdrop()
    {
        if (_backdropFrame is null)
        {
            _backdropReady = true;
            return;
        }

        try
        {
            var bitmap = new WriteableBitmap(_backdropFrame.Width, _backdropFrame.Height);
            using (var stream = bitmap.PixelBuffer.AsStream())
            {
                stream.Write(_backdropFrame.BgraPixels, 0, _backdropFrame.BgraPixels.Length);
            }

            bitmap.Invalidate();
            Backdrop.Source = bitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Region backdrop paint failed: {ex}");
        }
    }

    private void OnOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        FullDim.Width = e.NewSize.Width;
        FullDim.Height = e.NewSize.Height;
    }

    private void ConfigurePresenter()
    {
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        AppWindow.IsShownInSwitchers = false;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _start = e.GetCurrentPoint(OverlayCanvas).Position;
        _dragging = true;
        Canvas.SetLeft(SelectionRect, _start.X);
        Canvas.SetTop(SelectionRect, _start.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        SelectionRect.Visibility = Visibility.Visible;

        // Swap the uniform dim for the hole-punch panels so the selection stays clear.
        FullDim.Visibility = Visibility.Collapsed;
        TopDim.Visibility = Visibility.Visible;
        BottomDim.Visibility = Visibility.Visible;
        LeftDim.Visibility = Visibility.Visible;
        RightDim.Visibility = Visibility.Visible;

        RootGrid.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var current = e.GetCurrentPoint(OverlayCanvas).Position;
        var x = Math.Min(_start.X, current.X);
        var y = Math.Min(_start.Y, current.Y);
        var width = Math.Abs(current.X - _start.X);
        var height = Math.Abs(current.Y - _start.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = width;
        SelectionRect.Height = height;

        UpdateDimPanels(x, y, width, height);
    }

    /// <summary>
    /// Positions the four dim panels so that everything except the selection rectangle is
    /// darkened, giving a clear, un-dimmed view of the area being captured.
    /// </summary>
    private void UpdateDimPanels(double x, double y, double width, double height)
    {
        var w = OverlayCanvas.ActualWidth;
        var h = OverlayCanvas.ActualHeight;

        Canvas.SetLeft(TopDim, 0);
        Canvas.SetTop(TopDim, 0);
        TopDim.Width = w;
        TopDim.Height = Math.Max(0, y);

        Canvas.SetLeft(BottomDim, 0);
        Canvas.SetTop(BottomDim, y + height);
        BottomDim.Width = w;
        BottomDim.Height = Math.Max(0, h - (y + height));

        Canvas.SetLeft(LeftDim, 0);
        Canvas.SetTop(LeftDim, y);
        LeftDim.Width = Math.Max(0, x);
        LeftDim.Height = height;

        Canvas.SetLeft(RightDim, x + width);
        Canvas.SetTop(RightDim, y);
        RightDim.Width = Math.Max(0, w - (x + width));
        RightDim.Height = height;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        RootGrid.ReleasePointerCapture(e.Pointer);

        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        var x = Canvas.GetLeft(SelectionRect);
        var y = Canvas.GetTop(SelectionRect);
        var width = SelectionRect.Width;
        var height = SelectionRect.Height;

        if (width < 2 || height < 2)
        {
            Complete(null);
            return;
        }

        var region = new PixelRect(
            (int)Math.Round(x * scale),
            (int)Math.Round(y * scale),
            (int)Math.Round(width * scale),
            (int)Math.Round(height * scale));

        Complete(region);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Complete(null);
        }
    }

    private void Complete(PixelRect? region)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _dragging = false;
        RootGrid.ReleasePointerCaptures();
        _onComplete(region is { } selected
            ? new RegionSelectResult(_monitor.HMonitor, selected)
            : null);
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_closedByController || _completed)
        {
            return;
        }

        _completed = true;
        _dragging = false;
        _onComplete(null);
    }

    internal void CloseFromController()
    {
        if (_closedByController)
        {
            return;
        }

        _closedByController = true;
        Close();
    }

    private static void SetWindowAlpha(nint hwnd, byte alpha)
    {
        var exStyle = (long)GetWindowLongPtr(hwnd);
        SetWindowLongPtr(hwnd, (nint)(exStyle | WsExLayered));
        SetLayeredWindowAttributes(hwnd, 0, alpha, LwaAlpha);
    }

    // 32/64-bit-safe GetWindowLongPtr / SetWindowLongPtr wrappers.
    private static nint GetWindowLongPtr(nint hwnd) =>
        nint.Size == 8 ? GetWindowLongPtr64(hwnd, GwlExStyle) : GetWindowLong32(hwnd, GwlExStyle);

    private static nint SetWindowLongPtr(nint hwnd, nint value) =>
        nint.Size == 8 ? SetWindowLongPtr64(hwnd, GwlExStyle, value) : SetWindowLong32(hwnd, GwlExStyle, (int)value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(nint hwnd, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);
}
