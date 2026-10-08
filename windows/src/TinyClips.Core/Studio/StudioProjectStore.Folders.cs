using System.Buffers;
using System.Diagnostics;

namespace TinyClips.Core.Studio;

// A project saved as a folder, and opened from one: section 14 of the project format.
public sealed partial class StudioProjectStore
{
    private const int FolderCopyPieceBytes = 1 << 20;

    public void SaveProjectFolder(
        string projectId,
        string folder,
        bool replaceSavedProject = false,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        cancellationToken.ThrowIfCancellationRequested();

        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var name = Path.GetFileName(target);
        var parent = Path.GetDirectoryName(target);
        if (name.Length == 0 || string.IsNullOrEmpty(parent))
        {
            throw new ArgumentException("A project is saved as a folder with a name, inside another folder.", nameof(folder));
        }

        string directory;
        var names = new List<string>();
        string json;
        lock (_sync)
        {
            var project = LoadProjectFile(ProjectDirectory(projectId), projectId);
            if (project.Sources.Screen.External)
            {
                throw StudioProjectFolderException.ExternalSource();
            }

            var paths = BuildPaths(projectId, project);
            directory = paths.ProjectDirectory;

            void Require(string path)
            {
                if (!File.Exists(path))
                {
                    throw StudioProjectFolderException.MissingFile(Path.GetFileName(path));
                }

                IncludeIfPresent(path);
            }

            void IncludeIfPresent(string path)
            {
                var fileName = Path.GetFileName(path);
                if (File.Exists(path) && !names.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(fileName);
                }
            }

            Require(paths.ScreenPath);
            if (paths.CameraPath is { } cameraPath)
            {
                Require(cameraPath);
            }

            IncludeIfPresent(paths.EventsPath);
            if (project.Canvas.Background.Image is { } image && StudioProjectJson.IsPlainFileName(image))
            {
                IncludeIfPresent(Path.Combine(directory, image));
            }

            IncludeIfPresent(paths.PosterPath);

            // Where a video was exported to is a path on this computer, with its user's name in
            // it, and means nothing on another one.
            json = StudioProjectJson.WriteProject(project with { Exports = [] });
        }

        RefuseWhatMayNotBeReplaced(target, replaceSavedProject);

        // Not made here: a save that then failed would leave it behind.
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException($"The folder {parent} does not exist.");
        }

        // Filled beside where it will be, under a name of its own, and put under its name once
        // it is whole: no half of a project is ever to be seen under a project's name.
        var filling = Path.Combine(parent, $"{name}.saving-{ShortUniqueName()}");
        try
        {
            Directory.CreateDirectory(filling);

            // An editor that has a recording open reads it and nothing more, so reading beside
            // it is allowed for.
            CopyFiles(directory, filling, names, FileShare.ReadWrite | FileShare.Delete, progress, cancellationToken);

            // Written last, and the last to be put in place: a folder with its .tinyclips file
            // in it is a whole copy. The file has the name the folder will have.
            var projectFile = name + StudioProjectFolder.ProjectFileExtension;
            using (var stream = new FileStream(Path.Combine(filling, projectFile), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
            }

            cancellationToken.ThrowIfCancellationRequested();
            PutInPlace(filling, [.. names, projectFile], target, replaceSavedProject);
        }
        finally
        {
            // What is left of it: everything where the save did not finish, and where it did,
            // the folder the files were moved out of.
            DeleteFolderQuietly(filling);
        }
    }

    public StudioProject OpenProjectFolder(
        string path,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var file = StudioProjectFolder.FindProjectFile(path);
        var project = StudioProjectFolder.ReadProjectFile(file);
        if (project.Sources.Screen.External)
        {
            throw StudioProjectFolderException.ExternalSource();
        }

        var folder = Path.GetDirectoryName(file)!;
        var names = new List<string>();

        // Only the name of a file in the folder is followed, so a project file cannot name
        // something outside it. Two names for one file, or the name the store keeps the
        // project itself under, would have one copy overwrite another.
        void Follow(string fileName, string property)
        {
            if (!StudioProjectFolder.IsFileNameInFolder(fileName) || StudioProjectFolder.IsLink(Path.Combine(folder, fileName)))
            {
                throw StudioProjectFolderException.FileOutsideFolder(property);
            }

            if (names.Contains(fileName, StringComparer.OrdinalIgnoreCase)
                || string.Equals(fileName, ProjectFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw StudioProjectFolderException.SameFileTwice(fileName);
            }
        }

        void Require(string fileName, string property)
        {
            Follow(fileName, property);
            if (!File.Exists(Path.Combine(folder, fileName)))
            {
                throw StudioProjectFolderException.MissingFile(fileName);
            }

            names.Add(fileName);
        }

        void IncludeIfPresent(string fileName)
        {
            if (StudioProjectFolder.IsFileNameInFolder(fileName)
                && !string.Equals(fileName, ProjectFileName, StringComparison.OrdinalIgnoreCase)
                && !names.Contains(fileName, StringComparer.OrdinalIgnoreCase)
                && !StudioProjectFolder.IsLink(Path.Combine(folder, fileName))
                && File.Exists(Path.Combine(folder, fileName)))
            {
                names.Add(fileName);
            }
        }

        Require(project.Sources.Screen.File, "sources.screen.file");
        if (project.Sources.Camera is { } camera)
        {
            Require(camera.File, "sources.camera.file");
        }

        if (project.Sources.Events is { } events)
        {
            Follow(events, "sources.events");
        }

        IncludeIfPresent(project.Sources.Events ?? EventsFileName);
        if (project.Canvas.Background.Image is { } image)
        {
            IncludeIfPresent(image);
        }

        IncludeIfPresent(PosterFileName);

        // The new folder has no project.json until the files are in it. Cleanup takes such a
        // folder for a recording that is still being made, and leaves it alone for a day.
        string projectId;
        string directory;
        lock (_sync)
        {
            projectId = Guid.NewGuid().ToString("D");
            directory = ProjectDirectory(projectId);
            Directory.CreateDirectory(directory);
        }

        try
        {
            CopyFiles(folder, directory, names, FileShare.Read, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // An id of its own, and nothing exported on this computer, whatever the file says.
            // Its times stay what the file says: opening it in an editor writes when.
            var opened = project with { Id = projectId, Exports = [] };
            lock (_sync)
            {
                SaveProjectFile(directory, opened);
            }

            return opened;
        }
        catch
        {
            lock (_sync)
            {
                DeleteFolderQuietly(directory);
            }

            throw;
        }
    }

    /// <summary>
    /// Refuses a save where something is at the target that may not be replaced: anything
    /// that is not a saved project, a saved project when replacing was not asked for, and one
    /// that holds something a save does not write
    /// (<see cref="StudioProjectFolder.WhyASaveWouldNotReplace"/>).
    /// </summary>
    private static void RefuseWhatMayNotBeReplaced(string target, bool replaceSavedProject)
    {
        if (File.Exists(target)
            || (Directory.Exists(target) && !(replaceSavedProject && StudioProjectFolder.IsSavedProjectFolder(target))))
        {
            throw StudioProjectFolderException.DestinationExists();
        }

        if (StudioProjectFolder.WhyASaveWouldNotReplace(target) is { } refusal)
        {
            throw refusal;
        }
    }

    /// <summary>
    /// Puts a folder that was filled beside its place under its name: the name is taken with
    /// an empty folder, and the files follow one by one, the project file last. A folder that
    /// was just filled cannot be renamed while a virus scanner or the search index still looks
    /// at a file in it, and each of its files can be.
    /// </summary>
    private static void PutInPlace(string filled, IReadOnlyList<string> names, string target, bool replaceSavedProject)
    {
        // The one that is there steps aside, the new one takes its name, and only then does
        // the old one go. Where the new one cannot take the name, the old one gets it back.
        string? aside = null;
        if (Directory.Exists(target) || File.Exists(target))
        {
            // Asked again: copying took a while, and what is there now is what would be lost.
            RefuseWhatMayNotBeReplaced(target, replaceSavedProject);
            var steppedAside = $"{target}.replaced-{ShortUniqueName()}";
            Insist(() => Directory.Move(target, steppedAside));
            aside = steppedAside;
        }

        var taken = false;
        try
        {
            // Renaming fails where something has the name by now, which making a folder does not.
            var empty = $"{target}.placing-{ShortUniqueName()}";
            Directory.CreateDirectory(empty);
            try
            {
                Insist(() => Directory.Move(empty, target));
                taken = true;
            }
            catch
            {
                DeleteFolderQuietly(empty);
                throw;
            }

            foreach (var name in names)
            {
                Insist(() => File.Move(Path.Combine(filled, name), Path.Combine(target, name)));
            }
        }
        catch (Exception ex)
        {
            if (taken)
            {
                DeleteFolderQuietly(target);
            }

            if (aside is { } steppedAside)
            {
                try
                {
                    Insist(() => Directory.Move(steppedAside, target));
                }
                catch (Exception back) when (back is IOException or UnauthorizedAccessException)
                {
                    throw new IOException(
                        $"The project could not be saved, and the folder that was there is now called {Path.GetFileName(steppedAside)}.",
                        ex);
                }
            }

            throw;
        }

        if (aside is not null)
        {
            DeleteFolderQuietly(aside);
        }
    }

    /// <summary>
    /// Does what the system may refuse for a moment, a few times over the better part of a
    /// second. A file that was just written is looked at by a virus scanner and by the search
    /// index, and while they have it open it cannot always be renamed or removed.
    /// </summary>
    internal static void Insist(Action action)
    {
        const int attempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < attempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(25 << (attempt - 1));
            }
        }
    }

    /// <summary>
    /// Copies files from one folder into another under the names they have, in pieces, so that
    /// a copy of gigabytes can be given up between two of them. A file that is already there is
    /// not replaced: the copy fails. What is copied is the content and nothing else, so a file
    /// that may only be read does not become one that cannot be deleted where it is copied to.
    /// </summary>
    private static void CopyFiles(
        string fromFolder,
        string toFolder,
        IReadOnlyList<string> names,
        FileShare sourceSharing,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var total = names.Sum(name => new FileInfo(Path.Combine(fromFolder, name)).Length);
        long copied = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(FolderCopyPieceBytes);
        try
        {
            foreach (var name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var source = new FileStream(Path.Combine(fromFolder, name), FileMode.Open, FileAccess.Read, sourceSharing, bufferSize: 1, FileOptions.SequentialScan);
                using var target = new FileStream(Path.Combine(toFolder, name), FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1);
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    target.Write(buffer, 0, read);
                    copied += read;
                    progress?.Report(total > 0 ? Math.Min(1, (double)copied / total) : 1);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        progress?.Report(1);
    }

    /// <summary>
    /// Deletes a folder with everything in it and says nothing when it cannot. A file that may
    /// only be read is deleted as well. A link in it is removed and not followed.
    /// </summary>
    private static void DeleteFolderQuietly(string folder)
    {
        try
        {
            Insist(() => DeleteFolder(folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Studio could not remove {folder}: {ex.Message}");
        }
    }

    private static void DeleteFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            var everything = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            };
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", everything).Append(new DirectoryInfo(folder)))
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                }
            }

            Directory.Delete(folder, recursive: true);
        }
    }

    private static string ShortUniqueName() => Guid.NewGuid().ToString("N")[..8];
}
