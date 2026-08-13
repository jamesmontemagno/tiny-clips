using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using TinyClips.Core.Capture;
using Windows.Graphics;
using WinRT.Interop;

namespace TinyClips.App;

/// <summary>
/// Borderless, always-on-top outline shown around a selected region during countdown.
/// The window is click-through (never absorbs mouse input) and excluded from screen
/// capture so it does not appear in recordings or screenshots.
/// </summary>
public sealed partial class RegionIndicatorWindow : Window
{
    // Width (in DIPs) of the hollow outline frame. The window is expanded outward by this
    // amount and its interior is punched out (see PunchHole), so this constant alone controls
    // the visible border thickness; the XAML only supplies the fill color.
    private const int BorderThicknessDip = 4;

    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000;
    private const long WsExTransparent = 0x00000020;
    private const uint WdaExcludeFromCapture = 0x11;
    private const int RgnDiff = 4;

    private bool _closed;

    public RegionIndicatorWindow()
    {
        InitializeComponent();

        ConfigurePresenter();
        Closed += OnClosed;
    }

    public void Show(PixelRect regionInPhysicalPixels)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = GetScale(hwnd);

        // Draw the border OUTSIDE the region: expand the window outward by the border
        // thickness so the captured content beneath stays unobscured by the outline.
        var inset = Math.Max(1, (int)Math.Round(BorderThicknessDip * scale));

        var rect = new RectInt32(
            regionInPhysicalPixels.X - inset,
            regionInPhysicalPixels.Y - inset,
            Math.Max(1, regionInPhysicalPixels.Width + (inset * 2)),
            Math.Max(1, regionInPhysicalPixels.Height + (inset * 2)));

        AppWindow.MoveAndResize(rect);
        AppWindow.Show(false);

        ApplyOverlayStyles(hwnd);
        PunchHole(hwnd, rect.Width, rect.Height, inset);
    }

    /// <summary>
    /// Clips the window to just the border frame using a window region with the interior
    /// removed, so the recorded area beneath stays fully visible to the user (a hollow
    /// outline rather than an opaque overlay).
    /// </summary>
    private static void PunchHole(nint hwnd, int width, int height, int inset)
    {
        var outer = CreateRectRgn(0, 0, width, height);
        var inner = CreateRectRgn(inset, inset, width - inset, height - inset);
        var frame = CreateRectRgn(0, 0, 0, 0);

        CombineRgn(frame, outer, inner, RgnDiff);
        DeleteObject(outer);
        DeleteObject(inner);

        // SetWindowRgn takes ownership of 'frame'; do not delete it afterwards.
        SetWindowRgn(hwnd, frame, true);
    }

    public void ClosePanel()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        Close();
    }

    private void ConfigurePresenter()
    {
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        AppWindow.IsShownInSwitchers = false;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
    }

    /// <summary>
    /// Makes the overlay click-through (so it never steals mouse input, mirroring the
    /// macOS <c>ignoresMouseEvents</c>) and excludes it from screen capture so it stays
    /// invisible in the recorded video/GIF/screenshot.
    /// </summary>
    private static void ApplyOverlayStyles(nint hwnd)
    {
        var exStyle = (long)GetWindowLongPtr(hwnd, GwlExStyle);
        var newStyle = (nint)(exStyle | WsExLayered | WsExTransparent);
        SetWindowLongPtr(hwnd, GwlExStyle, newStyle);

        SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
    }

    private static double GetScale(nint hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi <= 0 ? 1.0 : dpi / 96.0;
    }

    // 32/64-bit-safe GetWindowLongPtr / SetWindowLongPtr wrappers. On 32-bit Windows the
    // *Ptr entry points do not exist, so fall back to the 32-bit GetWindowLong/SetWindowLong.
    private static nint GetWindowLongPtr(nint hwnd, int index) =>
        nint.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLong32(hwnd, index);

    private static nint SetWindowLongPtr(nint hwnd, int index, nint value) =>
        nint.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : SetWindowLong32(hwnd, index, (int)value);

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
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(nint hWnd, nint hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(nint hrgnDst, nint hrgnSrc1, nint hrgnSrc2, int fnCombineMode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint hObject);
}
