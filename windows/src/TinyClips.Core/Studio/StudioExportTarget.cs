namespace TinyClips.Core.Studio;

/// <summary>An explicit user-selected filename, including the file whose replacement was approved.</summary>
public sealed record StudioExportTarget(string Path, long? Length, DateTime? LastWrite)
{
    public static StudioExportTarget Capture(string path)
    {
        var file = new FileInfo(System.IO.Path.GetFullPath(path));
        return new(file.FullName, file.Exists ? file.Length : null, file.Exists ? file.LastWriteTimeUtc : null);
    }

    public void Commit(string stagedPath)
    {
        var current = Capture(Path);
        if (current != this)
        {
            throw new IOException("The selected output file changed while exporting. Export again to confirm its replacement.");
        }

        File.Move(stagedPath, Path, overwrite: Length.HasValue);
    }

    public void Validate(StudioProject project, StudioProjectPaths paths, string? savedProjectFile)
    {
        var protectedPaths = StudioProjectDestination.OwnedFiles(project)
            .Select(name => System.IO.Path.Combine(paths.ProjectDirectory, name)).ToList();
        protectedPaths.Add(System.IO.Path.Combine(paths.ProjectDirectory, StudioProjectStore.ProjectFileName));
        if (project.Sources.Screen.External) protectedPaths.Add(project.Sources.Screen.File);
        if (savedProjectFile is not null)
        {
            var saved = File.Exists(savedProjectFile) ? StudioProjectFolder.ReadProjectFile(savedProjectFile) : project;
            protectedPaths.AddRange(StudioProjectDestination.OwnedFiles(saved)
                .Select(name => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(savedProjectFile)!, name)));
            protectedPaths.Add(savedProjectFile);
        }

        if (StudioProjectFolder.IsLink(Path)
            || protectedPaths.Any(file => string.Equals(System.IO.Path.GetFullPath(file), Path, StringComparison.OrdinalIgnoreCase))
            || Path.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(paths.ProjectDirectory) + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Choose another filename: exporting here would overwrite project data or a source recording.");
        }
    }
}
