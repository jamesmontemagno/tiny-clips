using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TinyClips.Core.Capture;

internal sealed record BrandingBadge(byte[] Bgra, int Width, int Height, int Margin);

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

    private readonly Func<int, BrandingBadge?> _rasterize;
    private BrandingBadge? _badge;
    private int _builtForHeight = -1;

    public BrandingOverlayCompositor() : this(BuildBadge)
    {
    }

    internal BrandingOverlayCompositor(Func<int, BrandingBadge?> rasterize) => _rasterize = rasterize;

    /// <summary>
    /// Rasterizes on a worker thread before recording starts. The owner must await preparation
    /// before drawing or preparing another size. Cancellation waits for GDI+ to return before
    /// discarding its result, so teardown cannot race preparation.
    /// </summary>
    public async Task<bool> PrepareAsync(int frameHeight, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameHeight);
        var started = Stopwatch.GetTimestamp();
        var result = "cancelled";
        try
        {
            var ready = await Task.Run(
                () => EnsureBadge(frameHeight, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            result = ready ? "ready" : "unavailable";
            return ready;
        }
        finally
        {
            WebcamDiagnostics.Log($"Branding preparation: cpu={result} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}.");
        }
    }

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
        DrawPrepared(bgra, width, height);
    }

    /// <summary>Draws only an already prepared badge; never initializes fonts or rasterizes.</summary>
    public void DrawPrepared(byte[] bgra, int width, int height)
    {
        var badge = _badge;
        if (width <= 0 || height <= 0 || _builtForHeight != height || badge is null)
        {
            return;
        }

        int originX = width - badge.Width - badge.Margin;
        int originY = height - badge.Height - badge.Margin;

        for (int y = 0; y < badge.Height; y++)
        {
            int dy = originY + y;
            if (dy < 0 || dy >= height)
            {
                continue;
            }

            int badgeRow = y * badge.Width * 4;
            int frameRow = dy * width * 4;

            for (int x = 0; x < badge.Width; x++)
            {
                int dx = originX + x;
                if (dx < 0 || dx >= width)
                {
                    continue;
                }

                int si = badgeRow + (x * 4);
                double a = badge.Bgra[si + 3] / 255.0;
                if (a <= 0)
                {
                    continue;
                }

                int di = frameRow + (dx * 4);
                bgra[di] = Blend(bgra[di], badge.Bgra[si], a);
                bgra[di + 1] = Blend(bgra[di + 1], badge.Bgra[si + 1], a);
                bgra[di + 2] = Blend(bgra[di + 2], badge.Bgra[si + 2], a);
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
        return TryGetPreparedBadge(frameHeight, out bgra, out width, out height, out margin);
    }

    /// <summary>Reads the prepared cache without doing first-use work on the frame path.</summary>
    public bool TryGetPreparedBadge(int frameHeight, out byte[] bgra, out int width, out int height, out int margin)
    {
        var badge = _builtForHeight == frameHeight ? _badge : null;
        bgra = badge?.Bgra ?? [];
        width = badge?.Width ?? 0;
        height = badge?.Height ?? 0;
        margin = badge?.Margin ?? 0;
        return badge is not null;
    }

    private bool EnsureBadge(int frameHeight, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_builtForHeight == frameHeight)
        {
            return _badge is not null;
        }

        BrandingBadge? badge;
        try
        {
            badge = _rasterize(frameHeight);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            WebcamDiagnostics.Log($"Branding rasterization unavailable (0x{(uint)ex.HResult:X8} {ex.GetType().Name}); recording without branding.");
            badge = null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _badge = badge;
        // Remember failures too: do not retry font initialization on every frame.
        _builtForHeight = frameHeight;
        return badge is not null;
    }

    private static BrandingBadge? BuildBadge(int frameHeight)
    {
        float fontSize = Math.Clamp(frameHeight / 50f, 12f, 28f);
        float paddingH = fontSize * 0.7f;
        float paddingV = fontSize * 0.45f;
        int margin = (int)Math.Round(fontSize);

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
            return null;
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

            return new BrandingBadge(buffer, width, height, margin);
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
