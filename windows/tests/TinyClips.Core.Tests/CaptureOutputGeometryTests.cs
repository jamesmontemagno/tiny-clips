using TinyClips.Core.Capture;

namespace TinyClips.Core.Tests;

public sealed class CaptureOutputGeometryTests
{
    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1600, 900)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public void PhysicalDimensions_AreNotRescaledByDisplayDpi(int physicalWidth, int physicalHeight)
    {
        var result = CaptureOutputGeometry.Calculate(physicalWidth, physicalHeight, null);
        Assert.Equal(new PixelRect(0, 0, physicalWidth, physicalHeight), result.Encoded);
        Assert.False(result.WasClipped);
        Assert.False(result.WasEvenSized);
    }

    [Fact]
    public void OddAndOversizedRegion_IsClippedBeforeEvenCropping()
    {
        var result = CaptureOutputGeometry.Calculate(1921, 1081, new PixelRect(100, 100, 4000, 3000));
        Assert.Equal(new PixelRect(100, 100, 1821, 981), result.Clipped);
        Assert.Equal(new PixelRect(100, 100, 1820, 980), result.Encoded);
        Assert.True(result.WasClipped);
        Assert.True(result.WasEvenSized);
    }

    [Fact]
    public void NegativeOrigin_IntersectsInsteadOfMovingTheRequestedRectangle()
    {
        var result = CaptureOutputGeometry.Calculate(1920, 1080, new PixelRect(-101, -51, 500, 300));
        Assert.Equal(new PixelRect(0, 0, 399, 249), result.Clipped);
        Assert.Equal(new PixelRect(0, 0, 398, 248), result.Encoded);
    }

    [Fact]
    public void IntersectionArithmetic_DoesNotOverflow()
    {
        var result = CaptureOutputGeometry.Calculate(100, 100, new PixelRect(2, 2, int.MaxValue, int.MaxValue));
        Assert.Equal(new PixelRect(2, 2, 98, 98), result.Encoded);
    }

    [Theory]
    [InlineData(100, 0, 10, 10)]
    [InlineData(99, 0, 10, 10)]
    [InlineData(0, 99, 10, 10)]
    [InlineData(-100, 0, 10, 10)]
    [InlineData(0, 0, 1, 1)]
    public void EmptyOrSubTwoPixelIntersection_IsRejectedWithoutInventedPixels(int x, int y, int w, int h) =>
        Assert.Throws<ArgumentException>(() => CaptureOutputGeometry.Calculate(100, 100, new PixelRect(x, y, w, h)));
}
