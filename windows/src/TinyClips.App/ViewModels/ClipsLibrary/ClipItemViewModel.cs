using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using TinyClips.App.Services.ClipsLibrary;
using TinyClips.Core.Models;
using TinyClips.Core.Models.ClipsLibrary;

namespace TinyClips.App.ViewModels.ClipsLibrary;

/// <summary>
/// One clip as shown in the grid, list, and detail pane. Thumbnails load lazily the first time
/// the UI binds to <see cref="Thumbnail"/>, so only realized (virtualized) items pay the cost.
/// </summary>
public sealed partial class ClipItemViewModel : ObservableObject
{
    private readonly IThumbnailCache _thumbnails;
    private readonly DispatcherQueue _dispatcher;
    private bool _thumbnailRequested;
    private LibraryClip _clip;

    public ClipItemViewModel(LibraryClip clip, ClipsLibraryViewModel owner, IThumbnailCache thumbnails, DispatcherQueue dispatcher)
    {
        _clip = clip;
        Owner = owner;
        _thumbnails = thumbnails;
        _dispatcher = dispatcher;
        AutomationIdRoot = CreateAutomationIdRoot(clip.Path, clip.Entry.FileName);
    }

    /// <summary>The library that owns this item; templates bind to its commands with this item as parameter.</summary>
    public ClipsLibraryViewModel Owner { get; }

    public LibraryClip Clip => _clip;

    public ClipEntry Entry => _clip.Entry;

    public ClipMetadata Metadata => _clip.Metadata;

    public string Path => _clip.Path;

    public CaptureType Type => _clip.Type;

    public string FileName => _clip.Entry.FileName;

    public DateTimeOffset CapturedAt => _clip.Entry.CapturedAt;

    public long FileSizeBytes => _clip.Entry.FileSizeBytes;

    public string AutomationIdRoot { get; }

    // ------------------------------------------------------------------ display strings

    public string DisplayName => _clip.DisplayName;

    public bool IsFavorite => _clip.Metadata.IsFavorite;

    public IReadOnlyList<string> Tags => _clip.Metadata.Tags;

    public bool HasTags => Tags.Count > 0;

    public string TagsDisplay => string.Join("  ·  ", Tags.Select(tag => "#" + tag));

    public string? Notes => _clip.Metadata.Notes;

    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    public string NotesPreview => (Notes ?? string.Empty).ReplaceLineEndings(" ").Trim();

    public string? Collection => _clip.Metadata.Collection;

    public bool HasCollection => !string.IsNullOrWhiteSpace(Collection);

    public string? UploadedUrl => _clip.Metadata.UploadedUrl;

    public bool HasUploadedUrl => !string.IsNullOrWhiteSpace(UploadedUrl);

    public bool IsVideo => Type == CaptureType.Video;

    public bool IsGif => Type == CaptureType.Gif;

    public bool IsScreenshot => Type == CaptureType.Screenshot;

    public bool IsMotion => Type != CaptureType.Screenshot;

    public string TypeLabel => Type switch
    {
        CaptureType.Screenshot => "Screenshot",
        CaptureType.Video      => "Video",
        CaptureType.Gif        => "GIF",
        _                      => Type.ToString(),
    };

    public string TypeGlyph => Type switch
    {
        CaptureType.Screenshot => "\uE722",
        CaptureType.Video      => "\uE714",
        CaptureType.Gif        => "\uE8B9",
        _                      => "\uEB9F",
    };

    public string EditVerb => IsScreenshot ? "Edit" : "Trim";

    public string EditGlyph => IsScreenshot ? "\uE70F" : "\uE8E4";

    public string DisplayDate => CapturedAt.ToLocalTime().ToString("g");

    public string RelativeDate => FormatRelative(CapturedAt);

    public string FileSizeDisplay => FormatSize(FileSizeBytes);

    public string MetaLine => $"{TypeLabel} · {RelativeDate} · {FileSizeDisplay}";

    public string AutomationName => $"{DisplayName}, {TypeLabel}, {DisplayDate}{(IsFavorite ? ", favorite" : string.Empty)}";

    // ------------------------------------------------------------------ transient UI state

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUploadIdle))]
    private bool _isUploading;

    public bool IsUploadIdle => !IsUploading;

    [ObservableProperty]
    private string? _uploadStatus;

    [ObservableProperty]
    private BitmapImage? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;

    partial void OnThumbnailChanged(BitmapImage? value) => OnPropertyChanged(nameof(HasThumbnail));

    /// <summary>
    /// Bound by item templates; first access kicks off thumbnail generation off-thread and the
    /// property change notification swaps the placeholder for the real image.
    /// </summary>
    public BitmapImage? ThumbnailLazy
    {
        get
        {
            RequestThumbnail();
            return Thumbnail;
        }
    }

    public void RequestThumbnail()
    {
        if (_thumbnailRequested)
        {
            return;
        }

        _thumbnailRequested = true;
        _ = LoadThumbnailAsync();
    }

    /// <summary>Forces the thumbnail to regenerate (after edit/trim changed the file).</summary>
    public void InvalidateThumbnail()
    {
        _thumbnailRequested = false;
        Thumbnail = null;
    }

    private async Task LoadThumbnailAsync()
    {
        var path = await _thumbnails.GetThumbnailPathAsync(Entry).ConfigureAwait(false);
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

    // ------------------------------------------------------------------ updates

    /// <summary>Replaces the backing clip (new metadata and/or refreshed file info) in place.</summary>
    public void Update(LibraryClip clip)
    {
        var entryChanged = _clip.Entry != clip.Entry;
        _clip = clip;
        if (entryChanged)
        {
            InvalidateThumbnail();
        }

        OnPropertyChanged(string.Empty);
    }

    public void UpdateMetadata(ClipMetadata metadata) => Update(_clip with { Metadata = metadata });

    // ------------------------------------------------------------------ helpers

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576     => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024         => $"{bytes / 1_024.0:F0} KB",
        _                => $"{bytes} B",
    };

    public static string FormatRelative(DateTimeOffset captured, DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.Now;
        var local = captured.ToLocalTime();
        var age = now - captured;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "Just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} min ago";
        }

        if (local.Date == now.ToLocalTime().Date)
        {
            return $"Today {local:t}";
        }

        if (local.Date == now.ToLocalTime().Date.AddDays(-1))
        {
            return $"Yesterday {local:t}";
        }

        return age < TimeSpan.FromDays(7) ? local.ToString("ddd t") : local.ToString("d");
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
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))[..16];
        return $"Clip-{label}-{pathHash}";
    }
}
