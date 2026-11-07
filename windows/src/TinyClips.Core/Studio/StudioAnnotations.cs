namespace TinyClips.Core.Studio;

/// <summary>A point in canvas pixels, with its origin at the top left.</summary>
public readonly record struct StudioPoint(double X, double Y);

/// <summary>The kinds of annotation this version draws. Any other is kept in the project and not drawn.</summary>
public static class StudioAnnotationKinds
{
    public const string Arrow = "arrow";
    public const string Callout = "callout";
    public const string Text = "text";
    public const string Rectangle = "rectangle";
    public const string Ellipse = "ellipse";
    public const string Redact = "redact";
    public const string Emoji = "emoji";

    /// <summary>Whether a kind covers an area (a rectangle, an ellipse or a redaction) instead of pointing at a place.</summary>
    public static bool IsArea(string? kind) => kind is Rectangle or Ellipse or Redact;
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

    /// <summary>Whether it covers <see cref="Rect"/> (a rectangle, an ellipse or a redaction).</summary>
    public bool IsArea => StudioAnnotationKinds.IsArea(Kind);

    /// <summary>The area a rectangle, an ellipse or a redaction covers.</summary>
    public StudioFrameRect Rect { get; init; }

    /// <summary>The inside of a rectangle or an ellipse in <c>#RRGGBB</c>, or empty when it has none.</summary>
    public string ShapeFill { get; init; } = string.Empty;

    public double ShapeFillOpacity { get; init; }

    public StudioRedactStyle RedactStyle { get; init; }

    /// <summary>The blur's standard deviation, or the side of a pixelation's cells, in canvas pixels. 0 for a solid redaction.</summary>
    public double RedactAmount { get; init; }

    /// <summary>Whether it is an emoji, which is drawn in color with no box around it.</summary>
    public bool IsEmoji => Kind == StudioAnnotationKinds.Emoji;

    /// <summary>Whether an emoji has a drop shadow under it.</summary>
    public bool HasShadow { get; init; }
}

/// <summary>What a pointer can take hold of on an annotation in the preview.</summary>
public enum StudioAnnotationPart
{
    /// <summary>What an arrow or a callout points at.</summary>
    Tip,

    /// <summary>The far end of an arrow.</summary>
    Tail,

    /// <summary>A whole arrow, by its shaft.</summary>
    Body,

    /// <summary>The box of a callout or a text, or the whole of an area.</summary>
    Box,

    /// <summary>A corner of an area.</summary>
    TopLeft,

    /// <summary>A corner of an area.</summary>
    TopRight,

    /// <summary>A corner of an area.</summary>
    BottomLeft,

    /// <summary>A corner of an area.</summary>
    BottomRight,
}

/// <summary>Where a drag leaves an annotation, in the numbers of the project, and whether it has settled on a center line of the canvas.</summary>
public readonly record struct StudioAnnotationMove(
    double X,
    double Y,
    double OffsetX,
    double OffsetY,
    bool IsOnVerticalCenter,
    bool IsOnHorizontalCenter)
{
    /// <summary>The width an area has after the drag. Null for any other kind.</summary>
    public double? Width { get; init; }

    /// <summary>The height an area has after the drag. Null for any other kind.</summary>
    public double? Height { get; init; }
}

/// <summary>One annotation on the picture, in canvas pixels, for a pointer to take hold of.</summary>
/// <param name="Tip">What an arrow or a callout points at, or the center of a text box.</param>
/// <param name="Tail">The far end of an arrow, which has no <paramref name="Box"/>.</param>
/// <param name="Box">The box of a callout or a text, which has no <paramref name="Tail"/>.</param>
public readonly record struct StudioAnnotationHandle(
    int Index,
    string Kind,
    StudioPoint Tip,
    StudioPoint? Tail,
    double Stroke,
    StudioFrameRect? Box,
    double Radius);

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
    private const double SnapShareOfShortSide = 0.008;
    private const double ShortestArrowInShortSides = 0.02;
    private const double SmallestAreaInShortSides = 0.02;

    public const double MinimumScale = 0.5;
    public const double MaximumScale = 3;

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
        if (StudioAnnotationKinds.IsArea(kind))
        {
            return TryResolveArea(annotation, index, frame, time, canvasWidth, canvasHeight, out resolved);
        }

        var isArrow = kind == StudioAnnotationKinds.Arrow;
        if (!isArrow && kind is not (StudioAnnotationKinds.Callout or StudioAnnotationKinds.Text or StudioAnnotationKinds.Emoji))
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
        var (fill, textColor) = GetEffectiveColors(annotation);
        var (fontShare, strokeShare) = Sizes(annotation.Size);
        var scale = Math.Clamp(annotation.Scale, MinimumScale, MaximumScale);
        var font = shortSide * fontShare * scale;
        if (kind == StudioAnnotationKinds.Emoji)
        {
            // An emoji does not wrap, so it has no widest line.
            var emojiFont = shortSide * EmojiSize(annotation.Size) * scale;
            resolved = new StudioResolvedAnnotation(
                index, kind, opacity, fill, textColor, anchor, 0, default, default, 0, false, anchor, emojiFont, double.PositiveInfinity, 0, annotation.Text)
            {
                HasShadow = annotation.Shadow,
            };
            return true;
        }
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

            var stroke = shortSide * strokeShare * scale;
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

    private static bool TryResolveArea(
        StudioAnnotation annotation,
        int index,
        in StudioResolvedFrame frame,
        double time,
        double canvasWidth,
        double canvasHeight,
        out StudioResolvedAnnotation resolved)
    {
        resolved = default;
        if (!(annotation.Start <= time && time < annotation.End)
            || !TryGetAreaRect(annotation, frame, canvasWidth, canvasHeight, out var rect, out var opacity)
            || !(opacity > 0))
        {
            return false;
        }

        var shortSide = Math.Min(canvasWidth, canvasHeight);
        var (fill, textColor) = GetEffectiveColors(annotation);
        var center = new StudioPoint(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
        if (annotation.Kind == StudioAnnotationKinds.Redact)
        {
            var style = annotation.RedactStyle;
            var amount = style switch
            {
                StudioRedactStyle.Blur => shortSide * annotation.RedactLevel switch
                {
                    StudioRedactLevel.Light => 0.006,
                    StudioRedactLevel.Heavy => 0.022,
                    _ => 0.012,
                },
                StudioRedactStyle.Pixelate => shortSide * annotation.RedactLevel switch
                {
                    StudioRedactLevel.Light => 0.008,
                    StudioRedactLevel.Heavy => 0.028,
                    _ => 0.016,
                },
                _ => 0,
            };
            resolved = new StudioResolvedAnnotation(
                index, annotation.Kind, opacity, fill, textColor, center, 0, default, default, 0, false, default, 0, 0, 0, string.Empty)
            {
                Rect = rect,
                RedactStyle = style,
                RedactAmount = amount,
            };
            return true;
        }

        var (_, strokeShare) = Sizes(annotation.Size);
        var stroke = shortSide * strokeShare * Math.Clamp(annotation.Scale, MinimumScale, MaximumScale);
        var radius = annotation.Kind == StudioAnnotationKinds.Rectangle
            ? Math.Min(Math.Min(Math.Clamp(annotation.CornerRadius, 0, 0.5) * shortSide, rect.Width / 2), rect.Height / 2)
            : 0;
        resolved = new StudioResolvedAnnotation(
            index, annotation.Kind, opacity, fill, textColor, center, stroke, default, default, 0, false, default, 0, 0, radius, string.Empty)
        {
            Rect = rect,
            ShapeFill = GetCustomColor(annotation.ShapeFill) ?? string.Empty,
            ShapeFillOpacity = Math.Clamp(annotation.ShapeFillOpacity, 0, 1),
        };
        return true;
    }

    /// <summary>
    /// The area a rectangle, an ellipse or a redaction covers in canvas pixels, and how opaque it
    /// is. False when it has no size, or when it is fixed to the screen and none of it is in view.
    /// </summary>
    public static bool TryGetAreaRect(StudioAnnotation annotation, in StudioResolvedFrame frame, double canvasWidth, double canvasHeight, out StudioFrameRect rect, out double opacity)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        rect = default;
        opacity = 0;
        var x = Math.Clamp(annotation.X, 0, 1);
        var y = Math.Clamp(annotation.Y, 0, 1);
        var width = Math.Clamp(annotation.Width, 0, 1);
        var height = Math.Clamp(annotation.Height, 0, 1);
        if (!(width > 0) || !(height > 0))
        {
            return false;
        }

        var loX = x - (width / 2);
        var loY = y - (height / 2);
        if (annotation.Attach == StudioAnnotationAttach.Frame)
        {
            rect = new StudioFrameRect(loX * canvasWidth, loY * canvasHeight, width * canvasWidth, height * canvasHeight);
            opacity = 1;
            return true;
        }

        if (frame.Screen is not { } screen)
        {
            return false;
        }

        var source = screen.Source;
        if (!(source.Width > 0) || !(source.Height > 0)
            || !(loX < source.X + source.Width && loX + width > source.X)
            || !(loY < source.Y + source.Height && loY + height > source.Y))
        {
            return false;
        }

        rect = new StudioFrameRect(
            screen.Rect.X + ((loX - source.X) / source.Width * screen.Rect.Width),
            screen.Rect.Y + ((loY - source.Y) / source.Height * screen.Rect.Height),
            width / source.Width * screen.Rect.Width,
            height / source.Height * screen.Rect.Height);
        opacity = screen.Opacity;
        return true;
    }

    /// <summary>
    /// The numbers a rectangle, an ellipse or a redaction has once it is dragged by some canvas
    /// pixels from where it was at the start of the drag: moved by <see cref="StudioAnnotationPart.Box"/>,
    /// or resized by a corner while the opposite corner stays where it is. What is dragged settles
    /// on the middle of the canvas when it comes close, and the area never grows too small to take
    /// hold of again. Null when it isn't in the picture.
    /// </summary>
    public static StudioAnnotationMove? GetAreaMoved(
        StudioAnnotation annotation,
        StudioAnnotationPart part,
        double byX,
        double byY,
        in StudioResolvedFrame frame,
        double canvasWidth,
        double canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (!double.IsFinite(byX) || !double.IsFinite(byY) || !(canvasWidth > 0) || !(canvasHeight > 0)
            || !TryGetAreaRect(annotation, frame, canvasWidth, canvasHeight, out var rect, out _))
        {
            return null;
        }

        var shortSide = Math.Min(canvasWidth, canvasHeight);
        var reach = shortSide * SnapShareOfShortSide;
        var smallest = shortSide * SmallestAreaInShortSides;
        var midX = canvasWidth / 2;
        var midY = canvasHeight / 2;
        double left = rect.X, top = rect.Y, right = rect.X + rect.Width, bottom = rect.Y + rect.Height;
        bool isOnVerticalCenter, isOnHorizontalCenter;
        if (part is StudioAnnotationPart.TopLeft or StudioAnnotationPart.TopRight or StudioAnnotationPart.BottomLeft or StudioAnnotationPart.BottomRight)
        {
            var isLeft = part is StudioAnnotationPart.TopLeft or StudioAnnotationPart.BottomLeft;
            var isTop = part is StudioAnnotationPart.TopLeft or StudioAnnotationPart.TopRight;
            var x = (isLeft ? left : right) + byX;
            var y = (isTop ? top : bottom) + byY;
            isOnVerticalCenter = Math.Abs(x - midX) <= reach;
            isOnHorizontalCenter = Math.Abs(y - midY) <= reach;
            x = isOnVerticalCenter ? midX : x;
            y = isOnHorizontalCenter ? midY : y;
            if (isLeft)
            {
                left = Math.Min(x, right - smallest);
            }
            else
            {
                right = Math.Max(x, left + smallest);
            }

            if (isTop)
            {
                top = Math.Min(y, bottom - smallest);
            }
            else
            {
                bottom = Math.Max(y, top + smallest);
            }
        }
        else
        {
            var centerX = left + (rect.Width / 2) + byX;
            var centerY = top + (rect.Height / 2) + byY;
            isOnVerticalCenter = Math.Abs(centerX - midX) <= reach;
            isOnHorizontalCenter = Math.Abs(centerY - midY) <= reach;
            centerX = isOnVerticalCenter ? midX : centerX;
            centerY = isOnHorizontalCenter ? midY : centerY;
            left = centerX - (rect.Width / 2);
            right = centerX + (rect.Width / 2);
            top = centerY - (rect.Height / 2);
            bottom = centerY + (rect.Height / 2);
        }

        // From canvas pixels back to the numbers of the canvas, or of the recording where the frame shows it.
        double originX = 0, originY = 0, spanX = 1, spanY = 1, pixelsX = canvasWidth, pixelsY = canvasHeight, offsetX = 0, offsetY = 0;
        if (annotation.Attach == StudioAnnotationAttach.Screen)
        {
            if (frame.Screen is not { } screen || !(screen.Rect.Width > 0) || !(screen.Rect.Height > 0))
            {
                return null;
            }

            originX = screen.Source.X;
            originY = screen.Source.Y;
            spanX = screen.Source.Width;
            spanY = screen.Source.Height;
            pixelsX = screen.Rect.Width;
            pixelsY = screen.Rect.Height;
            offsetX = screen.Rect.X;
            offsetY = screen.Rect.Y;
        }

        var width = Math.Clamp((right - left) / pixelsX * spanX, 0, 1);
        var height = Math.Clamp((bottom - top) / pixelsY * spanY, 0, 1);
        var cx = originX + (((left + right) / 2) - offsetX) / pixelsX * spanX;
        var cy = originY + (((top + bottom) / 2) - offsetY) / pixelsY * spanY;

        // A screen area keeps its middle in view, so that it is not lost from the picture by a drag.
        if (annotation.Attach == StudioAnnotationAttach.Screen)
        {
            cx = Math.Clamp(cx, originX, originX + spanX);
            cy = Math.Clamp(cy, originY, originY + spanY);
        }

        return new StudioAnnotationMove(
            Math.Clamp(cx, 0, 1), Math.Clamp(cy, 0, 1), annotation.OffsetX, annotation.OffsetY, isOnVerticalCenter, isOnHorizontalCenter)
        {
            Width = width,
            Height = height,
        };
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

    /// <summary>Where an emoji whose text measures <paramref name="textWidth"/> by <paramref name="textHeight"/> goes: centered on its anchor and pushed inside the canvas.</summary>
    public static StudioFrameRect LayoutEmoji(in StudioResolvedAnnotation resolved, double textWidth, double textHeight, double canvasWidth, double canvasHeight) =>
        new(
            Math.Max(0, Math.Min(resolved.Anchor.X - (textWidth / 2), canvasWidth - textWidth)),
            Math.Max(0, Math.Min(resolved.Anchor.Y - (textHeight / 2), canvasHeight - textHeight)),
            textWidth,
            textHeight);

    /// <summary>How far down an emoji's shadow is moved, and its blur's standard deviation, in canvas pixels.</summary>
    public static (double OffsetY, double Blur) GetEmojiShadow(double font) => (font * 0.04, font * 0.06);

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
    /// The middle and size that keep a rectangle, an ellipse or a redaction where it is in the
    /// picture once it is fixed to <paramref name="attach"/>. Its own numbers where it is not in the
    /// picture to begin with, or the frame has no screen to fix it to.
    /// </summary>
    public static (double X, double Y, double Width, double Height) GetAreaWhenAttached(StudioAnnotation annotation, StudioAnnotationAttach attach, in StudioResolvedFrame frame, double canvasWidth, double canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var own = (annotation.X, annotation.Y, annotation.Width, annotation.Height);
        if (attach == annotation.Attach || !(canvasWidth > 0) || !(canvasHeight > 0)
            || !TryGetAreaRect(annotation, frame, canvasWidth, canvasHeight, out var rect, out _))
        {
            return own;
        }

        var centerX = rect.X + (rect.Width / 2);
        var centerY = rect.Y + (rect.Height / 2);
        if (attach == StudioAnnotationAttach.Frame)
        {
            return (Math.Clamp(centerX / canvasWidth, 0, 1), Math.Clamp(centerY / canvasHeight, 0, 1),
                Math.Clamp(rect.Width / canvasWidth, 0, 1), Math.Clamp(rect.Height / canvasHeight, 0, 1));
        }

        if (frame.Screen is not { } screen || !(screen.Rect.Width > 0) || !(screen.Rect.Height > 0))
        {
            return own;
        }

        var acrossX = Math.Clamp((centerX - screen.Rect.X) / screen.Rect.Width, 0, 1);
        var acrossY = Math.Clamp((centerY - screen.Rect.Y) / screen.Rect.Height, 0, 1);
        return (screen.Source.X + (acrossX * screen.Source.Width), screen.Source.Y + (acrossY * screen.Source.Height),
            Math.Clamp(rect.Width / screen.Rect.Width * screen.Source.Width, 0, 1),
            Math.Clamp(rect.Height / screen.Rect.Height * screen.Source.Height, 0, 1));
    }

    /// <summary>
    /// The numbers an annotation has once a part of it is dragged by some canvas pixels from where
    /// it was at the start of the drag, with where its box was drawn then, if it has one. What is not dragged stays where it is in the picture: the
    /// tail of an arrow whose tip moves, the box of a callout whose point moves. Whatever is dragged
    /// settles on the middle of the canvas when it comes close. Null when it isn't in the picture,
    /// or when it would make an arrow too short to see or to take hold of again.
    /// </summary>
    public static StudioAnnotationMove? GetMoved(
        StudioAnnotation annotation,
        StudioAnnotationPart part,
        double byX,
        double byY,
        in StudioResolvedFrame frame,
        double canvasWidth,
        double canvasHeight,
        StudioPoint? drawnBoxCenter = null)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (!double.IsFinite(byX) || !double.IsFinite(byY) || !(canvasWidth > 0) || !(canvasHeight > 0)
            || !TryAnchor(annotation, frame, canvasWidth, canvasHeight, out var anchor, out _))
        {
            return null;
        }

        var shortSide = Math.Min(canvasWidth, canvasHeight);
        var offsetX = Math.Clamp(annotation.OffsetX, -1, 1);
        var offsetY = Math.Clamp(annotation.OffsetY, -1, 1);
        var far = new StudioPoint(anchor.X + (offsetX * shortSide), anchor.Y + (offsetY * shortSide));
        var isFarEnd = part == StudioAnnotationPart.Tail || (part == StudioAnnotationPart.Box && annotation.Kind == StudioAnnotationKinds.Callout);

        // A box pushed inside the canvas is not where its numbers say, and it is what was taken hold of.
        var from = part == StudioAnnotationPart.Box && drawnBoxCenter is { } center ? center : isFarEnd ? far : anchor;

        var x = from.X + byX;
        var y = from.Y + byY;
        var reach = shortSide * SnapShareOfShortSide;
        var isOnVerticalCenter = Math.Abs(x - (canvasWidth / 2)) <= reach;
        var isOnHorizontalCenter = Math.Abs(y - (canvasHeight / 2)) <= reach;
        var target = new StudioPoint(isOnVerticalCenter ? canvasWidth / 2 : x, isOnHorizontalCenter ? canvasHeight / 2 : y);

        var move = new StudioAnnotationMove(
            Math.Clamp(annotation.X, 0, 1), Math.Clamp(annotation.Y, 0, 1), offsetX, offsetY, isOnVerticalCenter, isOnHorizontalCenter);
        if (isFarEnd)
        {
            move = move with
            {
                OffsetX = Math.Clamp((target.X - anchor.X) / shortSide, -1, 1),
                OffsetY = Math.Clamp((target.Y - anchor.Y) / shortSide, -1, 1),
            };
        }
        else
        {
            if (TryGetPointAt(target, annotation.Attach, frame, canvasWidth, canvasHeight) is not { } moved)
            {
                return null;
            }

            move = move with { X = moved.X, Y = moved.Y };
            if (part == StudioAnnotationPart.Tip)
            {
                if (!TryAnchor(annotation with { X = moved.X, Y = moved.Y }, frame, canvasWidth, canvasHeight, out var newAnchor, out _))
                {
                    return null;
                }

                move = move with
                {
                    OffsetX = Math.Clamp((far.X - newAnchor.X) / shortSide, -1, 1),
                    OffsetY = Math.Clamp((far.Y - newAnchor.Y) / shortSide, -1, 1),
                };
            }
        }

        var isTooShort = annotation.Kind == StudioAnnotationKinds.Arrow && part != StudioAnnotationPart.Body
            && Math.Sqrt((move.OffsetX * move.OffsetX) + (move.OffsetY * move.OffsetY)) < ShortestArrowInShortSides;
        return isTooShort ? null : move;
    }

    /// <summary>The numbers of a point on the canvas: in the canvas, or in the recording where the frame shows it. A point beside the screen goes to the screen's edge.</summary>
    private static StudioPoint? TryGetPointAt(StudioPoint point, StudioAnnotationAttach attach, in StudioResolvedFrame frame, double canvasWidth, double canvasHeight)
    {
        if (attach == StudioAnnotationAttach.Frame)
        {
            return new StudioPoint(Math.Clamp(point.X / canvasWidth, 0, 1), Math.Clamp(point.Y / canvasHeight, 0, 1));
        }

        if (frame.Screen is not { } screen || !(screen.Rect.Width > 0) || !(screen.Rect.Height > 0))
        {
            return null;
        }

        var acrossX = Math.Clamp((point.X - screen.Rect.X) / screen.Rect.Width, 0, 1);
        var acrossY = Math.Clamp((point.Y - screen.Rect.Y) / screen.Rect.Height, 0, 1);
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

    /// <summary>The colors an annotation is drawn in: its own where it has them, and otherwise its style's.</summary>
    public static (string Fill, string Text) GetEffectiveColors(StudioAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var style = GetStyleColors(annotation.Style);
        return (GetCustomColor(annotation.Fill) ?? style.Fill, GetCustomColor(annotation.TextColor) ?? style.Text);
    }

    /// <summary>A text in capitals when it is <c>#RRGGBB</c>. Null for anything else, which leaves the style's color.</summary>
    public static string? GetCustomColor(string? text) =>
        text is { Length: 7 } && text[0] == '#' && text.AsSpan(1).IndexOfAnyExcept(HexDigits) < 0 ? text.ToUpperInvariant() : null;

    private static readonly System.Buffers.SearchValues<char> HexDigits =
        System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");

    /// <summary>The colors a style gives, which the pickers show until they are changed.</summary>
    public static (string Fill, string Text) GetStyleColors(StudioAnnotationStyle style) => style switch
    {
        StudioAnnotationStyle.Blue => ("#0A84FF", "#FFFFFF"),
        StudioAnnotationStyle.Yellow => ("#FFD60A", "#1C1C1E"),
        StudioAnnotationStyle.White => ("#FFFFFF", "#1C1C1E"),
        StudioAnnotationStyle.Dark => ("#1C1C1E", "#FFFFFF"),
        _ => ("#FF453A", "#FFFFFF"),
    };

    /// <summary>An emoji's font at a scale of 1, in canvas short sides.</summary>
    private static double EmojiSize(StudioAnnotationSize size) => size switch
    {
        StudioAnnotationSize.Small => 0.06,
        StudioAnnotationSize.Large => 0.13,
        _ => 0.09,
    };

    private static (double Font, double Stroke) Sizes(StudioAnnotationSize size) => size switch
    {
        StudioAnnotationSize.Small => (0.028, 0.006),
        StudioAnnotationSize.Large => (0.05, 0.013),
        _ => (0.036, 0.009),
    };
}
