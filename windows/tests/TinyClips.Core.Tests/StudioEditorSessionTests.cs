using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>Opening a project, and the session's state flags.</summary>
public sealed class StudioEditorSessionTests : StudioEditorSessionTestBase
{
    [Fact]
    public async Task Load_OpensThePreviewAppliesMuteAndSeeksToTheTrimStart()
    {
        var id = CreateProject(camera: true, cameraStartOffset: 0.25);
        Projects.Save(Projects.Load(id) with { Audio = new StudioAudio { Muted = true } });
        Time.Advance(TimeSpan.FromHours(1));
        var session = CreateSession(id);

        var load = session.LoadAsync();

        // The preview is open, but the session has not been back on its own thread yet.
        Assert.Equal(StudioEditorLoadState.Loading, session.State);
        Assert.Null(session.Preview);
        Assert.Empty(Changes);

        await FinishAsync(load);

        Assert.Equal(StudioEditorLoadState.Ready, session.State);
        Assert.True(session.IsReady);
        Assert.Same(Preview, session.Preview);
        Assert.Equal(new[] { "UpdateProject", "Seek" }, Preview.Calls);
        Assert.True(Preview.LastProject!.Audio.Muted);
        Assert.Equal(0.25, Assert.Single(Preview.Seeks), Precision);
        Assert.Equal(0.25, session.Playhead, Precision);
        Assert.False(session.IsPlaying);
        Assert.Equal(new[] { StudioEditorChanges.All }, Changes);

        var request = Assert.Single(Previews.Requests);
        Assert.Equal(id, request.Project.Id);
        Assert.Equal(Projects.GetPaths(id).ScreenPath, request.Paths.ScreenPath);
        Assert.Equal(Projects.GetPaths(id).CameraPath, request.Paths.CameraPath);
        Assert.Equal(Time.GetUtcNow(), Projects.Load(id).LastOpenedAt);
        AssertLoggedInOrder("store.markOpened", "store.loadEvents", "preview.open");
    }

    [Fact]
    public async Task Load_IsStartedOnce()
    {
        var id = CreateProject();
        var session = CreateSession(id);

        var first = session.LoadAsync();
        var second = session.LoadAsync();
        await FinishAsync(first);

        Assert.Same(first, second);
        Assert.Single(Previews.Requests);
        Assert.Equal(1, LogCount("store.markOpened"));
    }

    [Fact]
    public async Task Load_WithoutTheScreenFile_IsUnavailableAndOpensNoPreview()
    {
        var id = CreateProject(writeScreenFile: false, name: "Moved away");

        var session = await OpenAsync(id);

        Assert.Equal(StudioEditorLoadState.Unavailable, session.State);
        Assert.Equal(
            "The original recording for this project is no longer on this PC, so it cannot be previewed or exported here.",
            session.UnavailableMessage);
        Assert.Empty(Previews.Requests);
        Assert.Null(session.Preview);
        Assert.Equal("Moved away", session.ClipName);
        Assert.False(session.IsEditable);
        Assert.False(session.CanExport);
        Assert.Equal(StudioClosePrompt.None, session.GetClosePrompt());
    }

    [Fact]
    public async Task Load_WhenThePreviewCannotOpen_IsUnavailableWithTheErrorMessage()
    {
        var id = CreateProject();
        Previews.Failure = new InvalidOperationException("The screen recording could not be decoded.");

        var session = await OpenAsync(id);

        Assert.Equal(StudioEditorLoadState.Unavailable, session.State);
        Assert.Equal("The screen recording could not be decoded.", session.UnavailableMessage);
        Assert.Null(session.Preview);
        Assert.Empty(Previews.Opened);
    }

    [Fact]
    public async Task Load_WhenTheProjectIsGone_SaysSoInPlainWords()
    {
        // Removed by the storage rules, say, since the Clips Library last looked.
        var session = await OpenAsync("3f0013cf-ba10-4453-af91-792b7882dae6");

        Assert.Equal(StudioEditorLoadState.Unavailable, session.State);
        Assert.Equal(StudioEditorSession.MissingProjectMessage, session.UnavailableMessage);
        Assert.DoesNotContain("project.json", session.UnavailableMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(session.Model);
        Assert.Empty(Previews.Requests);
        Assert.Equal("Untitled recording", session.ClipName);
        Assert.False(Directory.Exists(Projects.GetPaths("3f0013cf-ba10-4453-af91-792b7882dae6").ProjectDirectory));

        // Closing writes nothing for a project that was never read.
        Log.Clear();
        await FinishAsync(session.CloseAsync());
        Assert.Empty(Log);
        Assert.Empty(Errors);
    }

    [Fact]
    public async Task Load_WhenTheProjectFileIsGoneButItsFolderIsNot_SaysTheSame()
    {
        var id = CreateProject();
        File.Delete(Projects.GetPaths(id).ProjectJsonPath);

        var session = await OpenAsync(id);

        Assert.Equal(StudioEditorLoadState.Unavailable, session.State);
        Assert.Equal(StudioEditorSession.MissingProjectMessage, session.UnavailableMessage);
        Assert.Null(session.Model);
    }

    [Fact]
    public async Task Load_WhenTheProjectCannotBeRead_IsUnavailableWithTheErrorMessage()
    {
        var id = CreateProject();
        File.WriteAllText(Projects.GetPaths(id).ProjectJsonPath, "{ this is not a project");

        var session = await OpenAsync(id);

        Assert.Equal(StudioEditorLoadState.Unavailable, session.State);
        Assert.False(string.IsNullOrWhiteSpace(session.UnavailableMessage));
        Assert.NotEqual(StudioEditorSession.MissingProjectMessage, session.UnavailableMessage);
        Assert.Null(session.Model);
        Assert.Empty(Previews.Requests);
        Assert.Equal("Untitled recording", session.ClipName);
    }

    [Fact]
    public async Task Load_PassesTheRecordedEventsToThePreview()
    {
        var id = CreateProject();
        Projects.SaveEvents(id, new StudioEvents { Clicks = [new StudioClickEvent { T = 1.5, X = 0.4, Y = 0.6 }] });

        var session = await OpenAsync(id);

        Assert.True(session.IsReady);
        var click = Assert.Single(Assert.Single(Previews.Requests).Events.Clicks);
        Assert.Equal(1.5, click.T, Precision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ this is not JSON")]
    [InlineData("{ \"schemaVersion\": 99 }")]
    public async Task Load_WithMissingOrUnreadableEvents_UsesEmptyEvents(string? eventsText)
    {
        var id = CreateProject();
        if (eventsText is not null)
        {
            File.WriteAllText(Projects.GetPaths(id).EventsPath, eventsText);
        }

        var session = await OpenAsync(id);

        Assert.True(session.IsReady);
        var events = Assert.Single(Previews.Requests).Events;
        Assert.Empty(events.Clicks);
        Assert.Empty(events.Cursor);
    }

    [Fact]
    public async Task PreviewFailure_AfterOpening_MakesTheSessionUnavailable()
    {
        var session = await OpenAsync(CreateProject());
        session.TogglePlayback();

        Preview.RaiseFailed("The screen recording stopped decoding.");

        // Raised on a worker thread: nothing changes until the session is back on its own.
        Assert.True(session.IsReady);
        Pump();

        Assert.Equal(StudioEditorLoadState.Unavailable, session.State);
        Assert.Equal("The screen recording stopped decoding.", session.UnavailableMessage);
        Assert.False(session.IsPlaying);
        Assert.False(session.IsEditable);

        session.SetCanvasPadding(0.3);
        Assert.Equal(0.06, session.Project!.Canvas.Padding, Precision);
    }

    [Fact]
    public async Task Close_WhileThePreviewIsStillOpening_DisposesItWhenItArrives()
    {
        var id = CreateProject();
        Previews.Gate = new TaskCompletionSource();
        var session = CreateSession(id);
        var load = session.LoadAsync();

        var close = session.CloseAsync();

        Assert.True(Previews.LastCancellationToken.IsCancellationRequested);
        Previews.Gate.SetResult();
        await FinishAsync(load);
        await FinishAsync(close);

        Assert.Equal(StudioEditorLoadState.Loading, session.State);
        Assert.Null(session.Preview);
        Assert.True(Preview.IsDisposed);
        Assert.False(Preview.HasSubscribers);
        Assert.Empty(Preview.Calls);
    }

    [Fact]
    public async Task Flags_FollowTheStateTheEditsAndTheExport()
    {
        var session = await OpenAsync(CreateProject());

        Assert.True(session.IsEditable);
        Assert.True(session.CanExport);
        Assert.False(session.CanUndo);
        Assert.False(session.CanRedo);
        Assert.False(session.HasCamera);
        Assert.True(session.HasNeverExported);

        session.SetCanvasPadding(0.2);
        Assert.True(session.CanUndo);
        session.Undo();
        Assert.False(session.CanUndo);
        Assert.True(session.CanRedo);

        var export = session.ExportAsync(() => ExportPath, default);
        Assert.True(session.IsReady);
        Assert.False(session.IsEditable);
        Assert.False(session.CanExport);
        Assert.False(session.CanRedo);

        session.CancelExport();
        Assert.Equal(StudioExportOutcome.Cancelled, await FinishAsync(export));
        Assert.True(session.IsEditable);
        Assert.True(session.CanRedo);
    }

    [Fact]
    public async Task CanExport_NeedsSomethingToExport()
    {
        var session = await OpenAsync(CreateProject(duration: 0));

        Assert.True(session.IsEditable);
        Assert.False(session.CanExport);
        Assert.Equal(StudioExportOutcome.NotStarted, await FinishAsync(session.ExportAsync(() => ExportPath, default)));
        Assert.Empty(Exporter.Exports);
    }

    [Fact]
    public async Task ClosePrompt_DependsOnExportingAndOnHavingBeenExported()
    {
        var loading = CreateSession(CreateProject());
        Assert.Equal(StudioClosePrompt.None, loading.GetClosePrompt());

        var session = await OpenAsync(CreateProject());
        Assert.Equal(StudioClosePrompt.NeverExported, session.GetClosePrompt());

        var export = session.ExportAsync(() => ExportPath, default);
        Assert.Equal(StudioClosePrompt.ExportRunning, session.GetClosePrompt());

        Exporter.Complete();
        Assert.Equal(StudioExportOutcome.Exported, await FinishAsync(export));
        Assert.Equal(StudioClosePrompt.None, session.GetClosePrompt());
    }

    [Fact]
    public async Task ClosePrompt_IsNotNeededForAProjectThatWasExportedBefore()
    {
        var id = CreateProject(camera: true);
        Projects.RecordExport(id, ExportPath);

        var session = await OpenAsync(id);

        Assert.True(session.HasCamera);
        Assert.False(session.HasNeverExported);
        Assert.Equal(StudioClosePrompt.None, session.GetClosePrompt());
    }
}
