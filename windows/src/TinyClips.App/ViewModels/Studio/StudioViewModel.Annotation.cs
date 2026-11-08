using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using Windows.UI;

namespace TinyClips.App.ViewModels.Studio;

// The values behind the annotation lane, the Annotate panel of the inspector and the annotations on
// the preview. An annotation is selected in place of a zoom, a cut or a speed change, never with one. What
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
        nameof(AnnotationRotation),
        nameof(AnnotationRotationText),
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
        nameof(HasAnnotationColor),
        nameof(HasAnnotationScale),
        nameof(AnnotationScaleTitle),
        nameof(HasAnnotationShape),
        nameof(HasAnnotationArea),
        nameof(HasAnnotationCornerRadius),
        nameof(HasAnnotationRedaction),
        nameof(HasAnnotationRedactLevel),
        nameof(AnnotationChipGlyph),
        nameof(AnnotationShapeFilled),
        nameof(AnnotationShapeFillColor),
        nameof(AnnotationShapeFillOpacity),
        nameof(AnnotationShapeFillOpacityText),
        nameof(AnnotationCornerRadius),
        nameof(AnnotationCornerRadiusText),
        nameof(AnnotationWidth),
        nameof(AnnotationWidthText),
        nameof(AnnotationHeight),
        nameof(AnnotationHeightText),
        nameof(AnnotationRedactStyleIndex),
        nameof(AnnotationRedactLevelIndex),
        nameof(HasAnnotationEmoji),
        nameof(AnnotationEmoji),
        nameof(AnnotationShadow),
        nameof(AnnotationSizeIndex),
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
        SelectedAnnotation?.Kind is StudioAnnotationKinds.Arrow or StudioAnnotationKinds.Callout or StudioAnnotationKinds.Text
            or StudioAnnotationKinds.Rectangle or StudioAnnotationKinds.Ellipse or StudioAnnotationKinds.Redact or StudioAnnotationKinds.Emoji;

    public bool HasAnnotationStyle => IsSelectedKnown;

    public bool IsAnnotationNotDrawn => HasSelectedAnnotation && !IsSelectedKnown;

    public bool HasAnnotationText => SelectedAnnotation?.Kind is StudioAnnotationKinds.Callout or StudioAnnotationKinds.Text;

    public bool HasAnnotationOffset => SelectedAnnotation?.Kind is StudioAnnotationKinds.Arrow or StudioAnnotationKinds.Callout;

    /// <summary>A rectangle or an ellipse: an outline, with an inside that can be filled.</summary>
    public bool HasAnnotationShape => SelectedAnnotation?.Kind is StudioAnnotationKinds.Rectangle or StudioAnnotationKinds.Ellipse;

    /// <summary>A rectangle, an ellipse or a redaction: it covers an area, which has a width and a height.</summary>
    public bool HasAnnotationArea => StudioAnnotationKinds.IsArea(SelectedAnnotation?.Kind);

    public bool HasAnnotationCornerRadius => SelectedAnnotation?.Kind == StudioAnnotationKinds.Rectangle;

    public bool HasAnnotationRedaction => SelectedAnnotation?.Kind == StudioAnnotationKinds.Redact;

    /// <summary>A blurred or pixelated redaction has a strength. A solid one hides all of its area.</summary>
    public bool HasAnnotationRedactLevel => HasAnnotationRedaction && SelectedAnnotation!.RedactStyle != StudioRedactStyle.Solid;

    /// <summary>An emoji: its emoji, its size and its shadow, and no colors of its own.</summary>
    public bool HasAnnotationEmoji => SelectedAnnotation?.Kind == StudioAnnotationKinds.Emoji;

    /// <summary>A blurred or pixelated redaction shows what is under it, and an emoji has its own colors.</summary>
    public bool HasAnnotationColor => IsSelectedKnown && !HasAnnotationRedactLevel && !HasAnnotationEmoji;

    /// <summary>A redaction has nothing that the size of the others makes thicker or bigger.</summary>
    public bool HasAnnotationScale => IsSelectedKnown && !HasAnnotationRedaction;

    public string AnnotationScaleTitle => HasAnnotationShape ? "Outline width" : "Size";

    public string AnnotationRedactNote => StudioEditorText.AnnotationRedactNote;

    /// <summary>What a style chip shows for the selected kind: its tool's glyph, or null for the letters of a box.</summary>
    public string? AnnotationChipGlyph => SelectedAnnotation?.Kind switch
    {
        StudioAnnotationKinds.Arrow => StudioGlyphs.AddArrow,
        StudioAnnotationKinds.Rectangle => StudioGlyphs.AddRectangle,
        StudioAnnotationKinds.Ellipse => StudioGlyphs.AddEllipse,
        StudioAnnotationKinds.Redact => StudioGlyphs.AddRedaction,
        _ => null,
    };

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

    /// <summary>The emoji the panel offers, with the names a screen reader says for them.</summary>
    public IReadOnlyList<(string Emoji, string Name)> EmojiChoices => StudioEditorText.EmojiChoices;

    public string MoreEmojiHint => StudioEditorText.MoreEmojiHint;

    /// <summary>
    /// The selected emoji's emoji, for the box it can be typed or pasted in. What is typed with no
    /// other edit in between is one undo step, as in a text box; an empty box is no edit.
    /// </summary>
    public string AnnotationEmoji
    {
        get => HasAnnotationEmoji ? SelectedAnnotation!.Text : string.Empty;
        set
        {
            var emoji = value.ReplaceLineEndings(string.Empty);
            if (HasAnnotationEmoji && SelectedAnnotationIndex is { } index && !StudioAnnotationGeometry.IsBlank(emoji) && emoji != AnnotationEmoji)
            {
                _session.SetAnnotationText(index, emoji);
            }
        }
    }

    /// <summary>Puts one of <see cref="EmojiChoices"/> on the selected emoji, as one undo step.</summary>
    public void SetAnnotationEmoji(string emoji)
    {
        if (HasAnnotationEmoji && SelectedAnnotationIndex is { } index)
        {
            _session.SetAnnotationEmoji(index, emoji);
        }
    }

    /// <summary>Whether the selected emoji has a soft shadow under it.</summary>
    public bool AnnotationShadow
    {
        get => SelectedAnnotation?.Shadow ?? false;
        set
        {
            if (SelectedAnnotationIndex is { } index && value != AnnotationShadow)
            {
                _session.SetAnnotationShadow(index, value);
            }

            ResyncIfDifferent(value, AnnotationShadow);
        }
    }

    /// <summary>Small, Medium or Large, or -1 with none selected.</summary>
    public int AnnotationSizeIndex
    {
        get => SelectedAnnotation is { } annotation && Enum.IsDefined(annotation.Size) ? (int)annotation.Size : -1;
        set
        {
            if (value >= 0 && value != AnnotationSizeIndex && SelectedAnnotationIndex is { } index && Enum.IsDefined((StudioAnnotationSize)value))
            {
                _session.SetAnnotationSize(index, (StudioAnnotationSize)value);
            }

            ResyncIfDifferent(value, AnnotationSizeIndex);
        }
    }

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
    public StudioAnnotationMove? DragAnnotation(int index, StudioAnnotation start, StudioAnnotationPart part, double byX, double byY, StudioPoint? drawnBoxCenter = null) =>
        _session.DragAnnotation(index, start, part, byX, byY, drawnBoxCenter, CanvasWidth, CanvasHeight);

    /// <summary>A color has been chosen: the next one is another undo step.</summary>
    public void EndAnnotationColorChoice() => _session.EndAnnotationColorChoice();

    /// <summary>The look to start from. Choosing one takes back the colors chosen by hand.</summary>
    public void SetAnnotationStyle(StudioAnnotationStyle style)
    {
        if (SelectedAnnotationIndex is { } index)
        {
            _session.SetAnnotationStyle(index, style);
        }
    }

    public string AnnotationFillTitle => SelectedAnnotation?.Kind switch
    {
        StudioAnnotationKinds.Arrow or StudioAnnotationKinds.Redact => "Color",
        StudioAnnotationKinds.Rectangle or StudioAnnotationKinds.Ellipse => "Outline",
        _ => "Fill",
    };

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

    /// <summary>Whether the inside of a rectangle or an ellipse is filled.</summary>
    public bool AnnotationShapeFilled
    {
        get => StudioAnnotationGeometry.GetCustomColor(SelectedAnnotation?.ShapeFill) is not null;
        set
        {
            if (SelectedAnnotationIndex is { } index && value != AnnotationShapeFilled)
            {
                _session.SetAnnotationShapeFilled(index, value);
            }

            ResyncIfDifferent(value, AnnotationShapeFilled);
        }
    }

    /// <summary>The color inside, or the outline's while the inside is empty: the color it would be filled with.</summary>
    public Color AnnotationShapeFillColor =>
        ParseBackgroundColor(SelectedAnnotation is { } annotation
            ? StudioAnnotationGeometry.GetCustomColor(annotation.ShapeFill) ?? StudioAnnotationGeometry.GetEffectiveColors(annotation).Fill
            : "#FFFFFF");

    public void SetAnnotationShapeFillColor(Color color)
    {
        if (SelectedAnnotationIndex is { } index)
        {
            _session.SetAnnotationShapeFill(index, ToHex(color));
        }
    }

    public double AnnotationShapeFillOpacity
    {
        get => Math.Clamp(SelectedAnnotation?.ShapeFillOpacity ?? 0, 0, 1);
        set
        {
            if (SelectedAnnotationIndex is { } index && IsRequest(value, AnnotationShapeFillOpacity))
            {
                _session.SetAnnotationShapeFillOpacity(index, value);
            }
        }
    }

    public string AnnotationShapeFillOpacityText => PercentText(AnnotationShapeFillOpacity);

    /// <summary>How round a rectangle's corners are, as a share of the picture's short side.</summary>
    public double AnnotationCornerRadius
    {
        get => Math.Clamp(SelectedAnnotation?.CornerRadius ?? 0, 0, 0.5);
        set
        {
            if (SelectedAnnotationIndex is { } index && IsRequest(value, AnnotationCornerRadius))
            {
                _session.SetAnnotationCornerRadius(index, value);
            }
        }
    }

    public string AnnotationCornerRadiusText => PercentText(AnnotationCornerRadius);

    /// <summary>An area's width, as a share of the picture, or of the screen for one attached to the screen.</summary>
    public double AnnotationWidth
    {
        get => Math.Clamp(SelectedAnnotation?.Width ?? 0, StudioEditorModel.MinimumAreaSize, 1);
        set
        {
            if (SelectedAnnotationIndex is { } index && IsRequest(value, AnnotationWidth))
            {
                _session.SetAnnotationSize(index, value, AnnotationHeight);
            }
        }
    }

    public string AnnotationWidthText => PercentText(AnnotationWidth);

    public double AnnotationHeight
    {
        get => Math.Clamp(SelectedAnnotation?.Height ?? 0, StudioEditorModel.MinimumAreaSize, 1);
        set
        {
            if (SelectedAnnotationIndex is { } index && IsRequest(value, AnnotationHeight))
            {
                _session.SetAnnotationSize(index, AnnotationWidth, value);
            }
        }
    }

    public string AnnotationHeightText => PercentText(AnnotationHeight);

    /// <summary>Blur, Pixelate or Solid, or -1 with none selected.</summary>
    public int AnnotationRedactStyleIndex
    {
        get => SelectedAnnotation is { } annotation ? (int)annotation.RedactStyle : -1;
        set
        {
            if (value >= 0 && value != AnnotationRedactStyleIndex && SelectedAnnotationIndex is { } index && Enum.IsDefined((StudioRedactStyle)value))
            {
                _session.SetAnnotationRedactStyle(index, (StudioRedactStyle)value);
            }

            ResyncIfDifferent(value, AnnotationRedactStyleIndex);
        }
    }

    /// <summary>Light, Medium or Heavy, or -1 with none selected.</summary>
    public int AnnotationRedactLevelIndex
    {
        get => SelectedAnnotation is { } annotation ? (int)annotation.RedactLevel : -1;
        set
        {
            if (value >= 0 && value != AnnotationRedactLevelIndex && SelectedAnnotationIndex is { } index && Enum.IsDefined((StudioRedactLevel)value))
            {
                _session.SetAnnotationRedactLevel(index, (StudioRedactLevel)value);
            }

            ResyncIfDifferent(value, AnnotationRedactLevelIndex);
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

    public double AnnotationRotation
    {
        get => StudioAnnotationGeometry.NormalizeRotation(SelectedAnnotation?.Rotation ?? 0);
        set
        {
            if (SelectedAnnotationIndex is { } index && IsRequest(value, AnnotationRotation))
            {
                _session.SetAnnotationRotation(index, value);
            }
        }
    }

    public string AnnotationRotationText => string.Create(CultureInfo.InvariantCulture, $"{AnnotationRotation:0} degrees");

    public void TransformAnnotation(int index, StudioAnnotation start, StudioAnnotationPart part, StudioPoint anchor, StudioPoint pressed, StudioPoint current) =>
        _session.TransformAnnotation(index, start, part, anchor, pressed, current);

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

    public void AddRectangleAtPlayhead() => AddAnnotationAtPlayhead(StudioAnnotationKinds.Rectangle);

    public void AddEllipseAtPlayhead() => AddAnnotationAtPlayhead(StudioAnnotationKinds.Ellipse);

    public void AddRedactionAtPlayhead() => AddAnnotationAtPlayhead(StudioAnnotationKinds.Redact);

    public void AddEmojiAtPlayhead() => AddAnnotationAtPlayhead(StudioAnnotationKinds.Emoji);

    /// <summary>Copies the selected annotation a little right and down, above it, and selects the copy.</summary>
    public void DuplicateSelectedAnnotation()
    {
        if (!IsEditable || SelectedAnnotation is not { } annotation)
        {
            return;
        }

        if (_session.DuplicateSelectedAnnotation().Changed)
        {
            Announce(StudioEditorText.GetAnnotationDuplicatedMessage(annotation.Kind), AnnotationActivityId, StudioAnnouncementKind.Completed);
        }
    }

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
