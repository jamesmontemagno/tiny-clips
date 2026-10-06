using TinyClips.App.Models.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Capture;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 3. The inspector: every control changes the picture as it should, Undo takes the change back
// and Redo brings it again.
internal sealed partial class WindowChecks
{
    // The frame the picture rests on while the inspector is checked.
    private const int InspectorFrame = 90;

    private static readonly Rgb Lemon = new(255, 224, 64);
    private static readonly Rgb White = new(255, 255, 255);
    private static readonly Rgb Black = new(0, 0, 0);

    /// <summary>How a picture compares with the one it should be again, where an edit had changed it and elsewhere.</summary>
    /// <param name="Changed">The pixels the edit changed.</param>
    /// <param name="NotBack">Of those, the ones that are not as in the picture it should be again.</param>
    /// <param name="Elsewhere">The pixels the edit left alone that differ now.</param>
    /// <param name="Area">The pixels compared.</param>
    private readonly record struct Return(int Changed, int NotBack, int Elsewhere, int Area)
    {
        /// <summary>
        /// The picture is back when nearly every pixel the edit changed is as it was, and little
        /// differs elsewhere. Not every pixel: the preview keeps the texture a video frame is
        /// copied into while it is between one and two times the size needed, so the same frame
        /// in the same place can be scaled from another size than last time, which shows along
        /// its edges.
        /// </summary>
        public bool IsBack => Changed >= 50 && NotBack <= 4 + (Changed / 5) && Elsewhere <= Area / 20;

        public override string ToString() => $"{NotBack} of the {Changed} pixels the edit changed are not back, and {Elsewhere} of the other {Area - Changed} differ";
    }

    /// <summary>
    /// Compares a picture with the one it should be again, pixel by pixel inside a box.
    /// </summary>
    /// <param name="wanted">The picture it should be again.</param>
    /// <param name="other">The picture on the other side of the edit.</param>
    /// <param name="now">The picture now.</param>
    private static Return Compare(Shot wanted, Shot other, Shot now, Box box, int threshold = 12)
    {
        if (wanted.Width != other.Width || wanted.Height != other.Height || wanted.Width != now.Width || wanted.Height != now.Height)
        {
            return new Return(0, 0, int.MaxValue, 0);
        }

        int changed = 0, notBack = 0, elsewhere = 0, area = 0;
        for (var y = Math.Max(0, box.Y); y < Math.Min(wanted.Height, box.Bottom); y++)
        {
            var row = y * wanted.Width * 4;
            for (var x = Math.Max(0, box.X); x < Math.Min(wanted.Width, box.Right); x++)
            {
                var at = row + (x * 4);
                var edited = Differs(wanted.Bgra, other.Bgra, at, threshold);
                var differs = Differs(wanted.Bgra, now.Bgra, at, threshold);
                area++;
                if (edited)
                {
                    changed++;
                    notBack += differs ? 1 : 0;
                }
                else
                {
                    elsewhere += differs ? 1 : 0;
                }
            }
        }

        return new Return(changed, notBack, elsewhere, area);

        static bool Differs(byte[] a, byte[] b, int at, int threshold) =>
            Math.Abs(a[at] - b[at]) > threshold || Math.Abs(a[at + 1] - b[at + 1]) > threshold || Math.Abs(a[at + 2] - b[at + 2]) > threshold;
    }

    /// <summary>The smallest box around two boxes.</summary>
    private static Box Around(Box a, Box b)
    {
        var (left, top) = (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
        return new Box(left, top, Math.Max(a.Right, b.Right) - left, Math.Max(a.Bottom, b.Bottom) - top);
    }

    /// <summary>
    /// One edit, checked three times: the picture changes as it should, Undo gives back the
    /// picture from before where the edit had changed it, and Redo gives back the changed one.
    /// </summary>
    /// <param name="verdict">Given the picture before and the picture after: null when it changed as it should, otherwise what is wrong.</param>
    /// <param name="control">Null when the control itself reports the new value, otherwise what it reports.</param>
    private bool Edit(Editor editor, string name, Func<bool> apply, Func<StudioProject, StudioProject> expect, Func<Sight, Sight, string?> verdict, Func<string?>? control = null)
    {
        Timeline.Mark($"3: {name}");
        var projectBefore = editor.Expected;
        var shownBefore = Both(editor, InspectorFrame);
        var before = LookFor(editor, s => s.Shown == shownBefore, 3);
        if (before is null || before.Shown != shownBefore)
        {
            return _report.Check(name, false, $"before the edit the picture was {before?.Shown} and should have been {shownBefore}");
        }

        var applied = apply();
        Expect(editor, expect);
        var projectAfter = editor.Expected;
        var wanted = Both(editor, InspectorFrame);
        var after = LookFor(editor, s => s.Shown == wanted && verdict(before, s) is null, 3);
        var wrong = after is null ? "no screenshot" : after.Shown != wanted ? $"the picture shows {after.Shown} and should show {wanted}" : verdict(before, after);
        var reported = control?.Invoke();

        // Both canvases, for an edit that changes the shape of the canvas.
        var box = after is null ? before.Canvas : Around(before.Canvas, after.Canvas);
        var undoPressed = Invoke(editor, "StudioUndoButton");
        editor.Expected = projectBefore;
        var undone = after is null ? null : LookFor(editor, s => s.Shown == shownBefore && Compare(before.Shot, after.Shot, s.Shot, box).IsBack, 3);
        var isUndone = undoPressed && after is not null && undone is not null && undone.Shown == shownBefore && Compare(before.Shot, after.Shot, undone.Shot, box).IsBack;

        var redoPressed = Invoke(editor, "StudioRedoButton");
        editor.Expected = projectAfter;
        var redone = after is null ? null : LookFor(editor, s => s.Shown == wanted && Compare(after.Shot, before.Shot, s.Shot, box).IsBack, 3);
        var isRedone = redoPressed && after is not null && redone is not null && redone.Shown == wanted && Compare(after.Shot, before.Shot, redone.Shot, box).IsBack;

        string?[] problems =
        [
            applied ? null : "the control could not be set",
            wrong,
            reported is null ? null : $"the control reports {reported}",
            isUndone ? null : $"after Undo {(undone is null || after is null ? "there was no screenshot" : $"the picture shows {undone.Shown}, and {Compare(before.Shot, after.Shot, undone.Shot, box)}")}",
            isRedone ? null : $"after Redo {(redone is null || after is null ? "there was no screenshot" : $"the picture shows {redone.Shown}, and {Compare(after.Shot, before.Shot, redone.Shot, box)}")}",
        ];
        var detail = string.Join("; ", problems.Where(problem => problem is not null));
        if (detail.Length > 0)
        {
            detail += Kept(before.Shot, $"inspector-{Slug(name)}-before") + Kept(after?.Shot, $"inspector-{Slug(name)}-after");
        }

        return _report.Check(
            $"{name}: the picture changes as it should, Undo takes it back and Redo brings it again",
            detail.Length == 0,
            detail.Length == 0 ? null : detail);
    }

    private void Inspector()
    {
        Timeline.Mark("3: inspector");
        if (OpenReady(NewCameraProject("Inspector"), "inspector") is not { } editor)
        {
            return;
        }

        SetSlider(editor, "StudioPlayhead", MiddleOf(InspectorFrame));
        var rest = LookFor(editor, s => s.Shown == Both(editor, InspectorFrame), 5);
        var undo = Find(editor, "StudioUndoButton");
        var redo = Find(editor, "StudioRedoButton");
        if (!_report.Check(
            "before any edit the picture rests where the playhead was put, and Undo and Redo are disabled",
            rest?.Shown == Both(editor, InspectorFrame) && undo is { IsEnabled: false } && redo is { IsEnabled: false },
            $"{rest?.Shown}; Undo enabled {undo?.IsEnabled}, Redo enabled {redo?.IsEnabled}"))
        {
            return;
        }

        Backgrounds(editor);
        ScreenStyle(editor);
        var under = Layouts(editor);
        CameraStyle(editor, under);
        SideBySide(editor);
        CameraBorder(editor);
        CanvasShapes(editor);
        Extras(editor);
        Gestures(editor);
        DefaultLook(editor);
        WhatWasSaved(editor);
        SliderEnds(editor);
        CloseQuietly(editor);
    }

    /// <summary>
    /// Every slider of the inspector takes its lowest and its highest value through UI
    /// Automation, which is how a screen reader sets a slider. Last, because it leaves the
    /// project with values the other checks do not expect.
    /// </summary>
    private void SliderEnds(Editor editor)
    {
        Timeline.Mark("3: the lowest and the highest value of every slider");
        var refused = new List<string>();
        var sliders = 0;
        Try("StudioPaddingSlider", "StudioScreenCornerRadiusSlider", "StudioScreenShadowSlider", "StudioCameraCornerRadiusSlider", "StudioCameraSizeSlider", "StudioCameraOffsetXSlider", "StudioCameraOffsetYSlider", "StudioCameraBorderSlider", "StudioCameraShadowSlider");
        Find(editor, "StudioLayoutSideBySide")?.Select();
        Try("StudioCameraShareSlider");

        // The crops last: one edge cut all the way leaves a sliver of a canvas.
        Try("StudioScreenCropLeftSlider", "StudioScreenCropTopSlider", "StudioScreenCropRightSlider", "StudioScreenCropBottomSlider");
        Try("StudioCameraCropLeftSlider", "StudioCameraCropTopSlider", "StudioCameraCropRightSlider", "StudioCameraCropBottomSlider");
        _report.Check(
            "every slider of the inspector can be set to its lowest and to its highest value through UI Automation",
            refused.Count == 0 && sliders == 18,
            refused.Count == 0 ? $"{sliders} sliders" : string.Join("; ", refused));

        void Try(params string[] ids)
        {
            foreach (var id in ids)
            {
                if (Find(editor, id) is not { Range: { } range } slider)
                {
                    refused.Add($"{id} was not found");
                    continue;
                }

                sliders++;
                foreach (var end in new[] { range.Maximum, range.Minimum })
                {
                    var set = slider.SetRange(end);
                    var value = Until(() => slider.Range?.Value ?? double.NaN, v => Math.Abs(v - end) < 1e-6, set ? 1 : 0);
                    if (!set || Math.Abs(value - end) >= 1e-6)
                    {
                        refused.Add($"{id}: {F(end)} {(set ? $"gave {F(value, "0.######")}" : "was refused")}");
                    }
                }
            }
        }
    }

    private static string? Selected(Editor editor, string automationId, bool wanted = true) =>
        Find(editor, automationId, 0.5)?.IsSelected == wanted ? null : $"{automationId} selected: {Find(editor, automationId, 0)?.IsSelected}";

    private static string? Slider(Editor editor, string automationId, double value, string text)
    {
        var slider = Until(() => Find(editor, automationId, 0.5), s => s?.Range is { } r && Math.Abs(r.Value - value) < 1e-6 && s.ValueText == text, 1);
        return slider?.Range is { } range && Math.Abs(range.Value - value) < 1e-6 && slider.ValueText == text
            ? null
            : $"{automationId} = {F(slider?.Range?.Value ?? double.NaN, "0.####")} \"{slider?.ValueText}\"";
    }

    private static string? ComboShows(Editor editor, string automationId, string item)
    {
        var selected = Until(() => Find(editor, automationId, 0.5)?.SelectedNames ?? [], names => names.Contains(item), 1);
        return selected.Contains(item) ? null : $"{automationId} shows \"{string.Join(", ", selected)}\"";
    }

    /// <summary>The colours at the four corners of the canvas, just inside it: top left, top right, bottom left, bottom right.</summary>
    private static Rgb[] Corners(Sight sight) =>
    [
        sight.Shot.Color(sight.Canvas.X + 3, sight.Canvas.Y + 3),
        sight.Shot.Color(sight.Canvas.Right - 4, sight.Canvas.Y + 3),
        sight.Shot.Color(sight.Canvas.X + 3, sight.Canvas.Bottom - 4),
        sight.Shot.Color(sight.Canvas.Right - 4, sight.Canvas.Bottom - 4),
    ];

    private static string CornerText(Sight sight) => string.Join(" ", Corners(sight));

    private void Backgrounds(Editor editor)
    {
        var sunset = StudioSwatch.Find("sunset");
        var lemon = StudioSwatch.Find("lemon");
        if (sunset?.SecondaryHex is not { } sunsetEnd || lemon is null)
        {
            _report.Check("the inspector has the Sunset and Lemon backgrounds", false);
            return;
        }

        Edit(
            editor,
            "a gradient background (Sunset)",
            () => Find(editor, "StudioSwatch_sunset")?.Select() ?? false,
            p => p with { Canvas = p.Canvas with { Background = new StudioBackground { Style = StudioBackgroundStyle.Gradient, Preset = "sunset", Primary = sunset.PrimaryHex, Secondary = sunsetEnd } } },
            (_, after) => Near(Corners(after)[0], Hex(sunset.PrimaryHex)) && Near(Corners(after)[3], Hex(sunsetEnd)) ? null : $"top left {Corners(after)[0]}, bottom right {Corners(after)[3]}, asked for {Hex(sunset.PrimaryHex)} and {Hex(sunsetEnd)}",
            () => Selected(editor, "StudioSwatch_sunset") ?? Selected(editor, "StudioSwatch_ocean", false));

        Edit(
            editor,
            "a solid background (Lemon)",
            () => Find(editor, "StudioSwatch_lemon")?.Select() ?? false,
            p => p with { Canvas = p.Canvas with { Background = new StudioBackground { Style = StudioBackgroundStyle.Solid, Preset = "lemon", Primary = lemon.PrimaryHex, Secondary = null } } },
            (_, after) => lemon.PrimaryHex == "#FFE040" && Corners(after).All(c => Near(c, Lemon)) ? null : $"corners {CornerText(after)}, asked for {Lemon} ({lemon.PrimaryHex})",
            () => Selected(editor, "StudioSwatch_lemon") ?? Selected(editor, "StudioSwatch_sunset", false));

        var solid = editor.Expected.Canvas.Background;
        Edit(
            editor,
            "Show a background, off",
            () => Find(editor, "StudioShowBackgroundCheckBox")?.Toggle() ?? false,
            p => p with { Canvas = p.Canvas with { Background = p.Canvas.Background with { Style = StudioBackgroundStyle.None } } },
            (_, after) => Corners(after).All(c => Near(c, Black, 6)) ? null : $"corners {CornerText(after)}, and the canvas should be black",
            () => Find(editor, "StudioShowBackgroundCheckBox", 0.5)?.IsToggledOn == false && Gone(editor, "StudioSolidSwatches", 1) ? null : "the check box is still on, or the swatches are still shown");

        // On again brings the default background, not the one that was there.
        Edit(
            editor,
            "Show a background, on again",
            () => Find(editor, "StudioShowBackgroundCheckBox")?.Toggle() ?? false,
            p => p with { Canvas = p.Canvas with { Background = new StudioBackground() } },
            (_, after) => Near(Corners(after)[0], new Rgb(38, 135, 232)) && Near(Corners(after)[3], new Rgb(46, 224, 191)) ? null : $"top left {Corners(after)[0]}, bottom right {Corners(after)[3]}, and the default background runs from (38,135,232) to (46,224,191)",
            () => Selected(editor, "StudioSwatch_ocean"));

        // Lemon for the rest: on a plain colour it shows where the screen and the camera end.
        Find(editor, "StudioSwatch_lemon")?.Select();
        Expect(editor, p => p with { Canvas = p.Canvas with { Background = solid } });
        LookFor(editor, s => Corners(s).All(c => Near(c, Lemon)), 3);
    }

    private void ScreenStyle(Editor editor)
    {
        var withShadow = editor.Expected;
        Edit(
            editor,
            "the screen's shadow to 0 %",
            () => SetSlider(editor, "StudioScreenShadowSlider", 0),
            p => p with { Screen = p.Screen with { Shadow = 0 } },
            (before, after) =>
            {
                if (Layers(after.Canvas, withShadow).Screen is not { } screen)
                {
                    return "the layout has no screen";
                }

                // Just below the screen's bottom edge, a quarter of the way along it, where no camera is.
                var (x, y) = (screen.X + (screen.Width * 0.25), screen.Y + screen.Height + 4);
                var (dark, plain) = (before.Shot.Color(x, y), after.Shot.Color(x, y));
                return Near(plain, Lemon) && Luma(dark) < Luma(Lemon) - 8 ? null : $"just below the screen the background was {dark} with the shadow and is {plain} without it; the background is {Lemon}";
            },
            () => Slider(editor, "StudioScreenShadowSlider", 0, "0%"));

        Edit(
            editor,
            "the screen's corner radius to half round",
            () => SetSlider(editor, "StudioScreenCornerRadiusSlider", 0.1),
            p => p with { Screen = p.Screen with { CornerRadius = 0.1 } },
            (before, after) =>
            {
                if (Layers(after.Canvas, editor.Expected).Screen is not { } screen)
                {
                    return "the layout has no screen";
                }

                // Six pixels in from the corner of the screen's rectangle: recording before, background after.
                var (square, round) = (before.Shot.Color(screen.X + 6, screen.Y + 6), after.Shot.Color(screen.X + 6, screen.Y + 6));
                return Near(round, Lemon) && square.Distance(Lemon) > 30 ? null : $"in the corner of the screen there was {square} and there is {round}; the background is {Lemon}";
            },
            () => Slider(editor, "StudioScreenCornerRadiusSlider", 0.1, "50%"));

        Edit(
            editor,
            "padding to 20 %",
            () => SetSlider(editor, "StudioPaddingSlider", 0.2),
            p => p with { Canvas = p.Canvas with { Padding = 0.2 } },
            (before, after) =>
            {
                if (Layers(after.Canvas, editor.Expected).Screen is not { } screen)
                {
                    return "the layout has no screen";
                }

                // At the middle of the screen's new left edge: background outside, recording inside.
                var y = screen.Y + (screen.Height / 2);
                var (wasOutside, outside, inside) = (before.Shot.Color(screen.X - 4, y), after.Shot.Color(screen.X - 4, y), after.Shot.Color(screen.X + 4, y));
                return Near(outside, Lemon) && inside.Distance(Lemon) > 30 && wasOutside.Distance(Lemon) > 30
                    ? null
                    : $"left of the screen's new edge there was {wasOutside} and there is {outside}; right of it there is {inside}; the background is {Lemon}";
            },
            () => Slider(editor, "StudioPaddingSlider", 0.2, "20%"));
    }

    /// <summary>The four layouts. Returns the picture without the camera; the camera's checks take it again (<see cref="WithoutTheCamera"/>) and compare against that.</summary>
    private Sight? Layouts(Editor editor)
    {
        // The camera's frame number must not read any more where the layout before had the camera.
        string? CameraLeft(Sight before, Sight after)
        {
            if (before.View.CameraMap is not { } old)
            {
                return null;
            }

            var read = FrameCode.Decode(after.Shot.Bgra, after.Shot.Width, after.Shot.Height, editor.Folder.Camera!, old);
            return read == before.Shown.Camera ? "the camera still shows where it was before" : null;
        }

        string? Described(string wanted)
        {
            var help = Until(() => Find(editor, "StudioPreview", 0.5)?.HelpText, text => text == wanted, 1);
            return help == wanted ? null : $"the preview is described as \"{help}\"";
        }

        (string Id, StudioLayout Layout, string Name, string Description)[] layouts =
        [
            ("StudioLayoutScreen", StudioLayout.Screen, "Screen", "Screen only"),
            ("StudioLayoutSideBySide", StudioLayout.SideBySide, "Side by side", "Side by side"),
            ("StudioLayoutCamera", StudioLayout.Camera, "Camera", "Camera only"),
            ("StudioLayoutBubble", StudioLayout.Bubble, "Bubble", "Screen with camera bubble, camera bottom right"),
        ];
        Sight? under = null;
        foreach (var (id, layout, name, description) in layouts)
        {
            Edit(
                editor,
                $"the layout {name}",
                () => Find(editor, id)?.Select() ?? false,
                p => WithScene(p, s => s with { Layout = layout }),
                CameraLeft,
                () => Selected(editor, id) ?? Described(description));
            if (layout == StudioLayout.Screen)
            {
                under = Look(editor);
                var note = NameOf(editor, "StudioCameraHiddenNote", 0.5);
                _report.Check(
                    "with the screen layout the inspector says the camera is hidden and offers no camera controls, and the overlay has no handle",
                    note.Length > 0 && editor.Root.Find("StudioCameraMirrorCheckBox") is null && under is not null && HandleRect(editor, under.Shot) is null,
                    $"note \"{note}\"");
            }
        }

        // What the keys 1 to 4 run.
        Timeline.Mark("3: the layouts by what the keys 1 to 4 run");
        var wrong = new List<string>();
        (StudioShortcutKey Key, StudioLayout Layout, string Id)[] keys =
        [
            (StudioShortcutKey.Digit3, StudioLayout.SideBySide, "StudioLayoutSideBySide"),
            (StudioShortcutKey.Digit1, StudioLayout.Screen, "StudioLayoutScreen"),
            (StudioShortcutKey.Digit4, StudioLayout.Camera, "StudioLayoutCamera"),
            (StudioShortcutKey.Digit2, StudioLayout.Bubble, "StudioLayoutBubble"),
        ];
        foreach (var (key, layout, id) in keys)
        {
            Key(editor, key);
            Expect(editor, p => WithScene(p, s => s with { Layout = layout }));
            var wanted = Both(editor, InspectorFrame);
            var sight = LookFor(editor, s => s.Shown == wanted, 3);
            if (sight?.Shown != wanted || Selected(editor, id) is not null)
            {
                wrong.Add($"{key}: picture {sight?.Shown} (expected {wanted}), {id} selected {Find(editor, id, 0)?.IsSelected}");
            }
        }

        _report.Check("what the keys 1 to 4 run chooses the four layouts: the picture and the inspector follow", wrong.Count == 0, wrong.Count == 0 ? null : string.Join("; ", wrong));
        return under;
    }

    /// <summary>
    /// Which of three points near the top left corner of the camera's rectangle the camera
    /// covers: 2 %, 5 % and 10 % of its short side in from the corner, along the diagonal. A
    /// rectangle covers all three, a rounded rectangle the inner two, a squircle the innermost,
    /// a circle none. Covered means the pixel is not what the picture shows without the camera.
    /// </summary>
    private static string Coverage(Sight sight, Sight under, StudioProject project)
    {
        if (Layers(sight.Canvas, project).Camera is not { } camera)
        {
            return "no camera";
        }

        var side = Math.Min(camera.Rect.Width, camera.Rect.Height);
        return string.Concat(new[] { 0.02, 0.05, 0.10 }.Select(k =>
        {
            var (x, y) = (camera.Rect.X + (k * side), camera.Rect.Y + (k * side));
            return sight.Shot.Color(x, y).Distance(under.Shot.Color(x, y)) > 20 ? '1' : '0';
        }));
    }

    /// <summary>
    /// The three points of <see cref="Coverage"/> in numbers, for a check that did not hold:
    /// where each is in the screenshot, what is there with the camera, and what without.
    /// </summary>
    private static string CoverageNumbers(Sight sight, Sight under, StudioProject project)
    {
        if (Layers(sight.Canvas, project).Camera is not { } camera)
        {
            return "no camera";
        }

        var side = Math.Min(camera.Rect.Width, camera.Rect.Height);

        // Along the diagonal from the corner, pixel by pixel: where the picture is not what it is without the camera.
        var differing = new List<string>();
        var reach = (int)Math.Ceiling(0.2 * side);
        var from = -1;
        for (var step = 0; step <= reach + 1; step++)
        {
            var differs = step <= reach
                && sight.Shot.Color(camera.Rect.X + step, camera.Rect.Y + step, 0).Distance(under.Shot.Color(camera.Rect.X + step, camera.Rect.Y + step, 0)) > 20;
            if (differs && from < 0)
            {
                from = step;
            }
            else if (!differs && from >= 0)
            {
                differing.Add(from == step - 1 ? F(from) : $"{F(from)} to {F(step - 1)}");
                from = -1;
            }
        }

        return $"the camera is in {R(camera.Rect)} with corners of {F(camera.CornerRadius, "0.#")} px; " + string.Join("; ", new[] { 0.02, 0.05, 0.10 }.Select(k =>
        {
            var (x, y) = (camera.Rect.X + (k * side), camera.Rect.Y + (k * side));
            return $"{F(k * 100, "0")} % in, at ({F(x, "0.#")},{F(y, "0.#")}): {sight.Shot.Color(x, y)} with the camera and {under.Shot.Color(x, y)} without";
        })) + $"; along the diagonal from the corner, of the first {reach} pixels these are not what they are without the camera: {(differing.Count == 0 ? "none" : string.Join(", ", differing))}";
    }

    /// <summary>
    /// A second picture without the camera, taken just before the camera's checks. Null when it
    /// could not be taken.
    /// <para>
    /// On a canvas of 1082 × 609 the check of a fully round camera read the middle one of its
    /// three points as covered where nothing covers it ("010"), in every run of 5 October 2026.
    /// What follows is what was worked out for it while no check could be run. It is a
    /// prediction, and this picture is how a run tests it.
    /// </para>
    /// <para>
    /// The preview draws a clip from a copy of its frame, and how large that copy is depends on
    /// what the window showed before: a copy is kept while it is large enough and less than twice
    /// too large, and the camera layout, which hides the screen recording, makes its copy small
    /// (StudioPreviewCopyTargets). So the picture taken at the layout Screen further up was drawn
    /// from another copy than the pictures with the camera that follow, and two copies of one
    /// frame are not the same pixels where the recording has fine detail. Two of the three points
    /// near the corner of a wide camera lie on the noisy checker of the test picture, which is
    /// nothing but fine detail. The layouts Screen and Bubble put the screen recording into the
    /// same rectangle, so going from one to the other and back keeps the copy: this picture and
    /// the ones that follow should have the same pixels wherever the camera is not.
    /// </para>
    /// <para>
    /// How far that goes: with the test picture worked out from its definition and the layer on
    /// whole pixels, as the renderer puts it, the two copies of that evening (960 × 540 and
    /// 680 × 382) differ at the three points by 0, by 4 to 10 and by 4 to 21, depending on how
    /// a frame is scaled into its copy, where a point counts as covered above 20. That reads
    /// "000", or "001", and not "010". It reads "010" only when the copies are sampled a fifth
    /// of a copy pixel or more away from where that arithmetic has them, and then in fewer than
    /// one combination in five of those tried. So the copies may not be it, and then something
    /// is at the middle point in the picture with the camera: the note after the corner checks
    /// tells the two apart.
    /// </para>
    /// <para>
    /// How the earlier picture differs from this one is written into the report, and after the
    /// corner checks what each of them reads against either (<see cref="CameraStyle"/>). Which of
    /// the two the checks go by is <see cref="AsPrepared"/>: the earlier one, as in the runs
    /// that failed, unless the tool is told otherwise.
    /// </para>
    /// </summary>
    private Sight? WithoutTheCamera(Editor editor, Sight early)
    {
        Timeline.Mark("3: the picture without the camera, taken again");
        var with = LookFor(editor, s => s.Shown == Both(editor, InspectorFrame), 3);

        // Gone from the picture: the camera's frame number no longer reads where the camera was.
        bool CameraLeft(Sight sight) =>
            with?.View.CameraMap is not { } old
            || FrameCode.Decode(sight.Shot.Bgra, sight.Shot.Width, sight.Shot.Height, editor.Folder.Camera!, old) != with.Shown.Camera;

        Key(editor, StudioShortcutKey.Digit1);
        Expect(editor, p => WithScene(p, s => s with { Layout = StudioLayout.Screen }));
        var alone = Both(editor, InspectorFrame);
        var again = LookFor(editor, s => s.Shown == alone && CameraLeft(s), 3);
        Key(editor, StudioShortcutKey.Digit2);
        Expect(editor, p => WithScene(p, s => s with { Layout = StudioLayout.Bubble }));
        var together = Both(editor, InspectorFrame);
        var back = LookFor(editor, s => s.Shown == together, 3);
        if (with is null || again is null || again.Shown != alone || !CameraLeft(again) || back is null || back.Shown != together)
        {
            _report.Note(
                "a second picture without the camera could not be taken just before the camera's checks "
                + $"(with the screen alone the picture showed {again?.Shown} and should have shown {alone}{(again is not null && !CameraLeft(again) ? ", and the camera was still in it" : string.Empty)}; with the camera again {back?.Shown}, and {together}): "
                + "the checks go by the one from the layout Screen further up, which may have been drawn from a copy of another size");
            return null;
        }

        // A wide camera: the rectangle and the rounded rectangle have the shape of the camera's video.
        var wide = editor.Expected with { Camera = editor.Expected.Camera with { Shape = StudioCameraShape.RoundedRectangle } };
        var differs = "the two could not be compared, the canvas is not where it was";
        var kept = string.Empty;
        if (early.Canvas == again.Canvas
            && early.Shot.Width == again.Shot.Width
            && early.Shot.Height == again.Shot.Height
            && Layers(again.Canvas, wide) is ({ } screen, { } camera))
        {
            var side = Math.Min(camera.Rect.Width, camera.Rect.Height);
            var distances = new[] { 0.02, 0.05, 0.10 }
                .Select(k => early.Shot.Color(camera.Rect.X + (k * side), camera.Rect.Y + (k * side)).Distance(again.Shot.Color(camera.Rect.X + (k * side), camera.Rect.Y + (k * side))))
                .ToArray();
            int differing = 0, area = 0;
            for (var y = (int)Math.Ceiling(screen.Y); y < (int)Math.Floor(screen.Y + screen.Height); y++)
            {
                for (var x = (int)Math.Ceiling(screen.X); x < (int)Math.Floor(screen.X + screen.Width); x++)
                {
                    area++;
                    if (early.Shot.Color(x, y, 0).Distance(again.Shot.Color(x, y, 0)) > 20)
                    {
                        differing++;
                    }
                }
            }

            differs = $"by {string.Join(", ", distances.Select(d => F(d, "0")))} at the three points near the corner of a wide camera, which is in {R(camera.Rect)} (a point counts as covered above 20), "
                + $"and by more than 20 in {differing} of the {area} pixels of the screen recording";
            if (distances.Any(d => d > 20))
            {
                kept = Kept(early.Shot, "inspector-without-the-camera-before-the-layouts") + Kept(again.Shot, "inspector-without-the-camera-before-the-camera-checks");
            }
        }

        _report.Note(
            "a second picture without the camera was taken just before the camera's checks, with the layout Screen and back (what the keys 1 and 2 run), which should leave the preview's copy of the frame as it is; "
            + $"the picture taken at the layout Screen before the other layouts were shown differs from it {differs}{kept}");
        return again;
    }

    private void CameraStyle(Editor editor, Sight? early)
    {
        if (early is null)
        {
            _report.Check("the camera's controls can be checked", false, "there is no picture without the camera to compare against");
            return;
        }

        // The checks go by the picture taken further up, as they did in the runs that failed.
        // Told to (--as-prepared), they go by the second one.
        var again = WithoutTheCamera(editor, early);
        var under = AsPrepared && again is not null ? again : early;

        // What each corner check reads against either picture, for a note after them.
        var read = new List<(string Name, string Early, string Again)>();
        void Read(string name, Sight sight)
        {
            read.RemoveAll(entry => entry.Name == name);
            read.Add((name, Coverage(sight, early, editor.Expected), again is null ? "not taken" : Coverage(sight, again, editor.Expected)));
        }

        var shadowed = editor.Expected;
        Edit(
            editor,
            "the camera's shadow to 0 %",
            () => SetSlider(editor, "StudioCameraShadowSlider", 0),
            p => p with { Camera = p.Camera with { Shadow = 0 } },
            (before, after) =>
            {
                if (Layers(after.Canvas, shadowed).Camera is not { } camera)
                {
                    return "the layout has no camera";
                }

                // Below the middle of the bubble.
                var (x, y) = (camera.Rect.X + (camera.Rect.Width / 2), camera.Rect.Y + camera.Rect.Height + 5);
                var (dark, plain, without) = (before.Shot.Color(x, y), after.Shot.Color(x, y), under.Shot.Color(x, y));
                return Near(plain, without, 6) && Luma(dark) < Luma(without) - 5 ? null : $"just below the camera there was {dark} with the shadow and there is {plain} without it; without a camera it is {without}";
            },
            () => Slider(editor, "StudioCameraShadowSlider", 0, "0%"));

        var circle = Look(editor);
        if (circle is not null)
        {
            Read("the circle", circle);
        }

        _report.Check(
            "the camera starts as a circle: it covers none of the three points near the corner of its rectangle",
            circle is not null && Coverage(circle, under, editor.Expected) == "000",
            circle is null ? "no screenshot" : Coverage(circle, under, editor.Expected));

        (StudioCameraShape Shape, string Name, string Signature)[] shapes =
        [
            (StudioCameraShape.Rectangle, "Rectangle", "111"),
            (StudioCameraShape.Squircle, "Squircle", "001"),
            (StudioCameraShape.RoundedRectangle, "Rounded rectangle", "011"),
        ];
        var cornersHeld = true;
        foreach (var (shape, name, signature) in shapes)
        {
            cornersHeld &= Edit(
                editor,
                $"the camera's shape {name}",
                () => SetCombo(editor, "StudioCameraShapeComboBox", (int)shape),
                p => p with { Camera = p.Camera with { Shape = shape } },
                (_, after) =>
                {
                    Read(name, after);
                    return Coverage(after, under, editor.Expected) == signature ? null : $"of the three points near the corner the camera covers {Coverage(after, under, editor.Expected)}, and this shape covers {signature} ({CoverageNumbers(after, under, editor.Expected)})";
                },
                () => ComboShows(editor, "StudioCameraShapeComboBox", name));
        }

        cornersHeld &= Edit(
            editor,
            "the rounded rectangle's corner radius to fully round",
            () => SetSlider(editor, "StudioCameraCornerRadiusSlider", 0.5),
            p => p with { Camera = p.Camera with { CornerRadius = 0.5 } },
            (_, after) =>
            {
                Read("fully round", after);
                return Coverage(after, under, editor.Expected) == "000" ? null : $"of the three points near the corner the camera covers {Coverage(after, under, editor.Expected)}, and fully round corners cover none ({CoverageNumbers(after, under, editor.Expected)})";
            },
            () => Slider(editor, "StudioCameraCornerRadiusSlider", 0.5, "100%"));
        _report.Note(
            "the three points near the camera's corner, read against the picture without the camera that was taken at the layout Screen further up, and against the second one, taken right before these checks: "
            + string.Join("; ", read.Select(entry => $"{entry.Name} {entry.Early} and {entry.Again}"))
            + $". Wanted: the circle 000, Rectangle 111, Squircle 001, Rounded rectangle 011, fully round 000. The checks went by the {(ReferenceEquals(under, early) ? "first" : "second")} picture{(AsPrepared ? ", as --as-prepared asks" : "; with --as-prepared they go by the second")}");
        if (!cornersHeld)
        {
            _report.Note($"what the three points near the camera's corner are compared with is the picture without the camera{Kept(under.Shot, "inspector-without-the-camera")}");
        }

        // The camera's frame number reads where the layout now has the camera, and no longer where it was.
        string? Moved(Sight before, Sight after)
        {
            if (before.View.CameraMap is not { } old)
            {
                return "there was no camera before";
            }

            var read = FrameCode.Decode(after.Shot.Bgra, after.Shot.Width, after.Shot.Height, editor.Folder.Camera!, old);
            return read == before.Shown.Camera ? "the camera still reads where and as it was before" : null;
        }

        Edit(
            editor,
            "the camera's size to 25 %",
            () => SetSlider(editor, "StudioCameraSizeSlider", 0.25),
            p => WithScene(p, s => s with { Bubble = s.Bubble with { Size = 0.25 } }),
            Moved,
            () => Slider(editor, "StudioCameraSizeSlider", 0.25, "25%"));

        Edit(
            editor,
            "Mirror, off",
            () => Find(editor, "StudioCameraMirrorCheckBox")?.Toggle() ?? false,
            p => p with { Camera = p.Camera with { Mirror = false } },
            Moved,
            () => Find(editor, "StudioCameraMirrorCheckBox", 0.5)?.IsToggledOn == false ? null : "the check box is still on");

        // The bottom left corner, and from there to the right and up: clear of the screen
        // recording's frame number, which is near its top left corner.
        Edit(
            editor,
            "the camera's position Bottom left",
            () => SetCombo(editor, "StudioCameraPositionComboBox", (int)StudioAnchor.BottomLeft),
            p => WithScene(p, s => s with { Bubble = s.Bubble with { Anchor = StudioAnchor.BottomLeft, OffsetX = 0, OffsetY = 0 } }),
            Moved,
            () => ComboShows(editor, "StudioCameraPositionComboBox", "Bottom left")
                ?? (Find(editor, "StudioPreview", 0.5)?.HelpText == "Screen with camera bubble, camera bottom left" ? null : $"the preview is described as \"{Find(editor, "StudioPreview", 0)?.HelpText}\""));

        Edit(
            editor,
            "the camera's horizontal offset to +20 %",
            () => SetSlider(editor, "StudioCameraOffsetXSlider", 0.2),
            p => WithScene(p, s => s with { Bubble = s.Bubble with { OffsetX = 0.2 } }),
            Moved,
            () => Slider(editor, "StudioCameraOffsetXSlider", 0.2, "+20%"));

        Edit(
            editor,
            "the camera's vertical offset to -30 %",
            () => SetSlider(editor, "StudioCameraOffsetYSlider", -0.3),
            p => WithScene(p, s => s with { Bubble = s.Bubble with { OffsetY = -0.3 } }),
            Moved,
            () => Slider(editor, "StudioCameraOffsetYSlider", -0.3, "-30%"));
    }

    // After side by side, where a border would cover the ends of the camera's frame number.
    private void CameraBorder(Editor editor)
    {
        Edit(
            editor,
            "the camera's border to its widest",
            () => SetSlider(editor, "StudioCameraBorderSlider", 0.02),
            p => p with { Camera = p.Camera with { BorderWidth = 0.02 } },
            (before, after) =>
            {
                if (Layers(after.Canvas, editor.Expected).Camera is not { } camera)
                {
                    return "the layout has no camera";
                }

                // Just inside the middle of the camera's top edge.
                var (x, y) = (camera.Rect.X + (camera.Rect.Width / 2), camera.Rect.Y + 3);
                var (was, now) = (before.Shot.Color(x, y, 0), after.Shot.Color(x, y, 0));
                return Near(now, White, 24) && was.Distance(White) > 24 ? null : $"at the camera's top edge there was {was} and there is {now}; the border is white";
            },
            () => Slider(editor, "StudioCameraBorderSlider", 0.02, "100%"));
    }

    private void SideBySide(Editor editor)
    {
        Timeline.Mark("3: side by side");
        Find(editor, "StudioLayoutSideBySide")?.Select();
        Expect(editor, p => WithScene(p, s => s with { Layout = StudioLayout.SideBySide }));

        Edit(
            editor,
            "side by side, the camera on the left",
            () => SetCombo(editor, "StudioCameraSideComboBox", (int)StudioCameraSide.Leading),
            p => WithScene(p, s => s with { Split = s.Split with { CameraSide = StudioCameraSide.Leading } }),
            (before, after) => Layers(before.Canvas, WithScene(editor.Expected, s => s with { Split = s.Split with { CameraSide = StudioCameraSide.Trailing } })).Camera is { } right
                    && Layers(after.Canvas, editor.Expected).Camera is { } left
                    && left.Rect.X < right.Rect.X
                    && DifferenceCount(before.Shot, after.Shot, after.Canvas) > 2000
                ? null
                : "the picture did not change, or the layout did not move the camera to the left",
            () => ComboShows(editor, "StudioCameraSideComboBox", "Left or top"));

        Edit(
            editor,
            "side by side, the camera's share to 45 %",
            () => SetSlider(editor, "StudioCameraShareSlider", 0.45),
            p => WithScene(p, s => s with { Split = s.Split with { CameraFraction = 0.45 } }),
            (before, after) => DifferenceCount(before.Shot, after.Shot, after.Canvas) > 2000 ? null : "the picture did not change",
            () => Slider(editor, "StudioCameraShareSlider", 0.45, "45%"));

        Find(editor, "StudioLayoutBubble")?.Select();
        Expect(editor, p => WithScene(p, s => s with { Layout = StudioLayout.Bubble }));
    }

    private void CanvasShapes(Editor editor)
    {
        // The camera back in its corner: on a narrow canvas it would otherwise cover the screen recording's frame number.
        SetCombo(editor, "StudioCameraPositionComboBox", (int)StudioAnchor.BottomRight);
        Expect(editor, p => WithScene(p, s => s with { Bubble = s.Bubble with { Anchor = StudioAnchor.BottomRight, OffsetX = 0, OffsetY = 0 } }));

        (StudioCanvasAspect Aspect, string Name, double Ratio)[] shapes =
        [
            (StudioCanvasAspect.Square, "1:1", 1),
            (StudioCanvasAspect.Landscape4X3, "4:3", 4.0 / 3),
            (StudioCanvasAspect.Landscape16X9, "16:9", 16.0 / 9),
            (StudioCanvasAspect.Portrait3X4, "3:4", 3.0 / 4),
            (StudioCanvasAspect.Portrait9X16, "9:16", 9.0 / 16),
            (StudioCanvasAspect.Auto, "Auto", 16.0 / 9),
        ];
        foreach (var (aspect, name, ratio) in shapes)
        {
            // 16:9 is what the screen recording already is, so the picture is the same as with Auto.
            var samePicture = aspect == StudioCanvasAspect.Landscape16X9;
            if (samePicture)
            {
                Timeline.Mark("3: the canvas shape 16:9");
                var set = SetCombo(editor, "StudioCanvasComboBox", (int)aspect);
                Expect(editor, p => p with { Canvas = p.Canvas with { Aspect = aspect } });
                var sight = LookFor(editor, s => Shaped(s, ratio) is null, 3);
                _report.Check(
                    "the canvas shape 16:9: the canvas has that shape, and the header says so",
                    set && sight is not null && Shaped(sight, ratio) is null && SizeShown(name) is null,
                    sight is null ? "no screenshot" : Shaped(sight, ratio) ?? SizeShown(name));
                continue;
            }

            Edit(
                editor,
                $"the canvas shape {name}",
                () => SetCombo(editor, "StudioCanvasComboBox", (int)aspect),
                p => p with { Canvas = p.Canvas with { Aspect = aspect } },
                (_, after) => Shaped(after, ratio),
                () => SizeShown(name));
        }

        // The canvas has the shape asked for, is filled with the background to its corners, and the picture ends at its edges.
        static string? Shaped(Sight sight, double ratio)
        {
            var (canvas, shot) = (sight.Canvas, sight.Shot);
            var shape = Math.Abs(((double)canvas.Width / canvas.Height) - ratio) < 0.01;
            var filled = Corners(sight).All(c => Near(c, Lemon));
            var ends = shot.Color(canvas.X - 3, canvas.Y + (canvas.Height / 2.0)).Distance(Lemon) > 30
                && shot.Color(canvas.Right + 2, canvas.Y + (canvas.Height / 2.0)).Distance(Lemon) > 30
                && shot.Color(canvas.X + (canvas.Width / 2.0), canvas.Y - 3).Distance(Lemon) > 30
                && shot.Color(canvas.X + (canvas.Width / 2.0), canvas.Bottom + 2).Distance(Lemon) > 30;
            return shape && filled && ends ? null : $"the canvas is {canvas}, {F((double)canvas.Width / canvas.Height)} wide for 1 high where {F(ratio)} was asked for; its corners are {CornerText(sight)}; the picture ends at its edges: {ends}";
        }

        string? SizeShown(string name)
        {
            var wanted = StudioEditorText.GetExportSizeText(StudioExportLimits.GetExportSize(editor.Expected));
            var text = Until(() => NameOf(editor, "StudioExportSizeText", 0.5), shown => shown == wanted, 1);
            return text == wanted ? ComboShows(editor, "StudioCanvasComboBox", name) : $"the header says \"{text}\" and should say \"{wanted}\"";
        }
    }

    private void Extras(Editor editor)
    {
        Edit(
            editor,
            "Tiny Clips badge, on",
            () => Find(editor, "StudioBrandingCheckBox")?.Toggle() ?? false,
            p => p with { Overlays = p.Overlays with { Branding = true } },
            (before, after) => DifferenceCount(before.Shot, after.Shot, after.Canvas) >= 100 ? null : $"only {DifferenceCount(before.Shot, after.Shot, after.Canvas)} pixels of the picture changed",
            () => Find(editor, "StudioBrandingCheckBox", 0.5)?.IsToggledOn == true ? null : "the check box is off");

        // These two change nothing in a picture without clicks and without sound: the check boxes follow Undo and Redo.
        Timeline.Mark("3: click rings and mute");
        var wrong = new List<string>();
        foreach (var id in new[] { "StudioClickRingsCheckBox", "StudioMuteCheckBox" })
        {
            var toggled = Find(editor, id)?.Toggle() ?? false;
            var on = Until(() => Find(editor, id, 0)?.IsToggledOn, state => state == true, 2);
            Invoke(editor, "StudioUndoButton");
            var off = Until(() => Find(editor, id, 0)?.IsToggledOn, state => state == false, 2);
            Invoke(editor, "StudioRedoButton");
            var again = Until(() => Find(editor, id, 0)?.IsToggledOn, state => state == true, 2);
            if (!toggled || on != true || off != false || again != true)
            {
                wrong.Add($"{id}: toggled {toggled}, then {on}, after Undo {off}, after Redo {again}");
            }
        }

        Expect(editor, p => p with { Overlays = p.Overlays with { Clicks = p.Overlays.Clicks with { Enabled = true } }, Audio = p.Audio with { Muted = true } });
        _report.Check("Click rings and Mute audio switch on, and follow Undo and Redo", wrong.Count == 0, wrong.Count == 0 ? null : string.Join("; ", wrong));
    }

    private void Gestures(Editor editor)
    {
        Timeline.Mark("3: several steps of a slider inside one gesture");
        var start = SliderValue(editor, "StudioPaddingSlider");
        var before = Look(editor);

        // What the slider's row does when a pointer takes hold of the slider, and when it lets go.
        OnUi(editor.Window.ViewModel.BeginGesture);
        var moved = SetSlider(editor, "StudioPaddingSlider", 0.16) & SetSlider(editor, "StudioPaddingSlider", 0.12) & SetSlider(editor, "StudioPaddingSlider", 0.08);
        OnUi(editor.Window.ViewModel.EndGesture);
        var end = SliderValue(editor, "StudioPaddingSlider");
        var projectBefore = editor.Expected;
        Expect(editor, p => p with { Canvas = p.Canvas with { Padding = 0.08 } });
        var stepped = LookFor(editor, s => s.Shown == Both(editor, InspectorFrame), 3);
        editor.Expected = projectBefore;
        Invoke(editor, "StudioUndoButton");
        var undone = Until(() => SliderValue(editor, "StudioPaddingSlider"), value => Math.Abs(value - start) < 1e-6, 2);
        var picture = before is null || stepped is null ? null : LookFor(editor, s => Compare(before.Shot, stepped.Shot, s.Shot, before.Canvas).IsBack, 3);
        Invoke(editor, "StudioRedoButton");
        var redone = Until(() => SliderValue(editor, "StudioPaddingSlider"), value => Math.Abs(value - 0.08) < 1e-6, 2);
        _report.Check(
            "a slider moved in three steps inside one gesture is one undo step: one Undo gives back the value and the picture from before the gesture, one Redo the last value",
            moved && Math.Abs(end - 0.08) < 1e-6 && Math.Abs(undone - start) < 1e-6 && before is not null && stepped is not null && picture is not null && Compare(before.Shot, stepped.Shot, picture.Shot, before.Canvas).IsBack && Math.Abs(redone - 0.08) < 1e-6,
            $"padding {F(start)} before, {F(end)} after the three steps, {F(undone)} after one Undo, {F(redone)} after one Redo{(before is null || stepped is null || picture is null ? "; a screenshot is missing" : "; after the Undo " + Compare(before.Shot, stepped.Shot, picture.Shot, before.Canvas))}");

        // Without a gesture, each step is a step of its own.
        var apart = SetSlider(editor, "StudioPaddingSlider", 0.30) & SetSlider(editor, "StudioPaddingSlider", 0.32) & SetSlider(editor, "StudioPaddingSlider", 0.34);
        Invoke(editor, "StudioUndoButton");
        var one = Until(() => SliderValue(editor, "StudioPaddingSlider"), value => Math.Abs(value - 0.32) < 1e-6, 2);
        Invoke(editor, "StudioUndoButton");
        var two = Until(() => SliderValue(editor, "StudioPaddingSlider"), value => Math.Abs(value - 0.30) < 1e-6, 2);

        // What Ctrl+Z and Ctrl+Y run.
        var undoKey = Key(editor, StudioShortcutKey.Z, control: true);
        var three = Until(() => SliderValue(editor, "StudioPaddingSlider"), value => Math.Abs(value - 0.08) < 1e-6, 2);
        var redoKey = Key(editor, StudioShortcutKey.Y, control: true);
        var four = Until(() => SliderValue(editor, "StudioPaddingSlider"), value => Math.Abs(value - 0.30) < 1e-6, 2);
        Expect(editor, p => p with { Canvas = p.Canvas with { Padding = 0.30 } });
        _report.Check(
            "the same three steps without a gesture are three undo steps, and what Ctrl+Z and Ctrl+Y run undoes and redoes one",
            apart && Math.Abs(one - 0.32) < 1e-6 && Math.Abs(two - 0.30) < 1e-6 && undoKey == StudioShortcutAction.Undo && Math.Abs(three - 0.08) < 1e-6 && redoKey == StudioShortcutAction.Redo && Math.Abs(four - 0.30) < 1e-6,
            $"padding after Undo {F(one)}, after another {F(two)}, after what Ctrl+Z runs {F(three)}, after what Ctrl+Y runs {F(four)}");
        LookFor(editor, s => s.Shown == Both(editor, InspectorFrame), 3);
    }

    private void DefaultLook(Editor editor)
    {
        Timeline.Mark("3: save as default look");
        var pressed = Invoke(editor, "StudioSaveDefaultLookButton");
        var status = NameOf(editor, "StudioDefaultLookStatus");
        var look = _services.Settings.StudioDefaultLook;
        var expected = editor.Expected;
        _report.Check(
            "Save as default look says that it saved, and the settings hold this project's canvas, screen and camera styling",
            pressed && status.StartsWith("Saved.", StringComparison.Ordinal) && look is not null
                && look.Canvas.Padding == expected.Canvas.Padding && look.Canvas.Background.Preset == expected.Canvas.Background.Preset
                && look.Screen.CornerRadius == expected.Screen.CornerRadius && look.Camera.Shape == expected.Camera.Shape && look.Camera.Mirror == expected.Camera.Mirror,
            $"\"{status}\"; saved look: {(look is null ? "none" : $"padding {F(look.Canvas.Padding)}, background {look.Canvas.Background.Preset}, screen radius {F(look.Screen.CornerRadius)}, camera {look.Camera.Shape}, mirrored {look.Camera.Mirror}")}");
    }

    /// <summary>The project file, once the editor has saved by itself, holds every edit the checks made.</summary>
    private void WhatWasSaved(Editor editor)
    {
        Timeline.Mark("3: what the editor saved");
        var wanted = Describe(editor.Expected);
        var saved = Until(() => Describe(_services.Store.Load(editor.Id)), text => text == wanted, 3, 100);
        _report.Check(
            "the editor saves by itself: the project file holds every edit, each with the value the checks expect",
            saved == wanted,
            saved == wanted ? saved : $"saved: {saved} | expected: {wanted}");
    }

    /// <summary>Everything the inspector, the zoom lane and the trim bar can change, as one line.</summary>
    private static string Describe(StudioProject p) =>
        $"trim {F(p.Edits.TrimStart)} to {(p.Edits.TrimEnd is { } end ? F(end) : "the end")}; "
        + $"canvas {p.Canvas.Aspect}, padding {F(p.Canvas.Padding)}, background {p.Canvas.Background.Style} {p.Canvas.Background.Preset} {p.Canvas.Background.Primary} {p.Canvas.Background.Secondary}; "
        + $"screen radius {F(p.Screen.CornerRadius)}, shadow {F(p.Screen.Shadow)}; "
        + $"camera {p.Camera.Shape}, radius {F(p.Camera.CornerRadius)}, mirrored {p.Camera.Mirror}, border {F(p.Camera.BorderWidth)}, shadow {F(p.Camera.Shadow)}; "
        + $"layout {p.Scenes[0].Layout}, bubble {p.Scenes[0].Bubble.Anchor} {F(p.Scenes[0].Bubble.Size)} {F(p.Scenes[0].Bubble.OffsetX)} {F(p.Scenes[0].Bubble.OffsetY)}, split {p.Scenes[0].Split.CameraSide} {F(p.Scenes[0].Split.CameraFraction)}; "
        + $"badge {p.Overlays.Branding}, click rings {p.Overlays.Clicks.Enabled}, muted {p.Audio.Muted}; "
        + $"screen crop {Describe(p.Screen.Crop)}, camera crop {Describe(p.Camera.Crop)}; "
        + $"zooms {(p.Zooms.Length == 0 ? "none" : string.Join(", ", p.Zooms.Select(Describe)))}";

    private static string Describe(StudioRect? crop) =>
        crop is null ? "none" : $"({F(crop.X, "0.######")}, {F(crop.Y, "0.######")}, {F(crop.Width, "0.######")}, {F(crop.Height, "0.######")})";

    /// <summary>A zoom with every value it stores, times to a millionth of a second.</summary>
    private static string Describe(StudioZoom zoom) =>
        $"[{F(zoom.Start, "0.######")} to {F(zoom.End, "0.######")}, {F(zoom.Scale, "0.######")}x, {zoom.Focus.Mode} ({F(zoom.Focus.X, "0.######")}, {F(zoom.Focus.Y, "0.######")}), "
        + $"in {F(zoom.EaseIn, "0.######")}, out {F(zoom.EaseOut, "0.######")}, {zoom.Origin}]";
}
