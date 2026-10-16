namespace TinyClips.Core.Studio.Editing;

/// <summary>Where the Studio preview canvas sits inside the space the window gives it.</summary>
public static class StudioPreviewLayout
{
    /// <summary>
    /// The largest rectangle of <paramref name="aspectRatio"/> (width over height) that fits a view,
    /// centered. Every edge lands on a whole physical pixel, because a swap chain placed on a
    /// fraction of a pixel is resampled and looks soft.
    /// </summary>
    /// <param name="viewWidth">The width available, in effective pixels.</param>
    /// <param name="viewHeight">The height available, in effective pixels.</param>
    /// <param name="aspectRatio">The canvas width divided by its height.</param>
    /// <param name="rasterizationScale">Physical pixels per effective pixel, such as 1.5 at 150%.</param>
    /// <returns>
    /// The rectangle in effective pixels, relative to the view. Empty when the view has no room or
    /// the aspect ratio is not a positive number.
    /// </returns>
    public static StudioFrameRect FitCanvas(double viewWidth, double viewHeight, double aspectRatio, double rasterizationScale)
    {
        var scale = double.IsFinite(rasterizationScale) && rasterizationScale > 0 ? rasterizationScale : 1;
        if (!double.IsFinite(viewWidth) || !double.IsFinite(viewHeight) || !double.IsFinite(aspectRatio) || !(aspectRatio > 0))
        {
            return default;
        }

        // A view that is exactly 600 pixels wide can arrive as 599.9999 after the scale is applied.
        const double Slack = 1e-6;
        var pixelWidth = Math.Floor(viewWidth * scale + Slack);
        var pixelHeight = Math.Floor(viewHeight * scale + Slack);
        if (pixelWidth < 1 || pixelHeight < 1)
        {
            return default;
        }

        double width;
        double height;
        if (pixelWidth / pixelHeight > aspectRatio)
        {
            height = pixelHeight;
            width = Math.Floor(height * aspectRatio + Slack);
        }
        else
        {
            width = pixelWidth;
            height = Math.Floor(width / aspectRatio + Slack);
        }

        width = Math.Min(Math.Max(width, 1), pixelWidth);
        height = Math.Min(Math.Max(height, 1), pixelHeight);
        var x = Math.Floor((pixelWidth - width) / 2);
        var y = Math.Floor((pixelHeight - height) / 2);
        return new StudioFrameRect(x / scale, y / scale, width / scale, height / scale);
    }
}
