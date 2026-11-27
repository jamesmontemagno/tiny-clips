namespace TinyClips.Core.Studio.Editing;

// The timeline of clips. The project is the whole manifest: every clip and the registry of
// recordings they play. What the inspectors edit is the clip that is selected, as a recording of
// its own in source time, and an edit to it is written back into the manifest. The strip, the
// transport and the preview are in project time.
//
// Adding recordings copies files into the project, which takes time, and the editor may not
// change while that is going on or what is published would not be what the user sees. So a change
// to the timeline that is prepared elsewhere (the picker, a drop, the Library, a recording that
// was just made, an agent) is a lease: BeginTimelineChange saves the edits that are not saved yet
// and holds the editor, the change is made over the manifest that the lease hands out, and
// CommitTimelineChange publishes it and takes it into the history as one undo step. What was
// prepared is not published when the editor is no longer what it was prepared over.
public sealed partial class StudioEditorSession
{
    /// <summary>The clips of the timeline with where they are on it. Null for a single recording.</summary>
    public StudioTimelineMap? TimelineMap => Model?.TimelineMap;

    /// <summary>The clip the inspectors edit, or null for a single recording.</summary>
    public string? SelectedClipId => Model?.SelectedClipId;

    /// <summary>
    /// Whether a change to the timeline can be started: the editor is ready and nothing is being
    /// dragged, exported, saved or made at the moment.
    /// </summary>
    public bool IsReadyForTimelineChange =>
        IsEditable && !IsInGesture && !IsGeneratingCaptions && Model is not null;

    /// <summary>Whether recordings can be added to this project.</summary>
    public bool CanAddClips => IsReadyForTimelineChange && _timelineStore is not null;

    /// <summary>Whether the clip at the playhead can be split there.</summary>
    public bool CanSplitClipAtPlayhead => IsEditable && Model is { } model && model.CanSplitClipAt(Playhead);

    /// <summary>
    /// Splits the clip at the playhead in two and selects the second half. The two halves play as
    /// the clip did. One undo step.
    /// </summary>
    public bool SplitClipAtPlayhead() => Edit(model => model.SplitClip(Playhead), false);

    /// <summary>Moves a clip to a place in the order of the clips and selects it. One undo step.</summary>
    public bool MoveClip(string clipId, int index) => Edit(model => model.MoveClip(clipId, index), false);

    /// <summary>Moves a clip earlier (-1) or later (1) by that many places. One undo step.</summary>
    public bool MoveClipBy(string clipId, int places)
    {
        if (Model?.Manifest.Timeline?.Clips is not { } clips)
        {
            return false;
        }

        var index = Array.FindIndex(clips, clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal));
        return index >= 0 && MoveClip(clipId, Math.Clamp(index + places, 0, clips.Length - 1));
    }

    /// <summary>Takes a clip out of the timeline. Its recording stays in the project. The last clip cannot be taken out. One undo step.</summary>
    public bool RemoveClip(string clipId) => Edit(model => model.RemoveClip(clipId), false);

    /// <summary>Sets how a clip begins after the one before it. One undo step.</summary>
    public bool SetClipTransition(string clipId, StudioClipTransition transition) =>
        Edit(model => model.SetClipTransition(clipId, transition), false);

    /// <summary>The join before a clip, or null for the first clip.</summary>
    public StudioClipTransition? GetClipTransition(string clipId) => Model?.GetClipTransition(clipId);

    /// <summary>
    /// Takes note of what the project on disk says of its exports, name and dates, after something
    /// other than this editor recorded an export. Nothing that was edited changes, and neither
    /// does the history. The project of another editor is left alone.
    /// </summary>
    public void RefreshExportBookkeeping(StudioProject saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (_isClosed
            || Model is not { } model
            || !string.Equals(saved.Id, ProjectId, StringComparison.Ordinal))
        {
            return;
        }

        model.RefreshBookkeeping(saved);
        RaiseChanged(StudioEditorChanges.Project | StudioEditorChanges.Export);
    }

    /// <summary>
    /// Holds the editor for a change to the timeline. The edits that are not saved yet are saved,
    /// without changing the project when there are none, and
    /// <see cref="StudioTimelineChangeLease.BaseProject"/> is the whole project as it is on disk
    /// then. Nothing is written when the file on disk is not what this editor last saw: a project
    /// that was changed elsewhere is not overwritten, and the change is refused.
    /// </summary>
    public StudioTimelineChangeStart BeginTimelineChange()
    {
        if (_isClosed)
        {
            return StudioTimelineChangeStart.Refused(StudioTimelineChangeFailure.Closed, "The editor is closed.");
        }

        if (!IsReadyForTimelineChange || Model is not { } model || _persisted is not { } persisted)
        {
            return StudioTimelineChangeStart.Refused(
                StudioTimelineChangeFailure.NotReady,
                "The editor is busy. Try again when it has finished.");
        }

        PausePreview();
        CancelScheduledSave();

        StudioProject flushed;
        try
        {
            var onDisk = _store.Load(ProjectId);
            if (!StudioEditableState.From(onDisk).ContentEquals(persisted))
            {
                return StudioTimelineChangeStart.Refused(StudioTimelineChangeFailure.StaleDisk, ChangedOutsideMessage);
            }

            if (model.EditableState.ContentEquals(persisted))
            {
                flushed = onDisk;
            }
            else
            {
                var saved = _store.Save(model.EditableState.ApplyTo(onDisk));
                flushed = Detach(saved);
                _persisted = StudioEditableState.From(flushed);
            }

            model.RefreshBookkeeping(flushed);
            _hasUnsavedEdits = false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return StudioTimelineChangeStart.Refused(
                StudioTimelineChangeFailure.SaveFailed,
                $"Studio could not save this project: {ex.Message}");
        }

        var lease = new StudioTimelineChangeLease(flushed, model.EditableState, ReleaseTimelineLease);
        _timelineLease = lease;
        RaiseChanged(StudioEditorChanges.State | StudioEditorChanges.Project);
        return new StudioTimelineChangeStart(lease, StudioTimelineChangeFailure.None, string.Empty);
    }

    /// <summary>
    /// Publishes a change to the timeline and takes it into the history as one undo step.
    /// <paramref name="commit"/> makes the change durable and returns the project that was saved.
    /// It is called after the editor and the proposal have been checked, with nothing in between,
    /// and what it throws is thrown from here, with nothing taken in and the lease still held.
    /// </summary>
    /// <param name="lease">The lease from <see cref="BeginTimelineChange"/>. It is released when this succeeds.</param>
    /// <param name="proposed">The whole project as it is to be, over <see cref="StudioTimelineChangeLease.BaseProject"/>.</param>
    /// <param name="commit">Publishes <paramref name="proposed"/> and returns what was saved.</param>
    /// <param name="selectClipId">The clip the inspectors edit afterwards, with the playhead at its first picture. Null keeps the one that was selected.</param>
    public StudioTimelineChangeOutcome CommitTimelineChange(
        StudioTimelineChangeLease lease,
        StudioProject proposed,
        Func<StudioProject, StudioProject> commit,
        string? selectClipId = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(commit);

        if (Check(lease, proposed, selectClipId) is { } refused)
        {
            return refused;
        }

        var saved = commit(proposed);
        return Take(lease, saved, selectClipId);
    }

    /// <summary>
    /// Takes a change that was made durable elsewhere into the open editor, as one undo step. The
    /// same checks and result as <see cref="CommitTimelineChange"/>, for a change whose commit has
    /// already happened, which is why the project it was made over must still be the editor's.
    /// </summary>
    public StudioTimelineChangeOutcome ApplyCommittedTimelineChange(
        StudioTimelineChangeLease lease,
        StudioProject committed,
        string? selectClipId = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(committed);
        return Check(lease, committed, selectClipId) ?? Take(lease, committed, selectClipId);
    }

    /// <summary>
    /// Adds recordings or videos after the clips that are there, at an index or at a time on the
    /// timeline, as one undo step however many there are. Everything is copied in before anything
    /// is published, and nothing is added when any of them cannot be.
    /// </summary>
    public async Task<StudioTimelineChangeOutcome> AddClipsAsync(
        IReadOnlyList<StudioTimelineImport> imports,
        StudioClipPlacement placement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imports);
        if (imports.Count == 0 || _timelineStore is not { } store)
        {
            return Report(StudioTimelineChangeOutcome.Refused(
                StudioTimelineChangeFailure.NotReady,
                imports.Count == 0 ? "Choose something to add." : "This project cannot take more recordings."));
        }

        var start = BeginTimelineChange();
        if (start.Lease is not { } lease)
        {
            return Report(StudioTimelineChangeOutcome.Refused(start.Failure, start.Message));
        }

        StudioPreparedTimelineEdit? prepared = null;
        try
        {
            prepared = await store.StageTimelineAsync(lease.BaseProject, imports, cancellationToken);
            var (proposal, firstClipId) = Place(prepared, placement);
            var edit = prepared;
            var outcome = CommitTimelineChange(
                lease,
                proposal,
                proposed => edit.Commit(proposed, cancellationToken),
                firstClipId);
            return outcome.Succeeded ? outcome : Report(outcome);
        }
        catch (OperationCanceledException)
        {
            return StudioTimelineChangeOutcome.Refused(StudioTimelineChangeFailure.Canceled, "Adding was cancelled.");
        }
        catch (StudioProjectStaleException)
        {
            return Report(StudioTimelineChangeOutcome.Refused(StudioTimelineChangeFailure.StaleDisk, ChangedOutsideMessage));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or NotSupportedException or StudioProjectInvalidException or ArgumentException or InvalidOperationException)
        {
            return Report(StudioTimelineChangeOutcome.Refused(
                StudioTimelineChangeFailure.MediaFailed,
                $"Studio could not add this to the timeline: {ex.Message}"));
        }
        finally
        {
            prepared?.Dispose();
            lease.Dispose();
        }
    }

    private const string ChangedOutsideMessage =
        "The project was changed outside the editor. Close the editor and open the project again.";

    private StudioTimelineChangeOutcome Report(StudioTimelineChangeOutcome outcome)
    {
        if (!_isClosed && outcome.Failure != StudioTimelineChangeFailure.Canceled)
        {
            ReportError(StudioEditorErrorKind.Timeline, outcome.Message);
        }

        return outcome;
    }

    // The checks that come before anything is published. Null when the change may go ahead.
    private StudioTimelineChangeOutcome? Check(StudioTimelineChangeLease lease, StudioProject project, string? selectClipId)
    {
        if (!ReferenceEquals(_timelineLease, lease) || !lease.IsActive)
        {
            return StudioTimelineChangeOutcome.Refused(
                StudioTimelineChangeFailure.StaleLease,
                "The project changed while this was being prepared.");
        }

        if (_isClosed || Model is not { } model)
        {
            return StudioTimelineChangeOutcome.Refused(StudioTimelineChangeFailure.Closed, "The editor is closed.");
        }

        if (!string.Equals(project.Id, ProjectId, StringComparison.Ordinal))
        {
            return StudioTimelineChangeOutcome.Refused(
                StudioTimelineChangeFailure.WrongProject,
                "The change is for another project.");
        }

        if (!model.EditableState.ContentEquals(lease.BaseState))
        {
            return StudioTimelineChangeOutcome.Refused(
                StudioTimelineChangeFailure.StaleLease,
                "The project changed while this was being prepared.");
        }

        try
        {
            if (project.SchemaVersion < 2)
            {
                throw new StudioProjectInvalidException("A change to the timeline is a project of clips.");
            }

            _ = StudioTimelineProjection.Map(project);
            if (selectClipId is not null && !project.Timeline!.Clips.Any(clip => string.Equals(clip.Id, selectClipId, StringComparison.Ordinal)))
            {
                throw new StudioProjectInvalidException("The clip to select is not in the timeline.");
            }
        }
        catch (StudioProjectInvalidException ex)
        {
            return StudioTimelineChangeOutcome.Refused(StudioTimelineChangeFailure.Invalid, ex.Message);
        }

        return null;
    }

    // What was published is in the project on disk, so it goes into the editor whatever happens:
    // an editor that did not take it in would be one a later save has to refuse. The lease is
    // spent either way.
    private StudioTimelineChangeOutcome Take(StudioTimelineChangeLease lease, StudioProject saved, string? selectClipId)
    {
        var manifest = Detach(saved);
        try
        {
            AdoptCommitted(manifest, selectClipId);
        }
        catch (Exception ex) when (ex is ArgumentException or StudioProjectInvalidException)
        {
            lease.Dispose();
            return new StudioTimelineChangeOutcome(
                StudioTimelineChangeFailure.Invalid,
                $"The change was saved, but the editor could not show it: {ex.Message}",
                manifest);
        }

        lease.Dispose();
        return new StudioTimelineChangeOutcome(StudioTimelineChangeFailure.None, string.Empty, manifest);
    }

    private void AdoptCommitted(StudioProject saved, string? selectClipId)
    {
        var model = Model!;
        CancelScheduledSave();
        var clipBefore = model.SelectedClipId;
        _ = model.AdoptManifest(saved, selectClipId);
        model.RefreshBookkeeping(saved);
        _persisted = StudioEditableState.From(saved);
        _hasUnsavedEdits = false;

        var clipChanged = !string.Equals(clipBefore, model.SelectedClipId, StringComparison.Ordinal);
        if (clipChanged)
        {
            ClearClipSelections();
            RefreshEvents();
            _inspectorPanel = StudioInspectorPanels.Resolve(_inspectorPanel, model.HasCamera);
        }

        Playhead = selectClipId is not null ? model.TrimStart : model.ClampPlayhead(Playhead);
        SyncPreview();
        if (model.IsTimeline && _preview is not null)
        {
            SeekProject(model.GetProjectTime(Playhead), model.SelectedClipId);
        }

        RaiseChanged(
            StudioEditorChanges.Project
            | StudioEditorChanges.Playback
            | StudioEditorChanges.Selection
            | StudioEditorChanges.Inspector);
    }

    private void ReleaseTimelineLease(StudioTimelineChangeLease lease)
    {
        if (ReferenceEquals(_timelineLease, lease))
        {
            _timelineLease = null;
            RaiseChanged(StudioEditorChanges.State | StudioEditorChanges.Project);
        }
    }

    // The project as the file has it. What the store hands back after a save is the object that
    // was written, which is not what reading the file gives when the reader normalizes something.
    private static StudioProject Detach(StudioProject project) =>
        StudioProjectJson.ReadProject(StudioProjectJson.WriteProject(project));

    // The proposal for a batch, with the clips where they were asked to go, and the clip to select.
    private static (StudioProject Proposal, string FirstClipId) Place(
        StudioPreparedTimelineEdit prepared,
        StudioClipPlacement placement)
    {
        var proposed = prepared.ProposedProject;
        var imported = prepared.ImportedClips;
        if (imported.Count == 0 || proposed.Timeline is not { } timeline)
        {
            throw new StudioProjectInvalidException("Nothing was added to the timeline.");
        }

        var ids = imported.Select(clip => clip.Id).ToHashSet(StringComparer.Ordinal);
        var result = timeline with { Clips = timeline.Clips.Where(clip => !ids.Contains(clip.Id)).ToArray() };
        switch (placement.Kind)
        {
            case StudioClipPlacementKind.End:
                result = timeline;
                break;
            case StudioClipPlacementKind.Index:
                for (var i = 0; i < imported.Count; i++)
                {
                    result = StudioTimelineEditing.Insert(result, imported[i], placement.Index + i);
                }

                break;
            default:
                result = StudioTimelineEditing.InsertAtProjectTime(
                    result, imported[0], placement.ProjectTime, proposed.MediaSources!);
                var first = Array.FindIndex(result.Clips, clip => string.Equals(clip.Id, imported[0].Id, StringComparison.Ordinal));
                for (var i = 1; i < imported.Count; i++)
                {
                    result = StudioTimelineEditing.Insert(result, imported[i], first + i);
                }

                break;
        }

        return (proposed with { Timeline = result }, imported[0].Id);
    }
}

/// <summary>Why a change to the timeline was not made.</summary>
public enum StudioTimelineChangeFailure
{
    /// <summary>Nothing. The change was made.</summary>
    None,

    /// <summary>The editor cannot take a change just now: it is loading, exporting, being dragged in, or already held.</summary>
    NotReady,

    /// <summary>The lease is spent, or the editor is no longer what it was when the lease was given.</summary>
    StaleLease,

    /// <summary>The editor has closed.</summary>
    Closed,

    /// <summary>The change is for another project.</summary>
    WrongProject,

    /// <summary>The project on disk was changed by something other than this editor. Nothing was written over it.</summary>
    StaleDisk,

    /// <summary>The edits that were not saved yet could not be.</summary>
    SaveFailed,

    /// <summary>The project that was proposed is not a timeline that can play.</summary>
    Invalid,

    /// <summary>The user or the caller stopped it.</summary>
    Canceled,

    /// <summary>A recording could not be read, copied or probed.</summary>
    MediaFailed,
}

/// <summary>Where a batch of added clips goes.</summary>
public enum StudioClipPlacementKind
{
    End,
    Index,
    ProjectTime,
}

/// <summary>Where a batch of added clips goes: after the last, at a place in the order, or at a time on the timeline (which splits the clip that is there).</summary>
public readonly record struct StudioClipPlacement(StudioClipPlacementKind Kind, int Index = 0, double ProjectTime = 0)
{
    public static StudioClipPlacement End => new(StudioClipPlacementKind.End);

    public static StudioClipPlacement AtIndex(int index) => new(StudioClipPlacementKind.Index, Index: index);

    public static StudioClipPlacement AtProjectTime(double projectTime) =>
        new(StudioClipPlacementKind.ProjectTime, ProjectTime: projectTime);
}

/// <summary>
/// A hold on the editor for a change to the timeline. It ends when the change has been published
/// and taken in, or when it is disposed, and the editor can be edited again.
/// </summary>
public sealed class StudioTimelineChangeLease : IDisposable
{
    private readonly string _baseJson;
    private Action<StudioTimelineChangeLease>? _release;

    internal StudioTimelineChangeLease(
        StudioProject baseProject,
        StudioEditableState baseState,
        Action<StudioTimelineChangeLease> release)
    {
        _baseJson = StudioProjectJson.WriteProject(baseProject);
        BaseState = baseState;
        _release = release;
    }

    /// <summary>
    /// The whole project as it was on disk when the lease was given, with every clip and the
    /// registry of recordings. Each read is a detached copy that is free to change.
    /// </summary>
    public StudioProject BaseProject => StudioProjectJson.ReadProject(_baseJson);

    /// <summary>True until the lease is disposed or its change has been taken in.</summary>
    public bool IsActive => Volatile.Read(ref _release) is not null;

    // What the editor held when the lease was given.
    internal StudioEditableState BaseState { get; }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke(this);
}

/// <summary>The answer to <see cref="StudioEditorSession.BeginTimelineChange"/>: a lease, or why there is none.</summary>
public sealed record StudioTimelineChangeStart(
    StudioTimelineChangeLease? Lease,
    StudioTimelineChangeFailure Failure,
    string Message)
{
    public bool Succeeded => Lease is not null;

    internal static StudioTimelineChangeStart Refused(StudioTimelineChangeFailure failure, string message) =>
        new(null, failure, message);
}

/// <summary>
/// What came of a change to the timeline. <see cref="Succeeded"/> is <c>Failure == None</c>. A
/// failure after the change was saved (Invalid, with a manifest) still says what was saved.
/// </summary>
public sealed record StudioTimelineChangeOutcome(
    StudioTimelineChangeFailure Failure,
    string Message,
    StudioProject? Manifest)
{
    public bool Succeeded => Failure == StudioTimelineChangeFailure.None;

    internal static StudioTimelineChangeOutcome Refused(StudioTimelineChangeFailure failure, string message) =>
        new(failure, message, null);
}
