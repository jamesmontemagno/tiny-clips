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

    // Scenes from the recording (section 9.1 of the format). The shared fixtures hold the cases;
    // these are the ones a fixture cannot hold.

    [Fact]
    public void BuildScenes_AChangeExactlyAShortestSceneAfterTheLast_GetsItsOwnScene()
    {
        var first = new StudioScene { Bubble = new StudioBubble { Anchor = StudioAnchor.BottomRight } };
        var shortest = StudioRecordingBuilder.ShortestRecordedScene;
        Assert.Equal(StudioEditorModel.MinimumSceneDuration, shortest);
        Assert.Equal(0.3, shortest);

        var scenes = StudioRecordingBuilder.BuildScenes(
            first,
            [Corner(0, StudioAnchor.BottomRight), Corner(5, StudioAnchor.TopLeft), Corner(5 + shortest, StudioAnchor.TopRight)],
            null,
            20);

        Assert.Equal(new[] { 0, 5, 5 + shortest }, scenes.Select(scene => scene.Start));
        Assert.Equal(
            new[] { StudioAnchor.BottomRight, StudioAnchor.TopLeft, StudioAnchor.TopRight },
            scenes.Select(scene => scene.Bubble.Anchor));

        // And a change exactly that long after the start of the recording.
        scenes = StudioRecordingBuilder.BuildScenes(first, [Corner(shortest, StudioAnchor.TopLeft)], null, 20);
        Assert.Equal(new[] { 0, shortest }, scenes.Select(scene => scene.Start));
        Assert.Equal(StudioAnchor.BottomRight, scenes[0].Bubble.Anchor);
    }

    [Fact]
    public void BuildScenes_AChangeExactlyAShortestSceneBeforeTheEnd_IsKept()
    {
        var first = new StudioScene { Bubble = new StudioBubble { Anchor = StudioAnchor.BottomRight } };
        var shortest = StudioRecordingBuilder.ShortestRecordedScene;

        var scenes = StudioRecordingBuilder.BuildScenes(first, [Corner(20 - shortest, StudioAnchor.TopLeft)], null, 20);
        Assert.Equal(new[] { 0, 20 - shortest }, scenes.Select(scene => scene.Start));

        // The next number after it is too late.
        scenes = StudioRecordingBuilder.BuildScenes(first, [Corner(Math.BitIncrement(20 - shortest), StudioAnchor.TopLeft)], null, 20);
        Assert.Single(scenes);
        Assert.Equal(StudioAnchor.BottomRight, scenes[0].Bubble.Anchor);
    }

    [Fact]
    public void BuildScenes_LeavesOutTimesThatAreNoNumbers_AndTakesMissingLists()
    {
        var first = new StudioScene
        {
            Start = 4,
            Layout = StudioLayout.Bubble,
            Bubble = new StudioBubble { Anchor = StudioAnchor.BottomRight },
            Transition = new StudioTransition { Kind = StudioTransitionKind.Morph, Duration = 1 },
        };

        var scenes = StudioRecordingBuilder.BuildScenes(
            first,
            [
                Corner(double.NaN, StudioAnchor.TopLeft),
                Corner(double.PositiveInfinity, StudioAnchor.TopRight),
                Corner(double.NegativeInfinity, StudioAnchor.BottomLeft),
                Corner(6, StudioAnchor.TopRight),
            ],
            [new StudioLayoutMarker(double.NaN, StudioLayout.Camera), new StudioLayoutMarker(10, StudioLayout.SideBySide)],
            20);

        Assert.Equal(new[] { 0.0, 6, 10 }, scenes.Select(scene => scene.Start));
        Assert.Equal(
            new[] { StudioAnchor.BottomRight, StudioAnchor.TopRight, StudioAnchor.TopRight },
            scenes.Select(scene => scene.Bubble.Anchor));
        Assert.Equal(new[] { StudioLayout.Bubble, StudioLayout.Bubble, StudioLayout.SideBySide }, scenes.Select(scene => scene.Layout));

        // The first scene starts at 0 whatever it was given, and is entered as it was given.
        Assert.Equal(StudioTransitionKind.Morph, scenes[0].Transition.Kind);
        Assert.Equal(1, scenes[0].Transition.Duration);
        Assert.Equal(0.35, scenes[1].Transition.Duration);

        // No lists at all.
        var alone = StudioRecordingBuilder.BuildScenes(first, null, null, 20);
        Assert.Equal(0, Assert.Single(alone).Start);
    }

    [Fact]
    public void ANewProject_HasASceneForEachCornerTheCameraWasMovedTo()
    {
        var placements = new[]
        {
            new WebcamPlacementEvent(TimeSpan.Zero, WebcamCornerPosition.TopRight),
            new WebcamPlacementEvent(TimeSpan.FromSeconds(3), WebcamCornerPosition.BottomLeft),
            new WebcamPlacementEvent(TimeSpan.FromSeconds(7.5), WebcamCornerPosition.TopLeft),
        };
        var request = StudioRecordingBuilder.BuildCreationRequest(
            "Clip",
            new StudioRecordingSourceInfo(1920, 1080, 12, 30),
            new StudioCameraSourceInfo(1280, 720, 12, 0.2),
            WebcamCornerPosition.TopRight,
            new MouseClickOverlayStyle("#FF0000", 44, 4, 0.5, 0.6),
            clickVisualsEnabled: true,
            branding: false,
            appVersion: "1.2.3",
            cameraCorners: StudioRecordingBuilder.BuildCameraCornerEvents(placements));

        var project = StudioProjectStore.BuildDefaultProjectForRecording(
            "3f0013cf-ba10-4453-af91-792b7882dae6",
            request,
            DateTimeOffset.UnixEpoch);

        Assert.Equal(new[] { 0, 3, 7.5 }, project.Scenes.Select(scene => scene.Start));
        Assert.Equal(
            new[] { StudioAnchor.TopRight, StudioAnchor.BottomLeft, StudioAnchor.TopLeft },
            project.Scenes.Select(scene => scene.Bubble.Anchor));
        Assert.All(project.Scenes, scene => Assert.Equal(StudioLayout.Bubble, scene.Layout));
        Assert.Equal(
            new[] { StudioTransitionKind.Cut, StudioTransitionKind.Morph, StudioTransitionKind.Morph },
            project.Scenes.Select(scene => scene.Transition.Kind));
        Assert.Equal(0.2, project.Edits.TrimStart);

        // The recording's length is what decides which changes come too late.
        var shorter = StudioProjectStore.BuildDefaultProjectForRecording(
            "3f0013cf-ba10-4453-af91-792b7882dae6",
            request with { Screen = new StudioRecordingSourceInfo(1920, 1080, 7.6, 30) },
            DateTimeOffset.UnixEpoch);
        Assert.Equal(new[] { 0.0, 3 }, shorter.Scenes.Select(scene => scene.Start));

        // Layouts chosen while recording become scenes as well.
        var withLayouts = StudioProjectStore.BuildDefaultProjectForRecording(
            "3f0013cf-ba10-4453-af91-792b7882dae6",
            request with { LayoutMarkers = [new StudioLayoutMarker(5, StudioLayout.Camera)] },
            DateTimeOffset.UnixEpoch);
        Assert.Equal(new[] { 0.0, 3, 5 }, withLayouts.Scenes.Select(scene => scene.Start));
        Assert.Equal(StudioLayout.Camera, withLayouts.Scenes[2].Layout);
        Assert.Equal(StudioAnchor.TopLeft, withLayouts.Scenes[2].Bubble.Anchor);
    }

    [Fact]
    public void ANewProject_WithoutACamera_HasItsOneScreenScene_WhateverWasChanged()
    {
        var request = StudioRecordingBuilder.BuildCreationRequest(
            "Clip",
            new StudioRecordingSourceInfo(1920, 1080, 12, 30),
            null,
            WebcamCornerPosition.TopRight,
            new MouseClickOverlayStyle("#FF0000", 44, 4, 0.5, 0.6),
            clickVisualsEnabled: true,
            branding: false,
            appVersion: "1.2.3",
            cameraCorners: [Corner(0, StudioAnchor.TopRight), Corner(4, StudioAnchor.TopLeft)]);

        var project = StudioProjectStore.BuildDefaultProjectForRecording(
            "3f0013cf-ba10-4453-af91-792b7882dae6",
            request with { LayoutMarkers = [new StudioLayoutMarker(6, StudioLayout.Camera)] },
            DateTimeOffset.UnixEpoch);

        var scene = Assert.Single(project.Scenes);
        Assert.Equal(StudioLayout.Screen, scene.Layout);
        Assert.Equal(0, scene.Start);

        // A request that says nothing about corners gives the one scene, as before.
        var plain = StudioProjectStore.BuildDefaultProjectForRecording(
            "3f0013cf-ba10-4453-af91-792b7882dae6",
            StudioRecordingBuilder.BuildCreationRequest(
                "Clip",
                new StudioRecordingSourceInfo(1920, 1080, 12, 30),
                new StudioCameraSourceInfo(1280, 720, 12, 0),
                WebcamCornerPosition.BottomLeft,
                new MouseClickOverlayStyle("#FF0000", 44, 4, 0.5, 0.6),
                clickVisualsEnabled: true,
                branding: false,
                appVersion: "1.2.3"),
            DateTimeOffset.UnixEpoch);
        Assert.Equal(StudioAnchor.BottomLeft, Assert.Single(plain.Scenes).Bubble.Anchor);
    }

    private static StudioCameraCornerEvent Corner(double t, StudioAnchor corner) => new() { T = t, Corner = corner };
}
