using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Transcription;

namespace TinyClips.App.Models.Studio;

/// <summary>A cue as the list in the Captions panel shows it, in time order.</summary>
/// <param name="Index">The cue's place in the project's list.</param>
/// <param name="TimeText">When it starts and ends.</param>
/// <param name="Text">What it says, on one line.</param>
public sealed record StudioCaptionRow(int Index, string TimeText, string Text)
{
    /// <summary>What a screen reader says for the row. The list item takes it from here.</summary>
    public override string ToString() => $"{TimeText}: {Text}";

    public static StudioCaptionRow From(int index, StudioCaptionCue cue) =>
        new(index, StudioCaptionText.GetRangeText(cue), StudioCaptionText.GetRowText(cue));
}

/// <summary>A model as the list in the Generate flyout shows it.</summary>
public sealed record StudioCaptionModelOption(StudioCaptionModel Model)
{
    public string Text => $"{Model.Name} ({Model.SizeText})";

    /// <summary>What a screen reader says for the item. The list item takes it from here.</summary>
    public override string ToString() => Text;
}
