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
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmWindowCornerDoNotRound = 1;
    private const uint DwmColorNone = 0xFFFFFFFE;

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
    /// <para>
    /// Call this only <em>after</em> the window's first present (see
    /// <c>CountdownWindow.RunAsync</c>): applying <c>SetWindowRgn</c> before a WinUI window has
    /// been shown can leave its surface blank.
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
        ApplyRoundedRegionPx(hwnd, widthPx, heightPx, radius);
    }

    /// <summary>
    /// Clips a borderless overlay to a rounded rectangle whose corner radius is already expressed
    /// in physical pixels. Same HRGN ownership rules as <see cref="ApplyRoundedRegion"/>.
    /// </summary>
    public static void ApplyRoundedRegionPx(nint hwnd, int widthPx, int heightPx, int cornerRadiusPx)
    {
        // CreateRoundRectRgn takes the width/height of the *ellipse* that forms the corners, i.e.
        // the diameter — passing the radius directly would round the corners at half the value.
        var diameter = Math.Max(0, cornerRadiusPx) * 2;
        var hrgn = CreateRoundRectRgn(0, 0, widthPx + 1, heightPx + 1, diameter, diameter);
        ApplyRegion(hwnd, hrgn);
    }

    /// <summary>
    /// Clips a borderless overlay to an ellipse inscribed in the window bounds (a circle when the
    /// window is square). Same HRGN ownership rules as <see cref="ApplyRoundedRegion"/>.
    /// </summary>
    public static void ApplyEllipticRegion(nint hwnd, int widthPx, int heightPx)
    {
        var hrgn = CreateEllipticRgn(0, 0, widthPx + 1, heightPx + 1);
        ApplyRegion(hwnd, hrgn);
    }

    private static void ApplyRegion(nint hwnd, nint hrgn)
    {
        if (hrgn == 0)
        {
            // Region creation failed; nothing to apply or free.
            return;
        }

        if (SetWindowRgn(hwnd, hrgn, redrawWindow: true) == 0)
        {
            // SetWindowRgn failed; Windows did not take ownership — release the region.
            DeleteObject(hrgn);
        }
        // On success Windows owns hrgn; do not delete.
    }

    /// <summary>
    /// Removes the DWM-drawn window outline from a borderless overlay: no rounded-corner treatment
    /// and no border color. Without this the system paints a thin light/dark hairline around the
    /// window rectangle that survives <see cref="ApplyEllipticRegion"/> clipping and reads as a
    /// grey box around non-rectangular content.
    /// </summary>
    public static void RemoveSystemBorder(nint hwnd)
    {
        var doNotRound = DwmWindowCornerDoNotRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref doNotRound, sizeof(int));
        var noBorder = DwmColorNone;
        DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref noBorder, sizeof(uint));
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(nint hWnd, nint hRgn, [MarshalAs(UnmanagedType.Bool)] bool redrawWindow);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern nint CreateEllipticRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint hObject);
}
