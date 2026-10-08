using TinyClips.Core.Models;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>
/// An editor saves its project as a folder: the edits it holds are written first, it takes no
/// edits while the recordings are copied, the save can be stopped, and a close stops it and
/// waits for it.
/// </summary>
/// <remarks>
/// The store is the real one and the copy runs on another thread, as in the app. A test that
/// has to look at the editor in the middle of a save holds the copy before it starts
/// (<see cref="RecordingStore.SavingProjectFolder"/>). What the session says of a save, it
/// says on its own thread, so not before a test has run what the session posted.
/// </remarks>
public sealed class StudioEditorSessionProjectFolderTests : StudioEditorSessionTestBase
{
    /// <summary>
    /// The folder a test saves into: one that is there, as the store asks, in the test's own
    /// temp folder, which goes when the test is over.
    /// </summary>
    private string Parent =>
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(ExportPath)!)!, "Saved")).FullName;

    private string Folder => Path.Combine(Parent, "My Demo");

    private string ProjectFile => Path.Combine(Folder, "My Demo.tinyclips");

    [Fact]
    public async Task Save_WritesTheEditsFirst_ThenMakesAFolderTheStoreOpensAgain()
    {
        var id = CreateProject(camera: true);
        var session = await OpenAsync(id);
        session.SetCanvasPadding(0.12);
        Assert.True(session.HasUnsavedEdits);
        Assert.NotEqual(0.12, Projects.Load(id).Canvas.Padding, Precision);
        Log.Clear();
        Changes.Clear();

        var save = session.SaveProjectFolderAsync(Folder);

        // Under way from the moment the call returns, and the edit is on disk by then.
        Assert.True(session.IsSavingProjectFolder);
        Assert.False(session.IsEditable);
        Assert.False(session.HasUnsavedEdits);
        Assert.Equal(0.12, Projects.Load(id).Canvas.Padding, Precision);
        Assert.Contains(StudioEditorChanges.All, Changes);

        var result = await RunToEndAsync(save);

        Assert.Equal(StudioProjectFolderSaveOutcome.Saved, result.Outcome);
        Assert.Equal(ProjectFile, result.ProjectFilePath);
        Assert.Null(result.Failure);
        Assert.False(session.IsSavingProjectFolder);
        Assert.True(session.IsEditable);
        Assert.Equal(1, session.ProjectFolderProgress, Precision);
        Assert.Empty(Errors);
        AssertLoggedInOrder("store.save", "store.saveProjectFolder", "store.saveProjectFolder.ended");
        Assert.Equal(
            ["camera.mp4", "My Demo.tinyclips", "screen.mp4"],
            Directory.GetFiles(Folder).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));

        // The folder holds the project as the editor had it, and the store opens it as a new one.
        var opened = Projects.OpenProjectFolder(ProjectFile, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(id, opened.Id);
        Assert.Equal(0.12, opened.Canvas.Padding, Precision);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Projects.GetPaths(opened).ScreenPath));

        // The project in the store is the one that goes on being edited.
        session.SetCanvasPadding(0.2);
        Assert.Equal(0.2, session.Project!.Canvas.Padding, Precision);
    }

    [Fact]
    public async Task WhileTheProjectIsSaved_TheEditorTakesNoEdits_AndStartsNothingElse()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        using var hold = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        Store.SavingProjectFolder = () =>
        {
            started.Set();
            hold.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
        };

        var save = session.SaveProjectFolderAsync(Folder);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Pump();
        var before = session.Project;
        var savesBefore = LogCount("store.save");

        session.SetCanvasPadding(0.3);
        session.SetMuted(true);
        session.Undo();
        session.TogglePlayback();
        session.StepFrames(3);
        var export = await session.ExportAsync(() => ExportPath, VideoCodec.H264);
        var second = await session.SaveProjectFolderAsync(Path.Combine(Parent, "Another"));

        Assert.True(session.IsSavingProjectFolder);
        Assert.False(session.IsEditable);
        Assert.False(session.CanExport);
        Assert.False(session.CanUndo);
        Assert.Same(before, session.Project);
        Assert.False(session.HasUnsavedEdits);
        Assert.False(session.IsPlaying);
        Assert.Equal(0, session.Playhead, Precision);
        Assert.Equal(StudioExportOutcome.NotStarted, export);
        Assert.Empty(Exporter.Exports);
        Assert.Equal(StudioProjectFolderSaveOutcome.NotStarted, second.Outcome);
        Assert.Equal(1, LogCount("store.saveProjectFolder"));
        Assert.Equal(savesBefore, LogCount("store.save"));

        // A window that is asked to close meanwhile is told what to ask.
        Assert.Equal(StudioClosePrompt.ProjectSaveRunning, session.GetClosePrompt());

        hold.Set();
        var result = await RunToEndAsync(save);

        Assert.Equal(StudioProjectFolderSaveOutcome.Saved, result.Outcome);
        Assert.False(Directory.Exists(Path.Combine(Parent, "Another")));
        Assert.True(session.IsEditable);
        Assert.NotEqual(StudioClosePrompt.ProjectSaveRunning, session.GetClosePrompt());
        session.SetCanvasPadding(0.3);
        Assert.Equal(0.3, session.Project!.Canvas.Padding, Precision);
    }

    [Fact]
    public async Task ASaveThatIsStopped_LeavesNothing_AndReportsNothing()
    {
        var session = await OpenAsync(CreateProject());
        using var hold = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        Store.SavingProjectFolder = () =>
        {
            started.Set();
            hold.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
        };

        var save = session.SaveProjectFolderAsync(Folder);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        session.CancelProjectFolderSave();
        hold.Set();
        var result = await RunToEndAsync(save);

        Assert.Equal(StudioProjectFolderSaveOutcome.Cancelled, result.Outcome);
        Assert.Null(result.ProjectFilePath);
        Assert.Null(result.Failure);
        Assert.Empty(Directory.GetFileSystemEntries(Parent));
        Assert.Empty(Errors);
        Assert.False(session.IsSavingProjectFolder);
        Assert.Equal(0, session.ProjectFolderProgress, Precision);
        Assert.True(session.IsEditable);

        // Stopping again, with nothing under way, does nothing; and the next save works.
        session.CancelProjectFolderSave();
        Store.SavingProjectFolder = null;
        Assert.Equal(StudioProjectFolderSaveOutcome.Saved, (await RunToEndAsync(session.SaveProjectFolderAsync(Folder))).Outcome);
        Assert.True(File.Exists(ProjectFile));
    }

    [Fact]
    public async Task WhatTheStoreRefuses_ComesBackInTheResult_AndIsNotReportedAsAnError()
    {
        var session = await OpenAsync(CreateProject());
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "notes.txt"), "mine");

        var result = await RunToEndAsync(session.SaveProjectFolderAsync(Folder, replaceSavedProject: true));

        Assert.Equal(StudioProjectFolderSaveOutcome.Failed, result.Outcome);
        var refusal = Assert.IsType<StudioProjectFolderException>(result.Failure);
        Assert.Equal(StudioProjectFolderProblem.DestinationExists, refusal.Problem);
        Assert.Null(result.ProjectFilePath);
        Assert.Equal(["notes.txt"], Directory.GetFileSystemEntries(Folder).Select(Path.GetFileName));
        Assert.Empty(Errors);
        Assert.False(session.IsSavingProjectFolder);
        Assert.True(session.IsEditable);
    }

    [Fact]
    public async Task ASavedProjectThatHoldsOnlyWhatASaveWrites_IsReplacedWhenThatIsAskedFor_AndOnlyThen()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        Assert.Equal(StudioProjectFolderSaveOutcome.Saved, (await RunToEndAsync(session.SaveProjectFolderAsync(Folder))).Outcome);
        session.SetCanvasPadding(0.17);

        var notAsked = await RunToEndAsync(session.SaveProjectFolderAsync(Folder));
        Assert.Equal(StudioProjectFolderSaveOutcome.Failed, notAsked.Outcome);
        Assert.Equal(StudioProjectFolderProblem.DestinationExists, Assert.IsType<StudioProjectFolderException>(notAsked.Failure).Problem);
        Assert.NotEqual(0.17, Projects.OpenProjectFolder(ProjectFile, cancellationToken: TestContext.Current.CancellationToken).Canvas.Padding, Precision);

        var asked = await RunToEndAsync(session.SaveProjectFolderAsync(Folder, replaceSavedProject: true));
        Assert.Equal(StudioProjectFolderSaveOutcome.Saved, asked.Outcome);
        Assert.Equal(0.17, Projects.OpenProjectFolder(ProjectFile, cancellationToken: TestContext.Current.CancellationToken).Canvas.Padding, Precision);
    }

    [Fact]
    public async Task EditsThatCannotBeWritten_StopTheSaveBeforeAnythingIsCopied()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        session.SetCanvasPadding(0.12);
        File.Delete(Projects.GetPaths(id).ProjectJsonPath);
        Log.Clear();

        var result = await FinishAsync(session.SaveProjectFolderAsync(Folder));

        Assert.Equal(StudioProjectFolderSaveOutcome.NotStarted, result.Outcome);
        Assert.Equal([StudioEditorErrorKind.Save], ErrorKinds);
        Assert.Equal(0, LogCount("store.saveProjectFolder"));
        Assert.False(Directory.Exists(Folder));
        Assert.False(session.IsSavingProjectFolder);
        Assert.True(session.HasUnsavedEdits);
    }

    [Fact]
    public async Task AProjectThatIsNotOpen_OrIsExporting_IsNotSaved()
    {
        var loading = CreateSession(CreateProject());
        Assert.Equal(StudioProjectFolderSaveOutcome.NotStarted, (await loading.SaveProjectFolderAsync(Folder)).Outcome);

        var missing = await OpenAsync(CreateProject(writeScreenFile: false));
        Assert.Equal(StudioEditorLoadState.Unavailable, missing.State);
        Assert.Equal(StudioProjectFolderSaveOutcome.NotStarted, (await missing.SaveProjectFolderAsync(Folder)).Outcome);

        var exporting = await OpenAsync(CreateProject());
        _ = exporting.ExportAsync(() => ExportPath, VideoCodec.H264);
        Assert.True(exporting.IsExporting);
        Assert.Equal(StudioProjectFolderSaveOutcome.NotStarted, (await exporting.SaveProjectFolderAsync(Folder)).Outcome);

        Assert.Equal(0, LogCount("store.saveProjectFolder"));
        Assert.False(Directory.Exists(Folder));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => exporting.SaveProjectFolderAsync(" "));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AClose_StopsTheSave_AndDoesNotFinishBeforeTheSaveHasEnded(bool deleteProject)
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        using var hold = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        Store.SavingProjectFolder = () =>
        {
            started.Set();
            hold.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
        };

        var save = session.SaveProjectFolderAsync(Folder);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Changes.Clear();

        var close = session.CloseAsync(deleteProject);
        Pump();


        // The copy still reads the recordings, so the session has not closed, and the project
        // is not deleted from under it.
        Assert.False(close.IsCompleted);
        Assert.Equal(0, LogCount("store.delete"));

        hold.Set();
        var result = await RunToEndAsync(save);
        await FinishAsync(close);

        Assert.Equal(StudioProjectFolderSaveOutcome.Cancelled, result.Outcome);
        Assert.Empty(Directory.GetFileSystemEntries(Parent));
        Assert.Empty(Errors);
        Assert.Equal(!deleteProject, Projects.Exists(id));
        if (deleteProject)
        {
            AssertLoggedInOrder("store.saveProjectFolder.ended", "store.delete");
        }

        // A closed session tells no window that the save has ended.
        Assert.DoesNotContain(Changes, change => change.HasFlag(StudioEditorChanges.ProjectFolder));
    }

    /// <summary>Runs what the session posts until the task has finished: the copy ends on another thread.</summary>
    private async Task<T> RunToEndAsync<T>(Task<T> task)
    {
        Pump();
        while (!task.IsCompleted)
        {
            await PumpWhenPostedAsync();
        }

        return await task;
    }
}
