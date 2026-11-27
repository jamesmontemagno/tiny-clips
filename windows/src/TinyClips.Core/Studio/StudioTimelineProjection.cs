namespace TinyClips.Core.Studio;

/// <summary>Source-local views of timeline clips. The root project alone owns output size and frame rate.</summary>
public static class StudioTimelineProjection
{
    public static StudioTimelineMap Map(StudioProject project) =>
        new(project.Timeline ?? throw new StudioProjectInvalidException("The project has no timeline."),
            project.MediaSources ?? throw new StudioProjectInvalidException("The project has no media sources."));

    public static double Duration(StudioProject project) =>
        project.SchemaVersion >= 2 ? Map(project).Duration : StudioTimeMap.FromProject(project).OutputDuration;

    public static StudioProject ProjectForClip(StudioProject project, string clipId)
    {
        ArgumentNullException.ThrowIfNull(project);
        var clip = project.Timeline?.Clips.FirstOrDefault(item => item.Id == clipId)
            ?? throw new StudioProjectInvalidException($"Unknown timeline clip: {clipId}.");
        var source = project.MediaSources?.FirstOrDefault(item => item.Id == clip.SourceId)
            ?? throw new StudioProjectInvalidException($"Unknown media source: {clip.SourceId}.");
        var flat = source.Kind == StudioMediaSourceKind.FlatVideo;
        var edits = clip.Edits ?? new StudioEdits();
        return project with
        {
            SchemaVersion = 1,
            Timeline = null,
            MediaSources = null,
            Sources = flat ? source.Sources with { Camera = null, Events = null } : source.Sources,
            Screen = clip.Screen ?? project.Screen,
            Camera = clip.Camera ?? project.Camera,
            Scenes = clip.Scenes ?? project.Scenes,
            Zooms = clip.Zooms ?? project.Zooms,
            Audio = clip.Audio ?? project.Audio,
            Overlays = clip.Overlays ?? project.Overlays,
            Captions = clip.Captions ?? project.Captions,
            Edits = edits with
            {
                TrimStart = Math.Max(clip.SourceStart, edits.TrimStart),
                TrimEnd = Math.Min(clip.SourceEnd, edits.TrimEnd ?? clip.SourceEnd),
            },
        };
    }

    public static StudioProjectPaths PathsForClip(StudioProject project, StudioProjectPaths paths, string clipId)
    {
        var sources = ProjectForClip(project, clipId).Sources;
        return paths with
        {
            ScreenPath = sources.Screen.External ? sources.Screen.File : OwnedPath(paths.ProjectDirectory, sources.Screen.File),
            CameraPath = sources.Camera is { } camera ? OwnedPath(paths.ProjectDirectory, camera.File) : null,
            EventsPath = OwnedPath(paths.ProjectDirectory, sources.Events ?? StudioProjectStore.EventsFileName),
        };
    }

    public static StudioEvents EventsForClip(StudioProject project, StudioProjectPaths paths, string clipId)
    {
        var local = ProjectForClip(project, clipId);
        return local.Sources.Events is null
            ? new StudioEvents()
            : StudioProjectJson.ReadEvents(File.ReadAllText(PathsForClip(project, paths, clipId).EventsPath));
    }

    private static string OwnedPath(string directory, string file)
    {
        // Project media is portable and relative; never let a registry entry escape its folder.
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(directory, file));
        if (Path.IsPathRooted(file) || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioProjectInvalidException($"Project media must stay inside its folder: {file}.");
        }
        return path;
    }
}
