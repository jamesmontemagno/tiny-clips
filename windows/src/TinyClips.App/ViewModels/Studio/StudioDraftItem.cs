using CommunityToolkit.Mvvm.ComponentModel;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.ViewModels.Studio;

/// <summary>
/// One row of the drafts list in Settings: a Studio project that was never exported, with what the
/// row shows and what its two buttons are called.
/// </summary>
public sealed partial class StudioDraftItem : ObservableObject
{
    /// <summary>Why Delete is unavailable while the draft is open in an editor.</summary>
    public const string OpenInStudioNote = "Close this draft in Studio before deleting it.";

    private string _details;
    private bool _isOpen;

    /// <param name="id">The project id.</param>
    /// <param name="name">The project name. An empty one is shown as "Untitled recording".</param>
    /// <param name="details">When the draft was recorded and how much room it takes.</param>
    /// <param name="isOpen">Whether the draft is open in a Studio window.</param>
    public StudioDraftItem(string id, string? name, string details, bool isOpen)
    {
        Id = id;
        Name = StudioEditorText.GetClipName(name);
        _details = details;
        _isOpen = isOpen;
    }

    public string Id { get; }

    public string Name { get; }

    public string OpenButtonName => $"Open {Name} in Studio";

    public string DeleteButtonName => $"Delete {Name}";

    /// <summary>The automation id of the row's Open button. Ids are unique per project.</summary>
    public string OpenButtonId => $"StudioDraftOpen_{Id}";

    /// <summary>The automation id of the row's Delete button.</summary>
    public string DeleteButtonId => $"StudioDraftDelete_{Id}";

    /// <summary>The date and the size, such as "5/12/2025 3:04 PM, 12.3 MB".</summary>
    public string Details
    {
        get => _details;
        set => SetProperty(ref _details, value);
    }

    /// <summary>A draft that is open in an editor cannot be deleted from here.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (SetProperty(ref _isOpen, value))
            {
                OnPropertyChanged(nameof(CanDelete));
                OnPropertyChanged(nameof(DeleteHelpText));
            }
        }
    }

    public bool CanDelete => !IsOpen;

    /// <summary>What Delete does, or why it cannot be used right now.</summary>
    public string DeleteHelpText => IsOpen ? OpenInStudioNote : "Delete this draft and its recordings.";

    /// <summary>Whether another row is the same project under the same name.</summary>
    public bool IsSameDraft(StudioDraftItem other) =>
        string.Equals(Id, other.Id, StringComparison.Ordinal) && string.Equals(Name, other.Name, StringComparison.Ordinal);
}
