using System.Runtime.InteropServices;

namespace TinyClips.App;

/// <summary>
/// Focused helpers for native window behaviors shared by borderless overlay windows:
/// capture pickers, session overlays, and the tray popup.
/// Only behaviors with multiple consumers whose semantics are truly identical are
/// centralized here. Specialized region algorithms (punch-hole, layered alpha) stay local
/// to their respective windows.
/// </summary>
internal static class OverlayWindowHelpers
{
    private const uint WdaExcludeFromCapture = 0x11;

    /// <summary>
    /// Applies <c>WDA_EXCLUDEFROMCAPTURE</c> to the window.
    /// Returns <see langword="true"/> on success, <see langword="false"/> on failure.
    /// The caller is responsible for deciding whether to surface or silence a failure;
    /// callers that previously surfaced failures (e.g. <c>TeleprompterWindow</c>) should
    /// still read <see cref="Marshal.GetLastWin32Error"/> after a false return because this
    /// DllImport preserves the Win32 last-error via <c>SetLastError = true</c>.
    /// </summary>
    public static bool ExcludeFromCapture(nint hwnd) =>
        SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);

    /// <summary>
    /// Clips a borderless overlay to a rounded rectangle.
    /// <para>
    /// Native HRGN ownership: after a successful <c>SetWindowRgn</c> call Windows takes
    /// ownership of the HRGN and the caller must not delete it. On failure (or if
    /// <c>CreateRoundRectRgn</c> itself failed) the handle is released here.
    /// </para>
    /// </summary>
    /// <param name="hwnd">Target window handle.</param>
    /// <param name="widthPx">Window width in physical pixels.</param>
    /// <param name="heightPx">Window height in physical pixels.</param>
    /// <param name="scale">DPI scale factor (e.g. 1.5 for 144 DPI).</param>
    /// <param name="cornerRadiusDip">Corner radius in device-independent pixels.</param>
    public static void ApplyRoundedRegion(nint hwnd, int widthPx, int heightPx, double scale, int cornerRadiusDip)
    {
        var radius = (int)Math.Round(cornerRadiusDip * scale);
        var hrgn = CreateRoundRectRgn(0, 0, widthPx + 1, heightPx + 1, radius, radius);
        if (hrgn == 0)
        {
            // CreateRoundRectRgn failed; nothing to apply or free.
            return;
        }

        if (SetWindowRgn(hwnd, hrgn, redrawWindow: true) == 0)
        {
            // SetWindowRgn failed; Windows did not take ownership — release the region.
            DeleteObject(hrgn);
        }
        // On success Windows owns hrgn; do not delete.
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(nint hWnd, nint hRgn, [MarshalAs(UnmanagedType.Bool)] bool redrawWindow);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint hObject);
}
