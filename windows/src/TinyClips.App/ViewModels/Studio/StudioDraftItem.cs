using CommunityToolkit.Mvvm.ComponentModel;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.ViewModels.Studio;

/// <summary>
/// One row of the drafts list in Settings: a recording that only its Studio project holds, with
/// what the row shows and what its buttons are called. That is a project that was never
/// exported, one whose exported video is gone, or one Studio cannot read.
/// </summary>
public sealed partial class StudioDraftItem : ObservableObject
{
    /// <summary>Why Delete is unavailable while the draft is open in an editor.</summary>
    public const string OpenInStudioNote = "Close this draft in Studio before deleting it.";

    /// <summary>
    /// Said of a project whose exported videos are all gone. A file of another size under a
    /// video's name counts as gone too: it is another video, or the same one changed since.
    /// </summary>
    public const string ExportMissingNote = "Its exported video is no longer where it was saved, or has been changed since.";

    /// <summary>Said of a project whose file cannot be read.</summary>
    public const string UnreadableNote = "Studio cannot read this project. It may be damaged, or made by a newer version of Tiny Clips.";

    /// <summary>What a project that cannot be read is called. Its name is in the file that cannot be read.</summary>
    public const string UnreadableName = "Unreadable project";

    /// <summary>What the Save recording button shows while nothing is being saved.</summary>
    public const string SaveRecordingText = "Save recording";

    /// <summary>What the Save recording button shows while the recording is being copied.</summary>
    public const string SavingRecordingText = "Saving\u2026";

    /// <summary>What Save recording does. A screen reader reads it after the button's name.</summary>
    public const string SaveRecordingHelp = "Saves the screen recording to your videos folder as an ordinary video. The project is kept as it is.";

    /// <summary>What a screen reader reads after the button's name while the recording is being copied.</summary>
    public const string SavingRecordingHelp = "The screen recording is being saved.";

    private string _details;
    private bool _isOpen;
    private bool _isSavingRecording;

    /// <param name="id">The project id.</param>
    /// <param name="name">The project name. An empty one is shown as "Untitled recording".</param>
    /// <param name="details">When the draft was recorded and how much room it takes.</param>
    /// <param name="isOpen">Whether the draft is open in a Studio window.</param>
    /// <param name="note">Why the row is in the list although it is not a draft like the others, or empty.</param>
    /// <param name="canOpen">Whether Studio can open it. Not one it cannot read.</param>
    /// <param name="canSaveRecording">Whether its screen recording is there to be saved as a video.</param>
    public StudioDraftItem(
        string id,
        string? name,
        string details,
        bool isOpen,
        string note = "",
        bool canOpen = true,
        bool canSaveRecording = false)
    {
        Id = id;
        Name = StudioEditorText.GetClipName(name);
        _details = details;
        _isOpen = isOpen;
        Note = note;
        CanOpen = canOpen;
        CanSaveRecording = canSaveRecording;
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>Why the row is in the list although it is not a draft like the others. Empty for one that is.</summary>
    public string Note { get; }

    public bool HasNote => Note.Length > 0;

    public bool CanOpen { get; }

    /// <summary>Whether the row offers to save the screen recording.</summary>
    public bool CanSaveRecording { get; }

    public string OpenButtonName => $"Open {Name} in Studio";

    public string DeleteButtonName => $"Delete {Name}";

    public string SaveRecordingButtonName => $"Save the screen recording of {Name}";

    /// <summary>The automation id of the row's Open button. Ids are unique per project.</summary>
    public string OpenButtonId => $"StudioDraftOpen_{Id}";

    /// <summary>The automation id of the row's Delete button.</summary>
    public string DeleteButtonId => $"StudioDraftDelete_{Id}";

    /// <summary>The automation id of the row's Save recording button.</summary>
    public string SaveRecordingButtonId => $"StudioDraftSaveRecording_{Id}";

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

    /// <summary>
    /// True while the screen recording is being copied. The button stays enabled then, so that
    /// it keeps the keyboard focus: a button that is disabled while it has the focus passes the
    /// focus on, here to Delete. It says that it is busy instead, and a press does nothing more.
    /// </summary>
    public bool IsSavingRecording
    {
        get => _isSavingRecording;
        set
        {
            if (SetProperty(ref _isSavingRecording, value))
            {
                OnPropertyChanged(nameof(SaveRecordingLabel));
                OnPropertyChanged(nameof(SaveRecordingHelpText));
            }
        }
    }

    /// <summary>What the Save recording button shows: what it does, or that it is doing it.</summary>
    public string SaveRecordingLabel => IsSavingRecording ? SavingRecordingText : SaveRecordingText;

    /// <summary>What Save recording does, or, while the recording is being copied, that it is being saved.</summary>
    public string SaveRecordingHelpText => IsSavingRecording ? SavingRecordingHelp : SaveRecordingHelp;

    /// <summary>Whether another row shows the same project the same way, apart from what a row brings up to date by itself.</summary>
    public bool IsSameDraft(StudioDraftItem other) =>
        string.Equals(Id, other.Id, StringComparison.Ordinal)
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Note, other.Note, StringComparison.Ordinal)
        && CanOpen == other.CanOpen
        && CanSaveRecording == other.CanSaveRecording;
}