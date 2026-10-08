using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using TinyClips.App.Controls.Studio;
using TinyClips.Core.Studio;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// The handles at the two ends of a block on the zoom, the cut and the speed lane: what a press
// takes hold of, by the numbers the lanes have had since 7 October 2026 (a handle is 8 wide; a
// block of 28 or more has its handles inside its ends; a narrower one has none until it is
// selected, and then one outside each end), and where the handles are drawn. The same four
// checks run on each of the three lanes, at the end of the lane's presses and drags.
//
// No pointer is moved or pressed. A press and a drag are what the lane's pointer handlers call
// with a place along the lane, and the pointer coming over a block is what the block's handler
// for that calls. That a real pointer lands on a handle, that its shape changes there, and what
// the handles look like, is for a person.
internal sealed partial class WindowChecks
{
    /// <summary>What one of the three lanes is to the checks of the handles.</summary>
    /// <param name="Mark">What the lane's steps start with in the timeline, such as "13".</param>
    /// <param name="What">What a block stands for in a sentence: "zoom", "cut" or "speed change".</param>
    /// <param name="WhenOrWhere">The word the block's tooltip asks about its ends with.</param>
    /// <param name="BlockIdPrefix">What a block's automation id starts with.</param>
    /// <param name="Ranges">When each item starts and ends, in the order of the lane.</param>
    /// <param name="Selected">The selected item's place, or null.</param>
    private sealed record LaneWithHandles(
        string Mark,
        string What,
        string WhenOrWhere,
        string LaneId,
        string BlockIdPrefix,
        Func<(double Start, double End)[]> Ranges,
        Func<int?> Selected);

    /// <summary>
    /// Where the lane itself has a block, and how wide: the numbers a press is held against,
    /// which the lane sets at once, with how far the block's handles stand out past its ends.
    /// What UI Automation has of a block is its rectangle on the screen in whole pixels, which
    /// comes with the next layout and takes in the handles outside a narrow selected block.
    /// </summary>
    private (double Left, double Width, double Outset) BlockOnLane(Editor editor, string automationId) => OnUi(() =>
        Descendant<StudioLaneBlock>(editor.Window.Content, automationId) is { } block
            ? (Canvas.GetLeft(block), block.Width, block is StudioRangeBlock ? StudioEditorModel.GetLaneHandleOutset(block.Width, block.IsSelected) : 0)
            : default);

    /// <summary>
    /// The four checks of the handles, on a lane whose project is as it was opened. Every drag
    /// is undone again. <paramref name="wide"/> is a block of 28 or more with room to both
    /// sides, <paramref name="narrow"/> one that is narrower, with the lane empty for 40 on
    /// both sides of it.
    /// </summary>
    private void LaneHandles(Editor editor, LaneWithHandles lane, int wide, int narrow)
    {
        Timeline.Mark($"{lane.Mark}: the handles of a block of the {lane.What} lane");
        var laneBounds = Find(editor, lane.LaneId)?.Bounds ?? default;
        var laneWidth = laneBounds.Width / editor.Scale;
        var secondsPerPixel = RecordingLength / (laneWidth - 24);
        var opened = lane.Ranges();
        string Id(int index) => $"{lane.BlockIdPrefix}{index}";
        (double Left, double Width) Block(int index)
        {
            var bounds = Find(editor, Id(index))?.Bounds ?? default;
            return ((bounds.X - laneBounds.X) / editor.Scale, bounds.Width / editor.Scale);
        }

        string Times((double Start, double End) range) => $"{Seconds(range.Start)} to {Seconds(range.End)} s";
        bool IsAsOpened() => lane.Ranges().SequenceEqual(opened);
        void Press(double x) => OnUi(() => LaneOf(editor, lane.LaneId)!.PressAt(x));
        void Drag(double x) => OnUi(() => LaneOf(editor, lane.LaneId)!.DragTo(x));
        void LetGo() => OnUi(() => LaneOf(editor, lane.LaneId)!.EndPress());
        void SelectNothing()
        {
            OnUi(() => editor.Window.ViewModel.SelectNothing());
            Until(lane.Selected, selected => selected is null, 1);
        }

        void Select(int index)
        {
            Find(editor, Id(index))?.Select();
            Until(lane.Selected, selected => selected == index, 1);
        }

        // A press at a place, dragged by so much and let go: what the item is then, and which is selected. Then one Undo, if anything changed.
        ((double Start, double End) Range, int? Selected) Dragged(int index, double x, double by)
        {
            Press(x);
            Drag(x + by);
            LetGo();
            var result = (lane.Ranges()[index], lane.Selected());
            if (!IsAsOpened())
            {
                Invoke(editor, "StudioUndoButton");
                Until(IsAsOpened, back => back, 1.5);
            }

            return result;
        }

        // Where the lane has a block once the project is as it was opened: its own numbers, not a rectangle on the screen.
        (double Left, double Width) AsOpened(int index)
        {
            var middle = 12 + ((opened[index].Start + opened[index].End) / 2 / secondsPerPixel);
            var block = Until(() => BlockOnLane(editor, Id(index)), now => Math.Abs(now.Left + (now.Width / 2) - middle) <= 1, 1);
            return (block.Left, block.Width);
        }

        // 1. A wide block: a handle's width of each end is the end, and what is between them is the block.
        SelectNothing();
        var by = 20 * secondsPerPixel;
        var (left, width) = AsOpened(wide);
        var was = opened[wide];
        var atEight = Dragged(wide, left + 8, -20);
        var atNine = Dragged(wide, left + 8.5, 20);
        var eightFromEnd = Dragged(wide, left + width - 8, 20);
        var nineFromEnd = Dragged(wide, left + width - 8.5, -20);
        _report.Check(
            $"on the {lane.What} lane, a block of 28 or more is taken by an end within 8 of that end and as a whole between them: pressed 8 in from its start and dragged, its start moves and its end stays; pressed 8.5 in, it moves as a whole; and the same from its end",
            width >= 28
                && Same(atEight.Range.Start, was.Start - by) && atEight.Range.End == was.End
                && Same(atNine.Range.Start, was.Start + by) && Same(atNine.Range.End, was.End + by)
                && eightFromEnd.Range.Start == was.Start && Same(eightFromEnd.Range.End, was.End + by)
                && Same(nineFromEnd.Range.Start, was.Start - by) && Same(nineFromEnd.Range.End, was.End - by)
                && IsAsOpened(),
            $"the block is {F(width, "0.#")} wide and its {lane.What} is {Times(was)}; 20 along the lane is {Seconds(by)} s; pressed 8 in and dragged 20 back: {Times(atEight.Range)}; 8.5 in and 20 on: {Times(atNine.Range)}; "
                + $"8 in from the end and 20 on: {Times(eightFromEnd.Range)}; 8.5 in from the end and 20 back: {Times(nineFromEnd.Range)}; all undone: {IsAsOpened()}");

        // 2. A narrow block that is not selected has no handles: a press next to it is a press on the empty lane.
        SelectNothing();
        var (narrowLeft, narrowWidth) = AsOpened(narrow);
        var narrowWas = opened[narrow];
        var before = Dragged(narrow, narrowLeft - 4, -30);
        var after = Dragged(narrow, narrowLeft + narrowWidth + 4, 30);
        _report.Check(
            $"on the {lane.What} lane, a block narrower than 28 that is not selected is not resized: a press 4 outside either of its ends, where a selected one has its handles, takes hold of nothing and selects nothing, and dragged on it changes no {lane.What}",
            narrowWidth < 28 && before.Range == narrowWas && before.Selected is null && after.Range == narrowWas && after.Selected is null && IsAsOpened(),
            $"the block is {F(narrowWidth, "0.#")} wide and its {lane.What} is {Times(narrowWas)}; pressed 4 before it and dragged 30 back: {Times(before.Range)}, selected {before.Selected?.ToString(CultureInfo.InvariantCulture) ?? "none"}; "
                + $"pressed 4 after it and dragged 30 on: {Times(after.Range)}, selected {after.Selected?.ToString(CultureInfo.InvariantCulture) ?? "none"}");

        // 3. Selected, it is resized by the handles outside its ends, and moved by what is between them.
        Select(narrow);
        (narrowLeft, narrowWidth) = AsOpened(narrow);
        var far = 30 * secondsPerPixel;
        var byStart = Dragged(narrow, narrowLeft - 4, -30);

        // Selected again before each, so that what one drag and its Undo left does not decide the next.
        Select(narrow);
        (narrowLeft, narrowWidth) = AsOpened(narrow);
        var byEnd = Dragged(narrow, narrowLeft + narrowWidth + 4, 30);
        Select(narrow);
        (narrowLeft, narrowWidth) = AsOpened(narrow);
        var byBody = Dragged(narrow, narrowLeft + (narrowWidth / 2), -30);
        _report.Check(
            $"on the {lane.What} lane, a block narrower than 28 that is selected is 8 wider to press on each side, and is resized there: pressed 4 before its start and dragged, its start moves and its end stays; pressed 4 after its end, its end moves and its start stays; pressed in its middle, it moves as a whole; it stays the selected one throughout",
            narrowWidth < 28
                && Same(byStart.Range.Start, narrowWas.Start - far) && byStart.Range.End == narrowWas.End && byStart.Selected == narrow
                && byEnd.Range.Start == narrowWas.Start && Same(byEnd.Range.End, narrowWas.End + far) && byEnd.Selected == narrow
                && Same(byBody.Range.Start, narrowWas.Start - far) && Same(byBody.Range.End, narrowWas.End - far) && byBody.Selected == narrow
                && IsAsOpened(),
            $"30 along the lane is {Seconds(far)} s; pressed 4 before it and dragged 30 back: {Times(byStart.Range)}; pressed 4 after it and dragged 30 on: {Times(byEnd.Range)}; pressed in its middle and dragged 30 back: {Times(byBody.Range)}; "
                + $"selected after each: {byStart.Selected}, {byEnd.Selected}, {byBody.Selected}; all undone: {IsAsOpened()}");

        // 4. Where the handles are drawn, how strongly, what the tooltips say, and that a screen reader is given none of it.
        (string Placement, bool IsStrong, bool IsRaised, string ToolTip, int ZIndex, string Start, string End) Read(int index) => OnUi(() =>
        {
            var block = Descendant<StudioRangeBlock>(editor.Window.Content, Id(index));
            var handles = block?.Handles;
            var laneElement = LaneOf(editor, lane.LaneId);
            string Place(bool isStart) => handles is null || laneElement is null || handles.GetHandleBounds(isStart, laneElement) is not { } bounds
                ? "none"
                : $"{F(bounds.X, "0.#")} to {F(bounds.X + bounds.Width, "0.#")}";
            return (handles?.Placement.ToString() ?? "no handles", handles?.IsStrong == true, handles?.IsRaised == true, block?.ToolTipText ?? string.Empty, block is null ? -1 : Canvas.GetZIndex(block), Place(true), Place(false));
        });
        void PointerOver(int index, bool isOver)
        {
            OnUi(() => Descendant<StudioRangeBlock>(editor.Window.Content, Id(index))?.ShowPointerOver(isOver));
            Thread.Sleep(60);
        }

        string Between(double from, double to) => $"{F(from, "0.#")} to {F(to, "0.#")}";
        bool IsAt(string place, double from, double to)
        {
            var parts = place.Split(" to ");
            return parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var end)
                && Math.Abs(start - from) <= 1 && Math.Abs(end - to) <= 1;
        }

        var hasHandles = $"Drag to move this {lane.What}. Drag the handle at either end to change {lane.WhenOrWhere} it starts or stops.";
        var hasNone = $"Drag to move this {lane.What}. Select it to show the handles that change {lane.WhenOrWhere} it starts or stops.";
        SelectNothing();
        (left, width) = AsOpened(wide);
        (narrowLeft, narrowWidth) = AsOpened(narrow);
        Thread.Sleep(100);
        var (wideAtRest, narrowAtRest) = (Read(wide), Read(narrow));

        // The block's rectangle as a screen reader is given it, along the lane: the block's own while it has no handles outside it.
        var narrowAlone = Until(() => Block(narrow), now => Math.Abs(now.Left - narrowLeft) <= 1 && Math.Abs(now.Width - narrowWidth) <= 1, 1);
        PointerOver(wide, true);
        var wideUnderPointer = Read(wide);
        PointerOver(wide, false);
        var wideLeft = Read(wide);
        Select(wide);
        Thread.Sleep(100);
        var (wideSelected, narrowBeside) = (Read(wide), Read(narrow));
        Select(narrow);
        Thread.Sleep(100);
        var (wideBeside, narrowSelected) = (Read(wide), Read(narrow));
        var narrowWithHandles = Until(() => Block(narrow), now => Math.Abs(now.Left - (narrowLeft - 8)) <= 1 && Math.Abs(now.Width - (narrowWidth + 16)) <= 1, 1);

        // For a person to look at: the window with the narrow block selected, its handles outside its ends.
        var picture = Path.Combine(_output, $"lane-handles-{lane.What.Replace(' ', '-')}.png");
        if (editor.Camera.Take() is { } shot)
        {
            shot.Save(picture);
            _report.Line($"  saved {picture}");
        }
        var children = (Find(editor, Id(wide))?.Children().Count ?? -1, Find(editor, Id(narrow))?.Children().Count ?? -1);
        var raw = (editor.Root.FindRawAsItIs("StartHandle") is not null, editor.Root.FindRawAsItIs("EndHandle") is not null, editor.Root.FindRawAsItIs("BlockHandles") is not null);
        SelectNothing();
        _report.Check(
            $"on the {lane.What} lane, the handles are drawn where a press takes hold of an end: inside the two ends of a block of 28 or more, 8 wide each, faint at rest, stronger while the pointer is over the block, and at their strongest while it is selected; a narrower block has none until it is selected, and then one outside each end, 8 wide; the selected block is drawn over the others; the tooltip of a block says where its handles are, or that it has to be selected for them; and a screen reader is given the block as one item with nothing inside it, whose rectangle takes in the two handles outside a narrow selected block",
            wideAtRest is { Placement: "Inside", IsStrong: false, IsRaised: false, ZIndex: 0 } && wideAtRest.ToolTip == hasHandles
                && IsAt(wideAtRest.Start, left, left + 8) && IsAt(wideAtRest.End, left + width - 8, left + width)
                && narrowAtRest is { Placement: "None", Start: "none", End: "none", ZIndex: 0 } && narrowAtRest.ToolTip == hasNone
                && wideUnderPointer is { Placement: "Inside", IsStrong: false, IsRaised: true }
                && wideLeft is { IsStrong: false, IsRaised: false }
                && wideSelected is { Placement: "Inside", IsStrong: true, ZIndex: 1 } && narrowBeside is { Placement: "None", ZIndex: 0 }
                && narrowSelected is { Placement: "Outside", IsStrong: true, ZIndex: 1 } && narrowSelected.ToolTip == hasHandles
                && IsAt(narrowSelected.Start, narrowLeft - 8, narrowLeft) && IsAt(narrowSelected.End, narrowLeft + narrowWidth, narrowLeft + narrowWidth + 8)
                && wideBeside is { Placement: "Inside", IsStrong: false, ZIndex: 0 }
                && children == (0, 0) && raw == (false, false, false)
                && Math.Abs(narrowAlone.Left - narrowLeft) <= 1 && Math.Abs(narrowAlone.Width - narrowWidth) <= 1
                && Math.Abs(narrowWithHandles.Left - (narrowLeft - 8)) <= 1 && Math.Abs(narrowWithHandles.Width - (narrowWidth + 16)) <= 1,
            $"the wide block, at {Between(left, left + width)}: at rest {wideAtRest.Placement}, handles at {wideAtRest.Start} and {wideAtRest.End}, strong {wideAtRest.IsStrong}, raised {wideAtRest.IsRaised}, \"{wideAtRest.ToolTip}\"; "
                + $"with the pointer over it raised {wideUnderPointer.IsRaised}, and after it left {wideLeft.IsRaised}; selected: strong {wideSelected.IsStrong}, drawn over the others {wideSelected.ZIndex == 1}; "
                + $"the narrow block, at {Between(narrowLeft, narrowLeft + narrowWidth)}: at rest {narrowAtRest.Placement}, \"{narrowAtRest.ToolTip}\"; selected: {narrowSelected.Placement}, handles at {narrowSelected.Start} and {narrowSelected.End}, "
                + $"strong {narrowSelected.IsStrong}, drawn over the others {narrowSelected.ZIndex == 1}, \"{narrowSelected.ToolTip}\"; "
                + $"elements inside the two blocks for a screen reader: {children.Item1} and {children.Item2}; a handle known to UI Automation at all: {raw.Item1 || raw.Item2 || raw.Item3}; "
                + $"the narrow block's rectangle for a screen reader: {Between(narrowAlone.Left, narrowAlone.Left + narrowAlone.Width)} while it is not selected, and {Between(narrowWithHandles.Left, narrowWithHandles.Left + narrowWithHandles.Width)} while it is");
    }
}
