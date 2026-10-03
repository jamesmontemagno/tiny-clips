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
            return JsonSerializer.Deserialize(json, StudioJsonContext.Default.StudioEvents) ?? new StudioEvents();
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
        RequireNumber(screen, "width", "sources.screen.width");
        RequireNumber(screen, "height", "sources.screen.height");
        RequireNumber(screen, "duration", "sources.screen.duration");

        if (sources.TryGetProperty("camera", out var camera) && camera.ValueKind != JsonValueKind.Null)
        {
            if (camera.ValueKind != JsonValueKind.Object)
            {
                throw new StudioProjectInvalidException("Required property sources.camera must be an object or null.");
            }

            RequireNumber(camera, "width", "sources.camera.width");
            RequireNumber(camera, "height", "sources.camera.height");
            RequireNumber(camera, "duration", "sources.camera.duration");
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

    private static void RequireNumber(JsonElement element, string propertyName, string path)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            throw new StudioProjectInvalidException($"Required property {path} is missing.");
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

        var cameraSource = NormalizeCameraSource(sources.Camera, root);
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
            Sources = sources with { Screen = screenSource, Camera = cameraSource },
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
        return cameraSource with
        {
            File = HasNonNull(root, "sources", "camera", "file") ? cameraSource.File : new StudioCameraSource().File,
        };
    }

    private static StudioScene[] NormalizeSceneDefaults(StudioScene[]? scenes, JsonElement root)
    {
        if (scenes is null || scenes.Length == 0 || !root.TryGetProperty("scenes", out var scenesElement) || scenesElement.ValueKind != JsonValueKind.Array)
        {
            return [new StudioScene()];
        }

        var elements = scenesElement.EnumerateArray().ToArray();
        var result = new StudioScene[scenes.Length];
        for (var i = 0; i < scenes.Length; i++)
        {
            var scene = scenes[i] ?? new StudioScene();
            var element = i < elements.Length ? elements[i] : default;
            var bubble = scene.Bubble ?? new StudioBubble();
            var split = scene.Split ?? new StudioSplit();
            var transition = scene.Transition ?? new StudioTransition();
            result[i] = scene with
            {
                Layout = HasNonNull(element, "layout") ? scene.Layout : StudioLayout.Bubble,
                Bubble = bubble with
                {
                    Anchor = HasNonNull(element, "bubble", "anchor") ? bubble.Anchor : StudioAnchor.BottomRight,
                    Size = HasNonNull(element, "bubble", "size") ? bubble.Size : 0.24,
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
            Cuts = edits.Cuts ?? [],
            Speed = edits.Speed ?? [],
        };
    }

    private static StudioZoom[] NormalizeZooms(StudioZoom[]? zooms, JsonElement root)
    {
        if (zooms is null || zooms.Length == 0 || !root.TryGetProperty("zooms", out var zoomsElement) || zoomsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var elements = zoomsElement.EnumerateArray().ToArray();
        var result = new StudioZoom[zooms.Length];
        for (var i = 0; i < zooms.Length; i++)
        {
            var zoom = zooms[i] ?? new StudioZoom();
            var element = i < elements.Length ? elements[i] : default;
            var focus = zoom.Focus ?? new StudioZoomFocus();
            result[i] = zoom with
            {
                Scale = HasNonNull(element, "scale") ? zoom.Scale : 1,
                Focus = focus with
                {
                    Mode = HasNonNull(element, "focus", "mode") ? focus.Mode : StudioZoomFocusMode.Point,
                    X = HasNonNull(element, "focus", "x") ? focus.X : 0.5,
                    Y = HasNonNull(element, "focus", "y") ? focus.Y : 0.5,
                },
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

        var elements = exportsElement.EnumerateArray().ToArray();
        var result = new StudioExport[exports.Length];
        for (var i = 0; i < exports.Length; i++)
        {
            var export = exports[i] ?? new StudioExport();
            var element = i < elements.Length ? elements[i] : default;
            result[i] = export with
            {
                Path = HasNonNull(element, "path") ? export.Path : string.Empty,
                ExportedAt = HasNonNull(element, "exportedAt") ? export.ExportedAt : DateTimeOffset.UnixEpoch,
            };
        }

        return result;
    }

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
    protected abstract bool TryRead(string value, out TEnum result);
    protected abstract string Write(TEnum value);

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            return DefaultValue;
        }

        var value = reader.GetString();
        return value is not null && TryRead(value, out var result) ? result : DefaultValue;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Write(value));
}

internal sealed class StudioCanvasAspectJsonConverter : StudioEnumJsonConverter<StudioCanvasAspect>
{
    protected override StudioCanvasAspect DefaultValue => StudioCanvasAspect.Auto;
    protected override bool TryRead(string value, out StudioCanvasAspect result) => (result = value switch
    {
        "auto" => StudioCanvasAspect.Auto,
        "square" => StudioCanvasAspect.Square,
        "landscape4x3" => StudioCanvasAspect.Landscape4X3,
        "landscape16x9" => StudioCanvasAspect.Landscape16X9,
        "portrait3x4" => StudioCanvasAspect.Portrait3X4,
        "portrait9x16" => StudioCanvasAspect.Portrait9X16,
        _ => default,
    }) != default || value == "auto";
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
    protected override bool TryRead(string value, out StudioBackgroundStyle result) => (result = value switch
    {
        "none" => StudioBackgroundStyle.None,
        "solid" => StudioBackgroundStyle.Solid,
        "gradient" => StudioBackgroundStyle.Gradient,
        "image" => StudioBackgroundStyle.Image,
        _ => default,
    }) != default || value == "none";
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
    protected override bool TryRead(string value, out StudioCameraShape result) => (result = value switch
    {
        "circle" => StudioCameraShape.Circle,
        "roundedRectangle" => StudioCameraShape.RoundedRectangle,
        "squircle" => StudioCameraShape.Squircle,
        "rectangle" => StudioCameraShape.Rectangle,
        _ => default,
    }) != default || value == "circle";
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
    protected override bool TryRead(string value, out StudioCameraCutout result) => (result = value switch
    {
        "none" => StudioCameraCutout.None,
        "blur" => StudioCameraCutout.Blur,
        "remove" => StudioCameraCutout.Remove,
        _ => default,
    }) != default || value == "none";
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
    protected override bool TryRead(string value, out StudioLayout result) => (result = value switch
    {
        "screen" => StudioLayout.Screen,
        "bubble" => StudioLayout.Bubble,
        "sideBySide" => StudioLayout.SideBySide,
        "camera" => StudioLayout.Camera,
        _ => default,
    }) != default || value == "screen";
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
    protected override bool TryRead(string value, out StudioAnchor result) => (result = value switch
    {
        "topLeft" => StudioAnchor.TopLeft,
        "topRight" => StudioAnchor.TopRight,
        "bottomLeft" => StudioAnchor.BottomLeft,
        "bottomRight" => StudioAnchor.BottomRight,
        _ => default,
    }) != default || value == "topLeft";
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
    protected override bool TryRead(string value, out StudioCameraSide result) => (result = value switch
    {
        "leading" => StudioCameraSide.Leading,
        "trailing" => StudioCameraSide.Trailing,
        _ => default,
    }) != default || value == "leading";
    protected override string Write(StudioCameraSide value) => value == StudioCameraSide.Leading ? "leading" : "trailing";
}

internal sealed class StudioTransitionKindJsonConverter : StudioEnumJsonConverter<StudioTransitionKind>
{
    protected override StudioTransitionKind DefaultValue => StudioTransitionKind.Cut;
    protected override bool TryRead(string value, out StudioTransitionKind result) => (result = value switch
    {
        "cut" => StudioTransitionKind.Cut,
        "morph" => StudioTransitionKind.Morph,
        _ => default,
    }) != default || value == "cut";
    protected override string Write(StudioTransitionKind value) => value == StudioTransitionKind.Morph ? "morph" : "cut";
}

internal sealed class StudioZoomFocusModeJsonConverter : StudioEnumJsonConverter<StudioZoomFocusMode>
{
    protected override StudioZoomFocusMode DefaultValue => StudioZoomFocusMode.Point;
    protected override bool TryRead(string value, out StudioZoomFocusMode result) => (result = value switch
    {
        "point" => StudioZoomFocusMode.Point,
        "cursor" => StudioZoomFocusMode.Cursor,
        _ => default,
    }) != default || value == "point";
    protected override string Write(StudioZoomFocusMode value) => value == StudioZoomFocusMode.Cursor ? "cursor" : "point";
}

internal sealed class StudioZoomOriginJsonConverter : StudioEnumJsonConverter<StudioZoomOrigin>
{
    protected override StudioZoomOrigin DefaultValue => StudioZoomOrigin.Manual;
    protected override bool TryRead(string value, out StudioZoomOrigin result) => (result = value switch
    {
        "manual" => StudioZoomOrigin.Manual,
        "auto" => StudioZoomOrigin.Auto,
        _ => default,
    }) != default || value == "manual";
    protected override string Write(StudioZoomOrigin value) => value == StudioZoomOrigin.Auto ? "auto" : "manual";
}

internal sealed class StudioMouseButtonJsonConverter : StudioEnumJsonConverter<StudioMouseButton>
{
    protected override StudioMouseButton DefaultValue => StudioMouseButton.Left;
    protected override bool TryRead(string value, out StudioMouseButton result) => (result = value switch
    {
        "left" => StudioMouseButton.Left,
        "right" => StudioMouseButton.Right,
        "middle" => StudioMouseButton.Middle,
        "other" => StudioMouseButton.Other,
        _ => default,
    }) != default || value == "left";
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
    protected override bool TryRead(string value, out StudioCaptureKind result) => (result = value switch
    {
        "display" => StudioCaptureKind.Display,
        "region" => StudioCaptureKind.Region,
        "window" => StudioCaptureKind.Window,
        _ => default,
    }) != default || value == "display";
    protected override string Write(StudioCaptureKind value) => value switch
    {
        StudioCaptureKind.Region => "region",
        StudioCaptureKind.Window => "window",
        _ => "display",
    };
}
