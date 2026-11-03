namespace TinyClips.Core.Editing;

/// <summary>
/// A straight line as one filled outline, shared with macOS through
/// <c>shared/screenshot/fixtures/line</c>. Points are y-down; <c>scale</c> is the caller's units
/// per image pixel. Filling the outline (rather than stroking the line) lets each end take its own
/// cap and lets the line carry an outline of its own, like an arrow.
/// </summary>
public static class LineGeometry
{
    private const int CapSegments = 8;

    public static IReadOnlyList<PointD> Outline(PointD start, PointD end, double width, StrokeCap startCap, StrokeCap endCap, double scale = 1)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.001)
        {
            return Array.Empty<PointD>();
        }
        var u = new PointD(dx * (1 / length), dy * (1 / length));
        var n = new PointD(-u.Y, u.X);
        var half = width / 2 * scale;
        var points = new List<PointD>(4 + 2 * CapSegments)
        {
            new(start.X - n.X * half, start.Y - n.Y * half),
            new(end.X - n.X * half, end.Y - n.Y * half),
        };
        AddCap(points, end, u, n, half, endCap);
        points.Add(new PointD(end.X + n.X * half, end.Y + n.Y * half));
        points.Add(new PointD(start.X + n.X * half, start.Y + n.Y * half));
        AddCap(points, start, new PointD(-u.X, -u.Y), new PointD(-n.X, -n.Y), half, startCap);
        return points;
    }

    /// <summary>
    /// Points around one end between the line's two edges, going away along
    /// <paramref name="outward"/>. <paramref name="normal"/> points from the edge the outline
    /// arrives on to the one it leaves by.
    /// </summary>
    private static void AddCap(List<PointD> points, PointD center, PointD outward, PointD normal, double half, StrokeCap cap)
    {
        if (cap == StrokeCap.Round)
        {
            for (var j = 1; j < CapSegments; j++)
            {
                var theta = Math.PI * j / CapSegments;
                var along = -half * Math.Cos(theta);
                var away = half * Math.Sin(theta);
                points.Add(new PointD(
                    center.X + (normal.X * along + outward.X * away),
                    center.Y + (normal.Y * along + outward.Y * away)));
            }
        }
        else if (cap == StrokeCap.Square)
        {
            points.Add(new PointD(center.X - normal.X * half + outward.X * half, center.Y - normal.Y * half + outward.Y * half));
            points.Add(new PointD(center.X + normal.X * half + outward.X * half, center.Y + normal.Y * half + outward.Y * half));
        }
    }
}
