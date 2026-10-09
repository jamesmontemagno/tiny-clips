namespace TinyClips.Core.Editing;

public static class ScreenshotEditorZoomMath
{
    public static double? NativeSizeZoomFactor(double dipScalePerPixel, double rasterizationScale)
    {
        if (!double.IsFinite(dipScalePerPixel)
            || dipScalePerPixel <= 0
            || !double.IsFinite(rasterizationScale)
            || rasterizationScale <= 0)
        {
            return null;
        }

        var zoomFactor = 1.0 / (dipScalePerPixel * rasterizationScale);
        return double.IsFinite(zoomFactor) && zoomFactor > 0 ? zoomFactor : null;
    }
}
