using System.Diagnostics;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Core.Studio;

/// <summary>
/// The way out for a recording that Studio cannot show: its screen recording, copied to where
/// saved videos go, as an ordinary video. The project is left as it is.
/// </summary>
public static class StudioScreenRecording
{
    /// <summary>Said when a project has no screen recording in its folder.</summary>
    public const string NothingToSaveMessage = "This project has no screen recording to save.";

    /// <summary>Said while the recording is being copied.</summary>
    public const string SavingMessage = "Saving the screen recording\u2026";

    /// <summary>Said once the recording has been saved, with the name the video got.</summary>
    public static string GetSavedMessage(string path) => $"Saved as {Path.GetFileName(path)}.";

    /// <summary>Said when the recording could not be saved, with the reason.</summary>
    public static string GetNotSavedMessage(string reason) => $"The screen recording could not be saved: {reason}";

    /// <summary>
    /// Copies a project's screen recording to a new video file and returns the path it got. The
    /// copy never takes the place of a file that is there.
    /// </summary>
    /// <param name="createOutputPath">
    /// Returns the full path to write, in the folder and with the name the app gives a saved
    /// video. Called on the caller's thread, when the caller awaits this where it has a
    /// synchronization context: once before the copy, and again when a file has taken that name
    /// by the time the copy is finished.
    /// </param>
    /// <exception cref="FileNotFoundException">The project has no screen recording in its folder.</exception>
    public static async Task<string> SaveAsync(
        IStudioProjectStore store,
        string projectId,
        Func<string> createOutputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(createOutputPath);

        var sourcePath = store.FindScreenRecording(projectId) ?? throw new FileNotFoundException(NothingToSaveMessage);
        var wantedPath = createOutputPath();

        // Copied under a name of its own first, in the same folder, so that no half-written
        // video is ever to be seen under a video's name.
        var stagedPath = StudioRenderingMath.StagedOutputPath(wantedPath);
        try
        {
            if (Path.GetDirectoryName(stagedPath) is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
            }

            // The context is kept on purpose: the name is asked for again on the caller's thread.
            await CopyAsync(sourcePath, stagedPath, cancellationToken);
            return StudioFilePlacement.Place(stagedPath, wantedPath, createOutputPath, "the recording was being copied");
        }
        catch
        {
            DeleteQuietly(stagedPath);
            throw;
        }
    }

    private static async Task CopyAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        const int bufferSize = 1 << 20;

        // An editor that has the recording open reads it and nothing more, so reading beside it
        // is allowed for.
        var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (source.ConfigureAwait(false))
        {
            var target = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize,
                FileOptions.Asynchronous);
            await using (target.ConfigureAwait(false))
            {
                await source.CopyToAsync(target, bufferSize, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Studio could not remove {path}: {ex.Message}");
        }
    }
}
