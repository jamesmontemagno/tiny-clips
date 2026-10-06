using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TinyClips.App.ViewModels.Studio;
using TinyClips.App.Views.Studio;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Core.Studio.Preview;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.App.Services.Studio;

/// <summary>
/// Opens Studio editor windows, one per project, and looks after what follows from one closing:
/// the project stops counting as open, and storage cleanup gets a turn. Used on the UI thread.
/// </summary>
public sealed class StudioWindowService
{
    // Exit does not wait for a poster image. The edits are on disk before the wait starts.
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(3);

    private readonly IStudioProjectStore _store;
    private readonly IStudioPreviewFactory _previews;
    private readonly IStudioExportService _exporter;
    private readonly IStudioPreviewViewFactory _previewViews;
    private readonly ICaptureSettings _settings;
    private readonly IClipStorageService _storage;
    private readonly StudioProjectTracker _tracker;
    private readonly StudioProjectCleanupService _cleanup;
    private readonly Dictionary<string, StudioWindow> _windows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deletingProjectIds = new(StringComparer.Ordinal);
    private readonly List<Task> _teardowns = [];
    private bool _isExiting;

    public StudioWindowService(
        IStudioProjectStore store,
        IStudioPreviewFactory previews,
        IStudioExportService exporter,
        IStudioPreviewViewFactory previewViews,
        ICaptureSettings settings,
        IClipStorageService storage,
        StudioProjectTracker tracker,
        StudioProjectCleanupService cleanup)
    {
        _store = store;
        _previews = previews;
        _exporter = exporter;
        _previewViews = previewViews;
        _settings = settings;
        _storage = storage;
        _tracker = tracker;
        _cleanup = cleanup;
    }

    /// <summary>Raised once an export has finished, with the path of the video that was written.</summary>
    public event EventHandler<StudioExportedEventArgs>? Exported;

    /// <summary>
    /// Raised once the screen recording of a project that could not be shown has been saved as a
    /// video of its own, with the path of that video.
    /// </summary>
    public event EventHandler<StudioExportedEventArgs>? ScreenRecordingSaved;

    /// <summary>
    /// Raised with a sentence for the user when something failed and the window that would have
    /// shown it is gone, for example a project that could not be deleted, or a screen recording
    /// that was still being saved when its window closed and could not be.
    /// </summary>
    public event EventHandler<StudioEditorErrorEventArgs>? ErrorReported;

    /// <summary>
    /// How a window is shown and brought to the front. The app replaces this with the way it
    /// activates its other windows.
    /// </summary>
    public Action<Window> ActivateWindow { get; set; } = static window => window.Activate();

    /// <summary>
    /// Whether people can be found in a camera picture, which is what blurring or removing the
    /// camera's background takes. It is asked once for each editor, when its window is opened,
    /// and the editor offers the Camera background choice of its Camera panel only when the answer
    /// is yes. The app's answer is whether the model that finds people is next to it.
    /// </summary>
    public Func<bool> CanFindPeople { get; set; } = static () => StudioPersonFinders.IsAvailable;

    /// <summary>
    /// Opens the editor for a project, or brings the window that already has it open to the front.
    /// There is one window per project.
    /// </summary>
    /// <returns>
    /// The window, or null when none was opened: the app is exiting, or the project is being
    /// deleted.
    /// </returns>
    public Window? Open(string projectId)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectId);
        if (_isExiting || _deletingProjectIds.Contains(projectId))
        {
            return null;
        }

        if (!_windows.TryGetValue(projectId, out var window))
        {
            var viewModel = new StudioViewModel(
                projectId,
                _store,
                _previews,
                _exporter,
                _settings,
                _storage,
                DispatcherQueue.GetForCurrentThread(),
                CanFindPeople());
            viewModel.Exported += OnExported;
            viewModel.ScreenRecordingSaved += OnScreenRecordingSaved;
            viewModel.ErrorReported += OnErrorReported;
            window = new StudioWindow(viewModel, _previewViews, _settings, OnWindowClosed);
            _windows[projectId] = window;
            _tracker.MarkOpened(projectId);
        }

        ActivateWindow(window);
        return window;
    }

    /// <summary>
    /// Closes every editor without asking anything, for when the app is exiting. Edits are saved
    /// and running exports are stopped before this returns to its caller for the first time; the
    /// task then waits a short while for the windows to let go of their files.
    /// </summary>
    public async Task CloseAllForExitAsync()
    {
        _isExiting = true;
        foreach (var window in _windows.Values.ToArray())
        {
            _ = window.CloseForExit();
        }

        var teardowns = _teardowns.Where(static task => !task.IsCompleted).ToArray();
        if (teardowns.Length > 0)
        {
            await Task.WhenAny(Task.WhenAll(teardowns), Task.Delay(ExitWait));
        }
    }

    private void OnExported(object? sender, StudioExportedEventArgs e) => Exported?.Invoke(this, e);

    private void OnScreenRecordingSaved(object? sender, StudioExportedEventArgs e) => ScreenRecordingSaved?.Invoke(this, e);

    private void OnErrorReported(object? sender, StudioEditorErrorEventArgs e)
    {
        // A window that is still open shows the message itself. One that is closing does not:
        // it saves its edits as its last act, while it is still in the list here, and a save
        // that fails then would otherwise be told to nobody. Neither does one that has closed,
        // and is in the list no more: a project that could not be deleted, or a screen
        // recording whose copy failed after its window had gone.
        if (sender is StudioViewModel viewModel
            && _windows.TryGetValue(viewModel.ProjectId, out var window)
            && ReferenceEquals(window.ViewModel, viewModel)
            && !window.IsClosing)
        {
            return;
        }

        ErrorReported?.Invoke(this, e);
    }

    private void OnWindowClosed(StudioWindow window, Task teardown)
    {
        var projectId = window.ViewModel.ProjectId;
        if (_windows.TryGetValue(projectId, out var current) && ReferenceEquals(current, window))
        {
            _windows.Remove(projectId);
        }

        if (window.IsDeletingProject)
        {
            // Opening it again would bring back a project that is on its way out.
            _deletingProjectIds.Add(projectId);
        }

        _teardowns.RemoveAll(static task => task.IsCompleted);
        _teardowns.Add(teardown);
        _ = FinishClosingAsync(window, teardown);
    }

    /// <summary>
    /// Waits until the closed window has let go of the project's files, and only then says that
    /// the project is no longer open. Cleanup skips open projects, so it cannot take a folder away
    /// from under an editor that is still closing.
    /// </summary>
    private async Task FinishClosingAsync(StudioWindow window, Task teardown)
    {
        var projectId = window.ViewModel.ProjectId;
        try
        {
            await teardown;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Studio window for {projectId} did not close cleanly: {ex}");
        }

        // Only now, when the editor has closed and has nothing more to report. Until then it
        // can have: a project that could not be deleted, and a screen recording that was still
        // being saved when its window closed, which is then saved or could not be.
        window.ViewModel.Exported -= OnExported;
        window.ViewModel.ScreenRecordingSaved -= OnScreenRecordingSaved;
        window.ViewModel.ErrorReported -= OnErrorReported;
        _deletingProjectIds.Remove(projectId);

        // Unless it was opened again in the meantime.
        if (!_windows.ContainsKey(projectId))
        {
            _tracker.MarkClosed(projectId);
        }

        if (!_isExiting)
        {
            // Runs on a background thread and never throws.
            _ = _cleanup.RunAsync();
        }
    }
}
