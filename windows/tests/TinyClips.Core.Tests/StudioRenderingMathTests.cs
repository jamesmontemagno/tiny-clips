using TinyClips.Core.Models;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Core.Tests;

public sealed class StudioRenderingMathTests
{
    // Frame rates

    [Theory]
    [InlineData(30, 30, 1)]
    [InlineData(60, 60, 1)]
    [InlineData(25, 25, 1)]
    [InlineData(29.97, 30000, 1001)]
    [InlineData(29.97002997, 30000, 1001)]
    [InlineData(59.94, 60000, 1001)]
    [InlineData(23.976, 24000, 1001)]
    [InlineData(12.5, 25, 2)]
    [InlineData(14.999, 15, 1)]
    [InlineData(7.123, 7123, 1000)]
    [InlineData(29.67, 2967, 100)]
    [InlineData(2500000.0 / 186567, 67, 5)]
    public void FrameRate_KeepsTheExactRate(double framesPerSecond, int numerator, int denominator)
    {
        Assert.Equal(new StudioFrameRate(numerator, denominator), StudioRenderingMath.FrameRate(framesPerSecond));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void FrameRate_TreatsAnImpossibleRateAs30(double framesPerSecond)
    {
        Assert.Equal(new StudioFrameRate(30, 1), StudioRenderingMath.FrameRate(framesPerSecond));
    }

    [Fact]
    public void FrameRate_IsLimitedToOneTo240()
    {
        Assert.Equal(new StudioFrameRate(240, 1), StudioRenderingMath.FrameRate(1000));
        Assert.Equal(new StudioFrameRate(1, 1), StudioRenderingMath.FrameRate(0.2));
    }

    [Fact]
    public void FrameTimeTicks_RoundsToTheNearestTickAndDoesNotDrift()
    {
        var thirty = new StudioFrameRate(30, 1);
        Assert.Equal(0, thirty.FrameTimeTicks(0));
        Assert.Equal(333_333, thirty.FrameTimeTicks(1));
        Assert.Equal(666_667, thirty.FrameTimeTicks(2));
        Assert.Equal(1_000_000, thirty.FrameTimeTicks(3));
        Assert.Equal(36_000_000_000, thirty.FrameTimeTicks(108_000));

        var ntsc = new StudioFrameRate(30000, 1001);
        Assert.Equal(333_667, ntsc.FrameTimeTicks(1));
        Assert.Equal(10_010_000_000, ntsc.FrameTimeTicks(30000));

        // A week of frames does not overflow.
        Assert.Equal(6_048_000_000_000, thirty.FrameTimeTicks(30L * 604_800));
    }

    // The frame plan

    [Fact]
    public void BuildFramePlan_WithoutEdits_SamplesTheMiddleOfEveryFrame()
    {
        var frames = StudioRenderingMath.BuildFramePlan(Project(), 30);

        Assert.Equal(300, frames.Count);
        for (var index = 0; index < frames.Count; index++)
        {
            Assert.Equal(index, frames[index].Index);
            AssertClose((index + 0.5) / 30, frames[index].OutputTimeSeconds);
            AssertClose((index + 0.5) / 30, frames[index].SourceTimeSeconds);
        }
    }

    [Theory]
    [InlineData(29.97)]
    [InlineData(60)]
    [InlineData(23.976)]
    [InlineData(12.5)]
    public void BuildFramePlan_Trimmed_UsesMiddleOfOutputFrames(double fps)
    {
        var project = Project() with { Edits = new StudioEdits { TrimStart = 1, TrimEnd = 3 } };

        var frames = StudioRenderingMath.BuildFramePlan(project, fps);

        Assert.Equal((int)Math.Floor((2 * fps) + 1e-9), frames.Count);
        AssertClose(1 + (0.5 / fps), frames[0].SourceTimeSeconds);
        AssertClose(1 + (((frames.Count / 2) + 0.5) / fps), frames[frames.Count / 2].SourceTimeSeconds);
        AssertClose(1 + (((frames.Count - 1) + 0.5) / fps), frames[^1].SourceTimeSeconds);
    }

    [Fact]
    public void BuildFramePlan_TrimOffFrameBoundaries_DropsThePartialLastFrame()
    {
        // 4.615 s is 138.45 frames.
        var project = Project() with { Edits = new StudioEdits { TrimStart = 0.525, TrimEnd = 5.14 } };

        var frames = StudioRenderingMath.BuildFramePlan(project, 30);

        Assert.Equal(138, frames.Count);
        AssertClose(0.525 + (0.5 / 30), frames[0].SourceTimeSeconds);
        AssertClose(0.525 + (137.5 / 30), frames[^1].SourceTimeSeconds);
        Assert.True(frames[^1].SourceTimeSeconds < 5.14);
    }

    [Theory]
    [InlineData(0.1, 2.8, 30, 81)]
    [InlineData(0, 4.004, 30000.0 / 1001, 120)]
    [InlineData(1, 1.1, 30, 3)]
    [InlineData(0.3, 0.3 + (1.0 / 30), 30, 1)]
    [InlineData(2, 2.5, 60, 30)]
    public void BuildFramePlan_AWholeNumberOfFramesIsNotLostToRounding(double start, double end, double fps, int expected)
    {
        var project = Project() with { Edits = new StudioEdits { TrimStart = start, TrimEnd = end } };

        Assert.Equal(expected, StudioRenderingMath.BuildFramePlan(project, fps).Count);
    }

    [Fact]
    public void BuildFramePlan_WithACut_JumpsOverIt()
    {
        // Kept: 0.525–2.31 s (1.785 s) and 3.77–5.14 s (1.37 s). 3.155 s is 94.65 frames.
        var project = Project() with
        {
            Edits = new StudioEdits { TrimStart = 0.525, TrimEnd = 5.14, Cuts = [new StudioTimeRange { Start = 2.31, End = 3.77 }] },
        };

        var frames = StudioRenderingMath.BuildFramePlan(project, 30);

        Assert.Equal(94, frames.Count);
        AssertClose(0.525 + (53.5 / 30), frames[53].SourceTimeSeconds);
        AssertClose(3.77 + ((54.5 / 30) - 1.785), frames[54].SourceTimeSeconds);
    }

    [Theory]
    [InlineData(0, 0, 30)]
    [InlineData(0.525, 5.14, 30)]
    [InlineData(1.0 / 3, 9.99, 29.97)]
    [InlineData(2, 7, 60)]
    public void BuildFramePlan_EveryFrameShowsAMomentThatIsKept(double start, double end, double fps)
    {
        var project = Project() with
        {
            Edits = new StudioEdits
            {
                TrimStart = start,
                TrimEnd = end == 0 ? null : end,
                Cuts = [new StudioTimeRange { Start = 3, End = 3.7 }, new StudioTimeRange { Start = 4.01, End = 4.02 }],
            },
        };
        var map = StudioTimeMap.FromProject(project);

        var frames = StudioRenderingMath.BuildFramePlan(project, fps);

        Assert.Equal((int)Math.Floor((map.OutputDuration * fps) + 1e-9), frames.Count);
        var previous = double.NegativeInfinity;
        foreach (var frame in frames)
        {
            Assert.Contains(map.Segments, segment => frame.SourceTimeSeconds >= segment.Start && frame.SourceTimeSeconds < segment.End);
            Assert.True(frame.SourceTimeSeconds > previous);
            previous = frame.SourceTimeSeconds;
        }
    }

    [Fact]
    public void BuildFramePlan_RejectsTrimShorterThanOneFrame()
    {
        var project = Project() with { Edits = new StudioEdits { TrimStart = 0, TrimEnd = 0.01 } };

        var ex = Assert.Throws<InvalidOperationException>(() => StudioRenderingMath.BuildFramePlan(project, 30));

        Assert.Contains("shorter than one output frame", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFramePlan_RejectsATrimThatLeavesNothing()
    {
        var project = Project() with { Edits = new StudioEdits { TrimStart = 4, TrimEnd = 4 } };

        var ex = Assert.Throws<InvalidOperationException>(() => StudioRenderingMath.BuildFramePlan(project, 30));

        Assert.Contains("Nothing is left", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void BuildFramePlan_RejectsAnImpossibleFrameRate(double fps)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StudioRenderingMath.BuildFramePlan(Project(), fps));
    }

    [Fact]
    public void CheckedFrameCount_RejectsAVideoTooLongToCount()
    {
        Assert.Throws<InvalidOperationException>(() => StudioRenderingMath.CheckedFrameCount(1e9, 60));
    }

    // Camera time

    [Theory]
    [InlineData(0.2, 1.0, 0.8, true)]
    [InlineData(-0.5, 0.1, 0.6, true)]
    [InlineData(2.0, 1.0, -1.0, false)]
    [InlineData(0.0, 3.5, 3.5, false)]
    [InlineData(0.2, 0.2, 0.0, true)]
    [InlineData(0.2, 3.2, 3.0, true)]
    public void CameraTimeMapping_UsesStartOffsetAndVisibility(double offset, double sourceTime, double expectedCameraTime, bool visible)
    {
        var project = WithCameraOffset(offset);

        AssertClose(expectedCameraTime, StudioRenderingMath.CameraSourceTime(project, sourceTime));
        Assert.Equal(visible, StudioRenderingMath.CameraVisible(project, sourceTime));
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(-0.4)]
    [InlineData(2.5)]
    public void CameraTimeMapping_AgreesWithTheLayoutResolver(double offset)
    {
        var project = WithCameraOffset(offset);
        for (var step = -40; step <= 400; step++)
        {
            var time = step / 30.0;
            var camera = StudioLayoutResolver.Resolve(project, time, 1920, 1080).Camera!.Value;

            Assert.Equal(camera.Visible, StudioRenderingMath.CameraVisible(project, time));
            if (camera.Visible)
            {
                AssertClose(camera.SourceTime, StudioRenderingMath.CameraSourceTime(project, time));
            }
        }
    }

    [Fact]
    public void CameraVisible_IsFalseWithoutACamera()
    {
        var project = Project() with { Sources = new StudioSources { Screen = Project().Sources.Screen } };

        Assert.False(StudioRenderingMath.CameraVisible(project, 1));
        AssertClose(1, StudioRenderingMath.CameraSourceTime(project, 1));
    }

    // Sizes and bitrates

    [Theory]
    [InlineData(1921, 1081, 1000, 1000, 562)]
    [InlineData(2560, 1440, 0, 2560, 1440)]
    [InlineData(9, 16, 15, 8, 16)]
    [InlineData(2560, 4552, 3840, 2160, 3840)]
    [InlineData(1920, 1080, 3840, 1920, 1080)]
    public void ExportSize_ReturnsEvenNumbers(double width, double height, double limit, double expectedWidth, double expectedHeight)
    {
        var size = StudioCanvasMath.ExportSize(new StudioSize(width, height), limit);

        AssertClose(expectedWidth, size.Width);
        AssertClose(expectedHeight, size.Height);
        Assert.Equal(0, (int)size.Width % 2);
        Assert.Equal(0, (int)size.Height % 2);
    }

    [Theory]
    [InlineData(1920, 1080, 30, VideoCodec.H264, 6_220_800u)]
    [InlineData(2560, 1440, 30, VideoCodec.H264, 11_059_200u)]
    [InlineData(3840, 2160, 60, VideoCodec.H264, 24_000_000u)]
    [InlineData(640, 360, 30, VideoCodec.H264, 2_000_000u)]
    [InlineData(1920, 1080, 30, VideoCodec.Hevc, 3_732_480u)]
    public void VideoBitrate_FollowsTheRecordersRule(int width, int height, double fps, VideoCodec codec, uint expected)
    {
        Assert.Equal(expected, StudioRenderingMath.VideoBitrate(width, height, fps, codec));
    }

    [Theory]
    [InlineData(96, true)]
    [InlineData(360, true)]
    [InlineData(576, true)]
    [InlineData(577, false)]
    [InlineData(720, false)]
    [InlineData(2160, false)]
    public void EncodingMatrix_IsBt601UpTo576LinesAndBt709Above(int height, bool bt601)
    {
        var expected = bt601 ? Vortice.MediaFoundation.VideoTransferMatrix.Bt601 : Vortice.MediaFoundation.VideoTransferMatrix.Bt709;

        Assert.Equal(expected, StudioMediaFoundation.EncodingMatrix(height));
    }

    // Click rings

    [Theory]
    [InlineData(0, 20, 3, 0.85)]
    [InlineData(0.225, 31.6, 3, 0.425)]
    [InlineData(0.36, 38.56, 3, 0.17)]
    public void ClickRingGeometry_FollowsSpec(double elapsed, double expectedRadius, double expectedStroke, double expectedAlpha)
    {
        var ok = StudioRenderingMath.TryComputeClickRing(Click(0.5, 0.5), 1 + elapsed, Events(), Project(), Screen(), out var ring);

        Assert.True(ok);
        AssertClose(960, ring.CenterX);
        AssertClose(540, ring.CenterY);
        AssertClose(expectedRadius, ring.Radius);
        AssertClose(expectedStroke, ring.StrokeWidth);
        AssertClose(expectedAlpha, ring.Alpha);
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(0.4501)]
    [InlineData(5)]
    public void ClickRing_IsNotDrawnOutsideItsDuration(double elapsed)
    {
        Assert.False(StudioRenderingMath.TryComputeClickRing(Click(0.5, 0.5), 1 + elapsed, Events(), Project(), Screen(), out _));
    }

    [Fact]
    public void ClickRing_AtTheEndOfItsDurationHasFadedOut()
    {
        Assert.False(StudioRenderingMath.TryComputeClickRing(Click(0.5, 0.5), 1.45, Events(), Project(), Screen(), out var ring));
        Assert.True(ring.Alpha < 1e-9);
    }

    [Fact]
    public void ClickRing_ScalesWithTheCardTheCaptureAndTheCrop()
    {
        // A card half the screen's width: 0.5 canvas pixels per screen pixel.
        var half = new StudioResolvedScreen(new StudioFrameRect(100, 50, 960, 540), new StudioFrameRect(0, 0, 1, 1), 0, default);
        Assert.True(StudioRenderingMath.TryComputeClickRing(Click(0.25, 0.5), 1, Events(), Project(), half, out var ring));
        AssertClose(100 + 240, ring.CenterX);
        AssertClose(50 + 270, ring.CenterY);
        AssertClose(10, ring.Radius);
        AssertClose(1.5, ring.StrokeWidth);

        // A capture at two pixels per point.
        Assert.True(StudioRenderingMath.TryComputeClickRing(Click(0.5, 0.5), 1, Events(scale: 2), Project(), Screen(), out ring));
        AssertClose(40, ring.Radius);
        AssertClose(6, ring.StrokeWidth);

        // A video twice as wide as the capture it was made from.
        Assert.True(StudioRenderingMath.TryComputeClickRing(Click(0.5, 0.5), 1, Events(captureWidth: 960), Project(), Screen(), out ring));
        AssertClose(40, ring.Radius);

        // No capture width, or a scale that is not positive: one pixel per point.
        Assert.True(StudioRenderingMath.TryComputeClickRing(Click(0.5, 0.5), 1, Events(captureWidth: 0), Project(), Screen(), out ring));
        AssertClose(20, ring.Radius);
        Assert.True(StudioRenderingMath.TryComputeClickRing(Click(0.5, 0.5), 1, Events(scale: 0), Project(), Screen(), out ring));
        AssertClose(20, ring.Radius);

        // The middle half of the screen shown on the whole card: twice the scale, measured from the crop's corner.
        var cropped = new StudioResolvedScreen(new StudioFrameRect(0, 0, 1920, 1080), new StudioFrameRect(0.25, 0.25, 0.5, 0.5), 0, default);
        Assert.True(StudioRenderingMath.TryComputeClickRing(Click(0.5, 0.375), 1, Events(), Project(), cropped, out ring));
        AssertClose(960, ring.CenterX);
        AssertClose(270, ring.CenterY);
        AssertClose(40, ring.Radius);
    }

    [Theory]
    [InlineData(0.24, 0.5)]
    [InlineData(0.76, 0.5)]
    [InlineData(0.5, 0.2)]
    [InlineData(0.5, 0.8)]
    public void ClickRing_OutsideTheVisibleSourceIsNotDrawn(double x, double y)
    {
        var cropped = new StudioResolvedScreen(new StudioFrameRect(0, 0, 1920, 1080), new StudioFrameRect(0.25, 0.25, 0.5, 0.5), 0, default);

        Assert.False(StudioRenderingMath.TryComputeClickRing(Click(x, y), 1, Events(), Project(), cropped, out _));
    }

    [Fact]
    public void ClickRing_IsNotDrawnWhenSwitchedOffOrInvisible()
    {
        StudioProject With(StudioClickOverlay clicks) => Project() with { Overlays = new StudioOverlays { Clicks = clicks } };

        Assert.False(StudioRenderingMath.TryComputeClickRing(Click(0.5, 0.5), 1, Events(), With(new StudioClickOverlay { Enabled = false }), Screen(), out _));
        Assert.False(StudioRenderingMath.TryComputeClickRing(Click(0.5, 0.5), 1, Events(), With(new StudioClickOverlay { Duration = 0 }), Screen(), out _));
        Assert.False(StudioRenderingMath.TryComputeClickRing(Click(0.5, 0.5), 1, Events(), With(new StudioClickOverlay { Opacity = 0 }), Screen(), out _));
        Assert.False(StudioRenderingMath.TryComputeClickRing(Click(0.5, 0.5), 1, Events(), With(new StudioClickOverlay { StrokeWidth = 0 }), Screen(), out _));

        // Opacity above 1 is 1.
        Assert.True(StudioRenderingMath.TryComputeClickRing(Click(0.5, 0.5), 1, Events(), With(new StudioClickOverlay { Opacity = 4 }), Screen(), out var ring));
        AssertClose(1, ring.Alpha);
    }

    // Small things

    [Fact]
    public void GradientEndpoints_AreTopLeftToBottomRight()
    {
        Assert.Equal((0, 0, 1920, 1080), StudioRenderingMath.GradientEndpoints(1920, 1080));
    }

    [Fact]
    public void TemporaryOutputPath_StaysNextToOutputAndUsesUnscannedExtension()
    {
        var temp = StudioRenderingMath.TemporaryOutputPath(@"C:\Exports\clip.mp4");

        Assert.Equal(@"C:\Exports", Path.GetDirectoryName(temp));
        Assert.StartsWith(".clip.", Path.GetFileName(temp), StringComparison.Ordinal);
        Assert.EndsWith(StudioRenderingMath.TemporaryExtension, temp, StringComparison.Ordinal);
        foreach (var scanned in new[] { ".png", ".jpg", ".jpeg", ".webp", ".mp4", ".gif" })
        {
            Assert.False(temp.EndsWith(scanned, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void TemporaryOutputPath_IsDifferentEveryTimeAndWorksForAPoster()
    {
        var first = StudioRenderingMath.TemporaryOutputPath(@"C:\Projects\abc\poster.jpg");
        var second = StudioRenderingMath.TemporaryOutputPath(@"C:\Projects\abc\poster.jpg");

        Assert.NotEqual(first, second);
        Assert.Equal(@"C:\Projects\abc", Path.GetDirectoryName(first));
        Assert.StartsWith(".poster.", Path.GetFileName(first), StringComparison.Ordinal);
        Assert.Equal(".", Path.GetDirectoryName(StudioRenderingMath.TemporaryOutputPath("clip.mp4")));
    }

    [Theory]
    [InlineData(1.0, 48000, 48000)]
    [InlineData(0.525, 48000, 25200)]
    [InlineData(1.0 / 3, 48000, 16000)]
    [InlineData(0.00001, 48000, 0)]
    [InlineData(0.0000105, 48000, 1)]
    [InlineData(2.5, 44100, 110250)]
    public void SecondsToSamples_RoundsToTheNearestSample(double seconds, int rate, long expected)
    {
        Assert.Equal(expected, StudioRenderingMath.SecondsToSamples(seconds, rate));
    }

    [Fact]
    public void SecondsToMfTicks_RoundsToTheNearestTick()
    {
        Assert.Equal(10_000_000, StudioRenderingMath.SecondsToMfTicks(1));
        Assert.Equal(166_667, StudioRenderingMath.SecondsToMfTicks(0.5 / 30));
        Assert.Equal(-2_000_000, StudioRenderingMath.SecondsToMfTicks(-0.2));
    }

    private static StudioProject Project() =>
        new()
        {
            Id = "p",
            Sources = new StudioSources
            {
                Screen = new StudioScreenSource { Width = 1920, Height = 1080, Duration = 10, FrameRate = 30 },
                Camera = new StudioCameraSource { Width = 640, Height = 480, Duration = 3, StartOffset = 0.2 },
            },
        };

    private static StudioProject WithCameraOffset(double offset) =>
        Project() with
        {
            Sources = new StudioSources
            {
                Screen = new StudioScreenSource { Width = 1920, Height = 1080, Duration = 10, FrameRate = 30 },
                Camera = new StudioCameraSource { Width = 640, Height = 480, Duration = 3, StartOffset = offset },
            },
        };

    private static StudioEvents Events(double scale = 1, int captureWidth = 1920) =>
        new() { Capture = new StudioCaptureInfo { Width = captureWidth, Height = 1080, Scale = scale } };

    private static StudioClickEvent Click(double x, double y) => new() { T = 1, X = x, Y = y };

    private static StudioResolvedScreen Screen() =>
        new(new StudioFrameRect(0, 0, 1920, 1080), new StudioFrameRect(0, 0, 1, 1), 0, default);

    private static void AssertClose(double expected, double actual) =>
        Assert.True(Math.Abs(expected - actual) <= 1e-6, $"Expected {expected}, actual {actual}.");
}
