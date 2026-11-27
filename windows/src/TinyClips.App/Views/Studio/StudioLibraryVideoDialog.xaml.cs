using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using TinyClips.App.Services.ClipsLibrary;
using TinyClips.App.ViewModels.ClipsLibrary;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.App.Views.Studio;

/// <summary>One video of the Clips Library as the chooser lists it. Its picture loads when it is first shown.</summary>
internal sealed partial class StudioLibraryVideoItem : ObservableObject
{
    private readonly IThumbnailCache _thumbnails;
    private readonly DispatcherQueue _dispatcher;
    private bool _thumbnailRequested;

    public StudioLibraryVideoItem(ClipEntry entry, IThumbnailCache thumbnails, DispatcherQueue dispatcher)
    {
        Entry = entry;
        _thumbnails = thumbnails;
        _dispatcher = dispatcher;
    }

    public ClipEntry Entry { get; }

    public string Name => Path.GetFileNameWithoutExtension(Entry.FileName);

    public string Details =>
        $"{ClipItemViewModel.FormatRelative(Entry.CapturedAt)} · {ClipItemViewModel.FormatSize(Entry.FileSizeBytes)}";

    [ObservableProperty]
    private BitmapImage? _thumbnail;

    public BitmapImage? ThumbnailLazy
    {
        get
        {
            if (!_thumbnailRequested)
            {
                _thumbnailRequested = true;
                _ = LoadThumbnailAsync();
            }

            return Thumbnail;
        }
    }

    // A list item is named by its text, which is this.
    public override string ToString() => $"{Name}, {Details}";

    private async Task LoadThumbnailAsync()
    {
        string? path;
        try
        {
            path = await _thumbnails.GetThumbnailPathAsync(Entry).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return;
        }

        if (path is null)
        {
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            Thumbnail = new BitmapImage(new Uri(path)) { DecodePixelWidth = ThumbnailCacheService.ThumbnailWidth };
            OnPropertyChanged(nameof(ThumbnailLazy));
        });
    }
}

/// <summary>
/// Lists the videos of the Clips Library, newest first, and lets one be chosen to be added to a
/// timeline. The same Mac sheet's counterpart; screenshots and GIFs are not listed because a
/// timeline takes video.
/// </summary>
public sealed partial class StudioLibraryVideoDialog : ContentDialog
{
    private readonly IReadOnlyList<StudioLibraryVideoItem> _videos;

    private StudioLibraryVideoDialog(IReadOnlyList<StudioLibraryVideoItem> videos)
    {
        _videos = videos;
        InitializeComponent();
        ShowVideos();
    }

    /// <summary>The video that was chosen. Null until one is, and when the dialog is cancelled.</summary>
    internal ClipEntry? Choice { get; private set; }

    /// <summary>Asks for a video of the Clips Library, in a dialog on <paramref name="owner"/>. Null when none was chosen.</summary>
    internal static async Task<ClipEntry?> ChooseAsync(Window owner, IClipLibraryService library, IThumbnailCache thumbnails)
    {
        if (owner.Content is not FrameworkElement root)
        {
            return null;
        }

        var clips = await library.GetClipsAsync();
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var videos = clips
            .Where(static clip => clip.Type == CaptureType.Video)
            .OrderByDescending(static clip => clip.CapturedAt)
            .Select(clip => new StudioLibraryVideoItem(clip, thumbnails, dispatcher))
            .ToList();

        var dialog = new StudioLibraryVideoDialog(videos)
        {
            XamlRoot = root.XamlRoot,
            RequestedTheme = root.RequestedTheme,
        };
        dialog.PrimaryButtonClick += static (sender, _) =>
        {
            var self = (StudioLibraryVideoDialog)sender;
            self.Choice = (self.VideoList.SelectedItem as StudioLibraryVideoItem)?.Entry;
        };
        await dialog.ShowAsync();
        return dialog.Choice;
    }

    private void ShowVideos()
    {
        var filter = FilterBox.Text.Trim();
        var shown = filter.Length == 0
            ? _videos
            : _videos.Where(video => video.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
        VideoList.ItemsSource = shown;
        EmptyText.Text = _videos.Count == 0
            ? "There are no videos in your Clips Library yet."
            : "No video matches the filter.";
        EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        VideoList.Visibility = shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        IsPrimaryButtonEnabled = false;
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => ShowVideos();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        IsPrimaryButtonEnabled = VideoList.SelectedItem is StudioLibraryVideoItem;

    private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (VideoList.SelectedItem is StudioLibraryVideoItem selected)
        {
            Choice = selected.Entry;
            Hide();
        }
    }
}
