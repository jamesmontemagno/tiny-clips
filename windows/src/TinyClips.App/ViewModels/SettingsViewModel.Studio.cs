using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using TinyClips.App.Services.Studio;
using TinyClips.App.Settings;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;

namespace TinyClips.App;

/// <summary>
/// The part of the Settings view model that is about Tiny Clips Studio: its switch, the After
/// recording choice, the storage rules, and the recordings that only a Studio project holds.
/// </summary>
/// <remarks>
/// It is in a file of its own because the unit tests of <c>SettingsViewModel.cs</c> compile that
/// file by itself, without the app's services. This file is not in that build. The other file
/// reaches it through two partial methods, which are not there when this file is not.
/// </remarks>
public sealed partial class SettingsViewModel
{
    private sealed record StudioServices(
        IStudioProjectStore Projects,
        StudioProjectCleanupService Cleanup,
        StudioProjectTracker Tracker);

    // Set by the constructor below, which is the one the app uses. The constructors in the other
    // file know nothing of Studio.
    private readonly StudioServices? _studio;
    private Task? _studioStorageInitialization;
    private bool _studioCleanupRunning;
    private int _studioRefreshGeneration;
    private readonly HashSet<string> _studioRecordingsBeingSaved = new(StringComparer.Ordinal);

    public SettingsViewModel(
        ICaptureSettings settings,
        IHotKeyService hotKeys,
        ILaunchAtLoginService launchAtLogin,
        IAudioDeviceService audioDevices,
        IWebcamDeviceEnumerator webcamDevices,
        IClipStorageService storage,
        IClipAnalyticsService analytics,
        IUploadcareCredentialStore uploadcareCredentials,
        IStudioProjectStore studioProjects,
        StudioProjectCleanupService studioCleanup,
        StudioProjectTracker studioTracker)
        : this(settings, hotKeys, launchAtLogin, audioDevices, webcamDevices, storage, analytics, uploadcareCredentials)
    {
        _studio = new StudioServices(studioProjects, studioCleanup, studioTracker);
        studioTracker.Changed += OnStudioOpenProjectsChanged;
        studioCleanup.CleanupCompleted += OnStudioCleanupCompleted;
    }

    private StudioServices Studio => _studio
        ?? throw new InvalidOperationException("This Settings view model was made without the Studio services.");

    // Its switch is always shown. Everything else bound to these stays hidden unless the switch
    // is on.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StudioPreviewVisibility))]
    [NotifyPropertyChangedFor(nameof(ShowTrimmerToggleVisibility))]
    [NotifyPropertyChangedFor(nameof(StudioDraftsVisibility))]
    [NotifyPropertyChangedFor(nameof(StudioKeptNoteVisibility))]
    private bool _isStudioPreviewEnabled;

    public Microsoft.UI.Xaml.Visibility StudioPreviewVisibility => IsStudioPreviewEnabled
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>
    /// What is still on disk while Studio is switched off, such as "3 Studio projects are kept and
    /// use 1.2 GB." Empty when there is nothing. Shown under the switch, only while it is off.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StudioKeptNoteVisibility))]
    private string _studioKeptNote = string.Empty;

    public Microsoft.UI.Xaml.Visibility StudioKeptNoteVisibility => !IsStudioPreviewEnabled && StudioKeptNote.Length > 0
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>The trimmer toggle shows until the After recording choice takes its place.</summary>
    public Microsoft.UI.Xaml.Visibility ShowTrimmerToggleVisibility => IsStudioPreviewEnabled
        ? Microsoft.UI.Xaml.Visibility.Collapsed
        : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>0 = Save, 1 = Open trimmer, 2 = Open in Studio.</summary>
    [ObservableProperty]
    private int _videoAfterRecordingIndex = 1;

    [ObservableProperty]
    private double _studioSourceRetentionDays = CaptureSettings.DefaultStudioSourceRetentionDays;

    [ObservableProperty]
    private double _studioStorageCapGigabytes = CaptureSettings.DefaultStudioStorageCapGigabytes;

    /// <summary>Project count and total size, such as "3 projects, 1.2 GB".</summary>
    [ObservableProperty]
    private string _studioStorageDisplay = "Calculating\u2026";

    /// <summary>What the last Clean up now did. Empty until one has run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StudioCleanupStatusVisibility))]
    private string _studioCleanupStatus = string.Empty;

    public Microsoft.UI.Xaml.Visibility StudioCleanupStatusVisibility => string.IsNullOrEmpty(StudioCleanupStatus)
        ? Microsoft.UI.Xaml.Visibility.Collapsed
        : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>
    /// The recordings that only their Studio project holds, newest first: the projects that were
    /// never exported, the ones whose exported video is gone, and after them the ones Studio
    /// cannot read. None of them has a video in the Clips Library: this list is the way back to
    /// them.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<StudioDraftItem> StudioDrafts { get; } = new();

    public Microsoft.UI.Xaml.Visibility StudioDraftsVisibility => IsStudioPreviewEnabled && StudioDrafts.Count > 0
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>
    /// Reads the Studio project count and size the first time they are shown. Idempotent, like the
    /// analytics and media device loads. With Studio switched off it reads them all the same, for
    /// the line under the switch that says what is still kept; where Studio was never used there
    /// is no folder to read, and none is made.
    /// </summary>
    public Task EnsureStudioStorageInitializedAsync() => _studioStorageInitialization ??= RefreshStudioStorageAsync();

    private async Task RefreshStudioStorageAsync()
    {
        // Several things ask for a refresh at once: a window closing, then the cleanup it starts.
        // Only the answer to the newest request is shown.
        var generation = ++_studioRefreshGeneration;
        try
        {
            var projects = Studio.Projects;

            // Sizing every project folder reads the disk, so it stays off the UI thread.
            var listed = await Task.Run(() =>
            {
                var summaries = projects.ListSummaries();
                var unreadable = projects.ListUnreadableProjects();

                // Whether each row can offer its screen recording to be saved.
                var withRecording = summaries
                    .Where(IsShownAsDraft)
                    .Where(summary => projects.FindScreenRecording(summary.Id) is not null)
                    .Select(summary => summary.Id)
                    .ToHashSet(StringComparer.Ordinal);
                return (Summaries: summaries, Unreadable: unreadable, WithRecording: withRecording);
            });
            if (!_closed && generation == _studioRefreshGeneration)
            {
                var storage = new StudioStorageSummary(
                    listed.Summaries.Count + listed.Unreadable.Count,
                    listed.Summaries.Sum(summary => summary.SizeBytes) + listed.Unreadable.Sum(project => project.SizeBytes));
                StudioStorageDisplay = FormatStudioStorage(storage);
                StudioKeptNote = FormatStudioKeptNote(storage);
                ShowStudioDrafts(listed.Summaries, listed.Unreadable, listed.WithRecording);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to read Studio project storage: {ex}");
            if (!_closed && generation == _studioRefreshGeneration)
            {
                StudioStorageDisplay = "Couldn't read Studio project storage.";
                StudioKeptNote = string.Empty;
            }
        }
    }

    // A recording that only its project holds: never exported, or exported to a video that is gone.
    private static bool IsShownAsDraft(StudioProjectSummary summary) =>
        (summary.IsDraft || summary.ExportMissing) && !summary.IsFlat;

    /// <summary>
    /// Shows the recordings that only their project holds, newest first, and after them the
    /// projects that cannot be read. Rows that are still there keep their place, and only what
    /// changes by itself is brought up to date, so a button in the list does not lose keyboard
    /// focus because another row changed.
    /// </summary>
    private void ShowStudioDrafts(
        IReadOnlyList<StudioProjectSummary> summaries,
        IReadOnlyList<StudioUnreadableProject> unreadable,
        IReadOnlySet<string> withRecording)
    {
        var tracker = Studio.Tracker;
        var drafts = summaries
            .Where(IsShownAsDraft)
            .OrderByDescending(summary => summary.CreatedAt)
            .Select(summary => new StudioDraftItem(
                summary.Id,
                summary.Name,
                $"{summary.CreatedAt.ToLocalTime():g}, {FormatFileSize(summary.SizeBytes)}",
                tracker.IsOpen(summary.Id),
                note: summary.ExportMissing ? StudioDraftItem.ExportMissingNote : string.Empty,
                canOpen: true,
                canSaveRecording: withRecording.Contains(summary.Id)))
            .Concat(unreadable
                .OrderByDescending(project => project.CreatedAt)
                .Select(project => new StudioDraftItem(
                    project.Id,
                    StudioDraftItem.UnreadableName,
                    $"{project.CreatedAt.ToLocalTime():g}, {FormatFileSize(project.SizeBytes)}",
                    tracker.IsOpen(project.Id),
                    note: StudioDraftItem.UnreadableNote,
                    canOpen: false,
                    canSaveRecording: project.HasScreenRecording)))
            .ToList();

        var isSameList = drafts.Count == StudioDrafts.Count
            && drafts.Zip(StudioDrafts).All(pair => pair.First.IsSameDraft(pair.Second));
        if (isSameList)
        {
            for (var index = 0; index < drafts.Count; index++)
            {
                StudioDrafts[index].Details = drafts[index].Details;
                StudioDrafts[index].IsOpen = drafts[index].IsOpen;
            }
        }
        else
        {
            StudioDrafts.Clear();
            foreach (var draft in drafts)
            {
                // A copy that is under way belongs to the project, not to the row it was started from.
                draft.IsSavingRecording = _studioRecordingsBeingSaved.Contains(draft.Id);
                StudioDrafts.Add(draft);
            }
        }

        OnPropertyChanged(nameof(StudioDraftsVisibility));
    }

    // Raised on the UI thread, by a Studio window opening or having finished closing.
    private void OnStudioOpenProjectsChanged(object? sender, EventArgs e) => RefreshStudioProjectsIfShown();

    // Raised on a background thread. Clean up now refreshes by itself when it is done.
    private void OnStudioCleanupCompleted(object? sender, StudioCleanupResult e) =>
        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (!_studioCleanupRunning)
            {
                RefreshStudioProjectsIfShown();
            }
        });

    private void RefreshStudioProjectsIfShown()
    {
        // Nothing to refresh until General has shown the numbers for the first time.
        if (!_closed && _studioStorageInitialization is not null)
        {
            _ = RefreshStudioStorageAsync();
        }
    }

    /// <summary>
    /// Deletes a draft and its recordings, off the UI thread, then refreshes the list and the
    /// storage numbers. A draft that is open in an editor is left alone.
    /// </summary>
    /// <returns>Null when the draft is gone or was left alone, otherwise a sentence saying why not.</returns>
    public async Task<string?> DeleteStudioDraftAsync(StudioDraftItem draft)
    {
        if (!IsStudioPreviewEnabled || Studio.Tracker.IsOpen(draft.Id))
        {
            return null;
        }

        string? error = null;
        try
        {
            var projects = Studio.Projects;
            await Task.Run(() => projects.Delete(draft.Id));
        }
        catch (Exception ex)
        {
            error = $"The draft could not be deleted: {ex.Message}";
        }

        await RefreshStudioStorageAsync();
        return error;
    }

    /// <summary>
    /// Saves a row's screen recording as an ordinary video, in the folder and under the name any
    /// saved video gets. The project is left as it is. The copy is made off the UI thread, and the
    /// row's button waits until it is done.
    /// </summary>
    /// <returns>
    /// The path of the video, or a sentence saying why there is none. Both are null when a copy
    /// of that recording was already under way.
    /// </returns>
    public async Task<(string? Path, string? Error)> SaveStudioScreenRecordingAsync(StudioDraftItem draft)
    {
        var id = draft.Id;
        if (!IsStudioPreviewEnabled || !_studioRecordingsBeingSaved.Add(id))
        {
            return (null, null);
        }

        SetSavingStudioRecording(id, true);
        try
        {
            var path = await StudioScreenRecording.SaveAsync(
                Studio.Projects,
                id,
                () => _storage.GenerateFilePath(CaptureType.Video));
            return (path, null);
        }
        catch (Exception ex)
        {
            return (null, $"The screen recording could not be saved: {ex.Message}");
        }
        finally
        {
            _studioRecordingsBeingSaved.Remove(id);
            SetSavingStudioRecording(id, false);
        }
    }

    // The list may have been filled again since the copy started, so the row is found by its project.
    private void SetSavingStudioRecording(string projectId, bool isSaving)
    {
        foreach (var row in StudioDrafts)
        {
            if (string.Equals(row.Id, projectId, StringComparison.Ordinal))
            {
                row.IsSavingRecording = isSaving;
            }
        }
    }

    /// <summary>
    /// Runs the Studio cleanup rules now, off the UI thread, then refreshes the storage numbers and
    /// reports the outcome in <see cref="StudioCleanupStatus"/>. A request made while one is already
    /// running is ignored.
    /// </summary>
    public async Task CleanUpStudioProjectsAsync()
    {
        if (_studioCleanupRunning || !IsStudioPreviewEnabled)
        {
            return;
        }

        _studioCleanupRunning = true;
        StudioCleanupStatus = "Cleaning up\u2026";
        try
        {
            var result = await Studio.Cleanup.RunAsync();
            await RefreshStudioStorageAsync();
            if (!_closed)
            {
                StudioCleanupStatus = result switch
                {
                    null => "Cleanup couldn't finish. Try again later.",
                    { DeletedProjectCount: 0 } => "Nothing needed cleaning up.",
                    { DeletedProjectCount: 1 } => "Removed 1 project.",
                    _ => $"Removed {result.DeletedProjectCount:N0} projects.",
                };
            }
        }
        finally
        {
            _studioCleanupRunning = false;
        }
    }

    private static string FormatStudioStorage(StudioStorageSummary summary)
    {
        var projectLabel = summary.ProjectCount == 1 ? "project" : "projects";
        return $"{summary.ProjectCount:N0} {projectLabel}, {FormatFileSize(summary.TotalBytes)}";
    }

    // What switching Studio off leaves on disk. Nothing is deleted by the switch, and nothing is
    // cleaned up while it is off, so it is said where the switch is.
    private static string FormatStudioKeptNote(StudioStorageSummary summary) => summary.ProjectCount switch
    {
        0 => string.Empty,
        1 => $"1 Studio project is kept and uses {FormatFileSize(summary.TotalBytes)}. It is not cleaned up while Studio is off. Switch Studio on to open or delete it.",
        _ => $"{summary.ProjectCount:N0} Studio projects are kept and use {FormatFileSize(summary.TotalBytes)}. They are not cleaned up while Studio is off. Switch Studio on to open or delete them.",
    };

    private static int ToAfterRecordingIndex(VideoAfterRecording choice) => choice switch
    {
        VideoAfterRecording.Save => 0,
        VideoAfterRecording.Studio => 2,
        _ => 1,
    };

    // Called while the other file restores a section's saved values, or all of them. The switch
    // and the storage rules are in General; the After recording choice is in Video.
    partial void RestoreStudioSettings(SettingsSectionKind? kind)
    {
        if (kind is null or SettingsSectionKind.General)
        {
            IsStudioPreviewEnabled = _settings.StudioPreviewEnabled;
            StudioSourceRetentionDays = _settings.StudioSourceRetentionDays;
            StudioStorageCapGigabytes = _settings.StudioStorageCapGigabytes;
        }

        if (kind is null or SettingsSectionKind.Video)
        {
            VideoAfterRecordingIndex = ToAfterRecordingIndex(_settings.VideoAfterRecording);
        }
    }

    partial void ReleaseStudio()
    {
        if (_studio is { } studio)
        {
            studio.Tracker.Changed -= OnStudioOpenProjectsChanged;
            studio.Cleanup.CleanupCompleted -= OnStudioCleanupCompleted;
        }
    }

    partial void OnIsStudioPreviewEnabledChanged(bool value)
    {
        if (!IsPersistenceSuppressed(SettingsSectionKind.General))
        {
            _settings.StudioPreviewEnabled = value;

            // The trimmer switch and the After recording choice stand in for each other, and
            // the one that was hidden has not followed what the other was set to.
            var wasLoading = _loading;
            _loading = true;
            try
            {
                ShowTrimmer = _settings.ShowTrimmer;
                VideoAfterRecordingIndex = ToAfterRecordingIndex(_settings.VideoAfterRecording);
            }
            finally
            {
                _loading = wasLoading;
            }
        }

        // The numbers, the drafts, and the line that says what is kept while Studio is off.
        RefreshStudioProjectsIfShown();
    }

    partial void OnVideoAfterRecordingIndexChanged(int value) => PersistStudio(SettingsSectionKind.Video, () =>
    {
        // A ComboBox reports -1 while it has no selection.
        if (value is < 0 or > 2)
        {
            return;
        }

        _settings.VideoAfterRecording = value switch
        {
            0 => VideoAfterRecording.Save,
            2 => VideoAfterRecording.Studio,
            _ => VideoAfterRecording.Trimmer,
        };
    });

    partial void OnStudioSourceRetentionDaysChanged(double value)
    {
        // NumberBox reports NaN when its text is cleared; put the saved value back.
        if (double.IsNaN(value))
        {
            StudioSourceRetentionDays = _settings.StudioSourceRetentionDays;
            return;
        }

        PersistStudio(SettingsSectionKind.General, () => _settings.StudioSourceRetentionDays = (int)Math.Round(value));
    }

    partial void OnStudioStorageCapGigabytesChanged(double value)
    {
        if (double.IsNaN(value))
        {
            StudioStorageCapGigabytes = _settings.StudioStorageCapGigabytes;
            return;
        }

        PersistStudio(SettingsSectionKind.General, () => _settings.StudioStorageCapGigabytes = (int)Math.Round(value));
    }

    // The Studio controls are hidden while the preview is switched off, so nothing they are bound
    // to may reach the saved settings then.
    private void PersistStudio(SettingsSectionKind kind, Action apply)
    {
        if (IsStudioPreviewEnabled)
        {
            Persist(kind, apply);
        }
    }
}
