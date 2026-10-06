namespace TinyClips.Core.Studio.Editing;

/// <summary>
/// The panels of the Studio inspector, in the order of the rail. One is on show at a time. The
/// rail down the inspector's edge chooses which, and so does taking hold of something on the
/// timeline or in the preview.
/// </summary>
public enum StudioInspectorPanel
{
    /// <summary>The scene the playhead is in: its layout, splitting it, how it is entered, when it starts.</summary>
    Scene,

    /// <summary>What is behind the screen and the camera, and how much of it shows around them.</summary>
    Background,

    /// <summary>The screen recording: corners, shadow, click highlights and crop.</summary>
    Screen,

    /// <summary>The camera: where it is in the scene, how it looks, and its crop.</summary>
    Camera,

    /// <summary>The zooms, and the selected one.</summary>
    Zoom,

    /// <summary>The cuts, and the selected one.</summary>
    Cut,

    /// <summary>The speed changes, and the selected one.</summary>
    Speed,

    /// <summary>The sound of the video.</summary>
    Audio,

    /// <summary>What is true of the whole project and not of a part of the picture.</summary>
    Project,
}

/// <summary>
/// What the Studio inspector's rail shows for a recording: which panels there are, in which
/// groups, which one a project opens on, and which one is shown when one is asked for that the
/// recording does not have. The same on the Mac (<c>StudioInspectorPanel.swift</c>).
/// </summary>
public static class StudioInspectorPanels
{
    // Glyphs of Segoe Fluent Icons, which Segoe MDL2 Assets has as well, by the names the font's
    // documentation gives them.
    private const string MoviesGlyph = "\uE8B2";
    private const string PictureGlyph = "\uE8B9";
    private const string MonitorGlyph = "\uE7F4";
    private const string WebcamGlyph = "\uE8B8";
    private const string ZoomInGlyph = "\uE8A3";
    private const string CutGlyph = "\uE8C6";
    private const string SpeedHighGlyph = "\uEC4A";
    private const string VolumeGlyph = "\uE767";
    private const string FolderGlyph = "\uE8B7";

    private static readonly StudioInspectorPanel[] LookWithCamera =
    [
        StudioInspectorPanel.Scene,
        StudioInspectorPanel.Background,
        StudioInspectorPanel.Screen,
        StudioInspectorPanel.Camera,
    ];

    private static readonly StudioInspectorPanel[] LookWithoutCamera =
    [
        StudioInspectorPanel.Background,
        StudioInspectorPanel.Screen,
    ];

    private static readonly StudioInspectorPanel[] TimelineEdits =
    [
        StudioInspectorPanel.Zoom,
        StudioInspectorPanel.Cut,
        StudioInspectorPanel.Speed,
    ];

    private static readonly StudioInspectorPanel[] Rest =
    [
        StudioInspectorPanel.Audio,
        StudioInspectorPanel.Project,
    ];

    private static readonly IReadOnlyList<StudioInspectorPanel>[] GroupsWithCamera = [LookWithCamera, TimelineEdits, Rest];
    private static readonly IReadOnlyList<StudioInspectorPanel>[] GroupsWithoutCamera = [LookWithoutCamera, TimelineEdits, Rest];
    private static readonly StudioInspectorPanel[] AvailableWithCamera = [.. LookWithCamera, .. TimelineEdits, .. Rest];
    private static readonly StudioInspectorPanel[] AvailableWithoutCamera = [.. LookWithoutCamera, .. TimelineEdits, .. Rest];

    /// <summary>Every panel, in the order of the rail.</summary>
    public static IReadOnlyList<StudioInspectorPanel> All => AvailableWithCamera;

    /// <summary>The panel's name: on its rail item, and as the heading over its controls.</summary>
    public static string GetTitle(StudioInspectorPanel panel) => panel switch
    {
        StudioInspectorPanel.Scene => "Scene",
        StudioInspectorPanel.Background => "Background",
        StudioInspectorPanel.Screen => "Screen",
        StudioInspectorPanel.Camera => "Camera",
        StudioInspectorPanel.Zoom => "Zoom",
        StudioInspectorPanel.Cut => "Cut",
        StudioInspectorPanel.Speed => "Speed",
        StudioInspectorPanel.Audio => "Audio",
        StudioInspectorPanel.Project => "Project",
        _ => string.Empty,
    };

    /// <summary>The Segoe Fluent Icons glyph on the panel's rail item.</summary>
    public static string GetGlyph(StudioInspectorPanel panel) => panel switch
    {
        StudioInspectorPanel.Scene => MoviesGlyph,
        StudioInspectorPanel.Background => PictureGlyph,
        StudioInspectorPanel.Screen => MonitorGlyph,
        StudioInspectorPanel.Camera => WebcamGlyph,
        StudioInspectorPanel.Zoom => ZoomInGlyph,
        StudioInspectorPanel.Cut => CutGlyph,
        StudioInspectorPanel.Speed => SpeedHighGlyph,
        StudioInspectorPanel.Audio => VolumeGlyph,
        StudioInspectorPanel.Project => FolderGlyph,
        _ => string.Empty,
    };

    /// <summary>
    /// What the panel holds, for the help of its rail item. On Windows a recording has its sound
    /// in one track, so the Audio panel holds Mute and no volumes.
    /// </summary>
    public static string GetSummary(StudioInspectorPanel panel) => panel switch
    {
        StudioInspectorPanel.Scene => "Scene: layout, splits, and transitions",
        StudioInspectorPanel.Background => "Background and padding",
        StudioInspectorPanel.Screen => "Screen: corners, shadow, clicks, and crop",
        StudioInspectorPanel.Camera => "Camera: placement, appearance, and crop",
        StudioInspectorPanel.Zoom => "Zooms",
        StudioInspectorPanel.Cut => "Cuts",
        StudioInspectorPanel.Speed => "Speed changes",
        StudioInspectorPanel.Audio => "Audio: mute",
        StudioInspectorPanel.Project => "Project: badge, storage, and default look",
        _ => string.Empty,
    };

    /// <summary>
    /// The rail's panels in order, in the groups it draws a line between: how the picture looks,
    /// what is edited along the timeline, and the rest. A recording without a camera has one
    /// layout and nothing to place, so it has neither a Scene nor a Camera panel.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<StudioInspectorPanel>> GetGroups(bool hasCamera) =>
        hasCamera ? GroupsWithCamera : GroupsWithoutCamera;

    /// <summary>The panels a recording has, in the order of the rail.</summary>
    public static IReadOnlyList<StudioInspectorPanel> GetAvailable(bool hasCamera) =>
        hasCamera ? AvailableWithCamera : AvailableWithoutCamera;

    /// <summary>The panel a project opens on.</summary>
    public static StudioInspectorPanel GetInitial(bool hasCamera) =>
        hasCamera ? StudioInspectorPanel.Scene : StudioInspectorPanel.Background;

    /// <summary>
    /// The panel to show when <paramref name="panel"/> is asked for: itself, or its nearest
    /// neighbor when the recording has no camera.
    /// </summary>
    public static StudioInspectorPanel Resolve(StudioInspectorPanel panel, bool hasCamera)
    {
        if (hasCamera)
        {
            return panel;
        }

        return panel switch
        {
            StudioInspectorPanel.Scene => StudioInspectorPanel.Background,
            StudioInspectorPanel.Camera => StudioInspectorPanel.Screen,
            _ => panel,
        };
    }
}
