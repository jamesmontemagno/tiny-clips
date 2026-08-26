using TinyClips.Core.Models.ClipsLibrary;

namespace TinyClips.Core.Services.ClipsLibrary;

/// <summary>
/// Typed accessors for every Clips Library preference. Mirrors the macOS Clips Manager settings
/// sheet so behaviour stays in parity.
/// </summary>
public interface IClipsLibrarySettings
{
    // Defaults applied when the window opens (unless RememberLastState restores the previous ones).
    ClipsViewMode DefaultViewMode { get; set; }
    ClipSortOption DefaultSort { get; set; }
    ClipTypeFilter DefaultTypeFilter { get; set; }
    ClipDateFilter DefaultDateFilter { get; set; }

    // Display
    bool ShowNotesPreview { get; set; }
    bool ShowQuickActions { get; set; }
    bool ShowUploadStatus { get; set; }
    bool CompactListDensity { get; set; }

    // Behaviour
    bool ConfirmDelete { get; set; }
    bool IgnoreNonTinyClipsFiles { get; set; }
    bool RememberLastState { get; set; }
    bool SelectionRowTapSelects { get; set; }

    // Automation
    int AutoRefreshSeconds { get; set; }
    bool ArchiveOldClips { get; set; }
    int ArchiveAfterDays { get; set; }

    // Last-used state (persisted only when RememberLastState is on).
    ClipsViewMode LastViewMode { get; set; }
    ClipSortOption LastSort { get; set; }
    ClipTypeFilter LastTypeFilter { get; set; }
    ClipDateFilter LastDateFilter { get; set; }
    SmartCollection LastSmartCollection { get; set; }
    bool IsNavigationPaneOpen { get; set; }
    bool IsDetailPaneOpen { get; set; }

    /// <summary>Restores all preferences to their defaults.</summary>
    void ResetToDefaults();
}

public sealed class ClipsLibrarySettings : IClipsLibrarySettings
{
    private const string Prefix = "clipsLibrary.";

    private readonly ISettingsService _settings;

    public ClipsLibrarySettings(ISettingsService settings)
    {
        _settings = settings;
        MigrateLegacyKeys();
    }

    public ClipsViewMode DefaultViewMode
    {
        get => Get(nameof(DefaultViewMode), ClipsViewMode.Grid);
        set => Set(nameof(DefaultViewMode), value);
    }

    public ClipSortOption DefaultSort
    {
        get => Get(nameof(DefaultSort), ClipSortOption.NewestFirst);
        set => Set(nameof(DefaultSort), value);
    }

    public ClipTypeFilter DefaultTypeFilter
    {
        get => Get(nameof(DefaultTypeFilter), ClipTypeFilter.All);
        set => Set(nameof(DefaultTypeFilter), value);
    }

    public ClipDateFilter DefaultDateFilter
    {
        get => Get(nameof(DefaultDateFilter), ClipDateFilter.Any);
        set => Set(nameof(DefaultDateFilter), value);
    }

    public bool ShowNotesPreview
    {
        get => Get(nameof(ShowNotesPreview), true);
        set => Set(nameof(ShowNotesPreview), value);
    }

    public bool ShowQuickActions
    {
        get => Get(nameof(ShowQuickActions), true);
        set => Set(nameof(ShowQuickActions), value);
    }

    public bool ShowUploadStatus
    {
        get => Get(nameof(ShowUploadStatus), true);
        set => Set(nameof(ShowUploadStatus), value);
    }

    public bool CompactListDensity
    {
        get => Get(nameof(CompactListDensity), false);
        set => Set(nameof(CompactListDensity), value);
    }

    public bool ConfirmDelete
    {
        get => Get(nameof(ConfirmDelete), true);
        set => Set(nameof(ConfirmDelete), value);
    }

    public bool IgnoreNonTinyClipsFiles
    {
        get => Get(nameof(IgnoreNonTinyClipsFiles), false);
        set => Set(nameof(IgnoreNonTinyClipsFiles), value);
    }

    public bool RememberLastState
    {
        get => Get(nameof(RememberLastState), true);
        set => Set(nameof(RememberLastState), value);
    }

    public bool SelectionRowTapSelects
    {
        get => Get(nameof(SelectionRowTapSelects), true);
        set => Set(nameof(SelectionRowTapSelects), value);
    }

    public int AutoRefreshSeconds
    {
        get => Get(nameof(AutoRefreshSeconds), 0);
        set => Set(nameof(AutoRefreshSeconds), Math.Max(0, value));
    }

    public bool ArchiveOldClips
    {
        get => Get(nameof(ArchiveOldClips), false);
        set => Set(nameof(ArchiveOldClips), value);
    }

    public int ArchiveAfterDays
    {
        get => Get(nameof(ArchiveAfterDays), 30);
        set => Set(nameof(ArchiveAfterDays), Math.Max(1, value));
    }

    public ClipsViewMode LastViewMode
    {
        get => Get(nameof(LastViewMode), DefaultViewMode);
        set => Set(nameof(LastViewMode), value);
    }

    public ClipSortOption LastSort
    {
        get => Get(nameof(LastSort), DefaultSort);
        set => Set(nameof(LastSort), value);
    }

    public ClipTypeFilter LastTypeFilter
    {
        get => Get(nameof(LastTypeFilter), DefaultTypeFilter);
        set => Set(nameof(LastTypeFilter), value);
    }

    public ClipDateFilter LastDateFilter
    {
        get => Get(nameof(LastDateFilter), DefaultDateFilter);
        set => Set(nameof(LastDateFilter), value);
    }

    public SmartCollection LastSmartCollection
    {
        get => Get(nameof(LastSmartCollection), SmartCollection.AllClips);
        set => Set(nameof(LastSmartCollection), value);
    }

    public bool IsNavigationPaneOpen
    {
        get => Get(nameof(IsNavigationPaneOpen), true);
        set => Set(nameof(IsNavigationPaneOpen), value);
    }

    public bool IsDetailPaneOpen
    {
        get => Get(nameof(IsDetailPaneOpen), true);
        set => Set(nameof(IsDetailPaneOpen), value);
    }

    public void ResetToDefaults()
    {
        DefaultViewMode = ClipsViewMode.Grid;
        DefaultSort = ClipSortOption.NewestFirst;
        DefaultTypeFilter = ClipTypeFilter.All;
        DefaultDateFilter = ClipDateFilter.Any;
        ShowNotesPreview = true;
        ShowQuickActions = true;
        ShowUploadStatus = true;
        CompactListDensity = false;
        ConfirmDelete = true;
        IgnoreNonTinyClipsFiles = false;
        RememberLastState = true;
        SelectionRowTapSelects = true;
        AutoRefreshSeconds = 0;
        ArchiveOldClips = false;
        ArchiveAfterDays = 30;
    }

    /// <summary>
    /// The previous Clips Library persisted view mode / filter / sort under ad-hoc keys. Carry them
    /// forward once so users keep their layout after the rewrite.
    /// </summary>
    private void MigrateLegacyKeys()
    {
        const string migratedKey = Prefix + "legacyMigrated";
        if (_settings.Get(migratedKey, false))
        {
            return;
        }

        if (!_settings.Get("clipsManagerViewMode", true))
        {
            LastViewMode = ClipsViewMode.List;
        }

        LastTypeFilter = _settings.Get("clipsManagerFilter", "All") switch
        {
            "Screenshot" => ClipTypeFilter.Screenshots,
            "Video"      => ClipTypeFilter.Videos,
            "Gif"        => ClipTypeFilter.Gifs,
            _            => ClipTypeFilter.All,
        };

        LastDateFilter = _settings.Get("clipsManagerDateFilter", "All") switch
        {
            "Today" => ClipDateFilter.Today,
            "Week"  => ClipDateFilter.Last7Days,
            "Month" => ClipDateFilter.Last30Days,
            _       => ClipDateFilter.Any,
        };

        if (_settings.Get("clipsManagerSort", 0) == 1)
        {
            LastSort = ClipSortOption.OldestFirst;
        }

        _settings.Set(migratedKey, true);
    }

    private T Get<T>(string key, T defaultValue) => _settings.Get(Prefix + key, defaultValue);

    private void Set<T>(string key, T value) => _settings.Set(Prefix + key, value);
}
