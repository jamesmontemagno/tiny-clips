using System.Globalization;
using Vortice.Direct3D11;

namespace StudioEngineSpike.Engine;

/// <summary>
/// Six known colours in every frame of the test clips (four patches plus the always-black and
/// always-white cells of the strip's first column). <c>media</c> mode records what ffmpeg decodes
/// them to; the other modes compare their own decode paths against that, which shows up a wrong
/// YUV matrix or a limited/full range mix-up as a number instead of "looks a bit washed out".
/// </summary>
internal static class ReferenceColors
{
    public static readonly string[] Names = { "red", "lime", "blue", "gray", "black", "white" };

    public static string FilePath(string root) => Path.Combine(TestMedia.Directory(root), "reference-colors.txt");

    public static Rgb[] Read(ReadOnlySpan<byte> bgra, int width, int height, ClipSpec clip, ClipMap map)
    {
        var patches = FrameCode.ReadPatches(bgra, width, height, clip, map);
        var cellPixels = Math.Min(Math.Abs(map.ScaleX), Math.Abs(map.ScaleY)) * clip.CodeCell;
        var radius = Math.Clamp((int)(cellPixels / 5), 0, 8);

        // Column 0 is the 2048 bit, which no frame of a 900-frame clip sets: top is black, bottom is white.
        var x = clip.CodeX + (clip.CodeCell / 2.0);
        var black = FrameCode.ReadPoint(bgra, width, height, map, x, clip.CodeY + (0.5 * clip.CodeCell), radius);
        var white = FrameCode.ReadPoint(bgra, width, height, map, x, clip.CodeY + (1.5 * clip.CodeCell), radius);
        return new[] { patches[0], patches[1], patches[2], patches[3], black, white };
    }

    /// <summary>
    /// Reference values from a planar YUV 4:2:0 frame exactly as the decoder produced it, converted
    /// with the BT.709 limited-range matrix in floating point (what a correct player shows).
    /// </summary>
    public static Rgb[] FromYuv420(ReadOnlySpan<byte> yuv, int width, int height, ClipSpec clip) => FromYuv420(yuv, width, height, clip, ClipMap.Identity);

    /// <summary>The same for a frame in which the clip appears scaled and moved (an exported composite).</summary>
    public static Rgb[] FromYuv420(ReadOnlySpan<byte> yuv, int width, int height, ClipSpec clip, ClipMap map)
    {
        var scale = Math.Min(Math.Abs(map.ScaleX), Math.Abs(map.ScaleY));
        var result = new Rgb[Names.Length];
        var patchRadius = Math.Max(1, (int)(clip.PatchSize * scale / 5));
        for (var index = 0; index < TestMedia.PatchColors.Length; index++)
        {
            var (x, y) = map.Apply(clip.PatchX + (index * clip.PatchPitch) + (clip.PatchSize / 2.0), clip.PatchY + (clip.PatchSize / 2.0));
            result[index] = YuvAt(yuv, width, height, x, y, patchRadius);
        }

        var cellRadius = Math.Max(1, (int)(clip.CodeCell * scale / 5));
        var cellX = clip.CodeX + (clip.CodeCell / 2.0);
        var (blackX, blackY) = map.Apply(cellX, clip.CodeY + (0.5 * clip.CodeCell));
        var (whiteX, whiteY) = map.Apply(cellX, clip.CodeY + (1.5 * clip.CodeCell));
        result[4] = YuvAt(yuv, width, height, blackX, blackY, cellRadius);
        result[5] = YuvAt(yuv, width, height, whiteX, whiteY, cellRadius);
        return result;
    }

    private static Rgb YuvAt(ReadOnlySpan<byte> yuv, int width, int height, double x, double y, int radius)
    {
        var chromaWidth = width / 2;
        var chromaHeight = height / 2;
        var luma = yuv[..(width * height)];
        var u = yuv.Slice(width * height, chromaWidth * chromaHeight);
        var v = yuv.Slice((width * height) + (chromaWidth * chromaHeight), chromaWidth * chromaHeight);
        var py = Average(luma, width, (int)x, (int)y, radius);
        var pu = Average(u, chromaWidth, (int)(x / 2), (int)(y / 2), radius / 2);
        var pv = Average(v, chromaWidth, (int)(x / 2), (int)(y / 2), radius / 2);
        var c = 1.164383 * (py - 16);
        return new Rgb(
            Math.Clamp(c + (1.792741 * (pv - 128)), 0, 255),
            Math.Clamp(c - (0.213249 * (pu - 128)) - (0.532909 * (pv - 128)), 0, 255),
            Math.Clamp(c + (2.112402 * (pu - 128)), 0, 255));

        static double Average(ReadOnlySpan<byte> plane, int stride, int cx, int cy, int radius)
        {
            long sum = 0;
            var count = 0;
            for (var y = cy - radius; y <= cy + radius; y++)
            {
                for (var x = cx - radius; x <= cx + radius; x++)
                {
                    sum += plane[(y * stride) + x];
                    count++;
                }
            }

            return sum / (double)count;
        }
    }

    /// <summary>Image-space box covering the strip and the patches, for one GPU readback.</summary>
    public static (int X, int Y, int Width, int Height) Bounds(ClipSpec clip, ClipMap map, int imageWidth, int imageHeight)
    {
        var a = FrameCode.Bounds(clip, map, imageWidth, imageHeight);
        var b = FrameCode.PatchBounds(clip, map, imageWidth, imageHeight);
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        var right = Math.Max(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return (x, y, right - x, bottom - y);
    }

    public static Rgb[] Read(GraphicsDevice graphics, ID3D11Texture2D texture, uint subresource, int textureWidth, int textureHeight, ClipSpec clip, ClipMap map)
    {
        var bounds = Bounds(clip, map, textureWidth, textureHeight);
        var pixels = new byte[bounds.Width * bounds.Height * 4];
        lock (graphics.Gate)
        {
            graphics.ReadRegion(texture, subresource, bounds.X, bounds.Y, bounds.Width, bounds.Height, pixels);
        }

        return Read(pixels, bounds.Width, bounds.Height, clip, map.Offset(-bounds.X, -bounds.Y));
    }

    public static string Describe(Rgb[] colors) => string.Join("  ", colors.Select((color, index) => $"{Names[index]}={color}"));

    /// <summary>Largest per-channel error against the reference, and which colour it is on.</summary>
    public static string Compare(Rgb[] reference, Rgb[] actual)
    {
        var worst = 0.0;
        var worstName = Names[0];
        for (var index = 0; index < reference.Length; index++)
        {
            var difference = reference[index].MaxAbsDifference(actual[index]);
            if (difference > worst)
            {
                worst = difference;
                worstName = Names[index];
            }
        }

        return $"max channel error {worst:0.#} (on {worstName}); {Describe(actual)}";
    }

    public static double MaxError(Rgb[] reference, Rgb[] actual) =>
        reference.Select((color, index) => color.MaxAbsDifference(actual[index])).Max();

    public static string Serialize(ClipSpec clip, Rgb[] colors)
    {
        var c = CultureInfo.InvariantCulture;
        return clip.FileName + " " + string.Join(' ', colors.Select(color => string.Create(c, $"{color.R:0.##},{color.G:0.##},{color.B:0.##}")));
    }

    /// <summary>The colours recorded by <c>media</c> mode, or null when it has not been run.</summary>
    public static Rgb[]? Load(string root, ClipSpec clip)
    {
        var path = FilePath(root);
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != Names.Length + 1 || parts[0] != clip.FileName)
            {
                continue;
            }

            return parts.Skip(1).Select(text =>
            {
                var channels = text.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
                return new Rgb(channels[0], channels[1], channels[2]);
            }).ToArray();
        }

        return null;
    }
}
