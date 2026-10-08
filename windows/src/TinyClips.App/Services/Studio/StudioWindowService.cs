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
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace TinyClips.App.Services.Studio;

/// <summary>What came of opening a project from its <c>.tinyclips</c> file.</summary>
/// <param name="Window">The editor on the copy that was made, when one was opened.</param>
/// <param name="Failure">
/// Why none was, as a sentence for the user. Null where there is nothing to say: the editor
/// is open, the app is exiting, or the same file was being opened already.
/// </param>
public sealed record StudioProjectOpenResult(Window? Window, string? Failure);

/// <summary>
/// What an editor window asks of the app for the project as a whole: another project opened,
/// the system's pickers, and a folder shown in Explorer. <see cref="StudioWindowService"/> is
/// the one that answers.
/// </summary>
public interface IStudioProjectHost
{
    /// <summary>Opens the editor for a project in the store, or brings its window to the front.</summary>
    Window? Open(string projectId);

    /// <summary>Asks for a <c>.tinyclips</c> file. Null when none was chosen.</summary>
    Task<string?> ChooseProjectFileAsync(Window owner);

    /// <summary>Asks for the folder a project is to be saved in. Null when none was chosen.</summary>
    Task<string?> ChooseSaveFolderAsync(Window owner);

    /// <summary>Copies a saved project into the store as a new draft and opens the editor on the copy.</summary>
    Task<StudioProjectOpenResult> OpenProjectFileAsync(string path);

    /// <summary>Shows a file in Explorer, selected in its folder.</summary>
    void Reveal(string path);

    /// <summary>Says a sentence to the user where no window is left to show it.</summary>
    void Tell(string message);
}

/// <summary>
/// Opens Studio editor windows, one per project, and looks after what follows from one closing:
/// the project stops counting as open, and storage cleanup gets a turn. It also opens a project
/// that was saved as a folder. Used on the UI thread.
/// </summary>
public sealed class StudioWindowService : IStudioProjectHost
{
    // A copy that is still running after this long is said to be running: nothing else shows
    // that a project file was opened until its editor is there.
    private static readonly TimeSpan OpeningNoticeDelay = TimeSpan.FromSeconds(2);

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

    // The project files that are being copied into the store right now, by their full path.
    private readonly HashSet<string> _openingProjectFiles = new(StringComparer.OrdinalIgnoreCase);
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

        // An editor that opened or closed changes what the others list under Open recent.
        _tracker.Changed += OnOpenProjectsChanged;
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
    /// How a <c>.tinyclips</c> file is asked for: the system's open picker, owned by the window
    /// that asks. Returns the file's path, or null when none was chosen.
    /// </summary>
    public Func<Window, Task<string?>> ChooseProjectFile { get; set; } = PickProjectFileAsync;

    /// <summary>
    /// How the folder a project is saved in is asked for: the system's folder picker, owned by
    /// the window that asks. Returns the folder's path, or null when none was chosen.
    /// </summary>
    public Func<Window, Task<string?>> ChooseSaveFolder { get; set; } = PickFolderAsync;

    /// <summary>How a file is shown in its folder: an Explorer window with the file selected.</summary>
    public Action<string> RevealFile { get; set; } = RevealInExplorer;

    /// <summary>
    /// How a sentence is said to the user where no window shows it: that a project is being
    /// copied, or that it could not be opened when the editor that asked has closed. The app
    /// replaces this with a notification.
    /// </summary>
    public Action<string> Notify { get; set; } = static message => Debug.WriteLine(message);

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
            viewModel.StateChanged += OnEditorStateChanged;
            window = new StudioWindow(viewModel, _previewViews, _settings, this, OnWindowClosed);
            _windows[projectId] = window;
            _tracker.MarkOpened(projectId);
        }

        ActivateWindow(window);
        return window;
    }

    /// <summary>
    /// Opens a project that was saved as a folder, from its <c>.tinyclips</c> file or from the
    /// folder that holds one: the project is copied into the store as a new draft, and the
    /// editor opens on the copy. What was chosen is only read. With Studio switched off
    /// nothing is copied, and the result says so. Never fails.
    /// </summary>
    /// <remarks>
    /// The copy runs off the UI thread and takes as long as the recordings are large. Nothing
    /// of the app shows meanwhile, as on the Mac, so a copy that is still running after two
    /// seconds is said to be running (<see cref="Notify"/>), and the same file asked for again
    /// in that time is not copied twice.
    /// </remarks>
    public async Task<StudioProjectOpenResult> OpenProjectFileAsync(string path)
    {
        if (_isExiting)
        {
            return new StudioProjectOpenResult(null, null);
        }

        if (!_settings.StudioPreviewEnabled)
        {
            return new StudioProjectOpenResult(null, StudioProjectFolderText.StudioIsOffMessage);
        }

        string key;
        try
        {
            key = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            key = path;
        }

        if (!_openingProjectFiles.Add(key))
        {
            return new StudioProjectOpenResult(null, null);
        }

        try
        {
            var copy = Task.Run(() => _store.OpenProjectFolder(path));
            if (await Task.WhenAny(copy, Task.Delay(OpeningNoticeDelay)) != copy)
            {
                Notify(StudioProjectFolderText.GetOpeningMessage(key));
            }

            var project = await copy;

            // An app that is exiting opens nothing more. The copy stays in the store as a draft.
            return new StudioProjectOpenResult(Open(project.Id), null);
        }
        catch (Exception ex)
        {
            return new StudioProjectOpenResult(null, StudioProjectFolderText.GetOpenFailure(ex));
        }
        finally
        {
            _openingProjectFiles.Remove(key);
        }
    }

    Task<string?> IStudioProjectHost.ChooseProjectFileAsync(Window owner) => ChooseProjectFile(owner);

    Task<string?> IStudioProjectHost.ChooseSaveFolderAsync(Window owner) => ChooseSaveFolder(owner);

    void IStudioProjectHost.Reveal(string path) => RevealFile(path);

    void IStudioProjectHost.Tell(string message) => Notify(message);

    private static async Task<string?> PickProjectFileAsync(Window owner)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(StudioProjectFolder.ProjectFileExtension);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private static async Task<string?> PickFolderAsync(Window owner)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Studio could not show {path} in Explorer: {ex.Message}");
        }
    }

    // An editor has read its project, which made that project the one opened last. The lists
    // that were read when its window opened, a moment before, still have it where it was.
    private void OnEditorStateChanged(object? sender, EventArgs e)
    {
        if (sender is StudioViewModel { IsLoading: false } viewModel)
        {
            _tracker.MarkRead(viewModel.ProjectId);
        }
    }

    private void OnOpenProjectsChanged(object? sender, EventArgs e)
    {
        foreach (var window in _windows.Values)
        {
            _ = window.ViewModel.RefreshRecentProjectsAsync();
        }
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
        window.ViewModel.StateChanged -= OnEditorStateChanged;
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
