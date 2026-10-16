using System.Globalization;
using TinyClips.App.Views.Studio;
using TinyClips.Core.Models;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Host;
using Windows.System;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 12. Scenes: the lane above the zoom lane, Split, the keys S and Delete, the Scene panel of
// the inspector, and what the preview shows for a scene. What the Scene panel's controls do is
// in SceneSection.cs, the lane under a pointer and Delete in SceneLane.cs, a scene that is come
// into while the preview plays in ScenePlaying.cs, and something dragged while it plays, with the
// keys that wait for a drag to end, in SceneDragging.cs.
internal sealed partial class WindowChecks
{
    private const string SplitHint = "Start a new scene at the playhead (S)";

    private void Scenes()
    {
        Timeline.Mark("12: scenes");
        _layoutErrors.Clear();
        _report.Check(
            "the window maps the S key and the X key to the editor's keyboard model",
            StudioWindow.MapKey(VirtualKey.S) == StudioShortcutKey.S && StudioWindow.MapKey(VirtualKey.X) == StudioShortcutKey.X,
            $"S is {StudioWindow.MapKey(VirtualKey.S)}, X is {StudioWindow.MapKey(VirtualKey.X)}");

        ScenesWithoutACamera();
        SplittingScenes();
        SceneLaneUnderAPointer();
        SceneBlockLooks(AppTheme.Light, "light");
        SceneBlockLooks(AppTheme.Dark, "dark");
        DeletingScenes();
        SceneComesWhilePlaying();
        BubbleHandleFollowsTheScene();
        DragsWhileThePreviewPlays();
        if (_layoutErrors.Count > 0)
        {
            _report.Note($"over the {_layoutErrors.Count} pictures of a paused preview that were read for a scene's layout, the edge furthest from where the format puts it was {F(_layoutErrors.Max(), "0.00")} px from there, and on average the furthest edge of a picture {F(_layoutErrors.Average(), "0.00")} px");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Projects, and what the editor holds
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A recording with a camera for the pictures of scenes: a plain canvas without shadows, so
    /// that nothing but the two layers is on it, and a camera that takes two fifths of a
    /// side-by-side layout, which is wide enough to show its frame strip.
    /// </summary>
    private static StudioProject ForScenePictures(StudioProject project) =>
        WithScene(OnLemon(project), scene => scene with { Split = new StudioSplit { CameraFraction = 0.4 } });

    private static StudioProject WithSceneAt(StudioProject project, int index, Func<StudioScene, StudioScene> edit) =>
        project with { Scenes = [.. project.Scenes.Select((scene, at) => at == index ? edit(scene) : scene)] };

    /// <summary>The scenes the editor holds now. Read on the UI thread.</summary>
    private StudioScene[] ScenesOf(Editor editor) => OnUi(() => editor.Window.ViewModel.Scenes.ToArray());

    /// <summary>The scene the editor says the playhead is in. Read on the UI thread.</summary>
    private int CurrentSceneOf(Editor editor) => OnUi(() => editor.Window.ViewModel.CurrentSceneIndex);

    /// <summary>A scene with every value it stores, its start to a millionth of a second.</summary>
    private static string Describe(StudioScene scene) =>
        $"[{F(scene.Start, "0.######")} {scene.Layout}, bubble {scene.Bubble.Anchor} {F(scene.Bubble.Size)} {F(scene.Bubble.OffsetX, "0.######")} {F(scene.Bubble.OffsetY, "0.######")}, "
        + $"split {scene.Split.CameraSide} {F(scene.Split.CameraFraction)}, {scene.Transition.Kind} {F(scene.Transition.Duration, "0.######")}]";

    private static string Describe(IEnumerable<StudioScene> scenes) => string.Join(" ", scenes.Select(Describe));

    /// <summary>The editor's scenes once they are the ones the checks expect, or as they are when the time is up.</summary>
    private string ScenesWhenAs(Editor editor, double seconds = 1.5)
    {
        var wanted = Describe(editor.Expected.Scenes);
        return Until(() => Describe(ScenesOf(editor)), now => now == wanted, seconds);
    }

    /// <summary>A scene as the lane should name it, from the project the checks expect.</summary>
    private static string SceneName(Editor editor, int index)
    {
        var scenes = editor.Expected.Scenes;
        var end = index + 1 < scenes.Length ? scenes[index + 1].Start : RecordingLength;
        return string.Create(CultureInfo.InvariantCulture, $"Scene {index + 1} of {scenes.Length}, {StudioEditorModel.GetLayoutName(scenes[index].Layout)}, {scenes[index].Start:0.0} to {end:0.0} seconds");
    }

    /// <summary>The scene lane as it should read, from the project the checks expect, with a star before the scene the playhead is in.</summary>
    private static string SceneLaneWanted(Editor editor, int current) =>
        string.Join(" | ", Enumerable.Range(0, editor.Expected.Scenes.Length).Select(index => (index == current ? "*" : string.Empty) + SceneName(editor, index)));

    /// <summary>Whether an element is gone from what a screen reader is given, waited for.</summary>
    private static bool Absent(Editor editor, string automationId, double seconds = 1) =>
        Until(() => editor.Root.Find(automationId) is null, gone => gone, seconds);

    /// <summary>
    /// What is wrong with what the Scene panel shows, read through UI Automation, or null.
    /// </summary>
    /// <param name="start">The scene's start as the panel words it, or null for the first scene, which has none to show.</param>
    /// <param name="isMoving">Whether "Animated" is chosen as the transition, or null for the first scene.</param>
    /// <param name="moveTakes">How long the transition takes, or null where there is no slider for it.</param>
    private static string? SceneSectionShows(Editor editor, string position, string range, bool canGoBack, bool canGoOn, string? start, bool? isMoving, double? moveTakes, bool canDelete)
    {
        var positionRead = Until(() => NameOf(editor, "StudioScenePositionText", 0.5), text => text == position, 1.5);
        string?[] problems =
        [
            positionRead == position ? null : $"the position reads \"{positionRead}\"",
            NameOf(editor, "StudioSceneRangeText", 0.5) == range ? null : $"the times read \"{NameOf(editor, "StudioSceneRangeText", 0)}\"",
            Until(() => Find(editor, "StudioPreviousSceneButton", 0.5)?.IsEnabled, enabled => enabled == canGoBack, 1) == canGoBack ? null : $"Previous scene enabled: {!canGoBack}",
            Until(() => Find(editor, "StudioNextSceneButton", 0.5)?.IsEnabled, enabled => enabled == canGoOn, 1) == canGoOn ? null : $"Next scene enabled: {!canGoOn}",
            start is null
                ? Absent(editor, "StudioSceneStartText") && Absent(editor, "StudioSceneStartEarlierButton", 0.2) && Absent(editor, "StudioSceneStartAtPlayheadButton", 0.2) ? null : "there is a Start row"
                : Until(() => NameOf(editor, "StudioSceneStartText", 0.5), text => text == $"Scene start {start}", 1) == $"Scene start {start}" ? null : $"Start reads \"{NameOf(editor, "StudioSceneStartText", 0)}\"",
            isMoving switch
            {
                null => Absent(editor, "StudioSceneEntryChoice") ? null : "there is a Transition choice",
                true => Selected(editor, "StudioSceneEntryMove") ?? Selected(editor, "StudioSceneEntryCut", wanted: false),
                false => Selected(editor, "StudioSceneEntryCut") ?? Selected(editor, "StudioSceneEntryMove", wanted: false),
            },
            moveTakes is { } seconds
                ? Slider(editor, "StudioSceneMoveSlider", seconds, string.Create(CultureInfo.InvariantCulture, $"{seconds:0.00} seconds"))
                : Absent(editor, "StudioSceneMoveSlider") ? null : "there is a Duration slider",
            canDelete
                ? Find(editor, "StudioDeleteSceneButton", 0.5) is { IsEnabled: true, Name: "Delete scene" } ? null : "there is no Delete scene button"
                : Absent(editor, "StudioDeleteSceneButton") ? null : "there is a Delete scene button",
        ];
        var detail = string.Join("; ", problems.Where(problem => problem is not null));
        return detail.Length == 0 ? null : detail;
    }

    // ---------------------------------------------------------------------------------------
    // A recording without a camera
    // ---------------------------------------------------------------------------------------

    private void ScenesWithoutACamera()
    {
        Timeline.Mark("12: a recording without a camera");
        if (OpenReady(NewScreenProject("No camera, no scenes"), "scenes, no camera") is not { } editor)
        {
            return;
        }

        using var heard = UiaEvents.Listen(_uia, editor.Root);
        SetSlider(editor, "StudioPlayhead", 4.0);
        string[] scenes = [SceneLane, "StudioScene_0", "StudioSplitSceneButton", "StudioPreviousSceneButton", "StudioNextSceneButton", "StudioScenePositionText", "StudioSceneSectionSplitButton", "StudioSplitSceneNote", "StudioOneSceneNote", "StudioFirstSceneNote", "StudioDeleteSceneButton"];
        var there = scenes.Where(id => editor.Root.Find(id) is not null).ToArray();
        string[] others = ["StudioAddZoomButton", "StudioAddCutButton", "StudioAddSpeedButton", ZoomLane, CutLane, SpeedLane, "StudioTrimBar", "StudioNoCameraNote", "StudioPreviousZoomButton", "StudioPreviousCutButton", "StudioPreviousSpeedButton"];
        var missing = others.Where(id => editor.Root.Find(id) is null).ToArray();

        // S says why nothing happens, and nothing does.
        var mark = heard.Mark();
        var key = Key(editor, StudioShortcutKey.S);
        var said = Said(heard, mark, StudioEditorText.NoCameraForScenesExplanation);
        var held = ScenesOf(editor);
        var canUndo = Find(editor, "StudioUndoButton", 0.5)?.IsEnabled;
        _report.Check(
            "a recording without a camera has no scene lane, no Scene panel and no Split button, and everything else of the timeline and the inspector; what S runs changes nothing and tells a screen reader that there is no camera to arrange differently",
            there.Length == 0 && missing.Length == 0 && key == StudioShortcutAction.SplitScene && said.Said && held.Length == 1 && canUndo == false,
            $"{(there.Length == 0 ? "none of the scene controls is there" : "there: " + string.Join(", ", there))}; {(missing.Length == 0 ? "the zoom, cut and speed controls are" : "missing: " + string.Join(", ", missing))}; S ran {key}; sent: {said.Heard}; the editor holds {held.Length} scene(s); Undo enabled {canUndo}");
        CloseQuietly(editor);
    }

    // ---------------------------------------------------------------------------------------
    // One scene, Split, and a layout for each scene
    // ---------------------------------------------------------------------------------------

    private void SplittingScenes()
    {
        Timeline.Mark("12: one scene");
        if (OpenReady(NewCameraProject("Scenes", ForScenePictures), "scenes") is not { } editor)
        {
            return;
        }

        using var heard = UiaEvents.Listen(_uia, editor.Root);
        string TimeFor(double playhead) => StudioEditorText.GetTimeText(playhead - CameraOffset, RecordingLength - CameraOffset);
        LookForLayout(editor, FrameOf(CameraOffset), 5);

        // One scene: a list of one item, which is the selected one, and a panel that says so.
        const string OnlyName = "Scene 1 of 1, Screen with camera bubble, 0.0 to 12.0 seconds";
        var list = Find(editor, SceneLane);
        var item = Find(editor, "StudioScene_0");
        var lane = LaneText(editor, SceneLane);
        _report.Check(
            "a recording with a camera and one scene: the lane is a list called Scenes with one item, named as the editor words the scene, which is the selected one and stays so; the list can take the keyboard focus, and its item cannot",
            list is { ControlType: ControlTypeNames.List, Name: "Scenes", IsKeyboardFocusable: true, IsEnabled: true, HelpText: "" } && list.IsSelectionRequired == true && list.SelectedNames.SequenceEqual([OnlyName])
                && lane == "*" + OnlyName && OnlyName == SceneName(editor, 0)
                && item is { ControlType: ControlTypeNames.ListItem, IsKeyboardFocusable: false, Patterns: "Invoke,SelectionItem" } && item.Children().Count == 0 && Absent(editor, "StudioSceneLaneEmptyText", 0.2),
            $"{list}, one item is always selected: {list?.IsSelectionRequired}, described as \"{list?.HelpText}\"; the lane: {lane}; {item} patterns({item?.Patterns})");
        var oneScene = NameOf(editor, "StudioOneSceneNote", 1);
        var section = SceneSectionShows(editor, "Scene 1 of 1", "0.0 to 12.0 seconds", canGoBack: false, canGoOn: false, start: null, isMoving: null, moveTakes: null, canDelete: false);
        _report.Check(
            "with one scene the Scene panel says which scene the playhead is in and when it is, has nothing to step to, says what scenes are for, and shows no start, no transition and no Delete scene",
            section is null && oneScene == StudioEditorText.OneSceneExplanation && Absent(editor, "StudioFirstSceneNote", 0.2)
                && Find(editor, "StudioPreviousSceneButton") is { Name: "Previous scene" } && Find(editor, "StudioNextSceneButton") is { Name: "Next scene" },
            section ?? $"\"Scene 1 of 1\", \"0.0 to 12.0 seconds\"; the note: \"{oneScene}\"");

        // The editor opens where the video starts, 0.2 s in: too close to the start of the scene to split it.
        Timeline.Mark("12: where a scene cannot be split");
        (UiaElement? Row, UiaElement? Section, string Note) SplitControls() => (Find(editor, "StudioSplitSceneButton", 0.5), Find(editor, "StudioSceneSectionSplitButton", 0.5), editor.Root.Find("StudioSplitSceneNote")?.Name ?? string.Empty);
        var tooEarly = Until(SplitControls, read => read.Row?.IsEnabled == false && read.Note.Length > 0, 2);
        var mark = heard.Mark();
        var refusedKey = Key(editor, StudioShortcutKey.S);
        var refused = Said(heard, mark, StudioEditorText.SceneTooShortToSplitExplanation);
        _report.Check(
            "less than 0.3 s into the scene, Split in the transport row and Split scene in the panel are disabled, with the reason next to the panel's button and as the description of both; what S runs there changes nothing and tells a screen reader the reason",
            FrameOf(Playhead(editor)) == FrameOf(CameraOffset)
                && tooEarly.Row is { IsEnabled: false, Name: "Split scene" } && tooEarly.Row.HelpText == StudioEditorText.SceneTooShortToSplitExplanation
                && tooEarly.Section is { IsEnabled: false, Name: "Split scene" } && tooEarly.Section.HelpText == StudioEditorText.SceneTooShortToSplitExplanation
                && tooEarly.Note == StudioEditorText.SceneTooShortToSplitExplanation
                && refusedKey == StudioShortcutAction.SplitScene && refused.Said && ScenesOf(editor).Length == 1,
            $"playhead {Seconds(Playhead(editor))} s; {tooEarly.Row} enabled {tooEarly.Row?.IsEnabled}, described as \"{tooEarly.Row?.HelpText}\"; {tooEarly.Section} enabled {tooEarly.Section?.IsEnabled}, described as \"{tooEarly.Section?.HelpText}\"; the note: \"{tooEarly.Note}\"; S ran {refusedKey}; sent: {refused.Heard}");

        SetSlider(editor, "StudioPlayhead", 4.0);
        var canSplit = Until(SplitControls, read => read.Row?.IsEnabled == true && read.Note.Length == 0, 2);
        _report.Check(
            "four seconds in, both buttons are enabled, the reason is gone, and both are described by what they do",
            canSplit.Row is { IsEnabled: true } && canSplit.Row.HelpText == SplitHint && canSplit.Section is { IsEnabled: true } && canSplit.Section.HelpText == SplitHint && canSplit.Note.Length == 0,
            $"{canSplit.Row} enabled {canSplit.Row?.IsEnabled}, described as \"{canSplit.Row?.HelpText}\"; {canSplit.Section} enabled {canSplit.Section?.IsEnabled}, described as \"{canSplit.Section?.HelpText}\"; the note: \"{canSplit.Note}\"");

        // S, at 4.0 s.
        Timeline.Mark("12: S splits the scene at the playhead");
        var at = Playhead(editor);
        mark = heard.Mark();
        var key = Key(editor, StudioShortcutKey.S);
        Expect(editor, p => p with { Scenes = [p.Scenes[0], p.Scenes[0] with { Start = 4.0, Transition = new StudioTransition { Kind = StudioTransitionKind.Morph } }] });
        var held = ScenesWhenAs(editor);
        lane = WaitForLane(editor, SceneLaneWanted(editor, 1), 2, SceneLane);
        var split = Said(heard, mark, StudioEditorText.SceneSplitMessage);

        // At its first instant a scene still looks like the one before, so the playhead goes on to where it has been entered.
        var head = Until(() => Playhead(editor), value => Same(value, 4.35), 2);
        var time = TimeText(editor);
        _report.Check(
            "what S runs starts a new scene at the playhead: a copy of the scene it splits, entered by moving for 0.35 seconds; the lane gets a second item, named as the editor words the scene, and it is the selected one",
            key == StudioShortcutAction.SplitScene && at == 4.0 && held == Describe(editor.Expected.Scenes) && lane == SceneLaneWanted(editor, 1)
                && lane == "Scene 1 of 2, Screen with camera bubble, 0.0 to 4.0 seconds | *Scene 2 of 2, Screen with camera bubble, 4.0 to 12.0 seconds"
                && Find(editor, SceneLane)?.SelectedNames.SequenceEqual([SceneName(editor, 1)]) == true && CurrentSceneOf(editor) == 1,
            $"the key ran {key} with the playhead at {Seconds(at)} s; the editor holds {held}; the lane: {lane}");
        _report.Check("a screen reader is told \"Scene split.\"", split.Said, $"sent: {split.Heard}");
        _report.Check(
            "the paused playhead moves to where the new scene has been entered, 0.35 seconds after its start, and the time follows",
            Same(head, 4.35) && time == TimeFor(head),
            $"playhead {Seconds(head)} s (frame {FrameOf(head)}), time \"{time}\"");
        section = SceneSectionShows(editor, "Scene 2 of 2", "4.0 to 12.0 seconds", canGoBack: true, canGoOn: false, start: "4.0 seconds", isMoving: true, moveTakes: 0.35, canDelete: true);
        var move = Find(editor, "StudioSceneMoveSlider", 0.5);
        _report.Check(
            "the Scene panel shows the new scene: which it is and its times, its start, that its transition is animated, how long that takes on a slider called Transition duration, from 0.1 to 2 seconds in steps of 0.05, and Delete scene; the notes for one scene and for the first scene are gone",
            section is null && Absent(editor, "StudioOneSceneNote", 0.5) && Absent(editor, "StudioFirstSceneNote", 0.2)
                && move is { Name: "Transition duration" } && move.Range is { } range && Same(range.Minimum, 0.1) && range.Maximum == 2 && Same(range.SmallChange, 0.05),
            section ?? $"\"Scene 2 of 2\", \"4.0 to 12.0 seconds\", \"Scene start 4.0 seconds\", Animated, {move} \"{move?.ValueText}\" in {F(move?.Range?.Minimum ?? double.NaN)} to {F(move?.Range?.Maximum ?? double.NaN)} by {F(move?.Range?.SmallChange ?? double.NaN)}");

        // The new scene is a copy, so the picture is as it was: the bubble layout, at the frame the playhead is on.
        ShowsLayout(editor, "a scene that has just been split off looks like the scene it came from: the preview shows the playhead's frame in the bubble layout", FrameOf(head));

        // Side by side, by its key: for the scene the playhead is in, and no other.
        Timeline.Mark("12: a layout for the second scene");
        mark = heard.Mark();
        var layoutKey = Key(editor, StudioShortcutKey.Digit3);
        Expect(editor, p => WithSceneAt(p, 1, scene => scene with { Layout = StudioLayout.SideBySide }));
        held = ScenesWhenAs(editor);
        lane = WaitForLane(editor, SceneLaneWanted(editor, 1), 2, SceneLane);
        var layoutSaid = Said(heard, mark, "Layout: Side by side.");
        _report.Check(
            "what the 3 key runs makes the scene the playhead is in side by side and leaves the first scene as it was; the lane names the second scene by its new layout, and a screen reader is told the layout",
            layoutKey == StudioShortcutAction.ShowSideBySideLayout && held == Describe(editor.Expected.Scenes) && layoutSaid.Said
                && lane == "Scene 1 of 2, Screen with camera bubble, 0.0 to 4.0 seconds | *Scene 2 of 2, Side by side, 4.0 to 12.0 seconds",
            $"the key ran {layoutKey}; the editor holds {held}; the lane: {lane}; sent: {layoutSaid.Heard}");
        var after = ShowsLayout(editor, "the preview shows the second scene side by side, once it has been entered: the screen and the camera where the format puts them, each showing the playhead's frame", FrameOf(head));
        if (after is not null)
        {
            var path = Path.Combine(_output, "scene-side-by-side.png");
            after.Shot.Save(path);
            _report.Line($"  saved {path}");
        }

        InspectorFollowsTheScene(editor);
        TheMoveIntoAScene(editor);
        ThirdSceneEnteredByACut(editor, heard);
        MoveTakes(editor);
        SceneStartButtons(editor, heard);
        PreviousAndNextScene(editor, heard);
        SceneLaneKeys(editor, heard);
        SceneLaneItems(editor);
        SplitSwitchedOffByItself(editor);
        TimelineTabOrder(editor);
        UndoAndRedoOfScenes(editor);

        // What was saved, once the editor has saved by itself.
        var wanted = Describe(ScenesOf(editor));
        var saved = Until(() => Describe(_services.Store.Load(editor.Id).Scenes), text => text == wanted, 3, 100);
        _report.Check("the editor saves the scenes by itself: the project file holds the scenes the editor holds", saved == wanted && ScenesOf(editor).Length > 1, saved == wanted ? saved : $"saved: {saved} | the editor holds: {wanted}");
        CloseQuietly(editor);
    }

    /// <summary>
    /// Moving the playhead from one scene into another changes nothing in the project, and
    /// everything that shows the current scene has to follow: the lane's selected item, the
    /// Scene panel, the layout that is chosen, the camera's controls, and the handle on the
    /// camera in the preview.
    /// </summary>
    private void InspectorFollowsTheScene(Editor editor)
    {
        Timeline.Mark("12: the inspector follows the playhead into another scene");
        (string Lane, string? Section, string? Layout, bool Size, bool Share, string Preview, StudioFrameRect? Handle, LayoutSight? Sight) At(int frame, int scene, string position, string range, string layoutId, bool isFirst)
        {
            SetSlider(editor, "StudioPlayhead", MiddleOf(frame));
            var lane = WaitForLane(editor, SceneLaneWanted(editor, scene), 2, SceneLane);
            var section = isFirst
                ? SceneSectionShows(editor, position, range, canGoBack: false, canGoOn: true, start: null, isMoving: null, moveTakes: null, canDelete: true)
                : SceneSectionShows(editor, position, range, canGoBack: true, canGoOn: false, start: "4.0 seconds", isMoving: true, moveTakes: 0.35, canDelete: true);
            var layout = Until(() => Selected(editor, layoutId), wrong => wrong is null, 1.5);
            var sight = LookForLayout(editor, frame, 3);
            return (lane, section, layout, editor.Root.Find("StudioCameraSizeSlider") is not null, editor.Root.Find("StudioCameraShareSlider") is not null,
                Find(editor, "StudioPreview", 0.5)?.HelpText ?? string.Empty, sight is null ? null : HandleRect(editor, sight.Shot), sight);
        }

        var undoBefore = Find(editor, "StudioUndoButton", 0.5)?.IsEnabled;
        var first = At(60, 0, "Scene 1 of 2", "0.0 to 4.0 seconds", "StudioLayoutBubble", isFirst: true);
        var firstNote = editor.Root.Find("StudioFirstSceneNote")?.Name ?? string.Empty;
        var second = At(180, 1, "Scene 2 of 2", "4.0 to 12.0 seconds", "StudioLayoutSideBySide", isFirst: false);
        var secondNote = editor.Root.Find("StudioFirstSceneNote")?.Name ?? string.Empty;
        var again = At(60, 0, "Scene 1 of 2", "0.0 to 4.0 seconds", "StudioLayoutBubble", isFirst: true);
        var held = Describe(ScenesOf(editor));

        // The handle lies over the camera of the bubble layout, and a layout without a bubble has none.
        var bubble = first.Sight?.Reading.CameraLayer;
        var handleOff = first.Handle is { } handle && bubble is { } camera ? EdgeDistance(handle, camera) : double.NaN;
        _report.Check(
            "with the playhead in the first scene the lane marks the first scene, the Scene panel shows it and says that the first scene has nothing to move from, Layout shows Bubble, the camera has its bubble controls, and the handle in the preview lies over the camera",
            first.Lane == SceneLaneWanted(editor, 0) && first.Section is null && first.Layout is null && first.Size && !first.Share && firstNote == StudioEditorText.FirstSceneExplanation
                && first.Preview == "Screen with camera bubble, camera bottom right" && handleOff <= 2 && first.Sight is not null && JudgeLayout(first.Sight.Reading) is null,
            $"the lane: {first.Lane}; {first.Section ?? "the panel shows the first scene"}; {first.Layout ?? "Bubble is chosen"}; Size slider {first.Size}, Camera size slider of side by side {first.Share}; the note: \"{firstNote}\"; the preview is described as \"{first.Preview}\"; "
                + $"the handle is {(first.Handle is { } h ? R(h) : "hidden")} and the camera {(bubble is { } b ? R(b) : "nowhere")}; {(first.Sight is null ? "no screenshot" : JudgeLayout(first.Sight.Reading) ?? "the picture is the bubble layout")}");
        _report.Check(
            "with the playhead in the second scene all of that shows the second scene: the lane marks it, the panel shows its start and its move, Layout shows Side by side, the camera has its side-by-side controls, and the preview has no handle",
            second.Lane == SceneLaneWanted(editor, 1) && second.Section is null && second.Layout is null && !second.Size && second.Share && secondNote.Length == 0
                && second.Preview == "Side by side" && second.Handle is null && second.Sight is not null && JudgeLayout(second.Sight.Reading) is null,
            $"the lane: {second.Lane}; {second.Section ?? "the panel shows the second scene"}; {second.Layout ?? "Side by side is chosen"}; Size slider {second.Size}, Camera size slider of side by side {second.Share}; the first-scene note: \"{secondNote}\"; "
                + $"the preview is described as \"{second.Preview}\"; the handle is {(second.Handle is { } h2 ? R(h2) : "hidden")}; {(second.Sight is null ? "no screenshot" : JudgeLayout(second.Sight.Reading) ?? "the picture is side by side")}");
        _report.Check(
            "going back shows the first scene again, and none of it changed the project: the scenes are as they were and nothing was added to what can be undone",
            again.Lane == first.Lane && again.Section is null && again.Layout is null && again.Size && !again.Share && held == Describe(editor.Expected.Scenes) && Find(editor, "StudioUndoButton", 0.5)?.IsEnabled == undoBefore,
            $"the lane: {again.Lane}; {again.Section ?? "the panel shows the first scene"}; {again.Layout ?? "Bubble is chosen"}; the editor holds {held}");
    }

    /// <summary>
    /// The move into the second scene, with the preview paused inside it: frame by frame the
    /// two layers are where the format puts them on their way from the bubble layout to side by
    /// side (section 6.9: a share k = u × u × (3 − 2u) of the way, u being the share of the move
    /// that has passed at the frame's instant).
    /// </summary>
    private void TheMoveIntoAScene(Editor editor)
    {
        Timeline.Mark("12: the move into a scene, paused");

        // The scene starts at 4.0 s and its move takes 0.35 s. Frame 119 is the last of the first
        // scene. The instants of the frames 120 to 129, their middles, are inside the move, and
        // that of frame 130, 4.35 s, is its end.
        int[] frames = [119, 120, 122, 124, 125, 127, 129, 130];
        var readings = new List<(int Frame, LayoutReading? Reading, string? Wrong)>();
        var canvases = new Dictionary<int, Box>();
        foreach (var frame in frames)
        {
            SetSlider(editor, "StudioPlayhead", MiddleOf(frame));
            var sight = LookForLayout(editor, frame, 3);
            readings.Add((frame, sight?.Reading, sight is null ? "no screenshot" : JudgeLayout(sight.Reading)));
            if (sight is not null)
            {
                canvases[frame] = sight.Canvas;
            }
            if (frame == 125 && sight is not null)
            {
                var path = Path.Combine(_output, "scene-moving-in.png");
                sight.Shot.Save(path);
                _report.Line($"  saved {path}");
            }
        }

        var read = readings.Where(entry => entry.Reading is not null && entry.Wrong is null).ToList();
        foreach (var entry in read)
        {
            _layoutErrors.Add(entry.Reading!.Worst);
        }

        // The pictures are read against what StudioLayoutResolver gives, which is also what the
        // preview draws from. So that a fault in the resolver's move does not go by unseen, the
        // rectangles of the frames inside the move are worked out here as well, from section 6.9
        // of the project format and the two scenes at rest: a share k = u × u × (3 − 2u) of the
        // way from the one to the other, x, y, width and height each by itself, where u is how
        // much of the 0.35 s has passed at the frame's middle.
        var project = editor.Expected;
        var atRestBefore = project with { Scenes = [project.Scenes[0]] };
        var atRestAfter = project with { Scenes = [project.Scenes[1] with { Start = 0 }] };
        static StudioFrameRect Share(StudioFrameRect from, StudioFrameRect to, double k) =>
            new(from.X + ((to.X - from.X) * k), from.Y + ((to.Y - from.Y) * k), from.Width + ((to.Width - from.Width) * k), from.Height + ((to.Height - from.Height) * k));
        var byHand = new List<string>();
        var notByHand = new List<string>();
        foreach (var entry in read.Where(entry => entry.Frame is >= 120 and <= 129))
        {
            var canvas = canvases[entry.Frame];
            var u = (MiddleOf(entry.Frame) - 4.0) / 0.35;
            var k = u * u * (3 - (2 * u));
            var from = StudioLayoutResolver.Resolve(atRestBefore, 1, canvas.Width, canvas.Height);
            var to = StudioLayoutResolver.Resolve(atRestAfter, 1, canvas.Width, canvas.Height);
            var given = StudioLayoutResolver.Resolve(project, MiddleOf(entry.Frame), canvas.Width, canvas.Height);
            var screen = Share(from.Screen!.Value.Rect, to.Screen!.Value.Rect, k);
            var camera = Share(from.Camera!.Value.Rect, to.Camera!.Value.Rect, k);
            var apart = Math.Max(EdgeDistance(screen, given.Screen!.Value.Rect), EdgeDistance(camera, given.Camera!.Value.Rect));
            byHand.Add(string.Create(CultureInfo.InvariantCulture, $"frame {entry.Frame}: k {k:0.0000}, the screen {R(screen)}, the camera {R(camera)}"));
            if (apart > 0.01)
            {
                notByHand.Add(string.Create(CultureInfo.InvariantCulture, $"frame {entry.Frame}: by hand the screen is in {R(screen)} and the camera in {R(camera)}; the resolver has them in {R(given.Screen.Value.Rect)} and {R(given.Camera.Value.Rect)}, {apart:0.00} px apart"));
            }
        }

        _report.Check(
            "what those pictures were read against is the format's: worked out by hand from the two scenes at rest, a share k = u × u × (3 − 2u) of the way, the rectangles of the six frames inside the move are the ones the pictures showed",
            byHand.Count == 6 && notByHand.Count == 0,
            notByHand.Count == 0 ? string.Join("; ", byHand) : string.Join(" | ", notByHand));

        // What tells a frame's picture from its neighbour's: how far the screen's rectangle moves from one frame to the next.
        static double Step(LayoutReading a, LayoutReading b) => a.ScreenLayer is { } from && b.ScreenLayer is { } to ? EdgeDistance(from, to) : double.NaN;
        var byFrame = read.ToDictionary(entry => entry.Frame, entry => entry.Reading!);
        var steps = new[] { (120, 122), (122, 124), (124, 125), (125, 127), (127, 129) }.Where(pair => byFrame.ContainsKey(pair.Item1) && byFrame.ContainsKey(pair.Item2)).Select(pair => (pair.Item1, pair.Item2, Moved: Step(byFrame[pair.Item1], byFrame[pair.Item2]))).ToArray();
        var wrong = readings.Where(entry => entry.Wrong is not null).ToArray();
        var before = byFrame.GetValueOrDefault(119);
        var end = byFrame.GetValueOrDefault(130);
        _report.Check(
            "on both sides of the line between two scenes the preview shows each scene's own layout: the frame before the second scene starts is the bubble layout, and the frame at which its move ends is side by side",
            before is { Resolved.SceneIndex: 0, Resolved.Layout: StudioLayout.Bubble } && end is { Resolved.SceneIndex: 1, Resolved.Layout: StudioLayout.SideBySide } && before.ScreenLayer is { } wide && end.ScreenLayer is { } narrow && wide.Width - narrow.Width > 100,
            $"frame 119: {(before is null ? readings.First(entry => entry.Frame == 119).Wrong : before.ToString())}; frame 130: {(end is null ? readings.First(entry => entry.Frame == 130).Wrong : end.ToString())}");
        _report.Check(
            "paused inside the move, the preview shows the layers on their way: at each of six frames the screen and the camera are where the format puts them for that frame's instant, each showing that frame",
            wrong.Length == 0 && read.Count == frames.Length && steps.Length == 5 && steps.All(step => step.Moved > 2),
            wrong.Length > 0
                ? string.Join(" | ", wrong.Select(entry => $"frame {entry.Frame}: {entry.Wrong}"))
                : string.Join("; ", read.Where(entry => entry.Frame is > 119 and < 130).Select(entry => string.Create(CultureInfo.InvariantCulture, $"frame {entry.Frame}: the screen in {R(entry.Reading!.ScreenLayer!.Value)} and the camera in {R(entry.Reading.CameraLayer!.Value)}, {entry.Reading.Edges} edges, the furthest {entry.Reading.Worst:0.00} px from its place")))
                    + "; from one of these frames to the next the screen's rectangle moves by " + string.Join(", ", steps.Select(step => string.Create(CultureInfo.InvariantCulture, $"{step.Moved:0.0} px ({step.Item1} to {step.Item2})")))
                    + ", so a picture that showed a frame where a neighbouring frame has its layers would have its edges that far from their places");
    }
}
