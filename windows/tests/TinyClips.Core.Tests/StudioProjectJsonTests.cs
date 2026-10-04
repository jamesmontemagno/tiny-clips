using System.Text.Json;
using System.Text.Json.Nodes;
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
        Assert.Equal(2, project.Zooms[0].Scale);
        Assert.Equal(0.5, project.Zooms[0].EaseIn);
        Assert.Equal(0.5, project.Zooms[0].EaseOut);
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

    [Theory]
    [InlineData("""{ "id": "p", "sources": { "screen": { "width": 0, "height": 1, "duration": 1 } } }""")]
    [InlineData("""{ "id": "p", "sources": { "screen": { "width": 1.5, "height": 1, "duration": 1 } } }""")]
    [InlineData("""{ "id": "p", "sources": { "screen": { "width": 1, "height": 1, "duration": -0.1 } } }""")]
    [InlineData("""{ "id": "p", "sources": { "screen": { "width": 1, "height": 1, "duration": 1 }, "camera": { "width": 0, "height": 1, "duration": 1 } } }""")]
    [InlineData("""{ "id": "p", "sources": { "screen": { "width": 1, "height": 1, "duration": 1 }, "camera": { "width": 1, "height": 1, "duration": -1 } } }""")]
    public void ReadProject_InvalidRequiredDimensionsAndDurationsThrowInvalidException(string json)
    {
        Assert.Throws<StudioProjectInvalidException>(() => StudioProjectJson.ReadProject(json));
    }

    [Theory]
    [InlineData("""{ "id": "p", "sources": { "screen": { "file": "..\\screen.mp4", "width": 1, "height": 1, "duration": 1 } } }""")]
    [InlineData("""{ "id": "p", "sources": { "screen": { "file": "C:\\videos\\screen.mp4", "width": 1, "height": 1, "duration": 1 } } }""")]
    [InlineData("""{ "id": "p", "sources": { "screen": { "width": 1, "height": 1, "duration": 1 }, "camera": { "file": "camera\\track.mp4", "width": 1, "height": 1, "duration": 1 } } }""")]
    [InlineData("""{ "id": "p", "sources": { "screen": { "width": 1, "height": 1, "duration": 1 }, "events": "events\\bad.json" } }""")]
    [InlineData("""{ "id": "p", "sources": { "screen": { "file": "..", "width": 1, "height": 1, "duration": 1 } } }""")]
    [InlineData("""{ "id": "p", "sources": { "screen": { "width": 1, "height": 1, "duration": 1 }, "events": "." } }""")]
    public void ReadProject_SourceFileNamesWithDirectoriesThrowInvalidException(string json)
    {
        Assert.Throws<StudioProjectInvalidException>(() => StudioProjectJson.ReadProject(json));
    }

    [Fact]
    public void ReadProject_AllowsExternalScreenPathAndTreatsBackgroundImageWithDirectoryAsMissing()
    {
        var project = StudioProjectJson.ReadProject("""
            {
              "id": "p",
              "sources": { "screen": { "file": "C:\\videos\\screen.mp4", "width": 1, "height": 1, "duration": 1, "external": true } },
              "canvas": { "background": { "style": "image", "image": "folder\\background.png" } }
            }
            """);

        Assert.True(project.Sources.Screen.External);
        Assert.Equal("C:\\videos\\screen.mp4", project.Sources.Screen.File);
        Assert.Null(project.Canvas.Background.Image);
    }

    [Fact]
    public void ReadProject_DropsNullArrayElements()
    {
        var project = StudioProjectJson.ReadProject("""
            {
              "id": "p",
              "sources": { "screen": { "width": 1920, "height": 1080, "duration": 5 } },
              "scenes": [ null, { "layout": "screen" } ],
              "zooms": [ null, { "start": 1 } ],
              "exports": [ null, { "path": "C:\\out.mp4" } ],
              "edits": {
                "cuts": [ null, { "start": 1, "end": 2 } ],
                "speed": [ null, { "start": 1, "end": 2, "rate": 2 } ]
              }
            }
            """);

        Assert.Single(project.Scenes);
        Assert.Single(project.Zooms);
        Assert.Single(project.Exports);
        Assert.Single(project.Edits.Cuts);
        Assert.Single(project.Edits.Speed);
        Assert.Equal(1, project.Edits.Cuts[0].Start);
        Assert.Equal(1, project.Zooms[0].Start);
    }

    [Fact]
    public void ReadEvents_AppliesDefaultsAndDropsNullArrayElements()
    {
        var events = StudioProjectJson.ReadEvents("""
            {
              "schemaVersion": null,
              "capture": { "width": null, "height": null, "scale": null, "kind": null },
              "clicks": [ null, { "t": 1, "x": 0.2, "y": 0.3, "button": null } ],
              "cursor": [ null, { "t": 2, "x": 0.4, "y": 0.5 } ],
              "cameraCorners": [ null, { "t": 3, "corner": null } ],
              "markers": [ null, { "name": "m" } ]
            }
            """);

        Assert.Equal(1, events.SchemaVersion);
        Assert.Equal(0, events.Capture.Width);
        Assert.Equal(0, events.Capture.Height);
        Assert.Equal(1, events.Capture.Scale);
        Assert.Equal(StudioCaptureKind.Display, events.Capture.Kind);
        Assert.Single(events.Clicks);
        Assert.Single(events.Cursor);
        Assert.Single(events.CameraCorners);
        Assert.Single(events.Markers);
        Assert.Equal(StudioMouseButton.Left, events.Clicks[0].Button);
        Assert.Equal(StudioAnchor.BottomRight, events.CameraCorners[0].Corner);
    }

    [Fact]
    public void ReadProject_NullArrayElementsDoNotBreakTimeMap()
    {
        var project = StudioProjectJson.ReadProject("""
            {
              "id": "p",
              "sources": { "screen": { "width": 1920, "height": 1080, "duration": 5 } },
              "edits": { "cuts": [ null ] }
            }
            """);

        var map = StudioTimeMap.FromProject(project);

        Assert.Equal(5, map.OutputDuration);
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

    [Fact]
    public void ReadProjectAndEvents_NullAndMissingDefaultsStayInSyncForEveryDocumentedProperty()
    {
        VerifyNullAndMissingDefaults(
            JsonNode.Parse(StudioProjectJson.WriteProject(FullyPopulatedProject()))!,
            node => StudioProjectJson.WriteProject(StudioProjectJson.ReadProject(node.ToJsonString())),
            RequiredProjectPaths(),
            ExplicitNullProjectPaths());

        VerifyNullAndMissingDefaults(
            JsonNode.Parse(StudioProjectJson.WriteEvents(FullyPopulatedEvents()))!,
            node => StudioProjectJson.WriteEvents(StudioProjectJson.ReadEvents(node.ToJsonString())),
            [],
            []);
    }

    private static void VerifyNullAndMissingDefaults(
        JsonNode root,
        Func<JsonNode, string> readAndWrite,
        HashSet<string> requiredPaths,
        HashSet<string> explicitNullPaths)
    {
        foreach (var fullPath in EnumeratePropertyPaths(root))
        {
            var path = PathKey(fullPath);
            var nullDocument = root.DeepClone();
            SetProperty(nullDocument, fullPath, null);
            var missingDocument = root.DeepClone();
            RemoveProperty(missingDocument, fullPath);

            if (requiredPaths.Contains(path))
            {
                Assert.ThrowsAny<Exception>(() => readAndWrite(nullDocument));
                Assert.ThrowsAny<Exception>(() => readAndWrite(missingDocument));
                continue;
            }

            var nullJson = readAndWrite(nullDocument);
            var missingJson = readAndWrite(missingDocument);

            if (explicitNullPaths.Contains(path))
            {
                Assert.NotEqual(nullJson, missingJson);
                Assert.Contains($"\"{fullPath[^1]}\": null", nullJson, StringComparison.Ordinal);
            }
            else
            {
                Assert.True(string.Equals(missingJson, nullJson, StringComparison.Ordinal), path);
            }
        }
    }

    private static StudioProject FullyPopulatedProject() =>
        new()
        {
            Id = "00000000-0000-0000-0000-000000000001",
            Name = "Project",
            CreatedAt = new DateTimeOffset(2026, 10, 2, 22, 41, 0, TimeSpan.Zero),
            ModifiedAt = new DateTimeOffset(2026, 10, 2, 22, 42, 0, TimeSpan.Zero),
            LastOpenedAt = new DateTimeOffset(2026, 10, 2, 22, 43, 0, TimeSpan.Zero),
            App = new StudioAppInfo { Platform = "windows", Version = "1.9.0" },
            KeepSources = true,
            Sources = new StudioSources
            {
                Screen = new StudioScreenSource { File = "screen.mp4", Width = 1920, Height = 1080, Duration = 10, FrameRate = 60 },
                Camera = new StudioCameraSource { File = "camera.mp4", Width = 1280, Height = 720, Duration = 9, StartOffset = 0.25 },
                Events = "events.json",
            },
            Canvas = new StudioCanvas
            {
                Aspect = StudioCanvasAspect.Landscape16X9,
                Padding = 0.1,
                Background = new StudioBackground { Style = StudioBackgroundStyle.Gradient, Preset = "ocean", Primary = "#111111", Secondary = "#222222", Image = "background.jpg" },
            },
            Screen = new StudioScreenStyle { CornerRadius = 0.1, Shadow = 0.4, Crop = new StudioRect(0.1, 0.1, 0.8, 0.8) },
            Camera = new StudioCameraStyle { Shape = StudioCameraShape.RoundedRectangle, CornerRadius = 0.2, Mirror = false, BorderWidth = 0.01, BorderColor = "#ABCDEF", Shadow = 0.2, Crop = new StudioRect(0.2, 0.2, 0.6, 0.6), Cutout = StudioCameraCutout.Blur },
            Scenes = [new StudioScene { Start = 1, Layout = StudioLayout.SideBySide }],
            Zooms = [new StudioZoom { Start = 1, End = 2, Scale = 1.5, Focus = new StudioZoomFocus { Mode = StudioZoomFocusMode.Cursor, X = 0.2, Y = 0.3 }, EaseIn = 0.1, EaseOut = 0.2, Origin = StudioZoomOrigin.Auto }],
            Edits = new StudioEdits
            {
                TrimStart = 0.25,
                TrimEnd = 9,
                Cuts = [new StudioTimeRange { Start = 2, End = 3 }],
                Speed = [new StudioSpeedRange { Start = 4, End = 5, Rate = 2 }],
            },
            Audio = new StudioAudio { Muted = true, SystemVolume = 0.5, MicrophoneVolume = 0.25 },
            Overlays = new StudioOverlays
            {
                Clicks = new StudioClickOverlay { Enabled = false, Color = "#123456", Size = 20, StrokeWidth = 4, Opacity = 0.4, Duration = 0.2 },
                Branding = true,
            },
            Exports = [new StudioExport { Path = "C:\\exports\\clip.mp4", ExportedAt = new DateTimeOffset(2026, 10, 2, 22, 44, 0, TimeSpan.Zero) }],
        };

    private static StudioEvents FullyPopulatedEvents() =>
        new()
        {
            Capture = new StudioCaptureInfo { Width = 1920, Height = 1080, Scale = 2, Kind = StudioCaptureKind.Region },
            Clicks = [new StudioClickEvent { T = 1, X = 0.2, Y = 0.3, Button = StudioMouseButton.Right }],
            Cursor = [new StudioCursorSample { T = 1, X = 0.2, Y = 0.3 }],
            CameraCorners = [new StudioCameraCornerEvent { T = 1, Corner = StudioAnchor.TopLeft }],
        };

    private static HashSet<string> RequiredProjectPaths() =>
        [
            "id",
            "sources",
            "sources.screen",
            "sources.screen.width",
            "sources.screen.height",
            "sources.screen.duration",
            "sources.camera.width",
            "sources.camera.height",
            "sources.camera.duration",
        ];

    private static HashSet<string> ExplicitNullProjectPaths() =>
        [
            "canvas.background.preset",
            "canvas.background.secondary",
        ];

    private static IEnumerable<string[]> EnumeratePropertyPaths(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                var current = new[] { property.Key };
                yield return current;
                if (property.Value is not null)
                {
                    foreach (var child in EnumeratePropertyPaths(property.Value))
                    {
                        yield return [.. current, .. child];
                    }
                }
            }
        }
        else if (node is JsonArray array && array.Count > 0 && array[0] is not null)
        {
            foreach (var child in EnumeratePropertyPaths(array[0]!))
            {
                yield return ["0", .. child];
            }
        }
    }

    private static string PathKey(string[] path) =>
        string.Join('.', path.Where(segment => !int.TryParse(segment, out _)));

    private static void SetProperty(JsonNode root, string[] path, JsonNode? value)
    {
        var parent = GetParent(root, path);
        parent[path[^1]] = value;
    }

    private static void RemoveProperty(JsonNode root, string[] path)
    {
        var parent = GetParent(root, path);
        parent.Remove(path[^1]);
    }

    private static JsonNode? GetProperty(JsonNode root, string[] path)
    {
        JsonNode? current = root;
        foreach (var segment in path)
        {
            current = int.TryParse(segment, out var index)
                ? current?.AsArray()[index]
                : current?[segment];
        }

        return current;
    }

    private static JsonObject GetParent(JsonNode root, string[] segments)
    {
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = int.TryParse(segments[i], out var index)
                ? current.AsArray()[index]!
                : current[segments[i]]!;
        }

        return current.AsObject();
    }
}
