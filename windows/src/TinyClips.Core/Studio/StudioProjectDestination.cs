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

    public static string LibraryFolder(IStudioProjectStore store, string id)
    {
        if (!store.Exists(id)) throw new DirectoryNotFoundException("The Studio project no longer exists.");
        try
        {
            if (Read(store, id) is { } savedFile && Path.GetDirectoryName(savedFile) is { } folder
                && Directory.Exists(folder))
                return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or StudioProjectInvalidException or StudioUnsupportedSchemaVersionException)
        {
            // Even a damaged manifest or an inaccessible saved copy has a working folder.
            System.Diagnostics.Debug.WriteLine($"Using Studio's working folder for {id}: {ex.Message}");
        }
        return store.GetPaths(id).ProjectDirectory;
    }

    /// <returns>True when this is the first destination recorded for the project.</returns>
    public static bool Remember(IStudioProjectStore store, string id, string file)
    {
        var path = RecordPath(store, id);
        var firstSave = !File.Exists(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(Path.GetFullPath(file)));
            File.Move(temporary, path, overwrite: true);
            return firstSave;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static void Update(
        IStudioProjectStore store, string id, string file,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Update(store, id, file, progress, cancellationToken, beforeCommit: null);

    internal static void Update(
        IStudioProjectStore store, string id, string file,
        IProgress<double>? progress, CancellationToken cancellationToken, Action? beforeCommit)
    {
        lock (SaveLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(file);
            var linkedFolder = Path.GetDirectoryName(target)!;
            if (!Directory.Exists(linkedFolder) || StudioProjectFolder.IsLink(linkedFolder))
            {
                throw new IOException("The saved project folder is unavailable. Use Save as to choose a destination.");
            }

            var folder = StudioExportTarget.ResolvePath(linkedFolder);
            var manifest = Path.GetFileName(target);
            target = Path.Combine(folder, manifest);
            RequirePlain(target);
            byte[] original;
            HashSet<string> owned;
            // The ownership decision and the bytes compared at commit must describe the same manifest.
            using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                owned = OwnedFiles(StudioProjectFolder.ReadProjectFile(target));
                original = File.ReadAllBytes(target);
            }

            var staging = Path.Combine(folder, $".tinyclips-update-{Guid.NewGuid():N}");
            var backups = Path.Combine(staging, "backups");
            var installed = new List<string>();
            var moved = new List<string>();
            var keepBackup = false;
            using var media = new StudioLinkedMedia();
            try
            {
                var snapshot = StudioProjectStore.PlanFolderSnapshot(store, id);
                var changed = new List<(string Source, string Name)>();
                var replace = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { manifest };
                for (var index = 0; index < snapshot.Files.Count; index++)
                {
                    var asset = snapshot.Files[index];
                    var name = asset.Name;
                    if (!StudioProjectFolder.IsFileNameInFolder(name) || string.Equals(name, manifest, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException($"The project cannot be saved with the asset name {name}. Use Save as.");
                    }

                    var destination = Path.Combine(folder, name);
                    var exists = File.Exists(destination);
                    if (Directory.Exists(destination) || StudioProjectFolder.IsLink(destination) || (exists && !owned.Contains(name)))
                    {
                        throw new IOException($"The project was not saved because {name} is not a replaceable project file. Use Save as.");
                    }

                    if (exists)
                    {
                        replace.Add(name);
                        if (media.CanKeep(asset.Source, destination,
                            progress is null ? null : fraction => progress.Report((index + fraction) / (2 * snapshot.Files.Count)),
                            cancellationToken)) continue;
                    }
                    changed.Add(asset);
                }

                Directory.CreateDirectory(staging);
                StudioProjectStore.CopyFiles(staging, changed, FileShare.ReadWrite | FileShare.Delete,
                    progress is null ? null : new CopyProgress(progress), cancellationToken);
                snapshot.WriteManifest(Path.Combine(staging, manifest));
                beforeCommit?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                RequirePlain(target);
                if (!string.Equals(folder, StudioExportTarget.ResolvePath(linkedFolder), StringComparison.OrdinalIgnoreCase)
                    || !File.ReadAllBytes(target).SequenceEqual(original))
                {
                    throw new IOException("The saved project changed while saving. Save again or use Save as.");
                }

                Directory.CreateDirectory(backups);
                foreach (var name in changed.Select(asset => asset.Name).Append(manifest))
                {
                    var destination = Path.Combine(folder, name);
                    if (replace.Contains(name))
                    {
                        RequirePlain(destination);
                        media.ReleaseForReplacement(destination);
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

    private sealed class CopyProgress(IProgress<double> progress) : IProgress<double>
    {
        // Verification uses the first half, staging the files that differed uses the rest.
        public void Report(double value) => progress.Report((1 + value) / 2);
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
