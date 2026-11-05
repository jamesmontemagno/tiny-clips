namespace TinyClips.Core.Studio;

/// <summary>
/// What an edit to an annotation did: whether the project changed, and where the annotation is in
/// <see cref="StudioOverlays.Annotations"/> afterwards. The index is null when there is no such
/// annotation, which is also the case once it has been deleted.
/// </summary>
public readonly record struct StudioAnnotationEditResult(bool Changed, int? Index);

// Annotations: arrows, callouts and text over the picture. They are stored in source time, like
// zooms, and may overlap. The list is the order they are drawn in, bottom first, so an edit leaves
// an annotation at its place in the list and only changing its layer moves it there.
public sealed partial class StudioEditorModel
{
    /// <summary>The shortest annotation the editor makes, in seconds of the recording.</summary>
    public const double MinimumAnnotationDuration = 0.1;

    /// <summary>How long an annotation lasts when it is added, in seconds, where there is room for it.</summary>
    public const double NewAnnotationDuration = 3;

    /// <summary>What a callout or a text box says when it is added.</summary>
    public const string NewAnnotationText = "Your text";

    // The annotation whose text was last typed in, for as long as nothing else has been edited:
    // what is typed next is part of the same undo step.
    private int? _typingInAnnotation;

    /// <summary>Whether the recording has room for an annotation.</summary>
    public bool CanAddAnnotation => SourceDuration >= MinimumAnnotationDuration;

    /// <summary>
    /// Adds an annotation at <paramref name="sourceTime"/>, on top of the others, lasting
    /// <see cref="NewAnnotationDuration"/> or up to the end of the recording. Text is fixed to the
    /// frame, and an arrow and a callout to the screen, unless the frame has no screen then. It
    /// goes in the middle of what is in view.
    /// </summary>
    public StudioAnnotationEditResult AddAnnotation(string kind, double sourceTime, StudioEvents? events)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (!CanAddAnnotation || !double.IsFinite(sourceTime))
        {
            return new StudioAnnotationEditResult(false, null);
        }

        var start = Math.Min(Math.Max(0, sourceTime), SourceDuration - MinimumAnnotationDuration);
        var end = Math.Min(start + NewAnnotationDuration, SourceDuration);
        var canvas = StudioCanvasMath.NaturalSize(Project);
        var frame = StudioLayoutResolver.Resolve(Project, events, start, canvas.Width, canvas.Height);
        var attach = kind != StudioAnnotationKinds.Text && frame.Screen is not null
            ? StudioAnnotationAttach.Screen
            : StudioAnnotationAttach.Frame;
        var point = StudioAnnotationGeometry.GetCenterInView(attach, frame);
        var annotation = new StudioAnnotation
        {
            Id = Guid.NewGuid().ToString("D"),
            Kind = kind,
            Attach = attach,
            Start = start,
            End = end,
            X = point.X,
            Y = point.Y,
            Text = kind == StudioAnnotationKinds.Arrow ? string.Empty : NewAnnotationText,
            Style = kind switch
            {
                StudioAnnotationKinds.Callout => StudioAnnotationStyle.Dark,
                StudioAnnotationKinds.Text => StudioAnnotationStyle.White,
                _ => StudioAnnotationStyle.Red,
            },
        };
        Mutate(project => WithAnnotations(project, [.. project.Overlays.Annotations, annotation]));
        return new StudioAnnotationEditResult(true, Project.Overlays.Annotations.Length - 1);
    }

    /// <summary>Deletes an annotation.</summary>
    public StudioAnnotationEditResult RemoveAnnotation(int index)
    {
        if (!HasAnnotation(index))
        {
            return new StudioAnnotationEditResult(false, null);
        }

        Mutate(project => WithAnnotations(project, [.. project.Overlays.Annotations.Where((_, at) => at != index)]));
        return new StudioAnnotationEditResult(true, null);
    }

    /// <summary>Moves when an annotation starts, up to the shortest it can be.</summary>
    public StudioAnnotationEditResult SetAnnotationStart(int index, double sourceTime) =>
        !double.IsFinite(sourceTime) ? AnnotationUnchanged(index) : EditAnnotation(index, annotation => annotation with
        {
            Start = Math.Max(0, Math.Min(sourceTime, annotation.End - MinimumAnnotationDuration)),
        });

    /// <summary>Moves when an annotation ends, down to the shortest it can be.</summary>
    public StudioAnnotationEditResult SetAnnotationEnd(int index, double sourceTime) =>
        !double.IsFinite(sourceTime) ? AnnotationUnchanged(index) : EditAnnotation(index, annotation => annotation with
        {
            End = Math.Min(SourceDuration, Math.Max(sourceTime, annotation.Start + MinimumAnnotationDuration)),
        });

    /// <summary>Moves a whole annotation so it starts at <paramref name="sourceTime"/>, keeping its length.</summary>
    public StudioAnnotationEditResult MoveAnnotation(int index, double sourceTime) =>
        !double.IsFinite(sourceTime) ? AnnotationUnchanged(index) : EditAnnotation(index, annotation =>
        {
            var length = annotation.End - annotation.Start;
            var start = Math.Max(0, Math.Min(sourceTime, SourceDuration - length));
            return annotation with { Start = start, End = start + length };
        });

    /// <summary>
    /// Sets what a callout or a text box says. Text typed with no other edit in between is one
    /// undo step, until <see cref="EndAnnotationTyping"/>.
    /// </summary>
    public StudioAnnotationEditResult SetAnnotationText(int index, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!HasAnnotation(index))
        {
            return new StudioAnnotationEditResult(false, null);
        }

        var annotations = Project.Overlays.Annotations;
        if (annotations[index].Text == text)
        {
            return new StudioAnnotationEditResult(false, index);
        }

        var isTyping = _typingInAnnotation == index;
        StudioProject Changed(StudioProject project)
        {
            var list = (StudioAnnotation[])project.Overlays.Annotations.Clone();
            list[index] = list[index] with { Text = text };
            return WithAnnotations(project, list);
        }

        if (isTyping)
        {
            Project = Changed(Project);
        }
        else
        {
            Mutate(Changed);
        }

        _typingInAnnotation = index;
        return new StudioAnnotationEditResult(true, index);
    }

    /// <summary>Typing has stopped: what is typed from now on is another undo step.</summary>
    public void EndAnnotationTyping() => _typingInAnnotation = null;

    public StudioAnnotationEditResult SetAnnotationStyle(int index, StudioAnnotationStyle style) =>
        EditAnnotation(index, annotation => annotation with { Style = style });

    public StudioAnnotationEditResult SetAnnotationSize(int index, StudioAnnotationSize size) =>
        EditAnnotation(index, annotation => annotation with { Size = size });

    /// <summary>
    /// What an annotation is fixed to. It stays where it is in the picture at <paramref name="sourceTime"/>
    /// where it can: a point out of view, or a screen the frame does not have, leaves its numbers.
    /// </summary>
    public StudioAnnotationEditResult SetAnnotationAttach(int index, StudioAnnotationAttach attach, double sourceTime, StudioEvents? events)
    {
        var canvas = StudioCanvasMath.NaturalSize(Project);
        var frame = StudioLayoutResolver.Resolve(Project, events, sourceTime, canvas.Width, canvas.Height);
        return EditAnnotation(index, annotation =>
        {
            var point = StudioAnnotationGeometry.GetPointWhenAttached(annotation, attach, frame, canvas.Width, canvas.Height);
            return annotation with { Attach = attach, X = point.X, Y = point.Y };
        });
    }

    /// <summary>Moves what an annotation points at, or the center of a text box, in normalized numbers.</summary>
    public StudioAnnotationEditResult SetAnnotationPoint(int index, double x, double y) =>
        !double.IsFinite(x) || !double.IsFinite(y) ? AnnotationUnchanged(index) : EditAnnotation(index, annotation => annotation with
        {
            X = Math.Clamp(x, 0, 1),
            Y = Math.Clamp(y, 0, 1),
        });

    /// <summary>Moves an arrow's tail or a callout's box, in short sides of the canvas from what it points at.</summary>
    public StudioAnnotationEditResult SetAnnotationOffset(int index, double offsetX, double offsetY) =>
        !double.IsFinite(offsetX) || !double.IsFinite(offsetY) ? AnnotationUnchanged(index) : EditAnnotation(index, annotation => annotation with
        {
            OffsetX = Math.Clamp(offsetX, -1, 1),
            OffsetY = Math.Clamp(offsetY, -1, 1),
        });

    /// <summary>Moves an annotation one place up (1) or down (-1) in the drawing order. Nothing past either end.</summary>
    public StudioAnnotationEditResult MoveAnnotationLayer(int index, int places)
    {
        if (!HasAnnotation(index))
        {
            return new StudioAnnotationEditResult(false, null);
        }

        var target = index + Math.Sign(places);
        if (target < 0 || target >= Project.Overlays.Annotations.Length || target == index)
        {
            return new StudioAnnotationEditResult(false, index);
        }

        Mutate(project =>
        {
            var list = (StudioAnnotation[])project.Overlays.Annotations.Clone();
            (list[index], list[target]) = (list[target], list[index]);
            return WithAnnotations(project, list);
        });
        return new StudioAnnotationEditResult(true, target);
    }

    /// <summary>
    /// The annotation after the selected one in time, or with none selected the one at
    /// <paramref name="sourceTime"/> or the first one after it. Those that start together go by
    /// their place in the list.
    /// </summary>
    public int? GetAnnotationIndexAfter(int? selectedIndex, double sourceTime)
    {
        var order = AnnotationsInTimeOrder();
        if (selectedIndex is { } selected && HasAnnotation(selected))
        {
            var at = Array.IndexOf(order, selected);
            return at + 1 < order.Length ? order[at + 1] : null;
        }

        var annotations = Project.Overlays.Annotations;
        foreach (var index in order)
        {
            if (annotations[index].End > sourceTime)
            {
                return index;
            }
        }

        return null;
    }

    /// <summary>The annotation before the selected one in time, or with none selected the one at <paramref name="sourceTime"/> or the last one before it.</summary>
    public int? GetAnnotationIndexBefore(int? selectedIndex, double sourceTime)
    {
        var order = AnnotationsInTimeOrder();
        if (selectedIndex is { } selected && HasAnnotation(selected))
        {
            var at = Array.IndexOf(order, selected);
            return at > 0 ? order[at - 1] : null;
        }

        var annotations = Project.Overlays.Annotations;
        for (var at = order.Length - 1; at >= 0; at--)
        {
            if (annotations[order[at]].Start <= sourceTime)
            {
                return order[at];
            }
        }

        return null;
    }

    /// <summary>
    /// Where the annotation at <paramref name="index"/> of <paramref name="before"/> is in
    /// <paramref name="after"/>, for an edit that did not say, such as undo: the one with the same
    /// id, or for one without an id, which only an agent or a project file makes, the same place in
    /// a list of the same length. Null when it is gone.
    /// </summary>
    public static int? FindAnnotationFollowing(IReadOnlyList<StudioAnnotation> before, int index, IReadOnlyList<StudioAnnotation> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (index < 0 || index >= before.Count)
        {
            return null;
        }

        var id = before[index].Id;
        if (id.Length == 0)
        {
            return before.Count == after.Count ? index : null;
        }

        for (var at = 0; at < after.Count; at++)
        {
            if (after[at].Id == id)
            {
                return at;
            }
        }

        return null;
    }

    private bool HasAnnotation(int index) => index >= 0 && index < Project.Overlays.Annotations.Length;

    private int[] AnnotationsInTimeOrder()
    {
        var annotations = Project.Overlays.Annotations;
        return [.. Enumerable.Range(0, annotations.Length).OrderBy(index => annotations[index].Start).ThenBy(index => index)];
    }

    private StudioAnnotationEditResult AnnotationUnchanged(int index) =>
        new(false, HasAnnotation(index) ? index : null);

    private StudioAnnotationEditResult EditAnnotation(int index, Func<StudioAnnotation, StudioAnnotation> change)
    {
        if (!HasAnnotation(index))
        {
            return new StudioAnnotationEditResult(false, null);
        }

        var before = EditableState;
        Mutate(project =>
        {
            var list = (StudioAnnotation[])project.Overlays.Annotations.Clone();
            list[index] = change(list[index]);
            return WithAnnotations(project, list);
        });
        return new StudioAnnotationEditResult(!before.ContentEquals(EditableState), index);
    }

    private static StudioProject WithAnnotations(StudioProject project, StudioAnnotation[] annotations) =>
        project with { Overlays = project.Overlays with { Annotations = annotations } };
}
