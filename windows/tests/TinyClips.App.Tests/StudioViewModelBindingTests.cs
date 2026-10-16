using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Tests;

/// <summary>
/// The Studio editor's view model with the window's controls bound to it. A control that is
/// bound both ways hands back what it has just been told to show, and none of that may be an
/// edit: not when a project opens, not when something is selected, not in the middle of an
/// Undo. What a control is asked for by the user has to reach the project all the same.
/// </summary>
/// <remarks>
/// The controls are stand-ins (<see cref="StudioBoundControls"/>), bound to the values the markup
/// binds. So these say what the view model does with what a control hands it, and not what a
/// control hands it: that is read from the controls' code and from the framework's.
/// </remarks>
public sealed class StudioViewModelBindingTests : StudioViewModelTestBase
{
    // A crop as an editor stores it: 6% off the left and 3% off the top, 54% wide and 52% high,
    // which is 40% off the right and 45% off the bottom. And a zoom that looks at a point inside
    // it which does not survive being worked into a place in the crop and back: 0.57 comes back
    // as 0.5700000000000001 from this crop, and from the crop with its left edge at 7% as well.
    private const double CropLeft = 0.06;
    private const double CropTop = 0.03;
    private const double CropWidth = 0.54;
    private const double CropHeight = 0.52;
    private const double ZoomX = 0.57;
    private const double ZoomY = 0.45;

    private static readonly TimeSpan LongerThanAutosave = StudioEditorSession.AutosaveDelay * 3;

    private readonly StudioBoundControls _controls = new();

    // ---- The markup

    [Fact]
    public void EveryValueTheMarkupBindsBothWays_IsFound_AndTheViewModelHasIt()
    {
        var bindings = _controls.Bindings;

        // Every markup file of the Studio window and of its controls was read, and not these two only.
        Assert.Contains("StudioInspector.xaml", StudioBoundControls.MarkupFiles);
        Assert.Contains("StudioWindow.xaml", StudioBoundControls.MarkupFiles);
        Assert.Contains("StudioTimeline.xaml", StudioBoundControls.MarkupFiles);
        Assert.Contains("StudioSliderRow.xaml", StudioBoundControls.MarkupFiles);

        // Nothing that says Mode=TwoWay is written in a way the reader of the markup misses, and
        // no file makes a binding two-way without saying so.
        Assert.Equal(StudioBoundControls.CountTwoWayInMarkup(), bindings.Count);
        Assert.Empty(StudioBoundControls.FilesWithADefaultBindMode());

        var names = bindings.Select(binding => binding.Property).ToArray();
        Assert.Contains(nameof(StudioViewModel.ZoomFocusX), names);
        Assert.Contains(nameof(StudioViewModel.ScreenCropLeft), names);
        Assert.Contains(nameof(StudioViewModel.CameraBubbleSize), names);
        Assert.Contains(nameof(StudioViewModel.LayoutIndex), names);
        Assert.Contains(nameof(StudioViewModel.IsMuted), names);
        Assert.Contains(nameof(StudioViewModel.Volume), names);
        Assert.Contains(nameof(StudioViewModel.CanvasAspectIndex), names);
        Assert.Contains(nameof(StudioViewModel.HasError), names);

        // A slider says in the markup how far it goes, which is what the tests below move it by.
        Assert.All(
            bindings.Where(binding => binding.Control == "StudioSliderRow"),
            binding => Assert.True(binding.Minimum < binding.Maximum, $"{binding} has no range in the markup"));
    }

    // ---- Opening

    [Theory]
    [InlineData("without a camera")]
    [InlineData("as recorded")]
    [InlineData("edited")]
    [InlineData("out of range")]
    public async Task OpeningAProject_IsNoEdit(string kind)
    {
        var id = kind switch
        {
            "without a camera" => CreateProject(camera: false),
            "as recorded" => CreateProject(),
            "edited" => CreateEditedProject(),
            _ => CreateProjectOutOfRange(),
        };

        var viewModel = await OpenAsync(id, _controls);

        // Each control was shown its value, twice: while the project was loading, and when it
        // was there. What they handed back changed nothing.
        Assert.NotEmpty(_controls.HandedBack);
        AssertNothingWasEdited(viewModel, "opening");
        Assert.Empty(_controls.OutOfStep());

        // Not later either, when the editor would save.
        Advance(LongerThanAutosave);
        Assert.Equal(0, Saves);
        await CloseAsync(viewModel);
        Assert.Equal(0, Saves);
        Assert.Equal(0, KeepWrites);
    }

    [Fact]
    public async Task AProjectThatCannotBeShown_TakesWhatTheControlsHandBack()
    {
        // The screen recording is gone. The window opens all the same, with its controls
        // bound, and says that the project can't be opened.
        var id = CreateEditedProject();
        File.Delete(Projects.GetPaths(id).ScreenPath);

        var viewModel = await OpenAsync(id, _controls, expectReady: false);
        Assert.True(viewModel.IsUnavailable);
        _controls.HandBackEverything();
        Pump();

        Assert.False(viewModel.HasError, viewModel.ErrorMessage);
        Assert.False(viewModel.CanUndo);
        Advance(LongerThanAutosave);
        await CloseAsync(viewModel);
        Assert.Equal(0, Saves);
        Assert.Equal(0, KeepWrites);
    }

    [Fact]
    public async Task AnEditorThatHasClosed_TakesWhatAControlStillWrites()
    {
        var id = CreateEditedProject();
        var viewModel = await OpenAsync(id, _controls);
        viewModel.SelectZoom(0);
        await CloseAsync(viewModel);

        // The window is going, and its controls with it. What one of them still writes, a
        // value handed back or a slider that was being moved, is too late to be an edit.
        _controls.HandBackEverything();
        _controls.Move(nameof(StudioViewModel.CanvasPadding), 0.3);
        _controls.Move(nameof(StudioViewModel.ZoomScale), 4d);
        _controls.Move(nameof(StudioViewModel.KeepsProject), true);
        Pump();
        Advance(LongerThanAutosave);

        Assert.False(viewModel.HasError, viewModel.ErrorMessage);
        Assert.Equal(0, Saves);
        Assert.Equal(0, KeepWrites);
        var stored = Projects.Load(id);
        Assert.Equal(0.06, stored.Canvas.Padding, Precision);
        Assert.Equal(2, stored.Zooms[0].Scale, Precision);
        Assert.False(stored.KeepSources);
    }

    [Fact]
    public async Task AValueItsSliderCannotShow_StaysInTheProject_UntilTheSliderIsMoved()
    {
        var id = CreateProjectOutOfRange();
        var viewModel = await OpenAsync(id, _controls);

        // The controls of a zoom and of a scene show the selected zoom and the scene the
        // playhead is in. Each is shown what it cannot show.
        viewModel.ShowScene(1);
        viewModel.SelectZoom(0);
        Pump();
        Assert.Equal(1, viewModel.CurrentSceneIndex);
        Assert.Equal(0, viewModel.SelectedZoomIndex);
        _controls.HandBackEverything();
        Pump();
        AssertNothingWasEdited(viewModel, "showing the second scene and selecting the zoom");

        // The one slider that is moved writes its own value, and no other.
        _controls.Move(nameof(StudioViewModel.CanvasPadding), 0.3);
        Pump();
        Assert.Single(Preview.Updates);
        await CloseAsync(viewModel);

        var stored = Projects.Load(id);
        Assert.Equal(0.3, stored.Canvas.Padding, Precision);
        Assert.Equal("ink", stored.Canvas.Background.Preset);
        Assert.Equal(0.5, stored.Screen.CornerRadius, Precision);
        Assert.Equal(1.7, stored.Screen.Shadow, Precision);
        Assert.Equal(0.9, stored.Camera.CornerRadius, Precision);
        Assert.Equal(0.5, stored.Camera.BorderWidth, Precision);
        Assert.Equal(-0.4, stored.Camera.Shadow, Precision);
        Assert.Equal(0.9, stored.Scenes[1].Bubble.Size, Precision);
        Assert.Equal(1.6, stored.Scenes[1].Bubble.OffsetX, Precision);
        Assert.Equal(-1.6, stored.Scenes[1].Bubble.OffsetY, Precision);
        Assert.Equal(0.9, stored.Scenes[1].Split.CameraFraction, Precision);
        Assert.Equal(5, stored.Scenes[1].Transition.Duration, Precision);
        Assert.Equal(7, stored.Zooms[0].Scale, Precision);
        Assert.Equal(1.4, stored.Zooms[0].Focus.X, Precision);
        Assert.Equal(-0.3, stored.Zooms[0].Focus.Y, Precision);
        Assert.Equal(9, stored.Zooms[0].EaseIn, Precision);
        Assert.Equal(9, stored.Zooms[0].EaseOut, Precision);
        Assert.Equal(3, stored.Audio.Volume, Precision);
    }

    // ---- The volume

    [Fact]
    public async Task TheVolume_IsShownAsItIsUsed_ADragIsOneStep_AndMuteSwitchesItOffAndLeavesItsValue()
    {
        var id = CreateProject(project => project with { Audio = project.Audio with { Volume = 3 } });
        var viewModel = await OpenAsync(id, _controls);

        // Out of range in the file: the slider shows what is heard, and that is no edit.
        Assert.Equal(1, viewModel.Volume, Precision);
        Assert.Equal("100%", viewModel.VolumeText);
        Assert.True(viewModel.IsVolumeEnabled);
        AssertNothingWasEdited(viewModel, "opening");

        // A drag is one step, however many values it passes.
        viewModel.BeginGesture();
        _controls.Move(nameof(StudioViewModel.Volume), 0.8);
        _controls.Move(nameof(StudioViewModel.Volume), 0.35);
        viewModel.EndGesture();
        Pump();
        Assert.Equal(0.35, viewModel.Volume, Precision);
        Assert.Equal("35%", viewModel.VolumeText);
        Assert.Equal(0.35, LastProject().Audio.Volume, Precision);

        _controls.Move(nameof(StudioViewModel.IsMuted), true);
        Pump();
        Assert.False(viewModel.IsVolumeEnabled);
        Assert.Equal(0.35, viewModel.Volume, Precision);
        Assert.Equal("35%", viewModel.VolumeText);
        Assert.Equal(0.35, LastProject().Audio.Volume, Precision);

        // Two steps back: the mute, and the whole drag.
        viewModel.Undo();
        Pump();
        Assert.True(viewModel.IsVolumeEnabled);
        Assert.Equal(0.35, viewModel.Volume, Precision);
        viewModel.Undo();
        Pump();
        Assert.Equal(1, viewModel.Volume, Precision);
        Assert.Equal(3, LastProject().Audio.Volume, Precision);
        Assert.False(viewModel.CanUndo);
        Assert.Empty(_controls.OutOfStep());
    }

    // ---- Selecting, stepping and playing

    [Fact]
    public async Task SelectingSteppingPlayingAndShowingPanels_HandNothingBackThatIsAnEdit()
    {
        var viewModel = await OpenAsync(CreateEditedProject(), _controls);
        (string What, Action Do)[] steps =
        [
            ("selecting the first zoom", () => viewModel.SelectZoom(0)),

            // The second zoom looks at a point below the crop, so its sliders show the nearest
            // place inside it. Handed back, that place is another point than the zoom stores.
            ("selecting the second zoom", () => viewModel.SelectZoom(1)),
            ("selecting the cut", () => viewModel.SelectCut(0)),
            ("selecting the speed change", () => viewModel.SelectSpeed(0)),
            ("selecting nothing", viewModel.SelectNothing),
            ("showing the second scene", () => viewModel.ShowScene(1)),
            ("showing the third scene", () => viewModel.ShowScene(2)),
            ("showing the first scene", () => viewModel.ShowScene(0)),
            ("going to the second zoom", () => viewModel.SelectAndShowZoom(1)),
            ("showing each panel", () =>
            {
                foreach (var panel in Enum.GetValues<StudioInspectorPanel>())
                {
                    viewModel.ShowInspectorPanel(panel);
                }
            }),
            ("opening and closing the crop groups", () =>
            {
                viewModel.SetScreenCropOpen(false);
                viewModel.SetCameraCropOpen(true);
                viewModel.SetScreenCropOpen(true);
                viewModel.SetCameraCropOpen(false);
            }),
            ("playing through all three scenes", () =>
            {
                viewModel.Scrub(3.5);
                viewModel.TogglePlayback();
                Preview.RaisePosition(4.2);
                Pump();
                Preview.RaisePosition(7.3);
                Pump();
                viewModel.Pause();
            }),
        ];

        foreach (var (what, step) in steps)
        {
            step();
            Pump();
            _controls.HandBackEverything();
            Pump();

            AssertNothingWasEdited(viewModel, what);
            Assert.True(viewModel.HasSuggestedZooms, $"after {what} the suggested zoom is still a suggestion");
            Assert.Empty(_controls.OutOfStep());
        }

        Advance(LongerThanAutosave);
        Assert.Equal(0, Saves);
        Assert.Equal(0, KeepWrites);
    }

    // ---- What was wrong on 6 October: a slider of the selected zoom handing its value back

    [Fact]
    public async Task ACropEdgeMovedWhileAZoomIsSelected_StaysInTheScreenPanel_AndIsOneUndoStep()
    {
        // What makes this a test: where the zoom looks, worked into a place in either crop and
        // back, is not the number the zoom stores. Passed on, it is an edit of the zoom.
        Assert.NotEqual(ZoomX, ThroughTheCrop(ZoomX, CropLeft, CropWidth));
        Assert.NotEqual(ZoomX, ThroughTheCrop(ZoomX, 0.07, 0.53));

        var viewModel = await OpenAsync(CreateEditedProject(), _controls);
        viewModel.SelectZoom(0);
        Assert.Equal(StudioInspectorPanel.Zoom, viewModel.InspectorPanel);
        viewModel.ShowInspectorPanel(StudioInspectorPanel.Screen);
        viewModel.SetScreenCropOpen(true);
        var zooms = viewModel.Zooms;
        var place = viewModel.ZoomFocusX;

        _controls.Move(nameof(StudioViewModel.ScreenCropLeft), 0.07);
        Pump();

        // The zoom's sliders were given a new place to show, and handed it back.
        Assert.NotEqual(place, viewModel.ZoomFocusX);
        Assert.Contains(_controls.HandedBack, handed => handed.Property == nameof(StudioViewModel.ZoomFocusX) && Equals(handed.Value, viewModel.ZoomFocusX));
        Assert.Equal(StudioInspectorPanel.Screen, viewModel.InspectorPanel);
        Assert.True(viewModel.IsScreenCropOpen);
        Assert.Equal(0.07, viewModel.ScreenCropLeft, Precision);
        Assert.Single(Preview.Updates);
        Assert.Same(zooms, viewModel.Zooms);
        Assert.Equal(0, viewModel.SelectedZoomIndex);

        // One step back is the whole of it, and it leaves the step forward.
        viewModel.Undo();
        Pump();
        Assert.Equal(CropLeft, viewModel.ScreenCropLeft, Precision);
        Assert.Equal(place, viewModel.ZoomFocusX);
        Assert.False(viewModel.CanUndo);
        Assert.True(viewModel.CanRedo);
        Assert.Equal(StudioInspectorPanel.Screen, viewModel.InspectorPanel);
        Assert.Same(zooms, viewModel.Zooms);

        viewModel.Redo();
        Pump();
        Assert.Equal(0.07, viewModel.ScreenCropLeft, Precision);
        Assert.False(viewModel.CanRedo);
        Assert.Equal(StudioInspectorPanel.Screen, viewModel.InspectorPanel);
        Assert.Same(zooms, viewModel.Zooms);
        Assert.Empty(_controls.OutOfStep());
    }

    [Fact]
    public async Task UndoAndRedoOfTheSelectedZoom_LeaveThePanelThatIsOnShow_AndEachOther()
    {
        var viewModel = await OpenAsync(CreateEditedProject(), _controls);
        viewModel.SelectZoom(0);
        _controls.Move(nameof(StudioViewModel.ZoomScale), 3d);
        Pump();
        Assert.Single(Preview.Updates);
        viewModel.ShowInspectorPanel(StudioInspectorPanel.Background);

        viewModel.Undo();
        Pump();
        Assert.Equal(2, viewModel.ZoomScale, Precision);
        Assert.Equal(StudioInspectorPanel.Background, viewModel.InspectorPanel);
        Assert.False(viewModel.CanUndo);
        Assert.True(viewModel.CanRedo);

        viewModel.Redo();
        Pump();
        Assert.Equal(3, viewModel.ZoomScale, Precision);
        Assert.Equal(StudioInspectorPanel.Background, viewModel.InspectorPanel);
        Assert.True(viewModel.CanUndo);
        Assert.False(viewModel.CanRedo);
        Assert.Equal(3, Preview.Updates.Count);
        Assert.Empty(_controls.OutOfStep());
    }

    // ---- One thing done, one step to undo

    [Fact]
    public async Task WhatIsDone_IsOneStepEach_AndUndoAndRedoGoThroughThemAll()
    {
        var viewModel = await OpenAsync(CreateEditedProject(), _controls);
        (string What, Action Do)[] steps =
        [
            ("moving the padding", () => _controls.Move(nameof(StudioViewModel.CanvasPadding), 0.2)),
            ("adding a zoom with Z", () =>
            {
                viewModel.Scrub(3.3);
                viewModel.Run(StudioShortcutAction.AddZoom);
            }),
            ("moving the new zoom's level", () =>
            {
                viewModel.SelectZoom(1);
                _controls.Move(nameof(StudioViewModel.ZoomScale), 4d);
            }),
            ("moving where the new zoom looks", () =>
            {
                viewModel.SelectZoom(1);
                _controls.Move(nameof(StudioViewModel.ZoomFocusX), 0.25);
            }),
            ("moving the top of the crop, with the zoom selected", () =>
            {
                viewModel.SelectZoom(1);
                _controls.Move(nameof(StudioViewModel.ScreenCropTop), 0.1);
            }),
            ("adding a cut with X", () =>
            {
                viewModel.Scrub(6.8);
                viewModel.Run(StudioShortcutAction.AddCut);
            }),
            ("adding a speed change with R", () =>
            {
                viewModel.Scrub(0.2);
                viewModel.Run(StudioShortcutAction.AddSpeed);
            }),
            ("splitting the scene with S", () =>
            {
                viewModel.Scrub(2);
                viewModel.Run(StudioShortcutAction.SplitScene);
            }),
            ("the side by side layout with 3", () =>
            {
                viewModel.Scrub(2);
                viewModel.Run(StudioShortcutAction.ShowSideBySideLayout);
            }),
            ("moving the camera's share", () =>
            {
                viewModel.Scrub(2);
                _controls.Move(nameof(StudioViewModel.CameraShare), 0.5);
            }),
            ("deleting the first zoom", () =>
            {
                viewModel.SelectZoom(0);
                viewModel.Run(StudioShortcutAction.RemoveSelectedZoom);
            }),
            ("muting", () => _controls.Move(nameof(StudioViewModel.IsMuted), true)),
            ("moving the volume", () => _controls.Move(nameof(StudioViewModel.Volume), 0.35)),
            ("resetting the crop", viewModel.ResetScreenCrop),
        ];

        // Each is one change to the project, with every control handing back what it is then
        // shown. And each is one step to undo: one step back gives the project as it was
        // before, and one step forward the project as it was after.
        var opened = LastProject();
        foreach (var (what, step) in steps)
        {
            var before = LastProject();
            var count = Preview.Updates.Count;
            step();
            Pump();
            Assert.True(
                Preview.Updates.Count == count + 1,
                $"{what}: the project was changed {Preview.Updates.Count - count} times. Handed back last: {string.Join(", ", _controls.HandedBack.TakeLast(4))}");
            var after = LastProject();
            Assert.False(SameContent(before, after), $"{what} changed nothing");

            viewModel.Undo();
            Pump();
            Assert.True(
                Preview.Updates.Count == count + 2 && SameContent(before, LastProject()),
                $"{what}: one step back made {Preview.Updates.Count - count - 1} changes, and the project is {(SameContent(before, LastProject()) ? "as" : "not as")} it was before");
            viewModel.Redo();
            Pump();
            Assert.True(
                Preview.Updates.Count == count + 3 && SameContent(after, LastProject()),
                $"{what}: one step forward made {Preview.Updates.Count - count - 2} changes, and the project is {(SameContent(after, LastProject()) ? "as" : "not as")} it was after");
            Assert.Empty(_controls.OutOfStep());
        }

        // As many steps back as things were done, and then there is none and the project is as it was opened.
        var edited = LastProject();
        for (var undone = 0; undone < steps.Length; undone++)
        {
            Assert.True(viewModel.CanUndo, $"after {undone} of {steps.Length} steps back there is nothing left to undo");
            viewModel.Undo();
            Pump();
        }

        Assert.False(viewModel.CanUndo, $"after {steps.Length} steps back there is still something to undo");
        Assert.True(SameContent(opened, LastProject()), "after every step back the project is not as it was opened");
        Assert.True(viewModel.HasSuggestedZooms);

        // No step back took a step forward away.
        for (var redone = 0; redone < steps.Length; redone++)
        {
            Assert.True(viewModel.CanRedo, $"after {redone} of {steps.Length} steps forward there is nothing left to redo");
            viewModel.Redo();
            Pump();
        }

        Assert.False(viewModel.CanRedo);
        Assert.True(SameContent(edited, LastProject()), "after every step forward the project is not as it was edited");
        Assert.Equal(5 * steps.Length, Preview.Updates.Count);
        Assert.Empty(_controls.OutOfStep());
    }

    // ---- A drag, while the recording plays into the next scene

    [Theory]
    [InlineData(nameof(StudioViewModel.LayoutIndex))]
    [InlineData(nameof(StudioViewModel.CameraBubbleSize))]
    [InlineData(nameof(StudioViewModel.CameraAnchorIndex))]
    [InlineData(nameof(StudioViewModel.CameraOffsetX))]
    [InlineData(nameof(StudioViewModel.CameraOffsetY))]
    [InlineData(nameof(StudioViewModel.CameraSideIndex))]
    [InlineData(nameof(StudioViewModel.CameraShare))]
    [InlineData(nameof(StudioViewModel.SceneEntryIndex))]
    [InlineData(nameof(StudioViewModel.SceneMoveDuration))]
    public async Task WithSomethingHeld_PlayingIntoASceneWithAnotherValue_GoesOnPlaying(string property)
    {
        // The second scene differs from the first in the one value.
        var second = new StudioScene { Start = 4 };
        second = property switch
        {
            nameof(StudioViewModel.SceneEntryIndex) => second with { Transition = second.Transition with { Kind = StudioTransitionKind.Morph } },
            nameof(StudioViewModel.SceneMoveDuration) => second with { Transition = second.Transition with { Duration = 0.8 } },
            nameof(StudioViewModel.LayoutIndex) => second with { Layout = StudioLayout.SideBySide },
            nameof(StudioViewModel.CameraBubbleSize) => second with { Bubble = second.Bubble with { Size = 0.4 } },
            nameof(StudioViewModel.CameraAnchorIndex) => second with { Bubble = second.Bubble with { Anchor = StudioAnchor.TopLeft } },
            nameof(StudioViewModel.CameraOffsetX) => second with { Bubble = second.Bubble with { OffsetX = 0.2 } },
            nameof(StudioViewModel.CameraOffsetY) => second with { Bubble = second.Bubble with { OffsetY = 0.2 } },
            nameof(StudioViewModel.CameraSideIndex) => second with { Split = second.Split with { CameraSide = StudioCameraSide.Leading } },
            _ => second with { Split = second.Split with { CameraFraction = 0.45 } },
        };
        var viewModel = await OpenAsync(CreateProject(project => project with { Scenes = [new StudioScene { Start = 0 }, second] }), _controls);
        viewModel.Scrub(3.5);
        viewModel.TogglePlayback();
        Preview.RaisePosition(3.8);
        Pump();
        Assert.True(viewModel.IsPlaying);

        // A pointer goes down on something that is not the scene's: a slider of another panel,
        // a block on a lane. Then the playhead comes into the second scene.
        viewModel.BeginGesture();
        _controls.HandedBack.Clear();
        Preview.RaisePosition(4.2);
        Pump();

        // The scene's control was shown the second scene's value and handed it back. That is no
        // change to a scene, and only a change to a scene stops playback in the middle of a drag.
        Assert.Equal(1, viewModel.CurrentSceneIndex);
        Assert.Contains(_controls.HandedBack, handed => handed.Property == property);
        Assert.True(viewModel.IsPlaying);
        AssertNothingWasEdited(viewModel, "playing into the second scene");
        viewModel.EndGesture();
    }

    [Fact]
    public async Task ACameraDraggedTowardsAnotherCorner_StaysWhereItWasDropped()
    {
        var viewModel = await OpenAsync(CreateProject(), _controls);
        Assert.Equal((int)StudioAnchor.BottomRight, viewModel.CameraAnchorIndex);

        // Towards the top left, and not into the corner itself: the camera is held to that
        // corner from here on, with a distance from it.
        viewModel.BeginGesture();
        viewModel.MoveBubbleTopLeft(300, 200);
        viewModel.EndGesture();
        Pump();

        // The list of corners was shown the new corner and handed it back. Choosing a corner
        // from that list puts the camera into it; a corner handed back must not.
        Assert.Equal((int)StudioAnchor.TopLeft, viewModel.CameraAnchorIndex);
        Assert.Contains(_controls.HandedBack, handed => handed.Property == nameof(StudioViewModel.CameraAnchorIndex) && Equals(handed.Value, (int)StudioAnchor.TopLeft));
        Assert.True(viewModel.BubbleRect is { } bubble && Math.Abs(bubble.X - 300) < 0.5 && Math.Abs(bubble.Y - 200) < 0.5, $"the camera is at {viewModel.BubbleRect}");
        Assert.Single(Preview.Updates);

        viewModel.Undo();
        Pump();
        Assert.Equal((int)StudioAnchor.BottomRight, viewModel.CameraAnchorIndex);
        Assert.False(viewModel.CanUndo);
        Assert.True(viewModel.CanRedo);
    }

    // ---- What a control is asked for

    [Fact]
    public async Task WhatTheUserAsksOfAControl_ReachesTheProject_AsOneEdit()
    {
        var viewModel = await OpenAsync(CreateEditedProject(), _controls);
        foreach (var binding in _controls.Bindings)
        {
            if (binding.Property == nameof(StudioViewModel.HasError))
            {
                // The message bar's close button: it has its own test below.
                continue;
            }

            Select(viewModel, binding.Property);
            Pump();
            var before = Preview.Updates.Count;
            switch (_controls.Shown(binding))
            {
                case double shown:
                    // A tenth of the slider's way, upwards while there is room.
                    var tenth = (binding.Maximum!.Value - binding.Minimum!.Value) / 10;
                    var asked = Math.Round(shown + tenth <= binding.Maximum.Value ? shown + tenth : shown - tenth, 6);
                    _controls.Move(binding, asked);
                    Pump();
                    Assert.True(
                        _controls.Shown(binding) is double now && Math.Abs(now - asked) < 1e-6,
                        $"{binding} was moved from {shown} to {asked} and is {_controls.Shown(binding)}");
                    break;

                case bool shown:
                    _controls.Move(binding, !shown);
                    Pump();
                    Assert.True(Equals(_controls.Shown(binding), !shown), $"{binding} was switched from {shown} and still is");
                    break;

                case int shown:
                    // Another choice of the list. Not every choice is one the editor makes: a
                    // speed that would leave too little video is refused. One of them is.
                    var taken = false;
                    for (var choice = 0; choice < 8 && !taken; choice++)
                    {
                        if (choice != shown)
                        {
                            _controls.Move(binding, choice);
                            Pump();
                            taken = Equals(_controls.Shown(binding), choice);
                        }
                    }

                    Assert.True(taken, $"{binding} shows {shown}, and no other choice was taken");
                    break;
            }

            // Keeping a project is written at once and is no edit. Everything else is one.
            var edits = binding.Property == nameof(StudioViewModel.KeepsProject) ? 0 : 1;
            Assert.True(
                Preview.Updates.Count == before + edits,
                $"{binding}: {Preview.Updates.Count - before} edits, where {edits} was asked for");
            Assert.Empty(_controls.OutOfStep());
        }
    }

    [Fact]
    public async Task AClickedRadioButton_HandsBackNoChoiceAndThenTheChoice_WhichIsOneEdit()
    {
        var viewModel = await OpenAsync(CreateEditedProject(), _controls);
        var groups = _controls.Bindings.Where(binding => binding.Control == "RadioButtons").ToArray();
        Assert.Equal(5, groups.Length);

        foreach (var group in groups)
        {
            Select(viewModel, group.Property);
            Pump();
            var shown = (int)_controls.Shown(group)!;
            var before = Preview.Updates.Count;
            var taken = false;
            for (var choice = 0; choice < 8 && !taken; choice++)
            {
                if (choice == shown)
                {
                    continue;
                }

                // The button that was chosen is unchecked first, and the group says so through
                // its binding. Then the button that was clicked is checked.
                _controls.Move(group, -1);
                _controls.Move(group, choice);
                Pump();
                taken = Equals(_controls.Shown(group), choice);
                Assert.True(
                    Equals(_controls.Held(group), _controls.Shown(group)),
                    $"{group} holds {_controls.Held(group)} after a click on {choice}, and the view model has {_controls.Shown(group)}");
            }

            Assert.True(taken, $"{group} shows {shown}, and no other choice was taken");
            Assert.True(Preview.Updates.Count == before + 1, $"{group}: a click made {Preview.Updates.Count - before} edits");
            Assert.Empty(_controls.OutOfStep());
        }
    }

    [Fact]
    public async Task ACropEdgeAskedPastWhereItStops_IsPutBackOnItsSlider()
    {
        var viewModel = await OpenAsync(CreateEditedProject(), _controls);
        var left = _controls.Find(nameof(StudioViewModel.ScreenCropLeft));

        // 40% is off the right, and a twentieth of the picture has to stay: the left edge stops at 55%.
        _controls.Move(left, 0.9);
        Pump();
        Assert.Equal(0.55, viewModel.ScreenCropLeft, Precision);
        Assert.Equal(0.55, (double)_controls.Held(left)!, Precision);
        Assert.Single(Preview.Updates);

        // Asked for more where it has stopped, the edge stays, and nothing tells the slider:
        // nothing changed. So it is told a moment later what the edge is.
        _controls.Move(left, 0.9);
        Assert.Equal(0.55, viewModel.ScreenCropLeft, Precision);
        Assert.Equal(0.9, _controls.Held(left));
        Pump();
        Assert.Equal(0.55, (double)_controls.Held(left)!, Precision);
        Assert.Single(Preview.Updates);
        Assert.Empty(_controls.OutOfStep());
    }

    [Fact]
    public async Task TheMessageBarsCloseButton_TakesTheMessageAway()
    {
        var viewModel = await OpenAsync(CreateProject(), _controls);
        viewModel.ShowError("The video could not be saved.");
        Assert.True(viewModel.HasError);
        Assert.Equal(true, _controls.Held(_controls.Find(nameof(StudioViewModel.HasError))));

        _controls.Move(nameof(StudioViewModel.HasError), false);

        Assert.False(viewModel.HasError);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);
        AssertNothingWasEdited(viewModel, "closing the message bar");
    }

    [Fact]
    public async Task AListThatHoldsNoChoiceWhileItIsBuilt_ChangesNothing_AndIsToldItsChoiceAgain()
    {
        var viewModel = await OpenAsync(CreateEditedProject(), _controls);
        viewModel.SelectSpeed(0);
        viewModel.ShowScene(1);
        Pump();
        var lists = _controls.Bindings.Where(binding => _controls.Shown(binding) is int).ToArray();
        Assert.NotEmpty(lists);

        foreach (var list in lists)
        {
            var shown = (int)_controls.Shown(list)!;
            Assert.True(shown >= 0, $"{list} has no choice to show in this project");

            // A list has no choice until its items are there, and says so through its binding.
            _controls.Move(list, -1);
            Assert.Equal(shown, _controls.Shown(list));
            Assert.Equal(-1, _controls.Held(list));

            // A moment later it is told what the choice is.
            Pump();
            Assert.Equal(shown, _controls.Held(list));
        }

        AssertNothingWasEdited(viewModel, "lists being built");
    }

    // ---- Helpers

    /// <summary>Nothing reached the project: the preview was handed no new one, and there is nothing to undo.</summary>
    private void AssertNothingWasEdited(StudioViewModel viewModel, string after)
    {
        Assert.True(Preview.Updates.Count == 0, $"after {after} the project was changed {Preview.Updates.Count} times. Handed back last: {string.Join(", ", _controls.HandedBack.TakeLast(4))}");
        Assert.False(viewModel.CanUndo, $"after {after} there is something to undo");
        Assert.False(viewModel.CanRedo, $"after {after} there is something to redo");
    }

    /// <summary>The project as the editor last handed it to the preview.</summary>
    private StudioProject LastProject() => Preview.Updates.Count > 0 ? Preview.Updates[^1] : Preview.OpenedWith!;

    /// <summary>Whether two projects hold the same of everything that an edit can change.</summary>
    private static bool SameContent(StudioProject one, StudioProject other) =>
        StudioEditableState.From(one).ContentEquals(StudioEditableState.From(other));

    /// <summary>Selects what a control edits: the first zoom or the speed change, and for a scene's controls the second scene.</summary>
    private static void Select(StudioViewModel viewModel, string property)
    {
        if (property.StartsWith("Zoom", StringComparison.Ordinal))
        {
            viewModel.SelectZoom(0);
        }
        else if (property.StartsWith("Speed", StringComparison.Ordinal))
        {
            viewModel.SelectSpeed(0);
        }
        else if (property.StartsWith("Scene", StringComparison.Ordinal) && viewModel.CurrentSceneIndex != 1)
        {
            viewModel.ShowScene(1);
        }
    }

    /// <summary>A point of the screen as a place in a crop, and back, as the editor works it out.</summary>
    private static double ThroughTheCrop(double point, double cropStart, double cropLength) =>
        cropStart + (((point - cropStart) / cropLength) * cropLength);

    /// <summary>
    /// A recording with everything in it that a control can show: a cropped screen, two zooms,
    /// one of them a suggestion that looks below the crop, a cut, a speed change, and three
    /// scenes of which the second differs from the first in all a scene holds.
    /// </summary>
    private string CreateEditedProject() => CreateProject(project => project with
    {
        Screen = project.Screen with
        {
            Crop = new StudioRect(CropLeft, CropTop, CropWidth, CropHeight),
        },
        Scenes =
        [
            new StudioScene { Start = 0, Layout = StudioLayout.Bubble },
            new StudioScene
            {
                Start = 4,
                Layout = StudioLayout.SideBySide,
                Bubble = new StudioBubble { Anchor = StudioAnchor.TopLeft, Size = 0.4, OffsetX = 0.1, OffsetY = 0.05 },
                Split = new StudioSplit { CameraSide = StudioCameraSide.Leading, CameraFraction = 0.45 },
                Transition = new StudioTransition { Kind = StudioTransitionKind.Morph, Duration = 0.8 },
            },
            new StudioScene { Start = 7, Layout = StudioLayout.Camera },
        ],
        Zooms =
        [
            new StudioZoom { Start = 1, End = 3, Scale = 2, Focus = new StudioZoomFocus { X = ZoomX, Y = ZoomY } },
            new StudioZoom { Start = 5, End = 6.5, Scale = 3, Focus = new StudioZoomFocus { X = 0.3, Y = 0.6 }, Origin = StudioZoomOrigin.Auto },
        ],
        Edits = project.Edits with
        {
            Cuts = [new StudioTimeRange { Start = 8.5, End = 9.2 }],
            Speed = [new StudioSpeedRange { Start = 3.2, End = 3.9, Rate = 2 }],
        },
    });

    /// <summary>
    /// A project as no editor writes it: every number that a slider shows is outside what its
    /// slider can show. It is what a project written by hand, or by another version, can hold.
    /// Its background is another one than "Show background" brings back.
    /// </summary>
    private string CreateProjectOutOfRange() => CreateProject(project => project with
    {
        Canvas = project.Canvas with
        {
            Padding = 0.9,
            Background = new StudioBackground { Style = StudioBackgroundStyle.Solid, Preset = "ink", Primary = "#1C1C1E", Secondary = null },
        },
        Screen = project.Screen with { CornerRadius = 0.5, Shadow = 1.7 },
        Camera = project.Camera with { CornerRadius = 0.9, BorderWidth = 0.5, Shadow = -0.4 },
        Scenes =
        [
            new StudioScene { Start = 0 },
            new StudioScene
            {
                Start = 4,
                Bubble = new StudioBubble { Size = 0.9, OffsetX = 1.6, OffsetY = -1.6 },
                Split = new StudioSplit { CameraFraction = 0.9 },
                Transition = new StudioTransition { Kind = StudioTransitionKind.Morph, Duration = 5 },
            },
        ],
        Zooms =
        [
            new StudioZoom { Start = 1, End = 3, Scale = 7, Focus = new StudioZoomFocus { X = 1.4, Y = -0.3 }, EaseIn = 9, EaseOut = 9 },
        ],
        Audio = project.Audio with { Volume = 3 },
    });
}
