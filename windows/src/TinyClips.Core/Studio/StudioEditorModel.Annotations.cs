namespace TinyClips.Core.Studio;

/// <summary>
/// What an edit to an annotation did: whether the project changed, and where the annotation is in
/// <see cref="StudioOverlays.Annotations"/> afterwards. The index is null when there is no such
/// annotation, which is also the case once it has been deleted.
/// </summary>
public readonly record struct StudioAnnotationEditResult(bool Changed, int? Index);

// Annotations: arrows, callouts, text, rectangles, ellipses, redactions and emoji over the picture. They are stored in source time, like
// zooms, and may overlap. The list is the order they are drawn in, bottom first, so an edit leaves
// an annotation at its place in the list and only changing its layer moves it there.
public sealed partial class StudioEditorModel
{
    /// <summary>The shortest annotation the editor makes, in seconds of the recording.</summary>
    public const double MinimumAnnotationDuration = 0.1;

    /// <summary>How long an annotation lasts when it is added, in seconds, where there is room for it.</summary>
    public const double NewAnnotationDuration = 3;

    /// <summary>How wide a rectangle, an ellipse or a redaction is when it is added, as a share of what is in view.</summary>
    public const double NewAreaWidth = 0.2;

    /// <summary>How tall a rectangle, an ellipse or a redaction is when it is added, as a share of what is in view.</summary>
    public const double NewAreaHeight = 0.15;

    /// <summary>The smallest width or height the editor gives an area, as a share of the picture.</summary>
    public const double MinimumAreaSize = 0.01;

    /// <summary>What a callout or a text box says when it is added.</summary>
    public const string NewAnnotationText = "Your text";

    /// <summary>The emoji an emoji annotation has when it is added: thumbs up.</summary>
    public const string NewAnnotationEmoji = "\U0001F44D";

    /// <summary>How far a copy of an annotation is moved right and down from the original, in the numbers of the project.</summary>
    public const double DuplicateNudge = 0.02;

    // The annotation whose text was last typed in, for as long as nothing else has been edited:
    // what is typed next is part of the same undo step.
    private int? _typingInAnnotation;

    /// <summary>The color of an annotation that is being chosen, in the same way.</summary>
    private int? _choosingColorInAnnotation;

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
        var isArea = StudioAnnotationKinds.IsArea(kind);

        // An area is the same size on the screen however far it is zoomed in.
        var (viewWidth, viewHeight) = attach == StudioAnnotationAttach.Screen && frame.Screen is { } screen
            ? (screen.Source.Width, screen.Source.Height)
            : (1.0, 1.0);
        var annotation = new StudioAnnotation
        {
            Id = Guid.NewGuid().ToString("D"),
            Kind = kind,
            Attach = attach,
            Start = start,
            End = end,
            X = point.X,
            Y = point.Y,
            Text = kind == StudioAnnotationKinds.Emoji ? NewAnnotationEmoji : kind == StudioAnnotationKinds.Arrow || isArea ? string.Empty : NewAnnotationText,
            Width = NewAreaWidth * viewWidth,
            Height = NewAreaHeight * viewHeight,
            Style = kind switch
            {
                StudioAnnotationKinds.Callout or StudioAnnotationKinds.Redact => StudioAnnotationStyle.Dark,
                StudioAnnotationKinds.Text => StudioAnnotationStyle.White,
                _ => StudioAnnotationStyle.Red,
            },
        };
        Mutate(project => WithAnnotations(project, [.. project.Overlays.Annotations, annotation]));
        return new StudioAnnotationEditResult(true, Project.Overlays.Annotations.Length - 1);
    }

    /// <summary>
    /// Copies an annotation, with a new id and the same timing, moved <see cref="DuplicateNudge"/>
    /// right and down, directly above the original in the list.
    /// </summary>
    public StudioAnnotationEditResult DuplicateAnnotation(int index)
    {
        if (!HasAnnotation(index))
        {
            return new StudioAnnotationEditResult(false, null);
        }

        var original = Project.Overlays.Annotations[index];
        var copy = original with
        {
            Id = Guid.NewGuid().ToString("D"),
            X = Math.Clamp(original.X + DuplicateNudge, 0, 1),
            Y = Math.Clamp(original.Y + DuplicateNudge, 0, 1),
            ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>(original.ExtensionData),
        };
        Mutate(project =>
        {
            var list = project.Overlays.Annotations.ToList();
            list.Insert(index + 1, copy);
            return WithAnnotations(project, [.. list]);
        });
        return new StudioAnnotationEditResult(true, index + 1);
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

    /// <summary>A style is a set of colors, so choosing one takes back the colors chosen by hand.</summary>
    public StudioAnnotationEditResult SetAnnotationStyle(int index, StudioAnnotationStyle style) =>
        EditAnnotation(index, annotation => annotation with { Style = style, Fill = string.Empty, TextColor = string.Empty });

    /// <summary>
    /// The color of an arrow or a box, as <c>#RRGGBB</c>. A color picker hands over many on its way
    /// to the one that is meant, which are one undo step, as text being typed is.
    /// </summary>
    public StudioAnnotationEditResult SetAnnotationFill(int index, string color) =>
        SetAnnotationColor(index, 0, color, static (annotation, value) => annotation with { Fill = value });

    /// <summary>The color of the text of a box, as <c>#RRGGBB</c>, in the same way.</summary>
    public StudioAnnotationEditResult SetAnnotationTextColor(int index, string color) =>
        SetAnnotationColor(index, 1, color, static (annotation, value) => annotation with { TextColor = value });

    /// <summary>The color inside a rectangle or an ellipse, as <c>#RRGGBB</c>, in the same way. It fills the inside if it was empty.</summary>
    public StudioAnnotationEditResult SetAnnotationShapeFill(int index, string color) =>
        SetAnnotationColor(index, 2, color, static (annotation, value) => annotation with { ShapeFill = value });

    /// <summary>Fills the inside of a rectangle or an ellipse in the color of its outline, or empties it.</summary>
    public StudioAnnotationEditResult SetAnnotationShapeFilled(int index, bool isFilled) =>
        EditAnnotation(index, annotation => isFilled
            ? StudioAnnotationGeometry.GetCustomColor(annotation.ShapeFill) is null
                ? annotation with { ShapeFill = StudioAnnotationGeometry.GetEffectiveColors(annotation).Fill }
                : annotation
            : annotation with { ShapeFill = string.Empty });

    /// <summary>How opaque the inside of a rectangle or an ellipse is.</summary>
    public StudioAnnotationEditResult SetAnnotationShapeFillOpacity(int index, double opacity) =>
        !double.IsFinite(opacity) ? AnnotationUnchanged(index) : EditAnnotation(index, annotation => annotation with
        {
            ShapeFillOpacity = Math.Clamp(opacity, 0, 1),
        });

    /// <summary>How round the corners of a rectangle are, as a share of the canvas's short side.</summary>
    public StudioAnnotationEditResult SetAnnotationCornerRadius(int index, double radius) =>
        !double.IsFinite(radius) ? AnnotationUnchanged(index) : EditAnnotation(index, annotation => annotation with
        {
            CornerRadius = Math.Clamp(radius, 0, 0.5),
        });

    /// <summary>The size of a rectangle, an ellipse or a redaction, as shares of the picture, around its middle.</summary>
    public StudioAnnotationEditResult SetAnnotationSize(int index, double width, double height) =>
        !double.IsFinite(width) || !double.IsFinite(height) ? AnnotationUnchanged(index) : EditAnnotation(index, annotation => annotation with
        {
            Width = Math.Clamp(width, MinimumAreaSize, 1),
            Height = Math.Clamp(height, MinimumAreaSize, 1),
        });

    /// <summary>Sets an emoji's emoji, as one undo step of its own.</summary>
    public StudioAnnotationEditResult SetAnnotationEmoji(int index, string emoji)
    {
        ArgumentNullException.ThrowIfNull(emoji);
        EndAnnotationTyping();
        return StudioAnnotationGeometry.IsBlank(emoji) ? AnnotationUnchanged(index) : EditAnnotation(index, annotation => annotation with { Text = emoji });
    }

    /// <summary>Small, medium, or large: the size of an annotation before its scale.</summary>
    public StudioAnnotationEditResult SetAnnotationSize(int index, StudioAnnotationSize size) =>
        !Enum.IsDefined(size) ? AnnotationUnchanged(index) : EditAnnotation(index, annotation => annotation with { Size = size });

    /// <summary>Whether an emoji has a drop shadow under it.</summary>
    public StudioAnnotationEditResult SetAnnotationShadow(int index, bool hasShadow) =>
        EditAnnotation(index, annotation => annotation with { Shadow = hasShadow });

    /// <summary>How a redaction hides what is under it.</summary>
    public StudioAnnotationEditResult SetAnnotationRedactStyle(int index, StudioRedactStyle style) =>
        EditAnnotation(index, annotation => annotation with { RedactStyle = style });

    /// <summary>How strongly a blurred or pixelated redaction hides what is under it.</summary>
    public StudioAnnotationEditResult SetAnnotationRedactLevel(int index, StudioRedactLevel level) =>
        EditAnnotation(index, annotation => annotation with { RedactLevel = level });

    private StudioAnnotationEditResult SetAnnotationColor(int index, int part, string color, Func<StudioAnnotation, string, StudioAnnotation> apply)
    {
        if (!HasAnnotation(index) || StudioAnnotationGeometry.GetCustomColor(color) is not { } value)
        {
            return AnnotationUnchanged(index);
        }

        // The color it already has: a picker that says what it shows must not turn a style into a custom color.
        var effective = StudioAnnotationGeometry.GetEffectiveColors(Project.Overlays.Annotations[index]);
        var current = part switch
        {
            0 => effective.Fill,
            1 => effective.Text,
            _ => StudioAnnotationGeometry.GetCustomColor(Project.Overlays.Annotations[index].ShapeFill),
        };
        if (value == current)
        {
            return AnnotationUnchanged(index);
        }

        var after = apply(Project.Overlays.Annotations[index], value);
        var marker = (index * 3) + part;
        StudioProject Changed(StudioProject project)
        {
            var list = (StudioAnnotation[])project.Overlays.Annotations.Clone();
            list[index] = after;
            return WithAnnotations(project, list);
        }

        if (_choosingColorInAnnotation == marker)
        {
            Project = Changed(Project);
        }
        else
        {
            Mutate(Changed);
        }

        _choosingColorInAnnotation = marker;
        return new StudioAnnotationEditResult(true, index);
    }

    /// <summary>A color has been chosen: the next one is another undo step.</summary>
    public void EndAnnotationColorChoice() => _choosingColorInAnnotation = null;

    /// <summary>What the size of an annotation is multiplied by.</summary>
    public StudioAnnotationEditResult SetAnnotationScale(int index, double scale) =>
        !double.IsFinite(scale) ? AnnotationUnchanged(index) : EditAnnotation(index, annotation => annotation with
        {
            Scale = Math.Clamp(scale, StudioAnnotationGeometry.MinimumScale, StudioAnnotationGeometry.MaximumScale),
        });

    public StudioAnnotationEditResult SetAnnotationRotation(int index, double rotation) =>
        !double.IsFinite(rotation) ? AnnotationUnchanged(index) : EditAnnotation(index, annotation => annotation with
        {
            Rotation = StudioAnnotationGeometry.NormalizeRotation(rotation),
        });

    /// <summary>Where a drag on the preview leaves an annotation.</summary>
    public StudioAnnotationEditResult MoveAnnotation(int index, StudioAnnotationMove move) =>
        EditAnnotation(index, annotation => annotation with
        {
            X = move.X,
            Y = move.Y,
            OffsetX = move.OffsetX,
            OffsetY = move.OffsetY,
            Width = move.Width ?? annotation.Width,
            Height = move.Height ?? annotation.Height,
            Scale = move.Scale ?? annotation.Scale,
            Rotation = move.Rotation ?? annotation.Rotation,
        });

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
            if (StudioAnnotationKinds.IsArea(annotation.Kind))
            {
                var area = StudioAnnotationGeometry.GetAreaWhenAttached(annotation, attach, frame, canvas.Width, canvas.Height);
                return annotation with { Attach = attach, X = area.X, Y = area.Y, Width = area.Width, Height = area.Height };
            }

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
