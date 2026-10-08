using System.Runtime.InteropServices;

namespace TinyClips.Tools.StudioPreviewCheck.Windowed;

/// <summary>The few Win32 calls the window checks need: keeping the window out of the way, and saying which window is in front.</summary>
internal static partial class NativeMethods
{
    private static readonly nint HwndBottom = 1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static unsafe partial int GetWindowText(nint window, char* text, int maxCount);

    /// <summary>Puts a window behind every other window without activating it.</summary>
    internal static void SendToBack(nint window) =>
        SetWindowPos(window, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

    internal static bool IsForeground(nint window) => GetForegroundWindow() == window;

    /// <summary>The title of the window the person at the machine is working in, for the report.</summary>
    internal static unsafe string ForegroundTitle()
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0)
        {
            return "(none)";
        }

        var buffer = stackalloc char[96];
        var length = GetWindowText(foreground, buffer, 96);
        return length > 0 ? new string(buffer, 0, length) : "(untitled)";
    }
}
