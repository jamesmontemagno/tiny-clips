using System.Diagnostics;
using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;
using TinyClips.Tools.StudioPreviewCheck.Media;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

/// <summary>
/// Where the edges inside a clip's frame-number strip are in a picture, against where a layout
/// puts them. The strip is drawn with its clip, so its edges move with the clip's rectangle and
/// with the part of the clip that is shown: a scene drawn with the layout of another frame than
/// its picture has them in the wrong places, by as far as the layout moves in a frame.
/// </summary>
/// <param name="Edges">How many edges were looked for.</param>
/// <param name="Worst">The largest distance of an edge from its place, in pixels. An edge that is not within reach of its place counts as the reach.</param>
internal readonly record struct EdgeFit(int Edges, double Worst)
{
    public bool Within(double pixels) => Edges >= 8 && Worst <= pixels;

    public override string ToString() => Edges == 0 ? "no edges" : string.Create(CultureInfo.InvariantCulture, $"{Worst:0.00} px ({Edges} edges)");
}

internal static class StripEdges
{
    /// <summary>
    /// Measures the edges of the strip of <paramref name="frame"/> in a picture, each to a
    /// fraction of a pixel, against where <paramref name="map"/> puts them.
    /// </summary>
    public static EdgeFit Measure(ReadOnlySpan<byte> bgra, int width, int height, ClipSpec clip, ClipMap map, int frame)
    {
        var cell = Math.Min(Math.Abs(map.ScaleX), Math.Abs(map.ScaleY)) * clip.CodeCell;
        var reach = (int)Math.Clamp(Math.Floor(cell / 2) - 2, 3, 9);
        var edges = 0;
        var worst = 0.0;

        // Between two columns whose cells differ, in the upper row and the lower.
        for (var column = 1; column < TestMedia.CodeBits; column++)
        {
            if (Bit(frame, column - 1) == Bit(frame, column))
            {
                continue;
            }

            for (var row = 0; row < 2; row++)
            {
                var (x, y) = map.Apply(clip.CodeX + (column * clip.CodeCell), clip.CodeY + ((row + 0.5) * clip.CodeCell));
                edges++;
                worst = Math.Max(worst, Crossing(bgra, width, height, x, y, reach, alongX: true) is { } at ? Math.Abs(at - x) : reach);
            }
        }

        // Between the two rows, which differ in every column.
        for (var column = 0; column < TestMedia.CodeBits; column++)
        {
            var (x, y) = map.Apply(clip.CodeX + ((column + 0.5) * clip.CodeCell), clip.CodeY + clip.CodeCell);
            edges++;
            worst = Math.Max(worst, Crossing(bgra, width, height, x, y, reach, alongX: false) is { } at ? Math.Abs(at - y) : reach);
        }

        return new EdgeFit(edges, worst);
    }

    /// <summary>Whether a column's upper cell is the bright one in the strip of a frame.</summary>
    private static bool Bit(int frame, int column) => ((frame >> (TestMedia.CodeBits - 1 - column)) & 1) != 0;

    /// <summary>
    /// Where the brightness crosses the middle between its two levels along a line through
    /// (<paramref name="x"/>, <paramref name="y"/>), as a coordinate along that line, or null when
    /// there is no edge within reach.
    /// </summary>
    private static double? Crossing(ReadOnlySpan<byte> bgra, int width, int height, double x, double y, int reach, bool alongX)
    {
        var along = alongX ? x : y;
        var across = (int)Math.Floor(alongX ? y : x);
        var first = (int)Math.Floor(along) - reach;
        var count = (2 * reach) + 2;
        Span<double> luma = stackalloc double[count];
        for (var index = 0; index < count; index++)
        {
            // Three lines side by side, to even out what the video's compression left.
            double sum = 0;
            for (var offset = -1; offset <= 1; offset++)
            {
                var px = alongX ? first + index : across + offset;
                var py = alongX ? across + offset : first + index;
                if (px < 0 || py < 0 || px >= width || py >= height)
                {
                    return null;
                }

                var at = ((py * width) + px) * 4;
                sum += (0.114 * bgra[at]) + (0.587 * bgra[at + 1]) + (0.299 * bgra[at + 2]);
            }

            luma[index] = sum / 3;
        }

        var before = (luma[0] + luma[1]) / 2;
        var after = (luma[count - 1] + luma[count - 2]) / 2;
        if (Math.Abs(before - after) < 96)
        {
            return null;
        }

        // The crossing nearest to where the edge should be. A pixel's value stands for its middle.
        var middle = (before + after) / 2;
        double? best = null;
        for (var index = 0; index + 1 < count; index++)
        {
            var a = luma[index] - middle;
            var b = luma[index + 1] - middle;
            if ((a <= 0) == (b <= 0) || a == b)
            {
                continue;
            }

            var crossing = first + index + 0.5 + (a / (a - b));
            if (best is null || Math.Abs(crossing - along) < Math.Abs(best.Value - along))
            {
                best = crossing;
            }
        }

        return best;
    }
}

/// <summary>What one scene shows, as read from its pixels.</summary>
/// <param name="At">Stopwatch timestamp of the scene, taken just before it was presented.</param>
/// <param name="Screen">The screen clip's frame, or <see cref="FrameCode.Unreadable"/>.</param>
/// <param name="ScreenFit">The screen strip's edges against the layout of that frame.</param>
/// <param name="FitsFrame">For a scene whose edges are not where its own frame puts them: the frame whose layout they fit, or null when none of those around it does.</param>
/// <param name="Camera">The camera clip's frame, or <see cref="FrameCode.Unreadable"/>.</param>
/// <param name="CameraFit">The camera strip's edges against the layout of the screen's frame.</param>
/// <param name="CameraExpected">Whether the layout of the screen's frame shows a camera.</param>
/// <param name="Slot">
/// The frame of the timeline whose layout the screen's edges fit, or -1 when they fit none of
/// those around the picture's. On the usual clips it is <paramref name="Screen"/> for a scene
/// that is right; for a clip whose frames are not one to a slot it is what the scene is judged
/// by (<see cref="MovingLayout.SlotsOfFrame"/>).
/// </param>
internal readonly record struct SceneFit(long At, int Screen, EdgeFit ScreenFit, int? FitsFrame, int Camera, EdgeFit CameraFit, bool CameraExpected, int Slot = -1);

/// <summary>
/// Reads a scene of a project whose layout moves: which frame the screen shows, from its strip,
/// and whether both clips are where the layout of that frame puts them. The layout is worked out
/// here, from the project and <see cref="StudioLayoutResolver"/>, for the middle of the frame,
/// which is the instant a frame stands for (docs\studio-project-format.md, section 6.5); a layer's
/// rectangle is drawn on whole pixels, as the renderer draws it.
/// </summary>
internal sealed class MovingLayout(StudioProject project, ClipSpec screen, ClipSpec? camera, int width, int height)
{
    /// <summary>
    /// How far, in pixels, an edge may be from where the layout of its frame puts it. What was
    /// measured of scenes that are right is in the checks' notes; a layout that moves is a pixel
    /// or more further on with every frame.
    /// </summary>
    public const double Tolerance = 1.0;

    // A frame before a long gap is shown by many slots. This many of them are looked at.
    private const int MostSlotsOfAFrame = 90;

    /// <summary>The frames a second of the timeline the layout is worked out for.</summary>
    public double Fps { get; init; } = TestMedia.Fps;

    /// <summary>
    /// For a screen clip whose frames are not one to a slot, as a recording's are not: the
    /// frames of the timeline in which a frame of the file is right, the first and the last.
    /// The strip then holds the frame's place in the file, and a scene is read against the
    /// layouts of those timeline frames. Not set, a frame's number is its timeline frame's.
    /// </summary>
    public Func<int, (int First, int Last)>? SlotsOfFrame { get; init; }

    public int Width => width;

    public int Height => height;

    public ClipSpec Screen => screen;

    public ClipSpec? Camera => camera;

    public StudioResolvedFrame Resolve(int frame) =>
        StudioLayoutResolver.Resolve(project, (frame + 0.5) / Fps, width, height);

    /// <summary>Where the layout of a frame puts the screen clip's pixels, or null when it shows no screen.</summary>
    public ClipMap? ScreenMap(int frame) =>
        Resolve(frame).Screen is { } layer ? ClipMap.ForLayer(screen, WholePixels(layer.Rect), layer.Source, mirror: false) : null;

    /// <summary>Where the layout of a frame puts the camera clip's pixels, or null when it shows no camera.</summary>
    public ClipMap? CameraMap(int frame) =>
        camera is not null && Resolve(frame).Camera is { Visible: true } layer ? ClipMap.ForLayer(camera, WholePixels(layer.Rect), layer.Source, layer.Mirror) : null;

    /// <summary>The rectangles of a frame's layout, in words.</summary>
    public string Describe(int frame)
    {
        var layout = Resolve(frame);
        static string Rect(StudioFrameRect r) => string.Create(CultureInfo.InvariantCulture, $"({r.X:0.0}, {r.Y:0.0}, {r.Width:0.0} x {r.Height:0.0})");
        static string Part(StudioFrameRect r) => r is { X: 0, Y: 0, Width: 1, Height: 1 } ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" showing ({r.X:0.000}, {r.Y:0.000}, {r.Width:0.000} x {r.Height:0.000})");
        return $"{layout.Layout}: screen {(layout.Screen is { } s ? Rect(s.Rect) + Part(s.Source) : "none")}, camera {(layout.Camera is { Visible: true } c ? Rect(c.Rect) : "none")}";
    }

    /// <summary>
    /// Reads one scene from the parts of it that were kept. Each part is given with where its
    /// first pixel is in the scene.
    /// </summary>
    /// <param name="last">A frame at or just before this scene's; set to the frame read.</param>
    public SceneFit Read(
        long at,
        ReadOnlySpan<byte> screenPixels,
        (int X, int Y, int Width, int Height) screenRegion,
        ReadOnlySpan<byte> cameraPixels,
        (int X, int Y, int Width, int Height) cameraRegion,
        ref int last)
    {
        var r = screenRegion;

        // Which frame the picture is: the strip, looked for where each of the frames around the
        // last one would put it. It counts when it reads the same from where its own frame puts
        // it. For a clip whose frames are not one to a slot, a strip that reads at all counts,
        // and is looked for further on: after a gap the next frame is many slots later.
        var frame = FrameCode.Unreadable;
        var readAt = last;
        var reach = SlotsOfFrame is null ? 24 : 150;
        for (var candidate = Math.Max(0, last - 3); candidate <= last + reach && frame == FrameCode.Unreadable; candidate++)
        {
            if (ScreenMap(candidate) is not { } map)
            {
                continue;
            }

            var read = FrameCode.Decode(screenPixels, r.Width, r.Height, screen, map.Offset(-r.X, -r.Y));
            if (read != FrameCode.Unreadable
                && (SlotsOfFrame is not null
                    || (ScreenMap(read) is { } own && FrameCode.Decode(screenPixels, r.Width, r.Height, screen, own.Offset(-r.X, -r.Y)) == read)))
            {
                frame = read;
                readAt = candidate;
            }
        }

        if (frame == FrameCode.Unreadable)
        {
            return new SceneFit(at, frame, default, null, FrameCode.Unreadable, default, false);
        }

        // The timeline frames the picture is right in: its own on the grid.
        var (first, final) = SlotsOfFrame is { } slotsOf ? slotsOf(frame) : (frame, frame);
        final = Math.Min(final, first + MostSlotsOfAFrame);
        last = SlotsOfFrame is null ? frame : readAt;

        // The layout of which of them the edges fit, or come nearest to.
        var slot = -1;
        EdgeFit screenFit = default;
        for (var candidate = first; candidate <= final; candidate++)
        {
            if (ScreenMap(candidate) is not { } map)
            {
                continue;
            }

            var fit = StripEdges.Measure(screenPixels, r.Width, r.Height, screen, map.Offset(-r.X, -r.Y), frame);
            if (slot < 0 || fit.Worst < screenFit.Worst)
            {
                screenFit = fit;
                slot = candidate;
            }

            if (fit.Within(Tolerance))
            {
                break;
            }
        }

        if (slot < 0)
        {
            // None of those layouts shows a screen.
            return new SceneFit(at, frame, default, null, FrameCode.Unreadable, default, false);
        }

        int? fitsFrame = null;
        if (!screenFit.Within(Tolerance))
        {
            // The layout of which of the frames around it do the edges fit?
            var best = double.MaxValue;
            for (var other = Math.Max(0, first - 8); other <= final + 8; other++)
            {
                if ((other >= first && other <= final) || ScreenMap(other) is not { } map)
                {
                    continue;
                }

                var fit = StripEdges.Measure(screenPixels, r.Width, r.Height, screen, map.Offset(-r.X, -r.Y), frame);
                if (fit.Within(Tolerance) && fit.Worst < best)
                {
                    best = fit.Worst;
                    fitsFrame = other;
                }
            }
        }

        // The camera, where the layout of the screen's frame puts it. The players are their own
        // masters, so its frame may be the one before or after the frame that goes with the screen's.
        var cameraFrame = FrameCode.Unreadable;
        EdgeFit cameraFit = default;
        var fits = screenFit.Within(Tolerance) ? slot : fitsFrame ?? -1;
        var cameraMap = CameraMap(fits >= 0 && SlotsOfFrame is not null ? fits : slot);
        if (cameraMap is { } placed && camera is not null && cameraPixels.Length > 0)
        {
            var c = cameraRegion;
            var local = placed.Offset(-c.X, -c.Y);
            cameraFrame = FrameCode.Decode(cameraPixels, c.Width, c.Height, camera, local);
            if (cameraFrame != FrameCode.Unreadable)
            {
                cameraFit = StripEdges.Measure(cameraPixels, c.Width, c.Height, camera, local, cameraFrame);
            }
        }

        return new SceneFit(at, frame, screenFit, fitsFrame, cameraFrame, cameraFit, cameraMap is not null, fits);
    }

    /// <summary>Reads a whole picture of the scene, as a surface gives it.</summary>
    public SceneFit Read(Picture picture, int around)
    {
        var whole = (0, 0, picture.Width, picture.Height);
        var last = around;
        return Read(0, picture.Bgra, whole, picture.Bgra, whole, ref last);
    }

    private static StudioFrameRect WholePixels(StudioFrameRect rect)
    {
        var left = Math.Round(rect.X, MidpointRounding.AwayFromZero);
        var top = Math.Round(rect.Y, MidpointRounding.AwayFromZero);
        var right = Math.Round(rect.X + rect.Width, MidpointRounding.AwayFromZero);
        var bottom = Math.Round(rect.Y + rect.Height, MidpointRounding.AwayFromZero);
        return new StudioFrameRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }
}

/// <summary>
/// Keeps, of every scene an engine draws, the parts where the clips' frame-number strips can be
/// while a layout moves, and reads them when the playing is over.
/// </summary>
/// <remarks>
/// Everything a scene needs is made before the preview plays, so that keeping two hundred scenes
/// gives the garbage collector nothing to do in the middle of what is being timed.
/// </remarks>
internal sealed class MovingSceneRecorder : IDisposable
{
    private sealed class Kept(byte[] screen, byte[] camera)
    {
        public long At;

        public byte[] Screen { get; } = screen;

        public byte[] Camera { get; } = camera;
    }

    private readonly MovingLayout _layout;
    private readonly (int X, int Y, int Width, int Height) _screenRegion;
    private readonly (int X, int Y, int Width, int Height) _cameraRegion;
    private readonly bool _hasCamera;
    private readonly Kept[] _scenes;
    private ID3D11Texture2D? _staging;
    private nint _device;
    private int _count;
    private int _unkept;
    private string? _failure;

    /// <param name="first">The first frame whose layout the kept parts have to cover.</param>
    /// <param name="last">The last.</param>
    /// <param name="capacity">How many scenes can be kept.</param>
    public MovingSceneRecorder(MovingLayout layout, int first, int last, int capacity)
    {
        _layout = layout;
        (int X0, int Y0, int X1, int Y1)? screenBox = null;
        (int X0, int Y0, int X1, int Y1)? cameraBox = null;
        for (var frame = Math.Max(0, first); frame <= last; frame++)
        {
            if (layout.ScreenMap(frame) is { } map)
            {
                screenBox = Union(screenBox, FrameCode.Bounds(layout.Screen, map, layout.Width, layout.Height));
            }

            if (layout.Camera is { } camera && layout.CameraMap(frame) is { } cameraMap)
            {
                cameraBox = Union(cameraBox, FrameCode.Bounds(camera, cameraMap, layout.Width, layout.Height));
            }
        }

        _screenRegion = Padded(screenBox ?? (0, 0, 1, 1));
        _hasCamera = cameraBox is not null;
        _cameraRegion = Padded(cameraBox ?? (0, 0, 1, 1));
        _scenes = new Kept[capacity];
        for (var index = 0; index < capacity; index++)
        {
            _scenes[index] = new Kept(
                new byte[_screenRegion.Width * _screenRegion.Height * 4],
                _hasCamera ? new byte[_cameraRegion.Width * _cameraRegion.Height * 4] : []);
        }
    }

    /// <summary>Scenes kept so far.</summary>
    public int Count => Math.Min(Volatile.Read(ref _count), _scenes.Length);

    /// <summary>Scenes that were drawn when there was no room left to keep them.</summary>
    public int Unkept => Volatile.Read(ref _unkept);

    public string? Failure => Volatile.Read(ref _failure);

    /// <summary>Megabytes set aside for the scenes.</summary>
    public double Megabytes => _scenes.Length * (double)(_scenes[0].Screen.Length + _scenes[0].Camera.Length) / 1048576;

    /// <summary>The engine's after-render hook. Runs on the render thread with the device lock held.</summary>
    public unsafe void OnRender(StudioGraphicsDevice graphics, ID3D11Texture2D target, int width, int height)
    {
        var at = Stopwatch.GetTimestamp();
        if (width != _layout.Width || height != _layout.Height)
        {
            Volatile.Write(ref _failure, $"a scene of {width}x{height} was drawn where {_layout.Width}x{_layout.Height} was expected");
            return;
        }

        var index = _count;
        if (index >= _scenes.Length)
        {
            Interlocked.Increment(ref _unkept);
            return;
        }

        try
        {
            var r = _screenRegion;
            var c = _cameraRegion;
            if (_staging is null || _device != graphics.Device.NativePointer)
            {
                _staging?.Dispose();
                _staging = null;
                _device = graphics.Device.NativePointer;
                _staging = graphics.Device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)Math.Max(r.Width, c.Width),
                    Height = (uint)(r.Height + c.Height),
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                });
            }

            graphics.Context.CopySubresourceRegion(_staging, 0, 0, 0, 0, target, 0, new Box(r.X, r.Y, 0, r.X + r.Width, r.Y + r.Height, 1));
            if (_hasCamera)
            {
                graphics.Context.CopySubresourceRegion(_staging, 0, 0, (uint)r.Height, 0, target, 0, new Box(c.X, c.Y, 0, c.X + c.Width, c.Y + c.Height, 1));
            }

            var scene = _scenes[index];
            var mapped = graphics.Context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var source = (byte*)mapped.DataPointer;
                Copy(source, mapped.RowPitch, 0, r.Width, r.Height, scene.Screen);
                if (_hasCamera)
                {
                    Copy(source, mapped.RowPitch, r.Height, c.Width, c.Height, scene.Camera);
                }
            }
            finally
            {
                graphics.Context.Unmap(_staging, 0);
            }

            scene.At = at;
            Volatile.Write(ref _count, index + 1);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _failure, ex.Message);
        }
    }

    /// <summary>
    /// Reads every scene kept so far. To be called when the engine has stopped drawing into this
    /// recorder.
    /// </summary>
    /// <param name="startFrame">A frame at or just before the first scene's.</param>
    public List<SceneFit> Read(int startFrame)
    {
        var fits = new List<SceneFit>(Count);
        var last = startFrame;
        for (var index = 0; index < Count; index++)
        {
            var scene = _scenes[index];
            fits.Add(_layout.Read(scene.At, scene.Screen, _screenRegion, scene.Camera, _cameraRegion, ref last));
        }

        return fits;
    }

    /// <summary>Writes the kept part of a scene around the screen's strip as a PNG file, for a look at one that is wrong.</summary>
    public void Save(int index, string path)
    {
        if (index >= 0 && index < Count)
        {
            PngWriter.WriteBgra(path, _scenes[index].Screen, _screenRegion.Width, _screenRegion.Height);
        }
    }

    public void Dispose()
    {
        _staging?.Dispose();
        _staging = null;
    }

    private static (int X0, int Y0, int X1, int Y1) Union((int X0, int Y0, int X1, int Y1)? box, (int X, int Y, int Width, int Height) bounds) =>
        box is { } b
            ? (Math.Min(b.X0, bounds.X), Math.Min(b.Y0, bounds.Y), Math.Max(b.X1, bounds.X + bounds.Width), Math.Max(b.Y1, bounds.Y + bounds.Height))
            : (bounds.X, bounds.Y, bounds.X + bounds.Width, bounds.Y + bounds.Height);

    private (int X, int Y, int Width, int Height) Padded((int X0, int Y0, int X1, int Y1) box)
    {
        const int Margin = 12;
        var x0 = Math.Clamp(box.X0 - Margin, 0, _layout.Width - 1);
        var y0 = Math.Clamp(box.Y0 - Margin, 0, _layout.Height - 1);
        var x1 = Math.Clamp(box.X1 + Margin, x0 + 1, _layout.Width);
        var y1 = Math.Clamp(box.Y1 + Margin, y0 + 1, _layout.Height);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    private static unsafe void Copy(byte* source, uint rowPitch, int top, int width, int height, byte[] destination)
    {
        fixed (byte* target = destination)
        {
            for (var row = 0; row < height; row++)
            {
                Buffer.MemoryCopy(source + ((long)(top + row) * rowPitch), target + ((long)row * width * 4), width * 4, width * 4);
            }
        }
    }
}
