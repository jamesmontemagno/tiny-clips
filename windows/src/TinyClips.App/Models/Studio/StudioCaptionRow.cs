using CommunityToolkit.Mvvm.ComponentModel;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Transcription;

namespace TinyClips.App.Models.Studio;

/// <summary>
/// A cue as the list in the Captions panel shows it, in time order: when it is, what it says,
/// what the checks found in it, and whether it is the one at the playhead.
/// </summary>
public sealed class StudioCaptionRow : ObservableObject
{
    private static readonly StudioTextRange[] NoHighlights = [];

    private readonly string _spokenTime;
    private IReadOnlyList<StudioTextRange> _highlights = NoHighlights;
    private bool _isCurrent;

    public StudioCaptionRow(int index, StudioCaptionCue cue, IEnumerable<StudioCaptionFindingKind> problems)
    {
        ArgumentNullException.ThrowIfNull(cue);
        ArgumentNullException.ThrowIfNull(problems);
        Index = index;
        Cue = cue;
        _spokenTime = StudioCaptionText.GetRangeText(cue);
        TimeText = StudioCaptionPanelText.GetClockRangeText(cue);
        Text = StudioCaptionText.GetRowText(cue);
        ProblemText = StudioCaptionPanelText.GetProblemsText(problems);
    }

    /// <summary>The cue's place in the project's list.</summary>
    public int Index { get; }

    public StudioCaptionCue Cue { get; }

    /// <summary>When it starts and ends, as a clock shows it.</summary>
    public string TimeText { get; }

    /// <summary>What it says, on one line.</summary>
    public string Text { get; }

    /// <summary>What the checks found in it, or nothing.</summary>
    public string ProblemText { get; }

    public bool HasProblems => ProblemText.Length > 0;

    /// <summary>Where what is searched for is in <see cref="Text"/>.</summary>
    public IReadOnlyList<StudioTextRange> Highlights
    {
        get => _highlights;
        set => SetProperty(ref _highlights, value);
    }

    /// <summary>Whether it is on show at the playhead.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    /// <summary>What a screen reader says for the row. The list item takes it from here.</summary>
    public override string ToString() => HasProblems ? $"{_spokenTime}: {Text}. {ProblemText}" : $"{_spokenTime}: {Text}";
}

/// <summary>A model as the list in the Generate flyout shows it.</summary>
public sealed record StudioCaptionModelOption(StudioCaptionModel Model)
{
    public string Text => $"{Model.Name} ({Model.SizeText})";

    /// <summary>What a screen reader says for the item. The list item takes it from here.</summary>
    public override string ToString() => Text;
}
