using Windows.UI;

namespace TinyClips.Core.Editing;

public enum PointerStyle
{
    Arrow,
    Hand,
    Text,
}

public readonly record struct PointerShadow(
    bool Enabled = true,
    double Opacity = 0.35,
    double Blur = 4,
    double OffsetX = 2,
    double OffsetY = 2)
{
    public static PointerShadow Default => new(true, 0.35, 4, 2, 2);
}

public readonly record struct PointerAppearance(
    PointerStyle Style,
    Color Fill,
    Color Stroke,
    double StrokeWidth,
    double Opacity,
    PointerShadow Shadow)
{
    public static PointerAppearance Default => new(
        PointerStyle.Arrow,
        Color.FromArgb(255, 255, 255, 255),
        Color.FromArgb(255, 0, 0, 0),
        2,
        1,
        PointerShadow.Default);
}

public static class PointerGeometry
{
    public static PointD Hotspot(PointerStyle style) => style switch
    {
        PointerStyle.Arrow => new(0.08, 0.04),
        PointerStyle.Hand => new(0.36, 0.04),
        PointerStyle.Text => new(0.5, 0.5),
        _ => throw new ArgumentOutOfRangeException(nameof(style)),
    };

    public static IReadOnlyList<PointD> Outline(PointerStyle style) => style switch
    {
        PointerStyle.Arrow =>
        [
            new(0.08, 0.04), new(0.08, 0.84), new(0.29, 0.64), new(0.45, 0.96),
            new(0.59, 0.89), new(0.42, 0.58), new(0.72, 0.58),
        ],
        PointerStyle.Hand =>
        [
            new(0.36, 0.04), new(0.46, 0.04), new(0.46, 0.40), new(0.55, 0.36),
            new(0.63, 0.40), new(0.70, 0.38), new(0.81, 0.45), new(0.81, 0.72),
            new(0.70, 0.96), new(0.35, 0.96), new(0.25, 0.74), new(0.12, 0.62),
            new(0.13, 0.52), new(0.20, 0.48), new(0.36, 0.61),
        ],
        PointerStyle.Text =>
        [
            new(0.24, 0.04), new(0.76, 0.04), new(0.76, 0.16), new(0.58, 0.16),
            new(0.58, 0.84), new(0.76, 0.84), new(0.76, 0.96), new(0.24, 0.96),
            new(0.24, 0.84), new(0.42, 0.84), new(0.42, 0.16), new(0.24, 0.16),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(style)),
    };

    public static RectD Fit(RectD bounds)
    {
        var side = Math.Min(bounds.Width, bounds.Height);
        return new RectD(bounds.X + (bounds.Width - side) / 2, bounds.Y + (bounds.Height - side) / 2, side, side);
    }

    public static RectD Place(PointD hotspot, PointerStyle style, double side)
    {
        var anchor = Hotspot(style);
        return new RectD(hotspot.X - anchor.X * side, hotspot.Y - anchor.Y * side, side, side);
    }
}
