using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 4. Trimming: the two handles of the trim bar, and trimming at the playhead.
internal sealed partial class WindowChecks
{
    private void Trimming()
    {
        Timeline.Mark("4: trimming");
        if (OpenReady(NewCameraProject("Trimming"), "trimming") is not { } editor)
        {
            return;
        }

        double duration = TestMedia.Screen.Seconds;
        var frame = 1.0 / Fps;
        var step = Math.Max(frame, Math.Min(1, duration / 100));
        var trimStart = CameraOffset;
        var trimEnd = duration;
        string TimeFor(double playhead) => StudioEditorText.GetTimeText(playhead - trimStart, trimEnd - trimStart);
        double Start() => SliderValue(editor, "StudioTrimStart");
        double End() => SliderValue(editor, "StudioTrimEnd");
        bool Is(double value, double wanted) => Math.Abs(value - wanted) < 1e-6;
        LookFor(editor, s => s.Shown == Both(editor, FrameOf(trimStart)), 5);

        // What the bar tells a screen reader before anything is moved.
        var bar = Find(editor, "StudioTrimBar");
        var startThumb = Find(editor, "StudioTrimStart");
        var endThumb = Find(editor, "StudioTrimEnd");
        var headThumb = Find(editor, "StudioPlayhead");
        string Told(UiaElement? thumb) => thumb?.Range is { } r
            ? $"{thumb.ControlTypeName} \"{thumb.Name}\" {F(r.Value)} in {F(r.Minimum)} to {F(r.Maximum)}, step {F(r.SmallChange, "0.####")}, \"{thumb.ValueText}\""
            : "missing";
        bool Tells(UiaElement? thumb, string name, double value, double small, string text) =>
            thumb is { ControlType: ControlTypeNames.Slider, Range: { } r } && thumb.Name == name && Is(r.Value, value) && Is(r.Minimum, 0) && Is(r.Maximum, duration) && Is(r.SmallChange, small) && !r.IsReadOnly && thumb.ValueText == text;
        _report.Check(
            "the trim bar is a group of three sliders, Start, End and Playhead, each with its time in seconds, the length of the recording as its range, and its step",
            bar is { ControlType: ControlTypeNames.Group, Name: "Trim bar" }
                && Tells(startThumb, "Start", trimStart, step, "0.2 seconds")
                && Tells(endThumb, "End", duration, step, "12.0 seconds")
                && Tells(headThumb, "Playhead", trimStart, frame, "0:00.0 of 0:11.8"),
            $"{bar}: {Told(startThumb)}; {Told(endThumb)}; {Told(headThumb)}");

        // Start, to a time on a frame boundary.
        Timeline.Mark("4: the Start handle");
        var startSet = SetSlider(editor, "StudioTrimStart", 2.0);
        trimStart = Start();
        Expect(editor, p => p with { Edits = p.Edits with { TrimStart = trimStart } });
        var atStart = LookFor(editor, s => s.Shown == Both(editor, 60), 3);
        _report.Check(
            "Start set to 2.0 s: the handle reports it, the playhead goes there, the picture shows the frame the video now starts on, and the time reads the new length",
            startSet && Is(trimStart, 2.0) && Find(editor, "StudioTrimStart")?.ValueText == "2.0 seconds" && Is(Playhead(editor), 2.0) && atStart?.Shown == Both(editor, 60) && TimeText(editor) == "0:00.0 / 0:10.0",
            $"Start {F(trimStart)} s \"{Find(editor, "StudioTrimStart")?.ValueText}\"; playhead {F(Playhead(editor))} s; {atStart?.Shown} (expected {Both(editor, 60)}); time \"{TimeText(editor)}\"");

        // End, to the middle of a frame. Such a time is kept to a millisecond.
        Timeline.Mark("4: the End handle");
        var endSet = SetSlider(editor, "StudioTrimEnd", MiddleOf(105));
        trimEnd = End();
        Expect(editor, p => p with { Edits = p.Edits with { TrimEnd = Math.Round(trimEnd, 3) } });
        var atEnd = LookFor(editor, s => s.Shown == Both(editor, 105), 3);
        _report.Check(
            "End set to the middle of frame 105: the handle reports that time to a millisecond, the playhead goes there, the picture shows that frame, and the time reads the new length",
            endSet && Is(trimEnd, 3.517) && Find(editor, "StudioTrimEnd")?.ValueText == "3.5 seconds" && Is(Playhead(editor), 3.517) && atEnd?.Shown == Both(editor, 105) && TimeText(editor) == TimeFor(trimEnd) && TimeText(editor) == "0:01.5 / 0:01.5",
            $"End {F(trimEnd, "0.######")} s \"{Find(editor, "StudioTrimEnd")?.ValueText}\"; playhead {F(Playhead(editor), "0.######")} s; {atEnd?.Shown} (expected {Both(editor, 105)}); time \"{TimeText(editor)}\"");

        // One step, the way a screen reader takes it: the value it read plus the step it read.
        var stepSet = SetSlider(editor, "StudioTrimEnd", trimEnd + (Find(editor, "StudioTrimEnd")?.Range?.SmallChange ?? 0));
        var stepped = End();
        _report.Check(
            "End raised by one step, as a screen reader raises it: exactly one step on",
            stepSet && Is(stepped, 3.637) && Math.Abs(OnUi(() => editor.Window.ViewModel.TrimEnd) - (3.517 + step)) < 1e-12,
            $"End {F(stepped, "0.######")} s; the editor holds {F(OnUi(() => editor.Window.ViewModel.TrimEnd), "0.#########")} s, and one step is {F(step)} s");
        trimEnd = OnUi(() => editor.Window.ViewModel.TrimEnd);
        Expect(editor, p => p with { Edits = p.Edits with { TrimEnd = trimEnd } });

        // Start cannot pass End: it stops a tenth of a second before it.
        var pastSet = SetSlider(editor, "StudioTrimStart", 11.0);
        var stopped = Start();
        _report.Check(
            "Start asked to go past End stops a tenth of a second before it, and End stays",
            pastSet && Is(stopped, trimEnd - 0.1) && Is(End(), trimEnd),
            $"Start {F(stopped, "0.######")} s, End {F(End(), "0.######")} s");

        // Undo and Redo, by the buttons: three steps back, and forward again.
        Timeline.Mark("4: undo and redo of the trim");
        Invoke(editor, "StudioUndoButton");
        var undo1 = (Until(Start, value => Is(value, 2.0), 2), End());
        Invoke(editor, "StudioUndoButton");
        var undo2 = (Start(), Until(End, value => Is(value, 3.517), 2));
        Invoke(editor, "StudioUndoButton");
        var undo3 = (Start(), Until(End, value => Is(value, duration), 2));
        var lengthAfterUndo = TimeText(editor);
        Invoke(editor, "StudioRedoButton");
        Invoke(editor, "StudioRedoButton");
        var redo = (Start(), Until(End, value => Is(value, 3.637), 2));
        _report.Check(
            "Undo takes the trims back one at a time, the time reads the length again, and Redo brings them back",
            Is(undo1.Item1, 2.0) && Is(undo1.Item2, 3.637) && Is(undo2.Item1, 2.0) && Is(undo2.Item2, 3.517) && Is(undo3.Item1, 2.0) && Is(undo3.Item2, duration)
                && lengthAfterUndo.EndsWith("/ 0:10.0", StringComparison.Ordinal) && Is(redo.Item1, 2.0) && Is(redo.Item2, 3.637),
            $"Start and End after each Undo: {F(undo1.Item1)} and {F(undo1.Item2)}, {F(undo2.Item1)} and {F(undo2.Item2)}, {F(undo3.Item1)} and {F(undo3.Item2)} (time \"{lengthAfterUndo}\"); after two Redo: {F(redo.Item1)} and {F(redo.Item2)}");
        trimStart = 2.0;

        TrimAtPlayhead(editor, ref trimStart, ref trimEnd);
        ThumbsAreWhereTheTimesAre(editor, duration);

        // What was saved, once the editor has saved by itself.
        var wanted = Describe(editor.Expected);
        var saved = Until(() => Describe(_services.Store.Load(editor.Id)), text => text == wanted, 3, 100);
        _report.Check(
            "the editor saves the trim by itself: the project file holds the start and the end the handles report",
            saved == wanted,
            saved == wanted ? saved[..saved.IndexOf(';')] : $"saved: {saved[..saved.IndexOf(';')]} | expected: {wanted[..wanted.IndexOf(';')]}");
        CloseQuietly(editor);
    }

    /// <summary>Start here and End here, by the buttons and by what the keys I and O run. The playhead stays where it is.</summary>
    private void TrimAtPlayhead(Editor editor, ref double trimStart, ref double trimEnd)
    {
        Timeline.Mark("4: trimming at the playhead");
        var wrong = new List<string>();
        (string What, int Frame, bool IsStart, Func<bool> Do)[] trims =
        [
            ("the Start here button", 75, true, () => Invoke(editor, "StudioStartHereButton")),
            ("the End here button", 95, false, () => Invoke(editor, "StudioEndHereButton")),
            ("what the I key runs", 80, true, () => Key(editor, StudioShortcutKey.I) == StudioShortcutAction.SetTrimStartAtPlayhead),
            ("what the O key runs", 90, false, () => Key(editor, StudioShortcutKey.O) == StudioShortcutAction.SetTrimEndAtPlayhead),
        ];
        foreach (var (what, frame, isStart, act) in trims)
        {
            SetSlider(editor, "StudioPlayhead", MiddleOf(frame));
            var playhead = Until(() => Playhead(editor), value => FrameOf(value) == frame, 2);
            var done = act();
            var handle = isStart ? "StudioTrimStart" : "StudioTrimEnd";
            var value = Until(() => SliderValue(editor, handle), v => Math.Abs(v - playhead) < 1e-6, 2);
            if (isStart)
            {
                trimStart = value;
            }
            else
            {
                trimEnd = value;
            }

            var (start, end) = (trimStart, trimEnd);
            Expect(editor, p => p with { Edits = p.Edits with { TrimStart = start, TrimEnd = end } });
            var sight = LookFor(editor, s => s.Shown == Both(editor, frame), 3);
            var time = TimeText(editor);
            var expected = StudioEditorText.GetTimeText(playhead - start, end - start);
            if (!done || Math.Abs(value - playhead) >= 1e-6 || Math.Abs(Playhead(editor) - playhead) >= 1e-6 || sight?.Shown != Both(editor, frame) || time != expected)
            {
                wrong.Add($"{what} with the playhead at {F(playhead)} s: done {done}, the handle is at {F(value)} s, the playhead at {F(Playhead(editor))} s, the picture shows {sight?.Shown}, the time reads \"{time}\" (expected \"{expected}\")");
            }
        }

        _report.Check(
            "Start here and End here, by the buttons and by what the keys I and O run, put the handle on the playhead: the playhead and the picture stay, and the time reads the new start and length",
            wrong.Count == 0,
            wrong.Count == 0 ? $"the video now runs from {F(trimStart)} s to {F(trimEnd)} s" : string.Join("; ", wrong));
    }

    /// <summary>The handles and the playhead are drawn where their times are along the bar, and the part between the handles is tinted.</summary>
    private void ThumbsAreWhereTheTimesAre(Editor editor, double duration)
    {
        Timeline.Mark("4: where the handles are drawn");
        if (Find(editor, "StudioTrimBar") is not { } bar || Find(editor, "StudioTrimStart") is not { } start || Find(editor, "StudioTrimEnd") is not { } end || Find(editor, "StudioPlayhead") is not { } head
            || Look(editor) is not { } sight)
        {
            _report.Check("the handles are drawn where their times are", false, "the trim bar was not found");
            return;
        }

        // A handle is 12 effective pixels wide, and the times run over the bar less both handles.
        var handle = 12 * editor.Scale;
        var usable = bar.Bounds.Width - (2 * handle);
        double At(double time) => bar.Bounds.X + (time / duration * usable);
        var startApart = Math.Abs(start.Bounds.X - At(start.Range?.Value ?? 0));
        var endApart = Math.Abs(end.Bounds.X - (At(end.Range?.Value ?? 0) + handle));
        var headApart = Math.Abs(head.Bounds.X + (head.Bounds.Width / 2.0) - (At(head.Range?.Value ?? 0) + handle));

        // In the screenshot: the middle of the bar's height, inside each handle, between them, and outside them.
        var y = bar.Bounds.Y + (bar.Bounds.Height / 2.0) - sight.Shot.ScreenY;
        Rgb Pixel(double screenX) => sight.Shot.Color(screenX - sight.Shot.ScreenX, y - (bar.Bounds.Height / 4.0), 0);
        var startColor = Pixel(start.Bounds.X + (handle / 2) + (handle / 4));
        var endColor = Pixel(end.Bounds.X + (handle / 2) + (handle / 4));
        var kept = Pixel((start.Bounds.X + handle + end.Bounds.X) / 2);
        var before = Pixel(start.Bounds.X - (4 * handle));
        var after = Pixel(end.Bounds.X + (5 * handle));
        _report.Check(
            "the Start and End handles and the playhead are drawn where their times are along the bar; the handles have one colour, and the part between them is tinted where the rest of the bar is not",
            startApart <= 2 && endApart <= 2 && headApart <= 2 && Near(startColor, endColor, 6) && startColor.Distance(before) > 30 && kept.Distance(before) > 12 && Near(before, after, 6),
            $"Start {F(startApart, "0.#")} px, End {F(endApart, "0.#")} px and the playhead {F(headApart, "0.#")} px from where their times are; handles {startColor} and {endColor}, between them {kept}, before Start {before}, after End {after}");
    }
}
