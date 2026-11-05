namespace TinyClips.Core.Studio;

/// <summary>A point in canvas pixels, with its origin at the top left.</summary>
public readonly record struct StudioPoint(double X, double Y);

/// <summary>The kinds of annotation this version draws. Any other is kept in the project and not drawn.</summary>
public static class StudioAnnotationKinds
{
    public const string Arrow = "arrow";
    public const string Callout = "callout";
    public const string Text = "text";
}

/// <summary>
/// One annotation at one moment, in canvas pixels, before any text is measured (section 6.10 of
/// the project format). The arrow's values are 0 for a box, and the box's are 0 for an arrow.
/// </summary>
/// <param name="Index">Its place in the project's list, which is also its place from the bottom.</param>
/// <param name="Anchor">What an arrow or a callout points at, or the center of a text box.</param>
/// <param name="BoxCenter">Where the box of a callout or a text is centered before it is pushed inside the canvas.</param>
public readonly record struct StudioResolvedAnnotation(
    int Index,
    string Kind,
    double Opacity,
    string Fill,
    string TextColor,
    StudioPoint Anchor,
    double Stroke,
    StudioPoint Tail,
    StudioPoint HeadBase,
    double HalfWidth,
    bool HasShaft,
    StudioPoint BoxCenter,
    double Font,
    double MaxTextWidth,
    double Radius,
    string Text)
{
    public bool IsArrow => Kind == StudioAnnotationKinds.Arrow;
}

/// <summary>Where a box goes once its text has been measured. The pointer is the two points of its base, when it has one.</summary>
public readonly record struct StudioAnnotationBox(
    StudioFrameRect Box,
    StudioFrameRect TextRect,
    bool HasPointer,
    StudioPoint PointerA,
    StudioPoint PointerB);

/// <summary>
/// The rules of section 6.10 of the project format, which the fixtures in
/// <c>shared/studio/fixtures/annotations</c> hold both platforms to. The same on the Mac
/// (<c>StudioAnnotations.swift</c>).
/// </summary>
public static class StudioAnnotationGeometry
{
    private const double HeadLengthInStrokes = 3.2;
    private const double HeadHalfWidthInLengths = 0.62;
    private const double PadXInFonts = 0.7;
    private const double PadYInFonts = 0.45;
    private const double RadiusInFonts = 0.4;
    private const double PointerHalfBaseInFonts = 0.45;
    private const double MaxBoxWidthInCanvases = 0.7;

    /// <summary>What <paramref name="annotation"/> draws at <paramref name="time"/>. False when it draws nothing then.</summary>
    public static bool TryResolve(
        StudioAnnotation annotation,
        int index,
        in StudioResolvedFrame frame,
        double time,
        double canvasWidth,
        double canvasHeight,
        out StudioResolvedAnnotation resolved)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        resolved = default;
        var kind = annotation.Kind;
        var isArrow = kind == StudioAnnotationKinds.Arrow;
        if (!isArrow && kind is not (StudioAnnotationKinds.Callout or StudioAnnotationKinds.Text))
        {
            return false;
        }

        if (!(annotation.Start <= time && time < annotation.End)
            || (!isArrow && IsBlank(annotation.Text))
            || !TryAnchor(annotation, frame, canvasWidth, canvasHeight, out var anchor, out var opacity)
            || !(opacity > 0))
        {
            return false;
        }

        var shortSide = Math.Min(canvasWidth, canvasHeight);
        var (fill, textColor) = Colors(annotation.Style);
        var (fontShare, strokeShare) = Sizes(annotation.Size);
        var font = shortSide * fontShare;
        var offset = new StudioPoint(
            Math.Clamp(annotation.OffsetX, -1, 1) * shortSide,
            Math.Clamp(annotation.OffsetY, -1, 1) * shortSide);

        if (isArrow)
        {
            var tail = new StudioPoint(anchor.X + offset.X, anchor.Y + offset.Y);
            var length = Math.Sqrt((offset.X * offset.X) + (offset.Y * offset.Y));
            if (!(length > 0))
            {
                return false;
            }

            var stroke = shortSide * strokeShare;
            var head = Math.Min(stroke * HeadLengthInStrokes, length);
            var headBase = new StudioPoint(anchor.X + (offset.X / length * head), anchor.Y + (offset.Y / length * head));
            resolved = new StudioResolvedAnnotation(
                index, kind, opacity, fill, textColor, anchor, stroke, tail, headBase, head * HeadHalfWidthInLengths, head < length, default, 0, 0, 0, string.Empty);
            return true;
        }

        var center = kind == StudioAnnotationKinds.Callout ? new StudioPoint(anchor.X + offset.X, anchor.Y + offset.Y) : anchor;
        var maxTextWidth = Math.Max(font, (canvasWidth * MaxBoxWidthInCanvases) - (2 * font * PadXInFonts));
        resolved = new StudioResolvedAnnotation(
            index, kind, opacity, fill, textColor, anchor, 0, default, default, 0, false, center, font, maxTextWidth, font * RadiusInFonts, annotation.Text);
        return true;
    }

    /// <summary>The box of a callout or a text whose text measures <paramref name="textWidth"/> by <paramref name="textHeight"/>.</summary>
    public static StudioAnnotationBox LayoutBox(in StudioResolvedAnnotation resolved, double textWidth, double textHeight, double canvasWidth, double canvasHeight)
    {
        var padX = resolved.Font * PadXInFonts;
        var padY = resolved.Font * PadYInFonts;
        var width = textWidth + (2 * padX);
        var height = textHeight + (2 * padY);

        // The lower bound last: a box larger than the canvas keeps its top and left in view.
        var x = Math.Max(0, Math.Min(resolved.BoxCenter.X - (width / 2), canvasWidth - width));
        var y = Math.Max(0, Math.Min(resolved.BoxCenter.Y - (height / 2), canvasHeight - height));
        var box = new StudioFrameRect(x, y, width, height);
        var textRect = new StudioFrameRect(x + padX, y + padY, textWidth, textHeight);
        if (resolved.Kind != StudioAnnotationKinds.Callout || Contains(box, resolved.Anchor))
        {
            return new StudioAnnotationBox(box, textRect, false, default, default);
        }

        var anchor = resolved.Anchor;
        var dx = Math.Max(Math.Max(x - anchor.X, anchor.X - (x + width)), 0);
        var dy = Math.Max(Math.Max(y - anchor.Y, anchor.Y - (y + height)), 0);
        var radius = resolved.Radius;
        var mostHalf = resolved.Font * PointerHalfBaseInFonts;
        if (dy >= dx)
        {
            var edge = anchor.Y < y ? y : y + height;
            var half = Math.Min(mostHalf, Math.Max(0, (width / 2) - radius));
            var along = Math.Max(x + radius + half, Math.Min(anchor.X, x + width - radius - half));
            return new StudioAnnotationBox(box, textRect, true, new StudioPoint(along - half, edge), new StudioPoint(along + half, edge));
        }
        else
        {
            var edge = anchor.X < x ? x : x + width;
            var half = Math.Min(mostHalf, Math.Max(0, (height / 2) - radius));
            var along = Math.Max(y + radius + half, Math.Min(anchor.Y, y + height - radius - half));
            return new StudioAnnotationBox(box, textRect, true, new StudioPoint(edge, along - half), new StudioPoint(edge, along + half));
        }
    }

    /// <summary>
    /// The numbers of the middle of what is in view: of the part of the recording a frame shows,
    /// or of the canvas. A frame with no screen has the middle of the recording.
    /// </summary>
    public static StudioPoint GetCenterInView(StudioAnnotationAttach attach, in StudioResolvedFrame frame) =>
        attach == StudioAnnotationAttach.Screen && frame.Screen is { } screen
            ? new StudioPoint(screen.Source.X + (screen.Source.Width / 2), screen.Source.Y + (screen.Source.Height / 2))
            : new StudioPoint(0.5, 0.5);

    /// <summary>
    /// The numbers that keep an annotation where it is in the picture once it is fixed to
    /// <paramref name="attach"/>. Its own numbers where it is not in the picture to begin with, or
    /// the frame has no screen to fix it to. A point beside the screen goes to the screen's edge.
    /// </summary>
    public static StudioPoint GetPointWhenAttached(StudioAnnotation annotation, StudioAnnotationAttach attach, in StudioResolvedFrame frame, double canvasWidth, double canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var own = new StudioPoint(annotation.X, annotation.Y);
        if (attach == annotation.Attach || !TryAnchor(annotation, frame, canvasWidth, canvasHeight, out var anchor, out _))
        {
            return own;
        }

        if (attach == StudioAnnotationAttach.Frame)
        {
            return new StudioPoint(anchor.X / canvasWidth, anchor.Y / canvasHeight);
        }

        if (frame.Screen is not { } screen || !(screen.Rect.Width > 0) || !(screen.Rect.Height > 0))
        {
            return own;
        }

        var acrossX = Math.Clamp((anchor.X - screen.Rect.X) / screen.Rect.Width, 0, 1);
        var acrossY = Math.Clamp((anchor.Y - screen.Rect.Y) / screen.Rect.Height, 0, 1);
        return new StudioPoint(screen.Source.X + (acrossX * screen.Source.Width), screen.Source.Y + (acrossY * screen.Source.Height));
    }

    /// <summary>
    /// True when any annotation comes or goes after <paramref name="from"/>, up to and including
    /// <paramref name="to"/>, so that a picture drawn for one of those times is not right for the other.
    /// </summary>
    public static bool ChangesBetween(IReadOnlyList<StudioAnnotation>? annotations, double from, double to)
    {
        if (annotations is null)
        {
            return false;
        }

        foreach (var annotation in annotations)
        {
            if ((annotation.Start > from && annotation.Start <= to) || (annotation.End > from && annotation.End <= to))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="text"/> has nothing to show, by the list in section 6.10. Not
    /// <see cref="string.IsNullOrWhiteSpace"/>: that keeps U+200B, which the Mac's list leaves out,
    /// and the same project has to draw the same boxes on both.
    /// </summary>
    public static bool IsBlank(string? text)
    {
        foreach (var character in text.AsSpan())
        {
            if (character is not ((>= '\u0009' and <= '\u000D') or ' ' or '\u0085' or '\u00A0' or '\u1680'
                or (>= '\u2000' and <= '\u200B') or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryAnchor(StudioAnnotation annotation, in StudioResolvedFrame frame, double canvasWidth, double canvasHeight, out StudioPoint anchor, out double opacity)
    {
        var x = Math.Clamp(annotation.X, 0, 1);
        var y = Math.Clamp(annotation.Y, 0, 1);
        if (annotation.Attach == StudioAnnotationAttach.Frame)
        {
            anchor = new StudioPoint(x * canvasWidth, y * canvasHeight);
            opacity = 1;
            return true;
        }

        anchor = default;
        opacity = 0;
        if (frame.Screen is not { } screen)
        {
            return false;
        }

        var source = screen.Source;
        if (!(source.Width > 0) || !(source.Height > 0)
            || x < source.X || x > source.X + source.Width
            || y < source.Y || y > source.Y + source.Height)
        {
            return false;
        }

        anchor = new StudioPoint(
            screen.Rect.X + ((x - source.X) / source.Width * screen.Rect.Width),
            screen.Rect.Y + ((y - source.Y) / source.Height * screen.Rect.Height));
        opacity = screen.Opacity;
        return true;
    }

    private static bool Contains(in StudioFrameRect box, StudioPoint point) =>
        point.X >= box.X && point.X <= box.X + box.Width && point.Y >= box.Y && point.Y <= box.Y + box.Height;

    private static (string Fill, string Text) Colors(StudioAnnotationStyle style) => style switch
    {
        StudioAnnotationStyle.Blue => ("#0A84FF", "#FFFFFF"),
        StudioAnnotationStyle.Yellow => ("#FFD60A", "#1C1C1E"),
        StudioAnnotationStyle.White => ("#FFFFFF", "#1C1C1E"),
        StudioAnnotationStyle.Dark => ("#1C1C1E", "#FFFFFF"),
        _ => ("#FF453A", "#FFFFFF"),
    };

    private static (double Font, double Stroke) Sizes(StudioAnnotationSize size) => size switch
    {
        StudioAnnotationSize.Small => (0.028, 0.006),
        StudioAnnotationSize.Large => (0.05, 0.013),
        _ => (0.036, 0.009),
    };
}
