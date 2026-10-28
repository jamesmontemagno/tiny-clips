using Windows.Foundation;

namespace TinyClips.Core.Editing;

public static class ScreenshotBackgroundLayout
{
    /// <summary>Full image bounds, centered in frame pixels; Fill extends beyond the frame clip.</summary>
    public static Rect Create(double imageWidth, double imageHeight, Size frame, BackgroundImagePlacement placement)
    {
        if (!double.IsFinite(imageWidth) || !double.IsFinite(imageHeight) || imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Image dimensions must be positive and finite.");
        if (!double.IsFinite(frame.Width) || !double.IsFinite(frame.Height) || frame.Width <= 0 || frame.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(frame), "Frame dimensions must be positive and finite.");
        if (!Enum.IsDefined(placement)) throw new ArgumentOutOfRangeException(nameof(placement));

        var scale = placement == BackgroundImagePlacement.Fill
            ? Math.Max(frame.Width / imageWidth, frame.Height / imageHeight)
            : Math.Min(frame.Width / imageWidth, frame.Height / imageHeight);
        var width = imageWidth * scale;
        var height = imageHeight * scale;
        if (!double.IsFinite(width) || !double.IsFinite(height))
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Scaled image dimensions are too large.");
        return new Rect((frame.Width - width) / 2, (frame.Height - height) / 2, width, height);
    }
}
