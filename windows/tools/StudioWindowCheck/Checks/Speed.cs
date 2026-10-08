using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using TinyClips.App.Controls.Studio;
using TinyClips.App.Views.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Host;
using Windows.System;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 14. Speed changes: the lane between the cut lane and the trim bar, Speed, the keys R and
// Delete, and the Speed panel of the inspector. The lane under a pointer, what a speed change
// looks like, what a press does to the selection, the trim bar and a trim that is refused are in
// SpeedLane.cs; playing over a speed change and an export with speed changes are in
// SpeedPlaying.cs.
internal sealed partial class WindowChecks
{
    // What the two buttons that add a speed change say they do, with the key.
    private const string AddSpeedTip = "Play two seconds of the video twice as fast from the playhead (R)";

    // The six rates the editor offers, as they are shown and as a screen reader names them. Written
    // out here, and not taken from the editor, so that a rate that is worded wrongly is seen.
    private static readonly string[] RateTexts = ["0.25×", "0.5×", "1.5×", "2×", "4×", "8×"];
    private static readonly string[] RateNames = ["Quarter speed", "Half speed", "One and a half times the speed", "Twice the speed", "4 times the speed", "8 times the speed"];

    private void Speeds()
    {
        Timeline.Mark("14: speed changes");
        _layoutErrors.Clear();
        _report.Check(
            "the window maps the R key to the editor's keyboard model",
            StudioWindow.MapKey(VirtualKey.R) == StudioShortcutKey.R,
            $"R is {StudioWindow.MapKey(VirtualKey.R)}");

        AddingSpeedChanges();
        RatesOfAProjectFile();
        SpeedLaneUnderAPointer();
        PressesWithASpeedChange();
        TrimBarWithSpeedChanges();
        TrimThatASpeedChangeDoesNotAllow();
        PlayingOverSpeedChanges();
        ExportWithSpeedChanges();
    }

    // ---------------------------------------------------------------------------------------
    // What the editor holds, and how a speed change is worded
    // ---------------------------------------------------------------------------------------

    private static StudioSpeedRange Speed(double start, double end, double rate = 2) => new() { Start = start, End = end, Rate = rate };

    /// <summary>The speed changes the editor holds now. Read on the UI thread.</summary>
    private StudioSpeedRange[] SpeedsOf(Editor editor) => OnUi(() => editor.Window.ViewModel.SpeedChanges.ToArray());

    private int? SelectedSpeedOf(Editor editor) => OnUi(() => editor.Window.ViewModel.SelectedSpeedIndex);

    /// <summary>A speed change's two times and its rate, to a millionth.</summary>
    private static string Describe(StudioSpeedRange speed) => $"[{F(speed.Start, "0.######")} to {F(speed.End, "0.######")} at {F(speed.Rate, "0.######")}]";

    private static string Describe(StudioSpeedRange[] speeds) => speeds.Length == 0 ? "none" : string.Join(", ", speeds.Select(Describe));

    /// <summary>A speed change as the lane should name it: "Speed 2×, 2.0 to 4.0 seconds".</summary>
    private static string SpeedName(StudioSpeedRange speed) => string.Create(CultureInfo.InvariantCulture, $"Speed {speed.Rate:0.##}×, {speed.Start:0.0} to {speed.End:0.0} seconds");

    /// <summary>The speed lane as it should read for a list of speed changes, with a star before the selected one.</summary>
    private static string SpeedLaneFor(StudioSpeedRange[] speeds, int? selected) =>
        speeds.Length == 0 ? "empty" : string.Join(" | ", speeds.Select((speed, index) => (index == selected ? "*" : string.Empty) + SpeedName(speed)));

    /// <summary>Waits for the editor to hold the given speed changes and for the lane to show them. Null when both do, otherwise what is different.</summary>
    private string? SpeedsAre(Editor editor, StudioSpeedRange[] speeds, int? selected)
    {
        var lane = WaitForLane(editor, SpeedLaneFor(speeds, selected), 2, SpeedLane);
        var held = Until(() => Describe(SpeedsOf(editor)), now => now == Describe(speeds), 1);
        var chosen = Until(() => SelectedSpeedOf(editor), index => index == selected, 1);
        return lane == SpeedLaneFor(speeds, selected) && held == Describe(speeds) && chosen == selected
            ? null
            : $"the lane is {lane} and should be {SpeedLaneFor(speeds, selected)}; the editor holds {held} and should hold {Describe(speeds)}; the selected speed change is {(chosen is { } index ? (index + 1).ToString(CultureInfo.InvariantCulture) : "none")}";
    }

    /// <summary>Which of the six rates of the Speed panel are chosen, as their places among them. One at most should be.</summary>
    private static int[] ChosenRates(Editor editor) =>
        [.. Enumerable.Range(0, RateTexts.Length).Where(index => editor.Root.Find($"StudioSpeedRate_{index}")?.IsSelected == true)];

    /// <summary>Null when the rate at a place among the six is the chosen one and no other is; a place of -1 stands for none of the six.</summary>
    private static string? RateChosen(Editor editor, int wanted)
    {
        int[] expected = wanted < 0 ? [] : [wanted];
        var chosen = Until(() => ChosenRates(editor), now => now.SequenceEqual(expected), 1.5);
        return chosen.SequenceEqual(expected) ? null : $"of the six rates, {(chosen.Length == 0 ? "none is chosen" : "chosen: " + string.Join(", ", chosen.Select(index => RateTexts[index])))}";
    }

    /// <summary>What is wrong with what the Speed panel shows, read through UI Automation, or null.</summary>
    /// <param name="range">The selected speed change's times, or null when none is selected.</param>
    /// <param name="rate">The place among the six rates of the one that should be chosen, -1 for none of them, or null not to look.</param>
    private static string? SpeedSectionShows(Editor editor, string position, string? range, string? start = null, string? end = null, string? length = null, int? rate = null)
    {
        var positionRead = Until(() => NameOf(editor, "StudioSpeedPositionText", 0.5), text => text == position, 1.5);
        string[] ofOne = ["StudioSpeedRangeText", "StudioSpeedRateChoice", "StudioSpeedStartText", "StudioSpeedEndText", "StudioSpeedLengthText", "StudioSpeedSilentNote", "StudioDeleteSpeedButton"];
        string?[] problems =
        [
            positionRead == position ? null : $"the position reads \"{positionRead}\"",
            range is null
                ? ofOne.Where((id, index) => !Absent(editor, id, index == 0 ? 1 : 0.2)).ToArray() is { Length: > 0 } there ? "it shows a speed change's controls: " + string.Join(", ", there) : null
                : Until(() => NameOf(editor, "StudioSpeedRangeText", 0.5), text => text == range, 1) == range ? null : $"the times read \"{NameOf(editor, "StudioSpeedRangeText", 0)}\"",
            start is null || Until(() => NameOf(editor, "StudioSpeedStartText", 0.5), text => text == start, 1) == start ? null : $"Start reads \"{NameOf(editor, "StudioSpeedStartText", 0)}\"",
            end is null || Until(() => NameOf(editor, "StudioSpeedEndText", 0.5), text => text == end, 1) == end ? null : $"End reads \"{NameOf(editor, "StudioSpeedEndText", 0)}\"",
            length is null || Until(() => NameOf(editor, "StudioSpeedLengthText", 0.5), text => text == length, 1) == length ? null : $"the length reads \"{NameOf(editor, "StudioSpeedLengthText", 0)}\"",
            rate is { } chosen ? RateChosen(editor, chosen) : null,
            range is null || NameOf(editor, "StudioSpeedSilentNote", 0.5) == StudioEditorText.SpeedSilentNote ? null : $"the note about the sound reads \"{NameOf(editor, "StudioSpeedSilentNote", 0)}\"",
            range is null || Find(editor, "StudioDeleteSpeedButton", 0.5) is { IsEnabled: true, Name: "Delete speed change" } ? null : "there is no Delete speed change button",
        ];
        var detail = string.Join("; ", problems.Where(problem => problem is not null));
        return detail.Length == 0 ? null : detail;
    }

    /// <summary>What is selected of the three kinds, in a few words: "zoom none, cut none, speed change 2".</summary>
    private string OfThreeSelected(Editor editor)
    {
        static string Place(int? index) => index is { } at ? (at + 1).ToString(CultureInfo.InvariantCulture) : "none";
        return $"zoom {Place(SelectedZoomOf(editor))}, cut {Place(SelectedCutOf(editor))}, speed change {Place(SelectedSpeedOf(editor))}";
    }

    // ---------------------------------------------------------------------------------------
    // Adding speed changes, the lane, the panel and the keys
    // ---------------------------------------------------------------------------------------

    private void AddingSpeedChanges()
    {
        Timeline.Mark("14: adding speed changes");
        if (OpenReady(NewScreenProject("Speed changes"), "speed changes") is not { } editor)
        {
            return;
        }

        using var heard = UiaEvents.Listen(_uia, editor.Root);
        SetSlider(editor, "StudioPlayhead", 2.0);

        // No speed changes.
        var list = Find(editor, SpeedLane);
        var text = Find(editor, "StudioSpeedLaneEmptyText", 1);
        _report.Check(
            "a recording without speed changes: the lane is a list called Speed changes with no items, which can take the keyboard focus, and it says how to make one, as a text and as the list's description",
            list is { ControlType: ControlTypeNames.List, Name: "Speed changes", IsKeyboardFocusable: true, IsEnabled: true } && list.IsSelectionRequired == false && LaneItems(editor, SpeedLane).Count == 0
                && text?.Name == StudioEditorText.NoSpeedHint && list.HelpText == StudioEditorText.NoSpeedHint && list.Patterns.Contains("Selection", StringComparison.Ordinal) && list.SelectedNames.Length == 0,
            $"{list} with {LaneItems(editor, SpeedLane).Count} item(s), described as \"{list?.HelpText}\"; the text: \"{text?.Name}\"; patterns({list?.Patterns})");
        var section = SpeedSectionShows(editor, "No speed changes yet", range: null);
        var row = OnUi(() => Descendant<Button>(editor.Window.Content, "StudioAddSpeedButton") is { } button ? (Text: (button.Content as StudioButtonLabel)?.Text, Tip: ToolTipService.GetToolTip(button) as string) : default);
        var inSection = OnUi(() => Descendant<Button>(editor.Window.Content, "StudioSpeedSectionAddButton") is { } button ? ToolTipService.GetToolTip(button) as string : null);
        _report.Check(
            "without speed changes the Speed panel says so, has nothing to step to and no speed change's controls, says how to add the first one, and offers Add speed change; Speed in the transport row has the same name for a screen reader, and both say in their tooltips what they do, with the key",
            section is null && Find(editor, "StudioPreviousSpeedButton") is { IsEnabled: false, Name: "Previous speed change" } && Find(editor, "StudioNextSpeedButton") is { IsEnabled: false, Name: "Next speed change" }
                && Find(editor, "StudioSpeedSectionAddButton") is { IsEnabled: true, Name: "Add speed change" } && Find(editor, "StudioAddSpeedButton") is { IsEnabled: true, Name: "Add speed change", ControlType: ControlTypeNames.Button }
                && row.Text == "Speed" && row.Tip == AddSpeedTip && inSection == AddSpeedTip && Absent(editor, "StudioSpeedHint", 0.3)
                && NameOf(editor, "StudioSpeedEmptyHint", 0.5) == SpeedEmptyHint,
            section ?? $"\"No speed changes yet\"; {Find(editor, "StudioSpeedSectionAddButton")}, {Find(editor, "StudioAddSpeedButton")} showing \"{row.Text}\"; the tooltips: \"{row.Tip}\" and \"{inSection}\"; the hint: \"{NameOf(editor, "StudioSpeedEmptyHint", 0)}\"");
        var timeBefore = TimeText(editor);

        // R, at 2.0 s.
        Timeline.Mark("14: R changes the speed at the playhead");
        var mark = heard.Mark();
        var key = Key(editor, StudioShortcutKey.R);
        StudioSpeedRange[] one = [Speed(2, 4)];
        var added = SpeedsAre(editor, one, 0);
        var said = Said(heard, mark, StudioEditorText.SpeedAddedMessage);
        var head = Playhead(editor);
        var time = Until(() => TimeText(editor), read => read != timeBefore, 1);
        list = Find(editor, SpeedLane);
        _report.Check(
            "what R runs makes the two seconds from the playhead play twice as fast; the lane gets one item, named as the editor words the speed change, and it is the selected one; the playhead stays where the speed change starts",
            key == StudioShortcutAction.AddSpeed && added is null && SpeedName(one[0]) == "Speed 2×, 2.0 to 4.0 seconds" && SpeedName(one[0]) == StudioEditorText.GetSpeedDescription(one[0])
                && list?.SelectedNames.SequenceEqual([SpeedName(one[0])]) == true
                && Find(editor, "StudioSpeed_0") is { ControlType: ControlTypeNames.ListItem, IsEnabled: true } && Absent(editor, "StudioSpeedLaneEmptyText") && list.HelpText.Length == 0 && head == 2,
            added ?? $"the key ran {key}; the editor holds {Describe(SpeedsOf(editor))}; the lane: {LaneText(editor, SpeedLane)}; playhead {Seconds(head)} s");
        _report.Check("a screen reader is told \"Speed change added.\"", said.Said && StudioEditorText.SpeedAddedMessage == "Speed change added.", $"sent: {said.Heard}");
        _report.Check(
            "the time counts the video's time, which two seconds at twice the speed make a second shorter",
            timeBefore == StudioEditorText.GetTimeText(2, 12) && time == StudioEditorText.GetTimeText(2, 11),
            $"\"{timeBefore}\" before the speed change, \"{time}\" after it");
        section = SpeedSectionShows(editor, "Speed change 1 of 1", "2.0 to 4.0 seconds", "Start 2.0 seconds", "End 4.0 seconds", "2.0 seconds, plays in 1.0 seconds", rate: 3);
        _report.Check(
            "the Speed panel shows the new speed change: which it is and its times, its rate chosen among the six, its start, its end, how much of the recording it covers and how long that plays, that it plays without sound, and Delete speed change",
            section is null && Absent(editor, "StudioSpeedHint", 0.3) && StudioEditorText.SpeedSilentNote == "A stretch at another speed plays without sound.",
            section ?? "\"Speed change 1 of 1\", \"2.0 to 4.0 seconds\", 2× chosen, \"Start 2.0 seconds\", \"End 4.0 seconds\", \"2.0 seconds, plays in 1.0 seconds\", \"" + StudioEditorText.SpeedSilentNote + "\"");

        // R again, where the speed change now is, with nothing selected: that one is selected.
        Timeline.Mark("14: R where a speed change is, and where none fits");
        Find(editor, "StudioSpeed_0")?.RemoveFromSelection();
        var letGo = Until(() => SelectedSpeedOf(editor), index => index is null, 1);
        mark = heard.Mark();
        var again = Key(editor, StudioShortcutKey.R);
        var already = Said(heard, mark, StudioEditorText.SpeedAlreadyThereMessage);
        var same = SpeedsAre(editor, one, 0);
        _report.Check(
            "what R runs where a speed change already is adds nothing, selects that one, and tells a screen reader that there is one",
            letGo is null && again == StudioShortcutAction.AddSpeed && already.Said && same is null && Playhead(editor) == 2,
            same ?? $"sent: {already.Heard}; the lane: {LaneText(editor, SpeedLane)}");

        // A twentieth of a second before the end of the recording there is no room for the shortest speed change, which is a tenth.
        SetSlider(editor, "StudioPlayhead", RecordingLength - 0.05);
        var canAdd = Until(() => Find(editor, "StudioAddSpeedButton", 0.5)?.IsEnabled, enabled => enabled == false, 2);
        var canAddInSection = Find(editor, "StudioSpeedSectionAddButton", 0.5)?.IsEnabled;
        mark = heard.Mark();
        var noRoomKey = Key(editor, StudioShortcutKey.R);
        var noRoom = Said(heard, mark, StudioEditorText.NoRoomForSpeedMessage);
        same = SpeedsAre(editor, one, 0);
        _report.Check(
            "where less than the shortest speed change fits, both buttons are disabled, and what R runs adds nothing and tells a screen reader that there is no room",
            canAdd == false && canAddInSection == false && noRoomKey == StudioShortcutAction.AddSpeed && noRoom.Said && same is null,
            $"with the playhead at {Seconds(Playhead(editor))} s: Speed enabled {canAdd}, in the Speed panel {canAddInSection}; sent: {noRoom.Heard}; {same ?? "the lane: " + LaneText(editor, SpeedLane)}");

        // The two buttons.
        Timeline.Mark("14: the two buttons that change the speed");
        SetSlider(editor, "StudioPlayhead", 6.0);
        var enabledAgain = Until(() => Find(editor, "StudioAddSpeedButton", 0.5)?.IsEnabled, enabled => enabled == true, 2);
        mark = heard.Mark();
        var pressed = Invoke(editor, "StudioAddSpeedButton");
        StudioSpeedRange[] two = [Speed(2, 4), Speed(6, 8)];
        var second = SpeedsAre(editor, two, 1);
        var saidSecond = Said(heard, mark, StudioEditorText.SpeedAddedMessage);
        var headSecond = Playhead(editor);
        SetSlider(editor, "StudioPlayhead", 11.0);
        var pressedInSection = Invoke(editor, "StudioSpeedSectionAddButton");
        StudioSpeedRange[] three = [Speed(2, 4), Speed(6, 8), Speed(11, 12)];
        var third = SpeedsAre(editor, three, 2);
        section = SpeedSectionShows(editor, "Speed change 3 of 3", "11.0 to 12.0 seconds", "Start 11.0 seconds", "End 12.0 seconds", "1.0 seconds, plays in 0.5 seconds", rate: 3);

        // 2 + 1 + 2 + 1 + 3 seconds of video come before 11.0 s, and half a second after it.
        _report.Check(
            "Speed in the transport row and Add speed change in the Speed panel each start a speed change at the playhead, select it, leave the playhead where it is and say so; one that reaches the end of the recording stops there, and the panel says which one is selected, when it is and how long it plays",
            enabledAgain == true && pressed && second is null && saidSecond.Said && headSecond == 6 && pressedInSection && third is null && section is null && Playhead(editor) == 11
                && TimeText(editor) == StudioEditorText.GetTimeText(9, 9.5),
            second ?? third ?? section ?? $"the lane: {LaneText(editor, SpeedLane)}; sent: {saidSecond.Heard}; the time reads \"{TimeText(editor)}\"");

        LaneIsOnTheTimeScale(editor, SpeedLane, "StudioSpeed_", "speed", [(2, 4), (6, 8), (11, 12)], inset: 0);
        SpeedLaneKeys(editor, heard, three);
        SpeedLaneItems(editor, three);
        PreviousAndNextSpeed(editor, heard, three);
        ChoosingARate(editor, heard, three);
        SpeedStartAndEndButtons(editor, heard, three);
        OneOfThreeIsSelected(editor, three);
        DeletingSpeedChanges(editor, heard, three);
        UndoAndRedoOfSpeed(editor);
        SpeedSectionForAScreenReader(editor);

        // What was saved, once the editor has saved by itself.
        var live = SpeedsOf(editor);
        var wanted = Describe(live);
        var saved = Until(() => Describe(_services.Store.Load(editor.Id).Edits.Speed), now => now == wanted, 3, 100);
        _report.Check("the editor saves the speed changes by itself: the project file holds the speed changes the editor holds", saved == wanted && live.Length > 0, saved == wanted ? saved : $"saved: {saved} | the editor holds: {wanted}");
        CloseQuietly(editor);
    }

    /// <summary>What the speed lane does with the arrow keys, Home and End while it has the focus. The third speed change is selected.</summary>
    private void SpeedLaneKeys(Editor editor, UiaEvents heard, StudioSpeedRange[] speeds)
    {
        Timeline.Mark("14: the speed lane's keys");
        var steps = new List<string>();
        var wrong = new List<string>();
        var mark = heard.Mark();
        void Press(VirtualKey key, int wanted)
        {
            var handled = LaneKey(editor, key, SpeedLane);
            var selected = Until(() => SelectedSpeedOf(editor), index => index == wanted, 1);
            var head = Until(() => Playhead(editor), value => Same(value, speeds[wanted].Start), 1);
            var marked = LaneItems(editor, SpeedLane).Select((item, at) => item.IsSelected == true ? at : -1).Where(at => at >= 0).ToArray();
            steps.Add($"{key}: speed change {(selected is { } s ? (s + 1).ToString(CultureInfo.InvariantCulture) : "none")} at {Seconds(head)} s");
            if (!handled || selected != wanted || !Same(head, speeds[wanted].Start) || !marked.SequenceEqual([wanted]))
            {
                wrong.Add($"{key}: handled {handled}, selected {selected?.ToString(CultureInfo.InvariantCulture) ?? "none"} (the lane marks {string.Join(",", marked)}), playhead {Seconds(head)} s; wanted {wanted} at {Seconds(speeds[wanted].Start)} s");
            }
        }

        // Each key selects a speed change and moves the playhead to where it starts, the first picture at the other speed.
        Press(VirtualKey.Home, 0);
        Press(VirtualKey.Right, 1);
        Press(VirtualKey.Right, 2);
        Press(VirtualKey.Right, 2);
        Press(VirtualKey.Left, 1);
        Press(VirtualKey.End, 2);
        Press(VirtualKey.Home, 0);
        Press(VirtualKey.Left, 0);

        // With none selected, the arrows start from the playhead: between the first speed change and the second.
        Find(editor, "StudioSpeed_0")?.RemoveFromSelection();
        Until(() => SelectedSpeedOf(editor), index => index is null, 1);
        SetSlider(editor, "StudioPlayhead", 5.0);
        Press(VirtualKey.Right, 1);
        Find(editor, "StudioSpeed_1")?.RemoveFromSelection();
        Until(() => SelectedSpeedOf(editor), index => index is null, 1);
        SetSlider(editor, "StudioPlayhead", 5.0);
        Press(VirtualKey.Left, 0);
        var others = new[] { VirtualKey.Delete, VirtualKey.Space, VirtualKey.R, VirtualKey.S, VirtualKey.X, VirtualKey.Z, VirtualKey.Up, VirtualKey.Down }.Where(key => LaneKey(editor, key, SpeedLane)).ToArray();
        var selectedEvents = heard.WaitFor(mark, "selected", e => false, 0.4).Where(e => e.Id.StartsWith("StudioSpeed_", StringComparison.Ordinal)).ToArray();
        _report.Check(
            "with the focus on the speed lane, Home and End select the first and the last speed change and Left and Right the one before and after, each shown where it starts; with none selected the arrows start from the playhead; at either end, and for any other key, the lane does nothing",
            wrong.Count == 0 && others.Length == 0 && Describe(SpeedsOf(editor)) == Describe(speeds),
            wrong.Count == 0 ? $"{string.Join("; ", steps)}; keys the lane leaves to the window: Delete, Space, R, S, X, Z, Up, Down{(others.Length == 0 ? string.Empty : "; but it took " + string.Join(", ", others))}" : string.Join("; ", wrong));

        // The keys changed the selection eight times: to speed change 1, 2, 3, 2, 3, 1, and from none to 2 and to 1.
        string[] changes = ["StudioSpeed_0", "StudioSpeed_1", "StudioSpeed_2", "StudioSpeed_1", "StudioSpeed_2", "StudioSpeed_0", "StudioSpeed_1", "StudioSpeed_0"];
        var each = selectedEvents.Length / changes.Length;
        _report.Check(
            "a screen reader is told each time another speed change becomes the selected one, by its name",
            each is 1 or 2 && selectedEvents.Select(e => e.Id).SequenceEqual(changes.SelectMany(id => Enumerable.Repeat(id, each))) && selectedEvents.All(e => e.Text.StartsWith("Speed 2×, ", StringComparison.Ordinal)),
            $"{selectedEvents.Length} events for the 8 changes of the selection the keys made: {string.Join(", ", selectedEvents.Select(e => e.Id.Replace("StudioSpeed_", "speed change ", StringComparison.Ordinal)))}{(heard.Problem is null ? string.Empty : $" ({heard.Problem})")}");
    }

    /// <summary>A speed change's block as a list item: it can be selected, taken out of the selection, and pressed.</summary>
    private void SpeedLaneItems(Editor editor, StudioSpeedRange[] speeds)
    {
        Timeline.Mark("14: the speed lane's items, through UI Automation");
        var items = LaneItems(editor, SpeedLane);
        var patterns = items.Select(item => item.Patterns).Distinct().ToArray();
        var ids = items.Select(item => item.Id).ToArray();
        var classes = items.Select(item => item.ClassName).Distinct().ToArray();
        SetSlider(editor, "StudioPlayhead", 5.0);
        var head = Playhead(editor);
        var selected = Find(editor, "StudioSpeed_2")?.Select() ?? false;
        var afterSelect = (Until(() => SelectedSpeedOf(editor), index => index == 2, 1), Playhead(editor));
        var invoked = Find(editor, "StudioSpeed_1")?.Invoke() ?? false;
        var afterInvoke = (Until(() => SelectedSpeedOf(editor), index => index == 1, 1), Until(() => Playhead(editor), value => Same(value, speeds[1].Start), 1));
        var removed = Find(editor, "StudioSpeed_1")?.RemoveFromSelection() ?? false;
        var afterRemove = (Until(() => SelectedSpeedOf(editor), index => index is null, 1), Playhead(editor));
        var section = SpeedSectionShows(editor, "3 speed changes", range: null);
        var hint = NameOf(editor, "StudioSpeedHint", 1);
        _report.Check(
            "each speed change's block is a list item with an automation id that can be selected and pressed: selecting it leaves the playhead where it is, pressing it also moves the playhead to where the speed change starts, and taking it out of the selection leaves none selected; the Speed panel then says how many speed changes there are and how to select one",
            items.Count == 3 && patterns.Length == 1 && patterns[0] == "Invoke,SelectionItem" && ids.SequenceEqual(["StudioSpeed_0", "StudioSpeed_1", "StudioSpeed_2"]) && items.All(item => !item.IsKeyboardFocusable && item.Children().Count == 0)
                && classes.SequenceEqual(["StudioSpeedBlock"]) && Find(editor, SpeedLane)?.ClassName == "StudioSpeedLane"
                && selected && afterSelect.Item1 == 2 && Same(afterSelect.Item2, head)
                && invoked && afterInvoke.Item1 == 1 && Same(afterInvoke.Item2, speeds[1].Start)
                && removed && afterRemove.Item1 is null && Same(afterRemove.Item2, afterInvoke.Item2) && section is null && hint == StudioEditorText.SelectSpeedHint,
            $"ids {string.Join(", ", ids)}, patterns({string.Join(" / ", patterns)}), class {string.Join(" / ", classes)}; selected: speed change {afterSelect.Item1 + 1} with the playhead at {Seconds(afterSelect.Item2)} s ({Seconds(head)} s before); "
                + $"pressed: speed change {afterInvoke.Item1 + 1} at {Seconds(afterInvoke.Item2)} s; taken out: {(afterRemove.Item1 is null ? "none selected" : "still selected")}; {section ?? "\"3 speed changes\""}; the hint: \"{hint}\"");
    }

    /// <summary>Previous speed change and Next speed change: what they select, what is read out, and where the keyboard focus goes at the ends. None is selected, and the playhead is where the second starts.</summary>
    private void PreviousAndNextSpeed(Editor editor, UiaEvents heard, StudioSpeedRange[] speeds)
    {
        Timeline.Mark("14: Previous speed change and Next speed change");
        var wrong = new List<string>();
        var steps = new List<string>();
        void Step(string button, int wanted, bool canGoBack, bool canGoOn)
        {
            var mark = heard.Mark();
            var pressed = Invoke(editor, button);
            var selected = Until(() => SelectedSpeedOf(editor), index => index == wanted, 1);
            var head = Until(() => Playhead(editor), value => Same(value, speeds[wanted].Start), 1);
            var sentence = StudioEditorText.GetSpeedStepText(wanted, speeds.Length, speeds[wanted]);
            var said = Said(heard, mark, sentence);
            var position = Until(() => NameOf(editor, "StudioSpeedPositionText", 0.5), name => name == $"Speed change {wanted + 1} of 3", 1);
            var previous = Until(() => Find(editor, "StudioPreviousSpeedButton", 0.5)?.IsEnabled, enabled => enabled == canGoBack, 1);
            var next = Until(() => Find(editor, "StudioNextSpeedButton", 0.5)?.IsEnabled, enabled => enabled == canGoOn, 1);
            steps.Add($"\"{position}\" at {Seconds(head)} s");
            if (!pressed || selected != wanted || !Same(head, speeds[wanted].Start) || !said.Said || position != $"Speed change {wanted + 1} of 3" || previous != canGoBack || next != canGoOn)
            {
                wrong.Add($"{button}: pressed {pressed}, speed change {selected + 1} at {Seconds(head)} s, \"{position}\", Previous enabled {previous}, Next enabled {next}, sent: {said.Heard} (wanted \"{sentence}\")");
            }
        }

        // None is selected and the playhead is in the second speed change: Next is that one, the one at the playhead.
        var both = Find(editor, "StudioPreviousSpeedButton")?.IsEnabled == true && Find(editor, "StudioNextSpeedButton")?.IsEnabled == true;
        var focus = new List<string> { FocusOn(editor, "StudioNextSpeedButton") };
        Step("StudioNextSpeedButton", 1, canGoBack: true, canGoOn: true);
        focus.Add(FocusedId(editor));
        Step("StudioNextSpeedButton", 2, canGoBack: true, canGoOn: false);
        focus.Add(Until(() => FocusedId(editor), id => id == "StudioPreviousSpeedButton", 1.5));
        Step("StudioPreviousSpeedButton", 1, canGoBack: true, canGoOn: true);
        focus.Add(FocusedId(editor));
        Step("StudioPreviousSpeedButton", 0, canGoBack: false, canGoOn: true);
        focus.Add(Until(() => FocusedId(editor), id => id == "StudioNextSpeedButton", 1.5));
        _report.Check(
            "Previous speed change and Next speed change step through the speed changes and move the playhead to where each starts; the text between them says which one it is, each button is disabled where there is none to step to, and a screen reader is told the one it lands on, such as \"Speed change 2 of 3, 2×, 6.0 to 8.0 seconds\"",
            both && wrong.Count == 0 && StudioEditorText.GetSpeedStepText(1, 3, speeds[1]) == "Speed change 2 of 3, 2×, 6.0 to 8.0 seconds",
            wrong.Count == 0 ? string.Join(", then ", steps) : string.Join(" | ", wrong));
        string[] focusWanted = ["StudioNextSpeedButton", "StudioNextSpeedButton", "StudioPreviousSpeedButton", "StudioPreviousSpeedButton", "StudioNextSpeedButton"];
        _report.Check(
            "the keyboard focus stays on Next speed change and Previous speed change while they have one to step to, and goes to the other one when the last or the first speed change is reached and the pressed button is switched off",
            focus.SequenceEqual(focusWanted),
            $"the focus was on: {string.Join(", ", focus.Select(id => id.Replace("Studio", string.Empty, StringComparison.Ordinal)))}");
    }

    /// <summary>The choice of rate in the Speed panel, for the second speed change, which runs from 6.0 to 8.0 s at twice the speed.</summary>
    private void ChoosingARate(Editor editor, UiaEvents heard, StudioSpeedRange[] speeds)
    {
        Timeline.Mark("14: the rate of a speed change");
        Find(editor, "StudioSpeed_1")?.Invoke();
        Until(() => SelectedSpeedOf(editor), index => index == 1, 1);
        var group = Find(editor, "StudioSpeedRateChoice");
        var choices = Enumerable.Range(0, RateTexts.Length).Select(index => Find(editor, $"StudioSpeedRate_{index}", 0.5)).ToArray();
        var shown = OnUi(() => Enumerable.Range(0, RateTexts.Length).Select(index => Descendant<RadioButton>(editor.Window.Content, $"StudioSpeedRate_{index}")?.Content as string ?? string.Empty).ToArray());
        var tip = OnUi(() => Descendant<RadioButtons>(editor.Window.Content, "StudioSpeedRateChoice") is { } buttons ? ToolTipService.GetToolTip(buttons) as string : null);
        var chosenFirst = RateChosen(editor, 3);
        _report.Check(
            "the rate is a choice called Speed of the editor's six rates, slowest first: each shows its rate, such as 2×, is named in words for a screen reader, such as \"Twice the speed\", and can be selected; the one the speed change plays at is the chosen one",
            group is { Name: "Speed", IsEnabled: true } && choices.All(choice => choice is { ControlType: ControlTypeNames.RadioButton, IsEnabled: true } && choice.Patterns.Contains("SelectionItem", StringComparison.Ordinal))
                && choices.Select(choice => choice?.Name).SequenceEqual(RateNames) && shown.SequenceEqual(RateTexts) && chosenFirst is null && tip == "How fast this part of the video plays"
                && StudioEditorModel.SpeedRates.Select(StudioEditorText.GetSpeedRateText).SequenceEqual(RateTexts) && StudioEditorModel.SpeedRates.Select(StudioEditorText.GetSpeedRateName).SequenceEqual(RateNames),
            $"{group}, its tooltip \"{tip}\"; shown: {string.Join(", ", shown)}; named: {string.Join(", ", choices.Select(choice => $"\"{choice?.Name}\""))}; {chosenFirst ?? "2× is the chosen one"}");

        // Half speed, then four times the speed: the block, the panel, the length of the video and what is read out.
        var steps = new List<string>();
        var wrong = new List<string>();
        void Choose(int place, double rate, string length, string time)
        {
            var headBefore = Playhead(editor);
            var mark = heard.Mark();
            var selected = Find(editor, $"StudioSpeedRate_{place}")?.Select() ?? false;
            StudioSpeedRange[] wanted = [speeds[0], speeds[1] with { Rate = rate }, speeds[2]];
            var held = SpeedsAre(editor, wanted, 1);
            var said = Said(heard, mark, RateNames[place]);
            var section = SpeedSectionShows(editor, "Speed change 2 of 3", "6.0 to 8.0 seconds", "Start 6.0 seconds", "End 8.0 seconds", length, rate: place);
            var read = Until(() => TimeText(editor), text => text == time, 1);
            var head = Playhead(editor);
            steps.Add($"{RateTexts[place]}: {LaneText(editor, SpeedLane)}, \"{length}\", time \"{read}\"");
            if (!selected || held is not null || !said.Said || section is not null || read != time || !Same(head, headBefore))
            {
                wrong.Add($"{RateTexts[place]}: selected {selected}; {held ?? "the lane and the editor hold it"}; sent: {said.Heard} (wanted \"{RateNames[place]}\"); {section ?? "the panel shows it"}; the time reads \"{read}\" (wanted \"{time}\"); playhead {Seconds(head)} s ({Seconds(headBefore)} s before)");
            }
        }

        // The playhead is at 6.0 s, with 2 + 1 + 2 seconds of video before it. At half speed the two
        // seconds play in four, and the video is 2 + 1 + 2 + 4 + 3 + 0.5 = 12.5 s long; at four times
        // the speed they play in half a second, and it is 9.0 s long.
        Choose(1, 0.5, "2.0 seconds, plays in 4.0 seconds", StudioEditorText.GetTimeText(5, 12.5));
        var slower = SpeedMarks(editor, 1);
        Choose(4, 4, "2.0 seconds, plays in 0.5 seconds", StudioEditorText.GetTimeText(5, 9));
        var faster = SpeedMarks(editor, 1);
        _report.Check(
            "choosing another rate changes how fast the selected speed change plays and nothing else of it: its block and the panel show the new rate and how long that part now plays, the video's length follows, the playhead stays, and a screen reader is told the new rate by its name",
            wrong.Count == 0,
            wrong.Count == 0 ? string.Join("; ", steps) : string.Join(" | ", wrong));
        _report.Check(
            "the block's mark follows the rate: the mark for slower at half speed, and the mark for faster at four times the speed",
            !slower.Faster && slower.Slower && faster.Faster && !faster.Slower,
            $"at half speed: faster mark {slower.Faster}, slower mark {slower.Slower}, \"{slower.Rate}\"; at four times: faster mark {faster.Faster}, slower mark {faster.Slower}, \"{faster.Rate}\"");

        // Each choice is one undo step, and the speed change stays selected through both.
        Invoke(editor, "StudioUndoButton");
        StudioSpeedRange[] half = [speeds[0], speeds[1] with { Rate = 0.5 }, speeds[2]];
        var undoneOnce = SpeedsAre(editor, half, 1) ?? RateChosen(editor, 1);
        Invoke(editor, "StudioUndoButton");
        var undoneTwice = SpeedsAre(editor, speeds, 1) ?? RateChosen(editor, 3);

        // The rate that is already the chosen one, chosen again: nothing to tell, and nothing new to undo, so what was undone can still be redone.
        var mark = heard.Mark();
        Find(editor, "StudioSpeedRate_3")?.Select();
        var told = heard.WaitFor(mark, "notification", e => false, 0.5).Where(e => RateNames.Contains(e.Text)).Select(e => e.Text).ToArray();
        var canRedo = Find(editor, "StudioRedoButton", 0.5)?.IsEnabled;
        var unchanged = SpeedsAre(editor, speeds, 1);
        _report.Check(
            "each choice of a rate is one undo step, through which the speed change stays selected and the choice shows the rate it has again; choosing the rate it already has changes nothing and is not read out",
            undoneOnce is null && undoneTwice is null && told.Length == 0 && canRedo == true && unchanged is null,
            undoneOnce ?? undoneTwice ?? unchanged ?? $"after one Undo half speed, after two twice the speed; chosen again: {(told.Length == 0 ? "nothing read out" : "read out: " + string.Join(", ", told))}, Redo enabled {canRedo}");
    }

    /// <summary>What a speed change's block carries: the mark for faster, the mark for slower, and the rate written on it, empty when it is not shown. Read on the UI thread.</summary>
    private (bool Faster, bool Slower, string Rate) SpeedMarks(Editor editor, int index) => OnUi(() =>
    {
        var block = Descendant<TinyClips.App.Controls.Studio.StudioSpeedBlock>(editor.Window.Content, $"StudioSpeed_{index}");
        var faster = block?.FindName("FasterIcon") is Microsoft.UI.Xaml.FrameworkElement { Visibility: Microsoft.UI.Xaml.Visibility.Visible };
        var slower = block?.FindName("SlowerIcon") is Microsoft.UI.Xaml.FrameworkElement { Visibility: Microsoft.UI.Xaml.Visibility.Visible };
        var rate = block?.FindName("RateText") is TextBlock { Visibility: Microsoft.UI.Xaml.Visibility.Visible } text ? text.Text : string.Empty;
        return (faster, slower, rate);
    });

    /// <summary>The Start and End rows of the Speed panel, for the second speed change, which runs from 6.0 to 8.0 s.</summary>
    private void SpeedStartAndEndButtons(Editor editor, UiaEvents heard, StudioSpeedRange[] speeds)
    {
        Timeline.Mark("14: the start and the end of a speed change");
        Find(editor, "StudioSpeed_1")?.Invoke();
        Until(() => SelectedSpeedOf(editor), index => index == 1, 1);
        string[] ids = ["StudioSpeedStartEarlierButton", "StudioSpeedStartLaterButton", "StudioSpeedStartAtPlayheadButton", "StudioSpeedEndEarlierButton", "StudioSpeedEndLaterButton", "StudioSpeedEndAtPlayheadButton"];
        var names = ids.Select(id => NameOf(editor, id)).ToArray();
        var steps = new List<string>();
        var wrong = new List<string>();
        void Step(string what, string button, double wantedStart, double wantedEnd, double? wantedHead, bool isStart)
        {
            var headBefore = Playhead(editor);
            var mark = heard.Mark();
            var pressed = Invoke(editor, button);
            var speed = Until(() => SpeedsOf(editor)[1], now => Same(now.Start, wantedStart) && Same(now.End, wantedEnd), 1);
            var headWanted = wantedHead ?? headBefore;
            var head = Until(() => Playhead(editor), value => Same(value, headWanted), 1);
            var sentence = isStart ? $"Start {StudioEditorModel.GetSecondsText(wantedStart)}" : $"End {StudioEditorModel.GetSecondsText(wantedEnd)}";
            var said = Said(heard, mark, sentence);
            var shown = Until(() => NameOf(editor, isStart ? "StudioSpeedStartText" : "StudioSpeedEndText", 0.5), text => text == sentence, 1);
            steps.Add($"{what}: {Seconds(speed.Start)} to {Seconds(speed.End)} s, playhead {Seconds(head)} s");
            if (!pressed || !Same(speed.Start, wantedStart) || !Same(speed.End, wantedEnd) || speed.Rate != 2 || !Same(head, headWanted) || !said.Said || shown != sentence || SelectedSpeedOf(editor) != 1)
            {
                wrong.Add($"{what}: pressed {pressed}, the speed change is {Describe(speed)} (wanted {Seconds(wantedStart)} to {Seconds(wantedEnd)}), playhead {Seconds(head)} s (wanted {Seconds(headWanted)}), the panel reads \"{shown}\", sent: {said.Heard}");
            }
        }

        // A step moves the playhead to the end it moved, as a trim handle does. At playhead leaves it where it is.
        Step("Start 0.1 s later", ids[1], 6.1, 8.0, 6.1, isStart: true);
        Step("Start 0.1 s earlier, twice", ids[0], 6.0, 8.0, 6.0, isStart: true);
        Step("Start 0.1 s earlier, twice", ids[0], 5.9, 8.0, 5.9, isStart: true);
        Step("End 0.1 s later", ids[4], 5.9, 8.1, 8.1, isStart: false);
        Step("End 0.1 s earlier", ids[3], 5.9, 8.0, 8.0, isStart: false);
        SetSlider(editor, "StudioPlayhead", 7.0);
        Step("Start at the playhead, at 7.0 s", ids[2], 7.0, 8.0, null, isStart: true);
        var length = NameOf(editor, "StudioSpeedLengthText");
        var range = NameOf(editor, "StudioSpeedRangeText");

        // A speed change covers at least a tenth of a second: its end stops that far after its start, and its start that far before its end.
        Step("End at the playhead, which is where the speed change starts", ids[5], 7.0, 7.1, null, isStart: false);
        Step("Start 0.1 s later, against the end", ids[1], 7.0, 7.1, 7.0, isStart: true);

        // Speed changes do not overlap: an end stops where the next one starts, and the two then touch.
        SetSlider(editor, "StudioPlayhead", 11.8);
        Step("End at the playhead, at 11.8 s, inside the next speed change", ids[5], 7.0, 11.0, null, isStart: false);
        var lane = LaneText(editor, SpeedLane);

        // With the playhead at 5.25 s, which 2 + 1 + 1.25 seconds of video come before: the video is 2 + 1 + 3 + 2 + 0.5 seconds long.
        SetSlider(editor, "StudioPlayhead", 5.25);
        var time = Until(() => TimeText(editor), text => text == StudioEditorText.GetTimeText(4.25, 8.5), 1);
        _report.Check(
            "the six buttons of Start and End are named for what they do, move that end of the selected speed change by 0.1 s or to the playhead, and say the time they leave it at; the playhead follows a step and stays for At playhead; a speed change keeps its rate, covers at least a tenth of a second, and its end stops where the next one starts",
            wrong.Count == 0 && names.SequenceEqual(["Start 0.1 seconds earlier", "Start 0.1 seconds later", "Start at playhead", "End 0.1 seconds earlier", "End 0.1 seconds later", "End at playhead"])
                && length == "1.0 seconds, plays in 0.5 seconds" && range == "7.0 to 8.0 seconds" && lane == $"{SpeedName(speeds[0])} | *{SpeedName(Speed(7, 11))} | {SpeedName(speeds[2])}" && time == "0:04.2 / 0:08.5",
            wrong.Count == 0 ? $"{string.Join("; ", steps)}; with the speed change from 7.0 to 8.0 s the panel said \"{range}\", \"{length}\"; the lane: {lane}; the time reads \"{time}\"" : string.Join(" | ", wrong));

        // Eight of the nine presses changed the speed change. Eight Undo bring it back, still selected.
        for (var undo = 0; undo < 8; undo++)
        {
            var before = Describe(SpeedsOf(editor));
            Invoke(editor, "StudioUndoButton");
            Until(() => Describe(SpeedsOf(editor)), now => now != before, 1);
        }

        var restored = SpeedsAre(editor, speeds, 1);
        _report.Check(
            "each press that moved an end is one undo step, and the one that changed nothing is none: eight Undo bring the speed change back to 6.0 to 8.0 s, and it is still the selected one",
            restored is null,
            restored ?? $"the lane: {LaneText(editor, SpeedLane)}");
    }

    /// <summary>One thing is selected at most: a zoom, a cut or a speed change. The second speed change is selected.</summary>
    private void OneOfThreeIsSelected(Editor editor, StudioSpeedRange[] speeds)
    {
        Timeline.Mark("14: a zoom, a cut or a speed change is selected, never two of them");
        var before = OfThreeSelected(editor);

        // Z adds a zoom and selects it, which lets go of the speed change.
        SetSlider(editor, "StudioPlayhead", 4.5);
        Key(editor, StudioShortcutKey.Z);
        const string ZoomName = "Zoom 2×, 4.5 to 7.5 seconds";
        var zoomLane = WaitForLane(editor, "*" + ZoomName);
        var speedsThen = SpeedsAre(editor, speeds, null);
        var speedSection = SpeedSectionShows(editor, "3 speed changes", range: null);
        var speedHint = NameOf(editor, "StudioSpeedHint", 1);
        var zoomShown = Until(() => Find(editor, "StudioZoomScaleSlider", 0.5), slider => slider is not null, 1) is not null;
        _report.Check(
            "adding a zoom selects it and lets go of the selected speed change: the speed lane marks none, and the Speed panel shows no speed change and says how to select one, while the Zoom panel shows the zoom",
            before == "zoom none, cut none, speed change 2" && zoomLane == "*" + ZoomName && speedsThen is null && speedSection is null && speedHint == StudioEditorText.SelectSpeedHint && zoomShown
                && OfThreeSelected(editor) == "zoom 1, cut none, speed change none",
            speedsThen ?? speedSection ?? $"before: {before}; then: {OfThreeSelected(editor)}; zooms: {zoomLane}; speed changes: {LaneText(editor, SpeedLane)}; the Speed panel's hint: \"{speedHint}\"");

        // X starts a cut and selects it, which lets go of the zoom. Selecting a speed change then lets go of the cut.
        SetSlider(editor, "StudioPlayhead", 9.0);
        Key(editor, StudioShortcutKey.X);
        StudioTimeRange[] cuts = [Cut(9, 10)];
        var cutAdded = CutsAre(editor, cuts, 0);
        var afterCut = OfThreeSelected(editor);
        var zoomLaneThen = WaitForLane(editor, ZoomName);
        var selected = Find(editor, "StudioSpeed_0")?.Select() ?? false;
        var speedsNow = SpeedsAre(editor, speeds, 0);
        var cutsNow = CutsAre(editor, cuts, null);
        var afterSpeed = OfThreeSelected(editor);
        var cutSection = CutSectionShows(editor, "1 cut", range: null);
        var cutHint = NameOf(editor, "StudioCutHint", 1);
        var zoomPosition = NameOf(editor, "StudioZoomPositionText");
        var zoomGone = Absent(editor, "StudioZoomScaleSlider") && Absent(editor, "StudioDeleteZoomButton", 0.3);
        var speedShown = SpeedSectionShows(editor, "Speed change 1 of 3", "2.0 to 4.0 seconds", "Start 2.0 seconds", "End 4.0 seconds", "2.0 seconds, plays in 1.0 seconds", rate: 3);
        _report.Check(
            "starting a cut selects it and lets go of the zoom; selecting a speed change then lets go of the cut: the zoom lane and the cut lane mark none, the Zoom panel and the Cut panel show none and say how many there are, and the Speed panel shows the speed change",
            cutAdded is null && afterCut == "zoom none, cut 1, speed change none" && zoomLaneThen == ZoomName && selected && speedsNow is null && cutsNow is null && afterSpeed == "zoom none, cut none, speed change 1"
                && cutSection is null && cutHint == StudioEditorText.SelectCutHint && zoomPosition == "1 zoom" && zoomGone && speedShown is null,
            cutAdded ?? speedsNow ?? cutsNow ?? cutSection ?? speedShown ?? $"after X: {afterCut}; after selecting the first speed change: {afterSpeed}; zooms: {LaneText(editor)}, \"{zoomPosition}\", a zoom's controls gone: {zoomGone}; cuts: {LaneText(editor, CutLane)}, the hint: \"{cutHint}\"");

        // Delete removes what is selected, and only that: the zoom, then the cut, and no speed change.
        Find(editor, "StudioZoom_0")?.Select();
        var zoomAgain = Until(() => OfThreeSelected(editor), now => now == "zoom 1, cut none, speed change none", 1);
        var deleteZoom = Key(editor, StudioShortcutKey.Delete);
        var zoomsLeft = Until(() => ZoomsOf(editor).Length, count => count == 0, 1);
        Find(editor, "StudioCut_0")?.Select();
        var cutAgain = Until(() => OfThreeSelected(editor), now => now == "zoom none, cut 1, speed change none", 1);
        var deleteCut = Key(editor, StudioShortcutKey.Delete);
        var cutsLeft = Until(() => CutsOf(editor).Length, count => count == 0, 1);
        var speedsLeft = SpeedsAre(editor, speeds, null);
        _report.Check(
            "selecting the zoom again lets go of the speed change, and what Delete runs then removes the zoom; with the cut selected it removes the cut; no speed change is removed by either",
            zoomAgain == "zoom 1, cut none, speed change none" && deleteZoom == StudioShortcutAction.RemoveSelectedZoom && zoomsLeft == 0
                && cutAgain == "zoom none, cut 1, speed change none" && deleteCut == StudioShortcutAction.RemoveSelectedCut && cutsLeft == 0 && speedsLeft is null,
            speedsLeft ?? $"selected: {zoomAgain}, Delete ran {deleteZoom}, zooms left {zoomsLeft}; selected: {cutAgain}, Delete ran {deleteCut}, cuts left {cutsLeft}; speed changes: {LaneText(editor, SpeedLane)}");
    }

    /// <summary>Delete, with a speed change selected and with none, and the Delete speed change button. None is selected, and there is no zoom and no cut.</summary>
    private void DeletingSpeedChanges(Editor editor, UiaEvents heard, StudioSpeedRange[] speeds)
    {
        Timeline.Mark("14: deleting speed changes");
        SetSlider(editor, "StudioPlayhead", 5.0);
        var timeBefore = Until(() => TimeText(editor), text => text == StudioEditorText.GetTimeText(4, 9.5), 1);
        Find(editor, "StudioSpeed_1")?.Select();
        Until(() => SelectedSpeedOf(editor), index => index == 1, 1);
        var mark = heard.Mark();
        var delete = Key(editor, StudioShortcutKey.Delete);
        StudioSpeedRange[] two = [speeds[0], speeds[2]];
        var afterKey = SpeedsAre(editor, two, null);
        var deleted = Said(heard, mark, StudioEditorText.SpeedDeletedMessage);
        var deleteAgain = Key(editor, StudioShortcutKey.Delete);
        var section = SpeedSectionShows(editor, "2 speed changes", range: null);
        var timeAfter = TimeText(editor);
        _report.Check(
            "what Delete runs removes the selected speed change, so that this part plays at normal speed again: none is selected, the playhead stays, the video is a second longer, and a screen reader is told; with nothing selected the key is left alone",
            delete == StudioShortcutAction.RemoveSelectedSpeed && afterKey is null && deleted.Said && StudioEditorText.SpeedDeletedMessage == "Speed change deleted." && deleteAgain == StudioShortcutAction.None && Playhead(editor) == 5 && section is null
                && timeBefore == StudioEditorText.GetTimeText(4, 9.5) && timeAfter == StudioEditorText.GetTimeText(4, 10.5),
            afterKey ?? section ?? $"Delete ran {delete}, then {deleteAgain}; the lane: {LaneText(editor, SpeedLane)}; sent: {deleted.Heard}; the time read \"{timeBefore}\" and reads \"{timeAfter}\"");

        // Delete speed change, with the keyboard focus on it. The playhead is before the last speed change, so Next has one to step to.
        Find(editor, "StudioSpeed_0")?.Select();
        Until(() => SelectedSpeedOf(editor), index => index == 0, 1);
        var button = Find(editor, "StudioDeleteSpeedButton");
        var onDelete = FocusOn(editor, "StudioDeleteSpeedButton");
        mark = heard.Mark();
        var pressed = Invoke(editor, "StudioDeleteSpeedButton");
        StudioSpeedRange[] one = [speeds[2]];
        var afterButton = SpeedsAre(editor, one, null);
        var saidAgain = Said(heard, mark, StudioEditorText.SpeedDeletedMessage);
        var focusAfter = Until(() => FocusedId(editor), id => id == "StudioNextSpeedButton", 1.5);
        _report.Check(
            "Delete speed change removes the selected speed change and says so; the button goes with it, and the keyboard focus goes to Next speed change, which has one to step to",
            button is { Name: "Delete speed change", IsEnabled: true } && onDelete == "StudioDeleteSpeedButton" && pressed && afterButton is null && saidAgain.Said && focusAfter == "StudioNextSpeedButton" && Absent(editor, "StudioDeleteSpeedButton", 0.5),
            afterButton ?? $"the lane: {LaneText(editor, SpeedLane)}; sent: {saidAgain.Heard}; the focus was on \"{onDelete}\" and is on \"{focusAfter}\"");

        // Both back, for what comes next.
        Invoke(editor, "StudioUndoButton");
        Until(() => SpeedsOf(editor).Length, count => count == 2, 1);
        Invoke(editor, "StudioUndoButton");
        var back = SpeedsAre(editor, speeds, null);
        _report.Check("two Undo bring both speed changes back, and select neither", back is null, back ?? $"the lane: {LaneText(editor, SpeedLane)}");
    }

    /// <summary>Undo and Redo across an add, a move, a rate, an end and a delete: the lane and the selection follow. There are three speed changes, none selected.</summary>
    private void UndoAndRedoOfSpeed(Editor editor)
    {
        Timeline.Mark("14: undo and redo of speed changes");
        var wrong = new List<string>();
        var steps = new List<string>();
        StudioSpeedRange first = Speed(2, 4), second = Speed(6, 8), last = Speed(11, 12);
        void Expect(string what, StudioSpeedRange[] speeds, int? selected)
        {
            var problem = SpeedsAre(editor, speeds, selected);
            steps.Add($"{what}: {LaneText(editor, SpeedLane)}");
            if (problem is not null)
            {
                wrong.Add($"{what}: {problem}");
            }
        }

        // Add: at 8.25 s, between the last two. It covers two seconds at twice the speed, and is selected.
        SetSlider(editor, "StudioPlayhead", 8.25);
        Key(editor, StudioShortcutKey.R);
        Expect("add", [first, second, Speed(8.25, 10.25), last], 2);

        // Move: half a second later, in two steps inside one gesture, which is what a drag of the block asks for.
        var moved = OnUi(() =>
        {
            var viewModel = editor.Window.ViewModel;
            viewModel.BeginGesture();
            var result = viewModel.MoveSpeed(2, 8.5);
            result = viewModel.MoveSpeed(result.Index ?? 2, 8.75);
            viewModel.EndGesture();
            return result;
        });
        Expect("move", [first, second, Speed(8.75, 10.75), last], 2);

        // Its rate, to four times the speed.
        Find(editor, "StudioSpeedRate_4")?.Select();
        Expect("rate", [first, second, Speed(8.75, 10.75, 4), last], 2);

        // Its end, 0.1 s later.
        Invoke(editor, "StudioSpeedEndLaterButton");
        Expect("end", [first, second, Speed(8.75, 10.85, 4), last], 2);

        // Delete.
        Key(editor, StudioShortcutKey.Delete);
        Expect("delete", [first, second, last], null);

        // Back: the deleted speed change returns. It is not selected: Undo brings back the project, and nothing was selected for it to follow.
        Invoke(editor, "StudioUndoButton");
        Expect("Undo of the delete", [first, second, Speed(8.75, 10.85, 4), last], null);
        Find(editor, "StudioSpeed_2")?.Select();
        Expect("selected again", [first, second, Speed(8.75, 10.85, 4), last], 2);
        Invoke(editor, "StudioUndoButton");
        Expect("Undo of the end, which keeps the speed change selected", [first, second, Speed(8.75, 10.75, 4), last], 2);
        Invoke(editor, "StudioUndoButton");
        Expect("Undo of the rate, which keeps it selected", [first, second, Speed(8.75, 10.75), last], 2);
        var rateBack = RateChosen(editor, 3);
        Key(editor, StudioShortcutKey.Z, control: true);
        Expect("what Ctrl+Z runs: the move undone, and the speed change still selected", [first, second, Speed(8.25, 10.25), last], 2);
        Invoke(editor, "StudioUndoButton");
        Expect("Undo of the add, which leaves none selected", [first, second, last], null);

        // Forward again. Nothing is selected, so nothing becomes selected.
        Invoke(editor, "StudioRedoButton");
        Expect("Redo of the add", [first, second, Speed(8.25, 10.25), last], null);
        Invoke(editor, "StudioRedoButton");
        Expect("Redo of the move", [first, second, Speed(8.75, 10.75), last], null);
        Key(editor, StudioShortcutKey.Y, control: true);
        Expect("what Ctrl+Y runs: the rate again", [first, second, Speed(8.75, 10.75, 4), last], null);
        Invoke(editor, "StudioRedoButton");
        Expect("Redo of the end", [first, second, Speed(8.75, 10.85, 4), last], null);
        Invoke(editor, "StudioRedoButton");
        Expect("Redo of the delete", [first, second, last], null);
        _report.Check(
            "Undo and Redo across an add, a move, a rate, an end and a delete, by the buttons and by what Ctrl+Z and Ctrl+Y run: the lane shows the speed changes of each step, one that is selected stays selected through the undo of its end, of its rate and of its move, the choice of rate follows, and one that Undo brings back is not selected",
            wrong.Count == 0 && moved is { Changed: true, Index: 2 } && rateBack is null,
            wrong.Count == 0 ? string.Join("; ", steps) + (rateBack is null ? string.Empty : "; " + rateBack) : string.Join(" | ", wrong));
    }

    /// <summary>
    /// What a screen reader is given of the timeline and the Speed panel with the second of
    /// three speed changes selected, and the order of the tab stops there.
    /// </summary>
    private void SpeedSectionForAScreenReader(Editor editor)
    {
        Timeline.Mark("14: the names and the order of the tab stops, with a speed change selected");
        Find(editor, "StudioSpeed_1")?.Invoke();
        Until(() => SelectedSpeedOf(editor), index => index == 1, 1);
        Until(() => Find(editor, "StudioDeleteSpeedButton", 0.5), found => found is not null, 1.5);

        // A recording without a camera has eleven sliders, and a speed change adds none.
        (string Id, string Name)[] names =
        [
            ("StudioAddSpeedButton", "Add speed change"), (SpeedLane, "Speed changes"), ("StudioSpeed_0", "Speed 2×, 2.0 to 4.0 seconds"), ("StudioSpeed_1", "Speed 2×, 6.0 to 8.0 seconds"), ("StudioSpeed_2", "Speed 2×, 11.0 to 12.0 seconds"),
            ("StudioPreviousSpeedButton", "Previous speed change"), ("StudioSpeedPositionText", "Speed change 2 of 3"), ("StudioSpeedRangeText", "6.0 to 8.0 seconds"), ("StudioNextSpeedButton", "Next speed change"),
            ("StudioSpeedSectionAddButton", "Add speed change"), ("StudioSpeedRateChoice", "Speed"),
            .. RateNames.Select((name, index) => ($"StudioSpeedRate_{index}", name)),
            ("StudioSpeedStartText", "Start 6.0 seconds"), ("StudioSpeedStartEarlierButton", "Start 0.1 seconds earlier"), ("StudioSpeedStartLaterButton", "Start 0.1 seconds later"), ("StudioSpeedStartAtPlayheadButton", "Start at playhead"),
            ("StudioSpeedEndText", "End 8.0 seconds"), ("StudioSpeedEndEarlierButton", "End 0.1 seconds earlier"), ("StudioSpeedEndLaterButton", "End 0.1 seconds later"), ("StudioSpeedEndAtPlayheadButton", "End at playhead"),
            ("StudioSpeedLengthText", "2.0 seconds, plays in 1.0 seconds"), ("StudioSpeedSilentNote", "A stretch at another speed plays without sound."), ("StudioDeleteSpeedButton", "Delete speed change"),
        ];
        AuditState(editor, "the editor with a speed change selected", "tree-speed.txt", 11, tree => Named(tree, names));

        // What Delete speed change does to the video is said to a screen reader, and shown as its tooltip with its key.
        var delete = Find(editor, "StudioDeleteSpeedButton");
        var tip = OnUi(() => Descendant<Button>(editor.Window.Content, "StudioDeleteSpeedButton") is { } button ? ToolTipService.GetToolTip(button) as string : null);
        _report.Check(
            "Delete speed change says that this part then plays at normal speed again: as its description for a screen reader, and in its tooltip, which also names its key",
            delete?.HelpText == "This part then plays at normal speed again." && tip == "Delete this speed change, so this part plays at normal speed again (Delete)",
            $"{delete}, described as \"{delete?.HelpText}\"; the tooltip: \"{tip}\"");

        // The stops, in order: the Speed panel from top to bottom, after the Cut panel and
        // before Mute, which is the Audio panel's; and the timeline of a recording without a
        // camera, which has no Split and no scene lane.
        var order = TabStops(editor);
        var path = Path.Combine(_output, "tab-order-speed.txt");
        File.WriteAllLines(path, order);
        string[] section =
        [
            "StudioPreviousSpeedButton", "StudioNextSpeedButton", "StudioSpeedSectionAddButton", "StudioSpeedRateChoice", "StudioSpeedStartEarlierButton", "StudioSpeedStartLaterButton", "StudioSpeedStartAtPlayheadButton",
            "StudioSpeedEndEarlierButton", "StudioSpeedEndLaterButton", "StudioSpeedEndAtPlayheadButton", "StudioDeleteSpeedButton",
        ];
        string[] timeline =
        [
            "StudioPlayPauseButton", "StudioPreviousFrameButton", "StudioNextFrameButton", "StudioAddZoomButton", "StudioAddCutButton", "StudioAddSpeedButton", "StudioStartHereButton", "StudioEndHereButton",
            ZoomLane, CutLane, SpeedLane, "StudioTrimStart", "StudioTrimEnd", "StudioPlayhead",
        ];
        var at = order.IndexOf(section[0]);
        var sectionTogether = at >= 0 && order.Skip(at).Take(section.Length).SequenceEqual(section);
        var afterCut = order.IndexOf("StudioCutSectionAddButton") is >= 0 and var cut && cut < at;
        var beforeMute = order.IndexOf("StudioMuteCheckBox") > at;
        var row = order.IndexOf(timeline[0]);
        var timelineTogether = row >= 0 && order.Skip(row).Take(timeline.Length).SequenceEqual(timeline);
        var blocks = order.Where(id => id.StartsWith("StudioSpeed_", StringComparison.Ordinal) || id.StartsWith("StudioSpeedRate_", StringComparison.Ordinal)).ToArray();
        _report.Check(
            "the keyboard focus, moved from stop to stop with each panel on show in turn, goes through the Speed panel from Previous speed change to Delete speed change with nothing between them, the choice of rate one stop among them, after the Cut panel and before Mute; the timeline of a recording without a camera is the transport row with Speed after Cut, then the zoom lane, the cut lane, the speed lane and the trim bar; the speed lane is one stop, and no single speed change is one",
            sectionTogether && afterCut && beforeMute && timelineTogether && blocks.Length == 0,
            sectionTogether && afterCut && beforeMute && timelineTogether && blocks.Length == 0
                ? $"{order.Count} stops, saved as {Path.GetFileName(path)}: {string.Join(", ", order.Select(id => id.Replace("Studio", string.Empty, StringComparison.Ordinal)))}"
                : $"{(sectionTogether ? string.Empty : "the Speed panel's stops are not one after the other; ")}{(afterCut ? string.Empty : "not after the Cut panel; ")}{(beforeMute ? string.Empty : "not before Mute; ")}{(timelineTogether ? string.Empty : "the timeline's stops are not one after the other; ")}{(blocks.Length == 0 ? string.Empty : "also stops: " + string.Join(", ", blocks) + "; ")}the order: {string.Join(", ", order)}");
        Find(editor, "StudioSpeed_1")?.RemoveFromSelection();
        Until(() => SelectedSpeedOf(editor), index => index is null, 1);
    }

    /// <summary>
    /// A project file can hold a rate that is none of the six the editor offers, one outside
    /// what a video plays, and one that is no speed change at all. The window shows what the
    /// editor made of them.
    /// </summary>
    private void RatesOfAProjectFile()
    {
        Timeline.Mark("14: rates a project file holds");

        // Three times the speed, which is none of the six; ten times, which plays as eight; the
        // recording's own speed, which is no speed change; and a tenth, which plays as a quarter.
        var folder = NewScreenProject("Speed, rates of a file", p => p with { Edits = p.Edits with { Speed = [Speed(1, 2, 3), Speed(4, 5, 10), Speed(7, 8, 1), Speed(9, 10, 0.1)] } });
        if (OpenReady(folder, "speed, rates of a file") is not { } editor)
        {
            return;
        }

        StudioSpeedRange[] held = [Speed(1, 2, 3), Speed(4, 5, 8), Speed(9, 10, 0.25)];
        var opened = SpeedsAre(editor, held, null);
        var lane = LaneText(editor, SpeedLane);

        // The video: 1 + 1/3 + 2 + 1/8 + 4 + 4 + 2 seconds, which is 13.46.
        var time = TimeText(editor);
        Find(editor, "StudioSpeed_0")?.Select();
        Until(() => SelectedSpeedOf(editor), index => index == 0, 1);
        var none = RateChosen(editor, -1);
        var rateIndex = OnUi(() => editor.Window.ViewModel.SpeedRateIndex);
        Find(editor, "StudioSpeed_1")?.Select();
        Until(() => SelectedSpeedOf(editor), now => now == 1, 1);
        var eight = RateChosen(editor, 5);
        Find(editor, "StudioSpeed_2")?.Select();
        Until(() => SelectedSpeedOf(editor), now => now == 2, 1);
        var quarter = RateChosen(editor, 0);
        var marks = Enumerable.Range(0, 3).Select(at => SpeedMarks(editor, at)).ToArray();
        _report.Check(
            "a project file's speed changes are shown as the video plays them: a rate that is none of the six is named on its block and none of the six is chosen for it, a rate past the fastest plays and is shown as the fastest, one under the slowest as the slowest, and a stretch at the recording's own speed is no speed change",
            opened is null && lane == "Speed 3×, 1.0 to 2.0 seconds | Speed 8×, 4.0 to 5.0 seconds | Speed 0.25×, 9.0 to 10.0 seconds" && time.EndsWith("/ 0:13.4", StringComparison.Ordinal)
                && none is null && rateIndex == -1 && eight is null && quarter is null
                && marks[0].Faster && !marks[0].Slower && marks[1].Faster && !marks[1].Slower && !marks[2].Faster && marks[2].Slower,
            opened ?? $"the lane: {lane}; the time reads \"{time}\"; with the first selected: {none ?? "none of the six is chosen"} (the view model says {rateIndex}); the second: {eight ?? "8× is chosen"}; the third: {quarter ?? "0.25× is chosen"}; "
                + $"marks: {string.Join(", ", marks.Select(mark => mark.Faster ? "faster" : mark.Slower ? "slower" : "none"))}");

        // A rate chosen for the first one is one of the six from then on.
        Find(editor, "StudioSpeed_0")?.Select();
        Until(() => SelectedSpeedOf(editor), now => now == 0, 1);
        var chosen = Find(editor, "StudioSpeedRate_2")?.Select() ?? false;
        StudioSpeedRange[] changed = [Speed(1, 2, 1.5), held[1], held[2]];
        var after = SpeedsAre(editor, changed, 0) ?? RateChosen(editor, 2);
        Invoke(editor, "StudioUndoButton");
        var undone = SpeedsAre(editor, held, 0) ?? RateChosen(editor, -1);
        _report.Check(
            "one of the six chosen for a speed change with a rate of its own gives it that rate, and Undo gives it its own rate back, with none of the six chosen again",
            chosen && after is null && undone is null,
            after ?? undone ?? "1.5× chosen, then 3× again after Undo");
        CloseQuietly(editor);
    }
}
