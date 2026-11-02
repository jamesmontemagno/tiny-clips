namespace TinyClips.Core.Editing;

/// <summary>
/// Geometry for a straight or curved arrow. The shaft is a quadratic Bézier from
/// <see cref="ShaftStart"/> through <see cref="ShaftControl"/> to <see cref="ShaftEnd"/> (a straight
/// shaft repeats its end as the control), and the head is the triangle
/// <see cref="Tip"/>, <see cref="Head1"/>, <see cref="Head2"/>.
/// </summary>
public readonly record struct ArrowShape(
    bool Curved,
    PointD ShaftStart,
    PointD ShaftControl,
    PointD ShaftEnd,
    PointD Tip,
    PointD Head1,
    PointD Head2);

/// <summary>
/// Arrow geometry shared by the live preview, the export renderer, and the macOS app through
/// <c>shared/screenshot/fixtures/arrow</c>. Points are y-down. Widths are in source-image pixels;
/// <c>scale</c> is the caller's units per image pixel, so the preview and the export draw the
/// same proportions.
/// </summary>
public static class ArrowGeometry
{
    /// <summary>Shortest head, in image pixels, so thin arrows still read as arrows.</summary>
    public const double MinimumHeadLength = 12;

    /// <summary>Head length as a multiple of the stroke width.</summary>
    public const double HeadLengthPerWidth = 3.5;

    /// <summary>Half the angle between the head's two wings.</summary>
    public const double HeadSpread = Math.PI / 7;

    /// <summary>How far a curved arrow bows from its chord, as a fraction of the chord length.</summary>
    public const double CurveBow = 0.22;

    public static double HeadLength(double width) => Math.Max(MinimumHeadLength, width * HeadLengthPerWidth);

    public static ArrowShape Build(PointD start, PointD end, double width, ArrowStyle style, double scale = 1)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        var headLength = HeadLength(width) * scale;

        if (length < 0.001)
        {
            return new ArrowShape(false, start, start, start, end, end, end);
        }

        if (style == ArrowStyle.Straight)
        {
            var trim = Math.Min(headLength * 0.9, length * 0.98);
            var shaftEnd = new PointD(end.X - dx / length * trim, end.Y - dy / length * trim);
            var angle = Math.Atan2(dy, dx);
            var (head1, head2) = Wings(end, angle, headLength);
            return new ArrowShape(false, start, shaftEnd, shaftEnd, end, head1, head2);
        }

        // The perpendicular (-dy, dx) points to the right of travel in a y-down space, so a
        // left curve bows against it.
        var bow = length * CurveBow * (style == ArrowStyle.Curved1 ? -1 : 1);
        var control = new PointD(
            (start.X + end.X) / 2 - dy / length * bow,
            (start.Y + end.Y) / 2 + dx / length * bow);

        // The tangent at the tip of a quadratic Bézier is proportional to (end - control).
        var tipAngle = Math.Atan2(end.Y - control.Y, end.X - control.X);
        var (curvedHead1, curvedHead2) = Wings(end, tipAngle, headLength);

        // Split with de Casteljau so the shaft stops short of the tip and its cap never pokes through.
        var trimCurved = Math.Min(headLength * 0.9, length * 0.6);
        var t = Math.Clamp(1.0 - trimCurved / length, 0.02, 0.98);
        var a = Lerp(start, control, t);
        var b = Lerp(control, end, t);
        return new ArrowShape(true, start, a, Lerp(a, b, t), end, curvedHead1, curvedHead2);
    }

    private static (PointD, PointD) Wings(PointD tip, double angle, double headLength) => (
        new PointD(tip.X - headLength * Math.Cos(angle - HeadSpread), tip.Y - headLength * Math.Sin(angle - HeadSpread)),
        new PointD(tip.X - headLength * Math.Cos(angle + HeadSpread), tip.Y - headLength * Math.Sin(angle + HeadSpread)));

    private static PointD Lerp(PointD a, PointD b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}
