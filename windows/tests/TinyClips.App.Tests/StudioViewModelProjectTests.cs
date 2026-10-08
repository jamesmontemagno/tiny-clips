using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Tests;

/// <summary>
/// The project as a whole, as far as the editor's view model goes: what Open recent lists,
/// when Open, Save and Delete are passed on to the window, and what a save says and shows.
/// The questions themselves are the window's, and are checked by StudioWindowCheck.
/// </summary>
public sealed class StudioViewModelProjectTests : StudioViewModelTestBase, IDisposable
{
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "TinyClipsAppTests", "saved-" + Guid.NewGuid().ToString("N"));

    public StudioViewModelProjectTests()
    {
        Directory.CreateDirectory(_outside);
    }

    [Fact]
    public async Task OpenRecent_ListsTheOtherProjects_TheOneOpenedLastFirst_EachWithWhenItWasRecorded()
    {
        var first = CreateProject();
        Advance(TimeSpan.FromHours(1));
        var second = CreateProject();
        Advance(TimeSpan.FromHours(1));
        var mine = CreateProject();
        var gone = CreateProject();
        File.Delete(Projects.GetPaths(gone).ScreenPath);

        // Opened in this order: the first one last.
        Advance(TimeSpan.FromHours(1));
        Projects.MarkOpened(second);
        Advance(TimeSpan.FromHours(1));
        Projects.MarkOpened(first);
        var viewModel = await OpenAsync(mine);
        var changes = 0;
        viewModel.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(StudioViewModel.RecentProjects) ? 1 : 0;
        Assert.Empty(viewModel.RecentProjects);

        await viewModel.RefreshRecentProjectsAsync();

        // Read on another thread, and shown only once the UI thread has come to it.
        Assert.Empty(viewModel.RecentProjects);
        Pump();

        // Not the project the menu belongs to, and not one whose recording is gone.
        Assert.Equal([first, second], viewModel.RecentProjects.Select(project => project.Id));
        var summaries = Projects.ListSummaries().ToDictionary(summary => summary.Id);
        Assert.Equal(
            [StudioProjectFolderText.GetRecentTitle(summaries[first]), StudioProjectFolderText.GetRecentTitle(summaries[second])],
            viewModel.RecentProjects.Select(project => project.Title));
        Assert.All(viewModel.RecentProjects, project => Assert.StartsWith("Recording, ", project.Title, StringComparison.Ordinal));
        Assert.Equal(1, changes);

        // Read again and found the same: the menu is not told to fill itself again.
        await viewModel.RefreshRecentProjectsAsync();
        Pump();
        Assert.Equal(1, changes);

        // Another project is deleted elsewhere: the next read drops it.
        Projects.Delete(first);
        await viewModel.RefreshRecentProjectsAsync();
        Pump();
        Assert.Equal([second], viewModel.RecentProjects.Select(project => project.Id));
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task OpenRecent_OfAnEditorThatHasClosed_ChangesNothing()
    {
        CreateProject();
        var viewModel = await OpenAsync(CreateProject());
        var read = viewModel.RefreshRecentProjectsAsync();
        await CloseAsync(viewModel);

        await read;
        Pump();

        Assert.Empty(viewModel.RecentProjects);
    }

    [Fact]
    public async Task OpenSaveAndDelete_ArePassedOnToTheWindow_WhenTheyCanBeDone()
    {
        var viewModel = await OpenAsync(CreateProject());
        var asked = Listen(viewModel);

        viewModel.RequestOpenProject();
        viewModel.RequestSaveProject();
        viewModel.RequestDeleteProject();

        Assert.Equal(["open", "save", "delete"], asked);
        Assert.True(viewModel.CanSaveProject);
        Assert.True(viewModel.CanDeleteProject);

        // The keys come the same way: Ctrl+O and Ctrl+S, once the window has asked what they mean.
        asked.Clear();
        viewModel.Run(StudioShortcutAction.OpenProject);
        viewModel.Run(StudioShortcutAction.SaveProject);
        Assert.Equal(["open", "save"], asked);

        // Each of the three asks something first, so none of them is an edit.
        Assert.False(viewModel.CanUndo);
        Assert.Empty(Preview.Updates);
    }

    [Fact]
    public async Task SaveAndDelete_StopPlayback_BeforeTheWindowAsksItsQuestion()
    {
        var viewModel = await OpenAsync(CreateProject());
        viewModel.TogglePlayback();
        Assert.True(viewModel.IsPlaying);

        viewModel.RequestSaveProject();
        Assert.False(viewModel.IsPlaying);

        viewModel.TogglePlayback();
        viewModel.RequestDeleteProject();
        Assert.False(viewModel.IsPlaying);
    }

    [Fact]
    public async Task AProjectThatCannotBeShown_CanBeDeleted_AndNotSaved()
    {
        var id = CreateProject();
        File.Delete(Projects.GetPaths(id).ScreenPath);
        var viewModel = await OpenAsync(id, expectReady: false);
        var asked = Listen(viewModel);

        viewModel.RequestSaveProject();
        viewModel.Run(StudioShortcutAction.SaveProject);
        viewModel.RequestDeleteProject();

        Assert.Equal(["delete"], asked);
        Assert.False(viewModel.CanSaveProject);
        Assert.True(viewModel.CanDeleteProject);
        Assert.Equal("Delete \u201CRecording\u201D?", viewModel.DeleteProjectTitle);
        Assert.Equal(
            "The recording and every edit are removed from Tiny Clips Studio. Videos you exported and folders you saved this project to are not deleted. This cannot be undone.",
            viewModel.DeleteProjectMessage);
    }

    [Fact]
    public async Task AnEditorThatHasClosed_PassesNothingOn()
    {
        var viewModel = await OpenAsync(CreateProject());
        var asked = Listen(viewModel);
        await CloseAsync(viewModel);

        viewModel.RequestOpenProject();
        viewModel.RequestSaveProject();
        viewModel.RequestDeleteProject();

        Assert.Empty(asked);
    }

    [Fact]
    public async Task ASave_SaysThatItIsSaving_TakesNoEditsMeanwhile_ThenSaysWhereTheFolderIs_AndAsksForItToBeShown()
    {
        var id = CreateProject(project => project with { Name = "My: Demo" });
        var viewModel = await OpenAsync(id);
        var saved = new List<string>();
        viewModel.ProjectSaved += (_, e) => saved.Add(e.ProjectFilePath);
        Assert.Equal("My- Demo", viewModel.SuggestedProjectFolderName);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), viewModel.ProjectSaveFolder);
        viewModel.CanvasPadding = 0.12;
        var target = viewModel.CheckSaveTarget(_outside, viewModel.SuggestedProjectFolderName);
        Assert.Equal(StudioSaveTargetKind.Free, target.Kind);
        Announcements.Clear();

        var save = viewModel.SaveProjectAsync(target);

        // The copy has not come back to the UI thread: the save is under way, however fast it was.
        Assert.True(viewModel.IsSavingProject);
        Assert.False(viewModel.IsEditable);
        Assert.False(viewModel.CanSaveProject);
        Assert.False(viewModel.CanDeleteProject);
        Assert.Equal(["Saving project\u2026"], Announcements);
        Assert.Equal("Saving project\u2026", viewModel.SavingProjectText);
        var asked = Listen(viewModel);
        viewModel.CanvasPadding = 0.3;
        viewModel.RequestSaveProject();
        viewModel.RequestDeleteProject();
        Assert.Equal(0.12, viewModel.CanvasPadding, Precision);
        Assert.Empty(asked);

        Assert.Equal(StudioProjectFolderSaveOutcome.Saved, await RunToEndAsync(save));

        var file = Path.Combine(_outside, "My- Demo", "My- Demo.tinyclips");
        Assert.False(viewModel.IsSavingProject);
        Assert.True(viewModel.IsEditable);
        Assert.Equal(100, viewModel.SaveProjectProgressPercent, Precision);
        Assert.Equal([file], saved);
        Assert.Equal(["Saving project\u2026", "Project saved to the folder My- Demo."], Announcements);
        Assert.False(viewModel.HasError);

        // The folder has the edit the editor held, and the next save starts where this one went.
        Assert.Equal(0.12, Projects.OpenProjectFolder(file, cancellationToken: TestContext.Current.CancellationToken).Canvas.Padding, Precision);
        Assert.Equal(_outside, viewModel.ProjectSaveFolder);
    }

    [Fact]
    public async Task ASaveTheStoreRefuses_IsSaidInTheMessageBar_InTheStoresSentence_AndNothingIsShown()
    {
        var viewModel = await OpenAsync(CreateProject());
        var saved = new List<string>();
        viewModel.ProjectSaved += (_, e) => saved.Add(e.ProjectFilePath);
        var target = viewModel.CheckSaveTarget(_outside, "My Demo");

        // Something takes the name between the look and the save.
        Directory.CreateDirectory(target.Folder);
        File.WriteAllText(Path.Combine(target.Folder, "holiday.jpg"), "a picture");
        var outcome = await RunToEndAsync(viewModel.SaveProjectAsync(target));

        Assert.Equal(StudioProjectFolderSaveOutcome.Failed, outcome);
        Assert.Equal(
            "Studio could not save this project: There is already something with that name, and it is not a saved Tiny Clips project. Choose another name.",
            viewModel.ErrorMessage);
        Assert.Empty(saved);
        Assert.Equal(["holiday.jpg"], Directory.GetFileSystemEntries(target.Folder).Select(Path.GetFileName));
        Assert.True(viewModel.IsEditable);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), viewModel.ProjectSaveFolder);
    }

    [Fact]
    public async Task APlaceThatCannotTakeTheProject_IsNotSavedTo()
    {
        var viewModel = await OpenAsync(CreateProject());
        Directory.CreateDirectory(Path.Combine(_outside, "Taken"));

        foreach (var target in new[] { viewModel.CheckSaveTarget(_outside, "Taken"), viewModel.CheckSaveTarget(_outside, "a/b"), viewModel.CheckSaveTarget(null, "My Demo") })
        {
            Assert.False(target.CanSave);
            Assert.Equal(StudioProjectFolderSaveOutcome.NotStarted, await viewModel.SaveProjectAsync(target));
        }

        Assert.False(viewModel.IsSavingProject);
        Assert.Empty(Announcements);
        Assert.Equal(["Taken"], Directory.GetFileSystemEntries(_outside).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ASaveThatIsStopped_SaysSo_AndShowsNoError()
    {
        var viewModel = await OpenAsync(CreateProject());
        var saved = new List<string>();
        viewModel.ProjectSaved += (_, e) => saved.Add(e.ProjectFilePath);
        Announcements.Clear();

        var save = viewModel.SaveProjectAsync(viewModel.CheckSaveTarget(_outside, "My Demo"));
        viewModel.Run(StudioShortcutAction.CancelProjectSave);
        var outcome = await RunToEndAsync(save);

        // The copy of a few bytes may have been over before it was told to stop. Either way
        // the editor says which it was, and a save that was stopped leaves nothing.
        if (outcome == StudioProjectFolderSaveOutcome.Cancelled)
        {
            Assert.Equal(["Saving project\u2026", "Saving the project was cancelled. Nothing was saved."], Announcements);
            Assert.Empty(saved);
            Assert.Empty(Directory.GetFileSystemEntries(_outside));
        }
        else
        {
            Assert.Equal(StudioProjectFolderSaveOutcome.Saved, outcome);
            Assert.Single(saved);
        }

        Assert.False(viewModel.HasError);
        Assert.True(viewModel.IsEditable);
    }

    private static List<string> Listen(StudioViewModel viewModel)
    {
        var asked = new List<string>();
        viewModel.OpenProjectRequested += (_, _) => asked.Add("open");
        viewModel.SaveProjectRequested += (_, _) => asked.Add("save");
        viewModel.DeleteProjectRequested += (_, _) => asked.Add("delete");
        return asked;
    }

    /// <summary>Runs what was posted to the UI thread until a save has ended: its copy ends on another thread.</summary>
    private async Task<T> RunToEndAsync<T>(Task<T> task)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted)
        {
            Pump();
            Assert.True(DateTime.UtcNow < until, "The save did not end within 10 s.");
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        Pump();
        return await task;
    }

    void IDisposable.Dispose()
    {
        try
        {
            Directory.Delete(_outside, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp folder that stays behind fails no test.
        }

        base.Dispose();
    }
}
