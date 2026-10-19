using System.Text.Json;

namespace TinyClips.Core.Studio;

/// <summary>Machine-local links to saved projects; never included in portable snapshots.</summary>
public static class StudioProjectDestination
{
    private static readonly object SaveLock = new();

    private static string RecordPath(IStudioProjectStore store, string id)
    {
        if (OwnedFiles(store.Load(id)).Contains("saved-project.json"))
        {
            throw new InvalidDataException("A project asset uses Studio's local destination filename. Rename that asset before saving.");
        }
        return Path.Combine(store.GetPaths(id).ProjectDirectory, "saved-project.json");
    }

    public static string? Read(IStudioProjectStore store, string id)
    {
        var path = RecordPath(store, id);
        if (!File.Exists(path)) return null;
        var file = JsonSerializer.Deserialize<string>(File.ReadAllText(path));
        if (string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file)
            || !file.EndsWith(StudioProjectFolder.ProjectFileExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The saved project destination is invalid. Use Save as to choose it again.");
        }
        return file;
    }

    public static void Remember(IStudioProjectStore store, string id, string file)
    {
        var path = RecordPath(store, id);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(Path.GetFullPath(file)));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static void Update(
        IStudioProjectStore store, string id, string file,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        lock (SaveLock)
        {
            var target = Path.GetFullPath(file);
            var folder = Path.GetDirectoryName(target)!;
            RequirePlain(target);
            if (!Directory.Exists(folder) || StudioProjectFolder.IsLink(folder))
            {
                throw new IOException("The saved project folder is unavailable. Use Save as to choose a destination.");
            }

            var original = File.ReadAllBytes(target);
            var oldProject = StudioProjectFolder.ReadProjectFile(target);
            var owned = OwnedFiles(oldProject);
            owned.Add(Path.GetFileName(target));
            var staging = Path.Combine(folder, $".tinyclips-update-{Guid.NewGuid():N}");
            var backups = Path.Combine(staging, "backups");
            var installed = new List<string>();
            var moved = new List<string>();
            var keepBackup = false;
            try
            {
                store.SaveProjectFolder(id, staging, progress: progress, cancellationToken: cancellationToken);
                var stagedFile = Path.Combine(staging, Path.GetFileName(staging) + StudioProjectFolder.ProjectFileExtension);
                File.Move(stagedFile, Path.Combine(staging, Path.GetFileName(target)));
                var names = Directory.GetFiles(staging).Select(Path.GetFileName).OfType<string>()
                    .Where(name => !string.Equals(name, Path.GetFileName(target), StringComparison.OrdinalIgnoreCase))
                    .Append(Path.GetFileName(target)).ToArray();
                foreach (var name in names)
                {
                    var destination = Path.Combine(folder, name);
                    if (Directory.Exists(destination) || StudioProjectFolder.IsLink(destination)
                        || (File.Exists(destination) && !owned.Contains(name)))
                    {
                        throw new IOException($"The project was not saved because {name} is not a replaceable project file. Use Save as.");
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!File.ReadAllBytes(target).SequenceEqual(original))
                {
                    throw new IOException("The saved project changed while saving. Save again or use Save as.");
                }
                Directory.CreateDirectory(backups);
                foreach (var name in names)
                {
                    var destination = Path.Combine(folder, name);
                    if (File.Exists(destination))
                    {
                        File.Move(destination, Path.Combine(backups, name));
                        moved.Add(name);
                    }

                    File.Move(Path.Combine(staging, name), destination);
                    installed.Add(name);
                }
            }
            catch (Exception failure)
            {
                try
                {
                    foreach (var name in installed.AsEnumerable().Reverse())
                    {
                        File.Delete(Path.Combine(folder, name));
                    }

                    foreach (var name in moved.AsEnumerable().Reverse())
                    {
                        File.Move(Path.Combine(backups, name), Path.Combine(folder, name));
                    }
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    keepBackup = true;
                    throw new IOException($"Saving failed and the original files could not all be restored. Backups are in {backups}.",
                        new AggregateException(failure, rollback));
                }

                throw;
            }
            finally
            {
                if (!keepBackup && Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
        }
    }

    public static HashSet<string> OwnedFiles(StudioProject project)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            project.Sources.Screen.File,
            project.Sources.Events ?? StudioProjectStore.EventsFileName,
            StudioProjectStore.PosterFileName,
        };
        if (project.Sources.Camera is { } camera) names.Add(camera.File);
        if (project.Canvas.Background.Image is { } image) names.Add(image);
        return names;
    }

    private static void RequirePlain(string file)
    {
        if (!File.Exists(file) || StudioProjectFolder.IsLink(file))
        {
            throw new IOException("The saved project file is unavailable. Use Save as to choose a destination.");
        }
    }
}
