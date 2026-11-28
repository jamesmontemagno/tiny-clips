using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.ViewModels.Studio;

/// <summary>
/// The primary clip strip of a project, and what changes its clips. The strip is in project time;
/// the lanes and inspectors under it are in the source time of the clip that is selected.
/// </summary>
public sealed partial class StudioViewModel
{
    private IReadOnlyList<StudioClipStripItem>? _clipStrip;

    /// <summary>Whether the project is a timeline of clips and not a single recording.</summary>
    public bool IsTimeline => _session.IsTimeline;

    /// <summary>
    /// A copy of the whole manifest as the editor has it, every clip included, for whoever
    /// edits it outside the editor. Never the selected clip alone.
    /// </summary>
    public StudioProject? Manifest => _session.Manifest;

    // Import what the user sees without committing the source editor's text box or undo history.
    internal StudioProject? SnapshotForTimelineImport()
    {
        var source = Manifest;
        if (source is not { SchemaVersion: 1 } || _pendingCaptionText is not { } text
            || _session.SelectedCaptionIndex is not { } index)
            return source;

        var snapshot = new StudioEditorModel(source);
        snapshot.SetCaptionText(index, text);
        return snapshot.Manifest;
    }

    public StudioTimelineMap? TimelineMap => _session.TimelineMap;

    public string? SelectedClipId => _session.SelectedClipId;

    /// <summary>The playhead on the project's timeline. <see cref="Playhead"/> is the clip's own time.</summary>
    public double ProjectPlayhead => _session.ProjectPlayhead;

    public double ProjectDuration => _session.ProjectDuration;

    /// <summary>Whether a clip can be added, or a timeline edit started, now.</summary>
    public bool CanAddClips => _session.CanAddClips;

    internal bool IsReadyForTimelineChange => _session.IsReadyForTimelineChange;

    public bool CanSplitClip => _session.CanSplitClipAtPlayhead;

    /// <summary>Whether clips can be moved, removed and joined differently: the editor is free to edit.</summary>
    public bool CanEditClips => IsEditable && !IsGeneratingCaptions;

    /// <summary>The clips of the strip, in project time. A single recording is one clip.</summary>
    public IReadOnlyList<StudioClipStripItem> ClipStrip =>
        _clipStrip ??= _session.Model is { } model && IsReady
            ? StudioClipStrip.Build(model.Manifest, model.TimelineMap, model.SelectedClipId, ClipName, model.ProjectDuration)
            : [];

    /// <summary>The selected clip, or null before the project is open.</summary>
    public StudioClipStripItem? SelectedClip => ClipStrip.FirstOrDefault(static item => item.IsSelected);

    /// <summary>True while the editor is taking clips in.</summary>
    public bool IsAddingClips => _isAddingClips;

    private bool _isAddingClips;

    // The strip's clips depend on the manifest and on which clip is selected, and both only
    // change with State, Project and Selection.
    private void InvalidateClipStrip() => _clipStrip = null;

    /// <summary>Moves the playhead to a time on the project's timeline, for the strip's own slider.</summary>
    public void ScrubProject(double projectTime)
    {
        CommitCaptionText();
        _session.ScrubProject(projectTime);
    }

    /// <summary>Selects a clip of the strip and puts the playhead on its first picture.</summary>
    public bool SelectClip(string clipId)
    {
        CommitCaptionText();
        return _session.SelectClip(clipId);
    }

    /// <summary>Splits the selected clip where the playhead is. Both halves keep their layers.</summary>
    public bool SplitClipAtPlayhead()
    {
        CommitCaptionText();
        var name = SelectedClip?.Name ?? ClipName;
        if (!_session.SplitClipAtPlayhead())
        {
            ShowError("Move the playhead inside a playable part of the clip to split it.");
            return false;
        }

        Announce(StudioClipStrip.GetClipSplitMessage(name), "StudioClipEdited", StudioAnnouncementKind.Information);
        return true;
    }

    public bool MoveClipBy(string clipId, int places)
    {
        CommitCaptionText();
        if (!_session.MoveClipBy(clipId, places))
        {
            return false;
        }

        var strip = ClipStrip;
        var moved = strip.FirstOrDefault(item => item.Id == clipId);
        if (moved is not null)
        {
            Announce(
                StudioClipStrip.GetClipMovedMessage(moved.Name, moved.Index + 1, moved.Count),
                "StudioClipEdited",
                StudioAnnouncementKind.Information);
        }

        return true;
    }

    public bool MoveClip(string clipId, int index)
    {
        CommitCaptionText();
        return _session.MoveClip(clipId, index);
    }

    public bool RemoveClip(string clipId)
    {
        CommitCaptionText();
        var name = ClipStrip.FirstOrDefault(item => item.Id == clipId)?.Name ?? ClipName;
        if (!_session.RemoveClip(clipId))
        {
            return false;
        }

        Announce(StudioClipStrip.GetClipRemovedMessage(name), "StudioClipEdited", StudioAnnouncementKind.Information);
        return true;
    }

    public bool SetClipTransition(string clipId, StudioClipTransition transition)
    {
        CommitCaptionText();
        if (!_session.SetClipTransition(clipId, transition))
        {
            return false;
        }

        var item = ClipStrip.FirstOrDefault(clip => clip.Id == clipId);
        if (item is not null)
        {
            Announce(
                StudioClipStrip.GetTransitionSetMessage(item.Name, transition),
                "StudioClipEdited",
                StudioAnnouncementKind.Information);
        }

        return true;
    }

    // What a timeline edit made outside the editor needs. The editor and whoever edits then
    // agree on one manifest, and the edit is one undo step.

    /// <summary>
    /// Takes the editor's pending edits to disk and holds the editor still until the returned
    /// lease is let go of or one of the commit methods uses it. Typed caption text is part of the
    /// pending edits.
    /// </summary>
    public StudioTimelineChangeStart BeginTimelineChange()
    {
        CommitCaptionText();
        return _session.BeginTimelineChange();
    }

    /// <summary>
    /// Publishes a change that <paramref name="commit"/> makes durable. Its saved manifest is
    /// adopted as one undoable edit. What <paramref name="commit"/> throws is not caught.
    /// </summary>
    public StudioTimelineChangeOutcome CommitTimelineChange(
        StudioTimelineChangeLease lease,
        StudioProject proposed,
        Func<StudioProject, StudioProject> commit,
        string? selectClipId = null) =>
        _session.CommitTimelineChange(lease, proposed, commit, selectClipId);

    /// <summary>Adopts a change that was made durable already, as one undoable edit.</summary>
    public StudioTimelineChangeOutcome ApplyCommittedTimelineChange(
        StudioTimelineChangeLease lease,
        StudioProject committed,
        string? selectClipId = null) =>
        _session.ApplyCommittedTimelineChange(lease, committed, selectClipId);

    /// <summary>
    /// Takes in what an export wrote to the manifest (when it was exported, and to where) without
    /// touching the edits or the history.
    /// </summary>
    public void RefreshExportBookkeeping(StudioProject saved) => _session.RefreshExportBookkeeping(saved);

    /// <summary>
    /// Brings videos into the timeline in the order given, as one transaction and one undo step.
    /// Nothing changes where one cannot be used. Says what came of it. Cancellation is not an error.
    /// </summary>
    public Task<StudioTimelineChangeOutcome> AddClipsAsync(
        IReadOnlyList<StudioTimelineImport> imports,
        StudioClipPlacement placement,
        CancellationToken cancellationToken = default) =>
        AddClipsAsync(_ => Task.FromResult(imports), placement, cancellationToken);

    /// <summary>
    /// Like the overload that takes the videos, for videos that are chosen or recorded first: the
    /// editor counts as adding from when this is called, so a second add cannot start while the
    /// first is choosing. <paramref name="acquire"/> returns nothing when nothing was chosen,
    /// which is not an error; what it throws for a file it cannot read is shown in the editor.
    /// </summary>
    public async Task<StudioTimelineChangeOutcome> AddClipsAsync(
        Func<CancellationToken, Task<IReadOnlyList<StudioTimelineImport>>> acquire,
        StudioClipPlacement placement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        if (_isAddingClips)
        {
            return new StudioTimelineChangeOutcome(
                StudioTimelineChangeFailure.NotReady,
                "Studio is still adding clips.",
                null);
        }

        CommitCaptionText();
        _isAddingClips = true;
        OnPropertyChanged(nameof(IsAddingClips));
        try
        {
            IReadOnlyList<StudioTimelineImport> imports;
            try
            {
                imports = await acquire(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new StudioTimelineChangeOutcome(StudioTimelineChangeFailure.Canceled, "Adding was cancelled.", null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or NotSupportedException)
            {
                // What the window and the store throw for the user is already a sentence.
                var message = ex is InvalidOperationException
                    ? ex.Message
                    : $"Studio could not add this to the timeline: {ex.Message}";
                ShowError(message);
                return new StudioTimelineChangeOutcome(StudioTimelineChangeFailure.MediaFailed, message, null);
            }

            if (imports.Count == 0)
            {
                return new StudioTimelineChangeOutcome(StudioTimelineChangeFailure.Canceled, "Nothing was chosen.", null);
            }

            var outcome = await _session.AddClipsAsync(imports, placement, cancellationToken);
            if (outcome.Succeeded)
            {
                Announce(
                    StudioClipStrip.GetClipAddedMessage(imports.Count),
                    "StudioClipEdited",
                    StudioAnnouncementKind.Completed);
            }

            return outcome;
        }
        finally
        {
            _isAddingClips = false;
            OnPropertyChanged(nameof(IsAddingClips));
        }
    }

    /// <summary>
    /// Asks the window to show a Studio project in the Clips Library. The name is what the
    /// Library is searched for, or empty to list every project.
    /// </summary>
    public event EventHandler<StudioTakeReference>? ShowInLibraryRequested;

    /// <summary>
    /// The take draft a clip was recorded as with Record another segment, while that draft is
    /// still in the Library. The project keeps its own copy either way, and the draft is only read.
    /// </summary>
    public string? GetOriginalTakeId(string clipId)
    {
        if (Project is not { } project || StudioTakeLinkage.TakeProjectIdForClip(project, clipId) is not { } takeId)
        {
            return null;
        }

        try
        {
            return _store.Exists(takeId) ? takeId : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Shows the clip's original take draft in the Clips Library, as Show in Library does after recording it.</summary>
    public async Task ShowOriginalTakeAsync(string clipId)
    {
        if (GetOriginalTakeId(clipId) is not { } takeId)
        {
            return;
        }

        string search;
        try
        {
            search = StudioTakeLinkage.LibraryName((await Task.Run(() => _store.Load(takeId))).Name);
        }
        catch (Exception ex)
        {
            // The Library still lists it, and shows why it cannot be read.
            System.Diagnostics.Debug.WriteLine($"Studio could not read the original take {takeId}: {ex.Message}");
            search = string.Empty;
        }

        ShowInLibraryRequested?.Invoke(this, new StudioTakeReference(takeId, search));
    }

    private void RaiseTimelineChanged()
    {
        InvalidateClipStrip();
        OnPropertyChanged(nameof(ClipStrip));
        OnPropertyChanged(nameof(SelectedClip));
        OnPropertyChanged(nameof(SelectedClipId));
        OnPropertyChanged(nameof(IsTimeline));
    }
}
