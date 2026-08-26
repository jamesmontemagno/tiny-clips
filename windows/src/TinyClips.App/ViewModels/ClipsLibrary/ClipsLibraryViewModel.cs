using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using TinyClips.App.Services.ClipsLibrary;
using TinyClips.Core.Models;
using TinyClips.Core.Models.ClipsLibrary;
using TinyClips.Core.Services;
using TinyClips.Core.Services.ClipsLibrary;

namespace TinyClips.App.ViewModels.ClipsLibrary;

public enum LibraryEmptyState
{
    None,
    Loading,
    NoClips,
    NoMatch,
    FolderMissing,
}

/// <summary>
/// Page view model for the Clips Library: owns the scanned clip set, the active
/// <see cref="ClipQuery"/>, sidebar entries, selection mode, and every clip action.
/// </summary>
public sealed partial class ClipsLibraryViewModel : ObservableObject, IDisposable
{
    private readonly IClipLibraryService _library;
    private readonly IClipMetadataStore _metadata;
    private readonly IClipsLibrarySettings _settings;
    private readonly ICaptureSettings _captureSettings;
    private readonly IUploadcareUploadService _uploadcare;
    private readonly IClipArchiveService _archive;
    private readonly IClipLibraryWatcher _watcher;
    private readonly IThumbnailCache _thumbnails;
    private readonly TimeProvider _time;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _autoRefreshTimer;
    private readonly DispatcherQueueTimer _searchDebounce;
    private readonly DispatcherQueueTimer _statusTimer;
    private readonly Dictionary<string, ClipItemViewModel> _itemsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ClipItemViewModel> _selection = [];
    private IClipsLibraryInteraction? _interaction;
    private bool _suspendQueryReactions;
    private bool _refreshQueued;
    private bool _refreshing;
    private bool _disposed;

    public ClipsLibraryViewModel(
        IClipLibraryService library,
        IClipMetadataStore metadata,
        IClipsLibrarySettings settings,
        ICaptureSettings captureSettings,
        IUploadcareUploadService uploadcare,
        IClipArchiveService archive,
        IClipLibraryWatcher watcher,
        IThumbnailCache thumbnails,
        TimeProvider time,
        DispatcherQueue dispatcher)
    {
        _library = library;
        _metadata = metadata;
        _settings = settings;
        _captureSettings = captureSettings;
        _uploadcare = uploadcare;
        _archive = archive;
        _watcher = watcher;
        _thumbnails = thumbnails;
        _time = time;
        _dispatcher = dispatcher;

        Detail = new ClipDetailViewModel(CommitMetadataAsync);

        foreach (var collection in Enum.GetValues<SmartCollection>())
        {
            SmartCollections.Add(new NavigationEntryViewModel(
                NavigationEntryKind.SmartCollection,
                NavigationEntryViewModel.TitleFor(collection),
                NavigationEntryViewModel.GlyphFor(collection),
                collection));
        }

        _autoRefreshTimer = dispatcher.CreateTimer();
        _autoRefreshTimer.IsRepeating = true;
        _autoRefreshTimer.Tick += (_, _) => QueueRefresh();

        _searchDebounce = dispatcher.CreateTimer();
        _searchDebounce.Interval = TimeSpan.FromMilliseconds(180);
        _searchDebounce.IsRepeating = false;
        _searchDebounce.Tick += (_, _) => ApplyQuery();

        _statusTimer = dispatcher.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(6);
        _statusTimer.IsRepeating = false;
        _statusTimer.Tick += (_, _) => StatusMessage = null;

        _watcher.Changed += OnWatcherChanged;
        _metadata.Changed += OnMetadataChanged;

        RestoreState();
    }

    // ------------------------------------------------------------------ collections

    public ObservableCollection<ClipItemViewModel> Clips { get; } = [];

    public ObservableCollection<NavigationEntryViewModel> SmartCollections { get; } = [];

    public ObservableCollection<NavigationEntryViewModel> Collections { get; } = [];

    public ObservableCollection<NavigationEntryViewModel> TagEntries { get; } = [];

    public ObservableCollection<string> Tags { get; } = [];

    public ObservableCollection<string> SearchSuggestions { get; } = [];

    public ClipDetailViewModel Detail { get; }

    public IClipsLibrarySettings Settings => _settings;

    public bool IsUploadcareEnabled => _captureSettings.UploadcareEnabled;

    public bool ShowUploadActions => IsUploadcareEnabled;

    // ------------------------------------------------------------------ query state

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ClipTypeFilter _typeFilter;

    [ObservableProperty]
    private ClipDateFilter _dateFilter;

    [ObservableProperty]
    private ClipSortOption _sort;

    [ObservableProperty]
    private SmartCollection _smartCollection;

    [ObservableProperty]
    private string? _selectedTag;

    [ObservableProperty]
    private string? _selectedCollection;

    [ObservableProperty]
    private NavigationEntryViewModel? _selectedNavigationEntry;

    public ClipQuery Query => new(SearchText, TypeFilter, DateFilter, SmartCollection, SelectedTag, SelectedCollection, Sort);

    public bool HasActiveFilters => Query.HasActiveFilters;

    [ObservableProperty]
    private string _filterSummary = "All clips";

    // ------------------------------------------------------------------ view state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGridView))]
    [NotifyPropertyChangedFor(nameof(IsListView))]
    private ClipsViewMode _viewMode;

    public bool IsGridView => ViewMode == ClipsViewMode.Grid;

    public bool IsListView => ViewMode == ClipsViewMode.List;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    private LibraryEmptyState _emptyState = LibraryEmptyState.Loading;

    public bool ShowEmptyState => EmptyState != LibraryEmptyState.None;

    public bool ShowContent => EmptyState == LibraryEmptyState.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionCountText))]
    private bool _isSelectionMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionCountText))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private int _selectionCount;

    public bool HasSelection => SelectionCount > 0;

    public string SelectionCountText => SelectionCount switch
    {
        0 => "No clips selected",
        1 => "1 clip selected",
        _ => $"{SelectionCount} clips selected",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedClip))]
    private ClipItemViewModel? _selectedClip;

    public bool HasSelectedClip => SelectedClip is not null;

    [ObservableProperty]
    private bool _isDetailPaneOpen;

    [ObservableProperty]
    private bool _isNavigationPaneOpen;

    [ObservableProperty]
    private string _countSubtitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? _statusMessage;

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    [ObservableProperty]
    private bool _isStatusError;

    public bool ShowNotesPreview => _settings.ShowNotesPreview;

    public bool ShowQuickActions => _settings.ShowQuickActions;

    public bool CompactListDensity => _settings.CompactListDensity;

    public double ListRowHeight => CompactListDensity ? 48 : 68;

    public double ListThumbnailWidth => CompactListDensity ? 64 : 96;

    // ------------------------------------------------------------------ lifecycle

    public void Attach(IClipsLibraryInteraction interaction) => _interaction = interaction;

    public async Task InitializeAsync()
    {
        await RefreshAsync(initial: true);
        _watcher.Watch(_library.GetLibraryDirectories());
        ConfigureAutoRefresh();
    }

    /// <summary>Re-reads display settings (called when the Settings window changes them).</summary>
    public void ReloadSettings()
    {
        OnPropertyChanged(nameof(ShowNotesPreview));
        OnPropertyChanged(nameof(ShowQuickActions));
        OnPropertyChanged(nameof(CompactListDensity));
        OnPropertyChanged(nameof(ListRowHeight));
        OnPropertyChanged(nameof(ListThumbnailWidth));
        OnPropertyChanged(nameof(IsUploadcareEnabled));
        OnPropertyChanged(nameof(ShowUploadActions));
        ConfigureAutoRefresh();
        QueueRefresh();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher.Changed -= OnWatcherChanged;
        _metadata.Changed -= OnMetadataChanged;
        _watcher.Stop();
        _autoRefreshTimer.Stop();
        _searchDebounce.Stop();
        _statusTimer.Stop();
        _metadata.Flush();
        PersistState();
    }

    private void ConfigureAutoRefresh()
    {
        _autoRefreshTimer.Stop();
        var seconds = _settings.AutoRefreshSeconds;
        if (seconds > 0)
        {
            _autoRefreshTimer.Interval = TimeSpan.FromSeconds(seconds);
            _autoRefreshTimer.Start();
        }
    }

    // ------------------------------------------------------------------ loading

    [RelayCommand]
    private Task RefreshAsync() => RefreshAsync(initial: false);

    private async Task RefreshAsync(bool initial)
    {
        if (_refreshing)
        {
            _refreshQueued = true;
            return;
        }

        _refreshing = true;
        if (initial || Clips.Count == 0)
        {
            IsLoading = true;
            EmptyState = LibraryEmptyState.Loading;
        }

        try
        {
            var entries = await _library.GetClipsAsync(_settings.IgnoreNonTinyClipsFiles);

            if (_settings.ArchiveOldClips && _settings.ArchiveAfterDays > 0)
            {
                entries = await ArchiveOldClipsAsync(entries);
            }

            var livePaths = entries.Select(entry => entry.Path).ToList();
            _metadata.Prune(livePaths);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                seen.Add(entry.Path);
                var clip = new LibraryClip(entry, _metadata.Get(entry.Path));
                if (_itemsByPath.TryGetValue(entry.Path, out var existing))
                {
                    existing.Update(clip);
                }
                else
                {
                    _itemsByPath[entry.Path] = new ClipItemViewModel(clip, this, _thumbnails, _dispatcher);
                }
            }

            foreach (var stale in _itemsByPath.Keys.Where(path => !seen.Contains(path)).ToList())
            {
                var removed = _itemsByPath[stale];
                _itemsByPath.Remove(stale);
                if (ReferenceEquals(SelectedClip, removed))
                {
                    SelectedClip = null;
                }
            }

            _ = _thumbnails.PruneAsync(entries);
            ApplyQuery(folderMissing: _library.GetLibraryDirectories().All(dir => !Directory.Exists(dir)));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ClipsLibraryViewModel: refresh failed: {ex}");
            ShowStatus("Couldn't read your clips folder.", isError: true);
        }
        finally
        {
            IsLoading = false;
            _refreshing = false;
            if (_refreshQueued)
            {
                _refreshQueued = false;
                _ = RefreshAsync(initial: false);
            }
        }
    }

    private async Task<IReadOnlyList<ClipEntry>> ArchiveOldClipsAsync(IReadOnlyList<ClipEntry> entries)
    {
        try
        {
            _watcher.IsPaused = true;
            var moved = await Task.Run(() => _archive.ArchiveOlderThan(entries, _settings.ArchiveAfterDays, _time.GetUtcNow()));
            if (moved.Count == 0)
            {
                return entries;
            }

            foreach (var (oldPath, newPath) in moved)
            {
                _metadata.RenamePath(oldPath, newPath);
            }

            var archived = new HashSet<string>(moved.Select(m => m.OldPath), StringComparer.OrdinalIgnoreCase);
            ShowStatus(moved.Count == 1 ? "Archived 1 older clip." : $"Archived {moved.Count} older clips.");
            return entries.Where(entry => !archived.Contains(entry.Path)).ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ClipsLibraryViewModel: archive failed: {ex}");
            return entries;
        }
        finally
        {
            _watcher.IsPaused = false;
        }
    }

    private void QueueRefresh()
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.TryEnqueue(() => _ = RefreshAsync(initial: false));
    }

    private void OnWatcherChanged(object? sender, EventArgs e) => QueueRefresh();

    private void OnMetadataChanged(object? sender, EventArgs e)
    {
        // Metadata edits made through this VM already update items directly; this catches
        // writes from elsewhere (e.g. auto-upload recording a link after save).
        _dispatcher.TryEnqueue(() =>
        {
            foreach (var item in _itemsByPath.Values)
            {
                var latest = _metadata.Get(item.Path);
                if (latest != item.Metadata)
                {
                    item.UpdateMetadata(latest);
                }
            }

            RebuildSidebar();
            Detail.RefreshFromClip();
        });
    }

    // ------------------------------------------------------------------ query application

    private void ApplyQuery(bool? folderMissing = null)
    {
        var all = _itemsByPath.Values.Select(item => item.Clip).ToList();
        var now = _time.GetUtcNow();
        var visible = ClipQueryEngine.Apply(all, Query, now);

        var visibleSet = new HashSet<string>(visible.Select(clip => clip.Path), StringComparer.OrdinalIgnoreCase);
        SyncVisibleClips(visible.Select(clip => _itemsByPath[clip.Path]).ToList());

        RebuildSidebar();
        UpdateFilterSummary();
        OnPropertyChanged(nameof(HasActiveFilters));

        CountSubtitle = visible.Count switch
        {
            0 => all.Count == 0 ? "No clips yet" : "No matching clips",
            1 => all.Count == 1 ? "1 clip" : $"1 of {all.Count} clips",
            _ => visible.Count == all.Count ? $"{visible.Count} clips" : $"{visible.Count} of {all.Count} clips",
        };

        EmptyState =
            all.Count == 0 && folderMissing == true ? LibraryEmptyState.FolderMissing
            : all.Count == 0 ? LibraryEmptyState.NoClips
            : visible.Count == 0 ? LibraryEmptyState.NoMatch
            : LibraryEmptyState.None;

        if (SelectedClip is not null && !visibleSet.Contains(SelectedClip.Path))
        {
            SelectedClip = null;
        }

        PersistState();
    }

    /// <summary>Minimal-diff sync so the list keeps scroll position and selection where possible.</summary>
    private void SyncVisibleClips(IReadOnlyList<ClipItemViewModel> target)
    {
        for (var i = Clips.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(Clips[i]))
            {
                Clips.RemoveAt(i);
            }
        }

        for (var i = 0; i < target.Count; i++)
        {
            var item = target[i];
            var currentIndex = Clips.IndexOf(item);
            if (currentIndex == i)
            {
                continue;
            }

            if (currentIndex >= 0)
            {
                Clips.Move(currentIndex, i);
            }
            else
            {
                Clips.Insert(i, item);
            }
        }
    }

    private void RebuildSidebar()
    {
        var all = _itemsByPath.Values.Select(item => item.Clip).ToList();
        var now = _time.GetUtcNow();
        var counts = ClipQueryEngine.CountSmartCollections(all, now);
        foreach (var entry in SmartCollections)
        {
            entry.Count = counts.TryGetValue(entry.SmartCollection!.Value, out var count) ? count : 0;
        }

        SyncNavigationEntries(Collections, ClipQueryEngine.CollectCollections(all), NavigationEntryKind.Collection, "\uE8B7",
            value => all.Count(clip => ClipQueryEngine.MatchesCollection(clip, value)));
        SyncNavigationEntries(TagEntries, ClipQueryEngine.CollectTags(all), NavigationEntryKind.Tag, "\uE8EC",
            value => all.Count(clip => ClipQueryEngine.MatchesTag(clip, value)));

        var tags = ClipQueryEngine.CollectTags(all);
        if (!Tags.SequenceEqual(tags))
        {
            Tags.Clear();
            foreach (var tag in tags)
            {
                Tags.Add(tag);
            }
        }

        Detail.TagSuggestions.Clear();
        foreach (var tag in tags)
        {
            Detail.TagSuggestions.Add(tag);
        }

        Detail.CollectionSuggestions.Clear();
        foreach (var collection in ClipQueryEngine.CollectCollections(all))
        {
            Detail.CollectionSuggestions.Add(collection);
        }

        // Dropping the last clip with a tag/collection must not leave a dangling filter.
        if (SelectedTag is not null && !tags.Contains(SelectedTag, StringComparer.OrdinalIgnoreCase))
        {
            SelectedTag = null;
        }
    }

    private static void SyncNavigationEntries(
        ObservableCollection<NavigationEntryViewModel> target,
        IReadOnlyList<string> values,
        NavigationEntryKind kind,
        string glyph,
        Func<string, int> countFor)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!values.Contains(target[i].Value!, StringComparer.OrdinalIgnoreCase))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            var existing = target.FirstOrDefault(entry => string.Equals(entry.Value, value, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new NavigationEntryViewModel(kind, value, glyph, value: value);
                target.Insert(Math.Min(i, target.Count), existing);
            }
            else if (target.IndexOf(existing) != i)
            {
                target.Move(target.IndexOf(existing), Math.Min(i, target.Count - 1));
            }

            existing.Count = countFor(value);
        }
    }

    private void UpdateFilterSummary()
    {
        var parts = new List<string>();
        parts.Add(TypeFilter switch
        {
            ClipTypeFilter.Screenshots => "Screenshots",
            ClipTypeFilter.Videos      => "Videos",
            ClipTypeFilter.Gifs        => "GIFs",
            ClipTypeFilter.Favorites   => "Favorites",
            _                          => "All types",
        });
        if (DateFilter != ClipDateFilter.Any)
        {
            parts.Add(DateFilterLabel(DateFilter));
        }

        if (!string.IsNullOrWhiteSpace(SelectedTag))
        {
            parts.Add("#" + SelectedTag);
        }

        FilterSummary = string.Join(" · ", parts);
    }

    public static string DateFilterLabel(ClipDateFilter filter) => filter switch
    {
        ClipDateFilter.Today      => "Today",
        ClipDateFilter.Last7Days  => "Last 7 days",
        ClipDateFilter.Last30Days => "Last 30 days",
        _                         => "Any date",
    };

    public static string SortLabel(ClipSortOption sort) => sort switch
    {
        ClipSortOption.OldestFirst    => "Oldest first",
        ClipSortOption.Largest        => "Largest first",
        ClipSortOption.Name           => "Name",
        ClipSortOption.FavoritesFirst => "Favorites first",
        _                             => "Newest first",
    };

    // ------------------------------------------------------------------ query property reactions

    partial void OnSearchTextChanged(string value)
    {
        if (_suspendQueryReactions)
        {
            return;
        }

        UpdateSearchSuggestions(value);
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    partial void OnTypeFilterChanged(ClipTypeFilter value) => ReactToQueryChange();

    partial void OnDateFilterChanged(ClipDateFilter value) => ReactToQueryChange();

    partial void OnSortChanged(ClipSortOption value) => ReactToQueryChange();

    partial void OnSmartCollectionChanged(SmartCollection value) => ReactToQueryChange();

    partial void OnSelectedTagChanged(string? value) => ReactToQueryChange();

    partial void OnSelectedCollectionChanged(string? value) => ReactToQueryChange();

    partial void OnSelectedNavigationEntryChanged(NavigationEntryViewModel? value)
    {
        if (value is null || _suspendQueryReactions)
        {
            return;
        }

        _suspendQueryReactions = true;
        try
        {
            switch (value.Kind)
            {
                case NavigationEntryKind.SmartCollection:
                    SmartCollection = value.SmartCollection!.Value;
                    SelectedCollection = null;
                    SelectedTag = null;
                    break;
                case NavigationEntryKind.Collection:
                    SmartCollection = SmartCollection.AllClips;
                    SelectedCollection = value.Value;
                    SelectedTag = null;
                    break;
                case NavigationEntryKind.Tag:
                    SmartCollection = SmartCollection.AllClips;
                    SelectedCollection = null;
                    SelectedTag = value.Value;
                    break;
            }
        }
        finally
        {
            _suspendQueryReactions = false;
        }

        ApplyQuery();
    }

    partial void OnViewModeChanged(ClipsViewMode value) => PersistState();

    partial void OnIsDetailPaneOpenChanged(bool value) => PersistState();

    partial void OnIsNavigationPaneOpenChanged(bool value) => PersistState();

    partial void OnSelectedClipChanged(ClipItemViewModel? value) => Detail.Load(value);

    partial void OnIsSelectionModeChanged(bool value)
    {
        if (!value)
        {
            _interaction?.ClearSelection();
            SetSelection([]);
        }
    }

    private void ReactToQueryChange()
    {
        if (!_suspendQueryReactions)
        {
            ApplyQuery();
        }
    }

    private void UpdateSearchSuggestions(string text)
    {
        SearchSuggestions.Clear();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var matches = _itemsByPath.Values
            .Select(item => item.DisplayName)
            .Concat(Tags.Select(tag => "#" + tag))
            .Where(candidate => candidate.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
            .Take(8);
        foreach (var match in matches)
        {
            SearchSuggestions.Add(match);
        }
    }

    // ------------------------------------------------------------------ view commands

    [RelayCommand]
    private void SetGridView() => ViewMode = ClipsViewMode.Grid;

    [RelayCommand]
    private void SetListView() => ViewMode = ClipsViewMode.List;

    [RelayCommand]
    private void ToggleViewMode() => ViewMode = IsGridView ? ClipsViewMode.List : ClipsViewMode.Grid;

    [RelayCommand]
    private void ClearFilters()
    {
        _suspendQueryReactions = true;
        try
        {
            SearchText = string.Empty;
            TypeFilter = ClipTypeFilter.All;
            DateFilter = ClipDateFilter.Any;
            SmartCollection = SmartCollection.AllClips;
            SelectedTag = null;
            SelectedCollection = null;
            SelectedNavigationEntry = SmartCollections.FirstOrDefault();
            SearchSuggestions.Clear();
        }
        finally
        {
            _suspendQueryReactions = false;
        }

        ApplyQuery();
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void SetTypeFilter(string? value)
    {
        if (Enum.TryParse<ClipTypeFilter>(value, out var parsed))
        {
            TypeFilter = parsed;
        }
    }

    [RelayCommand]
    private void SetDateFilter(string? value)
    {
        if (Enum.TryParse<ClipDateFilter>(value, out var parsed))
        {
            DateFilter = parsed;
        }
    }

    [RelayCommand]
    private void SetSort(string? value)
    {
        if (Enum.TryParse<ClipSortOption>(value, out var parsed))
        {
            Sort = parsed;
        }
    }

    [RelayCommand]
    private void FilterByTag(string? tag)
    {
        SelectedNavigationEntry = TagEntries.FirstOrDefault(entry => string.Equals(entry.Value, tag, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    private void ToggleSelectionMode() => IsSelectionMode = !IsSelectionMode;

    [RelayCommand]
    private void ExitSelectionMode() => IsSelectionMode = false;

    [RelayCommand]
    private void ToggleDetailPane() => IsDetailPaneOpen = !IsDetailPaneOpen;

    [RelayCommand]
    private void OpenSettings() => _interaction?.OpenSettings();

    [RelayCommand]
    private void SelectAll()
    {
        if (!IsSelectionMode)
        {
            IsSelectionMode = true;
        }

        _interaction?.SelectAllVisible();
    }

    /// <summary>Called by the view whenever the list/grid selection changes.</summary>
    public void SetSelection(IEnumerable<ClipItemViewModel> items)
    {
        foreach (var previous in _selection)
        {
            previous.IsSelected = false;
        }

        _selection.Clear();
        _selection.AddRange(items.Distinct());
        foreach (var item in _selection)
        {
            item.IsSelected = true;
        }

        SelectionCount = _selection.Count;
        if (!IsSelectionMode)
        {
            SelectedClip = _selection.Count == 1 ? _selection[0] : SelectedClip is not null && _selection.Contains(SelectedClip) ? SelectedClip : _selection.FirstOrDefault();
        }
        else if (_selection.Count == 1)
        {
            SelectedClip = _selection[0];
        }
    }

    public IReadOnlyList<ClipItemViewModel> CurrentSelection => _selection.ToList();

    private IReadOnlyList<ClipItemViewModel> Targets(ClipItemViewModel? explicitItem)
    {
        if (explicitItem is not null)
        {
            return _selection.Count > 1 && _selection.Contains(explicitItem) ? _selection.ToList() : [explicitItem];
        }

        if (_selection.Count > 0)
        {
            return _selection.ToList();
        }

        return SelectedClip is null ? [] : [SelectedClip];
    }

    // ------------------------------------------------------------------ clip actions

    [RelayCommand]
    private void Open(ClipItemViewModel? item)
    {
        var target = item ?? SelectedClip;
        if (target is null)
        {
            return;
        }

        _interaction?.OpenInEditor(new RecentCapture(target.Path, target.Type, target.CapturedAt));
    }

    [RelayCommand]
    private void Reveal(ClipItemViewModel? item)
    {
        var target = item ?? SelectedClip;
        if (target is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ClipsLibraryViewModel: reveal failed: {ex}");
        }
    }

    [RelayCommand]
    private async Task CopyAsync(ClipItemViewModel? item)
    {
        var targets = Targets(item);
        if (targets.Count == 0)
        {
            return;
        }

        try
        {
            if (targets.Count == 1)
            {
                await ClipboardService.CopySavedClipAsync(targets[0].Path, targets[0].Type);
                ShowStatus($"Copied \"{targets[0].DisplayName}\" to the clipboard.");
            }
            else
            {
                await ClipboardService.CopyFilesAsync(targets.Select(target => target.Path).ToList());
                ShowStatus($"Copied {targets.Count} clips to the clipboard.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ClipsLibraryViewModel: copy failed: {ex}");
            App.ShowClipboardFailureNotification(targets[0].FileName);
            ShowStatus("Couldn't copy to the clipboard.", isError: true);
        }
    }

    [RelayCommand]
    private void Share(ClipItemViewModel? item)
    {
        var targets = Targets(item);
        if (targets.Count == 0)
        {
            return;
        }

        _interaction?.Share(targets.Select(target => target.Path).ToList(), targets.Count == 1 ? targets[0].DisplayName : "Tiny Clips");
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(ClipItemViewModel? item)
    {
        var targets = Targets(item);
        if (targets.Count == 0)
        {
            return;
        }

        // Batch: if any is not a favorite, favorite all; otherwise unfavorite all.
        var makeFavorite = targets.Any(target => !target.IsFavorite);
        foreach (var target in targets)
        {
            await CommitMetadataAsync(target, target.Metadata with { IsFavorite = makeFavorite });
        }
    }

    [RelayCommand]
    private async Task RenameAsync(ClipItemViewModel? item)
    {
        var target = item ?? SelectedClip;
        if (target is null || _interaction is null)
        {
            return;
        }

        var newName = await _interaction.PromptTextAsync("Rename clip", "Name", target.DisplayName, "Rename");
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName.Trim(), target.DisplayName, StringComparison.Ordinal))
        {
            return;
        }

        await CommitMetadataAsync(target, target.Metadata with { DisplayName = newName.Trim() });
    }

    [RelayCommand]
    private async Task RenameFileAsync(ClipItemViewModel? item)
    {
        var target = item ?? SelectedClip;
        if (target is null || _interaction is null)
        {
            return;
        }

        var stem = System.IO.Path.GetFileNameWithoutExtension(target.FileName);
        var newStem = await _interaction.PromptTextAsync("Rename file on disk", "File name", stem, "Rename");
        if (string.IsNullOrWhiteSpace(newStem) || string.Equals(newStem.Trim(), stem, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _watcher.IsPaused = true;
            var oldPath = target.Path;
            var newPath = _library.Rename(oldPath, newStem.Trim());
            _metadata.RenamePath(oldPath, newPath);
            _itemsByPath.Remove(oldPath);
            var entry = target.Entry with { Path = newPath, FileName = System.IO.Path.GetFileName(newPath) };
            target.Update(new LibraryClip(entry, _metadata.Get(newPath)));
            _itemsByPath[newPath] = target;
            ApplyQuery();
            ShowStatus($"Renamed to \"{entry.FileName}\".");
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, isError: true);
        }
        finally
        {
            _watcher.IsPaused = false;
        }
    }

    [RelayCommand]
    private async Task EditTagsAsync(ClipItemViewModel? item)
    {
        var targets = Targets(item);
        if (targets.Count == 0 || _interaction is null)
        {
            return;
        }

        var initial = targets.Count == 1 ? string.Join(", ", targets[0].Tags) : string.Empty;
        var label = targets.Count == 1 ? "Tags (comma separated)" : $"Add tags to {targets.Count} clips (comma separated)";
        var text = await _interaction.PromptTextAsync(targets.Count == 1 ? "Edit tags" : "Add tags", label, initial, "Save", "design, demo, bug");
        if (text is null)
        {
            return;
        }

        var tags = ClipMetadata.NormalizeTags(text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(tag => tag.TrimStart('#')));
        foreach (var target in targets)
        {
            var merged = targets.Count == 1 ? tags : ClipMetadata.NormalizeTags(target.Tags.Concat(tags));
            await CommitMetadataAsync(target, target.Metadata.WithTags(merged));
        }
    }

    [RelayCommand]
    private async Task EditNotesAsync(ClipItemViewModel? item)
    {
        var target = item ?? SelectedClip;
        if (target is null || _interaction is null)
        {
            return;
        }

        var notes = await _interaction.PromptTextAsync("Notes", "Notes", target.Notes, "Save", "What is this clip about?");
        if (notes is null)
        {
            return;
        }

        await CommitMetadataAsync(target, target.Metadata with { Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim() });
    }

    [RelayCommand]
    private async Task SetCollectionAsync(ClipItemViewModel? item)
    {
        var targets = Targets(item);
        if (targets.Count == 0 || _interaction is null)
        {
            return;
        }

        var choices = ClipQueryEngine.CollectCollections(_itemsByPath.Values.Select(v => v.Clip));
        var current = targets.Count == 1 ? targets[0].Collection : null;
        var chosen = await _interaction.PromptChoiceAsync("Set collection", "Collection", choices, current, "Save", allowNew: true);
        if (chosen is null)
        {
            return;
        }

        var collection = string.IsNullOrWhiteSpace(chosen) ? null : chosen.Trim();
        foreach (var target in targets)
        {
            await CommitMetadataAsync(target, target.Metadata with { Collection = collection });
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(ClipItemViewModel? item)
    {
        var targets = Targets(item);
        if (targets.Count == 0)
        {
            return;
        }

        if (_settings.ConfirmDelete && _interaction is not null)
        {
            var message = targets.Count == 1
                ? $"\"{targets[0].DisplayName}\" will be permanently deleted from disk."
                : $"{targets.Count} clips will be permanently deleted from disk.";
            if (!await _interaction.ConfirmAsync(targets.Count == 1 ? "Delete clip?" : $"Delete {targets.Count} clips?", message, "Delete", destructive: true))
            {
                return;
            }
        }

        var deleted = 0;
        try
        {
            _watcher.IsPaused = true;
            foreach (var target in targets)
            {
                try
                {
                    _library.Delete(target.Path);
                    _metadata.Remove(target.Path);
                    _itemsByPath.Remove(target.Path);
                    Clips.Remove(target);
                    deleted++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ClipsLibraryViewModel: delete failed for {target.Path}: {ex}");
                }
            }
        }
        finally
        {
            _watcher.IsPaused = false;
        }

        if (targets.Contains(SelectedClip!))
        {
            SelectedClip = null;
        }

        SetSelection([]);
        _interaction?.ClearSelection();
        ApplyQuery();
        ShowStatus(deleted == 1 ? "Deleted 1 clip." : $"Deleted {deleted} clips.");
    }

    [RelayCommand]
    private async Task ArchiveAsync(ClipItemViewModel? item)
    {
        var targets = Targets(item);
        if (targets.Count == 0)
        {
            return;
        }

        var moved = 0;
        try
        {
            _watcher.IsPaused = true;
            foreach (var target in targets)
            {
                try
                {
                    var newPath = await Task.Run(() => _archive.Archive(target.Entry));
                    _metadata.RenamePath(target.Path, newPath);
                    _itemsByPath.Remove(target.Path);
                    Clips.Remove(target);
                    moved++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ClipsLibraryViewModel: archive failed for {target.Path}: {ex}");
                }
            }
        }
        finally
        {
            _watcher.IsPaused = false;
        }

        if (targets.Contains(SelectedClip!))
        {
            SelectedClip = null;
        }

        SetSelection([]);
        _interaction?.ClearSelection();
        ApplyQuery();
        ShowStatus(moved == 1 ? "Moved 1 clip to Archive." : $"Moved {moved} clips to Archive.");
    }

    [RelayCommand]
    private async Task UploadAsync(ClipItemViewModel? item)
    {
        var target = item ?? SelectedClip;
        if (target is null || target.IsUploading)
        {
            return;
        }

        if (!IsUploadcareEnabled)
        {
            ShowStatus("Enable Uploadcare in Settings before uploading.", isError: true);
            return;
        }

        target.IsUploading = true;
        target.UploadStatus = "Uploading…";
        try
        {
            var result = await _uploadcare.UploadAsync(target.Path);
            await CommitMetadataAsync(target, target.Metadata with { UploadedUrl = result.DeliveryUri.AbsoluteUri });
            target.UploadStatus = "Uploaded";
            ShowStatus("Uploaded to Uploadcare. The link is saved with the clip.");
            if (_captureSettings.UploadcareCopyUrl)
            {
                await CopyLinkAsync(target);
            }
        }
        catch (UploadcareUploadException ex)
        {
            target.UploadStatus = "Upload failed";
            ShowStatus(ex.Message, isError: true);
        }
        finally
        {
            target.IsUploading = false;
        }
    }

    [RelayCommand]
    private async Task CopyLinkAsync(ClipItemViewModel? item)
    {
        var target = item ?? SelectedClip;
        if (string.IsNullOrWhiteSpace(target?.UploadedUrl))
        {
            return;
        }

        try
        {
            await ClipboardService.CopyTextAsync(target.UploadedUrl);
            ShowStatus("Upload link copied to the clipboard.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ClipsLibraryViewModel: copy link failed: {ex}");
            ShowStatus("Couldn't copy the link.", isError: true);
        }
    }

    [RelayCommand]
    private async Task OpenLinkAsync(ClipItemViewModel? item)
    {
        var target = item ?? SelectedClip;
        if (string.IsNullOrWhiteSpace(target?.UploadedUrl) || !Uri.TryCreate(target.UploadedUrl, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (!await Windows.System.Launcher.LaunchUriAsync(uri))
        {
            ShowStatus("Windows couldn't open the upload link.", isError: true);
        }
    }

    [RelayCommand]
    private void OpenSaveFolder()
    {
        var directory = _library.GetLibraryDirectories().FirstOrDefault();
        if (directory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ClipsLibraryViewModel: open folder failed: {ex}");
        }
    }

    [RelayCommand]
    private void DismissStatus() => StatusMessage = null;

    // ------------------------------------------------------------------ metadata commit

    private Task CommitMetadataAsync(ClipItemViewModel item, ClipMetadata metadata)
    {
        _metadata.Upsert(metadata);
        item.UpdateMetadata(_metadata.Get(item.Path));
        if (ReferenceEquals(item, SelectedClip))
        {
            Detail.RefreshFromClip();
        }

        // Favorites/tags/collections affect filters & sidebar; re-run the query in place.
        ApplyQuery();
        return Task.CompletedTask;
    }

    /// <summary>Called by the app after an editor/trimmer saves over a clip so its tile refreshes.</summary>
    public void NotifyClipChanged(string path)
    {
        if (_itemsByPath.TryGetValue(path, out var item))
        {
            item.InvalidateThumbnail();
        }

        QueueRefresh();
    }

    // ------------------------------------------------------------------ status + persistence

    private void ShowStatus(string message, bool isError = false)
    {
        IsStatusError = isError;
        StatusMessage = message;
        _statusTimer.Stop();
        if (!isError)
        {
            _statusTimer.Start();
        }
    }

    private void RestoreState()
    {
        _suspendQueryReactions = true;
        try
        {
            var remember = _settings.RememberLastState;
            ViewMode = remember ? _settings.LastViewMode : _settings.DefaultViewMode;
            Sort = remember ? _settings.LastSort : _settings.DefaultSort;
            TypeFilter = remember ? _settings.LastTypeFilter : _settings.DefaultTypeFilter;
            DateFilter = remember ? _settings.LastDateFilter : _settings.DefaultDateFilter;
            SmartCollection = remember ? _settings.LastSmartCollection : SmartCollection.AllClips;
            IsNavigationPaneOpen = _settings.IsNavigationPaneOpen;
            IsDetailPaneOpen = _settings.IsDetailPaneOpen;
            SelectedNavigationEntry = SmartCollections.FirstOrDefault(entry => entry.SmartCollection == SmartCollection) ?? SmartCollections[0];
        }
        finally
        {
            _suspendQueryReactions = false;
        }

        UpdateFilterSummary();
    }

    private void PersistState()
    {
        if (_suspendQueryReactions)
        {
            return;
        }

        _settings.IsNavigationPaneOpen = IsNavigationPaneOpen;
        _settings.IsDetailPaneOpen = IsDetailPaneOpen;
        if (!_settings.RememberLastState)
        {
            return;
        }

        _settings.LastViewMode = ViewMode;
        _settings.LastSort = Sort;
        _settings.LastTypeFilter = TypeFilter;
        _settings.LastDateFilter = DateFilter;
        _settings.LastSmartCollection = SmartCollection;
    }
}
