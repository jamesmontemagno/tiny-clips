using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>
/// The renderer's pixel matrix: single frames drawn from known pictures and read back. Every
/// expectation is a position or colour worked out here from the format's rules, never a number
/// the renderer produced.
/// </summary>
internal static partial class RendererChecks
{
    public const string Orange = "#C86432";

    private static readonly Rgb OrangeRgb = Rgb.FromHex(Orange);

    public static async Task Run(Harness harness)
    {
        if (!harness.Wants("renderer"))
        {
            return;
        }

        foreach (var warp in new[] { false, true })
        {
            var prefix = warp ? "renderer on WARP: " : "renderer: ";
            RenderBench bench;
            try
            {
                bench = new RenderBench(warp);
            }
            catch (Exception ex)
            {
                await harness.Check("renderer", prefix + "a device and a renderer can be made", _ => throw new CheckFailedException($"{ex.GetType().Name}: {ex.Message}")).ConfigureAwait(false);
                continue;
            }

            using (bench)
            {
                Task Check(string name, Action<CheckContext> body) => harness.Check("renderer", prefix + name, body);

                await Check("layout: screen", context => Layout(context, bench, StudioLayout.Screen, (115, 65, 1805, 1015), null)).ConfigureAwait(false);
                await Check("layout: bubble", context => Layout(context, bench, StudioLayout.Bubble, (115, 65, 1805, 1015), (1499, 659, 1888, 1048))).ConfigureAwait(false);
                await Check("layout: side by side", context => Layout(context, bench, StudioLayout.SideBySide, (65, 192, 1303, 888), (1325, 192, 1855, 888))).ConfigureAwait(false);
                await Check("layout: camera", context => Layout(context, bench, StudioLayout.Camera, null, (65, 65, 1855, 1015))).ConfigureAwait(false);
                await Check("camera shape: circle", context => Shape(context, bench, StudioCameraShape.Circle, (1499, 659, 1888, 1048))).ConfigureAwait(false);
                await Check("camera shape: squircle", context => Shape(context, bench, StudioCameraShape.Squircle, (1499, 659, 1888, 1048))).ConfigureAwait(false);
                await Check("camera shape: rounded rectangle", context => Shape(context, bench, StudioCameraShape.RoundedRectangle, (1196, 659, 1888, 1048))).ConfigureAwait(false);
                await Check("camera shape: rectangle", context => Shape(context, bench, StudioCameraShape.Rectangle, (1196, 659, 1888, 1048))).ConfigureAwait(false);
                await Check("camera mirror", context => Mirror(context, bench)).ConfigureAwait(false);
                await Check("crops and scenes", context => CropsAndScenes(context, bench)).ConfigureAwait(false);
                await Check("camera not visible: nothing of it is drawn", context => InvisibleCamera(context, bench)).ConfigureAwait(false);
                await Check("camera border", context => Border(context, bench)).ConfigureAwait(false);
                await Check("shadows follow the Gaussian the format describes", context => Shadows(context, bench)).ConfigureAwait(false);
                await Check("click ring: radius, width and alpha", context => ClickRing(context, bench)).ConfigureAwait(false);
                await Check("click ring: clipped to the card", context => ClickRingClip(context, bench)).ConfigureAwait(false);
                await Check("background: none, solid, gradient", context => Background(context, bench)).ConfigureAwait(false);
                await Check("background: image", context => BackgroundImage(context, bench)).ConfigureAwait(false);
                await Check("branding badge", context => Badge(context, bench)).ConfigureAwait(false);
                await Check("targets and sources: formats, padding, caches", context => TargetsAndSources(context, bench)).ConfigureAwait(false);
                await Check("scaling quality: linear and high-quality", context => ScalingQuality(context, bench)).ConfigureAwait(false);
                await Check("time to draw a frame", context => DrawTime(context, bench)).ConfigureAwait(false);
            }
        }
    }

    // What the checks share

    private static (int Left, int Top, int Right, int Bottom) Box(StudioFrameRect rect)
    {
        var aligned = Projects.Aligned(rect);
        return ((int)aligned.X, (int)aligned.Y, (int)(aligned.X + aligned.Width), (int)(aligned.Y + aligned.Height));
    }

    private static void ExpectBox(CheckContext context, string what, StudioFrameRect? rect, (int Left, int Top, int Right, int Bottom)? want)
    {
        if (rect is null || want is null)
        {
            context.Expect(rect is null && want is null, $"{what} is {(rect is null ? "not in the layout" : "in the layout")}, and should {(want is null ? "not be" : "be")}");
            return;
        }

        var box = Box(rect.Value);
        context.Expect(box == want.Value, $"{what} is at {box}, worked out by hand as {want.Value}");
    }

    private static void ExpectPixel(CheckContext context, Picture picture, double x, double y, Rgb want, double tolerance, string what)
    {
        var px = (int)Math.Floor(x);
        var py = (int)Math.Floor(y);
        if (px < 0 || py < 0 || px >= picture.Width || py >= picture.Height)
        {
            context.Fail($"{what}: ({px},{py}) is off the picture");
            return;
        }

        var got = picture.Pixel(px, py);
        context.Expect(got.Distance(want) <= tolerance, $"{what}: pixel ({px},{py}) is {got}, want {want}");
    }

    internal static void ExpectSame(CheckContext context, Picture picture, Picture reference, string what, int left = 0, int top = 0, int right = int.MaxValue, int bottom = int.MaxValue, int tolerance = 0)
    {
        var (difference, x, y) = Picture.MaxDifference(picture, reference, left, top, right, bottom);
        context.Expect(difference <= tolerance, $"{what}: pixel ({x},{y}) is {picture.Pixel(Math.Max(0, x), Math.Max(0, y))}, want {reference.Pixel(Math.Max(0, x), Math.Max(0, y))}");
    }

    /// <summary>
    /// The clip's own colour at the place a picture pixel shows, or null near a seam in the
    /// pattern. At the very edge of the clip that is the colour a little further in: the pattern
    /// is flat out to its edges wherever it is flat that far in.
    /// </summary>
    private static Rgb? ClipColorAt(ClipSpec spec, ClipMap map, int number, double x, double y)
    {
        var margin = (3 / Math.Min(Math.Abs(map.ScaleX), Math.Abs(map.ScaleY))) + 1;
        var clipX = (Math.Floor(x) + 0.5 - map.OriginX) / map.ScaleX;
        var clipY = (Math.Floor(y) + 0.5 - map.OriginY) / map.ScaleY;
        if (clipX < 0 || clipY < 0 || clipX > spec.Width || clipY > spec.Height)
        {
            return null;
        }

        return spec.FlatColorAt(Math.Clamp(clipX, margin + 0.01, spec.Width - margin - 0.01), Math.Clamp(clipY, margin + 0.01, spec.Height - margin - 0.01), number, margin);
    }

    /// <summary>The frame number reads right and the four colour patches are the colours drawn.</summary>
    private static void ExpectContent(CheckContext context, string what, Picture picture, ClipSpec spec, ClipMap map, int number)
    {
        var read = FrameCode.Decode(picture, spec, map);
        context.Expect(read == number, $"{what}'s strip reads {read}, want {number} ({FrameCode.Describe(picture, spec, map)})");
        var patches = FrameCode.ReadPatches(picture, spec, map);
        for (var index = 0; index < patches.Length; index++)
        {
            context.Expect(patches[index].Distance(ClipSpec.PatchColors[index]) <= 1, $"{what}'s patch {index} is {patches[index]}, drawn {ClipSpec.PatchColors[index]}");
        }

        // The picture is where the layout puts it, to well under a pixel.
        var edge = FrameCode.PatchEdgeError(picture, spec, map);
        context.Expect(edge is <= 0.6, $"{what}: an edge inside it is {edge?.ToString("0.00", CultureInfo.InvariantCulture) ?? "not measurable"} px from where the layout puts it");
    }

    /// <summary>
    /// At the middle of each edge of a layer: the pixel outside is what is there without the
    /// layer, and the pixel inside is the layer's own colour. So an edge is a step, on the
    /// pixel boundary the format's rounding gives, and not a blend.
    /// </summary>
    private static void ExpectEdges(CheckContext context, string what, Picture picture, Picture without, (int Left, int Top, int Right, int Bottom) box, ClipSpec spec, ClipMap map, int number)
    {
        var (left, top, right, bottom) = box;
        var middleX = (left + right) / 2;
        var middleY = (top + bottom) / 2;
        var edges = new (string Name, int OutX, int OutY, int InX, int InY)[]
        {
            ("left", left - 1, middleY, left, middleY),
            ("right", right, middleY, right - 1, middleY),
            ("top", middleX, top - 1, middleX, top),
            ("bottom", middleX, bottom, middleX, bottom - 1),
        };
        var compared = 0;
        foreach (var edge in edges)
        {
            ExpectPixel(context, picture, edge.OutX, edge.OutY, without.Pixel(edge.OutX, edge.OutY), 0, $"{what}, just outside its {edge.Name} edge");

            if (ClipColorAt(spec, map, number, edge.InX, edge.InY) is { } flat)
            {
                compared++;
                ExpectPixel(context, picture, edge.InX, edge.InY, flat, 1, $"{what}, just inside its {edge.Name} edge");
            }
        }

        context.Expect(compared >= 2, $"{what}: only {compared} of its edges could be compared with the pattern");
    }

    /// <summary>Just outside a rounded corner is what is there without the layer; just inside is the layer.</summary>
    private static void ExpectRoundedCorners(CheckContext context, string what, Picture picture, Picture without, (int Left, int Top, int Right, int Bottom) box, double radius)
    {
        var diagonal = Math.Sqrt(0.5);
        foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
        {
            var cornerX = sx < 0 ? box.Left : box.Right;
            var cornerY = sy < 0 ? box.Top : box.Bottom;
            var centerX = cornerX - (sx * radius);
            var centerY = cornerY - (sy * radius);
            var outX = centerX + (sx * (radius + 3) * diagonal);
            var outY = centerY + (sy * (radius + 3) * diagonal);
            var inX = centerX + (sx * (radius - 3) * diagonal);
            var inY = centerY + (sy * (radius - 3) * diagonal);
            ExpectPixel(context, picture, outX, outY, without.Pixel((int)Math.Floor(outX), (int)Math.Floor(outY)), 0, $"{what}, 3 px outside its rounded corner");
            var inside = picture.Pixel((int)Math.Floor(inX), (int)Math.Floor(inY));
            context.Expect(inside.Distance(without.Pixel((int)Math.Floor(inX), (int)Math.Floor(inY))) > 20, $"{what}, 3 px inside its rounded corner at ({inX:0},{inY:0}): {inside} is what is there without it");

            // The corner of the bounding box itself is outside the shape.
            var boxX = cornerX - (sx * 1.5);
            var boxY = cornerY - (sy * 1.5);
            ExpectPixel(context, picture, boxX, boxY, without.Pixel((int)Math.Floor(boxX), (int)Math.Floor(boxY)), 0, $"{what}, the corner of its box");
        }
    }

    private static string Show(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    // Layouts

    private static void Layout(CheckContext context, RenderBench bench, StudioLayout layout, (int, int, int, int)? wantScreen, (int, int, int, int)? wantCamera)
    {
        var project = bench.Base(layout);
        var resolved = StudioLayoutResolver.Resolve(project, 0, 1920, 1080);
        ExpectBox(context, "the screen", resolved.Screen?.Rect, wantScreen);
        ExpectBox(context, "the camera", resolved.Camera?.Rect, wantCamera);

        var picture = bench.Render(project);
        var withoutScreen = bench.Render(project, withScreen: false);
        var withoutCamera = bench.Render(project, withCamera: false);
        var nothing = bench.Render(project, withScreen: false, withCamera: false);

        // With neither picture there is only the background.
        ExpectSame(context, nothing, bench.Render(bench.Base(StudioLayout.Screen), withScreen: false, withCamera: false), "the frame with no pictures");
        foreach (var (x, y) in new[] { (0, 0), (1919, 0), (0, 1079), (1919, 1079), (30, 540), (960, 30) })
        {
            ExpectPixel(context, picture, x, y, OrangeRgb, 0, "the background");
        }

        if (resolved.Screen is { } screen)
        {
            var map = Projects.ScreenMap(screen, bench.ScreenClip.Spec);
            ExpectContent(context, "the screen", picture, bench.ScreenClip.Spec, map, RenderBench.ScreenNumber);

            // The middle of the card shows the middle of the screen, which is its plain background.
            var card = Box(screen.Rect);
            ExpectPixel(context, picture, (card.Left + card.Right) / 2.0, (card.Top + card.Bottom) / 2.0, bench.ScreenClip.Spec.Background, 1, "the middle of the screen card");

            // The camera bubble sits on one corner of the card, so the card's outline is looked at without it.
            ExpectEdges(context, "the screen", withoutCamera, nothing, Box(screen.Rect), bench.ScreenClip.Spec, map, RenderBench.ScreenNumber);

            // 0.02 of the short side: 21.6 px.
            context.Expect(Math.Abs(screen.CornerRadius - 21.6) < 1e-9, $"the screen's corner radius resolves to {screen.CornerRadius}, want 21.6");
            ExpectRoundedCorners(context, "the screen", withoutCamera, nothing, Box(screen.Rect), 21.6);
        }
        else
        {
            ExpectSame(context, picture, withoutScreen, "a layout without the screen, with and without the screen's picture");
        }

        if (resolved.Camera is { } camera)
        {
            var map = Projects.CameraMap(camera, bench.CameraClip.Spec);
            ExpectContent(context, "the camera", picture, bench.CameraClip.Spec, map, RenderBench.CameraNumber);
            ExpectEdges(context, "the camera", picture, withoutCamera, Box(camera.Rect), bench.CameraClip.Spec, map, RenderBench.CameraNumber);
            if (layout != StudioLayout.Bubble)
            {
                // Outside the bubble layout the camera is a card like the screen: same radius.
                context.Expect(camera.Shape == StudioCameraShape.RoundedRectangle && Math.Abs(camera.CornerRadius - 21.6) < 1e-9, $"the camera resolves to {camera.Shape} with radius {camera.CornerRadius}");
                ExpectRoundedCorners(context, "the camera", picture, withoutCamera, Box(camera.Rect), 21.6);
            }
        }
        else
        {
            ExpectSame(context, picture, withoutCamera, "a layout without the camera, with and without the camera's picture");
        }
    }
}
