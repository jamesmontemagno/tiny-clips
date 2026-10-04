using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Core.Studio.Preview;

namespace TinyClips.App.ViewModels.Studio;

/// <summary>
/// Binds one Studio editor window to its <see cref="StudioEditorSession"/>. The session decides
/// what happens; this class turns its state into bindable values and brings its events to the
/// window. Used on the UI thread only.
/// </summary>
/// <remarks>
/// Every value is read from the session when it is asked for, so there is no second copy to keep
/// in step. A change to the project refreshes every binding; the playhead and export progress,
/// which change many times a second, refresh only what depends on them.
/// </remarks>
public sealed partial class StudioViewModel : ObservableObject
{
    private const string PlayGlyph = "\uE768";
    private const string PauseGlyph = "\uE769";

    private static readonly string[] PlaybackPropertyNames =
    [
        nameof(Playhead),
        nameof(IsPlaying),
        nameof(PlayPauseGlyph),
        nameof(PlayPauseLabel),
        nameof(TimeText),
        nameof(TimeAccessibleName),
        nameof(PlayheadText),
    ];

    private static readonly string[] ExportPropertyNames =
    [
        nameof(ExportProgressPercent),
        nameof(ExportPercentText),
    ];

    private readonly StudioEditorSession _session;
    private readonly ICaptureSettings _settings;
    private readonly IClipStorageService _storage;
    private readonly DispatcherQueue _dispatcher;
    private StudioEditorLoadState _lastState = StudioEditorLoadState.Loading;
    private string _errorMessage = string.Empty;
    private bool _isSaveErrorShown;
    private string _defaultLookStatus = string.Empty;

    public StudioViewModel(
        string projectId,
        IStudioProjectStore store,
        IStudioPreviewFactory previewFactory,
        IStudioExportService exporter,
        ICaptureSettings settings,
        IClipStorageService storage,
        DispatcherQueue dispatcher)
    {
        _settings = settings;
        _storage = storage;
        _dispatcher = dispatcher;
        _session = new StudioEditorSession(
            projectId,
            store,
            previewFactory,
            exporter,
            settings,
            action => dispatcher.TryEnqueue(() => action()));
        _session.Changed += OnSessionChanged;
        _session.Exported += OnSessionExported;
        _session.ErrorReported += OnSessionErrorReported;
    }

    /// <summary>Raised when the editor goes from loading to ready or unavailable, or stops being ready.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised with a sentence a screen reader should say.</summary>
    public event EventHandler<StudioAnnouncementEventArgs>? Announced;

    /// <summary>Raised once an export has finished, with the path of the video.</summary>
    public event EventHandler<StudioExportedEventArgs>? Exported;

    /// <summary>Raised with a sentence for the user when saving, exporting or deleting failed.</summary>
    public event EventHandler<StudioEditorErrorEventArgs>? ErrorReported;

    public string ProjectId => _session.ProjectId;

    // State

    public bool IsLoading => _session.State == StudioEditorLoadState.Loading;

    public bool IsReady => _session.IsReady;

    public bool IsUnavailable => _session.State == StudioEditorLoadState.Unavailable;

    public string UnavailableHeading => "This project can't be opened";

    public string UnavailableMessage => _session.UnavailableMessage;

    /// <summary>The heading and the reason as one sentence pair, for a screen reader.</summary>
    public string UnavailableDescription =>
        IsUnavailable ? $"{UnavailableHeading}. {UnavailableMessage}" : string.Empty;

    /// <summary>False while loading, while exporting and after closing. The whole editor follows it.</summary>
    public bool IsEditable => _session.IsEditable;

    public bool IsExporting => _session.IsExporting;

    public double ExportProgressPercent => _session.ExportProgress * 100;

    public string ExportPercentText => StudioEditorText.GetPercentText(_session.ExportProgress);

    /// <summary>The last thing that went wrong, shown until it is dismissed. Empty when nothing did.</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError
    {
        get => _errorMessage.Length > 0;
        set
        {
            // The message bar's close button turns this off.
            if (!value)
            {
                ErrorMessage = string.Empty;
            }
        }
    }

    // Header

    public string ClipName => _session.ClipName;

    public string ExportSizeText => _session.ExportSizeText;

    public bool CanUndo => _session.CanUndo;

    public bool CanRedo => _session.CanRedo;

    public bool CanExport => _session.CanExport;

    public int CanvasAspectIndex
    {
        get => (int)(Project?.Canvas.Aspect ?? StudioCanvasAspect.Auto);
        set
        {
            if (value is >= 0 and <= (int)StudioCanvasAspect.Portrait9X16)
            {
                _session.SetCanvasAspect((StudioCanvasAspect)value);
            }

            ResyncIfDifferent(value, CanvasAspectIndex);
        }
    }

    // Preview

    /// <summary>The live preview, once the project is open.</summary>
    public IStudioPreview? Preview => _session.Preview;

    /// <summary>The size of the exported video, which is the canvas everything is laid out on.</summary>
    public double CanvasWidth => _session.ExportSize.Width;

    public double CanvasHeight => _session.ExportSize.Height;

    /// <summary>Canvas width over height. 16:9 until a project is open.</summary>
    public double CanvasAspectRatio =>
        CanvasWidth > 0 && CanvasHeight > 0 ? CanvasWidth / CanvasHeight : 16.0 / 9;

    public string PreviewDescription =>
        _session.Model is { } model && IsReady ? StudioEditorText.GetPreviewDescription(model) : string.Empty;

    /// <summary>The camera bubble in canvas pixels, or null when the layout has none.</summary>
    public StudioFrameRect? BubbleRect =>
        IsReady ? _session.GetBubbleRect(CanvasWidth, CanvasHeight) : null;

    // Transport

    /// <summary>The playhead in source time, as the trim bar shows it.</summary>
    public double Playhead => _session.Playhead;

    public bool IsPlaying => _session.IsPlaying;

    public string PlayPauseGlyph => IsPlaying ? PauseGlyph : PlayGlyph;

    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    public string TimeText => _session.TimeText;

    public string TimeAccessibleName => $"Time {TimeText}";

    public double SourceDuration => _session.Model?.SourceDuration ?? 0;

    public double FrameDuration => _session.Model?.FrameDuration ?? 1.0 / 30;

    public double TrimStart => _session.Model?.TrimStart ?? 0;

    public double TrimEnd => _session.Model?.TrimEnd ?? 0;

    /// <summary>How far one key press or screen reader step moves a trim handle.</summary>
    public double TrimStep => StudioEditorText.GetTrimStep(FrameDuration, SourceDuration);

    public string TrimStartText => StudioEditorModel.GetSecondsText(TrimStart);

    public string TrimEndText => StudioEditorModel.GetSecondsText(TrimEnd);

    public string PlayheadText => _session.Model?.GetPlayheadText(Playhead) ?? string.Empty;

    private StudioProject? Project => _session.Project;

    private StudioScene Scene => _session.Model?.CurrentScene ?? new StudioScene();

    // Loading

    /// <summary>Opens the project. Finishes when the editor is ready or unavailable, and never fails.</summary>
    public Task LoadAsync() => _session.LoadAsync();

    // Commands

    public void BeginGesture() => _session.BeginGesture();

    public void EndGesture() => _session.EndGesture();

    public void Undo() => _session.Undo();

    public void Redo() => _session.Redo();

    public void TogglePlayback() => _session.TogglePlayback();

    public void Pause() => _session.Pause();

    public void StepPreviousFrame() => _session.StepFrames(-1);

    public void StepNextFrame() => _session.StepFrames(1);

    public void Scrub(double sourceTime) => _session.Scrub(sourceTime);

    public void SetTrimStart(double sourceTime) => _session.SetTrimStart(sourceTime);

    public void SetTrimEnd(double sourceTime) => _session.SetTrimEnd(sourceTime);

    public void SetTrimStartAtPlayhead() => _session.SetTrimStartAtPlayhead();

    public void SetTrimEndAtPlayhead() => _session.SetTrimEndAtPlayhead();

    /// <summary>Moves the camera bubble so its top-left corner is at a point in canvas pixels.</summary>
    public void MoveBubbleTopLeft(double x, double y) =>
        _session.MoveBubbleTopLeft(x, y, CanvasWidth, CanvasHeight);

    /// <summary>Runs what a key press means. A layout chosen this way is read out.</summary>
    public void Run(StudioShortcutAction action)
    {
        switch (action)
        {
            case StudioShortcutAction.TogglePlayback:
                TogglePlayback();
                break;
            case StudioShortcutAction.PreviousFrame:
                StepPreviousFrame();
                break;
            case StudioShortcutAction.NextFrame:
                StepNextFrame();
                break;
            case StudioShortcutAction.SetTrimStartAtPlayhead:
                SetTrimStartAtPlayhead();
                break;
            case StudioShortcutAction.SetTrimEndAtPlayhead:
                SetTrimEndAtPlayhead();
                break;
            case StudioShortcutAction.ShowScreenLayout:
                SetLayoutFromKey(StudioLayout.Screen);
                break;
            case StudioShortcutAction.ShowBubbleLayout:
                SetLayoutFromKey(StudioLayout.Bubble);
                break;
            case StudioShortcutAction.ShowSideBySideLayout:
                SetLayoutFromKey(StudioLayout.SideBySide);
                break;
            case StudioShortcutAction.ShowCameraLayout:
                SetLayoutFromKey(StudioLayout.Camera);
                break;
            case StudioShortcutAction.Undo:
                Undo();
                break;
            case StudioShortcutAction.Redo:
                Redo();
                break;
            case StudioShortcutAction.Export:
                _ = ExportAsync();
                break;
            case StudioShortcutAction.CancelExport:
                CancelExport();
                break;
        }
    }

    // Export

    /// <summary>
    /// Renders the video into the folder, and with the file name, the app gives any saved video,
    /// using the codec chosen in settings.
    /// </summary>
    public async Task<StudioExportOutcome> ExportAsync()
    {
        var wasExporting = _session.IsExporting;
        var export = _session.ExportAsync(() => _storage.GenerateFilePath(CaptureType.Video), _settings.VideoCodec);
        if (!wasExporting && _session.IsExporting)
        {
            Announce("Export started.", "StudioExportStarted", StudioAnnouncementKind.Information);
        }

        // Finishing is announced when the session reports the video, and a failure by the message
        // bar that shows it.
        var outcome = await export;
        if (outcome == StudioExportOutcome.Cancelled && !_session.IsClosed)
        {
            Announce("Export cancelled.", "StudioExportCancelled", StudioAnnouncementKind.Stopped);
        }

        return outcome;
    }

    public void CancelExport() => _session.CancelExport();

    // Closing

    /// <summary>What to ask before the window closes.</summary>
    public StudioClosePrompt GetClosePrompt() => _session.GetClosePrompt();

    /// <summary>
    /// Ends the editor when its window is closing for good. The edits are saved by the time this
    /// returns; the task finishes when the preview has let go of its files and, for
    /// <paramref name="deleteProject"/>, the project is gone.
    /// </summary>
    public Task CloseAsync(bool deleteProject) => _session.CloseAsync(deleteProject);

    // Session events

    private void OnSessionChanged(object? sender, StudioEditorChangedEventArgs e)
    {
        if (e.Includes(StudioEditorChanges.State) || e.Includes(StudioEditorChanges.Project))
        {
            ClearDefaultLookStatusIfEdited();
            ClearSaveErrorIfSaved();
            OnPropertyChanged(string.Empty);
        }
        else
        {
            if (e.Includes(StudioEditorChanges.Playback))
            {
                Raise(PlaybackPropertyNames);
            }

            if (e.Includes(StudioEditorChanges.Export))
            {
                Raise(ExportPropertyNames);
            }
        }

        if (_session.State != _lastState)
        {
            _lastState = _session.State;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSessionExported(object? sender, StudioExportedEventArgs e)
    {
        Announce("Export finished.", "StudioExportFinished", StudioAnnouncementKind.Completed);
        Exported?.Invoke(this, e);
    }

    private void OnSessionErrorReported(object? sender, StudioEditorErrorEventArgs e)
    {
        ShowError(e.Message);
        _isSaveErrorShown = e.Kind == StudioEditorErrorKind.Save;
        ErrorReported?.Invoke(this, e);
    }

    /// <summary>Shows a sentence in the window's message bar, which also reads it out.</summary>
    public void ShowError(string message)
    {
        _isSaveErrorShown = false;
        ErrorMessage = message;
    }

    /// <summary>
    /// A message that the project could not be saved stops being true once a later save has
    /// worked, so it is taken away then. Other messages stay until they are dismissed.
    /// </summary>
    private void ClearSaveErrorIfSaved()
    {
        if (_isSaveErrorShown && !_session.HasUnsavedEdits)
        {
            _isSaveErrorShown = false;
            _errorMessage = string.Empty;
        }
    }

    // Helpers

    private void Announce(string message, string activityId, StudioAnnouncementKind kind) =>
        Announced?.Invoke(this, new StudioAnnouncementEventArgs(message, activityId, kind));

    private void Raise(string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>
    /// A control keeps the value it was given even when the editor did not take it, for example an
    /// index a list passes through while it is being built. This puts the real value back, a moment
    /// later so the control is not changed again from inside its own change notice.
    /// </summary>
    private void ResyncIfDifferent<T>(T requested, T actual, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(requested, actual) && propertyName is not null)
        {
            _dispatcher.TryEnqueue(() => OnPropertyChanged(propertyName));
        }
    }
}
