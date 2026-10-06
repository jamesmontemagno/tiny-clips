using System.Runtime.InteropServices;

namespace TinyClips.Tools.StudioWindowCheck;

/// <summary>
/// The few Win32 calls the tool needs. All of them read, except the one that puts a window of the
/// tool behind the others. None sends input, and none changes which window is in front.
/// </summary>
internal static partial class Native
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExTopmost = 0x00000008;
    private const int SwHide = 0;
    private const uint WmActivate = 0x0006;
    private static readonly nint HwndBottom = 1;

    [ThreadStatic]
    private static List<nint>? _enumerated;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static unsafe partial int GetWindowText(nint window, char* text, int maxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassName(nint window, char* text, int maxCount);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint parameter);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, int attribute, out Rect value, int size);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint window, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(nint window, int command);

    /// <summary>
    /// Marks a window as one the system does not bring to the front by itself, for instance when
    /// the window in front of it is closed or minimized.
    /// </summary>
    internal static void MakeUnactivatable(nint window) =>
        SetWindowLongPtr(window, GwlExStyle, (nint)(GetWindowLongPtr(window, GwlExStyle) | WsExNoActivate));

    /// <summary>Takes a window off the screen without waiting for its thread.</summary>
    internal static void Hide(nint window) => ShowWindowAsync(window, SwHide);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    /// <summary>
    /// Tells a window that it has been activated, or deactivated, with the message Windows sends
    /// for it. Windows itself is not involved: nothing is activated, and which window is in front
    /// does not change. Call it on the window's thread.
    /// </summary>
    internal static void TellActivation(nint window, bool isActive) => SendMessage(window, WmActivate, isActive ? 1 : 0, 0);

    /// <summary>Puts a window behind every other window without activating it.</summary>
    internal static void SendToBack(nint window) =>
        SetWindowPos(window, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

    internal static nint Foreground() => GetForegroundWindow();

    internal static bool Exists(nint window) => IsWindow(window);

    /// <summary>The process a window belongs to, or 0.</summary>
    internal static uint ProcessOf(nint window)
    {
        if (window == 0)
        {
            return 0;
        }

        GetWindowThreadProcessId(window, out var processId);
        return processId;
    }

    internal static unsafe string TitleOf(nint window)
    {
        if (window == 0)
        {
            return "(none)";
        }

        var buffer = stackalloc char[160];
        var length = GetWindowText(window, buffer, 160);
        return length > 0 ? new string(buffer, 0, length) : "(untitled)";
    }

    internal static unsafe string ClassOf(nint window)
    {
        var buffer = stackalloc char[160];
        var length = GetClassName(window, buffer, 160);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    /// <summary>
    /// The window as the system draws it, in screen pixels: without the invisible borders a
    /// resizable window has. A screenshot of the window is this rectangle.
    /// </summary>
    internal static Rect FrameBounds(nint window)
    {
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(window, DwmwaExtendedFrameBounds, out var bounds, Marshal.SizeOf<Rect>()));
        return bounds;
    }

    /// <summary>The visible top-level windows of this process.</summary>
    internal static List<nint> VisibleWindowsOfThisProcess() => VisibleWindows(everyProcess: false);

    /// <summary>Every visible top-level window on the desktop, from the one in front to the one at the back.</summary>
    internal static List<nint> VisibleWindowsFromTheFront() => VisibleWindows(everyProcess: true);

    /// <summary>Whether a window is one the system keeps in front of every window that is not.</summary>
    internal static bool IsTopmost(nint window) => ((long)GetWindowLongPtr(window, GwlExStyle) & WsExTopmost) != 0;

    private static unsafe List<nint> VisibleWindows(bool everyProcess)
    {
        var found = new List<nint>();
        _enumerated = found;
        try
        {
            EnumWindows(&OnWindow, everyProcess ? 1 : 0);
        }
        finally
        {
            _enumerated = null;
        }

        return found;
    }

    // Windows hands the top-level windows over from the front to the back.
    [UnmanagedCallersOnly]
    private static int OnWindow(nint window, nint everyProcess)
    {
        if (IsWindowVisible(window) && (everyProcess != 0 || ProcessOf(window) == (uint)Environment.ProcessId))
        {
            _enumerated?.Add(window);
        }

        return 1;
    }
}
