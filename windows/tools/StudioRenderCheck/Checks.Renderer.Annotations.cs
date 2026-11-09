using TinyClips.Core.Studio;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>Arrows, callouts and text boxes (section 6.10 of the format), read back from the pixels.</summary>
internal static partial class RendererChecks
{
    private const double AnnotationColorTolerance = 2;

    private static StudioProject WithAnnotations(StudioProject project, params StudioAnnotation[] annotations) =>
        project with { Overlays = project.Overlays with { Annotations = annotations } };

    private static void ExpectColor(CheckContext context, Picture picture, double x, double y, string hex, string what)
    {
        var actual = picture.Pixel((int)Math.Floor(x), (int)Math.Floor(y));
        context.Expect(actual.Distance(Rgb.FromHex(hex)) <= AnnotationColorTolerance, $"{what}: {actual} at ({x:0.#}, {y:0.#}), expected {hex}");
    }

    private static void ExpectUnchanged(CheckContext context, Picture picture, Picture plain, double x, double y, string what)
    {
        var (px, py) = ((int)Math.Floor(x), (int)Math.Floor(y));
        context.Expect(picture.Pixel(px, py).Distance(plain.Pixel(px, py)) <= AnnotationColorTolerance, $"{what}: the picture changed at ({x:0.#}, {y:0.#})");
    }

    /// <summary>A frame arrow: where section 6.10 says, only while it lasts, and over the camera.</summary>
    private static void AnnotationArrow(CheckContext context, RenderBench bench)
    {
        var bubble = StudioLayoutResolver.Resolve(bench.Base(), 0, 1920, 1080).Camera!.Value.Rect;
        var (centerX, centerY) = (bubble.X + (bubble.Width / 2), bubble.Y + (bubble.Height / 2));

        // A level arrow pointing left at the middle of the camera bubble, its tail further right.
        var project = WithAnnotations(bench.Base(), new StudioAnnotation
        {
            Attach = StudioAnnotationAttach.Frame,
            Start = 1,
            End = 2,
            X = centerX / 1920,
            Y = centerY / 1080,
            OffsetX = 0.1,
            OffsetY = 0,
            Style = StudioAnnotationStyle.Blue,
        });
        const string Fill = "#0A84FF";
        var stroke = 1080 * 0.009;
        var head = stroke * 3.2;
        var plain = bench.Render(bench.Base(), time: 1.5);
        var shown = bench.Render(project, time: 1.5);

        ExpectColor(context, shown, centerX + (head * 0.6), centerY, Fill, "the head, over the camera");
        ExpectColor(context, shown, centerX + head + 30, centerY, Fill, "the shaft");
        ExpectUnchanged(context, shown, plain, centerX + head + 30, centerY + stroke + 3, "beside the shaft");
        ExpectUnchanged(context, shown, plain, centerX - 6, centerY, "past the tip");
        ExpectUnchanged(context, bench.Render(project, time: 0.5), plain, centerX + head + 30, centerY, "before its start");
        ExpectUnchanged(context, bench.Render(project, time: 2), plain, centerX + head + 30, centerY, "at its end");

        // An arrow no longer than its head is that head alone: nothing behind its base.
        var shortArrow = WithAnnotations(bench.Base(), new StudioAnnotation
        {
            Attach = StudioAnnotationAttach.Frame,
            Start = 1,
            End = 2,
            X = centerX / 1920,
            Y = centerY / 1080,
            OffsetX = 0.02,
            OffsetY = 0,
            Style = StudioAnnotationStyle.Blue,
        });
        var reach = 1080 * 0.02;
        var shown2 = bench.Render(shortArrow, time: 1.5);
        ExpectColor(context, shown2, centerX + (reach * 0.7), centerY, Fill, "the head of a short arrow");
        ExpectUnchanged(context, shown2, plain, centerX + reach + 3, centerY, "behind a short arrow's head");
    }

    /// <summary>A screen arrow keeps to its place in the recording through a zoom, at the same size, and goes when that place does.</summary>
    private static void AnnotationFollowsZoom(CheckContext context, RenderBench bench)
    {
        var zoom = new StudioZoom { Start = 5, End = 15, Scale = 2, Focus = new StudioZoomFocus { X = 0.5, Y = 0.5 }, EaseIn = 0, EaseOut = 0 };
        var baseProject = bench.Base(StudioLayout.Screen) with { Zooms = [zoom] };
        var inView = WithAnnotations(baseProject, new StudioAnnotation { Start = 0, End = 20, X = 0.4, Y = 0.5, OffsetX = -0.1, OffsetY = 0, Style = StudioAnnotationStyle.White });
        var outOfView = WithAnnotations(baseProject, new StudioAnnotation { Start = 0, End = 20, X = 0.2, Y = 0.5, OffsetX = 0.25, OffsetY = 0, Style = StudioAnnotationStyle.White });
        const string Fill = "#FFFFFF";

        foreach (var time in new[] { 1.0, 10.0 })
        {
            var frame = StudioLayoutResolver.Resolve(baseProject, time, 1920, 1080);
            var screen = frame.Screen!.Value;
            var x = screen.Rect.X + ((0.4 - screen.Source.X) / screen.Source.Width * screen.Rect.Width);
            var y = screen.Rect.Y + ((0.5 - screen.Source.Y) / screen.Source.Height * screen.Rect.Height);
            var picture = bench.Render(inView, time: time);

            // The tail reaches left from the tip; the same distance in pixels however far it is zoomed.
            ExpectColor(context, picture, x - (1080 * 0.1) + 10, y, Fill, $"the shaft at {time} s");
            ExpectColor(context, picture, x - 12, y, Fill, $"the head at {time} s");
        }

        // Drawn while its point is in view, and were it drawn at 10 s its shaft would cross the
        // row looked at below: its tip would be at x = -54 and its tail at x = 216.
        ExpectColor(context, bench.Render(outOfView, time: 1), 600, 540, Fill, "the arrow while its point is in view");

        var plain = bench.Render(baseProject, time: 10);
        var zoomed = bench.Render(outOfView, time: 10);
        for (var x = 100; x < 1820; x += 40)
        {
            ExpectUnchanged(context, zoomed, plain, x, 540, "an arrow whose point is out of view");
        }
    }

    /// <summary>A callout's box, pointer and text; a text box; and that the later of two is on top.</summary>
    private static void AnnotationBoxes(CheckContext context, RenderBench bench)
    {
        var baseProject = bench.Base(StudioLayout.Screen);
        var plain = bench.Render(baseProject, time: 1);
        var callout = new StudioAnnotation
        {
            Kind = StudioAnnotationKinds.Callout,
            Attach = StudioAnnotationAttach.Frame,
            Start = 0,
            End = 9,
            X = 0.5,
            Y = 0.25,
            OffsetX = 0,
            OffsetY = 0.25,
            Text = "IIIII IIIII IIIII",
            Style = StudioAnnotationStyle.Yellow,
        };
        var picture = bench.Render(WithAnnotations(baseProject, callout), time: 1);
        var font = 1080 * 0.036;
        var boxCenterY = 270 + (1080 * 0.25);

        // Under the pointer's base and over the box's padding: the fill, whatever the font's metrics.
        ExpectColor(context, picture, 960, 270 + 100, "#FFD60A", "the pointer");
        var above = boxCenterY - font;
        ExpectColor(context, picture, 960, above - 2, "#FFD60A", "the top of the box");

        var dark = 0;
        for (var x = 760; x < 1160; x++)
        {
            if (picture.Pixel(x, (int)boxCenterY).Distance(Rgb.FromHex("#1C1C1E")) <= 60)
            {
                dark++;
            }
        }

        context.Expect(dark > 20, $"the text was not drawn in its color: {dark} dark pixels across the box");
        ExpectUnchanged(context, picture, plain, 960, 240, "past the pointer's tip");

        // Two boxes at one place: the last in the list hides the first.
        var first = new StudioAnnotation { Kind = StudioAnnotationKinds.Text, Attach = StudioAnnotationAttach.Frame, Start = 0, End = 9, X = 0.5, Y = 0.5, Text = "IIIIIIII", Style = StudioAnnotationStyle.Red };
        var last = first with { Style = StudioAnnotationStyle.Blue };
        var stacked = bench.Render(WithAnnotations(baseProject, first, last), time: 1);
        var edge = 540 - (font * 0.6) - (font * 0.45) + 2;
        ExpectColor(context, stacked, 960, edge + 4, "#0A84FF", "the last of two on top");

        // A kind from a later version draws nothing, and neither does a box with nothing to say.
        var nothing = bench.Render(WithAnnotations(baseProject, first with { Kind = "highlight" }, first with { Text = "   " }), time: 1);
        ExpectUnchanged(context, nothing, plain, 960, edge + 4, "a later kind and an empty box");

        // On the shaft of the arrow a later kind would be if it were read as the default.
        ExpectUnchanged(context, nothing, plain, 895, 605, "a later kind drawn as an arrow");
    }

    /// <summary>A screen annotation fades with a screen that is fading, and a frame annotation does not.</summary>
    private static void AnnotationFades(CheckContext context, RenderBench bench)
    {
        var scenes = new[]
        {
            new StudioScene { Start = 0, Layout = StudioLayout.Screen },
            new StudioScene { Start = 5, Layout = StudioLayout.Camera, Transition = new StudioTransition { Kind = StudioTransitionKind.Morph, Duration = 1 } },
        };
        var baseProject = bench.Base(StudioLayout.Screen) with { Scenes = scenes };
        var frame = StudioLayoutResolver.Resolve(baseProject, 5.5, 1920, 1080);
        if (!context.Expect(frame.Screen is { Opacity: > 0 and < 1 }, "the scene change did not fade the screen"))
        {
            return;
        }

        var screen = frame.Screen!.Value;
        var x = screen.Rect.X + (0.5 * screen.Rect.Width);
        var y = screen.Rect.Y + (0.5 * screen.Rect.Height);
        var attached = new StudioAnnotation { Kind = StudioAnnotationKinds.Text, Start = 0, End = 9, X = 0.5, Y = 0.5, Text = "IIIIIIII", Style = StudioAnnotationStyle.White };
        var font = 1080 * 0.036;
        var sampleY = y - (font * 0.6) - (font * 0.45) + 4;
        var plain = bench.Render(baseProject, time: 5.5);
        var faded = bench.Render(WithAnnotations(baseProject, attached), time: 5.5);
        var fixedBox = bench.Render(WithAnnotations(baseProject, attached with { Attach = StudioAnnotationAttach.Frame, X = x / 1920, Y = y / 1080 }), time: 5.5);

        var under = plain.Pixel((int)x, (int)sampleY);
        var mixed = faded.Pixel((int)x, (int)sampleY);
        var opacity = screen.Opacity;
        var wanted = new Rgb(under.R + ((255 - under.R) * opacity), under.G + ((255 - under.G) * opacity), under.B + ((255 - under.B) * opacity));
        context.Expect(mixed.Distance(wanted) <= 4, $"a screen annotation at opacity {opacity:0.###}: {mixed}, expected {wanted}");
        ExpectColor(context, fixedBox, x, sampleY, "#FFFFFF", "a frame annotation stays whole");
    }

    /// <summary>
    /// A rectangle's fill and outline, and redactions over it: a blur smooths the hard edge it
    /// covers, a pixelation makes square cells of one color each, a solid one is its color, and
    /// none of them changes anything outside its area.
    /// </summary>
    private static void AnnotationAreas(CheckContext context, RenderBench bench)
    {
        var baseProject = bench.Base(StudioLayout.Screen);

        // A white rectangle with a blue outline whose right edge, at x = 960, runs through the redactions.
        var rectangle = new StudioAnnotation
        {
            Kind = StudioAnnotationKinds.Rectangle,
            Attach = StudioAnnotationAttach.Frame,
            Start = 0,
            End = 9,
            X = 0.4,
            Y = 0.5,
            Width = 0.2,
            Height = 0.3,
            ShapeFill = "#FFFFFF",
            ShapeFillOpacity = 1,
            Style = StudioAnnotationStyle.Blue,
        };
        var under = WithAnnotations(baseProject, rectangle);
        var plain = bench.Render(under, time: 1);
        ExpectColor(context, plain, 576, 540, "#0A84FF", "a rectangle's outline");
        ExpectColor(context, plain, 660, 540, "#FFFFFF", "a rectangle's fill");

        var redaction = new StudioAnnotation
        {
            Kind = StudioAnnotationKinds.Redact,
            Attach = StudioAnnotationAttach.Frame,
            Start = 0,
            End = 9,
            X = 0.5,
            Y = 0.5,
            Width = 0.2,
            Height = 0.2,
            RedactLevel = StudioRedactLevel.Heavy,
        };
        const int Left = 768, Top = 432, Right = 1152, Bottom = 648;
        var plainJump = LargestStep(plain, Left + 4, Right - 4, 540);
        context.Expect(plainJump >= 60, $"the edge under the redactions is not sharp enough to check: {plainJump:0.#}");

        var blurred = bench.Render(WithAnnotations(baseProject, rectangle, redaction), time: 1);
        var blurredJump = LargestStep(blurred, Left + 4, Right - 4, 540);
        context.Expect(blurredJump < plainJump * 0.4, $"a blur left the edge sharp: a step of {blurredJump:0.#} against {plainJump:0.#}");

        var pixelated = bench.Render(WithAnnotations(baseProject, rectangle, redaction with { RedactStyle = StudioRedactStyle.Pixelate }), time: 1);
        var cell = 1080 * 0.028;
        var changed = false;
        for (var row = 0; Top + ((row + 1) * cell) <= Bottom; row++)
        {
            for (var column = 0; Left + ((column + 1) * cell) <= Right; column++)
            {
                var (x0, y0) = (Left + (column * cell) + 3, Top + (row * cell) + 3);
                var (x1, y1) = (Left + ((column + 1) * cell) - 4, Top + ((row + 1) * cell) - 4);
                var corner = pixelated.Pixel((int)x0, (int)y0);
                context.Expect(corner.Distance(pixelated.Pixel((int)x1, (int)y1)) <= 3, $"a pixelation's cell at ({x0:0}, {y0:0}) is not one color");
                changed |= corner.Distance(plain.Pixel((int)x0, (int)y0)) > 8;
            }
        }

        context.Expect(changed, "a pixelation changed nothing");

        var solid = bench.Render(WithAnnotations(baseProject, rectangle, redaction with { RedactStyle = StudioRedactStyle.Solid }), time: 1);
        ExpectColor(context, solid, 960, 540, StudioAnnotationGeometry.GetEffectiveColors(redaction).Fill, "a solid redaction");

        foreach (var (picture, what) in new[] { (blurred, "a blur"), (pixelated, "a pixelation"), (solid, "a solid redaction") })
        {
            ExpectUnchanged(context, picture, plain, Left - 3, 540, $"left of {what}");
            ExpectUnchanged(context, picture, plain, Right + 3, 540, $"right of {what}");
            ExpectUnchanged(context, picture, plain, 960, Top - 3, $"above {what}");
            ExpectUnchanged(context, picture, plain, 960, Bottom + 3, $"below {what}");
        }
    }

    private static double LargestStep(Picture picture, int from, int to, int y)
    {
        var largest = 0.0;
        for (var x = from; x < to; x++)
        {
            largest = Math.Max(largest, picture.Pixel(x, y).Distance(picture.Pixel(x + 1, y)));
        }

        return largest;
    }

    private static void AnnotationRotation(CheckContext context, RenderBench bench)
    {
        var project = bench.Base(StudioLayout.Screen);
        var plain = bench.Render(project, time: 1);
        var area = new StudioAnnotation
        {
            Kind = StudioAnnotationKinds.Rectangle, Attach = StudioAnnotationAttach.Frame,
            Start = 0, End = 9, X = 0.5, Y = 0.5, Width = 0.2, Height = 0.08,
            ShapeFill = "#FFFFFF", ShapeFillOpacity = 1, Fill = "#FFFFFF", Rotation = 90,
        };
        foreach (var kind in new[] { StudioAnnotationKinds.Rectangle, StudioAnnotationKinds.Ellipse, StudioAnnotationKinds.Redact })
        {
            var rotated = bench.Render(WithAnnotations(project, area with { Kind = kind, RedactStyle = StudioRedactStyle.Solid }), time: 1);
            ExpectColor(context, rotated, 960, 680, "#FFFFFF", $"{kind} rotated into its vertical extent");
            ExpectUnchanged(context, rotated, plain, 1100, 540, $"{kind} no longer covers its old horizontal extent");
        }

        var arrow = area with { Kind = StudioAnnotationKinds.Arrow, OffsetX = 0.2, OffsetY = 0 };
        var rotatedArrow = bench.Render(WithAnnotations(project, arrow), time: 1);
        ExpectColor(context, rotatedArrow, 960, 650, "#FFFFFF", "the rotated arrow's shaft");
        ExpectUnchanged(context, rotatedArrow, plain, 1070, 540, "the arrow's old shaft");

        var white = area with { X = 0.4, Width = 0.2, Height = 0.3, Rotation = 0 };
        var under = bench.Render(WithAnnotations(project, white), time: 1);
        var redaction = area with
        {
            Kind = StudioAnnotationKinds.Redact, Width = 0.2, Height = 0.2,
            RedactStyle = StudioRedactStyle.Blur, RedactLevel = StudioRedactLevel.Heavy,
        };
        var blurred = bench.Render(WithAnnotations(project, white, redaction), time: 1);
        context.Expect(LargestStep(blurred, 880, 1040, 540) < LargestStep(under, 880, 1040, 540) * 0.4,
            "a rotated blur must still soften the stationary vertical edge");
        context.Expect(blurred.Pixel(900, 450).Distance(blurred.Pixel(900, 630)) <= 4,
            "rotating a redaction must not rotate the pixels under it");

        var diagonal = bench.Render(WithAnnotations(project, white, redaction with { Rotation = 45, RedactStyle = StudioRedactStyle.Solid }), time: 1);
        ExpectColor(context, diagonal, 960, 540, "#FFFFFF", "inside the diagonal redaction");
        ExpectUnchanged(context, diagonal, under, 1160, 680, "inside its bounding box but outside the diagonal mask");
    }

    /// <summary>
    /// An emoji is drawn in its own colors, not as a gray glyph; its shadow darkens what is just
    /// below it and nothing else gets lighter; and an emoji of white space draws nothing.
    /// </summary>
    private static void AnnotationEmoji(CheckContext context, RenderBench bench)
    {
        var baseProject = bench.Base(StudioLayout.Screen);
        var emoji = new StudioAnnotation
        {
            Kind = StudioAnnotationKinds.Emoji,
            Attach = StudioAnnotationAttach.Frame,
            Start = 0,
            End = 9,
            X = 0.5,
            Y = 0.5,
            Text = "👍",
            Size = StudioAnnotationSize.Large,
        };

        // Large: 13% of the shorter side, about 140 pixels, centered on (960, 540).
        var font = 1080 * 0.13;
        var (left, top, right, bottom) = ((int)(960 - font), (int)(540 - font), (int)(960 + font), (int)(540 + font));
        var plain = bench.Render(baseProject, time: 1);
        var drawn = bench.Render(WithAnnotations(baseProject, emoji), time: 1);
        var shadowed = bench.Render(WithAnnotations(baseProject, emoji with { Shadow = true }), time: 1);

        static double Chroma(Rgb color) => Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
        static double Sum(Rgb color) => color.R + color.G + color.B;

        var colorful = 0;
        var darker = 0;
        var darkerBelow = 0;
        var lighter = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var (was, now, withShadow) = (plain.Pixel(x, y), drawn.Pixel(x, y), shadowed.Pixel(x, y));
                if (Chroma(now) > 80 && now.Distance(was) > 40)
                {
                    colorful++;
                }

                var change = Sum(withShadow) - Sum(now);
                if (change < -15)
                {
                    darker++;
                    darkerBelow += y > 540 ? 1 : 0;
                }
                else if (change > 15)
                {
                    lighter++;
                }
            }
        }

        context.Expect(colorful > 2000, $"the emoji is not drawn in color: {colorful} colorful pixels changed");
        context.Expect(darker > 500, $"the shadow darkened only {darker} pixels");
        context.Expect(darkerBelow > darker / 2, $"the shadow is not below the emoji: {darkerBelow} of {darker} darker pixels are in the lower half");
        context.Expect(lighter == 0, $"the shadow made {lighter} pixels lighter");
        ExpectUnchanged(context, drawn, plain, left - 3, 540, "left of the emoji");
        ExpectUnchanged(context, drawn, plain, right + 3, 540, "right of the emoji");

        var blank = bench.Render(WithAnnotations(baseProject, emoji with { Text = " \n\t" }), time: 1);
        ExpectUnchanged(context, blank, plain, 960, 540, "an emoji of white space");

        // A plain emoji and two with shadows in one frame, also while they fade in: once broke the
        // preview with D2DERR_LAYER_ALREADY_IN_USE. The middle one is as it is by itself.
        var several = WithAnnotations(
            baseProject,
            emoji with { X = 0.2 },
            emoji with { Shadow = true },
            emoji with { X = 0.8, Shadow = true, Text = "🔥" });
        bench.Render(several, time: 0.1);
        var together = bench.Render(several, time: 1);
        var differing = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                differing += together.Pixel(x, y).Distance(shadowed.Pixel(x, y)) > 10 ? 1 : 0;
            }
        }

        context.Expect(differing == 0, $"an emoji and its shadow among others differ from it alone in {differing} pixels");
    }

    private static void AnnotationManyEmoji(CheckContext context, RenderBench bench)
    {
        var baseProject = bench.Base(StudioLayout.Screen);
        var first = new StudioAnnotation
        {
            Kind = StudioAnnotationKinds.Emoji,
            Attach = StudioAnnotationAttach.Frame,
            Start = 0,
            End = 9,
            X = 0.2,
            Y = 0.5,
            Text = "👍0",
            Size = StudioAnnotationSize.Large,
            Shadow = true,
        };
        var more = Enumerable.Range(1, 32).Select(index => first with { X = 0.8, Text = $"👍{index}" });
        var alone = bench.Render(WithAnnotations(baseProject, first), time: 1);
        var project = WithAnnotations(baseProject, [first, .. more]);
        var together = bench.Render(project, time: 1);
        var again = bench.Render(project, time: 1);
        var differing = 0;
        var differingAgain = 0;
        for (var y = 330; y < 750; y++)
        {
            for (var x = 170; x < 600; x++)
            {
                var reference = alone.Pixel(x, y);
                differing += together.Pixel(x, y).Distance(reference) > 10 ? 1 : 0;
                differingAgain += again.Pixel(x, y).Distance(reference) > 10 ? 1 : 0;
            }
        }

        context.Expect(differing == 0 && differingAgain == 0, $"the first of 33 shadowed emoji differs from it alone in {differing} pixels, then {differingAgain} pixels");
    }
}
