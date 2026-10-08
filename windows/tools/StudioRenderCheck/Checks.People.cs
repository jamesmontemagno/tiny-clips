using System.Diagnostics;
using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>
/// The camera with its background blurred or removed (section 6.7 of the format). Where the
/// people are is told to the renderer by a stand-in that calls one block of the camera frame a
/// person, whatever the frame shows, so every pixel of the result can be worked out by hand.
/// A real model is tried as well when one is given on the command line.
/// </summary>
internal static class PeopleChecks
{
    /// <summary>The size of the picture the stand-in asks for.</summary>
    private const int Side = 256;

    /// <summary>The Gaussian the format asks for: 2 percent of the longer side of the 1280×720 camera frame.</summary>
    private const double BlurDeviation = 25.6;

    /// <summary>
    /// The part of the frame the stand-in calls a person, in its own 256×256 picture: from 112
    /// to 208 across and from 64 down to the bottom. In the 1280×720 frame that is 560 to 1040
    /// across and 180 to 720 down. It is off-centre both ways, so a flipped, mirrored or
    /// transposed mask lands somewhere else.
    /// </summary>
    private static readonly (int Left, int Top, int Right, int Bottom) Block = (112, 64, 208, 256);

    private static readonly Rgb BorderColor = new(255, 128, 0);

    /// <summary>A model to try, from <c>--person-model</c>.</summary>
    public static string? ModelPath { get; set; }

    /// <summary>A photograph with a person in it for that model, from <c>--person-photo</c>.</summary>
    public static string? PhotoPath { get; set; }

    public static async Task Run(Harness harness)
    {
        if (!harness.Wants("people"))
        {
            return;
        }

        foreach (var warp in new[] { false, true })
        {
            var prefix = warp ? "people on WARP: " : "people: ";
            RenderBench bench;
            try
            {
                bench = new RenderBench(warp);
            }
            catch (Exception ex)
            {
                await harness.Check("people", prefix + "a device and a renderer can be made", _ => throw new CheckFailedException($"{ex.GetType().Name}: {ex.Message}")).ConfigureAwait(false);
                continue;
            }

            using (bench)
            {
                Task Check(string name, Action<CheckContext> body) => harness.Check("people", prefix + name, body);

                await Check("the finder is given the whole camera frame, upright, blue first", context => FinderInput(context, bench)).ConfigureAwait(false);
                await Check("removed: only the people are drawn, without shadow or border", context => Removed(context, bench)).ConfigureAwait(false);
                await Check("removed: the camera's shape still clips", context => RemovedInAShape(context, bench)).ConfigureAwait(false);
                await Check("removed: the edge of a person is where the finder put it, and is a clean ramp", context => Edge(context, bench)).ConfigureAwait(false);
                await Check("blurred: the people are sharp, the rest is blurred, the frame keeps its shadow and border", context => Blurred(context, bench)).ConfigureAwait(false);
                await Check("blurred: the blur is the Gaussian the format describes", context => BlurWidth(context, bench)).ConfigureAwait(false);
                await Check("people that cannot be found: the background is kept", context => NotFound(context, bench)).ConfigureAwait(false);
                await Check("a frame that names itself is looked at once", context => Stamps(context, bench)).ConfigureAwait(false);
                await Check("a fading camera fades as one, people and all", context => Fading(context, bench)).ConfigureAwait(false);
                await Check("export quality and a padded camera texture", context => QualityAndPadding(context, bench)).ConfigureAwait(false);
                await Check("time to find the people and draw a frame", context => DrawTime(context, bench)).ConfigureAwait(false);
                if (!warp)
                {
                    await Check("a real model: it loads, and finds the person in a photograph", context => RealModel(context, bench)).ConfigureAwait(false);
                }
            }
        }

        await harness.Check("people", "people: exported, the right camera frame in every frame, and each looked at once", context => ExportedWhole(context, harness)).ConfigureAwait(false);
        await harness.Check("people", "people: exported and postered, only the people in every frame", context => ExportedBlock(context, harness)).ConfigureAwait(false);
    }

    // The stand-in

    /// <summary>Calls <see cref="Block"/> a person in every frame, and keeps what it was given.</summary>
    private sealed class BlockFinder : IStudioPersonFinder
    {
        public int Width { get; init; } = Side;

        public int Height { get; init; } = Side;

        public int Calls { get; private set; }

        public byte[]? LastInput { get; private set; }

        public bool Fails { get; set; }

        public bool Throws { get; set; }

        /// <summary>Calls the whole frame a person.</summary>
        public bool Everything { get; init; }

        public bool Disposed { get; private set; }

        public bool TryFind(ReadOnlySpan<byte> bgra, Span<byte> mask)
        {
            Calls++;
            LastInput = bgra.ToArray();
            if (Throws)
            {
                throw new InvalidOperationException("The stand-in finder was told to throw.");
            }

            if (Fails)
            {
                return false;
            }

            if (Everything)
            {
                mask.Fill(255);
                return true;
            }

            mask.Clear();
            for (var row = Block.Top; row < Block.Bottom; row++)
            {
                mask.Slice((row * Width) + Block.Left, Block.Right - Block.Left).Fill(255);
            }

            return true;
        }

        public void Dispose() => Disposed = true;
    }

    private static StudioSceneRenderer NewRenderer(RenderBench bench, Func<IStudioPersonFinder?> factory)
    {
        lock (bench.Graphics.Gate)
        {
            return new StudioSceneRenderer(bench.Graphics.Device, factory);
        }
    }

    private static void Release(RenderBench bench, StudioSceneRenderer renderer)
    {
        lock (bench.Graphics.Gate)
        {
            renderer.Dispose();
        }
    }

    /// <summary>Runs <paramref name="body"/> with a renderer whose finder is the stand-in.</summary>
    private static void WithBlockFinder(RenderBench bench, Action<StudioSceneRenderer, BlockFinder> body)
    {
        var finder = new BlockFinder();
        var renderer = NewRenderer(bench, () => finder);
        try
        {
            body(renderer, finder);
        }
        finally
        {
            Release(bench, renderer);
        }
    }

    private static StudioProject Project(
        RenderBench bench,
        StudioCameraCutout cutout,
        StudioCameraShape shape = StudioCameraShape.Rectangle,
        bool mirror = false,
        double shadow = 0,
        double border = 0,
        StudioLayout layout = StudioLayout.Bubble) =>
        bench.Base(layout) with
        {
            Camera = new StudioCameraStyle { Shape = shape, CornerRadius = 0.3, Mirror = mirror, Shadow = shadow, BorderWidth = border, BorderColor = "#FF8000", Cutout = cutout },
        };

    private static (int Left, int Top, int Right, int Bottom) Box(StudioFrameRect rect)
    {
        var aligned = Projects.Aligned(rect);
        return ((int)aligned.X, (int)aligned.Y, (int)(aligned.X + aligned.Width), (int)(aligned.Y + aligned.Height));
    }

    /// <summary>
    /// The whole pixels of the picture that lie inside a rectangle of the camera frame, made
    /// smaller by <paramref name="inset"/> camera pixels on each side that is not an edge of the
    /// frame (a negative inset makes it larger).
    /// </summary>
    private static (int Left, int Top, int Right, int Bottom) InPicture(ClipMap map, ClipSpec spec, double left, double top, double right, double bottom, double inset)
    {
        var l = left <= 0 ? left : left + inset;
        var t = top <= 0 ? top : top + inset;
        var r = right >= spec.Width ? right : right - inset;
        var b = bottom >= spec.Height ? bottom : bottom - inset;
        var (x0, y0) = map.Apply(l, t);
        var (x1, y1) = map.Apply(r, b);
        return inset >= 0
            ? ((int)Math.Ceiling(Math.Min(x0, x1)), (int)Math.Ceiling(Math.Min(y0, y1)), (int)Math.Floor(Math.Max(x0, x1)), (int)Math.Floor(Math.Max(y0, y1)))
            : ((int)Math.Floor(Math.Min(x0, x1)), (int)Math.Floor(Math.Min(y0, y1)), (int)Math.Ceiling(Math.Max(x0, x1)), (int)Math.Ceiling(Math.Max(y0, y1)));
    }

    /// <summary>The block in camera pixels.</summary>
    private static (double Left, double Top, double Right, double Bottom) BlockInCamera(ClipSpec spec) =>
        (Block.Left * spec.Width / (double)Side, Block.Top * spec.Height / (double)Side, Block.Right * spec.Width / (double)Side, Block.Bottom * spec.Height / (double)Side);

    /// <summary>Two texels of the stand-in's picture, in camera pixels: further than that from the block's edge, the mask is 0 or 255.</summary>
    private static double RampReach(ClipSpec spec) => 2.0 * Math.Max(spec.Width, spec.Height) / Side;

    private static void ExpectOutsideIs(CheckContext context, Picture picture, Picture reference, (int Left, int Top, int Right, int Bottom) hole, string what)
    {
        RendererChecks.ExpectSame(context, picture, reference, $"{what}, above the people", 0, 0, picture.Width, hole.Top);
        RendererChecks.ExpectSame(context, picture, reference, $"{what}, below the people", 0, hole.Bottom, picture.Width, picture.Height);
        RendererChecks.ExpectSame(context, picture, reference, $"{what}, left of the people", 0, hole.Top, hole.Left, hole.Bottom);
        RendererChecks.ExpectSame(context, picture, reference, $"{what}, right of the people", hole.Right, hole.Top, picture.Width, hole.Bottom);
    }

    /// <summary>
    /// How much of <paramref name="over"/> a pixel shows on top of <paramref name="under"/>, from
    /// 0 to 1, read from the channel in which the two differ most. Null where they are too alike
    /// to tell.
    /// </summary>
    private static double? Share(Rgb got, Rgb under, Rgb over)
    {
        var red = over.R - under.R;
        var green = over.G - under.G;
        var blue = over.B - under.B;
        var (difference, value) = Math.Abs(red) >= Math.Abs(green) && Math.Abs(red) >= Math.Abs(blue)
            ? (red, got.R - under.R)
            : Math.Abs(green) >= Math.Abs(blue) ? (green, got.G - under.G) : (blue, got.B - under.B);
        return Math.Abs(difference) < 30 ? null : value / difference;
    }

    // What the finder is given

    private static void FinderInput(CheckContext context, RenderBench bench)
    {
        var spec = bench.CameraClip.Spec;
        byte[]? rectangle = null;
        WithBlockFinder(bench, (renderer, finder) =>
        {
            bench.Render(Project(bench, StudioCameraCutout.Remove), renderer: renderer);
            context.Expect(finder.Calls == 1, $"one frame was drawn and the finder was asked {finder.Calls} times");
            var given = finder.LastInput ?? [];
            if (!context.Expect(given.Length == Side * Side * 4, $"the finder was given {given.Length} bytes, want {Side * Side * 4}"))
            {
                return;
            }

            rectangle = given;

            // Every texel whose place in the frame is a flat colour for 16 camera pixels around must be that colour.
            var compared = 0;
            double worst = 0;
            var worstAt = string.Empty;
            for (var row = 0; row < Side; row++)
            {
                for (var column = 0; column < Side; column++)
                {
                    var x = (column + 0.5) * spec.Width / Side;
                    var y = (row + 0.5) * spec.Height / Side;
                    if (spec.FlatColorAt(x, y, RenderBench.CameraNumber, 16) is not { } want)
                    {
                        continue;
                    }

                    compared++;
                    var index = ((row * Side) + column) * 4;
                    var got = new Rgb(given[index + 2], given[index + 1], given[index]);
                    if (got.Distance(want) > worst)
                    {
                        worst = got.Distance(want);
                        worstAt = $"texel ({column},{row}), the frame's ({x:0},{y:0}): {got}, want {want}";
                    }
                }
            }

            context.Expect(compared > 30000, $"only {compared} texels could be compared with the pattern");
            context.Expect(worst <= 2, $"the picture given to the finder is not the camera frame scaled down: {worstAt}");

            // The red block is top right in the frame, and its colour has the most red: both tell a flip or a swap of channels.
            var marker = ((int)((spec.MarkerY + (spec.MarkerHeight / 2.0)) * Side / spec.Height) * Side) + (int)((spec.MarkerX + (spec.MarkerWidth / 2.0)) * Side / spec.Width);
            var markerGot = new Rgb(given[(marker * 4) + 2], given[(marker * 4) + 1], given[marker * 4]);
            context.Expect(markerGot.Distance(ClipSpec.MarkerColor) <= 2, $"where the frame's red block is, the finder was given {markerGot}, want {ClipSpec.MarkerColor}");
            context.Note($"{compared} of {Side * Side} texels compared with the frame's own colours; the largest difference is {worst:0}");
        });

        // A circle shows a square out of the middle of the frame. The finder still gets all of it.
        WithBlockFinder(bench, (renderer, finder) =>
        {
            bench.Render(Project(bench, StudioCameraCutout.Blur, StudioCameraShape.Circle, mirror: true), renderer: renderer);
            context.Expect(rectangle is not null && finder.LastInput is not null && finder.LastInput.AsSpan().SequenceEqual(rectangle), "for a mirrored circle the finder was given another picture than for a rectangle");
        });

        // Fine stripes, one white column in three on the left half of the frame and one white
        // row in three on the right: a frame that is filtered on its way down gives the finder
        // flat grey at their mean, 85, and one that is only sampled gives it a ripple.
        var stripes = new byte[spec.Width * spec.Height * 4];
        for (var y = 0; y < spec.Height; y++)
        {
            for (var x = 0; x < spec.Width; x++)
            {
                var white = x < spec.Width / 2 ? x % 3 == 0 : y % 3 == 0;
                var index = ((y * spec.Width) + x) * 4;
                stripes[index] = stripes[index + 1] = stripes[index + 2] = white ? (byte)255 : (byte)0;
                stripes[index + 3] = 255;
            }
        }

        var striped = new StudioGpuVideoFrame(bench.Texture(stripes, spec.Width, spec.Height), 0, spec.Width, spec.Height);
        WithBlockFinder(bench, (renderer, finder) =>
        {
            bench.Render(Project(bench, StudioCameraCutout.Blur), renderer: renderer, camera: striped);
            var given = finder.LastInput ?? [];
            if (!context.Expect(given.Length == Side * Side * 4, $"for the striped frame the finder was given {given.Length} bytes"))
            {
                return;
            }

            foreach (var (name, from, to) in new[] { ("columns", 0.05, 0.45), ("rows", 0.55, 0.95) })
            {
                double sum = 0;
                double squares = 0;
                var count = 0;
                for (var row = (int)(Side * 0.1); row < (int)(Side * 0.9); row++)
                {
                    for (var column = (int)(Side * from); column < (int)(Side * to); column++)
                    {
                        double value = given[(((row * Side) + column) * 4) + 1];
                        sum += value;
                        squares += value * value;
                        count++;
                    }
                }

                var mean = sum / count;
                var deviation = Math.Sqrt(Math.Max(0, (squares / count) - (mean * mean)));
                context.Expect(Math.Abs(mean - 85) <= 4, $"striped {name}: the finder's picture averages {mean:0.0}, want 85");
                context.Expect(deviation <= 12, $"striped {name}: the finder's picture has a ripple of {deviation:0.0} of 255 (standard deviation)");
                context.Note($"striped {name}, a third of them white: the finder's picture is {mean:0.0} with a ripple of {deviation:0.0}");
            }
        });
    }

    // Removed

    private static void Removed(CheckContext context, RenderBench bench)
    {
        var spec = bench.CameraClip.Spec;
        var block = BlockInCamera(spec);
        var reach = RampReach(spec);
        foreach (var mirror in new[] { false, true })
        {
            var project = Project(bench, StudioCameraCutout.Remove, mirror: mirror, shadow: 0.8, border: 0.01);
            var camera = StudioLayoutResolver.Resolve(project, 0, 1920, 1080).Camera!.Value;
            var map = Projects.CameraMap(camera, spec);
            var layer = Box(camera.Rect);
            var bare = bench.Render(Project(bench, StudioCameraCutout.None, mirror: mirror));
            var framed = bench.Render(project with { Camera = project.Camera with { Cutout = StudioCameraCutout.None } });
            var without = bench.Render(project, withCamera: false);

            // The frame this camera has when its background is kept: a shadow under it and a border in it.
            var middle = (layer.Left + layer.Right) / 2;
            context.Expect(framed.Pixel(middle, layer.Bottom + 6).Distance(without.Pixel(middle, layer.Bottom + 6)) > 10, "the kept camera casts no shadow to compare with");
            context.Expect(framed.Pixel(middle, layer.Bottom - 3).Distance(BorderColor) <= 1, "the kept camera has no border to compare with");

            WithBlockFinder(bench, (renderer, _) =>
            {
                var picture = bench.Render(project, renderer: renderer);
                if (context.Harness.KeepsFiles)
                {
                    picture.SavePng(Path.Combine(context.Folder(), $"removed{(mirror ? "-mirrored" : string.Empty)}.png"));
                }

                // In the block, the camera as it is, down to the bottom edge where the border would be.
                var inside = InPicture(map, spec, block.Left, block.Top, block.Right, block.Bottom, reach);
                context.Expect(inside.Right - inside.Left > 200 && inside.Bottom - inside.Top > 250, $"mirror {mirror}: the block is only {inside.Right - inside.Left}×{inside.Bottom - inside.Top} px in the picture");
                context.Expect(inside.Bottom == layer.Bottom, $"mirror {mirror}: the block ends at row {inside.Bottom}, and the layer at {layer.Bottom}");
                RendererChecks.ExpectSame(context, picture, bare, $"mirror {mirror}: the people", inside.Left, inside.Top, inside.Right, inside.Bottom, tolerance: 1);

                // And that is the camera's own picture: the third colour patch and a white cell of the number strip are in the block.
                var (patchX, patchY) = map.Apply(spec.PatchCenter(2).X, spec.PatchCenter(2).Y);
                RendererChecks.ExpectPixel(context, picture, patchX, patchY, ClipSpec.PatchColors[2], 1, $"mirror {mirror}: the camera's blue patch, in the people");
                var (cellX, cellY) = map.Apply(spec.CodeX + (8.5 * spec.CodeCell), spec.CodeY + (0.5 * spec.CodeCell));
                RendererChecks.ExpectPixel(context, picture, cellX, cellY, new Rgb(255, 255, 255), 1, $"mirror {mirror}: a white cell of the camera's strip, in the people");

                // Everywhere else, to the pixel, what is there without the camera: no picture, no shadow, no border.
                var hole = InPicture(map, spec, block.Left, block.Top, block.Right, block.Bottom, -reach);
                ExpectOutsideIs(context, picture, without, hole, $"mirror {mirror}: the frame without its camera");
                context.Note($"mirror {mirror}: people at ({inside.Left},{inside.Top})–({inside.Right},{inside.Bottom}) of the layer at ({layer.Left},{layer.Top})–({layer.Right},{layer.Bottom})");
            });
        }
    }

    private static void RemovedInAShape(CheckContext context, RenderBench bench)
    {
        var spec = bench.CameraClip.Spec;
        var block = BlockInCamera(spec);
        var reach = RampReach(spec);
        foreach (var shape in new[] { StudioCameraShape.Circle, StudioCameraShape.Squircle, StudioCameraShape.RoundedRectangle })
        {
            var project = Project(bench, StudioCameraCutout.Remove, shape, shadow: 0.8, border: 0.01);
            var camera = StudioLayoutResolver.Resolve(project, 0, 1920, 1080).Camera!.Value;
            var map = Projects.CameraMap(camera, spec);
            var layer = Box(camera.Rect);
            var without = bench.Render(project, withCamera: false);
            var bare = bench.Render(Project(bench, StudioCameraCutout.None, shape));
            WithBlockFinder(bench, (renderer, _) =>
            {
                var picture = bench.Render(project, renderer: renderer);
                var inside = InPicture(map, spec, block.Left, block.Top, block.Right, block.Bottom, reach);
                inside = (Math.Max(inside.Left, layer.Left), Math.Max(inside.Top, layer.Top), Math.Min(inside.Right, layer.Right), Math.Min(inside.Bottom, layer.Bottom));
                var inPeople = 0;
                var outPeople = 0;
                foreach (var (inPoint, outPoint) in RendererChecks.OutlinePairs(shape, layer, camera.CornerRadius))
                {
                    // Just outside the shape nothing is drawn, people or not.
                    RendererChecks.ExpectPixel(context, picture, outPoint.X, outPoint.Y, without.Pixel((int)Math.Floor(outPoint.X), (int)Math.Floor(outPoint.Y)), 0, $"{shape}: outside the outline at ({outPoint.X:0},{outPoint.Y:0})");
                    var x = (int)Math.Floor(inPoint.X);
                    var y = (int)Math.Floor(inPoint.Y);
                    if (x >= inside.Left && x < inside.Right && y >= inside.Top && y < inside.Bottom)
                    {
                        // Just inside it, where the people are, the camera shows right up to the outline: no border.
                        inPeople++;
                        RendererChecks.ExpectPixel(context, picture, inPoint.X, inPoint.Y, bare.Pixel(x, y), 1, $"{shape}: inside the outline, in the people, at ({x},{y})");
                    }
                    else if (x < inside.Left - (2 * reach) || y < inside.Top - (2 * reach))
                    {
                        outPeople++;
                        RendererChecks.ExpectPixel(context, picture, inPoint.X, inPoint.Y, without.Pixel(x, y), 0, $"{shape}: inside the outline, away from the people, at ({x},{y})");
                    }
                }

                // A rounded rectangle is looked at in eight places only, and one of them, the middle of its bottom edge, is in the people.
                context.Expect(inPeople >= (shape == StudioCameraShape.RoundedRectangle ? 1 : 2) && outPeople >= 2, $"{shape}: only {inPeople} outline points in the people and {outPeople} away from them");
                context.Note($"{shape}: {inPeople} points just inside the outline show the people, {outPeople} show what is under the layer");
            });
        }
    }

    private static void Edge(CheckContext context, RenderBench bench)
    {
        var spec = bench.CameraClip.Spec;
        var block = BlockInCamera(spec);
        var project = Project(bench, StudioCameraCutout.Remove, layout: StudioLayout.Camera);
        var camera = StudioLayoutResolver.Resolve(project, 0, 1920, 1080).Camera!.Value;
        var map = Projects.CameraMap(camera, spec);
        var bare = bench.Render(Project(bench, StudioCameraCutout.None, layout: StudioLayout.Camera));
        var without = bench.Render(project, withCamera: false);
        WithBlockFinder(bench, (renderer, _) =>
        {
            var picture = bench.Render(project, renderer: renderer);

            // Across the block's left edge, on a row where the camera is one flat colour, and
            // down across its top edge, on a column where it is.
            var (edgeX, rowY) = map.Apply(block.Left, 600);
            var (columnX, edgeY) = map.Apply(800, block.Top);

            // A texel of the mask is wider than it is high in this frame: 5 by 2.8 camera pixels.
            foreach (var (name, want, along, texel) in new[] { ("left", edgeX, true, spec.Width / (double)Side * map.ScaleX), ("top", edgeY, false, spec.Height / (double)Side * map.ScaleY) })
            {
                double? half = null;
                double? tenth = null;
                double? ninth = null;
                double low = 0;
                double high = 0;
                double? previous = null;
                var backwards = 0.0;
                var first = (int)Math.Floor(want - (4 * texel));
                var last = (int)Math.Ceiling(want + (4 * texel));
                for (var position = first; position <= last; position++)
                {
                    var x = along ? position : (int)Math.Floor(columnX);
                    var y = along ? (int)Math.Floor(rowY) : position;
                    if (Share(picture.Pixel(x, y), without.Pixel(x, y), bare.Pixel(x, y)) is not { } share)
                    {
                        context.Fail($"the {name} edge: at ({x},{y}) the camera and what is under it are too alike to measure");
                        return;
                    }

                    low = Math.Min(low, share);
                    high = Math.Max(high, share);
                    if (previous is { } before)
                    {
                        backwards = Math.Max(backwards, before - share);
                        // The pixel centres are half a pixel in.
                        if (half is null && before < 0.5 && share >= 0.5)
                        {
                            half = position - 0.5 + ((0.5 - before) / (share - before));
                        }

                        if (tenth is null && before < 0.1 && share >= 0.1)
                        {
                            tenth = position - 0.5 + ((0.1 - before) / (share - before));
                        }

                        if (ninth is null && before < 0.9 && share >= 0.9)
                        {
                            ninth = position - 0.5 + ((0.9 - before) / (share - before));
                        }
                    }

                    previous = share;
                }

                context.Expect(half is not null, $"the {name} edge: the picture never goes from under the camera to the camera");
                if (half is { } found)
                {
                    context.Expect(Math.Abs(found - want) <= 1, $"the {name} edge of the people is at {found:0.00}, and the finder put it at {want:0.00}");
                    context.Note($"the {name} edge: half-way at {found:0.00}, want {want:0.00}; a texel of the mask is {texel:0.0} px here");
                }

                // The mask is a 256 by 256 picture laid over a frame several times its size. Its
                // edge has to come out as a ramp about a texel wide, not as the step of a texel
                // copied whole, which would show as stairs along every slanted edge.
                if (tenth is { } from && ninth is { } to)
                {
                    var width = (to - from) / texel;
                    context.Expect(width is >= 0.5 and <= 1.6, $"the {name} edge goes from a tenth to nine tenths of the camera over {width:0.00} texels of the mask, want about one");
                    context.Note($"the {name} edge: from a tenth to nine tenths over {to - from:0.0} px, {width:0.00} texels");
                }
                else
                {
                    context.Fail($"the {name} edge: the camera's share never passes a tenth and nine tenths");
                }

                // No halo: the share never leaves 0 to 1, and never turns back, by more than rounding allows.
                context.Expect(low >= -0.03 && high <= 1.03, $"the {name} edge: the camera's share goes from {low:0.000} to {high:0.000}");
                context.Expect(backwards <= 0.03, $"the {name} edge: the camera's share falls back by {backwards:0.000} on the way in");
            }
        });
    }

    // Blurred

    private static void Blurred(CheckContext context, RenderBench bench)
    {
        var spec = bench.CameraClip.Spec;
        var block = BlockInCamera(spec);
        var reach = RampReach(spec);
        foreach (var mirror in new[] { false, true })
        {
            var project = Project(bench, StudioCameraCutout.Blur, mirror: mirror, shadow: 0.8, border: 0.01);
            var camera = StudioLayoutResolver.Resolve(project, 0, 1920, 1080).Camera!.Value;
            var map = Projects.CameraMap(camera, spec);
            var layer = Box(camera.Rect);
            var framed = bench.Render(project with { Camera = project.Camera with { Cutout = StudioCameraCutout.None } });
            WithBlockFinder(bench, (renderer, _) =>
            {
                var picture = bench.Render(project, renderer: renderer);
                if (context.Harness.KeepsFiles)
                {
                    picture.SavePng(Path.Combine(context.Folder(), $"blurred{(mirror ? "-mirrored" : string.Empty)}.png"));
                }

                // Outside the layer nothing changes: the shadow is the kept camera's.
                RendererChecks.ExpectSame(context, picture, framed, $"mirror {mirror}: above the layer", 0, 0, 1920, layer.Top);
                RendererChecks.ExpectSame(context, picture, framed, $"mirror {mirror}: below the layer", 0, layer.Bottom, 1920, 1080);
                RendererChecks.ExpectSame(context, picture, framed, $"mirror {mirror}: left of the layer", 0, layer.Top, layer.Left, layer.Bottom);
                RendererChecks.ExpectSame(context, picture, framed, $"mirror {mirror}: right of the layer", layer.Right, layer.Top, 1920, layer.Bottom);

                // The border is the kept camera's too: 10.8 px of orange inside each edge.
                var middleY = (layer.Top + layer.Bottom) / 2;
                var middleX = (layer.Left + layer.Right) / 2;
                foreach (var (x, y) in new[] { (layer.Left + 4, middleY), (layer.Right - 5, middleY), (middleX, layer.Top + 4), (middleX, layer.Bottom - 5) })
                {
                    RendererChecks.ExpectPixel(context, picture, x, y, BorderColor, 1, $"mirror {mirror}: the border");
                }

                // The people: the camera as it is, clear of the border at the bottom.
                var inside = InPicture(map, spec, block.Left, block.Top, block.Right, block.Bottom - 30, reach);
                RendererChecks.ExpectSame(context, picture, framed, $"mirror {mirror}: the people", inside.Left, inside.Top, inside.Right, inside.Bottom, tolerance: 1);

                // Away from the people: a flat part stays its colour, and a sharp part is no longer sharp.
                var (flatX, flatY) = map.Apply(150, 560);
                RendererChecks.ExpectPixel(context, picture, flatX, flatY, spec.LeftBackground, 1, $"mirror {mirror}: a flat part of the blurred background");

                // The step between the darker left third and the rest, at 426: three pixels to each side of it.
                var (stepX, stepY) = map.Apply(spec.LeftWidth, 600);
                var direction = Math.Sign(map.ScaleX);
                var sharp = framed.Pixel((int)Math.Floor(stepX + (3 * direction)), (int)Math.Floor(stepY)).G - framed.Pixel((int)Math.Floor(stepX - (3 * direction)), (int)Math.Floor(stepY)).G;
                var soft = picture.Pixel((int)Math.Floor(stepX + (3 * direction)), (int)Math.Floor(stepY)).G - picture.Pixel((int)Math.Floor(stepX - (3 * direction)), (int)Math.Floor(stepY)).G;
                context.Expect(sharp >= 34, $"mirror {mirror}: the kept camera's step is only {sharp:0} of 36 levels over six pixels");
                context.Expect(soft is > 2 and < 14, $"mirror {mirror}: across the blurred step, green changes by {soft:0} levels over six pixels (kept: {sharp:0})");

                // The frame's number strip is outside the people on the left and inside them on the right.
                var (blurredCellX, cellY) = map.Apply(spec.CodeX + (1.5 * spec.CodeCell), spec.CodeY + (0.5 * spec.CodeCell));
                var kept = framed.Pixel((int)Math.Floor(blurredCellX), (int)Math.Floor(cellY));
                var blurred = picture.Pixel((int)Math.Floor(blurredCellX), (int)Math.Floor(cellY));
                context.Expect(kept.Distance(blurred) > 40, $"mirror {mirror}: a cell of the strip outside the people is {blurred} blurred and {kept} kept");
            });
        }
    }

    private static void BlurWidth(CheckContext context, RenderBench bench)
    {
        var spec = bench.CameraClip.Spec;
        var project = Project(bench, StudioCameraCutout.Blur, layout: StudioLayout.Camera);
        var camera = StudioLayoutResolver.Resolve(project, 0, 1920, 1080).Camera!.Value;
        var map = Projects.CameraMap(camera, spec);
        WithBlockFinder(bench, (renderer, _) =>
        {
            var picture = bench.Render(project, renderer: renderer);

            // A step blurred with a Gaussian of deviation s differs from the step by an area of
            // s × sqrt(2/pi) on a row across it. The step is the frame's own, at 426, between two
            // flat colours; the row is 600, with nothing else within 4.5 deviations of it.
            var dark = spec.LeftBackground.R + spec.LeftBackground.G + spec.LeftBackground.B;
            var bright = spec.Background.R + spec.Background.G + spec.Background.B;
            var (_, rowY) = map.Apply(0, 600);
            var y = (int)Math.Floor(rowY);
            var (startX, _) = map.Apply(spec.LeftWidth - (4.2 * BlurDeviation), 600);
            var (endX, _) = map.Apply(spec.LeftWidth + (4.2 * BlurDeviation), 600);
            double area = 0;
            double? half = null;
            double? previous = null;
            for (var x = (int)Math.Ceiling(startX); x <= (int)Math.Floor(endX); x++)
            {
                var pixel = picture.Pixel(x, y);
                var share = (pixel.R + pixel.G + pixel.B - dark) / (bright - dark);
                var frameX = (x + 0.5 - map.OriginX) / map.ScaleX;
                area += Math.Abs(share - (frameX >= spec.LeftWidth ? 1 : 0)) / map.ScaleX;
                if (previous is { } before && half is null && before < 0.5 && share >= 0.5)
                {
                    half = ((x - 0.5 + ((0.5 - before) / (share - before))) - map.OriginX) / map.ScaleX;
                }

                previous = share;
            }

            var deviation = area / Math.Sqrt(2 / Math.PI);
            context.Expect(Math.Abs(deviation - BlurDeviation) <= 1.0, $"the blur's standard deviation measures {deviation:0.00} camera px, want {BlurDeviation:0.0} (2 % of 1280)");

            // At the frame's own edges the blur has nothing beyond them to take in: a flat part there keeps its colour, and the layer stays whole.
            var (leftX, _) = map.Apply(3, 600);
            var (rightX, _) = map.Apply(spec.Width - 3, 600);
            RendererChecks.ExpectPixel(context, picture, leftX, rowY, spec.LeftBackground, 1, "the blurred frame at its left edge");
            RendererChecks.ExpectPixel(context, picture, rightX, rowY, spec.Background, 1, "the blurred frame at its right edge");

            // Direct2D blurs this wide by way of a smaller picture, which can move the result a little: half a pixel on this PC's graphics card and two on WARP.
            context.Expect(half is { } middle && Math.Abs(middle - spec.LeftWidth) <= 3, $"the blurred step is half-way at {half?.ToString("0.0", CultureInfo.InvariantCulture) ?? "nowhere"}, and the step is at {spec.LeftWidth}");
            context.Measure($"blur deviation {deviation:0.00} camera px (want {BlurDeviation:0.0}), step half-way at {half:0.0} (want {spec.LeftWidth})");
        });
    }

    // People that cannot be found

    private static void NotFound(CheckContext context, RenderBench bench)
    {
        foreach (var cutout in new[] { StudioCameraCutout.Remove, StudioCameraCutout.Blur })
        {
            var project = Project(bench, cutout, StudioCameraShape.Circle, mirror: true, shadow: 0.8, border: 0.01);
            var kept = bench.Render(project with { Camera = project.Camera with { Cutout = StudioCameraCutout.None } });

            void Same(string what, Func<IStudioPersonFinder?> factory)
            {
                var renderer = NewRenderer(bench, factory);
                try
                {
                    RendererChecks.ExpectSame(context, bench.Render(project, renderer: renderer), kept, $"{cutout}, {what}");
                    RendererChecks.ExpectSame(context, bench.Render(project, renderer: renderer), kept, $"{cutout}, {what}, drawn again");
                }
                finally
                {
                    Release(bench, renderer);
                }
            }

            var failing = new BlockFinder { Fails = true };
            Same("a finder that finds nothing to look at", () => failing);
            context.Expect(failing.Calls == 2 && failing.Disposed, $"{cutout}: the failing finder was asked {failing.Calls} times, disposed {failing.Disposed}");

            var throwing = new BlockFinder { Throws = true };
            Same("a finder that throws", () => throwing);
            context.Expect(throwing.Calls == 2 && throwing.Disposed, $"{cutout}: the throwing finder was asked {throwing.Calls} times, disposed {throwing.Disposed}");

            var made = 0;
            Same("no finder", () =>
            {
                made++;
                return null;
            });
            context.Expect(made == 1, $"{cutout}: a factory that gives no finder was asked {made} times for two frames");

            made = 0;
            Same("a factory that throws", () =>
            {
                made++;
                throw new InvalidOperationException("The factory was told to throw.");
            });
            context.Expect(made == 1, $"{cutout}: a factory that throws was asked {made} times for two frames");

            foreach (var side in new[] { 0, 5000 })
            {
                var absurd = new BlockFinder { Width = side };
                Same($"a finder that wants a picture {side} px wide", () => absurd);
                context.Expect(absurd.Calls == 0 && absurd.Disposed, $"{cutout}: a finder that wants {side} px was asked {absurd.Calls} times, disposed {absurd.Disposed}");
            }
        }

        // A camera whose background is kept never asks for a finder at all.
        var asked = 0;
        var idle = NewRenderer(bench, () =>
        {
            asked++;
            return new BlockFinder();
        });
        try
        {
            bench.Render(Project(bench, StudioCameraCutout.None), renderer: idle);
            bench.Render(Project(bench, StudioCameraCutout.Remove), renderer: idle, withCamera: false);
            bench.Render(Project(bench, StudioCameraCutout.Remove, layout: StudioLayout.Screen), renderer: idle);
            context.Expect(asked == 0, $"three frames that show no camera to cut out asked for a finder {asked} times");
        }
        finally
        {
            Release(bench, idle);
        }
    }

    // Stamps

    private static void Stamps(CheckContext context, RenderBench bench)
    {
        var remove = Project(bench, StudioCameraCutout.Remove);
        var blur = Project(bench, StudioCameraCutout.Blur);
        WithBlockFinder(bench, (renderer, finder) =>
        {
            var named = bench.CameraFrame with { Stamp = 5 };
            var first = bench.Render(remove, renderer: renderer, camera: named);
            var second = bench.Render(remove, renderer: renderer, camera: named);
            context.Expect(finder.Calls == 1, $"a named frame drawn twice was looked at {finder.Calls} times");
            RendererChecks.ExpectSame(context, second, first, "the second draw of a named frame");

            // Another way of drawing the same frame, another frame, and a frame with no name.
            var blurred = bench.Render(blur, renderer: renderer, camera: named);
            context.Expect(finder.Calls == 2, $"the same frame blurred after being removed: looked at {finder.Calls} times in all, want 2");
            context.Expect(Picture.MaxDifference(blurred, first).Difference > 20, "blurred and removed look the same");
            bench.Render(blur, renderer: renderer, camera: bench.CameraFrame with { Stamp = 6 });
            context.Expect(finder.Calls == 3, $"a frame with another name: looked at {finder.Calls} times in all, want 3");
            bench.Render(blur, renderer: renderer);
            bench.Render(blur, renderer: renderer);
            context.Expect(finder.Calls == 5, $"a frame without a name drawn twice: looked at {finder.Calls} times in all, want 5");

            // A texture that is let go of can come back with another picture under the same name.
            bench.Render(remove, renderer: renderer, camera: named);
            var before = finder.Calls;
            bench.Render(remove, renderer: renderer, camera: named);
            lock (bench.Graphics.Gate)
            {
                renderer.ForgetSources();
            }

            bench.Render(remove, renderer: renderer, camera: named);
            context.Expect(finder.Calls == before + 1, $"after the sources were forgotten the named frame was looked at {finder.Calls - before} more times, want 1");
            context.Expect(renderer.PeopleSearchCount == finder.Calls, $"the renderer counts {renderer.PeopleSearchCount} searches, the finder {finder.Calls}");
        });
    }

    // Fading

    private static void Fading(CheckContext context, RenderBench bench)
    {
        var spec = bench.CameraClip.Spec;
        var block = BlockInCamera(spec);
        var reach = RampReach(spec);

        // The screen alone, then the bubble moves in over one second from 2 s: half-way, the camera is half there.
        var project = Project(bench, StudioCameraCutout.Remove, shadow: 0.8, border: 0.01) with
        {
            Scenes =
            [
                new StudioScene { Layout = StudioLayout.Screen },
                new StudioScene { Start = 2, Layout = StudioLayout.Bubble, Bubble = new StudioBubble { Size = 0.36 }, Transition = new StudioTransition { Kind = StudioTransitionKind.Morph, Duration = 1 } },
            ],
        };
        const double time = 2.5;
        var resolved = StudioLayoutResolver.Resolve(project, time, 1920, 1080);
        if (resolved.Camera is not { } camera || !context.Expect(camera.Opacity is > 0.2 and < 0.8, $"half-way in, the layout has the camera at opacity {resolved.Camera?.Opacity}"))
        {
            return;
        }

        var map = Projects.CameraMap(camera, spec);
        var without = bench.Render(project, time: time, withCamera: false);

        // The same frame with the camera whole: its layout at that moment, drawn with nothing fading.
        var whole = bench.Render(project with { Camera = project.Camera with { Cutout = StudioCameraCutout.None, Shadow = 0, BorderWidth = 0 } }, time: 3.5);
        var settled = StudioLayoutResolver.Resolve(project, 3.5, 1920, 1080).Camera!.Value;
        WithBlockFinder(bench, (renderer, _) =>
        {
            var picture = bench.Render(project, time: time, renderer: renderer);
            var inside = InPicture(map, spec, block.Left, block.Top, block.Right, block.Bottom, reach);
            var hole = InPicture(map, spec, block.Left, block.Top, block.Right, block.Bottom, -reach);
            ExpectOutsideIs(context, picture, without, hole, "the fading frame without its camera");

            // In the people, each pixel is the camera's colour over what is under it, at the layer's opacity.
            var settledMap = Projects.CameraMap(settled, spec);
            var compared = 0;
            double worst = 0;
            for (var y = inside.Top; y < inside.Bottom; y += 7)
            {
                for (var x = inside.Left; x < inside.Right; x += 7)
                {
                    var frameX = (x + 0.5 - map.OriginX) / map.ScaleX;
                    var frameY = (y + 0.5 - map.OriginY) / map.ScaleY;
                    if (spec.FlatColorAt(frameX, frameY, RenderBench.CameraNumber, 6) is not { } colour)
                    {
                        continue;
                    }

                    compared++;
                    var want = without.Pixel(x, y).Mix(colour, camera.Opacity);
                    worst = Math.Max(worst, picture.Pixel(x, y).Distance(want));
                }
            }

            context.Expect(compared > 200, $"only {compared} points in the people could be compared");
            context.Expect(worst <= 2, $"a point in the fading people is {worst:0.0} levels from the camera at opacity {camera.Opacity:0.000} over what is under it");
            context.Expect(settledMap.ScaleX > 0 && whole.Width == picture.Width, "the settled frame could not be drawn");
            context.Note($"opacity {camera.Opacity:0.000}; {compared} points compared, the largest difference is {worst:0.0}");
        });
    }

    // Quality and padding

    private static void QualityAndPadding(CheckContext context, RenderBench bench)
    {
        var spec = bench.CameraClip.Spec;
        var block = BlockInCamera(spec);
        var reach = RampReach(spec);
        var project = Project(bench, StudioCameraCutout.Remove);
        var camera = StudioLayoutResolver.Resolve(project, 0, 1920, 1080).Camera!.Value;
        var map = Projects.CameraMap(camera, spec);
        var inside = InPicture(map, spec, block.Left, block.Top, block.Right, block.Bottom, reach);
        var hole = InPicture(map, spec, block.Left, block.Top, block.Right, block.Bottom, -reach);

        // Export quality filters the camera on its way down. The people are filtered the same way.
        var bare = bench.Render(Project(bench, StudioCameraCutout.None), quality: StudioRenderQuality.Export);
        var without = bench.Render(project, withCamera: false, quality: StudioRenderQuality.Export);
        byte[]? plainInput = null;
        Picture? plain = null;
        WithBlockFinder(bench, (renderer, finder) =>
        {
            var picture = bench.Render(project, renderer: renderer, quality: StudioRenderQuality.Export);
            RendererChecks.ExpectSame(context, picture, bare, "export quality: the people", inside.Left + 2, inside.Top + 2, inside.Right - 2, inside.Bottom, tolerance: 1);
            ExpectOutsideIs(context, picture, without, (hole.Left - 2, hole.Top - 2, hole.Right + 2, hole.Bottom), "export quality: the frame without its camera");
            plain = bench.Render(project, renderer: renderer);
            plainInput = finder.LastInput;
        });

        // The camera's picture in a texture larger than it is, magenta around it: none of that reaches the finder or the frame.
        var padded = new StudioGpuVideoFrame(bench.Texture(spec.DrawBgra(RenderBench.CameraNumber), 1280, 720, 1296, 736), 0, 1280, 720);
        WithBlockFinder(bench, (renderer, finder) =>
        {
            foreach (var cutout in new[] { StudioCameraCutout.Remove, StudioCameraCutout.Blur })
            {
                var cut = Project(bench, cutout);
                var fromPadded = bench.Render(cut, renderer: renderer, camera: padded);
                context.Expect(plainInput is not null && finder.LastInput is not null && finder.LastInput.AsSpan().SequenceEqual(plainInput), $"{cutout}: from a padded texture the finder was given another picture");
                Picture? reference = null;
                WithBlockFinder(bench, (other, _) => reference = bench.Render(cut, renderer: other));
                RendererChecks.ExpectSame(context, fromPadded, reference!, $"{cutout}: the frame from a padded camera texture");
            }
        });

        context.Expect(plain is not null, "the frame at preview quality was not drawn");
    }

    // Time

    private static void DrawTime(CheckContext context, RenderBench bench)
    {
        const int Frames = 60;
        var times = new Dictionary<StudioCameraCutout, double>();
        WithBlockFinder(bench, (renderer, finder) =>
        {
            foreach (var cutout in new[] { StudioCameraCutout.None, StudioCameraCutout.Blur, StudioCameraCutout.Remove })
            {
                var project = Project(bench, cutout, StudioCameraShape.Circle, shadow: 0.5);
                bench.Render(project, renderer: renderer);
                var watch = Stopwatch.StartNew();
                for (var frame = 0; frame < Frames; frame++)
                {
                    bench.Render(project, renderer: renderer);
                }

                times[cutout] = watch.Elapsed.TotalMilliseconds / Frames;
            }

            context.Expect(finder.Calls == 2 * (Frames + 1), $"the finder was asked {finder.Calls} times for {2 * (Frames + 1)} frames");
        });

        // Each of these reads the 1080p frame back as well, as every picture of this tool is.
        context.Measure($"a 1080p frame with a 1280×720 camera, drawn and read back: kept {times[StudioCameraCutout.None]:0.0} ms, blurred {times[StudioCameraCutout.Blur]:0.0} ms, removed {times[StudioCameraCutout.Remove]:0.0} ms (the stand-in finder takes no time of its own)");

        // Measured against the frame with its background kept, so that a busy PC, which slows
        // all three, does not fail this: on WARP the blur is four times the kept frame.
        foreach (var cutout in new[] { StudioCameraCutout.Blur, StudioCameraCutout.Remove })
        {
            context.Expect(times[cutout] <= (times[StudioCameraCutout.None] * 8) + 15, $"{cutout}: {times[cutout]:0.0} ms a frame against {times[StudioCameraCutout.None]:0.0} ms kept");
        }
    }

    // Exports

    private static async Task ExportedWhole(CheckContext context, Harness harness)
    {
        var clips = harness.Clips;
        var finders = new List<BlockFinder>();
        var exporter = new StudioExporter
        {
            PersonFinderFactory = () =>
            {
                var finder = new BlockFinder { Everything = true };
                lock (finders)
                {
                    finders.Add(finder);
                }

                return finder;
            },
        };

        // With the whole frame called a person, all of the camera is drawn, through everything
        // the cutout does, and its number strip says which frame it was made from. The second
        // from 2 s to 3 s plays at half speed, so each of its frames is drawn twice.
        var project = Projects.Create(clips.Screen, clips.Camera);
        project = project with
        {
            Camera = project.Camera with { Cutout = StudioCameraCutout.Remove },
            Edits = new StudioEdits { Speed = [new StudioSpeedRange { Start = 2, End = 3, Rate = 0.5 }] },
        };
        var (_, reading, _) = await Exporting.ExportAndVerify(context, "people", project, clips.Screen, clips.Camera, 1920, 1080, 30, 1, expectAudio: true, exporter: exporter).ConfigureAwait(false);
        if (reading is null)
        {
            return;
        }

        // Six seconds, one of them twice as long: 210 frames. The camera starts 0.2 s in, so
        // 174 of its frames are shown, 30 of them twice.
        var shown = reading.Camera.Where(number => number >= 0).ToArray();
        var different = shown.Distinct().Count();
        context.Expect(reading.FrameCount == 210 && shown.Length == 204 && different == 174, $"{reading.FrameCount} frames, {shown.Length} with the camera, {different} different camera frames; want 210, 204 and 174");
        int made;
        int calls;
        bool disposed;
        lock (finders)
        {
            made = finders.Count;
            calls = finders.Sum(finder => finder.Calls);
            disposed = finders.All(finder => finder.Disposed);
        }

        context.Expect(made == 1, $"the export made {made} finders");
        context.Expect(calls == different, $"{different} different camera frames in {shown.Length} drawn frames were looked at {calls} times");
        context.Expect(disposed, "the export left a finder undisposed");
        context.Note($"{shown.Length} frames show the camera, {different} different camera frames, looked at {calls} times by {made} finder");

        // The same on the software adapter. There the frames are decoded in system memory and
        // every one of them is drawn from the same texture, so only its stamp tells the
        // renderer that the picture is another.
        lock (finders)
        {
            finders.Clear();
        }

        var (software, softwareReading, _) = await Exporting.ExportAndVerify(context, "people-warp", project, clips.Screen, clips.Camera, 1920, 1080, 30, 1, expectAudio: true, options: new StudioExportOptions(DevicePreference: StudioRenderDevicePreference.WarpOnly), exporter: exporter).ConfigureAwait(false);
        context.Expect(software.SoftwareRendering, "the export for the software adapter was not drawn by it");
        if (softwareReading is not null)
        {
            var softwareShown = softwareReading.Camera.Where(number => number >= 0).ToArray();
            int softwareCalls;
            lock (finders)
            {
                softwareCalls = finders.Sum(finder => finder.Calls);
            }

            context.Expect(softwareShown.Length == 204 && softwareShown.Distinct().Count() == 174, $"on WARP {softwareShown.Length} frames show the camera, {softwareShown.Distinct().Count()} different camera frames; want 204 and 174");
            context.Expect(softwareCalls == 174, $"on WARP 174 different camera frames were looked at {softwareCalls} times");
        }
    }

    private static async Task ExportedBlock(CheckContext context, Harness harness)
    {
        var clips = harness.Clips;
        var spec = clips.Camera.Spec;
        var screenSpec = clips.Screen.Spec;
        var exporter = new StudioExporter { PersonFinderFactory = () => new BlockFinder() };
        var project = Projects.Create(clips.Screen, clips.Camera);
        project = project with
        {
            Screen = new StudioScreenStyle { Shadow = 0 },
            Camera = new StudioCameraStyle { Shape = StudioCameraShape.Rectangle, Mirror = false, Shadow = 0.8, BorderWidth = 0.01, BorderColor = "#FF8000", Cutout = StudioCameraCutout.Remove },
        };
        var folder = context.Folder();
        var output = Path.Combine(folder, "people-block.mp4");
        var paths = Projects.Paths(folder, clips.Screen, clips.Camera);
        var result = await exporter.ExportAsync(project, Projects.Events(clips.Screen), paths, output).ConfigureAwait(false);
        var plan = StudioRenderingMath.BuildFramePlan(project, 30);
        var layout = StudioLayoutPlan.Create(project);

        // Four places in the camera's layer: in the people where the camera is flat green; at
        // the middle of the bottom edge, in the people, where a kept camera has its border; and
        // two places the finder did not call a person, one of them on the left border. Under
        // three of them is the plain part of the screen; the bottom edge hangs over the canvas.
        (double X, double Y)[] Places(StudioResolvedCamera camera, double scale)
        {
            var map = Projects.CameraMap(camera, spec);
            var layer = Box(camera.Rect);
            var (inX, inY) = map.Apply(800, 600);
            var (outX, outY) = map.Apply(200, 300);
            return
            [
                (inX * scale, inY * scale),
                ((layer.Left + layer.Right) / 2.0 * scale, (layer.Bottom - 3.5) * scale),
                (outX * scale, outY * scale),
                ((layer.Left + 3.5) * scale, (layer.Top + layer.Bottom) / 2.0 * scale),
            ];
        }

        var wrong = new List<string>();
        var withCamera = 0;
        var frames = Media.ReadFrames(output, chroma: true, frame =>
        {
            if (frame.Index >= plan.Count)
            {
                return true;
            }

            var resolved = layout.Resolve(plan[frame.Index].SourceTimeSeconds, 1920, 1080);
            if (resolved.Camera is not { } camera || resolved.Screen is not { } screen)
            {
                wrong.Add($"frame {frame.Index}: the layout has no camera or no screen");
                return true;
            }

            var screenMap = Projects.ScreenMap(screen, screenSpec);
            var places = Places(camera, 1);
            withCamera += camera.Visible ? 1 : 0;
            for (var place = 0; place < places.Length; place++)
            {
                var (x, y) = places[place];
                var shows = camera.Visible && place < 2;
                var under = RendererChecks.ClipColorAt(screenSpec, screenMap, clips.Screen.FrameAt(plan[frame.Index].SourceTimeSeconds), x, y);
                if (under is null && place == 1 && !shows)
                {
                    // The canvas under the bottom edge is a gradient, and not looked at while the camera is hidden.
                    continue;
                }

                if (under is null && !shows)
                {
                    wrong.Add($"frame {frame.Index}: place {place} is not over a flat part of the screen");
                    continue;
                }

                var want = shows ? spec.Background : under!.Value;
                var got = frame.Picture.Average(x, y, 1);
                if (got.Distance(want) > ExportVerifier.ColorTolerance)
                {
                    wrong.Add($"frame {frame.Index}, place {place} at ({x:0},{y:0}): {got}, want {want}");
                }
            }

            return true;
        });

        context.Expect(frames == plan.Count && frames == 180, $"{frames} frames in the file, the plan has {plan.Count}, want 180");
        context.Expect(withCamera == 174, $"{withCamera} frames have the camera, want 174");
        context.Expect(wrong.Count == 0, $"{wrong.Count} places are wrong ({wrong.FirstOrDefault()})");
        context.Note($"{result.EncoderDescription}; {frames} frames, {withCamera} with the camera; four places read in each");

        // The poster for 1 s of the video, a third the size.
        var posterPath = Path.Combine(folder, "people-block.jpg");
        await exporter.WritePosterAsync(project, Projects.Events(clips.Screen), paths, posterPath, 640, 1.0).ConfigureAwait(false);
        var poster = Images.Load(posterPath);
        if (!context.Expect(poster.Width == 640 && poster.Height == 360, $"the poster is {poster.Width}×{poster.Height}, want 640×360"))
        {
            return;
        }

        // At this size the bottom edge is one pixel from the canvas, which a JPEG smears into it: the other three places.
        var posterCamera = layout.Resolve(1.0, 1920, 1080).Camera!.Value;
        var posterPlaces = Places(posterCamera, 1.0 / 3);
        foreach (var place in new[] { 0, 2, 3 })
        {
            var (x, y) = posterPlaces[place];
            var want = place < 2 ? spec.Background : screenSpec.Background;
            var got = poster.Pixel((int)Math.Floor(x), (int)Math.Floor(y));
            context.Expect(got.Distance(want) <= 14, $"the poster, place {place} at ({x:0},{y:0}): {got}, want {want}");
        }
    }

    // A real model

    private const int SoakFrames = 1800;

    /// <summary>
    /// What a process run with <c>--soak-model</c> does, and all it does: load the model, look at
    /// <see cref="SoakFrames"/> frames, and say how much it held before and after them.
    /// </summary>
    internal static int Soak(string modelPath)
    {
        using var finder = StudioModelPersonFinder.TryCreate(modelPath);
        if (finder is null)
        {
            Console.WriteLine("SOAK the model could not be loaded");
            return 3;
        }

        // Any picture will do: every frame costs the model the same.
        var input = new byte[finder.Width * finder.Height * 4];
        for (var index = 0; index < input.Length; index++)
        {
            input[index] = (byte)(((uint)index * 2654435761u) >> 24);
        }

        var mask = new byte[finder.Width * finder.Height];
        using var process = Process.GetCurrentProcess();
        long before = 0;
        for (var index = -200; index < SoakFrames; index++)
        {
            if (index == 0)
            {
                before = Held(process);
            }

            if (!finder.TryFind(input, mask))
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"SOAK the model could not look at frame {index}"));
                return 3;
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"SOAK {before} {Held(process)}"));
        return 0;

        static long Held(Process process)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            process.Refresh();
            return process.PrivateMemorySize64;
        }
    }

    /// <summary>Runs this tool again with <c>--soak-model</c> and reads its one line. Null when it gave none.</summary>
    private static (long Before, long After)? RunSoak(string modelPath)
    {
        if (Environment.ProcessPath is not { } tool)
        {
            return null;
        }

        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--soak-model");
        start.ArgumentList.Add(modelPath);
        using var child = Process.Start(start);
        if (child is null)
        {
            return null;
        }

        // Windows' machine learning writes a few lines about the processor to the error stream.
        child.ErrorDataReceived += (_, _) => { };
        child.BeginErrorReadLine();
        var reading = child.StandardOutput.ReadToEndAsync();
        if (!child.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            child.Kill(entireProcessTree: true);
            return null;
        }

        foreach (var line in reading.GetAwaiter().GetResult().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ');
            if (parts is ["SOAK", var first, var second]
                && long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var before)
                && long.TryParse(second, NumberStyles.None, CultureInfo.InvariantCulture, out var after))
            {
                return (before, after);
            }
        }

        return null;
    }

    private static unsafe void RealModel(CheckContext context, RenderBench bench)
    {
        if (ModelPath is null)
        {
            throw new CheckSkippedException($"no model was given (--person-model <file>). The app looks for {StudioPersonFinders.ModelFileName} under Assets\\Studio next to itself; here that is {(StudioPersonFinders.IsAvailable ? "there" : "not there")}.");
        }

        context.Expect(StudioModelPersonFinder.TryCreate(Path.Combine(Path.GetTempPath(), "there-is-no-such-model.onnx")) is null, "a model file that is not there gave a finder");
        var notAModel = Path.Combine(context.Folder(), "not-a-model.onnx");
        File.WriteAllText(notAModel, "This is not a model.");
        context.Expect(StudioModelPersonFinder.TryCreate(notAModel) is null, "a text file gave a finder");

        var watch = Stopwatch.StartNew();
        using var probe = StudioModelPersonFinder.TryCreate(ModelPath);
        var loaded = watch.Elapsed.TotalMilliseconds;
        if (probe is null)
        {
            context.Fail($"{ModelPath} could not be loaded as a model that finds people");
            return;
        }

        context.Note($"{Path.GetFileName(ModelPath)}: looks at a {probe.Width}×{probe.Height} picture; loaded in {loaded:0} ms");

        // The frame of the test clip has nobody in it.
        var input = new byte[probe.Width * probe.Height * 4];
        var mask = new byte[probe.Width * probe.Height];
        var pattern = bench.CameraClip.Spec.DrawBgra(RenderBench.CameraNumber);
        for (var y = 0; y < probe.Height; y++)
        {
            for (var x = 0; x < probe.Width; x++)
            {
                var source = (((y * 720 / probe.Height) * 1280) + (x * 1280 / probe.Width)) * 4;
                pattern.AsSpan(source, 4).CopyTo(input.AsSpan(((y * probe.Width) + x) * 4, 4));
            }
        }

        context.Expect(probe.TryFind(input, mask), "the model could not look at the test clip's frame");
        context.Note($"in the test clip's frame, which has nobody in it, the model calls {mask.Count(value => value >= 128) * 100.0 / mask.Length:0.0} % a person");

        var times = new double[60];
        for (var index = 0; index < times.Length; index++)
        {
            var start = Stopwatch.GetTimestamp();
            probe.TryFind(input, mask);
            times[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(times);
        context.Measure($"the model alone, 60 frames one after another: median {times[30]:0.0} ms, slowest {times[^1]:0.0} ms");

        // A minute of video at 30 frames a second. Each frame hands the model a copy of the picture
        // and gets a mask back, both kept outside the garbage collector's sight: what the process
        // holds must not grow with the number of frames. With a binding made anew for each frame
        // it grew by some 56 MB over these frames; with one binding kept it does not grow.
        //
        // This is measured in a process of its own that does nothing else. Here, with the graphics
        // devices, the clips and the pictures of every check before this one, what the process
        // holds moves by more than that on its own: it was once 188 MB less after the frames.
        if (RunSoak(ModelPath) is { } soak)
        {
            var grown = (soak.After - soak.Before) / (1024.0 * 1024.0);
            context.Expect(grown < 16, $"after {SoakFrames} more frames a process that only runs the model holds {grown:0.0} MB more");
            context.Measure($"{SoakFrames} frames through the model, in a process of its own: it holds {grown:0.0} MB more afterwards (of {soak.Before / (1024.0 * 1024.0):0} MB)");
        }
        else
        {
            context.Fail("the process that runs the model for a minute of frames gave no answer");
        }

        if (PhotoPath is null)
        {
            context.Note("no photograph was given (--person-photo <file>), so nothing with a person in it was drawn");
            return;
        }

        // The photograph as the camera's frame, alone on the canvas, with its background removed.
        int width;
        int height;
        byte[] photo;
        using (var bitmap = new System.Drawing.Bitmap(PhotoPath))
        {
            width = bitmap.Width;
            height = bitmap.Height;
            photo = new byte[width * height * 4];
            var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            try
            {
                for (var row = 0; row < height; row++)
                {
                    new ReadOnlySpan<byte>((byte*)data.Scan0 + ((long)row * data.Stride), width * 4).CopyTo(photo.AsSpan(row * width * 4));
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        var frame = new StudioGpuVideoFrame(bench.Texture(photo, width, height), 0, width, height);
        var baseProject = Project(bench, StudioCameraCutout.Remove, layout: StudioLayout.Camera);
        var project = baseProject with { Sources = baseProject.Sources with { Camera = baseProject.Sources.Camera! with { Width = width, Height = height } } };
        var camera = StudioLayoutResolver.Resolve(project, 0, 1920, 1080).Camera!.Value;
        var layer = Box(camera.Rect);
        var kept = bench.Render(project with { Camera = project.Camera with { Cutout = StudioCameraCutout.None } }, camera: frame);
        var without = bench.Render(project, withCamera: false);
        var modelPath = ModelPath;
        var renderer = NewRenderer(bench, () => StudioModelPersonFinder.TryCreate(modelPath));
        try
        {
            var first = Stopwatch.StartNew();
            var removed = bench.Render(project, renderer: renderer, camera: frame);
            var firstFrame = first.Elapsed.TotalMilliseconds;
            var blurred = bench.Render(project with { Camera = project.Camera with { Cutout = StudioCameraCutout.Blur } }, renderer: renderer, camera: frame);
            removed.SavePng(Path.Combine(context.Folder(), "photo-removed.png"));
            blurred.SavePng(Path.Combine(context.Folder(), "photo-blurred.png"));
            kept.SavePng(Path.Combine(context.Folder(), "photo-kept.png"));

            // How much of the layer shows the photograph, and how much what is under it.
            var person = 0;
            var gone = 0;
            var between = 0;
            var total = 0;
            for (var y = layer.Top + 4; y < layer.Bottom - 4; y += 2)
            {
                for (var x = layer.Left + 4; x < layer.Right - 4; x += 2)
                {
                    if (Share(removed.Pixel(x, y), without.Pixel(x, y), kept.Pixel(x, y)) is not { } share)
                    {
                        continue;
                    }

                    total++;
                    if (share > 0.9)
                    {
                        person++;
                    }
                    else if (share < 0.1)
                    {
                        gone++;
                    }
                    else
                    {
                        between++;
                    }
                }
            }

            context.Expect(total > 50000, $"only {total} points of the layer could be told from what is under it");
            var personShare = person * 100.0 / Math.Max(1, total);
            var goneShare = gone * 100.0 / Math.Max(1, total);
            context.Expect(personShare is > 15 and < 85, $"{personShare:0.0} % of the layer shows the photograph");
            context.Expect(goneShare > 15, $"only {goneShare:0.0} % of the layer shows what is under it");
            context.Expect(between * 100.0 / Math.Max(1, total) < 15, $"{between * 100.0 / Math.Max(1, total):0.0} % of the layer is neither");
            context.Expect(Picture.MaxDifference(blurred, kept).Difference > 20, "blurred, the photograph looks as it does kept");
            context.Measure($"{Path.GetFileName(PhotoPath)} ({width}×{height}): {personShare:0.0} % of the layer is the person, {goneShare:0.0} % is gone, {between * 100.0 / Math.Max(1, total):0.0} % is edge; the first frame took {firstFrame:0} ms");

            var draws = new double[30];
            for (var index = 0; index < draws.Length; index++)
            {
                var start = Stopwatch.GetTimestamp();
                bench.Render(project, renderer: renderer, camera: frame);
                draws[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }

            Array.Sort(draws);
            var plain = Stopwatch.StartNew();
            for (var index = 0; index < 30; index++)
            {
                bench.Render(project with { Camera = project.Camera with { Cutout = StudioCameraCutout.None } }, camera: frame);
            }

            context.Measure($"a 1080p frame with the model finding the person, drawn and read back: median {draws[15]:0.0} ms, slowest {draws[^1]:0.0} ms; with the background kept {plain.Elapsed.TotalMilliseconds / 30:0.0} ms");
            context.Note($"pictures in {context.Folder()}");
        }
        finally
        {
            Release(bench, renderer);
        }
    }
}
