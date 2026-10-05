using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.ViewModels.Studio;

// The values behind the speed lane and the Speed section of the inspector. A speed change is
// selected in place of a zoom or a cut, never with one. What an edit does is decided in the
// session, and what is said about it in StudioEditorText.
public sealed partial class StudioViewModel
{
    private const string SpeedActivityId = "StudioSpeed";
    private const double SpeedTimeStep = 0.1;

    // What changes when another speed change is selected and the project stays as it is.
    private static readonly string[] SpeedSelectionPropertyNames =
    [
        nameof(SelectedSpeedIndex),
        nameof(HasSelectedSpeed),
        nameof(IsSpeedHintVisible),
        nameof(SpeedSelectionTitle),
        nameof(SpeedSelectionDetail),
        nameof(HasSpeedSelectionDetail),
        nameof(SpeedRateIndex),
        nameof(SpeedStartText),
        nameof(SpeedStartAccessibleName),
        nameof(SpeedEndText),
        nameof(SpeedEndAccessibleName),
        nameof(SpeedLengthText),
    ];

    private static readonly StudioSpeedRange[] NoSpeedChanges = [];

    // What was last reported of the three values that follow the playhead, so that a playhead that
    // moves thirty times a second only refreshes them when one has changed.
    private bool _couldAddSpeedAtPlayhead;
    private bool _couldSelectPreviousSpeed;
    private bool _couldSelectNextSpeed;

    // The lane

    /// <summary>
    /// The project's speed changes, in time order. It is the same list for as long as none of
    /// them changes, so whoever draws them can tell by reference that there is nothing to draw
    /// again.
    /// </summary>
    public IReadOnlyList<StudioSpeedRange> SpeedChanges => Project?.Edits.Speed ?? NoSpeedChanges;

    /// <summary>The selected speed change's place in <see cref="SpeedChanges"/>, or null.</summary>
    public int? SelectedSpeedIndex => _session.SelectedSpeedIndex;

    public bool HasSelectedSpeed => SelectedSpeedIndex is not null;

    public bool CanAddSpeedAtPlayhead => _session.CanAddSpeedAtPlayhead;

    private StudioSpeedRange? SelectedSpeed => _session.SelectedSpeed;

    // Stepping through the speed changes

    public bool CanSelectPreviousSpeed =>
        IsEditable && _session.Model?.GetSpeedIndexBefore(SelectedSpeedIndex, Playhead) is not null;

    public bool CanSelectNextSpeed =>
        IsEditable && _session.Model?.GetSpeedIndexAfter(SelectedSpeedIndex, Playhead) is not null;

    /// <summary>"Speed change 2 of 3" for the selected one, and otherwise how many there are.</summary>
    public string SpeedSelectionTitle
    {
        get
        {
            var count = SpeedChanges.Count;
            if (SelectedSpeedIndex is { } index)
            {
                return StudioEditorText.GetSpeedPositionText(index, count);
            }

            return count switch
            {
                0 => "No speed changes yet",
                1 => "1 speed change",
                _ => string.Create(CultureInfo.InvariantCulture, $"{count} speed changes"),
            };
        }
    }

    /// <summary>When the selected speed change starts and ends. Empty without one.</summary>
    public string SpeedSelectionDetail =>
        SelectedSpeed is { } speed ? StudioEditorText.GetSpeedRangeText(speed) : string.Empty;

    public bool HasSpeedSelectionDetail => HasSelectedSpeed;

    /// <summary>True when there are speed changes and none is selected: the section then says how to select one.</summary>
    public bool IsSpeedHintVisible => SpeedChanges.Count > 0 && !HasSelectedSpeed;

    public string SpeedHint => StudioEditorText.SelectSpeedHint;

    // The selected speed change

    /// <summary>
    /// Which of the editor's rates the selected speed change plays at, as its place in
    /// <see cref="StudioEditorModel.SpeedRates"/>. None, which is -1, without a selected speed
    /// change, and for one whose project file gave it another rate.
    /// </summary>
    public int SpeedRateIndex
    {
        get
        {
            if (SelectedSpeed is not { } speed)
            {
                return -1;
            }

            var rates = StudioEditorModel.SpeedRates;
            for (var index = 0; index < rates.Count; index++)
            {
                if (rates[index] == speed.Rate)
                {
                    return index;
                }
            }

            return -1;
        }

        set
        {
            // A two-way binding also hands back the rate the editor reported itself. Only another
            // rate is a choice, and only a choice that the editor took is read out: one that
            // would leave too little video is not made.
            var rates = StudioEditorModel.SpeedRates;
            if (value >= 0
                && value < rates.Count
                && value != SpeedRateIndex
                && SelectedSpeedIndex is { } index
                && _session.SetSpeedRate(index, rates[value]).Changed)
            {
                Announce(StudioEditorText.GetSpeedRateName(rates[value]), "StudioSpeedRate", StudioAnnouncementKind.Completed);
            }

            ResyncIfDifferent(value, SpeedRateIndex);
        }
    }

    public string SpeedStartText => StudioEditorModel.GetSecondsText(SelectedSpeed?.Start ?? 0);

    public string SpeedStartAccessibleName => $"Start {SpeedStartText}";

    public string SpeedEndText => StudioEditorModel.GetSecondsText(SelectedSpeed?.End ?? 0);

    public string SpeedEndAccessibleName => $"End {SpeedEndText}";

    /// <summary>
    /// How much of the recording the selected speed change covers and how long that plays, such
    /// as "4.5 seconds, plays in 2.3 seconds". Empty without one.
    /// </summary>
    public string SpeedLengthText =>
        SelectedSpeed is { } speed ? StudioEditorText.GetSpeedLengthText(speed) : string.Empty;

    /// <summary>What the section says of the sound of a stretch at another speed.</summary>
    public string SpeedSilentNote => StudioEditorText.SpeedSilentNote;

    // Commands

    /// <summary>Selects a speed change, or none. The playhead stays.</summary>
    public void SelectSpeed(int? index) => _session.SelectSpeed(index);

    /// <summary>Selects a speed change and moves the playhead to where it starts.</summary>
    public bool SelectAndShowSpeed(int index) => _session.SelectAndShowSpeed(index);

    public bool SelectPreviousSpeed() => _session.SelectPreviousSpeed();

    public bool SelectNextSpeed() => _session.SelectNextSpeed();

    /// <summary>The Previous button of the Speed section. The speed change it lands on is read out.</summary>
    public void ShowPreviousSpeed()
    {
        if (SelectPreviousSpeed())
        {
            AnnounceSelectedSpeed();
        }
    }

    /// <summary>The Next button of the Speed section. The speed change it lands on is read out.</summary>
    public void ShowNextSpeed()
    {
        if (SelectNextSpeed())
        {
            AnnounceSelectedSpeed();
        }
    }

    /// <summary>Starts a speed change at the playhead and says what came of it.</summary>
    public void AddSpeedAtPlayhead()
    {
        if (!IsEditable)
        {
            return;
        }

        var result = _session.AddSpeedAtPlayhead();
        if (result.Changed)
        {
            Announce(StudioEditorText.SpeedAddedMessage, SpeedActivityId, StudioAnnouncementKind.Completed);
        }
        else
        {
            Announce(
                result.Index is null ? StudioEditorText.NoRoomForSpeedMessage : StudioEditorText.SpeedAlreadyThereMessage,
                SpeedActivityId,
                StudioAnnouncementKind.Information);
        }
    }

    /// <summary>Deletes the selected speed change, so its stretch plays at the recording's own speed again.</summary>
    public void RemoveSelectedSpeed()
    {
        if (_session.RemoveSelectedSpeed().Changed)
        {
            Announce(StudioEditorText.SpeedDeletedMessage, SpeedActivityId, StudioAnnouncementKind.Completed);
        }
    }

    /// <summary>Moves a whole speed change so it starts at a source time, for a drag on the lane.</summary>
    public StudioSpeedEditResult MoveSpeed(int index, double sourceTime) => _session.MoveSpeed(index, sourceTime);

    /// <summary>Moves a speed change's start, for a drag on the lane. The playhead follows it.</summary>
    public StudioSpeedEditResult SetSpeedStart(int index, double sourceTime) => _session.SetSpeedStart(index, sourceTime);

    /// <summary>Moves a speed change's end, for a drag on the lane. The playhead follows it.</summary>
    public StudioSpeedEditResult SetSpeedEnd(int index, double sourceTime) => _session.SetSpeedEnd(index, sourceTime);

    public void StepSpeedStartEarlier() => StepSpeedStart(-SpeedTimeStep);

    public void StepSpeedStartLater() => StepSpeedStart(SpeedTimeStep);

    public void StepSpeedEndEarlier() => StepSpeedEnd(-SpeedTimeStep);

    public void StepSpeedEndLater() => StepSpeedEnd(SpeedTimeStep);

    public void SetSpeedStartAtPlayhead()
    {
        if (SelectedSpeedIndex is { } index)
        {
            _session.SetSpeedStartAtPlayhead(index);
            AnnounceSpeedTime(SpeedStartAccessibleName);
        }
    }

    public void SetSpeedEndAtPlayhead()
    {
        if (SelectedSpeedIndex is { } index)
        {
            _session.SetSpeedEndAtPlayhead(index);
            AnnounceSpeedTime(SpeedEndAccessibleName);
        }
    }

    private void StepSpeedStart(double seconds)
    {
        if (SelectedSpeedIndex is { } index && SelectedSpeed is { } speed)
        {
            _session.SetSpeedStart(index, speed.Start + seconds);
            AnnounceSpeedTime(SpeedStartAccessibleName);
        }
    }

    private void StepSpeedEnd(double seconds)
    {
        if (SelectedSpeedIndex is { } index && SelectedSpeed is { } speed)
        {
            _session.SetSpeedEnd(index, speed.End + seconds);
            AnnounceSpeedTime(SpeedEndAccessibleName);
        }
    }

    // Previous and Next say nothing of where they land, and the text between them is not read
    // when it changes.
    private void AnnounceSelectedSpeed()
    {
        if (SelectedSpeedIndex is { } index && SelectedSpeed is { } speed)
        {
            Announce(
                StudioEditorText.GetSpeedStepText(index, SpeedChanges.Count, speed),
                "StudioSpeedSelection",
                StudioAnnouncementKind.Information);
        }
    }

    // A button that moves a time says nothing by itself, and the time is written elsewhere. The
    // time it ended up with is said, which is also how a step that a neighboring speed change
    // stopped is heard.
    private void AnnounceSpeedTime(string text) =>
        Announce(text, "StudioSpeedTime", StudioAnnouncementKind.Information);

    /// <summary>
    /// Whether a speed change can be added, and whether there is one to step to, follow the
    /// playhead. Each is reported when it has changed, and not for every frame.
    /// </summary>
    private void RaiseSpeedStateAtPlayhead()
    {
        var addChanged = CanAddSpeedAtPlayhead != _couldAddSpeedAtPlayhead;
        var previousChanged = CanSelectPreviousSpeed != _couldSelectPreviousSpeed;
        var nextChanged = CanSelectNextSpeed != _couldSelectNextSpeed;
        RememberSpeedStateAtPlayhead();
        if (addChanged)
        {
            OnPropertyChanged(nameof(CanAddSpeedAtPlayhead));
        }

        if (previousChanged)
        {
            OnPropertyChanged(nameof(CanSelectPreviousSpeed));
        }

        if (nextChanged)
        {
            OnPropertyChanged(nameof(CanSelectNextSpeed));
        }
    }

    private void RememberSpeedStateAtPlayhead()
    {
        _couldAddSpeedAtPlayhead = CanAddSpeedAtPlayhead;
        _couldSelectPreviousSpeed = CanSelectPreviousSpeed;
        _couldSelectNextSpeed = CanSelectNextSpeed;
    }
}
