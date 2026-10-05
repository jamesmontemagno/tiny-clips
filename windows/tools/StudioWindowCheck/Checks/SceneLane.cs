using System.Globalization;
using Microsoft.UI.Xaml;
using TinyClips.App.Controls.Studio;
using TinyClips.Core.Models;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Capture;
using TinyClips.Tools.StudioWindowCheck.Host;
using Windows.System;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 12, continued. The scene lane: its keys, its items as a screen reader uses them, what a press
// and a drag on it do, and Delete while it has the keyboard focus.
internal sealed partial class WindowChecks
{
    /// <summary>What the scene lane does with the arrow keys, Home and End while it has the focus.</summary>
    private void SceneLaneKeys(Editor editor, UiaEvents heard)
    {
        Timeline.Mark("12: the scene lane's keys");
        SetSlider(editor, "StudioPlayhead", 6.0);
        WaitForLane(editor, SceneLaneWanted(editor, 1), 2, SceneLane);

        // Where each of the three scenes has been entered: the first at its start, the second at
        // the end of its move, and the third, which is cut to, at its start.
        double[] entered = [0, 4.35, 8.0];
        var steps = new List<string>();
        var wrong = new List<string>();
        var mark = heard.Mark();
        void Press(VirtualKey key, int wanted)
        {
            var handled = LaneKey(editor, key, SceneLane);
            var current = Until(() => CurrentSceneOf(editor), index => index == wanted, 1);
            var head = Until(() => Playhead(editor), value => Same(value, entered[wanted]), 1);
            var marked = LaneItems(editor, SceneLane).Select((item, at) => item.IsSelected == true ? at : -1).Where(at => at >= 0).ToArray();
            steps.Add($"{key}: scene {current + 1} at {Seconds(head)} s");
            if (!handled || current != wanted || !Same(head, entered[wanted]) || !marked.SequenceEqual([wanted]))
            {
                wrong.Add($"{key}: handled {handled}, scene {current + 1} (the lane marks {string.Join(",", marked)}), playhead {Seconds(head)} s; wanted scene {wanted + 1} at {Seconds(entered[wanted])} s");
            }
        }

        // The playhead is in the second scene. At either end the key is the lane's and changes nothing.
        Press(VirtualKey.End, 2);
        Press(VirtualKey.Left, 1);
        Press(VirtualKey.Left, 0);
        Press(VirtualKey.Left, 0);
        Press(VirtualKey.Right, 1);
        Press(VirtualKey.Home, 0);
        Press(VirtualKey.End, 2);
        Press(VirtualKey.Right, 2);
        var others = new[] { VirtualKey.Delete, VirtualKey.Space, VirtualKey.S, VirtualKey.X, VirtualKey.Z, VirtualKey.Up, VirtualKey.Down }.Where(key => LaneKey(editor, key, SceneLane)).ToArray();
        var selectedEvents = heard.WaitFor(mark, "selected", e => false, 0.4).Where(e => e.Id.StartsWith("StudioScene_", StringComparison.Ordinal)).ToArray();
        _report.Check(
            "with the focus on the scene lane, Home and End go to the first and the last scene and Left and Right to the one before and after, each shown where it has been entered; at either end, and for any other key, the lane does nothing",
            wrong.Count == 0 && others.Length == 0 && ScenesWhenAs(editor) == Describe(editor.Expected.Scenes),
            wrong.Count == 0 ? $"{string.Join("; ", steps)}; keys the lane leaves to the window: Delete, Space, S, X, Z, Up, Down{(others.Length == 0 ? string.Empty : "; but it took " + string.Join(", ", others))}" : string.Join("; ", wrong));

        // The keys came into another scene six times: the third, second, first, second, first and third.
        string[] changes = ["StudioScene_2", "StudioScene_1", "StudioScene_0", "StudioScene_1", "StudioScene_0", "StudioScene_2"];
        var each = selectedEvents.Length / changes.Length;
        _report.Check(
            "a screen reader is told each time another scene becomes the one the playhead is in, as the selected item of the list, by its name",
            each is 1 or 2 && selectedEvents.Select(e => e.Id).SequenceEqual(changes.SelectMany(id => Enumerable.Repeat(id, each))) && selectedEvents.All(e => e.Text.StartsWith("Scene ", StringComparison.Ordinal)),
            $"{selectedEvents.Length} events for the 6 changes the keys made: {string.Join(", ", selectedEvents.Select(e => e.Id.Replace("StudioScene_", "scene ", StringComparison.Ordinal)))}; the first is named \"{(selectedEvents.Length > 0 ? selectedEvents[0].Text : string.Empty)}\"{(heard.Problem is null ? string.Empty : $" ({heard.Problem})")}");
    }

    /// <summary>A scene's block as a list item: selecting it and pressing it both go to the scene, and the scene the playhead is in cannot be taken out of the selection.</summary>
    private void SceneLaneItems(Editor editor)
    {
        Timeline.Mark("12: the scene lane's items, through UI Automation");
        var items = LaneItems(editor, SceneLane);
        var patterns = items.Select(item => item.Patterns).Distinct().ToArray();
        var ids = items.Select(item => item.Id).ToArray();
        SetSlider(editor, "StudioPlayhead", 6.0);
        WaitForLane(editor, SceneLaneWanted(editor, 1), 2, SceneLane);

        var selected = Find(editor, "StudioScene_2")?.Select() ?? false;
        var afterSelect = (Until(() => CurrentSceneOf(editor), index => index == 2, 1), Until(() => Playhead(editor), value => Same(value, 8.0), 1));
        var invoked = Find(editor, "StudioScene_0")?.Invoke() ?? false;
        var afterInvoke = (Until(() => CurrentSceneOf(editor), index => index == 0, 1), Until(() => Playhead(editor), value => Same(value, 0), 1));

        // One scene is always the one the playhead is in, so the selected item says no to being taken out.
        var removed = Find(editor, "StudioScene_0")?.RemoveFromSelection() ?? false;
        Thread.Sleep(150);
        var afterRemove = (CurrentSceneOf(editor), LaneText(editor, SceneLane));
        var again = Find(editor, "StudioScene_1")?.Select() ?? false;
        var afterAgain = (Until(() => CurrentSceneOf(editor), index => index == 1, 1), Until(() => Playhead(editor), value => Same(value, 4.35), 1));
        _report.Check(
            "each scene's block is a list item with an automation id that can be selected and pressed, and both go to the scene, where it has been entered; the scene the playhead is in cannot be taken out of the selection",
            items.Count == 3 && patterns.Length == 1 && patterns[0] == "Invoke,SelectionItem" && ids.SequenceEqual(["StudioScene_0", "StudioScene_1", "StudioScene_2"]) && items.All(item => !item.IsKeyboardFocusable && item.Children().Count == 0)
                && selected && afterSelect.Item1 == 2 && Same(afterSelect.Item2, 8.0)
                && invoked && afterInvoke.Item1 == 0 && Same(afterInvoke.Item2, 0)
                && !removed && afterRemove.Item1 == 0 && afterRemove.Item2 == SceneLaneWanted(editor, 0)
                && again && afterAgain.Item1 == 1 && Same(afterAgain.Item2, 4.35) && ScenesWhenAs(editor) == Describe(editor.Expected.Scenes),
            $"ids {string.Join(", ", ids)}, patterns({string.Join(" / ", patterns)}); selected: scene {afterSelect.Item1 + 1} at {Seconds(afterSelect.Item2)} s; pressed: scene {afterInvoke.Item1 + 1} at {Seconds(afterInvoke.Item2)} s; "
                + $"taking the first out of the selection was {(removed ? "accepted" : "refused")}, and the lane is {afterRemove.Item2}; selected: scene {afterAgain.Item1 + 1} at {Seconds(afterAgain.Item2)} s");
    }

    /// <summary>
    /// Whether a lane's blocks are on the trim bar's time scale: each starts and ends, a given
    /// distance inside its times, where the trim bar's playhead is when it is on those times,
    /// and the lane's own playhead line is there with it. Two checks.
    /// </summary>
    /// <param name="inset">How far inside its times a block is drawn, in effective pixels: half the gap the scene lane leaves between two scenes.</param>
    private void LaneIsOnTheTimeScale(Editor editor, string lane, string itemPrefix, string what, (double Start, double End)[] ranges, double inset)
    {
        var laneBox = Find(editor, lane)?.Bounds ?? default;
        var bar = Find(editor, "StudioTrimBar")?.Bounds ?? default;
        var wrong = new List<string>();
        var worst = 0.0;
        var wrongLine = new List<string>();
        var worstLine = 0.0;
        var lineSeen = new HashSet<int>();
        for (var index = 0; index < ranges.Length; index++)
        {
            var block = Find(editor, $"{itemPrefix}{index}")?.Bounds ?? default;
            foreach (var (time, edge, which) in new[] { (ranges[index].Start, block.X - (inset * editor.Scale), "starts"), (ranges[index].End, block.X + block.Width + (inset * editor.Scale), "ends") })
            {
                SetSlider(editor, "StudioPlayhead", time);
                var head = Until(() => Find(editor, "StudioPlayhead", 0.5), thumb => thumb?.Range is { } r && Math.Abs(r.Value - time) < 0.001, 2)?.Bounds ?? default;
                var middle = head.X + (head.Width / 2.0);
                worst = Math.Max(worst, Math.Abs(middle - edge));
                if (Math.Abs(middle - edge) > 1.5 || block.Width <= 0)
                {
                    wrong.Add($"{what} {index + 1} {which} at x {F(edge)} and the playhead at {Seconds(time)} s is at x {F(middle)}");
                }

                // The lane's line is not in the tree a screen reader gets, so the lane is asked, in its own units from its left edge.
                var line = laneBox.X + (OnUi(() => LaneOf(editor, lane)?.PlayheadLineX ?? double.NaN) * editor.Scale);
                lineSeen.Add((int)Math.Round(line));
                if (Math.Abs(middle - line) <= 1.5)
                {
                    worstLine = Math.Max(worstLine, Math.Abs(middle - line));
                }
                else
                {
                    wrongLine.Add($"at {Seconds(time)} s the line is at x {F(line)} and the trim bar's playhead at x {F(middle)}");
                }
            }
        }

        var gap = inset > 0 ? $", {F(inset)} inside them" : string.Empty;
        _report.Check(
            $"the {what} lane is as wide as the trim bar and on its time scale: every block starts and ends where the trim bar's playhead is at those times{gap}",
            laneBox.X == bar.X && laneBox.Width == bar.Width && laneBox.Y + laneBox.Height <= bar.Y && wrong.Count == 0 && ranges.Length > 0,
            wrong.Count == 0 ? $"lane {laneBox.Width}x{laneBox.Height} at ({laneBox.X},{laneBox.Y}), trim bar {bar.Width}x{bar.Height} at ({bar.X},{bar.Y}); no block edge more than {F(worst, "0.#")} px from the playhead at its time" : string.Join("; ", wrong));
        _report.Check(
            $"the {what} lane's playhead line follows the playhead: at each of those times it is over the playhead of the trim bar",
            wrongLine.Count == 0 && lineSeen.Count >= Math.Min(4, ranges.Length + 1),
            wrongLine.Count == 0 ? $"the line was at {lineSeen.Count} different places for the {ranges.Length * 2} times, never more than {F(worstLine, "0.#")} px from the trim bar's playhead" : string.Join("; ", wrongLine));
    }

    /// <summary>
    /// What a press and a drag on the scene lane do, by calling what the lane's pointer handlers
    /// call with a place along the lane. No pointer is moved or pressed, so which element a real
    /// pointer would land on, and that the lane keeps the pointer while it is down, is not seen.
    /// </summary>
    private void SceneLaneUnderAPointer()
    {
        Timeline.Mark("12: presses and drags on the scene lane");
        var folder = NewCameraProject("Scene drags", p => p with
        {
            Scenes =
            [
                p.Scenes[0],
                p.Scenes[0] with { Start = 4, Layout = StudioLayout.SideBySide, Transition = new StudioTransition { Kind = StudioTransitionKind.Morph } },
                p.Scenes[0] with { Start = 8, Layout = StudioLayout.Camera },
            ],
            Zooms = [PointZoom(1, 3, 0.5, 0.5)],
        });
        if (OpenReady(folder, "scene drags") is not { } editor)
        {
            return;
        }

        var opened = Describe(editor.Expected.Scenes);
        WaitForLane(editor, SceneLaneWanted(editor, 0), 2, SceneLane);

        // The lane in effective pixels, which is what its pointer handlers work in.
        var laneBounds = Find(editor, SceneLane)?.Bounds ?? default;
        var laneWidth = laneBounds.Width / editor.Scale;
        var secondsPerPixel = RecordingLength / (laneWidth - 24);
        double XOf(double time) => 12 + (time / secondsPerPixel);
        (double Left, double Width) Block(int index)
        {
            var bounds = Find(editor, $"StudioScene_{index}")?.Bounds ?? default;
            return ((bounds.X - laneBounds.X) / editor.Scale, bounds.Width / editor.Scale);
        }

        void Press(double x) => OnUi(() => LaneOf(editor, SceneLane)!.PressAt(x));
        void Drag(double x) => OnUi(() => LaneOf(editor, SceneLane)!.DragTo(x));
        void LetGo() => OnUi(() => LaneOf(editor, SceneLane)!.EndPress());
        bool CanUndo() => Find(editor, "StudioUndoButton", 0.5)?.IsEnabled == true;
        double StartOf(int index) => ScenesOf(editor)[index].Start;

        // Each block runs from one pixel after its scene's start to one before the next scene's.
        var blocks = Enumerable.Range(0, 3).Select(Block).ToArray();
        (double Start, double End)[] times = [(0, 4), (4, 8), (8, 12)];
        var geometry = blocks.Select((block, index) => Math.Abs(block.Left - (XOf(times[index].Start) + 1)) <= 1 && Math.Abs(block.Width - (((times[index].End - times[index].Start) / secondsPerPixel) - 2)) <= 1).ToArray();
        var grips = OnUi(() => Enumerable.Range(0, 3).Select(index => Descendant<StudioSceneBlock>(editor.Window.Content, $"StudioScene_{index}")?.FindName("Grip") is FrameworkElement { Visibility: Visibility.Visible }).ToArray());
        var names = OnUi(() => Enumerable.Range(0, 3).Select(index => Descendant<StudioSceneBlock>(editor.Window.Content, $"StudioScene_{index}")?.FindName("NameText") is Microsoft.UI.Xaml.Controls.TextBlock { Visibility: Visibility.Visible } text ? text.Text : string.Empty).ToArray());
        _report.Check(
            "each scene's block runs from its start to the start of the next scene, with a gap of two between neighbours, and shows the name of its layout; every block but the first has a mark at its start",
            geometry.All(ok => ok) && grips.SequenceEqual([false, true, true]) && names.SequenceEqual(["Screen with camera bubble", "Side by side", "Camera only"]),
            $"the lane is {F(laneWidth, "0.#")} wide; the blocks: {string.Join(", ", blocks.Select((block, index) => $"{F(block.Width, "0.#")} wide at {F(block.Left, "0.#")} (wanted {F(((times[index].End - times[index].Start) / secondsPerPixel) - 2, "0.#")} at {F(XOf(times[index].Start) + 1, "0.#")})"))}; "
                + $"a mark at the start: {string.Join(", ", grips)}; written on them: {string.Join(", ", names.Select(name => $"\"{name}\""))}");
        LaneIsOnTheTimeScale(editor, SceneLane, "StudioScene_", "scene", times, inset: 1);

        // A press in the middle of a block, with a zoom selected: the playhead goes there, and nothing else changes.
        Timeline.Mark("12: a press on a scene");
        Find(editor, "StudioZoom_0")?.Select();
        Until(() => SelectedZoomOf(editor), index => index == 0, 1);
        Press(XOf(6.0));
        LetGo();
        var afterPress = (CurrentSceneOf(editor), Playhead(editor), CanUndo(), SelectedZoomOf(editor), Describe(ScenesOf(editor)));
        _report.Check(
            "a press on a scene's block moves the playhead to the time that was pressed, which makes that scene the one the playhead is in; it selects nothing, lets go of nothing that is selected, and changes nothing that could be undone",
            afterPress.Item1 == 1 && Same(afterPress.Item2, 6.0, 0.01) && !afterPress.Item3 && afterPress.Item4 == 0 && afterPress.Item5 == opened && LaneText(editor, SceneLane) == SceneLaneWanted(editor, 1),
            $"the playhead is in scene {afterPress.Item1 + 1} at {Seconds(afterPress.Item2)} s; Undo enabled {afterPress.Item3}; the selected zoom is {(afterPress.Item4 is { } zoom ? (zoom + 1).ToString(CultureInfo.InvariantCulture) : "none")}; the lane: {LaneText(editor, SceneLane)}");

        // A drag that starts in the middle of a block moves the playhead along, and no scene.
        Press(XOf(2.0));
        var pressed = Playhead(editor);
        Drag(XOf(9.0));
        var dragged = (Playhead(editor), CurrentSceneOf(editor));
        Drag(XOf(20));
        var pastTheEnd = Playhead(editor);
        LetGo();
        _report.Check(
            "a drag from the middle of a block moves the playhead along, through the scenes and no further than the end of the recording, and moves no scene",
            Same(pressed, 2.0, 0.01) && Same(dragged.Item1, 9.0, 0.01) && dragged.Item2 == 2 && pastTheEnd == RecordingLength && !CanUndo() && Describe(ScenesOf(editor)) == opened,
            $"pressed at 2.0 s: {Seconds(pressed)} s; dragged to 9.0 s: {Seconds(dragged.Item1)} s, in scene {dragged.Item2 + 1}; dragged past the right end: {Seconds(pastTheEnd)} s; Undo enabled {CanUndo()}");

        // The start of the second scene, taken by the first pixels of its own block. A press there that does not move is a press.
        Timeline.Mark("12: dragging the start of a scene");
        var line = XOf(4.0);
        Press(line + 3);
        Drag(line + 3 + 2);
        LetGo();
        var still = (StartOf(1), Playhead(editor), CanUndo());
        Press(line + 3);
        Drag(line + 3 + 60);
        var half = StartOf(1);
        var headHalf = Playhead(editor);
        Drag(line + 3 + 120);
        LetGo();
        var whole = StartOf(1);
        var headWhole = Playhead(editor);
        var by = 120 * secondsPerPixel;
        var (movedLeft, _) = Block(1);
        var (_, firstWidth) = Block(0);
        Invoke(editor, "StudioUndoButton");
        var undone = Until(() => StartOf(1), start => start == 4, 1);
        var undoLeft = Until(() => !CanUndo(), nothing => nothing, 1);
        Invoke(editor, "StudioRedoButton");
        var redone = Until(() => StartOf(1), start => Same(start, 4 + by), 1);
        _report.Check(
            "a press on the first pixels of a block that moves less than 3 is a press: the playhead goes there and no scene moves. Dragged, those pixels move where the scene starts: the line between the two blocks follows, the playhead follows the line, and the whole drag is one undo step",
            still.Item1 == 4 && Same(still.Item2, 4 + (3 * secondsPerPixel), 0.01) && !still.Item3
                && Same(half, 4 + (by / 2)) && Same(headHalf, half) && Same(whole, 4 + by) && Same(headWhole, whole)
                && Math.Abs(movedLeft - (line + 120 + 1)) <= 1 && Math.Abs(firstWidth - (line + 120 - 12 - 2)) <= 1
                && undone == 4 && undoLeft && Same(redone, 4 + by),
            $"not moved: the scene starts at {Seconds(still.Item1)} s, playhead {Seconds(still.Item2)} s, Undo enabled {still.Item3}; 120 along the lane is {Seconds(by)} s; after 60 the scene starts at {Seconds(half)} s with the playhead at {Seconds(headHalf)} s; "
                + $"after 120 at {Seconds(whole)} s with the playhead at {Seconds(headWhole)} s, its block at {F(movedLeft, "0.#")} and the first block {F(firstWidth, "0.#")} wide; after one Undo {Seconds(undone)} s with nothing left to undo: {undoLeft}; after Redo {Seconds(redone)} s");

        // The same line, taken from its other side: the last pixels of the block before it. And from the gap between the two.
        line = XOf(StartOf(1));
        Press(line - 4);
        Drag(line - 4 - 50);
        LetGo();
        var fromBefore = StartOf(1);
        var headBefore = Playhead(editor);
        var back = 50 * secondsPerPixel;
        line = XOf(fromBefore);
        Press(line);
        Drag(line + 30);
        LetGo();
        var fromGap = StartOf(1);
        _report.Check(
            "the line between two scenes can be taken from either side: the last pixels of the block before it, and the gap between the two, move the later scene's start as its own first pixels do",
            Same(fromBefore, whole - back) && Same(headBefore, fromBefore) && Same(fromGap, fromBefore + (30 * secondsPerPixel)) && StartOf(2) == 8 && ScenesOf(editor).Length == 3,
            $"pressed 4 before the line and dragged 50 back: the second scene starts at {Seconds(fromBefore)} s (wanted {Seconds(whole - back)}), playhead {Seconds(headBefore)} s; pressed on the line and dragged 30 on: {Seconds(fromGap)} s (wanted {Seconds(fromBefore + (30 * secondsPerPixel))})");

        // A start stops 0.3 s after the start of the scene before, and 0.3 s before its own scene's end.
        Timeline.Mark("12: where the start of a scene stops");
        line = XOf(8.0);
        Press(line + 3);
        Drag(XOf(0));
        LetGo();
        var earliest = StartOf(2);
        var headEarliest = Playhead(editor);
        line = XOf(earliest);
        Press(line + 3);
        Drag(XOf(12) + 100);
        LetGo();
        var latest = StartOf(2);
        _report.Check(
            "a start dragged against the scene before stops 0.3 seconds after that scene's start, and dragged the other way 0.3 seconds before the end of the recording; the playhead stops with it",
            Same(earliest, fromGap + 0.3) && Same(headEarliest, earliest) && Same(latest, RecordingLength - 0.3) && Same(Playhead(editor), latest) && Same(StartOf(1), fromGap),
            $"dragged to the left end: the third scene starts at {Seconds(earliest)} s, and the second at {Seconds(StartOf(1))} s; dragged past the right end: at {Seconds(latest)} s; playhead {Seconds(Playhead(editor))} s");

        // Five drags moved a start, and nothing else changed the project: five Undo bring it back to how it was opened.
        for (var undo = 0; undo < 5; undo++)
        {
            var before = Describe(ScenesOf(editor));
            Invoke(editor, "StudioUndoButton");
            Until(() => Describe(ScenesOf(editor)), now => now != before, 1);
        }

        var restored = Describe(ScenesOf(editor));
        var nothingLeft = Until(() => !CanUndo(), nothing => nothing, 1);
        _report.Check(
            "each drag that moved a start is one undo step, and a press or a drag of the playhead is none: five Undo bring back the three scenes as they were opened and leave nothing to undo",
            nothingLeft && restored == opened,
            $"after five Undo the editor holds {restored}, and nothing is left to undo: {nothingLeft}");
        CloseQuietly(editor);
    }

    /// <summary>
    /// Deleting scenes: Delete while the scene lane has the keyboard focus, which is about the
    /// scene and not about a selected zoom, and the Delete scene button with the focus on it.
    /// </summary>
    private void DeletingScenes()
    {
        Timeline.Mark("12: deleting scenes");
        var folder = NewCameraProject("Scenes to delete", p => p with
        {
            Scenes =
            [
                p.Scenes[0],
                p.Scenes[0] with { Start = 3, Layout = StudioLayout.SideBySide, Transition = new StudioTransition { Kind = StudioTransitionKind.Morph } },
                p.Scenes[0] with { Start = 6, Layout = StudioLayout.Camera },
                p.Scenes[0] with { Start = 9, Layout = StudioLayout.Screen },
            ],
            Zooms = [PointZoom(1, 2, 0.5, 0.5)],
        });
        if (OpenReady(folder, "scenes to delete") is not { } editor)
        {
            return;
        }

        using var heard = UiaEvents.Listen(_uia, editor.Root);
        var four = editor.Expected.Scenes;
        string Lane() => LaneText(editor, SceneLane);

        // The playhead in the third scene, a zoom selected, and the keyboard focus on the scene lane.
        SetSlider(editor, "StudioPlayhead", 7.0);
        Find(editor, "StudioZoom_0")?.Select();
        Until(() => SelectedZoomOf(editor), index => index == 0, 1);
        var onLane = FocusOn(editor, SceneLane);
        var mark = heard.Mark();
        var laneKey = Key(editor, StudioShortcutKey.Delete);
        StudioScene[] three = [four[0], four[1], four[3]];
        var afterLane = WaitForLane(editor, SceneLaneFor(three, 1), 2, SceneLane);
        var laneSaid = Said(heard, mark, StudioEditorText.SceneDeletedMessage);
        var zoomsLeft = ZoomsOf(editor).Length;
        var stillSelected = SelectedZoomOf(editor);
        _report.Check(
            "with the keyboard focus on the scene lane, what Delete runs deletes the scene the playhead is in, and not the selected zoom: the scene before it then lasts until the next one, the playhead is in that scene, and a screen reader is told \"Scene deleted.\"",
            onLane == SceneLane && laneKey == StudioShortcutAction.RemoveCurrentScene && afterLane == SceneLaneFor(three, 1) && Describe(ScenesOf(editor)) == Describe(three) && laneSaid.Said && zoomsLeft == 1 && stillSelected == 0,
            $"the focus is on \"{onLane}\"; Delete ran {laneKey}; the lane: {afterLane}; sent: {laneSaid.Heard}; zooms left: {zoomsLeft}, the selected one: {(stillSelected is { } zoom ? (zoom + 1).ToString(CultureInfo.InvariantCulture) : "none")}");

        // The same key with the focus elsewhere is about the selected zoom.
        var onPlay = FocusOn(editor, "StudioPlayPauseButton");
        var elsewhereKey = Key(editor, StudioShortcutKey.Delete);
        var zoomsAfter = Until(() => ZoomsOf(editor).Length, count => count == 0, 1);
        _report.Check(
            "with the keyboard focus on Play, what Delete runs deletes the selected zoom and leaves the scenes alone",
            onPlay == "StudioPlayPauseButton" && elsewhereKey == StudioShortcutAction.RemoveSelectedZoom && zoomsAfter == 0 && Describe(ScenesOf(editor)) == Describe(three),
            $"the focus is on \"{onPlay}\"; Delete ran {elsewhereKey}; zooms left: {zoomsAfter}; the lane: {Lane()}");

        // Delete scene, with the keyboard focus on it, in the last of three scenes: two are left, and the button stays.
        Timeline.Mark("12: the Delete scene button");
        SetSlider(editor, "StudioPlayhead", 10.0);
        WaitForLane(editor, SceneLaneFor(three, 2), 2, SceneLane);
        var onDelete = FocusOn(editor, "StudioDeleteSceneButton");
        var help = Find(editor, "StudioDeleteSceneButton", 0.5);
        mark = heard.Mark();
        var pressed = Invoke(editor, "StudioDeleteSceneButton");
        StudioScene[] two = [four[0], four[1]];
        var afterButton = WaitForLane(editor, SceneLaneFor(two, 1), 2, SceneLane);
        var buttonSaid = Said(heard, mark, StudioEditorText.SceneDeletedMessage);
        var focusAfter = FocusedId(editor);
        _report.Check(
            "Delete scene deletes the scene the playhead is in and says so; with another scene left to delete the button stays, and keeps the keyboard focus",
            onDelete == "StudioDeleteSceneButton" && pressed && afterButton == SceneLaneFor(two, 1) && Describe(ScenesOf(editor)) == Describe(two) && buttonSaid.Said && focusAfter == "StudioDeleteSceneButton" && help is { Name: "Delete scene", IsEnabled: true },
            $"the lane: {afterButton}; sent: {buttonSaid.Heard}; the focus was on \"{onDelete}\" and is on \"{focusAfter}\"");

        // In the first of two scenes: its time goes to the second, which then starts with the
        // recording. One scene is left, the button goes, and the focus goes to Split at playhead.
        SetSlider(editor, "StudioPlayhead", 1.0);
        WaitForLane(editor, SceneLaneFor(two, 0), 2, SceneLane);
        mark = heard.Mark();
        var pressedAgain = Invoke(editor, "StudioDeleteSceneButton");
        StudioScene[] one = [four[1] with { Start = 0 }];
        var afterFirst = WaitForLane(editor, SceneLaneFor(one, 0), 2, SceneLane);
        var firstSaid = Said(heard, mark, StudioEditorText.SceneDeletedMessage);
        var gone = Absent(editor, "StudioDeleteSceneButton");
        var focusOnSplit = Until(() => FocusedId(editor), id => id == "StudioSceneSectionSplitButton", 1.5);
        var note = NameOf(editor, "StudioOneSceneNote", 1);
        _report.Check(
            "deleting the first scene hands its time to the second, which then starts with the recording; with one scene left Delete scene is gone, the section says what scenes are for, and the keyboard focus is on Split at playhead",
            pressedAgain && afterFirst == "*Scene 1 of 1, Side by side, 0.0 to 12.0 seconds" && Describe(ScenesOf(editor)) == Describe(one) && firstSaid.Said && gone && focusOnSplit == "StudioSceneSectionSplitButton" && note == StudioEditorText.OneSceneExplanation,
            $"the lane: {afterFirst}; the editor holds {Describe(ScenesOf(editor))}; sent: {firstSaid.Heard}; Delete scene gone: {gone}; the focus is on \"{focusOnSplit}\"; the note: \"{note}\"");

        // The only scene stays, and Delete on the lane says so.
        FocusOn(editor, SceneLane);
        mark = heard.Mark();
        var onlyKey = Key(editor, StudioShortcutKey.Delete);
        var onlySaid = Said(heard, mark, StudioEditorText.OnlySceneExplanation);
        _report.Check(
            "with one scene, what Delete runs on the scene lane deletes nothing and tells a screen reader that the only scene cannot be deleted",
            onlyKey == StudioShortcutAction.RemoveCurrentScene && onlySaid.Said && Describe(ScenesOf(editor)) == Describe(one),
            $"Delete ran {onlyKey}; sent: {onlySaid.Heard}; the lane: {Lane()}");

        // Four Undo: the two scenes, the zoom, and the first scene that was deleted.
        string[] wanted = [SceneLaneFor(two, 0), SceneLaneFor(three, 0), SceneLaneFor(three, 0), SceneLaneFor(four, 0)];
        var seen = new List<string>();
        foreach (var lane in wanted)
        {
            var before = (Lane(), ZoomsOf(editor).Length);
            Invoke(editor, "StudioUndoButton");
            Until(() => (Lane(), ZoomsOf(editor).Length), now => now != before, 1.5);
            seen.Add(WaitForLane(editor, lane, 1, SceneLane));
        }

        _report.Check(
            "each delete is one undo step: four Undo bring back the scene that was deleted last, the one before it, the zoom, and the first one, in that order, and the lane shows each",
            seen.SequenceEqual(wanted) && Describe(ScenesOf(editor)) == Describe(four) && ZoomsOf(editor).Length == 1,
            $"the lane after each Undo: {string.Join(" || ", seen)}; zooms: {ZoomsOf(editor).Length}");
        CloseQuietly(editor);
    }

    /// <summary>
    /// The scene the playhead is in has to be told from the others by more than its colour.
    /// Read from the pixels of a window in one theme: its border is twice as thick as another
    /// scene's; and from the block itself: its name is in a heavier weight.
    /// </summary>
    /// <remarks>
    /// What this cannot tell: whether the difference is easy to see on a real screen. Contrast
    /// themes are not seen.
    /// </remarks>
    private void SceneBlockLooks(AppTheme theme, string name)
    {
        Timeline.Mark($"12: what the scene the playhead is in looks like, {name}");
        _services.Settings.Theme = theme;
        try
        {
            var folder = NewCameraProject($"Scene looks, {name}", p => p with
            {
                Scenes = [p.Scenes[0], p.Scenes[0] with { Start = 4, Layout = StudioLayout.SideBySide }, p.Scenes[0] with { Start = 8, Layout = StudioLayout.Camera }],
            });
            if (OpenReady(folder, $"scene looks, {name}") is not { } editor)
            {
                return;
            }

            var scale = editor.Scale;

            // Of one block: the colour along the row of its border, along the row an effective
            // pixel under it, and of its inside clear of what is written on it; and the weight
            // of its name.
            (Rgb Border, Rgb Under, Rgb Inside, int Weight)? Read(Shot shot, int index)
            {
                if (Find(editor, $"StudioScene_{index}", 0.5)?.Bounds is not { Width: > 0 } bounds)
                {
                    return null;
                }

                var (left, top) = (bounds.X - shot.ScreenX, bounds.Y - shot.ScreenY);
                Rgb Along(double y)
                {
                    var colours = Enumerable.Range(0, (int)(40 * scale)).Select(x => shot.Color(left + (8 * scale) + x, Math.Floor(y), 0)).ToArray();
                    return new Rgb(colours.Average(colour => colour.R), colours.Average(colour => colour.G), colours.Average(colour => colour.B));
                }

                var weight = OnUi(() => Descendant<StudioSceneBlock>(editor.Window.Content, $"StudioScene_{index}")?.FindName("NameText") is Microsoft.UI.Xaml.Controls.TextBlock text ? (int)text.FontWeight.Weight : 0);
                return (Along(top + (0.5 * scale)), Along(top + (1.5 * scale)), shot.Color(left + bounds.Width - (40 * scale), top + (bounds.Height / 2.0), 2), weight);
            }

            // With the playhead in the second scene, and then in the first.
            var wrong = new List<string>();
            var told = new List<string>();
            foreach (var (at, current, other) in new[] { (6.0, 1, 0), (2.0, 0, 1) })
            {
                SetSlider(editor, "StudioPlayhead", at);
                WaitForLane(editor, SceneLaneWanted(editor, current), 2, SceneLane);
                Thread.Sleep(500);
                if (editor.Camera.Take() is not { } shot || Read(shot, current) is not { } marked || Read(shot, other) is not { } plain)
                {
                    wrong.Add($"with the playhead in scene {current + 1}: no screenshot, or no block");
                    continue;
                }

                if (current == 1)
                {
                    var path = Path.Combine(_output, $"scene-looks-{name}.png");
                    shot.Save(path);
                    _report.Line($"  saved {path}");
                }

                // Another scene: one row of border, and its inside right under it. The scene the
                // playhead is in: two rows of border, in another colour than its inside.
                var isThin = plain.Border.Distance(plain.Inside) > 24 && plain.Under.Distance(plain.Inside) < 10;
                var isThick = marked.Border.Distance(marked.Inside) > 24 && marked.Under.Distance(marked.Border) < 10;
                var isHeavier = marked.Weight >= 600 && plain.Weight is > 0 and < 600;
                var isTinted = marked.Inside.Distance(plain.Inside) > 12;
                told.Add($"with the playhead in scene {current + 1}: its block has a border of {marked.Border} that goes on in the row under it ({marked.Under}) around an inside of {marked.Inside}, and its name at weight {marked.Weight}; scene {other + 1} has a border of {plain.Border} with {plain.Under} under it, an inside of {plain.Inside}, and its name at weight {plain.Weight}");
                if (!isThin || !isThick || !isHeavier || !isTinted)
                {
                    wrong.Add($"with the playhead in scene {current + 1}: {(isThick ? string.Empty : "its border is not two rows thick; ")}{(isThin ? string.Empty : $"the border of scene {other + 1} is not one row thick; ")}{(isHeavier ? string.Empty : "its name is not heavier; ")}{(isTinted ? string.Empty : "its inside is the colour of the other's; ")}{told[^1]}");
                }
            }

            _report.Check(
                $"in the {name} theme the scene the playhead is in differs from the others by more than its colour: its border is twice as thick and its name is in a heavier weight; it is also tinted, and all of that goes to another block when the playhead goes to another scene",
                wrong.Count == 0 && told.Count == 2,
                wrong.Count == 0 ? string.Join("; ", told) : string.Join(" | ", wrong));
            CloseQuietly(editor);
        }
        finally
        {
            _services.Settings.Theme = AppTheme.Default;
        }
    }
}
