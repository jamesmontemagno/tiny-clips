using TinyClips.Core.Automation;

namespace TinyClips.Core.Tests;

public class AutomationMediaRangeTests
{
    private static readonly double[] Delays = [0.1, 0.1, 0.2, 0.1];

    [Fact]
    public void Clamp_LimitsEndToDuration()
    {
        Assert.Equal((1.0, 5.0), AutomationMediaRange.Clamp(1, 9, 5));
        Assert.Equal((0.5, 2.0), AutomationMediaRange.Clamp(0.5, 2, 5));
    }

    [Theory]
    [InlineData(-1, 2, 5)]
    [InlineData(2, 2, 5)]
    [InlineData(3, 1, 5)]
    [InlineData(5, 6, 5)]
    [InlineData(double.NaN, 1, 5)]
    public void Clamp_RejectsInvalidRanges(double start, double end, double duration)
    {
        var ex = Assert.Throws<AutomationException>(() => AutomationMediaRange.Clamp(start, end, duration));
        Assert.Equal(AutomationErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public void GifFrames_CoverFramesOverlappingTheRange()
    {
        Assert.Equal(0.5, AutomationMediaRange.Duration(Delays), 6);
        Assert.Equal((0, 3), AutomationMediaRange.GifFrames(0, 0.5, Delays));
        Assert.Equal((1, 2), AutomationMediaRange.GifFrames(0.1, 0.4, Delays));
        Assert.Equal((2, 2), AutomationMediaRange.GifFrames(0.25, 0.3, Delays));
    }

    [Fact]
    public void GifFrameAt_FindsFrameAndRejectsOutOfRange()
    {
        Assert.Equal(0, AutomationMediaRange.GifFrameAt(0, Delays));
        Assert.Equal(2, AutomationMediaRange.GifFrameAt(0.35, Delays));
        Assert.Equal(3, AutomationMediaRange.GifFrameAt(0.45, Delays));
        Assert.Equal(0.2, AutomationMediaRange.StartTime(2, Delays), 6);
        var ex = Assert.Throws<AutomationException>(() => AutomationMediaRange.GifFrameAt(0.5, Delays));
        Assert.Equal(AutomationErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public void FractionalGifBoundariesSelectExactlyOverlappingFrames()
    {
        var delays = Enumerable.Repeat(0.1, 10).ToArray();
        Assert.Equal(1.0, AutomationMediaRange.Duration(delays));
        Assert.Equal(3, AutomationMediaRange.GifFrameAt(0.3, delays));
        Assert.Equal(2, AutomationMediaRange.GifFrameAt(0.3 - 0.000001, delays));
        Assert.Equal(3, AutomationMediaRange.GifFrameAt(0.3 + 0.000001, delays));
        Assert.Equal((0, 7), AutomationMediaRange.GifFrames(0, 0.8, delays));
        Assert.Equal((3, 7), AutomationMediaRange.GifFrames(0.3, 0.8, delays));
        Assert.Equal((0, 8), AutomationMediaRange.GifFrames(0, 0.8 + 0.000001, delays));
        Assert.Throws<AutomationException>(() => AutomationMediaRange.GifFrameAt(1.0, delays));
    }

    [Fact]
    public void CentisecondBoundaryDoesNotDriftAcrossManyFrames()
    {
        var delays = Enumerable.Repeat(0.01, 1000).ToArray();
        Assert.Equal(10.0, AutomationMediaRange.Duration(delays));
        Assert.Equal(29, AutomationMediaRange.GifFrameAt(0.29, delays));
        Assert.Equal((0, 28), AutomationMediaRange.GifFrames(0, 0.29, delays));
    }
}
