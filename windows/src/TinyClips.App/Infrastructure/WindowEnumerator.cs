using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TinyClips.Core.Capture;

namespace TinyClips.App;

/// <summary>
/// A top-level window in the window picker's front-to-back hit-test list. <see cref="Bounds"/>
/// is the visible frame in virtual-desktop physical pixels. Non-selectable entries (the taskbar)
/// only block the windows behind them.
/// </summary>
public sealed record WindowPickerCandidate(nint Hwnd, string Title, string AppName, PixelRect Bounds, bool IsSelectable)
{
    public WindowPickerTarget Target => new(Hwnd, Bounds, IsSelectable);
}

/// <summary>
/// Enumerates visible, non-minimized top-level windows (with titles) via Win32 so the user can
/// pick a specific window to capture. <c>EnumWindows</c> reports windows in z-order (topmost
/// first), which the picker relies on to highlight the window that is actually on top. Cloaked
/// (other virtual desktops, hidden UWP), click-through, fully transparent and desktop windows are
/// ignored; visible tool, tiny and untitled windows can't be picked but still block the windows
/// behind them, mirroring what the macOS window picker shows.
/// </summary>
internal static class WindowEnumerator
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExTransparent = 0x00000020;
    private const long WsExLayered = 0x00080000;
    private const uint LwaColorKey = 0x00000001;
    private const uint LwaAlpha = 0x00000002;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MinimumSizePx = 40;

    private static readonly HashSet<string> TaskbarClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    };

    private static readonly HashSet<string> DesktopClasses = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
    };

    public static IReadOnlyList<WindowPickerCandidate> GetWindows()
    {
        var windows = new List<WindowPickerCandidate>();
        var appNames = new Dictionary<uint, string>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd))
            {
                return true;
            }

            var className = GetClassName(hwnd);
            if (DesktopClasses.Contains(className))
            {
                return true;
            }

            var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            var layered = GetLayeredVisibility(hwnd, exStyle);
            if ((exStyle & WsExTransparent) != 0
                || layered == LayeredVisibility.Invisible
                || !TryGetVisibleBounds(hwnd, out var bounds))
            {
                return true;
            }

            if (TaskbarClasses.Contains(className))
            {
                windows.Add(Occluder(hwnd, bounds));
                return true;
            }

            // Tiny Clips' own windows are hidden or excluded from capture, so they neither show
            // in the backdrop nor block the windows behind them.
            GetWindowThreadProcessId(hwnd, out var processId);
            if (!WindowCaptureVisibility.Policy.CanSelectWindow(processId == Environment.ProcessId, hwnd, pickerHwnd: 0))
            {
                return true;
            }

            var title = (exStyle & WsExToolWindow) != 0
                || bounds.Width < MinimumSizePx
                || bounds.Height < MinimumSizePx
                ? string.Empty
                : GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title))
            {
                // Visible palettes, popups and untitled windows still cover what is behind them,
                // so they block the hit-test instead of letting the click fall through. Per-pixel
                // layered windows are skipped because their visible shape is unknown and they are
                // often full-screen, mostly transparent overlays.
                if (layered == LayeredVisibility.Opaque)
                {
                    windows.Add(Occluder(hwnd, bounds));
                }

                return true;
            }

            if (!appNames.TryGetValue(processId, out var appName))
            {
                appName = GetAppName(processId);
                appNames[processId] = appName;
            }

            windows.Add(new WindowPickerCandidate(hwnd, title, appName, bounds, IsSelectable: true));
            return true;
        }, nint.Zero);

        return windows;
    }

    private static WindowPickerCandidate Occluder(nint hwnd, PixelRect bounds) =>
        new(hwnd, string.Empty, string.Empty, bounds, IsSelectable: false);

    /// <summary>
    /// Classifies a window's layered transparency. Windows faded fully out with
    /// <c>SetLayeredWindowAttributes</c> (alpha 0) are invisible; windows drawn with
    /// <c>UpdateLayeredWindow</c> report no attributes, and color-keyed windows have transparent
    /// pixels, so both have an unknown per-pixel shape.
    /// </summary>
    private static LayeredVisibility GetLayeredVisibility(nint hwnd, long exStyle)
    {
        if ((exStyle & WsExLayered) == 0)
        {
            return LayeredVisibility.Opaque;
        }

        if (!GetLayeredWindowAttributes(hwnd, out _, out var alpha, out var flags))
        {
            return LayeredVisibility.PerPixel;
        }

        if ((flags & LwaAlpha) != 0 && alpha == 0)
        {
            return LayeredVisibility.Invisible;
        }

        // Color-keyed windows have transparent pixels even at full alpha, so their shape is unknown.
        return (flags & LwaColorKey) != 0 ? LayeredVisibility.PerPixel : LayeredVisibility.Opaque;
    }

    private enum LayeredVisibility
    {
        Opaque,
        PerPixel,
        Invisible,
    }

    private static bool IsCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>
    /// Uses DWM's extended frame bounds, which exclude the invisible resize borders and drop
    /// shadow that <c>GetWindowRect</c> includes on Windows 10/11, so the highlight hugs the
    /// window the user sees. Both APIs report physical pixels for this Per-Monitor-V2 process.
    /// </summary>
    private static bool TryGetVisibleBounds(nint hwnd, out PixelRect bounds)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out Rect frame, Marshal.SizeOf<Rect>()) != 0
            && !GetWindowRect(hwnd, out frame))
        {
            bounds = default;
            return false;
        }

        bounds = new PixelRect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private static string GetWindowTitle(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString().Trim();
    }

    private static string GetClassName(nint hwnd)
    {
        var builder = new StringBuilder(256);
        return GetClassName(hwnd, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
    }

    /// <summary>
    /// Returns the friendly product name of the window's process (e.g. "Microsoft Edge"), or an
    /// empty string when it cannot be read. UWP frames are hosted by ApplicationFrameHost, whose
    /// name would be misleading, so those fall back to the window title alone.
    /// </summary>
    private static string GetAppName(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == nint.Zero)
        {
            return string.Empty;
        }

        try
        {
            var builder = new StringBuilder(1024);
            var size = builder.Capacity;
            if (!QueryFullProcessImageName(process, 0, builder, ref size))
            {
                return string.Empty;
            }

            var path = builder.ToString();
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (string.Equals(fileName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                return string.IsNullOrWhiteSpace(description) ? fileName : description.Trim();
            }
            catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException)
            {
                return fileName;
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLayeredWindowAttributes(nint hwnd, out uint colorKey, out byte alpha, out uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
