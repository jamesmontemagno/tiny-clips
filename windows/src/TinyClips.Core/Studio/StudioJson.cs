using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyClips.Core.Studio;

public sealed class StudioProjectInvalidException : Exception
{
    public StudioProjectInvalidException(string message)
        : base(message)
    {
    }

    public StudioProjectInvalidException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class StudioUnsupportedSchemaVersionException : Exception
{
    public StudioUnsupportedSchemaVersionException(int schemaVersion)
        : base($"Studio project schema version {schemaVersion} is newer than supported version {StudioProject.CurrentSchemaVersion}.")
    {
        SchemaVersion = schemaVersion;
    }

    public int SchemaVersion { get; }
}

public static class StudioProjectJson
{
    public static StudioProject ReadProject(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var defaults = ValidateProjectJson(json);

        try
        {
            var project = JsonSerializer.Deserialize(json, StudioJsonContext.Default.StudioProject)
                ?? throw new StudioProjectInvalidException("Project JSON did not contain a project.");
            project = project with
            {
                SchemaVersion = defaults.HasSchemaVersion ? project.SchemaVersion : StudioProject.CurrentSchemaVersion,
                CreatedAt = defaults.HasCreatedAt ? project.CreatedAt : DateTimeOffset.UnixEpoch,
                ModifiedAt = defaults.HasModifiedAt ? project.ModifiedAt : DateTimeOffset.UnixEpoch,
                LastOpenedAt = defaults.HasLastOpenedAt ? project.LastOpenedAt : DateTimeOffset.UnixEpoch,
            };
            return ApplyDefaults(project, json);
        }
        catch (JsonException ex)
        {
            throw new StudioProjectInvalidException("Project JSON is invalid.", ex);
        }
    }

    public static string WriteProject(StudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return JsonSerializer.Serialize(project, StudioJsonContext.Default.StudioProject);
    }

    public static StudioEvents ReadEvents(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        ValidateSchemaVersion(json, allowMissingSchemaVersion: true);

        try
        {
            var events = JsonSerializer.Deserialize(json, StudioJsonContext.Default.StudioEvents) ?? new StudioEvents();
            return ApplyEventDefaults(events, json);
        }
        catch (JsonException ex)
        {
            throw new StudioProjectInvalidException("Events JSON is invalid.", ex);
        }
    }

    public static string WriteEvents(StudioEvents events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return JsonSerializer.Serialize(events, StudioJsonContext.Default.StudioEvents);
    }

    private static ProjectJsonDefaults ValidateProjectJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new StudioProjectInvalidException("Project JSON root must be an object.");
        }

        var hasSchemaVersion = HasNonNull(root, "schemaVersion");
        ValidateSchemaVersion(root, allowMissingSchemaVersion: true);
        RequireString(root, "id", "id");
        RequireObject(root, "sources", "sources");
        var sources = root.GetProperty("sources");
        RequireObject(sources, "screen", "sources.screen");
        var screen = sources.GetProperty("screen");
        RequirePositiveInt(screen, "width", "sources.screen.width");
        RequirePositiveInt(screen, "height", "sources.screen.height");
        RequireNonNegativeFiniteNumber(screen, "duration", "sources.screen.duration");

        if (sources.TryGetProperty("camera", out var camera) && camera.ValueKind != JsonValueKind.Null)
        {
            if (camera.ValueKind != JsonValueKind.Object)
            {
                throw new StudioProjectInvalidException("Required property sources.camera must be an object or null.");
            }

            RequirePositiveInt(camera, "width", "sources.camera.width");
            RequirePositiveInt(camera, "height", "sources.camera.height");
            RequireNonNegativeFiniteNumber(camera, "duration", "sources.camera.duration");
        }

        return new ProjectJsonDefaults(
            hasSchemaVersion,
            HasNonNull(root, "createdAt"),
            HasNonNull(root, "modifiedAt"),
            HasNonNull(root, "lastOpenedAt"));
    }

    private static void ValidateSchemaVersion(string json, bool allowMissingSchemaVersion)
    {
        using var document = JsonDocument.Parse(json);
        ValidateSchemaVersion(document.RootElement, allowMissingSchemaVersion);
    }

    private static void ValidateSchemaVersion(JsonElement root, bool allowMissingSchemaVersion)
    {
        if (!root.TryGetProperty("schemaVersion", out var schemaVersion))
        {
            if (allowMissingSchemaVersion)
            {
                return;
            }

            throw new StudioProjectInvalidException("Required property schemaVersion is missing.");
        }

        if (schemaVersion.ValueKind == JsonValueKind.Null && allowMissingSchemaVersion)
        {
            return;
        }

        if (schemaVersion.ValueKind != JsonValueKind.Number || !schemaVersion.TryGetInt32(out var value))
        {
            throw new StudioProjectInvalidException("Property schemaVersion must be an integer.");
        }

        if (value > StudioProject.CurrentSchemaVersion)
        {
            throw new StudioUnsupportedSchemaVersionException(value);
        }
    }

    private static void RequireObject(JsonElement element, string propertyName, string path)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            throw new StudioProjectInvalidException($"Required property {path} is missing.");
        }
    }

    private static void RequireString(JsonElement element, string propertyName, string path)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new StudioProjectInvalidException($"Required property {path} is missing.");
        }
    }

    private static void RequirePositiveInt(JsonElement element, string propertyName, string path)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var value)
            || value < 1)
        {
            throw new StudioProjectInvalidException($"Required property {path} must be an integer of at least 1.");
        }
    }

    private static void RequireNonNegativeFiniteNumber(JsonElement element, string propertyName, string path)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetDouble(out var value)
            || !double.IsFinite(value)
            || value < 0)
        {
            throw new StudioProjectInvalidException($"Required property {path} must be a non-negative finite number.");
        }
    }

    private readonly record struct ProjectJsonDefaults(bool HasSchemaVersion, bool HasCreatedAt, bool HasModifiedAt, bool HasLastOpenedAt);

    private static StudioProject ApplyDefaults(StudioProject project, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var defaultProject = new StudioProject();
        var sources = project.Sources ?? new StudioSources();
        var defaultSources = new StudioSources();
        var screenSource = sources.Screen ?? new StudioScreenSource();
        var screenStyle = project.Screen ?? new StudioScreenStyle();
        var cameraStyle = project.Camera ?? new StudioCameraStyle();
        var canvas = project.Canvas ?? new StudioCanvas();
        var background = canvas.Background ?? new StudioBackground();
        var app = project.App ?? new StudioAppInfo();

        screenSource = screenSource with
        {
            File = HasNonNull(root, "sources", "screen", "file") ? screenSource.File : defaultSources.Screen.File,
            FrameRate = HasNonNull(root, "sources", "screen", "frameRate") ? screenSource.FrameRate : defaultSources.Screen.FrameRate,
            External = HasNonNull(root, "sources", "screen", "external") && screenSource.External,
        };
        ValidateSourceFileName(screenSource.File, "sources.screen.file", allowExternal: screenSource.External);

        var cameraSource = NormalizeCameraSource(sources.Camera, root);
        var eventsFile = Has(root, "sources", "events") ? sources.Events : defaultSources.Events;
        if (eventsFile is not null)
        {
            ValidateSourceFileName(eventsFile, "sources.events", allowExternal: false);
        }

        canvas = canvas with
        {
            Aspect = HasNonNull(root, "canvas", "aspect") ? canvas.Aspect : defaultProject.Canvas.Aspect,
            Padding = HasNonNull(root, "canvas", "padding") ? canvas.Padding : defaultProject.Canvas.Padding,
            Background = background with
            {
                Style = HasNonNull(root, "canvas", "background", "style") ? background.Style : defaultProject.Canvas.Background.Style,
                Preset = Has(root, "canvas", "background", "preset") ? background.Preset : defaultProject.Canvas.Background.Preset,
                Primary = HasNonNull(root, "canvas", "background", "primary") ? background.Primary : defaultProject.Canvas.Background.Primary,
                Secondary = Has(root, "canvas", "background", "secondary") ? background.Secondary : defaultProject.Canvas.Background.Secondary,
                Image = Has(root, "canvas", "background", "image") ? PlainFileNameOrNull(background.Image) : defaultProject.Canvas.Background.Image,
            },
        };

        screenStyle = screenStyle with
        {
            CornerRadius = HasNonNull(root, "screen", "cornerRadius") ? screenStyle.CornerRadius : defaultProject.Screen.CornerRadius,
            Shadow = HasNonNull(root, "screen", "shadow") ? screenStyle.Shadow : defaultProject.Screen.Shadow,
        };

        cameraStyle = cameraStyle with
        {
            Shape = HasNonNull(root, "camera", "shape") ? cameraStyle.Shape : defaultProject.Camera.Shape,
            CornerRadius = HasNonNull(root, "camera", "cornerRadius") ? cameraStyle.CornerRadius : defaultProject.Camera.CornerRadius,
            Mirror = HasNonNull(root, "camera", "mirror") ? cameraStyle.Mirror : defaultProject.Camera.Mirror,
            BorderColor = HasNonNull(root, "camera", "borderColor") ? cameraStyle.BorderColor : defaultProject.Camera.BorderColor,
            Shadow = HasNonNull(root, "camera", "shadow") ? cameraStyle.Shadow : defaultProject.Camera.Shadow,
            Cutout = HasNonNull(root, "camera", "cutout") ? cameraStyle.Cutout : defaultProject.Camera.Cutout,
        };

        var overlays = project.Overlays ?? new StudioOverlays();
        var clicks = overlays.Clicks ?? new StudioClickOverlay();
        overlays = overlays with
        {
            Clicks = clicks with
            {
                Enabled = HasNonNull(root, "overlays", "clicks", "enabled") ? clicks.Enabled : defaultProject.Overlays.Clicks.Enabled,
                Color = HasNonNull(root, "overlays", "clicks", "color") ? clicks.Color : defaultProject.Overlays.Clicks.Color,
                Size = HasNonNull(root, "overlays", "clicks", "size") ? clicks.Size : defaultProject.Overlays.Clicks.Size,
                StrokeWidth = HasNonNull(root, "overlays", "clicks", "strokeWidth") ? clicks.StrokeWidth : defaultProject.Overlays.Clicks.StrokeWidth,
                Opacity = HasNonNull(root, "overlays", "clicks", "opacity") ? clicks.Opacity : defaultProject.Overlays.Clicks.Opacity,
                Duration = HasNonNull(root, "overlays", "clicks", "duration") ? clicks.Duration : defaultProject.Overlays.Clicks.Duration,
            },
        };

        return project with
        {
            Name = HasNonNull(root, "name") ? project.Name : defaultProject.Name,
            App = app with
            {
                Platform = HasNonNull(root, "app", "platform") ? app.Platform : defaultProject.App.Platform,
                Version = HasNonNull(root, "app", "version") ? app.Version : defaultProject.App.Version,
            },
            Sources = sources with { Screen = screenSource, Camera = cameraSource, Events = eventsFile },
            Canvas = canvas,
            Screen = screenStyle,
            Camera = cameraStyle,
            Scenes = NormalizeSceneDefaults(project.Scenes, root),
            Zooms = NormalizeZooms(project.Zooms, root),
            Edits = NormalizeEdits(project.Edits, root),
            Audio = NormalizeAudio(project.Audio, root),
            Overlays = overlays,
            Exports = NormalizeExports(project.Exports, root),
        };
    }

    private static StudioCameraSource? NormalizeCameraSource(StudioCameraSource? cameraSource, JsonElement root)
    {
        if (!Has(root, "sources", "camera"))
        {
            return null;
        }

        if (!HasNonNull(root, "sources", "camera"))
        {
            return null;
        }

        cameraSource ??= new StudioCameraSource();
        cameraSource = cameraSource with
        {
            File = HasNonNull(root, "sources", "camera", "file") ? cameraSource.File : new StudioCameraSource().File,
        };
        ValidateSourceFileName(cameraSource.File, "sources.camera.file", allowExternal: false);
        return cameraSource;
    }

    private static StudioScene[] NormalizeSceneDefaults(StudioScene[]? scenes, JsonElement root)
    {
        if (scenes is null || scenes.Length == 0 || !root.TryGetProperty("scenes", out var scenesElement) || scenesElement.ValueKind != JsonValueKind.Array)
        {
            return [new StudioScene()];
        }

        var elements = NonNullArrayElements(scenesElement);
        var values = NonNullItems(scenes);
        var result = new StudioScene[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var scene = values[i];
            var element = i < elements.Length ? elements[i] : default;
            var bubble = scene.Bubble ?? new StudioBubble();
            var split = scene.Split ?? new StudioSplit();
            var transition = scene.Transition ?? new StudioTransition();
            result[i] = scene with
            {
                Start = HasNonNull(element, "start") ? scene.Start : 0,
                Layout = HasNonNull(element, "layout") ? scene.Layout : StudioLayout.Bubble,
                Bubble = bubble with
                {
                    Anchor = HasNonNull(element, "bubble", "anchor") ? bubble.Anchor : StudioAnchor.BottomRight,
                    Size = HasNonNull(element, "bubble", "size") ? bubble.Size : 0.24,
                    OffsetX = HasNonNull(element, "bubble", "offsetX") ? bubble.OffsetX : 0,
                    OffsetY = HasNonNull(element, "bubble", "offsetY") ? bubble.OffsetY : 0,
                },
                Split = split with
                {
                    CameraSide = HasNonNull(element, "split", "cameraSide") ? split.CameraSide : StudioCameraSide.Trailing,
                    CameraFraction = HasNonNull(element, "split", "cameraFraction") ? split.CameraFraction : 0.3,
                },
                Transition = transition with
                {
                    Kind = HasNonNull(element, "transition", "kind") ? transition.Kind : StudioTransitionKind.Cut,
                    Duration = HasNonNull(element, "transition", "duration") ? transition.Duration : 0.35,
                },
            };
        }

        return result;
    }

    private static StudioEdits NormalizeEdits(StudioEdits? edits, JsonElement root)
    {
        edits ??= new StudioEdits();
        return edits with
        {
            TrimStart = HasNonNull(root, "edits", "trimStart") ? edits.TrimStart : 0,
            TrimEnd = Has(root, "edits", "trimEnd") ? edits.TrimEnd : null,
            Cuts = HasNonNull(root, "edits", "cuts") ? NonNullItems(edits.Cuts) : [],
            Speed = HasNonNull(root, "edits", "speed") ? NonNullItems(edits.Speed) : [],
        };
    }

    private static StudioZoom[] NormalizeZooms(StudioZoom[]? zooms, JsonElement root)
    {
        if (zooms is null || zooms.Length == 0 || !root.TryGetProperty("zooms", out var zoomsElement) || zoomsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var elements = NonNullArrayElements(zoomsElement);
        var values = NonNullItems(zooms);
        var result = new StudioZoom[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var zoom = values[i];
            var element = i < elements.Length ? elements[i] : default;
            var focus = zoom.Focus ?? new StudioZoomFocus();
            result[i] = zoom with
            {
                Start = HasNonNull(element, "start") ? zoom.Start : 0,
                End = HasNonNull(element, "end") ? zoom.End : 0,
                Scale = HasNonNull(element, "scale") ? zoom.Scale : 2,
                Focus = focus with
                {
                    Mode = HasNonNull(element, "focus", "mode") ? focus.Mode : StudioZoomFocusMode.Point,
                    X = HasNonNull(element, "focus", "x") ? focus.X : 0.5,
                    Y = HasNonNull(element, "focus", "y") ? focus.Y : 0.5,
                },
                EaseIn = HasNonNull(element, "easeIn") ? zoom.EaseIn : 0.5,
                EaseOut = HasNonNull(element, "easeOut") ? zoom.EaseOut : 0.5,
                Origin = HasNonNull(element, "origin") ? zoom.Origin : StudioZoomOrigin.Manual,
            };
        }

        return result;
    }

    private static StudioAudio NormalizeAudio(StudioAudio? audio, JsonElement root)
    {
        audio ??= new StudioAudio();
        return audio with
        {
            Muted = HasNonNull(root, "audio", "muted") && audio.Muted,
            SystemVolume = HasNonNull(root, "audio", "systemVolume") ? audio.SystemVolume : 1,
            MicrophoneVolume = HasNonNull(root, "audio", "microphoneVolume") ? audio.MicrophoneVolume : 1,
        };
    }

    private static StudioExport[] NormalizeExports(StudioExport[]? exports, JsonElement root)
    {
        if (exports is null || exports.Length == 0 || !root.TryGetProperty("exports", out var exportsElement) || exportsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var elements = NonNullArrayElements(exportsElement);
        var values = NonNullItems(exports);
        var result = new StudioExport[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var export = values[i];
            var element = i < elements.Length ? elements[i] : default;
            result[i] = export with
            {
                Path = HasNonNull(element, "path") ? export.Path : string.Empty,
                ExportedAt = HasNonNull(element, "exportedAt") ? export.ExportedAt : DateTimeOffset.UnixEpoch,
            };
        }

        return result;
    }

    private static StudioEvents ApplyEventDefaults(StudioEvents events, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var capture = events.Capture ?? new StudioCaptureInfo();

        return events with
        {
            SchemaVersion = HasNonNull(root, "schemaVersion") ? events.SchemaVersion : StudioProject.CurrentSchemaVersion,
            Capture = capture with
            {
                Width = HasNonNull(root, "capture", "width") ? capture.Width : 0,
                Height = HasNonNull(root, "capture", "height") ? capture.Height : 0,
                Scale = HasNonNull(root, "capture", "scale") ? capture.Scale : 1,
                Kind = HasNonNull(root, "capture", "kind") ? capture.Kind : StudioCaptureKind.Display,
            },
            Clicks = NormalizeClickEvents(events.Clicks, root),
            Cursor = NormalizeCursorSamples(events.Cursor, root),
            CameraCorners = NormalizeCameraCornerEvents(events.CameraCorners, root),
            Markers = HasNonNull(root, "markers") ? events.Markers.Where(marker => marker.ValueKind != JsonValueKind.Null).ToArray() : [],
        };
    }

    private static StudioClickEvent[] NormalizeClickEvents(StudioClickEvent[]? clicks, JsonElement root)
    {
        if (clicks is null || clicks.Length == 0 || !root.TryGetProperty("clicks", out var clicksElement) || clicksElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var elements = NonNullArrayElements(clicksElement);
        var values = NonNullItems(clicks);
        var result = new StudioClickEvent[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var click = values[i];
            var element = i < elements.Length ? elements[i] : default;
            result[i] = click with
            {
                T = HasNonNull(element, "t") ? click.T : 0,
                X = HasNonNull(element, "x") ? click.X : 0,
                Y = HasNonNull(element, "y") ? click.Y : 0,
                Button = HasNonNull(element, "button") ? click.Button : StudioMouseButton.Left,
            };
        }

        return result;
    }

    private static StudioCursorSample[] NormalizeCursorSamples(StudioCursorSample[]? cursor, JsonElement root)
    {
        if (cursor is null || cursor.Length == 0 || !root.TryGetProperty("cursor", out var cursorElement) || cursorElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var elements = NonNullArrayElements(cursorElement);
        var values = NonNullItems(cursor);
        var result = new StudioCursorSample[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var sample = values[i];
            var element = i < elements.Length ? elements[i] : default;
            result[i] = sample with
            {
                T = HasNonNull(element, "t") ? sample.T : 0,
                X = HasNonNull(element, "x") ? sample.X : 0,
                Y = HasNonNull(element, "y") ? sample.Y : 0,
            };
        }

        return result;
    }

    private static StudioCameraCornerEvent[] NormalizeCameraCornerEvents(StudioCameraCornerEvent[]? cameraCorners, JsonElement root)
    {
        if (cameraCorners is null || cameraCorners.Length == 0 || !root.TryGetProperty("cameraCorners", out var cameraCornersElement) || cameraCornersElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var elements = NonNullArrayElements(cameraCornersElement);
        var values = NonNullItems(cameraCorners);
        var result = new StudioCameraCornerEvent[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var corner = values[i];
            var element = i < elements.Length ? elements[i] : default;
            result[i] = corner with
            {
                T = HasNonNull(element, "t") ? corner.T : 0,
                Corner = HasNonNull(element, "corner") ? corner.Corner : StudioAnchor.BottomRight,
            };
        }

        return result;
    }

    private static T[] NonNullItems<T>(T[]? items)
        where T : class =>
        items?.Where(static item => item is not null).ToArray() ?? [];

    private static JsonElement[] NonNullArrayElements(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(static element => element.ValueKind != JsonValueKind.Null).ToArray()
            : [];

    private static void ValidateSourceFileName(string fileName, string path, bool allowExternal)
    {
        if (allowExternal)
        {
            return;
        }

        if (!IsPlainFileName(fileName))
        {
            throw new StudioProjectInvalidException($"Property {path} must be a file name.");
        }
    }

    private static string? PlainFileNameOrNull(string? fileName) =>
        fileName is not null && IsPlainFileName(fileName) ? fileName : null;

    internal static bool IsPlainFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName is not ("." or "..")
        && !Path.IsPathRooted(fileName)
        && fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;

    private static bool Has(JsonElement element, params ReadOnlySpan<string> path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!element.TryGetProperty(segment, out element))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasNonNull(JsonElement element, params ReadOnlySpan<string> path)
    {
        if (!Has(element, path))
        {
            return false;
        }

        foreach (var segment in path)
        {
            element = element.GetProperty(segment);
        }

        return element.ValueKind != JsonValueKind.Null;
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    Converters =
    [
        typeof(StudioTimestampJsonConverter),
        typeof(StudioNullAsDefaultInt32JsonConverter),
        typeof(StudioNullAsDefaultDoubleJsonConverter),
        typeof(StudioNullAsDefaultBoolJsonConverter),
    ])]
[JsonSerializable(typeof(StudioProject))]
[JsonSerializable(typeof(StudioEvents))]
[JsonSerializable(typeof(StudioLook))]
internal sealed partial class StudioJsonContext : JsonSerializerContext;

internal sealed class StudioNullAsDefaultInt32JsonConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? 0 : reader.GetInt32();

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

internal sealed class StudioNullAsDefaultDoubleJsonConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? 0 : reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

internal sealed class StudioNullAsDefaultBoolJsonConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? false : reader.GetBoolean();

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value);
}

internal sealed class StudioTimestampJsonConverter : JsonConverter<DateTimeOffset>
{
    private const string WholeSecondsUtcFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return DateTimeOffset.UnixEpoch;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Timestamp must be an ISO 8601 string.");
        }

        var value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return DateTimeOffset.UnixEpoch;
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            throw new JsonException($"Timestamp '{value}' is invalid.");
        }

        return timestamp.ToUniversalTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToUniversalTime().ToString(WholeSecondsUtcFormat, CultureInfo.InvariantCulture));
    }
}

internal abstract class StudioEnumJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    protected abstract TEnum DefaultValue { get; }
    protected abstract TEnum? ReadValue(string value);
    protected abstract string Write(TEnum value);

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            return DefaultValue;
        }

        var value = reader.GetString();
        return value is not null ? ReadValue(value) ?? DefaultValue : DefaultValue;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Write(value));
}

internal sealed class StudioCanvasAspectJsonConverter : StudioEnumJsonConverter<StudioCanvasAspect>
{
    protected override StudioCanvasAspect DefaultValue => StudioCanvasAspect.Auto;
    protected override StudioCanvasAspect? ReadValue(string value) => value switch
    {
        "auto" => StudioCanvasAspect.Auto,
        "square" => StudioCanvasAspect.Square,
        "landscape4x3" => StudioCanvasAspect.Landscape4X3,
        "landscape16x9" => StudioCanvasAspect.Landscape16X9,
        "portrait3x4" => StudioCanvasAspect.Portrait3X4,
        "portrait9x16" => StudioCanvasAspect.Portrait9X16,
        _ => null,
    };
    protected override string Write(StudioCanvasAspect value) => value switch
    {
        StudioCanvasAspect.Square => "square",
        StudioCanvasAspect.Landscape4X3 => "landscape4x3",
        StudioCanvasAspect.Landscape16X9 => "landscape16x9",
        StudioCanvasAspect.Portrait3X4 => "portrait3x4",
        StudioCanvasAspect.Portrait9X16 => "portrait9x16",
        _ => "auto",
    };
}

internal sealed class StudioBackgroundStyleJsonConverter : StudioEnumJsonConverter<StudioBackgroundStyle>
{
    protected override StudioBackgroundStyle DefaultValue => StudioBackgroundStyle.Gradient;
    protected override StudioBackgroundStyle? ReadValue(string value) => value switch
    {
        "none" => StudioBackgroundStyle.None,
        "solid" => StudioBackgroundStyle.Solid,
        "gradient" => StudioBackgroundStyle.Gradient,
        "image" => StudioBackgroundStyle.Image,
        _ => null,
    };
    protected override string Write(StudioBackgroundStyle value) => value switch
    {
        StudioBackgroundStyle.None => "none",
        StudioBackgroundStyle.Solid => "solid",
        StudioBackgroundStyle.Image => "image",
        _ => "gradient",
    };
}

internal sealed class StudioCameraShapeJsonConverter : StudioEnumJsonConverter<StudioCameraShape>
{
    protected override StudioCameraShape DefaultValue => StudioCameraShape.Circle;
    protected override StudioCameraShape? ReadValue(string value) => value switch
    {
        "circle" => StudioCameraShape.Circle,
        "roundedRectangle" => StudioCameraShape.RoundedRectangle,
        "squircle" => StudioCameraShape.Squircle,
        "rectangle" => StudioCameraShape.Rectangle,
        _ => null,
    };
    protected override string Write(StudioCameraShape value) => value switch
    {
        StudioCameraShape.RoundedRectangle => "roundedRectangle",
        StudioCameraShape.Squircle => "squircle",
        StudioCameraShape.Rectangle => "rectangle",
        _ => "circle",
    };
}

internal sealed class StudioCameraCutoutJsonConverter : StudioEnumJsonConverter<StudioCameraCutout>
{
    protected override StudioCameraCutout DefaultValue => StudioCameraCutout.None;
    protected override StudioCameraCutout? ReadValue(string value) => value switch
    {
        "none" => StudioCameraCutout.None,
        "blur" => StudioCameraCutout.Blur,
        "remove" => StudioCameraCutout.Remove,
        _ => null,
    };
    protected override string Write(StudioCameraCutout value) => value switch
    {
        StudioCameraCutout.Blur => "blur",
        StudioCameraCutout.Remove => "remove",
        _ => "none",
    };
}

internal sealed class StudioLayoutJsonConverter : StudioEnumJsonConverter<StudioLayout>
{
    protected override StudioLayout DefaultValue => StudioLayout.Bubble;
    protected override StudioLayout? ReadValue(string value) => value switch
    {
        "screen" => StudioLayout.Screen,
        "bubble" => StudioLayout.Bubble,
        "sideBySide" => StudioLayout.SideBySide,
        "camera" => StudioLayout.Camera,
        _ => null,
    };
    protected override string Write(StudioLayout value) => value switch
    {
        StudioLayout.Screen => "screen",
        StudioLayout.SideBySide => "sideBySide",
        StudioLayout.Camera => "camera",
        _ => "bubble",
    };
}

internal sealed class StudioAnchorJsonConverter : StudioEnumJsonConverter<StudioAnchor>
{
    protected override StudioAnchor DefaultValue => StudioAnchor.BottomRight;
    protected override StudioAnchor? ReadValue(string value) => value switch
    {
        "topLeft" => StudioAnchor.TopLeft,
        "topRight" => StudioAnchor.TopRight,
        "bottomLeft" => StudioAnchor.BottomLeft,
        "bottomRight" => StudioAnchor.BottomRight,
        _ => null,
    };
    protected override string Write(StudioAnchor value) => value switch
    {
        StudioAnchor.TopLeft => "topLeft",
        StudioAnchor.TopRight => "topRight",
        StudioAnchor.BottomLeft => "bottomLeft",
        _ => "bottomRight",
    };
}

internal sealed class StudioCameraSideJsonConverter : StudioEnumJsonConverter<StudioCameraSide>
{
    protected override StudioCameraSide DefaultValue => StudioCameraSide.Trailing;
    protected override StudioCameraSide? ReadValue(string value) => value switch
    {
        "leading" => StudioCameraSide.Leading,
        "trailing" => StudioCameraSide.Trailing,
        _ => null,
    };
    protected override string Write(StudioCameraSide value) => value == StudioCameraSide.Leading ? "leading" : "trailing";
}

internal sealed class StudioTransitionKindJsonConverter : StudioEnumJsonConverter<StudioTransitionKind>
{
    protected override StudioTransitionKind DefaultValue => StudioTransitionKind.Cut;
    protected override StudioTransitionKind? ReadValue(string value) => value switch
    {
        "cut" => StudioTransitionKind.Cut,
        "morph" => StudioTransitionKind.Morph,
        _ => null,
    };
    protected override string Write(StudioTransitionKind value) => value == StudioTransitionKind.Morph ? "morph" : "cut";
}

internal sealed class StudioZoomFocusModeJsonConverter : StudioEnumJsonConverter<StudioZoomFocusMode>
{
    protected override StudioZoomFocusMode DefaultValue => StudioZoomFocusMode.Point;
    protected override StudioZoomFocusMode? ReadValue(string value) => value switch
    {
        "point" => StudioZoomFocusMode.Point,
        "cursor" => StudioZoomFocusMode.Cursor,
        _ => null,
    };
    protected override string Write(StudioZoomFocusMode value) => value == StudioZoomFocusMode.Cursor ? "cursor" : "point";
}

internal sealed class StudioZoomOriginJsonConverter : StudioEnumJsonConverter<StudioZoomOrigin>
{
    protected override StudioZoomOrigin DefaultValue => StudioZoomOrigin.Manual;
    protected override StudioZoomOrigin? ReadValue(string value) => value switch
    {
        "manual" => StudioZoomOrigin.Manual,
        "auto" => StudioZoomOrigin.Auto,
        _ => null,
    };
    protected override string Write(StudioZoomOrigin value) => value == StudioZoomOrigin.Auto ? "auto" : "manual";
}

internal sealed class StudioMouseButtonJsonConverter : StudioEnumJsonConverter<StudioMouseButton>
{
    protected override StudioMouseButton DefaultValue => StudioMouseButton.Left;
    protected override StudioMouseButton? ReadValue(string value) => value switch
    {
        "left" => StudioMouseButton.Left,
        "right" => StudioMouseButton.Right,
        "middle" => StudioMouseButton.Middle,
        "other" => StudioMouseButton.Other,
        _ => null,
    };
    protected override string Write(StudioMouseButton value) => value switch
    {
        StudioMouseButton.Right => "right",
        StudioMouseButton.Middle => "middle",
        StudioMouseButton.Other => "other",
        _ => "left",
    };
}

internal sealed class StudioCaptureKindJsonConverter : StudioEnumJsonConverter<StudioCaptureKind>
{
    protected override StudioCaptureKind DefaultValue => StudioCaptureKind.Display;
    protected override StudioCaptureKind? ReadValue(string value) => value switch
    {
        "display" => StudioCaptureKind.Display,
        "region" => StudioCaptureKind.Region,
        "window" => StudioCaptureKind.Window,
        _ => null,
    };
    protected override string Write(StudioCaptureKind value) => value switch
    {
        StudioCaptureKind.Region => "region",
        StudioCaptureKind.Window => "window",
        _ => "display",
    };
}
