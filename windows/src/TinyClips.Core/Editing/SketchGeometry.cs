namespace TinyClips.Core.Editing;

/// <summary>One step of a sketched stroke: move to <see cref="End"/>, or a cubic to it.</summary>
public readonly record struct SketchOp(bool IsMove, PointD Control1, PointD Control2, PointD End);

/// <summary>
/// The hand-drawn ("sketch") style, shared with macOS through
/// <c>shared/screenshot/fixtures/sketch</c>. A port of rough.js (MIT, © 2019 Preet Shihn,
/// github.com/rough-stuff/rough at 56a2762) with its default options. Each shape seeds its own
/// Park–Miller generator, so a stored seed redraws the same wobble everywhere. Geometry is
/// computed in units of half the stroke width (at least 0.5), so the wobble looks the same at any
/// stroke width or image size, as if rough.js drew it with its default 2-unit-wide stroke.
/// Random draws happen in exactly rough.js's order; keep it when editing.
/// </summary>
public static class SketchGeometry
{
    public const double DefaultRoughness = 1;
    public const double MaximumRoughness = 3;

    private const double Bowing = 1;
    private const double MaxRandomnessOffset = 2;
    private const double CurveFitting = 0.95;
    private const double CurveStepCount = 9;
    private const double CurveTightness = 0;
    private const double MinimumUnit = 0.5;

    /// <summary>A new non-zero seed for an annotation; zero would mean "not seeded".</summary>
    public static int NewSeed() => Random.Shared.Next(1, int.MaxValue);

    public static IReadOnlyList<SketchOp> Rectangle(RectD bounds, double width, double roughness, int seed)
    {
        var unit = Unit(width);
        var ops = new Rough(roughness, seed).Rectangle(bounds.X / unit, bounds.Y / unit, bounds.Width / unit, bounds.Height / unit);
        return Scale(ops, unit);
    }

    public static IReadOnlyList<SketchOp> Ellipse(RectD bounds, double width, double roughness, int seed)
    {
        var unit = Unit(width);
        var cx = (bounds.X + bounds.Width / 2) / unit;
        var cy = (bounds.Y + bounds.Height / 2) / unit;
        var ops = new Rough(roughness, seed).Ellipse(cx, cy, bounds.Width / unit, bounds.Height / unit);
        return Scale(ops, unit);
    }

    /// <summary>A hand-drawn cubic from start to end: rough.js's single-stroke line.</summary>
    public static (PointD Start, PointD Control1, PointD Control2, PointD End) Centerline(PointD start, PointD end, double width, double roughness, int seed)
    {
        var unit = Unit(width);
        var ops = new Rough(roughness, seed).Line(start.X / unit, start.Y / unit, end.X / unit, end.Y / unit, move: true, overlay: false);
        var move = ops[0].End;
        var curve = ops[1];
        return (
            new PointD(move.X * unit, move.Y * unit),
            new PointD(curve.Control1.X * unit, curve.Control1.Y * unit),
            new PointD(curve.Control2.X * unit, curve.Control2.Y * unit),
            new PointD(curve.End.X * unit, curve.End.Y * unit));
    }

    /// <summary>The generator's first values for a seed; exposed for the shared fixtures.</summary>
    public static double[] RandomValues(int seed, int count)
    {
        var random = new RoughRandom(seed);
        var values = new double[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = random.Next();
        }
        return values;
    }

    private static double Unit(double width) => Math.Max(MinimumUnit, width / 2);

    private static List<SketchOp> Scale(List<SketchOp> ops, double unit) => ops
        .Select(op => new SketchOp(
            op.IsMove,
            new PointD(op.Control1.X * unit, op.Control1.Y * unit),
            new PointD(op.Control2.X * unit, op.Control2.Y * unit),
            new PointD(op.End.X * unit, op.End.Y * unit)))
        .ToList();

    private sealed class RoughRandom(int seed)
    {
        private int _seed = seed != 0 ? seed : throw new ArgumentOutOfRangeException(nameof(seed), "A sketch needs a non-zero seed.");

        public double Next()
        {
            _seed = unchecked(48271 * _seed);
            return (int.MaxValue & _seed) / 2147483648.0;
        }
    }

    private sealed class Rough(double roughness, int seed)
    {
        private readonly RoughRandom _random = new(seed);

        private double Offset(double min, double max, double gain = 1) =>
            roughness * gain * ((_random.Next() * (max - min)) + min);

        private double OffsetOpt(double x, double gain = 1) => Offset(-x, x, gain);

        public List<SketchOp> Line(double x1, double y1, double x2, double y2, bool move, bool overlay)
        {
            var lengthSq = (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2);
            var length = Math.Sqrt(lengthSq);
            double gain = length < 200 ? 1
                : length > 500 ? 0.4
                : (-0.0016668) * length + 1.233334;
            var offset = MaxRandomnessOffset;
            if (offset * offset * 100 > lengthSq)
            {
                offset = length / 10;
            }
            var halfOffset = offset / 2;
            var diverge = 0.2 + _random.Next() * 0.2;
            var midX = Bowing * MaxRandomnessOffset * (y2 - y1) / 200;
            var midY = Bowing * MaxRandomnessOffset * (x1 - x2) / 200;
            midX = OffsetOpt(midX, gain);
            midY = OffsetOpt(midY, gain);
            var jitter = overlay ? halfOffset : offset;
            var ops = new List<SketchOp>(2);
            if (move)
            {
                var mx = x1 + OffsetOpt(jitter, gain);
                var my = y1 + OffsetOpt(jitter, gain);
                ops.Add(new SketchOp(true, default, default, new PointD(mx, my)));
            }
            var c1x = midX + x1 + (x2 - x1) * diverge + OffsetOpt(jitter, gain);
            var c1y = midY + y1 + (y2 - y1) * diverge + OffsetOpt(jitter, gain);
            var c2x = midX + x1 + 2 * (x2 - x1) * diverge + OffsetOpt(jitter, gain);
            var c2y = midY + y1 + 2 * (y2 - y1) * diverge + OffsetOpt(jitter, gain);
            var ex = x2 + OffsetOpt(jitter, gain);
            var ey = y2 + OffsetOpt(jitter, gain);
            ops.Add(new SketchOp(false, new PointD(c1x, c1y), new PointD(c2x, c2y), new PointD(ex, ey)));
            return ops;
        }

        private List<SketchOp> DoubleLine(double x1, double y1, double x2, double y2)
        {
            var ops = Line(x1, y1, x2, y2, move: true, overlay: false);
            ops.AddRange(Line(x1, y1, x2, y2, move: true, overlay: true));
            return ops;
        }

        public List<SketchOp> Rectangle(double x, double y, double w, double h)
        {
            (double X, double Y)[] pts = [(x, y), (x + w, y), (x + w, y + h), (x, y + h)];
            var ops = new List<SketchOp>();
            for (var i = 0; i < 3; i++)
            {
                ops.AddRange(DoubleLine(pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y));
            }
            ops.AddRange(DoubleLine(pts[3].X, pts[3].Y, pts[0].X, pts[0].Y));
            return ops;
        }

        public List<SketchOp> Ellipse(double cx, double cy, double w, double h)
        {
            var psq = Math.Sqrt(Math.PI * 2 * Math.Sqrt(((w / 2) * (w / 2) + (h / 2) * (h / 2)) / 2));
            var stepCount = Math.Ceiling(Math.Max(CurveStepCount, (CurveStepCount / Math.Sqrt(200)) * psq));
            var increment = (Math.PI * 2) / stepCount;
            var rx = Math.Abs(w / 2);
            var ry = Math.Abs(h / 2);
            var fitRandomness = 1 - CurveFitting;
            rx += OffsetOpt(rx * fitRandomness);
            ry += OffsetOpt(ry * fitRandomness);
            var inner = Offset(0.4, 1);
            var overlap = increment * Offset(0.1, inner);
            var ops = Curve(EllipsePoints(increment, cx, cy, rx, ry, 1, overlap));
            if (roughness != 0)
            {
                ops.AddRange(Curve(EllipsePoints(increment, cx, cy, rx, ry, 1.5, 0)));
            }
            return ops;
        }

        private List<(double X, double Y)> EllipsePoints(double increment, double cx, double cy, double rx, double ry, double offset, double overlap)
        {
            var pts = new List<(double X, double Y)>();
            if (roughness == 0)
            {
                increment /= 4;
                pts.Add((cx + rx * Math.Cos(-increment), cy + ry * Math.Sin(-increment)));
                for (double angle = 0; angle <= Math.PI * 2; angle = angle + increment)
                {
                    pts.Add((cx + rx * Math.Cos(angle), cy + ry * Math.Sin(angle)));
                }
                pts.Add((cx + rx * Math.Cos(0), cy + ry * Math.Sin(0)));
                pts.Add((cx + rx * Math.Cos(increment), cy + ry * Math.Sin(increment)));
                return pts;
            }

            var radOffset = OffsetOpt(0.5) - (Math.PI / 2);
            (double X, double Y) Jittered(double scale, double angle)
            {
                var x = OffsetOpt(offset) + cx + scale * rx * Math.Cos(angle);
                var y = OffsetOpt(offset) + cy + scale * ry * Math.Sin(angle);
                return (x, y);
            }

            pts.Add(Jittered(0.9, radOffset - increment));
            var endAngle = Math.PI * 2 + radOffset - 0.01;
            for (var angle = radOffset; angle < endAngle; angle = angle + increment)
            {
                pts.Add(Jittered(1, angle));
            }
            pts.Add(Jittered(1, radOffset + Math.PI * 2 + overlap * 0.5));
            pts.Add(Jittered(0.98, radOffset + overlap));
            pts.Add(Jittered(0.9, radOffset + overlap * 0.5));
            return pts;
        }

        private static List<SketchOp> Curve(List<(double X, double Y)> pts)
        {
            var ops = new List<SketchOp>();
            if (pts.Count > 3)
            {
                var s = 1 - CurveTightness;
                ops.Add(new SketchOp(true, default, default, new PointD(pts[1].X, pts[1].Y)));
                for (var i = 1; i + 2 < pts.Count; i++)
                {
                    var b1 = new PointD(pts[i].X + (s * pts[i + 1].X - s * pts[i - 1].X) / 6, pts[i].Y + (s * pts[i + 1].Y - s * pts[i - 1].Y) / 6);
                    var b2 = new PointD(pts[i + 1].X + (s * pts[i].X - s * pts[i + 2].X) / 6, pts[i + 1].Y + (s * pts[i].Y - s * pts[i + 2].Y) / 6);
                    ops.Add(new SketchOp(false, b1, b2, new PointD(pts[i + 1].X, pts[i + 1].Y)));
                }
            }
            return ops;
        }
    }
}
