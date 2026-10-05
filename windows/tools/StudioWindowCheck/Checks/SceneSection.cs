using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 12, continued. What the controls of the Scene section do, each through UI Automation: a third
// scene and how it is entered, how long a move takes, the start of a scene, stepping from scene
// to scene, where the keyboard focus goes when a button switches itself off, the order of the
// tab stops, and Undo and Redo. They all work on the window SplittingScenes opened, one after
// the other, each leaving the scenes as the next one expects them.
internal sealed partial class WindowChecks
{
    /// <summary>A scene lane as it should read for a list of scenes, with a star before the scene the playhead is in.</summary>
    private static string SceneLaneFor(StudioScene[] scenes, int current) => string.Join(" | ", scenes.Select((scene, index) =>
    {
        var end = index + 1 < scenes.Length ? scenes[index + 1].Start : RecordingLength;
        return string.Create(CultureInfo.InvariantCulture, $"{(index == current ? "*" : string.Empty)}Scene {index + 1} of {scenes.Length}, {StudioEditorModel.GetLayoutName(scene.Layout)}, {scene.Start:0.0} to {end:0.0} seconds");
    }));

    /// <summary>
    /// A third scene, split off with the button of the transport row, made camera only in the
    /// Layout section, and entered by a cut: the picture changes from one frame to the next.
    /// </summary>
    private void ThirdSceneEnteredByACut(Editor editor, UiaEvents heard)
    {
        Timeline.Mark("12: a third scene, by the Split button");
        SetSlider(editor, "StudioPlayhead", 8.0);
        Until(() => Find(editor, "StudioSplitSceneButton", 0.5)?.IsEnabled, enabled => enabled == true, 2);
        var mark = heard.Mark();
        var pressed = Invoke(editor, "StudioSplitSceneButton");
        Expect(editor, p => p with { Scenes = [p.Scenes[0], p.Scenes[1], p.Scenes[1] with { Start = 8.0, Transition = new StudioTransition { Kind = StudioTransitionKind.Morph } }] });
        var held = ScenesWhenAs(editor);
        var lane = WaitForLane(editor, SceneLaneWanted(editor, 2), 2, SceneLane);
        var said = Said(heard, mark, StudioEditorText.SceneSplitMessage);
        var head = Until(() => Playhead(editor), value => Same(value, 8.35), 2);
        _report.Check(
            "Split in the transport row does what S does: a third scene starts at the playhead, a copy of the second, the lane has three items with the third selected, the playhead goes on to where it has been entered, and a screen reader is told",
            pressed && held == Describe(editor.Expected.Scenes) && lane == SceneLaneWanted(editor, 2) && said.Said && Same(head, 8.35),
            $"the editor holds {held}; the lane: {lane}; sent: {said.Heard}; playhead {Seconds(head)} s");

        // Camera only, chosen in the Layout section, which shows and changes the scene the playhead is in.
        Timeline.Mark("12: the Layout section changes the current scene");
        var chosen = Find(editor, "StudioLayoutCamera")?.Select() ?? false;
        Expect(editor, p => WithSceneAt(p, 2, scene => scene with { Layout = StudioLayout.Camera }));
        held = ScenesWhenAs(editor);
        lane = WaitForLane(editor, SceneLaneWanted(editor, 2), 2, SceneLane);
        _report.Check(
            "Camera chosen in the Layout section changes the third scene, which the playhead is in, and neither of the other two",
            chosen && held == Describe(editor.Expected.Scenes) && lane.EndsWith("*Scene 3 of 3, Camera only, 8.0 to 12.0 seconds", StringComparison.Ordinal),
            $"the editor holds {held}; the lane: {lane}");

        // Inside the move into a scene that has no screen: the camera is on its way and the screen fades where it was.
        const int Inside = 244;
        SetSlider(editor, "StudioPlayhead", MiddleOf(Inside));
        var fading = ShowsLayout(editor, "inside the move into a scene without a screen, the camera is where the format puts it on its way to filling the content area", Inside);
        if (fading is not null)
        {
            var screen = fading.Reading.Resolved.Screen;
            _report.Note(string.Create(CultureInfo.InvariantCulture, $"at that frame the format has the screen at {screen?.Opacity ?? double.NaN:0.00} of its strength, where the second scene had it; a layer that fades is not read"));
        }

        // Entered by: a cut.
        Timeline.Mark("12: Entered by");
        var entry = Find(editor, "StudioSceneEntryChoice");
        var names = (Cut: NameOf(editor, "StudioSceneEntryCut"), Move: NameOf(editor, "StudioSceneEntryMove"));
        var cut = Find(editor, "StudioSceneEntryCut")?.Select() ?? false;
        Expect(editor, p => WithSceneAt(p, 2, scene => scene with { Transition = scene.Transition with { Kind = StudioTransitionKind.Cut } }));
        held = ScenesWhenAs(editor);
        var section = SceneSectionShows(editor, "Scene 3 of 3", "8.0 to 12.0 seconds", canGoBack: true, canGoOn: false, start: "8.0 seconds", isMoving: false, moveTakes: null, canDelete: true);
        _report.Check(
            "Entered by offers \"A cut\" and \"Moving\"; choosing \"A cut\" makes the scene one that is cut to and keeps how long its move took, and the slider for the move goes",
            entry is { Name: "Entered by" } && names == ("A cut", "Moving") && cut && held == Describe(editor.Expected.Scenes) && section is null,
            $"{entry}: \"{names.Cut}\", \"{names.Move}\"; the editor holds {held}; {section ?? "the section shows a scene that is cut to, without a Move takes slider"}");

        // On both sides of the line: the last frame of the second scene, and the first of the third.
        SetSlider(editor, "StudioPlayhead", MiddleOf(239));
        var last = ShowsLayout(editor, "the frame before a scene that is cut to still shows the scene before it: side by side", 239);
        SetSlider(editor, "StudioPlayhead", MiddleOf(240));
        var first = ShowsLayout(editor, "the first frame of a scene that is cut to shows that scene at once: the camera alone, filling the content area", 240);
        if (last is not null && first is not null)
        {
            // Where the second scene had the screen recording, nothing of it is left: its frame strip no longer reads there.
            var was = last.Reading.Resolved.Screen!.Value;
            var strip = StripAt(first.Shot, TestMedia.Screen, last.Reading.ScreenLayer!.Value, was.Source, mirror: false);
            var cameraGrew = first.Reading.CameraLayer!.Value.Width - last.Reading.CameraLayer!.Value.Width;
            _report.Check(
                "from the one frame to the next the screen recording is gone from where it was, and the camera is larger by more than a hundred pixels",
                strip == FrameCode.Unreadable && cameraGrew > 100 && first.Reading.Resolved.Screen is null,
                $"where the screen was, its frame strip reads {(strip == FrameCode.Unreadable ? "nothing" : strip.ToString(CultureInfo.InvariantCulture))}; the camera went from {R(last.Reading.CameraLayer.Value)} to {R(first.Reading.CameraLayer.Value)}");
        }

        // Back to moving and to a cut again: the choice goes both ways, and the time of the move was kept.
        var moving = Find(editor, "StudioSceneEntryMove")?.Select() ?? false;
        var back = SceneSectionShows(editor, "Scene 3 of 3", "8.0 to 12.0 seconds", canGoBack: true, canGoOn: false, start: "8.0 seconds", isMoving: true, moveTakes: 0.35, canDelete: true);
        var kind = ScenesOf(editor)[2].Transition;
        var again = Find(editor, "StudioSceneEntryCut")?.Select() ?? false;
        held = ScenesWhenAs(editor);
        _report.Check(
            "choosing \"Moving\" brings the slider back with the 0.35 seconds the scene had, and \"A cut\" takes it away again",
            moving && back is null && kind is { Kind: StudioTransitionKind.Morph, Duration: 0.35 } && again && held == Describe(editor.Expected.Scenes) && Absent(editor, "StudioSceneMoveSlider"),
            $"{back ?? "Moving, 0.35 seconds"}; the scene was {kind.Kind} {F(kind.Duration)}; the editor holds {held}");
    }

    /// <summary>How long the move into the second scene takes: the slider, the picture inside a longer move, and one drag as one undo step.</summary>
    private void MoveTakes(Editor editor)
    {
        Timeline.Mark("12: Move takes");
        SetSlider(editor, "StudioPlayhead", 6.0);
        WaitForLane(editor, SceneLaneWanted(editor, 1), 2, SceneLane);
        var shorter = editor.Expected;
        var set = SetSlider(editor, "StudioSceneMoveSlider", 1.0);
        Expect(editor, p => WithSceneAt(p, 1, scene => scene with { Transition = scene.Transition with { Duration = 1.0 } }));
        var held = ScenesWhenAs(editor);
        var slider = Slider(editor, "StudioSceneMoveSlider", 1.0, "1.00 seconds");
        _report.Check(
            "Move takes set to one second: the second scene's move lasts that long, and the slider says \"1.00 seconds\"",
            set && held == Describe(editor.Expected.Scenes) && slider is null,
            $"the editor holds {held}; {slider ?? "the slider is at 1, \"1.00 seconds\""}");

        // Frame 135 is 0.52 s after the scene's start: past the end of a move of 0.35 s, and half way through one of a second.
        const int Half = 135;
        SetSlider(editor, "StudioPlayhead", MiddleOf(Half));
        var sight = ShowsLayout(editor, "with a move of one second, the frame 0.52 s after the scene's start shows the layers a little more than half way", Half);
        if (sight is not null)
        {
            var atRest = ReadLayout(sight.Shot, sight.Canvas, editor.Folder, shorter, Half);
            var apart = sight.Reading.ScreenLayer is { } moving && atRest.ScreenLayer is { } rested ? EdgeDistance(moving, rested) : double.NaN;
            _report.Check(
                "that picture is not the one a move of 0.35 seconds gives, which would be at rest by then: read against it, the picture is wrong",
                apart > 20 && JudgeLayout(atRest) is not null,
                string.Create(CultureInfo.InvariantCulture, $"with a move of 0.35 s the screen would be in {R(atRest.ScreenLayer ?? default)}, {apart:0.0} px from where it is; read against that: {JudgeLayout(atRest) ?? "it passes"}"));
        }

        // One drag of the slider is one undo step: three steps inside a gesture, as the slider's row makes of a drag.
        OnUi(editor.Window.ViewModel.BeginGesture);
        var dragged = SetSlider(editor, "StudioSceneMoveSlider", 0.5) & SetSlider(editor, "StudioSceneMoveSlider", 0.8) & SetSlider(editor, "StudioSceneMoveSlider", 0.6);
        OnUi(editor.Window.ViewModel.EndGesture);
        var afterDrag = SliderValue(editor, "StudioSceneMoveSlider");
        Invoke(editor, "StudioUndoButton");
        var afterUndo = Until(() => SliderValue(editor, "StudioSceneMoveSlider"), value => Same(value, 1.0), 2);
        held = ScenesWhenAs(editor);
        _report.Check(
            "Move takes moved in three steps inside one gesture is one undo step",
            dragged && Same(afterDrag, 0.6) && Same(afterUndo, 1.0) && held == Describe(editor.Expected.Scenes),
            $"{F(afterDrag)} after the three steps, {F(afterUndo)} after one Undo; the editor holds {held}");

        // The scene is four seconds long, so its move is as long as asked, and the section has nothing to add.
        var limited = editor.Root.Find("StudioSceneMoveLimitedNote")?.Name;
        var reset = SetSlider(editor, "StudioSceneMoveSlider", 0.35);
        Expect(editor, p => WithSceneAt(p, 1, scene => scene with { Transition = scene.Transition with { Duration = 0.35 } }));
        held = ScenesWhenAs(editor);
        _report.Check(
            "set back to 0.35 seconds the slider says \"0.35 seconds\"; in a scene longer than its move the section says nothing about the move being shorter",
            reset && held == Describe(editor.Expected.Scenes) && Slider(editor, "StudioSceneMoveSlider", 0.35, "0.35 seconds") is null && limited is null,
            $"the editor holds {held}; the note about a shorter move: {(limited is null ? "none" : $"\"{limited}\"")}");
    }

    /// <summary>The Start row of the Scene section: 0.1 s earlier and later, at the playhead, what is said, and where the start stops.</summary>
    private void SceneStartButtons(Editor editor, UiaEvents heard)
    {
        Timeline.Mark("12: the start of a scene");
        SetSlider(editor, "StudioPlayhead", 6.0);
        WaitForLane(editor, SceneLaneWanted(editor, 1), 2, SceneLane);
        var buttons = new[] { "StudioSceneStartEarlierButton", "StudioSceneStartLaterButton", "StudioSceneStartAtPlayheadButton" }.Select(id => NameOf(editor, id)).ToArray();
        var steps = new List<string>();
        var wrong = new List<string>();
        void Step(string what, string button, double wantedStart, double? wantedHead)
        {
            var headBefore = Playhead(editor);
            var mark = heard.Mark();
            var pressed = Invoke(editor, button);
            var start = Until(() => ScenesOf(editor)[1].Start, value => Same(value, wantedStart), 1);
            var headWanted = wantedHead ?? headBefore;
            var head = Until(() => Playhead(editor), value => Same(value, headWanted), 1);
            var sentence = $"Scene start {StudioEditorModel.GetSecondsText(wantedStart)}";
            var said = Said(heard, mark, sentence);
            var shown = Until(() => NameOf(editor, "StudioSceneStartText", 0.5), text => text == sentence, 1);
            steps.Add($"{what}: the scene starts at {Seconds(start)} s, playhead {Seconds(head)} s");
            if (!pressed || !Same(start, wantedStart) || !Same(head, headWanted) || !said.Said || shown != sentence || CurrentSceneOf(editor) != 1)
            {
                wrong.Add($"{what}: pressed {pressed}, the scene starts at {Seconds(start)} s (wanted {Seconds(wantedStart)}), playhead {Seconds(head)} s (wanted {Seconds(headWanted)}), the section reads \"{shown}\", sent: {said.Heard}, the playhead is in scene {CurrentSceneOf(editor) + 1}");
            }
        }

        // The second scene starts at 4.0 s. Its start may be from 0.3 s, which is 0.3 s after the
        // first scene's, to 7.7 s, which is 0.3 s before the third's.
        Step("0.1 s later", "StudioSceneStartLaterButton", 4.1, 4.1);
        Step("0.1 s earlier", "StudioSceneStartEarlierButton", 4.0, 4.0);
        Step("0.1 s earlier again", "StudioSceneStartEarlierButton", 3.9, 3.9);
        SetSlider(editor, "StudioPlayhead", 5.0);
        Step("at the playhead, at 5.0 s", "StudioSceneStartAtPlayheadButton", 5.0, null);
        SetSlider(editor, "StudioPlayhead", 7.9);
        Step("at the playhead, at 7.9 s, less than 0.3 s before the next scene", "StudioSceneStartAtPlayheadButton", 7.7, null);
        var lane = LaneText(editor, SceneLane);
        var limited = Until(() => editor.Root.Find("StudioSceneMoveLimitedNote")?.Name ?? string.Empty, note => note.Length > 0, 1);
        Step("0.1 s later, against the next scene", "StudioSceneStartLaterButton", 7.7, 7.7);
        _report.Check(
            "the three Start buttons are named for what they do, move the scene's start by 0.1 s or to the playhead, and say the start they leave it at; the playhead follows a step and stays for At playhead; the start stops 0.3 s before the next scene, where a further step changes nothing and still says where it is",
            wrong.Count == 0 && buttons.SequenceEqual(["Scene start 0.1 seconds earlier", "Scene start 0.1 seconds later", "Start scene at playhead"])
                && lane == "Scene 1 of 3, Screen with camera bubble, 0.0 to 7.7 seconds | *Scene 2 of 3, Side by side, 7.7 to 8.0 seconds | Scene 3 of 3, Camera only, 8.0 to 12.0 seconds",
            wrong.Count == 0 ? $"{string.Join("; ", steps)}; the buttons: {string.Join(", ", buttons.Select(name => $"\"{name}\""))}; the lane: {lane}" : string.Join(" | ", wrong));
        _report.Check(
            "in a scene of 0.3 seconds that is to be entered by a move of 0.35, the section says how long the move really is",
            limited == "The scene is shorter than that, so the move takes 0.30 seconds.",
            $"\"{limited}\"");

        // Five of the six presses changed the start. Five Undo bring it back to 4.0 s.
        for (var undo = 0; undo < 5; undo++)
        {
            var before = ScenesOf(editor)[1].Start;
            Invoke(editor, "StudioUndoButton");
            Until(() => ScenesOf(editor)[1].Start, value => value != before, 1);
        }

        var held = ScenesWhenAs(editor);
        _report.Check(
            "each press that moved the start is one undo step, and the one that changed nothing is none: five Undo bring the second scene back to 4.0 s, and the note about its move goes",
            held == Describe(editor.Expected.Scenes) && Absent(editor, "StudioSceneMoveLimitedNote"),
            $"the editor holds {held}");
    }

    /// <summary>Previous scene and Next scene: where the playhead goes, what is read out, and where the keyboard focus goes at the ends.</summary>
    private void PreviousAndNextScene(Editor editor, UiaEvents heard)
    {
        Timeline.Mark("12: Previous scene and Next scene");
        SetSlider(editor, "StudioPlayhead", 6.0);
        WaitForLane(editor, SceneLaneWanted(editor, 1), 2, SceneLane);
        var steps = new List<string>();
        var wrong = new List<string>();
        void Step(string button, int wanted, double wantedHead, bool canGoBack, bool canGoOn)
        {
            var mark = heard.Mark();
            var pressed = Invoke(editor, button);
            var current = Until(() => CurrentSceneOf(editor), index => index == wanted, 1);
            var head = Until(() => Playhead(editor), value => Same(value, wantedHead), 1);
            var said = Said(heard, mark, SceneName(editor, wanted));
            var position = Until(() => NameOf(editor, "StudioScenePositionText", 0.5), text => text == $"Scene {wanted + 1} of 3", 1);
            var previous = Until(() => Find(editor, "StudioPreviousSceneButton", 0.5)?.IsEnabled, enabled => enabled == canGoBack, 1);
            var next = Until(() => Find(editor, "StudioNextSceneButton", 0.5)?.IsEnabled, enabled => enabled == canGoOn, 1);
            steps.Add($"\"{position}\" at {Seconds(head)} s");
            if (!pressed || current != wanted || !Same(head, wantedHead) || !said.Said || position != $"Scene {wanted + 1} of 3" || previous != canGoBack || next != canGoOn)
            {
                wrong.Add($"{button}: pressed {pressed}, scene {current + 1} at {Seconds(head)} s (wanted scene {wanted + 1} at {Seconds(wantedHead)}), \"{position}\", Previous enabled {previous}, Next enabled {next}, sent: {said.Heard}");
            }
        }

        // Each scene is shown where it has been entered: the first at its start, the second at
        // the end of its move, 0.35 s after its start, and the third, which is cut to, at its start.
        var focus = new List<string> { FocusOn(editor, "StudioNextSceneButton") };
        Step("StudioNextSceneButton", 2, 8.0, canGoBack: true, canGoOn: false);
        focus.Add(Until(() => FocusedId(editor), id => id == "StudioPreviousSceneButton", 1.5));
        Step("StudioPreviousSceneButton", 1, 4.35, canGoBack: true, canGoOn: true);
        focus.Add(FocusedId(editor));
        Step("StudioPreviousSceneButton", 0, 0, canGoBack: false, canGoOn: true);
        focus.Add(Until(() => FocusedId(editor), id => id == "StudioNextSceneButton", 1.5));
        Step("StudioNextSceneButton", 1, 4.35, canGoBack: true, canGoOn: true);
        focus.Add(FocusedId(editor));
        _report.Check(
            "Previous scene and Next scene move the playhead to where the scene before and after has been entered, the text between them says which scene that is, each is disabled where there is no scene to go to, and a screen reader is told the scene it lands on as the lane names it",
            wrong.Count == 0,
            wrong.Count == 0 ? string.Join(", then ", steps) : string.Join(" | ", wrong));
        string[] focusWanted = ["StudioNextSceneButton", "StudioPreviousSceneButton", "StudioPreviousSceneButton", "StudioNextSceneButton", "StudioNextSceneButton"];
        _report.Check(
            "the keyboard focus stays on the button that was pressed while it has a scene to go to, and goes to the other one when the last or the first scene is reached and the pressed button is switched off",
            focus.SequenceEqual(focusWanted),
            $"the focus was on: {string.Join(", ", focus.Select(id => id.Replace("Studio", string.Empty, StringComparison.Ordinal)))}");
    }

    /// <summary>
    /// Split, pressed so close to the end of the recording that the new scene cannot be split
    /// again: the button switches itself off, and the keyboard focus must not be left to chance.
    /// </summary>
    private void SplitSwitchedOffByItself(Editor editor)
    {
        Timeline.Mark("12: Split, where it switches itself off");
        var before = Describe(ScenesOf(editor));

        // The section's button. The new scene starts at 11.5 s and the playhead goes on to 11.85 s, 0.15 s before the end.
        SetSlider(editor, "StudioPlayhead", 11.5);
        Until(() => Find(editor, "StudioSceneSectionSplitButton", 0.5)?.IsEnabled, enabled => enabled == true, 2);
        var onSection = FocusOn(editor, "StudioSceneSectionSplitButton");
        var pressedSection = Invoke(editor, "StudioSceneSectionSplitButton");
        var four = Until(() => ScenesOf(editor).Length, count => count == 4, 1.5);
        var sectionOff = Until(() => Find(editor, "StudioSceneSectionSplitButton", 0.5)?.IsEnabled, enabled => enabled == false, 1.5);
        var afterSection = Until(() => FocusedId(editor), id => id == "StudioPreviousSceneButton", 1.5);
        var note = editor.Root.Find("StudioSplitSceneNote")?.Name ?? string.Empty;
        var headAfter = Playhead(editor);
        Invoke(editor, "StudioUndoButton");
        Until(() => ScenesOf(editor).Length, count => count == 3, 1.5);

        // The transport row's button, the same way.
        SetSlider(editor, "StudioPlayhead", 11.5);
        Until(() => Find(editor, "StudioSplitSceneButton", 0.5)?.IsEnabled, enabled => enabled == true, 2);
        var onRow = FocusOn(editor, "StudioSplitSceneButton");
        var pressedRow = Invoke(editor, "StudioSplitSceneButton");
        var fourAgain = Until(() => ScenesOf(editor).Length, count => count == 4, 1.5);
        var rowOff = Until(() => Find(editor, "StudioSplitSceneButton", 0.5)?.IsEnabled, enabled => enabled == false, 1.5);

        // To the first of Add zoom, Cut and Play that can take it.
        var firstEnabled = new[] { "StudioAddZoomButton", "StudioAddCutButton", "StudioPlayPauseButton" }.First(id => Find(editor, id, 0.5)?.IsEnabled == true);
        var afterRow = Until(() => FocusedId(editor), id => id == firstEnabled, 1.5);
        Invoke(editor, "StudioUndoButton");
        Until(() => ScenesOf(editor).Length, count => count == 3, 1.5);
        var restored = Describe(ScenesOf(editor));
        _report.Check(
            "Split pressed half a second before the end of the recording makes a scene that cannot be split again, so the button is switched off by what it did: the section's button then hands the keyboard focus to Previous scene and says why it is off, and the transport row's hands it to the next button of the row that works",
            onSection == "StudioSceneSectionSplitButton" && pressedSection && four == 4 && sectionOff == false && afterSection == "StudioPreviousSceneButton" && note == StudioEditorText.SceneTooShortToSplitExplanation && Same(headAfter, 11.85)
                && onRow == "StudioSplitSceneButton" && pressedRow && fourAgain == 4 && rowOff == false && afterRow == firstEnabled && restored == before && restored == Describe(editor.Expected.Scenes),
            $"the section's button: {four} scenes, playhead {Seconds(headAfter)} s, enabled {sectionOff}, the focus went from \"{onSection}\" to \"{afterSection}\", the note: \"{note}\"; "
                + $"the row's button: {fourAgain} scenes, enabled {rowOff}, the focus went from \"{onRow}\" to \"{afterRow}\" (the first that works: {firstEnabled}); after the two Undo the editor holds {restored}");

        // Where no zoom and no cut fits either. Left to itself the focus goes on from a button
        // that is switched off to the next stop that works, which above is the same button the
        // row picks, so that check cannot tell the row's choice from none. Here it can: a cut of
        // the last eighth of a second leaves 0.025 s between itself and 11.85 s, where the
        // playhead goes on to, and the 0.15 s to the end are too short for a zoom. The next stop
        // that works is then Start here, a button that trims, and the row goes back to Play instead.
        SetSlider(editor, "StudioPlayhead", 11.875);
        var cutPressed = Invoke(editor, "StudioAddCutButton");
        var cuts = Until(() => CutsOf(editor), now => now.Length == 1, 1.5);
        SetSlider(editor, "StudioPlayhead", 11.5);
        Until(() => Find(editor, "StudioSplitSceneButton", 0.5)?.IsEnabled, enabled => enabled == true, 2);
        var onRowAgain = FocusOn(editor, "StudioSplitSceneButton");
        var pressedAgain = Invoke(editor, "StudioSplitSceneButton");
        var fourOnceMore = Until(() => ScenesOf(editor).Length, count => count == 4, 1.5);
        string[] three = ["StudioSplitSceneButton", "StudioAddZoomButton", "StudioAddCutButton"];
        var stillOn = Until(() => three.Where(id => Find(editor, id, 0.5)?.IsEnabled != false).ToArray(), on => on.Length == 0, 1.5);
        var startHere = Find(editor, "StudioStartHereButton", 0.5)?.IsEnabled;
        var afterAll = Until(() => FocusedId(editor), id => id == "StudioPlayPauseButton", 1.5);
        var headThen = Playhead(editor);
        Invoke(editor, "StudioUndoButton");
        Until(() => ScenesOf(editor).Length, count => count == 3, 1.5);
        Invoke(editor, "StudioUndoButton");
        var cutsLeft = Until(() => CutsOf(editor).Length, count => count == 0, 1.5);
        var restoredAgain = Describe(ScenesOf(editor));
        _report.Check(
            "where no zoom and no cut fits either, so that Split, Add zoom and Cut are all three switched off by the press, the transport row hands the keyboard focus back to Play, and not on to Start here, the next stop that works",
            cutPressed && cuts.Length == 1 && Same(cuts[0].Start, 11.875) && Same(cuts[0].End, RecordingLength) && onRowAgain == "StudioSplitSceneButton" && pressedAgain && fourOnceMore == 4 && Same(headThen, 11.85)
                && stillOn.Length == 0 && startHere == true && afterAll == "StudioPlayPauseButton" && cutsLeft == 0 && restoredAgain == before,
            $"with a cut of {string.Join(", ", cuts.Select(cut => $"{Seconds(cut.Start)} to {Seconds(cut.End)} s"))}: {fourOnceMore} scenes, playhead {Seconds(headThen)} s, still enabled: {(stillOn.Length == 0 ? "none of the three" : string.Join(", ", stillOn))}, Start here enabled {startHere}; "
                + $"the focus went from \"{onRowAgain}\" to \"{afterAll}\"; after the two Undo the editor holds {cutsLeft} cuts and {restoredAgain}");
    }

    /// <summary>
    /// The stops of the Tab key in order, read by asking the window to move the keyboard focus
    /// to the next stop, over and over, and noting where it lands. No key is pressed, and the
    /// window does not have the keyboard: the request for it is refused like every other.
    /// </summary>
    /// <remarks>
    /// A group of radio buttons is one stop, and is listed by the group's own id. Which of its
    /// buttons the Tab key lands on is the group's doing, which sends a focus that comes by the
    /// keyboard on to the chosen button. A focus that is moved from here is not from the
    /// keyboard and stays on the group's first button, so that is not seen.
    /// </remarks>
    private List<string> TabStops(Editor editor) => OnUi(() =>
    {
        var stops = new List<string>();
        var seen = new List<DependencyObject>();
        if (editor.Window.Content is not FrameworkElement { XamlRoot: { } root } content)
        {
            return stops;
        }

        var before = FocusManager.GetFocusedElement(root) as UIElement;
        var options = new FindNextElementOptions { SearchRoot = content };
        (FocusManager.FindFirstFocusableElement(content) as UIElement)?.Focus(FocusState.Keyboard);
        for (var step = 0; step < 200; step++)
        {
            // Round once: the first stop comes again after the last.
            if (FocusManager.GetFocusedElement(root) is not DependencyObject focused || seen.Exists(element => ReferenceEquals(element, focused)))
            {
                break;
            }

            seen.Add(focused);
            var named = focused;
            if (focused is RadioButton)
            {
                for (var parent = VisualTreeHelper.GetParent(focused); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                {
                    if (parent is RadioButtons group)
                    {
                        named = group;
                        break;
                    }
                }
            }

            var id = AutomationProperties.GetAutomationId(named);
            stops.Add(id.Length > 0 ? id : (focused as FrameworkElement)?.Name is { Length: > 0 } own ? own : focused.GetType().Name);
            if (!FocusManager.TryMoveFocus(FocusNavigationDirection.Next, options))
            {
                break;
            }
        }

        before?.Focus(FocusState.Programmatic);
        return stops;
    });

    /// <summary>Null when each of the given elements is in a tree, under the name given for it. Otherwise which are not.</summary>
    private static string? Named(List<(int Depth, UiaElement Element)> tree, (string Id, string Name)[] wanted)
    {
        var names = tree.Where(entry => entry.Element.Id.Length > 0).GroupBy(entry => entry.Element.Id).ToDictionary(group => group.Key, group => group.First().Element.Name);
        var wrong = wanted.Where(item => names.GetValueOrDefault(item.Id) != item.Name)
            .Select(item => $"{item.Id} is {(names.TryGetValue(item.Id, out var name) ? $"called \"{name}\"" : "not there")} and should be called \"{item.Name}\"").ToArray();
        return wrong.Length == 0 ? null : string.Join("; ", wrong);
    }

    /// <summary>
    /// What a screen reader is given of a window with three scenes, with the playhead in the
    /// second, which is side by side and entered by moving; and the order of the tab stops.
    /// </summary>
    private void TimelineTabOrder(Editor editor)
    {
        Timeline.Mark("12: the names and the order of the tab stops, with scenes");
        SetSlider(editor, "StudioPlayhead", 6.0);
        WaitForLane(editor, SceneLaneWanted(editor, 1), 2, SceneLane);

        // The seventeen sliders of the side-by-side layout, and Move takes.
        (string Id, string Name)[] names =
        [
            ("StudioSplitSceneButton", "Split scene"), (SceneLane, "Scenes"), ("StudioScene_0", SceneName(editor, 0)), ("StudioScene_1", SceneName(editor, 1)), ("StudioScene_2", SceneName(editor, 2)),
            ("StudioPreviousSceneButton", "Previous scene"), ("StudioScenePositionText", "Scene 2 of 3"), ("StudioSceneRangeText", "4.0 to 8.0 seconds"), ("StudioNextSceneButton", "Next scene"),
            ("StudioSceneSectionSplitButton", "Split at playhead"), ("StudioSceneStartText", "Scene start 4.0 seconds"),
            ("StudioSceneStartEarlierButton", "Scene start 0.1 seconds earlier"), ("StudioSceneStartLaterButton", "Scene start 0.1 seconds later"), ("StudioSceneStartAtPlayheadButton", "Start scene at playhead"),
            ("StudioSceneEntryChoice", "Entered by"), ("StudioSceneEntryCut", "A cut"), ("StudioSceneEntryMove", "Moving"), ("StudioSceneMoveSlider", "Move takes"), ("StudioDeleteSceneButton", "Delete scene"),
        ];
        AuditState(editor, "the editor with three scenes, in one that is entered by moving", "tree-scenes.txt", 18, tree => Named(tree, names));
        var delete = Find(editor, "StudioDeleteSceneButton");
        _report.Check(
            "Delete scene tells a screen reader what becomes of the scene's time",
            delete?.HelpText == "The scene before it then lasts until the next one.",
            $"{delete}, described as \"{delete?.HelpText}\"");
        var order = TabStops(editor);
        var path = Path.Combine(_output, "tab-order-scenes.txt");
        File.WriteAllLines(path, order);

        // The Scene section from top to bottom, then Layout; later the two buttons that add a
        // zoom and a cut in their sections; and the timeline: its row from left to right, then
        // the three lanes from top to bottom, each one stop, and the trim bar.
        string[] wanted =
        [
            "StudioPreviousSceneButton", "StudioNextSceneButton", "StudioSceneSectionSplitButton",
            "StudioSceneStartEarlierButton", "StudioSceneStartLaterButton", "StudioSceneStartAtPlayheadButton", "StudioSceneEntryChoice", "StudioSceneMoveSlider", "StudioDeleteSceneButton",
            "StudioLayoutChoice", "StudioShowBackgroundCheckBox", "StudioZoomSectionAddButton", "StudioCutSectionAddButton", "StudioClickRingsCheckBox",
            "StudioPlayPauseButton", "StudioPreviousFrameButton", "StudioNextFrameButton", "StudioSplitSceneButton", "StudioAddZoomButton", "StudioAddCutButton", "StudioStartHereButton", "StudioEndHereButton",
            SceneLane, ZoomLane, CutLane, "StudioTrimStart", "StudioTrimEnd", "StudioPlayhead",
        ];
        var places = wanted.Select(id => order.IndexOf(id)).ToArray();
        var missing = wanted.Where((_, index) => places[index] < 0).ToArray();
        var outOfOrder = places.Where(place => place >= 0).ToArray() is var found && !found.SequenceEqual(found.Order());

        // The timeline's stops come one right after the other, with nothing between them.
        var row = order.IndexOf("StudioPlayPauseButton");
        var together = row >= 0 && order.Skip(row).Take(14).SequenceEqual(wanted[^14..]);
        var blocks = order.Where(id => id.StartsWith("StudioScene_", StringComparison.Ordinal) || id.StartsWith("StudioCut_", StringComparison.Ordinal) || id.StartsWith("StudioZoom_", StringComparison.Ordinal)).ToArray();
        _report.Check(
            "the keyboard focus, moved from stop to stop, goes through the Scene section from top to bottom and on to Layout, and through the timeline as the transport row from Play to End here, then the scene lane, the zoom lane, the cut lane and the trim bar, with nothing between them; each lane is one stop, and no single scene, zoom or cut is one",
            missing.Length == 0 && !outOfOrder && together && blocks.Length == 0,
            missing.Length == 0 && !outOfOrder && together && blocks.Length == 0
                ? $"{order.Count} stops, saved as {Path.GetFileName(path)}: {string.Join(", ", order.Select(id => id.Replace("Studio", string.Empty, StringComparison.Ordinal)))}"
                : $"{(missing.Length == 0 ? string.Empty : "not reached: " + string.Join(", ", missing) + "; ")}{(outOfOrder ? "out of order; " : string.Empty)}{(together ? string.Empty : "the timeline's stops are not one after the other; ")}{(blocks.Length == 0 ? string.Empty : "also stops: " + string.Join(", ", blocks) + "; ")}the order: {string.Join(", ", order)}");
    }

    /// <summary>Undo and Redo across a split, a layout, a start, a way of entering and a delete: the lane and the section follow.</summary>
    private void UndoAndRedoOfScenes(Editor editor)
    {
        Timeline.Mark("12: undo and redo of scenes");
        var wrong = new List<string>();
        var steps = new List<string>();
        var three = editor.Expected.Scenes;
        void Expect(string what, StudioScene[] scenes, int current)
        {
            var lane = SceneLaneFor(scenes, current);
            var text = WaitForLane(editor, lane, 2, SceneLane);
            var held = Until(() => Describe(ScenesOf(editor)), now => now == Describe(scenes), 1);
            steps.Add($"{what}: {text}");
            if (text != lane || held != Describe(scenes))
            {
                wrong.Add($"{what}: the lane is {text} and should be {lane}; the editor holds {held} and should hold {Describe(scenes)}");
            }
        }

        // Split at 10.0 s: a fourth scene, a copy of the third that is entered by moving. The playhead goes on to 10.35 s.
        SetSlider(editor, "StudioPlayhead", 10.0);
        Key(editor, StudioShortcutKey.S);
        var split = three[2] with { Start = 10.0, Transition = new StudioTransition { Kind = StudioTransitionKind.Morph } };
        Expect("split", [.. three, split], 3);

        // Screen only, by its key.
        Key(editor, StudioShortcutKey.Digit1);
        var plain = split with { Layout = StudioLayout.Screen };
        Expect("layout", [.. three, plain], 3);

        // Its start, 0.1 s earlier. The playhead follows to 9.9 s.
        Invoke(editor, "StudioSceneStartEarlierButton");
        var earlier = plain with { Start = 9.9 };
        Expect("start", [.. three, earlier], 3);

        // Entered by a cut.
        Find(editor, "StudioSceneEntryCut")?.Select();
        var cutTo = earlier with { Transition = earlier.Transition with { Kind = StudioTransitionKind.Cut } };
        Expect("entered by a cut", [.. three, cutTo], 3);

        // Deleted: the third scene lasts to the end again, and the playhead is in it.
        Invoke(editor, "StudioDeleteSceneButton");
        Expect("delete", three, 2);

        // Back, step by step. The playhead stays at 9.9 s, so it is in the fourth scene while that starts at 9.9 s, and in the third once it starts at 10.0 s again.
        Invoke(editor, "StudioUndoButton");
        Expect("Undo of the delete", [.. three, cutTo], 3);
        var afterUndo = SceneSectionShows(editor, "Scene 4 of 4", "9.9 to 12.0 seconds", canGoBack: true, canGoOn: false, start: "9.9 seconds", isMoving: false, moveTakes: null, canDelete: true);
        Invoke(editor, "StudioUndoButton");
        Expect("Undo of the cut", [.. three, earlier], 3);
        var moving = SceneSectionShows(editor, "Scene 4 of 4", "9.9 to 12.0 seconds", canGoBack: true, canGoOn: false, start: "9.9 seconds", isMoving: true, moveTakes: 0.35, canDelete: true);
        Key(editor, StudioShortcutKey.Z, control: true);
        Expect("what Ctrl+Z runs: the start back at 10.0 s, which leaves the playhead in the third scene", [.. three, plain], 2);
        var inThird = SceneSectionShows(editor, "Scene 3 of 4", "8.0 to 10.0 seconds", canGoBack: true, canGoOn: true, start: "8.0 seconds", isMoving: false, moveTakes: null, canDelete: true);
        Invoke(editor, "StudioUndoButton");
        Expect("Undo of the layout", [.. three, split], 2);
        Invoke(editor, "StudioUndoButton");
        Expect("Undo of the split", three, 2);

        // Forward again.
        Invoke(editor, "StudioRedoButton");
        Expect("Redo of the split", [.. three, split], 2);
        Invoke(editor, "StudioRedoButton");
        Expect("Redo of the layout", [.. three, plain], 2);
        Key(editor, StudioShortcutKey.Y, control: true);
        Expect("what Ctrl+Y runs: the start at 9.9 s again, which puts the playhead in the fourth scene", [.. three, earlier], 3);
        Invoke(editor, "StudioRedoButton");
        Expect("Redo of the cut", [.. three, cutTo], 3);
        Invoke(editor, "StudioRedoButton");
        Expect("Redo of the delete", three, 2);
        _report.Check(
            "Undo and Redo across a split, a layout, a start, a way of entering and a delete: the lane shows the scenes of each step and marks the scene the playhead is in, which changes when a scene's start moves past the playhead; S still splits while Ctrl+Z undoes",
            wrong.Count == 0,
            wrong.Count == 0 ? string.Join("; ", steps) : string.Join(" | ", wrong));
        _report.Check(
            "the Scene section follows each of those steps: after the delete is undone it shows the fourth scene cut to, after the cut is undone it shows it moving for 0.35 seconds, and once the start is back it shows the third scene",
            afterUndo is null && moving is null && inThird is null,
            $"{afterUndo ?? "the fourth scene, cut to"}; {moving ?? "the fourth scene, moving"}; {inThird ?? "the third scene"}");
    }
}
