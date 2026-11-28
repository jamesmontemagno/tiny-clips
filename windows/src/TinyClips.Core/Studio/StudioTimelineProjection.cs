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
        if (sources.Screen.External)
            throw new StudioProjectInvalidException("Timeline sources must be owned by the project, not external files.");
        return paths with
        {
            ScreenPath = OwnedPath(paths.ProjectDirectory, sources.Screen.File),
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

    internal static string OwnedPath(string directory, string file)
    {
        // Project media is portable and relative; never let a registry entry escape its folder.
        if (!StudioProjectJson.IsPlainFileName(file)
            || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || file.EndsWith('.') || file.EndsWith(' '))
        {
            throw new StudioProjectInvalidException($"Project media must be a plain file name: {file}.");
        }
        var path = Path.Combine(Path.GetFullPath(directory), file);
        if (Path.Exists(path) && (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new StudioProjectInvalidException($"Project media must be a regular file, not a link or folder: {file}.");
        return path;
    }
}
