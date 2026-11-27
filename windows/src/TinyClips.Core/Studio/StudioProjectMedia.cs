using System.Text.Json;

namespace TinyClips.Core.Studio;

internal static class StudioProjectMedia
{
    internal const string RecordingBackgroundImageKey = "recordingBackgroundImage";

    internal static string? RecordingBackgroundImage(StudioMediaSource source)
    {
        if (!source.ExtensionData.TryGetValue(RecordingBackgroundImageKey, out var image)
            || image.ValueKind == JsonValueKind.Null) return null;
        if (image.ValueKind != JsonValueKind.String)
        {
            throw new StudioProjectInvalidException("recordingBackgroundImage must be a project-relative filename.");
        }
        return image.GetString();
    }

    internal static void ValidateName(string name, string property)
    {
        if (!StudioProjectFolder.IsFileNameInFolder(name)
            || string.Equals(name, StudioProjectStore.ProjectFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "saved-project.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "imported-video-source.json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(StudioProjectFolder.ProjectFileExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw StudioProjectFolderException.FileOutsideFolder(property);
        }
    }

    internal static IReadOnlyList<(string Name, bool Required)> Assets(StudioProject project)
    {
        var names = new Dictionary<string, bool>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, bool required, string property)
        {
            ValidateName(name, property);
            if (!names.ContainsKey(name) && !seen.Add(name))
                throw StudioProjectFolderException.SameFileTwice(name);
            names[name] = required || names.GetValueOrDefault(name);
        }
        void Sources(StudioSources sources, bool registry)
        {
            if (sources.Screen.External) throw StudioProjectFolderException.ExternalSource();
            Add(sources.Screen.File, true, "sources.screen.file");
            if (sources.Camera is { } camera) Add(camera.File, true, "sources.camera.file");
            if (sources.Events is { } events)
                Add(events, registry, "sources.events");
            else if (!registry) Add(StudioProjectStore.EventsFileName, false, "sources.events");
        }

        Sources(project.Sources, registry: false);
        foreach (var source in project.MediaSources ?? [])
        {
            Sources(source.Sources, registry: true);
            if (RecordingBackgroundImage(source) is { } image)
                Add(image, true, RecordingBackgroundImageKey);
        }
        if (project.Canvas.Background.Image is { } background)
            Add(background, true, "canvas.background.image");
        Add(StudioProjectStore.PosterFileName, false, "poster");
        return names.Select(pair => (pair.Key, pair.Value)).ToArray();
    }

    internal static IReadOnlyList<(string Source, string Name)> Files(StudioProject project, string directory, string? staging = null)
    {
        if (StudioProjectFolder.IsLink(directory))
            throw StudioProjectFolderException.FileOutsideFolder("project directory");
        var files = new List<(string Source, string Name)>();
        foreach (var (name, required) in Assets(project))
        {
            var path = staging is not null && File.Exists(Path.Combine(staging, name))
                ? Path.Combine(staging, name) : Path.Combine(directory, name);
            if (StudioProjectFolder.IsLink(path))
                throw StudioProjectFolderException.FileOutsideFolder(name);
            if (File.Exists(path)) files.Add((path, name));
            else if (required) throw StudioProjectFolderException.MissingFile(name);
        }
        return files;
    }
}
