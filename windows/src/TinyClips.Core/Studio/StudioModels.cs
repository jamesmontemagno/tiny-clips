using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyClips.Core.Studio;

[JsonConverter(typeof(StudioCanvasAspectJsonConverter))]
public enum StudioCanvasAspect
{
    Auto,
    Square,
    Landscape4X3,
    Landscape16X9,
    Portrait3X4,
    Portrait9X16,
}

[JsonConverter(typeof(StudioBackgroundStyleJsonConverter))]
public enum StudioBackgroundStyle
{
    None,
    Solid,
    Gradient,
    Image,
}

[JsonConverter(typeof(StudioCameraShapeJsonConverter))]
public enum StudioCameraShape
{
    Circle,
    RoundedRectangle,
    Squircle,
    Rectangle,
}

[JsonConverter(typeof(StudioCameraCutoutJsonConverter))]
public enum StudioCameraCutout
{
    None,
    Blur,
    Remove,
}

[JsonConverter(typeof(StudioLayoutJsonConverter))]
public enum StudioLayout
{
    Screen,
    Bubble,
    SideBySide,
    Camera,
}

[JsonConverter(typeof(StudioAnchorJsonConverter))]
public enum StudioAnchor
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

[JsonConverter(typeof(StudioCameraSideJsonConverter))]
public enum StudioCameraSide
{
    Leading,
    Trailing,
}

[JsonConverter(typeof(StudioTransitionKindJsonConverter))]
public enum StudioTransitionKind
{
    Cut,
    Morph,
}

[JsonConverter(typeof(StudioZoomFocusModeJsonConverter))]
public enum StudioZoomFocusMode
{
    Point,
    Cursor,
}

[JsonConverter(typeof(StudioZoomOriginJsonConverter))]
public enum StudioZoomOrigin
{
    Manual,
    Auto,
}

[JsonConverter(typeof(StudioMouseButtonJsonConverter))]
public enum StudioMouseButton
{
    Left,
    Right,
    Middle,
    Other,
}

[JsonConverter(typeof(StudioCaptureKindJsonConverter))]
public enum StudioCaptureKind
{
    Display,
    Region,
    Window,
}

public sealed record StudioProject
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UnixEpoch;
    public DateTimeOffset ModifiedAt { get; init; } = DateTimeOffset.UnixEpoch;
    public DateTimeOffset LastOpenedAt { get; init; } = DateTimeOffset.UnixEpoch;
    public StudioAppInfo App { get; init; } = new();
    public bool KeepSources { get; init; }
    public StudioSources Sources { get; init; } = new();
    public StudioCanvas Canvas { get; init; } = new();
    public StudioScreenStyle Screen { get; init; } = new();
    public StudioCameraStyle Camera { get; init; } = new();
    public StudioScene[] Scenes { get; init; } = [new()];
    public StudioZoom[] Zooms { get; init; } = [];
    public StudioEdits Edits { get; init; } = new();
    public StudioAudio Audio { get; init; } = new();
    public StudioOverlays Overlays { get; init; } = new();
    public StudioExport[] Exports { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioAppInfo
{
    public string Platform { get; init; } = "windows";
    public string Version { get; init; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioSources
{
    public StudioScreenSource Screen { get; init; } = new();
    public StudioCameraSource? Camera { get; init; }
    public string? Events { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioScreenSource
{
    public string File { get; init; } = "screen.mp4";
    public int Width { get; init; }
    public int Height { get; init; }
    public double FrameRate { get; init; } = 30;
    public double Duration { get; init; }
    public bool External { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioCameraSource
{
    public string File { get; init; } = "camera.mp4";
    public int Width { get; init; }
    public int Height { get; init; }
    public double Duration { get; init; }
    public double StartOffset { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioCanvas
{
    public StudioCanvasAspect Aspect { get; init; } = StudioCanvasAspect.Auto;
    public double Padding { get; init; } = 0.06;
    public StudioBackground Background { get; init; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioBackground
{
    public StudioBackgroundStyle Style { get; init; } = StudioBackgroundStyle.Gradient;
    public string? Preset { get; init; } = "ocean";
    public string Primary { get; init; } = "#2687E8";
    public string? Secondary { get; init; } = "#2EE0BF";
    public string? Image { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioScreenStyle
{
    public double CornerRadius { get; init; } = 0.02;
    public double Shadow { get; init; } = 0.5;
    public StudioRect? Crop { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioCameraStyle
{
    public StudioCameraShape Shape { get; init; } = StudioCameraShape.Circle;
    public double CornerRadius { get; init; } = 0.12;
    public bool Mirror { get; init; } = true;
    public double BorderWidth { get; init; }
    public string BorderColor { get; init; } = "#FFFFFF";
    public double Shadow { get; init; } = 0.35;
    public StudioRect? Crop { get; init; }
    public StudioCameraCutout Cutout { get; init; } = StudioCameraCutout.None;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioLook(StudioCanvas Canvas, StudioScreenStyle Screen, StudioCameraStyle Camera);

public sealed record StudioScene
{
    public double Start { get; init; }
    public StudioLayout Layout { get; init; } = StudioLayout.Bubble;
    public StudioBubble Bubble { get; init; } = new();
    public StudioSplit Split { get; init; } = new();
    public StudioTransition Transition { get; init; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioBubble
{
    public StudioAnchor Anchor { get; init; } = StudioAnchor.BottomRight;
    public double Size { get; init; } = 0.24;
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioSplit
{
    public StudioCameraSide CameraSide { get; init; } = StudioCameraSide.Trailing;
    public double CameraFraction { get; init; } = 0.3;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioTransition
{
    public StudioTransitionKind Kind { get; init; } = StudioTransitionKind.Cut;
    public double Duration { get; init; } = 0.35;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioZoom
{
    public double Start { get; init; }
    public double End { get; init; }
    public double Scale { get; init; } = 1;
    public StudioZoomFocus Focus { get; init; } = new();
    public double EaseIn { get; init; }
    public double EaseOut { get; init; }
    public StudioZoomOrigin Origin { get; init; } = StudioZoomOrigin.Manual;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioZoomFocus
{
    public StudioZoomFocusMode Mode { get; init; } = StudioZoomFocusMode.Point;
    public double X { get; init; } = 0.5;
    public double Y { get; init; } = 0.5;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioEdits
{
    public double TrimStart { get; init; }
    public double? TrimEnd { get; init; }
    public StudioTimeRange[] Cuts { get; init; } = [];
    public StudioSpeedRange[] Speed { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioTimeRange
{
    public double Start { get; init; }
    public double End { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioSpeedRange
{
    public double Start { get; init; }
    public double End { get; init; }
    public double Rate { get; init; } = 1;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioAudio
{
    public bool Muted { get; init; }
    public double SystemVolume { get; init; } = 1;
    public double MicrophoneVolume { get; init; } = 1;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioOverlays
{
    public StudioClickOverlay Clicks { get; init; } = new();
    public bool Branding { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioClickOverlay
{
    public bool Enabled { get; init; } = true;
    public string Color { get; init; } = "#0A84FF";
    public double Size { get; init; } = 40;
    public double StrokeWidth { get; init; } = 3;
    public double Opacity { get; init; } = 0.85;
    public double Duration { get; init; } = 0.45;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioExport
{
    public string Path { get; init; } = string.Empty;
    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.UnixEpoch;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioRect
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];

    public StudioRect()
    {
    }

    public StudioRect(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }
}

public sealed record StudioEvents
{
    public int SchemaVersion { get; init; } = StudioProject.CurrentSchemaVersion;
    public StudioCaptureInfo Capture { get; init; } = new();
    public StudioClickEvent[] Clicks { get; init; } = [];
    public StudioCursorSample[] Cursor { get; init; } = [];
    public StudioCameraCornerEvent[] CameraCorners { get; init; } = [];
    public JsonElement[] Markers { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioCaptureInfo
{
    public int Width { get; init; }
    public int Height { get; init; }
    public double Scale { get; init; } = 1;
    public StudioCaptureKind Kind { get; init; } = StudioCaptureKind.Display;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioClickEvent
{
    public double T { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public StudioMouseButton Button { get; init; } = StudioMouseButton.Left;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioCursorSample
{
    public double T { get; init; }
    public double X { get; init; }
    public double Y { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public sealed record StudioCameraCornerEvent
{
    public double T { get; init; }
    public StudioAnchor Corner { get; init; } = StudioAnchor.BottomRight;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}
