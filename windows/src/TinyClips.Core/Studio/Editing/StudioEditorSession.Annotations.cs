using TinyClips.Core.Studio.Rendering;
namespace TinyClips.Core.Studio.Editing;

// Annotations. An index is an annotation's place in Project.Overlays.Annotations, which is the order
// they are drawn in. One can be selected, in place of a zoom, a cut or a speed change. An edit to the
// selected one takes the selection with it, and so does adding one. Through every other edit, and
// undo and redo, the selection follows its annotation as StudioEditorModel.FindAnnotationFollowing
// finds it, or lets go when it is gone.
public sealed partial class StudioEditorSession
{
    private int? _selectedAnnotationIndex;

    /// <summary>The annotation the inspector shows, as its place in the list, or null.</summary>
    public int? SelectedAnnotationIndex =>
        _selectedAnnotationIndex is { } index && Model is { } model && index >= 0 && index < model.Project.Overlays.Annotations.Length
            ? index
            : null;

    public StudioAnnotation? SelectedAnnotation =>
        SelectedAnnotationIndex is { } index ? Model?.Project.Overlays.Annotations[index] : null;

    public bool CanAddAnnotation => IsEditable && Model is { CanAddAnnotation: true };

    /// <summary>
    /// Selects an annotation, or none with null or a place that has none. The playhead stays. What
    /// else was selected is let go, and the inspector shows the Annotate panel.
    /// </summary>
    public void SelectAnnotation(int? index)
    {
        if (index != _selectedAnnotationIndex)
        {
            EndAnnotationColorChoice();
        }

        var before = Selection;
        _selectedAnnotationIndex = index;
        _selectedAnnotationIndex = SelectedAnnotationIndex;
        if (_selectedAnnotationIndex is not null)
        {
            _selectedZoomIndex = null;
            _selectedCutIndex = null;
            _selectedSpeedIndex = null;
            ShowInspectorPanel(StudioInspectorPanel.Annotate);
        }

        if (Selection != before)
        {
            RaiseChanged(StudioEditorChanges.Selection);
        }
    }

    /// <summary>Selects the annotation after the selected one and moves the playhead to where it starts.</summary>
    public bool SelectNextAnnotation() => SelectAndShowAnnotation(Model?.GetAnnotationIndexAfter(SelectedAnnotationIndex, Playhead));

    /// <summary>Selects the annotation before the selected one and moves the playhead to where it starts.</summary>
    public bool SelectPreviousAnnotation() => SelectAndShowAnnotation(Model?.GetAnnotationIndexBefore(SelectedAnnotationIndex, Playhead));

    /// <summary>Selects an annotation and moves the playhead to where it starts. False, with nothing changed, without one.</summary>
    public bool SelectAndShowAnnotation(int? index)
    {
        if (index is not { } place || !IsEditable || Model is not { } model || place < 0 || place >= model.Project.Overlays.Annotations.Length)
        {
            return false;
        }

        SelectAnnotation(place);
        Scrub(model.Project.Overlays.Annotations[place].Start);
        return true;
    }

    /// <summary>Adds an annotation at the playhead, on top of the others, and selects it. The playhead stays.</summary>
    public StudioAnnotationEditResult AddAnnotationAtPlayhead(string kind)
    {
        var time = Playhead;
        var result = new StudioAnnotationEditResult(false, null);
        EditAndSelect(model =>
        {
            result = model.AddAnnotation(kind, time, _events);
            return result.Index is { } index ? EditSelection.Annotation(index) : null;
        });
        return result;
    }

    /// <summary>
    /// Copies the selected annotation, a little right and down, directly above it, and selects the
    /// copy. Unchanged, with no index, when no annotation is selected.
    /// </summary>
    public StudioAnnotationEditResult DuplicateSelectedAnnotation()
    {
        var result = new StudioAnnotationEditResult(false, null);
        if (SelectedAnnotationIndex is not { } index)
        {
            return result;
        }

        EditAndSelect(model =>
        {
            result = model.DuplicateAnnotation(index);
            return result.Index is { } copy ? EditSelection.Annotation(copy) : null;
        });
        return result;
    }

    public StudioAnnotationEditResult RemoveAnnotation(int index) => EditAnnotation(index, model => model.RemoveAnnotation(index));

    /// <summary>Deletes the selected annotation. Unchanged, with no index, when none is selected.</summary>
    public StudioAnnotationEditResult RemoveSelectedAnnotation() =>
        SelectedAnnotationIndex is { } index ? RemoveAnnotation(index) : new StudioAnnotationEditResult(false, null);

    /// <summary>Moves when an annotation starts, and shows the picture there, the first one that has it.</summary>
    public StudioAnnotationEditResult SetAnnotationStart(int index, double sourceTime)
    {
        var result = EditAnnotation(index, model => model.SetAnnotationStart(index, sourceTime));
        if (IsEditable && result.Index is { } place && Model is { } model)
        {
            Scrub(model.Project.Overlays.Annotations[place].Start);
        }

        return result;
    }

    /// <summary>Moves when an annotation ends, and shows the last picture that has it.</summary>
    public StudioAnnotationEditResult SetAnnotationEnd(int index, double sourceTime)
    {
        var result = EditAnnotation(index, model => model.SetAnnotationEnd(index, sourceTime));
        if (IsEditable && result.Index is { } place && Model is { } model)
        {
            var annotation = model.Project.Overlays.Annotations[place];
            Scrub(Math.Max(annotation.Start, annotation.End - model.FrameDuration));
        }

        return result;
    }

    /// <summary>Starts an annotation at the playhead. The playhead stays where it is.</summary>
    public StudioAnnotationEditResult SetAnnotationStartAtPlayhead(int index)
    {
        var time = Playhead;
        return EditAnnotation(index, model => model.SetAnnotationStart(index, time));
    }

    /// <summary>Ends an annotation at the playhead. The playhead stays where it is.</summary>
    public StudioAnnotationEditResult SetAnnotationEndAtPlayhead(int index)
    {
        var time = Playhead;
        return EditAnnotation(index, model => model.SetAnnotationEnd(index, time));
    }

    /// <summary>Moves a whole annotation so it starts at a source time. The playhead stays.</summary>
    public StudioAnnotationEditResult MoveAnnotation(int index, double sourceTime) =>
        EditAnnotation(index, model => model.MoveAnnotation(index, sourceTime));

    public StudioAnnotationEditResult SetAnnotationFill(int index, string color) =>
        EditAnnotation(index, model => model.SetAnnotationFill(index, color));

    public StudioAnnotationEditResult SetAnnotationTextColor(int index, string color) =>
        EditAnnotation(index, model => model.SetAnnotationTextColor(index, color));

    /// <summary>A color has been chosen: the next one is another undo step.</summary>
    public void EndAnnotationColorChoice() => Model?.EndAnnotationColorChoice();

    public StudioAnnotationEditResult SetAnnotationScale(int index, double scale) =>
        EditAnnotation(index, model => model.SetAnnotationScale(index, scale));

    public StudioAnnotationEditResult SetAnnotationRotation(int index, double rotation) =>
        EditAnnotation(index, model => model.SetAnnotationRotation(index, rotation));

    public void TransformAnnotation(int index, StudioAnnotation start, StudioAnnotationPart part, StudioPoint anchor, StudioPoint pressed, StudioPoint current)
    {
        if (IsEditable && StudioAnnotationGeometry.Transform(start, part, anchor, pressed, current) is { } move)
        {
            EditAnnotation(index, model => model.MoveAnnotation(index, move));
        }
    }

    public StudioAnnotationEditResult SetAnnotationShapeFill(int index, string color) =>
        EditAnnotation(index, model => model.SetAnnotationShapeFill(index, color));

    public StudioAnnotationEditResult SetAnnotationShapeFilled(int index, bool isFilled) =>
        EditAnnotation(index, model => model.SetAnnotationShapeFilled(index, isFilled));

    public StudioAnnotationEditResult SetAnnotationShapeFillOpacity(int index, double opacity) =>
        EditAnnotation(index, model => model.SetAnnotationShapeFillOpacity(index, opacity));

    public StudioAnnotationEditResult SetAnnotationCornerRadius(int index, double radius) =>
        EditAnnotation(index, model => model.SetAnnotationCornerRadius(index, radius));

    public StudioAnnotationEditResult SetAnnotationSize(int index, double width, double height) =>
        EditAnnotation(index, model => model.SetAnnotationSize(index, width, height));

    public StudioAnnotationEditResult SetAnnotationRedactStyle(int index, StudioRedactStyle style) =>
        EditAnnotation(index, model => model.SetAnnotationRedactStyle(index, style));

    public StudioAnnotationEditResult SetAnnotationRedactLevel(int index, StudioRedactLevel level) =>
        EditAnnotation(index, model => model.SetAnnotationRedactLevel(index, level));

    public StudioAnnotationEditResult SetAnnotationEmoji(int index, string emoji) =>
        EditAnnotation(index, model => model.SetAnnotationEmoji(index, emoji));

    public StudioAnnotationEditResult SetAnnotationSize(int index, StudioAnnotationSize size) =>
        EditAnnotation(index, model => model.SetAnnotationSize(index, size));

    public StudioAnnotationEditResult SetAnnotationShadow(int index, bool hasShadow) =>
        EditAnnotation(index, model => model.SetAnnotationShadow(index, hasShadow));

    /// <summary>
    /// What is drawn at the playhead, bottom first, for the preview to let a pointer take hold of.
    /// Nothing while the video plays, when there is nothing to hold still.
    /// </summary>
    public IReadOnlyList<StudioAnnotationHandle> GetAnnotationHandles(double canvasWidth, double canvasHeight)
    {
        if (!IsEditable || IsPlaying || Model is not { } model || !(canvasWidth > 0) || !(canvasHeight > 0))
        {
            return [];
        }

        var project = model.Project;
        var frame = StudioLayoutResolver.Resolve(project, _events, Playhead, canvasWidth, canvasHeight);
        var handles = new List<StudioAnnotationHandle>();
        for (var index = 0; index < project.Overlays.Annotations.Length; index++)
        {
            if (!StudioAnnotationGeometry.TryResolve(project.Overlays.Annotations[index], index, frame, Playhead, canvasWidth, canvasHeight, out var resolved))
            {
                continue;
            }

            var handle = resolved.IsArea
                ? new StudioAnnotationHandle(index, resolved.Kind, resolved.Anchor, null, resolved.Stroke, resolved.Rect, resolved.Radius)
                : resolved.IsArrow
                ? new StudioAnnotationHandle(index, resolved.Kind, resolved.Anchor, resolved.Tail, resolved.Stroke, null, 0)
                : resolved.IsEmoji
                ? new StudioAnnotationHandle(index, resolved.Kind, resolved.Anchor, null, 0, StudioAnnotationTextMeasure.LayoutEmoji(resolved, canvasWidth, canvasHeight), 0)
                : new StudioAnnotationHandle(index, resolved.Kind, resolved.Anchor, null, 0, StudioAnnotationTextMeasure.LayoutBox(resolved, canvasWidth, canvasHeight).Box, resolved.Radius);
            handles.Add(handle with { Rotation = resolved.Rotation });
        }

        return handles;
    }

    /// <summary>
    /// Where a drag on the preview leaves an annotation that was <paramref name="start"/> when it
    /// began. What the move did is handed back so that the preview can show a center line it settled on.
    /// </summary>
    public StudioAnnotationMove? DragAnnotation(int index, StudioAnnotation start, StudioAnnotationPart part, double byX, double byY, StudioPoint? drawnBoxCenter, double canvasWidth, double canvasHeight)
    {
        if (!IsEditable || Model is not { } model)
        {
            return null;
        }

        var frame = StudioLayoutResolver.Resolve(model.Project, _events, Playhead, canvasWidth, canvasHeight);
        var moved = StudioAnnotationKinds.IsArea(start.Kind)
            ? StudioAnnotationGeometry.GetAreaMoved(start, part, byX, byY, frame, canvasWidth, canvasHeight)
            : StudioAnnotationGeometry.GetMoved(start, part, byX, byY, frame, canvasWidth, canvasHeight, drawnBoxCenter);
        if (moved is not { } move)
        {
            return null;
        }

        EditAnnotation(index, editor => editor.MoveAnnotation(index, move));
        return move;
    }

    public StudioAnnotationEditResult SetAnnotationText(int index, string text) =>
        EditAnnotation(index, model => model.SetAnnotationText(index, text));

    /// <summary>Typing has stopped: what is typed from now on is another undo step.</summary>
    public void EndAnnotationTyping() => Model?.EndAnnotationTyping();

    public StudioAnnotationEditResult SetAnnotationStyle(int index, StudioAnnotationStyle style) =>
        EditAnnotation(index, model => model.SetAnnotationStyle(index, style));

    /// <summary>What an annotation is fixed to. It stays where it is in the picture at the playhead where it can.</summary>
    public StudioAnnotationEditResult SetAnnotationAttach(int index, StudioAnnotationAttach attach)
    {
        var time = Playhead;
        return EditAnnotation(index, model => model.SetAnnotationAttach(index, attach, time, _events));
    }

    public StudioAnnotationEditResult SetAnnotationPoint(int index, double x, double y) =>
        EditAnnotation(index, model => model.SetAnnotationPoint(index, x, y));

    public StudioAnnotationEditResult SetAnnotationOffset(int index, double offsetX, double offsetY) =>
        EditAnnotation(index, model => model.SetAnnotationOffset(index, offsetX, offsetY));

    /// <summary>Draws an annotation one place higher, over the one above it.</summary>
    public StudioAnnotationEditResult BringAnnotationForward(int index) =>
        EditAnnotation(index, model => model.MoveAnnotationLayer(index, 1));

    /// <summary>Draws an annotation one place lower, under the one below it.</summary>
    public StudioAnnotationEditResult SendAnnotationBack(int index) =>
        EditAnnotation(index, model => model.MoveAnnotationLayer(index, -1));

    // An edit to one annotation. The selection goes with it when it is the selected one. An edit
    // that changed nothing says nothing of the selection and shows no panel, as for a zoom.
    private StudioAnnotationEditResult EditAnnotation(int index, Func<StudioEditorModel, StudioAnnotationEditResult> change)
    {
        var result = new StudioAnnotationEditResult(false, index);
        var isSelected = SelectedAnnotationIndex == index;
        EditAndSelect(model =>
        {
            result = change(model);
            return isSelected && result.Changed ? EditSelection.Annotation(result.Index) : null;
        });
        return result;
    }
}
