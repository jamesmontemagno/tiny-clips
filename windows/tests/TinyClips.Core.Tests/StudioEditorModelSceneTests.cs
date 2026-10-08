using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>
/// The editor's rules for scenes. The same cases, with the same numbers, are in the Mac's
/// <c>StudioEditorModelTests</c>.
/// </summary>
public sealed class StudioEditorModelSceneTests
{
    private const int Precision = 9;

    [Fact]
    public void TheCurrentScene_IsTheOneThePlayheadIsIn()
    {
        var model = new StudioEditorModel(ThreeScenes());
        foreach (var (time, index) in new[] { (-5.0, 0), (0.0, 0), (3.999, 0), (4.0, 1), (6.9, 1), (7.0, 2), (100.0, 2), (double.NaN, 0) })
        {
            model.SceneTime = time;
            Assert.Equal(index, model.CurrentSceneIndex);
            Assert.Equal(index, model.GetSceneIndexAt(time));
        }

        model.SceneTime = 5;
        Assert.Equal(StudioLayout.SideBySide, model.CurrentScene.Layout);
        Assert.Equal(StudioLayout.SideBySide, model.EffectiveLayout);

        // Moving the playhead is not an edit.
        Assert.False(model.CanUndo);
        Assert.Equal(Starts(new StudioEditorModel(ThreeScenes())), Starts(model));
    }

    [Fact]
    public void TheLayoutControls_ChangeTheCurrentSceneOnly()
    {
        var model = new StudioEditorModel(ThreeScenes());
        var before = model.Project.Scenes;

        model.SceneTime = 5;
        model.SetLayout(StudioLayout.Screen);
        model.SetSideBySide(StudioCameraSide.Leading, 0.5);
        Assert.Equal(StudioLayout.Screen, model.Project.Scenes[1].Layout);
        Assert.Equal(StudioCameraSide.Leading, model.Project.Scenes[1].Split.CameraSide);
        Assert.Equal(0.5, model.Project.Scenes[1].Split.CameraFraction, Precision);
        Assert.Same(before[0], model.Project.Scenes[0]);
        Assert.Same(before[2], model.Project.Scenes[2]);

        model.SceneTime = 8;
        model.SetCameraBubbleSize(0.4);
        model.SetCameraAnchor(StudioAnchor.TopLeft);
        model.SetCameraBubbleOffsets(0.1, 0.2);
        Assert.Equal(0.4, model.Project.Scenes[2].Bubble.Size, Precision);
        Assert.Equal(StudioAnchor.TopLeft, model.Project.Scenes[2].Bubble.Anchor);
        Assert.Equal(0.1, model.Project.Scenes[2].Bubble.OffsetX, Precision);
        Assert.Equal(0.2, model.Project.Scenes[2].Bubble.OffsetY, Precision);
        Assert.Same(before[0], model.Project.Scenes[0]);

        // Undo puts the scenes back, and the current scene is still the one the playhead is in.
        model.SceneTime = 5;
        for (var step = 0; step < 5; step++)
        {
            model.Undo();
        }

        Assert.False(model.CanUndo);
        Assert.Equal(before, model.Project.Scenes);
        Assert.Equal(1, model.CurrentSceneIndex);
    }

    [Fact]
    public void Opening_PutsScenesInOrderAndDropsThoseThatNeverShow()
    {
        // As section 6.1 reads them: a negative start is 0, the last scene stored for a start wins,
        // and the first scene starts at 0. Scenes that start at or after the end of the recording
        // are never shown and are dropped.
        var model = new StudioEditorModel(MakeProject() with
        {
            Scenes =
            [
                new StudioScene { Start = 6, Layout = StudioLayout.Camera },
                new StudioScene { Start = -2, Layout = StudioLayout.Screen },
                new StudioScene { Start = 3, Layout = StudioLayout.SideBySide },
                new StudioScene { Start = 3, Layout = StudioLayout.Bubble },
                new StudioScene { Start = 10, Layout = StudioLayout.Camera },
                new StudioScene { Start = 12, Layout = StudioLayout.Screen },
            ],
        });
        Assert.Equal([0, 3, 6], Starts(model));
        Assert.Equal([StudioLayout.Screen, StudioLayout.Bubble, StudioLayout.Camera], Layouts(model));
        Assert.False(model.CanUndo);

        // A first scene that starts late starts at 0, and stays even when it is the only one.
        var late = new StudioEditorModel(MakeProject() with { Scenes = [new StudioScene { Start = 25, Layout = StudioLayout.Camera }] });
        Assert.Equal([0], Starts(late));
        Assert.Equal(StudioLayout.Camera, late.Project.Scenes[0].Layout);

        // A recording of no length still has its first scene.
        var empty = new StudioEditorModel(MakeProject(duration: 0));
        Assert.Single(empty.Project.Scenes);
        Assert.Equal(0, empty.CurrentSceneIndex);
        Assert.Equal(StudioLayout.Bubble, empty.CurrentScene.Layout);
    }

    [Fact]
    public void Splitting_StartsACopyOfTheSceneEnteredByMoving()
    {
        var model = new StudioEditorModel(MakeProject());
        model.SetCameraBubbleSize(0.4);
        model.SetSideBySide(StudioCameraSide.Leading, 0.45);

        Assert.Equal(new StudioSceneEditResult(true, 1), model.SplitScene(4));
        var scenes = model.Project.Scenes;
        Assert.Equal([0, 4], Starts(model));
        Assert.Equal(scenes[0].Layout, scenes[1].Layout);
        Assert.Equal(scenes[0].Bubble, scenes[1].Bubble);
        Assert.Equal(scenes[0].Split, scenes[1].Split);
        Assert.Equal(StudioTransitionKind.Morph, scenes[1].Transition.Kind);
        Assert.Equal(0.35, scenes[1].Transition.Duration, Precision);

        // The scene before keeps how it was entered.
        Assert.Equal(StudioTransitionKind.Cut, scenes[0].Transition.Kind);

        // One undo step, and the playhead's scene follows.
        model.SceneTime = 4;
        Assert.Equal(1, model.CurrentSceneIndex);
        model.Undo();
        Assert.Single(model.Project.Scenes);
        Assert.Equal(0, model.CurrentSceneIndex);
        model.Redo();
        Assert.Equal(2, model.Project.Scenes.Length);
    }

    [Fact]
    public void AScene_IsNotSplitWhereAHalfWouldBeTooShortOrItIsStillMoving()
    {
        var model = new StudioEditorModel(MakeProject());

        // Less than 0.3 s from the start of the scene, or from the end of the recording.
        foreach (var time in new[] { 0.0, 0.25, 9.75, 10.0, 50.0, -3.0, double.NaN })
        {
            Assert.False(model.CanSplitScene(time));
            Assert.Equal(StudioSceneSplitObstacle.TooShort, model.GetSplitSceneObstacle(time));
            Assert.Equal(new StudioSceneEditResult(false, 0), model.SplitScene(time));
        }

        Assert.False(model.CanUndo);

        // Half a second from either is enough.
        Assert.True(model.CanSplitScene(0.5));
        Assert.True(model.CanSplitScene(9.5));
        Assert.Equal(StudioSceneSplitObstacle.None, model.GetSplitSceneObstacle(4));
        Assert.True(model.SplitScene(4).Changed);

        // The new scene is entered over 0.35 s. At 4.32 s both halves would be long enough, and
        // its layers are still moving.
        Assert.Equal(StudioSceneSplitObstacle.StillMoving, model.GetSplitSceneObstacle(4.32));
        Assert.Equal(new StudioSceneEditResult(false, 1), model.SplitScene(4.32));
        Assert.True(model.CanSplitScene(4.5));

        // Too close to the scene after it, and too close to its own start.
        Assert.Equal(StudioSceneSplitObstacle.TooShort, model.GetSplitSceneObstacle(3.75));
        Assert.Equal(StudioSceneSplitObstacle.TooShort, model.GetSplitSceneObstacle(4.25));

        // A scene that is cut to is at rest from its first instant.
        model.SetSceneTransitionKind(1, StudioTransitionKind.Cut);
        Assert.True(model.CanSplitScene(4.32));

        // Without a camera there is nothing to arrange differently.
        var screenOnly = new StudioEditorModel(MakeProject(camera: false));
        Assert.False(screenOnly.CanSplitScene(4));
        Assert.Equal(StudioSceneSplitObstacle.NoCamera, screenOnly.GetSplitSceneObstacle(4));
        Assert.False(screenOnly.SplitScene(4).Changed);
        Assert.Single(screenOnly.Project.Scenes);
    }

    [Fact]
    public void Deleting_HandsASceneTimeToTheSceneBefore()
    {
        var model = new StudioEditorModel(ThreeScenes());
        Assert.Equal(new StudioSceneEditResult(true, 0), model.RemoveScene(1));
        Assert.Equal([0, 7], Starts(model));
        Assert.Equal([StudioLayout.Bubble, StudioLayout.Camera], Layouts(model));
        model.Undo();
        Assert.Equal(3, model.Project.Scenes.Length);

        // The first scene's time goes to the second, which then starts at 0.
        Assert.Equal(new StudioSceneEditResult(true, 0), model.RemoveScene(0));
        Assert.Equal([0, 7], Starts(model));
        Assert.Equal([StudioLayout.SideBySide, StudioLayout.Camera], Layouts(model));

        Assert.Equal(new StudioSceneEditResult(true, 0), model.RemoveScene(1));
        Assert.Equal([StudioLayout.SideBySide], Layouts(model));

        // The only scene stays, and so does everything when there is no such scene.
        Assert.False(model.CanRemoveScene(0));
        Assert.Equal(new StudioSceneEditResult(false, 0), model.RemoveScene(0));
        var three = new StudioEditorModel(ThreeScenes());
        Assert.False(three.CanRemoveScene(3));
        Assert.False(three.CanRemoveScene(-1));
        Assert.Equal(new StudioSceneEditResult(false, 2), three.RemoveScene(3));
        Assert.Equal(new StudioSceneEditResult(false, 0), three.RemoveScene(-1));
        Assert.False(three.CanUndo);
    }

    [Fact]
    public void ASceneStart_StaysClearOfItsNeighbors()
    {
        var model = new StudioEditorModel(ThreeScenes());
        Assert.Equal(new StudioSceneEditResult(true, 1), model.SetSceneStart(1, 5));
        Assert.Equal([0, 5, 7], Starts(model));

        // No closer than 0.3 s to the start of the scene before, or to its own end.
        model.SetSceneStart(1, 0.1);
        Assert.Equal(0.3, model.Project.Scenes[1].Start, Precision);
        model.SetSceneStart(1, 6.9);
        Assert.Equal(6.7, model.Project.Scenes[1].Start, Precision);

        // The last scene ends with the recording, 10 s long.
        model.SetSceneStart(2, 50);
        Assert.Equal(9.7, model.Project.Scenes[2].Start, Precision);
        model.SetSceneStart(2, 0);
        Assert.Equal(7.0, model.Project.Scenes[2].Start, Precision);

        // The first scene starts with the recording; a start that is not a number, the start a
        // scene already has, and a scene that is not there change nothing.
        var before = model.Project.Scenes;
        Assert.False(model.SetSceneStart(0, 2).Changed);
        Assert.False(model.SetSceneStart(1, double.NaN).Changed);
        Assert.False(model.SetSceneStart(2, 7.0).Changed);
        Assert.False(model.SetSceneStart(3, 5).Changed);
        Assert.Same(before, model.Project.Scenes);

        // A drag is one undo step.
        var dragged = new StudioEditorModel(ThreeScenes());
        dragged.BeginEditingGroup();
        dragged.SetSceneStart(1, 4.5);
        dragged.SetSceneStart(1, 5.5);
        dragged.CommitEditingGroup();
        dragged.Undo();
        Assert.Equal([0, 4, 7], Starts(dragged));
        Assert.False(dragged.CanUndo);

        // Scenes from a file that are closer together than the editor makes them stay where they are.
        var close = new StudioEditorModel(MakeProject() with
        {
            Scenes =
            [
                new StudioScene { Start = 0 },
                new StudioScene { Start = 0.2, Layout = StudioLayout.Camera },
                new StudioScene { Start = 0.4, Layout = StudioLayout.Screen },
            ],
        });
        Assert.False(close.SetSceneStart(1, 0.3).Changed);
        Assert.Equal([0, 0.2, 0.4], Starts(close));
    }

    [Fact]
    public void HowASceneIsEntered_CanBeSetForEverySceneButTheFirst()
    {
        var model = new StudioEditorModel(ThreeScenes());
        Assert.Equal(StudioTransitionKind.Cut, model.Project.Scenes[1].Transition.Kind);
        Assert.Equal(0, model.GetSceneTransitionLength(1));

        Assert.Equal(new StudioSceneEditResult(true, 1), model.SetSceneTransitionKind(1, StudioTransitionKind.Morph));
        Assert.Equal(0.35, model.GetSceneTransitionLength(1), Precision);
        Assert.False(model.SetSceneTransitionKind(1, StudioTransitionKind.Morph).Changed);

        // Between 0.1 and 2 seconds.
        model.SetSceneTransitionDuration(1, 1.5);
        Assert.Equal(1.5, model.Project.Scenes[1].Transition.Duration, Precision);
        model.SetSceneTransitionDuration(1, 9);
        Assert.Equal(2, model.Project.Scenes[1].Transition.Duration, Precision);
        model.SetSceneTransitionDuration(1, 0);
        Assert.Equal(0.1, model.Project.Scenes[1].Transition.Duration, Precision);
        Assert.False(model.SetSceneTransitionDuration(1, double.NaN).Changed);
        Assert.Equal(0.1, model.Project.Scenes[1].Transition.Duration, Precision);

        // The first scene has nothing to move from, and a scene that is not there cannot be changed.
        var before = model.Project.Scenes;
        Assert.Equal(new StudioSceneEditResult(false, 0), model.SetSceneTransitionKind(0, StudioTransitionKind.Morph));
        Assert.False(model.SetSceneTransitionDuration(0, 1).Changed);
        Assert.Equal(new StudioSceneEditResult(false, 2), model.SetSceneTransitionKind(5, StudioTransitionKind.Morph));
        Assert.Same(before, model.Project.Scenes);
        Assert.Equal(0, model.GetSceneTransitionLength(0));

        // A move is never longer than its scene. The second scene lasts from 4 s to 7 s; with the
        // third brought to 5 s it lasts one second, and a move of 2 s takes that one second.
        model.SetSceneTransitionDuration(1, 2);
        Assert.Null(StudioEditorText.GetSceneMoveLimitedText(model, 1));
        model.SetSceneStart(2, 5);
        Assert.Equal(1, model.GetSceneTransitionLength(1), Precision);
        Assert.Equal("The scene is shorter than that, so the move takes 1.00 seconds.", StudioEditorText.GetSceneMoveLimitedText(model, 1));
        Assert.Null(StudioEditorText.GetSceneMoveLimitedText(model, 2));
        Assert.Null(StudioEditorText.GetSceneMoveLimitedText(model, 0));
        Assert.Null(StudioEditorText.GetSceneMoveLimitedText(model, 3));
    }

    [Fact]
    public void AScene_IsLookedAtWhereItHasBeenEntered()
    {
        var model = new StudioEditorModel(ThreeScenes());
        model.SetSceneTransitionKind(1, StudioTransitionKind.Morph);

        // The first scene and a scene that is cut to are whole from their first instant. A scene
        // that is moved into is whole when the move ends, 0.35 s after it starts.
        Assert.Equal(0, model.GetSceneLookTime(0));
        Assert.Equal(4.35, model.GetSceneLookTime(1)!.Value, Precision);
        Assert.Equal(7, model.GetSceneLookTime(2));
        Assert.Null(model.GetSceneLookTime(3));

        // A move that takes the whole scene: the last frame of the scene, at 30 frames a second.
        model.SetSceneTransitionDuration(1, 2);
        model.SetSceneStart(2, 4.5);
        Assert.Equal(4.5 - (1.0 / 30), model.GetSceneLookTime(1)!.Value, Precision);

        Assert.Equal((0, 4), model.GetSceneRange(0));
        Assert.Equal((4.5, 10), model.GetSceneRange(2));
        Assert.Null(model.GetSceneRange(3));
        Assert.Null(model.GetSceneRange(-1));
    }

    [Fact]
    public void SceneText_NamesTheLayoutAndTheTimes()
    {
        var model = new StudioEditorModel(ThreeScenes());
        Assert.Equal("Scene 1 of 3, Screen with camera bubble, 0.0 to 4.0 seconds", StudioEditorText.GetSceneDescription(model, 0));
        Assert.Equal("Scene 2 of 3, Side by side, 4.0 to 7.0 seconds", StudioEditorText.GetSceneDescription(model, 1));
        Assert.Equal("Scene 3 of 3, Camera only, 7.0 to 10.0 seconds", StudioEditorText.GetSceneDescription(model, 2));
        Assert.Equal(string.Empty, StudioEditorText.GetSceneDescription(model, 3));
        Assert.Equal("Scene 2 of 3", StudioEditorText.GetScenePositionText(1, 3));
        Assert.Equal("4.0 to 7.0 seconds", StudioEditorText.GetSceneRangeText(model, 1));
        Assert.Equal(string.Empty, StudioEditorText.GetSceneRangeText(model, 3));

        // Without a camera every scene shows the screen alone, whatever layout it stores.
        var screenOnly = new StudioEditorModel(ThreeScenes() with { Sources = MakeProject(camera: false).Sources });
        Assert.Equal("Scene 2 of 3, Screen only, 4.0 to 7.0 seconds", StudioEditorText.GetSceneDescription(screenOnly, 1));

        // Why a scene cannot be split, in words.
        Assert.Null(StudioEditorText.GetSplitSceneExplanation(StudioSceneSplitObstacle.None));
        Assert.Equal(StudioEditorText.NoCameraForScenesExplanation, StudioEditorText.GetSplitSceneExplanation(StudioSceneSplitObstacle.NoCamera));
        Assert.Equal(StudioEditorText.SceneTooShortToSplitExplanation, StudioEditorText.GetSplitSceneExplanation(StudioSceneSplitObstacle.TooShort));
        Assert.Equal(StudioEditorText.SceneStillMovingExplanation, StudioEditorText.GetSplitSceneExplanation(StudioSceneSplitObstacle.StillMoving));
    }

    [Fact]
    public void TheBubbleHandle_IsWhereTheCurrentSceneHasTheBubbleAtRest()
    {
        // On a 1000×800 canvas the gap is 24 px. The first scene's bubble is 0.24 of 800, 192 px,
        // in the top-left corner. The second's is 0.4 of 800, 320 px, in the bottom-right corner,
        // and is moved into over 2 s.
        var project = MakeProject() with
        {
            Scenes =
            [
                new StudioScene { Start = 0, Layout = StudioLayout.Bubble, Bubble = new StudioBubble { Anchor = StudioAnchor.TopLeft, Size = 0.24 } },
                new StudioScene
                {
                    Start = 4,
                    Layout = StudioLayout.Bubble,
                    Bubble = new StudioBubble { Anchor = StudioAnchor.BottomRight, Size = 0.4 },
                    Transition = new StudioTransition { Kind = StudioTransitionKind.Morph, Duration = 2 },
                },
            ],
        };
        var model = new StudioEditorModel(project);
        var first = model.Project.Scenes[0];

        model.SceneTime = 1;
        AssertRect(model.GetBubbleRect(1000, 800), 24, 24, 192, 192);

        // A second into the move the picture has the bubble on its way; the handle is where the
        // scene has it once it is there, because that is what dragging changes.
        model.SceneTime = 5;
        AssertRect(model.GetBubbleRect(1000, 800), 656, 456, 320, 320);

        model.MoveBubbleTopLeft(100, 456, 1000, 800);
        AssertRect(model.GetBubbleRect(1000, 800), 100, 456, 320, 320);
        Assert.Equal(StudioAnchor.BottomLeft, model.Project.Scenes[1].Bubble.Anchor);
        Assert.Same(first, model.Project.Scenes[0]);
    }

    private static void AssertRect(StudioFrameRect? rect, double x, double y, double width, double height)
    {
        Assert.NotNull(rect);
        Assert.Equal(x, rect.Value.X, Precision);
        Assert.Equal(y, rect.Value.Y, Precision);
        Assert.Equal(width, rect.Value.Width, Precision);
        Assert.Equal(height, rect.Value.Height, Precision);
    }

    private static double[] Starts(StudioEditorModel model) => [.. model.Project.Scenes.Select(scene => scene.Start)];

    private static StudioLayout[] Layouts(StudioEditorModel model) => [.. model.Project.Scenes.Select(scene => scene.Layout)];

    /// <summary>The bubble until 4 s, side by side until 7 s, then the camera alone, in a recording 10 s long.</summary>
    private static StudioProject ThreeScenes() => MakeProject() with
    {
        Scenes =
        [
            new StudioScene { Start = 0, Layout = StudioLayout.Bubble },
            new StudioScene { Start = 4, Layout = StudioLayout.SideBySide },
            new StudioScene { Start = 7, Layout = StudioLayout.Camera },
        ],
    };

    private static StudioProject MakeProject(bool camera = true, double duration = 10) => new()
    {
        Id = "3f0013cf-ba10-4453-af91-792b7882dae6",
        Name = "Test",
        Sources = new StudioSources
        {
            Screen = new StudioScreenSource { Width = 1920, Height = 1080, FrameRate = 30, Duration = duration },
            Camera = camera ? new StudioCameraSource { Width = 1280, Height = 720, Duration = duration } : null,
        },
        Scenes = [new StudioScene { Start = 0, Layout = camera ? StudioLayout.Bubble : StudioLayout.Screen }],
    };
}
