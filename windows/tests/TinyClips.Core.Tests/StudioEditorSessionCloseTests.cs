using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>Closing a session, keeping the project or deleting it.</summary>
public sealed class StudioEditorSessionCloseTests : StudioEditorSessionTestBase
{
    [Fact]
    public async Task Close_SavesAtOnce_DisposesThePreview_ThenWritesThePoster()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        session.TogglePlayback();
        session.SetCanvasPadding(0.2);
        var preview = Preview;
        preview.DisposeGate = new TaskCompletionSource();

        var close = session.CloseAsync();

        // The save does not wait for anything: it is done when CloseAsync returns.
        Assert.True(session.IsClosed);
        Assert.Equal(0.2, Projects.Load(id).Canvas.Padding, Precision);
        Assert.False(session.HasUnsavedEdits);
        Assert.False(session.IsPlaying);
        Assert.Null(session.Preview);
        Assert.False(preview.HasSubscribers);
        Assert.Equal("Pause", preview.Calls[^1]);
        Assert.False(close.IsCompleted);
        Assert.Empty(Exporter.Posters);

        preview.DisposeGate.SetResult();
        await FinishAsync(close);

        Assert.True(preview.IsDisposed);
        Assert.Equal(0.2, Assert.Single(Exporter.Posters).Canvas.Padding, Precision);
        Assert.True(Directory.Exists(Projects.GetPaths(id).ProjectDirectory));
        Assert.Equal(0, LogCount("store.delete"));
        AssertLoggedInOrder("store.save", "preview.disposed", "exporter.poster");
    }

    [Fact]
    public async Task Close_IsDoneOnce()
    {
        var session = await OpenAsync(CreateProject());

        var first = session.CloseAsync();
        var second = session.CloseAsync(deleteProject: true);
        await FinishAsync(first);

        Assert.Same(first, second);
        Assert.Equal(1, LogCount("preview.dispose"));
        Assert.Equal(0, LogCount("store.delete"));
    }

    [Fact]
    public async Task Close_StopsARunningExport_AndSaysNothingAboutIt()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        var export = session.ExportAsync(() => ExportPath, default);

        var close = session.CloseAsync();

        Assert.True(Exporter.ExportCancellationToken.IsCancellationRequested);
        Assert.Equal(StudioExportOutcome.Cancelled, await FinishAsync(export));
        await FinishAsync(close);
        Assert.Empty(Errors);
        Assert.Empty(Projects.Load(id).Exports);
        Assert.True(Preview.IsDisposed);
    }

    [Fact]
    public async Task Close_OfAnUnavailableProject_WritesNoPoster()
    {
        var session = await OpenAsync(CreateProject(writeScreenFile: false));

        await FinishAsync(session.CloseAsync());

        Assert.Empty(Exporter.Posters);
        Assert.Equal(0, LogCount("preview.dispose"));
    }

    [Fact]
    public async Task Close_Deleting_DisposesThePreviewAndWaitsForIt_BeforeDeletingTheFolder()
    {
        var id = CreateProject(camera: true);
        var directory = Projects.GetPaths(id).ProjectDirectory;
        var session = await OpenAsync(id);
        session.SetCanvasPadding(0.2);
        Preview.DisposeGate = new TaskCompletionSource();
        Log.Clear();

        var close = session.CloseAsync(deleteProject: true);

        // The preview still has the media files open, so nothing may be deleted yet.
        Assert.Equal(new[] { "preview.dispose" }, Log);
        Assert.True(Directory.Exists(directory));
        Assert.False(close.IsCompleted);

        Preview.DisposeGate.SetResult();
        await FinishAsync(close);

        Assert.False(Directory.Exists(directory));
        Assert.Equal(new[] { "preview.dispose", "preview.disposed", "store.delete" }, Log);
        Assert.Empty(Exporter.Posters);
        Assert.Empty(Errors);

        // The edit was never saved, and the save that was scheduled for it does not run.
        Advance(5000);
        Assert.Equal(0, LogCount("store.save"));
    }

    [Fact]
    public async Task Close_Deleting_AlsoWaitsForARunningExportToLetGo()
    {
        var id = CreateProject();
        var directory = Projects.GetPaths(id).ProjectDirectory;
        var session = await OpenAsync(id);
        var export = session.ExportAsync(() => ExportPath, default);

        var close = session.CloseAsync(deleteProject: true);

        // The export has been told to stop, but has not finished on the session's thread yet.
        Assert.True(Exporter.ExportCancellationToken.IsCancellationRequested);
        Assert.True(Preview.IsDisposed);
        Assert.True(Directory.Exists(directory));

        Assert.Equal(StudioExportOutcome.Cancelled, await FinishAsync(export));
        await FinishAsync(close);

        Assert.False(Directory.Exists(directory));
        Assert.Empty(Errors);
    }

    [Fact]
    public async Task Close_Deleting_TriesAgainWhileAFileIsStillInUse()
    {
        var id = CreateProject();
        var paths = Projects.GetPaths(id);
        var session = await OpenAsync(id);
        var stillOpen = new FileStream(paths.ScreenPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var close = session.CloseAsync(deleteProject: true);

        Assert.Equal(1, LogCount("store.delete"));
        Assert.True(File.Exists(paths.ScreenPath));
        Assert.False(close.IsCompleted);

        stillOpen.Dispose();
        Advance(120);
        await FinishAsync(close);

        Assert.Equal(2, LogCount("store.delete"));
        Assert.False(Directory.Exists(paths.ProjectDirectory));
        Assert.Empty(Errors);
    }

    [Fact]
    public async Task Close_Deleting_ReportsWhenTheFolderCannotBeDeleted()
    {
        var id = CreateProject();
        var paths = Projects.GetPaths(id);
        var session = await OpenAsync(id);
        using var stillOpen = new FileStream(paths.ScreenPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var close = session.CloseAsync(deleteProject: true);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            Advance(120);
        }

        await FinishAsync(close);

        Assert.Equal(5, LogCount("store.delete"));
        Assert.True(File.Exists(paths.ScreenPath));
        Assert.StartsWith("The project could not be deleted: ", Assert.Single(Errors));
        Assert.Equal(StudioEditorErrorKind.Delete, Assert.Single(ErrorKinds));
    }

    [Fact]
    public async Task AfterClosing_NothingFromThePreviewOrTheClockIsActedOn()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        session.Scrub(5);
        session.TogglePlayback();
        var preview = Preview;

        // Raised just before the window closed, and reaching the session's thread just after.
        preview.RaisePosition(7);
        preview.RaiseIsPlaying(false);
        preview.RaiseFailed("The screen recording stopped decoding.");
        Assert.Equal(3, PostedCount);
        var close = session.CloseAsync();
        Changes.Clear();
        await FinishAsync(close);

        Assert.Equal(5, session.Playhead, Precision);
        Assert.Equal(StudioEditorLoadState.Ready, session.State);
        Assert.Empty(Changes);

        // Raised after the preview was let go of: the session is no longer listening.
        preview.RaisePosition(8);
        preview.RaiseIsPlaying(false);
        preview.RaiseFailed("Too late.");
        Assert.Equal(0, PostedCount);

        Log.Clear();
        Advance(5000);
        Assert.Empty(Log);
        Assert.Empty(Errors);
    }

    [Fact]
    public async Task AfterClosing_TheSessionRefusesToPlayOrExport()
    {
        var session = await OpenAsync(CreateProject());
        await FinishAsync(session.CloseAsync());

        session.TogglePlayback();
        session.Scrub(3);
        var outcome = await FinishAsync(session.ExportAsync(() => ExportPath, default));

        Assert.Equal(StudioExportOutcome.NotStarted, outcome);
        Assert.False(session.IsPlaying);
        Assert.False(session.IsEditable);
        Assert.Empty(Exporter.Exports);
    }
}
