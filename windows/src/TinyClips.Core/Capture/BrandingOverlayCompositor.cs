using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TinyClips.Core.Capture;

/// <summary>
/// Draws a "Captured on Tiny Clips" branding badge into the bottom-right corner of a
/// tightly-packed BGRA8 frame buffer, mirroring the macOS <c>BrandingOverlayProcessor</c>
/// (a black rounded pill with white text, sized proportionally to the frame height).
///
/// The badge is a fixed string, so it is rasterized once with GDI+ and cached as a
/// straight-alpha BGRA bitmap; per-frame work is just a cheap CPU alpha-blend, matching
/// the approach used by <see cref="MouseClickOverlayCompositor"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrandingOverlayCompositor
{
    private const string OverlayText = "Captured on Tiny Clips";

    private byte[]? _badge;
    private int _badgeWidth;
    private int _badgeHeight;
    private int _margin;
    private int _builtForHeight = -1;

    /// <summary>
    /// Composites the branding badge onto <paramref name="bgra"/> (stride = width * 4).
    /// </summary>
    public void Draw(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        EnsureBadge(height);
        if (_badge is null || _badgeWidth <= 0 || _badgeHeight <= 0)
        {
            return;
        }

        int originX = width - _badgeWidth - _margin;
        int originY = height - _badgeHeight - _margin;

        for (int y = 0; y < _badgeHeight; y++)
        {
            int dy = originY + y;
            if (dy < 0 || dy >= height)
            {
                continue;
            }

            int badgeRow = y * _badgeWidth * 4;
            int frameRow = dy * width * 4;

            for (int x = 0; x < _badgeWidth; x++)
            {
                int dx = originX + x;
                if (dx < 0 || dx >= width)
                {
                    continue;
                }

                int si = badgeRow + (x * 4);
                double a = _badge[si + 3] / 255.0;
                if (a <= 0)
                {
                    continue;
                }

                int di = frameRow + (dx * 4);
                bgra[di] = Blend(bgra[di], _badge[si], a);
                bgra[di + 1] = Blend(bgra[di + 1], _badge[si + 1], a);
                bgra[di + 2] = Blend(bgra[di + 2], _badge[si + 2], a);
            }
        }
    }

    /// <summary>
    /// Returns the cached straight-alpha BGRA badge bitmap for a frame of the given height, so a
    /// GPU compositor can upload it once instead of re-rasterizing. Returns false when branding
    /// could not be rasterized.
    /// </summary>
    public bool TryGetBadge(int frameHeight, out byte[] bgra, out int width, out int height, out int margin)
    {
        EnsureBadge(frameHeight);
        bgra = _badge ?? [];
        width = _badgeWidth;
        height = _badgeHeight;
        margin = _margin;
        return _badge is not null && _badgeWidth > 0 && _badgeHeight > 0;
    }

    private void EnsureBadge(int frameHeight)
    {
        if (_badge is not null && _builtForHeight == frameHeight)
        {
            return;
        }

        _builtForHeight = frameHeight;
        try
        {
            BuildBadge(frameHeight);
        }
        catch
        {
            // Branding is best-effort; never let a rendering failure break a recording.
            _badge = null;
            _badgeWidth = 0;
            _badgeHeight = 0;
        }
    }

    private void BuildBadge(int frameHeight)
    {
        float fontSize = Math.Clamp(frameHeight / 50f, 12f, 28f);
        float paddingH = fontSize * 0.7f;
        float paddingV = fontSize * 0.45f;
        _margin = (int)Math.Round(fontSize);

        using var font = new Font("Segoe UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);

        SizeF textSize;
        using (var measureBitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb))
        using (var measureGraphics = Graphics.FromImage(measureBitmap))
        {
            measureGraphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            textSize = measureGraphics.MeasureString(OverlayText, font);
        }

        int width = (int)Math.Ceiling(textSize.Width + (paddingH * 2));
        int height = (int)Math.Ceiling(textSize.Height + (paddingV * 2));
        if (width <= 0 || height <= 0)
        {
            _badge = null;
            return;
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
            graphics.Clear(Color.Transparent);

            float corner = height / 3f;
            using (var pillPath = CreateRoundedRect(new RectangleF(0, 0, width, height), corner))
            using (var pillBrush = new SolidBrush(Color.FromArgb(128, 0, 0, 0)))
            {
                graphics.FillPath(pillBrush, pillPath);
            }

            using var textBrush = new SolidBrush(Color.White);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            graphics.DrawString(OverlayText, font, textBrush, new RectangleF(0, 0, width, height), format);
        }

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var buffer = new byte[width * height * 4];
            int stride = data.Stride;
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + (y * stride), buffer, y * width * 4, width * 4);
            }

            _badge = buffer;
            _badgeWidth = width;
            _badgeHeight = height;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static GraphicsPath CreateRoundedRect(RectangleF rect, float radius)
    {
        float diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        var path = new GraphicsPath();
        if (diameter <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }

        var arc = new RectangleF(rect.X, rect.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rect.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static byte Blend(byte dst, byte src, double a) =>
        (byte)Math.Clamp((src * a) + (dst * (1 - a)), 0, 255);
}
