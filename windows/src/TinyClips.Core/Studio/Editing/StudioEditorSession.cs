using System.Diagnostics;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio.Preview;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Core.Studio.Editing;

/// <summary>
/// Everything one Studio editor window does that is not user interface: opening the project,
/// editing it, the transport, autosave, export, and what closing means.
/// </summary>
/// <remarks>
/// <para>
/// A session belongs to one thread, the one that created it, which is the UI thread in the app.
/// Every member is called there and every event is raised there. The preview and the exporter
/// answer on other threads, and so do timers, so the session comes back through the <c>post</c>
/// delegate it was given.
/// </para>
/// <para>
/// The preview always plays the whole recording, so <see cref="Playhead"/> is in source time. The
/// trim is applied here, by deciding where playback starts and where it stops.
/// </para>
/// </remarks>
public sealed partial class StudioEditorSession
{
    /// <summary>Shown when the project is there but its screen recording is not.</summary>
    public const string MissingRecordingMessage =
        "The original recording for this project is no longer on this PC, so it cannot be previewed or exported here.";

    /// <summary>
    /// Shown when the project itself is gone: removed by the storage rules, or deleted, since the
    /// window that offered to open it last looked.
    /// </summary>
    public const string MissingProjectMessage =
        "This project is no longer stored on this PC, so it cannot be opened in Studio. A video that was exported from it is not affected.";

    /// <summary>How long after the last edit the project is saved.</summary>
    public static readonly TimeSpan AutosaveDelay = TimeSpan.FromMilliseconds(600);

    private const int DeleteAttempts = 5;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(120);

    private readonly IStudioProjectStore _store;
    private readonly IStudioPreviewFactory _previewFactory;
    private readonly IStudioExportService _exporter;
    private readonly ICaptureSettings _settings;
    private readonly Action<Action> _post;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();

    private StudioEvents _events = new();
    private StudioProjectPaths? _paths;
    private IStudioPreview? _preview;
    private EventHandler? _positionHandler;
    private EventHandler? _isPlayingHandler;
    private EventHandler<StudioPreviewFailedEventArgs>? _failedHandler;
    private ITimer? _saveTimer;
    private int _saveGeneration;
    private CancellationTokenSource? _exportCancellation;
    private Task _exportTask = Task.CompletedTask;
    private int _exportGeneration;
    private Task? _loadTask;
    private Task? _closeTask;
    private bool _hasUnsavedEdits;
    private bool _isClosed;
    private int? _selectedZoomIndex;
    private double _playhead;

    // Read on the threads the preview raises its events on.
    private int _playGeneration;
    private int _isPositionPosted;

    /// <param name="projectId">The project to edit.</param>
    /// <param name="store">Where the project is kept.</param>
    /// <param name="previewFactory">Opens the live preview.</param>
    /// <param name="exporter">Renders the video and the poster image.</param>
    /// <param name="settings">Where a look saved as the default goes.</param>
    /// <param name="post">Runs an action on the session's thread, later. It may be called from any thread.</param>
    /// <param name="timeProvider">The clock behind the autosave delay. Tests pass one they control.</param>
    public StudioEditorSession(
        string projectId,
        IStudioProjectStore store,
        IStudioPreviewFactory previewFactory,
        IStudioExportService exporter,
        ICaptureSettings settings,
        Action<Action> post,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectId);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(previewFactory);
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(post);

        ProjectId = projectId;
        _store = store;
        _previewFactory = previewFactory;
        _exporter = exporter;
        _settings = settings;
        _post = post;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // Events

    /// <summary>Raised after anything a window shows has changed. The arguments say which part.</summary>
    public event EventHandler<StudioEditorChangedEventArgs>? Changed;

    /// <summary>Raised once an export has finished and its link is recorded in the project.</summary>
    public event EventHandler<StudioExportedEventArgs>? Exported;

    /// <summary>Raised with a sentence for the user when saving, exporting or deleting failed.</summary>
    public event EventHandler<StudioEditorErrorEventArgs>? ErrorReported;

    // State

    public string ProjectId { get; }

    public StudioEditorLoadState State { get; private set; } = StudioEditorLoadState.Loading;

    /// <summary>Why the project cannot be shown. Empty unless <see cref="State"/> is Unavailable.</summary>
    public string UnavailableMessage { get; private set; } = string.Empty;

    /// <summary>
    /// Whether a project that cannot be shown still has its screen recording, to be saved as a
    /// video of its own (<see cref="StudioScreenRecording"/>). False unless <see cref="State"/>
    /// is Unavailable.
    /// </summary>
    public bool HasScreenRecordingToSave { get; private set; }

    /// <summary>The editor state. Null until the project has been read.</summary>
    public StudioEditorModel? Model { get; private set; }

    public StudioProject? Project => Model?.Project;

    /// <summary>The live preview, for the view that shows it. Null until the session is ready.</summary>
    public IStudioPreview? Preview => _preview;

    /// <summary>
    /// Where the editor is in the recording, in source time. It moves at once when the user seeks,
    /// steps or scrubs, and follows the preview only while that is playing.
    /// </summary>
    public double Playhead
    {
        get => _playhead;
        private set
        {
            // The model is told as well: its layout controls change the scene the playhead is in.
            _playhead = value;
            if (Model is { } model)
            {
                model.SceneTime = value;
            }
        }
    }

    public bool IsPlaying { get; private set; }

    public bool IsExporting { get; private set; }

    /// <summary>How far the running export is, from 0 to 1.</summary>
    public double ExportProgress { get; private set; }

    public bool IsReady => State == StudioEditorLoadState.Ready;

    /// <summary>Whether edits, the transport and export are accepted: ready, not exporting, not closed.</summary>
    public bool IsEditable => IsReady && !IsExporting && !_isClosed;

    public bool CanUndo => IsEditable && Model is { CanUndo: true };

    public bool CanRedo => IsEditable && Model is { CanRedo: true };

    public bool CanExport => IsEditable && Model is { OutputDuration: > 0 };

    /// <summary>
    /// Whether adding a zoom at the playhead has a zoom to answer with: one fits there, or one is
    /// already there to select.
    /// </summary>
    public bool CanAddZoomAtPlayhead => IsEditable && Model is { } model && model.CanAddZoom(Playhead);

    public bool HasCamera => Model?.HasCamera ?? false;

    public bool HasNeverExported => Model?.HasNeverExported ?? false;

    /// <summary>Whether the project is pinned against automatic cleanup.</summary>
    public bool KeepSources => Model?.Project.KeepSources ?? false;

    /// <summary>True from an edit until it has been written to the project on disk.</summary>
    public bool HasUnsavedEdits => _hasUnsavedEdits;

    /// <summary>True once <see cref="CloseAsync"/> has been called.</summary>
    public bool IsClosed => _isClosed;

    /// <summary>
    /// The zoom the inspector shows, as its place in <see cref="StudioProject.Zooms"/>, or null.
    /// It stays on its zoom while edits, undo and redo change the list around it.
    /// </summary>
    public int? SelectedZoomIndex =>
        _selectedZoomIndex is { } index && Model is { } model && index >= 0 && index < model.Project.Zooms.Length
            ? index
            : null;

    public StudioZoom? SelectedZoom => SelectedZoomIndex is { } index ? Model?.Project.Zooms[index] : null;

    /// <summary>Whether the recording has clicks to suggest zooms from. One of a window has none.</summary>
    public bool HasClicks => _events.Clicks is { Length: > 0 };

    /// <summary>Whether the recording has pointer positions for a zoom to follow. One of a window has none.</summary>
    public bool HasPointerPositions => _events.Cursor is { Length: > 0 };

    // Text

    /// <summary>The project name, or "Untitled recording".</summary>
    public string ClipName => StudioEditorText.GetClipName(Project?.Name);

    /// <summary>The playhead and the length of the video, in output time: <c>0:02.5 / 0:10.0</c>.</summary>
    public string TimeText => Model is { } model
        ? StudioEditorText.GetTimeText(model.GetOutputTime(Playhead), model.OutputDuration)
        : StudioEditorText.GetTimeText(0, 0);

    /// <summary>The pixel size the project exports at.</summary>
    public StudioSize ExportSize => Project is { } project
        ? StudioExportLimits.GetExportSize(project)
        : new StudioSize(1920, 1080);

    public string ExportSizeText => StudioEditorText.GetExportSizeText(ExportSize);

    /// <summary>The camera bubble on a canvas of the given size, while the bubble layout is showing.</summary>
    public StudioFrameRect? GetBubbleRect(double canvasWidth, double canvasHeight) =>
        Model is { EffectiveLayout: StudioLayout.Bubble } model ? model.GetBubbleRect(canvasWidth, canvasHeight) : null;

    // Loading

    /// <summary>
    /// Opens the project and its preview. The returned task finishes when the session is ready or
    /// unavailable. It never fails. Calling it again returns the same task.
    /// </summary>
    public Task LoadAsync() => _loadTask ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        if (_isClosed)
        {
            return;
        }

        StudioEditorModel model;
        StudioProjectPaths paths;
        try
        {
            var opened = _store.MarkOpened(ProjectId);
            paths = _store.GetPaths(opened);
            model = new StudioEditorModel(opened);
        }
        catch (Exception ex)
        {
            // A project file that is not there says so in the words of the file system, with a
            // path nobody has seen before. Anything else is said as it is.
            SetUnavailable(ex is FileNotFoundException or DirectoryNotFoundException ? MissingProjectMessage : ex.Message);
            return;
        }

        _paths = paths;
        Model = model;

        if (!File.Exists(paths.ScreenPath))
        {
            SetUnavailable(MissingRecordingMessage);
            return;
        }

        _events = LoadEvents();

        IStudioPreview preview;
        try
        {
            preview = await _previewFactory.OpenAsync(model.Project, _events, paths, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await PostAsync(() =>
            {
                if (!_isClosed)
                {
                    SetUnavailable(ex.Message);
                }
            }).ConfigureAwait(false);
            return;
        }

        await PostAsync(() => AttachPreview(preview, model)).ConfigureAwait(false);
    }

    /// <summary>Clicks and cursor samples are optional: a project without readable events has none.</summary>
    private StudioEvents LoadEvents()
    {
        try
        {
            return _store.LoadEvents(ProjectId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Studio events could not be read for {ProjectId}: {ex.Message}");
            return new StudioEvents();
        }
    }

    private void AttachPreview(IStudioPreview preview, StudioEditorModel model)
    {
        if (_isClosed)
        {
            _ = DisposeQuietlyAsync(preview);
            return;
        }

        _preview = preview;
        _positionHandler = (_, _) => OnPreviewPositionRaised(preview);
        _isPlayingHandler = (_, _) => OnPreviewIsPlayingRaised(preview);
        _failedHandler = (_, e) => OnPreviewFailedRaised(preview, e.Message);
        preview.PositionChanged += _positionHandler;
        preview.IsPlayingChanged += _isPlayingHandler;
        preview.Failed += _failedHandler;

        // The contract has no separate mute call: muting is part of the project the preview draws.
        preview.UpdateProject(model.Project);
        State = StudioEditorLoadState.Ready;
        Seek(model.TrimStart);
        RaiseChanged(StudioEditorChanges.All);
    }

    private void SetUnavailable(string? message)
    {
        State = StudioEditorLoadState.Unavailable;
        UnavailableMessage = string.IsNullOrWhiteSpace(message) ? "The project could not be opened." : message;
        HasScreenRecordingToSave = FindScreenRecordingQuietly();
        IsPlaying = false;
        RaiseChanged(StudioEditorChanges.All);
    }

    private bool FindScreenRecordingQuietly()
    {
        try
        {
            return _store.FindScreenRecording(ProjectId) is not null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Studio could not look for the screen recording of {ProjectId}: {ex.Message}");
            return false;
        }
    }

    // Edits

    /// <summary>
    /// True from <see cref="BeginGesture"/> to <see cref="EndGesture"/>: a pointer is dragging
    /// something, and all it changes is one undo step. The keys that change the project wait
    /// until it is over (<see cref="StudioShortcutInput.IsDragging"/>).
    /// </summary>
    public bool IsInGesture => Model is { IsGroupingEdits: true };

    /// <summary>Starts a gesture such as a drag. Everything until <see cref="EndGesture"/> is one undo step.</summary>
    public void BeginGesture()
    {
        if (IsEditable)
        {
            Model?.BeginEditingGroup();
        }
    }

    public void EndGesture()
    {
        if (Model is not { IsGroupingEdits: true } model)
        {
            return;
        }

        var couldUndo = model.CanUndo;
        var couldRedo = model.CanRedo;
        model.CommitEditingGroup();
        if (model.CanUndo != couldUndo || model.CanRedo != couldRedo)
        {
            RaiseChanged(StudioEditorChanges.Project);
        }
    }

    public void Undo() => Edit(static model => model.Undo());

    public void Redo() => Edit(static model => model.Redo());

    public void SetLayout(StudioLayout layout) => EditCurrentScene(model => model.SetLayout(layout));

    public void SetCanvasAspect(StudioCanvasAspect aspect) => Edit(model => model.SetCanvasAspect(aspect));

    public void SetCanvasPadding(double value) => Edit(model => model.SetCanvasPadding(value));

    /// <summary>
    /// Fills the canvas with a solid or gradient preset. The preset's id is stored next to the
    /// colors, which are <c>#RRGGBB</c>. A solid uses <paramref name="primary"/> only. Other
    /// styles are ignored.
    /// </summary>
    public void SetBackgroundPreset(StudioBackgroundStyle style, string presetId, string primary, string? secondary = null)
    {
        switch (style)
        {
            case StudioBackgroundStyle.Solid:
                Edit(model => model.SetBackground(StudioBackgroundStyle.Solid, presetId, primary));
                break;
            case StudioBackgroundStyle.Gradient:
                Edit(model => model.SetBackground(StudioBackgroundStyle.Gradient, presetId, primary, secondary));
                break;
        }
    }

    /// <summary>
    /// No background: the canvas is black, as in a recording made without Studio. The colors stay
    /// in the project.
    /// </summary>
    public void RemoveBackground() => Edit(static model =>
    {
        var current = model.Project.Canvas.Background;
        model.SetBackground(StudioBackgroundStyle.None, null, current.Primary, current.Secondary);
    });

    public void SetScreenCornerRadius(double value) => Edit(model => model.SetScreenCornerRadius(value));

    public void SetScreenShadow(double value) => Edit(model => model.SetScreenShadow(value));

    public void SetScreenCrop(StudioRect? crop) => Edit(model => model.SetScreenCrop(crop));

    public void ClearScreenCrop() => Edit(static model => model.ClearScreenCrop());

    /// <summary>Moves one edge of the screen crop to cut off a fraction of the frame.</summary>
    public void SetScreenCropInset(StudioCropEdge edge, double value) =>
        Edit(model => model.SetScreenCropInset(edge, value));

    public void SetCameraShape(StudioCameraShape shape) => Edit(model => model.SetCameraShape(shape));

    public void SetCameraCornerRadius(double value) => Edit(model => model.SetCameraCornerRadius(value));

    public void SetCameraBubbleSize(double value) => EditCurrentScene(model => model.SetCameraBubbleSize(value));

    /// <summary>Snaps the bubble to a corner, clearing its offsets.</summary>
    public void SetCameraAnchor(StudioAnchor anchor) => EditCurrentScene(model => model.SetCameraAnchor(anchor));

    public void SetCameraBubbleOffsets(double x, double y) => EditCurrentScene(model => model.SetCameraBubbleOffsets(x, y));

    /// <summary>Moves the bubble sideways from its corner. The other offset stays as the scene has it.</summary>
    public void SetCameraBubbleOffsetX(double x) =>
        EditCurrentScene(model => model.SetCameraBubbleOffsets(x, model.CurrentScene.Bubble.OffsetY));

    /// <summary>Moves the bubble up or down from its corner. The other offset stays as the scene has it.</summary>
    public void SetCameraBubbleOffsetY(double y) =>
        EditCurrentScene(model => model.SetCameraBubbleOffsets(model.CurrentScene.Bubble.OffsetX, y));

    /// <summary>Moves the bubble so its top-left corner is at a point in canvas pixels.</summary>
    public void MoveBubbleTopLeft(double x, double y, double canvasWidth, double canvasHeight) =>
        EditCurrentScene(model => model.MoveBubbleTopLeft(x, y, canvasWidth, canvasHeight));

    public void SetCameraMirror(bool isMirrored) => Edit(model => model.SetCameraMirror(isMirrored));

    /// <summary>Keeps, blurs or takes away everything in the camera picture that is not a person.</summary>
    public void SetCameraCutout(StudioCameraCutout cutout) => Edit(model => model.SetCameraCutout(cutout));

    public void SetCameraBorderWidth(double value) => Edit(model => model.SetCameraBorderWidth(value));

    public void SetCameraShadow(double value) => Edit(model => model.SetCameraShadow(value));

    public void SetCameraCrop(StudioRect? crop) => Edit(model => model.SetCameraCrop(crop));

    public void ClearCameraCrop() => Edit(static model => model.ClearCameraCrop());

    /// <summary>Moves one edge of the camera crop to cut off a fraction of the frame.</summary>
    public void SetCameraCropInset(StudioCropEdge edge, double value) =>
        Edit(model => model.SetCameraCropInset(edge, value));

    public void SetSideBySide(StudioCameraSide cameraSide, double fraction) =>
        EditCurrentScene(model => model.SetSideBySide(cameraSide, fraction));

    /// <summary>
    /// Sets how much of the canvas the camera takes side by side. Its side stays as the scene has it.
    /// </summary>
    public void SetCameraShare(double fraction) =>
        EditCurrentScene(model => model.SetSideBySide(model.CurrentScene.Split.CameraSide, fraction));

    public void SetMuted(bool isMuted) => Edit(model => model.SetMuted(isMuted));

    public void SetClickRingsEnabled(bool isEnabled) => Edit(model => model.SetClickRingsEnabled(isEnabled));

    public void SetBrandingEnabled(bool isEnabled) => Edit(model => model.SetBrandingEnabled(isEnabled));

    // Zooms. An index is a zoom's place in Project.Zooms, which is kept in time order. Every edit
    // answers with the place the zoom has afterwards, or none when it is gone. An edit that is
    // refused, because the project cannot be edited just now, leaves the zoom where it was.
    //
    // One zoom can be selected, or one cut, or one speed change, never two of them. An edit to the
    // selected zoom takes the selection with it, and so does adding a zoom. Through every other
    // edit, and undo and redo, the selection follows its zoom as StudioEditorModel.FindZoomFollowing
    // finds it, or lets go when the zoom is gone.

    // What is selected. At most one of the three is not null.
    private (int? Zoom, int? Cut, int? Speed) Selection => (SelectedZoomIndex, SelectedCutIndex, SelectedSpeedIndex);

    /// <summary>The zoom that contains a source time, or null.</summary>
    public int? GetZoomIndexAt(double sourceTime) => Model?.GetZoomIndexAt(sourceTime);

    /// <summary>
    /// Selects a zoom, or none with null or a place that has no zoom. The playhead stays. A
    /// selected cut or speed change is let go when a zoom is selected.
    /// </summary>
    public void SelectZoom(int? index)
    {
        var before = Selection;
        _selectedZoomIndex = index;
        _selectedZoomIndex = SelectedZoomIndex;
        if (_selectedZoomIndex is not null)
        {
            _selectedCutIndex = null;
            _selectedSpeedIndex = null;
        }

        if (Selection != before)
        {
            RaiseChanged(StudioEditorChanges.Selection);
        }
    }

    /// <summary>
    /// Lets go of whatever is selected: a zoom, a cut, or a speed change. The playhead stays. A
    /// press on an empty part of a lane does this: selecting no zoom alone would leave a selected
    /// cut as it is.
    /// </summary>
    public void SelectNothing()
    {
        var before = Selection;
        _selectedZoomIndex = null;
        _selectedCutIndex = null;
        _selectedSpeedIndex = null;
        if (Selection != before)
        {
            RaiseChanged(StudioEditorChanges.Selection);
        }
    }

    /// <summary>
    /// Selects the zoom after the selected one and shows it. With nothing selected, the zoom at
    /// the playhead or the first one after it. False when there is none.
    /// </summary>
    public bool SelectNextZoom() => SelectAndShowZoom(Model?.GetZoomIndexAfter(SelectedZoomIndex, Playhead));

    /// <summary>
    /// Selects the zoom before the selected one and shows it. With nothing selected, the zoom at
    /// the playhead or the last one before it. False when there is none.
    /// </summary>
    public bool SelectPreviousZoom() => SelectAndShowZoom(Model?.GetZoomIndexBefore(SelectedZoomIndex, Playhead));

    /// <summary>
    /// Adds a zoom that starts at a source time and looks at where the pointer is then, and
    /// selects it. Where a zoom already is, that one is selected instead.
    /// </summary>
    public StudioZoomEditResult AddZoom(double sourceTime)
    {
        var result = new StudioZoomEditResult(false, null);
        EditAndSelect(model =>
        {
            result = model.AddZoom(sourceTime, _events);
            return result.Index is { } index ? EditSelection.Zoom(index) : null;
        });
        return result;
    }

    /// <summary>
    /// Adds a zoom at the playhead and selects it. A zoom starts unzoomed, so a paused playhead
    /// then moves to where the zoom has moved in, which shows what it looks at.
    /// </summary>
    public StudioZoomEditResult AddZoomAtPlayhead()
    {
        var result = AddZoom(Playhead);
        if (result is { Changed: true, Index: { } index } && !IsPlaying && Model?.GetZoomLookTime(index) is { } time)
        {
            Scrub(time);
        }

        return result;
    }

    public StudioZoomEditResult RemoveZoom(int index) => EditZoom(index, model => model.RemoveZoom(index));

    /// <summary>Removes the selected zoom. Unchanged, with no index, when none is selected.</summary>
    public StudioZoomEditResult RemoveSelectedZoom() =>
        SelectedZoomIndex is { } index ? RemoveZoom(index) : new StudioZoomEditResult(false, null);

    public StudioZoomEditResult SetZoomStart(int index, double sourceTime) =>
        EditZoom(index, model => model.SetZoomStart(index, sourceTime));

    public StudioZoomEditResult SetZoomEnd(int index, double sourceTime) =>
        EditZoom(index, model => model.SetZoomEnd(index, sourceTime));

    /// <summary>Moves a whole zoom so it starts at a source time, keeping its length.</summary>
    public StudioZoomEditResult MoveZoom(int index, double sourceTime) =>
        EditZoom(index, model => model.MoveZoom(index, sourceTime));

    public StudioZoomEditResult SetZoomScale(int index, double scale) =>
        EditZoom(index, model => model.SetZoomScale(index, scale));

    public StudioZoomEditResult SetZoomFocusMode(int index, StudioZoomFocusMode mode) =>
        EditZoom(index, model => model.SetZoomFocusMode(index, mode));

    public StudioZoomEditResult SetZoomFocusPoint(int index, double x, double y) =>
        EditZoom(index, model => model.SetZoomFocusPoint(index, x, y));

    /// <summary>Points a zoom at a place on its focus pad, from 0 to 1 across and down the pad.</summary>
    public StudioZoomEditResult SetZoomFocusOnPad(int index, double x, double y) =>
        EditZoom(index, model => model.SetZoomFocusOnPad(index, x, y));

    public StudioZoomEditResult SetZoomEaseIn(int index, double seconds) =>
        EditZoom(index, model => model.SetZoomEaseIn(index, seconds));

    public StudioZoomEditResult SetZoomEaseOut(int index, double seconds) =>
        EditZoom(index, model => model.SetZoomEaseOut(index, seconds));

    /// <summary>
    /// Replaces the suggested zooms with new ones worked out from the recording's clicks, as one
    /// undo step. Zooms the user made or changed stay. Returns whether anything changed.
    /// </summary>
    public bool ApplyZoomSuggestions() =>
        Edit(model => model.ApplyZoomSuggestions(StudioZoomSuggestions.Suggest(model.Project, _events)), false);

    /// <summary>Removes the suggested zooms, as one undo step. Returns whether there were any.</summary>
    public bool RemoveZoomSuggestions() =>
        Edit(static model => model.ApplyZoomSuggestions([]), false);

    /// <summary>Moves the trim start, from a handle, and shows the frame the video now starts on.</summary>
    public void SetTrimStart(double sourceTime)
    {
        Edit(model => model.SetTrimStart(sourceTime));
        if (IsEditable && Model is { } model)
        {
            Scrub(model.TrimStart);
        }
    }

    /// <summary>
    /// Moves the trim end, from a handle, and shows the picture at the new end. That picture is
    /// just past the last frame the video keeps: an export holds the frames before this instant.
    /// </summary>
    public void SetTrimEnd(double sourceTime)
    {
        Edit(model => model.SetTrimEnd(sourceTime));
        if (IsEditable && Model is { } model)
        {
            Scrub(model.TrimEnd);
        }
    }

    /// <summary>Starts the video at the playhead. The playhead stays where it is.</summary>
    public void SetTrimStartAtPlayhead()
    {
        var time = Playhead;
        Edit(model => model.SetTrimStart(time));
    }

    /// <summary>Ends the video at the playhead. The playhead stays where it is.</summary>
    public void SetTrimEndAtPlayhead()
    {
        var time = Playhead;
        Edit(model => model.SetTrimEnd(time));
    }

    /// <summary>Makes the project's canvas, screen and camera styling the look new recordings start with.</summary>
    public void SaveDefaultLook()
    {
        if (Model is { } model)
        {
            _settings.StudioDefaultLook = model.CurrentLook;
        }
    }

    /// <summary>
    /// Pins the project against automatic cleanup, or lets go of it. It is written into the
    /// project on disk at once. It is not an edit: Undo leaves it alone, and it does not have
    /// to wait for an export to end.
    /// </summary>
    /// <returns>False, after reporting the error, when it could not be written.</returns>
    public bool SetKeepSources(bool keepSources)
    {
        if (_isClosed || Model is not { } model)
        {
            return false;
        }

        if (model.Project.KeepSources == keepSources)
        {
            return true;
        }

        try
        {
            model.RefreshBookkeeping(_store.SetKeepSources(ProjectId, keepSources));
            RaiseChanged(StudioEditorChanges.Project);
            return true;
        }
        catch (Exception ex)
        {
            ReportError(StudioEditorErrorKind.Keep, $"Studio could not change whether this project is kept: {ex.Message}");
            return false;
        }
    }

    private void Edit(Action<StudioEditorModel> change) =>
        EditAndSelect(model =>
        {
            change(model);
            return null;
        });

    // An edit to the scene the playhead is in: its layout, its bubble, its split, or how long the
    // move into it takes.
    private void EditCurrentScene(Action<StudioEditorModel> change)
    {
        HoldSceneForDrag();
        Edit(change);
    }

    // A drag is many changes to the scene the playhead is in. While the recording plays, the
    // playhead may come into the next scene before the drag is over, and the rest of the drag
    // would then change that scene as well. So the first such change of a drag stops playback
    // where the picture is, and the whole drag stays in one scene. A change that stands alone, a
    // key or a choice from a list, is over at once and leaves playback alone. So does a drag in a
    // recording with one scene, which has no other scene to come into.
    private void HoldSceneForDrag()
    {
        if (IsEditable && IsPlaying && Model is { IsGroupingEdits: true, Project.Scenes.Length: > 1 })
        {
            Pause();
        }
    }

    // An edit that may say where the selection goes: to a zoom, a cut or a speed change, or away
    // from the one it was on. One that does not leaves it to follow whichever it was on.
    private void EditAndSelect(Func<StudioEditorModel, EditSelection?> change)
    {
        if (!IsEditable || Model is not { } model)
        {
            return;
        }

        var before = model.EditableState;
        var zoomsBefore = model.Project.Zooms;
        var cutsBefore = model.Project.Edits.Cuts;
        var speedBefore = model.Project.Edits.Speed;
        var selected = Selection;
        var selection = change(model);
        var isChanged = !model.EditableState.ContentEquals(before);

        if (selection is { Kind: EditSelectionKind.Zoom } zoomChosen)
        {
            _selectedZoomIndex = zoomChosen.Index;
        }
        else if (isChanged && selected.Zoom is { } zoomIndex)
        {
            _selectedZoomIndex = StudioEditorModel.FindZoomFollowing(zoomsBefore, zoomIndex, model.Project.Zooms);
        }

        if (selection is { Kind: EditSelectionKind.Cut } cutChosen)
        {
            _selectedCutIndex = cutChosen.Index;
        }
        else if (isChanged && selected.Cut is { } cutIndex)
        {
            _selectedCutIndex = StudioEditorModel.FindCutFollowing(cutsBefore, cutIndex, model.Project.Edits.Cuts);
        }

        if (selection is { Kind: EditSelectionKind.Speed } speedChosen)
        {
            _selectedSpeedIndex = speedChosen.Index;
        }
        else if (isChanged && selected.Speed is { } speedIndex)
        {
            _selectedSpeedIndex = StudioEditorModel.FindSpeedFollowing(speedBefore, speedIndex, model.Project.Edits.Speed);
        }

        // One selection: an edit that says where a zoom is lets go of a cut and a speed change,
        // and so on. An edit to the selected zoom finds nothing else selected, so only adding one
        // does.
        if (selection is { } chosen)
        {
            if (chosen.Kind != EditSelectionKind.Zoom)
            {
                _selectedZoomIndex = null;
            }

            if (chosen.Kind != EditSelectionKind.Cut)
            {
                _selectedCutIndex = null;
            }

            if (chosen.Kind != EditSelectionKind.Speed)
            {
                _selectedSpeedIndex = null;
            }
        }

        _selectedZoomIndex = SelectedZoomIndex;
        _selectedCutIndex = SelectedCutIndex;
        _selectedSpeedIndex = SelectedSpeedIndex;
        var selectionChange = Selection != selected
            ? StudioEditorChanges.Selection
            : StudioEditorChanges.None;
        if (!isChanged)
        {
            if (selectionChange != StudioEditorChanges.None)
            {
                RaiseChanged(selectionChange);
            }

            return;
        }

        _hasUnsavedEdits = true;
        ScheduleSave();
        _preview?.UpdateProject(model.Project);
        if (IsPlaying && model.IsAtPlaybackEnd(Playhead))
        {
            PausePreview();
        }

        RaiseChanged(StudioEditorChanges.Project | StudioEditorChanges.Playback | selectionChange);
    }

    // An edit that also has something to say about what it did.
    private TResult Edit<TResult>(Func<StudioEditorModel, TResult> change, TResult refused)
    {
        var result = refused;
        Edit(model => { result = change(model); });
        return result;
    }

    // An edit to one zoom. The selection goes with the zoom when it is the selected one.
    private StudioZoomEditResult EditZoom(int index, Func<StudioEditorModel, StudioZoomEditResult> change)
    {
        var result = new StudioZoomEditResult(false, index);
        var isSelected = SelectedZoomIndex == index;
        EditAndSelect(model =>
        {
            result = change(model);
            return isSelected ? EditSelection.Zoom(result.Index) : null;
        });
        return result;
    }

    /// <summary>
    /// Selects a zoom and shows it where it has moved in: the playhead goes to the end of its
    /// ease in, as it does for <see cref="SelectNextZoom"/>. False, with nothing changed, when
    /// there is no such zoom or the project cannot be edited just now.
    /// </summary>
    public bool SelectAndShowZoom(int? index)
    {
        if (index is not { } zoom || !IsEditable || Model?.GetZoomLookTime(zoom) is not { } time)
        {
            return false;
        }

        SelectZoom(zoom);
        Scrub(time);
        return true;
    }

    private enum EditSelectionKind
    {
        Zoom,
        Cut,
        Speed,
    }

    // Where the selection goes after an edit that knows: to a zoom, a cut or a speed change, or
    // away from the one it was on when there is no index.
    private readonly record struct EditSelection(EditSelectionKind Kind, int? Index)
    {
        public static EditSelection Zoom(int? index) => new(EditSelectionKind.Zoom, index);

        public static EditSelection Cut(int? index) => new(EditSelectionKind.Cut, index);

        public static EditSelection Speed(int? index) => new(EditSelectionKind.Speed, index);
    }

    // Transport

    /// <summary>
    /// Pauses when playing. Otherwise plays from the playhead, or from the trim start when the
    /// playhead is outside the kept range or at its end.
    /// </summary>
    public void TogglePlayback()
    {
        if (!IsEditable || Model is not { } model || _preview is not { } preview)
        {
            return;
        }

        if (IsPlaying)
        {
            Pause();
            return;
        }

        var start = model.GetPlaybackStart(Playhead);
        if (Math.Abs(start - Playhead) > model.FrameDuration / 2)
        {
            Seek(start);
        }

        _cutSkipTarget = null;
        ApplyPlaybackRate(preview, model, start);
        Interlocked.Increment(ref _playGeneration);
        preview.Play();
        IsPlaying = true;
        RaiseChanged(StudioEditorChanges.Playback);
    }

    public void Pause()
    {
        var wasPlaying = IsPlaying;
        PausePreview();
        if (wasPlaying)
        {
            RaiseChanged(StudioEditorChanges.Playback);
        }
    }

    /// <summary>Pauses and moves the playhead by a number of frames. Negative steps go back.</summary>
    public void StepFrames(int count)
    {
        if (!IsEditable || Model is not { } model)
        {
            return;
        }

        PausePreview();
        Seek(Playhead + count * model.FrameDuration);
        RaiseChanged(StudioEditorChanges.Playback);
    }

    /// <summary>Pauses and moves the playhead to a source time, for dragging along the timeline.</summary>
    public void Scrub(double sourceTime)
    {
        if (!IsEditable)
        {
            return;
        }

        PausePreview();
        Seek(sourceTime);
        RaiseChanged(StudioEditorChanges.Playback);
    }

    private void PausePreview()
    {
        var wasPlaying = IsPlaying;
        _preview?.Pause();
        IsPlaying = false;

        // While playing, the playhead is the last position that was handled, which trails the
        // picture by however long the session's thread took to get to it. Once Pause has returned,
        // the preview's position is the frame the picture stays on. A step, or a trim at the
        // playhead, has to start from that frame.
        if (wasPlaying && _preview is { } preview && Model is { } model)
        {
            var position = preview.Position;
            if (double.IsFinite(position))
            {
                Playhead = model.ClampSourceTime(position);
            }
        }
    }

    private void Seek(double sourceTime)
    {
        if (Model is not { } model)
        {
            return;
        }

        var time = model.ClampSourceTime(sourceTime);
        Playhead = time;
        _preview?.Seek(time);
    }

    // Preview events. These three arrive on a worker thread.

    private void OnPreviewPositionRaised(IStudioPreview preview)
    {
        // A playing preview raises this for every frame, so only one notice waits at a time.
        if (Interlocked.Exchange(ref _isPositionPosted, 1) != 0)
        {
            return;
        }

        _post(() =>
        {
            Volatile.Write(ref _isPositionPosted, 0);
            HandlePreviewPosition(preview);
        });
    }

    private void OnPreviewIsPlayingRaised(IStudioPreview preview)
    {
        var generation = Volatile.Read(ref _playGeneration);
        _post(() => HandlePreviewIsPlayingChanged(preview, generation));
    }

    private void OnPreviewFailedRaised(IStudioPreview preview, string message) =>
        _post(() => HandlePreviewFailed(preview, message));

    private void HandlePreviewPosition(IStudioPreview preview)
    {
        if (!ReferenceEquals(preview, _preview) || !IsPlaying || Model is not { } model)
        {
            return;
        }

        var position = preview.Position;
        if (!double.IsFinite(position))
        {
            return;
        }

        if (!model.IsAtPlaybackEnd(position) && model.GetCutSkipTarget(position) is { } target)
        {
            // Playback has reached a cut and goes on from its end. The preview reports positions
            // inside the cut until its seek has landed. Those do not send it there again, and
            // they leave the playhead where it was sent.
            if (target != _cutSkipTarget)
            {
                _cutSkipTarget = target;
                Seek(target);

                // The stretch after the cut may play at another speed than the one before it.
                ApplyPlaybackRate(preview, model, target);
            }
        }
        else
        {
            Playhead = model.ClampSourceTime(position);
            if (model.IsAtPlaybackEnd(position))
            {
                PausePreview();
                Seek(model.PlaybackEnd);
            }
            else
            {
                ApplyPlaybackRate(preview, model, position);
            }
        }

        RaiseChanged(StudioEditorChanges.Playback);
    }

    private void HandlePreviewIsPlayingChanged(IStudioPreview preview, int generation)
    {
        // A notice raised before the latest Play is about an earlier pause.
        if (!ReferenceEquals(preview, _preview)
            || !IsPlaying
            || generation != Volatile.Read(ref _playGeneration)
            || preview.IsPlaying
            || Model is not { } model)
        {
            return;
        }

        // The preview stopped by itself: the end of the recording, or an interruption.
        IsPlaying = false;
        var position = preview.Position;
        if (double.IsFinite(position))
        {
            Playhead = model.ClampSourceTime(position);

            // A preview reports the start of the frame it shows, so at the end of the recording it
            // stops one frame short of the trim end. That is the end of playback all the same, and
            // the playhead goes to the end of the video as it does when playback is stopped there.
            if (model.IsAtPlaybackEnd(Playhead + model.FrameDuration))
            {
                Seek(model.PlaybackEnd);
            }
        }

        RaiseChanged(StudioEditorChanges.Playback);
    }

    private void HandlePreviewFailed(IStudioPreview preview, string message)
    {
        if (ReferenceEquals(preview, _preview))
        {
            SetUnavailable(message);
        }
    }

    // Autosave

    /// <summary>
    /// Writes the edits into the project on disk now. Only the edited parts are replaced, so export
    /// links or a name changed elsewhere are kept. Returns false, after reporting the error, when
    /// the project could not be saved.
    /// </summary>
    public bool SaveNow()
    {
        CancelScheduledSave();
        if (!_hasUnsavedEdits || Model is not { } model)
        {
            return true;
        }

        try
        {
            var onDisk = _store.Load(ProjectId);
            var saved = _store.Save(model.EditableState.ApplyTo(onDisk));
            model.RefreshBookkeeping(saved);
            _hasUnsavedEdits = false;
            RaiseChanged(StudioEditorChanges.Project);
            return true;
        }
        catch (Exception ex)
        {
            ReportError(StudioEditorErrorKind.Save, $"Studio could not save this project: {ex.Message}");
            return false;
        }
    }

    private void ScheduleSave()
    {
        CancelScheduledSave();
        var generation = _saveGeneration;
        _saveTimer = _timeProvider.CreateTimer(
            _ => _post(() => OnSaveTimer(generation)),
            null,
            AutosaveDelay,
            Timeout.InfiniteTimeSpan);
    }

    private void CancelScheduledSave()
    {
        _saveGeneration++;
        _saveTimer?.Dispose();
        _saveTimer = null;
    }

    private void OnSaveTimer(int generation)
    {
        if (!_isClosed && generation == _saveGeneration)
        {
            SaveNow();
        }
    }

    // Export

    /// <summary>
    /// Renders the project to a video. Does nothing unless <see cref="CanExport"/>. The edits are
    /// saved first, because the export link is written into the saved project.
    /// </summary>
    /// <param name="createOutputPath">
    /// Returns the full path to write, in the folder and with the name the app gives a saved video.
    /// Called on the session's thread: once when the export starts, and again when the video is
    /// finished and a file has taken that name in the meantime, for the name the video gets then.
    /// </param>
    /// <param name="codec">The video codec chosen in settings.</param>
    /// <returns>How the export ended. The task itself never fails.</returns>
    public Task<StudioExportOutcome> ExportAsync(Func<string> createOutputPath, VideoCodec codec)
    {
        ArgumentNullException.ThrowIfNull(createOutputPath);
        if (!CanExport || Model is not { } model || _paths is not { } paths)
        {
            return Task.FromResult(StudioExportOutcome.NotStarted);
        }

        Pause();
        if (!SaveNow())
        {
            return Task.FromResult(StudioExportOutcome.NotStarted);
        }

        var task = ExportCoreAsync(model, paths, createOutputPath, codec);
        _exportTask = task;
        return task;
    }

    /// <summary>Stops the running export. The session goes back to idle without a message.</summary>
    public void CancelExport() => _exportCancellation?.Cancel();

    private async Task<StudioExportOutcome> ExportCoreAsync(
        StudioEditorModel model,
        StudioProjectPaths paths,
        Func<string> createOutputPath,
        VideoCodec codec)
    {
        var generation = ++_exportGeneration;
        var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        IsExporting = true;
        ExportProgress = 0;
        RaiseChanged(StudioEditorChanges.All);

        var project = model.Project;
        var rendered = model.EditableState;
        var events = _events;
        string? outputPath = null;
        string? stagedPath = null;
        string? failure = null;
        var isWritten = false;
        try
        {
            outputPath = createOutputPath();

            // The video is written under a name of its own, and gets the one that was made for
            // it only once it is complete, in FinishExport. Making it takes minutes. Whatever
            // else is saved in that time is offered the same name, because no file has it yet,
            // and would lose its file to this video if the video simply took the name at the end.
            stagedPath = StudioRenderingMath.StagedOutputPath(outputPath);
            var progress = new ExportProgressRelay(this, generation);
            await _exporter
                .ExportAsync(project, events, paths, stagedPath, codec, progress, cancellation.Token)
                .ConfigureAwait(false);
            isWritten = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // An exporter that cannot write says which file, and that is the one under the
            // name nobody has seen. The message is about the video.
            failure = stagedPath is null || outputPath is null
                ? ex.Message
                : ex.Message.Replace(stagedPath, outputPath, StringComparison.OrdinalIgnoreCase);
        }

        if (isWritten)
        {
            // The Clips Library thumbnail. The video is what matters, so a failure here is ignored.
            await WritePosterQuietlyAsync(project, events, paths, cancellation.Token).ConfigureAwait(false);
        }

        var outcome = StudioExportOutcome.Cancelled;
        await PostAsync(() =>
        {
            outcome = FinishExport(cancellation, isWritten ? stagedPath : null, outputPath, createOutputPath, rendered, failure);
        }).ConfigureAwait(false);
        return outcome;
    }

    private StudioExportOutcome FinishExport(
        CancellationTokenSource cancellation,
        string? stagedPath,
        string? outputPath,
        Func<string> createOutputPath,
        StudioEditableState rendered,
        string? failure)
    {
        if (ReferenceEquals(_exportCancellation, cancellation))
        {
            _exportCancellation = null;
        }

        cancellation.Dispose();

        string? exportedPath = null;
        if (stagedPath is not null && outputPath is not null)
        {
            try
            {
                // The video gets its name and the project its link to it in one step on this
                // thread, so that whatever notices the new file finds the link as well.
                var placedPath = StudioFilePlacement.Place(stagedPath, outputPath, createOutputPath, "the video was being made");
                var saved = _store.RecordExport(ProjectId, placedPath);
                Model?.RefreshBookkeeping(saved);
                Model?.MarkExported(rendered);
                exportedPath = placedPath;
            }
            catch (Exception ex)
            {
                failure = ex.Message;

                // Still there when it could not be given a name. A video that has its name
                // stays, though the project could not take note of it.
                DeleteQuietly(stagedPath);
            }
        }

        IsExporting = false;
        ExportProgress = exportedPath is not null ? 1 : 0;
        RaiseChanged(StudioEditorChanges.All);

        if (exportedPath is not null)
        {
            Exported?.Invoke(this, new StudioExportedEventArgs(ProjectId, exportedPath));
            return StudioExportOutcome.Exported;
        }

        if (failure is null)
        {
            return StudioExportOutcome.Cancelled;
        }

        if (!_isClosed)
        {
            ReportError(StudioEditorErrorKind.Export, $"Studio export failed: {failure}");
        }

        return StudioExportOutcome.Failed;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Studio could not remove {path}: {ex.Message}");
        }
    }

    private void ApplyExportProgress(int generation, double value)
    {
        if (!IsExporting || generation != _exportGeneration || !double.IsFinite(value))
        {
            return;
        }

        ExportProgress = Math.Min(Math.Max(value, 0), 1);
        RaiseChanged(StudioEditorChanges.Export);
    }

    private async Task WritePosterQuietlyAsync(
        StudioProject project,
        StudioEvents events,
        StudioProjectPaths paths,
        CancellationToken cancellationToken)
    {
        try
        {
            await _exporter.WritePosterAsync(project, events, paths, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Studio poster could not be written for {project.Id}: {ex.Message}");
        }
    }

    /// <summary>Brings export progress, reported on the exporter's thread, to the session's thread.</summary>
    private sealed class ExportProgressRelay(StudioEditorSession session, int generation) : IProgress<double>
    {
        private double _latest;
        private int _isPosted;

        public void Report(double value)
        {
            Volatile.Write(ref _latest, value);
            if (Interlocked.Exchange(ref _isPosted, 1) != 0)
            {
                return;
            }

            session._post(() =>
            {
                Volatile.Write(ref _isPosted, 0);
                session.ApplyExportProgress(generation, Volatile.Read(ref _latest));
            });
        }
    }

    // Closing

    /// <summary>What to ask before the window closes. The window shows the question.</summary>
    public StudioClosePrompt GetClosePrompt()
    {
        if (IsExporting)
        {
            return StudioClosePrompt.ExportRunning;
        }

        return IsReady && HasNeverExported ? StudioClosePrompt.NeverExported : StudioClosePrompt.None;
    }

    /// <summary>
    /// Ends the session, when its window is closing for good. A running export is stopped, the
    /// edits are saved, the preview is disposed, and then the poster image is written. The save has
    /// happened by the time this returns; the task finishes when the rest has. Calling it again
    /// returns the same task.
    /// </summary>
    /// <param name="deleteProject">
    /// True to delete the project instead of saving it. The preview is disposed first, and waited
    /// for, because it holds the media files open.
    /// </param>
    public Task CloseAsync(bool deleteProject = false) => _closeTask ??= CloseCoreAsync(deleteProject);

    private async Task CloseCoreAsync(bool deleteProject)
    {
        var wasReady = IsReady;
        _isClosed = true;
        _lifetime.Cancel();
        _exportCancellation?.Cancel();
        var exportTask = _exportTask;
        var preview = DetachPreview();

        if (deleteProject)
        {
            CancelScheduledSave();
        }
        else
        {
            SaveNow();
            MarkLastUsed();
        }

        var project = Model?.Project;
        var paths = _paths;
        var events = _events;

        if (preview is not null)
        {
            await DisposeQuietlyAsync(preview).ConfigureAwait(false);
        }

        if (deleteProject)
        {
            // An export reads the same files, so it has to have let go of them as well.
            await WaitQuietlyAsync(exportTask).ConfigureAwait(false);
            await DeleteProjectAsync().ConfigureAwait(false);
            return;
        }

        if (wasReady && project is not null && paths is not null)
        {
            await WritePosterQuietlyAsync(project, events, paths, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private IStudioPreview? DetachPreview()
    {
        if (_preview is not { } preview)
        {
            return null;
        }

        _preview = null;
        preview.PositionChanged -= _positionHandler;
        preview.IsPlayingChanged -= _isPlayingHandler;
        preview.Failed -= _failedHandler;
        preview.Pause();
        IsPlaying = false;
        return preview;
    }

    /// <summary>
    /// Writes down that the project was in use until now. Cleanup removes the recordings of an
    /// exported project some days after it was last open, and counts from this time. Left at the
    /// time the editor opened, a project whose editor stayed open for longer than that would be
    /// removed the moment the editor closed, by the cleanup that follows every close. A project
    /// that was never read has nothing to write down, and a failure here is not the user's
    /// concern: the edits are saved separately, and that is reported.
    /// </summary>
    private void MarkLastUsed()
    {
        if (Model is not { } model)
        {
            return;
        }

        try
        {
            model.RefreshBookkeeping(_store.MarkOpened(ProjectId));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Studio could not note when {ProjectId} was last in use: {ex.Message}");
        }
    }

    private async Task DeleteProjectAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _store.Delete(ProjectId);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= DeleteAttempts)
                {
                    var message = $"The project could not be deleted: {ex.Message}";
                    _post(() => ReportError(StudioEditorErrorKind.Delete, message));
                    return;
                }
            }

            // A decoder can keep a file open for a moment after it was told to let go.
            await Task.Delay(DeleteRetryDelay, _timeProvider).ConfigureAwait(false);
        }
    }

    private static async Task DisposeQuietlyAsync(IStudioPreview preview)
    {
        try
        {
            await preview.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Studio preview could not be disposed: {ex.Message}");
        }
    }

    private static async Task WaitQuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Studio export ended with an error while closing: {ex.Message}");
        }
    }

    // Helpers

    /// <summary>Runs <paramref name="action"/> on the session's thread and finishes when it has run.</summary>
    private Task PostAsync(Action action)
    {
        var completion = new TaskCompletionSource();
        _post(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    private void RaiseChanged(StudioEditorChanges changes)
    {
        // Whatever moved the playhead or changed the scenes, a different current scene is said once.
        var sceneIndex = CurrentSceneIndex;
        if (sceneIndex != _raisedSceneIndex)
        {
            _raisedSceneIndex = sceneIndex;
            changes |= StudioEditorChanges.Scene;
        }

        Changed?.Invoke(this, new StudioEditorChangedEventArgs(changes));
    }

    private void ReportError(StudioEditorErrorKind kind, string message) =>
        ErrorReported?.Invoke(this, new StudioEditorErrorEventArgs(kind, message));
}
