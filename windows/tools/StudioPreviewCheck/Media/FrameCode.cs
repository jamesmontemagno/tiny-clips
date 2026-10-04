using TinyClips.Core.Studio;

namespace TinyClips.Tools.StudioPreviewCheck.Media;

/// <summary>Maps clip pixel coordinates to image pixel coordinates: image = origin + clip × scale.</summary>
/// <remarks>A negative <see cref="ScaleX"/> is a mirrored camera.</remarks>
internal readonly record struct ClipMap(double OriginX, double OriginY, double ScaleX, double ScaleY)
{
    public (double X, double Y) Apply(double clipX, double clipY) => (OriginX + (clipX * ScaleX), OriginY + (clipY * ScaleY));

    public ClipMap Offset(double dx, double dy) => this with { OriginX = OriginX + dx, OriginY = OriginY + dy };

    /// <summary>A clip scaled into a texture of another size, as a player's copy target is.</summary>
    public static ClipMap Scaled(ClipSpec clip, int width, int height) => new(0, 0, (double)width / clip.Width, (double)height / clip.Height);

    /// <summary>Where a layer of the resolved scene puts its clip's pixels.</summary>
    public static ClipMap ForLayer(ClipSpec clip, StudioFrameRect rect, StudioFrameRect source, bool mirror)
    {
        var scaleX = rect.Width / (source.Width * clip.Width);
        var scaleY = rect.Height / (source.Height * clip.Height);
        var originY = rect.Y - (source.Y * clip.Height * scaleY);
        return mirror
            ? new ClipMap(rect.X + rect.Width + (source.X * clip.Width * scaleX), originY, -scaleX, scaleY)
            : new ClipMap(rect.X - (source.X * clip.Width * scaleX), originY, scaleX, scaleY);
    }
}

/// <summary>A colour read from pixels.</summary>
internal readonly record struct Rgb(double R, double G, double B)
{
    public double Distance(Rgb other) => Math.Max(Math.Abs(R - other.R), Math.Max(Math.Abs(G - other.G), Math.Abs(B - other.B)));

    public override string ToString() => $"({R:0},{G:0},{B:0})";
}

/// <summary>Reads the frame-number strip (see <see cref="TestMedia"/>) and colours back out of BGRA pixels.</summary>
internal static class FrameCode
{
    /// <summary>Returned when the strip is missing, covered, or a blend of two frames.</summary>
    public const int Unreadable = -1;

    /// <summary>
    /// Decodes the strip from tightly packed top-down BGRA pixels. Returns
    /// <see cref="Unreadable"/> unless every column has one bright and one dark cell.
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> bgra, int width, int height, ClipSpec clip, ClipMap map)
    {
        var cellPixels = Math.Min(Math.Abs(map.ScaleX), Math.Abs(map.ScaleY)) * clip.CodeCell;
        var radius = Math.Clamp((int)(cellPixels / 6), 0, 6);
        var value = 0;
        for (var column = 0; column < TestMedia.CodeBits; column++)
        {
            var centerX = clip.CodeX + ((column + 0.5) * clip.CodeCell);
            var top = Luma(bgra, width, height, map.Apply(centerX, clip.CodeY + (0.5 * clip.CodeCell)), radius);
            var bottom = Luma(bgra, width, height, map.Apply(centerX, clip.CodeY + (1.5 * clip.CodeCell)), radius);
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

    /// <summary>Image-space bounding box of the strip, padded and clamped: the part of an image a read needs.</summary>
    public static (int X, int Y, int Width, int Height) Bounds(ClipSpec clip, ClipMap map, int imageWidth, int imageHeight)
    {
        var (x0, y0) = map.Apply(clip.CodeX, clip.CodeY);
        var (x1, y1) = map.Apply(clip.CodeX + clip.CodeWidth, clip.CodeY + clip.CodeHeight);
        var left = Math.Clamp((int)Math.Floor(Math.Min(x0, x1)) - 2, 0, Math.Max(0, imageWidth - 1));
        var top = Math.Clamp((int)Math.Floor(Math.Min(y0, y1)) - 2, 0, Math.Max(0, imageHeight - 1));
        var right = Math.Clamp((int)Math.Ceiling(Math.Max(x0, x1)) + 2, left + 1, Math.Max(left + 1, imageWidth));
        var bottom = Math.Clamp((int)Math.Ceiling(Math.Max(y0, y1)) + 2, top + 1, Math.Max(top + 1, imageHeight));
        return (left, top, right - left, bottom - top);
    }

    /// <summary>The colour at the centre of each colour patch.</summary>
    public static Rgb[] Patches(ReadOnlySpan<byte> bgra, int width, int height, ClipSpec clip, ClipMap map)
    {
        var result = new Rgb[TestMedia.PatchColors.Length];
        var patchPixels = Math.Min(Math.Abs(map.ScaleX), Math.Abs(map.ScaleY)) * clip.PatchSize;
        var radius = Math.Clamp((int)(patchPixels / 5), 0, 8);
        for (var index = 0; index < result.Length; index++)
        {
            var (x, y) = map.Apply(clip.PatchX + (index * clip.PatchPitch) + (clip.PatchSize / 2.0), clip.PatchY + (clip.PatchSize / 2.0));
            result[index] = Color(bgra, width, height, x, y, radius);
        }

        return result;
    }

    /// <summary>Average colour around an image point, or (-1,-1,-1) outside the image.</summary>
    public static Rgb Color(ReadOnlySpan<byte> bgra, int width, int height, double x, double y, int radius)
    {
        var cx = (int)Math.Floor(x);
        var cy = (int)Math.Floor(y);
        if (cx - radius < 0 || cy - radius < 0 || cx + radius >= width || cy + radius >= height)
        {
            return new Rgb(-1, -1, -1);
        }

        long r = 0;
        long g = 0;
        long b = 0;
        var count = 0;
        for (var row = cy - radius; row <= cy + radius; row++)
        {
            var line = bgra.Slice(row * width * 4, width * 4);
            for (var column = cx - radius; column <= cx + radius; column++)
            {
                b += line[column * 4];
                g += line[(column * 4) + 1];
                r += line[(column * 4) + 2];
                count++;
            }
        }

        return new Rgb(r / (double)count, g / (double)count, b / (double)count);
    }

    private static double Luma(ReadOnlySpan<byte> bgra, int width, int height, (double X, double Y) point, int radius)
    {
        var color = Color(bgra, width, height, point.X, point.Y, radius);
        return color.R < 0 ? -1 : (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);
    }
}

/// <summary>What a project's scene looks like at one canvas size: where each clip's pixels are, and whether the camera shows.</summary>
internal sealed record SceneView(int Width, int Height, ClipMap? ScreenMap, ClipMap? CameraMap, StudioFrameRect? CameraRect, StudioLayout Layout)
{
    /// <summary>Resolves the layout the way the renderer does. The camera's visibility in time is not part of it.</summary>
    public static SceneView Resolve(StudioProject project, ClipSpec screen, ClipSpec? camera, int width, int height)
    {
        var frame = StudioLayoutResolver.Resolve(project, 0, width, height);
        ClipMap? screenMap = frame.Screen is { } s ? ClipMap.ForLayer(screen, s.Rect, s.Source, mirror: false) : null;
        ClipMap? cameraMap = frame.Camera is { } c && camera is not null ? ClipMap.ForLayer(camera, c.Rect, c.Source, c.Mirror) : null;
        return new SceneView(width, height, screenMap, cameraMap, frame.Camera?.Rect, frame.Layout);
    }

    public SceneView Offset(double dx, double dy) => this with
    {
        ScreenMap = ScreenMap?.Offset(dx, dy),
        CameraMap = CameraMap?.Offset(dx, dy),
        CameraRect = CameraRect is { } rect ? new StudioFrameRect(rect.X + dx, rect.Y + dy, rect.Width, rect.Height) : null,
    };
}

/// <summary>Which frame of each clip an image shows, read from its pixels.</summary>
/// <param name="Screen">The screen clip's frame, or <see cref="FrameCode.Unreadable"/>.</param>
/// <param name="Camera">The camera clip's frame, or <see cref="FrameCode.Unreadable"/> when no camera is visible.</param>
internal readonly record struct Shown(int Screen, int Camera)
{
    public static Shown Read(ReadOnlySpan<byte> bgra, int width, int height, SceneView view, ClipSpec screen, ClipSpec? camera)
    {
        var screenFrame = view.ScreenMap is { } s ? FrameCode.Decode(bgra, width, height, screen, s) : FrameCode.Unreadable;
        var cameraFrame = view.CameraMap is { } c && camera is not null ? FrameCode.Decode(bgra, width, height, camera, c) : FrameCode.Unreadable;
        return new Shown(screenFrame, cameraFrame);
    }

    public override string ToString() => $"screen {Text(Screen)}, camera {Text(Camera)}";

    private static string Text(int frame) => frame == FrameCode.Unreadable ? "none" : frame.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
