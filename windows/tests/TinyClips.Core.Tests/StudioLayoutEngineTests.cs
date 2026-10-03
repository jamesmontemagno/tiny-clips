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
        AssertClose(0, map.OutputDuration);
        AssertClose(0, map.SourceToOutput(3));
        AssertClose(2, map.OutputToSource(0));
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
