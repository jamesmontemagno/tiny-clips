using System.Numerics;
using Windows.Foundation;

namespace TinyClips.Core.Editing;

/// <summary>Body hit testing in image pixels. The canvas tests its visible handles in DIPs first.</summary>
public static class ScreenshotAnnotationHitTest
{
    public static T? FindBody<T>(EditTool tool, IReadOnlyList<T> annotations, T? selected, Point pixel)
        where T : ScreenshotAnnotation
    {
        if (tool is EditTool.Crop or EditTool.Background) return null;
        if (tool != EditTool.Select)
        {
            // A drawing tool can adjust its selection, never pick another item underneath.
            return selected is not null && Contains(selected, pixel, useLineOutline: true) ? selected : null;
        }

        for (var i = annotations.Count - 1; i >= 0; i--)
        {
            if (Contains(annotations[i], pixel, useLineOutline: false)) return annotations[i];
        }
        return null;
    }

    private static bool Contains(ScreenshotAnnotation ann, Point pixel, bool useLineOutline)
    {
        var b = InteractionBounds(ann);
        var point = new PointD(pixel.X, pixel.Y);
        if (ann.Tool is EditTool.Emoji or EditTool.Pointer || ann.IsRotated)
        {
            return RotatableAnnotationGeometry.Contains(point, new RectD(b.X, b.Y, b.Width, b.Height),
                ann.Rotation, padding: ann.Thickness + 6);
        }

        var pad = ann.Thickness + ann.OutlineWidth + 6;
        if (ann.Tool == EditTool.Arrow || (useLineOutline && ann.Tool == EditTool.Line))
        {
            // Endpoints are tested by the canvas. A press far from the visible shaft of a
            // selected diagonal line must start a drawing, even within its axis-aligned bounds.
            // Explicit Select keeps its more forgiving historical bounds test for lines.
            var (s, e) = Segment(ann);
            var start = new PointD(s.X, s.Y);
            var end = new PointD(e.X, e.Y);
            if (ann.Tool == EditTool.Arrow && ArrowGeometry.DistanceToCurve(point, start, end, ann.Arrow.Bend) <= pad)
                return true;
            var outline = ann.Tool == EditTool.Line
                ? LineGeometry.Outline(start, end, ann.Thickness, ann.StartCap, ann.EndCap)
                : ArrowGeometry.Build(start, end, ann.Thickness, ann.Arrow, ann.StartCap, ann.LineJoin).Points;
            var reach = ann.StrokeAlignment == StrokeAlignment.Inside ? 0 : ann.OutlineWidth;
            return ArrowGeometry.OutlineContains(outline, point, reach + 6);
        }

        return new Rect(b.X - pad, b.Y - pad, b.Width + pad * 2, b.Height + pad * 2).Contains(pixel);
    }

    public static Rect InteractionBounds(ScreenshotAnnotation ann)
    {
        var bounds = NormalizeBounds(ann.Bounds);
        return ann.Tool == EditTool.Text ? ann.TextBoxStyle.DecoratedBounds(bounds) : bounds;
    }

    public static Rect NormalizeBounds(Rect b) =>
        new(b.Width < 0 ? b.X + b.Width : b.X, b.Height < 0 ? b.Y + b.Height : b.Y,
            Math.Abs(b.Width), Math.Abs(b.Height));

    public static (Vector2 Start, Vector2 End) Segment(ScreenshotAnnotation ann)
    {
        if (ann.Points.Count >= 2) return (ann.Points[0], ann.Points[^1]);
        var start = new Vector2((float)ann.Bounds.X, (float)ann.Bounds.Y);
        return (start, start);
    }
}
