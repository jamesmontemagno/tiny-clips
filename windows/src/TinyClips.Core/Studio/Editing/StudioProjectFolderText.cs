using System.Globalization;

namespace TinyClips.Core.Studio.Editing;

/// <summary>What is at the place a project is about to be saved to as a folder, and so what is said or asked first.</summary>
public enum StudioSaveTargetKind
{
    /// <summary>Nothing is there. The folder is made without a question.</summary>
    Free,

    /// <summary>
    /// A saved project is there, with nothing in it but what a save writes. It is replaced
    /// after the user has said so: <see cref="StudioSaveTarget.Message"/> is the question.
    /// </summary>
    SavedProject,

    /// <summary>
    /// A saved project is there, and it holds something that is not part of it, which a
    /// replacement would take with it. Nothing is asked and nothing replaced:
    /// <see cref="StudioSaveTarget.Message"/> names what it holds.
    /// </summary>
    SavedProjectWithOtherFiles,

    /// <summary>Something else is there: a file, a folder that is not a saved project, or one whose project file cannot be read. The name is taken.</summary>
    Taken,

    /// <summary>The name cannot be a folder's. <see cref="StudioSaveTarget.Message"/> offers one that can.</summary>
    UnusableName,

    /// <summary>The folder the project was to be saved in is not there, or none was chosen.</summary>
    NoPlace,
}

/// <summary>What <see cref="StudioProjectFolderText.CheckSaveTarget"/> found.</summary>
/// <param name="Kind">What is there.</param>
/// <param name="Folder">The full path of the folder that would be made. Empty where there is none to make.</param>
/// <param name="Message">
/// For <see cref="StudioSaveTargetKind.SavedProject"/> the question to ask, for the kinds that
/// refuse the sentence to show, and empty for <see cref="StudioSaveTargetKind.Free"/>.
/// </param>
public sealed record StudioSaveTarget(StudioSaveTargetKind Kind, string Folder, string Message)
{
    /// <summary>Whether a save may go ahead: at once, or once the question has been answered with yes.</summary>
    public bool CanSave => Kind is StudioSaveTargetKind.Free or StudioSaveTargetKind.SavedProject;

    /// <summary>What to pass the store as <c>replaceSavedProject</c> once the user has agreed.</summary>
    public bool Replaces => Kind == StudioSaveTargetKind.SavedProject;
}

/// <summary>
/// The words the app has for a project that is saved as a folder, opened from one, or deleted:
/// what it asks, and what it says when the store or the system refuses. Kept apart from the
/// windows so that every sentence can be tested.
/// </summary>
public static class StudioProjectFolderText
{
    /// <summary>How many other projects an Open recent menu lists at most. The Mac's number.</summary>
    public const int RecentProjectLimit = 8;

    /// <summary>What the editor shows, and a screen reader says, while the recordings are copied.</summary>
    public const string SavingMessage = "Saving project\u2026";

    /// <summary>Said to a screen reader when a save was stopped.</summary>
    public const string SaveCancelledMessage = "Saving the project was cancelled. Nothing was saved.";

    /// <summary>What the Open recent menu shows when there is no other project.</summary>
    public const string NoOtherProjects = "No other projects";

    /// <summary>Said when a <c>.tinyclips</c> file is opened while Studio is switched off. Nothing is imported then.</summary>
    public const string StudioIsOffMessage =
        "Tiny Clips Studio is switched off. Switch it on in Settings, under Studio, to open this project.";

    /// <summary>Said when the recordings of a project that is being opened take a while to copy.</summary>
    public static string GetOpeningMessage(string path) =>
        $"Opening {Path.GetFileName(Path.TrimEndingDirectorySeparator(path))}. Its recordings are being copied into Tiny Clips Studio.";

    /// <summary>The question a close asks while the project is being saved as a folder.</summary>
    public const string CloseWhileSavingTitle = "The project is still being saved.";

    /// <summary>What that question says under its title.</summary>
    public const string CloseWhileSavingMessage =
        "Closing the window stops the save, and nothing is left of the folder that was being written. Your edits are kept.";

    /// <summary>The question Delete project asks: its title, with the project's name.</summary>
    public static string GetDeleteTitle(string? projectName) =>
        $"Delete \u201C{StudioEditorText.GetClipName(projectName)}\u201D?";

    /// <summary>What Delete project says under its question.</summary>
    public const string DeleteMessage =
        "The recording and every edit are removed from Tiny Clips Studio. Videos you exported and folders you saved this project to are not deleted. This cannot be undone.";

    /// <summary>Said once a project has been saved as a folder.</summary>
    public static string GetSavedMessage(string projectFilePath) =>
        $"Project saved to the folder {Path.GetFileName(Path.GetDirectoryName(projectFilePath))}.";

    /// <summary>
    /// Why a project was not saved as a folder, for the user. The store's own refusals are
    /// sentences already; what the system refused with is passed on as it is.
    /// </summary>
    public static string GetSaveFailure(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return $"Studio could not save this project: {Reason(failure)}";
    }

    /// <summary>Why a project was not opened from a <c>.tinyclips</c> file or its folder.</summary>
    public static string GetOpenFailure(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return $"Studio could not open this project: {Reason(failure)}";
    }

    /// <summary>
    /// A line of the Open recent menu: the project's name with when it was recorded, because
    /// two recordings can have one name.
    /// </summary>
    /// <param name="formatProvider">How the date is written. The user's own way when null.</param>
    /// <param name="timeZone">The time zone the date is shown in. This computer's when null.</param>
    public static string GetRecentTitle(StudioProjectSummary summary, IFormatProvider? formatProvider = null, TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return $"{StudioEditorText.GetClipName(summary.Name)}, {When(summary.CreatedAt, formatProvider, timeZone)}";
    }

    /// <summary>
    /// A line of the tray menu's recent captures for a project that holds a recording no file
    /// has yet: its name, what it is, and when it was last worked on.
    /// </summary>
    public static string GetTrayDraftTitle(StudioProjectSummary draft, IFormatProvider? formatProvider = null, TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return $"{StudioEditorText.GetClipName(draft.Name)} \u2014 Studio project, {When(draft.LastUsedAt, formatProvider, timeZone)}";
    }

    /// <summary>
    /// The other projects, for an Open recent menu: the one opened last first, without the
    /// project the menu belongs to, <see cref="RecentProjectLimit"/> at most.
    /// </summary>
    public static IReadOnlyList<StudioProjectSummary> GetRecentProjects(IEnumerable<StudioProjectSummary> summaries, string? currentProjectId) =>
        StudioProjectSummary.Recent(summaries, currentProjectId, RecentProjectLimit);

    /// <summary>
    /// Looks at where a project is about to be saved as a folder, before anything is copied,
    /// and says which of the cases it is. Only reads.
    /// </summary>
    /// <param name="parentFolder">The folder the project's folder is to be made in.</param>
    /// <param name="folderName">The name the user gave the project's folder.</param>
    public static StudioSaveTarget CheckSaveTarget(string? parentFolder, string? folderName)
    {
        var name = folderName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return new StudioSaveTarget(StudioSaveTargetKind.UnusableName, string.Empty, "Give the folder a name.");
        }

        // The same rule the suggested name is made by: what it would change cannot be a folder's name.
        var usable = StudioProjectFolder.FolderName(name);
        if (!string.Equals(usable, name, StringComparison.Ordinal))
        {
            return new StudioSaveTarget(
                StudioSaveTargetKind.UnusableName,
                string.Empty,
                $"A folder cannot be called that. Try \u201C{usable}\u201D.");
        }

        if (string.IsNullOrWhiteSpace(parentFolder))
        {
            return new StudioSaveTarget(StudioSaveTargetKind.NoPlace, string.Empty, "Choose a folder to save the project in.");
        }

        string parent;
        string target;
        try
        {
            parent = Path.GetFullPath(parentFolder);
            target = Path.Combine(parent, name);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new StudioSaveTarget(StudioSaveTargetKind.NoPlace, string.Empty, "Choose a folder to save the project in.");
        }

        if (!Directory.Exists(parent))
        {
            return new StudioSaveTarget(
                StudioSaveTargetKind.NoPlace,
                target,
                $"The folder {parent} is not there any more. Choose another folder.");
        }

        if (!Directory.Exists(target) && !File.Exists(target))
        {
            return new StudioSaveTarget(StudioSaveTargetKind.Free, target, string.Empty);
        }

        if (!StudioProjectFolder.IsSavedProjectFolder(target))
        {
            return new StudioSaveTarget(StudioSaveTargetKind.Taken, target, Reason(StudioProjectFolder.WhyASaveWouldNotReplace(target)));
        }

        return StudioProjectFolder.WhyASaveWouldNotReplace(target) switch
        {
            null => new StudioSaveTarget(
                StudioSaveTargetKind.SavedProject,
                target,
                $"The folder \u201C{name}\u201D in {parent} already holds a saved Tiny Clips project. Replacing it deletes the recordings and the project file that are in it now. This cannot be undone."),
            { Problem: StudioProjectFolderProblem.DestinationHasOtherFiles } refusal => new StudioSaveTarget(
                StudioSaveTargetKind.SavedProjectWithOtherFiles,
                target,
                refusal.Message),

            // One project file, which cannot be read as a project: nobody can say what belongs to it.
            _ => new StudioSaveTarget(
                StudioSaveTargetKind.Taken,
                target,
                "There is already a folder with that name, and its project file cannot be read, so it was not replaced. Choose another name."),
        };
    }

    /// <summary>The title of the question that <see cref="StudioSaveTargetKind.SavedProject"/> asks.</summary>
    public static string GetReplaceTitle(string folderPath) =>
        $"Replace the saved project \u201C{Path.GetFileName(Path.TrimEndingDirectorySeparator(folderPath))}\u201D?";

    // The store's refusals are sentences. What the system refused with is one as well, in
    // the system's words: a disk that is full, a folder that may not be written.
    private static string Reason(Exception? failure) => failure switch
    {
        // Nothing was there to refuse by the time it was asked: the name is as good as taken.
        null => "There is already something with that name. Choose another name.",
        _ => string.IsNullOrWhiteSpace(failure.Message) ? "Something went wrong." : failure.Message.Trim(),
    };

    private static string When(DateTimeOffset time, IFormatProvider? formatProvider, TimeZoneInfo? timeZone) =>
        TimeZoneInfo.ConvertTime(time, timeZone ?? TimeZoneInfo.Local).ToString("g", formatProvider ?? CultureInfo.CurrentCulture);
}
