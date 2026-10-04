using System.Diagnostics;
using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>
/// Zooms, read back from pixels. For every frame looked at, the part of the screen the card
/// should show is worked out here by hand from section 6.8 of the format. The layout resolver
/// has to give exactly that, and the picture has to show it: four edges of the test pattern are
/// found in the picture to a fraction of a pixel, and they have to be where that window puts them.
/// </summary>
internal static class ZoomChecks
{
    /// <summary>How far, in pixels, an edge of the pattern may be from its place in a frame read straight from the renderer.</summary>
    private const double DrawnTolerance = 0.5;

    /// <summary>The same for a frame that has been through a video encoder or a JPEG: the tolerance every export is held to.</summary>
    private const double EncodedTolerance = ExportVerifier.EdgeTolerance;

    // Where the layers are on a 1920×1080 canvas, worked out by hand from section 6.3 with every
    // edge on a whole pixel. Padding is 0.06 of the short side, 64.8 px.
    private static readonly Box ScreenCard = new(115, 65, 1805, 1015);
    private static readonly Box SideBySideScreenCard = new(65, 192, 1303, 888);

    /// <summary>The same card on a 640×360 poster: padding 21.6 px.</summary>
    private static readonly Box PosterCard = new(38, 22, 602, 338);

    private static readonly double[] Grid = [0.18, 0.38, 0.62, 0.82];

    public static async Task Run(Harness harness)
    {
        if (!harness.Wants("zoom"))
        {
            return;
        }

        foreach (var warp in new[] { false, true })
        {
            var prefix = warp ? "zoom on WARP: " : "zoom: ";
            RenderBench bench;
            try
            {
                bench = new RenderBench(warp);
            }
            catch (Exception ex)
            {
                await harness.Check("zoom", prefix + "a device and a renderer can be made", _ => throw new CheckFailedException($"{ex.GetType().Name}: {ex.Message}")).ConfigureAwait(false);
                continue;
            }

            using (bench)
            {
                Task Check(string name, Action<CheckContext> body) => harness.Check("zoom", prefix + name, body);

                await Check("a held window, and the limits of its scale", context => Held(context, bench)).ConfigureAwait(false);
                await Check("a focus near a corner is pushed inside", context => CornerPush(context, bench)).ConfigureAwait(false);
                await Check("moving in and out", context => Eases(context, bench)).ConfigureAwait(false);
                await Check("chained zooms move straight to the next window", context => Chained(context, bench)).ConfigureAwait(false);
                await Check("following the pointer", context => Pointer(context, bench)).ConfigureAwait(false);
                await Check("a cropped screen is zoomed inside its crop", context => Crop(context, bench)).ConfigureAwait(false);
                await Check("other layouts: only the screen's picture changes", context => Layouts(context, bench)).ConfigureAwait(false);
                await Check("click rings grow and move with the picture", context => ClickRings(context, bench)).ConfigureAwait(false);
                await Check("time to draw a zoomed frame", context => DrawTime(context, bench)).ConfigureAwait(false);
            }
        }

        await harness.Check("zoom", "zoom: export, frame by frame", context => Export(context, harness)).ConfigureAwait(false);
        await harness.Check("zoom", "zoom: export in software", context => SoftwareExports(context, harness)).ConfigureAwait(false);
        await harness.Check("zoom", "zoom: poster", context => Poster(context, harness)).ConfigureAwait(false);
    }

    // The projects

    /// <summary>The screen alone on a plain canvas, with no shadow, so a pixel is exactly one colour.</summary>
    private static StudioProject ScreenOnly(TestClip screen, params StudioZoom[] zooms) =>
        Projects.Create(screen, null) with
        {
            Canvas = new StudioCanvas { Background = new StudioBackground { Style = StudioBackgroundStyle.Solid, Primary = RendererChecks.Orange } },
            Screen = new StudioScreenStyle { Shadow = 0 },
            Scenes = [new StudioScene { Layout = StudioLayout.Screen }],
            Zooms = zooms,
        };

    /// <summary>A zoom on a fixed point. It cuts in and out unless it is given eases.</summary>
    private static StudioZoom Zoom(double start, double end, double scale, double x, double y, double easeIn = 0, double easeOut = 0) =>
        new() { Start = start, End = end, Scale = scale, Focus = new StudioZoomFocus { X = x, Y = y }, EaseIn = easeIn, EaseOut = easeOut };

    /// <summary>
    /// A 2× zoom that follows the pointer and cuts in and out. Its own point, which it uses only
    /// when no pointer was recorded, is (0.27, 0.27): a window from (0.02, 0.02).
    /// </summary>
    private static StudioZoom FollowPointer(double start, double end) =>
        new() { Start = start, End = end, Scale = 2, Focus = new StudioZoomFocus { Mode = StudioZoomFocusMode.Cursor, X = 0.27, Y = 0.27 }, EaseIn = 0, EaseOut = 0 };

    /// <summary>The format's ease: u² (3 − 2u).</summary>
    private static double Ease(double u) => u * u * (3 - (2 * u));

    /// <summary>The format's lerp between two windows: each of x, y, width and height by itself.</summary>
    private static Window Between(Window from, Window to, double amount) =>
        new(
            from.X + ((to.X - from.X) * amount),
            from.Y + ((to.Y - from.Y) * amount),
            from.Width + ((to.Width - from.Width) * amount),
            from.Height + ((to.Height - from.Height) * amount));

    // Single frames

    private static void Held(CheckContext context, RenderBench bench)
    {
        // Scale 2 shows half the screen each way. Centred on (0.25, 0.25) that is the top-left
        // quarter: 0.25 − 0.5 / 2 = 0 across, and the same down.
        var plain = ScreenOnly(bench.ScreenClip);
        var project = ScreenOnly(bench.ScreenClip, Zoom(1, 2, 2, 0.25, 0.25));
        var quarter = new Window(0, 0, 0.5, 0.5);
        ExpectFrame(context, bench, plain, 1, Window.Whole, "with no zoom");
        ExpectFrame(context, bench, project, 1, quarter, "as the zoom starts");
        ExpectFrame(context, bench, project, 1.999, quarter, "just before it ends");

        // A zoom lasts from its start up to, and not including, its end.
        var without = bench.Render(plain, time: 1);
        RendererChecks.ExpectSame(context, bench.Render(project, time: 0.999), without, "just before the zoom, against the frame with no zoom");
        RendererChecks.ExpectSame(context, bench.Render(project, time: 2), without, "at the zoom's end, against the frame with no zoom");

        // Only the picture inside the card changes: the canvas around it and the card's round corners stay.
        var zoomed = bench.Render(project, time: 1);
        ExpectSameOutside(context, zoomed, without, ScreenCard, "the zoomed frame against the frame with no zoom");
        ExpectSameCorners(context, zoomed, without, ScreenCard);

        // A scale above 5 is 5, a fifth of the screen each way. Centred on (0.32, 0.44) that is
        // from 0.32 − 0.1 = 0.22 across and from 0.44 − 0.1 = 0.34 down. A scale below 1 is 1.
        ExpectFrame(context, bench, ScreenOnly(bench.ScreenClip, Zoom(0, 2, 9, 0.32, 0.44)), 1, new Window(0.22, 0.34, 0.2, 0.2), "a scale of 9, which is 5");
        ExpectFrame(context, bench, ScreenOnly(bench.ScreenClip, Zoom(0, 2, 0.5, 0.32, 0.44)), 1, Window.Whole, "a scale of 0.5, which is 1");
    }

    private static void CornerPush(CheckContext context, RenderBench bench)
    {
        // Scale 4 shows a quarter of the screen each way. Centred on (0.95, 0.05) it would run
        // from 0.825 to 1.075 across and from −0.075 to 0.175 down. Pushed back inside it runs
        // from 0.75 to 1 and from 0 to 0.25: the top-right corner, where the red block is.
        ExpectFrame(context, bench, ScreenOnly(bench.ScreenClip, Zoom(0, 2, 4, 0.95, 0.05)), 1, new Window(0.75, 0, 0.25, 0.25), "a focus near the top-right corner");

        // At scale 2, centred on (0.02, 0.03), it would start at −0.23 and −0.22.
        ExpectFrame(context, bench, ScreenOnly(bench.ScreenClip, Zoom(0, 2, 2, 0.02, 0.03)), 1, new Window(0, 0, 0.5, 0.5), "a focus near the top-left corner");
    }

    private static void Eases(CheckContext context, RenderBench bench)
    {
        // Scale 2 centred on (0.29, 0.30) holds (0.04, 0.05, 0.5, 0.5). The zoom lasts 2 s and
        // spends 1 s moving in and 1 s moving out, so it is still only at t = 2. Moving in, the
        // window is between the whole screen and the held one by ease(time since the start / 1 s);
        // moving out, by ease(time to the end / 1 s).
        var held = new Window(0.04, 0.05, 0.5, 0.5);
        var project = ScreenOnly(bench.ScreenClip, Zoom(1, 3, 2, 0.29, 0.30, easeIn: 1, easeOut: 1));
        foreach (var (time, fraction, what) in new[]
        {
            (1.25, 0.25, "a quarter of the way in"),
            (1.5, 0.5, "halfway in"),
            (2.0, 1.0, "between moving in and moving out"),
            (2.5, 0.5, "halfway out"),
            (2.75, 0.25, "three quarters of the way out"),
        })
        {
            ExpectFrame(context, bench, project, time, Between(Window.Whole, held, Ease(fraction)), what);
        }

        // Eases longer than the zoom are shortened in proportion: 1.5 s in and 0.5 s out of a
        // zoom 1 s long become 0.75 s and 0.25 s. Halfway through each, the window is halfway.
        var squeezed = ScreenOnly(bench.ScreenClip, Zoom(1, 2, 2, 0.29, 0.30, easeIn: 1.5, easeOut: 0.5));
        ExpectFrame(context, bench, squeezed, 1.375, Between(Window.Whole, held, Ease(0.5)), "halfway through an ease in shortened to 0.75 s");
        ExpectFrame(context, bench, squeezed, 1.875, Between(Window.Whole, held, Ease(0.5)), "halfway through an ease out shortened to 0.25 s");
    }

    private static void Chained(CheckContext context, RenderBench bench)
    {
        // Two zooms that share the instant t = 2. The first holds the top-left quarter. The second
        // is at scale 2.5, which shows 0.4 of the screen each way; centred on (0.27, 0.29) that is
        // from 0.27 − 0.2 = 0.07 across and from 0.29 − 0.2 = 0.09 down.
        var first = new Window(0, 0, 0.5, 0.5);
        var second = new Window(0.07, 0.09, 0.4, 0.4);
        var project = ScreenOnly(bench.ScreenClip, Zoom(1, 2, 2, 0.25, 0.25, easeOut: 1), Zoom(2, 4, 2.5, 0.27, 0.29, easeIn: 1));

        // The first does not move out, although it asks for a second of it.
        ExpectFrame(context, bench, project, 1.5, first, "the first zoom, where it would be moving out");
        ExpectFrame(context, bench, project, 1.999, first, "the first zoom as it ends");

        // The second moves in from where the first was, not from the whole screen, and its size
        // goes straight from one to the other.
        ExpectFrame(context, bench, project, 2, first, "the second zoom as it starts");
        ExpectFrame(context, bench, project, 2.25, Between(first, second, Ease(0.25)), "a quarter of the way to the second window");
        ExpectFrame(context, bench, project, 2.5, Between(first, second, Ease(0.5)), "halfway to the second window");
        ExpectFrame(context, bench, project, 3.5, second, "the second window, held");

        // A thousandth of a second apart they are not chained: the first moves out to the whole
        // screen, and the second moves in from it.
        var apart = ScreenOnly(bench.ScreenClip, Zoom(1, 2, 2, 0.25, 0.25, easeOut: 1), Zoom(2.001, 4.001, 2.5, 0.27, 0.29, easeIn: 1));
        ExpectFrame(context, bench, apart, 1.5, Between(Window.Whole, first, Ease(0.5)), "not chained: the first zoom halfway out");
        ExpectFrame(context, bench, apart, 2.501, Between(Window.Whole, second, Ease(0.5)), "not chained: the second zoom halfway in");
    }

    private static void Pointer(CheckContext context, RenderBench bench)
    {
        // The pointer is at (0.25, 0.25) from t = 1 and at (0.31, 0.33) from t = 2: samples are
        // steps. The focus is its mean place over the second around t, so with a share f of that
        // second spent at the second place the focus is (0.25 + 0.06 f, 0.25 + 0.08 f), and at
        // scale 2 the window starts 0.25 before it: at (0.06 f, 0.08 f).
        var project = ScreenOnly(bench.ScreenClip, FollowPointer(0, 6));
        var events = Projects.Events(bench.ScreenClip) with
        {
            Cursor =
            [
                new StudioCursorSample { T = 1, X = 0.25, Y = 0.25 },
                new StudioCursorSample { T = 2, X = 0.31, Y = 0.33 },
            ],
        };
        foreach (var (time, share, what) in new[]
        {
            (0.25, 0.0, "before the first sample, the pointer is where it first is"),
            (1.5, 0.0, "the pointer at rest for the whole second"),
            (1.75, 0.25, "a quarter of the second after the pointer moved"),
            (2.0, 0.5, "half of the second either side of the move"),
            (2.25, 0.75, "three quarters of the second after the move"),
            (3.5, 1.0, "after the last sample"),
        })
        {
            ExpectFrame(context, bench, project, time, new Window(0.06 * share, 0.08 * share, 0.5, 0.5), what, events);
        }

        // With no pointer recorded the zoom uses its own point: (0.27, 0.27), a window from 0.02.
        ExpectFrame(context, bench, project, 2, new Window(0.02, 0.02, 0.5, 0.5), "no pointer recorded: the zoom's own point");
    }

    private static void Crop(CheckContext context, RenderBench bench)
    {
        // The screen is cropped to (0.05, 0.05, 0.6, 0.6), which has the shape of the whole
        // screen, so the card stays where it is. Scale 1.25 shows 0.6 / 1.25 = 0.48 of the screen
        // each way, and the window stays inside the crop: it starts between 0.05 and 0.65 − 0.48 = 0.17.
        var project = ScreenOnly(bench.ScreenClip, Zoom(1, 2, 1.25, 0.31, 0.32), Zoom(3, 4, 1.25, 0, 0), Zoom(5, 6, 1.25, 1, 1)) with
        {
            Screen = new StudioScreenStyle { Shadow = 0, Crop = new StudioRect(0.05, 0.05, 0.6, 0.6) },
        };
        ExpectFrame(context, bench, project, 0.5, new Window(0.05, 0.05, 0.6, 0.6), "no zoom: the crop");

        // The focus is a point of the whole screen, not of the crop: the window starts at
        // 0.31 − 0.24 = 0.07 across and 0.32 − 0.24 = 0.08 down.
        ExpectFrame(context, bench, project, 1.5, new Window(0.07, 0.08, 0.48, 0.48), "a focus inside the crop");

        // A focus at a corner of the screen is outside the crop, and the window is pushed to the
        // crop's corner, not the screen's.
        ExpectFrame(context, bench, project, 3.5, new Window(0.05, 0.05, 0.48, 0.48), "a focus above and left of the crop");
        ExpectFrame(context, bench, project, 5.5, new Window(0.17, 0.17, 0.48, 0.48), "a focus below and right of the crop");
    }

    private static void Layouts(CheckContext context, RenderBench bench)
    {
        var zoom = Zoom(0, 2, 2, 0.25, 0.25);
        var quarter = new Window(0, 0, 0.5, 0.5);
        foreach (var (layout, card, name) in new[] { (StudioLayout.Bubble, ScreenCard, "bubble"), (StudioLayout.SideBySide, SideBySideScreenCard, "side by side") })
        {
            var plain = bench.Base(layout);
            var project = plain with { Zooms = [zoom] };
            var without = bench.Render(plain, time: 1);
            var with = bench.Render(project, time: 1);
            ExpectSameOutside(context, with, without, card, $"{name}: the zoomed frame against the frame with no zoom");
            ExpectFrame(context, bench, project, 1, quarter, $"{name}: the screen", card: card);

            if (layout == StudioLayout.Bubble)
            {
                // The bubble is a circle 388.8 px across with its centre at (1693.2, 853.2), and
                // it lies partly over the card. Inside it nothing changes.
                var different = 0;
                var compared = 0;
                for (var y = 659; y < 1048; y++)
                {
                    for (var x = 1499; x < 1888; x++)
                    {
                        if (Math.Sqrt(Math.Pow(x + 0.5 - 1693.2, 2) + Math.Pow(y + 0.5 - 853.2, 2)) < 192)
                        {
                            compared++;
                            different += with.Pixel(x, y) == without.Pixel(x, y) ? 0 : 1;
                        }
                    }
                }

                context.Expect(different == 0, $"bubble: {different} of {compared} pixels of the camera differ from the frame with no zoom");
            }

            Keep(context, with, $"{name}: the whole frame", always: context.Problems.Count > 0);
        }

        // The camera layout shows no screen, so a zoom changes nothing at all.
        var camera = bench.Base(StudioLayout.Camera);
        var zoomedCamera = camera with { Zooms = [zoom] };
        context.Expect(StudioLayoutResolver.Resolve(zoomedCamera, Projects.Events(bench.ScreenClip), 1, 1920, 1080).Screen is null, "camera: the layout has a screen");
        RendererChecks.ExpectSame(context, bench.Render(zoomedCamera, time: 1), bench.Render(camera, time: 1), "camera: the zoomed frame against the frame with no zoom");
    }

    private static void ClickRings(CheckContext context, RenderBench bench)
    {
        // The top-left quarter fills the card, so the card's 1689.6 px hold 960 screen pixels:
        // 1.76 px each. A 100-point ring with a 10-point stroke is 176 px across and 17.6 px
        // wide at the moment of the click, the size a 200-point ring has with no zoom. A click
        // at (0.25, 0.25) is the middle of the window, and so the middle of the card: (960, 540).
        var project = ScreenOnly(bench.ScreenClip, Zoom(0, 2, 2, 0.25, 0.25)) with
        {
            Overlays = new StudioOverlays { Clicks = new StudioClickOverlay { Enabled = true, Size = 100, StrokeWidth = 10, Opacity = 0.85, Duration = 0.45 }, Branding = false },
        };
        StudioEvents ClickAt(double x, double y) => Projects.Events(bench.ScreenClip) with { Clicks = [new StudioClickEvent { T = 1, X = x, Y = y }] };
        var plain = bench.Render(project, time: 1);
        var middle = bench.Render(project, time: 1, events: ClickAt(0.25, 0.25));
        RendererChecks.ExpectRing(context, middle, plain, 960, 540, 88, 17.6, 0.85, "a click in the middle of the window");

        // Halfway through its 0.45 s the ring has grown by 0.58 × 0.5 of its diameter and half faded.
        RendererChecks.ExpectRing(context, bench.Render(project, time: 1.225, events: ClickAt(0.25, 0.25)), plain, 960, 540, 88 + (176 * 0.58 * 0.5), 17.6, 0.425, "the same click halfway through");

        // A click on a part of the screen the window does not show is not drawn.
        RendererChecks.ExpectSame(context, bench.Render(project, time: 1, events: ClickAt(0.75, 0.75)), plain, "a click outside the window");
        RendererChecks.ExpectSame(context, bench.Render(project, time: 1, events: ClickAt(0.51, 0.25)), plain, "a click just outside the window");

        // A click at (0.49, 0.25) is 0.98 of the way across the card, 33.8 px inside its right
        // edge at x = 1771.0. Its ring reaches over the edge and is cut off there.
        var edge = bench.Render(project, time: 1, events: ClickAt(0.49, 0.25));
        ExpectSameOutside(context, edge, plain, ScreenCard, "a ring over the card's right edge");
        foreach (var (x, y, what) in new[] { (1771 - 88, 540, "left of the click"), (1771, 540 - 88, "above the click"), (1771, 540 + 88, "below the click") })
        {
            var want = plain.Pixel(x, y).Mix(new Rgb(10, 132, 255), 0.85);
            context.Expect(edge.Pixel(x, y).Distance(want) <= 1.5, $"a ring over the card's right edge: {what}, pixel ({x},{y}) is {edge.Pixel(x, y)}, want {want}");
        }

        Keep(context, middle, "a click in the middle of the window", always: context.Problems.Count > 0);
        Keep(context, edge, "a ring over the card's right edge", always: context.Problems.Count > 0);
    }

    private static void DrawTime(CheckContext context, RenderBench bench)
    {
        // The exporter samples linearly on WARP, so that is all that is timed there.
        var software = bench.Graphics.IsSoftware;
        var frames = software ? 20 : 240;
        var plain = ScreenOnly(bench.ScreenClip);
        var zoomed = ScreenOnly(bench.ScreenClip, Zoom(0, 2, 2, 0.25, 0.25));
        var parts = new List<string>();
        foreach (var quality in software ? new[] { StudioRenderQuality.Preview } : new[] { StudioRenderQuality.Preview, StudioRenderQuality.Export })
        {
            var without = Time(bench, plain, quality, frames);
            var with = Time(bench, zoomed, quality, frames);
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{(quality == StudioRenderQuality.Export ? "high-quality" : "linear")} sampling {without:0.00} ms with no zoom and {with:0.00} ms at 2×"));
        }

        context.Measure($"one 1920×1080 frame, best of three runs of {frames} on {bench.Graphics.AdapterName}: {string.Join(", ", parts)}");
    }

    private static double Time(RenderBench bench, StudioProject project, StudioRenderQuality quality, int frames)
    {
        var target = bench.Target(1920, 1080);
        var request = new StudioRenderRequest(project, Projects.Events(bench.ScreenClip), bench.EmptyFolder, 1, 1920, 1080, bench.ScreenFrame, null, target, null, quality);
        var best = double.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            lock (bench.Graphics.Gate)
            {
                // Once to warm up, and a read to make sure the device has finished before the clock starts.
                bench.Renderer.Render(request);
                bench.Graphics.ReadTexture(target);
                var watch = Stopwatch.StartNew();
                for (var frame = 0; frame < frames; frame++)
                {
                    bench.Renderer.Render(request);
                }

                bench.Graphics.ReadTexture(target);
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds / frames);
            }
        }

        return best;
    }

    // Exports

    /// <summary>
    /// A zoom that cuts in and out, from 1 s to 2 s, one that moves, from 3 s to 5 s with a
    /// second each way, and one that follows the pointer, from 5.2 s to 5.8 s. An export draws
    /// frame n as the video is at the middle of that frame, (n + 0.5) / 30 s.
    /// </summary>
    private static async Task Export(CheckContext context, Harness harness)
    {
        var clip = harness.Clips.Screen;
        var quarter = new Window(0, 0, 0.5, 0.5);
        var held = new Window(0.04, 0.05, 0.5, 0.5);
        var project = ScreenOnly(clip, Zoom(1, 2, 2, 0.25, 0.25), Zoom(3, 5, 2, 0.29, 0.30, easeIn: 1, easeOut: 1), FollowPointer(5.2, 5.8)) with { Audio = new StudioAudio { Muted = true } };

        // The pointer is at (0.25, 0.25) until 5.5 s and at (0.31, 0.33) after it.
        var events = Projects.Events(clip) with
        {
            Cursor =
            [
                new StudioCursorSample { T = 0, X = 0.25, Y = 0.25 },
                new StudioCursorSample { T = 5.5, X = 0.31, Y = 0.33 },
            ],
        };
        var (path, result) = await ExportTo(context, "export", project, clip, events).ConfigureAwait(false);

        // A zoom leaves the size and the length of the video alone.
        context.Expect(result.Width == 1920 && result.Height == 1080 && result.FrameCount == clip.Frames, $"the export is {result.Width}×{result.Height} with {result.FrameCount} frames, want 1920×1080 with {clip.Frames}");

        // Frames 30 to 59 have their middles inside the first zoom; 29 and 60 do not. Frames 97
        // and 142 have theirs at 3.25 s and 4.75 s: a quarter of a second into the second zoom,
        // and a quarter of a second before its end. Frames 157 and 172 have theirs at 5.25 s and
        // 5.75 s, when the pointer spends a quarter and three quarters of the second around them
        // at its second place: the window starts at (0.06, 0.08) times that share.
        var moving = Between(Window.Whole, held, Ease(0.25));
        var frames = Frames(path, 29, 30, 59, 60, 97, 142, 157, 172);
        foreach (var (index, want, what) in new[]
        {
            (29, Window.Whole, "frame 29, the last before the zoom"),
            (30, quarter, "frame 30, the first of the zoom"),
            (59, quarter, "frame 59, the last of the zoom"),
            (60, Window.Whole, "frame 60, the first after the zoom"),
            (97, moving, "frame 97, a quarter of a second into moving in"),
            (142, moving, "frame 142, a quarter of a second from the end of moving out"),
            (157, new Window(0.015, 0.02, 0.5, 0.5), "frame 157, a quarter of a second before the pointer moves"),
            (172, new Window(0.045, 0.06, 0.5, 0.5), "frame 172, a quarter of a second after the pointer moved"),
        })
        {
            ExpectWindow(context, frames[index], clip.Spec, index, ScreenCard, want, EncodedTolerance, ExportVerifier.ColorTolerance, what);
        }
    }

    /// <summary>The same zoom through the software encoder, and drawn by WARP, which samples linearly.</summary>
    private static async Task SoftwareExports(CheckContext context, Harness harness)
    {
        // Moving in from 0.5 s for a second: frame 22 has its middle at 0.75 s, a quarter of
        // the way in, and frame 60 is in the part that is held.
        var clip = harness.Clips.Screen;
        var held = new Window(0.04, 0.05, 0.5, 0.5);
        var project = ScreenOnly(clip, Zoom(0.5, 2.5, 2, 0.29, 0.30, easeIn: 1)) with { Audio = new StudioAudio { Muted = true } };
        foreach (var (label, options) in new[]
        {
            ("software encoder", new StudioExportOptions(EncoderPreference: StudioEncoderPreference.SoftwareOnly)),
            ("WARP", new StudioExportOptions(DevicePreference: StudioRenderDevicePreference.WarpOnly)),
        })
        {
            var (path, result) = await ExportTo(context, label, project, clip, Projects.Events(clip), options).ConfigureAwait(false);
            context.Expect(label != "software encoder" || !result.HardwareEncoder, $"{label}: the export used a hardware encoder");
            context.Expect(label != "WARP" || result.SoftwareRendering, $"{label}: the export was not drawn by WARP");
            var frames = Frames(path, 22, 60);
            ExpectWindow(context, frames[22], clip.Spec, 22, ScreenCard, Between(Window.Whole, held, Ease(0.25)), EncodedTolerance, ExportVerifier.ColorTolerance, $"{label}: frame 22, a quarter of the way in");
            ExpectWindow(context, frames[60], clip.Spec, 60, ScreenCard, held, EncodedTolerance, ExportVerifier.ColorTolerance, $"{label}: frame 60, held");
        }
    }

    /// <summary>A poster is the frame the export shows at that moment, zoomed as it is.</summary>
    private static async Task Poster(CheckContext context, Harness harness)
    {
        // The zoom of the software exports, and one from 3 s to 4 s that follows a pointer resting
        // at (0.25, 0.25). The poster for 0.75 s is frame 22, for 2 s frame 60, and for 3.5 s frame 105.
        var clip = harness.Clips.Screen;
        var held = new Window(0.04, 0.05, 0.5, 0.5);
        var project = ScreenOnly(clip, Zoom(0.5, 2.5, 2, 0.29, 0.30, easeIn: 1), FollowPointer(3, 4));
        var events = Projects.Events(clip) with { Cursor = [new StudioCursorSample { T = 0, X = 0.25, Y = 0.25 }] };
        var folder = context.Folder();
        foreach (var (seconds, number, want, what) in new[]
        {
            (0.75, 22, Between(Window.Whole, held, Ease(0.25)), "at 0.75 s, a quarter of the way in"),
            (2.0, 60, held, "at 2 s, held"),
            (3.5, 105, new Window(0, 0, 0.5, 0.5), "at 3.5 s, following the pointer"),
        })
        {
            var path = Path.Combine(folder, $"poster-{number}.jpg");
            await new StudioExporter().WritePosterAsync(project, events, Projects.Paths(folder, clip, null), path, 640, seconds).ConfigureAwait(false);
            var picture = Images.Load(path);
            if (context.Expect(picture.Width == 640 && picture.Height == 360, $"{what}: the poster is {picture.Width}×{picture.Height}, want 640×360"))
            {
                // A JPEG smears colour a little at this size.
                ExpectWindow(context, picture, clip.Spec, number, PosterCard, want, EncodedTolerance, 10, what);
            }
        }
    }

    private static async Task<(string Path, StudioExportResult Result)> ExportTo(CheckContext context, string label, StudioProject project, TestClip screen, StudioEvents events, StudioExportOptions? options = null)
    {
        var folder = context.Folder(label);
        var output = Path.Combine(folder, "zoom.mp4");
        var result = await new StudioExporter().ExportAsync(project, events, Projects.Paths(folder, screen, null), output, options ?? new StudioExportOptions()).ConfigureAwait(false);
        context.Note($"{label}: {result.EncoderDescription}{(result.SoftwareRendering ? ", drawn by WARP" : string.Empty)}, {result.FrameCount} frames at {result.Width}×{result.Height}");
        return (output, result);
    }

    /// <summary>The named frames of a video, decoded in one pass.</summary>
    internal static Dictionary<int, Picture> Frames(string path, params int[] wanted)
    {
        var found = new Dictionary<int, Picture>();
        var last = wanted.Max();
        var decoded = Media.ReadFrames(path, chroma: true, frame =>
        {
            if (wanted.Contains(frame.Index))
            {
                found[frame.Index] = frame.Picture;
            }

            return frame.Index < last;
        });

        if (found.Count != wanted.Length)
        {
            throw new CheckFailedException($"{Path.GetFileName(path)} has {decoded} frames; frames {string.Join(", ", wanted.Where(index => !found.ContainsKey(index)))} are not there");
        }

        return found;
    }

    // What the checks share

    /// <summary>
    /// One frame of a project, three ways: the window worked out by hand, the one the layout
    /// resolver gives, and the one the drawn picture shows.
    /// </summary>
    private static void ExpectFrame(CheckContext context, RenderBench bench, StudioProject project, double time, Window want, string what, StudioEvents? events = null, Box? card = null)
    {
        events ??= Projects.Events(bench.ScreenClip);
        var box = card ?? ScreenCard;
        if (StudioLayoutResolver.Resolve(project, events, time, 1920, 1080).Screen is not { } resolved)
        {
            context.Fail($"{what}: the layout has no screen");
            return;
        }

        // The card itself never moves for a zoom.
        var rect = Projects.Aligned(resolved.Rect);
        context.Expect(
            rect.X == box.Left && rect.Y == box.Top && rect.Width == box.Width && rect.Height == box.Height,
            $"{what}: the screen's card resolves to ({rect.X}, {rect.Y}) {rect.Width}×{rect.Height}, worked out by hand as ({box.Left}, {box.Top}) {box.Width}×{box.Height}");

        var source = resolved.Source;
        var difference = Math.Max(
            Math.Max(Math.Abs(source.X - want.X), Math.Abs(source.Y - want.Y)),
            Math.Max(Math.Abs(source.Width - want.Width), Math.Abs(source.Height - want.Height)));
        context.Expect(difference < 1e-9, $"{what}: the layout resolver gives {new Window(source.X, source.Y, source.Width, source.Height)}, worked out by hand as {want}");

        // The camera is left out: in the bubble layout it lies over a corner of the card.
        var picture = bench.Render(project, time: time, events: events, withCamera: false);
        ExpectWindow(context, picture, bench.ScreenClip.Spec, RenderBench.ScreenNumber, box, want, DrawnTolerance, 1, what);
    }

    /// <summary>
    /// The card of a picture shows the part <paramref name="want"/> of the screen: the frame
    /// number reads right where the strip should be, flat places of the pattern have their
    /// colours, and four of the pattern's edges are where they should be.
    /// </summary>
    internal static void ExpectWindow(CheckContext context, Picture picture, ClipSpec spec, int number, Box card, Window want, double edgeTolerance, double colorTolerance, string what)
    {
        var problems = context.Problems.Count;
        var map = Map(card, spec, want);
        var scale = Math.Min(map.ScaleX, map.ScaleY);

        var strip = "the frame strip is not in the window";
        if (Holds(want, spec, spec.CodeX, spec.CodeY, ClipSpec.CodeBits * spec.CodeCell, 2 * spec.CodeCell, 0))
        {
            var read = FrameCode.Decode(picture, spec, map);
            context.Expect(read == number, $"{what}: the frame strip reads {read}, want {number} ({FrameCode.Describe(picture, spec, map)})");
            strip = $"the frame strip reads {read}";
        }

        // Flat places: the middle of each colour patch, a grid of points across the window, the
        // red block and the plain background. Each is averaged over a small square, and counts
        // only where the pattern is one colour for the whole square and a little more.
        var radius = Math.Clamp((int)(scale * 18 / 5), 0, 8);
        var margin = ((radius + 2) / scale) + 1;
        var samples = 0;
        var worst = 0.0;
        var worstWhere = string.Empty;
        void Sample(double x, double y, string name)
        {
            if (!Holds(want, spec, x, y, 0, 0, margin) || spec.FlatColorAt(x, y, number, margin) is not { } drawn)
            {
                return;
            }

            var (px, py) = map.Apply(x, y);
            var got = picture.Average(px, py, radius);
            samples++;
            if (got.Distance(drawn) > worst)
            {
                worst = got.Distance(drawn);
                worstWhere = string.Create(CultureInfo.InvariantCulture, $"{name}, at ({px:0}, {py:0}) in the picture, is {got}, drawn {drawn}");
            }
        }

        for (var index = 0; index < ClipSpec.PatchColors.Length; index++)
        {
            var (x, y) = spec.PatchCenter(index);
            Sample(x, y, $"patch {index}");
        }

        foreach (var down in Grid)
        {
            foreach (var across in Grid)
            {
                Sample((want.X + (want.Width * across)) * spec.Width, (want.Y + (want.Height * down)) * spec.Height, string.Create(CultureInfo.InvariantCulture, $"the point {across:0.00} across and {down:0.00} down the window"));
            }
        }

        Sample(spec.MarkerX + (spec.MarkerWidth / 2.0), spec.MarkerY + (spec.MarkerHeight / 2.0), "the red block");
        Sample(spec.BackgroundPoint.X, spec.BackgroundPoint.Y, "the plain background");
        context.Expect(samples >= 8, $"{what}: only {samples} flat places of the pattern are inside {want}");
        context.Expect(worst <= colorTolerance, $"{what}: {worstWhere}");

        var edges = "nothing to measure";
        if (Measure(picture, spec, number, card, want) is not { } measured)
        {
            context.Fail($"{what}: nothing in {want} has edges to measure the window by");
        }
        else if (double.IsNaN(measured.Off))
        {
            context.Fail($"{what}: {measured.From} should be in the picture, and not all of its edges were found");
            edges = $"the edges of {measured.From} were not found";
        }
        else
        {
            context.Expect(measured.Off <= edgeTolerance, string.Create(CultureInfo.InvariantCulture, $"{what}: the edges of {measured.From} are up to {measured.Off:0.00} px from where {want} puts them; they fit {measured.Window}"));
            edges = string.Create(CultureInfo.InvariantCulture, $"measured {measured.Window} from {measured.From}, its edges within {measured.Off:0.00} px");
        }

        context.Note(string.Create(CultureInfo.InvariantCulture, $"{what}: wanted {want}, {edges}; {strip}; {samples} colours within {worst:0.0} of 255"));
        Keep(context, picture, what, always: context.Problems.Count > problems);
    }

    /// <summary>Saves a picture in the check's folder when the run's files are kept, and whenever it was not right.</summary>
    internal static void Keep(CheckContext context, Picture picture, string what, bool always = false)
    {
        if (always || context.Harness.KeepsFiles)
        {
            var name = string.Concat(what.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
            picture.SavePng(Path.Combine(context.Folder(), (name.Length > 48 ? name[..48] : name) + ".png"));
        }
    }

    /// <summary>
    /// Finds four edges of the pattern in the picture, near where <paramref name="want"/> puts
    /// them. Returns how far the furthest is from its place, in picture pixels, and the window
    /// the four fit; null when the window holds no set of edges to go by.
    /// </summary>
    private static Measured? Measure(Picture picture, ClipSpec spec, int number, Box card, Window want)
    {
        // Each edge is looked for along a line of 2 × half pixels across it. Both ends of the
        // line, and a little more, have to be flat colour: half the gap between two patches is
        // the least there is.
        var map = Map(card, spec, want);
        var half = Math.Min(6, (int)((spec.PatchPitch - spec.PatchSize) * Math.Min(map.ScaleX, map.ScaleY) / 2) - 2);
        if (half < 2)
        {
            return null;
        }

        var reach = half + 3;
        foreach (var set in LandmarkSets(spec, number))
        {
            var (left, acrossY) = map.Apply(set.Left, set.AcrossY);
            var (right, _) = map.Apply(set.Right, set.AcrossY);
            var (topX, top) = map.Apply(set.TopX, set.Top);
            var (bottomX, bottom) = map.Apply(set.BottomX, set.Bottom);
            if (left - reach < card.Left || right + reach > card.Right || top - reach < card.Top || bottom + reach > card.Bottom
                || acrossY - 2 < card.Top || acrossY + 2 > card.Bottom
                || Math.Min(topX, bottomX) - 2 < card.Left || Math.Max(topX, bottomX) + 2 > card.Right)
            {
                continue;
            }

            var gotLeft = Edge(picture, left, acrossY, alongX: true, half);
            var gotRight = Edge(picture, right, acrossY, alongX: true, half);
            var gotTop = Edge(picture, topX, top, alongX: false, half);
            var gotBottom = Edge(picture, bottomX, bottom, alongX: false, half);
            var off = Math.Max(Math.Max(Math.Abs(gotLeft - left), Math.Abs(gotRight - right)), Math.Max(Math.Abs(gotTop - top), Math.Abs(gotBottom - bottom)));

            // The window that puts the four edges exactly where they were found.
            var scaleX = (gotRight - gotLeft) / (set.Right - set.Left);
            var scaleY = (gotBottom - gotTop) / (set.Bottom - set.Top);
            var originX = gotLeft - (set.Left * scaleX);
            var originY = gotTop - (set.Top * scaleY);
            var fitted = new Window(
                (card.Left - originX) / (scaleX * spec.Width),
                (card.Top - originY) / (scaleY * spec.Height),
                card.Width / (scaleX * spec.Width),
                card.Height / (scaleY * spec.Height));
            return new Measured(fitted, off, set.Name);
        }

        return null;
    }

    /// <summary>The sets of edges a window can be measured by, widest apart first.</summary>
    private static IEnumerable<Landmarks> LandmarkSets(ClipSpec spec, int number)
    {
        var grey = spec.PatchX + (3 * spec.PatchPitch);
        var greyMiddle = grey + (spec.PatchSize / 2.0);
        var patchMiddle = spec.PatchY + (spec.PatchSize / 2.0);
        var patchBottom = spec.PatchY + spec.PatchSize;

        // Across, from the green patch's left edge to the grey patch's right edge. Down, from the
        // top of the frame strip, at the first cell of its upper row that is white, to the grey
        // patch's bottom edge.
        for (var column = 0; column < ClipSpec.CodeBits; column++)
        {
            if (((number >> (ClipSpec.CodeBits - 1 - column)) & 1) != 0)
            {
                yield return new Landmarks("the patches and the frame strip", spec.PatchX + spec.PatchPitch, grey + spec.PatchSize, patchMiddle, spec.CodeY, spec.CodeX + ((column + 0.5) * spec.CodeCell), patchBottom, greyMiddle);
                break;
            }
        }

        yield return new Landmarks("the grey patch", grey, grey + spec.PatchSize, patchMiddle, spec.PatchY, greyMiddle, patchBottom, greyMiddle);

        var blockMiddle = spec.MarkerX + (spec.MarkerWidth / 2.0);
        yield return new Landmarks("the red block", spec.MarkerX, spec.MarkerX + spec.MarkerWidth, spec.MarkerY + (spec.MarkerHeight / 2.0), spec.MarkerY, blockMiddle, spec.MarkerY + spec.MarkerHeight, blockMiddle);
    }

    /// <summary>
    /// Where an edge between two flat colours is, from the luma of the 2 × half pixels of a line
    /// across it: each pixel counts for as much of itself as is still the first colour. The
    /// line runs along x through (x, y), or along y. NaN when its two ends are too alike for
    /// there to be an edge between them, or when it leaves the picture.
    /// </summary>
    private static double Edge(Picture picture, double x, double y, bool alongX, int half)
    {
        // Three pixels side by side along the edge, to steady the reading.
        double At(int index)
        {
            double sum = 0;
            for (var offset = -1; offset <= 1; offset++)
            {
                var px = alongX ? index : (int)Math.Floor(x) + offset;
                var py = alongX ? (int)Math.Floor(y) + offset : index;
                if (px < 0 || py < 0 || px >= picture.Width || py >= picture.Height)
                {
                    return double.NaN;
                }

                sum += picture.Luma(px, py);
            }

            return sum / 3;
        }

        var start = (int)Math.Floor(alongX ? x : y) - half;
        var before = At(start - 2);
        var after = At(start + (2 * half) + 1);
        if (!(Math.Abs(after - before) >= 12))
        {
            return double.NaN;
        }

        double first = 0;
        for (var index = start; index < start + (2 * half); index++)
        {
            first += 1 - Math.Clamp((At(index) - before) / (after - before), 0, 1);
        }

        return start + first;
    }

    /// <summary>Where the clip's pixels land when the part <paramref name="window"/> of it fills a card.</summary>
    internal static ClipMap Map(Box card, ClipSpec spec, Window window)
    {
        var scaleX = card.Width / (window.Width * spec.Width);
        var scaleY = card.Height / (window.Height * spec.Height);
        return new ClipMap(card.Left - (window.X * spec.Width * scaleX), card.Top - (window.Y * spec.Height * scaleY), scaleX, scaleY);
    }

    /// <summary>Whether a rectangle of clip pixels is inside a window by at least <paramref name="margin"/> clip pixels.</summary>
    private static bool Holds(Window window, ClipSpec spec, double x, double y, double width, double height, double margin) =>
        x - margin >= (window.X * spec.Width) - 1e-9
        && y - margin >= (window.Y * spec.Height) - 1e-9
        && x + width + margin <= ((window.X + window.Width) * spec.Width) + 1e-9
        && y + height + margin <= ((window.Y + window.Height) * spec.Height) + 1e-9;

    /// <summary>Everything outside a card's box is the same in two pictures.</summary>
    internal static void ExpectSameOutside(CheckContext context, Picture picture, Picture reference, Box card, string what)
    {
        RendererChecks.ExpectSame(context, picture, reference, $"{what}, left of the card", 0, 0, card.Left, picture.Height);
        RendererChecks.ExpectSame(context, picture, reference, $"{what}, right of the card", card.Right, 0, picture.Width, picture.Height);
        RendererChecks.ExpectSame(context, picture, reference, $"{what}, above the card", 0, 0, picture.Width, card.Top);
        RendererChecks.ExpectSame(context, picture, reference, $"{what}, below the card", 0, card.Bottom, picture.Width, picture.Height);
    }

    /// <summary>
    /// The card's round corners, 21.6 px in radius, cut the same pixels out of the zoomed picture
    /// as out of the plain one: a pixel in a corner of the card's box is canvas in both, or in neither.
    /// </summary>
    private static void ExpectSameCorners(CheckContext context, Picture zoomed, Picture plain, Box card)
    {
        const int Reach = 24;
        var canvas = Rgb.FromHex(RendererChecks.Orange);
        var canvasPixels = 0;
        var different = 0;
        foreach (var (left, top) in new[] { (card.Left, card.Top), (card.Right - Reach, card.Top), (card.Left, card.Bottom - Reach), (card.Right - Reach, card.Bottom - Reach) })
        {
            for (var y = top; y < top + Reach; y++)
            {
                for (var x = left; x < left + Reach; x++)
                {
                    var isCanvas = plain.Pixel(x, y) == canvas;
                    canvasPixels += isCanvas ? 1 : 0;
                    different += isCanvas == (zoomed.Pixel(x, y) == canvas) ? 0 : 1;
                }
            }
        }

        context.Expect(canvasPixels > 200, $"only {canvasPixels} pixels in the corners of the card's box are canvas, too few for the check to mean anything");
        context.Expect(different == 0, $"{different} pixels in the corners of the card's box are canvas in one of the zoomed and plain frames and not in the other");
        context.Note($"{canvasPixels} pixels in the corners of the card's box are canvas with and without the zoom");
    }

    /// <summary>A layer's box on the canvas, in whole pixels: right and bottom are the first pixels outside it.</summary>
    internal readonly record struct Box(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    /// <summary>A part of the screen, as fractions of it.</summary>
    internal readonly record struct Window(double X, double Y, double Width, double Height)
    {
        public static readonly Window Whole = new(0, 0, 1, 1);

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X:0.0000}, {Y:0.0000}, {Width:0.0000}, {Height:0.0000})");
    }

    /// <summary>What <see cref="Measure"/> found: the window the edges fit, how far they are from their places, and which edges they were.</summary>
    private readonly record struct Measured(Window Window, double Off, string From);

    /// <summary>
    /// Four straight edges of the test pattern, in clip pixels: two upright ones crossed along the
    /// row <paramref name="AcrossY"/>, and two level ones, each crossed down its own column.
    /// </summary>
    private readonly record struct Landmarks(string Name, double Left, double Right, double AcrossY, double Top, double TopX, double Bottom, double BottomX);
}
