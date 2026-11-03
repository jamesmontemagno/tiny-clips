namespace TinyClips.Core.Editing;

/// <summary>How an open stroke ends. Matches the macOS <c>StrokeCap</c> raw values.</summary>
public enum StrokeCap
{
    Butt,
    Round,
    Square,
}

/// <summary>How a stroke turns a corner. Matches the macOS <c>StrokeJoin</c> raw values.</summary>
public enum StrokeJoin
{
    Miter,
    Round,
    Bevel,
}

/// <summary>Where a closed shape's stroke sits relative to its outline.</summary>
public enum StrokeAlignment
{
    Center,
    Inside,
    Outside,
}

/// <summary>
/// A rectangle's stroke as a filled ring: <see cref="Outer"/> with its corners shaped by
/// <see cref="OuterJoin"/> (rounded or cut by <see cref="CornerSize"/>), minus the always-sharp
/// <see cref="Inner"/> rectangle. Fill it with the even-odd rule.
/// </summary>
public readonly record struct RectStrokeRing(RectD Outer, StrokeJoin OuterJoin, double CornerSize, RectD Inner);

/// <summary>Stroke alignment geometry shared with macOS through <c>shared/screenshot/fixtures/alignment</c>.</summary>
public static class StrokeAlignmentGeometry
{
    public static RectStrokeRing RectangleRing(RectD bounds, double width, StrokeAlignment alignment, StrokeJoin join)
    {
        var outerOffset = alignment switch
        {
            StrokeAlignment.Inside => 0,
            StrokeAlignment.Outside => width,
            _ => width / 2,
        };
        var innerInset = alignment switch
        {
            StrokeAlignment.Inside => width,
            StrokeAlignment.Outside => 0,
            _ => width / 2,
        };
        return new RectStrokeRing(
            Inset(bounds, -outerOffset),
            outerOffset > 0 ? join : StrokeJoin.Miter,
            outerOffset > 0 && join != StrokeJoin.Miter ? outerOffset : 0,
            Inset(bounds, innerInset));
    }

    /// <summary>
    /// The ellipse to stroke at the plain width for an alignment. An ellipse's offset curve is not
    /// an ellipse, so this is only an approximation for renderers that cannot clip; exact
    /// renderers stroke at double width clipped to (or outside) the shape instead.
    /// </summary>
    public static RectD EllipseCenterline(RectD bounds, double width, StrokeAlignment alignment) =>
        Inset(bounds, alignment switch
        {
            StrokeAlignment.Inside => width / 2,
            StrokeAlignment.Outside => -width / 2,
            _ => 0,
        });

    /// <summary>Insets by <paramref name="amount"/> (outsets when negative), never past an empty rectangle.</summary>
    public static RectD Inset(RectD rect, double amount)
    {
        var dx = Math.Min(amount, rect.Width / 2);
        var dy = Math.Min(amount, rect.Height / 2);
        return new RectD(rect.X + dx, rect.Y + dy, rect.Width - 2 * dx, rect.Height - 2 * dy);
    }
}

/// <summary>Which stroke options each annotation tool uses; the inspector and renderers share these.</summary>
public static class AnnotationStroke
{
    public const StrokeCap DefaultCap = StrokeCap.Round;
    public const StrokeJoin DefaultJoin = StrokeJoin.Round;

    /// <summary>Lines and arrow tails take a start cap; an arrow's other end is its head.</summary>
    public static bool UsesStartCap(this EditTool tool) => tool is EditTool.Line or EditTool.Arrow;

    public static bool UsesEndCap(this EditTool tool) => tool is EditTool.Line;

    /// <summary>Rectangles and arrowheads have corners; ellipses have none and pen strokes always round.</summary>
    public static bool UsesJoin(this EditTool tool) => tool is EditTool.Rectangle or EditTool.Arrow;

    /// <summary>Closed shapes align their stroke; arrows and lines align their outline.</summary>
    public static bool UsesAlignment(this EditTool tool) =>
        tool is EditTool.Rectangle or EditTool.Ellipse or EditTool.Arrow or EditTool.Line;

    /// <summary>Arrows and lines are filled bodies (in the annotation's color, at its thickness)
    /// that can carry a separate outline.</summary>
    public static bool UsesOutline(this EditTool tool) => tool is EditTool.Arrow or EditTool.Line;

    /// <summary>
    /// Pen strokes are drawn as overlapping segments, so a translucent color has to be applied to
    /// them as one layer or the overlaps draw darker. Arrows are a single outline and need none.
    /// </summary>
    public static bool NeedsOpacityLayer(this EditTool tool) => tool is EditTool.Pen;
}
