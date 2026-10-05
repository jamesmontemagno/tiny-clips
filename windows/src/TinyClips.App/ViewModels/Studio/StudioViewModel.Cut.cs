using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.ViewModels.Studio;

// The values behind the cut lane, the gaps in the trim bar and the Cut section of the inspector.
// A cut is selected in place of a zoom, never with one. What an edit does is decided in the
// session, and what is said about it in StudioEditorText.
public sealed partial class StudioViewModel
{
    private const string CutActivityId = "StudioCut";
    private const double CutTimeStep = 0.1;

    // What changes when another cut is selected and the project stays as it is.
    private static readonly string[] CutSelectionPropertyNames =
    [
        nameof(SelectedCutIndex),
        nameof(HasSelectedCut),
        nameof(IsCutHintVisible),
        nameof(CutSelectionTitle),
        nameof(CutSelectionDetail),
        nameof(HasCutSelectionDetail),
        nameof(CutStartText),
        nameof(CutStartAccessibleName),
        nameof(CutEndText),
        nameof(CutEndAccessibleName),
        nameof(CutLengthText),
    ];

    private static readonly StudioTimeRange[] NoCuts = [];
    private static readonly StudioTimeSegment[] NoSegments = [];

    // What was last reported of the three values that follow the playhead, so that a playhead that
    // moves thirty times a second only refreshes them when one has changed.
    private bool _couldAddCutAtPlayhead;
    private bool _couldSelectPreviousCut;
    private bool _couldSelectNextCut;

    // The lane and the trim bar

    /// <summary>
    /// The project's cuts, in time order. It is the same list for as long as no cut changes, so
    /// whoever draws them can tell by reference that there is nothing to draw again.
    /// </summary>
    public IReadOnlyList<StudioTimeRange> Cuts => Project?.Edits.Cuts ?? NoCuts;

    /// <summary>The selected cut's place in <see cref="Cuts"/>, or null.</summary>
    public int? SelectedCutIndex => _session.SelectedCutIndex;

    public bool HasSelectedCut => SelectedCutIndex is not null;

    public bool CanAddCutAtPlayhead => _session.CanAddCutAtPlayhead;

    /// <summary>The stretches of the recording the video keeps, in time order: what is left of the trimmed range once the cuts are out.</summary>
    public IReadOnlyList<StudioTimeSegment> KeptSegments => _session.Model?.TimeMap.Segments ?? NoSegments;

    private StudioTimeRange? SelectedCut => _session.SelectedCut;

    // Stepping through the cuts

    public bool CanSelectPreviousCut =>
        IsEditable && _session.Model?.GetCutIndexBefore(SelectedCutIndex, Playhead) is not null;

    public bool CanSelectNextCut =>
        IsEditable && _session.Model?.GetCutIndexAfter(SelectedCutIndex, Playhead) is not null;

    /// <summary>"Cut 2 of 3" for the selected cut, and otherwise how many cuts there are.</summary>
    public string CutSelectionTitle
    {
        get
        {
            var count = Cuts.Count;
            if (SelectedCutIndex is { } index)
            {
                return StudioEditorText.GetCutPositionText(index, count);
            }

            return count switch
            {
                0 => "No cuts yet",
                1 => "1 cut",
                _ => string.Create(CultureInfo.InvariantCulture, $"{count} cuts"),
            };
        }
    }

    /// <summary>When the selected cut starts and ends. Empty without one.</summary>
    public string CutSelectionDetail => SelectedCut is { } cut ? StudioEditorText.GetCutRangeText(cut) : string.Empty;

    public bool HasCutSelectionDetail => HasSelectedCut;

    /// <summary>True when there are cuts and none is selected: the section then says how to select one.</summary>
    public bool IsCutHintVisible => Cuts.Count > 0 && !HasSelectedCut;

    public string CutHint => StudioEditorText.SelectCutHint;

    // The selected cut

    public string CutStartText => StudioEditorModel.GetSecondsText(SelectedCut?.Start ?? 0);

    public string CutStartAccessibleName => $"Start {CutStartText}";

    public string CutEndText => StudioEditorModel.GetSecondsText(SelectedCut?.End ?? 0);

    public string CutEndAccessibleName => $"End {CutEndText}";

    /// <summary>How long the selected cut is, such as "1.0 seconds long". Empty without one.</summary>
    public string CutLengthText => SelectedCut is { } cut ? StudioEditorText.GetCutLengthText(cut) : string.Empty;

    // Commands

    /// <summary>Lets go of whatever is selected, a zoom or a cut. The playhead stays.</summary>
    public void SelectNothing() => _session.SelectNothing();

    /// <summary>Selects a cut, or none. The playhead stays.</summary>
    public void SelectCut(int? index) => _session.SelectCut(index);

    /// <summary>Selects a cut and moves the playhead to where it starts.</summary>
    public bool SelectAndShowCut(int index) => _session.SelectAndShowCut(index);

    public bool SelectPreviousCut() => _session.SelectPreviousCut();

    public bool SelectNextCut() => _session.SelectNextCut();

    /// <summary>The Previous button of the Cut section. The cut it lands on is read out.</summary>
    public void ShowPreviousCut()
    {
        if (SelectPreviousCut())
        {
            AnnounceSelectedCut();
        }
    }

    /// <summary>The Next button of the Cut section. The cut it lands on is read out.</summary>
    public void ShowNextCut()
    {
        if (SelectNextCut())
        {
            AnnounceSelectedCut();
        }
    }

    /// <summary>Starts a cut at the playhead and says what came of it.</summary>
    public void AddCutAtPlayhead()
    {
        if (!IsEditable)
        {
            return;
        }

        var result = _session.AddCutAtPlayhead();
        if (result.Changed)
        {
            Announce(StudioEditorText.CutAddedMessage, CutActivityId, StudioAnnouncementKind.Completed);
        }
        else
        {
            Announce(
                result.Index is null ? StudioEditorText.NoRoomForCutMessage : StudioEditorText.CutAlreadyThereMessage,
                CutActivityId,
                StudioAnnouncementKind.Information);
        }
    }

    /// <summary>Deletes the selected cut, which puts its stretch back into the video.</summary>
    public void RemoveSelectedCut()
    {
        if (_session.RemoveSelectedCut().Changed)
        {
            Announce(StudioEditorText.CutDeletedMessage, CutActivityId, StudioAnnouncementKind.Completed);
        }
    }

    /// <summary>Moves a whole cut so it starts at a source time, for a drag on the lane.</summary>
    public StudioCutEditResult MoveCut(int index, double sourceTime) => _session.MoveCut(index, sourceTime);

    /// <summary>Moves a cut's start, for a drag on the lane. The playhead follows it.</summary>
    public StudioCutEditResult SetCutStart(int index, double sourceTime) => _session.SetCutStart(index, sourceTime);

    /// <summary>Moves a cut's end, for a drag on the lane. The playhead follows it.</summary>
    public StudioCutEditResult SetCutEnd(int index, double sourceTime) => _session.SetCutEnd(index, sourceTime);

    public void StepCutStartEarlier() => StepCutStart(-CutTimeStep);

    public void StepCutStartLater() => StepCutStart(CutTimeStep);

    public void StepCutEndEarlier() => StepCutEnd(-CutTimeStep);

    public void StepCutEndLater() => StepCutEnd(CutTimeStep);

    public void SetCutStartAtPlayhead()
    {
        if (SelectedCutIndex is { } index)
        {
            _session.SetCutStartAtPlayhead(index);
            AnnounceCutTime(CutStartAccessibleName);
        }
    }

    public void SetCutEndAtPlayhead()
    {
        if (SelectedCutIndex is { } index)
        {
            _session.SetCutEndAtPlayhead(index);
            AnnounceCutTime(CutEndAccessibleName);
        }
    }

    private void StepCutStart(double seconds)
    {
        if (SelectedCutIndex is { } index && SelectedCut is { } cut)
        {
            _session.SetCutStart(index, cut.Start + seconds);
            AnnounceCutTime(CutStartAccessibleName);
        }
    }

    private void StepCutEnd(double seconds)
    {
        if (SelectedCutIndex is { } index && SelectedCut is { } cut)
        {
            _session.SetCutEnd(index, cut.End + seconds);
            AnnounceCutTime(CutEndAccessibleName);
        }
    }

    // Previous and Next say nothing of where they land, and the text between them is not read
    // when it changes.
    private void AnnounceSelectedCut()
    {
        if (SelectedCutIndex is { } index && SelectedCut is { } cut)
        {
            Announce(StudioEditorText.GetCutStepText(index, Cuts.Count, cut), "StudioCutSelection", StudioAnnouncementKind.Information);
        }
    }

    // A button that moves a time says nothing by itself, and the time is written elsewhere. The
    // time it ended up with is said, which is also how a step that a neighboring cut stopped is
    // heard.
    private void AnnounceCutTime(string text) =>
        Announce(text, "StudioCutTime", StudioAnnouncementKind.Information);

    /// <summary>
    /// Whether a cut can be added, and whether there is one to step to, follow the playhead.
    /// Each is reported when it has changed, and not for every frame.
    /// </summary>
    private void RaiseCutStateAtPlayhead()
    {
        var addChanged = CanAddCutAtPlayhead != _couldAddCutAtPlayhead;
        var previousChanged = CanSelectPreviousCut != _couldSelectPreviousCut;
        var nextChanged = CanSelectNextCut != _couldSelectNextCut;
        RememberCutStateAtPlayhead();
        if (addChanged)
        {
            OnPropertyChanged(nameof(CanAddCutAtPlayhead));
        }

        if (previousChanged)
        {
            OnPropertyChanged(nameof(CanSelectPreviousCut));
        }

        if (nextChanged)
        {
            OnPropertyChanged(nameof(CanSelectNextCut));
        }
    }

    private void RememberCutStateAtPlayhead()
    {
        _couldAddCutAtPlayhead = CanAddCutAtPlayhead;
        _couldSelectPreviousCut = CanSelectPreviousCut;
        _couldSelectNextCut = CanSelectNextCut;
    }
}
