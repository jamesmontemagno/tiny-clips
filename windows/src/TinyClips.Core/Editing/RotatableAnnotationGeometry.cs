namespace TinyClips.Core.Editing;

/// <summary>A double-precision point used by the editor geometry helpers (UI-free).</summary>
public readonly record struct PointD(double X, double Y);

/// <summary>An axis-aligned rectangle in whatever coordinate space the caller chooses.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public PointD Center => new(X + Width / 2, Y + Height / 2);
}

/// <summary>
/// Geometry for annotations that rotate around the center of their bounds. Angles are in degrees,
/// clockwise-positive in a y-down coordinate space (matching XAML's <c>RotateTransform</c>), and
/// 0° means the annotation's top edge faces up. All functions are pure so they can be shared by
/// hit-testing (pixel space), the live canvas (DIP space) and unit tests.
/// </summary>
public static class RotatableAnnotationGeometry
{
    /// <summary>Rotates <paramref name="point"/> clockwise around <paramref name="center"/>.</summary>
    public static PointD Rotate(PointD point, PointD center, double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        return new PointD(
            center.X + dx * cos - dy * sin,
            center.Y + dx * sin + dy * cos);
    }

    /// <summary>Corners after rotation, ordered top-left, top-right, bottom-left, bottom-right.</summary>
    public static PointD[] Corners(RectD bounds, double degrees)
    {
        var center = bounds.Center;
        return
        [
            Rotate(new PointD(bounds.Left, bounds.Top), center, degrees),
            Rotate(new PointD(bounds.Right, bounds.Top), center, degrees),
            Rotate(new PointD(bounds.Left, bounds.Bottom), center, degrees),
            Rotate(new PointD(bounds.Right, bounds.Bottom), center, degrees),
        ];
    }

    /// <summary>Gap between the rotated top edge and the rotation grip for a box of this height.</summary>
    public static double RotationHandleOffset(double height) => Math.Max(18, height * 0.3);

    /// <summary>
    /// Position of the rotation grip: centered above the top edge by <paramref name="offset"/>
    /// and rotated along with the box.
    /// </summary>
    public static PointD RotationHandle(RectD bounds, double degrees, double offset)
    {
        var center = bounds.Center;
        return Rotate(new PointD(center.X, bounds.Top - offset), center, degrees);
    }

    /// <summary>Whether <paramref name="point"/> is inside the rotated box, grown by <paramref name="padding"/>.</summary>
    public static bool Contains(PointD point, RectD bounds, double degrees, double padding = 0)
    {
        var local = Rotate(point, bounds.Center, -degrees);
        return local.X >= bounds.Left - padding
            && local.X <= bounds.Right + padding
            && local.Y >= bounds.Top - padding
            && local.Y <= bounds.Bottom + padding;
    }

    /// <summary>Clockwise angle in degrees from "straight up" of the direction center → point.</summary>
    public static double AngleDegrees(PointD center, PointD point)
    {
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        return Math.Atan2(dx, -dy) * 180.0 / Math.PI;
    }

    /// <summary>Wraps an angle into the (-180, 180] range.</summary>
    public static double NormalizeDegrees(double degrees)
    {
        var result = degrees % 360.0;
        if (result > 180.0)
        {
            result -= 360.0;
        }
        else if (result <= -180.0)
        {
            result += 360.0;
        }
        return result;
    }

    /// <summary>Rounds to the nearest <paramref name="increment"/> when <paramref name="snap"/> is set.</summary>
    public static double Snap(double degrees, double increment, bool snap)
    {
        if (!snap || increment <= 0)
        {
            return degrees;
        }
        return Math.Round(degrees / increment) * increment;
    }

    /// <summary>Distance between two points.</summary>
    public static double Distance(PointD a, PointD b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
