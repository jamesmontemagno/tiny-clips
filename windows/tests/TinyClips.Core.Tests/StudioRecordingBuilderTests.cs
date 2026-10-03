using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioRecordingBuilderTests
{
    [Fact]
    public void TryNormalizePoint_DropsOutsideCapturedRectangle()
    {
        Assert.True(StudioRecordingBuilder.TryNormalizePoint(150, 75, 100, 50, 200, 100, out var point));
        Assert.Equal(0.25, point.X);
        Assert.Equal(0.25, point.Y);

        Assert.False(StudioRecordingBuilder.TryNormalizePoint(99, 75, 100, 50, 200, 100, out _));
        Assert.False(StudioRecordingBuilder.TryNormalizePoint(300, 75, 100, 50, 200, 100, out _));
    }

    [Fact]
    public void BuildCursorSamples_ThinsSortsAndRemovesConsecutiveDuplicates()
    {
        var samples = new[]
        {
            new StudioPointSample(TimeSpan.FromMilliseconds(50), 25, 25),
            new StudioPointSample(TimeSpan.Zero, 10, 10),
            new StudioPointSample(TimeSpan.FromMilliseconds(5), 20, 20),
            new StudioPointSample(TimeSpan.FromMilliseconds(100), 25, 25),
            new StudioPointSample(TimeSpan.FromMilliseconds(120), 50, 50),
        };

        var cursor = StudioRecordingBuilder.BuildCursorSamples(samples, 0, 0, 100, 100, maxSamplesPerSecond: 20);

        Assert.Collection(
            cursor,
            first =>
            {
                Assert.Equal(0, first.T);
                Assert.Equal(0.1, first.X);
                Assert.Equal(0.1, first.Y);
            },
            second =>
            {
                Assert.Equal(0.05, second.T, 6);
                Assert.Equal(0.25, second.X);
                Assert.Equal(0.25, second.Y);
            },
            third =>
            {
                Assert.Equal(0.12, third.T, 6);
                Assert.Equal(0.5, third.X);
                Assert.Equal(0.5, third.Y);
            });
    }

    [Fact]
    public void BuildClickEvents_NormalizesActiveTimelineClicksOnly()
    {
        var origin = TimeSpan.FromSeconds(10);
        var timeline = RecordingTimeline.FromOrigin(origin);
        var clicks = new[]
        {
            new MouseClickSample(0, 150, 75, MouseClickButton.Right, origin + TimeSpan.FromMilliseconds(250)),
            new MouseClickSample(0, 99, 75, MouseClickButton.Left, origin + TimeSpan.FromMilliseconds(300)),
            new MouseClickSample(0, 150, 75, MouseClickButton.Left, origin - TimeSpan.FromMilliseconds(1)),
        };

        var result = StudioRecordingBuilder.BuildClickEvents(clicks, timeline, 100, 50, 200, 100);

        var click = Assert.Single(result);
        Assert.Equal(0.25, click.T);
        Assert.Equal(0.25, click.X);
        Assert.Equal(0.25, click.Y);
        Assert.Equal(StudioMouseButton.Right, click.Button);
    }

    [Fact]
    public void BuildClickEvents_SubtractsOnlyThePausesBeforeEachClick()
    {
        // Clicks are converted when the recording stops, so each one must be placed by the pauses
        // that came before it rather than by the total paused time.
        var origin = TimeSpan.FromSeconds(10);
        var timeline = RecordingTimeline.FromOrigin(origin);
        timeline.Pause(origin + TimeSpan.FromSeconds(2));
        timeline.Resume(origin + TimeSpan.FromSeconds(5));
        timeline.Pause(origin + TimeSpan.FromSeconds(8));
        timeline.Resume(origin + TimeSpan.FromSeconds(9));
        var clicks = new[]
        {
            new MouseClickSample(0, 110, 60, MouseClickButton.Left, origin + TimeSpan.FromSeconds(1)),
            new MouseClickSample(0, 110, 60, MouseClickButton.Left, origin + TimeSpan.FromSeconds(3)),
            new MouseClickSample(0, 110, 60, MouseClickButton.Left, origin + TimeSpan.FromSeconds(6)),
            new MouseClickSample(0, 110, 60, MouseClickButton.Left, origin + TimeSpan.FromSeconds(8.5)),
            new MouseClickSample(0, 110, 60, MouseClickButton.Left, origin + TimeSpan.FromSeconds(10)),
        };

        var result = StudioRecordingBuilder.BuildClickEvents(clicks, timeline, 100, 50, 200, 100);

        Assert.Equal(new[] { 1.0, 3.0, 6.0 }, result.Select(click => click.T).ToArray());
    }

    [Fact]
    public void TryNormalizeActive_DropsTimestampsInsideAnOpenPause()
    {
        var origin = TimeSpan.FromSeconds(10);
        var timeline = RecordingTimeline.FromOrigin(origin);
        timeline.Pause(origin + TimeSpan.FromSeconds(2));

        Assert.True(timeline.TryNormalizeActive(origin + TimeSpan.FromSeconds(1.5), out var before));
        Assert.Equal(TimeSpan.FromSeconds(1.5), before);
        Assert.False(timeline.TryNormalizeActive(origin + TimeSpan.FromSeconds(2), out _));
        Assert.False(timeline.TryNormalizeActive(origin + TimeSpan.FromSeconds(30), out _));
        Assert.False(timeline.TryNormalizeActive(origin - TimeSpan.FromMilliseconds(1), out _));
    }

    [Theory]
    [InlineData(1280u, 720u, 1280u, 720u)]
    [InlineData(1920u, 1080u, 1920u, 1080u)]
    [InlineData(3840u, 2160u, 1920u, 1080u)]
    [InlineData(640u, 480u, 640u, 480u)]
    [InlineData(1920u, 1440u, 1440u, 1080u)]
    [InlineData(1080u, 1920u, 608u, 1080u)]
    [InlineData(0u, 0u, 1920u, 1080u)]
    public void WebcamFrameSizing_FitsInsideBoundsWithoutUpscalingOrStretching(uint sourceWidth, uint sourceHeight, uint expectedWidth, uint expectedHeight)
    {
        var size = WebcamFrameSizing.FitWithin(sourceWidth, sourceHeight, new Windows.Graphics.Imaging.BitmapSize { Width = 1920, Height = 1080 });

        Assert.Equal(expectedWidth, size.Width);
        Assert.Equal(expectedHeight, size.Height);
    }

    [Fact]
    public void BuildCameraCornerEvents_StartsAtInitialCornerAndRemovesDuplicates()
    {
        var events = new[]
        {
            new WebcamPlacementEvent(TimeSpan.Zero, WebcamCornerPosition.BottomRight),
            new WebcamPlacementEvent(TimeSpan.FromSeconds(1), WebcamCornerPosition.BottomRight),
            new WebcamPlacementEvent(TimeSpan.FromSeconds(2), WebcamCornerPosition.TopLeft),
        };

        var result = StudioRecordingBuilder.BuildCameraCornerEvents(events);

        Assert.Collection(
            result,
            first => Assert.Equal(StudioAnchor.BottomRight, first.Corner),
            second =>
            {
                Assert.Equal(2, second.T);
                Assert.Equal(StudioAnchor.TopLeft, second.Corner);
            });
    }

    [Fact]
    public void BuildCreationRequest_UsesSettingsForOverlaysAndInitialCorner()
    {
        var request = StudioRecordingBuilder.BuildCreationRequest(
            "Clip",
            new StudioRecordingSourceInfo(1920, 1080, 5, 30),
            null,
            WebcamCornerPosition.TopRight,
            new MouseClickOverlayStyle("#FF0000", 44, 4, 0.5, 0.6),
            clickVisualsEnabled: false,
            branding: true,
            appVersion: "1.2.3");

        Assert.Equal("Clip", request.Name);
        Assert.Equal(StudioAnchor.TopRight, request.BubbleAnchor);
        Assert.False(request.ClickOverlay.Enabled);
        Assert.Equal("#FF0000", request.ClickOverlay.Color);
        Assert.True(request.Branding);
        Assert.Equal("1.2.3", request.AppVersion);
        Assert.Null(request.Look);
    }

    [Fact]
    public void BuildCreationRequest_CarriesTheLookIntoTheNewProject()
    {
        var look = new StudioLook(
            new StudioCanvas { Padding = 0.1, Background = new StudioBackground { Style = StudioBackgroundStyle.Solid, Primary = "#101820" } },
            new StudioScreenStyle { CornerRadius = 0.04, Shadow = 0.2 },
            new StudioCameraStyle { Shape = StudioCameraShape.Rectangle, Mirror = false });

        var request = StudioRecordingBuilder.BuildCreationRequest(
            "Clip",
            new StudioRecordingSourceInfo(1920, 1080, 5, 30),
            null,
            WebcamCornerPosition.BottomRight,
            new MouseClickOverlayStyle("#FF0000", 44, 4, 0.5, 0.6),
            clickVisualsEnabled: true,
            branding: false,
            appVersion: "1.2.3",
            look: look);

        Assert.Same(look, request.Look);

        var project = StudioProjectStore.BuildDefaultProjectForRecording(
            "3f0013cf-ba10-4453-af91-792b7882dae6",
            request,
            DateTimeOffset.UnixEpoch);

        Assert.Equal(0.1, project.Canvas.Padding);
        Assert.Equal(StudioBackgroundStyle.Solid, project.Canvas.Background.Style);
        Assert.Equal("#101820", project.Canvas.Background.Primary);
        Assert.Equal(0.04, project.Screen.CornerRadius);
        Assert.Equal(0.2, project.Screen.Shadow);
        Assert.Equal(StudioCameraShape.Rectangle, project.Camera.Shape);
        Assert.False(project.Camera.Mirror);
    }
}
