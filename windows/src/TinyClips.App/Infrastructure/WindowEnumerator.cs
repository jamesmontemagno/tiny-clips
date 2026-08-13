using System.Runtime.InteropServices;
using System.Text;

namespace TinyClips.App;

/// <summary>A top-level window the user can target for capture.</summary>
public sealed record WindowEntry(nint Hwnd, string Title);

/// <summary>
/// Enumerates visible, non-minimized top-level windows (with titles) via Win32 so the
/// user can pick a specific window to capture. Cloaked (hidden UWP) and tool windows
/// are filtered out, mirroring what the macOS window picker shows.
/// </summary>
internal static class WindowEnumerator
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const int DwmwaCloaked = 14;

    public static IReadOnlyList<WindowEntry> GetWindows(nint ownHwnd)
    {
        var windows = new List<WindowEntry>();

        EnumWindows((hwnd, _) =>
        {
            if (hwnd == ownHwnd || !IsWindowVisible(hwnd) || IsIconic(hwnd))
            {
                return true;
            }

            var length = GetWindowTextLength(hwnd);
            if (length == 0)
            {
                return true;
            }

            var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            if ((exStyle & WsExToolWindow) != 0)
            {
                return true;
            }

            if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            var builder = new StringBuilder(length + 1);
            GetWindowText(hwnd, builder, builder.Capacity);
            var title = builder.ToString();
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            windows.Add(new WindowEntry(hwnd, title));
            return true;
        }, nint.Zero);

        return windows;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
}
