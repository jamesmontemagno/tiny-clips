using System.Globalization;
using TinyClips.Core.Models;

namespace TinyClips.Core.Capture;

/// <summary>
/// Draws expanding "pulse ring" mouse-click overlays directly into a tightly-packed
/// BGRA8 frame buffer, mirroring the macOS <c>MouseClickOverlayProcessor</c> animation:
/// a stroked circle whose radius grows and whose alpha fades over the configured
/// duration. All drawing is CPU-side alpha blending — no GPU dependency.
/// </summary>
public static class MouseClickOverlayCompositor
{
    /// <summary>
    /// Renders every active click pulse onto <paramref name="bgra"/> for the given
    /// frame time.
    /// </summary>
    /// <param name="bgra">Tightly-packed BGRA8 pixels (stride = width * 4).</param>
    /// <param name="frameSeconds">Frame timestamp in seconds, relative to recording start.</param>
    /// <param name="clicks">Recorded clicks (screen-space, physical pixels).</param>
    /// <param name="originX">Frame's left edge in virtual-desktop physical pixels.</param>
    /// <param name="originY">Frame's top edge in virtual-desktop physical pixels.</param>
    public static void Draw(
        byte[] bgra,
        int width,
        int height,
        double frameSeconds,
        IReadOnlyList<MouseClickSample> clicks,
        int originX,
        int originY,
        in MouseClickOverlayStyle style)
    {
        if (clicks.Count == 0 || style.DurationSeconds <= 0 || style.Opacity <= 0)
        {
            return;
        }

        (byte r, byte g, byte b) = ParseColor(style.ColorHex);

        foreach (MouseClickSample click in clicks)
        {
            if (!TryComputeRing(click, frameSeconds, originX, originY, style, out var ring))
            {
                continue;
            }

            DrawRing(bgra, width, height, ring.CenterX, ring.CenterY, ring.Radius, ring.HalfStroke, r, g, b, ring.Alpha);
        }
    }

    /// <summary>Geometry of one click pulse at a given frame time, in frame pixel space.</summary>
    public readonly record struct Ring(double CenterX, double CenterY, double Radius, double HalfStroke, double Alpha);

    /// <summary>
    /// Computes the pulse ring for <paramref name="click"/> at <paramref name="frameSeconds"/>.
    /// Shared by the CPU rasterizer and the Direct2D path so both animate identically.
    /// </summary>
    public static bool TryComputeRing(
        in MouseClickSample click,
        double frameSeconds,
        int originX,
        int originY,
        in MouseClickOverlayStyle style,
        out Ring ring)
    {
        ring = default;
        double elapsed = frameSeconds - click.TimeSeconds;
        if (elapsed < 0 || elapsed > style.DurationSeconds || style.DurationSeconds <= 0)
        {
            return false;
        }

        double progress = elapsed / style.DurationSeconds;
        double alpha = Math.Max(0, (1 - progress) * style.Opacity);
        if (alpha <= 0)
        {
            return false;
        }

        double radius = (style.Size / 2.0) + (style.Size * 0.58 * progress);
        double half = Math.Max(0.5, style.StrokeWidth / 2.0);
        ring = new Ring(click.ScreenX - originX, click.ScreenY - originY, radius, half, alpha);
        return true;
    }

    private static void DrawRing(
        byte[] bgra,
        int width,
        int height,
        double cx,
        double cy,
        double radius,
        double halfStroke,
        byte r,
        byte g,
        byte b,
        double alpha)
    {
        double outer = radius + halfStroke + 1;
        int minX = Math.Max(0, (int)Math.Floor(cx - outer));
        int maxX = Math.Min(width - 1, (int)Math.Ceiling(cx + outer));
        int minY = Math.Max(0, (int)Math.Floor(cy - outer));
        int maxY = Math.Min(height - 1, (int)Math.Ceiling(cy + outer));

        for (int y = minY; y <= maxY; y++)
        {
            double dy = y - cy;
            for (int x = minX; x <= maxX; x++)
            {
                double dx = x - cx;
                double dist = Math.Sqrt((dx * dx) + (dy * dy));
                double edge = Math.Abs(dist - radius);

                // 1px anti-aliased feather around the stroke band.
                double coverage = Math.Clamp(halfStroke + 0.75 - edge, 0, 1);
                if (coverage <= 0)
                {
                    continue;
                }

                double a = alpha * coverage;
                int i = ((y * width) + x) * 4;
                bgra[i] = Blend(bgra[i], b, a);
                bgra[i + 1] = Blend(bgra[i + 1], g, a);
                bgra[i + 2] = Blend(bgra[i + 2], r, a);
            }
        }
    }

    private static byte Blend(byte dst, byte src, double a) =>
        (byte)Math.Clamp((src * a) + (dst * (1 - a)), 0, 255);

    /// <summary>Parses a "#RRGGBB" / "#AARRGGBB" style color; falls back to the default accent.</summary>
    public static (byte R, byte G, byte B) ParseColor(string hex)
    {
        string s = (hex ?? string.Empty).Trim().TrimStart('#');
        if (s.Length == 8)
        {
            s = s[2..]; // drop leading alpha if present
        }

        if (s.Length == 6 &&
            byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
            byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
            byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            return (r, g, b);
        }

        return (255, 214, 10); // fallback amber, matches default accent
    }
}
