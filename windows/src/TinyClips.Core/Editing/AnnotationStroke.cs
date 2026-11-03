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

/// <summary>Which stroke options each annotation tool uses; the inspector and renderers share these.</summary>
public static class AnnotationStroke
{
    public const StrokeCap DefaultCap = StrokeCap.Round;
    public const StrokeJoin DefaultJoin = StrokeJoin.Round;

    /// <summary>Lines and arrow tails take a start cap; an arrow's other end is its head.</summary>
    public static bool UsesStartCap(this EditTool tool) => tool is EditTool.Line or EditTool.Arrow;

    public static bool UsesEndCap(this EditTool tool) => tool is EditTool.Line;

    /// <summary>Only rectangles have corners; ellipses have none and pen strokes always round.</summary>
    public static bool UsesJoin(this EditTool tool) => tool is EditTool.Rectangle;

    /// <summary>
    /// Arrows and pen strokes are several overlapping pieces, so a translucent color has to be
    /// applied to them as one layer or the overlaps draw darker.
    /// </summary>
    public static bool NeedsOpacityLayer(this EditTool tool) => tool is EditTool.Arrow or EditTool.Pen;
}
