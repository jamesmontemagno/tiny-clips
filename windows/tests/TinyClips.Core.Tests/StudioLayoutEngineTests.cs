using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioLayoutEngineTests
{
    [Theory]
    [InlineData(StudioCanvasAspect.Auto, 1920, 1080)]
    [InlineData(StudioCanvasAspect.Square, 1920, 1920)]
    [InlineData(StudioCanvasAspect.Landscape4X3, 1920, 1440)]
    [InlineData(StudioCanvasAspect.Landscape16X9, 1920, 1080)]
    [InlineData(StudioCanvasAspect.Portrait3X4, 1920, 2560)]
    [InlineData(StudioCanvasAspect.Portrait9X16, 1920, 3414)]
    public void NaturalSize_UsesAspectAndEvenRounding(StudioCanvasAspect aspect, double expectedWidth, double expectedHeight)
    {
        var size = StudioCanvasMath.NaturalSize(Project() with { Canvas = new StudioCanvas { Aspect = aspect } });

        AssertClose(expectedWidth, size.Width);
        AssertClose(expectedHeight, size.Height);
    }

    [Fact]
    public void ExportSize_LimitsLongSideAndRoundsEven()
    {
        var size = StudioCanvasMath.ExportSize(new StudioSize(1920, 1080), 1000);

        AssertClose(1000, size.Width);
        AssertClose(562, size.Height);
    }

    [Fact]
    public void ScreenLayout_FitsScreenInsidePaddedContentArea()
    {
        var frame = StudioLayoutResolver.Resolve(Project() with { Scenes = [new StudioScene { Layout = StudioLayout.Screen }] }, 0, 1000, 800);

        Assert.Equal(StudioLayout.Screen, frame.Layout);
        Assert.Null(frame.Camera);
        Assert.NotNull(frame.Screen);
        AssertClose(48, frame.Screen!.Value.Rect.X);
        AssertClose(145.75, frame.Screen!.Value.Rect.Y);
        AssertClose(904, frame.Screen!.Value.Rect.Width);
        AssertClose(508.5, frame.Screen!.Value.Rect.Height);
    }

    [Fact]
    public void BubbleLayout_PlacesCameraAtAnchoredCornerWithOffsetsAndClamps()
    {
        var project = Project() with
        {
            Scenes =
            [
                new StudioScene
                {
                    Layout = StudioLayout.Bubble,
                    Bubble = new StudioBubble { Anchor = StudioAnchor.TopLeft, Size = 0.25, OffsetX = 1, OffsetY = 1 },
                },
            ],
        };

        var frame = StudioLayoutResolver.Resolve(project, 0, 1000, 800);

        Assert.NotNull(frame.Camera);
        AssertClose(800, frame.Camera!.Value.Rect.X);
        AssertClose(600, frame.Camera!.Value.Rect.Y);
        AssertClose(200, frame.Camera!.Value.Rect.Width);
        AssertClose(200, frame.Camera!.Value.Rect.Height);
        Assert.Equal(StudioCameraShape.Circle, frame.Camera!.Value.Shape);
        AssertClose(100, frame.Camera!.Value.CornerRadius);
    }

    [Theory]
    [InlineData(StudioCameraSide.Trailing, 48, 685.6)]
    [InlineData(StudioCameraSide.Leading, 330.4, 48)]
    public void SideBySideHorizontal_RespectsCameraSide(StudioCameraSide side, double expectedScreenX, double expectedCameraX)
    {
        var project = Project() with
        {
            Scenes = [new StudioScene { Layout = StudioLayout.SideBySide, Split = new StudioSplit { CameraSide = side, CameraFraction = 0.3 } }],
        };

        var frame = StudioLayoutResolver.Resolve(project, 0, 1000, 800);

        Assert.NotNull(frame.Screen);
        Assert.NotNull(frame.Camera);
        AssertClose(expectedScreenX, frame.Screen!.Value.Rect.X);
        AssertClose(expectedCameraX, frame.Camera!.Value.Rect.X);
        AssertClose(621.6, frame.Screen!.Value.Rect.Width);
        AssertClose(266.4, frame.Camera!.Value.Rect.Width);
    }

    [Fact]
    public void SideBySideVertical_StacksCameraAndScreen()
    {
        var project = Project() with
        {
            Scenes = [new StudioScene { Layout = StudioLayout.SideBySide, Split = new StudioSplit { CameraSide = StudioCameraSide.Leading, CameraFraction = 0.25 } }],
        };

        var frame = StudioLayoutResolver.Resolve(project, 0, 800, 1000);

        Assert.NotNull(frame.Screen);
        Assert.NotNull(frame.Camera);
        AssertClose(48, frame.Camera!.Value.Rect.X);
        AssertClose(183, frame.Camera!.Value.Rect.Y);
        AssertClose(704, frame.Camera!.Value.Rect.Width);
        AssertClose(222, frame.Camera!.Value.Rect.Height);
        AssertClose(48, frame.Screen!.Value.Rect.X);
        AssertClose(421, frame.Screen!.Value.Rect.Y);
    }

    [Fact]
    public void CameraLayout_UsesContentAreaAndOmitsScreen()
    {
        var frame = StudioLayoutResolver.Resolve(Project() with { Scenes = [new StudioScene { Layout = StudioLayout.Camera }] }, 0, 1000, 800);

        Assert.Null(frame.Screen);
        Assert.NotNull(frame.Camera);
        Assert.Equal(StudioLayout.Camera, frame.Layout);
        AssertClose(48, frame.Camera!.Value.Rect.X);
        AssertClose(48, frame.Camera!.Value.Rect.Y);
        AssertClose(904, frame.Camera!.Value.Rect.Width);
        AssertClose(704, frame.Camera!.Value.Rect.Height);
        Assert.Equal(StudioCameraShape.RoundedRectangle, frame.Camera!.Value.Shape);
    }

    [Fact]
    public void LayoutWithoutCameraSource_AlwaysResolvesAsScreen()
    {
        var project = Project() with
        {
            Sources = new StudioSources { Screen = new StudioScreenSource { Width = 1920, Height = 1080, Duration = 10 } },
            Scenes = [new StudioScene { Layout = StudioLayout.Camera }],
        };

        var frame = StudioLayoutResolver.Resolve(project, 0, 1000, 800);

        Assert.Equal(StudioLayout.Screen, frame.Layout);
        Assert.NotNull(frame.Screen);
        Assert.Null(frame.Camera);
    }

    [Fact]
    public void SceneSelection_NormalizesStartsSortsAndKeepsLastDuplicate()
    {
        var project = Project() with
        {
            Scenes =
            [
                new StudioScene { Start = 5, Layout = StudioLayout.Camera },
                new StudioScene { Start = -1, Layout = StudioLayout.Screen },
                new StudioScene { Start = 5, Layout = StudioLayout.SideBySide },
            ],
        };

        var early = StudioLayoutResolver.Resolve(project, -1, 1000, 800);
        var later = StudioLayoutResolver.Resolve(project, 5, 1000, 800);

        Assert.Equal(0, early.SceneIndex);
        Assert.Equal(StudioLayout.Screen, early.Layout);
        Assert.Equal(1, later.SceneIndex);
        Assert.Equal(StudioLayout.SideBySide, later.Layout);
    }

    [Fact]
    public void SourceRectanglesAndStylingValuesFollowSpec()
    {
        var project = Project() with
        {
            Screen = new StudioScreenStyle { CornerRadius = 1, Shadow = 0.25, Crop = new StudioRect(0.1, 0.2, 0.5, 0.5) },
            Camera = new StudioCameraStyle
            {
                Shape = StudioCameraShape.RoundedRectangle,
                CornerRadius = 0.25,
                BorderWidth = 1,
                Shadow = 2,
                Crop = new StudioRect(0.2, 0.1, 0.5, 0.5),
            },
            Scenes = [new StudioScene { Layout = StudioLayout.Bubble, Bubble = new StudioBubble { Size = 0.25 } }],
        };

        var frame = StudioLayoutResolver.Resolve(project, 0, 1000, 800);

        Assert.NotNull(frame.Screen);
        Assert.NotNull(frame.Camera);
        AssertRect(new StudioRect(0.1, 0.2, 0.5, 0.5), frame.Screen!.Value.Source);
        AssertClose(160, frame.Screen!.Value.CornerRadius);
        AssertClose(8, frame.Screen!.Value.Shadow.Blur);
        AssertClose(2.4, frame.Screen!.Value.Shadow.OffsetY);
        AssertClose(0.125, frame.Screen!.Value.Shadow.Opacity);
        Assert.Equal(StudioCameraShape.RoundedRectangle, frame.Camera!.Value.Shape);
        AssertClose(50, frame.Camera!.Value.CornerRadius);
        AssertClose(16, frame.Camera!.Value.BorderWidth);
        AssertClose(32, frame.Camera!.Value.Shadow.Blur);
        AssertClose(0.5, frame.Camera!.Value.Shadow.Opacity);
    }

    [Fact]
    public void CameraTiming_UsesStartOffsetAndDuration()
    {
        var project = Project() with
        {
            Sources = new StudioSources
            {
                Screen = new StudioScreenSource { Width = 1920, Height = 1080, Duration = 10 },
                Camera = new StudioCameraSource { Width = 1280, Height = 720, Duration = 3, StartOffset = 2 },
            },
        };

        var before = StudioLayoutResolver.Resolve(project, 1, 1000, 800);
        var during = StudioLayoutResolver.Resolve(project, 4, 1000, 800);
        var after = StudioLayoutResolver.Resolve(project, 6, 1000, 800);

        Assert.False(before.Camera!.Value.Visible);
        AssertClose(0, before.Camera!.Value.SourceTime);
        Assert.True(during.Camera!.Value.Visible);
        AssertClose(2, during.Camera!.Value.SourceTime);
        Assert.False(after.Camera!.Value.Visible);
        AssertClose(3, after.Camera!.Value.SourceTime);
    }

    [Fact]
    public void TimeMap_AppliesTrimCutsMergeAndConversions()
    {
        var map = new StudioTimeMap(
            20,
            new StudioEdits
            {
                TrimStart = 2,
                TrimEnd = 18,
                Cuts =
                [
                    new StudioTimeRange { Start = 5, End = 7 },
                    new StudioTimeRange { Start = 7, End = 8 },
                    new StudioTimeRange { Start = -10, End = 1 },
                    new StudioTimeRange { Start = 19, End = 30 },
                ],
            });

        Assert.Equal([new StudioTimeSegment(2, 5), new StudioTimeSegment(8, 18)], map.Segments);
        AssertClose(13, map.OutputDuration);
        AssertClose(0, map.SourceToOutput(1));
        AssertClose(2, map.SourceToOutput(4));
        AssertClose(3, map.SourceToOutput(6));
        AssertClose(13, map.SourceToOutput(18));
        AssertClose(2, map.OutputToSource(-1));
        AssertClose(4, map.OutputToSource(2));
        AssertClose(8, map.OutputToSource(3));
        AssertClose(18, map.OutputToSource(13));
    }

    [Fact]
    public void TimeMap_NoKeptSegmentsMapsToZeroAndTrimStart()
    {
        var map = new StudioTimeMap(
            10,
            new StudioEdits { TrimStart = 2, TrimEnd = 4, Cuts = [new StudioTimeRange { Start = 2, End = 4 }] });

        Assert.Empty(map.Segments);
        Assert.Empty(map.Pieces);
        AssertClose(0, map.OutputDuration);
        AssertClose(0, map.SourceToOutput(3));
        AssertClose(2, map.OutputToSource(0));
        Assert.Equal(1, map.GetRate(3));
    }

    [Fact]
    public void TimeMap_WithoutSpeed_ThePiecesAreTheKeptSegmentsAtRateOne()
    {
        var map = new StudioTimeMap(
            20,
            new StudioEdits { TrimStart = 2, TrimEnd = 18, Cuts = [new StudioTimeRange { Start = 5, End = 8 }] });

        Assert.Equal([new StudioTimePiece(2, 5, 1), new StudioTimePiece(8, 18, 1)], map.Pieces);
        Assert.Equal(3, map.Pieces[0].OutputDuration);
        Assert.Equal(1, map.GetRate(4));
        Assert.Equal(1, map.GetRate(6));
    }

    [Fact]
    public void TimeMap_Speed_DividesWhatIsKept_AndChangesHowLongItTakes()
    {
        // Four times as fast from 4 to 12, with a cut inside it, and half as fast from 14 to 16.
        var map = new StudioTimeMap(
            20,
            new StudioEdits
            {
                Cuts = [new StudioTimeRange { Start = 6, End = 8 }],
                Speed =
                [
                    new StudioSpeedRange { Start = 14, End = 16, Rate = 0.5 },
                    new StudioSpeedRange { Start = 4, End = 12, Rate = 4 },
                ],
            });

        Assert.Equal([new StudioTimeSegment(0, 6), new StudioTimeSegment(8, 20)], map.Segments);
        Assert.Equal(
            [
                new StudioTimePiece(0, 4, 1),
                new StudioTimePiece(4, 6, 4),
                new StudioTimePiece(8, 12, 4),
                new StudioTimePiece(12, 14, 1),
                new StudioTimePiece(14, 16, 0.5),
                new StudioTimePiece(16, 20, 1),
            ],
            map.Pieces);

        // 4 + 0.5 + 1 + 2 + 4 + 4.
        AssertClose(15.5, map.OutputDuration);
        AssertClose(4.25, map.SourceToOutput(5));
        AssertClose(4.5, map.SourceToOutput(7));
        AssertClose(5, map.SourceToOutput(10));
        AssertClose(9.5, map.SourceToOutput(15));
        AssertClose(5, map.OutputToSource(4.25));
        AssertClose(8, map.OutputToSource(4.5));
        AssertClose(15, map.OutputToSource(9.5));
        AssertClose(20, map.OutputToSource(15.5));

        // The rate at a time: of the piece it is in, and 1 inside a cut and outside everything.
        Assert.Equal(1, map.GetRate(3.999));
        Assert.Equal(4, map.GetRate(4));
        Assert.Equal(1, map.GetRate(7));
        Assert.Equal(4, map.GetRate(11.999));
        Assert.Equal(1, map.GetRate(12));
        Assert.Equal(0.5, map.GetRate(15));
        Assert.Equal(1, map.GetRate(-1));
        Assert.Equal(1, map.GetRate(25));
    }

    [Fact]
    public void TimeMap_Speed_KeepsRatesWithinItsLimits_AndLeavesOutWhatIsNoRate()
    {
        var map = new StudioTimeMap(
            40,
            new StudioEdits
            {
                Speed =
                [
                    new StudioSpeedRange { Start = 2, End = 4, Rate = 100 },
                    new StudioSpeedRange { Start = 6, End = 8, Rate = 0.01 },
                    new StudioSpeedRange { Start = 10, End = 12, Rate = 1 },
                    new StudioSpeedRange { Start = 14, End = 16, Rate = 0 },
                    new StudioSpeedRange { Start = 18, End = 20, Rate = -2 },
                    new StudioSpeedRange { Start = 22, End = 24, Rate = double.NaN },
                    new StudioSpeedRange { Start = 26, End = 28, Rate = double.PositiveInfinity },
                    new StudioSpeedRange { Start = 32, End = 30, Rate = 2 },
                    new StudioSpeedRange { Start = double.NaN, End = 36, Rate = 2 },
                ],
            });

        Assert.Equal(
            [
                new StudioTimePiece(0, 2, 1),
                new StudioTimePiece(2, 4, StudioTimeMap.FastestRate),
                new StudioTimePiece(4, 6, 1),
                new StudioTimePiece(6, 8, StudioTimeMap.SlowestRate),
                new StudioTimePiece(8, 40, 1),
            ],
            map.Pieces);
        Assert.Equal(8, StudioTimeMap.FastestRate);
        Assert.Equal(0.25, StudioTimeMap.SlowestRate);
    }

    private static StudioProject Project() =>
        new()
        {
            Id = "p",
            Sources = new StudioSources
            {
                Screen = new StudioScreenSource { Width = 1920, Height = 1080, Duration = 10 },
                Camera = new StudioCameraSource { Width = 1280, Height = 720, Duration = 10 },
            },
        };

    private static void AssertClose(double expected, double actual) =>
        Assert.True(Math.Abs(expected - actual) <= 1e-6, $"Expected {expected}, actual {actual}.");

    private static void AssertRect(StudioRect expected, StudioFrameRect actual)
    {
        AssertClose(expected.X, actual.X);
        AssertClose(expected.Y, actual.Y);
        AssertClose(expected.Width, actual.Width);
        AssertClose(expected.Height, actual.Height);
    }
}
