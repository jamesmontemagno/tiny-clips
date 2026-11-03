namespace TinyClips.Core.Editing;

/// <summary>The arrow's shape, which stays in sync with the bend and size sliders.</summary>
public enum ArrowPreset
{
    Classic,
    Tapered,
    Curved,
    Custom,
}

/// <summary>
/// How an arrow is shaped. <see cref="Bend"/> is how far the curve's midpoint sits from the
/// chord's midpoint, as a fraction of the chord length; positive bows to the left of travel.
/// The width scales multiply the stroke width at the tail and where the shaft meets the head.
/// </summary>
public readonly record struct ArrowShapeOptions(double Bend, double StartWidthScale, double EndWidthScale, double HeadScale)
{
    public static readonly ArrowShapeOptions Classic = new(0, 1, 1, 1);
    public static readonly ArrowShapeOptions Tapered = new(0, 0.25, 1, 1.2);
    public static readonly ArrowShapeOptions Curved = new(0.2, 1, 1, 1);

    public const double MinimumBend = -1;
    public const double MaximumBend = 1;
    public const double MinimumWidthScale = 0;
    public const double MaximumWidthScale = 2;
    public const double MinimumHeadScale = 0.5;
    public const double MaximumHeadScale = 3;

    public static ArrowShapeOptions For(ArrowPreset preset) => preset switch
    {
        ArrowPreset.Tapered => Tapered,
        ArrowPreset.Curved => Curved,
        _ => Classic,
    };

    /// <summary>The preset these options match, or <see cref="ArrowPreset.Custom"/>.</summary>
    public ArrowPreset Preset =>
        Matches(Classic) ? ArrowPreset.Classic
        : Matches(Tapered) ? ArrowPreset.Tapered
        : Matches(Curved) ? ArrowPreset.Curved
        : ArrowPreset.Custom;

    private bool Matches(ArrowShapeOptions other) =>
        Math.Abs(Bend - other.Bend) < 0.001
        && Math.Abs(StartWidthScale - other.StartWidthScale) < 0.001
        && Math.Abs(EndWidthScale - other.EndWidthScale) < 0.001
        && Math.Abs(HeadScale - other.HeadScale) < 0.001;
}

/// <summary>An arrow's single filled outline, plus the curve's control point and bend handle.</summary>
public readonly record struct ArrowOutline(PointD Control, PointD Handle, IReadOnlyList<PointD> Points);

/// <summary>
/// Arrow geometry shared by the live preview, the export renderer, and the macOS app through
/// <c>shared/screenshot/fixtures/arrow</c>. Points are y-down. Widths are in source-image pixels;
/// <c>scale</c> is the caller's units per image pixel, so the preview and the export draw the
/// same proportions. The arrow is one outline — tapered shaft, head, and tail cap — so a
/// translucent color fills it evenly.
/// </summary>
public static class ArrowGeometry
{
    /// <summary>Shortest head, in image pixels, so thin arrows still read as arrows.</summary>
    public const double MinimumHeadLength = 12;

    /// <summary>Head length as a multiple of the stroke width.</summary>
    public const double HeadLengthPerWidth = 3.5;

    /// <summary>Half the angle between the head's two wings.</summary>
    public const double HeadSpread = Math.PI / 7;

    private const double MaximumHeadFraction = 0.45;
    private const double ShaftEndFraction = 0.8;
    private const int ShaftSamples = 24;
    private const int CapSegments = 8;
    private const int CornerSegments = 6;
    private const int TrimIterations = 40;

    public static double HeadLength(double width) => Math.Max(MinimumHeadLength, width * HeadLengthPerWidth);

    /// <summary>The point on the curve at its midpoint, where the bend handle sits.</summary>
    public static PointD Handle(PointD start, PointD end, double bend)
    {
        var chord = Sub(end, start);
        var n = RightNormal(Normalize(chord, new PointD(1, 0)));
        return Sub(Lerp(start, end, 0.5), Mul(n, bend * Length(chord)));
    }

    /// <summary>The bend that puts the curve's midpoint nearest <paramref name="handle"/>.</summary>
    public static double Bend(PointD start, PointD end, PointD handle)
    {
        var chord = Sub(end, start);
        var length = Length(chord);
        if (length < 0.001)
        {
            return 0;
        }
        var n = RightNormal(Mul(chord, 1 / length));
        var offset = Sub(handle, Lerp(start, end, 0.5));
        return Math.Clamp(-(offset.X * n.X + offset.Y * n.Y) / length, ArrowShapeOptions.MinimumBend, ArrowShapeOptions.MaximumBend);
    }

    /// <summary>The quadratic Bézier control point for a bend.</summary>
    public static PointD Control(PointD start, PointD end, double bend) =>
        Sub(Mul(Handle(start, end, bend), 2), Lerp(start, end, 0.5));

    /// <summary>Distance from <paramref name="point"/> to the arrow's curve, for hit testing.</summary>
    public static double DistanceToCurve(PointD point, PointD start, PointD end, double bend)
    {
        var control = Control(start, end, bend);
        var previous = start;
        var best = double.MaxValue;
        for (var i = 1; i <= 32; i++)
        {
            var next = PointOnCurve(start, control, end, i / 32.0);
            best = Math.Min(best, DistanceToSegment(point, previous, next));
            previous = next;
        }
        return best;
    }

    /// <summary>
    /// Whether <paramref name="point"/> is inside a filled outline (nonzero rule) or within
    /// <paramref name="tolerance"/> of its edge, so a click anywhere on the drawn shape, its head
    /// and its outline selects it.
    /// </summary>
    public static bool OutlineContains(IReadOnlyList<PointD> outline, PointD point, double tolerance)
    {
        if (outline.Count < 3)
        {
            return false;
        }
        var winding = 0;
        var previous = outline[^1];
        foreach (var current in outline)
        {
            if (DistanceToSegment(point, previous, current) <= tolerance)
            {
                return true;
            }
            var cross = (current.X - previous.X) * (point.Y - previous.Y) - (point.X - previous.X) * (current.Y - previous.Y);
            if (previous.Y <= point.Y)
            {
                if (current.Y > point.Y && cross > 0)
                {
                    winding++;
                }
            }
            else if (current.Y <= point.Y && cross < 0)
            {
                winding--;
            }
            previous = current;
        }
        return winding != 0;
    }

    public static PointD PointOnCurve(PointD start, PointD control, PointD end, double t)
    {
        var u = 1 - t;
        return new PointD(
            u * u * start.X + 2 * u * t * control.X + t * t * end.X,
            u * u * start.Y + 2 * u * t * control.Y + t * t * end.Y);
    }

    /// <param name="roughness">Above 0, the shaft follows a hand-drawn wobble seeded by
    /// <paramref name="seed"/> (see <see cref="SketchGeometry"/>); the taper follows it.</param>
    public static ArrowOutline Build(
        PointD start,
        PointD end,
        double width,
        ArrowShapeOptions options,
        StrokeCap startCap,
        StrokeJoin join,
        double scale = 1,
        double roughness = 0,
        int seed = 0)
    {
        var chord = Sub(end, start);
        var length = Length(chord);
        var handle = Handle(start, end, options.Bend);
        var control = Sub(Mul(handle, 2), Lerp(start, end, 0.5));
        if (length < 0.001)
        {
            return new ArrowOutline(control, handle, Array.Empty<PointD>());
        }
        var u = Mul(chord, 1 / length);
        var (p0, c1, c2, p3) = Centerline(start, end, control, width, scale, roughness, seed);

        var head = Math.Min(options.HeadScale * HeadLength(width) * scale, MaximumHeadFraction * length);
        var tipDirection = Normalize(Sub(p3, c2), u);
        var angle = Math.Atan2(tipDirection.Y, tipDirection.X);
        var wingRight = new PointD(p3.X - head * Math.Cos(angle - HeadSpread), p3.Y - head * Math.Sin(angle - HeadSpread));
        var wingLeft = new PointD(p3.X - head * Math.Cos(angle + HeadSpread), p3.Y - head * Math.Sin(angle + HeadSpread));

        // The shaft stops where the curve is as far from the tip as the head's base.
        var baseDistance = head * Math.Cos(HeadSpread);
        double lo = 0, hi = 1;
        for (var i = 0; i < TrimIterations; i++)
        {
            var m = (lo + hi) / 2;
            if (Length(Sub(Cubic(p0, c1, c2, p3, m), p3)) > baseDistance)
            {
                lo = m;
            }
            else
            {
                hi = m;
            }
        }
        var trim = (lo + hi) / 2;

        var startHalf = width * options.StartWidthScale / 2 * scale;
        var endHalf = Math.Min(width * options.EndWidthScale / 2 * scale, ShaftEndFraction * head * Math.Sin(HeadSpread));
        var (left, right) = ShaftEdges(p0, c1, c2, p3, trim, startHalf, endHalf, u);

        var size = Math.Min(width / 2 * scale, 0.2 * head);
        var points = new List<PointD>(left.Length * 2 + 3 * (CornerSegments + 1) + CapSegments);
        points.AddRange(left);
        AddCorner(points, left[^1], wingLeft, p3, size, join);
        AddCorner(points, wingLeft, p3, wingRight, size, join);
        AddCorner(points, p3, wingRight, right[^1], size, join);
        for (var i = right.Length - 1; i >= 0; i--)
        {
            points.Add(right[i]);
        }

        var tailTangent = Normalize(CubicTangent(p0, c1, c2, p3, 0), u);
        LineGeometry.AddCap(points, p0, Mul(tailTangent, -1), Mul(RightNormal(tailTangent), -1), startHalf, startCap);
        return new ArrowOutline(control, handle, points);
    }

    /// <summary>
    /// The arrow's centerline as a cubic: its bend's quadratic raised to a cubic, plus the
    /// hand-drawn wobble when sketched.
    /// </summary>
    private static (PointD, PointD, PointD, PointD) Centerline(PointD start, PointD end, PointD control, double width, double scale, double roughness, int seed)
    {
        if (roughness > 0)
        {
            var (s0, c1, c2, e) = SketchGeometry.Centerline(start, end, width, roughness, seed, scale);
            var d = Mul(Sub(control, Lerp(start, end, 0.5)), 2.0 / 3);
            return (s0, Add(c1, d), Add(c2, d), e);
        }
        return (start, Add(start, Mul(Sub(control, start), 2.0 / 3)), Add(end, Mul(Sub(control, end), 2.0 / 3)), end);
    }

    /// <summary>
    /// The left and right edges of a stroke along a cubic, from t = 0 to <paramref name="tEnd"/>,
    /// whose half width runs from <paramref name="startHalf"/> to <paramref name="endHalf"/>.
    /// </summary>
    internal static (PointD[] Left, PointD[] Right) ShaftEdges(
        PointD p0, PointD c1, PointD c2, PointD p3, double tEnd, double startHalf, double endHalf, PointD fallback)
    {
        var left = new PointD[ShaftSamples + 1];
        var right = new PointD[ShaftSamples + 1];
        for (var i = 0; i <= ShaftSamples; i++)
        {
            var f = (double)i / ShaftSamples;
            var t = tEnd * f;
            var p = Cubic(p0, c1, c2, p3, t);
            var n = RightNormal(Normalize(CubicTangent(p0, c1, c2, p3, t), fallback));
            var half = startHalf + (endHalf - startHalf) * f;
            left[i] = Sub(p, Mul(n, half));
            right[i] = Add(p, Mul(n, half));
        }
        return (left, right);
    }

    internal static PointD Cubic(PointD p0, PointD c1, PointD c2, PointD p3, double t)
    {
        var u = 1 - t;
        var a = u * u * u;
        var b = 3 * u * u * t;
        var c = 3 * u * t * t;
        var d = t * t * t;
        return new PointD(
            a * p0.X + b * c1.X + c * c2.X + d * p3.X,
            a * p0.Y + b * c1.Y + c * c2.Y + d * p3.Y);
    }

    internal static PointD CubicTangent(PointD p0, PointD c1, PointD c2, PointD p3, double t)
    {
        var u = 1 - t;
        var a = 3 * u * u;
        var b = 6 * u * t;
        var c = 3 * t * t;
        return new PointD(
            a * (c1.X - p0.X) + b * (c2.X - c1.X) + c * (p3.X - c2.X),
            a * (c1.Y - p0.Y) + b * (c2.Y - c1.Y) + c * (p3.Y - c2.Y));
    }

    private static void AddCorner(List<PointD> points, PointD previous, PointD vertex, PointD next, double size, StrokeJoin join)
    {
        if (join == StrokeJoin.Miter || size <= 0)
        {
            points.Add(vertex);
            return;
        }
        var toPrevious = Sub(previous, vertex);
        var toNext = Sub(next, vertex);
        var k = Math.Min(size, Math.Min(0.45 * Length(toPrevious), 0.45 * Length(toNext)));
        var a = Add(vertex, Mul(Normalize(toPrevious, new PointD(0, 0)), k));
        var b = Add(vertex, Mul(Normalize(toNext, new PointD(0, 0)), k));
        if (join == StrokeJoin.Bevel)
        {
            points.Add(a);
            points.Add(b);
            return;
        }
        for (var j = 0; j <= CornerSegments; j++)
        {
            points.Add(PointOnCurve(a, vertex, b, (double)j / CornerSegments));
        }
    }

    private static double DistanceToSegment(PointD p, PointD a, PointD b)
    {
        var ab = Sub(b, a);
        var lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
        if (lengthSquared <= 0)
        {
            return Length(Sub(p, a));
        }
        var t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / lengthSquared, 0, 1);
        return Length(Sub(p, Add(a, Mul(ab, t))));
    }

    /// <summary>The unit normal to the right of travel in a y-down space.</summary>
    internal static PointD RightNormal(PointD direction) => new(-direction.Y, direction.X);

    internal static PointD Normalize(PointD v, PointD fallback)
    {
        var length = Length(v);
        return length > 1e-9 ? Mul(v, 1 / length) : fallback;
    }

    private static double Length(PointD v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);

    private static PointD Add(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);

    private static PointD Sub(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);

    private static PointD Mul(PointD a, double k) => new(a.X * k, a.Y * k);

    private static PointD Lerp(PointD a, PointD b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}
