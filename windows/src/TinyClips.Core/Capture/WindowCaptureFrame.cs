namespace TinyClips.Core.Capture;

/// <summary>
/// The physical WGC content and its destination in the fixed encoder frame. Published as an
/// immutable snapshot, including the first (possibly surface-clipped) frame after a resize.
/// </summary>
internal sealed record WindowCaptureFrame(
    int ContentWidth, int ContentHeight, int SourceWidth, int SourceHeight,
    int OutputWidth, int OutputHeight, double Left, double Top, double DrawWidth, double DrawHeight, bool IsScaled)
{
    public static bool NeedsLetterbox(int width, int height, int outputWidth, int outputHeight) =>
        width < outputWidth || height < outputHeight || width > outputWidth + 1 || height > outputHeight + 1;

    public static WindowCaptureFrame Create(
        int contentWidth, int contentHeight, int surfaceWidth, int surfaceHeight,
        int outputWidth, int outputHeight, bool letterbox, bool roundToPixels)
    {
        var width = Math.Clamp(contentWidth, 0, surfaceWidth);
        var height = Math.Clamp(contentHeight, 0, surfaceHeight);
        double drawWidth = Math.Min(width, outputWidth), drawHeight = Math.Min(height, outputHeight);
        double left = 0, top = 0;
        var isScaled = letterbox && width > 0 && height > 0 &&
            NeedsLetterbox(width, height, outputWidth, outputHeight);
        if (isScaled)
        {
            // Match CPU integer rasterization and GPU float Direct2D positioning respectively.
            var scale = roundToPixels
                ? Math.Min(outputWidth / (double)width, outputHeight / (double)height)
                : Math.Min(outputWidth / (float)width, outputHeight / (float)height);
            drawWidth = roundToPixels ? Math.Clamp(Math.Round(width * scale), 1, outputWidth) : width * (float)scale;
            drawHeight = roundToPixels ? Math.Clamp(Math.Round(height * scale), 1, outputHeight) : height * (float)scale;
            left = roundToPixels ? (outputWidth - (int)drawWidth) / 2 : (outputWidth - (float)drawWidth) / 2f;
            top = roundToPixels ? (outputHeight - (int)drawHeight) / 2 : (outputHeight - (float)drawHeight) / 2f;
        }

        return new(contentWidth, contentHeight, width, height, outputWidth, outputHeight, left, top, drawWidth, drawHeight, isScaled);
    }

    public bool TryMap(PixelRect bounds, int x, int y, out (double X, double Y) point)
    {
        point = default;
        // Never combine a resized HWND's origin/size with an old WGC frame.
        if (bounds.Width != ContentWidth || bounds.Height != ContentHeight ||
            SourceWidth <= 0 || SourceHeight <= 0 || OutputWidth <= 0 || OutputHeight <= 0)
        {
            return false;
        }

        var relativeX = (double)x - bounds.X;
        var relativeY = (double)y - bounds.Y;
        if (relativeX < 0 || relativeY < 0 || relativeX >= SourceWidth || relativeY >= SourceHeight)
        {
            return false;
        }

        var frameX = Left + relativeX * (IsScaled ? DrawWidth / SourceWidth : 1);
        var frameY = Top + relativeY * (IsScaled ? DrawHeight / SourceHeight : 1);
        if (frameX >= OutputWidth || frameY >= OutputHeight)
        {
            return false;
        }

        point = (frameX / OutputWidth, frameY / OutputHeight);
        return true;
    }
}
