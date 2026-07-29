using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.Graphics;

namespace TinyClips.App;

/// <summary>
/// View model for a single clip entry shown in the library grid or list.
/// </summary>
public sealed class ClipItemViewModel
{
    public required string Path { get; init; }
    public required CaptureType Type { get; init; }
    public required string FileName { get; init; }
    public required string TypeLabel { get; init; }
    public required string TypeGlyph { get; init; }
    public required string DisplayDate { get; init; }
    public required string FileSizeDisplay { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public BitmapImage? Thumbnail { get; init; }
    public Visibility HasThumbnail => Thumbnail is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoThumbnail => Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    public static ClipItemViewModel From(ClipEntry entry)
    {
        var glyph = entry.Type switch
        {
            CaptureType.Screenshot => "\uE722",
            CaptureType.Video      => "\uE714",
            CaptureType.Gif        => "\uE8B9",
            _                      => "\uEB9F",
        };

        var label = entry.Type switch
        {
            CaptureType.Screenshot => "Screenshot",
            CaptureType.Video      => "Video",
            CaptureType.Gif        => "GIF",
            _                      => entry.Type.ToString(),
        };

        var sizeDisplay = entry.FileSizeBytes switch
        {
            >= 1_073_741_824 => $"{entry.FileSizeBytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576     => $"{entry.FileSizeBytes / 1_048_576.0:F1} MB",
            >= 1_024         => $"{entry.FileSizeBytes / 1_024.0:F0} KB",
            _                => $"{entry.FileSizeBytes} B",
        };

        BitmapImage? thumbnail = null;
        if (entry.Type != CaptureType.Video && File.Exists(entry.Path))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.DecodePixelWidth = 180;
                bmp.UriSource = new Uri(entry.Path);
                thumbnail = bmp;
            }
            catch
            {
                // Thumbnail load failures are non-fatal; fall back to the type icon.
            }
        }

        return new ClipItemViewModel
        {
            Path           = entry.Path,
            Type           = entry.Type,
            FileName       = entry.FileName,
            TypeLabel      = label,
            TypeGlyph      = glyph,
            DisplayDate    = entry.CapturedAt.ToLocalTime().ToString("g"),
            FileSizeDisplay = sizeDisplay,
            CapturedAt     = entry.CapturedAt,
            Thumbnail      = thumbnail,
        };
    }
}

/// <summary>
/// A persistent media library window that lets users browse, filter, and act on all saved
/// Tiny Clips captures without leaving the app.
/// </summary>
public sealed partial class ClipsManagerWindow : Window
{
    private const string SettingsKeyViewMode = "clipsManagerViewMode";
    private const string SettingsKeyFilter   = "clipsManagerFilter";
    private const string SettingsKeySort     = "clipsManagerSort";

    private readonly IClipLibraryService _library;
    private readonly ISettingsService _settings;

    private readonly ObservableCollection<ClipItemViewModel> _visibleClips = [];
    private IReadOnlyList<ClipItemViewModel> _allClips = [];
    private bool _isGridView = true;
    private bool _suppressPersist;

    public ClipsManagerWindow()
    {
        _library  = App.Services.GetRequiredService<IClipLibraryService>();
        _settings = App.Services.GetRequiredService<ISettingsService>();

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1100, 750));

        ApplyTheme();
        RestorePersistedState();

        ClipsGridView.ItemsSource = _visibleClips;
        ClipsListView.ItemsSource = _visibleClips;

        Activated += OnFirstActivated;
    }

    private void ApplyTheme()
    {
        var captureSettings = App.Services.GetRequiredService<ICaptureSettings>();
        RootGrid.RequestedTheme = captureSettings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark  => ElementTheme.Dark,
            _              => ElementTheme.Default,
        };
    }

    // Load clips the first time the window becomes active (avoids blocking construction).
    private void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        Activated -= OnFirstActivated;
        _ = LoadClipsAsync();
    }

    private async Task LoadClipsAsync()
    {
        SetLoading(true);
        try
        {
            var entries = await _library.GetClipsAsync();
            _allClips = entries.Select(ClipItemViewModel.From).ToList();
            ApplyFilterAndSort();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ClipsManagerWindow: failed to load clips: {ex}");
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void SetLoading(bool loading)
    {
        LoadingRing.Visibility  = loading ? Visibility.Visible : Visibility.Collapsed;
        ClipsGridView.Visibility = Visibility.Collapsed;
        ClipsListView.Visibility = Visibility.Collapsed;
        EmptyState.Visibility   = Visibility.Collapsed;

        if (!loading)
        {
            UpdateContentVisibility();
        }
    }

    private void UpdateContentVisibility()
    {
        if (_visibleClips.Count == 0)
        {
            EmptyState.Visibility   = Visibility.Visible;
            ClipsGridView.Visibility = Visibility.Collapsed;
            ClipsListView.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyState.Visibility   = Visibility.Collapsed;
        ClipsGridView.Visibility = _isGridView ? Visibility.Visible : Visibility.Collapsed;
        ClipsListView.Visibility = _isGridView ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyFilterAndSort()
    {
        var filtered = _allClips.AsEnumerable();

        // Type filter
        if (FilterScreenshot.IsChecked == true)
        {
            filtered = filtered.Where(c => c.Type == CaptureType.Screenshot);
        }
        else if (FilterVideo.IsChecked == true)
        {
            filtered = filtered.Where(c => c.Type == CaptureType.Video);
        }
        else if (FilterGif.IsChecked == true)
        {
            filtered = filtered.Where(c => c.Type == CaptureType.Gif);
        }

        // Sort
        var sorted = SortCombo.SelectedIndex == 1
            ? filtered.OrderBy(c => c.CapturedAt)
            : filtered.OrderByDescending(c => c.CapturedAt);

        _visibleClips.Clear();
        foreach (var item in sorted)
        {
            _visibleClips.Add(item);
        }

        UpdateContentVisibility();
    }

    // -----------------------------------------------------------------------
    // Toolbar event handlers
    // -----------------------------------------------------------------------

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressPersist) return;
        ApplyFilterAndSort();
        PersistState();
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPersist) return;
        ApplyFilterAndSort();
        PersistState();
    }

    private void OnViewModeToggled(object sender, RoutedEventArgs e)
    {
        _isGridView = GridViewToggle.IsChecked == true;
        UpdateContentVisibility();
        PersistState();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        await LoadClipsAsync();
    }

    // -----------------------------------------------------------------------
    // Per-clip action handlers
    // -----------------------------------------------------------------------

    private void OnRevealClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path })
        {
            RevealInExplorer(path);
        }
    }

    private void OnOpenClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path })
        {
            return;
        }

        var item = _allClips.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;

        var capture = new RecentCapture(item.Path, item.Type, item.CapturedAt);
        (Application.Current as App)?.OpenRecentCaptureFromLibrary(capture);
    }

    private async void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;
        var item = _allClips.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;

        try
        {
            await ClipboardService.CopySavedClipAsync(path, item.Type);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ClipsManagerWindow: clipboard copy failed: {ex}");
            App.ShowClipboardFailureNotification(System.IO.Path.GetFileName(path));
        }
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;

        var dialog = new ContentDialog
        {
            Title           = "Delete clip?",
            Content         = $""{System.IO.Path.GetFileName(path)}" will be permanently deleted.",
            PrimaryButtonText   = "Delete",
            CloseButtonText     = "Cancel",
            DefaultButton       = ContentDialogButton.Close,
            XamlRoot            = RootGrid.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        try
        {
            _library.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ClipsManagerWindow: delete failed: {ex}");
            return;
        }

        // Remove from both collections.
        var vm = _allClips.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
        if (vm is not null)
        {
            _allClips = _allClips.Where(c => !string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
            _visibleClips.Remove(vm);
            UpdateContentVisibility();
        }
    }

    // -----------------------------------------------------------------------
    // State persistence (view mode + filter + sort)
    // -----------------------------------------------------------------------

    private void RestorePersistedState()
    {
        _suppressPersist = true;
        try
        {
            _isGridView = _settings.Get(SettingsKeyViewMode, true);
            GridViewToggle.IsChecked = _isGridView;

            var filter = _settings.Get(SettingsKeyFilter, "All");
            switch (filter)
            {
                case "Screenshot": FilterScreenshot.IsChecked = true; break;
                case "Video":      FilterVideo.IsChecked      = true; break;
                case "Gif":        FilterGif.IsChecked        = true; break;
                default:           FilterAll.IsChecked        = true; break;
            }

            var sort = _settings.Get(SettingsKeySort, 0);
            SortCombo.SelectedIndex = Math.Clamp(sort, 0, 1);
        }
        finally
        {
            _suppressPersist = false;
        }
    }

    private void PersistState()
    {
        _settings.Set(SettingsKeyViewMode, _isGridView);

        var filter = FilterScreenshot.IsChecked == true ? "Screenshot"
                   : FilterVideo.IsChecked      == true ? "Video"
                   : FilterGif.IsChecked        == true ? "Gif"
                   : "All";
        _settings.Set(SettingsKeyFilter, filter);
        _settings.Set(SettingsKeySort, SortCombo.SelectedIndex);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ClipsManagerWindow: reveal in explorer failed: {ex}");
        }
    }
}
