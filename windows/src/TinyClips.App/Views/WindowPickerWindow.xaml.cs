using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
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
/// A full-screen, borderless overlay covering one monitor for the window picker. It shows a
/// frozen snapshot of the monitor, dims everything except the window under the cursor, and
/// labels that window. Hit-testing and selection are shared through <see cref="WindowPickerController"/>.
/// </summary>
public sealed partial class WindowPickerWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;
    private const double LabelMarginDip = 12;

    private readonly MonitorInfo _monitor;
    private readonly PixelRect _monitorBounds;
    private readonly Task<CapturedFrame?> _backdropTask;
    private readonly WindowPickerController _controller;
    private readonly nint _hwnd;
    private WindowPickerCandidate? _highlighted;
    private bool _showLabel;
    private bool _closedByController;
    private bool _rootLoaded;
    private bool _backdropReady;
    private bool _revealed;

    internal WindowPickerWindow(MonitorInfo monitor, Task<CapturedFrame?> backdropTask, WindowPickerController controller)
    {
        _monitor = monitor;
        _monitorBounds = new PixelRect(monitor.X, monitor.Y, monitor.Width, monitor.Height);
        _backdropTask = backdropTask;
        _controller = controller;

        InitializeComponent();
        WindowCaptureVisibility.Register(this);

        ConfigurePresenter();
        _hwnd = WindowNative.GetWindowHandle(this);
        SetWindowAlpha(_hwnd, 0);
        AppWindow.Move(new PointInt32(monitor.X, monitor.Y));
        AppWindow.Resize(new SizeInt32(monitor.Width, monitor.Height));

        if (!controller.HasSelectableWindows)
        {
            HintText.Text = "No windows to capture · Esc to cancel";
        }

        RootGrid.Loaded += OnRootGridLoaded;
        Activated += OnActivated;
        Closed += OnClosed;
        _ = ShowBackdropWhenReadyAsync();
    }

    /// <summary>
    /// Highlights <paramref name="candidate"/> (clipped to this monitor), or clears the highlight
    /// when null. Only one monitor shows the label so it is not duplicated across displays.
    /// </summary>
    internal void ShowHighlight(WindowPickerCandidate? candidate, bool showLabel)
    {
        _highlighted = candidate;
        _showLabel = showLabel;
        Render();
    }

    internal void Announce(string message)
    {
        AnnouncementAnchor.Text = message;
        var peer = FrameworkElementAutomationPeer.FromElement(AnnouncementAnchor)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(AnnouncementAnchor);
        peer?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.ImportantMostRecent,
            message,
            "TinyClipsWindowPicker");
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

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            RootGrid.Focus(FocusState.Programmatic);
        }
    }

    private void OnRootGridLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnRootGridLoaded;
        _rootLoaded = true;
        Render();
        TryReveal();
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) => Render();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (!_closedByController)
        {
            _closedByController = true;
            _controller.OnWindowClosed();
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var (x, y) = ToVirtualPixels(e.GetCurrentPoint(RootGrid).Position);
        _controller.HoverAt(x, y);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(RootGrid);
        e.Handled = true;
        if (point.Properties.IsRightButtonPressed)
        {
            _controller.Cancel();
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            var (x, y) = ToVirtualPixels(point.Position);
            _controller.SelectAt(x, y);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                _controller.Cancel();
                break;
            case VirtualKey.Tab:
                e.Handled = true;
                var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                _controller.Cycle(forward: !shift);
                break;
            case VirtualKey.Enter:
            case VirtualKey.Space:
                e.Handled = true;
                _controller.SelectHovered();
                break;
        }
    }

    private (int X, int Y) ToVirtualPixels(Point dip)
    {
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? _monitor.ScaleFactor;
        return (_monitor.X + (int)Math.Round(dip.X * scale), _monitor.Y + (int)Math.Round(dip.Y * scale));
    }

    private void Render()
    {
        if (!_rootLoaded)
        {
            return;
        }

        var width = RootGrid.ActualWidth;
        var height = RootGrid.ActualHeight;
        FullDim.Width = width;
        FullDim.Height = height;

        var local = _highlighted is { } candidate
            ? WindowPickerGeometry.ToMonitorLocal(candidate.Bounds, _monitorBounds)
            : null;
        if (local is not { } rect)
        {
            FullDim.Visibility = Visibility.Visible;
            TopDim.Visibility = BottomDim.Visibility = LeftDim.Visibility = RightDim.Visibility = Visibility.Collapsed;
            Highlight.Visibility = Visibility.Collapsed;
            HoverLabel.Visibility = Visibility.Collapsed;
            return;
        }

        var scale = RootGrid.XamlRoot?.RasterizationScale ?? _monitor.ScaleFactor;
        var x = rect.X / scale;
        var y = rect.Y / scale;
        var w = rect.Width / scale;
        var h = rect.Height / scale;

        FullDim.Visibility = Visibility.Collapsed;
        TopDim.Visibility = BottomDim.Visibility = LeftDim.Visibility = RightDim.Visibility = Visibility.Visible;
        Place(TopDim, 0, 0, width, y);
        Place(BottomDim, 0, y + h, width, height - (y + h));
        Place(LeftDim, 0, y, x, h);
        Place(RightDim, x + w, y, width - (x + w), h);

        Highlight.Visibility = Visibility.Visible;
        Place(Highlight, x, y, w, h);

        if (!_showLabel || _highlighted is null)
        {
            HoverLabel.Visibility = Visibility.Collapsed;
            return;
        }

        HoverTitle.Text = _highlighted.Title;
        HoverDetail.Text = string.IsNullOrEmpty(_highlighted.AppName)
            ? WindowPickerGeometry.FormatSize(_highlighted.Bounds)
            : $"{_highlighted.AppName} · {WindowPickerGeometry.FormatSize(_highlighted.Bounds)}";
        HoverLabel.Visibility = Visibility.Visible;
        HoverLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var labelSize = HoverLabel.DesiredSize;

        // Centre the label in the visible part of the window, clamped to this monitor.
        var labelX = Math.Clamp(x + ((w - labelSize.Width) / 2), LabelMarginDip, Math.Max(LabelMarginDip, width - labelSize.Width - LabelMarginDip));
        var labelY = Math.Clamp(y + ((h - labelSize.Height) / 2), LabelMarginDip, Math.Max(LabelMarginDip, height - labelSize.Height - LabelMarginDip));
        Canvas.SetLeft(HoverLabel, labelX);
        Canvas.SetTop(HoverLabel, labelY);
    }

    private static void Place(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    /// <summary>
    /// Paints the pre-captured monitor snapshot behind the dim so the highlighted window shows
    /// through undimmed. The window stays fully transparent until the snapshot is ready, so the
    /// user never sees a flash of bare dim.
    /// </summary>
    private async Task ShowBackdropWhenReadyAsync()
    {
        try
        {
            var frame = await _backdropTask;
            if (_closedByController)
            {
                return;
            }

            if (frame is not null)
            {
                var softwareBitmap = await Task.Run(() => SoftwareBitmap.CreateCopyFromBuffer(
                    frame.BgraPixels.AsBuffer(),
                    BitmapPixelFormat.Bgra8,
                    frame.Width,
                    frame.Height,
                    BitmapAlphaMode.Premultiplied));
                if (_closedByController)
                {
                    softwareBitmap.Dispose();
                    return;
                }

                var source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(softwareBitmap);
                softwareBitmap.Dispose();
                Backdrop.Source = source;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Window picker backdrop paint failed: {ex}");
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
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_closedByController)
            {
                SetWindowAlpha(_hwnd, 255);
            }
        });
    }

    private void ConfigurePresenter()
    {
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
    }

    private static void SetWindowAlpha(nint hwnd, byte alpha)
    {
        var exStyle = (long)GetWindowLongPtr(hwnd);
        SetWindowLongPtr(hwnd, (nint)(exStyle | WsExLayered));
        SetLayeredWindowAttributes(hwnd, 0, alpha, LwaAlpha);
    }

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
