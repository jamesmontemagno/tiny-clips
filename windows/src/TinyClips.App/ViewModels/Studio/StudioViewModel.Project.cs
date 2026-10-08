using System.Diagnostics;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.ViewModels.Studio;

/// <summary>A line of the editor's Open recent menu: another project, and what the menu shows for it.</summary>
/// <param name="Id">The project to open.</param>
/// <param name="Title">Its name and when it was recorded.</param>
public sealed record StudioRecentProject(string Id, string Title);

/// <summary>The <c>.tinyclips</c> file of a folder a project was just saved to.</summary>
public sealed class StudioProjectSavedEventArgs(string projectFilePath) : EventArgs
{
    public string ProjectFilePath { get; } = projectFilePath;
}

// The project as a whole: saving it as a folder, opening another, and deleting it. What each
// of them asks first is the window's to ask, so a command here only says that it was asked
// for; the words are StudioProjectFolderText's.
public sealed partial class StudioViewModel
{
    private const string ProjectSaveActivityId = "StudioProjectSaved";

    private static readonly string[] ProjectFolderPropertyNames =
    [
        nameof(SaveProjectProgressPercent),
        nameof(SaveProjectPercentText),
    ];

    private IReadOnlyList<StudioRecentProject> _recentProjects = [];
    private int _recentProjectsRead;

    /// <summary>Raised when Open project was asked for: by its menu item, or by Ctrl+O.</summary>
    public event EventHandler? OpenProjectRequested;

    /// <summary>Raised when Save project was asked for, by its buttons or by Ctrl+S, while the project can be saved.</summary>
    public event EventHandler? SaveProjectRequested;

    /// <summary>Raised when Delete project was asked for, while the project can be deleted.</summary>
    public event EventHandler? DeleteProjectRequested;

    /// <summary>Raised once the project has been saved as a folder, for the folder to be shown.</summary>
    public event EventHandler<StudioProjectSavedEventArgs>? ProjectSaved;

    /// <summary>True while the recordings are copied into a folder. The editor takes no edits then.</summary>
    public bool IsSavingProject => _session.IsSavingProjectFolder;

    public double SaveProjectProgressPercent => _session.ProjectFolderProgress * 100;

    public string SaveProjectPercentText => StudioEditorText.GetPercentText(_session.ProjectFolderProgress);

    public string SavingProjectText => StudioProjectFolderText.SavingMessage;

    /// <summary>Whether the project can be saved as a folder: it is open, and neither exporting nor being saved.</summary>
    public bool CanSaveProject => IsEditable;

    /// <summary>
    /// Whether the project can be deleted from here: also one that cannot be shown, but not
    /// while it is being opened, exported or saved as a folder.
    /// </summary>
    public bool CanDeleteProject => !IsLoading && !IsExporting && !IsSavingProject && !_session.IsClosed;

    /// <summary>The name Save project suggests for the folder: the project's, as a folder can have it.</summary>
    public string SuggestedProjectFolderName => StudioProjectFolder.FolderName(ClipName);

    /// <summary>
    /// Where Save project starts: the folder a project was last saved in, while it is still
    /// there, and otherwise Documents.
    /// </summary>
    public string ProjectSaveFolder
    {
        get
        {
            var last = _settings.StudioProjectSaveFolder;
            return !string.IsNullOrWhiteSpace(last) && Directory.Exists(last)
                ? last
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }

    /// <summary>The question Delete project asks, with the project's name.</summary>
    public string DeleteProjectTitle => StudioProjectFolderText.GetDeleteTitle(Project?.Name);

    public string DeleteProjectMessage => StudioProjectFolderText.DeleteMessage;

    /// <summary>
    /// The other projects, the one opened last first, as <see cref="RefreshRecentProjectsAsync"/>
    /// read them last. Empty until it has.
    /// </summary>
    public IReadOnlyList<StudioRecentProject> RecentProjects
    {
        get => _recentProjects;
        private set => SetProperty(ref _recentProjects, value);
    }

    /// <summary>
    /// Reads which other projects there are, for the Open recent menu. Every project folder is
    /// sized for that, so it is done off the UI thread; the list changes a moment later, on the
    /// UI thread, and only when it is another list. Never fails.
    /// </summary>
    public async Task RefreshRecentProjectsAsync()
    {
        var read = ++_recentProjectsRead;
        IReadOnlyList<StudioProjectSummary> summaries;
        try
        {
            summaries = await Task.Run(_store.ListSummaries).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Studio could not list the other projects: {ex.Message}");
            return;
        }

        _post(() =>
        {
            // A later read may have come back first.
            if (read != _recentProjectsRead || _session.IsClosed)
            {
                return;
            }

            StudioRecentProject[] recent =
            [
                .. StudioProjectFolderText.GetRecentProjects(summaries, ProjectId)
                    .Select(static summary => new StudioRecentProject(summary.Id, StudioProjectFolderText.GetRecentTitle(summary))),
            ];
            if (!recent.SequenceEqual(_recentProjects))
            {
                RecentProjects = recent;
            }
        });
    }

    /// <summary>Asks for a project that was saved as a folder to be opened. The window shows the picker.</summary>
    public void RequestOpenProject()
    {
        if (!_session.IsClosed)
        {
            OpenProjectRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Asks for this project to be saved as a folder. The window asks where.</summary>
    public void RequestSaveProject()
    {
        if (CanSaveProject)
        {
            Pause();
            SaveProjectRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Asks for this project to be deleted. The window asks first.</summary>
    public void RequestDeleteProject()
    {
        if (CanDeleteProject)
        {
            Pause();
            DeleteProjectRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>What is at the place the project is about to be saved to, and so what to say or ask first.</summary>
    public StudioSaveTarget CheckSaveTarget(string? parentFolder, string? folderName) =>
        StudioProjectFolderText.CheckSaveTarget(parentFolder, folderName);

    /// <summary>
    /// Saves a copy of the project as the folder that was checked: the recordings, and a
    /// <c>.tinyclips</c> file. A target that would replace a saved project has been asked
    /// about by now. The editor takes no edits until the copy is done. What came of it is
    /// read out, and a failure is shown in the message bar in the store's own sentence.
    /// Never fails.
    /// </summary>
    public async Task<StudioProjectFolderSaveOutcome> SaveProjectAsync(StudioSaveTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.CanSave)
        {
            return StudioProjectFolderSaveOutcome.NotStarted;
        }

        var save = _session.SaveProjectFolderAsync(target.Folder, target.Replaces);
        if (_session.IsSavingProjectFolder)
        {
            Announce(StudioProjectFolderText.SavingMessage, ProjectSaveActivityId, StudioAnnouncementKind.Information);
        }

        var result = await save;
        if (_session.IsClosed)
        {
            // The window closed while the recordings were copied, which stopped the save.
            return result.Outcome;
        }

        switch (result.Outcome)
        {
            case StudioProjectFolderSaveOutcome.Saved when result.ProjectFilePath is { } projectFile:
                // The next save starts where this one went.
                _settings.StudioProjectSaveFolder = Path.GetDirectoryName(target.Folder) ?? string.Empty;
                Announce(StudioProjectFolderText.GetSavedMessage(projectFile), ProjectSaveActivityId, StudioAnnouncementKind.Completed);
                ProjectSaved?.Invoke(this, new StudioProjectSavedEventArgs(projectFile));
                break;
            case StudioProjectFolderSaveOutcome.Cancelled:
                Announce(StudioProjectFolderText.SaveCancelledMessage, ProjectSaveActivityId, StudioAnnouncementKind.Stopped);
                break;
            case StudioProjectFolderSaveOutcome.Failed when result.Failure is { } failure:
                ShowError(StudioProjectFolderText.GetSaveFailure(failure));
                break;
        }

        return result.Outcome;
    }

    /// <summary>Stops the save that is under way. Nothing is left of it.</summary>
    public void CancelProjectSave() => _session.CancelProjectFolderSave();
}
