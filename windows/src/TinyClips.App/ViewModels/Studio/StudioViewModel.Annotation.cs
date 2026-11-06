using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using Windows.UI;

namespace TinyClips.App.ViewModels.Studio;

// The values behind the annotation lane, the Annotate panel of the inspector and the tools in the
// header. An annotation is selected in place of a zoom, a cut or a speed change, never with one. What
// an edit does is decided in the session, and what is said about it in StudioEditorText.
public sealed partial class StudioViewModel
{
    private const string AnnotationActivityId = "StudioAnnotation";
    private const double AnnotationTimeStep = 0.1;

    private static readonly StudioAnnotation[] NoAnnotations = [];

    // What changes when another annotation is selected and the project stays as it is.
    private static readonly string[] AnnotationSelectionPropertyNames =
    [
        nameof(SelectedAnnotationIndex),
        nameof(HasSelectedAnnotation),
        nameof(IsAnnotationHintVisible),
        nameof(AnnotationSelectionTitle),
        nameof(AnnotationSelectionDetail),
        nameof(AnnotationText),
        nameof(HasAnnotationText),
        nameof(HasAnnotationStyle),
        nameof(HasAnnotationOffset),
        nameof(IsAnnotationNotDrawn),
        nameof(AnnotationOffsetXTitle),
        nameof(AnnotationOffsetYTitle),
        nameof(AnnotationHandles),
        nameof(AnnotationFillTitle),
        nameof(AnnotationFillColor),
        nameof(AnnotationTextColor),
        nameof(HasAnnotationTextColor),
        nameof(AnnotationStyleIndex),
        nameof(HasCustomAnnotationColors),
        nameof(AnnotationScale),
        nameof(AnnotationScaleText),
        nameof(AnnotationAttachIndex),
        nameof(AnnotationX),
        nameof(AnnotationXText),
        nameof(AnnotationY),
        nameof(AnnotationYText),
        nameof(AnnotationOffsetX),
        nameof(AnnotationOffsetXText),
        nameof(AnnotationOffsetY),
        nameof(AnnotationOffsetYText),
        nameof(AnnotationStartText),
        nameof(AnnotationStartAccessibleName),
        nameof(AnnotationEndText),
        nameof(AnnotationEndAccessibleName),
        nameof(CanBringAnnotationForward),
        nameof(CanSendAnnotationBack),
    ];

    private bool _couldSelectPreviousAnnotation;
    private bool _couldSelectNextAnnotation;

    /// <summary>The project's annotations, bottom first. The same list for as long as none of them changes.</summary>
    public IReadOnlyList<StudioAnnotation> Annotations => Project?.Overlays.Annotations ?? NoAnnotations;

    public int? SelectedAnnotationIndex => _session.SelectedAnnotationIndex;

    public bool HasSelectedAnnotation => SelectedAnnotationIndex is not null;

    public bool CanAddAnnotation => _session.CanAddAnnotation;

    private StudioAnnotation? SelectedAnnotation => _session.SelectedAnnotation;

    public bool CanSelectPreviousAnnotation =>
        IsEditable && _session.Model?.GetAnnotationIndexBefore(SelectedAnnotationIndex, Playhead) is not null;

    public bool CanSelectNextAnnotation =>
        IsEditable && _session.Model?.GetAnnotationIndexAfter(SelectedAnnotationIndex, Playhead) is not null;

    /// <summary>"Annotation 2 of 3" for the selected one, and otherwise how many there are.</summary>
    public string AnnotationSelectionTitle
    {
        get
        {
            var count = Annotations.Count;
            if (SelectedAnnotationIndex is { } index)
            {
                return StudioEditorText.GetAnnotationPositionText(index, count);
            }

            return count switch
            {
                0 => "No annotations yet",
                1 => "1 annotation",
                _ => string.Create(CultureInfo.InvariantCulture, $"{count} annotations"),
            };
        }
    }

    /// <summary>What the selected annotation is and when it shows. Empty without one.</summary>
    public string AnnotationSelectionDetail =>
        SelectedAnnotation is { } annotation ? StudioEditorText.GetAnnotationDescription(annotation) : string.Empty;

    public bool IsAnnotationHintVisible => Annotations.Count > 0 && !HasSelectedAnnotation;

    public string AnnotationHint => StudioEditorText.SelectAnnotationHint;

    public bool IsAnnotationEmptyHintVisible => IsReady && Annotations.Count == 0;

    public string AnnotationEmptyHint => StudioEditorText.AnnotationEmptyHint;

    public string AnnotationNotDrawnNote => StudioEditorText.AnnotationNotDrawnNote;

    // Which controls the selected annotation has

    private bool IsSelectedKnown =>
        SelectedAnnotation?.Kind is StudioAnnotationKinds.Arrow or StudioAnnotationKinds.Callout or StudioAnnotationKinds.Text;

    public bool HasAnnotationStyle => IsSelectedKnown;

    public bool IsAnnotationNotDrawn => HasSelectedAnnotation && !IsSelectedKnown;

    public bool HasAnnotationText => SelectedAnnotation?.Kind is StudioAnnotationKinds.Callout or StudioAnnotationKinds.Text;

    public bool HasAnnotationOffset => SelectedAnnotation?.Kind is StudioAnnotationKinds.Arrow or StudioAnnotationKinds.Callout;

    public string AnnotationOffsetXTitle => SelectedAnnotation?.Kind == StudioAnnotationKinds.Callout ? "Box horizontal" : "Tail horizontal";

    public string AnnotationOffsetYTitle => SelectedAnnotation?.Kind == StudioAnnotationKinds.Callout ? "Box vertical" : "Tail vertical";

    // The selected annotation

    /// <summary>What it says, with \n for a new line. The text box is kept in step in the inspector.</summary>
    public string AnnotationText
    {
        get => SelectedAnnotation?.Text ?? string.Empty;
        set
        {
            // The box hands back what it was given, with line breaks of its own: that is no edit.
            var text = value.ReplaceLineEndings("\n");
            if (SelectedAnnotationIndex is { } index && text != AnnotationText.ReplaceLineEndings("\n"))
            {
                _session.SetAnnotationText(index, text);
            }
        }
    }

    /// <summary>The text box has lost the focus: what is typed from now on is another undo step.</summary>
    public void EndAnnotationTyping() => _session.EndAnnotationTyping();

    /// <summary>The selected annotation's style, or -1 with none selected.</summary>
    public int AnnotationStyleIndex => SelectedAnnotation is { } annotation ? (int)annotation.Style : -1;

    /// <summary>What is on the picture at the playhead, for the preview to let a pointer take hold of.</summary>
    public IReadOnlyList<StudioAnnotationHandle> AnnotationHandles => _session.GetAnnotationHandles(CanvasWidth, CanvasHeight);

    /// <summary>Selects an annotation pressed on the preview, and hands back what it is before it is dragged.</summary>
    public StudioAnnotation? BeginAnnotationDrag(int index)
    {
        SelectAnnotation(index);
        return SelectedAnnotation;
    }

    /// <summary>Where a drag on the preview leaves an annotation. See <see cref="StudioEditorSession.DragAnnotation"/>.</summary>
    public StudioAnnotationMove? DragAnnotation(int index, StudioAnnotation start, StudioAnnotationPart part, double byX, double byY) =>
        _session.DragAnnotation(index, start, part, byX, byY, CanvasWidth, CanvasHeight);

    /// <summary>The look to start from. Choosing one takes back the colors chosen by hand.</summary>
    public void SetAnnotationStyle(StudioAnnotationStyle style)
    {
        if (SelectedAnnotationIndex is { } index)
        {
            _session.SetAnnotationStyle(index, style);
        }
    }

    public string AnnotationFillTitle => SelectedAnnotation?.Kind == StudioAnnotationKinds.Arrow ? "Color" : "Fill";

    public bool HasAnnotationTextColor => SelectedAnnotation?.Kind is StudioAnnotationKinds.Callout or StudioAnnotationKinds.Text;

    /// <summary>Whether the selected annotation has a color chosen by hand, so that no style is what it looks like.</summary>
    public bool HasCustomAnnotationColors =>
        SelectedAnnotation is { } annotation
        && (StudioAnnotationGeometry.GetCustomColor(annotation.Fill) is not null || StudioAnnotationGeometry.GetCustomColor(annotation.TextColor) is not null);

    public Color AnnotationFillColor =>
        ParseBackgroundColor(SelectedAnnotation is { } annotation ? StudioAnnotationGeometry.GetEffectiveColors(annotation).Fill : "#FFFFFF");

    public Color AnnotationTextColor =>
        ParseBackgroundColor(SelectedAnnotation is { } annotation ? StudioAnnotationGeometry.GetEffectiveColors(annotation).Text : "#FFFFFF");

    public void SetAnnotationFillColor(Color color)
    {
        if (SelectedAnnotationIndex is { } index)
        {
            _session.SetAnnotationFill(index, ToHex(color));
        }
    }

    public void SetAnnotationTextColor(Color color)
    {
        if (SelectedAnnotationIndex is { } index)
        {
            _session.SetAnnotationTextColor(index, ToHex(color));
        }
    }

    public double AnnotationScale
    {
        get => Math.Clamp(SelectedAnnotation?.Scale ?? 1, StudioAnnotationGeometry.MinimumScale, StudioAnnotationGeometry.MaximumScale);
        set
        {
            if (SelectedAnnotationIndex is { } index && IsRequest(value, AnnotationScale))
            {
                _session.SetAnnotationScale(index, value);
            }
        }
    }

    public string AnnotationScaleText => PercentText(AnnotationScale);

    public int AnnotationAttachIndex
    {
        get => SelectedAnnotation is { } annotation ? (int)annotation.Attach : -1;
        set
        {
            if (value >= 0 && value != AnnotationAttachIndex && SelectedAnnotationIndex is { } index && Enum.IsDefined((StudioAnnotationAttach)value))
            {
                _session.SetAnnotationAttach(index, (StudioAnnotationAttach)value);
            }

            ResyncIfDifferent(value, AnnotationAttachIndex);
        }
    }

    public double AnnotationX
    {
        get => Math.Clamp(SelectedAnnotation?.X ?? 0, 0, 1);
        set
        {
            if (SelectedAnnotationIndex is { } index && IsRequest(value, AnnotationX))
            {
                _session.SetAnnotationPoint(index, value, AnnotationY);
            }
        }
    }

    public string AnnotationXText => PercentText(AnnotationX);

    public double AnnotationY
    {
        get => Math.Clamp(SelectedAnnotation?.Y ?? 0, 0, 1);
        set
        {
            if (SelectedAnnotationIndex is { } index && IsRequest(value, AnnotationY))
            {
                _session.SetAnnotationPoint(index, AnnotationX, value);
            }
        }
    }

    public string AnnotationYText => PercentText(AnnotationY);

    public double AnnotationOffsetX
    {
        get => Math.Clamp(SelectedAnnotation?.OffsetX ?? 0, -1, 1);
        set
        {
            if (SelectedAnnotationIndex is { } index && IsRequest(value, AnnotationOffsetX))
            {
                _session.SetAnnotationOffset(index, value, AnnotationOffsetY);
            }
        }
    }

    public string AnnotationOffsetXText => PercentText(AnnotationOffsetX);

    public double AnnotationOffsetY
    {
        get => Math.Clamp(SelectedAnnotation?.OffsetY ?? 0, -1, 1);
        set
        {
            if (SelectedAnnotationIndex is { } index && IsRequest(value, AnnotationOffsetY))
            {
                _session.SetAnnotationOffset(index, AnnotationOffsetX, value);
            }
        }
    }

    public string AnnotationOffsetYText => PercentText(AnnotationOffsetY);

    public string AnnotationStartText => StudioEditorModel.GetSecondsText(SelectedAnnotation?.Start ?? 0);

    public string AnnotationStartAccessibleName => $"Start {AnnotationStartText}";

    public string AnnotationEndText => StudioEditorModel.GetSecondsText(SelectedAnnotation?.End ?? 0);

    public string AnnotationEndAccessibleName => $"End {AnnotationEndText}";

    public bool CanBringAnnotationForward => SelectedAnnotationIndex is { } index && index + 1 < Annotations.Count;

    public bool CanSendAnnotationBack => SelectedAnnotationIndex is > 0;

    // Commands

    public void SelectAnnotation(int? index) => _session.SelectAnnotation(index);

    /// <summary>
    /// Selects an annotation from the panel's list, and moves the playhead to where it starts when
    /// it would not be on the picture otherwise.
    /// </summary>
    public void SelectAnnotationInView(int index)
    {
        if (index < 0 || index >= Annotations.Count)
        {
            return;
        }

        var annotation = Annotations[index];
        if (annotation.Start <= Playhead && Playhead < annotation.End)
        {
            SelectAnnotation(index);
        }
        else
        {
            SelectAndShowAnnotation(index);
        }
    }

    public bool SelectAndShowAnnotation(int index) => _session.SelectAndShowAnnotation(index);

    public bool SelectPreviousAnnotation() => _session.SelectPreviousAnnotation();

    public bool SelectNextAnnotation() => _session.SelectNextAnnotation();

    public void ShowPreviousAnnotation()
    {
        if (SelectPreviousAnnotation())
        {
            AnnounceSelectedAnnotation();
        }
    }

    public void ShowNextAnnotation()
    {
        if (SelectNextAnnotation())
        {
            AnnounceSelectedAnnotation();
        }
    }

    public void AddArrowAtPlayhead() => AddAnnotationAtPlayhead(StudioAnnotationKinds.Arrow);

    public void AddCalloutAtPlayhead() => AddAnnotationAtPlayhead(StudioAnnotationKinds.Callout);

    public void AddTextAtPlayhead() => AddAnnotationAtPlayhead(StudioAnnotationKinds.Text);

    public void RemoveSelectedAnnotation()
    {
        if (_session.RemoveSelectedAnnotation().Changed)
        {
            Announce(StudioEditorText.AnnotationDeletedMessage, AnnotationActivityId, StudioAnnouncementKind.Completed);
        }
    }

    public void BringAnnotationForward()
    {
        if (SelectedAnnotationIndex is { } index)
        {
            _session.BringAnnotationForward(index);
        }
    }

    public void SendAnnotationBack()
    {
        if (SelectedAnnotationIndex is { } index)
        {
            _session.SendAnnotationBack(index);
        }
    }

    /// <summary>Moves a whole annotation so it starts at a source time, for a drag on the lane.</summary>
    public StudioAnnotationEditResult MoveAnnotation(int index, double sourceTime) => _session.MoveAnnotation(index, sourceTime);

    public StudioAnnotationEditResult SetAnnotationStart(int index, double sourceTime) => _session.SetAnnotationStart(index, sourceTime);

    public StudioAnnotationEditResult SetAnnotationEnd(int index, double sourceTime) => _session.SetAnnotationEnd(index, sourceTime);

    public void StepAnnotationStartEarlier() => StepAnnotationStart(-AnnotationTimeStep);

    public void StepAnnotationStartLater() => StepAnnotationStart(AnnotationTimeStep);

    public void StepAnnotationEndEarlier() => StepAnnotationEnd(-AnnotationTimeStep);

    public void StepAnnotationEndLater() => StepAnnotationEnd(AnnotationTimeStep);

    public void SetAnnotationStartAtPlayhead()
    {
        if (SelectedAnnotationIndex is { } index)
        {
            _session.SetAnnotationStartAtPlayhead(index);
            AnnounceAnnotationTime(AnnotationStartAccessibleName);
        }
    }

    public void SetAnnotationEndAtPlayhead()
    {
        if (SelectedAnnotationIndex is { } index)
        {
            _session.SetAnnotationEndAtPlayhead(index);
            AnnounceAnnotationTime(AnnotationEndAccessibleName);
        }
    }

    private void AddAnnotationAtPlayhead(string kind)
    {
        if (!IsEditable)
        {
            return;
        }

        Announce(
            _session.AddAnnotationAtPlayhead(kind).Changed
                ? StudioEditorText.GetAnnotationAddedMessage(kind)
                : StudioEditorText.NoRoomForAnnotationMessage,
            AnnotationActivityId,
            StudioAnnouncementKind.Completed);
    }

    private void StepAnnotationStart(double seconds)
    {
        if (SelectedAnnotationIndex is { } index && SelectedAnnotation is { } annotation)
        {
            _session.SetAnnotationStart(index, annotation.Start + seconds);
            AnnounceAnnotationTime(AnnotationStartAccessibleName);
        }
    }

    private void StepAnnotationEnd(double seconds)
    {
        if (SelectedAnnotationIndex is { } index && SelectedAnnotation is { } annotation)
        {
            _session.SetAnnotationEnd(index, annotation.End + seconds);
            AnnounceAnnotationTime(AnnotationEndAccessibleName);
        }
    }

    private void AnnounceSelectedAnnotation()
    {
        if (SelectedAnnotationIndex is { } index && SelectedAnnotation is { } annotation)
        {
            Announce(
                StudioEditorText.GetAnnotationStepText(index, Annotations.Count, annotation),
                "StudioAnnotationSelection",
                StudioAnnouncementKind.Information);
        }
    }

    private void AnnounceAnnotationTime(string text) =>
        Announce(text, "StudioAnnotationTime", StudioAnnouncementKind.Information);

    private static string PercentText(double value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value * 100:0}%");

    /// <summary>Whether there is an annotation to step to follows the playhead. Reported when it has changed.</summary>
    private void RaiseAnnotationStateAtPlayhead()
    {
        var previousChanged = CanSelectPreviousAnnotation != _couldSelectPreviousAnnotation;
        var nextChanged = CanSelectNextAnnotation != _couldSelectNextAnnotation;
        RememberAnnotationStateAtPlayhead();
        if (previousChanged)
        {
            OnPropertyChanged(nameof(CanSelectPreviousAnnotation));
        }

        if (nextChanged)
        {
            OnPropertyChanged(nameof(CanSelectNextAnnotation));
        }
    }

    private void RememberAnnotationStateAtPlayhead()
    {
        _couldSelectPreviousAnnotation = CanSelectPreviousAnnotation;
        _couldSelectNextAnnotation = CanSelectNextAnnotation;
    }
}
