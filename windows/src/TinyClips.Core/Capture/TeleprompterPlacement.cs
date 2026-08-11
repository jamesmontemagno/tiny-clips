namespace TinyClips.Core.Capture;

public static class TeleprompterPlacement
{
    public static PixelRect Calculate(
        MonitorInfo monitor,
        int widthDip,
        int heightDip,
        int topOffsetDip,
        double savedXDip,
        double savedYDip,
        bool savedPositionIsMonitorRelative)
    {
        var scale = monitor.ScaleFactor > 0 ? monitor.ScaleFactor : 1.0;
        var width = Math.Clamp(
            (int)Math.Round(widthDip * scale),
            1,
            Math.Max(1, monitor.WorkAreaWidth));
        var height = Math.Clamp(
            (int)Math.Round(heightDip * scale),
            1,
            Math.Max(1, monitor.WorkAreaHeight));
        var workRight = monitor.WorkAreaX + Math.Max(0, monitor.WorkAreaWidth - width);
        var workBottom = monitor.WorkAreaY + Math.Max(0, monitor.WorkAreaHeight - height);

        int x;
        int y;
        var hasSavedPosition = double.IsFinite(savedXDip) &&
            double.IsFinite(savedYDip) &&
            (savedPositionIsMonitorRelative || (savedXDip >= 0 && savedYDip >= 0));
        if (hasSavedPosition)
        {
            x = (int)Math.Round(savedXDip * scale);
            y = (int)Math.Round(savedYDip * scale);
            if (savedPositionIsMonitorRelative)
            {
                x += monitor.WorkAreaX;
                y += monitor.WorkAreaY;
            }
        }
        else
        {
            x = monitor.WorkAreaX + Math.Max(0, (monitor.WorkAreaWidth - width) / 2);
            y = monitor.WorkAreaY + (int)Math.Round(topOffsetDip * scale);
        }

        return new PixelRect(
            Math.Clamp(x, monitor.WorkAreaX, workRight),
            Math.Clamp(y, monitor.WorkAreaY, workBottom),
            width,
            height);
    }

    public static (double X, double Y) ToMonitorRelativeDips(
        MonitorInfo monitor,
        int windowX,
        int windowY)
    {
        var scale = monitor.ScaleFactor > 0 ? monitor.ScaleFactor : 1.0;
        return (
            (windowX - monitor.WorkAreaX) / scale,
            (windowY - monitor.WorkAreaY) / scale);
    }
}
