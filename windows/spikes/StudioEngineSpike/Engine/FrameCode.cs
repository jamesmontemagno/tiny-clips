using Vortice.Direct3D11;

namespace StudioEngineSpike.Engine;

/// <summary>Maps clip pixel coordinates to image pixel coordinates: image = origin + clip × scale.</summary>
/// <remarks>A negative <see cref="ScaleX"/> describes a mirrored camera.</remarks>
internal readonly record struct ClipMap(double OriginX, double OriginY, double ScaleX, double ScaleY)
{
    public static readonly ClipMap Identity = new(0, 0, 1, 1);

    public (double X, double Y) Apply(double clipX, double clipY) => (OriginX + (clipX * ScaleX), OriginY + (clipY * ScaleY));

    public ClipMap Offset(double dx, double dy) => this with { OriginX = OriginX + dx, OriginY = OriginY + dy };
}

/// <summary>Average colour of a sampled patch.</summary>
internal readonly record struct Rgb(double R, double G, double B)
{
    public double Luma => (0.2126 * R) + (0.7152 * G) + (0.0722 * B);

    public override string ToString() => $"({R:0},{G:0},{B:0})";

    public double MaxAbsDifference(Rgb other) => Math.Max(Math.Abs(R - other.R), Math.Max(Math.Abs(G - other.G), Math.Abs(B - other.B)));
}

/// <summary>
/// Reads the frame-number strip (see <see cref="TestMedia"/>) and the colour patches back out of
/// pixels: a decoded source frame, a composited preview frame, or a frame of the exported video.
/// </summary>
internal static class FrameCode
{
    /// <summary>Returned when the strip is missing, covered, or a blend of two frames.</summary>
    public const int Unreadable = -1;

    /// <summary>
    /// Decodes the strip from tightly packed top-down pixels. <paramref name="bytesPerPixel"/> is 4
    /// for BGRA or 1 for gray. Returns <see cref="Unreadable"/> unless every column has one bright
    /// and one dark cell.
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> pixels, int width, int height, int bytesPerPixel, ClipSpec clip, ClipMap map)
    {
        var cellPixels = Math.Min(Math.Abs(map.ScaleX), Math.Abs(map.ScaleY)) * clip.CodeCell;
        var radius = Math.Clamp((int)(cellPixels / 6), 0, 6);
        var value = 0;
        for (var column = 0; column < TestMedia.CodeBits; column++)
        {
            var centerX = clip.CodeX + ((column + 0.5) * clip.CodeCell);
            var top = SampleLuma(pixels, width, height, bytesPerPixel, map.Apply(centerX, clip.CodeY + (0.5 * clip.CodeCell)), radius);
            var bottom = SampleLuma(pixels, width, height, bytesPerPixel, map.Apply(centerX, clip.CodeY + (1.5 * clip.CodeCell)), radius);
            if (top < 0 || bottom < 0 || Math.Abs(top - bottom) < 96)
            {
                return Unreadable;
            }

            if (top > bottom)
            {
                value |= 1 << (TestMedia.CodeBits - 1 - column);
            }
        }

        return value;
    }

    /// <summary>Image-space bounding box of the strip, padded and clamped, for a partial GPU readback.</summary>
    public static (int X, int Y, int Width, int Height) Bounds(ClipSpec clip, ClipMap map, int imageWidth, int imageHeight)
    {
        var (x0, y0) = map.Apply(clip.CodeX, clip.CodeY);
        var (x1, y1) = map.Apply(clip.CodeX + clip.CodeWidth, clip.CodeY + clip.CodeHeight);
        return Clamp(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1), imageWidth, imageHeight);
    }

    public static (int X, int Y, int Width, int Height) PatchBounds(ClipSpec clip, ClipMap map, int imageWidth, int imageHeight)
    {
        var (x0, y0) = map.Apply(clip.PatchX, clip.PatchY);
        var (x1, y1) = map.Apply(clip.PatchX + (TestMedia.PatchColors.Length * clip.PatchPitch), clip.PatchY + clip.PatchSize);
        return Clamp(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1), imageWidth, imageHeight);
    }

    /// <summary>Average colour at the centre of each colour patch (BGRA pixels).</summary>
    public static Rgb[] ReadPatches(ReadOnlySpan<byte> bgra, int width, int height, ClipSpec clip, ClipMap map)
    {
        var result = new Rgb[TestMedia.PatchColors.Length];
        var patchPixels = Math.Min(Math.Abs(map.ScaleX), Math.Abs(map.ScaleY)) * clip.PatchSize;
        var radius = Math.Clamp((int)(patchPixels / 5), 0, 8);
        for (var index = 0; index < result.Length; index++)
        {
            var (x, y) = map.Apply(clip.PatchX + (index * clip.PatchPitch) + (clip.PatchSize / 2.0), clip.PatchY + (clip.PatchSize / 2.0));
            result[index] = SampleRgb(bgra, width, height, x, y, radius);
        }

        return result;
    }

    /// <summary>Average colour around a point in clip coordinates (BGRA pixels).</summary>
    public static Rgb ReadPoint(ReadOnlySpan<byte> bgra, int width, int height, ClipMap map, double clipX, double clipY, int radius)
    {
        var (x, y) = map.Apply(clipX, clipY);
        return SampleRgb(bgra, width, height, x, y, radius);
    }

    private static (int X, int Y, int Width, int Height) Clamp(double left, double top, double right, double bottom, int imageWidth, int imageHeight)
    {
        var x = Math.Clamp((int)Math.Floor(left) - 2, 0, imageWidth - 1);
        var y = Math.Clamp((int)Math.Floor(top) - 2, 0, imageHeight - 1);
        var r = Math.Clamp((int)Math.Ceiling(right) + 2, x + 1, imageWidth);
        var b = Math.Clamp((int)Math.Ceiling(bottom) + 2, y + 1, imageHeight);
        return (x, y, r - x, b - y);
    }

    private static double SampleLuma(ReadOnlySpan<byte> pixels, int width, int height, int bytesPerPixel, (double X, double Y) point, int radius)
    {
        var cx = (int)Math.Floor(point.X);
        var cy = (int)Math.Floor(point.Y);
        if (cx - radius < 0 || cy - radius < 0 || cx + radius >= width || cy + radius >= height)
        {
            return -1;
        }

        long sum = 0;
        var count = 0;
        for (var y = cy - radius; y <= cy + radius; y++)
        {
            var row = pixels.Slice(y * width * bytesPerPixel, width * bytesPerPixel);
            for (var x = cx - radius; x <= cx + radius; x++)
            {
                if (bytesPerPixel == 1)
                {
                    sum += row[x];
                }
                else
                {
                    var offset = x * bytesPerPixel;
                    sum += ((row[offset] * 29) + (row[offset + 1] * 150) + (row[offset + 2] * 77)) >> 8;
                }

                count++;
            }
        }

        return sum / (double)count;
    }

    private static Rgb SampleRgb(ReadOnlySpan<byte> bgra, int width, int height, double px, double py, int radius)
    {
        var cx = (int)Math.Floor(px);
        var cy = (int)Math.Floor(py);
        if (cx - radius < 0 || cy - radius < 0 || cx + radius >= width || cy + radius >= height)
        {
            return new Rgb(-1, -1, -1);
        }

        long r = 0;
        long g = 0;
        long b = 0;
        var count = 0;
        for (var y = cy - radius; y <= cy + radius; y++)
        {
            var row = bgra.Slice(y * width * 4, width * 4);
            for (var x = cx - radius; x <= cx + radius; x++)
            {
                b += row[x * 4];
                g += row[(x * 4) + 1];
                r += row[(x * 4) + 2];
                count++;
            }
        }

        return new Rgb(r / (double)count, g / (double)count, b / (double)count);
    }
}

/// <summary>Reads strips and patches straight from GPU textures through a small staging readback.</summary>
/// <remarks>Not thread-safe (it reuses one buffer): each thread that reads creates its own.</remarks>
internal sealed class FrameCodeReader
{
    private readonly GraphicsDevice _graphics;
    private byte[] _buffer = new byte[256 * 1024];

    public FrameCodeReader(GraphicsDevice graphics) => _graphics = graphics;

    /// <summary>Frame number shown by <paramref name="clip"/> inside the texture, or <see cref="FrameCode.Unreadable"/>.</summary>
    /// <remarks>Takes <see cref="GraphicsDevice.Gate"/>; this forces a GPU sync and is measurement-only.</remarks>
    public int Read(ID3D11Texture2D texture, uint subresource, int textureWidth, int textureHeight, ClipSpec clip, ClipMap map)
    {
        var bounds = FrameCode.Bounds(clip, map, textureWidth, textureHeight);
        var pixels = ReadRegion(texture, subresource, bounds);
        return FrameCode.Decode(pixels, bounds.Width, bounds.Height, 4, clip, map.Offset(-bounds.X, -bounds.Y));
    }

    public Rgb[] ReadPatches(ID3D11Texture2D texture, uint subresource, int textureWidth, int textureHeight, ClipSpec clip, ClipMap map)
    {
        var bounds = FrameCode.PatchBounds(clip, map, textureWidth, textureHeight);
        var pixels = ReadRegion(texture, subresource, bounds);
        return FrameCode.ReadPatches(pixels, bounds.Width, bounds.Height, clip, map.Offset(-bounds.X, -bounds.Y));
    }

    private ReadOnlySpan<byte> ReadRegion(ID3D11Texture2D texture, uint subresource, (int X, int Y, int Width, int Height) bounds)
    {
        var needed = bounds.Width * bounds.Height * 4;
        if (_buffer.Length < needed)
        {
            _buffer = new byte[needed];
        }

        lock (_graphics.Gate)
        {
            _graphics.ReadRegion(texture, subresource, bounds.X, bounds.Y, bounds.Width, bounds.Height, _buffer);
        }

        return _buffer.AsSpan(0, needed);
    }
}
