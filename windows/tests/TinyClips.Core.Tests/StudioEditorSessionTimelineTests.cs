using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>A project of clips in the editor: the history over the whole manifest, the clip the inspectors edit, and changes made over a lease.</summary>
public sealed class StudioEditorSessionTimelineTests : StudioEditorSessionTestBase
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<StudioEditorSession> OpenTimelineAsync()
    {
        var id = CreateProject();
        Projects.SaveEvents(id, new StudioEvents());
        Projects.Save(StudioTimelineEditing.ToTimelineProject(Projects.Load(id)));
        return await OpenAsync(id);
    }

    private static string[] ClipIds(StudioEditorSession session) =>
        [.. session.TimelineMap!.Clips.Select(clip => clip.ClipId)];

    [Fact]
    public async Task SplitClip_IsOneUndoStepThatKeepsTheProjectLongAndSelectsTheSecondHalf()
    {
        var session = await OpenTimelineAsync();
        var duration = session.ProjectDuration;
        var first = session.SelectedClipId;
        session.Scrub(4);
        Assert.True(session.CanSplitClipAtPlayhead);

        Assert.True(session.SplitClipAtPlayhead());

        var clips = session.TimelineMap!.Clips;
        Assert.Equal(2, clips.Count);
        Assert.Equal(first, clips[0].ClipId);
        Assert.Equal(clips[1].ClipId, session.SelectedClipId);
        Assert.Equal(duration, session.ProjectDuration, Precision);
        Assert.Equal(4, clips[0].ProjectEnd, Precision);
        Assert.Equal(4, session.Playhead, Precision);
        Assert.Equal(4, session.ProjectPlayhead, Precision);

        session.Undo();
        Assert.Single(session.TimelineMap!.Clips);
        Assert.Equal(first, session.SelectedClipId);
        Assert.False(session.CanUndo);

        session.Redo();
        Assert.Equal(2, session.TimelineMap!.Clips.Count);
        Assert.Equal(clips[1].ClipId, session.SelectedClipId);
    }

    [Fact]
    public async Task EditsToTheSelectedClipAreSavedAsTheWholeProject()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        session.Scrub(4);
        Assert.True(session.SplitClipAtPlayhead());
        session.SetTrimEnd(8);
        Advance(600);

        var onDisk = Projects.Load(id);
        Assert.Equal(2, onDisk.SchemaVersion);
        Assert.Equal(2, onDisk.Timeline!.Clips.Length);
        Assert.Equal(8d, onDisk.Timeline.Clips[1].Edits!.TrimEnd!.Value, Precision);
        Assert.Equal(0d, onDisk.Timeline.Clips[0].Edits?.TrimStart ?? 0, Precision);
        Assert.False(session.HasUnsavedEdits);
        Assert.Empty(Errors);
        Assert.Equal(8d, session.Project!.Edits.TrimEnd!.Value, Precision);
        Assert.Equal(8, session.ProjectDuration, Precision);
    }

    [Fact]
    public async Task MoveClip_FollowsTheClipInTheProjectTime()
    {
        var session = await OpenTimelineAsync();
        session.Scrub(4);
        Assert.True(session.SplitClipAtPlayhead());
        var second = session.SelectedClipId!;
        Assert.Equal(4, session.ProjectPlayhead, Precision);

        Assert.True(session.MoveClip(second, 0));

        Assert.Equal(second, ClipIds(session)[0]);
        Assert.Equal(second, session.SelectedClipId);
        Assert.Equal(0, session.ProjectPlayhead, Precision);
        Assert.Equal(4, session.Playhead, Precision);
        Assert.Equal(0, Preview.Seeks[^1], Precision);
        Assert.True(session.MoveClipBy(second, 1));
        Assert.Equal(second, ClipIds(session)[1]);
    }

    [Fact]
    public async Task ScrubbingTheProjectSelectsTheClipThereAndKeepsTheInspectorInSourceTime()
    {
        var session = await OpenTimelineAsync();
        session.Scrub(4);
        Assert.True(session.SplitClipAtPlayhead());
        var second = session.SelectedClipId!;
        var first = ClipIds(session)[0];

        session.ScrubProject(1);
        Assert.Equal(first, session.SelectedClipId);
        Assert.Equal(1, session.Playhead, Precision);

        session.ScrubProject(7);
        Assert.Equal(second, session.SelectedClipId);
        Assert.Equal(7, session.Playhead, Precision);
        Assert.Equal(7, Preview.Seeks[^1], Precision);
        Assert.Equal(7, session.ProjectPlayhead, Precision);
    }

    [Fact]
    public async Task RemoveClipKeepsTheRecordingAndTheLastClip()
    {
        var session = await OpenTimelineAsync();
        session.Scrub(4);
        Assert.True(session.SplitClipAtPlayhead());
        var second = session.SelectedClipId!;

        Assert.True(session.RemoveClip(second));
        Assert.Single(session.TimelineMap!.Clips);
        Assert.Single(session.Manifest!.MediaSources!);
        Assert.False(session.RemoveClip(session.SelectedClipId!));

        session.Undo();
        Assert.Equal(2, session.TimelineMap!.Clips.Count);
    }

    [Fact]
    public async Task ClipTransitionIsOneUndoStep()
    {
        var session = await OpenTimelineAsync();
        session.Scrub(4);
        Assert.True(session.SplitClipAtPlayhead());
        var second = session.SelectedClipId!;

        Assert.True(session.SetClipTransition(second, StudioClipTransition.CrossDissolve));
        Assert.Equal(StudioClipTransition.CrossDissolve, session.GetClipTransition(second));
        Assert.False(session.SetClipTransition(second, StudioClipTransition.CrossDissolve));

        session.Undo();
        Assert.Equal(StudioClipTransition.Cut, session.GetClipTransition(second));
    }

    [Fact]
    public async Task SplittingASingleRecordingReopensThePreviewAndUndoReopensItAgain()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        var firstPreview = Preview;
        Changes.Clear();

        Assert.True(session.SplitClipAtPlayhead() || session.SelectedClipId is null);
        session.Scrub(4);
        Assert.True(session.SplitClipAtPlayhead());
        Pump();

        Assert.Equal(2, Previews.Opened.Count);
        Assert.True(firstPreview.IsDisposed);
        Assert.Equal(2, Previews.Requests[1].Project.SchemaVersion);
        Assert.Same(Preview, session.Preview);
        Assert.Contains(Changes, change => (change & StudioEditorChanges.Preview) != 0);
        Assert.Equal(4, Preview.Seeks[^1], Precision);

        session.Undo();
        session.Undo();
        Pump();

        Assert.Equal(1, Previews.Requests[^1].Project.SchemaVersion);
        Assert.False(session.IsTimeline);
        Assert.Same(Preview, session.Preview);
        Assert.NotNull(session.Preview);
    }

    [Fact]
    public async Task UpdatesWithinATimelineDoNotReopenThePreview()
    {
        var session = await OpenTimelineAsync();
        Preview.Calls.Clear();

        session.Scrub(4);
        Assert.True(session.SplitClipAtPlayhead());

        Assert.Single(Previews.Opened);
        Assert.Contains("UpdateProject", Preview.Calls);
        Assert.Equal(2, Preview.LastProject!.Timeline!.Clips.Length);
    }

    [Fact]
    public async Task BeginTimelineChange_OnACleanEditorWritesNothingAndHoldsTheEditor()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        var before = Projects.Load(id);
        Time.Advance(TimeSpan.FromHours(1));

        var start = session.BeginTimelineChange();

        Assert.True(start.Succeeded);
        Assert.Equal(0, LogCount("store.save"));
        Assert.Equal(before.ModifiedAt, Projects.Load(id).ModifiedAt);
        Assert.Equal(before.ModifiedAt, start.Lease!.BaseProject.ModifiedAt);
        Assert.False(session.IsEditable);
        Assert.False(session.BeginTimelineChange().Succeeded);
        session.SetCanvasPadding(0.4);
        Assert.False(session.HasUnsavedEdits);

        start.Lease.Dispose();
        Assert.True(session.IsEditable);
        session.SetCanvasPadding(0.4);
        Assert.True(session.HasUnsavedEdits);
    }

    [Fact]
    public async Task BeginTimelineChange_SavesTheEditsThatWereNotSavedAndHandsOutTheWholeManifest()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        session.SetCanvasPadding(0.2);
        Assert.True(session.HasUnsavedEdits);

        var start = session.BeginTimelineChange();

        Assert.True(start.Succeeded);
        Assert.Equal(1, LogCount("store.save"));
        Assert.Equal(0.2, Projects.Load(id).Canvas.Padding, Precision);
        Assert.Equal(0.2, start.Lease!.BaseProject.Canvas.Padding, Precision);
        Assert.False(session.HasUnsavedEdits);
        Advance(5000);
        Assert.Equal(1, LogCount("store.save"));
        start.Lease.Dispose();
    }

    [Fact]
    public async Task BeginTimelineChange_RefusesAndWritesNothingWhenTheFileWasChangedElsewhere()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        session.SetCanvasPadding(0.2);
        Projects.Save(Projects.Load(id) with { Audio = new StudioAudio { Volume = 0.3 } });
        var saves = LogCount("store.save");

        var start = session.BeginTimelineChange();

        Assert.False(start.Succeeded);
        Assert.Equal(StudioTimelineChangeFailure.StaleDisk, start.Failure);
        Assert.Equal(saves, LogCount("store.save"));
        Assert.Equal(0.3, Projects.Load(id).Audio.Volume, Precision);
        Assert.Equal(0.06, Projects.Load(id).Canvas.Padding, Precision);
        Assert.True(session.IsEditable);
    }

    [Fact]
    public async Task AddClips_AtTheEndIsOneUndoStepThatSelectsTheFirstAddedClip()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);

        var outcome = await session.AddClipsAsync(
            [new(CreateVideoFile("Alpha")), new(CreateVideoFile("Beta"))],
            StudioClipPlacement.End,
            Token);
        Pump();

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(3, session.TimelineMap!.Clips.Count);
        Assert.Equal(3, outcome.Manifest!.Timeline!.Clips.Length);
        Assert.Equal(session.TimelineMap.Clips[1].ClipId, session.SelectedClipId);
        Assert.Equal(0, session.Playhead, Precision);
        Assert.Equal(3, Projects.Load(id).Timeline!.Clips.Length);
        Assert.False(session.HasUnsavedEdits);
        Assert.Equal(2, Previews.Opened.Count);
        Assert.True(session.IsEditable);
        Assert.Empty(Errors);

        session.Undo();
        Pump();
        Assert.False(session.IsTimeline);
        Assert.False(session.CanUndo);

        session.Redo();
        Pump();
        Assert.Equal(3, session.TimelineMap!.Clips.Count);
        Assert.Equal(1, session.Manifest!.Timeline!.Clips.Count(clip => clip.Name == "Alpha"));
    }

    [Fact]
    public async Task AddClips_AtAProjectTimeSplitsTheClipThere()
    {
        var session = await OpenTimelineAsync();
        var before = session.ProjectDuration;

        var outcome = await session.AddClipsAsync([new(CreateVideoFile("Inserted"))], StudioClipPlacement.AtProjectTime(4), Token);

        Assert.True(outcome.Succeeded, outcome.Message);
        var clips = session.TimelineMap!.Clips;
        Assert.Equal(3, clips.Count);
        Assert.Equal("Inserted", session.Manifest!.Timeline!.Clips[1].Name);
        Assert.Equal(4, clips[1].ProjectStart, Precision);
        Assert.Equal(10, clips[1].ProjectEnd, Precision);
        Assert.Equal(before + 6, session.ProjectDuration, Precision);
        Assert.Equal(clips[1].ClipId, session.SelectedClipId);
    }

    [Fact]
    public async Task AddClips_ThatCannotBeReadAddsNothing()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        var before = File.ReadAllBytes(Projects.GetPaths(id).ProjectJsonPath);

        var outcome = await session.AddClipsAsync(
            [new(CreateVideoFile("Fine")), new(Path.Combine(Path.GetTempPath(), "TinyClipsTests", "missing.mp4"))],
            StudioClipPlacement.End,
            Token);

        Assert.False(outcome.Succeeded);
        Assert.Equal(StudioTimelineChangeFailure.MediaFailed, outcome.Failure);
        Assert.Equal(before, File.ReadAllBytes(Projects.GetPaths(id).ProjectJsonPath));
        Assert.False(session.IsTimeline);
        Assert.True(session.IsEditable);
        Assert.Equal(StudioEditorErrorKind.Timeline, Assert.Single(ErrorKinds));
    }

    [Fact]
    public async Task AddClips_IsRefusedWhenTheFileChangesBetweenPreparingAndPublishing()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        Store.Staged = () => Projects.Save(Projects.Load(id) with { Audio = new StudioAudio { Volume = 0.3 } });

        var outcome = await session.AddClipsAsync([new(CreateVideoFile("Late"))], StudioClipPlacement.End, Token);

        Assert.False(outcome.Succeeded);
        Assert.Equal(StudioTimelineChangeFailure.StaleDisk, outcome.Failure);
        Assert.Equal(1, Projects.Load(id).SchemaVersion);
        Assert.False(session.IsTimeline);
        Assert.True(session.IsEditable);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public async Task ACommittedChangeFromAnAgentIsOneUndoStepInTheOpenEditor()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        var lease = session.BeginTimelineChange().Lease!;
        using var prepared = await Projects.StageTimelineAsync(lease.BaseProject, [new(CreateVideoFile("Agent"))], Token);
        var saved = prepared.Commit(prepared.ProposedProject, Token);

        var outcome = session.ApplyCommittedTimelineChange(lease, saved, prepared.ImportedClips[0].Id);
        Pump();

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(saved.ModifiedAt, session.Manifest!.ModifiedAt);
        Assert.Equal(prepared.ImportedClips[0].Id, session.SelectedClipId);
        Assert.False(lease.IsActive);
        Assert.True(session.IsEditable);
        Assert.False(session.HasUnsavedEdits);

        session.SetCanvasPadding(0.3);
        Advance(600);
        Assert.Equal(2, Projects.Load(id).Timeline!.Clips.Length);
        Assert.Empty(Errors);

        session.Undo();
        Assert.Equal(2, session.TimelineMap!.Clips.Count);
        session.Undo();
        Pump();
        Assert.False(session.IsTimeline);
        session.Redo();
        Pump();
        Assert.True(session.IsTimeline);
        Assert.Equal(2, session.TimelineMap!.Clips.Count);
    }

    [Fact]
    public async Task ASpentOrForeignLeaseChangesNothing()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        var lease = session.BeginTimelineChange().Lease!;
        var proposed = StudioTimelineEditing.ToTimelineProject(lease.BaseProject);

        var wrongProject = session.CommitTimelineChange(lease, proposed with { Id = "someone-else" }, _ => throw new InvalidOperationException());
        Assert.Equal(StudioTimelineChangeFailure.WrongProject, wrongProject.Failure);
        var notATimeline = session.CommitTimelineChange(lease, lease.BaseProject, _ => throw new InvalidOperationException());
        Assert.Equal(StudioTimelineChangeFailure.Invalid, notATimeline.Failure);
        Assert.True(lease.IsActive);

        lease.Dispose();
        var spent = session.CommitTimelineChange(lease, proposed, _ => throw new InvalidOperationException());
        Assert.Equal(StudioTimelineChangeFailure.StaleLease, spent.Failure);
        Assert.Equal(1, Projects.Load(id).SchemaVersion);
        Assert.False(session.IsTimeline);
    }

    [Fact]
    public async Task ACommitThatThrowsLeavesTheEditorAndTheLeaseAsTheyWere()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        var lease = session.BeginTimelineChange().Lease!;
        var proposed = StudioTimelineEditing.ToTimelineProject(lease.BaseProject);

        Assert.Throws<StudioProjectStaleException>(() =>
            session.CommitTimelineChange(lease, proposed, _ => throw new StudioProjectStaleException()));

        Assert.True(lease.IsActive);
        Assert.False(session.IsTimeline);
        Assert.False(session.CanUndo);
        lease.Dispose();
        Assert.True(session.IsEditable);
    }

    [Fact]
    public async Task RefreshExportBookkeeping_TakesNoteOfAnExportWithoutTouchingEditsOrHistory()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        session.SetCanvasPadding(0.2);
        var exported = Path.Combine(Path.GetTempPath(), "TinyClipsTests", "exported.mp4");
        var saved = Projects.RecordExport(id, exported);

        session.RefreshExportBookkeeping(saved);

        Assert.Equal(saved.Exports.Length, session.Manifest!.Exports.Length);
        Assert.Equal(saved.ModifiedAt, session.Manifest.ModifiedAt);
        Assert.Equal(0.2, session.Project!.Canvas.Padding, Precision);
        Assert.True(session.HasUnsavedEdits);
        Assert.True(session.CanUndo);
        session.Undo();
        Assert.False(session.CanUndo);
        Assert.Equal(saved.Exports.Length, session.Manifest.Exports.Length);

        var before = session.Manifest;
        session.RefreshExportBookkeeping(saved with { Id = "another" });
        Assert.Equal(before.ModifiedAt, session.Manifest!.ModifiedAt);
    }

    [Fact]
    public async Task AnExternalExportDoesNotMakeTheNextTimelineEditConflict()
    {
        var session = await OpenTimelineAsync();
        session.Scrub(4);
        Assert.True(session.SplitClipAtPlayhead());
        Advance(600);
        var saved = Projects.RecordExport(session.Manifest!.Id, Path.Combine(Path.GetTempPath(), "TinyClipsTests", "again.mp4"));
        session.RefreshExportBookkeeping(saved);

        session.SetTrimEnd(8);
        Advance(600);

        Assert.Empty(Errors);
        Assert.Equal(8d, Projects.Load(saved.Id).Timeline!.Clips[1].Edits!.TrimEnd!.Value, Precision);
        Assert.Single(Projects.Load(saved.Id).Exports);
    }
}
