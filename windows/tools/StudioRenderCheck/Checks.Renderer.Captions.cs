using TinyClips.Core.Studio;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>Captions drawn into the picture: section 15.4 of the project format.</summary>
internal static partial class RendererChecks
{
    private const string ShortCaption = "Hxg";
    private const string OneLongLine = "Captions stay above everything else in the picture";

    private static StudioProject Captioned(StudioProject project, string text, StudioCaptionOverlay? overlay = null) => project with
    {
        Captions = new StudioCaptions { Cues = [new StudioCaptionCue { Id = "cue", Start = 1, End = 2, Text = text }] },
        Overlays = project.Overlays with { Captions = overlay ?? new StudioCaptionOverlay { Enabled = true } },
    };

    /// <summary>The smallest box around every pixel that differs, or null when none does.</summary>
    private static (int Left, int Top, int Right, int Bottom)? ChangedBox(Picture a, Picture b)
    {
        var (first, second) = (a.Bgra, b.Bgra);
        int left = a.Width, top = a.Height, right = -1, bottom = -1;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var at = ((y * a.Width) + x) * 4;
                if (first[at] != second[at] || first[at + 1] != second[at + 1] || first[at + 2] != second[at + 2])
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x + 1);
                    bottom = Math.Max(bottom, y + 1);
                }
            }
        }

        return right < 0 ? null : (left, top, right, bottom);
    }

    /// <summary>How many pixels inside <paramref name="box"/> are within a little of <paramref name="color"/>.</summary>
    private static int CountNear(Picture picture, (int Left, int Top, int Right, int Bottom) box, Rgb color, double tolerance = 3)
    {
        var count = 0;
        for (var y = box.Top; y < box.Bottom; y++)
        {
            for (var x = box.Left; x < box.Right; x++)
            {
                if (picture.Pixel(x, y).Distance(color) <= tolerance)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static void SaveCaption(CheckContext context, Picture picture, string name) =>
        picture.SavePng(Path.Combine(context.Folder(), name + ".png"));

    private static void CaptionTimes(CheckContext context, RenderBench bench)
    {
        var plain = bench.Base();
        var on = Captioned(plain, ShortCaption);
        var before = bench.Render(on, time: 0.9);
        ExpectSame(context, before, bench.Render(plain, time: 0.9), "before the caption starts");
        var after = bench.Render(on, time: 2.1);
        ExpectSame(context, after, bench.Render(plain, time: 2.1), "after the caption ends");

        var off = Captioned(plain, ShortCaption, new StudioCaptionOverlay { Enabled = false });
        var without = bench.Render(plain, time: 1.5);
        ExpectSame(context, bench.Render(off, time: 1.5), without, "with burn-in switched off");

        var shown = bench.Render(on, time: 1.5);
        if (context.Expect(ChangedBox(shown, without) is not null, "the caption draws nothing while it shows"))
        {
            SaveCaption(context, shown, "default");
        }

        // A cut before the caption moves it in the video, and it stays on its words.
        var cut = on with { Edits = new StudioEdits { Cuts = [new StudioTimeRange { Start = 0.2, End = 0.6 }] } };
        context.Expect(ChangedBox(bench.Render(cut, time: 1.5), without) is not null, "a cut earlier in the video loses the caption");

        // A faster piece has no sound, so it has no captions (section 15.2).
        var fast = on with { Edits = new StudioEdits { Speed = [new StudioSpeedRange { Start = 1, End = 2, Rate = 2 }] } };
        ExpectSame(context, bench.Render(fast, time: 1.5), without, "in a part of the video played faster");
    }

    private static void CaptionSizes(CheckContext context, RenderBench bench)
    {
        var plain = bench.Base();
        int? Height(StudioCaptionSize size, int width, int height, StudioCaptionPosition position = StudioCaptionPosition.Bottom)
        {
            var project = Captioned(plain, ShortCaption, new StudioCaptionOverlay { Enabled = true, Size = size, Position = position });
            return ChangedBox(bench.Render(project, width, height, 1.5), bench.Render(plain, width, height, 1.5)) is { } box ? box.Bottom - box.Top : null;
        }

        var small = Height(StudioCaptionSize.Small, 1920, 1080);
        var medium = Height(StudioCaptionSize.Medium, 1920, 1080);
        var large = Height(StudioCaptionSize.Large, 1920, 1080);
        if (!context.Expect(small > 0 && medium > small && large > medium, $"sizes small {small}, medium {medium}, large {large} do not grow"))
        {
            return;
        }

        var ratio = large!.Value / (double)small!.Value;
        context.Expect(Math.Abs(ratio - (24.0 / 14)) < 0.15, $"large is {ratio:0.00} times small, not about 1.71");

        var half = Height(StudioCaptionSize.Medium, 960, 540);
        context.Expect(Math.Abs((half!.Value * 2.0) - medium!.Value) <= 6, $"at half the canvas a caption is {half} tall, against {medium} at the full size");

        // The short side decides, so a portrait canvas has captions of the same size.
        var portrait = Height(StudioCaptionSize.Medium, 1080, 1920);
        context.Expect(Math.Abs(portrait!.Value - medium.Value) <= 2, $"on a portrait canvas a caption is {portrait} tall, against {medium}");
        context.Note($"ink heights at 1080p: small {small}, medium {medium}, large {large}");
    }

    private static void CaptionPlaces(CheckContext context, RenderBench bench)
    {
        foreach (var (width, height) in new[] { (1920, 1080), (1280, 720), (1080, 1920) })
        {
            var plain = bench.Base();
            var without = bench.Render(plain, width, height, 1.5);
            var unit = Math.Min(width, height) / 288.0;
            var margin = 12 * unit;
            var textHeight = 18 * unit;
            var outline = 1.2 * unit;
            foreach (var position in new[] { StudioCaptionPosition.Bottom, StudioCaptionPosition.Top })
            {
                var overlay = new StudioCaptionOverlay { Enabled = true, Position = position };
                var picture = bench.Render(Captioned(plain, ShortCaption, overlay), width, height, 1.5);
                if (ChangedBox(picture, without) is not { } box)
                {
                    context.Fail($"{width}×{height}, {position}: nothing was drawn");
                    continue;
                }

                // The ink is inside the line's box, so it is near the margin and never past it by more than the outline.
                var (near, label) = position == StudioCaptionPosition.Bottom ? (height - box.Bottom, "bottom") : (box.Top, "top");
                context.Expect(near >= margin - outline - 1 && near <= margin + (textHeight * 0.7), $"{width}×{height}: the ink is {near} px from the {label}, for a margin of {margin:0}");
                var middle = (box.Left + box.Right) / 2.0;
                context.Expect(Math.Abs(middle - (width / 2.0)) <= 12 * unit, $"{width}×{height}, {label}: the ink's middle is at {middle}, on a canvas {width} wide");
            }
        }
    }

    private static void CaptionWrapping(CheckContext context, RenderBench bench)
    {
        const int Width = 1920;
        const int Height = 1080;
        var plain = bench.Base();
        var without = bench.Render(plain, Width, Height, 1.5);
        (int Left, int Top, int Right, int Bottom)? Box(string text) => ChangedBox(bench.Render(Captioned(plain, text), Width, Height, 1.5), without);

        var one = Box("Hxg");
        var two = Box("Hxg\nHxg");
        if (context.Expect(one is not null && two is not null, "a short caption drew nothing"))
        {
            var (oneTall, twoTall) = (one!.Value.Bottom - one.Value.Top, two!.Value.Bottom - two.Value.Top);
            context.Expect(twoTall > oneTall * 1.7, $"a caption of two lines is {twoTall} tall, against {oneTall} for one");
        }

        var longText = string.Join(' ', Enumerable.Repeat(OneLongLine, 4));
        var picture = bench.Render(Captioned(plain, longText), Width, Height, 1.5);
        SaveCaption(context, picture, "wrapped");
        if (ChangedBox(picture, without) is { } wrapped)
        {
            var unit = Height / 288.0;
            var allowed = (Width * 0.9) + (2 * 1.2 * unit) + 2;
            context.Expect(wrapped.Right - wrapped.Left <= allowed, $"a long caption is {wrapped.Right - wrapped.Left} wide, past 90 percent of the canvas");
            context.Expect(wrapped.Bottom - wrapped.Top > 18 * unit * 2, "a long caption did not wrap");
            context.Note($"long caption: {wrapped.Right - wrapped.Left}×{wrapped.Bottom - wrapped.Top}");
        }
        else
        {
            context.Fail("a long caption drew nothing");
        }
    }

    private static void CaptionLooks(CheckContext context, RenderBench bench)
    {
        const int Width = 1920;
        const int Height = 1080;
        var plain = bench.Base();
        var without = bench.Render(plain, Width, Height, 1.5);
        (Picture Picture, (int Left, int Top, int Right, int Bottom) Box)? Look(StudioCaptionAppearance appearance)
        {
            var overlay = new StudioCaptionOverlay { Enabled = true, Appearance = appearance, Size = StudioCaptionSize.Large };
            var picture = bench.Render(Captioned(plain, "Captions look right", overlay), Width, Height, 1.5);
            SaveCaption(context, picture, appearance.ToString());
            return ChangedBox(picture, without) is { } box ? (picture, box) : null;
        }

        var white = new Rgb(255, 255, 255);
        var black = new Rgb(0, 0, 0);
        if (Look(StudioCaptionAppearance.ClassicOutline) is { } classic)
        {
            context.Expect(CountNear(classic.Picture, classic.Box, white) > 500, "classic outline: the letters are not white");
            context.Expect(CountNear(classic.Picture, classic.Box, black) > 500, "classic outline: there is no black outline");
        }
        else
        {
            context.Fail("classic outline drew nothing");
        }

        if (Look(StudioCaptionAppearance.ContrastPanel) is { } panel)
        {
            context.Expect(CountNear(panel.Picture, panel.Box, white) > 500, "contrast panel: the letters are not white");

            // A corner of the panel has no letter on it: the frame without captions at 40 percent.
            var (x, y) = (panel.Box.Left + 2, panel.Box.Top + 2);
            var expected = without.Pixel(x, y).Mix(black, 0.6);
            context.Expect(panel.Picture.Pixel(x, y).Distance(expected) <= 3, $"contrast panel: its corner is {panel.Picture.Pixel(x, y)}, not {expected}");
            context.Expect(CountNear(panel.Picture, panel.Box, black, 1) == 0, "contrast panel: something is drawn in solid black, though it has no outline");
        }
        else
        {
            context.Fail("contrast panel drew nothing");
        }

        if (Look(StudioCaptionAppearance.BoldImpact) is { } bold)
        {
            context.Expect(CountNear(bold.Picture, bold.Box, new Rgb(0xFF, 0xD7, 0x00)) > 500, "bold impact: the letters are not gold");
            context.Expect(CountNear(bold.Picture, bold.Box, black) > 500, "bold impact: there is no black outline");
        }
        else
        {
            context.Fail("bold impact drew nothing");
        }

        if (Look(StudioCaptionAppearance.CleanShadow) is { } shadow)
        {
            context.Expect(CountNear(shadow.Picture, shadow.Box, new Rgb(0xF5, 0xF5, 0xF5)) > 500, "clean shadow: the letters are not off-white");

            // Two sheets at 50 percent, the shadow and the outline, leave 25 percent at the darkest.
            // Anything darker means one sheet lay on itself where two letters overlap.
            var darkest = double.MaxValue;
            var darkestExpected = 0.0;
            for (var y = shadow.Box.Top; y < shadow.Box.Bottom; y++)
            {
                for (var x = shadow.Box.Left; x < shadow.Box.Right; x++)
                {
                    var under = without.Pixel(x, y);
                    if (under.Luma > 60)
                    {
                        var ratio = shadow.Picture.Pixel(x, y).Luma / under.Luma;
                        if (ratio < darkest)
                        {
                            darkest = ratio;
                            darkestExpected = under.Luma;
                        }
                    }
                }
            }

            context.Expect(darkest >= 0.25 - (3 / darkestExpected), $"clean shadow: a pixel is {darkest:0.00} of what is under it, darker than two half-black sheets");
            context.Expect(darkest < 0.55, $"clean shadow: the darkest pixel is only {darkest:0.00} of what is under it, so there is no half-black shadow");
        }
        else
        {
            context.Fail("clean shadow drew nothing");
        }
    }

    private static void CaptionOnTop(CheckContext context, RenderBench bench)
    {
        const int Width = 1920;
        const int Height = 1080;
        var white = new Rgb(255, 255, 255);

        // Over a camera that fills the canvas, or under a zoom, the caption is in the same place.
        var plain = bench.Base();
        var reference = ChangedBox(bench.Render(Captioned(plain, ShortCaption), Width, Height, 1.5), bench.Render(plain, Width, Height, 1.5));
        void SamePlace(StudioProject project, string what)
        {
            var picture = bench.Render(Captioned(project, ShortCaption), Width, Height, 1.5);
            SaveCaption(context, picture, what);
            var box = ChangedBox(picture, bench.Render(project, Width, Height, 1.5));
            context.Expect(
                box is { } found && reference is { } expected
                    && Math.Abs(found.Left - expected.Left) <= 2 && Math.Abs(found.Right - expected.Right) <= 2
                    && Math.Abs(found.Top - expected.Top) <= 2 && Math.Abs(found.Bottom - expected.Bottom) <= 2,
                $"{what}: the caption is at {box}, not {reference}");
        }

        SamePlace(bench.Base(StudioLayout.Camera), "over-the-camera");
        SamePlace(
            plain with { Zooms = [new StudioZoom { Start = 0, End = 3, Scale = 2 }] },
            "under-a-zoom");

        // Where a caption crosses the badge it is on top: a letter stays pure white over the
        // badge's half-black pill.
        var branded = plain with { Overlays = plain.Overlays with { Branding = true } };
        var overBadge = bench.Render(Captioned(branded, OneLongLine), Width, Height, 1.5);
        var overNothing = bench.Render(Captioned(plain, OneLongLine), Width, Height, 1.5);
        SaveCaption(context, overBadge, "over-the-badge");
        if (ChangedBox(bench.Render(branded, Width, Height, 1.5), bench.Render(plain, Width, Height, 1.5)) is not { } badge)
        {
            context.Fail("the badge drew nothing");
            return;
        }

        var letters = 0;
        var beneath = 0;
        for (var y = badge.Top; y < badge.Bottom; y++)
        {
            for (var x = badge.Left; x < badge.Right; x++)
            {
                if (overNothing.Pixel(x, y).Distance(white) <= 1)
                {
                    letters++;
                    beneath += overBadge.Pixel(x, y).Distance(white) <= 3 ? 0 : 1;
                }
            }
        }

        context.Expect(letters > 20, $"the long caption crosses the badge with only {letters} pixels of its letters");
        context.Expect(beneath == 0, $"{beneath} of {letters} pixels of the letters are under the badge");
    }

    private static void CaptionScripts(CheckContext context, RenderBench bench)
    {
        const int Width = 1920;
        const int Height = 1080;
        var plain = bench.Base();
        var without = bench.Render(plain, Width, Height, 1.5);
        foreach (var (name, language, text) in new[]
        {
            ("accents", "es", "\u00bfQu\u00e9 ta\u00f1o tiene?"),
            ("japanese", "ja", "\u3053\u308c\u306f\u5b57\u5e55\u3067\u3059"),
            ("arabic", "ar", "\u0645\u0631\u062d\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645"),
            ("hindi", "hi", "\u0928\u092e\u0938\u094d\u0924\u0947 \u0926\u0941\u0928\u093f\u092f\u093e"),
        })
        {
            var project = Captioned(plain, text);
            var picture = bench.Render(project with { Captions = project.Captions with { Language = language } }, Width, Height, 1.5);
            SaveCaption(context, picture, name);
            context.Expect(ChangedBox(picture, without) is not null, $"{name}: nothing was drawn");
        }
    }
}
