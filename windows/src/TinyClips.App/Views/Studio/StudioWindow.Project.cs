using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Views.Studio;

// The project as a whole: the Project menu, and what Open project, Save project and Delete
// project ask before anything is done. The view model says that one of them was asked for,
// from the menu, the inspector or a key; the questions and the system's pickers are here.
public sealed partial class StudioWindow
{
    // Where the keyboard focus was when Save project was asked for, to put it back there.
    private Control? _saveAskedFrom;

    // Opening

    /// <summary>
    /// The menu is brought up to date as it opens: what cannot be done just now is greyed, and
    /// Open recent lists what was read last. The projects are read again for the next time,
    /// and the list changes under the open menu when that finds another one.
    /// </summary>
    private void OnProjectMenuOpening(object? sender, object e)
    {
        SaveProjectItem.IsEnabled = ViewModel.CanSaveProject;
        DeleteProjectItem.IsEnabled = ViewModel.CanDeleteProject;
        FillOpenRecent();
        _ = ViewModel.RefreshRecentProjectsAsync();
    }

    private void FillOpenRecent()
    {
        OpenRecentItem.Items.Clear();
        var recent = ViewModel.RecentProjects;
        if (recent.Count == 0)
        {
            OpenRecentItem.Items.Add(new MenuFlyoutItem { Text = StudioProjectFolderText.NoOtherProjects, IsEnabled = false });
            return;
        }

        foreach (var project in recent)
        {
            var item = new MenuFlyoutItem { Text = project.Title, Tag = project.Id };
            AutomationProperties.SetAutomationId(item, $"StudioOpenRecentProject_{project.Id}");
            item.Click += OnOpenRecentProjectClick;
            OpenRecentItem.Items.Add(item);
        }
    }

    private void OnOpenRecentProjectClick(object sender, RoutedEventArgs e)
    {
        if (!_isClosed && sender is FrameworkElement { Tag: string projectId })
        {
            OpenRecentProject(projectId);
        }
    }

    /// <summary>Opens another project of the store in its own editor, or brings that editor to the front.</summary>
    internal void OpenRecentProject(string projectId)
    {
        try
        {
            _projects.Open(projectId);
        }
        catch (Exception ex)
        {
            ViewModel.ShowError($"The project could not be opened: {ex.Message}");
        }
    }

    private async void OnOpenProjectRequested(object? sender, EventArgs e)
    {
        if (_isClosed || _isPromptOpen)
        {
            return;
        }

        try
        {
            if (await _projects.ChooseProjectFileAsync(this) is { Length: > 0 } path)
            {
                await OpenProjectFileAsync(path);
            }
        }
        catch (Exception ex)
        {
            Say($"The project could not be chosen: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens a saved project from its <c>.tinyclips</c> file in an editor of its own. This
    /// editor stays as it is. Why a project could not be opened is said in this window's
    /// message bar, or by the app when this window has closed by then. Never fails.
    /// </summary>
    internal async Task OpenProjectFileAsync(string path)
    {
        var result = await _projects.OpenProjectFileAsync(path);
        if (result.Failure is { } failure)
        {
            Say(failure);
        }
    }

    private void Say(string message)
    {
        if (_isClosed)
        {
            _projects.Tell(message);
        }
        else
        {
            ViewModel.ShowError(message);
        }
    }

    // Saving

    private async void OnSaveProjectRequested(object? sender, EventArgs e)
    {
        if (_isClosed || _isPromptOpen)
        {
            return;
        }

        _saveAskedFrom = RootGrid.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as Control : null;
        try
        {
            if (await AskWhereToSaveAsync() is { } target && !_isClosed)
            {
                await ViewModel.SaveProjectAsync(target);
            }
        }
        catch (Exception ex)
        {
            Say($"Studio could not save this project: {ex.Message}");
        }
    }

    /// <summary>
    /// Asks for a name and a place until the project can be saved there, or the user gives
    /// up. A saved project that is in the way, and holds nothing else, is asked about; said
    /// no to, the name is asked for again. Null when nothing is to be saved.
    /// </summary>
    private async Task<StudioSaveTarget?> AskWhereToSaveAsync()
    {
        var name = ViewModel.SuggestedProjectFolderName;
        var place = ViewModel.ProjectSaveFolder;
        while (!_isClosed && ViewModel.CanSaveProject)
        {
            var dialog = new StudioSaveProjectDialog(name, place, ViewModel.CheckSaveTarget, () => _projects.ChooseSaveFolderAsync(this));
            var answer = await ShowPromptAsync(dialog);
            if (answer != ContentDialogResult.Primary || dialog.Target is not { } target)
            {
                return null;
            }

            if (!target.Replaces || await AskWhetherToReplaceAsync(target))
            {
                return target;
            }

            name = dialog.FolderName;
            place = dialog.Place;
        }

        return null;
    }

    private async Task<bool> AskWhetherToReplaceAsync(StudioSaveTarget target)
    {
        var dialog = new ContentDialog
        {
            Title = StudioProjectFolderText.GetReplaceTitle(target.Folder),
            Content = target.Message,
            PrimaryButtonText = "Replace",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        return await ShowPromptAsync(dialog) == ContentDialogResult.Primary && !_isClosed;
    }

    private void OnProjectSaved(object? sender, StudioProjectSavedEventArgs e)
    {
        if (!_isClosed)
        {
            _projects.Reveal(e.ProjectFilePath);
        }
    }

    private async Task AskAboutRunningProjectSaveAsync()
    {
        var dialog = new ContentDialog
        {
            Title = StudioProjectFolderText.CloseWhileSavingTitle,
            Content = StudioProjectFolderText.CloseWhileSavingMessage,
            PrimaryButtonText = "Stop and close",
            CloseButtonText = "Keep saving",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await ShowPromptAsync(dialog) == ContentDialogResult.Primary && !_isClosed)
        {
            // Closing the session stops the save.
            CloseWithoutAsking();
        }
    }

    // Deleting

    /// <summary>
    /// Delete project asks first, with the project's name, and says what is not deleted. On
    /// yes the window closes, which stops an export that runs and deletes the project in the
    /// store, as choosing Delete does when a recording that was never exported is closed.
    /// </summary>
    private async void OnDeleteProjectRequested(object? sender, EventArgs e)
    {
        if (_isClosed || _isPromptOpen)
        {
            return;
        }

        try
        {
            var dialog = new ContentDialog
            {
                Title = ViewModel.DeleteProjectTitle,
                Content = ViewModel.DeleteProjectMessage,
                PrimaryButtonText = "Delete project",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await ShowPromptAsync(dialog) == ContentDialogResult.Primary && !_isClosed && ViewModel.CanDeleteProject)
            {
                _deleteOnClose = true;
                CloseWithoutAsking();
            }
        }
        catch (Exception ex)
        {
            Say($"The project could not be deleted: {ex.Message}");
        }
    }
}
