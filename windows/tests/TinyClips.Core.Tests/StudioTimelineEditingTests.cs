using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioTimelineEditingTests
{
    [Fact]
    public void ToTimelineProject_PreservesV1RecordingAndMapsEditedSourceTime()
    {
        var legacy = StudioProjectJson.ReadProject("""
            {
              "schemaVersion": 1,
              "id": "p",
              "name": "Original",
              "sources": {
                "screen": { "file": "screen.mp4", "width": 1920, "height": 1080, "duration": 10 },
                "camera": { "file": "camera.mp4", "width": 640, "height": 480, "duration": 10 },
                "events": "events.json"
              },
              "edits": { "trimStart": 1, "trimEnd": 9, "cuts": [{ "start": 4, "end": 5 }], "speed": [{ "start": 6, "end": 8, "rate": 2 }] },
              "scenes": [{ "start": 0, "layout": "bubble" }]
            }
            """);

        var upgraded = StudioTimelineEditing.ToTimelineProject(legacy);

        Assert.Equal(1, legacy.SchemaVersion);
        Assert.Null(legacy.MediaSources);
        Assert.Equal(2, upgraded.SchemaVersion);
        var mediaSources = upgraded.MediaSources!;
        Assert.Equal(StudioMediaSourceKind.NativeRecording, Assert.Single(mediaSources).Kind);
        Assert.Equal(legacy.Sources.Camera, mediaSources[0].Sources.Camera);
        Assert.Equal("events.json", mediaSources[0].Sources.Events);
        var clip = Assert.Single(upgraded.Timeline!.Clips);
        Assert.Equal(legacy.Scenes, clip.Scenes);
        Assert.Equal(legacy.Edits, clip.Edits);

        var map = new StudioTimelineMap(upgraded.Timeline, mediaSources);
        Assert.Equal(6, map.Duration, 6);
        Assert.Equal(6, map.SourcePositionAt(4)!.Value.SourceTime, 6);
        Assert.Equal((double?)4.5, map.ProjectTimeFor(clip.Id, 7));

        var restored = StudioProjectJson.ReadProject(StudioProjectJson.WriteProject(upgraded));
        Assert.Equal(upgraded.Timeline!.Clips.Select(entry => entry.Id), restored.Timeline!.Clips.Select(entry => entry.Id));
        Assert.Equal(upgraded.MediaSources!.Select(entry => entry.Id), restored.MediaSources!.Select(entry => entry.Id));
    }

    [Fact]
    public void SplitMoveAndRemove_PreservePerClipLayerState()
    {
        var project = StudioProjectJson.ReadProject("""
            {
              "schemaVersion": 1,
              "id": "p",
              "sources": { "screen": { "width": 1920, "height": 1080, "duration": 10 } },
              "scenes": [{ "start": 0, "layout": "screen" }]
            }
            """);
        var timeline = StudioTimelineEditing.ToTimelineProject(project).Timeline!;
        var sourceClip = Assert.Single(timeline.Clips);
        var split = StudioTimelineEditing.Split(timeline, sourceClip.Id, 5)!;

        Assert.Equal(2, split.Clips.Length);
        Assert.Equal(new[] { 0d, 5d }, split.Clips.Select(clip => clip.SourceStart));
        Assert.Equal(new[] { 5d, 10d }, split.Clips.Select(clip => clip.SourceEnd));
        Assert.Equal(split.Clips[0].Scenes, split.Clips[1].Scenes);
        Assert.NotEqual(split.Clips[0].Id, split.Clips[1].Id);

        var trimmed = StudioTimelineEditing.Trim(timeline, sourceClip.Id, 2, 8)!;
        Assert.Equal(2, trimmed.Clips[0].SourceStart);
        Assert.Equal(8, trimmed.Clips[0].SourceEnd);

        var moved = StudioTimelineEditing.Move(split, split.Clips[0].Id, 1)!;
        Assert.Equal(split.Clips.Reverse().Select(clip => clip.Id), moved.Clips.Select(clip => clip.Id));
        Assert.Single(StudioTimelineEditing.Remove(moved, moved.Clips[0].Id)!.Clips);
    }

    [Fact]
    public void ReadProject_RequiresValidV2TimelineAndPreservesV1WithoutAddingIt()
    {
        var invalid = """
            {
              "schemaVersion": 2,
              "id": "p",
              "sources": { "screen": { "width": 1, "height": 1, "duration": 1 } },
              "mediaSources": [],
              "timeline": { "clips": [] }
            }
            """;
        Assert.Throws<StudioProjectInvalidException>(() => StudioProjectJson.ReadProject(invalid));

        var legacy = StudioProjectJson.ReadProject("""
            {
              "schemaVersion": 1,
              "id": "p",
              "sources": { "screen": { "width": 1, "height": 1, "duration": 1 } }
            }
            """);
        Assert.Null(legacy.MediaSources);
        Assert.Null(legacy.Timeline);
        Assert.Contains("\"schemaVersion\": 1", StudioProjectJson.WriteProject(legacy));
    }

    [Fact]
    public void SharedMultiClipFixtureMapsNativeAndFlatSources()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "StudioFixtures", "timeline", "two-clips-native-flat.json");
        var project = StudioProjectJson.ReadProject(File.ReadAllText(fixture));
        var map = new StudioTimelineMap(project.Timeline!, project.MediaSources!);

        Assert.Equal(2, project.SchemaVersion);
        Assert.Equal(11, map.Duration, 6);
        Assert.Equal(new[] { "native-clip", "flat-clip" }, project.Timeline!.Clips.Select(clip => clip.Id));
        Assert.Equal(90, project.MediaSources!.Single(source => source.Id == "flat-source").Sources.Screen.RotationDegrees);
        Assert.Equal("flat-clip", map.SourcePositionAt(6)!.Value.ClipId);
    }

    [Fact]
    public void EditsRefuseRemovingTheLastClipAndUnplayableRanges()
    {
        var clip = new StudioTimelineClip
        {
            Id = "clip",
            SourceId = "source",
            SourceEnd = 10,
            Edits = new StudioEdits
            {
                Cuts = [new StudioTimeRange { Start = 3, End = 7 }],
            },
        };
        var timeline = new StudioTimeline { Clips = [clip] };

        Assert.Null(StudioTimelineEditing.Remove(timeline, clip.Id));
        Assert.Null(StudioTimelineEditing.Split(timeline, clip.Id, 5));
        Assert.NotNull(StudioTimelineEditing.Split(timeline, clip.Id, 3));
        Assert.Null(StudioTimelineEditing.Trim(timeline, clip.Id, 3, 7));
        Assert.NotNull(StudioTimelineEditing.Split(timeline, clip.Id, 8));
    }

    [Theory]
    [InlineData(StudioClipTransition.Cut, 4, 2, null, null)]
    [InlineData(StudioClipTransition.FadeThroughBlack, 4, 2, 1.75, 2.25)]
    [InlineData(StudioClipTransition.FadeThroughWhite, 4, 2, 1.75, 2.25)]
    [InlineData(StudioClipTransition.CrossDissolve, 3.5, 1.5, 1.5, 2.0)]
    public void JoinMappingMatchesTheProjectClock(
        StudioClipTransition transition, double duration, double incomingStart, double? joinStart, double? joinEnd)
    {
        var sources = new[]
        {
            Source("a", 2),
            Source("b", 2),
        };
        var timeline = new StudioTimeline
        {
            Clips =
            [
                new StudioTimelineClip { Id = "first", SourceId = "a", SourceEnd = 2 },
                new StudioTimelineClip { Id = "second", SourceId = "b", SourceEnd = 2, TransitionIn = transition },
            ],
        };
        var map = new StudioTimelineMap(timeline, sources);

        Assert.Equal(duration, map.Duration, 6);
        Assert.Equal(incomingStart, map.Clips[1].ProjectStart, 6);
        Assert.Equal(joinStart, map.Clips[1].Entry?.Start);
        Assert.Equal(joinEnd, map.Clips[1].Entry?.End);
        Assert.Equal("second", map.SourcePositionAt(incomingStart)!.Value.ClipId);
        if (transition == StudioClipTransition.CrossDissolve)
        {
            Assert.Equal("first", map.SourcePositionAt(incomingStart, "first")!.Value.ClipId);
        }
        Assert.Equal(2, map.SourcePositionAt(duration)!.Value.SourceTime, 6);
    }

    [Fact]
    public void ShortClipsLimitJoinsAndSplitResetsIncomingTransition()
    {
        var timeline = new StudioTimeline
        {
            Clips =
            [
                new StudioTimelineClip { Id = "first", SourceId = "a", SourceEnd = 0.4 },
                new StudioTimelineClip { Id = "second", SourceId = "b", SourceEnd = 0.2, TransitionIn = StudioClipTransition.CrossDissolve },
            ],
        };
        var sources = new[] { Source("a", 0.4), Source("b", 0.2) };
        var map = new StudioTimelineMap(timeline, sources);
        Assert.Equal(0.5, map.Duration, 6);
        Assert.Equal(0.3, map.Clips[1].ProjectStart, 6);

        var split = StudioTimelineEditing.Split(timeline, "second", 0.1)!;
        Assert.Equal(StudioClipTransition.CrossDissolve, split.Clips[1].TransitionIn);
        Assert.Equal(StudioClipTransition.Cut, split.Clips[2].TransitionIn);
    }

    [Fact]
    public void InsertAtPlayheadSplitsOnlyInsideAClip()
    {
        var sources = new[] { Source("a", 10), Source("b", 1) };
        var timeline = new StudioTimeline { Clips = [new StudioTimelineClip { Id = "first", SourceId = "a", SourceEnd = 10 }] };
        var incoming = new StudioTimelineClip { Id = "inserted", SourceId = "b", SourceEnd = 1 };

        var result = StudioTimelineEditing.InsertAtProjectTime(timeline, incoming, 5, sources);
        Assert.Equal(3, result.Clips.Length);
        Assert.Equal(new[] { "first", "inserted" }, result.Clips.Take(2).Select(clip => clip.Id));
        Assert.Equal(5, result.Clips[2].SourceStart);
        Assert.Equal(2, StudioTimelineEditing.InsertAtProjectTime(timeline, incoming, 0, sources).Clips.Length);
        Assert.Equal(2, StudioTimelineEditing.InsertAtProjectTime(timeline, incoming, 10, sources).Clips.Length);
        Assert.Throws<StudioProjectInvalidException>(() => StudioTimelineEditing.InsertAtProjectTime(timeline, incoming, 11, sources));
    }

    [Fact]
    public void ReadAndWriteRejectUnplayableTimelineAndDuplicateIds()
    {
        var project = StudioTimelineEditing.ToTimelineProject(StudioProjectJson.ReadProject("""
            { "id": "p", "sources": { "screen": { "width": 1, "height": 1, "duration": 10 } } }
            """));
        var clip = project.Timeline!.Clips[0];
        var invalid = project with
        {
            Timeline = project.Timeline with
            {
                Clips = [clip with { Edits = new StudioEdits { Cuts = [new StudioTimeRange { Start = 0, End = 10 }] } }],
            },
        };
        Assert.Throws<StudioProjectInvalidException>(() => StudioProjectJson.WriteProject(invalid));
        Assert.Throws<StudioProjectInvalidException>(() => StudioProjectJson.ReadProject(
            StudioProjectJson.WriteProject(project).Replace("\"cuts\": []", "\"cuts\": [{ \"start\": 0, \"end\": 10 }]")));
        Assert.Throws<StudioProjectInvalidException>(() => new StudioTimelineMap(
            project.Timeline with { Clips = [clip, clip] }, project.MediaSources!));
    }

    [Theory]
    [InlineData("crossDissolve", StudioClipTransition.CrossDissolve)]
    [InlineData("fadeThroughBlack", StudioClipTransition.FadeThroughBlack)]
    [InlineData("fadeThroughWhite", StudioClipTransition.FadeThroughWhite)]
    [InlineData("future", StudioClipTransition.Cut)]
    public void TransitionRoundTripsWithUnknownValuesDefaultingToCut(string value, StudioClipTransition expected)
    {
        var project = StudioTimelineEditing.ToTimelineProject(StudioProjectJson.ReadProject("""
            { "id": "p", "sources": { "screen": { "width": 1, "height": 1, "duration": 10 } } }
            """));
        var json = StudioProjectJson.WriteProject(project).Replace("\"sourceStart\": 0", $"\"transitionIn\": \"{value}\", \"sourceStart\": 0");
        var read = StudioProjectJson.ReadProject(json);
        Assert.Equal(expected, read.Timeline!.Clips[0].TransitionIn);
        Assert.Equal(expected, StudioProjectJson.ReadProject(StudioProjectJson.WriteProject(read)).Timeline!.Clips[0].TransitionIn);
    }

    private static StudioMediaSource Source(string id, double duration) => new()
    {
        Id = id,
        Kind = StudioMediaSourceKind.NativeRecording,
        Sources = new StudioSources { Screen = new StudioScreenSource { Width = 1, Height = 1, Duration = duration } },
    };
}
