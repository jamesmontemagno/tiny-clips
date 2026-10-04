using System.Globalization;
using TinyClips.Core.Capture;
using TinyClips.Core.Studio;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>Shadows, click rings and the branding badge.</summary>
internal static partial class RendererChecks
{
    private static readonly Rgb RingColor = new(10, 132, 255);

    /// <summary>The standard normal distribution function, to 1.5e-7 (Abramowitz and Stegun 7.1.26).</summary>
    private static double NormalCdf(double z)
    {
        var x = Math.Abs(z) / Math.Sqrt(2);
        var t = 1 / (1 + (0.3275911 * x));
        var polynomial = 1.061405429;
        polynomial = (polynomial * t) - 1.453152027;
        polynomial = (polynomial * t) + 1.421413741;
        polynomial = (polynomial * t) - 0.284496736;
        polynomial = (polynomial * t) + 0.254829592;
        var erf = 1 - (polynomial * t * Math.Exp(-x * x));
        return 0.5 * (1 + (z < 0 ? -erf : erf));
    }

    private static void Shadows(CheckContext context, RenderBench bench)
    {
        var worst = 0.0;

        // On white, far from a corner, a shadow is a blurred half-plane: at a distance d outside
        // the edge of the shape (which sits offsetY lower than the layer) black is laid on at
        // opacity × (1 - Φ(d / blur)).
        void Profile(Picture picture, (int Left, int Top, int Right, int Bottom) box, double blur, double offsetY, double opacity, string what)
        {
            var middleX = (box.Left + box.Right) / 2;
            var middleY = (box.Top + box.Bottom) / 2;
            var points = new List<(int X, int Y, double Distance)>();
            foreach (var step in new[] { 1, 13, 25, 40, 56, 64 })
            {
                points.Add((middleX, box.Bottom + step - 1, box.Bottom + step - 0.5 - (box.Bottom + offsetY)));
                points.Add((middleX, box.Top - step, box.Top + offsetY - (box.Top - step + 0.5)));
                points.Add((box.Left - step, middleY, step - 0.5));
                points.Add((box.Right + step - 1, middleY, step - 0.5));
            }

            foreach (var (x, y, distance) in points)
            {
                if (x < 0 || y < 0 || x >= picture.Width || y >= picture.Height)
                {
                    continue;
                }

                var want = 255 * (1 - (opacity * (1 - NormalCdf(distance / blur))));
                var got = picture.Pixel(x, y);
                var difference = Math.Max(Math.Abs(got.R - want), Math.Max(Math.Abs(got.G - want), Math.Abs(got.B - want)));
                worst = Math.Max(worst, difference);
                context.Expect(difference <= 2.5, $"{what}: at ({x},{y}), {Show(distance)} px outside the shadow's edge, the white is {got}, want {Show(want)}");
            }
        }

        StudioProject Screen(double shadow) => bench.Base(StudioLayout.Screen) with
        {
            Canvas = new StudioCanvas { Background = new StudioBackground { Style = StudioBackgroundStyle.Solid, Primary = "#FFFFFF" } },
            Screen = new StudioScreenStyle { Shadow = shadow },
        };

        // Full strength on a 1080-line canvas: blur 0.04 × 1080, offset 0.012 × 1080, opacity 0.5.
        var full = StudioLayoutResolver.Resolve(Screen(1), 0, 1920, 1080).Screen!.Value;
        context.Expect(Math.Abs(full.Shadow.Blur - 43.2) < 1e-9 && Math.Abs(full.Shadow.OffsetY - 12.96) < 1e-9 && Math.Abs(full.Shadow.Opacity - 0.5) < 1e-9, $"the full shadow resolves to {full.Shadow}");
        Profile(bench.Render(Screen(1)), Box(full.Rect), 43.2, 12.96, 0.5, "the screen's shadow at full strength");
        Profile(bench.Render(Screen(0.5)), Box(full.Rect), 21.6, 6.48, 0.25, "the screen's shadow at half strength");

        // No shadow at strength 0, and none when the screen's picture is not there.
        var white = new Rgb(255, 255, 255);
        var none = bench.Render(Screen(0));
        var box = Box(full.Rect);
        foreach (var (x, y) in new[] { (box.Left - 1, 540), (box.Right, 540), (960, box.Bottom), (960, box.Top - 1), (box.Left + 2, box.Bottom + 3) })
        {
            ExpectPixel(context, none, x, y, white, 0, "with no shadow, next to the card");
        }

        var absent = bench.Render(Screen(1), withScreen: false);
        var (difference, dx, dy) = Picture.MaxDifference(absent, bench.Render(Screen(0), withScreen: false));
        context.Expect(difference == 0, $"with the screen's picture missing its shadow is still drawn: {difference} of 255 at ({dx},{dy})");

        // The camera's shadow, on the card the camera layout makes of it.
        var cameraProject = bench.Base(StudioLayout.Camera) with
        {
            Canvas = new StudioCanvas { Background = new StudioBackground { Style = StudioBackgroundStyle.Solid, Primary = "#FFFFFF" } },
            Camera = new StudioCameraStyle { Shape = StudioCameraShape.Circle, Mirror = true, Shadow = 1, BorderWidth = 0 },
        };
        var camera = StudioLayoutResolver.Resolve(cameraProject, 0, 1920, 1080).Camera!.Value;
        Profile(bench.Render(cameraProject), Box(camera.Rect), 43.2, 12.96, 0.5, "the camera's shadow at full strength");

        context.Measure(string.Create(CultureInfo.InvariantCulture, $"shadow profiles within {worst:0.0} of 255 of the Gaussian (tolerance 2.5)"));
    }

    private static StudioProject Clicks(RenderBench bench, double size = 200, double opacity = 0.85, bool enabled = true, double cornerRadius = 0.02, StudioRect? crop = null) =>
        bench.Base(StudioLayout.Screen) with
        {
            Screen = new StudioScreenStyle { Shadow = 0, CornerRadius = cornerRadius, Crop = crop },
            Overlays = new StudioOverlays { Clicks = new StudioClickOverlay { Enabled = enabled, Color = "#0A84FF", Size = size, StrokeWidth = 20, Opacity = opacity, Duration = 0.45 } },
        };

    private static StudioEvents ClickAt(double x, double y, double scale = 1, int captureWidth = 1920, params (double X, double Y)[] more)
    {
        var clicks = new List<StudioClickEvent> { new() { T = 1, X = x, Y = y } };
        clicks.AddRange(more.Select(point => new StudioClickEvent { T = 1, X = point.X, Y = point.Y }));
        return new StudioEvents { Capture = new StudioCaptureInfo { Width = captureWidth, Height = 1080, Scale = scale }, Clicks = [.. clicks] };
    }

    /// <summary>
    /// Along six directions from the centre: the plain picture up to 3 px before the ring, the
    /// ring's colour laid over it at <paramref name="alpha"/> across the stroke, the plain picture again after it.
    /// </summary>
    private static void ExpectRing(CheckContext context, Picture picture, Picture plain, double centerX, double centerY, double radius, double stroke, double alpha, string what)
    {
        var inner = radius - (stroke / 2);
        var outer = radius + (stroke / 2);
        var diagonal = Math.Sqrt(0.5);
        foreach (var (dx, dy) in new[] { (1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0), (diagonal, diagonal), (-diagonal, diagonal) })
        {
            foreach (var (distance, onRing) in new[] { (inner - 3, false), (inner + 2.5, true), (radius, true), (outer - 2.5, true), (outer + 3, false) })
            {
                var x = (int)Math.Floor(centerX + (distance * dx));
                var y = (int)Math.Floor(centerY + (distance * dy));
                var under = plain.Pixel(x, y);
                ExpectPixel(context, picture, x, y, onRing ? under.Mix(RingColor, alpha) : under, onRing ? 1.5 : 0, $"{what}, {Show(distance)} px from the click towards ({dx:0.#},{dy:0.#})");
            }
        }
    }

    private static void ClickRing(CheckContext context, RenderBench bench)
    {
        // The card is 1689.6 px wide for a 1920-pixel screen: 0.88 canvas pixels per screen pixel.
        // A 200-point ring with a 20-point stroke is 176 px across and 17.6 px wide; it grows by
        // 0.58 of its diameter and fades out over 0.45 s. The click is at the middle: (960, 540).
        var project = Clicks(bench);
        var plain = bench.Render(project, time: 1);
        var events = ClickAt(0.5, 0.5);

        ExpectRing(context, bench.Render(project, time: 1, events: events), plain, 960, 540, 88, 17.6, 0.85, "at the click");
        var halfway = bench.Render(project, time: 1.225, events: events);
        ExpectRing(context, halfway, plain, 960, 540, 88 + (176 * 0.58 * 0.5), 17.6, 0.425, "halfway through");
        ExpectRing(context, bench.Render(project, time: 1.36, events: events), plain, 960, 540, 88 + (176 * 0.58 * 0.8), 17.6, 0.2 * 0.85, "at 0.8 of the way");

        foreach (var time in new[] { 0.999, 1.45, 1.4501, 3.0 })
        {
            ExpectSame(context, bench.Render(project, time: time, events: events), plain, $"at {time} s, outside the ring's 0.45 s");
        }

        ExpectSame(context, bench.Render(Clicks(bench, enabled: false), time: 1.225, events: events), plain, "with click rings switched off");

        // Opacity is limited to 1: at the click the ring is its colour and nothing else.
        ExpectRing(context, bench.Render(Clicks(bench, opacity: 3), time: 1, events: events), plain, 960, 540, 88, 17.6, 1, "with an opacity above 1");

        // Two canvas pixels per screen pixel of ring: a capture at scale 2, or a video twice as wide as the capture.
        var doubled = bench.Render(project, time: 1.225, events: ClickAt(0.5, 0.5, scale: 2));
        ExpectRing(context, doubled, plain, 960, 540, 2 * (88 + (176 * 0.58 * 0.5)), 35.2, 0.425, "at capture scale 2");
        ExpectSame(context, bench.Render(project, time: 1.225, events: ClickAt(0.5, 0.5, captureWidth: 960)), doubled, "a capture half as wide as the video against a capture at scale 2");
        ExpectSame(context, bench.Render(project, time: 1.225, events: ClickAt(0.5, 0.5, captureWidth: 0)), halfway, "a capture with no width against one as wide as the video");

        // Two clicks at once.
        var two = bench.Render(project, time: 1, events: ClickAt(0.3, 0.6, more: (0.7, 0.3)));
        var left = (X: 115.2 + (0.3 * 1689.6), Y: 64.8 + (0.6 * 950.4));
        var right = (X: 115.2 + (0.7 * 1689.6), Y: 64.8 + (0.3 * 950.4));
        ExpectRing(context, two, plain, left.X, left.Y, 88, 17.6, 0.85, "the first of two clicks");
        ExpectRing(context, two, plain, right.X, right.Y, 88, 17.6, 0.85, "the second of two clicks");

        // With the middle half of the screen shown, the same card holds 960 screen pixels: 1.76 px each.
        var crop = new StudioRect(0.25, 0.25, 0.5, 0.5);
        var croppedProject = Clicks(bench, crop: crop);
        var croppedPlain = bench.Render(croppedProject, time: 1);
        ExpectRing(context, bench.Render(croppedProject, time: 1.225, events: events), croppedPlain, 960, 540, 2 * (88 + (176 * 0.58 * 0.5)), 35.2, 0.425, "on a cropped screen");

        // A click on a part of the screen that is cropped away is not drawn, however near the edge its ring would reach.
        ExpectSame(context, bench.Render(croppedProject, time: 1, events: ClickAt(0.24, 0.5)), croppedPlain, "a click just outside the crop");
    }

    private static void ClickRingClip(CheckContext context, RenderBench bench)
    {
        // A card with 108 px corners (0.1 of the short side) and a click 60 px in from its top-left
        // corner. At the moment of the click the ring is 70.7 px in radius and 17.6 px wide, so
        // it runs over the rounded corner and over the card's left and top edges.
        var events = ClickAt(60 / 1689.6, 60 / 950.4);
        var centerX = 115.2 + 60;
        var centerY = 64.8 + 60;
        const double Radius = 160.7 * 0.88 / 2;

        foreach (var cornerRadius in new[] { 0.1, 0.0 })
        {
            var project = Clicks(bench, size: 160.7, cornerRadius: cornerRadius);
            var plain = bench.Render(project, time: 1);
            var picture = bench.Render(project, time: 1, events: events);
            var what = cornerRadius > 0 ? "rounded card" : "square card";

            // Nothing outside the card's box changes.
            ExpectSame(context, picture, plain, $"{what}: left of the card", 0, 0, 115, 1080);
            ExpectSame(context, picture, plain, $"{what}: above the card", 0, 0, 1920, 65);

            // Where the card's box shows background because the corner is rounded, it still does.
            var changed = 0;
            var ringPixels = 0;
            for (var y = 65; y < 65 + 110; y++)
            {
                for (var x = 115; x < 115 + 110; x++)
                {
                    var distance = Math.Sqrt(Math.Pow(x + 0.5 - centerX, 2) + Math.Pow(y + 0.5 - centerY, 2));
                    var onRing = Math.Abs(distance - Radius) < (17.6 / 2) - 1;
                    if (plain.Pixel(x, y) == OrangeRgb)
                    {
                        ringPixels += onRing ? 1 : 0;
                        if (picture.Pixel(x, y) != OrangeRgb)
                        {
                            changed++;
                        }
                    }
                }
            }

            context.Expect(changed == 0, $"{what}: the ring is drawn on {changed} background pixels inside the card's box");
            if (cornerRadius > 0)
            {
                context.Expect(ringPixels > 200, $"{what}: only {ringPixels} background pixels lie under the ring, too few for the check to mean anything");
                context.Note($"{ringPixels} background pixels in the rounded corner lie under the ring's path, and none is touched");

                // Named points: on the ring's path, in the card's box, outside its rounded corner.
                foreach (var degrees in new[] { 215, 225, 232 })
                {
                    var x = centerX + (Radius * Math.Cos(degrees * Math.PI / 180));
                    var y = centerY + (Radius * Math.Sin(degrees * Math.PI / 180));
                    ExpectPixel(context, picture, x, y, OrangeRgb, 0, $"{what}: on the ring's path at {degrees}°, outside the corner");
                }

                // On the path and 3.7 px inside the corner's arc.
                ExpectPixel(context, picture, 119, 165, plain.Pixel(119, 165).Mix(RingColor, 0.85), 1.5, $"{what}: on the ring's path just inside the corner");
            }

            // Inside the card the ring is there: to the right of the click, and below it.
            foreach (var (dx, dy) in new[] { (1.0, 0.0), (0.0, 1.0), (Math.Sqrt(0.5), Math.Sqrt(0.5)) })
            {
                var x = (int)Math.Floor(centerX + (Radius * dx));
                var y = (int)Math.Floor(centerY + (Radius * dy));
                ExpectPixel(context, picture, x, y, plain.Pixel(x, y).Mix(RingColor, 0.85), 1.5, $"{what}: the ring inside the card towards ({dx:0.#},{dy:0.#})");
            }
        }
    }

    private static void Badge(CheckContext context, RenderBench bench)
    {
        foreach (var (width, height) in new[] { (1920, 1080), (1280, 720) })
        {
            var off = bench.Render(bench.Base(), width, height);
            var on = bench.Render(bench.Base() with { Overlays = new StudioOverlays { Branding = true } }, width, height);

            // What the recorder puts on a frame of this size: its own compositor, on the frame without the badge.
            var compositor = new BrandingOverlayCompositor();
            if (!context.Expect(compositor.TryGetBadge(height, out _, out var badgeWidth, out var badgeHeight, out var margin), "the recorder's badge could not be made"))
            {
                return;
            }

            var expected = (byte[])off.Bgra.Clone();
            compositor.Draw(expected, width, height);
            ExpectSame(context, on, Picture.FromBgra(expected, width, height), $"{width}×{height}: the frame with the badge against the recorder's badge on the frame without", tolerance: 2);

            var (inBadge, _, _) = Picture.MaxDifference(on, off, width - badgeWidth - margin, height - badgeHeight - margin, width - margin, height - margin);
            context.Expect(inBadge > 60, $"{width}×{height}: the badge changes its corner by only {inBadge} of 255");
            ExpectSame(context, on, off, $"{width}×{height}: above the badge", 0, 0, width, height - badgeHeight - margin);
            ExpectSame(context, on, off, $"{width}×{height}: left of the badge", 0, 0, width - badgeWidth - margin, height);
            context.Note($"{width}×{height}: a {badgeWidth}×{badgeHeight} badge {margin} px from the bottom-right corner");
        }
    }
}
