using System.Globalization;
using System.Runtime.CompilerServices;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.ViewModels.Studio;

// The values behind the zoom lane, the Zoom section of the inspector and the crop sliders. What an
// edit does is decided in the session, and what is said about it in StudioEditorText.
public sealed partial class StudioViewModel
{
    private const string ZoomActivityId = "StudioZoom";
    private const double ZoomTimeStep = 0.1;

    // How far a value the session answered with may be from the one a slider asked for and still
    // be the same value.
    private const double ValueTolerance = 1e-9;

    // What changes when another zoom is selected, or none because a cut is, and the project stays as it is.
    private static readonly string[] ZoomSelectionPropertyNames =
    [
        nameof(SelectedZoomIndex),
        nameof(HasSelectedZoom),
        nameof(IsZoomHintVisible),
        nameof(ZoomSelectionTitle),
        nameof(ZoomSelectionDetail),
        nameof(HasZoomSelectionDetail),
        nameof(ZoomScale),
        nameof(ZoomScaleText),
        nameof(ZoomFocusModeIndex),
        nameof(CanZoomFollowPointer),
        nameof(IsPointerExplanationVisible),
        nameof(AreZoomFocusControlsVisible),
        nameof(ZoomPad),
        nameof(ZoomFocusX),
        nameof(ZoomFocusXText),
        nameof(ZoomFocusY),
        nameof(ZoomFocusYText),
        nameof(ZoomStartText),
        nameof(ZoomStartAccessibleName),
        nameof(ZoomEndText),
        nameof(ZoomEndAccessibleName),
        nameof(ZoomEaseIn),
        nameof(ZoomEaseInText),
        nameof(ZoomEaseOut),
        nameof(ZoomEaseOutText),
    ];

    private static readonly StudioZoom[] NoZooms = [];

    // What was last reported of the three values that follow the playhead, so that a playhead that
    // moves thirty times a second only refreshes them when one has changed.
    private bool _couldAddZoomAtPlayhead;
    private bool _couldSelectPreviousZoom;
    private bool _couldSelectNextZoom;

    // The lane

    /// <summary>
    /// The project's zooms, in time order. It is the same list for as long as no zoom changes, so
    /// whoever draws them can tell by reference that there is nothing to draw again.
    /// </summary>
    public IReadOnlyList<StudioZoom> Zooms => Project?.Zooms ?? NoZooms;

    /// <summary>The selected zoom's place in <see cref="Zooms"/>, or null.</summary>
    public int? SelectedZoomIndex => _session.SelectedZoomIndex;

    public bool HasSelectedZoom => SelectedZoomIndex is not null;

    public bool CanAddZoomAtPlayhead => _session.CanAddZoomAtPlayhead;

    private StudioZoom? SelectedZoom => _session.SelectedZoom;

    // Stepping through the zooms

    public bool CanSelectPreviousZoom =>
        IsEditable && _session.Model?.GetZoomIndexBefore(SelectedZoomIndex, Playhead) is not null;

    public bool CanSelectNextZoom =>
        IsEditable && _session.Model?.GetZoomIndexAfter(SelectedZoomIndex, Playhead) is not null;

    /// <summary>"Zoom 2 of 5" for the selected zoom, and otherwise how many zooms there are.</summary>
    public string ZoomSelectionTitle
    {
        get
        {
            var count = Zooms.Count;
            if (SelectedZoomIndex is { } index)
            {
                return StudioEditorText.GetZoomPositionText(index, count);
            }

            return count switch
            {
                0 => "No zooms yet",
                1 => "1 zoom",
                _ => string.Create(CultureInfo.InvariantCulture, $"{count} zooms"),
            };
        }
    }

    /// <summary>When the selected zoom starts and ends, and whether it is a suggestion. Empty without one.</summary>
    public string ZoomSelectionDetail => SelectedZoom is { } zoom
        ? StudioEditorText.GetZoomRangeText(zoom) + (zoom.Origin == StudioZoomOrigin.Auto ? ", suggested" : string.Empty)
        : string.Empty;

    public bool HasZoomSelectionDetail => HasSelectedZoom;

    /// <summary>True when there are zooms and none is selected: the section then says how to select one.</summary>
    public bool IsZoomHintVisible => Zooms.Count > 0 && !HasSelectedZoom;

    // Suggestions

    public bool HasClicks => _session.HasClicks;

    public bool CanSuggestZooms => IsEditable && HasClicks;

    /// <summary>True for an open recording without clicks, such as one of a window.</summary>
    public bool IsClicksExplanationVisible => IsReady && !HasClicks;

    public string ClicksExplanation => StudioEditorText.NoClicksExplanation;

    /// <summary>What Suggest zooms does, or why it cannot.</summary>
    public string SuggestZoomsHelpText => IsClicksExplanationVisible ? StudioEditorText.NoClicksExplanation : "Add zooms where you clicked";

    public bool HasSuggestedZooms => _session.Model is { SuggestedZoomCount: > 0 };

    // The selected zoom

    public double ZoomScale
    {
        get => Math.Clamp(SelectedZoom?.Scale ?? 1, 1, 5);
        set
        {
            if (SelectedZoomIndex is { } index)
            {
                _session.SetZoomScale(index, value);
            }
        }
    }

    public string ZoomScaleText => StudioEditorText.GetZoomScaleText(SelectedZoom?.Scale ?? 1);

    /// <summary>0 for a point, 1 for the pointer.</summary>
    public int ZoomFocusModeIndex
    {
        get => SelectedZoom?.Focus.Mode == StudioZoomFocusMode.Cursor ? 1 : 0;
        set
        {
            if (value is 0 or 1 && SelectedZoomIndex is { } index && value != ZoomFocusModeIndex)
            {
                _session.SetZoomFocusMode(index, value == 1 ? StudioZoomFocusMode.Cursor : StudioZoomFocusMode.Point);
            }

            ResyncIfDifferent(value, ZoomFocusModeIndex);
        }
    }

    /// <summary>
    /// Whether "The pointer" can be chosen. A zoom that already follows the pointer keeps the
    /// choice, so that it can be seen and changed back.
    /// </summary>
    public bool CanZoomFollowPointer =>
        _session.HasPointerPositions || SelectedZoom?.Focus.Mode == StudioZoomFocusMode.Cursor;

    public bool IsPointerExplanationVisible => HasSelectedZoom && !_session.HasPointerPositions;

    public string PointerExplanation => StudioEditorText.NoPointerExplanation;

    /// <summary>Why "The pointer" cannot be chosen, for a screen reader. Empty when it can.</summary>
    public string ZoomFollowPointerHelpText => _session.HasPointerPositions ? string.Empty : StudioEditorText.NoPointerExplanation;

    /// <summary>The focus pad and its two sliders show for a zoom that looks at a point.</summary>
    public bool AreZoomFocusControlsVisible => SelectedZoom is { Focus.Mode: StudioZoomFocusMode.Point };

    /// <summary>What the focus pad shows for the selected zoom, or null without one.</summary>
    public StudioZoomPad? ZoomPad => SelectedZoomIndex is { } index ? _session.Model?.GetZoomPad(index) : null;

    /// <summary>Where the selected zoom looks, from 0 at the left edge of the focus pad to 1 at its right.</summary>
    public double ZoomFocusX
    {
        get => ZoomPad?.FocusX ?? 0.5;
        set => SetZoomFocusOnPad(value, ZoomFocusY);
    }

    public string ZoomFocusXText => StudioEditorText.GetPercentText(ZoomFocusX);

    public double ZoomFocusY
    {
        get => ZoomPad?.FocusY ?? 0.5;
        set => SetZoomFocusOnPad(ZoomFocusX, value);
    }

    public string ZoomFocusYText => StudioEditorText.GetPercentText(ZoomFocusY);

    public string ZoomStartText => StudioEditorModel.GetSecondsText(SelectedZoom?.Start ?? 0);

    public string ZoomStartAccessibleName => $"Start {ZoomStartText}";

    public string ZoomEndText => StudioEditorModel.GetSecondsText(SelectedZoom?.End ?? 0);

    public string ZoomEndAccessibleName => $"End {ZoomEndText}";

    public double ZoomEaseIn
    {
        get => Math.Clamp(SelectedZoom?.EaseIn ?? 0, 0, 3);
        set
        {
            if (SelectedZoomIndex is { } index)
            {
                _session.SetZoomEaseIn(index, value);
            }
        }
    }

    public string ZoomEaseInText => StudioEditorModel.GetSecondsText(ZoomEaseIn);

    public double ZoomEaseOut
    {
        get => Math.Clamp(SelectedZoom?.EaseOut ?? 0, 0, 3);
        set
        {
            if (SelectedZoomIndex is { } index)
            {
                _session.SetZoomEaseOut(index, value);
            }
        }
    }

    public string ZoomEaseOutText => StudioEditorModel.GetSecondsText(ZoomEaseOut);

    // Crops

    private StudioCropInsets ScreenCrop => _session.Model?.ScreenCropInsets ?? default;

    private StudioCropInsets CameraCrop => _session.Model?.CameraCropInsets ?? default;

    public double ScreenCropLeft
    {
        get => ScreenCrop.Left;
        set => SetScreenCrop(StudioCropEdge.Left, value);
    }

    public string ScreenCropLeftText => StudioEditorText.GetPercentText(ScreenCrop.Left);

    public double ScreenCropTop
    {
        get => ScreenCrop.Top;
        set => SetScreenCrop(StudioCropEdge.Top, value);
    }

    public string ScreenCropTopText => StudioEditorText.GetPercentText(ScreenCrop.Top);

    public double ScreenCropRight
    {
        get => ScreenCrop.Right;
        set => SetScreenCrop(StudioCropEdge.Right, value);
    }

    public string ScreenCropRightText => StudioEditorText.GetPercentText(ScreenCrop.Right);

    public double ScreenCropBottom
    {
        get => ScreenCrop.Bottom;
        set => SetScreenCrop(StudioCropEdge.Bottom, value);
    }

    public string ScreenCropBottomText => StudioEditorText.GetPercentText(ScreenCrop.Bottom);

    public bool CanResetScreenCrop => !ScreenCrop.IsEmpty;

    public double CameraCropLeft
    {
        get => CameraCrop.Left;
        set => SetCameraCrop(StudioCropEdge.Left, value);
    }

    public string CameraCropLeftText => StudioEditorText.GetPercentText(CameraCrop.Left);

    public double CameraCropTop
    {
        get => CameraCrop.Top;
        set => SetCameraCrop(StudioCropEdge.Top, value);
    }

    public string CameraCropTopText => StudioEditorText.GetPercentText(CameraCrop.Top);

    public double CameraCropRight
    {
        get => CameraCrop.Right;
        set => SetCameraCrop(StudioCropEdge.Right, value);
    }

    public string CameraCropRightText => StudioEditorText.GetPercentText(CameraCrop.Right);

    public double CameraCropBottom
    {
        get => CameraCrop.Bottom;
        set => SetCameraCrop(StudioCropEdge.Bottom, value);
    }

    public string CameraCropBottomText => StudioEditorText.GetPercentText(CameraCrop.Bottom);

    public bool CanResetCameraCrop => !CameraCrop.IsEmpty;

    // Commands

    /// <summary>Selects a zoom, or none. The playhead stays.</summary>
    public void SelectZoom(int? index) => _session.SelectZoom(index);

    /// <summary>Selects a zoom and moves the playhead to where it has moved in.</summary>
    public bool SelectAndShowZoom(int index) => _session.SelectAndShowZoom(index);

    public bool SelectPreviousZoom() => _session.SelectPreviousZoom();

    public bool SelectNextZoom() => _session.SelectNextZoom();

    /// <summary>The Previous button of the Zoom section. The zoom it lands on is read out.</summary>
    public void ShowPreviousZoom()
    {
        if (SelectPreviousZoom())
        {
            AnnounceSelectedZoom();
        }
    }

    /// <summary>The Next button of the Zoom section. The zoom it lands on is read out.</summary>
    public void ShowNextZoom()
    {
        if (SelectNextZoom())
        {
            AnnounceSelectedZoom();
        }
    }

    /// <summary>Adds a zoom at the playhead and says what came of it.</summary>
    public void AddZoomAtPlayhead()
    {
        if (!IsEditable)
        {
            return;
        }

        var result = _session.AddZoomAtPlayhead();
        if (result.Changed)
        {
            Announce(StudioEditorText.ZoomAddedMessage, ZoomActivityId, StudioAnnouncementKind.Completed);
        }
        else
        {
            Announce(
                result.Index is null ? StudioEditorText.NoRoomForZoomMessage : StudioEditorText.ZoomAlreadyThereMessage,
                ZoomActivityId,
                StudioAnnouncementKind.Information);
        }
    }

    public void RemoveSelectedZoom()
    {
        if (_session.RemoveSelectedZoom().Changed)
        {
            Announce(StudioEditorText.ZoomDeletedMessage, ZoomActivityId, StudioAnnouncementKind.Completed);
        }
    }

    /// <summary>Replaces the suggested zooms with new ones worked out from the clicks, and says how many there are.</summary>
    public void SuggestZooms()
    {
        if (!CanSuggestZooms || _session.Model is not { } model)
        {
            return;
        }

        _session.ApplyZoomSuggestions();
        Announce(StudioEditorText.GetZoomSuggestionsText(model.SuggestedZoomCount), ZoomActivityId, StudioAnnouncementKind.Completed);
    }

    public void RemoveZoomSuggestions()
    {
        if (_session.RemoveZoomSuggestions())
        {
            Announce(StudioEditorText.ZoomSuggestionsRemovedMessage, ZoomActivityId, StudioAnnouncementKind.Completed);
        }
    }

    /// <summary>Moves a whole zoom so it starts at a source time, for a drag on the lane.</summary>
    public StudioZoomEditResult MoveZoom(int index, double sourceTime) => _session.MoveZoom(index, sourceTime);

    /// <summary>
    /// Moves a zoom's start, for a drag on the lane, and shows the frame it now starts on, as a
    /// trim handle does.
    /// </summary>
    public StudioZoomEditResult DragZoomStart(int index, double sourceTime)
    {
        var result = _session.SetZoomStart(index, sourceTime);
        if (result.Index is { } now && now < Zooms.Count)
        {
            _session.Scrub(Zooms[now].Start);
        }

        return result;
    }

    /// <summary>Moves a zoom's end, for a drag on the lane, and shows the picture at the new end.</summary>
    public StudioZoomEditResult DragZoomEnd(int index, double sourceTime)
    {
        var result = _session.SetZoomEnd(index, sourceTime);
        if (result.Index is { } now && now < Zooms.Count)
        {
            _session.Scrub(Zooms[now].End);
        }

        return result;
    }

    public void StepZoomStartEarlier() => StepZoomStart(-ZoomTimeStep);

    public void StepZoomStartLater() => StepZoomStart(ZoomTimeStep);

    public void StepZoomEndEarlier() => StepZoomEnd(-ZoomTimeStep);

    public void StepZoomEndLater() => StepZoomEnd(ZoomTimeStep);

    public void SetZoomStartAtPlayhead()
    {
        if (SelectedZoomIndex is { } index)
        {
            _session.SetZoomStart(index, Playhead);
            AnnounceZoomTime(ZoomStartAccessibleName);
        }
    }

    public void SetZoomEndAtPlayhead()
    {
        if (SelectedZoomIndex is { } index)
        {
            _session.SetZoomEnd(index, Playhead);
            AnnounceZoomTime(ZoomEndAccessibleName);
        }
    }

    /// <summary>Points the selected zoom at a place on its focus pad, from 0 to 1 across and down.</summary>
    public void SetZoomFocusOnPad(double x, double y)
    {
        if (SelectedZoomIndex is { } index)
        {
            _session.SetZoomFocusOnPad(index, x, y);
        }
    }

    public void ResetScreenCrop() => _session.ClearScreenCrop();

    public void ResetCameraCrop() => _session.ClearCameraCrop();

    private void StepZoomStart(double seconds)
    {
        if (SelectedZoomIndex is { } index && SelectedZoom is { } zoom)
        {
            _session.SetZoomStart(index, zoom.Start + seconds);
            AnnounceZoomTime(ZoomStartAccessibleName);
        }
    }

    private void StepZoomEnd(double seconds)
    {
        if (SelectedZoomIndex is { } index && SelectedZoom is { } zoom)
        {
            _session.SetZoomEnd(index, zoom.End + seconds);
            AnnounceZoomTime(ZoomEndAccessibleName);
        }
    }

    // Previous and Next say nothing of where they land, and the text between them is not read
    // when it changes. On the lane the arrow keys need none of this: there the list says which
    // of its items is now the selected one.
    private void AnnounceSelectedZoom()
    {
        if (SelectedZoomIndex is { } index && SelectedZoom is { } zoom)
        {
            Announce(StudioEditorText.GetZoomStepText(index, Zooms.Count, zoom), "StudioZoomSelection", StudioAnnouncementKind.Information);
        }
    }

    // A button that moves a time says nothing by itself, and the time is written elsewhere. The
    // time it ended up with is said, which is also how a step that a neighbouring zoom stopped
    // is heard.
    private void AnnounceZoomTime(string text) =>
        Announce(text, "StudioZoomTime", StudioAnnouncementKind.Information);

    private void SetScreenCrop(StudioCropEdge edge, double value, [CallerMemberName] string? propertyName = null)
    {
        _session.SetScreenCropInset(edge, value);
        ResyncIfApart(value, ScreenCrop[edge], propertyName);
    }

    private void SetCameraCrop(StudioCropEdge edge, double value, [CallerMemberName] string? propertyName = null)
    {
        _session.SetCameraCropInset(edge, value);
        ResyncIfApart(value, CameraCrop[edge], propertyName);
    }

    /// <summary>
    /// A crop edge stops where the opposite edge leaves it no room, and a slider that was asked
    /// for more keeps showing what it was asked for. This puts the slider back on the real value.
    /// </summary>
    private void ResyncIfApart(double requested, double actual, string? propertyName)
    {
        if (Math.Abs(requested - actual) > ValueTolerance && propertyName is not null)
        {
            _dispatcher.TryEnqueue(() => OnPropertyChanged(propertyName));
        }
    }

    // A zoom, a cut or a speed change is selected, never two of them, so selecting one can let go
    // of another: what follows each of the three selections is refreshed.
    private void RaiseSelectionChanged()
    {
        Raise(ZoomSelectionPropertyNames);
        Raise(CutSelectionPropertyNames);
        Raise(SpeedSelectionPropertyNames);
        RaiseZoomStateAtPlayhead();
        RaiseCutStateAtPlayhead();
        RaiseSpeedStateAtPlayhead();
    }

    /// <summary>
    /// Whether a zoom can be added, and whether there is one to step to, follow the playhead.
    /// Each is reported when it has changed, and not for every frame.
    /// </summary>
    private void RaiseZoomStateAtPlayhead()
    {
        var canAdd = CanAddZoomAtPlayhead;
        var canSelectPrevious = CanSelectPreviousZoom;
        var canSelectNext = CanSelectNextZoom;
        var addChanged = canAdd != _couldAddZoomAtPlayhead;
        var previousChanged = canSelectPrevious != _couldSelectPreviousZoom;
        var nextChanged = canSelectNext != _couldSelectNextZoom;
        RememberZoomStateAtPlayhead();
        if (addChanged)
        {
            OnPropertyChanged(nameof(CanAddZoomAtPlayhead));
        }

        if (previousChanged)
        {
            OnPropertyChanged(nameof(CanSelectPreviousZoom));
        }

        if (nextChanged)
        {
            OnPropertyChanged(nameof(CanSelectNextZoom));
        }
    }

    private void RememberZoomStateAtPlayhead()
    {
        _couldAddZoomAtPlayhead = CanAddZoomAtPlayhead;
        _couldSelectPreviousZoom = CanSelectPreviousZoom;
        _couldSelectNextZoom = CanSelectNextZoom;
    }
}
