namespace TinyClips.Core.Studio.Editing;

/// <summary>
/// The Segoe Fluent Icons glyphs on the Studio editor's buttons, by what the button does. A
/// button that adds or shows what a panel edits has that panel's glyph, so the rail, the
/// timeline's buttons and the panel's own button show one picture for one thing. The same on the
/// Mac (<c>StudioSymbol</c> in <c>StudioInspectorPanel.swift</c>).
/// </summary>
/// <remarks>
/// They are here and not in the app because the rail's glyphs are here, which four of them are,
/// and because here the markup of the app, the tests of both test projects and the window check
/// can all name them. Segoe MDL2 Assets has each of them as well, as it has the rail's, but for
/// the mark of a suggestion, which is the one a zoom's block already carries.
/// </remarks>
public static class StudioGlyphs
{
    /// <summary>Split and Split scene: a frame in two parts (DockLeft).</summary>
    public static string SplitScene => "\uE90C";

    /// <summary>Add zoom, in the timeline row and in the Zoom panel: the Zoom panel's glyph.</summary>
    public static string AddZoom => StudioInspectorPanels.GetGlyph(StudioInspectorPanel.Zoom);

    /// <summary>Cut and Add cut: the Cut panel's glyph.</summary>
    public static string AddCut => StudioInspectorPanels.GetGlyph(StudioInspectorPanel.Cut);

    /// <summary>Speed and Add speed change: the Speed panel's glyph.</summary>
    public static string AddSpeed => StudioInspectorPanels.GetGlyph(StudioInspectorPanel.Speed);

    /// <summary>Suggest zooms: the mark a suggested zoom has on its block (Effects).</summary>
    public static string SuggestZooms => "\uE794";

    /// <summary>Remove suggestions: a cross in a ring (ErrorBadge).</summary>
    public static string RemoveSuggestions => "\uEA39";

    /// <summary>Delete scene, zoom, cut and speed change, and Delete on the question on closing: a bin (Delete).</summary>
    public static string Delete => "\uE74D";

    /// <summary>Start here: an arrow up to a line on its left, where the video starts (ImportMirrored).</summary>
    public static string TrimStart => "\uEA52";

    /// <summary>End here: an arrow up to a line on its right, where the video ends (Import).</summary>
    public static string TrimEnd => "\uE8B5";

    /// <summary>Reset crop: an arrow going round against the clock (UpdateRestore).</summary>
    public static string ResetCrop => "\uE777";

    /// <summary>Show scene, on the Camera panel while the camera is hidden: the Scene panel's glyph.</summary>
    public static string ShowScene => StudioInspectorPanels.GetGlyph(StudioInspectorPanel.Scene);

    /// <summary>Save as default look: a palette (Color).</summary>
    public static string SaveDefaultLook => "\uE790";

    /// <summary>Save screen recording, on a project that cannot be shown: a disk (Save).</summary>
    public static string SaveRecording => "\uE74E";

    /// <summary>Open project: an open folder (FolderOpen).</summary>
    public static string OpenProject => "\uE838";

    /// <summary>Open recent: a clock (Recent).</summary>
    public static string OpenRecentProject => "\uE823";

    /// <summary>Save project: the disk that saves the screen recording.</summary>
    public static string SaveProject => SaveRecording;

    /// <summary>Delete project: the bin of the other Delete buttons.</summary>
    public static string DeleteProject => Delete;

    /// <summary>Every glyph the editor names, the rail's included.</summary>
    public static IReadOnlyList<string> All =>
    [
        .. StudioInspectorPanels.All.Select(StudioInspectorPanels.GetGlyph),
        SplitScene, AddZoom, AddCut, AddSpeed, SuggestZooms, RemoveSuggestions, Delete,
        TrimStart, TrimEnd, ResetCrop, ShowScene, SaveDefaultLook, SaveRecording,
        OpenProject, OpenRecentProject, SaveProject, DeleteProject,
    ];
}
