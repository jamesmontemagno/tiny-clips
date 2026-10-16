using Windows.Graphics.Imaging;

namespace TinyClips.Core.Capture;

internal static class WebcamFrameSizing
{
    /// <summary>
    /// The largest size with the source's aspect ratio that fits inside <paramref name="bounds"/>
    /// without scaling up, with even dimensions for the video encoder. Returns
    /// <paramref name="bounds"/> when the source size is unknown.
    /// </summary>
    public static BitmapSize FitWithin(uint sourceWidth, uint sourceHeight, BitmapSize bounds)
    {
        if (sourceWidth == 0 || sourceHeight == 0 || bounds.Width == 0 || bounds.Height == 0)
        {
            return bounds;
        }

        var scale = Math.Min(1.0, Math.Min(bounds.Width / (double)sourceWidth, bounds.Height / (double)sourceHeight));
        return new BitmapSize
        {
            Width = Even(sourceWidth * scale),
            Height = Even(sourceHeight * scale),
        };
    }

    private static uint Even(double value)
    {
        var rounded = (uint)Math.Max(2, Math.Round(value));
        return rounded - (rounded % 2);
    }
}