using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyClips.App.Controls.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 10, continued. The Zoom panel of the inspector, the focus pad, drags on the lane, a zoom that
// follows the pointer, and suggested zooms.
internal sealed partial class WindowChecks
{
    private const string FirstZoomName = "Zoom 2×, 2.0 to 5.0 seconds";
    private const string SecondZoomName = "Zoom 2×, 6.5 to 9.5 seconds";
    private const string ThirdZoomName = "Zoom 2×, 10.0 to 12.0 seconds";

    /// <summary>
    /// Three zooms as the first group of checks leaves them: the first looks at the pointer's
    /// first place and the other two at its second, and each starts in the middle of a frame.
    /// </summary>
    private static StudioZoom[] ThreeZooms() =>
    [
        PointZoom(MiddleOf(60), MiddleOf(60) + 3, PointerFirst.X, PointerFirst.Y),
        PointZoom(MiddleOf(195), MiddleOf(195) + 3, PointerSecond.X, PointerSecond.Y),
        PointZoom(MiddleOf(300), 12, PointerSecond.X, PointerSecond.Y),
    ];

    // ---------------------------------------------------------------------------------------
    // The Zoom panel
    // ---------------------------------------------------------------------------------------

    private void ZoomSection()
    {
        Timeline.Mark("10: the Zoom panel");
        var folder = NewEventsProject("Zoom panel", PointerSamples(), clicks: [], p => p with { Zooms = ThreeZooms() });
        if (OpenReady(folder, "zoom section") is not { } editor)
        {
            return;
        }

        using var heard = UiaEvents.Listen(_uia, editor.Root);
        const int Shown = 75;
        var others = $" | {SecondZoomName} | {ThirdZoomName}";
        var opened = WaitForLane(editor, FirstZoomName + others);
        var pressed = Find(editor, "StudioZoom_0")?.Invoke() ?? false;
        var selected = WaitForLane(editor, "*" + FirstZoomName + others);
        var head = Until(() => Playhead(editor), value => Same(value, MiddleOf(Shown)), 2);
        _report.Check(
            "a project that comes with zooms shows them on the lane, none selected; pressing the first one selects it and moves the playhead to where it has moved in",
            opened == FirstZoomName + others && pressed && selected == "*" + FirstZoomName + others && Same(head, MiddleOf(Shown)),
            $"opened: {opened}; pressed: {selected}; playhead {Seconds(head)} s (frame {FrameOf(head)})");
        ShowsPart(editor, "pressing a zoom on the lane shows it: the preview is zoomed on the part that zoom holds", HeldAtFirst, Shown, "scale 2 around (0.30, 0.28) is the part from 0.05 across and 0.03 down, half the screen each way");
        ZoomSectionShows(editor, "the Zoom panel shows the zoom that was pressed", "Zoom 1 of 3", "2.0 to 5.0 seconds", 2, "2×", PointerFirst, "Start 2.0 seconds", "End 5.0 seconds", 0.5, 0.5);

        // With a zoom selected: padding, two sliders for the screen and four for its crop, five for the zoom, and the three of the trim bar.
        AuditState(editor, "the editor with a zoom selected", "tree-zoom.txt", 15, tree =>
            tree.Any(entry => entry.Element.Id == "StudioZoomFocusPad") ? "the focus pad is in what a screen reader walks"
            : !PadShows(editor) ? "the focus pad is not shown"
            : Find(editor, "StudioZoomFocusXSlider", 0.5) is not { Name: "Horizontal" } || Find(editor, "StudioZoomFocusYSlider", 0.5) is not { Name: "Vertical" } ? "the two sliders that stand for the focus pad are not called Horizontal and Vertical"
            : Find(editor, "StudioZoomFocusChoice", 0.5) is not { Name: "Focus" } ? $"the choice of what the zoom looks at is called \"{Find(editor, "StudioZoomFocusChoice", 0)?.Name}\""
            : null);
        _report.Note($"the focus pad is shown, and is not among what a screen reader walks; among everything UI Automation knows of the window it is {(editor.Root.FindRaw("StudioZoomFocusPad") is null ? "not there either" : "there, marked as not for reading")}");

        // What each of the new controls is called, as a screen reader says it.
        (string Id, string Name)[] called =
        [
            ("StudioAddZoomButton", "Add zoom"), ("StudioZoomLane", "Zooms"), ("StudioZoom_0", FirstZoomName),
            ("StudioPreviousZoomButton", "Previous zoom"), ("StudioZoomPositionText", "Zoom 1 of 3"), ("StudioZoomRangeText", "2.0 to 5.0 seconds"), ("StudioNextZoomButton", "Next zoom"),
            ("StudioZoomSectionAddButton", "Add zoom"), ("StudioSuggestZoomsButton", "Suggest zooms"),
            ("StudioZoomScaleSlider", "Zoom level"), ("StudioZoomFocusChoice", "Focus"), ("StudioZoomFocusPoint", "Fixed point"), ("StudioZoomFocusPointer", "Follow pointer"),
            ("StudioZoomFocusXSlider", "Horizontal"), ("StudioZoomFocusYSlider", "Vertical"),
            ("StudioZoomStartText", "Start 2.0 seconds"), ("StudioZoomStartEarlierButton", "Start 0.1 seconds earlier"), ("StudioZoomStartLaterButton", "Start 0.1 seconds later"), ("StudioZoomStartAtPlayheadButton", "Start at playhead"),
            ("StudioZoomEndText", "End 5.0 seconds"), ("StudioZoomEndEarlierButton", "End 0.1 seconds earlier"), ("StudioZoomEndLaterButton", "End 0.1 seconds later"), ("StudioZoomEndAtPlayheadButton", "End at playhead"),
            ("StudioZoomEaseInSlider", "Zoom-in time"), ("StudioZoomEaseOutSlider", "Zoom-out time"), ("StudioDeleteZoomButton", "Delete zoom"),
            ("StudioScreenCropGroup", "Screen crop"),
            ("StudioScreenCropLeftSlider", "Screen crop left"), ("StudioScreenCropTopSlider", "Screen crop top"), ("StudioScreenCropRightSlider", "Screen crop right"), ("StudioScreenCropBottomSlider", "Screen crop bottom"), ("StudioScreenCropResetButton", "Reset crop"),
        ];
        var miscalled = called.Select(control => (control.Id, control.Name, Is: editor.Root.Find(control.Id)?.Name)).Where(control => control.Is != control.Name).ToArray();
        _report.Check(
            "each of the new controls is called what it is: the lane and its items, both Add zoom buttons, every control of the Zoom panel, and the Screen panel's Crop group with its four sliders, which say whose crop they are",
            miscalled.Length == 0,
            miscalled.Length == 0
                ? string.Join(", ", called.Select(control => $"\"{control.Name}\"").Distinct())
                : string.Join("; ", miscalled.Select(control => $"{control.Id} is called \"{control.Is ?? "(not found)"}\" and should be \"{control.Name}\"")));
        TabOrder(editor);

        // Zoom level. The part the zoom holds would start before the screen, and is pushed back inside.
        Timeline.Mark("10: scale and focus");
        var scaleSet = SetSlider(editor, "StudioZoomScaleSlider", 1.6);
        var afterScale = WaitForLane(editor, "*Zoom 1.6×, 2.0 to 5.0 seconds" + others);
        _report.Check(
            "Zoom level set to 1.6: the zoom holds it, the slider and the lane say 1.6×",
            scaleSet && ZoomsOf(editor)[0].Scale == 1.6 && Slider(editor, "StudioZoomScaleSlider", 1.6, "1.6×") is null && afterScale == "*Zoom 1.6×, 2.0 to 5.0 seconds" + others,
            $"the zoom: {Describe(ZoomsOf(editor)[0])}; the slider: {Slider(editor, "StudioZoomScaleSlider", 1.6, "1.6×") ?? "1.6, \"1.6×\""}; the lane: {afterScale}");
        ShowsPart(editor, "at scale 1.6 the preview shows five eighths of the screen each way, pushed back inside the screen where it would start before it", new ScreenPart(0, 0, 0.625, 0.625), Shown, "1 / 1.6 = 0.625 of the screen; around (0.30, 0.28) it would start at −0.0125 and −0.0325, so it starts at 0 and 0");

        // The two focus sliders, which are the focus pad's equivalent.
        var acrossSet = SetSlider(editor, "StudioZoomFocusXSlider", 0.5);
        Until(() => ZoomsOf(editor)[0].Focus.X, value => Same(value, 0.5), 1);
        var afterAcross = ZoomsOf(editor)[0];
        _report.Check(
            "Horizontal set to 50%: the zoom looks at the middle across and where it did down, and the lane stays as it is",
            acrossSet && Same(afterAcross.Focus.X, 0.5, 1e-9) && Same(afterAcross.Focus.Y, PointerFirst.Y, 1e-9) && Slider(editor, "StudioZoomFocusXSlider", 0.5, "50%") is null && LaneText(editor) == afterScale,
            $"the zoom: {Describe(afterAcross)}; the slider: {Slider(editor, "StudioZoomFocusXSlider", 0.5, "50%") ?? "0.5, \"50%\""}");
        ShowsPart(editor, "with the focus at 50% across the preview shows the part that starts 0.1875 across", new ScreenPart(0.1875, 0, 0.625, 0.625), Shown, "0.5 − 0.625 / 2 = 0.1875 across; down it still starts at 0");

        var downSet = SetSlider(editor, "StudioZoomFocusYSlider", 0.6);
        Until(() => ZoomsOf(editor)[0].Focus.Y, value => Same(value, 0.6), 1);
        var afterDown = ZoomsOf(editor)[0];
        _report.Check(
            "Vertical set to 60%: the zoom looks there, and across where it did",
            downSet && Same(afterDown.Focus.X, 0.5, 1e-9) && Same(afterDown.Focus.Y, 0.6, 1e-9) && Slider(editor, "StudioZoomFocusYSlider", 0.6, "60%") is null,
            $"the zoom: {Describe(afterDown)}; the slider: {Slider(editor, "StudioZoomFocusYSlider", 0.6, "60%") ?? "0.6, \"60%\""}");

        // This part has no frame strip in it, and its only level edges are the tops and bottoms of
        // the patches, 72 clip pixels apart. So this one check cannot tell the right picture from
        // one stretched down the screen about the patches by less than about one part in 40.
        ShowsPart(editor, "with the focus at 60% down the preview shows the part that starts 0.2875 down", new ScreenPart(0.1875, 0.2875, 0.625, 0.625), Shown, "0.6 − 0.625 / 2 = 0.2875 down");

        FocusPad(editor);

        // Back to scale 2 for the eases, with the focus where the pad left it: the pointer's first place.
        SetSlider(editor, "StudioZoomScaleSlider", 2);
        WaitForLane(editor, "*" + FirstZoomName + others);

        // Ease in. The playhead is half a second into the zoom, which is now half way through
        // moving in. Half way, the format's curve and an even pace agree: this check tells that
        // the zoom is on its way and how far, and could not tell a preview that moved at an even
        // pace from the right one. The check after it, and the scenes read while a zoom plays, can.
        Timeline.Mark("10: the eases");
        var easeInSet = SetSlider(editor, "StudioZoomEaseInSlider", 1);
        Until(() => ZoomsOf(editor)[0].EaseIn, value => value == 1, 1);
        _report.Check(
            "Zoom-in time set to 1 second: the zoom holds it and the slider says so",
            easeInSet && ZoomsOf(editor)[0].EaseIn == 1 && Slider(editor, "StudioZoomEaseInSlider", 1, "1.0 seconds") is null && LaneText(editor) == "*" + FirstZoomName + others,
            $"the zoom: {Describe(ZoomsOf(editor)[0])}; the slider: {Slider(editor, "StudioZoomEaseInSlider", 1, "1.0 seconds") ?? "1, \"1.0 seconds\""}");
        ShowsPart(
            editor,
            "half way through a one-second ease in, the preview shows the part half way between the whole screen and the part the zoom holds",
            ScreenPart.Between(ScreenPart.Whole, HeldAtFirst, 0.5),
            Shown,
            "u = 0.5, and u × u × (3 − 2u) = 0.5: from (0, 0, 1, 1) half way to (0.05, 0.03, 0.5, 0.5) is (0.025, 0.015, 0.75, 0.75)");

        // Ease out. One second in and two and a half out are more than the three seconds the zoom
        // lasts, so both are shortened by 3 / 3.5: the ease in to 0.857 s. Half a second into it is
        // u = 0.5833, and u × u × (3 − 2u) = 0.6238.
        var easeOutSet = SetSlider(editor, "StudioZoomEaseOutSlider", 2.5);
        Until(() => ZoomsOf(editor)[0].EaseOut, value => value == 2.5, 1);
        var u = 0.5 / (1 * 3 / 3.5);
        var eased = u * u * (3 - (2 * u));
        _report.Check(
            "Zoom-out time set to 2.5 seconds: the zoom holds it and the slider says so",
            easeOutSet && ZoomsOf(editor)[0].EaseOut == 2.5 && Slider(editor, "StudioZoomEaseOutSlider", 2.5, "2.5 seconds") is null,
            $"the zoom: {Describe(ZoomsOf(editor)[0])}; the slider: {Slider(editor, "StudioZoomEaseOutSlider", 2.5, "2.5 seconds") ?? "2.5, \"2.5 seconds\""}");
        ShowsPart(
            editor,
            "eases that are longer than the zoom are shortened in proportion: the preview shows the part 0.6238 of the way to the one the zoom holds",
            ScreenPart.Between(ScreenPart.Whole, HeldAtFirst, eased),
            Shown,
            string.Create(CultureInfo.InvariantCulture, $"1 s in and 2.5 s out of a 3 s zoom become 0.857 s and 2.143 s; 0.5 s in is u = {u:0.0000}, eased {eased:0.0000}"));
        SetSlider(editor, "StudioZoomEaseOutSlider", 0.5);
        SetSlider(editor, "StudioZoomEaseInSlider", 0.5);
        Until(() => ZoomsOf(editor)[0], zoom => zoom.EaseIn == 0.5 && zoom.EaseOut == 0.5, 1);

        StartAndEnd(editor, heard, others);

        // Delete zoom.
        Timeline.Mark("10: Delete zoom");
        var mark = heard.Mark();

        // With the keyboard focus on the button, as when it is pressed with the keyboard: the button goes away with the zoom.
        var focusBefore = FocusOn(editor, "StudioDeleteZoomButton");
        var deletePressed = Invoke(editor, "StudioDeleteZoomButton");
        var afterDelete = WaitForLane(editor, $"{SecondZoomName} | {ThirdZoomName}");
        var deleted = Said(heard, mark, StudioEditorText.ZoomDeletedMessage);
        _report.Check(
            "Delete zoom removes the selected zoom, leaves none selected, and tells a screen reader",
            deletePressed && afterDelete == $"{SecondZoomName} | {ThirdZoomName}" && deleted.Said && ZoomsOf(editor).Length == 2 && NameOf(editor, "StudioZoomPositionText") == "2 zooms" && Gone(editor, "StudioDeleteZoomButton", 1),
            $"the lane: {afterDelete}; sent: {deleted.Heard}; \"{NameOf(editor, "StudioZoomPositionText")}\"");
        var focusAfter = Until(() => FocusedId(editor), id => id == "StudioNextZoomButton", 1.5);
        _report.Check(
            "after Delete zoom, which goes away with the zoom, the keyboard focus is on Next zoom, from where the zooms that are left are stepped to",
            focusBefore == "StudioDeleteZoomButton" && focusAfter == "StudioNextZoomButton" && Find(editor, "StudioNextZoomButton", 0.5) is { IsEnabled: true },
            $"the focus was on \"{focusBefore}\" and is on \"{focusAfter}\"");
        CloseQuietly(editor);
    }

    /// <summary>
    /// Start and End of the selected zoom: the buttons that step a tenth of a second, and the
    /// ones that set them to the playhead. The zoom is the first of three, from 2.0 to 5.0 s, and
    /// the playhead is half a second into it.
    /// </summary>
    private void StartAndEnd(Editor editor, UiaEvents heard, string others)
    {
        Timeline.Mark("10: Start and End");
        var wrong = new List<string>();
        var steps = new List<string>();
        var start = ZoomsOf(editor)[0].Start;
        var end = ZoomsOf(editor)[0].End;
        var head = Playhead(editor);
        var lane = Find(editor, "StudioZoomLane")?.Bounds ?? default;
        var pixelsPerSecond = (lane.Width - (2 * 12 * editor.Scale)) / RecordingLength;
        void Press(string button, double wantedStart, double wantedEnd, string name, string said)
        {
            var mark = heard.Mark();
            var blockBefore = BlockOnLane(editor, "StudioZoom_0");
            var pressed = Invoke(editor, button);
            var text = WaitForLane(editor, $"*{name}{others}");
            var zoom = ZoomsOf(editor)[0];
            var sentence = Said(heard, mark, said);

            // The block's two edges follow the zoom's two times: where the lane itself has the
            // block, which it sets at once. Until 7 October 2026 this read the block's
            // rectangle on the screen, which gets its place at once and its width with the next
            // layout: read between the two, in two runs of that day, both edges had moved.
            var leftWanted = (wantedStart - start) * pixelsPerSecond;
            var rightWanted = (wantedEnd - end) * pixelsPerSecond;
            var block = BlockOnLane(editor, "StudioZoom_0");
            var leftMoved = (block.Left - blockBefore.Left) * editor.Scale;
            var rightMoved = (block.Left + block.Width - blockBefore.Left - blockBefore.Width) * editor.Scale;

            // The rectangle a screen reader is given comes with the layout, and is the block
            // with the handles that stand outside it while it is narrow.
            var onScreen = Until(
                () => Find(editor, "StudioZoom_0")?.Bounds ?? default,
                now => Math.Abs(now.X - lane.X - ((block.Left - block.Outset) * editor.Scale)) <= 1.5 && Math.Abs(now.Width - ((block.Width + (2 * block.Outset)) * editor.Scale)) <= 1.5,
                1);
            var isOnScreen = Math.Abs(onScreen.X - lane.X - ((block.Left - block.Outset) * editor.Scale)) <= 1.5 && Math.Abs(onScreen.Width - ((block.Width + (2 * block.Outset)) * editor.Scale)) <= 1.5;
            var startText = NameOf(editor, "StudioZoomStartText");
            var endText = NameOf(editor, "StudioZoomEndText");
            steps.Add($"{button.Replace("Studio", string.Empty, StringComparison.Ordinal).Replace("Button", string.Empty, StringComparison.Ordinal)}: {Seconds(zoom.Start)} to {Seconds(zoom.End)} s, said \"{said}\"");
            if (!pressed || !Same(zoom.Start, wantedStart) || !Same(zoom.End, wantedEnd) || text != $"*{name}{others}" || !sentence.Said
                || Math.Abs(leftMoved - leftWanted) > 1.5 || Math.Abs(rightMoved - rightWanted) > 1.5 || !isOnScreen || !Same(Playhead(editor), head)
                || startText != "Start " + StudioEditorModel.GetSecondsText(wantedStart) || endText != "End " + StudioEditorModel.GetSecondsText(wantedEnd))
            {
                wrong.Add($"{button}: pressed {pressed}; the zoom is {Seconds(zoom.Start)} to {Seconds(zoom.End)} s and should be {Seconds(wantedStart)} to {Seconds(wantedEnd)} s; the lane: {text}; sent: {sentence.Heard}; "
                    + $"the block's edges moved {F(leftMoved, "0.#")} and {F(rightMoved, "0.#")} px, wanted {F(leftWanted, "0.#")} and {F(rightWanted, "0.#")}; its rectangle for a screen reader is {onScreen.Width} wide at {onScreen.X - lane.X}, and the lane has it {F(block.Width * editor.Scale, "0.#")} wide at {F(block.Left * editor.Scale, "0.#")} with handles {F(block.Outset * editor.Scale, "0.#")} outside it; \"{startText}\", \"{endText}\"; playhead {Seconds(Playhead(editor))} s");
            }

            (start, end) = (zoom.Start, zoom.End);
        }

        Press("StudioZoomStartLaterButton", start + 0.1, end, "Zoom 2×, 2.1 to 5.0 seconds", "Start 2.1 seconds");
        Press("StudioZoomStartEarlierButton", start - 0.1, end, "Zoom 2×, 2.0 to 5.0 seconds", "Start 2.0 seconds");
        Press("StudioZoomStartEarlierButton", start - 0.1, end, "Zoom 2×, 1.9 to 5.0 seconds", "Start 1.9 seconds");
        Press("StudioZoomStartAtPlayheadButton", head, end, "Zoom 2×, 2.5 to 5.0 seconds", "Start 2.5 seconds");
        Press("StudioZoomEndEarlierButton", start, end - 0.1, "Zoom 2×, 2.5 to 4.9 seconds", "End 4.9 seconds");
        Press("StudioZoomEndLaterButton", start, end + 0.1, "Zoom 2×, 2.5 to 5.0 seconds", "End 5.0 seconds");
        Press("StudioZoomEndLaterButton", start, end + 0.1, "Zoom 2×, 2.5 to 5.1 seconds", "End 5.1 seconds");

        // The playhead a second and a half into the zoom, and its end set there.
        SetSlider(editor, "StudioPlayhead", MiddleOf(120));
        head = Playhead(editor);
        Press("StudioZoomEndAtPlayheadButton", start, head, "Zoom 2×, 2.5 to 4.0 seconds", "End 4.0 seconds");

        // A zoom is never shorter than 0.3 s: its end set to a playhead before that stops there, and that is what is said.
        SetSlider(editor, "StudioPlayhead", 2.6);
        head = Playhead(editor);
        Press("StudioZoomEndAtPlayheadButton", start, start + 0.3, "Zoom 2×, 2.5 to 2.8 seconds", "End 2.8 seconds");
        (string Id, string Name)[] buttons =
        [
            ("StudioZoomStartEarlierButton", "Start 0.1 seconds earlier"), ("StudioZoomStartLaterButton", "Start 0.1 seconds later"), ("StudioZoomStartAtPlayheadButton", "Start at playhead"),
            ("StudioZoomEndEarlierButton", "End 0.1 seconds earlier"), ("StudioZoomEndLaterButton", "End 0.1 seconds later"), ("StudioZoomEndAtPlayheadButton", "End at playhead"),
        ];
        var misnamed = buttons.Where(button => Find(editor, button.Id, 0.5)?.Name != button.Name).Select(button => $"{button.Id} is called \"{Find(editor, button.Id, 0)?.Name}\"").ToArray();
        _report.Check(
            "the Start and End buttons move the zoom's start and end a tenth of a second, or to the playhead; the block on the lane, its name and the two times in the panel follow, a screen reader is told the new time, the playhead stays, and an end set too close to the start stops 0.3 s after it",
            wrong.Count == 0 && misnamed.Length == 0,
            wrong.Count == 0 && misnamed.Length == 0 ? string.Join("; ", steps) : string.Join("; ", wrong.Concat(misnamed)));
    }

    /// <summary>
    /// Whether the focus pad is shown: it and everything it is inside of is visible, and it has a
    /// size. It is asked of the window's own elements, because the pad is not an element for
    /// UI Automation.
    /// </summary>
    private bool PadShows(Editor editor) => OnUi(() =>
    {
        if (Descendant<StudioFocusPad>(editor.Window.Content, "StudioZoomFocusPad") is not { ActualWidth: > 0, ActualHeight: > 0 } pad)
        {
            return false;
        }

        for (DependencyObject? at = pad; at is not null; at = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(at))
        {
            if (at is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    });

    /// <summary>
    /// The focus pad: what it draws, read from where its parts are, and what a press and a drag
    /// on it do, by calling what its pointer handlers call. No pointer is moved or pressed.
    /// </summary>
    private void FocusPad(Editor editor)
    {
        Timeline.Mark("10: the focus pad");

        // The zoom is at scale 1.6 and looks at (0.5, 0.6): it holds the part from 0.1875 across and 0.2875 down, 0.625 each way.
        var drawn = OnUi(() =>
        {
            if (Descendant<StudioFocusPad>(editor.Window.Content, "StudioZoomFocusPad") is not { } pad
                || pad.FindName("FocusDot") is not FrameworkElement dot
                || pad.FindName("WindowOutline") is not FrameworkElement outline)
            {
                return ((double, double, double, double, double, double, double, double)?)null;
            }

            return (pad.ActualWidth, pad.ActualHeight,
                Canvas.GetLeft(dot) + (dot.Width / 2), Canvas.GetTop(dot) + (dot.Height / 2),
                Canvas.GetLeft(outline), Canvas.GetTop(outline), outline.Width, outline.Height);
        });
        if (drawn is not var (width, height, dotX, dotY, left, top, partWidth, partHeight))
        {
            _report.Check("the focus pad shows the selected zoom", false, "the pad or one of its parts was not found");
            return;
        }

        bool Near(double value, double wanted) => Math.Abs(value - wanted) <= 0.5;
        _report.Check(
            "the focus pad has the screen's shape and is at most 160 high; its rectangle is the part the zoom holds and its dot the point the zoom looks at",
            Math.Abs((width / height) - (16.0 / 9)) < 0.02 && height <= 160.5 && height > 100
                && Near(dotX, 0.5 * width) && Near(dotY, 0.6 * height)
                && Near(left, 0.1875 * width) && Near(top, 0.2875 * height) && Near(partWidth, 0.625 * width) && Near(partHeight, 0.625 * height),
            $"pad {F(width, "0.#")}x{F(height, "0.#")}; dot at ({F(dotX, "0.#")}, {F(dotY, "0.#")}), wanted ({F(0.5 * width, "0.#")}, {F(0.6 * height, "0.#")}); "
                + $"rectangle {F(partWidth, "0.#")}x{F(partHeight, "0.#")} at ({F(left, "0.#")}, {F(top, "0.#")}), wanted {F(0.625 * width, "0.#")}x{F(0.625 * height, "0.#")} at ({F(0.1875 * width, "0.#")}, {F(0.2875 * height, "0.#")})");

        // A press, a drag past the pad's edge and back, and letting go.
        var undoBefore = Find(editor, "StudioUndoButton")?.IsEnabled;
        var during = new List<string>();
        OnUi(() =>
        {
            var pad = Descendant<StudioFocusPad>(editor.Window.Content, "StudioZoomFocusPad")!;
            StudioZoomFocus Focus() => editor.Window.ViewModel.Zooms[0].Focus;
            pad.PressAt(0.2 * width, 0.9 * height);
            during.Add($"pressed at 20%, 90%: ({F(Focus().X)}, {F(Focus().Y)})");
            pad.DragTo(1.5 * width, -0.5 * height);
            during.Add($"dragged past the top right corner: ({F(Focus().X)}, {F(Focus().Y)})");
            pad.DragTo(PointerFirst.X * width, PointerFirst.Y * height);
            pad.EndPress();
        });
        var moved = ZoomsOf(editor)[0].Focus;
        Invoke(editor, "StudioUndoButton");
        Until(() => ZoomsOf(editor)[0].Focus.X, value => Same(value, 0.5), 1);
        var undone = ZoomsOf(editor)[0].Focus;
        Invoke(editor, "StudioRedoButton");
        Until(() => ZoomsOf(editor)[0].Focus.X, value => Same(value, PointerFirst.X), 1);
        var redone = ZoomsOf(editor)[0].Focus;
        _report.Check(
            "a press on the focus pad points the zoom there, a drag moves the point with it and stops at the pad's edge, and the whole drag is one undo step (by what the pad's pointer handlers call)",
            during.SequenceEqual(["pressed at 20%, 90%: (0.2, 0.9)", "dragged past the top right corner: (1, 0)"])
                && Same(moved.X, PointerFirst.X, 1e-9) && Same(moved.Y, PointerFirst.Y, 1e-9) && Same(undone.X, 0.5, 1e-9) && Same(undone.Y, 0.6, 1e-9) && Same(redone.X, PointerFirst.X, 1e-9) && Same(redone.Y, PointerFirst.Y, 1e-9)
                && Slider(editor, "StudioZoomFocusXSlider", PointerFirst.X, "30%") is null && Slider(editor, "StudioZoomFocusYSlider", PointerFirst.Y, "28%") is null,
            $"{string.Join("; ", during)}; let go at 30%, 28%: ({F(moved.X)}, {F(moved.Y)}); after one Undo ({F(undone.X)}, {F(undone.Y)}); after Redo ({F(redone.X)}, {F(redone.Y)}); Undo was enabled before: {undoBefore}");
    }

    /// <summary>
    /// The order the keyboard focus moves in with a zoom selected: see <see cref="TabStops"/>
    /// for how it is read. "Focus" is a group of radio buttons, which is one stop, and so is
    /// the header of the Screen panel's Crop group, whose sliders are stops while it is open.
    /// </summary>
    private void TabOrder(Editor editor)
    {
        Timeline.Mark("10: the order of the tab stops");

        // The Crop group is closed while nothing is cropped. Looking for one of its sliders opens it.
        Find(editor, "StudioScreenCropLeftSlider");
        var order = TabStops(editor);
        var path = Path.Combine(_output, "tab-order.txt");
        File.WriteAllLines(path, order);

        // The stops this pass added, in the order they should come in, among the ones around them.
        string[] wanted =
        [
            "StudioScreenShadowSlider", "StudioClickRingsCheckBox", "StudioScreenCropGroup", "StudioScreenCropLeftSlider", "StudioScreenCropTopSlider", "StudioScreenCropRightSlider", "StudioScreenCropBottomSlider",
            "StudioNextZoomButton", "StudioZoomSectionAddButton", "StudioZoomScaleSlider", "StudioZoomFocusChoice", "StudioZoomFocusXSlider", "StudioZoomFocusYSlider",
            "StudioZoomStartEarlierButton", "StudioZoomStartLaterButton", "StudioZoomStartAtPlayheadButton", "StudioZoomEndEarlierButton", "StudioZoomEndLaterButton", "StudioZoomEndAtPlayheadButton",
            "StudioZoomEaseInSlider", "StudioZoomEaseOutSlider", "StudioDeleteZoomButton", "StudioMuteCheckBox",
            "StudioPlayPauseButton", "StudioPreviousFrameButton", "StudioNextFrameButton", "StudioAddZoomButton", "StudioStartHereButton", "StudioEndHereButton", "StudioZoomLane", "StudioTrimStart", "StudioTrimEnd", "StudioPlayhead",
        ];
        var places = wanted.Select(id => order.IndexOf(id)).ToArray();
        var missing = wanted.Where((_, index) => places[index] < 0).ToArray();
        var outOfOrder = places.Where(place => place >= 0).ToArray() is var found && !found.SequenceEqual(found.Order());
        var unexpected = order.Where(id => id is "StudioZoomFocusPad" or "StudioZoom_0" or "StudioZoom_1" or "StudioZoom_2").ToArray();
        _report.Check(
            "the keyboard focus, moved from stop to stop with each panel on show in turn, reaches Click highlights, the Crop group's header and its four sliders after the screen's shadow, then every control of the Zoom panel in the order they are shown, then Mute; in the timeline Add zoom comes before Start here, and the lane is one stop between End here and the trim bar; the focus pad and the single zooms are not stops",
            missing.Length == 0 && !outOfOrder && unexpected.Length == 0,
            missing.Length == 0 && !outOfOrder && unexpected.Length == 0
                ? $"{order.Count} stops, saved as {Path.GetFileName(path)}: {string.Join(", ", order.Select(id => id.Replace("Studio", string.Empty, StringComparison.Ordinal)))}"
                : $"{(missing.Length == 0 ? string.Empty : "not reached: " + string.Join(", ", missing) + "; ")}{(outOfOrder ? "out of order; " : string.Empty)}{(unexpected.Length == 0 ? string.Empty : "also stops: " + string.Join(", ", unexpected) + "; ")}the order: {string.Join(", ", order)}");
    }

    // ---------------------------------------------------------------------------------------
    // Drags on the lane
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// What a press and a drag on the lane do, by calling what the lane's pointer handlers call
    /// with a place along the lane. No pointer is moved or pressed, so which element a real
    /// pointer would land on, and that the lane keeps the pointer while it is down, is not seen.
    /// </summary>
    private void DraggingOnTheLane()
    {
        Timeline.Mark("10: presses and drags on the lane");
        StudioZoom[] zooms = [PointZoom(2, 5, 0.5, 0.5), PointZoom(7, 9, 0.5, 0.5), PointZoom(10, 10.06, 0.5, 0.5)];
        var folder = NewScreenProject("Zoom drags", p => p with { Zooms = zooms });
        if (OpenReady(folder, "zoom drags") is not { } editor)
        {
            return;
        }

        WaitForLane(editor, "Zoom 2×, 2.0 to 5.0 seconds | Zoom 2×, 7.0 to 9.0 seconds | Zoom 2×, 10.0 to 10.1 seconds");

        // The lane in effective pixels, which is what its pointer handlers work in.
        var laneBounds = Find(editor, "StudioZoomLane")?.Bounds ?? default;
        var laneWidth = laneBounds.Width / editor.Scale;
        var secondsPerPixel = RecordingLength / (laneWidth - 24);
        double XOf(double time) => 12 + (time / secondsPerPixel);
        (double Left, double Width) Block(int index)
        {
            var bounds = Find(editor, $"StudioZoom_{index}")?.Bounds ?? default;
            return ((bounds.X - laneBounds.X) / editor.Scale, bounds.Width / editor.Scale);
        }

        void Press(double x) => OnUi(() => LaneOf(editor)!.PressAt(x));
        void Drag(double x) => OnUi(() => LaneOf(editor)!.DragTo(x));
        void LetGo() => OnUi(() => LaneOf(editor)!.EndPress());
        bool CanUndo() => Find(editor, "StudioUndoButton", 0.5)?.IsEnabled == true;
        string Times(StudioZoom zoom) => $"{Seconds(zoom.Start)} to {Seconds(zoom.End)} s";

        // The blocks are where the lane's time scale puts them, and the shortest is 10 wide around its middle.
        var (firstLeft, firstWidth) = Block(0);
        var (shortLeft, shortWidth) = Block(2);
        _report.Check(
            "a block is as wide as its zoom is long, and a zoom too short to press is drawn 10 wide around its middle",
            Math.Abs(firstLeft - XOf(2)) <= 1 && Math.Abs(firstWidth - (3 / secondsPerPixel)) <= 1 && Math.Abs(shortWidth - 10) <= 0.7 && Math.Abs(shortLeft + (shortWidth / 2) - XOf(10.03)) <= 1,
            $"the lane is {F(laneWidth, "0.#")} wide; the first block is {F(firstWidth, "0.#")} wide at {F(firstLeft, "0.#")}, wanted {F(3 / secondsPerPixel, "0.#")} at {F(XOf(2), "0.#")}; "
                + $"the block of the zoom of 0.06 s is {F(shortWidth, "0.#")} wide with its middle at {F(shortLeft + (shortWidth / 2), "0.#")}, and its zoom's middle is at {F(XOf(10.03), "0.#")}");

        // A press that does not move.
        var middle = XOf(3.5);
        Press(middle);
        Drag(middle + 2);
        LetGo();
        var afterPress = (SelectedZoomOf(editor), Playhead(editor), CanUndo(), ZoomsOf(editor)[0]);
        _report.Check(
            "a press on a block that moves less than 3 selects the zoom and moves the playhead to the time that was pressed, and changes nothing that could be undone",
            afterPress.Item1 == 0 && Same(afterPress.Item2, 3.5, 0.01) && !afterPress.Item3 && afterPress.Item4.Start == 2 && afterPress.Item4.End == 5,
            $"selected zoom {afterPress.Item1 + 1}, playhead {Seconds(afterPress.Item2)} s, Undo enabled {afterPress.Item3}, the zoom {Times(afterPress.Item4)}");

        // A drag of the block's middle, in two steps: the zoom moves by as much as the pointer did since the press.
        Timeline.Mark("10: dragging a block");
        Press(middle);
        Drag(middle + 60);
        var half = ZoomsOf(editor)[0];
        Drag(middle + 120);
        LetGo();
        var whole = ZoomsOf(editor)[0];
        var by = 120 * secondsPerPixel;
        var (movedLeft, movedWidth) = Block(0);
        var headAfterMove = Playhead(editor);
        Invoke(editor, "StudioUndoButton");
        var undone = Until(() => ZoomsOf(editor)[0], zoom => zoom.Start == 2, 1);
        var undoLeft = Until(() => !CanUndo(), nothing => nothing, 1);
        Invoke(editor, "StudioRedoButton");
        var redone = Until(() => ZoomsOf(editor)[0], zoom => Same(zoom.Start, 2 + by), 1);
        _report.Check(
            "dragging a block by its middle moves the zoom and keeps its length; the block follows, the playhead stays where it was pressed, and the whole drag is one undo step",
            Same(half.Start, 2 + (by / 2)) && Same(half.End, 5 + (by / 2)) && Same(whole.Start, 2 + by) && Same(whole.End, 5 + by)
                && Math.Abs(movedLeft - (firstLeft + 120)) <= 1 && Math.Abs(movedWidth - firstWidth) <= 1 && Same(headAfterMove, 3.5, 0.01)
                && undone.Start == 2 && undone.End == 5 && undoLeft && Same(redone.Start, 2 + by) && SelectedZoomOf(editor) == 0,
            $"120 along the lane is {Seconds(by)} s; after 60: {Times(half)}; after 120: {Times(whole)}, the block at {F(movedLeft, "0.#")} ({F(firstLeft, "0.#")} before), playhead {Seconds(headAfterMove)} s; "
                + $"after one Undo {Times(undone)} with nothing left to undo: {undoLeft}; after Redo {Times(redone)}");

        // The first 8 of a block are the handle that moves its start, and the playhead goes with it.
        Timeline.Mark("10: dragging the ends of a block");
        var (left, width) = Block(0);
        Press(left + 3);
        Drag(left + 3 - 40);
        LetGo();
        var startMoved = ZoomsOf(editor)[0];
        var headAtStart = Playhead(editor);
        var back = 40 * secondsPerPixel;

        // The last 8 move its end.
        (left, width) = Block(0);
        Press(left + width - 3);
        Drag(left + width - 3 + 50);
        LetGo();
        var endMoved = ZoomsOf(editor)[0];
        var headAtEnd = Playhead(editor);
        var on = 50 * secondsPerPixel;
        _report.Check(
            "dragging a block of 28 or more by the handle at its start, taken 3 in, moves the zoom's start, and by the one at its end its end; the other end stays, and the playhead follows the end that is dragged",
            Same(startMoved.Start, whole.Start - back) && startMoved.End == whole.End && Same(headAtStart, startMoved.Start)
                && endMoved.Start == startMoved.Start && Same(endMoved.End, whole.End + on) && Same(headAtEnd, endMoved.End),
            $"the start dragged 40 back: {Times(startMoved)} (wanted the start at {Seconds(whole.Start - back)} s), playhead {Seconds(headAtStart)} s; the end dragged 50 on: {Times(endMoved)} (wanted the end at {Seconds(whole.End + on)} s), playhead {Seconds(headAtEnd)} s");

        // Dragged into the next zoom, a zoom stops against it: its end is exactly that zoom's start.
        (left, width) = Block(0);
        Press(left + (width / 2));
        Drag(left + (width / 2) + 600);
        LetGo();
        var against = ZoomsOf(editor);
        _report.Check(
            "a block dragged past the next zoom stops against it, with the length it had: its end is exactly the next zoom's start",
            against[0].End == against[1].Start && against[1].Start == 7 && Same(against[0].End - against[0].Start, endMoved.End - endMoved.Start, 1e-9),
            $"{Times(against[0])}, and the next zoom is {Times(against[1])}");

        // The shortest zoom is too narrow to have handles inside its ends, and it is not selected: any press on it moves the whole zoom.
        (left, width) = Block(2);
        Press(left + 1);
        Drag(left + 1 + 30);
        LetGo();
        var shortMoved = ZoomsOf(editor)[2];
        _report.Check(
            "a block narrower than 28 that is not the selected one is moved as a whole, also when it is pressed at its edge",
            Same(shortMoved.Start, 10 + (30 * secondsPerPixel)) && Same(shortMoved.End - shortMoved.Start, 0.06, 1e-9) && SelectedZoomOf(editor) == 2,
            $"pressed 1 from its left edge and dragged 30: {Times(shortMoved)}, wanted the start at {Seconds(10 + (30 * secondsPerPixel))} s");

        // The empty lane: nothing is selected, and the playhead follows the pointer.
        Timeline.Mark("10: pressing the empty lane");
        Press(XOf(0.5));
        var afterEmpty = (SelectedZoomOf(editor), Playhead(editor));
        Drag(XOf(1.0));
        var afterScrub = Playhead(editor);
        Drag(XOf(-3));
        var beforeStart = Playhead(editor);
        LetGo();
        var laneAfter = LaneText(editor);
        _report.Check(
            "a press on the empty lane selects nothing and moves the playhead there, and a drag from it moves the playhead along, no further than the start of the recording",
            afterEmpty.Item1 is null && Same(afterEmpty.Item2, 0.5, 0.01) && Same(afterScrub, 1.0, 0.01) && beforeStart == 0 && !laneAfter.Contains('*', StringComparison.Ordinal),
            $"pressed at 0.5 s: {(afterEmpty.Item1 is null ? "none selected" : "a zoom is selected")}, playhead {Seconds(afterEmpty.Item2)} s; dragged to 1.0 s: {Seconds(afterScrub)} s; dragged past the left end: {Seconds(beforeStart)} s");

        // Five drags changed a zoom, and nothing else did: five Undo bring the project back to how
        // it was opened, and leave nothing to undo.
        for (var undo = 0; undo < 5; undo++)
        {
            var before = string.Join(", ", ZoomsOf(editor).Select(Times));
            Invoke(editor, "StudioUndoButton");
            Until(() => string.Join(", ", ZoomsOf(editor).Select(Times)), now => now != before, 1);
        }

        var restored = ZoomsOf(editor);
        var nothingLeft = Until(() => !CanUndo(), nothing => nothing, 1);
        _report.Check(
            "each drag that changed a zoom is one undo step, and a press or a drag of the playhead is none: five Undo bring back the three zooms as they were opened and leave nothing to undo",
            nothingLeft && restored.Length == 3 && restored.Zip(zooms, (now, then) => now.Start == then.Start && now.End == then.End).All(same => same),
            $"after five Undo the zooms are {string.Join(", ", restored.Select(Times))}, and nothing is left to undo: {nothingLeft}");

        // The handles, on the zoom from 7 to 9 s and on the short one.
        LaneHandles(
            editor,
            new LaneWithHandles("10", "zoom", "when", ZoomLane, "StudioZoom_", () => [.. ZoomsOf(editor).Select(zoom => (zoom.Start, zoom.End))], () => SelectedZoomOf(editor)),
            wide: 1,
            narrow: 2);
        CloseQuietly(editor);
    }

    // ---------------------------------------------------------------------------------------
    // A zoom that follows the pointer
    // ---------------------------------------------------------------------------------------

    private void ZoomFollowsThePointer()
    {
        Timeline.Mark("10: a zoom that follows the pointer");

        // One zoom from 4 s to 9 s that looks at the middle of the screen, in a recording whose
        // pointer rests at (0.30, 0.28) and from 6 s on at (0.45, 0.60).
        var folder = NewEventsProject("Zoom that follows the pointer", PointerSamples(), clicks: [], p => p with { Zooms = [PointZoom(4, 9, 0.5, 0.5)] });
        if (OpenReady(folder, "zoom and pointer") is not { } editor)
        {
            return;
        }

        const string PointName = "Zoom 2×, 4.0 to 9.0 seconds";
        const string PointerName = PointName + ", follows the pointer";
        Find(editor, "StudioZoom_0")?.Invoke();
        WaitForLane(editor, "*" + PointName);
        var pointer = Find(editor, "StudioZoomFocusPointer");
        var middle = new ScreenPart(0.25, 0.25, 0.5, 0.5);

        // This part has no frame strip in it. Its level edges are the top and the bottom of the
        // gray patch, so as far as up and down goes this check places the picture, and cannot tell
        // a picture stretched down the screen about the patch by less than about one part in 50.
        // It is also the part a preview would show that ignored where a zoom looks: this check
        // could not tell that preview from the right one. The three after it, which follow the
        // pointer away from the middle, can.
        SetSlider(editor, "StudioPlayhead", MiddleOf(150));
        ShowsPart(editor, "a zoom that looks at the middle of the screen shows its middle half", middle, 150, "scale 2 around (0.5, 0.5) is the part from 0.25 across and 0.25 down");

        var chosen = pointer?.Select() ?? false;
        var lane = WaitForLane(editor, "*" + PointerName);
        var zoom = ZoomsOf(editor)[0];
        _report.Check(
            "Follow pointer can be chosen in a recording that has pointer positions: the zoom then follows the pointer, the lane says so, the point it looked at is kept, and the focus pad and its two sliders go",
            pointer is { IsEnabled: true, Name: "Follow pointer" } && chosen && zoom.Focus.Mode == StudioZoomFocusMode.Cursor && zoom.Focus.X == 0.5 && zoom.Focus.Y == 0.5 && lane == "*" + PointerName
                && Selected(editor, "StudioZoomFocusPointer") is null && Gone(editor, "StudioZoomFocusXSlider", 1) && Gone(editor, "StudioZoomFocusYSlider", 1)
                && !PadShows(editor) && Gone(editor, "StudioNoPointerNote", 0.5),
            $"the zoom: {Describe(zoom)}; the lane: {lane}; Follow pointer selected: {Find(editor, "StudioZoomFocusPointer", 0)?.IsSelected}");

        // The pointer's mean place over the second around a frame's middle. A preview that left
        // the pointer out would show the middle half in all three. One that drew a line from the
        // first sample to the second, where the format has a step, would be at (0.425, 0.547) in
        // the first of them, a fifth of the screen from where the pointer is.
        ShowsPart(editor, "while the pointer rests at its first place, the zoom shows the part around it", HeldAtFirst, 150, "the second around 5.017 s is all spent at (0.30, 0.28): the part from 0.05 across and 0.03 down");

        // The middle of frame 180 is 6.0167 s. Of the second around it, the 0.5167 s from 6 s on are spent at the second place.
        var share = MiddleOf(180) + 0.5 - PointerMovesAt;
        SetSlider(editor, "StudioPlayhead", MiddleOf(180));
        var between = new ScreenPart(HeldAtFirst.X + ((HeldAtSecond.X - HeldAtFirst.X) * share), HeldAtFirst.Y + ((HeldAtSecond.Y - HeldAtFirst.Y) * share), 0.5, 0.5);
        ShowsPart(
            editor,
            "as the pointer moves on, the zoom looks at its mean place over the second around the frame",
            between,
            180,
            string.Create(CultureInfo.InvariantCulture, $"{share:0.0000} of the second around 6.017 s is spent at the second place: the part starts {between.X:0.0000} across and {between.Y:0.0000} down"));

        SetSlider(editor, "StudioPlayhead", MiddleOf(210));
        ShowsPart(editor, "once the pointer rests at its second place, the zoom shows the part around that", HeldAtSecond, 210, "the second around 7.017 s is all spent at (0.45, 0.60): the part from 0.20 across and 0.35 down");

        // And back to a point: the one it had.
        var back = Find(editor, "StudioZoomFocusPoint")?.Select() ?? false;
        var laneBack = WaitForLane(editor, "*" + PointName);
        _report.Check(
            "Fixed point chosen again: the zoom looks at the point it kept, and the focus pad and its sliders are back",
            back && laneBack == "*" + PointName && ZoomsOf(editor)[0].Focus is { Mode: StudioZoomFocusMode.Point, X: 0.5, Y: 0.5 }
                && Slider(editor, "StudioZoomFocusXSlider", 0.5, "50%") is null && PadShows(editor),
            $"the zoom: {Describe(ZoomsOf(editor)[0])}; the lane: {laneBack}; the focus pad shown: {PadShows(editor)}");
        ShowsPart(editor, "looking at its point again, the zoom shows the middle half where the pointer is elsewhere", middle, 210, "scale 2 around (0.5, 0.5) is the part from 0.25 across and 0.25 down");
        CloseQuietly(editor);
    }

    // ---------------------------------------------------------------------------------------
    // Suggested zooms
    // ---------------------------------------------------------------------------------------

    private void ZoomSuggestions()
    {
        Timeline.Mark("10: suggested zooms");

        // Four clicks, and no pointer positions. By section 8 of the project format:
        //   2.0 s at (0.20, 0.30)   starts a zoom 0.6 s before it, at 1.4 s.
        //   2.8 s at (0.22, 0.31)   is inside the middle 70% of what that zoom holds: the zoom stays.
        //   4.0 s at (0.80, 0.70)   is elsewhere, 1.2 s later: the first zoom ends at max(4.0 − 0.6, (2.8 + 4.0) / 2) = 3.4 s,
        //                           and a second one starts there, chained to it. It ends 1.5 s after its last click, at 5.5 s.
        //   10.5 s at (0.50, 0.50)  is more than 4 s later: a third zoom from 9.9 s to the end of the recording at 12 s.
        StudioClickEvent[] clicks =
        [
            new StudioClickEvent { T = 2.0, X = 0.20, Y = 0.30 },
            new StudioClickEvent { T = 2.8, X = 0.22, Y = 0.31 },
            new StudioClickEvent { T = 4.0, X = 0.80, Y = 0.70 },
            new StudioClickEvent { T = 10.5, X = 0.50, Y = 0.50 },
        ];
        const string Own = "Zoom 2×, 6.0 to 8.0 seconds";
        const string FirstSuggestion = "Zoom 2×, 1.4 to 3.4 seconds, suggested";
        const string SecondSuggestion = "Zoom 2×, 3.4 to 5.5 seconds, suggested";
        const string ThirdSuggestion = "Zoom 2×, 9.9 to 12.0 seconds, suggested";
        var folder = NewEventsProject("Suggested zooms", cursor: [], clicks, p => p with { Zooms = [PointZoom(6, 8, 0.5, 0.5)] });
        if (OpenReady(folder, "suggested zooms") is not { } editor)
        {
            return;
        }

        using var heard = UiaEvents.Listen(_uia, editor.Root);
        Find(editor, "StudioZoom_0")?.Invoke();
        WaitForLane(editor, "*" + Own);

        // Without pointer positions there is nothing for a zoom to follow.
        var pointer = Find(editor, "StudioZoomFocusPointer");
        var note = NameOf(editor, "StudioNoPointerNote", 1);
        _report.Check(
            "in a recording without pointer positions Follow pointer cannot be chosen, and the reason is next to it and is the choice's description",
            pointer is { IsEnabled: false, Name: "Follow pointer" } && pointer.HelpText == StudioEditorText.NoPointerExplanation && note == StudioEditorText.NoPointerExplanation
                && Find(editor, "StudioZoomFocusPoint") is { IsEnabled: true, IsSelected: true },
            $"Follow pointer enabled {pointer?.IsEnabled}, described as \"{pointer?.HelpText}\"; the note: \"{note}\"");

        // Suggest zooms.
        var suggest = Find(editor, "StudioSuggestZoomsButton");
        var canSuggest = suggest is { IsEnabled: true, HelpText: "Add zooms where you clicked" } && Gone(editor, "StudioNoClicksNote", 0.5) && Gone(editor, "StudioRemoveSuggestionsButton", 0.5);
        var mark = heard.Mark();
        var pressed = suggest?.Invoke() ?? false;
        var all = $"{FirstSuggestion} | {SecondSuggestion} | *{Own} | {ThirdSuggestion}";
        var lane = WaitForLane(editor, all);
        var said = Said(heard, mark, "3 zooms suggested.");
        var zooms = ZoomsOf(editor);
        bool Is(StudioZoom zoom, double start, double end, double x, double y) =>
            Same(zoom.Start, start, 1e-9) && Same(zoom.End, end, 1e-9) && zoom.Scale == 2 && zoom.Focus.Mode == StudioZoomFocusMode.Point && zoom.Focus.X == x && zoom.Focus.Y == y
            && zoom.EaseIn == 0.5 && zoom.EaseOut == 0.5 && zoom.Origin == StudioZoomOrigin.Auto;
        _report.Check(
            "Suggest zooms adds the zooms worked out from the clicks, marked as suggestions, keeps the zoom that was there and its selection, tells a screen reader how many there are, and offers Remove suggestions",
            canSuggest && pressed && lane == all && said.Said && zooms.Length == 4
                && Is(zooms[0], 1.4, 3.4, 0.20, 0.30) && Is(zooms[1], 3.4, 5.5, 0.80, 0.70) && zooms[0].End == zooms[1].Start && Is(zooms[3], 9.9, 12, 0.50, 0.50)
                && Find(editor, "StudioRemoveSuggestionsButton") is { IsEnabled: true, Name: "Remove suggestions" } && NameOf(editor, "StudioZoomPositionText") == "Zoom 3 of 4",
            $"Suggest zooms could be pressed: {canSuggest}; the lane: {lane}; sent: {said.Heard}; the editor holds {string.Join(", ", zooms.Select(Describe))}");

        // One undo step for all of them.
        Invoke(editor, "StudioUndoButton");
        var undone = WaitForLane(editor, "*" + Own);
        Invoke(editor, "StudioRedoButton");
        var redone = WaitForLane(editor, all);
        _report.Check("the suggestions are one undo step: one Undo takes all three away, and one Redo brings them back", undone == "*" + Own && redone == all, $"after Undo: {undone}; after Redo: {redone}");

        // A suggestion that is changed is the user's own from then on.
        Timeline.Mark("10: a changed suggestion, and removing the rest");
        Find(editor, "StudioZoom_0")?.Invoke();
        WaitForLane(editor, $"*{FirstSuggestion} | {SecondSuggestion} | {Own} | {ThirdSuggestion}");
        var range = NameOf(editor, "StudioZoomRangeText");
        SetSlider(editor, "StudioZoomScaleSlider", 3);
        const string Changed = "Zoom 3×, 1.4 to 3.4 seconds";
        var changed = WaitForLane(editor, $"*{Changed} | {SecondSuggestion} | {Own} | {ThirdSuggestion}");
        _report.Check(
            "a suggestion says that it is one, on the lane and in the Zoom panel, until it is changed",
            range == "1.4 to 3.4 seconds, suggested" && changed == $"*{Changed} | {SecondSuggestion} | {Own} | {ThirdSuggestion}" && ZoomsOf(editor)[0].Origin == StudioZoomOrigin.Manual && NameOf(editor, "StudioZoomRangeText") == "1.4 to 3.4 seconds",
            $"before: \"{range}\"; after its scale was set to 3: {changed}, \"{NameOf(editor, "StudioZoomRangeText")}\"");

        mark = heard.Mark();
        var focusBefore = FocusOn(editor, "StudioRemoveSuggestionsButton");
        var removePressed = Invoke(editor, "StudioRemoveSuggestionsButton");
        var removed = WaitForLane(editor, $"*{Changed} | {Own}");
        var removedSaid = Said(heard, mark, StudioEditorText.ZoomSuggestionsRemovedMessage);
        _report.Check(
            "Remove suggestions takes away the suggestions that were not changed, keeps the others, tells a screen reader, and is no longer offered",
            removePressed && removed == $"*{Changed} | {Own}" && removedSaid.Said && Gone(editor, "StudioRemoveSuggestionsButton", 1),
            $"the lane: {removed}; sent: {removedSaid.Heard}");
        var focusAfter = Until(() => FocusedId(editor), id => id == "StudioSuggestZoomsButton", 1.5);
        _report.Check(
            "after Remove suggestions, which goes away with the suggestions, the keyboard focus is on Suggest zooms, next to where it was",
            focusBefore == "StudioRemoveSuggestionsButton" && focusAfter == "StudioSuggestZoomsButton",
            $"the focus was on \"{focusBefore}\" and is on \"{focusAfter}\"");

        // Suggested again: the suggestion that would lie over the user's own zoom is left out.
        mark = heard.Mark();
        Invoke(editor, "StudioSuggestZoomsButton");
        var again = WaitForLane(editor, $"*{Changed} | {SecondSuggestion} | {Own} | {ThirdSuggestion}");
        var againSaid = Said(heard, mark, "2 zooms suggested.");
        _report.Check(
            "suggested again, a zoom the user made or changed wins: the suggestion that would lie over it is left out, and a screen reader is told that there are two",
            again == $"*{Changed} | {SecondSuggestion} | {Own} | {ThirdSuggestion}" && againSaid.Said,
            $"the lane: {again}; sent: {againSaid.Heard}");
        AuditState(editor, "the editor with suggested zooms", "tree-suggestions.txt", 15);
        CloseQuietly(editor);
    }
}
