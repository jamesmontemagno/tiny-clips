using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>
/// An editor that cannot show its project saves the screen recording as a video of its own:
/// what it says while it does and afterwards, what it does when it is asked again in the
/// meantime, and what becomes of a save whose editor closes before the copy is over.
/// </summary>
/// <remarks>
/// The copy is a real one, of a file of a few thousand bytes, and ends on another thread. What
/// the session says of it, it says on its own thread, and so not before a test has run what
/// the session posted (<see cref="StudioEditorSessionTestBase.PumpWhenPostedAsync"/>). Until
/// then the save is under way, however fast the copy was, and that is what these tests use.
/// </remarks>
public sealed class StudioEditorSessionScreenRecordingTests : StudioEditorSessionTestBase
{
    private const string Saving = "Saving the screen recording\u2026";

    private static readonly byte[] Recording = [.. Enumerable.Range(0, 5000).Select(static value => (byte)(value % 251))];

    /// <summary>Every video the session reported as saved, in order.</summary>
    private readonly List<(string ProjectId, string Path)> _saved = [];

    private string VideoFolder => Path.GetDirectoryName(ExportPath)!;

    private string VideoName => Path.GetFileName(ExportPath);

    /// <summary>What is said when every name the video could get was taken while it was copied.</summary>
    private string NoFreeName =>
        $"The screen recording could not be saved: Another file was saved as {VideoName} while the recording was being copied, and no free name was found for the video.";

    [Fact]
    public async Task Save_SaysThatItIsSaving_ThenTheNameTheVideoGot_AndReportsTheVideoOnce()
    {
        var id = CreateRecordedProject();
        var session = await OpenWhatCannotBeShownAsync(id);
        var projectBefore = File.ReadAllText(Projects.GetPaths(id).ProjectJsonPath);
        Assert.False(session.IsSavingScreenRecording);
        Assert.Equal(string.Empty, session.ScreenRecordingStatus);

        var save = session.SaveScreenRecordingAsync(() => ExportPath);

        Assert.True(session.IsSavingScreenRecording);
        Assert.Equal(Saving, session.ScreenRecordingStatus);
        Assert.Equal([StudioEditorChanges.ScreenRecording], Changes);
        Assert.Empty(_saved);

        await PumpWhenPostedAsync();

        Assert.Equal(StudioScreenRecordingOutcome.Saved, await FinishAsync(save));
        Assert.False(session.IsSavingScreenRecording);
        Assert.Equal($"Saved as {VideoName}.", session.ScreenRecordingStatus);
        Assert.Equal([StudioEditorChanges.ScreenRecording, StudioEditorChanges.ScreenRecording], Changes);
        Assert.Equal([(id, ExportPath)], _saved);
        Assert.Empty(Errors);
        Assert.Equal(Recording, File.ReadAllBytes(ExportPath));
        Assert.Equal([ExportPath], Directory.GetFiles(VideoFolder));

        // The project is as it was, and so is what the editor offers.
        Assert.Equal(projectBefore, File.ReadAllText(Projects.GetPaths(id).ProjectJsonPath));
        Assert.Equal(Recording, File.ReadAllBytes(Projects.GetPaths(id).ScreenPath));
        Assert.Equal(StudioEditorLoadState.Unavailable, session.State);
        Assert.True(session.HasScreenRecordingToSave);
    }

    [Fact]
    public async Task Save_AskedForAgainWhileTheRecordingIsCopied_DoesNothing()
    {
        var session = await OpenWhatCannotBeShownAsync();
        var second = Path.Combine(VideoFolder, "TinyClips 2026-10-03 at 12.00.00 (2).mp4");
        var names = new Queue<string>([ExportPath, second]);
        var asked = 0;
        string NextName()
        {
            asked++;
            return names.Dequeue();
        }

        var save = session.SaveScreenRecordingAsync(NextName);
        var again = session.SaveScreenRecordingAsync(NextName);

        // Over at once: no second name was asked for, and nothing the editor shows has changed.
        Assert.Equal(StudioScreenRecordingOutcome.NotStarted, await AtOnce(again));
        Assert.Equal(1, asked);
        Assert.True(session.IsSavingScreenRecording);
        Assert.Equal(Saving, session.ScreenRecordingStatus);
        Assert.Equal([StudioEditorChanges.ScreenRecording], Changes);

        await PumpWhenPostedAsync();

        Assert.Equal(StudioScreenRecordingOutcome.Saved, await FinishAsync(save));
        Assert.Equal([ExportPath], Directory.GetFiles(VideoFolder));
        Assert.Single(_saved);

        // Once the copy is over, the recording can be saved again: a second video.
        var later = session.SaveScreenRecordingAsync(NextName);
        await PumpWhenPostedAsync();

        Assert.Equal(StudioScreenRecordingOutcome.Saved, await FinishAsync(later));
        Assert.Equal(2, asked);
        Assert.Equal($"Saved as {Path.GetFileName(second)}.", session.ScreenRecordingStatus);
        Assert.Equal(Recording, File.ReadAllBytes(second));
        Assert.Equal(2, _saved.Count);
    }

    [Fact]
    public async Task Save_ThatFailsWhileTheEditorIsOpen_SaysWhyInItsStatus_AndReportsNoError()
    {
        var session = await OpenWhatCannotBeShownAsync();
        TakeEveryName();

        var save = session.SaveScreenRecordingAsync(() => ExportPath);
        await PumpWhenPostedAsync();

        // The window is there to show the status under its button, so nothing else is told.
        Assert.Equal(StudioScreenRecordingOutcome.Failed, await FinishAsync(save));
        Assert.False(session.IsSavingScreenRecording);
        Assert.Equal(NoFreeName, session.ScreenRecordingStatus);
        Assert.Equal([StudioEditorChanges.ScreenRecording, StudioEditorChanges.ScreenRecording], Changes);
        Assert.Empty(Errors);
        Assert.Empty(_saved);
        Assert.Equal([42], File.ReadAllBytes(ExportPath));
        Assert.Equal([ExportPath], Directory.GetFiles(VideoFolder));
    }

    [Fact]
    public async Task Save_OfARecordingThatHasGoneSinceTheEditorOpened_SaysThatThereIsNothingToSave()
    {
        var id = CreateRecordedProject();
        var session = await OpenWhatCannotBeShownAsync(id);
        File.Delete(Projects.GetPaths(id).ScreenPath);
        var asked = 0;

        var save = session.SaveScreenRecordingAsync(() =>
        {
            asked++;
            return ExportPath;
        });

        Assert.Equal(StudioScreenRecordingOutcome.Failed, await FinishAsync(save));
        Assert.False(session.IsSavingScreenRecording);
        Assert.Equal("The screen recording could not be saved: This project has no screen recording to save.", session.ScreenRecordingStatus);
        Assert.Empty(Errors);
        Assert.Empty(_saved);
        Assert.Equal(0, asked);
        Assert.False(Directory.Exists(VideoFolder));
    }

    [Fact]
    public async Task Save_DoesNothing_UnlessTheEditorCannotShowAProjectWhoseRecordingIsThere()
    {
        var asked = 0;
        string NextName()
        {
            asked++;
            return ExportPath;
        }

        // An editor that shows its project.
        var ready = await OpenAsync(CreateRecordedProject());
        Assert.Equal(StudioEditorLoadState.Ready, ready.State);
        Assert.Equal(StudioScreenRecordingOutcome.NotStarted, await AtOnce(ready.SaveScreenRecordingAsync(NextName)));

        // One that cannot, and has no recording either.
        var empty = await OpenAsync(CreateProject(writeScreenFile: false));
        Assert.Equal(StudioEditorLoadState.Unavailable, empty.State);
        Assert.Equal(StudioScreenRecordingOutcome.NotStarted, await AtOnce(empty.SaveScreenRecordingAsync(NextName)));

        // One that is still opening.
        var opening = CreateSession(CreateRecordedProject());
        Assert.Equal(StudioEditorLoadState.Loading, opening.State);
        Assert.Equal(StudioScreenRecordingOutcome.NotStarted, await AtOnce(opening.SaveScreenRecordingAsync(NextName)));

        Assert.Equal(0, asked);
        foreach (var session in new[] { ready, empty, opening })
        {
            Assert.False(session.IsSavingScreenRecording);
            Assert.Equal(string.Empty, session.ScreenRecordingStatus);
        }

        Assert.DoesNotContain(StudioEditorChanges.ScreenRecording, Changes);
        Assert.False(Directory.Exists(VideoFolder));
    }

    [Fact]
    public async Task Save_AfterTheEditorHasClosed_DoesNothing()
    {
        var session = await OpenWhatCannotBeShownAsync();
        await FinishAsync(session.CloseAsync());
        var changesWhenClosed = Changes.Count;
        var asked = 0;

        var outcome = await AtOnce(session.SaveScreenRecordingAsync(() =>
        {
            asked++;
            return ExportPath;
        }));

        Assert.Equal(StudioScreenRecordingOutcome.NotStarted, outcome);
        Assert.Equal(0, asked);
        Assert.False(session.IsSavingScreenRecording);
        Assert.Equal(string.Empty, session.ScreenRecordingStatus);
        Assert.Equal(changesWhenClosed, Changes.Count);
        Assert.False(Directory.Exists(VideoFolder));
    }

    [Fact]
    public async Task ClosedWhileItSaves_TheCopyGoesOn_AndTheVideoIsReportedBeforeTheCloseIsOver()
    {
        var id = CreateRecordedProject();
        var session = await OpenWhatCannotBeShownAsync(id);
        Task? close = null;
        bool? closeWasOverWhenReported = null;
        session.ScreenRecordingSaved += (_, _) => closeWasOverWhenReported = close!.IsCompleted;

        var save = session.SaveScreenRecordingAsync(() => ExportPath);
        close = session.CloseAsync();
        var changesWhenClosed = Changes.Count;

        // The save has said nothing yet, and the close waits for it.
        Assert.True(session.IsClosed);
        Assert.False(close.IsCompleted);
        Assert.Empty(_saved);

        await PumpWhenPostedAsync();

        Assert.Equal(StudioScreenRecordingOutcome.Saved, await FinishAsync(save));
        await FinishAsync(close);
        Assert.Equal([(id, ExportPath)], _saved);
        Assert.False(closeWasOverWhenReported);
        Assert.Equal(Recording, File.ReadAllBytes(ExportPath));
        Assert.Empty(Errors);

        // No window is left to show anything, so nothing is said to have changed.
        Assert.False(session.IsSavingScreenRecording);
        Assert.Equal(changesWhenClosed, Changes.Count);
    }

    [Fact]
    public async Task ClosedWhileItSaves_ACopyThatFailsThen_IsReportedAsAnError_BeforeTheCloseIsOver()
    {
        var session = await OpenWhatCannotBeShownAsync();
        TakeEveryName();
        Task? close = null;
        bool? closeWasOverWhenReported = null;
        session.ErrorReported += (_, _) => closeWasOverWhenReported = close!.IsCompleted;

        var save = session.SaveScreenRecordingAsync(() => ExportPath);
        close = session.CloseAsync();

        Assert.False(close.IsCompleted);
        Assert.Empty(Errors);

        await PumpWhenPostedAsync();

        // The line under the button that would have said this went with the window.
        Assert.Equal(StudioScreenRecordingOutcome.Failed, await FinishAsync(save));
        await FinishAsync(close);
        Assert.Equal([NoFreeName], Errors);
        Assert.Equal([StudioEditorErrorKind.ScreenRecording], ErrorKinds);
        Assert.False(closeWasOverWhenReported);
        Assert.Equal(NoFreeName, session.ScreenRecordingStatus);
        Assert.Empty(_saved);
        Assert.Equal([42], File.ReadAllBytes(ExportPath));
        Assert.Equal([ExportPath], Directory.GetFiles(VideoFolder));
    }

    /// <summary>
    /// The name is asked for while the save is being started, before anything is copied. A
    /// close that comes at that very moment waits for the save like any other.
    /// </summary>
    [Fact]
    public async Task ClosedAtTheMomentTheNameIsAskedFor_TheCloseWaitsForTheSaveAllTheSame()
    {
        var id = CreateRecordedProject();
        var session = await OpenWhatCannotBeShownAsync(id);
        Task? close = null;

        var save = session.SaveScreenRecordingAsync(() =>
        {
            close = session.CloseAsync();
            return ExportPath;
        });

        Assert.NotNull(close);
        Assert.False(close.IsCompleted);
        Assert.Empty(_saved);

        await PumpWhenPostedAsync();

        Assert.Equal(StudioScreenRecordingOutcome.Saved, await FinishAsync(save));
        await FinishAsync(close);
        Assert.Equal([(id, ExportPath)], _saved);
        Assert.Equal(Recording, File.ReadAllBytes(ExportPath));
        Assert.Empty(Errors);
    }

    /// <summary>
    /// No window can ask for this today: only an editor that shows its project offers to delete
    /// it. Should one ever, the recording is copied before the project goes.
    /// </summary>
    [Fact]
    public async Task ClosedToDeleteTheProjectWhileItSaves_TheRecordingIsCopiedBeforeTheProjectGoes()
    {
        var id = CreateRecordedProject();
        var session = await OpenWhatCannotBeShownAsync(id);
        bool? deleteWasTriedWhenReported = null;
        session.ScreenRecordingSaved += (_, _) => deleteWasTriedWhenReported = Log.Contains("store.delete");

        var save = session.SaveScreenRecordingAsync(() => ExportPath);
        var close = session.CloseAsync(deleteProject: true);

        Assert.DoesNotContain("store.delete", Log);
        Assert.True(Projects.Exists(id));

        await PumpWhenPostedAsync();

        Assert.Equal(StudioScreenRecordingOutcome.Saved, await FinishAsync(save));
        await FinishAsync(close);
        Assert.False(deleteWasTriedWhenReported);
        Assert.Equal(Recording, File.ReadAllBytes(ExportPath));
        Assert.Contains("store.delete", Log);
        Assert.False(Projects.Exists(id));
        Assert.Empty(Errors);
    }

    /// <summary>
    /// A save that was to do nothing is over when it returns. One that has begun after all
    /// would wait for the session's thread, and a test that waited for it would never end.
    /// </summary>
    private static async Task<StudioScreenRecordingOutcome> AtOnce(Task<StudioScreenRecordingOutcome> save)
    {
        Assert.True(save.IsCompletedSuccessfully, "The save did not end at once: it has begun to copy.");
        return await save;
    }

    private string CreateRecordedProject()
    {
        var id = CreateProject();
        File.WriteAllBytes(Projects.GetPaths(id).ScreenPath, Recording);
        return id;
    }

    /// <summary>
    /// Opens an editor on a project whose recording is there and whose preview cannot be opened,
    /// so that the editor cannot show it and offers to save the recording.
    /// </summary>
    private async Task<StudioEditorSession> OpenWhatCannotBeShownAsync(string? projectId = null)
    {
        Previews.Failure = new InvalidOperationException("The screen recording could not be decoded.");
        var session = await OpenAsync(projectId ?? CreateRecordedProject());
        Assert.Equal(StudioEditorLoadState.Unavailable, session.State);
        Assert.True(session.HasScreenRecordingToSave);
        session.ScreenRecordingSaved += (_, e) => _saved.Add((e.ProjectId, e.Path));
        Changes.Clear();
        return session;
    }

    /// <summary>
    /// Puts a file under the name the video is to get. A save that is always offered that name
    /// finds it taken when its copy is complete, is offered it again, and gives up.
    /// </summary>
    private void TakeEveryName()
    {
        Directory.CreateDirectory(VideoFolder);
        File.WriteAllBytes(ExportPath, [42]);
    }
}
