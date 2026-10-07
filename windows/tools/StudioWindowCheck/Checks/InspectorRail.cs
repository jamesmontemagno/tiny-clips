using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Host;
using Windows.System;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 3, continued. The inspector as a rail with one panel on show: the rail as a screen reader and
// the Tab key find it, that only the panel on show is in the window, what shows a panel by
// itself and what does not, where the keyboard focus goes when the panel that held it goes
// away, a recording without a camera, and the two crop groups.
//
// WRITTEN ON 6 OCTOBER 2026 IN QUIET MODE AND NOT RUN. Everything here reads the window as it
// is: it looks with FindAsItIs and never has a panel shown for it, except where it says so.
// The rail is the framework's list, and its Up, Down, Home and End are the list's own. The
// tool presses no keys, so those four are not checked here: what is checked is that the list
// is set up the way that makes them choose a panel, and that choosing an item does.
internal sealed partial class WindowChecks
{
    // One control of each panel that is there whenever the panel is on show.
    private static readonly (StudioInspectorPanel Panel, string Id)[] PanelAnchors =
    [
        (StudioInspectorPanel.Scene, "StudioLayoutChoice"),
        (StudioInspectorPanel.Background, "StudioShowBackgroundCheckBox"),
        (StudioInspectorPanel.Screen, "StudioScreenCornerRadiusSlider"),
        (StudioInspectorPanel.Camera, "StudioCameraMirrorCheckBox"),
        (StudioInspectorPanel.Zoom, "StudioZoomSectionAddButton"),
        (StudioInspectorPanel.Cut, "StudioCutSectionAddButton"),
        (StudioInspectorPanel.Speed, "StudioSpeedSectionAddButton"),
        (StudioInspectorPanel.Audio, "StudioMuteCheckBox"),
        (StudioInspectorPanel.Project, "StudioKeepProjectCheckBox"),
    ];

    private void InspectorRail()
    {
        Timeline.Mark("3: the inspector's rail");
        RailWithACamera();
        RailWithoutACamera();
        CropGroupsByThemselves();
    }

    /// <summary>The rail and its items as UI Automation gives them now. Nothing is shown first.</summary>
    private static (UiaElement? Rail, UiaElement[] Items) RailOf(Editor editor)
    {
        var rail = editor.Root.FindAsItIs(InspectorRailId);
        return (rail, rail is null ? [] : [.. rail.Children().Where(item => item.ControlType == ControlTypeNames.ListItem)]);
    }

    /// <summary>Waits until a button can be pressed, and presses it through UI Automation.</summary>
    private static bool PressWhenEnabled(Editor editor, string automationId)
    {
        Until(() => Find(editor, automationId, 0.5)?.IsEnabled, enabled => enabled == true, 2);
        return Invoke(editor, automationId);
    }

    /// <summary>A place along a lane, in effective pixels, as the lanes' pointer handlers take it.</summary>
    private static double LaneX(Editor editor, double time)
    {
        var width = (Find(editor, ZoomLane)?.Bounds.Width ?? 0) / editor.Scale;
        return 12 + (time / RecordingLength * (width - 24));
    }

    // ---------------------------------------------------------------------------------------
    // A recording with a camera
    // ---------------------------------------------------------------------------------------

    private void RailWithACamera()
    {
        Timeline.Mark("3: the rail of a recording with a camera");
        var folder = NewCameraProject("Inspector rail", p => p with
        {
            Scenes = [p.Scenes[0], p.Scenes[0] with { Start = 6.0, Transition = new StudioTransition { Kind = StudioTransitionKind.Morph } }],
        });
        if (OpenReady(folder, "inspector rail") is not { } editor)
        {
            return;
        }

        using var heard = UiaEvents.Listen(_uia, editor.Root);
        var all = StudioInspectorPanels.All;
        string[] titles = ["Scene", "Background", "Screen", "Camera", "Zoom", "Cut", "Speed", "Audio", "Project"];
        string[] summaries =
        [
            "Scene: layout, splits, and transitions", "Background and padding", "Screen: corners, shadow, clicks, and crop", "Camera: placement, appearance, and crop",
            "Zooms", "Cuts", "Speed changes", "Audio: mute", "Project: badge, storage, and default look",
        ];

        // As it opens.
        var opened = PanelShown(editor, "Scene", 2);
        var (rail, items) = RailOf(editor);
        var title = editor.Root.FindAsItIs(InspectorTitleId);
        _report.Check(
            "a recording with a camera opens on the Scene panel: its name is over the inspector as a text, and the rail is a list called Inspector panels with nine items in the order Scene, Background, Screen, Camera, Zoom, Cut, Speed, Audio, Project; each is a list item that can be selected and can take the keyboard focus, is called by its panel's name, says what the panel holds as its description, and has an automation id; Scene is the selected one and no other is",
            rail is { ControlType: ControlTypeNames.List, Name: "Inspector panels", IsEnabled: true } && rail.Patterns.Contains("Selection", StringComparison.Ordinal)
                && title is { ControlType: ControlTypeNames.Text, Name: "Scene" }
                && items.Select(item => item.Name).SequenceEqual(titles) && items.Select(item => item.HelpText).SequenceEqual(summaries)
                && items.Select(item => item.Id).SequenceEqual(all.Select(panel => $"{InspectorRailId}_{panel}"))
                && items.All(item => item.IsEnabled && item.IsKeyboardFocusable && item.Patterns.Contains("SelectionItem", StringComparison.Ordinal))
                && all.Select(StudioInspectorPanels.GetTitle).SequenceEqual(titles) && all.Select(StudioInspectorPanels.GetSummary).SequenceEqual(summaries)
                && IsShown(opened, "Scene") && rail.SelectedNames.SequenceEqual(["Scene"]),
            $"{rail}, patterns({rail?.Patterns}); the name over the inspector: {title}; {PanelWords(opened)}; "
                + $"items: {string.Join(" | ", items.Select(item => $"{item.Name} [{item.Id}] \"{item.HelpText}\", focusable {item.IsKeyboardFocusable}, patterns({item.Patterns})"))}");

        // What the list's own keys rest on.
        var list = OnUi(() => Descendant<ListView>(editor.Window.Content, InspectorRailId) is { } found
            ? (Found: true, found.SelectionMode, found.SingleSelectionFollowsFocus, found.TabNavigation, found.IsItemClickEnabled, Count: found.Items.Count, Index: found.SelectedIndex)
            : default);
        _report.Check(
            "the rail is the framework's list with one item selected at a time, whose selection goes with the keyboard focus inside it, and which is one stop for the Tab key: that is what makes the list's own Up, Down, Home and End choose a panel. Those keys are not pressed here",
            list is { Found: true, SelectionMode: ListViewSelectionMode.Single, SingleSelectionFollowsFocus: true, TabNavigation: KeyboardNavigationMode.Once, IsItemClickEnabled: false, Count: 9, Index: 0 },
            list.Found
                ? $"selection {list.SelectionMode}, follows the focus {list.SingleSelectionFollowsFocus}, Tab navigation {list.TabNavigation}, items are pressed like buttons {list.IsItemClickEnabled}, {list.Count} items, item {list.Index} selected"
                : "the rail is not a ListView");

        // Each panel in turn, chosen the way a screen reader chooses an item, which is also what a press on it does to the list.
        Timeline.Mark("3: each panel chosen on the rail");
        var island = editor.Root.Children().FirstOrDefault(child => child.ClassName == "Microsoft.UI.Content.DesktopChildSiteBridge");
        var (wrongShown, wrongTree, wrongStops) = (new List<string>(), new List<string>(), new List<string>());
        var counts = new List<string>();
        var mark = heard.Mark();
        foreach (var panel in all)
        {
            var name = StudioInspectorPanels.GetTitle(panel);
            var selected = RailOf(editor).Items.FirstOrDefault(item => item.Name == name)?.Select() ?? false;
            var shown = PanelShown(editor, name, 2);
            if (!selected || !IsShown(shown, name))
            {
                wrongShown.Add($"{name}: selected {selected}; {PanelWords(shown)}");
            }

            // What a screen reader walks, and everything UI Automation knows of the window.
            var anchor = PanelAnchors.First(entry => entry.Panel == panel).Id;
            var walked = Content(editor).Select(entry => entry.Element.Id).Where(id => PlaceOf(id) is not null).ToArray();
            string[] known = island is null ? [] : [.. Tree(island, raw: true).Select(entry => entry.Element.Id).Where(id => PlaceOf(id) is not null)];
            var strangers = walked.Concat(known).Where(id => PlaceOf(id) is { } place && place.Panel != panel).Distinct().ToArray();
            if (strangers.Length > 0 || !walked.Contains(anchor) || island is null)
            {
                wrongTree.Add($"{name}: {(walked.Contains(anchor) ? string.Empty : $"{anchor} is not there; ")}{(strangers.Length == 0 ? "no control of another panel" : "of other panels: " + string.Join(", ", strangers))}");
            }

            // The stops of the Tab key with this panel on show.
            var stops = TabStopsAsItIs(editor);
            var at = stops.IndexOf(InspectorRailId);
            var mine = stops.Where(id => PlaceOf(id) is not null).ToArray();
            var foreign = mine.Where(id => PlaceOf(id) is { } place && place.Panel != panel).ToArray();
            var single = stops.Where(id => id.StartsWith(InspectorRailId + "_", StringComparison.Ordinal)).ToArray();
            if (stops.Count(id => id == InspectorRailId) != 1 || foreign.Length > 0 || single.Length > 0 || mine.Length == 0 || stops.IndexOf(mine[0]) != at + 1)
            {
                wrongStops.Add($"{name}: the rail is a stop {stops.Count(id => id == InspectorRailId)} time(s), at {at}; the panel's first stop is {(mine.Length == 0 ? "missing" : $"{mine[0]} at {stops.IndexOf(mine[0])}")}; "
                    + $"{(foreign.Length == 0 ? string.Empty : "stops of other panels: " + string.Join(", ", foreign) + "; ")}{(single.Length == 0 ? string.Empty : "single items of the rail: " + string.Join(", ", single) + "; ")}the stops: {string.Join(", ", stops)}");
            }

            counts.Add($"{name} {walked.Length} controls and {mine.Length} stops");
        }

        _report.Check(
            "each of the nine items, selected through UI Automation as a screen reader selects it, shows its panel at once: the name over the inspector is the panel's, and the rail has that item selected and no other",
            wrongShown.Count == 0,
            wrongShown.Count == 0 ? string.Join(", ", titles) : string.Join(" | ", wrongShown));
        _report.Check(
            "with each panel on show, only that panel's controls are in the window for a screen reader: of the inspector's controls, none of another panel is in what a screen reader walks, nor in everything UI Automation knows of the window, and one that the panel always has is there",
            wrongTree.Count == 0,
            wrongTree.Count == 0 ? string.Join("; ", counts) : string.Join(" | ", wrongTree));
        _report.Check(
            "with each panel on show, the Tab key comes to the rail once, as one stop, and goes on from it to that panel's first control; no control of another panel is a stop, and no single item of the rail is one",
            wrongStops.Count == 0,
            wrongStops.Count == 0 ? string.Join("; ", counts) : string.Join(" | ", wrongStops));

        // What a screen reader was told while the panels were chosen: each newly selected item, and no sentence.
        var selectedEvents = heard.WaitFor(mark, "selected", e => false, 0.5).Where(e => e.Id.StartsWith(InspectorRailId + "_", StringComparison.Ordinal)).Select(e => e.Id).ToArray();

        // Each event once, however often the framework sends it. Scene was selected already when it was chosen, which may or may not be told.
        var inTurn = selectedEvents.Where((id, index) => index == 0 || selectedEvents[index - 1] != id).SkipWhile(id => id == $"{InspectorRailId}_Scene").ToArray();
        var sentences = heard.Since(mark, "notification").Select(e => e.Text).Distinct().ToArray();
        _report.Check(
            "a screen reader is told each item of the rail as it becomes the selected one, by the events of the list itself, and choosing a panel announces no sentence of its own",
            inTurn.SequenceEqual(all.Skip(1).Select(panel => $"{InspectorRailId}_{panel}")) && sentences.Length == 0,
            $"{selectedEvents.Length} selection events: {string.Join(", ", inTurn.Select(id => id.Replace(InspectorRailId + "_", string.Empty, StringComparison.Ordinal)))}; sentences: {(sentences.Length == 0 ? "none" : string.Join(" | ", sentences))}{(heard.Problem is null ? string.Empty : $" ({heard.Problem})")}");

        // A panel is always on show: taking the selection off the chosen item leaves it chosen.
        var taken = RailOf(editor).Items.LastOrDefault()?.RemoveFromSelection() ?? false;
        Thread.Sleep(300);
        var still = PanelShown(editor, "Project", 1);
        _report.Check(
            "taking the selection off the chosen item of the rail chooses nothing else: the Project panel stays on show and its item stays the selected one",
            IsShown(still, "Project"),
            $"taken out of the selection: {taken}; {PanelWords(still)}");

        WhatShowsAPanel(editor);
        WhereTheFocusGoes(editor, heard);
        WhatLeavesThePanel(editor);
        CloseQuietly(editor);
    }

    /// <summary>
    /// The table of what shows a panel by itself. Before each act another panel is put on show,
    /// so that the act has something to change. The recording has two scenes, both in the
    /// bubble layout, and to begin with no zoom, no cut and no speed change.
    /// </summary>
    private void WhatShowsAPanel(Editor editor)
    {
        Timeline.Mark("3: what shows a panel");
        var (wrong, steps) = (new List<string>(), new List<string>());
        void Shows(string what, StudioInspectorPanel from, Action act, StudioInspectorPanel wanted)
        {
            ShowPanel(editor, from);
            act();
            var name = StudioInspectorPanels.GetTitle(wanted);
            var shown = PanelShown(editor, name, 1.5);
            steps.Add($"{what}: {shown.Title}");
            if (!IsShown(shown, name))
            {
                wrong.Add($"{what}: {PanelWords(shown)}, and it should be {name}");
            }
        }

        void Report(string name)
        {
            var held = $"the editor holds {ZoomsOf(editor).Length} zoom(s), {CutsOf(editor).Length} cut(s), {SpeedsOf(editor).Length} speed change(s) and {ScenesOf(editor).Length} scenes";
            _report.Check(name, wrong.Count == 0, wrong.Count == 0 ? $"{string.Join("; ", steps)}; {held}" : $"{string.Join(" | ", wrong)}; {held}");
            wrong.Clear();
            steps.Clear();
        }

        bool HoldsOneOfEach() => ZoomsOf(editor).Length == 1 && CutsOf(editor).Length == 1 && SpeedsOf(editor).Length == 1 && ScenesOf(editor).Length == 3;

        // The four buttons of the transport row, each where there is room for what it adds.
        Shows("Add zoom at 1.0 s", StudioInspectorPanel.Background, () => { SetSlider(editor, "StudioPlayhead", 1.0); PressWhenEnabled(editor, "StudioAddZoomButton"); }, StudioInspectorPanel.Zoom);
        Shows("Cut at 4.4 s", StudioInspectorPanel.Background, () => { SetSlider(editor, "StudioPlayhead", 4.4); PressWhenEnabled(editor, "StudioAddCutButton"); }, StudioInspectorPanel.Cut);
        Shows("Speed at 7.0 s", StudioInspectorPanel.Background, () => { SetSlider(editor, "StudioPlayhead", 7.0); PressWhenEnabled(editor, "StudioAddSpeedButton"); }, StudioInspectorPanel.Speed);
        Shows("Split at 10.0 s", StudioInspectorPanel.Background, () => { SetSlider(editor, "StudioPlayhead", 10.0); PressWhenEnabled(editor, "StudioSplitSceneButton"); }, StudioInspectorPanel.Scene);
        if (!HoldsOneOfEach())
        {
            wrong.Add("one of the four buttons added nothing");
        }

        Report("adding a zoom, a cut, a speed change or a scene with the buttons of the transport row shows the Zoom, the Cut, the Speed or the Scene panel, whatever panel was on show");

        // The keys, each where what it would add is already there or cannot be: nothing is added, and the panel is shown all the same.
        Shows("what Z runs inside the zoom", StudioInspectorPanel.Background, () => { SetSlider(editor, "StudioPlayhead", 2.0); Key(editor, StudioShortcutKey.Z); }, StudioInspectorPanel.Zoom);
        Shows("what X runs inside the cut", StudioInspectorPanel.Background, () => { SetSlider(editor, "StudioPlayhead", 4.8); Key(editor, StudioShortcutKey.X); }, StudioInspectorPanel.Cut);
        Shows("what R runs inside the speed change", StudioInspectorPanel.Background, () => { SetSlider(editor, "StudioPlayhead", 8.0); Key(editor, StudioShortcutKey.R); }, StudioInspectorPanel.Speed);
        Shows("what S runs a tenth of a second into a scene, where it cannot be split", StudioInspectorPanel.Background, () => { SetSlider(editor, "StudioPlayhead", 10.1); Key(editor, StudioShortcutKey.S); }, StudioInspectorPanel.Scene);
        if (!HoldsOneOfEach())
        {
            wrong.Add("one of the four keys changed the project");
        }

        Report("what Z, X, R and S run shows the Zoom, the Cut, the Speed and the Scene panel also where it adds nothing: where a zoom, a cut or a speed change already is, and where a scene cannot be split");

        // Selected on the lanes, as a screen reader selects an item. The playhead stays.
        Shows("the zoom selected", StudioInspectorPanel.Background, () => Find(editor, "StudioZoom_0")?.Select(), StudioInspectorPanel.Zoom);
        Shows("the cut selected", StudioInspectorPanel.Background, () => Find(editor, "StudioCut_0")?.Select(), StudioInspectorPanel.Cut);
        Shows("the speed change selected", StudioInspectorPanel.Background, () => Find(editor, "StudioSpeed_0")?.Select(), StudioInspectorPanel.Speed);
        Shows("the first scene selected", StudioInspectorPanel.Background, () => Find(editor, "StudioScene_0")?.Select(), StudioInspectorPanel.Scene);
        Shows("the zoom selected, and selected once more", StudioInspectorPanel.Project, () =>
        {
            Find(editor, "StudioZoom_0")?.Select();
            ShowPanel(editor, StudioInspectorPanel.Project);
            Find(editor, "StudioZoom_0")?.Select();
        }, StudioInspectorPanel.Zoom);
        Report("selecting a zoom, a cut, a speed change or a scene on its lane through UI Automation shows its panel, also when it is the selected one already");

        // The lanes' own keys, by what their key handler runs. With none selected an arrow starts from the playhead.
        Shows("Home on the zoom lane", StudioInspectorPanel.Project, () => LaneKey(editor, VirtualKey.Home, ZoomLane), StudioInspectorPanel.Zoom);
        Shows("End on the cut lane", StudioInspectorPanel.Project, () => LaneKey(editor, VirtualKey.End, CutLane), StudioInspectorPanel.Cut);
        Shows("Right on the speed lane, from before the speed change", StudioInspectorPanel.Project, () =>
        {
            Find(editor, "StudioSpeed_0")?.RemoveFromSelection();
            SetSlider(editor, "StudioPlayhead", 0.5);
            LaneKey(editor, VirtualKey.Right, SpeedLane);
        }, StudioInspectorPanel.Speed);
        Shows("Left on the scene lane, from the third scene", StudioInspectorPanel.Project, () =>
        {
            SetSlider(editor, "StudioPlayhead", 11.0);
            LaneKey(editor, VirtualKey.Left, SceneLane);
        }, StudioInspectorPanel.Scene);
        Report("what Home, End, Left and Right run on a lane that has the keyboard focus selects an item and shows its panel");

        // The pointer, by what the pointer handlers call: the camera dragged in the preview, and a press on a scene's block.
        Shows("the camera dragged in the preview", StudioInspectorPanel.Background, () =>
        {
            SetSlider(editor, "StudioPlayhead", 3.0);
            OnUi(() =>
            {
                var viewModel = editor.Window.ViewModel;
                viewModel.BeginGesture();
                viewModel.MoveBubbleTopLeft(600, 300);
                viewModel.EndGesture();
            });
        }, StudioInspectorPanel.Camera);
        Shows("a press on the second scene's block", StudioInspectorPanel.Background, () =>
        {
            var x = LaneX(editor, 8.0);
            OnUi(() =>
            {
                var lane = LaneOf(editor, SceneLane)!;
                lane.PressAt(x);
                lane.EndPress();
            });
        }, StudioInspectorPanel.Scene);

        // In a scene that hides the camera, the Camera panel has a button that shows the Scene panel, where the layout is.
        UiaElement? button = null;
        Shows("Show scene, in the Camera panel of a scene in the Screen layout", StudioInspectorPanel.Camera, () =>
        {
            SetSlider(editor, "StudioPlayhead", 3.0);
            Key(editor, StudioShortcutKey.Digit1);
            button = Until(() => editor.Root.FindAsItIs("StudioShowSceneButton"), found => found is not null, 2);
            button?.Invoke();
        }, StudioInspectorPanel.Scene);
        Key(editor, StudioShortcutKey.Digit2);
        if (button is not { Name: "Show scene" })
        {
            wrong.Add($"the button is {(button is null ? "not there" : button.ToString())}");
        }

        Report("dragging the camera in the preview shows the Camera panel, a press on a scene's block the Scene panel, and so does Show scene, the button the Camera panel has where the scene hides the camera (by what the pointer handlers call)");
    }

    /// <summary>
    /// Where the keyboard focus is after a panel was shown by something else than the rail. A
    /// control of the panel that goes away cannot keep the focus, and the rail has one stop, the
    /// chosen item: both times the focus is put on the rail's item for the new panel. Anywhere
    /// else it stays. The same holds for a control that goes away inside its panel: the
    /// selected zoom's, when the zoom is let go of or deleted. The focus is read from the
    /// window's own elements, because the window does not have the keyboard.
    /// </summary>
    private void WhereTheFocusGoes(Editor editor, UiaEvents heard)
    {
        Timeline.Mark("3: the keyboard focus when a panel is shown by itself");

        // On a slider of the Background panel, and Z: the Background panel goes.
        ShowPanel(editor, StudioInspectorPanel.Background);
        SetSlider(editor, "StudioPlayhead", 2.0);
        var onSlider = FocusOn(editor, "StudioPaddingSlider");
        var mark = heard.Mark();
        Key(editor, StudioShortcutKey.Z);
        var zoom = PanelShown(editor, "Zoom", 1.5);
        var afterZ = Until(() => FocusedId(editor), id => id == $"{InspectorRailId}_Zoom", 1.5);
        var told = heard.WaitFor(mark, "notification", e => false, 0.6).Select(e => e.Text).Distinct().ToArray();
        _report.Check(
            "with the keyboard focus on a slider of the Background panel, what Z runs shows the Zoom panel and puts the focus on the rail's Zoom item, and not on whatever comes after the panel that went away; a screen reader is told what the key did, here that a zoom is already there, and nothing about the panel",
            onSlider == "StudioPaddingSlider" && IsShown(zoom, "Zoom") && afterZ == $"{InspectorRailId}_Zoom" && told.SequenceEqual([StudioEditorText.ZoomAlreadyThereMessage]),
            $"the focus was on \"{onSlider}\" and is on \"{afterZ}\"; {PanelWords(zoom)}; sentences: {(told.Length == 0 ? "none" : string.Join(" | ", told))}");

        // The focus is in the rail now, and X shows another panel: the focus goes with the choice.
        SetSlider(editor, "StudioPlayhead", 4.8);
        Key(editor, StudioShortcutKey.X);
        var cut = PanelShown(editor, "Cut", 1.5);
        var afterX = Until(() => FocusedId(editor), id => id == $"{InspectorRailId}_Cut", 1.5);
        _report.Check(
            "with the keyboard focus in the rail, what X runs shows the Cut panel and the focus goes with the choice, to the rail's Cut item",
            afterZ == $"{InspectorRailId}_Zoom" && IsShown(cut, "Cut") && afterX == $"{InspectorRailId}_Cut",
            $"the focus was on \"{afterZ}\" and is on \"{afterX}\"; {PanelWords(cut)}");

        // On Play, which is neither in the panel nor in the rail: the focus stays.
        var onPlay = FocusOn(editor, "StudioPlayPauseButton");
        SetSlider(editor, "StudioPlayhead", 8.0);
        Key(editor, StudioShortcutKey.R);
        var speed = PanelShown(editor, "Speed", 1.5);
        Thread.Sleep(200);
        var afterR = FocusedId(editor);
        _report.Check(
            "with the keyboard focus on Play, what R runs shows the Speed panel and leaves the focus on Play",
            onPlay == "StudioPlayPauseButton" && IsShown(speed, "Speed") && afterR == "StudioPlayPauseButton",
            $"the focus was on \"{onPlay}\" and is on \"{afterR}\"; {PanelWords(speed)}");

        // Show scene, pressed with the focus on it: the button is in the panel that goes away.
        SetSlider(editor, "StudioPlayhead", 3.0);
        Key(editor, StudioShortcutKey.Digit1);
        ShowPanel(editor, StudioInspectorPanel.Camera);
        Until(() => editor.Root.FindAsItIs("StudioShowSceneButton"), found => found is not null, 2);
        var onButton = FocusOn(editor, "StudioShowSceneButton");
        var pressed = Invoke(editor, "StudioShowSceneButton");
        var scene = PanelShown(editor, "Scene", 1.5);
        var afterButton = Until(() => FocusedId(editor), id => id == $"{InspectorRailId}_Scene", 1.5);
        Key(editor, StudioShortcutKey.Digit2);
        _report.Check(
            "Show scene, pressed with the keyboard focus on it, shows the Scene panel and puts the focus on the rail's Scene item: the button is in the Camera panel, which goes away",
            onButton == "StudioShowSceneButton" && pressed && IsShown(scene, "Scene") && afterButton == $"{InspectorRailId}_Scene",
            $"the focus was on \"{onButton}\" and is on \"{afterButton}\"; {PanelWords(scene)}");

        // A slider of the selected zoom has the focus, and Add cut is pressed through UI
        // Automation, as a screen reader presses a button the keyboard is not on. Selecting the
        // cut lets go of the zoom, which takes the zoom's controls away, in a panel that is
        // being replaced. First where the cut from above is, which is selected and nothing is
        // added; then, with that cut deleted, where one is added.
        SetSlider(editor, "StudioPlayhead", 2.0);
        Key(editor, StudioShortcutKey.Z);
        PanelShown(editor, "Zoom", 1.5);
        var onZoomSlider = FocusOn(editor, "StudioZoomScaleSlider");
        SetSlider(editor, "StudioPlayhead", 4.8);
        var cuts = CutsOf(editor).Length;
        var selectedIt = PressWhenEnabled(editor, "StudioAddCutButton");
        var forSelected = PanelShown(editor, "Cut", 1.5);
        var afterSelected = Until(() => FocusedId(editor), id => id == $"{InspectorRailId}_Cut", 1.5);
        var cutsWhenSelected = CutsOf(editor).Length;

        Key(editor, StudioShortcutKey.Delete);
        var cutsWhenDeleted = Until(() => CutsOf(editor).Length, count => count == cuts - 1, 1.5);
        SetSlider(editor, "StudioPlayhead", 2.0);
        Key(editor, StudioShortcutKey.Z);
        PanelShown(editor, "Zoom", 1.5);
        var onZoomSliderAgain = FocusOn(editor, "StudioZoomScaleSlider");
        SetSlider(editor, "StudioPlayhead", 4.8);
        var addedIt = PressWhenEnabled(editor, "StudioAddCutButton");
        var forAdded = PanelShown(editor, "Cut", 1.5);
        var afterAdded = Until(() => FocusedId(editor), id => id == $"{InspectorRailId}_Cut", 1.5);
        var cutsWhenAdded = Until(() => CutsOf(editor).Length, count => count == cuts, 1.5);
        _report.Check(
            "Add cut, pressed through UI Automation while the keyboard focus is on a slider of the selected zoom, shows the Cut panel and puts the focus on the rail's Cut item, and not on whatever comes after the inspector: where a cut already is, which is selected, and where one is added",
            onZoomSlider == "StudioZoomScaleSlider" && selectedIt && IsShown(forSelected, "Cut") && afterSelected == $"{InspectorRailId}_Cut" && cutsWhenSelected == cuts
                && cutsWhenDeleted == cuts - 1 && onZoomSliderAgain == "StudioZoomScaleSlider" && addedIt && IsShown(forAdded, "Cut") && afterAdded == $"{InspectorRailId}_Cut" && cutsWhenAdded == cuts,
            $"where a cut is: the focus was on \"{onZoomSlider}\" and is on \"{afterSelected}\", {PanelWords(forSelected)}, {cutsWhenSelected} of {cuts} cuts; where one is added, after {cutsWhenDeleted} were left: the focus was on \"{onZoomSliderAgain}\" and is on \"{afterAdded}\", {PanelWords(forAdded)}, {cutsWhenAdded} cuts");

        // Delete, with the focus on a slider of the selected zoom: the zoom goes, and its
        // controls with it, and no other panel comes. The key took away the control that had
        // the focus, which is put on the rail's item for the panel on show. Undo brings the
        // zoom back for what follows.
        SetSlider(editor, "StudioPlayhead", 2.0);
        Key(editor, StudioShortcutKey.Z);
        PanelShown(editor, "Zoom", 1.5);
        var zooms = ZoomsOf(editor).Length;
        var onSliderForDelete = FocusOn(editor, "StudioZoomScaleSlider");
        var deleteRan = Key(editor, StudioShortcutKey.Delete);
        var zoomsLeft = Until(() => ZoomsOf(editor).Length, count => count == zooms - 1, 1.5);
        var afterDelete = Until(() => FocusedId(editor), id => id == $"{InspectorRailId}_Zoom", 1.5);
        var stillZoom = PanelShown(editor);
        Key(editor, StudioShortcutKey.Z, control: true);
        var zoomsBack = Until(() => ZoomsOf(editor).Length, count => count == zooms, 1.5);
        _report.Check(
            "what Delete runs with the keyboard focus on a slider of the selected zoom deletes the zoom and leaves the Zoom panel on show; the focus goes to the rail's Zoom item, and not to whatever comes after the inspector",
            onSliderForDelete == "StudioZoomScaleSlider" && deleteRan == StudioShortcutAction.RemoveSelectedZoom && zoomsLeft == zooms - 1 && IsShown(stillZoom, "Zoom") && afterDelete == $"{InspectorRailId}_Zoom" && zoomsBack == zooms,
            $"the focus was on \"{onSliderForDelete}\" and is on \"{afterDelete}\"; the key ran {deleteRan}; {zoomsLeft} of {zooms} zooms were left; {PanelWords(stillZoom)}; after what Ctrl+Z runs there are {zoomsBack}");

        // The focus alone never chooses: put on an item of the rail that is not the chosen one, it goes to the chosen one.
        ShowPanel(editor, StudioInspectorPanel.Project);
        FocusOn(editor, "StudioPlayPauseButton");
        OnUi(() =>
        {
            Descendant<ListViewItem>(editor.Window.Content, $"{InspectorRailId}_Cut")?.Focus(FocusState.Keyboard);
        });
        Thread.Sleep(200);
        var landed = FocusedId(editor);
        var project = PanelShown(editor);
        _report.Check(
            "the keyboard focus alone chooses no panel: put on the rail's Cut item from outside the rail while Project is the chosen one, it lands on the Project item, and the Project panel stays on show",
            landed == $"{InspectorRailId}_Project" && IsShown(project, "Project"),
            $"the focus is on \"{landed}\"; {PanelWords(project)}");
    }

    /// <summary>
    /// What leaves the panel alone: undo and redo, the layout keys, playing, moving the
    /// playhead, a press on an empty part of a lane, letting go of what is selected, deleting
    /// it, and the keyboard focus coming to a lane. The Project panel is put on show before
    /// each, because nothing in the table shows it.
    /// </summary>
    private void WhatLeavesThePanel(Editor editor)
    {
        Timeline.Mark("3: what leaves the panel alone");
        var (wrong, steps) = (new List<string>(), new List<string>());
        void Stays(string what, Action? prepare, Action act)
        {
            prepare?.Invoke();
            ShowPanel(editor, StudioInspectorPanel.Project);
            act();
            Thread.Sleep(250);
            var shown = PanelShown(editor);
            steps.Add(what);
            if (!IsShown(shown, "Project"))
            {
                wrong.Add($"{what}: {PanelWords(shown)}");
            }
        }

        // Something to undo is there from the checks before this.
        Stays("Undo", null, () => PressWhenEnabled(editor, "StudioUndoButton"));
        Stays("Redo", null, () => PressWhenEnabled(editor, "StudioRedoButton"));
        Stays("what Ctrl+Z runs", null, () => Key(editor, StudioShortcutKey.Z, control: true));
        Stays("what Ctrl+Y runs", null, () => Key(editor, StudioShortcutKey.Y, control: true));
        Stays("what the keys 1, 3, 4 and 2 run", null, () =>
        {
            SetSlider(editor, "StudioPlayhead", 3.0);
            foreach (var key in new[] { StudioShortcutKey.Digit1, StudioShortcutKey.Digit3, StudioShortcutKey.Digit4, StudioShortcutKey.Digit2 })
            {
                Key(editor, key);
            }
        });
        var played = 0.0;
        Stays("playing from 0.5 s into the zoom", null, () =>
        {
            SetSlider(editor, "StudioPlayhead", 0.5);
            var from = Playhead(editor);
            Invoke(editor, "StudioPlayPauseButton");
            played = Until(() => Playhead(editor), value => value > from + 0.8, 5) - from;
            if (NameOf(editor, "StudioPlayPauseButton", 0.5) == "Pause")
            {
                Invoke(editor, "StudioPlayPauseButton");
            }

            Until(() => NameOf(editor, "StudioPlayPauseButton", 0.5), name => name == "Play", 2);
        });
        Stays("the playhead moved into the zoom, the cut, the speed change and the third scene", null, () =>
        {
            foreach (var time in new[] { 2.0, 4.8, 8.0, 11.0, 3.0 })
            {
                SetSlider(editor, "StudioPlayhead", time);
            }
        });
        Stays("a press on an empty part of the zoom, the cut and the speed lane", () => Find(editor, "StudioZoom_0")?.Select(), () =>
        {
            var x = LaneX(editor, 11.6);
            foreach (var lane in new[] { ZoomLane, CutLane, SpeedLane })
            {
                OnUi(() =>
                {
                    var pressed = LaneOf(editor, lane)!;
                    pressed.PressAt(x);
                    pressed.EndPress();
                });
            }
        });
        Stays("the selected cut taken out of the selection", () => Find(editor, "StudioCut_0")?.Select(), () => Find(editor, "StudioCut_0")?.RemoveFromSelection());
        Stays("the keyboard focus put on the cut lane, which selects nothing", null, () => FocusOn(editor, CutLane));
        var nothingSelected = SelectedCutOf(editor) is null && SelectedZoomOf(editor) is null && SelectedSpeedOf(editor) is null;
        var zoomsBefore = ZoomsOf(editor).Length;
        Stays("the selected zoom deleted by what Delete runs", () =>
        {
            FocusOn(editor, "StudioPlayPauseButton");
            Find(editor, "StudioZoom_0")?.Select();
        }, () => Key(editor, StudioShortcutKey.Delete));
        var zoomsAfter = ZoomsOf(editor).Length;
        _report.Check(
            "undo and redo, the layout keys, playing, moving the playhead, a press on an empty part of a lane, taking an item out of the selection, the keyboard focus coming to a lane, and deleting what is selected all leave the panel that is on show: the Project panel stays through each",
            wrong.Count == 0 && played > 0.8 && nothingSelected && zoomsBefore == 1 && zoomsAfter == 0,
            wrong.Count == 0
                ? $"{string.Join("; ", steps)}; the preview played {F(played, "0.00")} s; with the focus on the cut lane nothing was selected: {nothingSelected}; zooms before the delete {zoomsBefore}, after it {zoomsAfter}"
                : string.Join(" | ", wrong));
    }

    // ---------------------------------------------------------------------------------------
    // A recording without a camera
    // ---------------------------------------------------------------------------------------

    private void RailWithoutACamera()
    {
        Timeline.Mark("3: the rail of a recording without a camera");
        if (OpenReady(NewScreenProject("Inspector rail, no camera"), "inspector rail, no camera") is not { } editor)
        {
            return;
        }

        var opened = PanelShown(editor, "Background", 2);
        var (rail, items) = RailOf(editor);
        string[] titles = ["Background", "Screen", "Zoom", "Cut", "Speed", "Audio", "Project"];
        _report.Check(
            "a recording without a camera opens on the Background panel, and its rail has seven items: Background, Screen, Zoom, Cut, Speed, Audio, Project. There is no Scene and no Camera item",
            IsShown(opened, "Background") && items.Select(item => item.Name).SequenceEqual(titles) && rail?.SelectedNames.SequenceEqual(["Background"]) == true
                && StudioInspectorPanels.GetAvailable(false).Select(StudioInspectorPanels.GetTitle).SequenceEqual(titles),
            $"{PanelWords(opened)}; items: {string.Join(", ", items.Select(item => item.Name))}");

        // Asked for a panel it does not have, the editor shows the nearest one it has.
        OnUi(() => editor.Window.ViewModel.ShowInspectorPanel(StudioInspectorPanel.Camera));
        var forCamera = PanelShown(editor, "Screen", 1.5);
        var note = editor.Root.FindAsItIs("StudioNoCameraNote")?.Name ?? string.Empty;
        OnUi(() => editor.Window.ViewModel.ShowInspectorPanel(StudioInspectorPanel.Scene));
        var forScene = PanelShown(editor, "Background", 1.5);

        // S asks for the Scene panel, as it does with a camera.
        OnUi(() => editor.Window.ViewModel.ShowInspectorPanel(StudioInspectorPanel.Project));
        PanelShown(editor, "Project", 1.5);
        var key = Key(editor, StudioShortcutKey.S);
        var forSplit = PanelShown(editor, "Background", 1.5);
        _report.Check(
            "asked for the Camera panel a recording without a camera shows Screen, which says that the recording has no camera, and asked for Scene it shows Background; what S runs, which splits nothing there, shows Background too",
            IsShown(forCamera, "Screen") && note.Length > 0 && IsShown(forScene, "Background") && key == StudioShortcutAction.SplitScene && IsShown(forSplit, "Background") && ScenesOf(editor).Length == 1,
            $"for Camera: {PanelWords(forCamera)}, the note: \"{note}\"; for Scene: {PanelWords(forScene)}; S ran {key}: {PanelWords(forSplit)}");

        // Through every panel: none of the Scene panel's or the Camera panel's controls is anywhere.
        var (tree, problems, elements, _, _, saved) = ReadEveryPanel(editor, "tree-rail-no-camera.txt");
        var ofThose = tree.Select(entry => entry.Element.Id).Where(id => PlaceOf(id) is { Panel: StudioInspectorPanel.Scene or StudioInspectorPanel.Camera }).Distinct().ToArray();
        var inPanels = tree.Select(entry => entry.Element.Id).Where(id => PlaceOf(id) is not null).Distinct().Count();
        _report.Check(
            "with each of the seven panels on show in turn, no control of the Scene panel or of the Camera panel is in what a screen reader walks, and every element has a name",
            ofThose.Length == 0 && problems.Count == 0 && inPanels > 20,
            problems.Count > 0 ? string.Join("; ", problems) : $"{elements} elements, {inPanels} of them controls of the panels; {(ofThose.Length == 0 ? "none of Scene or Camera" : "of Scene or Camera: " + string.Join(", ", ofThose))}; saved as {saved}");
        CloseQuietly(editor);
    }

    // ---------------------------------------------------------------------------------------
    // The crop groups
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The Crop group of the Screen panel and of the Camera panel, left to themselves: closed
    /// while nothing is cropped, open while something is, until the header is pressed or one
    /// of its sliders moves an edge. From then on a group is as the header left it, and one
    /// that a slider was moved in stays open. A header is pressed here through UI Automation,
    /// which expands and collapses the group as a screen reader does. The screen comes cropped.
    /// </summary>
    /// <remarks>
    /// A group is the framework's expander, which tells UI Automation that it is a button
    /// with a name that can be expanded and collapsed (ExpanderAutomationPeer), and not a group.
    /// </remarks>
    private void CropGroupsByThemselves()
    {
        Timeline.Mark("3: the crop groups");
        var crop = new StudioRect(0.1, 0, 0.9, 1);
        if (OpenReady(NewCameraProject("Crop groups", p => p with { Screen = p.Screen with { Crop = crop } }), "crop groups") is not { } editor)
        {
            return;
        }

        string[] screenIds = ["StudioScreenCropLeftSlider", "StudioScreenCropTopSlider", "StudioScreenCropRightSlider", "StudioScreenCropBottomSlider"];
        string[] cameraIds = ["StudioCameraCropLeftSlider", "StudioCameraCropTopSlider", "StudioCameraCropRightSlider", "StudioCameraCropBottomSlider"];
        int There(string[] ids) => ids.Count(id => editor.Root.FindAsItIs(id) is not null);
        UiaElement? CropGroupOf(string id) => editor.Root.FindAsItIs(id);

        // As the project opens: the screen is cropped and the camera is not.
        ShowPanel(editor, StudioInspectorPanel.Screen);
        var screenSliders = Until(() => There(screenIds), count => count == 4, 2);
        var screen = CropGroupOf("StudioScreenCropGroup");
        var screenNames = screenIds.Select(id => editor.Root.FindAsItIs(id)).Select(slider => $"{slider?.Name} {slider?.ValueText}").ToArray();
        ShowPanel(editor, StudioInspectorPanel.Camera);
        Thread.Sleep(500);
        var camera = CropGroupOf("StudioCameraCropGroup");
        var cameraSliders = There(cameraIds);
        _report.Check(
            "a project whose screen is cropped opens with the Screen panel's Crop group open, its four sliders saying whose crop they are and how much, and with the Camera panel's Crop group closed, because the camera is not cropped; each group is to a screen reader what the framework's expander is, a button with a name that can be expanded and collapsed, and says nothing more while it shows what there is",
            screen is { ControlType: ControlTypeNames.Button, Name: "Screen crop", IsExpanded: true, HelpText: "" } && screenSliders == 4
                && screenNames.SequenceEqual(["Screen crop left 10%", "Screen crop top 0%", "Screen crop right 0%", "Screen crop bottom 0%"])
                && camera is { ControlType: ControlTypeNames.Button, Name: "Camera crop", IsExpanded: false, HelpText: "" } && cameraSliders == 0,
            $"{screen}, expanded {screen?.IsExpanded}, described as \"{screen?.HelpText}\", {screenSliders} sliders: {string.Join(", ", screenNames)}; {camera}, expanded {camera?.IsExpanded}, described as \"{camera?.HelpText}\", {cameraSliders} sliders");

        // Reset crop, with the keyboard focus on it, in a group that is open only because of the crop: the group closes under the focus.
        ShowPanel(editor, StudioInspectorPanel.Screen);
        Until(() => There(screenIds), count => count == 4, 2);
        var onReset = FocusOn(editor, "StudioScreenCropResetButton");
        var reset = Invoke(editor, "StudioScreenCropResetButton");
        var closed = Until(() => CropGroupOf("StudioScreenCropGroup")?.IsExpanded, expanded => expanded == false, 2);
        var slidersGone = Until(() => There(screenIds), count => count == 0, 2);
        var onHeader = Until(() => FocusedStop(editor), stop => stop == "StudioScreenCropGroup", 1.5);
        var header = FocusedId(editor);
        Invoke(editor, "StudioUndoButton");
        var openAgain = Until(() => CropGroupOf("StudioScreenCropGroup")?.IsExpanded, expanded => expanded == true, 2);
        var slidersBack = Until(() => There(screenIds), count => count == 4, 2);
        _report.Check(
            "Reset crop, pressed with the keyboard focus on it in a group whose header was never pressed, takes the crop away and the group closes with it; the focus goes to the group's header, and not to whatever comes after the group; Undo brings the crop back and the group opens again by itself",
            onReset == "StudioScreenCropResetButton" && reset && closed == false && slidersGone == 0 && onHeader == "StudioScreenCropGroup" && openAgain == true && slidersBack == 4,
            $"the focus was on \"{onReset}\" and is on the header of \"{onHeader}\" (\"{header}\"); after the reset the group is expanded: {closed}, with {slidersGone} sliders; after Undo: {openAgain}, with {slidersBack}");

        // A slider of the group brings the only edge that cuts anything off back to nothing.
        // The group has followed its crop until now, and would close under the slider: an edge
        // moved in it leaves it open. Undo brings the crop back for what follows.
        var onLeft = FocusOn(editor, "StudioScreenCropLeftSlider");
        var movedBack = SetSlider(editor, "StudioScreenCropLeftSlider", 0);
        var uncropped = Until(() => OnUi(() => !editor.Window.ViewModel.CanResetScreenCrop), done => done, 1.5);
        Thread.Sleep(500);
        var staysOpen = CropGroupOf("StudioScreenCropGroup")?.IsExpanded;
        var slidersStay = There(screenIds);
        var stillOnLeft = FocusedId(editor);
        Invoke(editor, "StudioUndoButton");
        var croppedAgain = Until(() => OnUi(() => editor.Window.ViewModel.CanResetScreenCrop), done => done, 1.5);
        _report.Check(
            "a slider of the Screen panel's Crop group that brings the last edge back to nothing leaves the group open, with its four sliders there and the keyboard focus still on the one that was moved: a group in use does not close under the hand that uses it",
            onLeft == "StudioScreenCropLeftSlider" && movedBack && uncropped && staysOpen == true && slidersStay == 4 && stillOnLeft == "StudioScreenCropLeftSlider" && croppedAgain,
            $"the slider was set: {movedBack}; nothing is cropped: {uncropped}; the group is expanded: {staysOpen}, with {slidersStay} sliders; the focus was on \"{onLeft}\" and is on \"{stillOnLeft}\"; after Undo the screen is cropped again: {croppedAgain}");

        // The header, pressed over a crop: the group closes and says that there is a crop inside it.
        var collapsed = CropGroupOf("StudioScreenCropGroup")?.Collapse() ?? false;
        var hidden = Until(() => There(screenIds), count => count == 0, 2);
        var saysCropped = Until(() => CropGroupOf("StudioScreenCropGroup")?.HelpText, text => text == "Cropped", 1.5);
        var word = editor.Root.FindRawAsItIs("StudioScreenCroppedText")?.Name;
        var heldClosed = OnUi(() => !editor.Window.ViewModel.IsScreenCropOpen);
        var expandedAgain = CropGroupOf("StudioScreenCropGroup")?.Expand() ?? false;
        var shownAgain = Until(() => There(screenIds), count => count == 4, 2);
        var saysNothing = Until(() => CropGroupOf("StudioScreenCropGroup")?.HelpText, text => text is { Length: 0 }, 1.5);
        _report.Check(
            "collapsed over a crop, the Screen panel's Crop group hides its sliders and says Cropped, as its description for a screen reader and as a word in its header; expanded again it shows them and says nothing more",
            collapsed && hidden == 0 && saysCropped == "Cropped" && word == "Cropped" && heldClosed && expandedAgain && shownAgain == 4 && saysNothing is { Length: 0 },
            $"collapsed: {collapsed}, {hidden} sliders, described as \"{saysCropped}\", the word in the header: \"{word}\", the editor holds it closed: {heldClosed}; expanded: {expandedAgain}, {shownAgain} sliders, described as \"{saysNothing}\"");

        // The camera's group, which is closed: its header opens it, and from then on it stays open without a crop.
        ShowPanel(editor, StudioInspectorPanel.Camera);
        Thread.Sleep(300);
        var opened = CropGroupOf("StudioCameraCropGroup")?.Expand() ?? false;
        var cameraShown = Until(() => There(cameraIds), count => count == 4, 2);
        var cameraNames = cameraIds.Select(id => editor.Root.FindAsItIs(id)?.Name).ToArray();
        var cameraReset = editor.Root.FindAsItIs("StudioCameraCropResetButton");
        var heldOpen = OnUi(() => editor.Window.ViewModel.IsCameraCropOpen);
        var closedAgain = CropGroupOf("StudioCameraCropGroup")?.Collapse() ?? false;
        var cameraHidden = Until(() => There(cameraIds), count => count == 0, 2);
        var plain = CropGroupOf("StudioCameraCropGroup")?.HelpText;
        _report.Check(
            "the Camera panel's Crop group, closed while the camera is not cropped, opens when it is expanded and shows four sliders that say the crop is the camera's, with Reset crop disabled; collapsed again without a crop it does not say Cropped",
            opened && cameraShown == 4 && cameraNames.SequenceEqual(["Camera crop left", "Camera crop top", "Camera crop right", "Camera crop bottom"]) && cameraReset is { IsEnabled: false, Name: "Reset crop" }
                && heldOpen && closedAgain && cameraHidden == 0 && plain is { Length: 0 },
            $"expanded: {opened}, {cameraShown} sliders: {string.Join(", ", cameraNames)}; {cameraReset} enabled {cameraReset?.IsEnabled}; the editor holds it open: {heldOpen}; collapsed: {closedAgain}, {cameraHidden} sliders, described as \"{plain}\"");
        CloseQuietly(editor);
    }
}
