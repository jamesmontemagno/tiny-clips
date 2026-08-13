using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace TinyClips.App;

internal static class AppWindowPlacement
{
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
}
