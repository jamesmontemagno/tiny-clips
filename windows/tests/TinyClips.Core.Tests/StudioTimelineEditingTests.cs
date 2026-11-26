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
}
