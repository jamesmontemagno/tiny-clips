using System.Text.Json;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioProjectJsonTests
{
    [Fact]
    public void ReadProject_AppliesDefaultsForMissingOptionalProperties()
    {
        var project = StudioProjectJson.ReadProject("""
            {
              "id": "p",
              "sources": { "screen": { "width": 1920, "height": 1080, "duration": 12.5 } }
            }
            """);

        Assert.Equal(1, project.SchemaVersion);
        Assert.Equal(DateTimeOffset.UnixEpoch, project.CreatedAt);
        Assert.Equal(StudioCanvasAspect.Auto, project.Canvas.Aspect);
        Assert.Equal(0.06, project.Canvas.Padding);
        Assert.Equal(StudioBackgroundStyle.Gradient, project.Canvas.Background.Style);
        Assert.Equal("#2687E8", project.Canvas.Background.Primary);
        Assert.Equal(StudioLayout.Bubble, project.Scenes.Single().Layout);
        Assert.True(project.Overlays.Clicks.Enabled);
        Assert.Null(project.Sources.Camera);
    }

    [Fact]
    public void RoundTrip_PreservesUnknownPropertiesAtRootNestedObjectsAndArrayElements()
    {
        var project = StudioProjectJson.ReadProject("""
            {
              "schemaVersion": 1,
              "id": "p",
              "rootUnknown": { "keep": true },
              "sources": {
                "screen": { "width": 1920, "height": 1080, "duration": 5, "screenUnknown": 42 }
              },
              "scenes": [
                { "layout": "screen", "sceneUnknown": "preserve" }
              ]
            }
            """);

        var json = StudioProjectJson.WriteProject(project);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.GetProperty("rootUnknown").GetProperty("keep").GetBoolean());
        Assert.Equal(42, root.GetProperty("sources").GetProperty("screen").GetProperty("screenUnknown").GetInt32());
        Assert.Equal("preserve", root.GetProperty("scenes")[0].GetProperty("sceneUnknown").GetString());
    }

    [Fact]
    public void ReadProject_UnrecognizedEnumsReadAsFieldDefaults()
    {
        var project = StudioProjectJson.ReadProject("""
            {
              "id": "p",
              "sources": { "screen": { "width": 1, "height": 1, "duration": 1 } },
              "canvas": { "aspect": "future", "background": { "style": "future" } },
              "camera": { "shape": "future", "cutout": "future" },
              "scenes": [
                { "layout": "future", "bubble": { "anchor": "future" }, "split": { "cameraSide": "future" }, "transition": { "kind": "future" } }
              ]
            }
            """);

        Assert.Equal(StudioCanvasAspect.Auto, project.Canvas.Aspect);
        Assert.Equal(StudioBackgroundStyle.Gradient, project.Canvas.Background.Style);
        Assert.Equal(StudioCameraShape.Circle, project.Camera.Shape);
        Assert.Equal(StudioCameraCutout.None, project.Camera.Cutout);
        Assert.Equal(StudioLayout.Bubble, project.Scenes[0].Layout);
        Assert.Equal(StudioAnchor.BottomRight, project.Scenes[0].Bubble.Anchor);
        Assert.Equal(StudioCameraSide.Trailing, project.Scenes[0].Split.CameraSide);
        Assert.Equal(StudioTransitionKind.Cut, project.Scenes[0].Transition.Kind);
    }

    [Fact]
    public void ReadProject_NullOptionalNonNullablePropertiesTakeDefaultsAtEveryLevel()
    {
        var project = StudioProjectJson.ReadProject("""
            {
              "schemaVersion": null,
              "id": "p",
              "name": null,
              "createdAt": null,
              "app": { "platform": null, "version": null },
              "sources": {
                "screen": { "file": null, "width": 1920, "height": 1080, "frameRate": null, "duration": 5, "external": null }
              },
              "canvas": { "aspect": null, "padding": null, "background": { "style": null, "primary": null } },
              "screen": { "cornerRadius": null, "shadow": null },
              "camera": null,
              "scenes": [
                {
                  "layout": null,
                  "bubble": { "anchor": null, "size": null },
                  "split": { "cameraSide": null, "cameraFraction": null },
                  "transition": { "kind": null, "duration": null }
                }
              ],
              "zooms": [ { "scale": null, "focus": { "mode": null, "x": null, "y": null }, "origin": null } ],
              "audio": { "systemVolume": null, "microphoneVolume": null },
              "overlays": { "clicks": { "enabled": null, "color": null, "size": null, "strokeWidth": null, "opacity": null, "duration": null } },
              "exports": [ { "path": null, "exportedAt": null } ]
            }
            """);

        Assert.Equal(1, project.SchemaVersion);
        Assert.Equal(string.Empty, project.Name);
        Assert.Equal(DateTimeOffset.UnixEpoch, project.CreatedAt);
        Assert.Equal("windows", project.App.Platform);
        Assert.Equal(string.Empty, project.App.Version);
        Assert.Equal("screen.mp4", project.Sources.Screen.File);
        Assert.Equal(30, project.Sources.Screen.FrameRate);
        Assert.False(project.Sources.Screen.External);
        Assert.Equal(StudioCanvasAspect.Auto, project.Canvas.Aspect);
        Assert.Equal(0.06, project.Canvas.Padding);
        Assert.Equal(StudioBackgroundStyle.Gradient, project.Canvas.Background.Style);
        Assert.Equal("#2687E8", project.Canvas.Background.Primary);
        Assert.Equal(0.02, project.Screen.CornerRadius);
        Assert.Equal(0.5, project.Screen.Shadow);
        Assert.Equal(StudioCameraShape.Circle, project.Camera.Shape);
        Assert.True(project.Camera.Mirror);
        Assert.Equal(StudioLayout.Bubble, project.Scenes[0].Layout);
        Assert.Equal(StudioAnchor.BottomRight, project.Scenes[0].Bubble.Anchor);
        Assert.Equal(0.24, project.Scenes[0].Bubble.Size);
        Assert.Equal(StudioCameraSide.Trailing, project.Scenes[0].Split.CameraSide);
        Assert.Equal(0.3, project.Scenes[0].Split.CameraFraction);
        Assert.Equal(StudioTransitionKind.Cut, project.Scenes[0].Transition.Kind);
        Assert.Equal(0.35, project.Scenes[0].Transition.Duration);
        Assert.Equal(1, project.Zooms[0].Scale);
        Assert.Equal(StudioZoomFocusMode.Point, project.Zooms[0].Focus.Mode);
        Assert.Equal(0.5, project.Zooms[0].Focus.X);
        Assert.Equal(0.5, project.Zooms[0].Focus.Y);
        Assert.Equal(StudioZoomOrigin.Manual, project.Zooms[0].Origin);
        Assert.Equal(1, project.Audio.SystemVolume);
        Assert.Equal(1, project.Audio.MicrophoneVolume);
        Assert.True(project.Overlays.Clicks.Enabled);
        Assert.Equal("#0A84FF", project.Overlays.Clicks.Color);
        Assert.Equal(40, project.Overlays.Clicks.Size);
        Assert.Equal(3, project.Overlays.Clicks.StrokeWidth);
        Assert.Equal(0.85, project.Overlays.Clicks.Opacity);
        Assert.Equal(0.45, project.Overlays.Clicks.Duration);
        Assert.Equal(string.Empty, project.Exports[0].Path);
        Assert.Equal(DateTimeOffset.UnixEpoch, project.Exports[0].ExportedAt);
    }

    [Fact]
    public void ReadProject_NullAllowedPropertiesRemainNull()
    {
        var project = StudioProjectJson.ReadProject("""
            {
              "id": "p",
              "sources": {
                "screen": { "width": 1920, "height": 1080, "duration": 5 },
                "camera": null,
                "events": null
              },
              "canvas": { "background": { "preset": null, "secondary": null, "image": null } },
              "screen": { "crop": null },
              "edits": { "trimEnd": null }
            }
            """);

        Assert.Null(project.Sources.Camera);
        Assert.Null(project.Sources.Events);
        Assert.Null(project.Canvas.Background.Preset);
        Assert.Null(project.Canvas.Background.Secondary);
        Assert.Null(project.Canvas.Background.Image);
        Assert.Null(project.Screen.Crop);
        Assert.Null(project.Edits.TrimEnd);
    }

    [Theory]
    [InlineData("""{ "sources": { "screen": { "width": 1, "height": 1, "duration": 1 } } }""")]
    [InlineData("""{ "id": "p", "sources": { "screen": { "height": 1, "duration": 1 } } }""")]
    [InlineData("""{ "id": "p", "sources": { "screen": { "width": 1, "height": 1 } } }""")]
    [InlineData("""{ "id": "p", "sources": { "screen": { "width": 1, "height": 1, "duration": 1 }, "camera": { "width": 1, "height": 1 } } }""")]
    [InlineData("""{ "id": null, "sources": { "screen": { "width": 1, "height": 1, "duration": 1 } } }""")]
    [InlineData("""{ "id": "p", "sources": { "screen": { "width": null, "height": 1, "duration": 1 } } }""")]
    [InlineData("""{ "id": "p", "sources": { "screen": { "width": 1, "height": 1, "duration": 1 }, "camera": { "width": 1, "height": 1, "duration": null } } }""")]
    public void ReadProject_MissingRequiredPropertiesThrowsInvalidException(string json)
    {
        Assert.Throws<StudioProjectInvalidException>(() => StudioProjectJson.ReadProject(json));
    }

    [Fact]
    public void ReadProject_RefusesNewerSchemaVersion()
    {
        var ex = Assert.Throws<StudioUnsupportedSchemaVersionException>(() => StudioProjectJson.ReadProject("""
            {
              "schemaVersion": 2,
              "id": "p",
              "sources": { "screen": { "width": 1, "height": 1, "duration": 1 } }
            }
            """));

        Assert.Equal(2, ex.SchemaVersion);
    }

    [Fact]
    public void WriteProject_UsesWholeSecondUtcTimestampsAndReadAcceptsFractions()
    {
        var project = StudioProjectJson.ReadProject("""
            {
              "id": "p",
              "createdAt": "2026-10-02T22:41:00.987Z",
              "sources": { "screen": { "width": 1, "height": 1, "duration": 1 } }
            }
            """);

        var json = StudioProjectJson.WriteProject(project);

        Assert.Contains("\"createdAt\": \"2026-10-02T22:41:00Z\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTrip_PreservesOutOfRangeValues()
    {
        var project = StudioProjectJson.ReadProject("""
            {
              "id": "p",
              "sources": { "screen": { "width": 1, "height": 1, "duration": 1 } },
              "canvas": { "padding": 99 },
              "screen": { "cornerRadius": -3 },
              "scenes": [ { "bubble": { "size": 42 } } ]
            }
            """);

        var roundTripped = StudioProjectJson.ReadProject(StudioProjectJson.WriteProject(project));

        Assert.Equal(99, roundTripped.Canvas.Padding);
        Assert.Equal(-3, roundTripped.Screen.CornerRadius);
        Assert.Equal(42, roundTripped.Scenes[0].Bubble.Size);
    }
}
