using TinyClips.Core.Models;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>Scenes in the editor session: the scene the playhead is in, and what splits, deletes and moves one.</summary>
public sealed class StudioEditorSessionSceneTests : StudioEditorSessionTestBase
{
    private const StudioEditorChanges Edited = StudioEditorChanges.Project | StudioEditorChanges.Playback;

    // The current scene

    [Fact]
    public async Task TheCurrentScene_FollowsThePlayhead_AndIsSaidOnce()
    {
        var session = await OpenAsync(CreateProjectWithScenes(ThreeScenes()));
        Assert.Equal(3, session.SceneCount);
        Assert.Equal(0, session.CurrentSceneIndex);
        Changes.Clear();

        session.Scrub(5);
        Assert.Equal(1, session.CurrentSceneIndex);
        Assert.Equal(StudioLayout.SideBySide, session.Model!.EffectiveLayout);
        Assert.Equal(new[] { StudioEditorChanges.Playback | StudioEditorChanges.Scene }, Changes);

        // Moving inside the scene says nothing about scenes.
        Changes.Clear();
        session.Scrub(6);
        session.StepFrames(1);
        Assert.Equal(new[] { StudioEditorChanges.Playback, StudioEditorChanges.Playback }, Changes);

        // Playing into the next scene says so when the playhead gets there.
        session.TogglePlayback();
        Changes.Clear();
        Preview.RaisePosition(6.5);
        Pump();
        Assert.Equal(1, session.CurrentSceneIndex);
        Assert.DoesNotContain(Changes, change => change.HasFlag(StudioEditorChanges.Scene));
        Preview.RaisePosition(7.2);
        Pump();
        Assert.Equal(2, session.CurrentSceneIndex);
        Assert.Contains(Changes, change => change.HasFlag(StudioEditorChanges.Scene));
        Assert.False(session.HasUnsavedEdits);
    }

    [Fact]
    public async Task TheLayoutControls_ChangeTheSceneThePlayheadIsIn()
    {
        var session = await OpenAsync(CreateProjectWithScenes(ThreeScenes()));
        session.Scrub(5);

        session.SetLayout(StudioLayout.Screen);
        session.SetSideBySide(StudioCameraSide.Leading, 0.5);

        var scenes = session.Project!.Scenes;
        Assert.Equal(StudioLayout.Bubble, scenes[0].Layout);
        Assert.Equal(StudioLayout.Screen, scenes[1].Layout);
        Assert.Equal(StudioCameraSide.Leading, scenes[1].Split.CameraSide);
        Assert.Equal(StudioLayout.Camera, scenes[2].Layout);
        Assert.Equal(StudioLayout.Screen, Preview.LastProject!.Scenes[1].Layout);

        // The bubble's handle is the current scene's bubble: the third scene has it top left.
        session.Scrub(8);
        session.SetCameraAnchor(StudioAnchor.TopLeft);
        var handle = session.GetBubbleRect(1000, 800);
        Assert.Null(handle);
        session.SetLayout(StudioLayout.Bubble);
        handle = session.GetBubbleRect(1000, 800);
        Assert.NotNull(handle);
        Assert.Equal(24, handle.Value.X, Precision);
        Assert.Equal(24, handle.Value.Y, Precision);
        Assert.Equal(StudioAnchor.BottomRight, session.Project!.Scenes[0].Bubble.Anchor);
    }

    // Split

    [Fact]
    public async Task Split_StartsASceneAtThePlayhead_AndShowsItEntered()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        session.Scrub(4);
        Changes.Clear();
        Preview.Seeks.Clear();
        Assert.True(session.CanSplitSceneAtPlayhead);
        Assert.Null(session.SplitSceneExplanation);

        var result = session.SplitSceneAtPlayhead();

        Assert.Equal(new StudioSceneEditResult(true, 1), result);
        Assert.Equal(2, session.SceneCount);
        Assert.Equal(4, session.Project!.Scenes[1].Start, Precision);
        Assert.Equal(StudioTransitionKind.Morph, session.Project.Scenes[1].Transition.Kind);
        Assert.Equal(2, Preview.LastProject!.Scenes.Length);

        // A paused playhead moves to where the new scene has been entered, 0.35 s on.
        Assert.Equal(4.35, session.Playhead, Precision);
        Assert.Equal(4.35, Assert.Single(Preview.Seeks), Precision);
        Assert.Equal(1, session.CurrentSceneIndex);
        Assert.Equal(new[] { Edited | StudioEditorChanges.Scene, StudioEditorChanges.Playback }, Changes);
        Assert.True(session.HasUnsavedEdits);

        // One undo step, and the playhead is in the only scene again.
        Changes.Clear();
        session.Undo();
        Assert.Equal(1, session.SceneCount);
        Assert.Equal(0, session.CurrentSceneIndex);
        Assert.Equal(new[] { Edited | StudioEditorChanges.Scene }, Changes);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public async Task Split_WhilePlaying_LeavesThePlayheadAlone()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        session.TogglePlayback();
        Preview.RaisePosition(6);
        Pump();
        Preview.Seeks.Clear();

        var result = session.SplitSceneAtPlayhead();

        Assert.Equal(new StudioSceneEditResult(true, 1), result);
        Assert.Equal(6, session.Project!.Scenes[1].Start, Precision);
        Assert.True(session.IsPlaying);
        Assert.Empty(Preview.Seeks);
        Assert.Equal(6, session.Playhead, Precision);
    }

    [Fact]
    public async Task Split_IsRefusedWithItsReason()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        Changes.Clear();

        // At the very start a half would have no length.
        Assert.False(session.CanSplitSceneAtPlayhead);
        Assert.Equal(StudioEditorText.SceneTooShortToSplitExplanation, session.SplitSceneExplanation);
        Assert.Equal(new StudioSceneEditResult(false, 0), session.SplitSceneAtPlayhead());
        Assert.Empty(Changes);
        Assert.False(session.HasUnsavedEdits);

        // Inside the move into a scene.
        session.Scrub(4);
        session.SplitSceneAtPlayhead();
        session.Scrub(4.32);
        Assert.False(session.CanSplitSceneAtPlayhead);
        Assert.Equal(StudioEditorText.SceneStillMovingExplanation, session.SplitSceneExplanation);
        Assert.False(session.SplitSceneAtPlayhead().Changed);
        Assert.Equal(2, session.SceneCount);

        // Without a camera there is nothing to arrange differently.
        var screenOnly = await OpenAsync(CreateProject(camera: false));
        screenOnly.Scrub(4);
        Assert.False(screenOnly.CanSplitSceneAtPlayhead);
        Assert.Equal(StudioEditorText.NoCameraForScenesExplanation, screenOnly.SplitSceneExplanation);
        Assert.False(screenOnly.SplitSceneAtPlayhead().Changed);
    }

    // Delete

    [Fact]
    public async Task RemoveCurrentScene_HandsItsTimeToTheSceneBefore()
    {
        var session = await OpenAsync(CreateProjectWithScenes(ThreeScenes()));
        session.Scrub(5);
        Changes.Clear();
        Assert.True(session.CanRemoveCurrentScene);

        var result = session.RemoveCurrentScene();

        Assert.Equal(new StudioSceneEditResult(true, 0), result);
        Assert.Equal(new[] { 0.0, 7.0 }, session.Project!.Scenes.Select(scene => scene.Start));
        Assert.Equal(new[] { StudioLayout.Bubble, StudioLayout.Camera }, session.Project.Scenes.Select(scene => scene.Layout));
        Assert.Equal(2, Preview.LastProject!.Scenes.Length);
        Assert.Equal(0, session.CurrentSceneIndex);
        Assert.Equal(5, session.Playhead, Precision);
        Assert.Equal(new[] { Edited | StudioEditorChanges.Scene }, Changes);

        // By its place on the lane, and then the only scene stays.
        Assert.Equal(new StudioSceneEditResult(true, 0), session.RemoveScene(1));
        Assert.Equal(StudioLayout.Bubble, Assert.Single(session.Project.Scenes).Layout);
        Assert.Equal(1, session.SceneCount);
        Assert.False(session.CanRemoveCurrentScene);
        Changes.Clear();
        Assert.Equal(new StudioSceneEditResult(false, 0), session.RemoveCurrentScene());
        Assert.Empty(Changes);
    }

    // Start and how a scene is entered

    [Fact]
    public async Task SetSceneStart_TakesThePlayheadWithIt()
    {
        var session = await OpenAsync(CreateProjectWithScenes(ThreeScenes()));
        Preview.Seeks.Clear();

        Assert.Equal(new StudioSceneEditResult(true, 1), session.SetSceneStart(1, 5));

        Assert.Equal(5, session.Project!.Scenes[1].Start, Precision);
        Assert.Equal(5, session.Playhead, Precision);
        Assert.Equal(5, Preview.Seeks[^1], Precision);
        Assert.Equal(1, session.CurrentSceneIndex);

        // A start that runs into the scene before stops 0.3 s after that scene's start, and the
        // playhead is where the start now is.
        session.SetSceneStart(1, 0.1);
        Assert.Equal(0.3, session.Project.Scenes[1].Start, Precision);
        Assert.Equal(0.3, session.Playhead, Precision);

        // The first scene starts with the recording: nothing changes and nothing is shown.
        session.Scrub(9);
        Preview.Seeks.Clear();
        Assert.False(session.SetSceneStart(0, 2).Changed);
        Assert.Empty(Preview.Seeks);
        Assert.Equal(9, session.Playhead, Precision);
    }

    [Fact]
    public async Task SetSceneStartAtPlayhead_LeavesThePlayheadWhereItIs()
    {
        var session = await OpenAsync(CreateProjectWithScenes(ThreeScenes()));
        session.Scrub(5.5);
        Preview.Seeks.Clear();

        Assert.Equal(new StudioSceneEditResult(true, 1), session.SetSceneStartAtPlayhead(session.CurrentSceneIndex));

        Assert.Equal(5.5, session.Project!.Scenes[1].Start, Precision);
        Assert.Equal(5.5, session.Playhead, Precision);
        Assert.Empty(Preview.Seeks);
    }

    [Fact]
    public async Task HowASceneIsEntered_IsAnEditLikeAnyOther()
    {
        var session = await OpenAsync(CreateProjectWithScenes(ThreeScenes()));
        Changes.Clear();

        Assert.True(session.SetSceneTransitionKind(1, StudioTransitionKind.Morph).Changed);
        Assert.True(session.SetSceneTransitionDuration(1, 1.5).Changed);

        Assert.Equal(StudioTransitionKind.Morph, session.Project!.Scenes[1].Transition.Kind);
        Assert.Equal(1.5, session.Project.Scenes[1].Transition.Duration, Precision);
        Assert.Equal(1.5, Preview.LastProject!.Scenes[1].Transition.Duration, Precision);
        Assert.Equal(new[] { Edited, Edited }, Changes);

        // Not for the first scene.
        Changes.Clear();
        Assert.False(session.SetSceneTransitionKind(0, StudioTransitionKind.Morph).Changed);
        Assert.Empty(Changes);

        session.Undo();
        session.Undo();
        Assert.Equal(StudioTransitionKind.Cut, session.Project.Scenes[1].Transition.Kind);
        Assert.False(session.CanUndo);
    }

    // A drag while the recording plays

    [Fact]
    public async Task ADragInTheCurrentScene_WhilePlaying_StopsPlaybackAtItsFirstChange()
    {
        var session = await OpenAsync(CreateProjectWithScenes(ThreeScenes()));
        PlayTo(session, from: 3.5, to: 3.8);

        // The pointer goes down. Until it changes something, playback goes on.
        session.BeginGesture();
        Assert.True(session.IsPlaying);

        // The first change stops playback where the picture is, which is a little further on than
        // the playhead had got, and is made there.
        Preview.Position = 3.9;
        Preview.Calls.Clear();
        Changes.Clear();
        session.SetCameraBubbleSize(0.3);

        Assert.False(session.IsPlaying);
        Assert.Equal(new[] { "Pause", "UpdateProject" }, Preview.Calls);
        Assert.Equal(3.9, session.Playhead, Precision);
        Assert.Equal(new[] { StudioEditorChanges.Playback, Edited }, Changes);
        Assert.Equal(0.3, session.Project!.Scenes[0].Bubble.Size, Precision);
        session.EndGesture();
    }

    [Fact]
    public async Task ADragInTheCurrentScene_WhilePlaying_ChangesThatSceneAndNoOther()
    {
        var session = await OpenAsync(CreateProjectWithScenes(ThreeScenes()));
        PlayTo(session, from: 3.5, to: 3.8);

        session.BeginGesture();
        session.SetCameraBubbleSize(0.3);

        // Where the playhead would have been by now, had the recording played on, is the second
        // scene. What the preview still says of it is not listened to.
        Preview.RaisePosition(4.2);
        Pump();
        session.SetCameraBubbleSize(0.35);
        session.MoveBubbleTopLeft(100, 100, 1920, 1080);
        session.SetCameraBubbleSize(0.4);
        session.EndGesture();

        var scenes = session.Project!.Scenes;
        Assert.Equal(0, session.CurrentSceneIndex);
        Assert.Equal(new[] { 0.4, 0.24, 0.24 }, scenes.Select(scene => scene.Bubble.Size));
        Assert.Equal(
            new[] { StudioAnchor.TopLeft, StudioAnchor.BottomRight, StudioAnchor.BottomRight },
            scenes.Select(scene => scene.Bubble.Anchor));

        // The whole drag is one undo step.
        session.Undo();
        Assert.Equal(new[] { 0.24, 0.24, 0.24 }, session.Project.Scenes.Select(scene => scene.Bubble.Size));
        Assert.Equal(StudioAnchor.BottomRight, session.Project.Scenes[0].Bubble.Anchor);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public async Task ADragThatBeginsAsTheNextSceneComes_ChangesTheSceneThePictureStopsIn()
    {
        var scenes = ThreeScenes();
        scenes[0] = scenes[0] with { Bubble = new StudioBubble { OffsetX = 0.1, OffsetY = 0.2 }, Split = new StudioSplit { CameraSide = StudioCameraSide.Leading } };
        scenes[1] = scenes[1] with { Bubble = new StudioBubble { OffsetX = -0.1, OffsetY = -0.3 } };
        var session = await OpenAsync(CreateProjectWithScenes(scenes));
        PlayTo(session, from: 3.5, to: 3.95);
        Assert.Equal(0, session.CurrentSceneIndex);

        // The playhead is still in the first scene, and the picture already in the second, when
        // the first change of the drag arrives. Each of these sets one number, and the other one
        // of the pair stays as the scene that is changed has it.
        session.BeginGesture();
        Preview.Position = 4.1;
        session.SetCameraBubbleOffsetX(0.05);
        session.SetCameraBubbleOffsetY(-0.25);
        session.SetCameraShare(0.4);
        session.EndGesture();

        Assert.False(session.IsPlaying);
        Assert.Equal(4.1, session.Playhead, Precision);
        Assert.Equal(1, session.CurrentSceneIndex);
        var after = session.Project!.Scenes;
        Assert.Equal((0.1, 0.2), (after[0].Bubble.OffsetX, after[0].Bubble.OffsetY));
        Assert.Equal((StudioCameraSide.Leading, 0.3), (after[0].Split.CameraSide, after[0].Split.CameraFraction));
        Assert.Equal((0.05, -0.25), (after[1].Bubble.OffsetX, after[1].Bubble.OffsetY));
        Assert.Equal((StudioCameraSide.Trailing, 0.4), (after[1].Split.CameraSide, after[1].Split.CameraFraction));

        // One at a time, each leaves the other as it was.
        session.SetCameraBubbleOffsetX(0.5);
        Assert.Equal((0.5, -0.25), (session.Project.Scenes[1].Bubble.OffsetX, session.Project.Scenes[1].Bubble.OffsetY));
        session.SetCameraBubbleOffsetY(0.5);
        Assert.Equal((0.5, 0.5), (session.Project.Scenes[1].Bubble.OffsetX, session.Project.Scenes[1].Bubble.OffsetY));
    }

    [Fact]
    public async Task ADragOfHowLongTheMoveTakes_WhilePlaying_StaysInItsScene()
    {
        var scenes = ThreeScenes();
        scenes[1] = scenes[1] with { Transition = new StudioTransition { Kind = StudioTransitionKind.Morph } };
        scenes[2] = scenes[2] with { Transition = new StudioTransition { Kind = StudioTransitionKind.Morph } };
        var session = await OpenAsync(CreateProjectWithScenes(scenes));
        PlayTo(session, from: 6.5, to: 6.8);
        Assert.Equal(1, session.CurrentSceneIndex);

        session.BeginGesture();
        Assert.Equal(new StudioSceneEditResult(true, 1), session.SetCurrentSceneTransitionDuration(0.8));
        Assert.False(session.IsPlaying);
        Preview.RaisePosition(7.2);
        Pump();
        Assert.Equal(new StudioSceneEditResult(true, 1), session.SetCurrentSceneTransitionDuration(1.0));
        session.EndGesture();

        Assert.Equal(1.0, session.Project!.Scenes[1].Transition.Duration, Precision);
        Assert.Equal(0.35, session.Project.Scenes[2].Transition.Duration, Precision);

        // The first scene is entered at once: there is nothing to set.
        session.Scrub(1);
        Assert.Equal(new StudioSceneEditResult(false, 0), session.SetCurrentSceneTransitionDuration(0.5));
    }

    [Fact]
    public async Task ADrag_InARecordingWithOneScene_LeavesPlaybackAlone()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        PlayTo(session, from: 1, to: 2);
        Preview.Calls.Clear();

        session.BeginGesture();
        session.SetCameraBubbleSize(0.3);
        session.MoveBubbleTopLeft(100, 100, 1920, 1080);
        session.SetCameraBubbleOffsetX(0.1);
        session.EndGesture();

        Assert.True(session.IsPlaying);
        Assert.DoesNotContain("Pause", Preview.Calls);
        Assert.Equal(0.3, session.Project!.Scenes[0].Bubble.Size, Precision);
    }

    [Fact]
    public async Task WhatIsNotADragInTheScene_LeavesPlaybackAlone()
    {
        var session = await OpenAsync(CreateProjectWithScenes(ThreeScenes()));
        PlayTo(session, from: 3.5, to: 3.8);
        Preview.Calls.Clear();

        // A change that stands alone, as a key or a choice from a list makes it.
        session.SetLayout(StudioLayout.SideBySide);
        session.SetCameraAnchor(StudioAnchor.TopLeft);
        session.SetCameraBubbleSize(0.3);
        session.SetCameraShare(0.4);
        Assert.True(session.IsPlaying);

        // A drag of something the whole recording has.
        session.BeginGesture();
        session.SetCanvasPadding(0.1);
        session.SetScreenShadow(0.9);
        session.EndGesture();
        Assert.True(session.IsPlaying);
        Assert.DoesNotContain("Pause", Preview.Calls);

        // A drag in the scene while nothing plays stops nothing either.
        session.Pause();
        Preview.Calls.Clear();
        session.BeginGesture();
        session.SetCameraBubbleSize(0.35);
        session.EndGesture();
        Assert.DoesNotContain("Pause", Preview.Calls);
    }

    // From scene to scene

    [Fact]
    public async Task ShowNextAndPreviousScene_GoToWhereEachHasBeenEntered()
    {
        var scenes = ThreeScenes();
        scenes[1] = scenes[1] with { Transition = new StudioTransition { Kind = StudioTransitionKind.Morph } };
        var session = await OpenAsync(CreateProjectWithScenes(scenes));

        Assert.True(session.ShowNextScene());
        Assert.Equal(4.35, session.Playhead, Precision);
        Assert.True(session.ShowNextScene());
        Assert.Equal(7, session.Playhead, Precision);
        Assert.False(session.ShowNextScene());
        Assert.Equal(7, session.Playhead, Precision);

        Assert.True(session.ShowPreviousScene());
        Assert.Equal(4.35, session.Playhead, Precision);
        Assert.True(session.ShowPreviousScene());
        Assert.Equal(0, session.Playhead, Precision);
        Assert.False(session.ShowPreviousScene());

        Assert.True(session.ShowScene(2));
        Assert.Equal(2, session.CurrentSceneIndex);
        Assert.False(session.ShowScene(3));
        Assert.False(session.HasUnsavedEdits);
    }

    // While the project cannot be edited

    [Fact]
    public async Task WhileExporting_ScenesAreLeftAlone()
    {
        var session = await OpenAsync(CreateProjectWithScenes(ThreeScenes()));
        session.Scrub(5);
        var export = session.ExportAsync(() => ExportPath, default);
        Assert.True(session.IsExporting);

        Assert.False(session.CanSplitSceneAtPlayhead);
        Assert.False(session.CanRemoveCurrentScene);
        Assert.Equal(new StudioSceneEditResult(false, 1), session.SplitSceneAtPlayhead());
        Assert.Equal(new StudioSceneEditResult(false, 1), session.RemoveCurrentScene());
        Assert.Equal(new StudioSceneEditResult(false, 1), session.SetSceneStart(1, 5.5));
        Assert.False(session.ShowNextScene());
        Assert.Equal(3, session.SceneCount);
        Assert.Equal(4, session.Project!.Scenes[1].Start, Precision);

        Exporter.Complete();
        await FinishAsync(export);
    }

    // Keys

    [Fact]
    public void TheSKey_SplitsTheScene_AndDeleteOnASceneRemovesIt()
    {
        Assert.Equal(StudioShortcutAction.SplitScene, StudioShortcuts.Resolve(Press(StudioShortcutKey.S)));

        // Held down, with Shift or Alt, or while text is being typed it does nothing. With
        // Ctrl it is another command, Save project, and splits nothing.
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.S) with { IsRepeat = true }));
        Assert.Equal(StudioShortcutAction.SaveProject, StudioShortcuts.Resolve(Press(StudioShortcutKey.S) with { IsControlDown = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.S) with { IsShiftDown = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.S) with { IsAltDown = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.S) with { IsTextInputFocused = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.S) with { IsTypeToSearchFocused = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.S) with { IsExporting = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.S) with { IsReady = false }));

        // Delete removes the scene while a scene on the lane has the focus, also when a zoom is
        // selected. Otherwise it removes the selected zoom, or does nothing.
        Assert.Equal(StudioShortcutAction.RemoveCurrentScene, StudioShortcuts.Resolve(Press(StudioShortcutKey.Delete) with { IsSceneFocused = true }));
        Assert.Equal(
            StudioShortcutAction.RemoveCurrentScene,
            StudioShortcuts.Resolve(Press(StudioShortcutKey.Delete) with { IsSceneFocused = true, HasSelectedZoom = true }));
        Assert.Equal(StudioShortcutAction.RemoveSelectedZoom, StudioShortcuts.Resolve(Press(StudioShortcutKey.Delete) with { HasSelectedZoom = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.Delete)));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.Delete) with { IsSceneFocused = true, IsRepeat = true }));
    }

    // Helpers

    /// <summary>Plays from one time until the preview has said it is at another.</summary>
    private void PlayTo(StudioEditorSession session, double from, double to)
    {
        session.Scrub(from);
        session.TogglePlayback();
        Preview.RaisePosition(to);
        Pump();
        Assert.True(session.IsPlaying);
        Assert.Equal(to, session.Playhead, Precision);
    }

    private string CreateProjectWithScenes(params StudioScene[] scenes)
    {
        var id = CreateProject(camera: true);
        Projects.Save(Projects.Load(id) with { Scenes = scenes });
        return id;
    }

    /// <summary>The bubble until 4 s, side by side until 7 s, then the camera alone, in a recording 10 s long.</summary>
    private static StudioScene[] ThreeScenes() =>
    [
        new StudioScene { Start = 0, Layout = StudioLayout.Bubble },
        new StudioScene { Start = 4, Layout = StudioLayout.SideBySide },
        new StudioScene { Start = 7, Layout = StudioLayout.Camera },
    ];

    private static StudioShortcutInput Press(StudioShortcutKey key) => new(
        key,
        IsControlDown: false,
        IsShiftDown: false,
        IsAltDown: false,
        IsRepeat: false,
        IsTextInputFocused: false,
        IsReady: true,
        IsExporting: false);
}
