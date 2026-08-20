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
using Windows.Graphics.Imaging;
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
    private readonly Task<CapturedFrame?> _backdropTask;
    private readonly Action<RegionSelectResult?> _onComplete;
    private readonly nint _hwnd;
    private CapturedFrame? _backdropFrame;
    private Point _start;
    private bool _dragging;
    private bool _completed;
    private bool _closedByController;
    private bool _rootLoaded;
    private bool _backdropReady;
    private bool _revealed;

    internal RegionSelectWindow(MonitorInfo monitor, Task<CapturedFrame?> backdropTask, Action<RegionSelectResult?> onComplete)
    {
        _monitor = monitor;
        _backdropTask = backdropTask;
        _onComplete = onComplete;

        InitializeComponent();

        ConfigurePresenter();
        _hwnd = WindowNative.GetWindowHandle(this);
        SetWindowAlpha(_hwnd, 0);
        // Keep the overlay itself out of any capture that runs while it is (or was just) visible.
        OverlayWindowHelpers.ExcludeFromCapture(_hwnd);
        AppWindow.Move(new PointInt32(monitor.X, monitor.Y));
        AppWindow.Resize(new SizeInt32(monitor.Width, monitor.Height));

        RootGrid.Loaded += OnRootGridLoaded;
        Activated += OnActivated;
        Closed += OnClosed;
        _ = ShowBackdropWhenReadyAsync();
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

    private void OnRootGridLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnRootGridLoaded;
        _rootLoaded = true;
        TryReveal();
    }

    /// <summary>
    /// Paints the pre-captured monitor snapshot behind the dim overlay so the user sees a true
    /// view of the screen, with only the area outside the selection darkened. The pixel upload
    /// happens off the UI thread; only the (async) source assignment runs here.
    /// </summary>
    private async Task ShowBackdropWhenReadyAsync()
    {
        try
        {
            var frame = await _backdropTask;
            if (_completed || _closedByController)
            {
                return;
            }

            _backdropFrame = frame;
            if (frame is null)
            {
                _backdropReady = true;
                TryReveal();
                return;
            }

            CaptureFlowTrace.Mark($"region: backdrop frame available ({frame.Width}x{frame.Height})");
            var softwareBitmap = await Task.Run(() => SoftwareBitmap.CreateCopyFromBuffer(
                frame.BgraPixels.AsBuffer(),
                BitmapPixelFormat.Bgra8,
                frame.Width,
                frame.Height,
                BitmapAlphaMode.Premultiplied));
            if (_completed || _closedByController)
            {
                softwareBitmap.Dispose();
                return;
            }

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(softwareBitmap);
            softwareBitmap.Dispose();
            Backdrop.Source = source;
            CaptureFlowTrace.Mark("region: backdrop source set");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Region backdrop paint failed: {ex}");
        }

        _backdropReady = true;
        TryReveal();
    }

    private void TryReveal()
    {
        if (_revealed || !_rootLoaded || !_backdropReady)
        {
            return;
        }

        _revealed = true;
        // Let the backdrop source commit to the visual tree before lifting the alpha, otherwise
        // the first presented frame can be the bare dim without the screen snapshot behind it.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_completed || _closedByController)
            {
                return;
            }

            SetWindowAlpha(_hwnd, 255);
            CaptureFlowTrace.Mark("region: overlay revealed");
        });
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
            ? new RegionSelectResult(_monitor.HMonitor, selected, _backdropFrame)
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
