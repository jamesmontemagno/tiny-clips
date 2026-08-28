using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace TinyClips.App;

/// <summary>
/// Excluded-from-capture live webcam surface that snaps to capture-region corners.
/// </summary>
public sealed partial class WebcamPreviewWindow : Window
{
    private readonly IWebcamCaptureService _capture;
    private readonly RectInt32 _captureBounds;
    private readonly int _margin;
    private readonly WebcamShape _shape;
    private readonly double? _cornerRadius;
    private const int ShapeInsetDip = 2;
    private WebcamCornerPosition _corner;
    private Action<WebcamCornerPosition>? _cornerChanged;
    private bool _closed;
    private bool _dragging;
    private POINT _dragCursorStart;
    private PointInt32 _dragWindowStart;

    public WebcamPreviewWindow(
        IWebcamCaptureService capture,
        CaptureTarget target,
        MonitorInfo? monitor,
        PixelRect? regionInVirtualDesktop,
        WebcamCornerPosition corner,
        WebcamSizePreset size,
        WebcamShape shape,
        double? cornerRadius,
        Action<WebcamCornerPosition> cornerChanged)
    {
        InitializeComponent();
        _capture = capture;
        _captureBounds = ResolveCaptureBounds(target, monitor, regionInVirtualDesktop);
        _corner = corner;
        _shape = shape;
        _cornerRadius = cornerRadius;
        _cornerChanged = cornerChanged;
        _margin = Math.Clamp((int)Math.Round(Math.Min(_captureBounds.Width, _captureBounds.Height) * 0.03), 12, 40);

        ConfigurePresenter();
        ResizePreview(size, shape);
        PreviewSurface.ConfigureShape(shape, cornerRadius);

        // Non-rectangular shapes are clipped twice: by the anti-aliased XAML shape and by the
        // window's GDI region, which has hard stair-stepped edges. Insetting the content keeps the
        // region boundary off the visible curve so the smooth XAML edge is what shows; the exposed
        // ring is transparent window background (see TransparentBackdrop + RemoveSystemBorder).
        PreviewSurface.Margin = shape == WebcamShape.Rectangle
            ? new Thickness(0)
            : new Thickness(ShapeInsetDip);

        Closed += OnClosed;
    }

    public void Show()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (!OverlayWindowHelpers.ExcludeFromCapture(hwnd))
        {
            ClosePanel();
            return;
        }

        SnapTo(_corner);
        ApplyWindowShape(hwnd);
        PreviewSurface.Attach(_capture);
        AppWindow.Show(false);
    }

    /// <summary>
    /// Clips the native window to the webcam shape. The XAML surface already rounds its own
    /// corners, but the window itself still paints an opaque rectangular background behind them;
    /// a matching HRGN keeps that rectangle from showing around a circle or rounded preview.
    /// </summary>
    private void ApplyWindowShape(nint hwnd)
    {
        if (_shape == WebcamShape.Rectangle)
        {
            return;
        }

        OverlayWindowHelpers.RemoveSystemBorder(hwnd);
        var size = AppWindow.Size;
        if (_shape == WebcamShape.Circle)
        {
            OverlayWindowHelpers.ApplyEllipticRegion(hwnd, size.Width, size.Height);
            return;
        }

        var radiusPx = _cornerRadius is { } configured
            ? (int)Math.Round(configured)
            : (int)Math.Round(Math.Min(size.Width, size.Height) * 0.12);
        OverlayWindowHelpers.ApplyRoundedRegionPx(hwnd, size.Width, size.Height, radiusPx);
    }

    public void ClosePanel()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _cornerChanged = null;
        PreviewSurface.Detach();
        Close();
    }

    private void ResizePreview(WebcamSizePreset size, WebcamShape shape)
    {
        var fraction = size switch
        {
            WebcamSizePreset.Small => 0.18,
            WebcamSizePreset.Large => 0.30,
            _ => 0.24,
        };
        int width;
        int height;
        if (shape == WebcamShape.Circle)
        {
            var maxSide = Math.Max(2, Math.Min(_captureBounds.Width, _captureBounds.Height) - 2);
            width = height = Math.Clamp(
                (int)Math.Round(Math.Min(_captureBounds.Width, _captureBounds.Height) * fraction),
                Math.Min(48, maxSide),
                maxSide);
        }
        else
        {
            var aspect = _capture.TryGetLatestFrame(out var frame) && frame is { Width: > 0, Height: > 0 }
                ? frame.Width / (double)frame.Height
                : 16.0 / 9.0;
            width = Math.Clamp(
                (int)Math.Round(_captureBounds.Width * fraction),
                64,
                Math.Max(64, (int)Math.Round(_captureBounds.Width * 0.45)));
            height = Math.Max(48, (int)Math.Round(width / aspect));
            var maxHeight = Math.Max(48, (int)Math.Round(_captureBounds.Height * 0.40));
            if (height > maxHeight)
            {
                height = maxHeight;
                width = Math.Max(64, (int)Math.Round(height * aspect));
            }

            width = Math.Min(width, Math.Max(2, _captureBounds.Width - 2));
            height = Math.Min(height, Math.Max(2, _captureBounds.Height - 2));
        }

        AppWindow.Resize(new SizeInt32(width, height));

        // Windows can enforce a minimum tracking size, so the window may come back non-square. A
        // circular preview stretched into a non-square window renders as an oval, so square it up
        // against whatever size actually took effect.
        if (shape == WebcamShape.Circle)
        {
            var actual = AppWindow.Size;
            if (actual.Width != actual.Height)
            {
                var side = Math.Max(actual.Width, actual.Height);
                AppWindow.Resize(new SizeInt32(side, side));
            }
        }
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
        PreviewControl.Focus(FocusState.Pointer);
        GetCursorPos(out _dragCursorStart);
        _dragWindowStart = AppWindow.Position;
        _dragging = PreviewControl.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        GetCursorPos(out var current);
        AppWindow.Move(new PointInt32(
            _dragWindowStart.X + current.X - _dragCursorStart.X,
            _dragWindowStart.Y + current.Y - _dragCursorStart.Y));
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        PreviewControl.ReleasePointerCapture(e.Pointer);
        var position = AppWindow.Position;
        ChangeCorner(NearestCorner(new PointInt32(
            position.X + (AppWindow.Size.Width / 2),
            position.Y + (AppWindow.Size.Height / 2))));
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var next = e.Key switch
        {
            VirtualKey.Left => IsTop(_corner) ? WebcamCornerPosition.TopLeft : WebcamCornerPosition.BottomLeft,
            VirtualKey.Right => IsTop(_corner) ? WebcamCornerPosition.TopRight : WebcamCornerPosition.BottomRight,
            VirtualKey.Up => IsLeft(_corner) ? WebcamCornerPosition.TopLeft : WebcamCornerPosition.TopRight,
            VirtualKey.Down => IsLeft(_corner) ? WebcamCornerPosition.BottomLeft : WebcamCornerPosition.BottomRight,
            VirtualKey.Home => WebcamCornerPosition.TopLeft,
            VirtualKey.PageUp => WebcamCornerPosition.TopRight,
            VirtualKey.End => WebcamCornerPosition.BottomLeft,
            VirtualKey.PageDown => WebcamCornerPosition.BottomRight,
            _ => _corner,
        };
        if (next != _corner)
        {
            ChangeCorner(next);
            e.Handled = true;
        }
    }

    private void ChangeCorner(WebcamCornerPosition corner)
    {
        _corner = corner;
        SnapTo(corner);
        _cornerChanged?.Invoke(corner);
    }

    private void SnapTo(WebcamCornerPosition corner)
    {
        var x = IsLeft(corner)
            ? _captureBounds.X + _margin
            : _captureBounds.X + _captureBounds.Width - AppWindow.Size.Width - _margin;
        var y = IsTop(corner)
            ? _captureBounds.Y + _margin
            : _captureBounds.Y + _captureBounds.Height - AppWindow.Size.Height - _margin;
        AppWindow.Move(new PointInt32(x, y));
    }

    private WebcamCornerPosition NearestCorner(PointInt32 center)
    {
        var left = center.X < _captureBounds.X + (_captureBounds.Width / 2);
        var top = center.Y < _captureBounds.Y + (_captureBounds.Height / 2);
        return (top, left) switch
        {
            (true, true) => WebcamCornerPosition.TopLeft,
            (true, false) => WebcamCornerPosition.TopRight,
            (false, true) => WebcamCornerPosition.BottomLeft,
            _ => WebcamCornerPosition.BottomRight,
        };
    }

    private static bool IsLeft(WebcamCornerPosition corner) =>
        corner is WebcamCornerPosition.TopLeft or WebcamCornerPosition.BottomLeft;

    private static bool IsTop(WebcamCornerPosition corner) =>
        corner is WebcamCornerPosition.TopLeft or WebcamCornerPosition.TopRight;

    private static RectInt32 ResolveCaptureBounds(
        CaptureTarget target,
        MonitorInfo? monitor,
        PixelRect? regionInVirtualDesktop)
    {
        if (regionInVirtualDesktop is { Width: > 0, Height: > 0 } region)
        {
            return new RectInt32(region.X, region.Y, region.Width, region.Height);
        }

        if (target.IsWindow && GetWindowRect(target.Hwnd, out var windowRect))
        {
            return new RectInt32(
                windowRect.Left,
                windowRect.Top,
                Math.Max(1, windowRect.Right - windowRect.Left),
                Math.Max(1, windowRect.Bottom - windowRect.Top));
        }

        if (monitor is { Width: > 0, Height: > 0 })
        {
            return new RectInt32(monitor.X, monitor.Y, monitor.Width, monitor.Height);
        }

        return DisplayArea.Primary?.OuterBounds ?? new RectInt32(0, 0, 1280, 720);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _cornerChanged = null;
        PreviewSurface.Detach();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);
}
