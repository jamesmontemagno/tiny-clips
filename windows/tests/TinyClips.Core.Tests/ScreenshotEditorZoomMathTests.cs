using TinyClips.Core.Editing;

namespace TinyClips.Core.Tests;

public sealed class ScreenshotEditorZoomMathTests
{
    [Theory]
    [InlineData(0.25, 2.0, 2.0)]
    [InlineData(0.5, 2.0, 1.0)]
    [InlineData(1.0, 1.5, 2.0 / 3.0)]
    public void NativeSizeZoomFactor_AccountsForRasterizationScale(
        double dipScalePerPixel,
        double rasterizationScale,
        double expected)
    {
        var zoomFactor = ScreenshotEditorZoomMath.NativeSizeZoomFactor(dipScalePerPixel, rasterizationScale);

        Assert.Equal(expected, zoomFactor!.Value, precision: 10);
    }

    [Fact]
    public void NativeSizeZoomFactor_ReturnsNullForInvalidOrUnrepresentableScale()
    {
        Assert.Null(ScreenshotEditorZoomMath.NativeSizeZoomFactor(0, 2));
        Assert.Null(ScreenshotEditorZoomMath.NativeSizeZoomFactor(0.5, double.NaN));
        Assert.Null(ScreenshotEditorZoomMath.NativeSizeZoomFactor(double.Epsilon, 1));
    }
}
