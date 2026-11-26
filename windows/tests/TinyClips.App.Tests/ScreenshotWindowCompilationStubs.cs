using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace TinyClips.App;

// The tests compile the real window classes but do not construct WinUI windows.
internal static class WindowCaptureVisibility
{
    public static void Register(Window window) { }
}

internal static class AppWindowPlacement
{
    public static void CenterInCurrentWorkAreaAtDipSize(AppWindow window, IntPtr hwnd, int width, int height) { }
}

internal sealed class WindowChromeController
{
    public WindowChromeController(Window window, FrameworkElement root, int minimumWidth, int minimumHeight) { }
}
