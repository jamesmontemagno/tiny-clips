namespace TinyClips.Core.Studio.Editing;

/// <summary>The lanes of the Studio timeline that can come and go. The trim bar under them always shows.</summary>
public enum StudioTimelineLane
{
    Scene,
    Zoom,
    Cut,
    Speed,
    Annotate,
}

/// <summary>
/// Which lanes the Studio timeline shows for a project, so that a lane takes room only when it has
/// something on it. The buttons, the keys and the inspector add the first zoom, cut, speed change or
/// annotation without a lane. The same on the Mac (<c>StudioTimelineLane</c> in
/// <c>StudioInspectorPanel.swift</c>).
/// </summary>
public static class StudioTimelineLanes
{
    private static readonly StudioTimelineLane[] All =
    [
        StudioTimelineLane.Scene,
        StudioTimelineLane.Zoom,
        StudioTimelineLane.Cut,
        StudioTimelineLane.Speed,
        StudioTimelineLane.Annotate,
    ];

    /// <summary>The lanes a project shows, from the top down.</summary>
    public static IReadOnlyList<StudioTimelineLane> GetShown(StudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return [.. All.Where(lane => IsShown(lane, project))];
    }

    /// <summary>
    /// Whether a lane shows. The scene lane needs a camera and more than one scene: one scene has
    /// nothing to choose between, and its panel still edits it.
    /// </summary>
    public static bool IsShown(StudioTimelineLane lane, StudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return lane switch
        {
            StudioTimelineLane.Scene => project.Sources.Camera is not null && StudioLayoutResolver.NormalizeScenes(project.Scenes).Length > 1,
            StudioTimelineLane.Zoom => project.Zooms.Length > 0,
            StudioTimelineLane.Cut => project.Edits.Cuts.Length > 0,
            StudioTimelineLane.Speed => project.Edits.Speed.Length > 0,
            StudioTimelineLane.Annotate => project.Overlays.Annotations.Length > 0,
            _ => false,
        };
    }
}
