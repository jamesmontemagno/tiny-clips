using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Capture;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 11. Crops: the four crop sliders of the screen and of the camera, and Reset crop. What a crop
// leaves is read from the picture the way a zoom is: from where the edges of the test clips are.
internal sealed partial class WindowChecks
{
    private void Cropping()
    {
        Timeline.Mark("11: crops");
        _partErrors.Clear();
        ScreenCrop();
        CameraCrop();
        if (_partErrors.Count > 0)
        {
            _report.Note($"over the {_partErrors.Count} pictures that were read for a crop, the edge furthest from its place was {F(_partErrors.Max(), "0.00")} px from it");
        }
    }

    /// <summary>A plain canvas around the layers, so that where a layer ends can be read from one pixel outside it and one inside.</summary>
    private static StudioProject OnLemon(StudioProject project) => project with
    {
        Canvas = project.Canvas with { Background = new StudioBackground { Style = StudioBackgroundStyle.Solid, Preset = "lemon", Primary = "#FFE040", Secondary = null } },
        Screen = project.Screen with { Shadow = 0 },
        Camera = project.Camera with { Shadow = 0 },
    };

    /// <summary>
    /// Null when a layer ends where its rectangle ends: at the middle of each of its four edges
    /// the canvas colour is just outside it and something else just inside.
    /// </summary>
    private static string? EndsAtItsRectangle(Shot shot, StudioFrameRect layer, Rgb canvas)
    {
        var (middleX, middleY) = (layer.X + (layer.Width / 2), layer.Y + (layer.Height / 2));
        (string Edge, double OutX, double OutY, double InX, double InY)[] edges =
        [
            ("left", layer.X - 4, middleY, layer.X + 4, middleY),
            ("right", layer.X + layer.Width + 3, middleY, layer.X + layer.Width - 5, middleY),
            ("top", middleX, layer.Y - 4, middleX, layer.Y + 4),
            ("bottom", middleX, layer.Y + layer.Height + 3, middleX, layer.Y + layer.Height - 5),
        ];
        var wrong = edges
            .Select(edge => (edge.Edge, Outside: shot.Color(edge.OutX, edge.OutY), Inside: shot.Color(edge.InX, edge.InY)))
            .Where(read => !Near(read.Outside, canvas) || read.Inside.R < 0 || read.Inside.Distance(canvas) < 30)
            .Select(read => $"at its {read.Edge} edge there is {read.Outside} outside and {read.Inside} inside")
            .ToArray();
        return wrong.Length == 0 ? null : string.Join("; ", wrong);
    }

    // ---------------------------------------------------------------------------------------
    // The screen
    // ---------------------------------------------------------------------------------------

    private void ScreenCrop()
    {
        Timeline.Mark("11: the screen's crop");
        if (OpenReady(NewScreenProject("Screen crop", OnLemon), "screen crop") is not { } editor)
        {
            return;
        }

        const int Frame = 60;
        string[] ids = ["StudioScreenCropLeftSlider", "StudioScreenCropTopSlider", "StudioScreenCropRightSlider", "StudioScreenCropBottomSlider"];
        SetSlider(editor, "StudioPlayhead", MiddleOf(Frame));
        LookForPart(editor, ScreenPart.Whole, Frame, 5);
        var names = ids.Select(id => Find(editor, id)).Select(slider => slider?.Range is { Value: 0, Minimum: 0 } range && Same(range.Maximum, 0.95) && Same(range.SmallChange, 0.01) ? $"{slider.Name} {slider.ValueText}" : $"{slider?.Name}: not from 0 to 0.95, at 0, in steps of 0.01").ToArray();
        var reset = Find(editor, "StudioScreenCropResetButton");
        _report.Check(
            "the Screen section has four crop sliders, from 0 to 95% in steps of 1%, and Reset crop, which is disabled while nothing is cut off",
            names.SequenceEqual(["Crop left 0%", "Crop top 0%", "Crop right 0%", "Crop bottom 0%"]) && reset is { IsEnabled: false, Name: "Reset crop" },
            $"{string.Join(", ", names)}; {reset} enabled {reset?.IsEnabled}");

        // One edge after the other. The canvas takes the shape of what is left of the screen, and
        // its size is the size of that, made even: 1920 × 0.92 = 1766.4 is 1766, 1080 × 0.97 =
        // 1047.6 is 1048, 1920 × 0.52 = 998.4 is 998, and 1080 × 0.52 = 561.6 is 562.
        (string Id, double Value, string Text, StudioRect Crop, string Size, string What)[] steps =
        [
            (ids[0], 0.08, "8%", new StudioRect(0.08, 0, 0.92, 1), "Exports at 1766 × 1080", "Crop left 8%: the screen from 0.08 across"),
            (ids[1], 0.03, "3%", new StudioRect(0.08, 0.03, 0.92, 0.97), "Exports at 1766 × 1048", "Crop top 3%: and from 0.03 down"),
            (ids[2], 0.40, "40%", new StudioRect(0.08, 0.03, 0.52, 0.97), "Exports at 998 × 1048", "Crop right 40%: and 1 − 0.08 − 0.40 = 0.52 wide"),
            (ids[3], 0.45, "45%", new StudioRect(0.08, 0.03, 0.52, 0.52), "Exports at 998 × 562", "Crop bottom 45%: and 1 − 0.03 − 0.45 = 0.52 high"),
        ];
        foreach (var (id, value, text, crop, size, what) in steps)
        {
            Timeline.Mark($"11: {what}");
            var set = SetSlider(editor, id, value);
            Expect(editor, p => p with { Screen = p.Screen with { Crop = crop } });
            var sizeText = Until(() => NameOf(editor, "StudioExportSizeText", 0.5), name => name == size, 2);
            var holds = Describe(ScreenCropOf(editor));
            _report.Check(
                $"{what}: the project holds that crop, the slider says {text}, and the header the size of what is left",
                set && holds == Describe(crop) && Slider(editor, id, value, text) is null && sizeText == size && Find(editor, "StudioScreenCropResetButton") is { IsEnabled: true },
                $"the crop is {holds} and should be {Describe(crop)}; the slider: {Slider(editor, id, value, text) ?? $"{F(value)}, \"{text}\""}; \"{sizeText}\"");

            // The part of the screen the crop leaves has to fill the card, and the card has the
            // shape of that part. The picture is told apart from one that only moved or only
            // scaled the screen by where its edges are, and from one that drew more of the screen
            // than the crop leaves by the canvas colour right outside the card.
            var part = new ScreenPart(crop.X, crop.Y, crop.Width, crop.Height);
            var sight = ShowsPart(editor, $"{what}: the preview shows the part of the screen the crop leaves", part, Frame, $"the crop {part}");
            if (sight is not null)
            {
                var natural = StudioCanvasMath.NaturalSize(editor.Expected);
                var ends = EndsAtItsRectangle(sight.Shot, sight.Card, Lemon);
                var shape = (double)sight.Canvas.Width / sight.Canvas.Height;
                _report.Check(
                    $"{what}: the canvas has the shape of what is left, and the screen's card ends where the layout puts it, with the canvas colour around it",
                    Math.Abs(shape - (natural.Width / natural.Height)) < 0.01 * shape && ends is null,
                    ends ?? $"canvas {sight.Canvas}, {F(shape, "0.000")} to 1, for a video of {F(natural.Width)} × {F(natural.Height)}; card {R(sight.Card)}");
            }
        }

        // An edge stops where it would leave less than a twentieth of the screen: with 40% off the right, at 55%.
        Timeline.Mark("11: where a crop edge stops");
        var asked = SetSlider(editor, ids[0], 0.95);
        var stopped = Until(() => SliderValue(editor, ids[0]), value => Same(value, 0.55), 2);
        var stoppedText = Find(editor, ids[0])?.ValueText;

        // Asked for more again, the project stays as it is, and the slider has to come back by itself.
        var askedAgain = SetSlider(editor, ids[0], 0.7);
        var stayed = Until(() => SliderValue(editor, ids[0]), value => Same(value, 0.55), 2);
        var held = ScreenCropOf(editor);
        _report.Check(
            "a crop edge stops where the opposite edge leaves it a twentieth of the screen, and the slider shows where it stopped, not what it was asked for",
            asked && Same(stopped, 0.55) && stoppedText == "55%" && askedAgain && Same(stayed, 0.55) && Describe(held) == Describe(new StudioRect(0.55, 0.03, 0.05, 0.52)),
            $"asked for 95% with 40% off the right: {F(stopped)} \"{stoppedText}\"; asked for 70% after that: {F(stayed)}; the crop is {Describe(held)}");
        SetSlider(editor, ids[0], 0.08);
        Until(() => SliderValue(editor, ids[0]), value => Same(value, 0.08), 2);
        var cropped = new ScreenPart(0.08, 0.03, 0.52, 0.52);
        LookForPart(editor, cropped, Frame, 3);

        // One drag of a slider is one undo step: three steps inside a gesture, as the slider's row makes of a drag.
        OnUi(editor.Window.ViewModel.BeginGesture);
        var dragged = SetSlider(editor, ids[1], 0.10) & SetSlider(editor, ids[1], 0.20) & SetSlider(editor, ids[1], 0.12);
        OnUi(editor.Window.ViewModel.EndGesture);
        var afterDrag = SliderValue(editor, ids[1]);
        Invoke(editor, "StudioUndoButton");
        var afterUndo = Until(() => SliderValue(editor, ids[1]), value => Same(value, 0.03), 2);
        _report.Check(
            "a crop slider moved in three steps inside one gesture is one undo step",
            dragged && Same(afterDrag, 0.12) && Same(afterUndo, 0.03) && Describe(ScreenCropOf(editor)) == Describe(new StudioRect(0.08, 0.03, 0.52, 0.52)),
            $"Crop top {F(afterDrag)} after the three steps, {F(afterUndo)} after one Undo; the crop is {Describe(ScreenCropOf(editor))}");

        // Reset crop.
        Timeline.Mark("11: Reset crop");

        // With the keyboard focus on the button, as when it is pressed with the keyboard: it is switched off by what it does.
        var resetFocus = FocusOn(editor, "StudioScreenCropResetButton");
        var resetPressed = Invoke(editor, "StudioScreenCropResetButton");
        Expect(editor, p => p with { Screen = p.Screen with { Crop = null } });
        var sizeAfter = Until(() => NameOf(editor, "StudioExportSizeText", 0.5), name => name == "Exports at 1920 × 1080", 2);
        var values = ids.Select(id => Until(() => SliderValue(editor, id), value => value == 0, 1)).ToArray();
        var resetAfter = Until(() => Find(editor, "StudioScreenCropResetButton", 0.5)?.IsEnabled, enabled => enabled == false, 1);
        _report.Check(
            "Reset crop takes the crop away: the project holds none, the four sliders are at 0, the header says the whole screen's size, and the button is disabled again",
            resetPressed && ScreenCropOf(editor) is null && values.All(value => value == 0) && sizeAfter == "Exports at 1920 × 1080" && resetAfter == false,
            $"the crop is {Describe(ScreenCropOf(editor))}; the sliders: {string.Join(", ", values.Select(value => F(value)))}; \"{sizeAfter}\"; Reset crop enabled {resetAfter}");
        ShowsPart(editor, "after Reset crop the preview shows the whole screen again", ScreenPart.Whole, Frame, "no crop");
        var focusAfterReset = Until(() => FocusedId(editor), id => id == "StudioScreenCropLeftSlider", 1.5);
        _report.Check(
            "after Reset crop, which is switched off once nothing is cut off, the keyboard focus is on Crop left, the first of the four sliders",
            resetFocus == "StudioScreenCropResetButton" && focusAfterReset == "StudioScreenCropLeftSlider",
            $"the focus was on \"{resetFocus}\" and is on \"{focusAfterReset}\"");

        // One Undo brings the whole crop back, and the picture with it.
        Invoke(editor, "StudioUndoButton");
        Expect(editor, p => p with { Screen = p.Screen with { Crop = new StudioRect(0.08, 0.03, 0.52, 0.52) } });
        var back = Until(() => Describe(ScreenCropOf(editor)), text => text == Describe(new StudioRect(0.08, 0.03, 0.52, 0.52)), 2);
        _report.Check(
            "Reset crop is one undo step: one Undo brings back all four edges",
            back == Describe(new StudioRect(0.08, 0.03, 0.52, 0.52)) && ids.Zip([0.08, 0.03, 0.40, 0.45], (id, value) => Same(SliderValue(editor, id), value)).All(same => same),
            $"the crop is {back}; the sliders: {string.Join(", ", ids.Select(id => F(SliderValue(editor, id))))}");
        ShowsPart(editor, "after that Undo the preview shows the cropped part again", cropped, Frame, $"the crop {cropped}");

        // A zoom works inside the crop: its focus pad stands for the part the crop leaves.
        ZoomInsideACrop(editor, Frame);

        var wanted = Describe(editor.Expected.Screen.Crop);
        var saved = Until(() => Describe(_services.Store.Load(editor.Id).Screen.Crop), text => text == wanted, 3, 100);
        _report.Check("the editor saves the crop by itself: the project file holds it", saved == wanted, saved == wanted ? saved : $"saved: {saved} | expected: {wanted}");
        CloseQuietly(editor);
    }

    private StudioRect? ScreenCropOf(Editor editor) => OnUi(() =>
    {
        var viewModel = editor.Window.ViewModel;
        var (left, top, right, bottom) = (viewModel.ScreenCropLeft, viewModel.ScreenCropTop, viewModel.ScreenCropRight, viewModel.ScreenCropBottom);
        return left == 0 && top == 0 && right == 0 && bottom == 0 ? null : new StudioRect(left, top, Math.Round(1 - left - right, 6), Math.Round(1 - top - bottom, 6));
    });

    private StudioRect? CameraCropOf(Editor editor) => OnUi(() =>
    {
        var viewModel = editor.Window.ViewModel;
        var (left, top, right, bottom) = (viewModel.CameraCropLeft, viewModel.CameraCropTop, viewModel.CameraCropRight, viewModel.CameraCropBottom);
        return left == 0 && top == 0 && right == 0 && bottom == 0 ? null : new StudioRect(left, top, Math.Round(1 - left - right, 6), Math.Round(1 - top - bottom, 6));
    });

    /// <summary>
    /// A zoom on a cropped screen. The screen is cropped to (0.08, 0.03, 0.52, 0.52), and the
    /// zoom added at the playhead looks at the middle of the whole screen, (0.5, 0.5).
    /// </summary>
    private void ZoomInsideACrop(Editor editor, int frame)
    {
        Timeline.Mark("11: a zoom inside a crop");
        Key(editor, StudioShortcutKey.Z);
        var lane = WaitForLane(editor, "*Zoom 2×, 2.0 to 5.0 seconds");
        var shown = frame + 15;
        Until(() => Playhead(editor), value => FrameOf(value) == shown, 2);

        // The pad stands for the crop: the point (0.5, 0.5) of the screen is (0.5 − 0.08) / 0.52 = 0.8077 across it
        // and (0.5 − 0.03) / 0.52 = 0.9038 down it, and the two sliders say that.
        var across = (0.5 - 0.08) / 0.52;
        var down = (0.5 - 0.03) / 0.52;
        var acrossRead = SliderValue(editor, "StudioZoomFocusXSlider");
        var downRead = SliderValue(editor, "StudioZoomFocusYSlider");

        // Horizontal at 0% and Vertical at 100% is the bottom left corner of the crop: (0.08, 0.55) of the screen.
        SetSlider(editor, "StudioZoomFocusXSlider", 0);
        SetSlider(editor, "StudioZoomFocusYSlider", 1);
        var focus = Until(() => ZoomsOf(editor)[0].Focus, value => Same(value.X, 0.08) && Same(value.Y, 0.55), 1);
        _report.Check(
            "on a cropped screen the focus sliders run across what the crop leaves: they show the zoom's point as a place in the crop, and 0% across and 100% down is the crop's bottom left corner",
            lane == "*Zoom 2×, 2.0 to 5.0 seconds" && Same(acrossRead, across, 1e-6) && Same(downRead, down, 1e-6) && Same(focus.X, 0.08, 1e-9) && Same(focus.Y, 0.55, 1e-9),
            string.Create(CultureInfo.InvariantCulture, $"the lane: {lane}; Horizontal {acrossRead:0.####} and Vertical {downRead:0.####} for the point (0.5, 0.5), wanted {across:0.####} and {down:0.####}; Horizontal 0% and Vertical 100% point the zoom at ({focus.X:0.####}, {focus.Y:0.####}) of the screen"));

        // Scale 2 shows half of what the crop leaves each way: 0.26 of the screen. Around the
        // crop's bottom left corner that would run from 0.08 − 0.13 = −0.05 across, and down to
        // 0.55 + 0.13 = 0.68. Pushed back inside the crop it starts at 0.08 across and at
        // 0.55 − 0.26 = 0.29 down. That part holds the patches and two bar edges, and no frame
        // strip: this check places and sizes the picture, and does not tell which frame it is.
        ShowsPart(
            editor,
            "a zoom on a cropped screen stays inside the crop: looking at the crop's corner, it shows the part pushed back inside what the crop leaves",
            new ScreenPart(0.08, 0.29, 0.26, 0.26),
            shown,
            "half of the crop each way around (0.08, 0.55) would start at −0.05 across and end at 0.68 down; inside the crop it is the part from 0.08 across and 0.29 down");
        Key(editor, StudioShortcutKey.Delete);
        WaitForLane(editor, "empty");
        SetSlider(editor, "StudioPlayhead", MiddleOf(frame));
    }

    // ---------------------------------------------------------------------------------------
    // The camera
    // ---------------------------------------------------------------------------------------

    private void CameraCrop()
    {
        Timeline.Mark("11: the camera's crop");

        // The camera layout: the camera fills the content area, which is the canvas less its padding.
        var folder = NewCameraProject("Camera crop", p => WithScene(OnLemon(p), scene => scene with { Layout = StudioLayout.Camera }));
        if (OpenReady(folder, "camera crop") is not { } editor)
        {
            return;
        }

        const int Frame = 60;
        var cameraFrame = editor.Folder.ExpectedCamera(Frame);
        string[] ids = ["StudioCameraCropLeftSlider", "StudioCameraCropTopSlider", "StudioCameraCropRightSlider", "StudioCameraCropBottomSlider"];
        SetSlider(editor, "StudioPlayhead", MiddleOf(Frame));
        var names = ids.Select(id => Find(editor, id)).Select(slider => slider?.Range is { Value: 0, Minimum: 0 } range && Same(range.Maximum, 0.95) && Same(range.SmallChange, 0.01) ? $"{slider.Name} {slider.ValueText}" : $"{slider?.Name}: not from 0 to 0.95, at 0, in steps of 0.01").ToArray();
        var reset = Find(editor, "StudioCameraCropResetButton");
        _report.Check(
            "the Camera section has four crop sliders, from 0 to 95% in steps of 1%, and Reset crop, which is disabled while nothing is cut off",
            names.SequenceEqual(["Crop left 0%", "Crop top 0%", "Crop right 0%", "Crop bottom 0%"]) && reset is { IsEnabled: false, Name: "Reset crop" },
            $"{string.Join(", ", names)}; {reset} enabled {reset?.IsEnabled}");
        ShowsCameraPart(editor, "before any crop the camera layout shows the camera's picture, as much of it as fills the content area", null, Frame, cameraFrame);

        (string Id, double Value, string Text, StudioRect Crop, string What)[] steps =
        [
            (ids[0], 0.20, "20%", new StudioRect(0.20, 0, 0.80, 1), "the camera's Crop left 20%"),
            (ids[1], 0.10, "10%", new StudioRect(0.20, 0.10, 0.80, 0.90), "the camera's Crop top 10%"),
            (ids[2], 0.10, "10%", new StudioRect(0.20, 0.10, 0.70, 0.90), "the camera's Crop right 10%"),
            (ids[3], 0.20, "20%", new StudioRect(0.20, 0.10, 0.70, 0.70), "the camera's Crop bottom 20%"),
        ];
        foreach (var (id, value, text, crop, what) in steps)
        {
            Timeline.Mark($"11: {what}");
            var set = SetSlider(editor, id, value);
            Expect(editor, p => p with { Camera = p.Camera with { Crop = crop } });
            var holds = Until(() => Describe(CameraCropOf(editor)), now => now == Describe(crop), 1);
            _report.Check(
                $"{what}: the project holds that crop, the slider says {text}, and the size of the video stays",
                set && holds == Describe(crop) && Slider(editor, id, value, text) is null && NameOf(editor, "StudioExportSizeText") == "Exports at 1920 × 1080" && Find(editor, "StudioCameraCropResetButton") is { IsEnabled: true },
                $"the crop is {holds} and should be {Describe(crop)}; the slider: {Slider(editor, id, value, text) ?? $"{F(value)}, \"{text}\""}; \"{NameOf(editor, "StudioExportSizeText")}\"");
            ShowsCameraPart(editor, $"{what}: the preview shows the part of the camera's picture the crop leaves, as much of it as fills the content area", crop, Frame, cameraFrame);
        }

        // Reset, and one Undo.
        Timeline.Mark("11: Reset crop, for the camera");
        var resetFocus = FocusOn(editor, "StudioCameraCropResetButton");
        var resetPressed = Invoke(editor, "StudioCameraCropResetButton");
        Expect(editor, p => p with { Camera = p.Camera with { Crop = null } });
        var values = ids.Select(id => Until(() => SliderValue(editor, id), value => value == 0, 1)).ToArray();
        var resetAfter = Until(() => Find(editor, "StudioCameraCropResetButton", 0.5)?.IsEnabled, enabled => enabled == false, 1);
        _report.Check(
            "Reset crop in the Camera section takes the camera's crop away, puts its four sliders at 0, is disabled again, and leaves the keyboard focus on the camera's Crop left",
            resetPressed && CameraCropOf(editor) is null && values.All(value => value == 0) && resetAfter == false && resetFocus == "StudioCameraCropResetButton" && Until(() => FocusedId(editor), id => id == "StudioCameraCropLeftSlider", 1.5) == "StudioCameraCropLeftSlider",
            $"the crop is {Describe(CameraCropOf(editor))}; the sliders: {string.Join(", ", values.Select(value => F(value)))}; Reset crop enabled {resetAfter}; the focus was on \"{resetFocus}\" and is on \"{FocusedId(editor)}\"");
        ShowsCameraPart(editor, "after Reset crop the preview shows the camera's picture without a crop again", null, Frame, cameraFrame);
        Invoke(editor, "StudioUndoButton");
        var last = steps[^1].Crop;
        Expect(editor, p => p with { Camera = p.Camera with { Crop = last } });
        var back = Until(() => Describe(CameraCropOf(editor)), text => text == Describe(last), 2);
        ShowsCameraPart(editor, "one Undo brings the camera's whole crop back, and the picture with it", last, Frame, cameraFrame);

        // The other layouts that show the camera have the sliders too, and the one that hides it does not.
        Find(editor, "StudioLayoutBubble")?.Select();
        var inBubble = Until(() => ids.Count(id => editor.Root.Find(id) is not null), count => count == 4, 2);
        Find(editor, "StudioLayoutSideBySide")?.Select();
        Until(() => editor.Root.Find("StudioCameraShareSlider"), found => found is not null, 2);
        var sideBySide = ids.Count(id => editor.Root.Find(id) is not null);
        Find(editor, "StudioLayoutScreen")?.Select();
        Until(() => editor.Root.Find("StudioCameraHiddenNote"), found => found is not null, 2);
        var hidden = ids.Count(id => editor.Root.Find(id) is not null) + (editor.Root.Find("StudioCameraCropResetButton") is null ? 0 : 1);
        Expect(editor, p => WithScene(p, scene => scene with { Layout = StudioLayout.Screen }));
        _report.Check(
            "the camera's crop sliders are there in every layout that shows the camera, with the crop they were left at, and gone in the one that hides it",
            back == Describe(last) && inBubble == 4 && sideBySide == 4 && hidden == 0,
            $"after the Undo the crop is {back}; crop sliders in the bubble layout: {inBubble}, side by side: {sideBySide}; with the screen layout, sliders and Reset crop: {hidden}");

        var wanted = Describe(editor.Expected.Camera.Crop);
        var saved = Until(() => Describe(_services.Store.Load(editor.Id).Camera.Crop), text => text == wanted, 3, 100);
        _report.Check("the editor saves the camera's crop by itself: the project file holds it", saved == wanted, saved == wanted ? saved : $"saved: {saved} | expected: {wanted}");
        CloseQuietly(editor);
    }

    /// <summary>
    /// One check that the camera layout shows a part of the camera's picture at a frame. The
    /// part is worked out here from section 6.4 of the project format: the camera's content, which
    /// is its crop, is fitted to fill the layer's rectangle and centered, so what does not fit the
    /// rectangle's shape is cut off the content's two sides, or off its top and bottom.
    /// </summary>
    private void ShowsCameraPart(Editor editor, string name, StudioRect? crop, int frame, int cameraFrame)
    {
        var camera = editor.Folder.Camera!;
        var content = crop ?? new StudioRect(0, 0, 1, 1);
        (Shot Shot, StudioFrameRect Layer, ScreenPart Part, PartReading Reading)? Look()
        {
            var project = editor.Expected;
            if (editor.Camera.Take() is not { } shot || CanvasBox(editor, shot) is not { } canvas || Layers(canvas, project).Camera is not { } resolved)
            {
                return null;
            }

            // Layers gives the rectangle in pixels of the screenshot, and the canvas starts on a whole pixel.
            var layer = Aligned(resolved.Rect);

            // ac is the shape of the content, ad the shape of the rectangle it has to fill.
            var contentShape = (camera.Width * content.Width) / (camera.Height * content.Height);
            var layerShape = resolved.Rect.Width / resolved.Rect.Height;
            ScreenPart part;
            if (contentShape > layerShape)
            {
                var kept = layerShape / contentShape;
                part = new ScreenPart(content.X + (content.Width * (1 - kept) / 2), content.Y, content.Width * kept, content.Height);
            }
            else
            {
                var kept = contentShape / layerShape;
                part = new ScreenPart(content.X, content.Y + (content.Height * (1 - kept) / 2), content.Width, content.Height * kept);
            }

            // Clear of the layer's round corners, which are the screen's corner radius here.
            var inset = (Math.Clamp(project.Screen.CornerRadius, 0, 0.2) * Math.Min(canvas.Width, canvas.Height)) + 2;
            return (shot, layer, part, ReadPart(shot, camera, CameraLandmarks(cameraFrame), layer, part, resolved.Mirror, inset));
        }

        var sight = Until(Look, look => look is { } seen && Judge(seen.Reading, cameraFrame) is null, 3, 30);
        if (sight is not { } read)
        {
            _report.Check(name, false, "no screenshot, or the layout has no camera");
            return;
        }

        var wrong = Judge(read.Reading, cameraFrame) ?? EndsAtItsRectangle(read.Shot, read.Layer, Lemon);
        if (wrong is null)
        {
            _partErrors.Add(read.Reading.Worst);
        }

        var fitted = Fitted(read.Reading, camera, mirror: true);
        _report.Check(
            name,
            wrong is null,
            wrong is null
                ? $"the content {Describe(crop) switch { "none" => "(0, 0, 1, 1)", var text => text }} fills a layer of {R(read.Layer)} with its part {read.Part}, mirrored: {read.Reading}; the edges fit the part {fitted?.ToString() ?? "(too few to tell)"}"
                : $"wanted the part {read.Part} in a layer of {R(read.Layer)}; {wrong}{(fitted is { } part ? $"; the edges that were found fit the part {part}" : string.Empty)}");
    }
}
