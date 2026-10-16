using System.Diagnostics;
using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;
using Box = TinyClips.Tools.StudioRenderCheck.ZoomChecks.Box;
using Window = TinyClips.Tools.StudioRenderCheck.ZoomChecks.Window;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>
/// Scenes entered with a morph, section 6.9 of the format, read back from pixels. Where each
/// layer rests in each layout is worked out here by hand from section 6.3, and where it is on
/// its way from one to the other by the format's ease. The layout resolver has to give exactly
/// that, and the picture has to show it: the test pattern is measured inside each layer, the
/// layer's outline is looked for where it should be, and a layer that fades is compared with
/// what is under it.
/// </summary>
internal static class SceneChecks
{
    /// <summary>How far, in pixels, an edge of the pattern may be from its place in a frame read straight from the renderer.</summary>
    private const double DrawnTolerance = 0.5;

    /// <summary>The same for a frame that has been through a video encoder or a JPEG.</summary>
    private const double EncodedTolerance = ExportVerifier.EdgeTolerance;

    // The ease of section 6.8, u² (3 − 2u), a quarter, half and three quarters of the way:
    // 1/16 × 2.5, 1/4 × 2 and 9/16 × 1.5.
    private const double Quarter = 0.15625;
    private const double Half = 0.5;
    private const double ThreeQuarters = 0.84375;

    /// <summary>A card's corners: 0.02 of the canvas's short side.</summary>
    private const double CardRadius = 21.6;

    /// <summary>A round bubble's: half its side.</summary>
    private const double BubbleRadius = 194.4;

    // Where the layers rest on a 1920×1080 canvas. Padding is 0.06 of the short side, 64.8 px,
    // which leaves a content box of 1790.4×950.4 at (64.8, 64.8).

    /// <summary>The screen by itself or under a bubble: 16:9, as tall as the content box and in its middle.</summary>
    private static readonly StudioFrameRect ScreenCard = new(115.2, 64.8, 1689.6, 950.4);

    /// <summary>A bubble 0.36 of the short side across, 388.8 px, 0.03 of it, 32.4 px, from the right and bottom edges.</summary>
    private static readonly StudioFrameRect Bubble = new(1498.8, 658.8, 388.8, 388.8);

    /// <summary>The same bubble in the top-left corner.</summary>
    private static readonly StudioFrameRect TopLeftBubble = new(32.4, 32.4, 388.8, 388.8);

    /// <summary>A bubble half the short side across, 540 px, in the bottom-right corner.</summary>
    private static readonly StudioFrameRect LargeBubble = new(1347.6, 507.6, 540, 540);

    // Side by side: the gap is 0.02 of the short side, 21.6 px. The camera gets 0.3 of the other
    // 1768.8 px, 530.64 px. The screen is 16:9 in the remaining 1238.16 px, so 696.465 px tall;
    // the camera is as tall, and both are in the middle of the content box's height.
    private static readonly StudioFrameRect SplitScreen = new(64.8, 191.7675, 1238.16, 696.465);
    private static readonly StudioFrameRect SplitCamera = new(1324.56, 191.7675, 530.64, 696.465);

    /// <summary>The camera by itself fills the content box.</summary>
    private static readonly StudioFrameRect CameraCard = new(64.8, 64.8, 1790.4, 950.4);

    /// <summary>The middle 9/16 of the camera's width: what a square shows of a 16:9 picture.</summary>
    private static readonly Window SquareOfCamera = new(0.21875, 0, 0.5625, 1);

    private static readonly Rgb Canvas = Rgb.FromHex(RendererChecks.Orange);
    private static readonly Rgb ScreenBackground = new(30, 50, 100);
    private static readonly Rgb CameraBackground = new(30, 90, 50);
    private static readonly Rgb CameraLeftBackground = new(16, 54, 28);

    public static async Task Run(Harness harness)
    {
        if (!harness.Wants("scenes"))
        {
            return;
        }

        foreach (var warp in new[] { false, true })
        {
            var prefix = warp ? "scenes on WARP: " : "scenes: ";
            RenderBench bench;
            try
            {
                bench = new RenderBench(warp);
            }
            catch (Exception ex)
            {
                await harness.Check("scenes", prefix + "a device and a renderer can be made", _ => throw new CheckFailedException($"{ex.GetType().Name}: {ex.Message}")).ConfigureAwait(false);
                continue;
            }

            using (bench)
            {
                Task Check(string name, Action<CheckContext> body) => harness.Check("scenes", prefix + name, body);

                await Check("a bubble becomes a card: both layers on their way", context => BubbleToCard(context, bench)).ConfigureAwait(false);
                await Check("a bubble that moves stays round, and a squircle that grows stays a squircle", context => Bubbles(context, bench)).ConfigureAwait(false);
                await Check("a screen the new scene does not have fades out where it was", context => ScreenFadesOut(context, bench)).ConfigureAwait(false);
                await Check("a camera the scene before did not have fades in where it will be", context => CameraFadesIn(context, bench)).ConfigureAwait(false);
                await Check("two scenes with no layer in common", context => NothingInCommon(context, bench)).ConfigureAwait(false);
                await Check("a shadow, a border and click rings fade with their layer", context => Parts(context, bench)).ConfigureAwait(false);
                await Check("a zoom and click rings on a card that moves", context => ZoomAndRings(context, bench)).ConfigureAwait(false);
                await Check("time to draw a frame of a move", context => DrawTime(context, bench)).ConfigureAwait(false);
            }
        }

        await harness.Check("scenes", "scenes: export, frame by frame", context => Export(context, harness)).ConfigureAwait(false);
        await harness.Check("scenes", "scenes: a camera fading out, exported", context => FadeExport(context, harness)).ConfigureAwait(false);
        await harness.Check("scenes", "scenes: poster during a move", context => Poster(context, harness)).ConfigureAwait(false);
    }

    // The projects

    /// <summary>A scene that is cut to, or entered by moving for <paramref name="morph"/> seconds.</summary>
    private static StudioScene Scene(double start, StudioLayout layout, double morph = 0, StudioAnchor anchor = StudioAnchor.BottomRight, double size = 0.36) =>
        new()
        {
            Start = start,
            Layout = layout,
            Bubble = new StudioBubble { Anchor = anchor, Size = size },
            Transition = morph > 0 ? new StudioTransition { Kind = StudioTransitionKind.Morph, Duration = morph } : new StudioTransition(),
        };

    /// <summary>
    /// The bench's plain project with these scenes: a solid canvas, no shadows and no border,
    /// so a pixel is exactly one colour, and a camera that is not mirrored, so its pattern can
    /// be measured like the screen's.
    /// </summary>
    private static StudioProject Plain(RenderBench bench, params StudioScene[] scenes) =>
        bench.Base() with
        {
            Camera = new StudioCameraStyle { Shape = StudioCameraShape.Circle, Mirror = false, Shadow = 0, BorderWidth = 0 },
            Scenes = scenes,
        };

    /// <summary>The same look over the clips an export is made from.</summary>
    private static StudioProject ForExport(TestClip screen, TestClip camera, params StudioScene[] scenes) =>
        Projects.Create(screen, camera, cameraOffset: 0) with
        {
            Canvas = new StudioCanvas { Background = new StudioBackground { Style = StudioBackgroundStyle.Solid, Primary = RendererChecks.Orange } },
            Screen = new StudioScreenStyle { Shadow = 0 },
            Camera = new StudioCameraStyle { Shape = StudioCameraShape.Circle, Mirror = false, Shadow = 0, BorderWidth = 0 },
            Scenes = scenes,
            Audio = new StudioAudio { Muted = true },
        };

    /// <summary>The format's lerp between two rectangles: each of x, y, width and height by itself.</summary>
    private static StudioFrameRect Between(StudioFrameRect from, StudioFrameRect to, double amount) =>
        new(
            from.X + ((to.X - from.X) * amount),
            from.Y + ((to.Y - from.Y) * amount),
            from.Width + ((to.Width - from.Width) * amount),
            from.Height + ((to.Height - from.Height) * amount));

    private static double Between(double from, double to, double amount) => from + ((to - from) * amount);

    /// <summary>
    /// Section 6.4 for a 16:9 camera that is not cropped: the part of its picture, around the
    /// middle, that has the shape of the card.
    /// </summary>
    private static Window CameraSource(StudioFrameRect card)
    {
        const double Camera = 16.0 / 9;
        var shape = card.Width / card.Height;
        return Camera > shape
            ? new Window((1 - (shape / Camera)) / 2, 0, shape / Camera, 1)
            : new Window(0, (1 - (Camera / shape)) / 2, 1, Camera / shape);
    }

    // Both layers move

    private static void BubbleToCard(CheckContext context, RenderBench bench)
    {
        // The bubble scene until 2 s, then a second of moving into the side-by-side scene. The
        // screen's card goes from one place to the other and keeps its radius. The camera goes
        // from a circle to a card: the two shapes differ, so on the way it is a rounded rectangle
        // whose radius goes from half the bubble's side to a card's, and it shows the part of its
        // picture that has the shape it has at that instant.
        var project = Plain(bench, Scene(0, StudioLayout.Bubble), Scene(2, StudioLayout.SideBySide, morph: 1));
        var nothing = bench.Render(project, withScreen: false, withCamera: false);
        foreach (var (time, amount, what) in new[]
        {
            (2.0, 0.0, "the instant the scene starts"),
            (2.25, Quarter, "a quarter of the way"),
            (2.5, Half, "halfway"),
            (2.75, ThreeQuarters, "three quarters of the way"),
        })
        {
            var screen = new Layer(Between(ScreenCard, SplitScreen, amount), CardRadius);
            var cameraRect = Between(Bubble, SplitCamera, amount);
            var camera = new Layer(cameraRect, Between(BubbleRadius, CardRadius, amount), Source: CameraSource(cameraRect));
            ExpectResolved(context, project, time, 1, StudioLayout.SideBySide, screen, camera, what);
            ExpectScreen(context, bench, project, time, screen, nothing, what);
            ExpectCamera(context, bench, project, time, camera, nothing, what);
            ExpectCameraOnTop(context, bench, project, time, camera, nothing, what, overTheScreen: true);
        }

        // Up to the scene's start the frame is the bubble scene's, and from the instant the move
        // ends it is the side-by-side scene's.
        var bubble = Plain(bench, Scene(0, StudioLayout.Bubble));
        var sideBySide = Plain(bench, Scene(0, StudioLayout.SideBySide));
        RendererChecks.ExpectSame(context, bench.Render(project, time: 1.999), bench.Render(bubble, time: 1.999), "just before the scene starts, against the bubble scene by itself");
        RendererChecks.ExpectSame(context, bench.Render(project, time: 3), bench.Render(sideBySide, time: 3), "the instant the move ends, against the side-by-side scene by itself");
        ExpectResolved(context, project, 3, 1, StudioLayout.SideBySide, new Layer(SplitScreen, CardRadius), new Layer(SplitCamera, CardRadius, Source: CameraSource(SplitCamera)), "the instant the move ends");

        // The other way, from a card to a round bubble, the camera is a rounded rectangle too for
        // as long as it moves, and a circle from the instant it arrives.
        var back = Plain(bench, Scene(0, StudioLayout.SideBySide), Scene(2, StudioLayout.Bubble, morph: 1));
        foreach (var (time, amount, what) in new[] { (2.5, Half, "a card halfway to a round bubble"), (2.75, ThreeQuarters, "a card three quarters of the way to a round bubble") })
        {
            var cameraRect = Between(SplitCamera, Bubble, amount);
            var camera = new Layer(cameraRect, Between(CardRadius, BubbleRadius, amount), Source: CameraSource(cameraRect));
            ExpectResolved(context, back, time, 1, StudioLayout.Bubble, new Layer(Between(SplitScreen, ScreenCard, amount), CardRadius), camera, what);
            ExpectCamera(context, bench, back, time, camera, nothing, what);
        }

        ExpectResolved(context, back, 3, 1, StudioLayout.Bubble, new Layer(ScreenCard, CardRadius), new Layer(Bubble, BubbleRadius, Source: SquareOfCamera, Shape: StudioCameraShape.Circle), "a card that has become a round bubble");
        RendererChecks.ExpectSame(context, bench.Render(back, time: 3), bench.Render(bubble, time: 3), "the instant a card has become a round bubble, against the bubble scene by itself");

        // No jump as the move starts: a rounded rectangle whose radius is half its side covers
        // the pixels the circle did. Only pixels the outline itself runs through may change.
        // Inside, a graphics card may sample the picture a level differently for the other
        // shape, where the picture has an edge of its own, and no more than that.
        var before = bench.Render(project, time: 1.999);
        var starting = bench.Render(project, time: 2);
        var box = ToBox(Bubble);
        var centerX = (box.Left + box.Right) / 2.0;
        var centerY = (box.Top + box.Bottom) / 2.0;
        var changed = 0;
        var touched = 0;
        var compared = 0;
        var worst = 0.0;
        var worstWhere = string.Empty;
        for (var y = 0; y < 1080; y++)
        {
            for (var x = 0; x < 1920; x++)
            {
                var fromOutline = Math.Abs(Math.Sqrt(Math.Pow(x + 0.5 - centerX, 2) + Math.Pow(y + 0.5 - centerY, 2)) - (box.Width / 2.0));
                if (fromOutline <= 1.5)
                {
                    continue;
                }

                compared++;
                var difference = starting.Pixel(x, y).Distance(before.Pixel(x, y));
                touched += difference > 0 ? 1 : 0;
                changed += difference > 1 ? 1 : 0;
                if (difference > worst)
                {
                    worst = difference;
                    worstWhere = string.Create(CultureInfo.InvariantCulture, $"({x}, {y}), {fromOutline:0.0} px from the outline, is {starting.Pixel(x, y)} and was {before.Pixel(x, y)}");
                }
            }
        }

        context.Expect(changed == 0, $"as the move starts, {changed} pixels more than 1.5 px from the bubble's outline differ by more than one level from the frame before; the worst, {worstWhere}");
        if (changed > 0)
        {
            ZoomChecks.Keep(context, before, "just before the move starts", always: true);
            ZoomChecks.Keep(context, starting, "as the move starts", always: true);
        }

        context.Note($"as the move starts, of the {compared} pixels more than 1.5 px from the bubble's outline {touched} differ from the frame before, by {worst:0} of 255 at most");
    }

    private static void Bubbles(CheckContext context, RenderBench bench)
    {
        // A round bubble goes from the top-left corner to the bottom-right in a straight line.
        // Both scenes have a circle, so it is one all the way, and it shows the same square of
        // the camera. The screen is where it was in both scenes and does not change.
        var project = Plain(bench, Scene(0, StudioLayout.Bubble, anchor: StudioAnchor.TopLeft), Scene(2, StudioLayout.Bubble, morph: 1));
        var nothing = bench.Render(project, withScreen: false, withCamera: false);
        var atRest = bench.Render(project, time: 1, withCamera: false);
        foreach (var (time, amount, what) in new[] { (2.25, Quarter, "a round bubble a quarter of the way across"), (2.5, Half, "a round bubble halfway across") })
        {
            var camera = new Layer(Between(TopLeftBubble, Bubble, amount), BubbleRadius, Source: SquareOfCamera, Shape: StudioCameraShape.Circle);
            ExpectResolved(context, project, time, 1, StudioLayout.Bubble, new Layer(ScreenCard, CardRadius), camera, what);
            ExpectCamera(context, bench, project, time, camera, nothing, what);
            ExpectCameraOnTop(context, bench, project, time, camera, nothing, what, overTheScreen: true);
            RendererChecks.ExpectSame(context, bench.Render(project, time: time, withCamera: false), atRest, $"{what}: the screen, against the scene before");
        }

        // A squircle that moves and grows from 0.36 to 0.5 of the short side stays a squircle.
        // Its outline is made again for every size, and the renderer keeps only so many: 80
        // frames of the move go through more sizes than it keeps.
        var squircle = Plain(bench, Scene(0, StudioLayout.Bubble, anchor: StudioAnchor.TopLeft), Scene(2, StudioLayout.Bubble, morph: 1, size: 0.5)) with
        {
            Camera = new StudioCameraStyle { Shape = StudioCameraShape.Squircle, Mirror = false, Shadow = 0, BorderWidth = 0 },
        };
        var sizes = new HashSet<double>();
        for (var index = 0; index < 80; index++)
        {
            var time = 2 + ((index + 0.5) / 80);
            sizes.Add(Projects.Aligned(StudioLayoutResolver.Resolve(squircle, time, 1920, 1080).Camera!.Value.Rect).Width);
            bench.Render(squircle, time: time, withScreen: false);
        }

        context.Expect(sizes.Count > 32, $"the 80 frames went through only {sizes.Count} sizes of squircle, no more than the renderer keeps");
        var grown = new Layer(Between(TopLeftBubble, LargeBubble, Half), Between(BubbleRadius, 270, Half), Source: SquareOfCamera, Shape: StudioCameraShape.Squircle);
        ExpectResolved(context, squircle, 2.5, 1, StudioLayout.Bubble, new Layer(ScreenCard, CardRadius), grown, "a squircle halfway");
        ExpectCamera(context, bench, squircle, 2.5, grown, nothing, "a squircle halfway");
        context.Note($"a squircle drawn at {sizes.Count} sizes before the one measured");

        // A squircle that becomes a card moves as a rounded rectangle. A squircle reaches further
        // into the corners of its box than a circle, so the radius does not start at half its
        // side: it starts at 0.22 of it, 85.536 px, the rounded rectangle that reaches as far.
        var leaving = squircle with { Scenes = [Scene(0, StudioLayout.Bubble), Scene(2, StudioLayout.SideBySide, morph: 1)] };
        ExpectResolved(context, leaving, 2, 1, StudioLayout.SideBySide, new Layer(ScreenCard, CardRadius), new Layer(Bubble, 85.536, Source: SquareOfCamera), "a squircle as it starts to become a card");
        var onTheWay = Between(Bubble, SplitCamera, Half);
        var card = new Layer(onTheWay, Between(85.536, CardRadius, Half), Source: CameraSource(onTheWay));
        ExpectResolved(context, leaving, 2.5, 1, StudioLayout.SideBySide, new Layer(Between(ScreenCard, SplitScreen, Half), CardRadius), card, "a squircle halfway to a card");
        ExpectCamera(context, bench, leaving, 2.5, card, nothing, "a squircle halfway to a card");

        // So its outline does not jump as the move starts. Along the line from the bubble's
        // middle through a pixel, the squircle |x|^5 + |y|^5 = 1 ends at a known distance, and
        // no pixel more than 4 px from there changes by more than a level.
        var before = bench.Render(leaving, time: 1.999, withScreen: false);
        var starting = bench.Render(leaving, time: 2, withScreen: false);
        var box = ToBox(Bubble);
        var half = box.Width / 2.0;
        var changed = 0;
        var furthest = 0.0;
        var moved = 0;
        for (var y = 0; y < 1080; y++)
        {
            for (var x = 0; x < 1920; x++)
            {
                var difference = starting.Pixel(x, y).Distance(before.Pixel(x, y));
                if (difference <= 1)
                {
                    continue;
                }

                var dx = x + 0.5 - (box.Left + half);
                var dy = y + 0.5 - (box.Top + half);
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                var reach = Math.Pow(Math.Pow(Math.Abs(dx) / half, 5) + Math.Pow(Math.Abs(dy) / half, 5), 0.2);
                var fromOutline = reach > 0 ? Math.Abs(distance - (distance / reach)) : half;
                moved++;
                furthest = Math.Max(furthest, fromOutline);
                changed += fromOutline > 4 ? 1 : 0;
            }
        }

        context.Expect(changed == 0, string.Create(CultureInfo.InvariantCulture, $"as a squircle starts to become a card, {changed} pixels more than 4 px from its outline change; the furthest is {furthest:0.0} px from it"));
        context.Expect(moved > 0, "as a squircle starts to become a card not one pixel changes, so the camera is still drawn as a squircle");
        context.Note(string.Create(CultureInfo.InvariantCulture, $"as a squircle starts to become a card, {moved} pixels change, none more than {furthest:0.0} px from its outline"));
    }

    // One layer fades

    private static void ScreenFadesOut(CheckContext context, RenderBench bench)
    {
        // The camera layout has no screen. While it is entered the screen stays where the bubble
        // scene had it and fades, the camera grows from the bubble into its card over it, and the
        // frame already says "camera" for its layout.
        var project = Plain(bench, Scene(0, StudioLayout.Bubble), Scene(2, StudioLayout.Camera, morph: 1));
        var nothing = bench.Render(project, withScreen: false, withCamera: false);
        foreach (var (time, amount, what) in new[]
        {
            (2.25, Quarter, "a quarter of the way"),
            (2.5, Half, "halfway"),
            (2.75, ThreeQuarters, "three quarters of the way"),
        })
        {
            var screen = new Layer(ScreenCard, CardRadius, Opacity: 1 - amount);
            var cameraRect = Between(Bubble, CameraCard, amount);
            var camera = new Layer(cameraRect, Between(BubbleRadius, CardRadius, amount), Source: CameraSource(cameraRect));
            ExpectResolved(context, project, time, 1, StudioLayout.Camera, screen, camera, what);
            ExpectScreen(context, bench, project, time, screen, nothing, what);
            ExpectCamera(context, bench, project, time, camera, nothing, what);
            ExpectCameraOnTop(context, bench, project, time, camera, nothing, what, overTheScreen: true);
        }

        // By hand, in the middle of the card, where the screen shows its plain (30, 50, 100)
        // over a canvas of (200, 100, 50): 27/32, a half and 5/32 of the way from the canvas to it.
        foreach (var (time, want, what) in new[]
        {
            (2.25, new Rgb(56.5625, 57.8125, 92.1875), "a quarter of the way, the screen at 0.84375"),
            (2.5, new Rgb(115, 75, 75), "halfway, the screen at 0.5"),
            (2.75, new Rgb(173.4375, 92.1875, 57.8125), "three quarters of the way, the screen at 0.15625"),
        })
        {
            RendererChecks.ExpectPixel(context, bench.Render(project, time: time, withCamera: false), 960, 540, want, 1, $"{what}: the middle of its card");
        }

        // As the move starts the screen is still whole, and the instant it ends there is none.
        RendererChecks.ExpectSame(context, bench.Render(project, time: 2, withCamera: false), bench.Render(Plain(bench, Scene(0, StudioLayout.Bubble)), time: 2, withCamera: false), "as the move starts, the screen against the bubble scene's");
        ExpectResolved(context, project, 3, 1, StudioLayout.Camera, null, new Layer(CameraCard, CardRadius, Source: CameraSource(CameraCard)), "the instant the move ends");
        RendererChecks.ExpectSame(context, bench.Render(project, time: 3), bench.Render(Plain(bench, Scene(0, StudioLayout.Camera)), time: 3), "the instant the move ends, against the camera scene by itself");
    }

    private static void CameraFadesIn(CheckContext context, RenderBench bench)
    {
        // The screen scene has no camera. While the bubble scene is entered the camera is where
        // that scene puts it, a circle, and fades in over the screen and the canvas. Here it is
        // mirrored, as a new project has it.
        var project = Plain(bench, Scene(0, StudioLayout.Screen), Scene(2, StudioLayout.Bubble, morph: 1)) with
        {
            Camera = new StudioCameraStyle { Shape = StudioCameraShape.Circle, Mirror = true, Shadow = 0, BorderWidth = 0 },
        };
        var spec = bench.CameraClip.Spec;
        var box = ToBox(Bubble);
        var map = Projects.CameraMap(new StudioResolvedCamera(Bubble, new StudioFrameRect(SquareOfCamera.X, SquareOfCamera.Y, SquareOfCamera.Width, SquareOfCamera.Height), StudioCameraShape.Circle, BubbleRadius, true, 0, default, 0, true), spec);
        foreach (var (time, amount, what) in new[]
        {
            (2.25, Quarter, "a quarter of the way"),
            (2.5, Half, "halfway"),
            (2.75, ThreeQuarters, "three quarters of the way"),
        })
        {
            var camera = new Layer(Bubble, BubbleRadius, Opacity: amount, Source: SquareOfCamera, Shape: StudioCameraShape.Circle);
            ExpectResolved(context, project, time, 1, StudioLayout.Bubble, new Layer(ScreenCard, CardRadius), camera, what);

            // With the fade undone against the frame that has no camera, the bubble is the
            // camera's picture, whole: its strip, its patches, and an edge inside it.
            var seen = bench.Render(project, time: time);
            var under = bench.Render(project, time: time, withCamera: false);
            var whole = Unfade(seen, under, amount);
            RendererChecks.ExpectContent(context, $"{what}: the camera, with the fade undone,", whole, spec, map, RenderBench.CameraNumber, ColorTolerance(amount));
            ExpectRoundOutline(context, whole, under, StudioCameraShape.Circle, box, $"{what}: the camera");
            ZoomChecks.ExpectSameOutside(context, seen, under, box, $"{what}: the frame against the one with no camera");
            ZoomChecks.Keep(context, seen, $"{what}: the frame");
        }

        // By hand, halfway, at a point where the camera shows its plain (30, 90, 50) over the
        // screen's plain (30, 50, 100): (30, 70, 75).
        var (x, y) = map.Apply(spec.BackgroundPoint.X, spec.BackgroundPoint.Y);
        var half = bench.Render(project, time: 2.5);
        var without = bench.Render(project, time: 2.5, withCamera: false);
        context.Expect(without.Pixel((int)x, (int)y) == ScreenBackground, $"without the camera, ({x:0}, {y:0}) is {without.Pixel((int)x, (int)y)}, and the check takes it for the screen's {ScreenBackground}");
        context.Expect(RendererChecks.ClipColorAt(spec, map, RenderBench.CameraNumber, x, y) == CameraBackground, $"the camera does not show its plain background at ({x:0}, {y:0})");
        RendererChecks.ExpectPixel(context, half, x, y, new Rgb(30, 70, 75), 1, "halfway, the camera's background over the screen's");

        // As the move starts the camera is in the layout and wholly see-through: nothing of it is drawn.
        ExpectResolved(context, project, 2, 1, StudioLayout.Bubble, new Layer(ScreenCard, CardRadius), new Layer(Bubble, BubbleRadius, Opacity: 0, Source: SquareOfCamera, Shape: StudioCameraShape.Circle), "as the move starts");
        RendererChecks.ExpectSame(context, bench.Render(project, time: 2), bench.Render(project, time: 2, withCamera: false), "as the move starts, against the frame with no camera");
        RendererChecks.ExpectSame(context, bench.Render(project, time: 3), bench.Render(project with { Scenes = [Scene(0, StudioLayout.Bubble)] }, time: 3), "the instant the move ends, against the bubble scene by itself");
    }

    private static void NothingInCommon(CheckContext context, RenderBench bench)
    {
        // From the camera by itself to the screen by itself: the screen fades in under the
        // camera's card, which fades out. The frame says "screen" for its layout and still has
        // a camera.
        var project = Plain(bench, Scene(0, StudioLayout.Camera), Scene(2, StudioLayout.Screen, morph: 1));
        var nothing = bench.Render(project, withScreen: false, withCamera: false);
        var source = CameraSource(CameraCard);
        var cameraBox = ToBox(CameraCard);
        var cameraSpec = bench.CameraClip.Spec;
        foreach (var (time, amount, what) in new[]
        {
            (2.25, Quarter, "a quarter of the way"),
            (2.5, Half, "halfway"),
            (2.75, ThreeQuarters, "three quarters of the way"),
        })
        {
            var screen = new Layer(ScreenCard, CardRadius, Opacity: amount);
            var camera = new Layer(CameraCard, CardRadius, Opacity: 1 - amount, Source: source);
            ExpectResolved(context, project, time, 1, StudioLayout.Screen, screen, camera, what);
            ExpectScreen(context, bench, project, time, screen, nothing, what);
            ExpectCamera(context, bench, project, time, camera, nothing, what);

            // Together: with the camera's fade undone against the frame that has only the
            // screen, the camera's card is whole. Both frames are rounded to whole levels, so
            // the colours are held a little less tightly.
            var both = Unfade(bench.Render(project, time: time), bench.Render(project, time: time, withCamera: false), 1 - amount);
            ZoomChecks.ExpectWindow(context, both, cameraSpec, RenderBench.CameraNumber, cameraBox, source, DrawnTolerance, 1 + (2 / (1 - amount)), $"{what}: the camera over the screen");
        }

        // By hand, halfway, at the screen's plain-background point, where the camera shows its
        // plain background too: the screen's (30, 50, 100) half over the canvas's (200, 100, 50)
        // is (115, 75, 75), and the camera's (30, 90, 50) half over that is (72.5, 82.5, 62.5).
        var screenSpec = bench.ScreenClip.Spec;
        var (x, y) = ZoomChecks.Map(ToBox(ScreenCard), screenSpec, Window.Whole).Apply(screenSpec.BackgroundPoint.X, screenSpec.BackgroundPoint.Y);
        context.Expect(RendererChecks.ClipColorAt(cameraSpec, ZoomChecks.Map(cameraBox, cameraSpec, source), RenderBench.CameraNumber, x, y) == CameraBackground, $"the camera does not show its plain background at ({x:0}, {y:0})");
        RendererChecks.ExpectPixel(context, bench.Render(project, time: 2.5), x, y, new Rgb(72.5, 82.5, 62.5), 1, "halfway, the camera's background over the screen's over the canvas");

        RendererChecks.ExpectSame(context, bench.Render(project, time: 2), bench.Render(Plain(bench, Scene(0, StudioLayout.Camera)), time: 2), "as the move starts, against the camera scene by itself");
        ExpectResolved(context, project, 3, 1, StudioLayout.Screen, new Layer(ScreenCard, CardRadius), null, "the instant the move ends");
        RendererChecks.ExpectSame(context, bench.Render(project, time: 3), bench.Render(Plain(bench, Scene(0, StudioLayout.Screen)), time: 3), "the instant the move ends, against the screen scene by itself");
    }

    private static void Parts(CheckContext context, RenderBench bench)
    {
        // Section 6.7: a layer that fades is put together first, with its shadow, its border and
        // its click rings, and the whole is then laid on the frame at the layer's opacity.

        // The screen's shadow, at full strength, while the screen fades out. Outside the card
        // only the shadow is drawn, so halfway each pixel there is half as far from the canvas
        // as with the screen whole.
        var shadowed = Plain(bench, Scene(0, StudioLayout.Bubble), Scene(2, StudioLayout.Camera, morph: 1)) with { Screen = new StudioScreenStyle { Shadow = 1 } };
        var whole = bench.Render(shadowed, time: 1, withCamera: false);
        var half = bench.Render(shadowed, time: 2.5, withCamera: false);
        var card = ToBox(ScreenCard);
        var darkest = ExpectShadow(context, half, whole, null, card, Half, "the screen's shadow, halfway");
        context.Expect(darkest >= 30, $"next to the whole screen its shadow darkens the canvas by only {darkest:0} of 255, too little for the check to mean anything");

        // The card covers its own shadow, so in the middle of the card the shadow plays no part:
        // the screen's (30, 50, 100) half over the canvas's (200, 100, 50) is (115, 75, 75), as
        // with no shadow at all. Had the shadow faded by itself under a card that fades, the
        // canvas there would be a quarter darker, (150, 75, 37.5), and the pixel (90, 62.5, 68.75).
        RendererChecks.ExpectPixel(context, half, 960, 540, new Rgb(115, 75, 75), 1.5, "halfway, the middle of a card with a shadow under it");
        ZoomChecks.Keep(context, half, "the screen and its shadow, halfway");

        // The camera's shadow while the camera fades in over the screen: outside the bubble each
        // pixel is, a quarter of the way through, 0.15625 of the way from the frame with no
        // camera to the frame with the camera whole. Inside it, at the point where the camera
        // shows its plain (30, 90, 50), the pixel is that far from the frame with no camera to
        // that colour: the bubble does not show its own shadow through itself either.
        var bubbleShadow = Plain(bench, Scene(0, StudioLayout.Screen), Scene(2, StudioLayout.Bubble, morph: 1)) with
        {
            Camera = new StudioCameraStyle { Shape = StudioCameraShape.Circle, Mirror = false, Shadow = 1, BorderWidth = 0 },
        };
        var bubble = ToBox(Bubble);
        var bubbleSpec = bench.CameraClip.Spec;
        var bubbleMap = ZoomChecks.Map(bubble, bubbleSpec, SquareOfCamera);
        var (plainX, plainY) = bubbleMap.Apply(bubbleSpec.BackgroundPoint.X, bubbleSpec.BackgroundPoint.Y);
        context.Expect(RendererChecks.ClipColorAt(bubbleSpec, bubbleMap, RenderBench.CameraNumber, plainX, plainY) == CameraBackground, $"the camera does not show its plain background at ({plainX:0}, {plainY:0})");
        foreach (var (time, amount, what) in new[] { (2.25, Quarter, "the camera's shadow, a quarter of the way"), (2.5, Half, "the camera's shadow, halfway") })
        {
            var seen = bench.Render(bubbleShadow, time: time);
            var noBubble = bench.Render(bubbleShadow, time: time, withCamera: false);
            var deepest = ExpectShadow(context, seen, bench.Render(bubbleShadow, time: 3.5), noBubble, bubble, amount, what);
            context.Expect(deepest >= 30, $"{what}: next to the whole camera its shadow changes the frame by only {deepest:0} of 255");
            RendererChecks.ExpectPixel(context, seen, plainX, plainY, noBubble.Pixel((int)plainX, (int)plainY).Mix(CameraBackground, amount), 1.5, $"{what}: inside the bubble, the camera's background over what is under it");
        }

        // The border. A rectangle bubble, not mirrored, shows the camera's darker left third,
        // (16, 54, 28), along its left edge, over the screen's (30, 50, 100). The border's
        // (255, 128, 0), 10.8 px wide, covers the camera there, so halfway it is half over the
        // screen: (142.5, 89, 50). Past it the camera is half over the screen: (23, 52, 64). Had
        // the border faded by itself over a camera that fades, it would be (139, 90, 32).
        var bordered = Plain(bench, Scene(0, StudioLayout.Screen), Scene(2, StudioLayout.Bubble, morph: 1)) with
        {
            Camera = new StudioCameraStyle { Shape = StudioCameraShape.Rectangle, Mirror = false, Shadow = 0, BorderWidth = 0.01, BorderColor = "#FF8000" },
        };

        // 388.8 px tall and 16:9: 691.2 px wide, 32.4 px from the right and bottom edges.
        var rectangle = new StudioFrameRect(1196.4, 658.8, 691.2, 388.8);
        ExpectResolved(context, bordered, 2.5, 1, StudioLayout.Bubble, new Layer(ScreenCard, CardRadius), new Layer(rectangle, 0, Opacity: Half, Source: Window.Whole, Shape: StudioCameraShape.Rectangle), "a rectangle bubble with a border, halfway");
        var edge = ToBox(rectangle);
        var row = (edge.Top + edge.Bottom) / 2;
        var withBorder = bench.Render(bordered, time: 2.5);
        var noCamera = bench.Render(bordered, time: 2.5, withCamera: false);
        var cameraMap = ZoomChecks.Map(edge, bench.CameraClip.Spec, Window.Whole);
        foreach (var inside in new[] { 1, 5, 9, 13 })
        {
            var x = edge.Left + inside;
            context.Expect(noCamera.Pixel(x, row) == ScreenBackground, $"without the camera, ({x}, {row}) is {noCamera.Pixel(x, row)}, and the check takes it for the screen's {ScreenBackground}");
            context.Expect(RendererChecks.ClipColorAt(bench.CameraClip.Spec, cameraMap, RenderBench.CameraNumber, x, row) == CameraLeftBackground, $"the camera does not show its darker third at ({x}, {row})");
        }

        var cameraOverScreen = new Rgb(23, 52, 64);
        var borderOverScreen = new Rgb(142.5, 89, 50);
        RendererChecks.ExpectPixel(context, withBorder, edge.Left + 1, row, borderOverScreen, 1.5, "halfway, 1 px inside the left edge: the border over the screen");
        RendererChecks.ExpectPixel(context, withBorder, edge.Left + 5, row, borderOverScreen, 1.5, "halfway, 5 px inside the left edge");
        RendererChecks.ExpectPixel(context, withBorder, edge.Left + 9, row, borderOverScreen, 1.5, "halfway, 9 px inside the left edge");
        RendererChecks.ExpectPixel(context, withBorder, edge.Left + 13, row, cameraOverScreen, 1.5, "halfway, 13 px inside the left edge, past the border");
        RendererChecks.ExpectPixel(context, withBorder, edge.Left - 1, row, ScreenBackground, 0, "halfway, 1 px outside the left edge");
        ZoomChecks.Keep(context, withBorder, "a border, halfway");

        // Click rings. A click the instant the screen is halfway gone. With the fade undone the
        // ring is where and as large as on the whole screen, 176 px across and 17.6 px wide
        // around (537.6, 302.4), and laid on the screen at its own 0.85.
        var clicked = Plain(bench, Scene(0, StudioLayout.Bubble), Scene(2, StudioLayout.Camera, morph: 1)) with
        {
            Overlays = new StudioOverlays { Clicks = new StudioClickOverlay { Enabled = true, Color = "#0A84FF", Size = 200, StrokeWidth = 20, Opacity = 0.85, Duration = 0.45 }, Branding = false },
        };
        var click = Projects.Events(bench.ScreenClip) with { Clicks = [new StudioClickEvent { T = 2.5, X = 0.25, Y = 0.25 }] };
        var canvas = bench.Render(clicked, withScreen: false, withCamera: false);
        var plain = bench.Render(clicked, time: 2.5, withCamera: false);
        var ringed = bench.Render(clicked, time: 2.5, withCamera: false, events: click);
        RendererChecks.ExpectRing(context, Unfade(ringed, canvas, Half), Unfade(plain, canvas, Half), 537.6, 302.4, 88, 17.6, 0.85, "a click on a screen that is halfway gone, with the fade undone");

        // By hand, on the ring to the right of the click, where the screen shows its darker
        // (20, 34, 88): the ring's (10, 132, 255) at 0.85 over that is (11.5, 117.3, 229.95), and
        // half of that over the canvas's (200, 100, 50) is (105.75, 108.65, 139.975). A ring
        // faded by itself over a screen that fades would give (67.5, 94.625, 148.05).
        var screenMap = ZoomChecks.Map(card, bench.ScreenClip.Spec, Window.Whole);
        context.Expect(RendererChecks.ClipColorAt(bench.ScreenClip.Spec, screenMap, RenderBench.ScreenNumber, 625, 302) == new Rgb(20, 34, 88), "the screen does not show its darker third at (625, 302)");
        RendererChecks.ExpectPixel(context, plain, 625, 302, new Rgb(110, 67, 69), 1, "halfway, to the right of the click, with no click");
        RendererChecks.ExpectPixel(context, ringed, 625, 302, new Rgb(105.75, 108.65, 139.975), 1.5, "halfway, on the ring to the right of the click");
        ZoomChecks.Keep(context, ringed, "a click ring, halfway");
    }

    /// <summary>
    /// Next to a layer, where only its shadow reaches: each pixel is <paramref name="amount"/>
    /// of the way from the frame without the layer (the plain canvas when there is no such
    /// frame) to the frame with the layer whole. Returns how much the whole layer's shadow
    /// changes the darkest of the pixels looked at.
    /// </summary>
    private static double ExpectShadow(CheckContext context, Picture picture, Picture whole, Picture? without, Box box, double amount, string what)
    {
        var middleX = (box.Left + box.Right) / 2;
        var middleY = (box.Top + box.Bottom) / 2;
        var deepest = 0.0;
        var looked = 0;
        foreach (var step in new[] { 1, 13, 25, 40 })
        {
            foreach (var (x, y) in new[] { (middleX, box.Bottom + step - 1), (middleX, box.Top - step), (box.Left - step, middleY), (box.Right + step - 1, middleY) })
            {
                if (x < 0 || y < 0 || x >= picture.Width || y >= picture.Height)
                {
                    continue;
                }

                looked++;
                var under = without?.Pixel(x, y) ?? Canvas;
                var full = whole.Pixel(x, y);
                deepest = Math.Max(deepest, full.Distance(under));
                RendererChecks.ExpectPixel(context, picture, x, y, under.Mix(full, amount), 1.5, $"{what}, {step} px outside the layer at ({x}, {y}), where it is {under} without the layer and {full} with it whole");
            }
        }

        context.Expect(looked >= 12, $"{what}: only {looked} points next to the layer are on the canvas");
        return deepest;
    }

    // A zoom and click rings on a card that moves

    private static void ZoomAndRings(CheckContext context, RenderBench bench)
    {
        // A zoom at scale 2 on (0.25, 0.25) shows the top-left quarter of the screen for the
        // whole clip. Halfway through the move the card is halfway between its two places and
        // still shows that quarter.
        var project = Plain(bench, Scene(0, StudioLayout.Bubble), Scene(2, StudioLayout.SideBySide, morph: 1)) with
        {
            Zooms = [new StudioZoom { Start = 0, End = 6, Scale = 2, Focus = new StudioZoomFocus { X = 0.25, Y = 0.25 }, EaseIn = 0, EaseOut = 0 }],
            Overlays = new StudioOverlays { Clicks = new StudioClickOverlay { Enabled = true, Color = "#0A84FF", Size = 100, StrokeWidth = 10, Opacity = 0.85, Duration = 0.45 }, Branding = false },
        };
        var nothing = bench.Render(project, withScreen: false, withCamera: false);
        var quarter = new Window(0, 0, 0.5, 0.5);
        var card = Between(ScreenCard, SplitScreen, Half);
        var screen = new Layer(card, CardRadius, Source: quarter);
        var cameraRect = Between(Bubble, SplitCamera, Half);
        ExpectResolved(context, project, 2.5, 1, StudioLayout.SideBySide, screen, new Layer(cameraRect, Between(BubbleRadius, CardRadius, Half), Source: CameraSource(cameraRect)), "halfway, zoomed");
        ExpectScreen(context, bench, project, 2.5, screen, nothing, "halfway, zoomed");

        // The card is 1463.88 px wide there and holds 960 screen pixels: 1.524875 px each. A
        // 100-point ring with a 10-point stroke is 152.4875 px across and 15.24875 px wide, and
        // a click at (0.25, 0.25), the middle of the window, is in the middle of the card:
        // (90 + 731.94, 128.28375 + 411.71625).
        var click = Projects.Events(bench.ScreenClip) with { Clicks = [new StudioClickEvent { T = 2.5, X = 0.25, Y = 0.25 }] };
        var plain = bench.Render(project, time: 2.5, withCamera: false);
        var ringed = bench.Render(project, time: 2.5, withCamera: false, events: click);
        RendererChecks.ExpectRing(context, ringed, plain, 821.94, 540, 76.24375, 15.24875, 0.85, "a click on a card halfway through its move");
        ZoomChecks.Keep(context, ringed, "a click ring on a moving card");
    }

    private static void DrawTime(CheckContext context, RenderBench bench)
    {
        // The look a new project has: a gradient, both shadows and a border. While both cards
        // move, the canvas under the screen and both shadows are made again for every frame.
        // While the screen fades it is put together by itself before it is laid on the frame.
        var software = bench.Graphics.IsSoftware;
        var frames = software ? 12 : 120;
        var project = Projects.Create(bench.ScreenClip, bench.CameraClip, cameraOffset: 0) with
        {
            Scenes = [Scene(0, StudioLayout.Bubble), Scene(2, StudioLayout.SideBySide, morph: 2)],
        };
        var fading = project with { Scenes = [Scene(0, StudioLayout.Bubble), Scene(2, StudioLayout.Camera, morph: 2)] };
        var atRest = Time(bench, project, frames, _ => 1);
        var moving = Time(bench, project, frames, frame => 2 + (2 * (frame + 0.5) / frames));
        var faded = Time(bench, fading, frames, frame => 2 + (2 * (frame + 0.5) / frames));
        context.Measure(string.Create(CultureInfo.InvariantCulture, $"one 1920×1080 frame with shadows, best of three runs of {frames} on {bench.Graphics.AdapterName}: {atRest:0.00} ms at rest, {moving:0.00} ms while both cards move, and {faded:0.00} ms while the screen fades under a camera that grows"));
    }

    private static double Time(RenderBench bench, StudioProject project, int frames, Func<int, double> timeOf)
    {
        var target = bench.Target(1920, 1080);
        var events = Projects.Events(bench.ScreenClip);
        StudioRenderRequest At(double time) => new(project, events, bench.EmptyFolder, time, 1920, 1080, bench.ScreenFrame, bench.CameraFrame, target, null, StudioRenderQuality.Preview);
        var best = double.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            lock (bench.Graphics.Gate)
            {
                // Once to warm up, and a read to make sure the device has finished before the clock starts.
                bench.Renderer.Render(At(0.5));
                bench.Graphics.ReadTexture(target);
                var watch = Stopwatch.StartNew();
                for (var frame = 0; frame < frames; frame++)
                {
                    bench.Renderer.Render(At(timeOf(frame)));
                }

                bench.Graphics.ReadTexture(target);
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds / frames);
            }
        }

        return best;
    }

    // Exports

    /// <summary>
    /// The bubble scene, then from 2 s a second of moving into the side-by-side scene. An export
    /// draws frame n as the video is at the middle of that frame, (n + 0.5) / 30 s.
    /// </summary>
    private static async Task Export(CheckContext context, Harness harness)
    {
        var screenClip = harness.Clips.Screen;
        var cameraClip = harness.Clips.Camera;
        var project = ForExport(screenClip, cameraClip, Scene(0, StudioLayout.Bubble), Scene(2, StudioLayout.SideBySide, morph: 1));
        var (path, result) = await ExportTo(context, project, screenClip, cameraClip).ConfigureAwait(false);
        context.Expect(result.Width == 1920 && result.Height == 1080 && result.FrameCount == screenClip.Frames, $"the export is {result.Width}×{result.Height} with {result.FrameCount} frames, want 1920×1080 with {screenClip.Frames}");

        // Every frame, with the layout resolver saying where the layers are: the screen's strip
        // and the camera's both read the frame's own number all the way through the move.
        ExportVerifier.Verify(context, path, new ExportExpectation(project, screenClip, cameraClip, 1920, 1080, 30, 1, ExpectAudio: false));

        // By hand. Frames 67 and 82 have their middles at 2.25 s and 2.75 s, a quarter and three
        // quarters of the way through the second. Frame 59 is the last whose middle is before
        // the scene, and frame 90 the first whose middle is after the move.
        var frames = ZoomChecks.Frames(path, 59, 67, 82, 90);
        foreach (var (index, amount, what) in new[]
        {
            (59, 0.0, "frame 59, the last before the scene"),
            (67, Quarter, "frame 67, a quarter of the way"),
            (82, ThreeQuarters, "frame 82, three quarters of the way"),
            (90, 1.0, "frame 90, the first after the move"),
        })
        {
            // In the bubble scene at rest the bubble lies over one of the places the screen's
            // colours are read at, so there only the camera is measured.
            if (index != 59)
            {
                ZoomChecks.ExpectWindow(context, frames[index], screenClip.Spec, index, ToBox(Between(ScreenCard, SplitScreen, amount)), Window.Whole, EncodedTolerance, ExportVerifier.ColorTolerance, $"{what}: the screen");
            }

            var camera = Between(Bubble, SplitCamera, amount);
            ZoomChecks.ExpectWindow(context, frames[index], cameraClip.Spec, index, ToBox(camera), CameraSource(camera), EncodedTolerance, ExportVerifier.ColorTolerance, $"{what}: the camera");
        }
    }

    /// <summary>
    /// The bubble scene, then from 2 s a second in which the screen scene is entered: the frames
    /// say "screen" for their layout and still have a camera, fading out.
    /// </summary>
    private static async Task FadeExport(CheckContext context, Harness harness)
    {
        var screenClip = harness.Clips.Screen;
        var cameraClip = harness.Clips.Camera;
        var project = ForExport(screenClip, cameraClip, Scene(0, StudioLayout.Bubble), Scene(2, StudioLayout.Screen, morph: 1));
        var (path, result) = await ExportTo(context, project, screenClip, cameraClip).ConfigureAwait(false);
        context.Expect(result.FrameCount == screenClip.Frames, $"the export has {result.FrameCount} frames, want {screenClip.Frames}");

        // The camera's four colour patches lie over the screen's plain (30, 50, 100), and its
        // plain-background point over the canvas, right of the screen's card. Each is the
        // camera's colour seen through at the frame's opacity: whole at frame 59, 0.84375 and
        // 0.15625 at frames 67 and 82, whose middles are a quarter and three quarters of the way
        // through the second, and gone at frame 90.
        var spec = cameraClip.Spec;
        var map = ZoomChecks.Map(ToBox(Bubble), spec, SquareOfCamera);
        var frames = ZoomChecks.Frames(path, 59, 67, 82, 90);
        var worst = 0.0;
        foreach (var (index, opacity, what) in new[]
        {
            (59, 1.0, "frame 59, before the scene"),
            (67, 1 - Quarter, "frame 67, a quarter of the way"),
            (82, 1 - ThreeQuarters, "frame 82, three quarters of the way"),
            (90, 0.0, "frame 90, after the move"),
        })
        {
            var picture = frames[index];
            for (var patch = 0; patch < ClipSpec.PatchColors.Length; patch++)
            {
                var (x, y) = map.Apply(spec.PatchCenter(patch).X, spec.PatchCenter(patch).Y);
                var got = picture.Average(x, y, 3);
                var want = ScreenBackground.Mix(ClipSpec.PatchColors[patch], opacity);
                worst = Math.Max(worst, got.Distance(want));
                context.Expect(got.Distance(want) <= ExportVerifier.ColorTolerance, $"{what}: the camera's patch {patch}, at ({x:0}, {y:0}), is {got}, want {want}");
            }

            var (bx, by) = map.Apply(spec.BackgroundPoint.X, spec.BackgroundPoint.Y);
            var background = picture.Average(bx, by, 3);
            var wantBackground = Canvas.Mix(CameraBackground, opacity);
            worst = Math.Max(worst, background.Distance(wantBackground));
            context.Expect(background.Distance(wantBackground) <= ExportVerifier.ColorTolerance, $"{what}: the camera's background, at ({bx:0}, {by:0}), is {background}, want {wantBackground}");

            // The strip reads once its cells are far enough apart in brightness: not at 0.15625.
            var read = FrameCode.Decode(picture, spec, map);
            var wantRead = opacity >= 0.5 ? index : FrameCode.Unreadable;
            context.Expect(read == wantRead, $"{what}: the camera's strip reads {read}, want {wantRead}");
            ZoomChecks.Keep(context, picture, what);
        }

        context.Note(string.Create(CultureInfo.InvariantCulture, $"the camera's colours, seen through at 1, 0.84375, 0.15625 and 0, within {worst:0.0} of 255 of the mix worked out by hand"));
    }

    /// <summary>A poster made for a moment inside a move is the frame the export shows then.</summary>
    private static async Task Poster(CheckContext context, Harness harness)
    {
        // The poster for 2.25 s is frame 67, a quarter of the way through the move. At 640×360
        // every length of the layout is a third of what it is at 1920×1080.
        var screenClip = harness.Clips.Screen;
        var cameraClip = harness.Clips.Camera;
        var project = ForExport(screenClip, cameraClip, Scene(0, StudioLayout.Bubble), Scene(2, StudioLayout.SideBySide, morph: 1));
        var folder = context.Folder();
        var path = Path.Combine(folder, "poster-67.jpg");
        await new StudioExporter().WritePosterAsync(project, Projects.Events(screenClip), Projects.Paths(folder, screenClip, cameraClip), path, 640, 2.25).ConfigureAwait(false);
        var picture = Images.Load(path);
        if (!context.Expect(picture.Width == 640 && picture.Height == 360, $"the poster is {picture.Width}×{picture.Height}, want 640×360"))
        {
            return;
        }

        // A JPEG smears colour a little at this size.
        ZoomChecks.ExpectWindow(context, picture, screenClip.Spec, 67, ToBox(Third(Between(ScreenCard, SplitScreen, Quarter))), Window.Whole, EncodedTolerance, 10, "the screen");

        // The camera is too small there for its edges to be measured: its strip is read.
        var camera = Between(Bubble, SplitCamera, Quarter);
        var read = FrameCode.Decode(picture, cameraClip.Spec, ZoomChecks.Map(ToBox(Third(camera)), cameraClip.Spec, CameraSource(camera)));
        context.Expect(read == 67, $"the camera's strip reads {read}, want 67");
    }

    private static StudioFrameRect Third(StudioFrameRect rect) => new(rect.X / 3, rect.Y / 3, rect.Width / 3, rect.Height / 3);

    private static async Task<(string Path, StudioExportResult Result)> ExportTo(CheckContext context, StudioProject project, TestClip screen, TestClip camera)
    {
        var folder = context.Folder();
        var output = Path.Combine(folder, "scenes.mp4");
        var result = await new StudioExporter().ExportAsync(project, Projects.Events(screen), Projects.Paths(folder, screen, camera), output, new StudioExportOptions()).ConfigureAwait(false);
        context.Note($"{result.EncoderDescription}{(result.SoftwareRendering ? ", drawn by WARP" : string.Empty)}, {result.FrameCount} frames at {result.Width}×{result.Height}");
        return (output, result);
    }

    // What the checks share

    /// <summary>A layer as the format should resolve it: worked out by hand.</summary>
    /// <param name="Source">The part of its picture the layer shows; the whole of it when null.</param>
    /// <param name="Shape">For the camera. A screen is always a rounded rectangle.</param>
    private readonly record struct Layer(StudioFrameRect Rect, double Radius, double Opacity = 1, Window? Source = null, StudioCameraShape Shape = StudioCameraShape.RoundedRectangle);

    private static Box ToBox(StudioFrameRect rect)
    {
        var aligned = Projects.Aligned(rect);
        return new Box((int)aligned.X, (int)aligned.Y, (int)(aligned.X + aligned.Width), (int)(aligned.Y + aligned.Height));
    }

    /// <summary>A colour may be this far off: one level, and with a fade undone the levels a rounded pixel is then worth.</summary>
    private static double ColorTolerance(double opacity) => opacity < 1 ? 1 + (1 / opacity) : 1;

    /// <summary>The layout resolver gives the scene, the layout and the two layers worked out by hand.</summary>
    private static void ExpectResolved(CheckContext context, StudioProject project, double time, int sceneIndex, StudioLayout layout, Layer? screen, Layer? camera, string what)
    {
        var frame = StudioLayoutResolver.Resolve(project, time, 1920, 1080);
        context.Expect(frame.SceneIndex == sceneIndex && frame.Layout == layout, $"{what}: the layout resolver says scene {frame.SceneIndex} and {frame.Layout}, want scene {sceneIndex} and {layout}");
        ExpectLayer(context, $"{what}: the screen", screen, frame.Screen is { } s ? new Layer(s.Rect, s.CornerRadius, s.Opacity, new Window(s.Source.X, s.Source.Y, s.Source.Width, s.Source.Height)) : null);
        ExpectLayer(context, $"{what}: the camera", camera, frame.Camera is { } c ? new Layer(c.Rect, c.CornerRadius, c.Opacity, new Window(c.Source.X, c.Source.Y, c.Source.Width, c.Source.Height), c.Shape) : null);
        context.Expect(frame.Camera is not { Visible: false }, $"{what}: the camera is not showing");
    }

    private static void ExpectLayer(CheckContext context, string what, Layer? want, Layer? got)
    {
        if (want is not { } wanted || got is not { } given)
        {
            context.Expect(want is null == got is null, $"{what} is {(got is null ? "not in the resolved frame" : "in the resolved frame")}, and should {(want is null ? "not be" : "be")}");
            return;
        }

        var wantSource = wanted.Source ?? Window.Whole;
        var gotSource = given.Source ?? Window.Whole;
        var off = new[]
        {
            given.Rect.X - wanted.Rect.X, given.Rect.Y - wanted.Rect.Y, given.Rect.Width - wanted.Rect.Width, given.Rect.Height - wanted.Rect.Height,
            given.Radius - wanted.Radius, given.Opacity - wanted.Opacity,
            gotSource.X - wantSource.X, gotSource.Y - wantSource.Y, gotSource.Width - wantSource.Width, gotSource.Height - wantSource.Height,
        }.Max(Math.Abs);
        context.Expect(off < 1e-9 && given.Shape == wanted.Shape, $"{what} resolves to {Describe(given)}, worked out by hand as {Describe(wanted)}");
    }

    private static string Describe(Layer layer) =>
        string.Create(CultureInfo.InvariantCulture, $"({layer.Rect.X:0.####}, {layer.Rect.Y:0.####}) {layer.Rect.Width:0.####}×{layer.Rect.Height:0.####}, {layer.Shape} of radius {layer.Radius:0.####}, opacity {layer.Opacity:0.#####}, showing {layer.Source ?? Window.Whole}");

    /// <summary>
    /// The screen of a frame drawn without the camera: its pattern is in the card, measured from
    /// four edges, and the card's outline is where the card's box is. A screen that is fading is
    /// looked at with the fade undone against the plain canvas.
    /// </summary>
    private static void ExpectScreen(CheckContext context, RenderBench bench, StudioProject project, double time, Layer layer, Picture nothing, string what)
    {
        var seen = bench.Render(project, time: time, withCamera: false);
        var picture = layer.Opacity < 1 ? Unfade(seen, nothing, layer.Opacity) : seen;
        var box = ToBox(layer.Rect);
        var spec = bench.ScreenClip.Spec;
        ZoomChecks.ExpectWindow(context, picture, spec, RenderBench.ScreenNumber, box, layer.Source ?? Window.Whole, DrawnTolerance, ColorTolerance(layer.Opacity), $"{what}: the screen");
        ExpectOutline(context, picture, nothing, layer, box, spec, RenderBench.ScreenNumber, $"{what}: the screen");
        ZoomChecks.ExpectSameOutside(context, seen, nothing, box, $"{what}: the screen's frame against the plain canvas");
        if (layer.Opacity < 1)
        {
            ZoomChecks.Keep(context, seen, $"{what}: the screen as drawn");
        }
    }

    /// <summary>The same for the camera, in a frame drawn without the screen. The camera must not be mirrored.</summary>
    private static void ExpectCamera(CheckContext context, RenderBench bench, StudioProject project, double time, Layer layer, Picture nothing, string what)
    {
        var seen = bench.Render(project, time: time, withScreen: false);
        var picture = layer.Opacity < 1 ? Unfade(seen, nothing, layer.Opacity) : seen;
        var box = ToBox(layer.Rect);
        var spec = bench.CameraClip.Spec;
        ZoomChecks.ExpectWindow(context, picture, spec, RenderBench.CameraNumber, box, layer.Source ?? Window.Whole, DrawnTolerance, ColorTolerance(layer.Opacity), $"{what}: the camera");
        ExpectOutline(context, picture, nothing, layer, box, spec, RenderBench.CameraNumber, $"{what}: the camera");
        ZoomChecks.ExpectSameOutside(context, seen, nothing, box, $"{what}: the camera's frame against the plain canvas");
    }

    /// <summary>
    /// The whole frame: inside the camera it is the frame drawn without the screen, and outside
    /// the camera's box it is the frame drawn without the camera. So the camera is over the screen.
    /// </summary>
    private static void ExpectCameraOnTop(CheckContext context, RenderBench bench, StudioProject project, double time, Layer camera, Picture nothing, string what, bool overTheScreen)
    {
        var frame = bench.Render(project, time: time);
        var cameraAlone = bench.Render(project, time: time, withScreen: false);
        var screenAlone = bench.Render(project, time: time, withCamera: false);
        var box = ToBox(camera.Rect);

        // A square inside a circle or a squircle, and inside a rounded rectangle clear of its corners.
        var inset = camera.Shape is StudioCameraShape.Circle or StudioCameraShape.Squircle
            ? (int)Math.Ceiling(box.Width * 0.15) + 2
            : (int)Math.Ceiling(camera.Radius * (1 - Math.Sqrt(0.5))) + 2;
        var (left, top, right, bottom) = (box.Left + inset, box.Top + inset, box.Right - inset, box.Bottom - inset);
        RendererChecks.ExpectSame(context, frame, cameraAlone, $"{what}: inside the camera, the frame against the one drawn without the screen", left, top, right, bottom);
        ZoomChecks.ExpectSameOutside(context, frame, screenAlone, box, $"{what}: the frame against the one drawn without the camera");
        if (overTheScreen)
        {
            var (difference, _, _) = Picture.MaxDifference(screenAlone, nothing, left, top, right, bottom);
            context.Expect(difference > 0, $"{what}: the screen is nowhere under the camera, so the frame does not show which is on top");
        }

        ZoomChecks.Keep(context, frame, $"{what}: the frame");
    }

    /// <summary>The outline of a layer: of a card at its box's edges and round corners, of a round shape all the way round.</summary>
    private static void ExpectOutline(CheckContext context, Picture picture, Picture without, Layer layer, Box box, ClipSpec spec, int number, string what)
    {
        if (layer.Shape is StudioCameraShape.Circle or StudioCameraShape.Squircle)
        {
            ExpectRoundOutline(context, picture, without, layer.Shape, box, what);
            return;
        }

        var tuple = (box.Left, box.Top, box.Right, box.Bottom);
        if (layer.Radius <= 30)
        {
            // A card: its edge is a step on the box's edge, with the layer's own colour right up to it.
            RendererChecks.ExpectEdges(context, what, picture, without, tuple, spec, ZoomChecks.Map(box, spec, layer.Source ?? Window.Whole), number, ColorTolerance(layer.Opacity));
        }
        else
        {
            // A rounded rectangle on its way from a bubble. At the middle of each side the pixel
            // outside the box is what is there without the layer, and 4 px inside is the layer.
            var middleX = (box.Left + box.Right) / 2;
            var middleY = (box.Top + box.Bottom) / 2;
            foreach (var (name, outX, outY, inX, inY) in new[]
            {
                ("left", box.Left - 1, middleY, box.Left + 4, middleY),
                ("right", box.Right, middleY, box.Right - 5, middleY),
                ("top", middleX, box.Top - 1, middleX, box.Top + 4),
                ("bottom", middleX, box.Bottom, middleX, box.Bottom - 5),
            })
            {
                RendererChecks.ExpectPixel(context, picture, outX, outY, without.Pixel(outX, outY), 0, $"{what}, just outside its {name} edge");
                context.Expect(picture.Pixel(inX, inY).Distance(without.Pixel(inX, inY)) > 20, $"{what}, 4 px inside its {name} edge at ({inX}, {inY}): {picture.Pixel(inX, inY)} is what is there without it");
            }
        }

        if (layer.Radius > 0)
        {
            RendererChecks.ExpectRoundedCorners(context, what, picture, without, tuple, layer.Radius);
        }
    }

    /// <summary>
    /// Points 4 px inside and 4 px outside a circle or a squircle, all the way round, and on the
    /// diagonal where the two shapes and a rectangle part ways: a circle ends 0.707 of the way to
    /// the corner of its box, a squircle 0.871.
    /// </summary>
    private static void ExpectRoundOutline(CheckContext context, Picture picture, Picture without, StudioCameraShape shape, Box box, string what)
    {
        var pairs = RendererChecks.OutlinePairs(shape, (box.Left, box.Top, box.Right, box.Bottom), box.Width / 2.0);
        var a = box.Width / 2.0;
        var b = box.Height / 2.0;
        (double X, double Y) Diagonal(double fraction) => (box.Left + a + (a * fraction), box.Top + b - (b * fraction));
        if (shape == StudioCameraShape.Circle)
        {
            pairs.Add((Diagonal(0.68), Diagonal(0.74)));
        }
        else
        {
            pairs.Add((Diagonal(0.80), Diagonal(0.94)));
            pairs.Add((Diagonal(RendererChecks.SquircleDiagonal - 0.03), Diagonal(RendererChecks.SquircleDiagonal + 0.03)));
        }

        foreach (var (inside, outside) in pairs)
        {
            var (outX, outY) = ((int)Math.Floor(outside.X), (int)Math.Floor(outside.Y));
            var (inX, inY) = ((int)Math.Floor(inside.X), (int)Math.Floor(inside.Y));
            RendererChecks.ExpectPixel(context, picture, outX, outY, without.Pixel(outX, outY), 0, $"{what}, outside the {shape} at ({outX}, {outY})");
            context.Expect(picture.Pixel(inX, inY).Distance(without.Pixel(inX, inY)) > 20, $"{what}, inside the {shape} at ({inX}, {inY}): {picture.Pixel(inX, inY)} is what is there without it");
        }
    }

    /// <summary>
    /// The picture a layer would make drawn whole, from one where it is seen through: every
    /// pixel is moved 1 / opacity times as far from what is under it. Where the layer is not,
    /// the two pictures are the same and stay so.
    /// </summary>
    private static Picture Unfade(Picture picture, Picture under, double opacity)
    {
        var seen = picture.Bgra;
        var below = under.Bgra;
        var whole = new byte[seen.Length];
        for (var index = 0; index < seen.Length; index++)
        {
            whole[index] = index % 4 == 3
                ? (byte)255
                : (byte)Math.Clamp(Math.Round(below[index] + ((seen[index] - below[index]) / opacity), MidpointRounding.AwayFromZero), 0, 255);
        }

        return Picture.FromBgra(whole, picture.Width, picture.Height);
    }
}
