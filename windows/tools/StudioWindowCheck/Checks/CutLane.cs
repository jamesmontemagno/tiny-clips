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

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 13, continued. The cut lane under a pointer, what a press on each row of the timeline does to
// what is selected, what a cut's block looks like, the gaps the cuts leave in the trim bar, and
// a trim that the cuts do not allow.
internal sealed partial class WindowChecks
{
    /// <summary>
    /// What a press and a drag on the cut lane do, by calling what the lane's pointer handlers
    /// call with a place along the lane. No pointer is moved or pressed, so which element a real
    /// pointer would land on, and that the lane keeps the pointer while it is down, is not seen.
    /// </summary>
    private void CutLaneUnderAPointer()
    {
        Timeline.Mark("13: presses and drags on the cut lane");

        // Four cuts of four widths: one wide enough for its length to be written on it, one for
        // the scissors alone, a long one, and one too narrow for either and for ends of its own.
        StudioTimeRange[] opened = [Cut(1, 2), Cut(4, 4.45), Cut(6, 7.5), Cut(11, 11.2)];
        var folder = NewScreenProject("Cut drags", p => p with { Edits = p.Edits with { Cuts = opened }, Zooms = [PointZoom(8, 10, 0.5, 0.5)] });
        if (OpenReady(folder, "cut drags") is not { } editor)
        {
            return;
        }

        var problem = CutsAre(editor, opened, null);
        if (problem is not null)
        {
            _report.Check("the editor opens a project with four cuts, and selects none", false, problem);
            CloseQuietly(editor);
            return;
        }

        // The lane in effective pixels, which is what its pointer handlers work in.
        var laneBounds = Find(editor, CutLane)?.Bounds ?? default;
        var laneWidth = laneBounds.Width / editor.Scale;
        var secondsPerPixel = RecordingLength / (laneWidth - 24);
        double XOf(double time) => 12 + (time / secondsPerPixel);
        (double Left, double Width) Block(int index)
        {
            var bounds = Find(editor, $"StudioCut_{index}")?.Bounds ?? default;
            return ((bounds.X - laneBounds.X) / editor.Scale, bounds.Width / editor.Scale);
        }

        // A block once it has been laid out where it is wanted, or as it is when the time is up.
        (double Left, double Width) BlockAt(int index, double left) => Until(() => Block(index), block => Math.Abs(block.Left - left) <= 1, 1);
        void Press(double x) => OnUi(() => LaneOf(editor, CutLane)!.PressAt(x));
        void Drag(double x) => OnUi(() => LaneOf(editor, CutLane)!.DragTo(x));
        void LetGo() => OnUi(() => LaneOf(editor, CutLane)!.EndPress());
        bool CanUndo() => Find(editor, "StudioUndoButton", 0.5)?.IsEnabled == true;
        StudioTimeRange CutAt(int index) => CutsOf(editor)[index];
        string Selection() => WhatIsSelected(editor);

        // Each block runs from where its cut starts to where it ends, and says what fits on it.
        var blocks = Enumerable.Range(0, 4).Select(Block).ToArray();
        var geometry = blocks.Select((block, index) => Math.Abs(block.Left - XOf(opened[index].Start)) <= 1 && Math.Abs(block.Width - ((opened[index].End - opened[index].Start) / secondsPerPixel)) <= 1).ToArray();
        var marks = OnUi(() => Enumerable.Range(0, 4).Select(index =>
        {
            var block = Descendant<StudioCutBlock>(editor.Window.Content, $"StudioCut_{index}");
            var scissors = block?.FindName("Plate") is FrameworkElement { Visibility: Visibility.Visible };
            var length = block?.FindName("LengthText") is Microsoft.UI.Xaml.Controls.TextBlock { Visibility: Visibility.Visible } text && scissors ? text.Text : string.Empty;
            return (Scissors: scissors, Length: length);
        }).ToArray());

        // The scissors from 26 wide, the length from 62: the four cuts are chosen to be on each side of both.
        string[] lengths = ["1.0 s", string.Empty, "1.5 s", string.Empty];
        bool[] scissors = [true, true, true, false];
        _report.Check(
            "each cut's block runs from where the cut starts to where it ends; one that is at least 26 wide carries the scissors, and one that is at least 62 wide its length as well, such as \"1.0 s\"",
            geometry.All(ok => ok) && marks.Select(mark => mark.Scissors).SequenceEqual(scissors) && marks.Select(mark => mark.Length).SequenceEqual(lengths)
                && blocks[0].Width >= 62 && blocks[1].Width is >= 26 and < 62 && blocks[3].Width < 24,
            $"the lane is {F(laneWidth, "0.#")} wide; the blocks: {string.Join(", ", blocks.Select((block, index) => $"{F(block.Width, "0.#")} wide at {F(block.Left, "0.#")} (wanted {F((opened[index].End - opened[index].Start) / secondsPerPixel, "0.#")} at {F(XOf(opened[index].Start), "0.#")})"))}; "
                + $"scissors: {string.Join(", ", marks.Select(mark => mark.Scissors))}; written on them: {string.Join(", ", marks.Select(mark => $"\"{mark.Length}\""))}");

        // A press in the middle of a block, with a zoom selected.
        Timeline.Mark("13: a press on a cut, and on the empty lane");
        Find(editor, "StudioZoom_0")?.Select();
        Until(() => SelectedZoomOf(editor), index => index == 0, 1);
        Press(XOf(6.7));
        LetGo();
        var pressed = (Selection(), Playhead(editor), CanUndo(), Describe(CutsOf(editor)));
        var laneAfterPress = WaitForLane(editor, CutLaneFor(opened, 2), 2, CutLane);
        var zoomLaneAfterPress = LaneText(editor);
        _report.Check(
            "a press on a cut's block selects the cut, which lets go of the selected zoom, and moves the playhead to the time that was pressed; no cut changes, and nothing is added to what can be undone",
            pressed.Item1 == "cut 3, zoom none" && Same(pressed.Item2, 6.7, 0.01) && !pressed.Item3 && pressed.Item4 == Describe(opened) && laneAfterPress == CutLaneFor(opened, 2) && !zoomLaneAfterPress.Contains('*'),
            $"selected: {pressed.Item1}; playhead {Seconds(pressed.Item2)} s; Undo enabled {pressed.Item3}; the cut lane: {laneAfterPress}; the zoom lane: {zoomLaneAfterPress}");

        // A press on the empty lane, with the cut selected: it lets go of the cut. And with the zoom selected: of the zoom.
        Press(XOf(9.0));
        LetGo();
        var empty = (Selection(), Playhead(editor));
        Find(editor, "StudioZoom_0")?.Select();
        Until(() => SelectedZoomOf(editor), index => index == 0, 1);
        Press(XOf(3.0));
        var emptyWithZoom = (Selection(), Playhead(editor));

        // Dragged on from there, over two cuts: the playhead goes along, and nothing is taken hold of.
        Drag(XOf(3.0) + 1);
        var afterOne = Playhead(editor);
        Drag(XOf(7.0));
        var over = (Playhead(editor), Selection());
        Drag(-40);
        var beforeTheStart = Playhead(editor);
        LetGo();
        _report.Check(
            "a press on the empty lane moves the playhead there and selects nothing: it lets go of the selected cut, and of the selected zoom as well; dragged on, it moves the playhead along from the first pixel, over the cuts and no further than the start of the recording, and takes hold of none",
            empty.Item1 == "cut none, zoom none" && Same(empty.Item2, 9.0, 0.01) && emptyWithZoom.Item1 == "cut none, zoom none" && Same(emptyWithZoom.Item2, 3.0, 0.01)
                && Same(afterOne, 3.0 + secondsPerPixel, 0.005) && Same(over.Item1, 7.0, 0.01) && over.Item2 == "cut none, zoom none" && beforeTheStart == 0 && !CanUndo() && Describe(CutsOf(editor)) == Describe(opened),
            $"pressed at 9.0 s with a cut selected: {empty.Item1}, playhead {Seconds(empty.Item2)} s; pressed at 3.0 s with a zoom selected: {emptyWithZoom.Item1}, playhead {Seconds(emptyWithZoom.Item2)} s; "
                + $"dragged by 1: {Seconds(afterOne)} s; dragged to 7.0 s: {Seconds(over.Item1)} s, {over.Item2}; dragged past the left end: {Seconds(beforeTheStart)} s; Undo enabled {CanUndo()}");

        // The body of the third cut. A press that moves less than 3 is a press.
        Timeline.Mark("13: dragging a cut");
        var grab = XOf(6.5);
        Press(grab);
        Drag(grab + 2);
        LetGo();
        var still = (Describe(CutAt(2)), CanUndo(), SelectedCutOf(editor));
        Press(grab);
        Drag(grab + 60);
        var half = CutAt(2);
        Drag(grab + 120);
        LetGo();
        var whole = CutAt(2);
        var by = 120 * secondsPerPixel;
        var headAfterMove = Playhead(editor);
        var (movedLeft, movedWidth) = BlockAt(2, XOf(6) + 120);
        Invoke(editor, "StudioUndoButton");
        var undone = Until(() => Describe(CutAt(2)), now => now == Describe(opened[2]), 1);
        var nothingLeft = Until(() => !CanUndo(), nothing => nothing, 1);
        var selectedAfterUndo = SelectedCutOf(editor);
        Invoke(editor, "StudioRedoButton");
        var redone = Until(() => CutAt(2), now => Same(now.Start, 6 + by), 1);
        _report.Check(
            "a press on a cut that moves less than 3 changes nothing. Dragged, the middle of a block moves the whole cut and keeps its length: its block follows, the playhead stays where it was pressed, and the whole drag is one undo step, through which the cut stays selected",
            still.Item1 == Describe(opened[2]) && !still.Item2 && still.Item3 == 2
                && Same(half.Start, 6 + (by / 2)) && Same(half.End, 7.5 + (by / 2)) && Same(whole.Start, 6 + by) && Same(whole.End, 7.5 + by) && Same(headAfterMove, 6.5, 0.01)
                && Math.Abs(movedLeft - (XOf(6) + 120)) <= 1 && Math.Abs(movedWidth - blocks[2].Width) <= 1
                && undone == Describe(opened[2]) && nothingLeft && selectedAfterUndo == 2 && Same(redone.Start, 6 + by) && Same(redone.End, 7.5 + by),
            $"not moved: {still.Item1}, Undo enabled {still.Item2}; 120 along the lane is {Seconds(by)} s; after 60 the cut is {Describe(half)}, after 120 {Describe(whole)} with the playhead at {Seconds(headAfterMove)} s and its block {F(movedWidth, "0.#")} wide at {F(movedLeft, "0.#")}; "
                + $"after one Undo {undone} with nothing left to undo: {nothingLeft}, selected cut {selectedAfterUndo + 1}; after Redo {Describe(redone)}");

        // Dragged on to the right, further than there is room: it stops against the next cut, and the two touch.
        var from = XOf(redone.Start + 0.75);
        Press(from);
        Drag(from + 400);
        LetGo();
        var stopped = CutAt(2);
        var stoppedBlock = BlockAt(2, XOf(9.5));
        var nextBlock = Block(3);
        Press(XOf(stopped.Start + 0.75));
        Drag(-400);
        LetGo();
        var stoppedLeft = CutAt(2);
        _report.Check(
            "a cut that is dragged further than there is room stops against its neighbour and keeps its length: to the right its end is where the next cut starts, so the two blocks touch, and to the left its start is where the cut before it ends; no cut changes its place among the others",
            stopped.End == 11 && Same(stopped.Start, 9.5) && Math.Abs(stoppedBlock.Left + stoppedBlock.Width - nextBlock.Left) <= 1
                && Same(stoppedLeft.Start, 4.45) && Same(stoppedLeft.End, 5.95) && SelectedCutOf(editor) == 2 && LaneItems(editor, CutLane).Count == 4,
            $"dragged far to the right: {Describe(stopped)}, its block ends at {F(stoppedBlock.Left + stoppedBlock.Width, "0.#")} and the next starts at {F(nextBlock.Left, "0.#")}; dragged far to the left: {Describe(stoppedLeft)}; the lane: {LaneText(editor, CutLane)}");

        // The first pixels of a block move where the cut starts.
        Timeline.Mark("13: dragging the ends of a cut");
        var first = Block(0);
        Press(first.Left + 3);
        Drag(first.Left + 3 - 30);
        var startEarlier = CutAt(0);
        var headAtStart = Playhead(editor);
        Drag(first.Left + 3 - 300);
        var startAtZero = CutAt(0);
        Drag(first.Left + 3 + 300);
        LetGo();
        var startAgainstEnd = CutAt(0);
        var headAgainstEnd = Playhead(editor);
        _report.Check(
            "the first 6 of a block move where its cut starts, and the playhead follows it: no earlier than the start of the recording, and no later than a tenth of a second before the cut's end, which stays",
            Same(startEarlier.Start, 1 - (30 * secondsPerPixel)) && startEarlier.End == 2 && Same(headAtStart, startEarlier.Start)
                && startAtZero.Start == 0 && startAtZero.End == 2 && Same(startAgainstEnd.Start, 1.9) && startAgainstEnd.End == 2 && Same(headAgainstEnd, 1.9) && SelectedCutOf(editor) == 0,
            $"30 to the left: {Describe(startEarlier)}, playhead {Seconds(headAtStart)} s; far to the left: {Describe(startAtZero)}; far to the right: {Describe(startAgainstEnd)}, playhead {Seconds(headAgainstEnd)} s");
        Invoke(editor, "StudioUndoButton");
        var startBack = Until(() => Describe(CutAt(0)), now => now == Describe(opened[0]), 1);

        // The last pixels move where it ends.
        first = BlockAt(0, blocks[0].Left);
        var end = first.Left + first.Width - 3;
        Press(end);
        Drag(end + 40);
        var endLater = CutAt(0);
        var headAtEnd = Playhead(editor);
        Drag(end + 400);
        var endAgainstNext = CutAt(0);
        Drag(end - 400);
        LetGo();
        var endAgainstStart = CutAt(0);
        var headAgainstStart = Playhead(editor);
        _report.Check(
            "the last 6 of a block move where its cut ends, and the playhead follows it: no later than where the next cut starts, and no earlier than a tenth of a second after the cut's start, which stays; the one Undo before it took the whole drag of the start back",
            startBack == Describe(opened[0]) && endLater.Start == 1 && Same(endLater.End, 2 + (40 * secondsPerPixel)) && Same(headAtEnd, endLater.End)
                && endAgainstNext.Start == 1 && Same(endAgainstNext.End, 4) && endAgainstStart.Start == 1 && Same(endAgainstStart.End, 1.1) && Same(headAgainstStart, 1.1),
            $"after the Undo: {startBack}; 40 to the right: {Describe(endLater)}, playhead {Seconds(headAtEnd)} s; far to the right: {Describe(endAgainstNext)}; far to the left: {Describe(endAgainstStart)}, playhead {Seconds(headAgainstStart)} s");
        Invoke(editor, "StudioUndoButton");
        Until(() => Describe(CutAt(0)), now => now == Describe(opened[0]), 1);

        // A block narrower than 24 has no ends of its own: wherever it is taken, the whole cut moves.
        var narrow = BlockAt(3, blocks[3].Left);
        Press(narrow.Left + 2);
        Drag(narrow.Left + 2 - 40);
        LetGo();
        var narrowMoved = CutAt(3);
        _report.Check(
            "a block narrower than 24 is moved as a whole from its first pixels too, because it has no room for ends of its own: the cut keeps its length",
            narrow.Width < 24 && Same(narrowMoved.Start, 11 - (40 * secondsPerPixel)) && Same(narrowMoved.End - narrowMoved.Start, 0.2) && SelectedCutOf(editor) == 3,
            $"the block is {F(narrow.Width, "0.#")} wide; taken at its second pixel and dragged 40 to the left: {Describe(narrowMoved)}");

        // Every drag was one step. Two were taken back above; the four that are left, of the
        // body of the third cut three times and of the narrow one, are taken back one by one.
        var steps = new List<string>();
        for (var undo = 0; undo < 4 && CanUndo(); undo++)
        {
            var before = Describe(CutsOf(editor));
            Invoke(editor, "StudioUndoButton");
            steps.Add(Until(() => Describe(CutsOf(editor)), now => now != before, 1));
        }

        var back = CutsAre(editor, opened, 3);
        _report.Check(
            "each drag was one undo step: the four that had not been undone are taken back by four Undo, after which the cuts are as the project was opened, the cut that was selected still is, and there is nothing left to undo",
            steps.Count == 4 && back is null && Until(() => !CanUndo(), nothing => nothing, 1),
            back ?? $"after each Undo: {string.Join(" | ", steps)}");
        CloseQuietly(editor);

        CutBlockLooks(AppTheme.Light, "light");
        CutBlockLooks(AppTheme.Dark, "dark");
    }

    // ---------------------------------------------------------------------------------------
    // What a press on each row of the timeline does to what is selected
    // ---------------------------------------------------------------------------------------

    /// <summary>What is selected, in a few words: "cut 2, zoom none".</summary>
    private string WhatIsSelected(Editor editor) =>
        $"cut {(SelectedCutOf(editor) is { } cut ? (cut + 1).ToString(CultureInfo.InvariantCulture) : "none")}, zoom {(SelectedZoomOf(editor) is { } zoom ? (zoom + 1).ToString(CultureInfo.InvariantCulture) : "none")}";

    /// <summary>
    /// A press on the empty part of the zoom lane or of the cut lane lets go of whatever is
    /// selected, a zoom or a cut, whichever of the two lanes it is on. A press on the scene lane
    /// and what the trim bar asks for leave it.
    /// </summary>
    /// <remarks>
    /// The lanes are pressed by calling what their pointer handlers call. The trim bar has
    /// nothing of that kind to call. Its playhead and its handles are set through UI Automation,
    /// which makes the bar raise the request that a press on it and on a handle raises. The
    /// gesture that a press on a handle begins, and ends when the handle is let go, is begun
    /// and ended on the view model, which is what the timeline does when the bar says so. The
    /// bar's own pointer handlers are not run: that a press asks for nothing else is read from
    /// its code and not seen here.
    /// </remarks>
    private void PressesAndWhatIsSelected()
    {
        Timeline.Mark("13: presses on the rows of the timeline, and what is selected");

        // Two of each, none where another is: a second scene from 6 s, cuts from 2 and from 9 s, zooms from 4 and from 7 s.
        StudioTimeRange[] cuts = [Cut(2, 3), Cut(9, 10)];
        StudioZoom[] zooms = [PointZoom(4, 5.5, 0.5, 0.5), PointZoom(7, 8, 0.5, 0.5)];
        var folder = NewCameraProject("Presses and the selection", p => p with
        {
            Scenes = [p.Scenes[0], p.Scenes[0] with { Start = 6, Layout = StudioLayout.SideBySide }],
            Edits = p.Edits with { Cuts = cuts },
            Zooms = zooms,
        });
        if (OpenReady(folder, "presses and the selection") is not { } editor)
        {
            return;
        }

        string ZoomLaneFor(int? selected) => string.Join(" | ", zooms.Select((zoom, index) => (index == selected ? "*" : string.Empty) + StudioEditorText.GetZoomDescription(zoom)));
        var problem = CutsAre(editor, cuts, null);
        var zoomsOpened = WaitForLane(editor, ZoomLaneFor(null));
        if (problem is not null || zoomsOpened != ZoomLaneFor(null) || ScenesOf(editor).Length != 2)
        {
            _report.Check("the editor opens a project with two scenes, two cuts and two zooms, and selects neither a cut nor a zoom", false, problem ?? $"the zoom lane: {zoomsOpened}; scenes: {ScenesOf(editor).Length}");
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

        bool SelectCut(int index) => (Find(editor, $"StudioCut_{index}")?.Select() ?? false) && Until(() => SelectedCutOf(editor), selected => selected == index, 1) == index;
        bool SelectZoom(int index) => (Find(editor, $"StudioZoom_{index}")?.Select() ?? false) && Until(() => SelectedZoomOf(editor), selected => selected == index, 1) == index;
        bool CanUndo() => Find(editor, "StudioUndoButton", 0.5)?.IsEnabled == true;
        const string Nothing = "cut none, zoom none";

        // The second cut is selected, and the zoom lane is pressed right above it, where no zoom is.
        Timeline.Mark("13: a press on the empty part of the other lane");
        var cutSelected = SelectCut(1);
        var cutMarked = WaitForLane(editor, CutLaneFor(cuts, 1), 1, CutLane);
        var beforeZoomLane = WhatIsSelected(editor);
        Press(ZoomLane, 9.5);
        var afterZoomLane = (WhatIsSelected(editor), Playhead(editor));
        var lanesAfterZoomLane = (WaitForLane(editor, CutLaneFor(cuts, null), 1, CutLane), WaitForLane(editor, ZoomLaneFor(null), 1));
        var cutSection = CutSectionShows(editor, "2 cuts", range: null);

        // The other way round: the first zoom is selected, and the cut lane is pressed right below it, where no cut is.
        var zoomSelected = SelectZoom(0);
        var zoomMarked = WaitForLane(editor, ZoomLaneFor(0), 1);
        var beforeCutLane = WhatIsSelected(editor);
        Press(CutLane, 5.0);
        var afterCutLane = (WhatIsSelected(editor), Playhead(editor));
        var lanesAfterCutLane = (WaitForLane(editor, CutLaneFor(cuts, null), 1, CutLane), WaitForLane(editor, ZoomLaneFor(null), 1));
        var zoomPosition = Until(() => NameOf(editor, "StudioZoomPositionText", 0.5), text => text == "2 zooms", 1.5);
        var zoomControlsGone = Absent(editor, "StudioZoomScaleSlider") && Absent(editor, "StudioDeleteZoomButton", 0.3);
        _report.Check(
            "with a cut selected, a press on the empty part of the zoom lane selects nothing: it lets go of the cut, although the press is on the other lane; and the other way round, with a zoom selected, a press on the empty part of the cut lane lets go of the zoom. "
                + "Each press moves the playhead to the time that was pressed; afterwards neither lane marks an item, and the Cut section and the Zoom section show none",
            cutSelected && cutMarked == CutLaneFor(cuts, 1) && beforeZoomLane == "cut 2, zoom none" && afterZoomLane.Item1 == Nothing && Same(afterZoomLane.Item2, 9.5, 0.01)
                && lanesAfterZoomLane == (CutLaneFor(cuts, null), ZoomLaneFor(null)) && cutSection is null
                && zoomSelected && zoomMarked == ZoomLaneFor(0) && beforeCutLane == "cut none, zoom 1" && afterCutLane.Item1 == Nothing && Same(afterCutLane.Item2, 5.0, 0.01)
                && lanesAfterCutLane == (CutLaneFor(cuts, null), ZoomLaneFor(null)) && zoomPosition == "2 zooms" && zoomControlsGone,
            $"selected: {beforeZoomLane}; the zoom lane pressed at 9.5 s, above that cut: {afterZoomLane.Item1}, playhead {Seconds(afterZoomLane.Item2)} s, the cut lane: {lanesAfterZoomLane.Item1}, the Cut section: {cutSection ?? "\"2 cuts\" and no cut's controls"}. "
                + $"Selected: {beforeCutLane}; the cut lane pressed at 5.0 s, below that zoom: {afterCutLane.Item1}, playhead {Seconds(afterCutLane.Item2)} s, the zoom lane: {lanesAfterCutLane.Item2}, the Zoom section: \"{zoomPosition}\", a zoom's controls gone: {zoomControlsGone}");

        // What a press on a handle of the trim bar and its release ask of the editor: a gesture,
        // and in it the time the handle is at. The editor answers by showing the picture there.
        void TakeHandle(string handle, double time)
        {
            OnUi(() => editor.Window.ViewModel.BeginGesture());
            SetSlider(editor, handle, time);
            OnUi(() => editor.Window.ViewModel.EndGesture());
        }

        // What leaves the selection alone, with the cut selected: a press on a scene, the playhead
        // moved along the trim bar, which is what a press on the bar asks for, and the Start handle
        // taken and let go where it is.
        Timeline.Mark("13: the scene lane and the trim bar leave what is selected");
        var kept = new List<string>();
        var lost = new List<string>();
        void Step(string what, string wanted, double head, Action act)
        {
            act();
            var playhead = Until(() => Playhead(editor), now => Same(now, head, 0.01), 1);
            var selected = WhatIsSelected(editor);
            (selected == wanted && Same(playhead, head, 0.01) ? kept : lost).Add($"{what}: {selected}, playhead {Seconds(playhead)} s");
        }

        var (trimStart, trimEnd) = (SliderValue(editor, "StudioTrimStart"), SliderValue(editor, "StudioTrimEnd"));
        var cutAgain = SelectCut(1);
        Step("with the second cut selected, the scene lane pressed at 1.0 s", "cut 2, zoom none", 1.0, () => Press(SceneLane, 1.0));
        Step("the trim bar's playhead moved to 6.5 s", "cut 2, zoom none", 6.5, () => SetSlider(editor, "StudioPlayhead", 6.5));
        Step($"the Start handle taken and let go at the {Seconds(trimStart)} s it is at", "cut 2, zoom none", trimStart, () => TakeHandle("StudioTrimStart", trimStart));

        // And with the zoom selected: a press on the other scene, the playhead, and the End handle.
        var zoomAgain = SelectZoom(0);
        Step("with the first zoom selected, the scene lane pressed at 11.0 s", "cut none, zoom 1", 11.0, () => Press(SceneLane, 11.0));
        Step("the trim bar's playhead moved to 1.5 s", "cut none, zoom 1", 1.5, () => SetSlider(editor, "StudioPlayhead", 1.5));
        Step($"the End handle taken and let go at the {Seconds(trimEnd)} s it is at", "cut none, zoom 1", trimEnd, () => TakeHandle("StudioTrimEnd", trimEnd));
        var canUndo = CanUndo();
        var unchanged = Describe(CutsOf(editor)) == Describe(cuts) && ZoomsOf(editor).Length == 2 && ScenesOf(editor).Length == 2
            && Same(SliderValue(editor, "StudioTrimStart"), trimStart) && Same(SliderValue(editor, "StudioTrimEnd"), trimEnd);
        _report.Check(
            "a press on the scene lane leaves what is selected as it is, and so does what the trim bar asks the editor for when it is pressed: a move of the playhead for a press on the bar, and for a handle that is taken and let go a gesture in which the handle is asked for the time it is at. "
                + "A selected cut stays selected through the three, and a selected zoom as well; each moves the playhead, and none changes the project or adds to what can be undone",
            cutAgain && zoomAgain && lost.Count == 0 && kept.Count == 6 && !canUndo && unchanged,
            lost.Count == 0 ? $"{string.Join("; ", kept)}; Undo enabled {canUndo}; the project as it was opened: {unchanged}" : $"not as wanted: {string.Join("; ", lost)}");
        CloseQuietly(editor);
    }

    // ---------------------------------------------------------------------------------------
    // What a cut's block looks like
    // ---------------------------------------------------------------------------------------

    /// <summary>The brightness of every pixel of a stretch of one row of a screenshot.</summary>
    private static double[] RowOf(Shot shot, double y, double fromX, double toX)
    {
        var (first, last) = ((int)Math.Ceiling(fromX), (int)Math.Floor(toX));
        var row = (int)Math.Floor(y);
        return [.. Enumerable.Range(first, Math.Max(0, last - first + 1)).Select(x => Luma(shot.Color(x, row, 0)))];
    }

    /// <summary>
    /// How often the brightness along a row goes from one side of a level to the other: twice
    /// for every stripe or dash across it. None for a row whose pixels are all within a few
    /// levels of each other.
    /// </summary>
    /// <param name="lane">
    /// Null to count stripes: the level is the middle between the row's darkest and brightest
    /// pixel. The brightness of the lane itself to count the dashes of an outline, which are the
    /// pixels furthest from it: the level is then a third of the way from the dashes to the
    /// other end, so that a stripe of the hatching seen between two dashes is not taken for one.
    /// </param>
    private static int Alternations(double[] row, double? lane = null)
    {
        if (row.Length < 2 || row.Max() - row.Min() < 12)
        {
            return 0;
        }

        var (low, high) = (row.Min(), row.Max());
        var level = lane is not { } own ? (low + high) / 2
            : Math.Abs(low - own) > Math.Abs(high - own) ? low + ((high - low) / 3)
            : high - ((high - low) / 3);
        return row.Zip(row.Skip(1), (a, b) => (a > level) != (b > level)).Count(changed => changed);
    }

    /// <summary>How far apart the brightness of a row's pixels is, leaving out the twentieth at each end.</summary>
    private static double SpreadOf(double[] row)
    {
        if (row.Length == 0)
        {
            return double.NaN;
        }

        var ordered = row.Order().ToArray();
        return ordered[(int)(ordered.Length * 0.95)] - ordered[(int)(ordered.Length * 0.05)];
    }

    /// <summary>
    /// A cut has to look like something taken away and not like a zoom, and the selected cut
    /// has to differ from the others by more than its colour. Read from the pixels of a window
    /// in one theme: the block is hatched where a zoom's is one colour, its outline is dashed,
    /// and the selected one's outline is solid and twice as thick.
    /// </summary>
    /// <remarks>
    /// What these readings cannot tell: the colours themselves, so a hatching in a colour that
    /// is hard to see on a real screen would pass as long as it differs by twelve levels of
    /// brightness; and whether the scissors are the scissors. Contrast themes are not seen.
    /// </remarks>
    private void CutBlockLooks(AppTheme theme, string name)
    {
        Timeline.Mark($"13: what a cut looks like, {name}");
        _services.Settings.Theme = theme;
        try
        {
            // A cut and a zoom of four seconds each, which leaves a hundred pixels of each clear of what is written in its middle.
            var folder = NewScreenProject($"Cut looks, {name}", p => p with { Edits = p.Edits with { Cuts = [Cut(1, 5)] }, Zooms = [PointZoom(7, 11, 0.5, 0.5)] });
            if (OpenReady(folder, $"cut looks, {name}") is not { } editor)
            {
                return;
            }

            SetSlider(editor, "StudioPlayhead", 6.0);
            WaitForLane(editor, CutLaneFor([Cut(1, 5)], null), 2, CutLane);
            Thread.Sleep(500);
            var scale = editor.Scale;

            // Three rows of the first 72 effective pixels after the round corner of a block: through
            // its outline, just under it, and through its middle.
            (double[] Outline, double[] Under, double[] Middle, double Track)? Rows(string id, string? saveAs = null)
            {
                if (editor.Camera.Take() is not { } shot || Find(editor, id, 0.5)?.Bounds is not { Width: > 0 } bounds || Find(editor, CutLane, 0.5)?.Bounds is not { Width: > 0 } lane)
                {
                    return null;
                }

                if (saveAs is not null)
                {
                    var path = Path.Combine(_output, saveAs);
                    shot.Save(path);
                    _report.Line($"  saved {path}");
                }

                var (left, top) = (bounds.X - shot.ScreenX + (8 * scale), bounds.Y - shot.ScreenY);
                var right = left + (72 * scale);

                // The lane itself, where it has no block: left of the first one.
                var track = Luma(shot.Color(lane.X - shot.ScreenX + (8 * scale), top + (bounds.Height / 2.0), 1));
                return (RowOf(shot, top + (0.5 * scale), left, right), RowOf(shot, top + (1.5 * scale), left, right), RowOf(shot, top + (bounds.Height / 2.0), left, right), track);
            }

            var cut = Rows("StudioCut_0", $"cut-looks-{name}.png");
            var zoom = Rows("StudioZoom_0");
            Find(editor, "StudioCut_0")?.Select();
            WaitForLane(editor, CutLaneFor([Cut(1, 5)], 0), 2, CutLane);
            Thread.Sleep(500);
            var selected = Rows("StudioCut_0", $"cut-looks-{name}-selected.png");
            if (cut is not { } plain || zoom is not { } flat || selected is not { } marked)
            {
                _report.Check($"a cut's block in the {name} theme can be pictured", false, "no screenshot, or no block");
                CloseQuietly(editor);
                return;
            }

            // Across 72 pixels a hatching that repeats every 12 has 6 stripes, which is 12 changes of brightness, and an outline of dashes that repeat every 5 has 14, which is 29.
            var hatched = (Middle: Alternations(plain.Middle), Under: Alternations(plain.Under));
            var dashed = Alternations(plain.Outline, plain.Track);
            var oneColour = (Middle: Alternations(flat.Middle), Spread: SpreadOf(flat.Middle));
            _report.Check(
                $"in the {name} theme a cut's block is hatched where a zoom's is one colour, inside an outline of dashes: something taken away, and not a zoom",
                hatched.Middle is >= 8 and <= 16 && hatched.Under is >= 8 and <= 16 && dashed >= 20 && oneColour.Middle == 0 && oneColour.Spread < 12,
                string.Create(CultureInfo.InvariantCulture, $"along 72 pixels of the cut's block the brightness changes sides {hatched.Middle} times through its middle and {hatched.Under} times just under its outline (12 for a hatching that repeats every 12), and {dashed} times along the outline (29 for dashes that repeat every 5); ")
                    + string.Create(CultureInfo.InvariantCulture, $"along 72 pixels of the zoom's block it changes sides {oneColour.Middle} times, its pixels within {oneColour.Spread:0} levels of each other"));

            // Selected: the outline is one unbroken line, two effective pixels thick, and far from the lane's own brightness.
            var solid = (Outline: SpreadOf(marked.Outline), Under: SpreadOf(marked.Under));
            var strong = Math.Abs(marked.Outline.Average() - marked.Track);
            var stillHatched = Alternations(marked.Middle);
            _report.Check(
                $"in the {name} theme the selected cut differs by more than its colour: its outline is one unbroken line, twice as thick as the dashes of the others, and it is still hatched",
                solid.Outline < 24 && solid.Under < 24 && strong > 60 && stillHatched is >= 8 and <= 16 && dashed >= 20,
                string.Create(CultureInfo.InvariantCulture, $"along the selected block's outline the pixels are within {solid.Outline:0} levels of each other, and in the row under it within {solid.Under:0}, {strong:0} levels from the lane's own brightness; ")
                    + string.Create(CultureInfo.InvariantCulture, $"through its middle the brightness changes sides {stillHatched} times; not selected, the outline's row changed sides {dashed} times and the row under it {hatched.Under} times; ")
                    + $"the lane is {F(marked.Track, "0")}; the first pixels of the outline's row, not selected: {string.Join(" ", plain.Outline.Take(32).Select(value => F(value, "0")))}; selected: {string.Join(" ", marked.Outline.Take(32).Select(value => F(value, "0")))}; under it, selected: {string.Join(" ", marked.Under.Take(32).Select(value => F(value, "0")))}");
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
    /// The stretches of the trim bar between its two handles that are tinted, read from a row of
    /// pixels of a screenshot, as times: where each starts and ends along the bar.
    /// </summary>
    private (List<(double Start, double End)> Kept, string Colours)? KeptParts(Editor editor)
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

        // A quarter of the bar's height down, clear of the round corners and of the marks on the handles.
        var y = bar.Y - shot.ScreenY + (bar.Height / 4.0);

        // The bar's own colour, where nothing is kept: just before the Start handle.
        var own = shot.Color(start.X - shot.ScreenX - handle, y, 1);
        var (from, to) = ((int)Math.Ceiling(start.X - shot.ScreenX + handle), (int)Math.Floor((double)end.X - shot.ScreenX) - 1);
        var kept = new List<(double Start, double End)>();
        int? runStart = null;
        Rgb? tint = null;
        for (var x = from; x <= to + 1; x++)
        {
            var isTinted = x <= to && shot.Color(x, y, 0).Distance(own) > 14;
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
                    tint ??= shot.Color((first + x) / 2.0, y, 1);
                }

                runStart = null;
            }
        }

        return (kept, $"the bar is {own} and what is kept {(tint is { } colour ? colour.ToString() : "nowhere")}");
    }

    /// <summary>Null when the tinted parts of the trim bar are the given stretches, each end within a number of seconds. Otherwise what was read.</summary>
    private static string? KeptPartsAre(List<(double Start, double End)>? read, (double Start, double End)[] wanted, double within)
    {
        if (read is null)
        {
            return "no screenshot";
        }

        return read.Count == wanted.Length && read.Zip(wanted, (a, b) => Math.Abs(a.Start - b.Start) <= within && Math.Abs(a.End - b.End) <= within).All(ok => ok)
            ? null
            : $"read {(read.Count == 0 ? "none" : string.Join(", ", read.Select(part => $"{F(part.Start, "0.00")} to {F(part.End, "0.00")} s")))}, wanted {string.Join(", ", wanted.Select(part => $"{F(part.Start, "0.00")} to {F(part.End, "0.00")} s"))}";
    }

    /// <summary>
    /// The trim bar shows what the video keeps: the range between the two handles is tinted,
    /// with a gap for every cut. Read from the pixels of the bar.
    /// </summary>
    /// <remarks>
    /// The reading tells where the tint starts and stops, to about two pixels. It cannot tell a
    /// gap from a part that is tinted in the bar's own colour, which is what a gap is.
    /// </remarks>
    private void TrimBarShowsWhatIsKept()
    {
        Timeline.Mark("13: the trim bar shows what is kept");
        var folder = NewScreenProject("Kept parts", p => p with { Edits = p.Edits with { TrimStart = 1, TrimEnd = 11, Cuts = [Cut(2, 3), Cut(6, 7.5)] } });
        if (OpenReady(folder, "kept parts") is not { } editor)
        {
            return;
        }

        // Two pixels of the bar, as seconds.
        var bar = Find(editor, "StudioTrimBar")?.Bounds ?? default;
        var within = 2.0 / (bar.Width - (24 * editor.Scale)) * RecordingLength;
        var time = TimeText(editor);
        var opened = KeptParts(editor);
        var openedWrong = KeptPartsAre(opened?.Kept, [(1, 2), (3, 6), (7.5, 11)], within);
        _report.Check(
            "the trim bar of a video that starts at 1.0 s and ends at 11.0 s, with cuts from 2.0 to 3.0 s and from 6.0 to 7.5 s, is tinted in three parts, with a gap in the bar's own colour where each cut is; the time counts what is kept, 7.5 seconds",
            openedWrong is null && time.EndsWith("/ 0:07.5", StringComparison.Ordinal),
            $"{openedWrong ?? "tinted from 1.0 to 2.0 s, from 3.0 to 6.0 s and from 7.5 to 11.0 s, each end within " + F(within, "0.00") + " s, which is two pixels"}; {opened?.Colours}; the time reads \"{time}\"");

        // A cut made in the window: a gap more. Undone: the gap goes.
        SetSlider(editor, "StudioPlayhead", 9.0);
        Key(editor, StudioShortcutKey.X);
        Until(() => CutsOf(editor).Length, count => count == 3, 1.5);
        var cutMade = KeptPartsAre(KeptParts(editor)?.Kept, [(1, 2), (3, 6), (7.5, 9), (10, 11)], within);
        Invoke(editor, "StudioUndoButton");
        Until(() => CutsOf(editor).Length, count => count == 2, 1.5);
        var cutUndone = KeptPartsAre(KeptParts(editor)?.Kept, [(1, 2), (3, 6), (7.5, 11)], within);

        // A cut's end moved in the Cut section, to the playhead at 3.25 s: the gap follows.
        Find(editor, "StudioCut_0")?.Select();
        Until(() => SelectedCutOf(editor), index => index == 0, 1);
        SetSlider(editor, "StudioPlayhead", 3.25);
        Invoke(editor, "StudioCutEndAtPlayheadButton");
        var end = Until(() => CutsOf(editor)[0].End, value => value == 3.25, 1.5);
        var endMoved = KeptPartsAre(KeptParts(editor)?.Kept, [(1, 2), (3.25, 6), (7.5, 11)], within);
        _report.Check(
            "the gaps follow the cuts: a cut made with X at 9.0 s leaves a fourth part, Undo joins the two again, and a cut whose end is moved a quarter of a second later moves the start of the part after it",
            cutMade is null && cutUndone is null && end == 3.25 && endMoved is null,
            $"after X at 9.0 s: {cutMade ?? "four parts, the last two from 7.5 to 9.0 s and from 10.0 to 11.0 s"}; after Undo: {cutUndone ?? "three parts again"}; with the first cut ending at {Seconds(end)} s: {endMoved ?? "the second part starts at 3.25 s"}");

        // The video started inside a cut: what is kept starts where the cut ends, and the handle stays where the start is.
        var set = SetSlider(editor, "StudioTrimStart", 2.5);
        var startValue = Until(() => SliderValue(editor, "StudioTrimStart"), value => Same(value, 2.5), 1.5);
        var inside = KeptParts(editor);
        var insideWrong = KeptPartsAre(inside?.Kept, [(3.25, 6), (7.5, 11)], within);

        // 2.75 s and 3.5 s are kept.
        var timeInside = TimeText(editor);
        _report.Check(
            "a video that starts inside a cut: the Start handle is at the time it was given, and what is kept starts where the cut ends, with the bar's own colour between the two; the time counts the 6.25 seconds that are kept",
            set && Same(startValue, 2.5) && insideWrong is null && timeInside.EndsWith("/ 0:06.2", StringComparison.Ordinal),
            $"Start {Seconds(startValue)} s; {insideWrong ?? "tinted from 3.25 to 6.0 s and from 7.5 to 11.0 s"}; the time reads \"{timeInside}\"");
        CloseQuietly(editor);
    }

    /// <summary>
    /// With cuts in the project, a trim that would leave less than a tenth of a second of video
    /// is refused as a whole. The handle that was asked to move has to stay where the video
    /// really starts or ends.
    /// </summary>
    private void TrimThatLeavesTooLittle()
    {
        Timeline.Mark("13: a trim that the cuts do not allow");

        // Two cuts leave the first quarter of a second and a quarter of a second near the end.
        var folder = NewScreenProject("Refused trims", p => p with { Edits = p.Edits with { Cuts = [Cut(0.25, 11.5), Cut(11.75, 12)] } });
        if (OpenReady(folder, "refused trims") is not { } editor)
        {
            return;
        }

        double Start() => SliderValue(editor, "StudioTrimStart");
        double End() => SliderValue(editor, "StudioTrimEnd");
        (double Start, double End) Held() => OnUi(() => (editor.Window.ViewModel.TrimStart, editor.Window.ViewModel.TrimEnd));
        bool CanUndo() => Find(editor, "StudioUndoButton", 0.5)?.IsEnabled == true;

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

        // Granted: the video starts where the first cut starts, and is the quarter of a second between the cuts.
        var startSet = SetSlider(editor, "StudioTrimStart", 0.25);
        var started = Until(Start, value => value == 0.25, 1.5);
        var timeGranted = TimeText(editor);
        _report.Check(
            "with cuts from 0.25 to 11.5 s and from 11.75 s to the end, Start set to 0.25 s is granted: a quarter of a second of video is left",
            startSet && started == 0.25 && timeGranted.EndsWith("/ 0:00.2", StringComparison.Ordinal) && CanUndo(),
            $"Start {Seconds(started)} s; the time reads \"{timeGranted}\"");

        // Refused: End at 11.55 s would leave a twentieth of a second.
        Timeline.Mark("13: End and Start, where they are refused");
        var headBefore = Playhead(editor);
        var endSet = SetSlider(editor, "StudioTrimEnd", 11.55);
        Thread.Sleep(300);
        var refused = (End(), Held(), Find(editor, "StudioTrimEnd")?.ValueText, TimeText(editor), Apart().End, Playhead(editor));
        _report.Check(
            "End asked to go to 11.55 s, which would leave a twentieth of a second, is refused as a whole: the handle reports 12.0 s, is drawn where 12.0 s is, the editor's video still ends there, and the time still reads a quarter of a second",
            refused.Item1 == RecordingLength && refused.Item2 == (0.25, RecordingLength) && refused.Item3 == "12.0 seconds" && refused.Item4.EndsWith("/ 0:00.2", StringComparison.Ordinal) && refused.Item5 <= 2,
            $"the request was {(endSet ? "taken" : "not taken")}; End reports {Seconds(refused.Item1)} s, \"{refused.Item3}\", and is drawn {F(refused.Item5, "0.#")} px from where that is; the editor holds {Seconds(refused.Item2.Start)} to {Seconds(refused.Item2.End)} s; "
                + $"the time reads \"{refused.Item4}\"; the playhead was at {Seconds(headBefore)} s and is at {Seconds(refused.Item6)} s");

        // Start, the same: at 11.7 s it would leave the twentieth of a second before the second cut.
        var startAsked = SetSlider(editor, "StudioTrimStart", 11.7);
        Thread.Sleep(300);
        var startStays = (Start(), Held(), Find(editor, "StudioTrimStart")?.ValueText, Apart().Start, TimeText(editor));
        _report.Check(
            "Start asked to go to 11.7 s, which would leave a twentieth of a second, is refused as a whole: the handle reports 0.25 s and is drawn there",
            startStays.Item1 == 0.25 && startStays.Item2 == (0.25, RecordingLength) && startStays.Item4 <= 2 && startStays.Item5.EndsWith("/ 0:00.2", StringComparison.Ordinal),
            $"the request was {(startAsked ? "taken" : "not taken")}; Start reports {Seconds(startStays.Item1)} s, \"{startStays.Item3}\", and is drawn {F(startStays.Item4, "0.#")} px from where that is; the time reads \"{startStays.Item5}\"");

        // By a drag, as the bar asks for it: a gesture with a request for every move of the pointer.
        Timeline.Mark("13: a drag of End that goes on to where it is refused");
        OnUi(() =>
        {
            var viewModel = editor.Window.ViewModel;
            viewModel.BeginGesture();
            viewModel.SetTrimEnd(11.72);
            viewModel.SetTrimEnd(11.58);
            viewModel.SetTrimEnd(11.52);
            viewModel.EndGesture();
        });
        var dragged = (Until(End, value => Same(value, 11.72), 1.5), Held(), 0.0, TimeText(editor));
        Thread.Sleep(300);
        dragged.Item3 = Apart().End;
        _report.Check(
            "dragged, the End handle goes as far as it is granted and stays there while the pointer goes on to where it is refused: asked for 11.72 s, then for 11.58 s and 11.52 s, it is at 11.72 s, and is drawn there",
            Same(dragged.Item1, 11.72) && Same(dragged.Item2.End, 11.72) && dragged.Item3 <= 2 && dragged.Item4.EndsWith("/ 0:00.2", StringComparison.Ordinal),
            $"End reports {Seconds(dragged.Item1)} s and is drawn {F(dragged.Item3, "0.#")} px from where that is; the editor holds {Seconds(dragged.Item2.Start)} to {Seconds(dragged.Item2.End)} s; the time reads \"{dragged.Item4}\"");

        // What was refused left nothing to undo: two steps were made, the start and the drag of the end.
        Invoke(editor, "StudioUndoButton");
        var afterOne = (Until(End, value => value == RecordingLength, 1.5), Start());
        Invoke(editor, "StudioUndoButton");
        var afterTwo = (End(), Until(Start, value => value == 0, 1.5));
        var nothingLeft = Until(() => !CanUndo(), nothing => nothing, 1);
        _report.Check(
            "a trim that was refused is no undo step: one Undo takes the drag of the end back, a second one the start, and then there is nothing left to undo",
            afterOne.Item1 == RecordingLength && afterOne.Item2 == 0.25 && afterTwo.Item1 == RecordingLength && afterTwo.Item2 == 0 && nothingLeft,
            $"after one Undo: {Seconds(afterOne.Item2)} to {Seconds(afterOne.Item1)} s; after two: {Seconds(afterTwo.Item2)} to {Seconds(afterTwo.Item1)} s; nothing left to undo: {nothingLeft}");

        // Without the cut at the end, the start that was refused is granted.
        Find(editor, "StudioCut_1")?.Select();
        Until(() => SelectedCutOf(editor), index => index == 1, 1);
        Key(editor, StudioShortcutKey.Delete);
        Until(() => CutsOf(editor).Length, count => count == 1, 1.5);
        SetSlider(editor, "StudioTrimStart", 11.7);
        var granted = Until(Start, value => Same(value, 11.7), 1.5);
        Thread.Sleep(300);
        var grantedApart = Apart().Start;
        _report.Check(
            "once the cut at the end is deleted, the same request is granted: Start goes to 11.7 s, and is drawn there",
            Same(granted, 11.7) && grantedApart <= 2,
            $"Start reports {Seconds(granted)} s and is drawn {F(grantedApart, "0.#")} px from where that is");
        CloseQuietly(editor);
    }
}
