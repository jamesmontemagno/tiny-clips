using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace TinyClips.App;

internal static class AppWindowPlacement
{
    private const double DefaultDpi = 96.0;

    public static void CenterInCurrentWorkAreaAtDipSize(AppWindow appWindow, nint hwnd, int widthDip, int heightDip)
    {
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi == 0 ? 1.0 : dpi / DefaultDpi;
        var workArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Clamp((int)Math.Round(widthDip * scale), 1, workArea.Width);
        var height = Math.Clamp((int)Math.Round(heightDip * scale), 1, workArea.Height);
        var x = workArea.X + ((workArea.Width - width) / 2);
        var y = workArea.Y + ((workArea.Height - height) / 2);

        appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    public static void CenterInCurrentWorkAreaAtHalfSize(AppWindow appWindow)
    {
        var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var width = Math.Max(1, workArea.Width / 2);
        var height = Math.Max(1, workArea.Height * 3 / 4);
        var x = workArea.X + ((workArea.Width - width) / 2);
        var y = workArea.Y + ((workArea.Height - height) / 2);

        appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
