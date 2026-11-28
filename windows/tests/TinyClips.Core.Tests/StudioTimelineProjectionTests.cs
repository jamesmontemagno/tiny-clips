using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Core.Tests;

public sealed class StudioTimelineProjectionTests
{
    [Fact]
    public void PresentClipPayloadUsesV1DefaultsWhileMissingPayloadInheritsRoot()
    {
        var project = StudioProjectJson.ReadProject("""
            {
              "schemaVersion": 2, "id": "p",
              "sources": { "screen": { "width": 100, "height": 100, "duration": 2 } },
              "audio": { "muted": true, "volume": 0.2 },
              "mediaSources": [{ "id": "s", "kind": "flatVideo", "sources": {
                "screen": { "file": "clip.mp4", "width": 100, "height": 100, "duration": 2 }
              }}],
              "timeline": { "clips": [
                { "id": "a", "sourceId": "s", "sourceStart": 0, "sourceEnd": 1,
                  "audio": { "microphoneVolume": 0.75 },
                  "scenes": [{ "start": 0, "layout": "screen" }],
                  "captions": { "cues": [{ "id": "c", "start": 0, "end": 1, "text": "Caption" }] },
                  "overlays": { "captions": { "enabled": true } }
                },
                { "id": "b", "sourceId": "s", "sourceStart": 1, "sourceEnd": 2 }
              ]}
            }
            """);
        var a = StudioTimelineProjection.ProjectForClip(project, "a");
        Assert.False(a.Audio.Muted);
        Assert.Equal(1, a.Audio.Volume);
        Assert.Equal(0.75, a.Audio.MicrophoneVolume);
        Assert.NotNull(a.Scenes[0].Bubble);
        Assert.Empty(a.Captions.Cues[0].Words);
        Assert.Single(StudioCaptionResolver.Resolve(a));
        Assert.NotNull(a.Overlays.Clicks);
        var b = StudioTimelineProjection.ProjectForClip(project, "b");
        Assert.True(b.Audio.Muted);
        Assert.Equal(0.2, b.Audio.Volume);
    }

    [Fact]
    public void ProjectionKeepsSourceLocalLayersAndIntersectsTrimWithoutChangingRootCanvas()
    {
        var root = Project();
        var clip = root.Timeline!.Clips[1] with
        {
            SourceStart = 1, SourceEnd = 3,
            Edits = new StudioEdits { TrimStart = 0.5, TrimEnd = 2.5 },
            Audio = new StudioAudio { Volume = 0.3 },
            Overlays = new StudioOverlays { Branding = true },
        };
        root = root with { Timeline = root.Timeline with { Clips = [root.Timeline.Clips[0], clip] } };
        var local = StudioTimelineProjection.ProjectForClip(root, clip.Id);

        Assert.Equal(1, local.SchemaVersion);
        Assert.Null(local.Timeline);
        Assert.Null(local.MediaSources);
        Assert.Equal("b.mp4", local.Sources.Screen.File);
        Assert.Null(local.Sources.Camera);
        Assert.Null(local.Sources.Events);
        Assert.Equal(1, local.Edits.TrimStart);
        Assert.Equal(2.5, local.Edits.TrimEnd);
        Assert.Equal(0.3, local.Audio.Volume);
        Assert.True(local.Overlays.Branding);
        Assert.Equal(new StudioSize(1920, 1080), StudioCanvasMath.NaturalSize(root));
        Assert.Equal(60, root.Sources.Screen.FrameRate);
        Assert.Equal(30, local.Sources.Screen.FrameRate);
    }

    [Theory]
    [InlineData(StudioClipTransition.Cut, 8, 1)]
    [InlineData(StudioClipTransition.FadeThroughBlack, 8, 1)]
    [InlineData(StudioClipTransition.FadeThroughWhite, 8, 1)]
    [InlineData(StudioClipTransition.CrossDissolve, 7.5, 2)]
    public void FramePlanUsesProjectDurationAndDecodesTwoLayersOnlyInsideDissolve(StudioClipTransition join, double duration, int layers)
    {
        var project = Project(join);
        var map = StudioTimelineProjection.Map(project);
        Assert.Equal(duration, StudioTimelineProjection.Duration(project));
        Assert.Equal((int)(duration * 60), StudioRenderingMath.BuildFramePlan(project, 60).Count);
        var options = StudioExportPresets.CreateOptions(project, Models.VideoCodec.H264, StudioExportChoice.Default);
        foreach (var quality in options.Qualities)
            Assert.Equal(StudioExportPresets.EstimateBytes(quality.VideoBitrate, duration, true), quality.EstimatedBytes);
        var during = StudioTimelineFrame.At(map, 3.875);
        Assert.Equal(layers, during.Layers.Length);
        Assert.Single(StudioTimelineFrame.At(map, 0).Layers);
        Assert.Single(StudioTimelineFrame.At(map, 4.5).Layers);
        if (join == StudioClipTransition.CrossDissolve)
        {
            Assert.Equal(0.25, during.Layers[0].Opacity);
            Assert.Equal(0.75, during.Layers[1].Opacity);
            Assert.Equal(3.875, during.Layers[0].SourceTime);
            Assert.Equal(0.375, during.Layers[1].SourceTime);
        }
        else if (join != StudioClipTransition.Cut)
        {
            Assert.Equal(0.5, during.ColorOpacity);
            Assert.Equal(1, StudioTimelineFrame.At(map, 4).ColorOpacity);
            Assert.Equal(0.5, StudioTimelineFrame.At(map, 4.125).ColorOpacity);
            Assert.Equal(join == StudioClipTransition.FadeThroughWhite, during.White);
        }
    }

    [Fact]
    public void RegistryOwnedPathsCannotEscapeProjectFolder()
    {
        var project = Project();
        var folder = Path.Combine(Path.GetTempPath(), "studio-projection");
        var paths = new StudioProjectPaths("p", folder, "", "", null, "", "");
        Assert.Equal(Path.Combine(folder, "b.mp4"), StudioTimelineProjection.PathsForClip(project, paths, "b").ScreenPath);
        var source = project.MediaSources![1];
        project = project with
        {
            MediaSources = [project.MediaSources[0], source with
            {
                Sources = source.Sources with { Screen = source.Sources.Screen with { File = @"..\outside.mp4" } },
            }],
        };
        Assert.Throws<StudioProjectInvalidException>(() => StudioTimelineProjection.PathsForClip(project, paths, "b"));
        project = project with
        {
            MediaSources = [project.MediaSources[0], source with
            {
                Sources = source.Sources with { Screen = source.Sources.Screen with { External = true } },
            }],
        };
        Assert.Throws<StudioProjectInvalidException>(() => StudioTimelineProjection.PathsForClip(project, paths, "b"));
    }

    private static StudioProject Project(StudioClipTransition transition = StudioClipTransition.Cut)
    {
        var root = StudioTimelineEditing.ToTimelineProject(new StudioProject
        {
            Id = "p",
            Sources = new StudioSources
            {
                Screen = new StudioScreenSource { File = "a.mp4", Width = 1920, Height = 1080, FrameRate = 60, Duration = 4 },
                Camera = new StudioCameraSource { File = "camera.mp4", Duration = 4, StartOffset = 0.2 },
                Events = "a-events.json",
            },
        });
        return root with
        {
            MediaSources = [root.MediaSources![0], new StudioMediaSource
            {
                Id = "source-b", Kind = StudioMediaSourceKind.FlatVideo,
                Sources = new StudioSources { Screen = new StudioScreenSource { File = "b.mp4", Width = 720, Height = 1280, Duration = 4 } },
            }],
            Timeline = root.Timeline! with
            {
                Clips = [root.Timeline.Clips[0], new StudioTimelineClip { Id = "b", SourceId = "source-b", SourceEnd = 4, TransitionIn = transition }],
            },
        };
    }
}
