using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Host;
using Windows.System;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 13. Cuts: the lane between the zoom lane and the trim bar, Cut, the keys X and Delete, and the
// Cut panel of the inspector. The lane under a pointer, what a press does to the selection, the
// gaps in the trim bar and a trim that is refused are in CutLane.cs; playing over a cut and an
// export with cuts and scenes are in CutPlaying.cs.
internal sealed partial class WindowChecks
{
    private void Cuts()
    {
        Timeline.Mark("13: cuts");
        _layoutErrors.Clear();
        AddingCuts();
        CutLaneUnderAPointer();
        PressesAndWhatIsSelected();
        TrimBarShowsWhatIsKept();
        TrimThatLeavesTooLittle();
        PlayingOverACut();
        ExportWithCutsAndScenes();
    }

    // ---------------------------------------------------------------------------------------
    // What the editor holds, and how a cut is worded
    // ---------------------------------------------------------------------------------------

    private static StudioTimeRange Cut(double start, double end) => new() { Start = start, End = end };

    /// <summary>The cuts the editor holds now. Read on the UI thread.</summary>
    private StudioTimeRange[] CutsOf(Editor editor) => OnUi(() => editor.Window.ViewModel.Cuts.ToArray());

    private int? SelectedCutOf(Editor editor) => OnUi(() => editor.Window.ViewModel.SelectedCutIndex);

    /// <summary>A cut's two times, to a millionth of a second.</summary>
    private static string Describe(StudioTimeRange cut) => $"[{F(cut.Start, "0.######")} to {F(cut.End, "0.######")}]";

    private static string Describe(StudioTimeRange[] cuts) => cuts.Length == 0 ? "none" : string.Join(", ", cuts.Select(Describe));

    /// <summary>A cut as the lane should name it.</summary>
    private static string CutName(double start, double end) => string.Create(CultureInfo.InvariantCulture, $"Cut, {start:0.0} to {end:0.0} seconds");

    /// <summary>The cut lane as it should read for a list of cuts, with a star before the selected one.</summary>
    private static string CutLaneFor(StudioTimeRange[] cuts, int? selected) =>
        cuts.Length == 0 ? "empty" : string.Join(" | ", cuts.Select((cut, index) => (index == selected ? "*" : string.Empty) + CutName(cut.Start, cut.End)));

    /// <summary>Waits for the editor to hold the given cuts and for the lane to show them. Null when both do, otherwise what is different.</summary>
    private string? CutsAre(Editor editor, StudioTimeRange[] cuts, int? selected)
    {
        var lane = WaitForLane(editor, CutLaneFor(cuts, selected), 2, CutLane);
        var held = Until(() => Describe(CutsOf(editor)), now => now == Describe(cuts), 1);
        var chosen = Until(() => SelectedCutOf(editor), index => index == selected, 1);
        return lane == CutLaneFor(cuts, selected) && held == Describe(cuts) && chosen == selected
            ? null
            : $"the lane is {lane} and should be {CutLaneFor(cuts, selected)}; the editor holds {held} and should hold {Describe(cuts)}; the selected cut is {(chosen is { } index ? (index + 1).ToString(CultureInfo.InvariantCulture) : "none")}";
    }

    /// <summary>What is wrong with what the Cut panel shows, read through UI Automation, or null.</summary>
    /// <param name="range">The selected cut's times, or null when none is selected.</param>
    private static string? CutSectionShows(Editor editor, string position, string? range, string? start = null, string? end = null, string? length = null)
    {
        var positionRead = Until(() => NameOf(editor, "StudioCutPositionText", 0.5), text => text == position, 1.5);
        string?[] problems =
        [
            positionRead == position ? null : $"the position reads \"{positionRead}\"",
            range is null
                ? Absent(editor, "StudioCutRangeText") && Absent(editor, "StudioCutStartText", 0.2) && Absent(editor, "StudioCutEndText", 0.2) && Absent(editor, "StudioCutLengthText", 0.2) && Absent(editor, "StudioDeleteCutButton", 0.2) ? null : "it shows a cut's controls"
                : Until(() => NameOf(editor, "StudioCutRangeText", 0.5), text => text == range, 1) == range ? null : $"the times read \"{NameOf(editor, "StudioCutRangeText", 0)}\"",
            start is null || Until(() => NameOf(editor, "StudioCutStartText", 0.5), text => text == start, 1) == start ? null : $"Start reads \"{NameOf(editor, "StudioCutStartText", 0)}\"",
            end is null || Until(() => NameOf(editor, "StudioCutEndText", 0.5), text => text == end, 1) == end ? null : $"End reads \"{NameOf(editor, "StudioCutEndText", 0)}\"",
            length is null || Until(() => NameOf(editor, "StudioCutLengthText", 0.5), text => text == length, 1) == length ? null : $"the length reads \"{NameOf(editor, "StudioCutLengthText", 0)}\"",
            range is null || Find(editor, "StudioDeleteCutButton", 0.5) is { IsEnabled: true, Name: "Delete cut" } ? null : "there is no Delete cut button",
        ];
        var detail = string.Join("; ", problems.Where(problem => problem is not null));
        return detail.Length == 0 ? null : detail;
    }

    // ---------------------------------------------------------------------------------------
    // Adding cuts, the lane, the panel and the keys
    // ---------------------------------------------------------------------------------------

    private void AddingCuts()
    {
        Timeline.Mark("13: adding cuts");
        if (OpenReady(NewScreenProject("Cuts"), "cuts") is not { } editor)
        {
            return;
        }

        using var heard = UiaEvents.Listen(_uia, editor.Root);
        SetSlider(editor, "StudioPlayhead", 2.0);

        // No cuts.
        var list = Find(editor, CutLane);
        var text = Find(editor, "StudioCutLaneEmptyText", 1);
        _report.Check(
            "a recording without cuts: the lane is a list called Cuts with no items, which can take the keyboard focus, and it says how to make a cut, as a text and as the list's description",
            list is { ControlType: ControlTypeNames.List, Name: "Cuts", IsKeyboardFocusable: true, IsEnabled: true } && list.IsSelectionRequired == false && LaneItems(editor, CutLane).Count == 0
                && text?.Name == StudioEditorText.NoCutsHint && list.HelpText == StudioEditorText.NoCutsHint && list.Patterns.Contains("Selection", StringComparison.Ordinal) && list.SelectedNames.Length == 0,
            $"{list} with {LaneItems(editor, CutLane).Count} item(s), described as \"{list?.HelpText}\"; the text: \"{text?.Name}\"; patterns({list?.Patterns})");
        var section = CutSectionShows(editor, "No cuts yet", range: null);
        _report.Check(
            "without cuts the Cut panel says so, has nothing to step to and no cut's controls, says how to add the first one, and offers Add cut; Cut in the transport row has the same name for a screen reader",
            section is null && Find(editor, "StudioPreviousCutButton") is { IsEnabled: false, Name: "Previous cut" } && Find(editor, "StudioNextCutButton") is { IsEnabled: false, Name: "Next cut" }
                && Find(editor, "StudioCutSectionAddButton") is { IsEnabled: true, Name: "Add cut" } && Find(editor, "StudioAddCutButton") is { IsEnabled: true, Name: "Add cut" } && Absent(editor, "StudioCutHint", 0.3)
                && NameOf(editor, "StudioCutEmptyHint", 0.5) == CutEmptyHint,
            section ?? $"\"No cuts yet\"; {Find(editor, "StudioCutSectionAddButton")}, {Find(editor, "StudioAddCutButton")}; the hint: \"{NameOf(editor, "StudioCutEmptyHint", 0)}\"");
        var timeBefore = TimeText(editor);

        // X, at 2.0 s.
        Timeline.Mark("13: X starts a cut at the playhead");
        var mark = heard.Mark();
        var key = Key(editor, StudioShortcutKey.X);
        StudioTimeRange[] one = [Cut(2, 3)];
        var added = CutsAre(editor, one, 0);
        var said = Said(heard, mark, StudioEditorText.CutAddedMessage);
        var head = Playhead(editor);
        var time = Until(() => TimeText(editor), read => read != timeBefore, 1);
        list = Find(editor, CutLane);
        _report.Check(
            "what X runs starts a cut at the playhead that lasts one second; the lane gets one item, named as the editor words the cut, and it is the selected one; the playhead stays where the cut starts",
            key == StudioShortcutAction.AddCut && added is null && CutName(2, 3) == StudioEditorText.GetCutDescription(one[0]) && list?.SelectedNames.SequenceEqual([CutName(2, 3)]) == true
                && Find(editor, "StudioCut_0") is { ControlType: ControlTypeNames.ListItem, IsEnabled: true } && Absent(editor, "StudioCutLaneEmptyText") && list.HelpText.Length == 0 && head == 2,
            added ?? $"the key ran {key}; the editor holds {Describe(CutsOf(editor))}; the lane: {LaneText(editor, CutLane)}; playhead {Seconds(head)} s");
        _report.Check("a screen reader is told \"Cut added.\"", said.Said, $"sent: {said.Heard}");
        _report.Check(
            "the time counts the video's time, which the cut makes a second shorter",
            timeBefore == StudioEditorText.GetTimeText(2, 12) && time == StudioEditorText.GetTimeText(2, 11),
            $"\"{timeBefore}\" before the cut, \"{time}\" after it");
        section = CutSectionShows(editor, "Cut 1 of 1", "2.0 to 3.0 seconds", "Start 2.0 seconds", "End 3.0 seconds", "1.0 seconds long");
        _report.Check(
            "the Cut panel shows the new cut: which it is and its times, its start, its end, how long it is, and Delete cut; the hint for a recording without cuts is gone",
            section is null && Absent(editor, "StudioCutHint", 0.3) && Absent(editor, "StudioCutEmptyHint", 0.3),
            section ?? "\"Cut 1 of 1\", \"2.0 to 3.0 seconds\", \"Start 2.0 seconds\", \"End 3.0 seconds\", \"1.0 seconds long\"");

        // X again, where the cut now is.
        Timeline.Mark("13: X where a cut is, and where none fits");
        mark = heard.Mark();
        var again = Key(editor, StudioShortcutKey.X);
        var already = Said(heard, mark, StudioEditorText.CutAlreadyThereMessage);
        var same = CutsAre(editor, one, 0);
        _report.Check(
            "what X runs where a cut already is adds nothing, keeps that cut selected, and tells a screen reader that there is one",
            again == StudioShortcutAction.AddCut && already.Said && same is null && Playhead(editor) == 2,
            same ?? $"sent: {already.Heard}; the lane: {LaneText(editor, CutLane)}");

        // A twentieth of a second before the end of the recording there is no room for the shortest cut, which is a tenth.
        SetSlider(editor, "StudioPlayhead", RecordingLength - 0.05);
        var canCut = Until(() => Find(editor, "StudioAddCutButton", 0.5)?.IsEnabled, enabled => enabled == false, 2);
        var canCutInSection = Find(editor, "StudioCutSectionAddButton", 0.5)?.IsEnabled;
        mark = heard.Mark();
        var noRoomKey = Key(editor, StudioShortcutKey.X);
        var noRoom = Said(heard, mark, StudioEditorText.NoRoomForCutMessage);
        same = CutsAre(editor, one, 0);
        _report.Check(
            "where less than the shortest cut fits, both Cut buttons are disabled, and what X runs adds nothing and tells a screen reader that there is no room",
            canCut == false && canCutInSection == false && noRoomKey == StudioShortcutAction.AddCut && noRoom.Said && same is null,
            $"with the playhead at {Seconds(Playhead(editor))} s: Cut enabled {canCut}, in the Cut panel {canCutInSection}; sent: {noRoom.Heard}; {same ?? "the lane: " + LaneText(editor, CutLane)}");

        // The two buttons.
        Timeline.Mark("13: the two Cut buttons");
        SetSlider(editor, "StudioPlayhead", 6.0);
        var enabledAgain = Until(() => Find(editor, "StudioAddCutButton", 0.5)?.IsEnabled, enabled => enabled == true, 2);
        mark = heard.Mark();
        var pressed = Invoke(editor, "StudioAddCutButton");
        StudioTimeRange[] two = [Cut(2, 3), Cut(6, 7)];
        var second = CutsAre(editor, two, 1);
        var saidSecond = Said(heard, mark, StudioEditorText.CutAddedMessage);
        var headSecond = Playhead(editor);
        SetSlider(editor, "StudioPlayhead", 11.5);
        var pressedInSection = Invoke(editor, "StudioCutSectionAddButton");
        StudioTimeRange[] three = [Cut(2, 3), Cut(6, 7), Cut(11.5, 12)];
        var third = CutsAre(editor, three, 2);
        section = CutSectionShows(editor, "Cut 3 of 3", "11.5 to 12.0 seconds", "Start 11.5 seconds", "End 12.0 seconds", "0.5 seconds long");
        _report.Check(
            "Cut in the transport row and Add cut in the Cut panel each start a cut at the playhead, select it, leave the playhead where it is and say so; a cut that reaches the end of the recording stops there, and the panel says which cut is selected, when it is and how long",
            enabledAgain == true && pressed && second is null && saidSecond.Said && headSecond == 6 && pressedInSection && third is null && section is null && Playhead(editor) == 11.5
                && time != TimeText(editor) && TimeText(editor) == StudioEditorText.GetTimeText(9.5, 9.5),
            second ?? third ?? section ?? $"the lane: {LaneText(editor, CutLane)}; sent: {saidSecond.Heard}; the time reads \"{TimeText(editor)}\"");

        LaneIsOnTheTimeScale(editor, CutLane, "StudioCut_", "cut", [(2, 3), (6, 7), (11.5, 12)], inset: 0);
        CutLaneKeys(editor, heard, three);
        CutLaneItems(editor, three);
        PreviousAndNextCut(editor, heard, three);
        CutStartAndEndButtons(editor, heard, three);
        OneSelection(editor, three);
        DeletingCuts(editor, heard, three);
        UndoAndRedoOfCuts(editor);
        CutSectionForAScreenReader(editor);

        // What was saved, once the editor has saved by itself.
        var live = CutsOf(editor);
        var wanted = Describe(live);
        var saved = Until(() => Describe(_services.Store.Load(editor.Id).Edits.Cuts), now => now == wanted, 3, 100);
        _report.Check("the editor saves the cuts by itself: the project file holds the cuts the editor holds", saved == wanted && live.Length > 0, saved == wanted ? saved : $"saved: {saved} | the editor holds: {wanted}");
        CloseQuietly(editor);
    }

    /// <summary>What the cut lane does with the arrow keys, Home and End while it has the focus. The third cut is selected.</summary>
    private void CutLaneKeys(Editor editor, UiaEvents heard, StudioTimeRange[] cuts)
    {
        Timeline.Mark("13: the cut lane's keys");
        var steps = new List<string>();
        var wrong = new List<string>();
        var mark = heard.Mark();
        void Press(VirtualKey key, int wanted)
        {
            var handled = LaneKey(editor, key, CutLane);
            var selected = Until(() => SelectedCutOf(editor), index => index == wanted, 1);
            var head = Until(() => Playhead(editor), value => Same(value, cuts[wanted].Start), 1);
            var marked = LaneItems(editor, CutLane).Select((item, at) => item.IsSelected == true ? at : -1).Where(at => at >= 0).ToArray();
            steps.Add($"{key}: cut {(selected is { } s ? (s + 1).ToString(CultureInfo.InvariantCulture) : "none")} at {Seconds(head)} s");
            if (!handled || selected != wanted || !Same(head, cuts[wanted].Start) || !marked.SequenceEqual([wanted]))
            {
                wrong.Add($"{key}: handled {handled}, selected {selected?.ToString(CultureInfo.InvariantCulture) ?? "none"} (the lane marks {string.Join(",", marked)}), playhead {Seconds(head)} s; wanted {wanted} at {Seconds(cuts[wanted].Start)} s");
            }
        }

        // Each key selects a cut and moves the playhead to where it starts, the first picture the video leaves out.
        Press(VirtualKey.Home, 0);
        Press(VirtualKey.Right, 1);
        Press(VirtualKey.Right, 2);
        Press(VirtualKey.Right, 2);
        Press(VirtualKey.Left, 1);
        Press(VirtualKey.End, 2);
        Press(VirtualKey.Home, 0);
        Press(VirtualKey.Left, 0);

        // With none selected, the arrows start from the playhead: between the first cut and the second.
        Find(editor, "StudioCut_0")?.RemoveFromSelection();
        Until(() => SelectedCutOf(editor), index => index is null, 1);
        SetSlider(editor, "StudioPlayhead", 5.0);
        Press(VirtualKey.Right, 1);
        Find(editor, "StudioCut_1")?.RemoveFromSelection();
        Until(() => SelectedCutOf(editor), index => index is null, 1);
        SetSlider(editor, "StudioPlayhead", 5.0);
        Press(VirtualKey.Left, 0);
        var others = new[] { VirtualKey.Delete, VirtualKey.Space, VirtualKey.S, VirtualKey.X, VirtualKey.Z, VirtualKey.Up, VirtualKey.Down }.Where(key => LaneKey(editor, key, CutLane)).ToArray();
        var selectedEvents = heard.WaitFor(mark, "selected", e => false, 0.4).Where(e => e.Id.StartsWith("StudioCut_", StringComparison.Ordinal)).ToArray();
        _report.Check(
            "with the focus on the cut lane, Home and End select the first and the last cut and Left and Right the one before and after, each shown where it starts; with none selected the arrows start from the playhead; at either end, and for any other key, the lane does nothing",
            wrong.Count == 0 && others.Length == 0 && Describe(CutsOf(editor)) == Describe(cuts),
            wrong.Count == 0 ? $"{string.Join("; ", steps)}; keys the lane leaves to the window: Delete, Space, S, X, Z, Up, Down{(others.Length == 0 ? string.Empty : "; but it took " + string.Join(", ", others))}" : string.Join("; ", wrong));

        // The keys changed the selection eight times: to cut 1, 2, 3, 2, 3, 1, and from none to 2 and to 1.
        string[] changes = ["StudioCut_0", "StudioCut_1", "StudioCut_2", "StudioCut_1", "StudioCut_2", "StudioCut_0", "StudioCut_1", "StudioCut_0"];
        var each = selectedEvents.Length / changes.Length;
        _report.Check(
            "a screen reader is told each time another cut becomes the selected one, by its name",
            each is 1 or 2 && selectedEvents.Select(e => e.Id).SequenceEqual(changes.SelectMany(id => Enumerable.Repeat(id, each))) && selectedEvents.All(e => e.Text.StartsWith("Cut, ", StringComparison.Ordinal)),
            $"{selectedEvents.Length} events for the 8 changes of the selection the keys made: {string.Join(", ", selectedEvents.Select(e => e.Id.Replace("StudioCut_", "cut ", StringComparison.Ordinal)))}{(heard.Problem is null ? string.Empty : $" ({heard.Problem})")}");
    }

    /// <summary>A cut's block as a list item: it can be selected, taken out of the selection, and pressed.</summary>
    private void CutLaneItems(Editor editor, StudioTimeRange[] cuts)
    {
        Timeline.Mark("13: the cut lane's items, through UI Automation");
        var items = LaneItems(editor, CutLane);
        var patterns = items.Select(item => item.Patterns).Distinct().ToArray();
        var ids = items.Select(item => item.Id).ToArray();
        SetSlider(editor, "StudioPlayhead", 5.0);
        var head = Playhead(editor);
        var selected = Find(editor, "StudioCut_2")?.Select() ?? false;
        var afterSelect = (Until(() => SelectedCutOf(editor), index => index == 2, 1), Playhead(editor));
        var invoked = Find(editor, "StudioCut_1")?.Invoke() ?? false;
        var afterInvoke = (Until(() => SelectedCutOf(editor), index => index == 1, 1), Until(() => Playhead(editor), value => Same(value, cuts[1].Start), 1));
        var removed = Find(editor, "StudioCut_1")?.RemoveFromSelection() ?? false;
        var afterRemove = (Until(() => SelectedCutOf(editor), index => index is null, 1), Playhead(editor));
        var section = CutSectionShows(editor, "3 cuts", range: null);
        var hint = NameOf(editor, "StudioCutHint", 1);
        _report.Check(
            "each cut's block is a list item with an automation id that can be selected and pressed: selecting it leaves the playhead where it is, pressing it also moves the playhead to where the cut starts, and taking it out of the selection leaves none selected; the Cut panel then says how many cuts there are and how to select one",
            items.Count == 3 && patterns.Length == 1 && patterns[0] == "Invoke,SelectionItem" && ids.SequenceEqual(["StudioCut_0", "StudioCut_1", "StudioCut_2"]) && items.All(item => !item.IsKeyboardFocusable && item.Children().Count == 0)
                && selected && afterSelect.Item1 == 2 && Same(afterSelect.Item2, head)
                && invoked && afterInvoke.Item1 == 1 && Same(afterInvoke.Item2, cuts[1].Start)
                && removed && afterRemove.Item1 is null && Same(afterRemove.Item2, afterInvoke.Item2) && section is null && hint == StudioEditorText.SelectCutHint,
            $"ids {string.Join(", ", ids)}, patterns({string.Join(" / ", patterns)}); selected: cut {afterSelect.Item1 + 1} with the playhead at {Seconds(afterSelect.Item2)} s ({Seconds(head)} s before); "
                + $"pressed: cut {afterInvoke.Item1 + 1} at {Seconds(afterInvoke.Item2)} s; taken out: {(afterRemove.Item1 is null ? "none selected" : "still selected")}; {section ?? "\"3 cuts\""}; the hint: \"{hint}\"");
    }

    /// <summary>Previous cut and Next cut: what they select, what is read out, and where the keyboard focus goes at the ends. No cut is selected.</summary>
    private void PreviousAndNextCut(Editor editor, UiaEvents heard, StudioTimeRange[] cuts)
    {
        Timeline.Mark("13: Previous cut and Next cut");
        var wrong = new List<string>();
        var steps = new List<string>();
        void Step(string button, int wanted, bool canGoBack, bool canGoOn)
        {
            var mark = heard.Mark();
            var pressed = Invoke(editor, button);
            var selected = Until(() => SelectedCutOf(editor), index => index == wanted, 1);
            var head = Until(() => Playhead(editor), value => Same(value, cuts[wanted].Start), 1);
            var sentence = StudioEditorText.GetCutStepText(wanted, cuts.Length, cuts[wanted]);
            var said = Said(heard, mark, sentence);
            var position = Until(() => NameOf(editor, "StudioCutPositionText", 0.5), name => name == $"Cut {wanted + 1} of 3", 1);
            var previous = Until(() => Find(editor, "StudioPreviousCutButton", 0.5)?.IsEnabled, enabled => enabled == canGoBack, 1);
            var next = Until(() => Find(editor, "StudioNextCutButton", 0.5)?.IsEnabled, enabled => enabled == canGoOn, 1);
            steps.Add($"\"{position}\" at {Seconds(head)} s");
            if (!pressed || selected != wanted || !Same(head, cuts[wanted].Start) || !said.Said || position != $"Cut {wanted + 1} of 3" || previous != canGoBack || next != canGoOn)
            {
                wrong.Add($"{button}: pressed {pressed}, cut {selected + 1} at {Seconds(head)} s, \"{position}\", Previous enabled {previous}, Next enabled {next}, sent: {said.Heard} (wanted \"{sentence}\")");
            }
        }

        // None is selected and the playhead is in the second cut: Next is that cut, the one at the playhead.
        var both = Find(editor, "StudioPreviousCutButton")?.IsEnabled == true && Find(editor, "StudioNextCutButton")?.IsEnabled == true;
        var focus = new List<string> { FocusOn(editor, "StudioNextCutButton") };
        Step("StudioNextCutButton", 1, canGoBack: true, canGoOn: true);
        focus.Add(FocusedId(editor));
        Step("StudioNextCutButton", 2, canGoBack: true, canGoOn: false);
        focus.Add(Until(() => FocusedId(editor), id => id == "StudioPreviousCutButton", 1.5));
        Step("StudioPreviousCutButton", 1, canGoBack: true, canGoOn: true);
        focus.Add(FocusedId(editor));
        Step("StudioPreviousCutButton", 0, canGoBack: false, canGoOn: true);
        focus.Add(Until(() => FocusedId(editor), id => id == "StudioNextCutButton", 1.5));
        _report.Check(
            "Previous cut and Next cut step through the cuts and move the playhead to where each starts; the text between them says which cut it is, each button is disabled where there is no cut to step to, and a screen reader is told the cut it lands on, such as \"Cut 2 of 3, 6.0 to 7.0 seconds\"",
            both && wrong.Count == 0 && StudioEditorText.GetCutStepText(1, 3, cuts[1]) == "Cut 2 of 3, 6.0 to 7.0 seconds",
            wrong.Count == 0 ? string.Join(", then ", steps) : string.Join(" | ", wrong));
        string[] focusWanted = ["StudioNextCutButton", "StudioNextCutButton", "StudioPreviousCutButton", "StudioPreviousCutButton", "StudioNextCutButton"];
        _report.Check(
            "the keyboard focus stays on Next cut and Previous cut while they have a cut to step to, and goes to the other one when the last or the first cut is reached and the pressed button is switched off",
            focus.SequenceEqual(focusWanted),
            $"the focus was on: {string.Join(", ", focus.Select(id => id.Replace("Studio", string.Empty, StringComparison.Ordinal)))}");
    }

    /// <summary>The Start and End rows of the Cut panel, for the second cut, which runs from 6.0 to 7.0 s.</summary>
    private void CutStartAndEndButtons(Editor editor, UiaEvents heard, StudioTimeRange[] cuts)
    {
        Timeline.Mark("13: the start and the end of a cut");
        Find(editor, "StudioCut_1")?.Invoke();
        Until(() => SelectedCutOf(editor), index => index == 1, 1);
        string[] ids = ["StudioCutStartEarlierButton", "StudioCutStartLaterButton", "StudioCutStartAtPlayheadButton", "StudioCutEndEarlierButton", "StudioCutEndLaterButton", "StudioCutEndAtPlayheadButton"];
        var names = ids.Select(id => NameOf(editor, id)).ToArray();
        var steps = new List<string>();
        var wrong = new List<string>();
        void Step(string what, string button, double wantedStart, double wantedEnd, double? wantedHead, bool isStart)
        {
            var headBefore = Playhead(editor);
            var mark = heard.Mark();
            var pressed = Invoke(editor, button);
            var cut = Until(() => CutsOf(editor)[1], now => Same(now.Start, wantedStart) && Same(now.End, wantedEnd), 1);
            var headWanted = wantedHead ?? headBefore;
            var head = Until(() => Playhead(editor), value => Same(value, headWanted), 1);
            var sentence = isStart ? $"Start {StudioEditorModel.GetSecondsText(wantedStart)}" : $"End {StudioEditorModel.GetSecondsText(wantedEnd)}";
            var said = Said(heard, mark, sentence);
            var shown = Until(() => NameOf(editor, isStart ? "StudioCutStartText" : "StudioCutEndText", 0.5), text => text == sentence, 1);
            steps.Add($"{what}: {Seconds(cut.Start)} to {Seconds(cut.End)} s, playhead {Seconds(head)} s");
            if (!pressed || !Same(cut.Start, wantedStart) || !Same(cut.End, wantedEnd) || !Same(head, headWanted) || !said.Said || shown != sentence || SelectedCutOf(editor) != 1)
            {
                wrong.Add($"{what}: pressed {pressed}, the cut is {Seconds(cut.Start)} to {Seconds(cut.End)} s (wanted {Seconds(wantedStart)} to {Seconds(wantedEnd)}), playhead {Seconds(head)} s (wanted {Seconds(headWanted)}), the panel reads \"{shown}\", sent: {said.Heard}");
            }
        }

        // A step moves the playhead to the end it moved, as a trim handle does. At playhead leaves it where it is.
        Step("Start 0.1 s later", ids[1], 6.1, 7.0, 6.1, isStart: true);
        Step("Start 0.1 s earlier, twice", ids[0], 6.0, 7.0, 6.0, isStart: true);
        Step("Start 0.1 s earlier, twice", ids[0], 5.9, 7.0, 5.9, isStart: true);
        Step("End 0.1 s later", ids[4], 5.9, 7.1, 7.1, isStart: false);
        Step("End 0.1 s earlier", ids[3], 5.9, 7.0, 7.0, isStart: false);
        SetSlider(editor, "StudioPlayhead", 6.5);
        Step("Start at the playhead, at 6.5 s", ids[2], 6.5, 7.0, null, isStart: true);
        var length = NameOf(editor, "StudioCutLengthText");
        var range = NameOf(editor, "StudioCutRangeText");

        // A cut is at least a tenth of a second long: its end stops that far after its start, and its start that far before its end.
        Step("End at the playhead, which is where the cut starts", ids[5], 6.5, 6.6, null, isStart: false);
        Step("Start 0.1 s later, against the end", ids[1], 6.5, 6.6, 6.5, isStart: true);

        // Cuts do not overlap: an end stops where the next cut starts, and the two then touch.
        SetSlider(editor, "StudioPlayhead", 11.8);
        Step("End at the playhead, at 11.8 s, inside the next cut", ids[5], 6.5, 11.5, null, isStart: false);
        var lane = LaneText(editor, CutLane);
        var time = TimeText(editor);
        _report.Check(
            "the six buttons of Start and End are named for what they do, move that end of the selected cut by 0.1 s or to the playhead, and say the time they leave it at; the playhead follows a step and stays for At playhead; a cut stays a tenth of a second long, and its end stops where the next cut starts",
            wrong.Count == 0 && names.SequenceEqual(["Start 0.1 seconds earlier", "Start 0.1 seconds later", "Start at playhead", "End 0.1 seconds earlier", "End 0.1 seconds later", "End at playhead"])
                && length == "0.5 seconds long" && range == "6.5 to 7.0 seconds" && lane == $"{CutName(2, 3)} | *{CutName(6.5, 11.5)} | {CutName(11.5, 12)}" && time == StudioEditorText.GetTimeText(5.5, 5.5),
            wrong.Count == 0 ? $"{string.Join("; ", steps)}; with the cut from 6.5 to 7.0 s the panel said \"{range}\", \"{length}\"; the lane: {lane}; the time reads \"{time}\"" : string.Join(" | ", wrong));

        // Eight of the nine presses changed the cut. Eight Undo bring it back, still selected.
        for (var undo = 0; undo < 8; undo++)
        {
            var before = Describe(CutsOf(editor));
            Invoke(editor, "StudioUndoButton");
            Until(() => Describe(CutsOf(editor)), now => now != before, 1);
        }

        var restored = CutsAre(editor, cuts, 1);
        _report.Check(
            "each press that moved an end is one undo step, and the one that changed nothing is none: eight Undo bring the cut back to 6.0 to 7.0 s, and it is still the selected one",
            restored is null,
            restored ?? $"the lane: {LaneText(editor, CutLane)}");
    }

    /// <summary>One thing is selected at most: a zoom or a cut. The second cut is selected.</summary>
    private void OneSelection(Editor editor, StudioTimeRange[] cuts)
    {
        Timeline.Mark("13: a zoom or a cut is selected, never both");

        // Z adds a zoom and selects it, which lets go of the cut.
        SetSlider(editor, "StudioPlayhead", 8.0);
        Key(editor, StudioShortcutKey.Z);
        var zoomName = "Zoom 2×, 8.0 to 11.0 seconds";
        var zoomLane = WaitForLane(editor, "*" + zoomName);
        var cutsThen = CutsAre(editor, cuts, null);
        var cutSection = CutSectionShows(editor, "3 cuts", range: null);
        var cutHint = NameOf(editor, "StudioCutHint", 1);
        var zoomShown = Until(() => Find(editor, "StudioZoomScaleSlider", 0.5), slider => slider is not null, 1) is not null;
        _report.Check(
            "adding a zoom selects it and lets go of the selected cut: the cut lane marks none, and the Cut panel shows no cut and says how to select one, while the Zoom panel shows the zoom",
            zoomLane == "*" + zoomName && cutsThen is null && cutSection is null && cutHint == StudioEditorText.SelectCutHint && zoomShown && SelectedZoomOf(editor) == 0,
            cutsThen ?? cutSection ?? $"zooms: {zoomLane}; cuts: {LaneText(editor, CutLane)}; the Cut panel's hint: \"{cutHint}\"");

        // Selecting a cut lets go of the zoom.
        var selected = Find(editor, "StudioCut_0")?.Select() ?? false;
        var cutsNow = CutsAre(editor, cuts, 0);
        var zoomLaneNow = WaitForLane(editor, zoomName);
        var zoomHint = NameOf(editor, "StudioZoomHint", 1);
        var zoomGone = Absent(editor, "StudioZoomScaleSlider") && Absent(editor, "StudioDeleteZoomButton", 0.3);
        var position = NameOf(editor, "StudioZoomPositionText");
        _report.Check(
            "selecting a cut lets go of the selected zoom: the zoom lane marks none, and the Zoom panel shows no zoom, says how many there are and how to select one, while the Cut panel shows the cut",
            selected && cutsNow is null && zoomLaneNow == zoomName && zoomGone && position == "1 zoom" && zoomHint.StartsWith("Select a zoom", StringComparison.Ordinal) && SelectedZoomOf(editor) is null
                && CutSectionShows(editor, "Cut 1 of 3", "2.0 to 3.0 seconds", "Start 2.0 seconds", "End 3.0 seconds", "1.0 seconds long") is null,
            cutsNow ?? $"zooms: {zoomLaneNow}, \"{position}\", the Zoom panel's hint: \"{zoomHint}\", its controls gone: {zoomGone}; cuts: {LaneText(editor, CutLane)}");

        // Back to the zoom, by its item: the cut is let go again. Then the zoom is deleted, so that the cuts are what is left.
        Find(editor, "StudioZoom_0")?.Select();
        var back = CutsAre(editor, cuts, null);
        var zoomBack = WaitForLane(editor, "*" + zoomName);
        var deleteKey = Key(editor, StudioShortcutKey.Delete);
        var zoomsLeft = Until(() => ZoomsOf(editor).Length, count => count == 0, 1);
        _report.Check(
            "selecting the zoom again lets go of the cut, and what Delete runs then removes the zoom and no cut",
            back is null && zoomBack == "*" + zoomName && deleteKey == StudioShortcutAction.RemoveSelectedZoom && zoomsLeft == 0 && Describe(CutsOf(editor)) == Describe(cuts),
            back ?? $"zooms before: {zoomBack}; Delete ran {deleteKey}; zooms left: {zoomsLeft}; cuts: {LaneText(editor, CutLane)}");
    }

    /// <summary>Delete, with a cut selected and with none, and the Delete cut button. No cut is selected, and there is no zoom.</summary>
    private void DeletingCuts(Editor editor, UiaEvents heard, StudioTimeRange[] cuts)
    {
        Timeline.Mark("13: deleting cuts");
        SetSlider(editor, "StudioPlayhead", 5.0);
        var timeBefore = TimeText(editor);
        Find(editor, "StudioCut_1")?.Select();
        Until(() => SelectedCutOf(editor), index => index == 1, 1);
        var mark = heard.Mark();
        var delete = Key(editor, StudioShortcutKey.Delete);
        StudioTimeRange[] two = [cuts[0], cuts[2]];
        var afterKey = CutsAre(editor, two, null);
        var deleted = Said(heard, mark, StudioEditorText.CutDeletedMessage);
        var deleteAgain = Key(editor, StudioShortcutKey.Delete);
        var section = CutSectionShows(editor, "2 cuts", range: null);
        var timeAfter = TimeText(editor);
        _report.Check(
            "what Delete runs removes the selected cut, which puts the part it removed back in the video: none is selected, the playhead stays, the video is a second longer, and a screen reader is told; with nothing selected the key is left alone",
            delete == StudioShortcutAction.RemoveSelectedCut && afterKey is null && deleted.Said && deleteAgain == StudioShortcutAction.None && Playhead(editor) == 5 && section is null
                && timeBefore == StudioEditorText.GetTimeText(4, 9.5) && timeAfter == StudioEditorText.GetTimeText(4, 10.5),
            afterKey ?? section ?? $"Delete ran {delete}, then {deleteAgain}; the lane: {LaneText(editor, CutLane)}; sent: {deleted.Heard}; the time read \"{timeBefore}\" and reads \"{timeAfter}\"");

        // Delete cut, with the keyboard focus on it. The playhead is before the last cut, so Next cut has one to step to.
        Find(editor, "StudioCut_0")?.Select();
        Until(() => SelectedCutOf(editor), index => index == 0, 1);
        var button = Find(editor, "StudioDeleteCutButton");
        var onDelete = FocusOn(editor, "StudioDeleteCutButton");
        mark = heard.Mark();
        var pressed = Invoke(editor, "StudioDeleteCutButton");
        StudioTimeRange[] one = [cuts[2]];
        var afterButton = CutsAre(editor, one, null);
        var saidAgain = Said(heard, mark, StudioEditorText.CutDeletedMessage);
        var focusAfter = Until(() => FocusedId(editor), id => id == "StudioNextCutButton", 1.5);
        _report.Check(
            "Delete cut removes the selected cut and says so; the button goes with the cut, and the keyboard focus goes to Next cut, which has a cut to step to",
            button is { Name: "Delete cut", IsEnabled: true } && onDelete == "StudioDeleteCutButton" && pressed && afterButton is null && saidAgain.Said && focusAfter == "StudioNextCutButton" && Absent(editor, "StudioDeleteCutButton", 0.5),
            afterButton ?? $"the lane: {LaneText(editor, CutLane)}; sent: {saidAgain.Heard}; the focus was on \"{onDelete}\" and is on \"{focusAfter}\"");

        // Both back, for what comes next.
        Invoke(editor, "StudioUndoButton");
        Until(() => CutsOf(editor).Length, count => count == 2, 1);
        Invoke(editor, "StudioUndoButton");
        var back = CutsAre(editor, cuts, null);
        _report.Check("two Undo bring both cuts back, and select neither", back is null, back ?? $"the lane: {LaneText(editor, CutLane)}");
    }

    /// <summary>
    /// What a screen reader is given of the timeline and the Cut panel with the second of
    /// three cuts selected, and the order of the tab stops there.
    /// </summary>
    private void CutSectionForAScreenReader(Editor editor)
    {
        Timeline.Mark("13: the names and the order of the tab stops, with a cut selected");
        Find(editor, "StudioCut_1")?.Invoke();
        Until(() => SelectedCutOf(editor), index => index == 1, 1);
        Until(() => Find(editor, "StudioDeleteCutButton", 0.5), found => found is not null, 1.5);

        // A recording without a camera has ten sliders, and a cut adds none.
        (string Id, string Name)[] names =
        [
            ("StudioAddCutButton", "Add cut"), (CutLane, "Cuts"), ("StudioCut_0", CutName(2, 3)), ("StudioCut_1", CutName(6, 7)), ("StudioCut_2", CutName(11.5, 12)),
            ("StudioPreviousCutButton", "Previous cut"), ("StudioCutPositionText", "Cut 2 of 3"), ("StudioCutRangeText", "6.0 to 7.0 seconds"), ("StudioNextCutButton", "Next cut"),
            ("StudioCutSectionAddButton", "Add cut"),
            ("StudioCutStartText", "Start 6.0 seconds"), ("StudioCutStartEarlierButton", "Start 0.1 seconds earlier"), ("StudioCutStartLaterButton", "Start 0.1 seconds later"), ("StudioCutStartAtPlayheadButton", "Start at playhead"),
            ("StudioCutEndText", "End 7.0 seconds"), ("StudioCutEndEarlierButton", "End 0.1 seconds earlier"), ("StudioCutEndLaterButton", "End 0.1 seconds later"), ("StudioCutEndAtPlayheadButton", "End at playhead"),
            ("StudioCutLengthText", "1.0 seconds long"), ("StudioDeleteCutButton", "Delete cut"),
        ];
        AuditState(editor, "the editor with a cut selected", "tree-cut.txt", 10, tree => Named(tree, names));

        // What Delete cut does to the video is said to a screen reader, and shown as its tooltip with its key.
        var delete = Find(editor, "StudioDeleteCutButton");
        var tip = OnUi(() => Descendant<Button>(editor.Window.Content, "StudioDeleteCutButton") is { } button ? ToolTipService.GetToolTip(button) as string : null);
        _report.Check(
            "Delete cut says that the part it removed is put back in the video: as its description for a screen reader, and in its tooltip, which also names its key",
            delete?.HelpText == "Puts the part it removed back in the video." && tip == "Delete this cut and put the part it removed back in the video (Delete)",
            $"{delete}, described as \"{delete?.HelpText}\"; the tooltip: \"{tip}\"");

        // The stops, in order: the Cut panel from top to bottom, after the Zoom panel and
        // before the Speed panel and Mute, which is the Audio panel's; and the timeline of a
        // recording without a camera, which has no Split and no scene lane.
        var order = TabStops(editor);
        var path = Path.Combine(_output, "tab-order-cuts.txt");
        File.WriteAllLines(path, order);
        string[] section =
        [
            "StudioPreviousCutButton", "StudioNextCutButton", "StudioCutSectionAddButton", "StudioCutStartEarlierButton", "StudioCutStartLaterButton", "StudioCutStartAtPlayheadButton",
            "StudioCutEndEarlierButton", "StudioCutEndLaterButton", "StudioCutEndAtPlayheadButton", "StudioDeleteCutButton",
        ];
        string[] timeline =
        [
            "StudioPlayPauseButton", "StudioPreviousFrameButton", "StudioNextFrameButton", "StudioAddZoomButton", "StudioAddCutButton", "StudioAddSpeedButton", "StudioStartHereButton", "StudioEndHereButton",
            ZoomLane, CutLane, SpeedLane, "StudioTrimStart", "StudioTrimEnd", "StudioPlayhead",
        ];
        var at = order.IndexOf(section[0]);
        var sectionTogether = at >= 0 && order.Skip(at).Take(section.Length).SequenceEqual(section);
        var afterZoom = order.IndexOf("StudioZoomSectionAddButton") is >= 0 and var zoom && zoom < at;
        var beforeMute = order.IndexOf("StudioMuteCheckBox") > at;
        var row = order.IndexOf(timeline[0]);
        var timelineTogether = row >= 0 && order.Skip(row).Take(timeline.Length).SequenceEqual(timeline);
        var blocks = order.Where(id => id.StartsWith("StudioCut_", StringComparison.Ordinal)).ToArray();
        _report.Check(
            "the keyboard focus, moved from stop to stop with each panel on show in turn, goes through the Cut panel from Previous cut to Delete cut with nothing between them, after the Zoom panel and before Mute; the timeline of a recording without a camera is the transport row, the zoom lane, the cut lane, the speed lane and the trim bar; the cut lane is one stop, and no single cut is one",
            sectionTogether && afterZoom && beforeMute && timelineTogether && blocks.Length == 0,
            sectionTogether && afterZoom && beforeMute && timelineTogether && blocks.Length == 0
                ? $"{order.Count} stops, saved as {Path.GetFileName(path)}: {string.Join(", ", order.Select(id => id.Replace("Studio", string.Empty, StringComparison.Ordinal)))}"
                : $"{(sectionTogether ? string.Empty : "the Cut panel's stops are not one after the other; ")}{(afterZoom ? string.Empty : "not after the Zoom panel; ")}{(beforeMute ? string.Empty : "not before Mute; ")}{(timelineTogether ? string.Empty : "the timeline's stops are not one after the other; ")}{(blocks.Length == 0 ? string.Empty : "also stops: " + string.Join(", ", blocks) + "; ")}the order: {string.Join(", ", order)}");
        Find(editor, "StudioCut_1")?.RemoveFromSelection();
        Until(() => SelectedCutOf(editor), index => index is null, 1);
    }

    /// <summary>Undo and Redo across an add, a move, an end and a delete: the lane and the selection follow. There are three cuts, none selected.</summary>
    private void UndoAndRedoOfCuts(Editor editor)
    {
        Timeline.Mark("13: undo and redo of cuts");
        var wrong = new List<string>();
        var steps = new List<string>();
        StudioTimeRange first = Cut(2, 3), second = Cut(6, 7), last = Cut(11.5, 12);
        void Expect(string what, StudioTimeRange[] cuts, int? selected)
        {
            var problem = CutsAre(editor, cuts, selected);
            steps.Add($"{what}: {LaneText(editor, CutLane)}");
            if (problem is not null)
            {
                wrong.Add($"{what}: {problem}");
            }
        }

        // Add: at 4.0 s, between the first two cuts. It is one second long, and selected.
        SetSlider(editor, "StudioPlayhead", 4.0);
        Key(editor, StudioShortcutKey.X);
        Expect("add", [first, Cut(4, 5), second, last], 1);

        // Move: half a second later, in two steps inside one gesture, which is what a drag of the block asks for.
        var moved = OnUi(() =>
        {
            var viewModel = editor.Window.ViewModel;
            viewModel.BeginGesture();
            var result = viewModel.MoveCut(1, 4.25);
            result = viewModel.MoveCut(result.Index ?? 1, 4.5);
            viewModel.EndGesture();
            return result;
        });
        Expect("move", [first, Cut(4.5, 5.5), second, last], 1);

        // Its end, 0.1 s later.
        Invoke(editor, "StudioCutEndLaterButton");
        Expect("end", [first, Cut(4.5, 5.6), second, last], 1);

        // Delete.
        Key(editor, StudioShortcutKey.Delete);
        Expect("delete", [first, second, last], null);

        // Back: the deleted cut returns. It is not selected: Undo brings back the project, and nothing was selected for it to follow.
        Invoke(editor, "StudioUndoButton");
        Expect("Undo of the delete", [first, Cut(4.5, 5.6), second, last], null);
        Find(editor, "StudioCut_1")?.Select();
        Expect("selected again", [first, Cut(4.5, 5.6), second, last], 1);
        Invoke(editor, "StudioUndoButton");
        Expect("Undo of the end, which keeps the cut selected", [first, Cut(4.5, 5.5), second, last], 1);
        Key(editor, StudioShortcutKey.Z, control: true);
        Expect("what Ctrl+Z runs: the move undone, and the cut still selected", [first, Cut(4, 5), second, last], 1);
        Invoke(editor, "StudioUndoButton");
        Expect("Undo of the add, which leaves none selected", [first, second, last], null);

        // Forward again. Nothing is selected, so nothing becomes selected.
        Invoke(editor, "StudioRedoButton");
        Expect("Redo of the add", [first, Cut(4, 5), second, last], null);
        Invoke(editor, "StudioRedoButton");
        Expect("Redo of the move", [first, Cut(4.5, 5.5), second, last], null);
        Key(editor, StudioShortcutKey.Y, control: true);
        Expect("what Ctrl+Y runs: the end again", [first, Cut(4.5, 5.6), second, last], null);
        Invoke(editor, "StudioRedoButton");
        Expect("Redo of the delete", [first, second, last], null);
        _report.Check(
            "Undo and Redo across an add, a move, an end and a delete, by the buttons and by what Ctrl+Z and Ctrl+Y run: the lane shows the cuts of each step, a cut that is selected stays selected through the undo of its end and of its move, and one that Undo brings back is not selected",
            wrong.Count == 0 && moved is { Changed: true, Index: 1 },
            wrong.Count == 0 ? string.Join("; ", steps) : string.Join(" | ", wrong));
    }
}
