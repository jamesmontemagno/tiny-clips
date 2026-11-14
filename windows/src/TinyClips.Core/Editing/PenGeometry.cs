namespace TinyClips.Core.Editing;

/// <summary>
/// Freehand pen strokes as one filled outline, shared with macOS through
/// <c>shared/screenshot/fixtures/pen</c>. A port of perfect-freehand's <c>getStroke</c>
/// (MIT, © 2021 Stephen Ruiz Ltd, github.com/steveruizok/perfect-freehand at 176e00f) with fixed
/// options: size = the stroke width, thinning 0.3, smoothing 0.5, streamline 0.5, simulated
/// pressure, linear easing, both ends tapered, and the stroke treated as complete. Lengths use
/// <c>Math.Sqrt</c> rather than <c>hypot</c>, and the cap loops keep the source's accumulating
/// steps, so every platform produces the same points. Work in image pixels: the thresholds are in
/// the stroke's own units.
/// </summary>
public static class PenGeometry
{
    private const double Thinning = 0.3;
    private const double Smoothing = 0.5;
    private const double Streamline = 0.5;
    private const double TaperPerWidth = 3;
    private const double RateOfPressureChange = 0.275;
    private const double FixedPi = Math.PI + 0.0001;
    private const int StartCapSegments = 13;
    private const int CornerCapSegments = 13;
    private const double EndNoiseThreshold = 3;
    private const double MinStreamlineT = 0.15;
    private const double StreamlineTRange = 0.85;
    private const double MinRadius = 0.01;
    private const double DefaultFirstPressure = 0.25;
    private const double DefaultPressure = 0.5;

    private sealed record StrokePoint(PointD Point, double Pressure, PointD Vector, double Distance, double RunningLength)
    {
        public PointD Vector { get; set; } = Vector;
    }

    public static IReadOnlyList<PointD> Outline(IReadOnlyList<PointD> input, double width)
    {
        var size = width;
        var points = StrokePoints(input, size);
        if (points.Count == 0 || size <= 0)
        {
            return Array.Empty<PointD>();
        }

        var total = points[^1].RunningLength;
        var taperStart = size * TaperPerWidth;
        var taperEnd = size * TaperPerWidth;
        var minDistance = Math.Pow(size * Smoothing, 2);
        var left = new List<PointD>();
        var right = new List<PointD>();

        var prevPressure = points[0].Pressure;
        for (var i = 0; i < Math.Min(10, points.Count); i++)
        {
            prevPressure = (prevPressure + SimulatePressure(prevPressure, points[i].Distance, size)) / 2;
        }

        var radius = StrokeRadius(size, points[^1].Pressure);
        double? firstRadius = null;
        var prevVector = points[0].Vector;
        var prevLeft = points[0].Point;
        var prevRight = prevLeft;
        var tempLeft = prevLeft;
        var tempRight = prevRight;
        var prevSharp = false;

        for (var i = 0; i < points.Count; i++)
        {
            var sp = points[i];
            var p = sp.Point;
            var vector = sp.Vector;
            var running = sp.RunningLength;
            var isLast = i == points.Count - 1;
            if (!isLast && total - running < EndNoiseThreshold)
            {
                continue;
            }

            var pressure = SimulatePressure(prevPressure, sp.Distance, size);
            radius = StrokeRadius(size, pressure);
            firstRadius ??= radius;

            double startStrength = 1;
            if (running < taperStart)
            {
                var x = running / taperStart;
                startStrength = x * (2 - x);
            }
            double endStrength = 1;
            if (total - running < taperEnd)
            {
                var x = (total - running) / taperEnd;
                endStrength = (x - 1) * (x - 1) * (x - 1) + 1;
            }
            radius = Math.Max(MinRadius, radius * Math.Min(startStrength, endStrength));

            var nextVector = (!isLast ? points[i + 1] : sp).Vector;
            var nextDpr = !isLast ? Dpr(vector, nextVector) : 1.0;
            var prevDpr = Dpr(vector, prevVector);
            var isSharp = prevDpr < 0 && !prevSharp;
            var isNextSharp = nextDpr < 0;

            if (isSharp || isNextSharp)
            {
                var offset = Mul(Per(prevVector), radius);
                var step = 1.0 / CornerCapSegments;
                for (double t = 0; t <= 1; t += step)
                {
                    tempLeft = RotateAround(Sub(p, offset), p, FixedPi * t);
                    left.Add(tempLeft);
                    tempRight = RotateAround(Add(p, offset), p, FixedPi * -t);
                    right.Add(tempRight);
                }
                prevLeft = tempLeft;
                prevRight = tempRight;
                if (isNextSharp)
                {
                    prevSharp = true;
                }
                continue;
            }

            prevSharp = false;
            if (isLast)
            {
                var lastOffset = Mul(Per(vector), radius);
                left.Add(Sub(p, lastOffset));
                right.Add(Add(p, lastOffset));
                continue;
            }

            var regularOffset = Mul(Per(Lerp(nextVector, vector, nextDpr)), radius);
            tempLeft = Sub(p, regularOffset);
            if (i <= 1 || Dist2(prevLeft, tempLeft) > minDistance)
            {
                left.Add(tempLeft);
                prevLeft = tempLeft;
            }
            tempRight = Add(p, regularOffset);
            if (i <= 1 || Dist2(prevRight, tempRight) > minDistance)
            {
                right.Add(tempRight);
                prevRight = tempRight;
            }
            prevPressure = pressure;
            prevVector = vector;
        }

        var first = points[0].Point;
        if (points.Count == 1)
        {
            // A dot: the stroke is complete, so even a tapered one draws a circle.
            var start = Add(first, Mul(Unit(Per(Sub(first, Add(first, new PointD(1, 1))))), -(firstRadius ?? radius)));
            var dot = new List<PointD>();
            var step = 1.0 / StartCapSegments;
            for (var t = step; t <= 1; t += step)
            {
                dot.Add(RotateAround(start, first, FixedPi * 2 * t));
            }
            return dot;
        }

        // Both ends taper, so there is no start cap and the end is the last point.
        var outline = new List<PointD>(left.Count + right.Count + 1);
        outline.AddRange(left);
        outline.Add(points[^1].Point);
        for (var i = right.Count - 1; i >= 0; i--)
        {
            outline.Add(right[i]);
        }
        return outline;
    }

    private static List<StrokePoint> StrokePoints(IReadOnlyList<PointD> input, double size)
    {
        var result = new List<StrokePoint>();
        if (input.Count == 0)
        {
            return result;
        }

        var t = MinStreamlineT + (1 - Streamline) * StreamlineTRange;
        var pts = new List<PointD>(input);
        if (pts.Count == 2)
        {
            var last = pts[1];
            pts = new List<PointD> { pts[0] };
            for (var i = 1; i < 5; i++)
            {
                pts.Add(Lerp(pts[0], last, i / 4.0));
            }
        }
        if (pts.Count == 1)
        {
            pts.Add(Add(pts[0], new PointD(1, 1)));
        }

        var prev = new StrokePoint(pts[0], DefaultFirstPressure, new PointD(1, 1), 0, 0);
        result.Add(prev);
        var reachedMinimum = false;
        double running = 0;
        var lastIndex = pts.Count - 1;
        for (var i = 1; i < pts.Count; i++)
        {
            var p = i == lastIndex ? pts[i] : Lerp(prev.Point, pts[i], t);
            if (p.X == prev.Point.X && p.Y == prev.Point.Y)
            {
                continue;
            }
            var distance = Length(Sub(p, prev.Point));
            running += distance;
            if (i < lastIndex && !reachedMinimum)
            {
                if (running < size)
                {
                    continue;
                }
                reachedMinimum = true;
            }
            prev = new StrokePoint(p, DefaultPressure, Unit(Sub(prev.Point, p)), distance, running);
            result.Add(prev);
        }
        result[0].Vector = result.Count > 1 ? result[1].Vector : new PointD(0, 0);
        return result;
    }

    private static double SimulatePressure(double prev, double distance, double size)
    {
        var sp = Math.Min(1, distance / size);
        var rp = Math.Min(1, 1 - sp);
        return Math.Min(1, prev + (rp - prev) * (sp * RateOfPressureChange));
    }

    private static double StrokeRadius(double size, double pressure) => size * (0.5 - Thinning * (0.5 - pressure));

    private static PointD RotateAround(PointD a, PointD c, double r)
    {
        var s = Math.Sin(r);
        var co = Math.Cos(r);
        var px = a.X - c.X;
        var py = a.Y - c.Y;
        return new PointD(px * co - py * s + c.X, px * s + py * co + c.Y);
    }

    private static double Length(PointD a) => Math.Sqrt(a.X * a.X + a.Y * a.Y);

    private static PointD Unit(PointD a)
    {
        var n = Length(a);
        return new PointD(a.X / n, a.Y / n);
    }

    private static PointD Per(PointD a) => new(a.Y, -a.X);

    private static double Dpr(PointD a, PointD b) => a.X * b.X + a.Y * b.Y;

    private static double Dist2(PointD a, PointD b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static PointD Lerp(PointD a, PointD b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static PointD Add(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);

    private static PointD Sub(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);

    private static PointD Mul(PointD a, double k) => new(a.X * k, a.Y * k);
}
