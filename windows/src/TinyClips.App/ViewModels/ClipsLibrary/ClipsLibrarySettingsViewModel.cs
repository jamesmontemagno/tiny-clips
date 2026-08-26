using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TinyClips.Core.Models.ClipsLibrary;
using TinyClips.Core.Services.ClipsLibrary;

namespace TinyClips.App.ViewModels.ClipsLibrary;

/// <summary>
/// Settings-page view model for Clips Library preferences. Every property persists immediately
/// through <see cref="IClipsLibrarySettings"/> and notifies the app so an open Library refreshes.
/// </summary>
public sealed partial class ClipsLibrarySettingsViewModel : ObservableObject
{
    private readonly IClipsLibrarySettings _settings;
    private readonly Action _changed;
    private bool _suppressPersist = true;

    public ClipsLibrarySettingsViewModel(IClipsLibrarySettings settings, Action changed)
    {
        _settings = settings;
        _changed = changed;
        Load();
    }

    public IReadOnlyList<string> ViewModeOptions { get; } = ["Grid", "List"];

    public IReadOnlyList<string> SortOptions { get; } = ["Newest first", "Oldest first", "Largest first", "Name", "Favorites first"];

    public IReadOnlyList<string> TypeFilterOptions { get; } = ["All types", "Screenshots", "Videos", "GIFs", "Favorites"];

    public IReadOnlyList<string> DateFilterOptions { get; } = ["Any date", "Today", "Last 7 days", "Last 30 days"];

    [ObservableProperty]
    private int _defaultViewModeIndex;

    [ObservableProperty]
    private int _defaultSortIndex;

    [ObservableProperty]
    private int _defaultTypeFilterIndex;

    [ObservableProperty]
    private int _defaultDateFilterIndex;

    [ObservableProperty]
    private bool _rememberLastState;

    [ObservableProperty]
    private bool _showNotesPreview;

    [ObservableProperty]
    private bool _showQuickActions;

    [ObservableProperty]
    private bool _showUploadStatus;

    [ObservableProperty]
    private bool _compactListDensity;

    [ObservableProperty]
    private bool _confirmDelete;

    [ObservableProperty]
    private bool _ignoreNonTinyClipsFiles;

    [ObservableProperty]
    private double _autoRefreshSeconds;

    [ObservableProperty]
    private bool _archiveOldClips;

    [ObservableProperty]
    private double _archiveAfterDays;

    partial void OnDefaultViewModeIndexChanged(int value) => Persist(() => _settings.DefaultViewMode = (ClipsViewMode)Math.Clamp(value, 0, 1));

    partial void OnDefaultSortIndexChanged(int value) => Persist(() => _settings.DefaultSort = (ClipSortOption)Math.Clamp(value, 0, 4));

    partial void OnDefaultTypeFilterIndexChanged(int value) => Persist(() => _settings.DefaultTypeFilter = (ClipTypeFilter)Math.Clamp(value, 0, 4));

    partial void OnDefaultDateFilterIndexChanged(int value) => Persist(() => _settings.DefaultDateFilter = (ClipDateFilter)Math.Clamp(value, 0, 3));

    partial void OnRememberLastStateChanged(bool value) => Persist(() => _settings.RememberLastState = value);

    partial void OnShowNotesPreviewChanged(bool value) => Persist(() => _settings.ShowNotesPreview = value);

    partial void OnShowQuickActionsChanged(bool value) => Persist(() => _settings.ShowQuickActions = value);

    partial void OnShowUploadStatusChanged(bool value) => Persist(() => _settings.ShowUploadStatus = value);

    partial void OnCompactListDensityChanged(bool value) => Persist(() => _settings.CompactListDensity = value);

    partial void OnConfirmDeleteChanged(bool value) => Persist(() => _settings.ConfirmDelete = value);

    partial void OnIgnoreNonTinyClipsFilesChanged(bool value) => Persist(() => _settings.IgnoreNonTinyClipsFiles = value);

    partial void OnAutoRefreshSecondsChanged(double value) => Persist(() => _settings.AutoRefreshSeconds = double.IsNaN(value) ? 0 : (int)value);

    partial void OnArchiveOldClipsChanged(bool value) => Persist(() => _settings.ArchiveOldClips = value);

    partial void OnArchiveAfterDaysChanged(double value) => Persist(() => _settings.ArchiveAfterDays = double.IsNaN(value) ? 30 : (int)value);

    [RelayCommand]
    private void ResetToDefaults()
    {
        _settings.ResetToDefaults();
        Load();
        _changed();
    }

    /// <summary>
    /// Call once the section's visual tree has loaded. TwoWay x:Bind targets push transient values
    /// (ComboBox -1, NumberBox NaN) during realization; persistence stays off until then and the
    /// real values are rehydrated afterwards.
    /// </summary>
    public void CompleteRealization()
    {
        Load();
        _suppressPersist = false;
    }

    private void Load()
    {
        var previous = _suppressPersist;
        _suppressPersist = true;
        try
        {
            DefaultViewModeIndex = (int)_settings.DefaultViewMode;
            DefaultSortIndex = (int)_settings.DefaultSort;
            DefaultTypeFilterIndex = (int)_settings.DefaultTypeFilter;
            DefaultDateFilterIndex = (int)_settings.DefaultDateFilter;
            RememberLastState = _settings.RememberLastState;
            ShowNotesPreview = _settings.ShowNotesPreview;
            ShowQuickActions = _settings.ShowQuickActions;
            ShowUploadStatus = _settings.ShowUploadStatus;
            CompactListDensity = _settings.CompactListDensity;
            ConfirmDelete = _settings.ConfirmDelete;
            IgnoreNonTinyClipsFiles = _settings.IgnoreNonTinyClipsFiles;
            AutoRefreshSeconds = _settings.AutoRefreshSeconds;
            ArchiveOldClips = _settings.ArchiveOldClips;
            ArchiveAfterDays = _settings.ArchiveAfterDays;
        }
        finally
        {
            _suppressPersist = previous;
        }
    }

    private void Persist(Action write)
    {
        if (_suppressPersist)
        {
            return;
        }

        write();
        _changed();
    }
}
