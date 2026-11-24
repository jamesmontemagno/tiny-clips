using Windows.UI;

namespace TinyClips.Core.Editing;

public static class AnnotationColorMath
{
    public static double Opacity(Color color) => color.A / 255.0;

    public static Color WithOpacity(Color color, double opacity, Color? fallback = null)
    {
        if (color.A == 0 && fallback is { } fallbackColor)
        {
            color = fallbackColor;
        }
        var alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }
}
