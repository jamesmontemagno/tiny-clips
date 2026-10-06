using System.Globalization;
using TinyClips.App.Controls.Studio;
using TinyClips.Core.Models;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Capture;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 14, continued. The speed lane under a pointer, what a speed change's block looks like, what a
// press on each row of the timeline does to a selected speed change, the trim bar of a video
// with speed changes, and a trim and a rate that the speed changes do not allow.
internal sealed partial class WindowChecks
{
    /// <summary>
    /// What a press and a drag on the speed lane do, by calling what the lane's pointer handlers
    /// call with a place along the lane. No pointer is moved or pressed, so which element a real
    /// pointer would land on, and that the lane keeps the pointer while it is down, is not seen.
    /// </summary>
    private void SpeedLaneUnderAPointer()
    {
        Timeline.Mark("14: presses and drags on the speed lane");

        // Four speed changes of four widths: one wide enough for its rate to be written on it, one
        // for the mark alone, a long one, and one too narrow for either and for ends of its own.
        // Two are faster than the recording and two slower.
        StudioSpeedRange[] opened = [Speed(1, 2), Speed(4, 4.45, 0.5), Speed(6, 7.5, 4), Speed(11, 11.2, 0.25)];
        var folder = NewScreenProject("Speed drags", p => p with { Edits = p.Edits with { Speed = opened }, Zooms = [PointZoom(8, 10, 0.5, 0.5)] });
        if (OpenReady(folder, "speed drags") is not { } editor)
        {
            return;
        }

        var problem = SpeedsAre(editor, opened, null);
        if (problem is not null)
        {
            _report.Check("the editor opens a project with four speed changes, and selects none", false, problem);
            CloseQuietly(editor);
            return;
        }

        // The lane in effective pixels, which is what its pointer handlers work in.
        var laneBounds = Find(editor, SpeedLane)?.Bounds ?? default;
        var laneWidth = laneBounds.Width / editor.Scale;
        var secondsPerPixel = RecordingLength / (laneWidth - 24);
        double XOf(double time) => 12 + (time / secondsPerPixel);
        (double Left, double Width) Block(int index)
        {
            var bounds = Find(editor, $"StudioSpeed_{index}")?.Bounds ?? default;
            return ((bounds.X - laneBounds.X) / editor.Scale, bounds.Width / editor.Scale);
        }

        // A block once it has been laid out where it is wanted, or as it is when the time is up.
        (double Left, double Width) BlockAt(int index, double left) => Until(() => Block(index), block => Math.Abs(block.Left - left) <= 1, 1);
        void Press(double x) => OnUi(() => LaneOf(editor, SpeedLane)!.PressAt(x));
        void Drag(double x) => OnUi(() => LaneOf(editor, SpeedLane)!.DragTo(x));
        void LetGo() => OnUi(() => LaneOf(editor, SpeedLane)!.EndPress());
        bool CanUndo() => Find(editor, "StudioUndoButton", 0.5)?.IsEnabled == true;
        StudioSpeedRange SpeedAt(int index) => SpeedsOf(editor)[index];
        string Selection() => OfThreeSelected(editor);

        // Each block runs from where its speed change starts to where it ends, and says what fits on it.
        var blocks = Enumerable.Range(0, 4).Select(Block).ToArray();
        var geometry = blocks.Select((block, index) => Math.Abs(block.Left - XOf(opened[index].Start)) <= 1 && Math.Abs(block.Width - ((opened[index].End - opened[index].Start) / secondsPerPixel)) <= 1).ToArray();
        var marks = Enumerable.Range(0, 4).Select(index => SpeedMarks(editor, index)).ToArray();

        // The mark from 26 wide, the rate from 64: the four speed changes are chosen to be on each side of both.
        string[] rates = ["2×", string.Empty, "4×", string.Empty];
        string[] kinds = ["faster", "slower", "faster", "none"];
        _report.Check(
            "each speed change's block runs from where it starts to where it ends; one that is at least 26 wide carries a mark, the one for faster or the one for slower and never both, and one that is at least 64 wide its rate as well, such as \"2×\"",
            geometry.All(ok => ok) && marks.Select(mark => mark.Faster && mark.Slower ? "both" : mark.Faster ? "faster" : mark.Slower ? "slower" : "none").SequenceEqual(kinds) && marks.Select(mark => mark.Rate).SequenceEqual(rates)
                && blocks[0].Width >= 64 && blocks[1].Width is >= 26 and < 64 && blocks[3].Width < 24,
            $"the lane is {F(laneWidth, "0.#")} wide; the blocks: {string.Join(", ", blocks.Select((block, index) => $"{F(block.Width, "0.#")} wide at {F(block.Left, "0.#")} (wanted {F((opened[index].End - opened[index].Start) / secondsPerPixel, "0.#")} at {F(XOf(opened[index].Start), "0.#")})"))}; "
                + $"marks: {string.Join(", ", marks.Select(mark => mark.Faster && mark.Slower ? "both" : mark.Faster ? "faster" : mark.Slower ? "slower" : "none"))}; written on them: {string.Join(", ", marks.Select(mark => $"\"{mark.Rate}\""))}");

        // A press in the middle of a block, with a zoom selected.
        Timeline.Mark("14: a press on a speed change, and on the empty lane");
        Find(editor, "StudioZoom_0")?.Select();
        Until(() => SelectedZoomOf(editor), index => index == 0, 1);
        Press(XOf(6.7));
        LetGo();
        var pressed = (Selection(), Playhead(editor), CanUndo(), Describe(SpeedsOf(editor)));
        var laneAfterPress = WaitForLane(editor, SpeedLaneFor(opened, 2), 2, SpeedLane);
        var zoomLaneAfterPress = LaneText(editor);
        _report.Check(
            "a press on a speed change's block selects it, which lets go of the selected zoom, and moves the playhead to the time that was pressed; no speed change changes, and nothing is added to what can be undone",
            pressed.Item1 == "zoom none, cut none, speed change 3" && Same(pressed.Item2, 6.7, 0.01) && !pressed.Item3 && pressed.Item4 == Describe(opened) && laneAfterPress == SpeedLaneFor(opened, 2) && !zoomLaneAfterPress.Contains('*'),
            $"selected: {pressed.Item1}; playhead {Seconds(pressed.Item2)} s; Undo enabled {pressed.Item3}; the speed lane: {laneAfterPress}; the zoom lane: {zoomLaneAfterPress}");

        // A press on the empty lane, with the speed change selected: it lets go of it. And with the zoom selected: of the zoom.
        Press(XOf(9.0));
        LetGo();
        var empty = (Selection(), Playhead(editor));
        Find(editor, "StudioZoom_0")?.Select();
        Until(() => SelectedZoomOf(editor), index => index == 0, 1);
        Press(XOf(3.0));
        var emptyWithZoom = (Selection(), Playhead(editor));

        // Dragged on from there, over two speed changes: the playhead goes along, and nothing is taken hold of.
        Drag(XOf(3.0) + 1);
        var afterOne = Playhead(editor);
        Drag(XOf(7.0));
        var over = (Playhead(editor), Selection());
        Drag(-40);
        var beforeTheStart = Playhead(editor);
        LetGo();
        const string Nothing = "zoom none, cut none, speed change none";
        _report.Check(
            "a press on the empty speed lane moves the playhead there and selects nothing: it lets go of the selected speed change, and of the selected zoom as well; dragged on, it moves the playhead along from the first pixel, over the speed changes and no further than the start of the recording, and takes hold of none",
            empty.Item1 == Nothing && Same(empty.Item2, 9.0, 0.01) && emptyWithZoom.Item1 == Nothing && Same(emptyWithZoom.Item2, 3.0, 0.01)
                && Same(afterOne, 3.0 + secondsPerPixel, 0.005) && Same(over.Item1, 7.0, 0.01) && over.Item2 == Nothing && beforeTheStart == 0 && !CanUndo() && Describe(SpeedsOf(editor)) == Describe(opened),
            $"pressed at 9.0 s with a speed change selected: {empty.Item1}, playhead {Seconds(empty.Item2)} s; pressed at 3.0 s with a zoom selected: {emptyWithZoom.Item1}, playhead {Seconds(emptyWithZoom.Item2)} s; "
                + $"dragged by 1: {Seconds(afterOne)} s; dragged to 7.0 s: {Seconds(over.Item1)} s, {over.Item2}; dragged past the left end: {Seconds(beforeTheStart)} s; Undo enabled {CanUndo()}");

        // The body of the third speed change. A press that moves less than 3 is a press.
        Timeline.Mark("14: dragging a speed change");
        var grab = XOf(6.5);
        Press(grab);
        Drag(grab + 2);
        LetGo();
        var still = (Describe(SpeedAt(2)), CanUndo(), SelectedSpeedOf(editor));
        Press(grab);
        Drag(grab + 60);
        var half = SpeedAt(2);
        Drag(grab + 120);
        LetGo();
        var whole = SpeedAt(2);
        var by = 120 * secondsPerPixel;
        var headAfterMove = Playhead(editor);
        var (movedLeft, movedWidth) = BlockAt(2, XOf(6) + 120);
        Invoke(editor, "StudioUndoButton");
        var undone = Until(() => Describe(SpeedAt(2)), now => now == Describe(opened[2]), 1);
        var nothingLeft = Until(() => !CanUndo(), nothing => nothing, 1);
        var selectedAfterUndo = SelectedSpeedOf(editor);
        Invoke(editor, "StudioRedoButton");
        var redone = Until(() => SpeedAt(2), now => Same(now.Start, 6 + by), 1);
        _report.Check(
            "a press on a speed change that moves less than 3 changes nothing. Dragged, the middle of a block moves the whole speed change and keeps its length and its rate: its block follows, the playhead stays where it was pressed, and the whole drag is one undo step, through which the speed change stays selected",
            still.Item1 == Describe(opened[2]) && !still.Item2 && still.Item3 == 2
                && Same(half.Start, 6 + (by / 2)) && Same(half.End, 7.5 + (by / 2)) && Same(whole.Start, 6 + by) && Same(whole.End, 7.5 + by) && whole.Rate == 4 && Same(headAfterMove, 6.5, 0.01)
                && Math.Abs(movedLeft - (XOf(6) + 120)) <= 1 && Math.Abs(movedWidth - blocks[2].Width) <= 1
                && undone == Describe(opened[2]) && nothingLeft && selectedAfterUndo == 2 && Same(redone.Start, 6 + by) && Same(redone.End, 7.5 + by),
            $"not moved: {still.Item1}, Undo enabled {still.Item2}; 120 along the lane is {Seconds(by)} s; after 60 the speed change is {Describe(half)}, after 120 {Describe(whole)} with the playhead at {Seconds(headAfterMove)} s and its block {F(movedWidth, "0.#")} wide at {F(movedLeft, "0.#")}; "
                + $"after one Undo {undone} with nothing left to undo: {nothingLeft}, selected speed change {selectedAfterUndo + 1}; after Redo {Describe(redone)}");

        // Dragged on to the right, further than there is room: it stops against the next speed change, and the two touch.
        var from = XOf(redone.Start + 0.75);
        Press(from);
        Drag(from + 400);
        LetGo();
        var stopped = SpeedAt(2);
        var stoppedBlock = BlockAt(2, XOf(9.5));
        var nextBlock = Block(3);
        Press(XOf(stopped.Start + 0.75));
        Drag(-400);
        LetGo();
        var stoppedLeft = SpeedAt(2);
        _report.Check(
            "a speed change that is dragged further than there is room stops against its neighbour and keeps its length: to the right its end is where the next one starts, so the two blocks touch, and to the left its start is where the one before it ends; none changes its place among the others",
            stopped.End == 11 && Same(stopped.Start, 9.5) && Math.Abs(stoppedBlock.Left + stoppedBlock.Width - nextBlock.Left) <= 1
                && Same(stoppedLeft.Start, 4.45) && Same(stoppedLeft.End, 5.95) && SelectedSpeedOf(editor) == 2 && LaneItems(editor, SpeedLane).Count == 4,
            $"dragged far to the right: {Describe(stopped)}, its block ends at {F(stoppedBlock.Left + stoppedBlock.Width, "0.#")} and the next starts at {F(nextBlock.Left, "0.#")}; dragged far to the left: {Describe(stoppedLeft)}; the lane: {LaneText(editor, SpeedLane)}");

        // The first pixels of a block move where the speed change starts.
        Timeline.Mark("14: dragging the ends of a speed change");
        var first = Block(0);
        Press(first.Left + 3);
        Drag(first.Left + 3 - 30);
        var startEarlier = SpeedAt(0);
        var headAtStart = Playhead(editor);
        Drag(first.Left + 3 - 300);
        var startAtZero = SpeedAt(0);
        Drag(first.Left + 3 + 300);
        LetGo();
        var startAgainstEnd = SpeedAt(0);
        var headAgainstEnd = Playhead(editor);
        _report.Check(
            "the first 6 of a block move where its speed change starts, and the playhead follows it: no earlier than the start of the recording, and no later than a tenth of a second before the speed change's end, which stays",
            Same(startEarlier.Start, 1 - (30 * secondsPerPixel)) && startEarlier.End == 2 && Same(headAtStart, startEarlier.Start)
                && startAtZero.Start == 0 && startAtZero.End == 2 && Same(startAgainstEnd.Start, 1.9) && startAgainstEnd.End == 2 && Same(headAgainstEnd, 1.9) && SelectedSpeedOf(editor) == 0,
            $"30 to the left: {Describe(startEarlier)}, playhead {Seconds(headAtStart)} s; far to the left: {Describe(startAtZero)}; far to the right: {Describe(startAgainstEnd)}, playhead {Seconds(headAgainstEnd)} s");
        Invoke(editor, "StudioUndoButton");
        var startBack = Until(() => Describe(SpeedAt(0)), now => now == Describe(opened[0]), 1);

        // The last pixels move where it ends.
        first = BlockAt(0, blocks[0].Left);
        var end = first.Left + first.Width - 3;
        Press(end);
        Drag(end + 40);
        var endLater = SpeedAt(0);
        var headAtEnd = Playhead(editor);
        Drag(end + 400);
        var endAgainstNext = SpeedAt(0);
        Drag(end - 400);
        LetGo();
        var endAgainstStart = SpeedAt(0);
        var headAgainstStart = Playhead(editor);
        _report.Check(
            "the last 6 of a block move where its speed change ends, and the playhead follows it: no later than where the next one starts, and no earlier than a tenth of a second after the speed change's start, which stays; the one Undo before it took the whole drag of the start back",
            startBack == Describe(opened[0]) && endLater.Start == 1 && Same(endLater.End, 2 + (40 * secondsPerPixel)) && Same(headAtEnd, endLater.End)
                && endAgainstNext.Start == 1 && Same(endAgainstNext.End, 4) && endAgainstStart.Start == 1 && Same(endAgainstStart.End, 1.1) && Same(headAgainstStart, 1.1),
            $"after the Undo: {startBack}; 40 to the right: {Describe(endLater)}, playhead {Seconds(headAtEnd)} s; far to the right: {Describe(endAgainstNext)}; far to the left: {Describe(endAgainstStart)}, playhead {Seconds(headAgainstStart)} s");
        Invoke(editor, "StudioUndoButton");
        Until(() => Describe(SpeedAt(0)), now => now == Describe(opened[0]), 1);

        // A block narrower than 24 has no ends of its own: wherever it is taken, the whole speed change moves.
        var narrow = BlockAt(3, blocks[3].Left);
        Press(narrow.Left + 2);
        Drag(narrow.Left + 2 - 40);
        LetGo();
        var narrowMoved = SpeedAt(3);
        _report.Check(
            "a block narrower than 24 is moved as a whole from its first pixels too, because it has no room for ends of its own: the speed change keeps its length",
            narrow.Width < 24 && Same(narrowMoved.Start, 11 - (40 * secondsPerPixel)) && Same(narrowMoved.End - narrowMoved.Start, 0.2) && SelectedSpeedOf(editor) == 3,
            $"the block is {F(narrow.Width, "0.#")} wide; taken at its second pixel and dragged 40 to the left: {Describe(narrowMoved)}");

        // Every drag was one step. Two were taken back above; the four that are left, of the
        // body of the third speed change three times and of the narrow one, are taken back one by one.
        var steps = new List<string>();
        for (var undo = 0; undo < 4 && CanUndo(); undo++)
        {
            var before = Describe(SpeedsOf(editor));
            Invoke(editor, "StudioUndoButton");
            steps.Add(Until(() => Describe(SpeedsOf(editor)), now => now != before, 1));
        }

        var back = SpeedsAre(editor, opened, 3);
        _report.Check(
            "each drag was one undo step: the four that had not been undone are taken back by four Undo, after which the speed changes are as the project was opened, the one that was selected still is, and there is nothing left to undo",
            steps.Count == 4 && back is null && Until(() => !CanUndo(), nothing => nothing, 1),
            back ?? $"after each Undo: {string.Join(" | ", steps)}");
        CloseQuietly(editor);

        SpeedBlockLooks(AppTheme.Light, "light");
        SpeedBlockLooks(AppTheme.Dark, "dark");
    }

    // ---------------------------------------------------------------------------------------
    // What a press on each row of the timeline does to a selected speed change
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A press on the empty part of the zoom lane, the cut lane or the speed lane lets go of
    /// whatever is selected, whichever of the three lanes it is on and whichever of the three
    /// kinds is selected. A press on the scene lane and what the trim bar asks for leave a
    /// selected speed change as it is.
    /// </summary>
    /// <remarks>
    /// The lanes are pressed by calling what their pointer handlers call, and the trim bar is
    /// driven as in the checks of the cuts: see <see cref="PressesAndWhatIsSelected"/>.
    /// </remarks>
    private void PressesWithASpeedChange()
    {
        Timeline.Mark("14: presses on the rows of the timeline, and a selected speed change");

        // One or two of each, none where another is: a second scene from 6 s, a cut from 2 s, a zoom from 4 s, speed changes from 7 and from 9 s.
        StudioTimeRange[] cuts = [Cut(2, 3)];
        StudioZoom[] zooms = [PointZoom(4, 5.5, 0.5, 0.5)];
        StudioSpeedRange[] speeds = [Speed(7, 8), Speed(9, 10, 0.5)];
        var folder = NewCameraProject("Presses and a selected speed change", p => p with
        {
            Scenes = [p.Scenes[0], p.Scenes[0] with { Start = 6, Layout = StudioLayout.SideBySide }],
            Edits = p.Edits with { Cuts = cuts, Speed = speeds },
            Zooms = zooms,
        });
        if (OpenReady(folder, "presses and a selected speed change") is not { } editor)
        {
            return;
        }

        var opened = SpeedsAre(editor, speeds, null) ?? CutsAre(editor, cuts, null);
        if (opened is not null || ZoomsOf(editor).Length != 1 || ScenesOf(editor).Length != 2)
        {
            _report.Check("the editor opens a project with two scenes, a cut, a zoom and two speed changes, and selects none of them", false, opened ?? $"zooms: {ZoomsOf(editor).Length}; scenes: {ScenesOf(editor).Length}");
            CloseQuietly(editor);
            return;
        }

        // A press and a release at a time along a lane, in the lane's own effective pixels.
        void Press(string lane, double time)
        {
            var width = (Find(editor, lane)?.Bounds.Width ?? 0) / editor.Scale;
            var x = 12 + (time / RecordingLength * (width - 24));
            OnUi(() =>
            {
                var pressed = LaneOf(editor, lane)!;
                pressed.PressAt(x);
                pressed.EndPress();
            });
        }

        bool SelectItem(string id, string wanted) => (Find(editor, id)?.Select() ?? false) && Until(() => OfThreeSelected(editor), now => now == wanted, 1) == wanted;
        bool CanUndo() => Find(editor, "StudioUndoButton", 0.5)?.IsEnabled == true;
        const string Nothing = "zoom none, cut none, speed change none";
        var lost = new List<string>();
        var kept = new List<string>();
        void Step(string what, string wanted, double head, Action act)
        {
            act();
            var playhead = Until(() => Playhead(editor), now => Same(now, head, 0.01), 1);
            var selected = Until(() => OfThreeSelected(editor), now => now == wanted, 1);
            (selected == wanted && Same(playhead, head, 0.01) ? kept : lost).Add($"{what}: {selected}, playhead {Seconds(playhead)} s");
        }

        // The empty part of each of the three lanes, with something of another kind selected.
        Timeline.Mark("14: a press on the empty part of another lane");
        var selections = new List<bool>
        {
            SelectItem("StudioSpeed_1", "zoom none, cut none, speed change 2"),
        };
        Step("with the second speed change selected, the zoom lane pressed at 9.5 s, above it", Nothing, 9.5, () => Press(ZoomLane, 9.5));
        var marked = (WaitForLane(editor, SpeedLaneFor(speeds, null), 1, SpeedLane), SpeedSectionShows(editor, "2 speed changes", range: null));
        selections.Add(SelectItem("StudioSpeed_0", "zoom none, cut none, speed change 1"));
        Step("with the first speed change selected, the cut lane pressed at 7.5 s, above it", Nothing, 7.5, () => Press(CutLane, 7.5));
        selections.Add(SelectItem("StudioZoom_0", "zoom 1, cut none, speed change none"));
        Step("with the zoom selected, the speed lane pressed at 4.5 s, below it", Nothing, 4.5, () => Press(SpeedLane, 4.5));
        selections.Add(SelectItem("StudioCut_0", "zoom none, cut 1, speed change none"));
        Step("with the cut selected, the speed lane pressed at 2.5 s, below it", Nothing, 2.5, () => Press(SpeedLane, 2.5));

        // And a block of the speed lane, with a zoom selected: the speed change takes the selection.
        selections.Add(SelectItem("StudioZoom_0", "zoom 1, cut none, speed change none"));
        Step("with the zoom selected, the first speed change pressed at 7.5 s", "zoom none, cut none, speed change 1", 7.5, () => Press(SpeedLane, 7.5));
        var lanes = (LaneText(editor), LaneText(editor, CutLane), LaneText(editor, SpeedLane));
        _report.Check(
            "a press on the empty part of the zoom lane or of the cut lane lets go of a selected speed change, and a press on the empty part of the speed lane lets go of a selected zoom and of a selected cut: whichever lane is pressed, nothing is selected afterwards, and the playhead is at the time that was pressed. "
                + "A press on a speed change's block with a zoom selected selects the speed change and lets go of the zoom",
            selections.All(ok => ok) && lost.Count == 0 && kept.Count == 5 && marked.Item1 == SpeedLaneFor(speeds, null) && marked.Item2 is null
                && !lanes.Item1.Contains('*') && !lanes.Item2.Contains('*') && lanes.Item3 == SpeedLaneFor(speeds, 0) && !CanUndo(),
            lost.Count == 0
                ? $"{string.Join("; ", kept)}; after the first press the speed lane was {marked.Item1} and the Speed panel {marked.Item2 ?? "said \"2 speed changes\" and showed none"}; at the end the speed lane: {lanes.Item3}; Undo enabled {CanUndo()}"
                : $"not as wanted: {string.Join("; ", lost)}");

        // What leaves the selection alone, with the first speed change selected.
        Timeline.Mark("14: the scene lane and the trim bar leave a selected speed change");
        void TakeHandle(string handle, double time)
        {
            OnUi(() => editor.Window.ViewModel.BeginGesture());
            SetSlider(editor, handle, time);
            OnUi(() => editor.Window.ViewModel.EndGesture());
        }

        lost.Clear();
        kept.Clear();
        const string First = "zoom none, cut none, speed change 1";
        var (trimStart, trimEnd) = (SliderValue(editor, "StudioTrimStart"), SliderValue(editor, "StudioTrimEnd"));
        Step("the scene lane pressed at 1.0 s", First, 1.0, () => Press(SceneLane, 1.0));
        Step("the scene lane pressed at 11.0 s, in the other scene", First, 11.0, () => Press(SceneLane, 11.0));
        Step("the trim bar's playhead moved to 6.5 s", First, 6.5, () => SetSlider(editor, "StudioPlayhead", 6.5));
        Step($"the Start handle taken and let go at the {Seconds(trimStart)} s it is at", First, trimStart, () => TakeHandle("StudioTrimStart", trimStart));
        Step($"the End handle taken and let go at the {Seconds(trimEnd)} s it is at", First, trimEnd, () => TakeHandle("StudioTrimEnd", trimEnd));
        var unchanged = Describe(SpeedsOf(editor)) == Describe(speeds) && Describe(CutsOf(editor)) == Describe(cuts) && ZoomsOf(editor).Length == 1 && ScenesOf(editor).Length == 2
            && Same(SliderValue(editor, "StudioTrimStart"), trimStart) && Same(SliderValue(editor, "StudioTrimEnd"), trimEnd);
        _report.Check(
            "a press on the scene lane leaves a selected speed change selected, and so does what the trim bar asks the editor for when it is pressed and when a handle is taken and let go; each moves the playhead, and none changes the project or adds to what can be undone",
            lost.Count == 0 && kept.Count == 5 && !CanUndo() && unchanged && WaitForLane(editor, SpeedLaneFor(speeds, 0), 1, SpeedLane) == SpeedLaneFor(speeds, 0),
            lost.Count == 0 ? $"{string.Join("; ", kept)}; Undo enabled {CanUndo()}; the project as it was opened: {unchanged}" : $"not as wanted: {string.Join("; ", lost)}");
        CloseQuietly(editor);
    }

    // ---------------------------------------------------------------------------------------
    // What a speed change's block looks like
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// How far in from a block's left edge its own pixels begin on one row of a screenshot, in
    /// effective pixels: the first pixel whose brightness is not the lane's own. A block with
    /// corners begins at its edge on every row; one with round ends begins further in near its
    /// top than through its middle.
    /// </summary>
    private static double EdgeOn(Shot shot, double y, double blockLeft, double track, double scale)
    {
        var row = (int)Math.Floor(y);
        var first = (int)Math.Floor(blockLeft - (3 * scale));
        var last = (int)Math.Ceiling(blockLeft + (20 * scale));
        for (var x = first; x <= last; x++)
        {
            if (Math.Abs(Luma(shot.Color(x, row, 0)) - track) > 12)
            {
                return (x - blockLeft) / scale;
            }
        }

        return double.NaN;
    }

    /// <summary>How many pixels of a square of a screenshot are far from one brightness: what is drawn there over a flat fill.</summary>
    private static int InkIn(Shot shot, double centerX, double centerY, double half, double fill)
    {
        var count = 0;
        for (var y = (int)Math.Floor(centerY - half); y <= (int)Math.Ceiling(centerY + half); y++)
        {
            for (var x = (int)Math.Floor(centerX - half); x <= (int)Math.Ceiling(centerX + half); x++)
            {
                count += Math.Abs(Luma(shot.Color(x, y, 0)) - fill) > 40 ? 1 : 0;
            }
        }

        return count;
    }

    /// <summary>
    /// How many pixels differ between two squares of a screenshot, each taken around its own
    /// middle, once each pixel is told to be drawn on or not: two marks that are the same
    /// picture differ in none, whatever the two fills are.
    /// </summary>
    private static int InkDiffers(Shot shot, (double X, double Y) a, double fillA, (double X, double Y) b, double fillB, double half)
    {
        var count = 0;
        for (var dy = -half; dy <= half; dy++)
        {
            for (var dx = -half; dx <= half; dx++)
            {
                var inA = Math.Abs(Luma(shot.Color((int)Math.Floor(a.X + dx), (int)Math.Floor(a.Y + dy), 0)) - fillA) > 40;
                var inB = Math.Abs(Luma(shot.Color((int)Math.Floor(b.X + dx), (int)Math.Floor(b.Y + dy), 0)) - fillB) > 40;
                count += inA != inB ? 1 : 0;
            }
        }

        return count;
    }

    /// <summary>
    /// A speed change has to look like neither a zoom nor a cut, by more than its colour, and
    /// the selected one has to differ from the others by more than its colour. Read from the
    /// pixels of a window in one theme: the block has round ends where a zoom's has corners, it
    /// is one flat colour where a cut's is hatched and its outline one line where a cut's is
    /// dashed, the mark for faster is another picture than the mark for slower, and the
    /// selected one's outline is twice as thick.
    /// </summary>
    /// <remarks>
    /// What these readings cannot tell: the colours themselves, so a block in a colour that is
    /// hard to see on a real screen would pass as long as it differs from the lane by twelve
    /// levels of brightness; and whether the mark for faster looks like fast forward, or the
    /// one for slower like a tortoise, only that the two are not the same picture. Contrast
    /// themes are not seen.
    /// </remarks>
    private void SpeedBlockLooks(AppTheme theme, string name)
    {
        Timeline.Mark($"14: what a speed change looks like, {name}");
        _services.Settings.Theme = theme;
        try
        {
            // Two speed changes of three seconds, a faster and a slower one, which leaves a
            // hundred pixels of each clear of what is written in its middle; two of half a
            // second, which carry their mark and nothing else; and a zoom and a cut of three
            // seconds to hold them against.
            StudioSpeedRange[] speeds = [Speed(0.5, 3.5), Speed(4, 7, 0.5), Speed(8, 8.5), Speed(9, 9.5, 0.5)];
            var folder = NewScreenProject($"Speed looks, {name}", p => p with { Edits = p.Edits with { Speed = speeds, Cuts = [Cut(4, 7)] }, Zooms = [PointZoom(0.5, 3.5, 0.5, 0.5)] });
            if (OpenReady(folder, $"speed looks, {name}") is not { } editor)
            {
                return;
            }

            SetSlider(editor, "StudioPlayhead", 11.0);
            WaitForLane(editor, SpeedLaneFor(speeds, null), 2, SpeedLane);
            Thread.Sleep(500);
            var scale = editor.Scale;

            // A block in a screenshot: where it is, the lane's own brightness next to it, and four
            // rows of 72 effective pixels that start clear of a round end: through its outline,
            // just under it, well inside it, and through its middle.
            (Shot Shot, double Left, double Top, double Width, double Height, double Track, double[] Outline, double[] Under, double[] Inside, double[] Middle, Rgb Fill)? Read(Shot? shot, string id, string lane)
            {
                if (shot is null || Find(editor, id, 0.5)?.Bounds is not { Width: > 0 } bounds || Find(editor, lane, 0.5)?.Bounds is not { Width: > 0 } laneBounds)
                {
                    return null;
                }

                var (left, top) = ((double)bounds.X - shot.ScreenX, (double)bounds.Y - shot.ScreenY);
                var (from, to) = (left + (14 * scale), left + (86 * scale));
                var middle = top + (bounds.Height / 2.0);

                // The lane itself, where it has no block: left of the first one, on the block's middle row.
                var track = Luma(shot.Color(laneBounds.X - shot.ScreenX + (8 * scale), middle, 1));
                return (shot, left, top, bounds.Width, bounds.Height, track,
                    RowOf(shot, top + (0.5 * scale), from, to), RowOf(shot, top + (1.5 * scale), from, to), RowOf(shot, top + (4.5 * scale), from, to), RowOf(shot, middle, from, to),
                    shot.Color((from + to) / 2, middle, 1));
            }

            string? Save(Shot? shot, string fileName)
            {
                if (shot is null)
                {
                    return null;
                }

                var path = Path.Combine(_output, fileName);
                shot.Save(path);
                _report.Line($"  saved {path}");
                return path;
            }

            var plainShot = editor.Camera.Take();
            Save(plainShot, $"speed-looks-{name}.png");
            var faster = Read(plainShot, "StudioSpeed_0", SpeedLane);
            var slower = Read(plainShot, "StudioSpeed_1", SpeedLane);
            var zoom = Read(plainShot, "StudioZoom_0", ZoomLane);
            var cut = Read(plainShot, "StudioCut_0", CutLane);
            var fasterMark = Find(editor, "StudioSpeed_2", 0.5)?.Bounds ?? default;
            var slowerMark = Find(editor, "StudioSpeed_3", 0.5)?.Bounds ?? default;
            Find(editor, "StudioSpeed_0")?.Select();
            WaitForLane(editor, SpeedLaneFor(speeds, 0), 2, SpeedLane);
            Thread.Sleep(500);
            var selectedShot = editor.Camera.Take();
            Save(selectedShot, $"speed-looks-{name}-selected.png");
            var selected = Read(selectedShot, "StudioSpeed_0", SpeedLane);
            if (faster is not { } fast || slower is not { } slow || zoom is not { } flat || cut is not { } hatched || selected is not { } marked || plainShot is null || fasterMark.Width <= 0 || slowerMark.Width <= 0)
            {
                _report.Check($"a speed change's block in the {name} theme can be pictured", false, "no screenshot, or no block");
                CloseQuietly(editor);
                return;
            }

            // The shape: how much further in the block begins a pixel and a half under its top than through its middle.
            double RoundEnd((Shot Shot, double Left, double Top, double Width, double Height, double Track, double[] Outline, double[] Under, double[] Inside, double[] Middle, Rgb Fill) block) =>
                EdgeOn(block.Shot, block.Top + (1.5 * scale), block.Left, block.Track, scale) - EdgeOn(block.Shot, block.Top + (block.Height / 2.0), block.Left, block.Track, scale);
            var ends = (Faster: RoundEnd(fast), Slower: RoundEnd(slow), Zoom: RoundEnd(flat), Selected: RoundEnd(marked));

            // The fill: one colour, apart from the lane, and no colour of its own; the outline: one line.
            static double Chroma(Rgb color) => Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
            var fill = (Stripes: Alternations(fast.Middle), Spread: SpreadOf(fast.Middle), Apart: Math.Abs(fast.Middle.Average() - fast.Track), Chroma: Chroma(fast.Fill), SlowerStripes: Alternations(slow.Middle), SlowerSpread: SpreadOf(slow.Middle));
            var outline = (Dashes: Alternations(fast.Outline, fast.Track), Apart: Math.Abs(fast.Outline.Average() - fast.Track), SlowerDashes: Alternations(slow.Outline, slow.Track));
            var ofTheCut = (Stripes: Alternations(hatched.Middle), Dashes: Alternations(hatched.Outline, hatched.Track));
            _report.Check(
                $"in the {name} theme a speed change's block is told from a zoom's and from a cut's by more than its colour: its ends are round where a zoom's block has corners, it is one flat colour where a cut's is hatched, and its outline is one line where a cut's is dashed; its colour is a shade of the lane's own and not the accent colour",
                ends.Faster >= 2.5 && ends.Slower >= 2.5 && ends.Zoom <= 2 && fill.Stripes == 0 && fill.Spread < 12 && fill.SlowerStripes == 0 && fill.SlowerSpread < 12 && fill.Apart > 12 && fill.Chroma < 12
                    && outline.Dashes == 0 && outline.SlowerDashes == 0 && outline.Apart > 20 && ofTheCut.Stripes >= 8 && ofTheCut.Dashes >= 20,
                string.Create(CultureInfo.InvariantCulture, $"a pixel and a half under its top the faster one's block begins {ends.Faster:0.0} further in than through its middle and the slower one's {ends.Slower:0.0} (4.7 for ends that are half circles of a block 20 high), and the zoom's {ends.Zoom:0.0} (0.9 for corners with a radius of 4); ")
                    + string.Create(CultureInfo.InvariantCulture, $"along 72 pixels through its middle its brightness changes sides {fill.Stripes} times, its pixels within {fill.Spread:0} levels of each other and {fill.Apart:0} from the lane's, its colour {fast.Fill} with {fill.Chroma:0} between its strongest and its weakest channel (the zoom's: {flat.Fill}, {Chroma(flat.Fill):0}); ")
                    + string.Create(CultureInfo.InvariantCulture, $"along its outline the brightness changes sides {outline.Dashes} times, {outline.Apart:0} levels from the lane's; the cut's block: {ofTheCut.Stripes} times through its middle and {ofTheCut.Dashes} along its outline"));

            // The marks: each of the two narrow blocks has something drawn in its middle, and the two are not the same picture.
            var half = 8 * scale;
            Shot picture = plainShot;
            (double X, double Y) MiddleOfBlock((int X, int Y, int Width, int Height) bounds) => (bounds.X - picture.ScreenX + (bounds.Width / 2.0), bounds.Y - picture.ScreenY + (bounds.Height / 2.0));
            var (fasterAt, slowerAt) = (MiddleOfBlock(fasterMark), MiddleOfBlock(slowerMark));

            // The fill of each narrow block, read between its mark and its round end.
            double FillOf((int X, int Y, int Width, int Height) bounds) => Luma(picture.Color(bounds.X - picture.ScreenX + (bounds.Width / 2.0) - (13 * scale), bounds.Y - picture.ScreenY + (bounds.Height / 2.0), 1));
            var (fasterFill, slowerFill) = (FillOf(fasterMark), FillOf(slowerMark));
            var ink = (Faster: InkIn(picture, fasterAt.X, fasterAt.Y, half, fasterFill), Slower: InkIn(picture, slowerAt.X, slowerAt.Y, half, slowerFill), Differ: InkDiffers(picture, fasterAt, fasterFill, slowerAt, slowerFill, half));
            var shown = (Faster: SpeedMarks(editor, 2), Slower: SpeedMarks(editor, 3));
            _report.Check(
                $"in the {name} theme a block that plays faster and one that plays slower carry different marks: something is drawn in the middle of each, and the two are not the same picture",
                ink.Faster >= 10 * scale * scale && ink.Slower >= 10 * scale * scale && ink.Differ >= 12 * scale * scale
                    && shown.Faster.Faster && !shown.Faster.Slower && shown.Faster.Rate.Length == 0 && shown.Slower.Slower && !shown.Slower.Faster && shown.Slower.Rate.Length == 0,
                string.Create(CultureInfo.InvariantCulture, $"in a square of 16 around the middle of each half-second block, {ink.Faster} pixels of the faster one's and {ink.Slower} of the slower one's are far from the block's own brightness, and {ink.Differ} are drawn on in the one and not in the other; ")
                    + $"the blocks are {F(fasterMark.Width / scale, "0.#")} and {F(slowerMark.Width / scale, "0.#")} wide, too narrow for a rate; the faster one shows its mark for faster: {shown.Faster.Faster}, the slower one its mark for slower: {shown.Slower.Slower}");

            // Selected: the row under the outline's own row belongs to the outline, which is then two thick; not selected, it belongs to the fill.
            static double Mean(double[] row) => row.Length == 0 ? double.NaN : row.Average();
            var thin = (Outline: Mean(fast.Outline), Under: Mean(fast.Under), Inside: Mean(fast.Inside));
            var thick = (Outline: Mean(marked.Outline), Under: Mean(marked.Under), Inside: Mean(marked.Inside));
            var isThin = Math.Abs(thin.Outline - thin.Inside) > 20 && Math.Abs(thin.Under - thin.Inside) < Math.Abs(thin.Under - thin.Outline);
            var isThick = Math.Abs(thick.Outline - thick.Inside) > 16 && Math.Abs(thick.Under - thick.Outline) < Math.Abs(thick.Under - thick.Inside);
            _report.Check(
                $"in the {name} theme the selected speed change differs by more than its colour: its outline is twice as thick as that of the others, and it keeps its round ends",
                isThin && isThick && SpreadOf(marked.Outline) < 24 && SpreadOf(marked.Under) < 24 && Math.Abs(thick.Outline - marked.Track) > 60 && ends.Selected >= 2.5 && Alternations(marked.Middle) == 0,
                string.Create(CultureInfo.InvariantCulture, $"not selected, the row of the outline is at {thin.Outline:0}, the row under it at {thin.Under:0} and a row well inside at {thin.Inside:0}: the row under the outline is the fill's; ")
                    + string.Create(CultureInfo.InvariantCulture, $"selected, they are at {thick.Outline:0}, {thick.Under:0} and {thick.Inside:0}: the row under the outline is the outline's, {Math.Abs(thick.Outline - marked.Track):0} levels from the lane's {marked.Track:0}; ")
                    + string.Create(CultureInfo.InvariantCulture, $"the selected block begins {ends.Selected:0.0} further in near its top than through its middle"));
            CloseQuietly(editor);
        }
        finally
        {
            _services.Settings.Theme = AppTheme.Default;
        }
    }

    // ---------------------------------------------------------------------------------------
    // The trim bar
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The stretches of the trim bar between its two handles that are tinted, read along one
    /// row of a screenshot, as times. As <see cref="KeptParts"/>, with the row chosen: a row near
    /// the top of the bar runs through the round corners of every tinted part, so two parts that
    /// lie side by side read as two there, with the bar's own colour between their corners.
    /// </summary>
    /// <remarks>
    /// On such a row the pixel at each end of a part is only partly tinted: about a fifth, a
    /// pixel and a half under the top of a corner with a radius of four. A pixel therefore counts
    /// as tinted when it is more than half as far from the bar's own colour as the most tinted
    /// pixel of the row. Measured against a fixed distance, as <see cref="KeptParts"/> does on a
    /// row that meets no corner, a fifth of a strong tint would still pass for tinted, and the
    /// notch between two parts that touch would be read as none.
    /// </remarks>
    /// <param name="down">How far under the top of the bar the row is, in effective pixels.</param>
    private (List<(double Start, double End)> Kept, string Colours)? KeptPartsAlong(Editor editor, double down)
    {
        // The playhead's line is drawn over the bar, so it is put before the Start handle, out of what is read.
        SetSlider(editor, "StudioPlayhead", 0.2);
        Thread.Sleep(350);
        if (editor.Camera.Take() is not { } shot || Find(editor, "StudioTrimBar")?.Bounds is not { Width: > 0 } bar || Find(editor, "StudioTrimStart")?.Bounds is not { } start || Find(editor, "StudioTrimEnd")?.Bounds is not { } end)
        {
            return null;
        }

        // A handle is 12 effective pixels wide, and the times run over the bar less both handles.
        var handle = 12 * editor.Scale;
        var usable = bar.Width - (2 * handle);
        double TimeAt(double x) => (x + shot.ScreenX - bar.X - handle) / usable * RecordingLength;
        var y = bar.Y - shot.ScreenY + (down * editor.Scale);

        // The bar's own colour on that row, where nothing is kept: just before the Start handle.
        var own = shot.Color(start.X - shot.ScreenX - handle, y, 0);
        var (from, to) = ((int)Math.Ceiling(start.X - shot.ScreenX + handle), (int)Math.Floor((double)end.X - shot.ScreenX) - 1);
        var full = Enumerable.Range(from, Math.Max(0, to - from + 1)).Select(x => shot.Color(x, y, 0).Distance(own)).DefaultIfEmpty(0).Max();
        var level = Math.Max(14, full / 2);
        var kept = new List<(double Start, double End)>();
        int? runStart = null;
        Rgb? tint = null;
        for (var x = from; x <= to + 1; x++)
        {
            var isTinted = x <= to && shot.Color(x, y, 0).Distance(own) > level;
            if (isTinted && runStart is null)
            {
                runStart = x;
            }
            else if (!isTinted && runStart is { } first)
            {
                // A pixel or two next to a handle is the soft edge of the handle, and no part of its own.
                if (x - first >= 3)
                {
                    kept.Add((TimeAt(first), TimeAt(x)));
                    tint ??= shot.Color((first + x) / 2.0, y, 0);
                }

                runStart = null;
            }
        }

        return (kept, $"the bar is {own} and what is kept {(tint is { } colour ? colour.ToString() : "nowhere")}, a pixel counting as tinted from {F(level, "0")} levels from the bar's own colour");
    }

    /// <summary>
    /// The trim bar of a video with speed changes: a speed change leaves nothing out, so the
    /// tinted range goes on through it in one piece, and only a cut leaves a gap. Read from the
    /// pixels of the bar, along its middle and along a row that runs through the round corners
    /// of its tinted parts.
    /// </summary>
    /// <remarks>
    /// The editor hands the bar the stretches the video keeps, which a speed change does not
    /// divide. That the bar draws stretches that touch as one is therefore checked a second
    /// time, with the same video handed to the bar in the pieces the time map plays it in, one
    /// for each stretch at one rate: those touch where the speed changes. That call is made by
    /// the tool, not by the window.
    /// </remarks>
    private void TrimBarWithSpeedChanges()
    {
        Timeline.Mark("14: the trim bar of a video with speed changes");
        var edits = new StudioEdits { TrimStart = 1, TrimEnd = 11, Cuts = [Cut(6, 7.5)], Speed = [Speed(2, 4), Speed(8, 9, 0.5)] };
        var folder = NewScreenProject("Kept parts and speed", p => p with { Edits = edits });
        if (OpenReady(folder, "kept parts and speed") is not { } editor)
        {
            return;
        }

        // Two pixels of the bar, as seconds.
        var bar = Find(editor, "StudioTrimBar")?.Bounds ?? default;
        var within = 2.0 / (bar.Width - (24 * editor.Scale)) * RecordingLength;
        (double Start, double End)[] wanted = [(1, 6), (7.5, 11)];

        // The video: 1 + 1 + 2 seconds before the cut, and 0.5 + 2 + 2 after it.
        var time = TimeText(editor);
        var middle = KeptParts(editor);
        var top = KeptPartsAlong(editor, 1.5);
        var drawn = OnUi(() => Descendant<StudioTrimBar>(editor.Window.Content, "StudioTrimBar")?.KeptPartCount ?? -1);
        var (middleWrong, topWrong) = (KeptPartsAre(middle?.Kept, wanted, within), KeptPartsAre(top?.Kept, wanted, within));
        _report.Check(
            "the trim bar of a video that starts at 1.0 s and ends at 11.0 s, with a cut from 6.0 to 7.5 s and speed changes from 2.0 to 4.0 s and from 8.0 to 9.0 s, is tinted in two parts, with a gap where the cut is and none where the speed changes: along its middle, and along a row a pixel and a half under its top, which runs through the round corners of each part",
            middleWrong is null && topWrong is null && drawn == 2 && time.EndsWith("/ 0:08.5", StringComparison.Ordinal),
            $"along the middle: {middleWrong ?? "tinted from 1.0 to 6.0 s and from 7.5 to 11.0 s"}; near the top: {topWrong ?? "the same two parts"}, each end within {F(within, "0.00")} s, which is two pixels; the bar draws {drawn} part(s); {top?.Colours}; the time reads \"{time}\"");

        // The same video, handed to the bar in the six pieces the time map plays it in. They touch at 2.0, 4.0, 8.0 and 9.0 s.
        var pieces = new StudioTimeMap(RecordingLength, edits).Pieces;
        var touching = pieces.Zip(pieces.Skip(1), (a, b) => a.End == b.Start).Count(touch => touch);
        var handed = OnUi(() =>
        {
            if (Descendant<StudioTrimBar>(editor.Window.Content, "StudioTrimBar") is not { } trimBar)
            {
                return -1;
            }

            var viewModel = editor.Window.ViewModel;
            trimBar.Update(
                viewModel.SourceDuration,
                viewModel.TrimStart,
                viewModel.TrimEnd,
                [.. pieces.Select(piece => new StudioTimeSegment(piece.Start, piece.End))],
                viewModel.Playhead,
                viewModel.FrameDuration,
                viewModel.TrimStep,
                viewModel.TrimStartText,
                viewModel.TrimEndText,
                viewModel.PlayheadText);
            return trimBar.KeptPartCount;
        });
        var topOfPieces = KeptPartsAlong(editor, 1.5);
        var piecesWrong = KeptPartsAre(topOfPieces?.Kept, wanted, within);
        _report.Check(
            "handed the same video as six stretches, four of which touch the one before them, the bar draws those that touch as one: two parts, and along the row through their round corners no notch where a speed change starts or ends",
            pieces.Count == 6 && touching == 4 && handed == 2 && piecesWrong is null,
            $"the time map plays the video in {pieces.Count} pieces, {touching} of which start where the one before ends; handed those, the bar draws {handed} part(s); near the top: {piecesWrong ?? "tinted from 1.0 to 6.0 s and from 7.5 to 11.0 s, without a break"}");

        // A speed change made in the window changes nothing of what the bar shows, and a cut inside it leaves its gap.
        SetSlider(editor, "StudioPlayhead", 4.5);
        Key(editor, StudioShortcutKey.R);
        Until(() => SpeedsOf(editor).Length, count => count == 3, 1.5);
        var afterSpeed = KeptPartsAre(KeptPartsAlong(editor, 1.5)?.Kept, wanted, within);
        SetSlider(editor, "StudioPlayhead", 3.0);
        Key(editor, StudioShortcutKey.X);
        Until(() => CutsOf(editor).Length, count => count == 2, 1.5);
        var afterCut = KeptPartsAre(KeptPartsAlong(editor, 1.5)?.Kept, [(1, 3), (4, 6), (7.5, 11)], within);
        _report.Check(
            "a speed change made in the window, with R at 4.5 s, leaves the tinted parts as they are; a cut made with X at 3.0 s, inside the first speed change, leaves a gap there",
            afterSpeed is null && afterCut is null,
            $"after R: {afterSpeed ?? "the same two parts"}; after X: {afterCut ?? "three parts, with a gap from 3.0 to 4.0 s"}");
        CloseQuietly(editor);
    }

    /// <summary>
    /// A faster stretch makes the video shorter, so a trim can leave less than a tenth of a
    /// second of video although more than that of the recording is left. Such a trim is refused
    /// as a whole, and so is a rate that would do the same: the handle and the choice of rate
    /// have to show what the editor holds.
    /// </summary>
    private void TrimThatASpeedChangeDoesNotAllow()
    {
        Timeline.Mark("14: a trim and a rate that a speed change does not allow");

        // The whole recording at eight times the speed: a second and a half of video.
        var folder = NewScreenProject("Refused trims, speed", p => p with { Edits = p.Edits with { Speed = [Speed(0, 12, 8)] } });
        if (OpenReady(folder, "refused trims, speed") is not { } editor)
        {
            return;
        }

        using var heard = UiaEvents.Listen(_uia, editor.Root);
        double Start() => SliderValue(editor, "StudioTrimStart");
        double End() => SliderValue(editor, "StudioTrimEnd");
        (double Start, double End) Held() => OnUi(() => (editor.Window.ViewModel.TrimStart, editor.Window.ViewModel.TrimEnd));

        // How far each handle is drawn from where its time puts it, in pixels.
        (double Start, double End) Apart()
        {
            var bar = Find(editor, "StudioTrimBar")?.Bounds ?? default;
            var (start, end) = (Find(editor, "StudioTrimStart"), Find(editor, "StudioTrimEnd"));
            var handle = 12 * editor.Scale;
            var usable = bar.Width - (2 * handle);
            double At(double time) => bar.X + (time / RecordingLength * usable);
            return (Math.Abs((start?.Bounds.X ?? 0) - At(start?.Range?.Value ?? 0)), Math.Abs((end?.Bounds.X ?? 0) - (At(end?.Range?.Value ?? 0) + handle)));
        }

        // Granted: the last second of the recording, an eighth of a second of video.
        var timeOpened = TimeText(editor);
        var startSet = SetSlider(editor, "StudioTrimStart", 11.0);
        var started = Until(Start, value => value == 11.0, 1.5);
        var timeGranted = TimeText(editor);
        _report.Check(
            "with the whole recording at eight times the speed, which is a second and a half of video, Start set to 11.0 s is granted: an eighth of a second of video is left",
            timeOpened.EndsWith("/ 0:01.5", StringComparison.Ordinal) && startSet && started == 11.0 && timeGranted.EndsWith("/ 0:00.1", StringComparison.Ordinal),
            $"opened, the time reads \"{timeOpened}\"; Start {Seconds(started)} s; the time reads \"{timeGranted}\"");

        // Refused: End at 11.5 s would leave half a second of the recording, which is a sixteenth of a second of video.
        var endSet = SetSlider(editor, "StudioTrimEnd", 11.5);
        Thread.Sleep(300);
        var refused = (End(), Held(), Find(editor, "StudioTrimEnd")?.ValueText, TimeText(editor), Apart().End);
        var startAsked = SetSlider(editor, "StudioTrimStart", 11.4);
        Thread.Sleep(300);
        var startStays = (Start(), Held(), Apart().Start);
        _report.Check(
            "End asked to go to 11.5 s, which would leave half a second of the recording but only a sixteenth of a second of video, is refused as a whole: the handle reports 12.0 s and is drawn where 12.0 s is; and so is Start asked to go to 11.4 s",
            refused.Item1 == RecordingLength && refused.Item2 == (11.0, RecordingLength) && refused.Item3 == "12.0 seconds" && refused.Item4.EndsWith("/ 0:00.1", StringComparison.Ordinal) && refused.Item5 <= 2
                && startStays.Item1 == 11.0 && startStays.Item2 == (11.0, RecordingLength) && startStays.Item3 <= 2,
            $"the request for End was {(endSet ? "taken" : "not taken")}; End reports {Seconds(refused.Item1)} s, \"{refused.Item3}\", and is drawn {F(refused.Item5, "0.#")} px from where that is; the editor holds {Seconds(refused.Item2.Start)} to {Seconds(refused.Item2.End)} s; the time reads \"{refused.Item4}\"; "
                + $"the request for Start was {(startAsked ? "taken" : "not taken")}; Start reports {Seconds(startStays.Item1)} s and is drawn {F(startStays.Item3, "0.#")} px from where that is");

        // At four times the speed the same half second is an eighth of a second of video, and the trim is granted.
        Find(editor, "StudioSpeed_0")?.Select();
        Until(() => SelectedSpeedOf(editor), index => index == 0, 1);
        var eightChosen = RateChosen(editor, 5);
        var four = Find(editor, "StudioSpeedRate_4")?.Select() ?? false;
        var rate = Until(() => SpeedsOf(editor)[0].Rate, value => value == 4, 1.5);
        SetSlider(editor, "StudioTrimEnd", 11.5);
        var ended = Until(End, value => value == 11.5, 1.5);

        // Eight times the speed again would leave a sixteenth of a second: the rate is refused, and the choice goes back to the rate it has.
        var mark = heard.Mark();
        var eight = Find(editor, "StudioSpeedRate_5")?.Select() ?? false;
        var back = RateChosen(editor, 4);
        var told = heard.WaitFor(mark, "notification", e => false, 0.5).Where(e => RateNames.Contains(e.Text)).Select(e => e.Text).ToArray();
        var held = SpeedsOf(editor)[0].Rate;
        var index = OnUi(() => editor.Window.ViewModel.SpeedRateIndex);
        _report.Check(
            "a rate is refused the same way: with the speed change at four times the speed, End at 11.5 s is granted; eight times the speed would then leave a sixteenth of a second of video, so choosing it changes nothing, is not read out, and the choice shows four times the speed again",
            eightChosen is null && four && rate == 4 && ended == 11.5 && eight && back is null && told.Length == 0 && held == 4 && index == 4,
            $"opened: {eightChosen ?? "8× is the chosen one"}; 4× chosen: rate {F(rate)}, End {Seconds(ended)} s; 8× chosen again: the speed change plays at {F(held)}×, {back ?? "4× is the chosen one"} (the view model says {index}); {(told.Length == 0 ? "nothing read out" : "read out: " + string.Join(", ", told))}");
        CloseQuietly(editor);
    }
}
