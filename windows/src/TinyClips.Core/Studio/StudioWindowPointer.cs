using System.Runtime.InteropServices;
using TinyClips.Core.Capture;

namespace TinyClips.Core.Studio;

/// <summary>Maps at observation time, never from a window origin remembered at recording start.</summary>
internal sealed class StudioWindowPointer(Func<PixelRect?> readBounds, Func<WindowCaptureFrame?> readFrame)
{
    public static StudioWindowPointer ForWindow(nint hwnd, Func<WindowCaptureFrame?> readFrame) =>
        new(() => ReadBounds(hwnd), readFrame);

    public MouseClickSample? CaptureClick(MouseClickSample click) =>
        TryMap(click.ScreenX, click.ScreenY, out var point) ? click with { CapturePoint = point } : null;

    public StudioPointSample? CaptureCursor(TimeSpan pts, int x, int y) =>
        TryMap(x, y, out var point) ? new StudioPointSample(pts, x, y) { CapturePoint = point } : null;

    private bool TryMap(int x, int y, out (double X, double Y) point)
    {
        point = default;
        return readBounds() is { } bounds && readFrame() is { } frame && frame.TryMap(bounds, x, y, out point);
    }

    private static PixelRect? ReadBounds(nint hwnd)
    {
        // DWM's visible frame, not GetWindowRect's invisible resize borders or the client rect.
        // DWM bounds and low-level mouse/GetCursorPos coordinates are physical desktop pixels
        // under the app's Per-Monitor-V2 awareness; do not apply a second DPI scale.
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd) ||
            DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) != 0 || cloaked != 0 ||
            DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT rect, Marshal.SizeOf<RECT>()) != 0 ||
            rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            return null;
        }

        return new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
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
    private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out RECT value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
}
