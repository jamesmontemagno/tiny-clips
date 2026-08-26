using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TinyClips.Core.Models.ClipsLibrary;
using Windows.Storage;

namespace TinyClips.App.ViewModels.ClipsLibrary;

/// <summary>
/// Edit surface for the clip selected in the library: draft name/tags/notes/collection with
/// explicit Save/Revert, plus media preview source and file facts (resolution, duration).
/// </summary>
public sealed partial class ClipDetailViewModel : ObservableObject
{
    private readonly Func<ClipItemViewModel, ClipMetadata, Task> _commitMetadata;
    private bool _loading;
    private int _infoVersion;

    public ClipDetailViewModel(Func<ClipItemViewModel, ClipMetadata, Task> commitMetadata)
    {
        _commitMetadata = commitMetadata;
        Tags.CollectionChanged += (_, _) => RecomputeDirty();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClip))]
    [NotifyPropertyChangedFor(nameof(IsVideo))]
    [NotifyPropertyChangedFor(nameof(IsImage))]
    [NotifyPropertyChangedFor(nameof(MediaUri))]
    private ClipItemViewModel? _clip;

    public bool HasClip => Clip is not null;

    public bool IsVideo => Clip?.IsVideo == true;

    /// <summary>Screenshots and GIFs both render through <c>Image</c> (GIFs animate natively).</summary>
    public bool IsImage => Clip is not null && !Clip.IsVideo;

    public Uri? MediaUri => Clip is null ? null : new Uri(Clip.Path);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _draftName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _draftNotes = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _draftCollection = string.Empty;

    [ObservableProperty]
    private string _newTagText = string.Empty;

    public ObservableCollection<string> Tags { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
    private bool _isDirty;

    [ObservableProperty]
    private string _resolutionDisplay = string.Empty;

    [ObservableProperty]
    private string _durationDisplay = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDuration))]
    private bool _hasDurationValue;

    public bool HasDuration => HasDurationValue;

    /// <summary>Suggestions for the tag entry box (all tags known to the library).</summary>
    public ObservableCollection<string> TagSuggestions { get; } = [];

    /// <summary>Known collections for the collection combo.</summary>
    public ObservableCollection<string> CollectionSuggestions { get; } = [];

    public void Load(ClipItemViewModel? clip)
    {
        _loading = true;
        try
        {
            Clip = clip;
            DraftName = clip?.DisplayName ?? string.Empty;
            DraftNotes = clip?.Notes ?? string.Empty;
            DraftCollection = clip?.Collection ?? string.Empty;
            NewTagText = string.Empty;
            Tags.Clear();
            foreach (var tag in clip?.Tags ?? [])
            {
                Tags.Add(tag);
            }
        }
        finally
        {
            _loading = false;
        }

        IsDirty = false;
        _ = LoadFileInfoAsync(clip);
    }

    /// <summary>Re-syncs drafts after an external metadata change without discarding unsaved edits.</summary>
    public void RefreshFromClip()
    {
        if (Clip is null || IsDirty)
        {
            return;
        }

        Load(Clip);
    }

    partial void OnDraftNameChanged(string value) => RecomputeDirty();

    partial void OnDraftNotesChanged(string value) => RecomputeDirty();

    partial void OnDraftCollectionChanged(string value) => RecomputeDirty();

    [RelayCommand]
    private void AddTag()
    {
        var tag = NewTagText.Trim().TrimStart('#');
        NewTagText = string.Empty;
        if (tag.Length == 0 || Tags.Any(existing => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Tags.Add(tag);
    }

    [RelayCommand]
    private void RemoveTag(string? tag)
    {
        if (tag is not null)
        {
            Tags.Remove(tag);
        }
    }

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private async Task SaveAsync()
    {
        if (Clip is null)
        {
            return;
        }

        var fallbackName = System.IO.Path.GetFileNameWithoutExtension(Clip.FileName);
        var name = DraftName.Trim();
        var metadata = Clip.Metadata with
        {
            DisplayName = name.Length == 0 || string.Equals(name, fallbackName, StringComparison.Ordinal) ? null : name,
            Notes = string.IsNullOrWhiteSpace(DraftNotes) ? null : DraftNotes.Trim(),
            Collection = string.IsNullOrWhiteSpace(DraftCollection) ? null : DraftCollection.Trim(),
            Tags = ClipMetadata.NormalizeTags(Tags),
        };

        await _commitMetadata(Clip, metadata);
        IsDirty = false;
    }

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Revert() => Load(Clip);

    private void RecomputeDirty()
    {
        if (_loading || Clip is null)
        {
            return;
        }

        var originalName = Clip.DisplayName;
        IsDirty =
            !string.Equals(DraftName.Trim(), originalName, StringComparison.Ordinal)
            || !string.Equals(DraftNotes.Trim(), Clip.Notes ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(DraftCollection.Trim(), Clip.Collection ?? string.Empty, StringComparison.Ordinal)
            || !Tags.SequenceEqual(Clip.Tags, StringComparer.OrdinalIgnoreCase);
    }

    private async Task LoadFileInfoAsync(ClipItemViewModel? clip)
    {
        var version = ++_infoVersion;
        ResolutionDisplay = string.Empty;
        DurationDisplay = string.Empty;
        HasDurationValue = false;
        if (clip is null)
        {
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(clip.Path);
            string resolution;
            string duration = string.Empty;
            if (clip.IsVideo)
            {
                var props = await file.Properties.GetVideoPropertiesAsync();
                resolution = props.Width > 0 ? $"{props.Width} × {props.Height}" : string.Empty;
                duration = FormatDuration(props.Duration);
            }
            else
            {
                var props = await file.Properties.GetImagePropertiesAsync();
                resolution = props.Width > 0 ? $"{props.Width} × {props.Height}" : string.Empty;
            }

            if (version != _infoVersion)
            {
                return;
            }

            ResolutionDisplay = resolution;
            DurationDisplay = duration;
            HasDurationValue = duration.Length > 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ClipDetailViewModel: file info failed: {ex.Message}");
        }
    }

    public static string FormatDuration(TimeSpan duration) =>
        duration <= TimeSpan.Zero ? string.Empty
        : duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss")
        : duration.ToString(@"m\:ss");
}
