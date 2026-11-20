using System.Text.Json.Nodes;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Automation;

/// <summary>Exports an unlinked, new-folder-only Studio snapshot within the video-save folder.</summary>
public static class AutomationStudioProjectCopy
{
    public static async Task<JsonObject> SaveAsync(
        IStudioProjectStore store,
        string projectId,
        string folderName,
        string videoFolder,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!StudioProjectFolder.IsFileNameInFolder(folderName)
            || folderName.IndexOfAny(['/', '\\', ':', '<', '>', '"', '|', '?', '*']) >= 0
            || folderName.Any(char.IsControl))
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams, "folderName must be a plain folder name.");
        }

        var root = StudioExportTarget.ResolvePath(videoFolder);
        var projectDirectory = StudioExportTarget.ResolvePath(store.GetPaths(projectId).ProjectDirectory);
        if (string.Equals(root, projectDirectory, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(Path.TrimEndingDirectorySeparator(projectDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams, "The video folder cannot be inside the Studio project.");
        }

        var folder = Path.Combine(root, folderName);
        try
        {
            Directory.CreateDirectory(root);
            await Task.Run(
                () => store.SaveProjectFolder(projectId, folder, cancellationToken: cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (StudioProjectFolderException ex) when (ex.Problem == StudioProjectFolderProblem.DestinationExists)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams, "The project folder already exists; choose a new name.");
        }
        catch (Exception ex) when (ex is StudioProjectFolderException or IOException or UnauthorizedAccessException)
        {
            throw new AutomationException(AutomationErrorCodes.Internal, $"Studio could not save this project: {ex.Message}");
        }

        return new JsonObject
        {
            ["projectId"] = projectId,
            ["folderPath"] = folder,
            ["projectFile"] = Path.Combine(folder, folderName + StudioProjectFolder.ProjectFileExtension),
        };
    }
}
