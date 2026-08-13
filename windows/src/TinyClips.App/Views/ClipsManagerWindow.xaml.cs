using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.Graphics;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TinyClips.App;

/// <summary>
/// View model for a single clip entry shown in the library grid or list.
/// </summary>
[SupportedOSPlatform("windows10.0.22000.0")]
public sealed partial class ClipItemViewModel : ObservableObject
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UploadAvailable))]
    [NotifyPropertyChangedFor(nameof(UploadedUrlAvailable))]
    private string? _uploadedUrl;

    public required bool IsUploadcareEnabled { get; init; }

    public Visibility UploadAvailable => IsUploadcareEnabled && string.IsNullOrWhiteSpace(UploadedUrl)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility UploadedUrlAvailable => string.IsNullOrWhiteSpace(UploadedUrl)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public required string AutomationIdRoot { get; init; }
    public string GridOpenAutomationId => $"{AutomationIdRoot}-Grid-Open";
    public string GridCopyAutomationId => $"{AutomationIdRoot}-Grid-Copy";
    public string GridMoreAutomationId => $"{AutomationIdRoot}-Grid-More";
    public string GridRevealAutomationId => $"{AutomationIdRoot}-Grid-Reveal";
    public string GridUploadAutomationId => $"{AutomationIdRoot}-Grid-Upload";
    public string GridCopyUrlAutomationId => $"{AutomationIdRoot}-Grid-CopyUrl";
    public string GridOpenUrlAutomationId => $"{AutomationIdRoot}-Grid-OpenUrl";
    public string GridDeleteAutomationId => $"{AutomationIdRoot}-Grid-Delete";
    public string ListOpenAutomationId => $"{AutomationIdRoot}-List-Open";
    public string ListCopyAutomationId => $"{AutomationIdRoot}-List-Copy";
    public string ListMoreAutomationId => $"{AutomationIdRoot}-List-More";
    public string ListRevealAutomationId => $"{AutomationIdRoot}-List-Reveal";
    public string ListUploadAutomationId => $"{AutomationIdRoot}-List-Upload";
    public string ListCopyUrlAutomationId => $"{AutomationIdRoot}-List-CopyUrl";
    public string ListOpenUrlAutomationId => $"{AutomationIdRoot}-List-OpenUrl";
    public string ListDeleteAutomationId => $"{AutomationIdRoot}-List-Delete";

    public static async Task<ClipItemViewModel> FromAsync(ClipEntry entry, bool isUploadcareEnabled)
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

        var thumbnail = await LoadThumbnailAsync(entry);

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
            IsUploadcareEnabled = isUploadcareEnabled,
            AutomationIdRoot = CreateAutomationIdRoot(entry.Path, entry.FileName),
        };
    }

    private static string CreateAutomationIdRoot(string path, string fileName)
    {
        const int maximumLabelLength = 40;

        var label = new string(System.IO.Path.GetFileNameWithoutExtension(fileName)
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray())
            .Trim('-');
        if (label.Length == 0)
        {
            label = "Clip";
        }
        else if (label.Length > maximumLabelLength)
        {
            label = label[..maximumLabelLength].TrimEnd('-');
        }

        var normalizedPath = path
            .Replace(System.IO.Path.AltDirectorySeparatorChar, System.IO.Path.DirectorySeparatorChar)
            .ToUpperInvariant();
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
        return $"Clip-{label}-{pathHash}";
    }

    private static async Task<BitmapImage?> LoadThumbnailAsync(ClipEntry entry)
    {
        const int thumbnailWidth = 260;
        const int thumbnailHeight = 146;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(entry.Path);
            var bitmap = new BitmapImage { DecodePixelWidth = thumbnailWidth };
            if (entry.Type == CaptureType.Video)
            {
                var clip = await MediaClip.CreateFromFileAsync(file);
                var composition = new MediaComposition();
                composition.Clips.Add(clip);
                using var thumbnail = await composition.GetThumbnailAsync(
                    TimeSpan.Zero, thumbnailWidth, thumbnailHeight, VideoFramePrecision.NearestFrame);
                await bitmap.SetSourceAsync(thumbnail);
            }
            else
            {
                using var stream = await file.OpenReadAsync();
                await bitmap.SetSourceAsync(stream);
            }

            return bitmap;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ClipsManagerWindow: thumbnail load failed: {ex}");
            return null;
        }
    }
}

/// <summary>
/// A persistent media library window that lets users browse, filter, and act on all saved
/// Tiny Clips captures without leaving the app.
/// </summary>
[SupportedOSPlatform("windows10.0.22000.0")]
public sealed partial class ClipsManagerWindow : Window
{
    private const string SettingsKeyViewMode = "clipsManagerViewMode";
    private const string SettingsKeyFilter   = "clipsManagerFilter";
    private const string SettingsKeyDateFilter = "clipsManagerDateFilter";
    private const string SettingsKeySort     = "clipsManagerSort";

    private readonly IClipLibraryService _library;
    private readonly IUploadcareUploadService _uploadcare;
    private readonly ISettingsService _settings;
    private readonly ICaptureSettings _captureSettings;

    private readonly ObservableCollection<ClipItemViewModel> _visibleClips = [];
    private IReadOnlyList<ClipItemViewModel> _allClips = [];
    private bool _isGridView = true;
    private bool _isInitializing = true;
    private bool _suppressPersist;

    public ClipsManagerWindow()
    {
        _library  = App.Services.GetRequiredService<IClipLibraryService>();
        _uploadcare = App.Services.GetRequiredService<IUploadcareUploadService>();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        _captureSettings = App.Services.GetRequiredService<ICaptureSettings>();

        InitializeComponent();
        _isInitializing = false;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1600, 820));

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
            _allClips = (await Task.WhenAll(entries.Select(entry =>
                ClipItemViewModel.FromAsync(entry, _captureSettings.UploadcareEnabled)))).ToList();
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
        LibrarySummaryText.Text = _visibleClips.Count switch
        {
            0 => "No captures",
            1 => "1 capture",
            _ => $"{_visibleClips.Count} captures",
        };

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

        var now = DateTimeOffset.Now;
        var startOfToday = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        if (DateToday.IsChecked == true)
        {
            filtered = filtered.Where(c => c.CapturedAt.ToLocalTime() >= startOfToday);
        }
        else if (DateWeek.IsChecked == true)
        {
            var daysSinceMonday = ((int)startOfToday.DayOfWeek + 6) % 7;
            var startOfWeek = startOfToday.AddDays(-daysSinceMonday);
            filtered = filtered.Where(c => c.CapturedAt.ToLocalTime() >= startOfWeek);
        }
        else if (DateMonth.IsChecked == true)
        {
            var startOfMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
            filtered = filtered.Where(c => c.CapturedAt.ToLocalTime() >= startOfMonth);
        }

        // Sort
        var sorted = SortOldest.IsChecked == true
            ? filtered.OrderBy(c => c.CapturedAt)
            : filtered.OrderByDescending(c => c.CapturedAt);

        _visibleClips.Clear();
        foreach (var item in sorted)
        {
            _visibleClips.Add(item);
        }

        UpdateFilterPresentation();
        UpdateContentVisibility();
    }

    // -----------------------------------------------------------------------
    // Toolbar event handlers
    // -----------------------------------------------------------------------

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _suppressPersist) return;
        ApplyFilterAndSort();
        PersistState();
    }

    private void OnClearFiltersClicked(object sender, RoutedEventArgs e)
    {
        _suppressPersist = true;
        try
        {
            FilterAll.IsChecked = true;
            DateAll.IsChecked = true;
        }
        finally
        {
            _suppressPersist = false;
        }

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

    private async void OnUploadClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string path } source)
        {
            return;
        }

        if (!_captureSettings.UploadcareEnabled)
        {
            SetUploadStatus("Enable Uploadcare in Settings before uploading.");
            return;
        }

        var item = _allClips.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        source.IsEnabled = false;
        SetUploadStatus("Uploading capture...");
        try
        {
            var result = await _uploadcare.UploadAsync(path);
            item.UploadedUrl = result.DeliveryUri.AbsoluteUri;
            SetUploadStatus("Uploaded to Uploadcare. Use Copy URL or Open URL to access it.");
        }
        catch (UploadcareUploadException ex)
        {
            SetUploadStatus(ex.Message);
        }
        finally
        {
            source.IsEnabled = true;
        }
    }

    private async void OnCopyUploadUrlClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path })
        {
            return;
        }

        var item = _allClips.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(item?.UploadedUrl))
        {
            return;
        }

        try
        {
            await ClipboardService.CopyTextAsync(item.UploadedUrl);
            SetUploadStatus("Upload URL copied to the clipboard.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ClipsManagerWindow: upload URL clipboard copy failed: {ex}");
            App.ShowClipboardFailureNotification(System.IO.Path.GetFileName(path));
        }
    }

    private async void OnOpenUploadUrlClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path })
        {
            return;
        }

        var item = _allClips.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(item?.UploadedUrl) ||
            !Uri.TryCreate(item.UploadedUrl, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (!await Windows.System.Launcher.LaunchUriAsync(uri))
        {
            SetUploadStatus("Windows couldn't open the Uploadcare URL.");
        }
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;

        var dialog = new ContentDialog
        {
            Title           = "Delete clip?",
            Content         = $"\"{System.IO.Path.GetFileName(path)}\" will be permanently deleted.",
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

            var dateFilter = _settings.Get(SettingsKeyDateFilter, "All");
            switch (dateFilter)
            {
                case "Today": DateToday.IsChecked = true; break;
                case "Week":  DateWeek.IsChecked  = true; break;
                case "Month": DateMonth.IsChecked = true; break;
                default:      DateAll.IsChecked   = true; break;
            }

            var sort = _settings.Get(SettingsKeySort, 0);
            SortOldest.IsChecked = sort == 1;
            SortNewest.IsChecked = sort != 1;
        }
        finally
        {
            _suppressPersist = false;
            UpdateFilterPresentation();
        }
    }

    private void PersistState()
    {
        _settings.Set(SettingsKeyViewMode, _isGridView);

        var filter = FilterScreenshot.IsChecked == true ? "Screenshot"
                   : FilterVideo.IsChecked      == true ? "Video"
                   : FilterGif.IsChecked        == true ? "Gif"
                   : "All";
        var dateFilter = DateToday.IsChecked == true ? "Today"
                       : DateWeek.IsChecked  == true ? "Week"
                       : DateMonth.IsChecked == true ? "Month"
                       : "All";
        _settings.Set(SettingsKeyFilter, filter);
        _settings.Set(SettingsKeyDateFilter, dateFilter);
        _settings.Set(SettingsKeySort, SortOldest.IsChecked == true ? 1 : 0);
    }

    private void UpdateFilterPresentation()
    {
        var typeLabel = FilterScreenshot.IsChecked == true ? "Screenshots"
                      : FilterVideo.IsChecked      == true ? "Videos"
                      : FilterGif.IsChecked        == true ? "GIFs"
                      : "All clips";
        var dateLabel = DateToday.IsChecked == true ? "Today"
                      : DateWeek.IsChecked  == true ? "This week"
                      : DateMonth.IsChecked == true ? "This month"
                      : null;

        FilterSummaryText.Text = dateLabel is null ? typeLabel : $"{typeLabel} · {dateLabel}";
        var hasActiveFilters = FilterAll.IsChecked != true || DateAll.IsChecked != true;
        ClearFiltersButton.Visibility = hasActiveFilters ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(
            FilterButton,
            $"Sort and filter clips. Current filter: {FilterSummaryText.Text}");
    }

    private void SetUploadStatus(string status)
    {
        UploadStatusText.Text = status;
        UploadStatusText.Visibility = Visibility.Visible;
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
