using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>
/// The inspector panel on show: what shows a panel, and what leaves it alone. The table these
/// follow is "What shows a panel" in windows/docs/studio-inspector-rail.md. The crop groups of
/// the Screen and the Camera panel are at the end.
/// </summary>
public sealed class StudioEditorSessionInspectorTests : StudioEditorSessionTestBase
{
    private const StudioInspectorPanel Scene = StudioInspectorPanel.Scene;
    private const StudioInspectorPanel Background = StudioInspectorPanel.Background;
    private const StudioInspectorPanel Screen = StudioInspectorPanel.Screen;
    private const StudioInspectorPanel Camera = StudioInspectorPanel.Camera;
    private const StudioInspectorPanel Zoom = StudioInspectorPanel.Zoom;
    private const StudioInspectorPanel Cut = StudioInspectorPanel.Cut;
    private const StudioInspectorPanel Speed = StudioInspectorPanel.Speed;
    private const StudioInspectorPanel Audio = StudioInspectorPanel.Audio;
    private const StudioInspectorPanel Project = StudioInspectorPanel.Project;

    // Opening a project

    [Fact]
    public async Task AProjectOpensOnScene_OrOnBackgroundWithoutACamera()
    {
        var session = CreateSession(CreateProject(camera: true));
        Assert.Equal(Background, session.InspectorPanel);

        await FinishAsync(session.LoadAsync());

        Assert.Equal(Scene, session.InspectorPanel);
        Assert.True(Changes[^1].HasFlag(StudioEditorChanges.Inspector));

        var screenOnly = await OpenAsync(CreateProject());
        Assert.Equal(Background, screenOnly.InspectorPanel);
    }

    // The rail

    [Fact]
    public async Task TheRail_ShowsThePanelThatIsAskedFor_AndSaysSoOnce()
    {
        var session = await OpenAsync(CreateProject(camera: true));

        foreach (var panel in StudioInspectorPanels.GetAvailable(hasCamera: true).Reverse())
        {
            Changes.Clear();

            session.ShowInspectorPanel(panel);

            Assert.Equal(panel, session.InspectorPanel);
            Assert.Equal(new[] { StudioEditorChanges.Inspector }, Changes);

            // The panel that is on show already is nothing new.
            session.ShowInspectorPanel(panel);
            Assert.Equal(new[] { StudioEditorChanges.Inspector }, Changes);
        }
    }

    [Fact]
    public async Task WithoutACamera_ARequestForSceneOrCamera_GoesToBackgroundOrScreen()
    {
        var session = await OpenAsync(CreateProject());
        session.ShowInspectorPanel(Project);

        session.ShowInspectorPanel(Scene);
        Assert.Equal(Background, session.InspectorPanel);

        session.ShowInspectorPanel(Project);
        session.ShowInspectorPanel(Camera);
        Assert.Equal(Screen, session.InspectorPanel);

        // The S key asks for Scene as well, where no scene can be split.
        session.ShowInspectorPanel(Project);
        session.Scrub(4);
        Assert.False(session.SplitSceneAtPlayhead().Changed);
        Assert.Equal(Background, session.InspectorPanel);

        // The other panels are there as they are.
        foreach (var panel in new[] { Screen, Zoom, Cut, Speed, Audio, Project, Background })
        {
            session.ShowInspectorPanel(panel);
            Assert.Equal(panel, session.InspectorPanel);
        }
    }

    [Fact]
    public async Task ShowingAPanel_IsNotAnEdit()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        var before = session.Model!.EditableState;
        Preview.Calls.Clear();
        Log.Clear();

        session.ShowInspectorPanel(Zoom);
        session.ShowInspectorPanel(Project);
        Advance(StudioEditorSession.AutosaveDelay.TotalMilliseconds * 2);

        Assert.True(before.ContentEquals(session.Model.EditableState));
        Assert.False(session.HasUnsavedEdits);
        Assert.False(session.CanUndo);
        Assert.Empty(Preview.Calls);
        Assert.Empty(Log);
        Assert.Empty(Errors);
    }

    // What shows a panel: a zoom, a cut or a speed change that is selected

    [Fact]
    public async Task SelectingAZoom_ShowsTheZoomPanel_AlsoForTheOneThatWasSelected()
    {
        var session = await OpenWithEverythingAsync();
        session.ShowInspectorPanel(Background);
        Changes.Clear();

        session.SelectZoom(1);

        Assert.Equal(Zoom, session.InspectorPanel);
        Assert.Equal(new[] { StudioEditorChanges.Selection, StudioEditorChanges.Inspector }, Changes);

        // A press on the block of the zoom that is selected already: nothing new is selected,
        // and its panel comes back.
        session.ShowInspectorPanel(Project);
        Changes.Clear();
        session.SelectZoom(1);
        Assert.Equal(Zoom, session.InspectorPanel);
        Assert.Equal(new[] { StudioEditorChanges.Inspector }, Changes);
    }

    [Fact]
    public async Task SelectingACut_ShowsTheCutPanel_AlsoForTheOneThatWasSelected()
    {
        var session = await OpenWithEverythingAsync();
        session.ShowInspectorPanel(Background);
        Changes.Clear();

        session.SelectCut(0);

        Assert.Equal(Cut, session.InspectorPanel);
        Assert.Equal(new[] { StudioEditorChanges.Selection, StudioEditorChanges.Inspector }, Changes);

        session.ShowInspectorPanel(Project);
        Changes.Clear();
        session.SelectCut(0);
        Assert.Equal(Cut, session.InspectorPanel);
        Assert.Equal(new[] { StudioEditorChanges.Inspector }, Changes);
    }

    [Fact]
    public async Task SelectingASpeedChange_ShowsTheSpeedPanel_AlsoForTheOneThatWasSelected()
    {
        var session = await OpenWithEverythingAsync();
        session.ShowInspectorPanel(Background);
        Changes.Clear();

        session.SelectSpeed(0);

        Assert.Equal(Speed, session.InspectorPanel);
        Assert.Equal(new[] { StudioEditorChanges.Selection, StudioEditorChanges.Inspector }, Changes);

        session.ShowInspectorPanel(Project);
        Changes.Clear();
        session.SelectSpeed(0);
        Assert.Equal(Speed, session.InspectorPanel);
        Assert.Equal(new[] { StudioEditorChanges.Inspector }, Changes);
    }

    // What shows a panel: stepping to the one before or after, and to one by its place. On a
    // lane these are Left, Right, Home and End; in the inspector, Previous and Next.

    [Fact]
    public async Task SteppingToAZoom_ShowsTheZoomPanel()
    {
        var session = await OpenWithEverythingAsync();
        session.Scrub(0);

        session.ShowInspectorPanel(Background);
        Assert.True(session.SelectNextZoom());
        Assert.Equal(Zoom, session.InspectorPanel);

        session.ShowInspectorPanel(Background);
        Assert.True(session.SelectNextZoom());
        Assert.Equal(Zoom, session.InspectorPanel);

        session.ShowInspectorPanel(Background);
        Assert.True(session.SelectPreviousZoom());
        Assert.Equal(Zoom, session.InspectorPanel);

        session.ShowInspectorPanel(Background);
        Assert.True(session.SelectAndShowZoom(1));
        Assert.Equal(Zoom, session.InspectorPanel);

        // With no zoom to step to, nothing is selected anew and the panel stays.
        session.SelectNothing();
        session.ShowInspectorPanel(Background);
        Assert.False(session.SelectAndShowZoom(7));
        Assert.Equal(Background, session.InspectorPanel);
    }

    [Fact]
    public async Task SteppingToACut_ShowsTheCutPanel()
    {
        var session = await OpenWithEverythingAsync();
        session.Scrub(0);

        session.ShowInspectorPanel(Background);
        Assert.True(session.SelectNextCut());
        Assert.Equal(Cut, session.InspectorPanel);

        session.ShowInspectorPanel(Background);
        Assert.False(session.SelectNextCut());
        Assert.Equal(Background, session.InspectorPanel);

        session.Scrub(9.9);
        session.SelectNothing();
        Assert.True(session.SelectPreviousCut());
        Assert.Equal(Cut, session.InspectorPanel);

        session.ShowInspectorPanel(Background);
        Assert.True(session.SelectAndShowCut(0));
        Assert.Equal(Cut, session.InspectorPanel);
    }

    [Fact]
    public async Task SteppingToASpeedChange_ShowsTheSpeedPanel()
    {
        var session = await OpenWithEverythingAsync();
        session.Scrub(0);

        session.ShowInspectorPanel(Background);
        Assert.True(session.SelectNextSpeed());
        Assert.Equal(Speed, session.InspectorPanel);

        session.ShowInspectorPanel(Background);
        Assert.False(session.SelectNextSpeed());
        Assert.Equal(Background, session.InspectorPanel);

        session.Scrub(9.9);
        session.SelectNothing();
        Assert.True(session.SelectPreviousSpeed());
        Assert.Equal(Speed, session.InspectorPanel);

        session.ShowInspectorPanel(Background);
        Assert.True(session.SelectAndShowSpeed(0));
        Assert.Equal(Speed, session.InspectorPanel);
    }

    // What shows a panel: adding one, from the timeline's button, the inspector's, or Z, X and R

    [Fact]
    public async Task AddingAZoom_ShowsTheZoomPanel()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        session.Scrub(2);
        Assert.Equal(Scene, session.InspectorPanel);

        Assert.True(session.AddZoomAtPlayhead().Changed);

        Assert.Equal(Zoom, session.InspectorPanel);
    }

    [Fact]
    public async Task AddingAZoomWhereOneIs_ShowsTheZoomPanel_AndWhereNoneFits_ThePanelStays()
    {
        var session = await OpenWithEverythingAsync();
        session.ShowInspectorPanel(Background);

        // Where a zoom is, that one is selected: nothing is added, and its panel shows.
        session.Scrub(2);
        Assert.Equal(new StudioZoomEditResult(false, 0), session.AddZoomAtPlayhead());
        Assert.Equal(Zoom, session.InspectorPanel);

        // In the last instants of the recording no zoom fits, and none is selected in its place.
        session.ShowInspectorPanel(Background);
        session.Scrub(9.95);
        Assert.Equal(new StudioZoomEditResult(false, null), session.AddZoomAtPlayhead());
        Assert.Equal(Background, session.InspectorPanel);
    }

    [Fact]
    public async Task AddingACut_ShowsTheCutPanel()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        session.Scrub(2);

        Assert.True(session.AddCutAtPlayhead().Changed);

        Assert.Equal(Cut, session.InspectorPanel);
    }

    [Fact]
    public async Task AddingACutWhereOneIs_ShowsTheCutPanel_AndWhereNoneFits_ThePanelStays()
    {
        var session = await OpenWithEverythingAsync();
        session.ShowInspectorPanel(Background);

        session.Scrub(8.5);
        Assert.Equal(new StudioCutEditResult(false, 0), session.AddCutAtPlayhead());
        Assert.Equal(Cut, session.InspectorPanel);

        session.ShowInspectorPanel(Background);
        session.Scrub(9.98);
        Assert.Equal(new StudioCutEditResult(false, null), session.AddCutAtPlayhead());
        Assert.Equal(Background, session.InspectorPanel);
    }

    [Fact]
    public async Task AddingASpeedChange_ShowsTheSpeedPanel()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        session.Scrub(2);

        Assert.True(session.AddSpeedAtPlayhead().Changed);

        Assert.Equal(Speed, session.InspectorPanel);
    }

    [Fact]
    public async Task AddingASpeedChangeWhereOneIs_ShowsTheSpeedPanel_AndWhereNoneFits_ThePanelStays()
    {
        var session = await OpenWithEverythingAsync();
        session.ShowInspectorPanel(Background);

        session.Scrub(6.5);
        Assert.Equal(new StudioSpeedEditResult(false, 0), session.AddSpeedAtPlayhead());
        Assert.Equal(Speed, session.InspectorPanel);

        session.ShowInspectorPanel(Background);
        session.Scrub(9.98);
        Assert.Equal(new StudioSpeedEditResult(false, null), session.AddSpeedAtPlayhead());
        Assert.Equal(Background, session.InspectorPanel);
    }

    // What shows a panel: an edit to the selected one, as a drag of its block on the lane is

    [Fact]
    public async Task AnEditToTheSelectedZoom_ShowsTheZoomPanel_AndOneToAnotherZoomDoesNot()
    {
        var session = await OpenWithEverythingAsync();
        session.SelectZoom(0);
        session.ShowInspectorPanel(Background);

        Assert.True(session.MoveZoom(0, 1.2).Changed);
        Assert.Equal(Zoom, session.InspectorPanel);

        // The other zoom is not the selected one, and an edit to it leaves the panel.
        session.ShowInspectorPanel(Background);
        Assert.True(session.MoveZoom(1, 4.7).Changed);
        Assert.Equal(0, session.SelectedZoomIndex);
        Assert.Equal(Background, session.InspectorPanel);
    }

    [Fact]
    public async Task AnEditToTheSelectedCutOrSpeedChange_ShowsItsPanel()
    {
        var session = await OpenWithEverythingAsync();

        session.SelectCut(0);
        session.ShowInspectorPanel(Background);
        Assert.True(session.MoveCut(0, 8.4).Changed);
        Assert.Equal(Cut, session.InspectorPanel);

        session.SelectSpeed(0);
        session.ShowInspectorPanel(Background);
        Assert.True(session.SetSpeedRate(0, 4).Changed);
        Assert.Equal(Speed, session.InspectorPanel);
    }

    [Fact]
    public async Task AnEditToACutOrASpeedChangeThatIsNotSelected_LeavesThePanel()
    {
        var session = await OpenWithEverythingAsync();

        // A zoom is the selected one, and stays it through an edit to the cut and to the speed change.
        session.SelectZoom(0);
        session.ShowInspectorPanel(Background);
        Assert.True(session.MoveCut(0, 8.4).Changed);
        Assert.Equal(Background, session.InspectorPanel);
        Assert.True(session.SetSpeedRate(0, 4).Changed);
        Assert.Equal(Background, session.InspectorPanel);
        Assert.Equal(0, session.SelectedZoomIndex);

        // Nor with nothing selected.
        session.SelectNothing();
        Assert.True(session.MoveCut(0, 8.2).Changed);
        Assert.Equal(Background, session.InspectorPanel);
        Assert.True(session.SetSpeedRate(0, 2).Changed);
        Assert.Equal(Background, session.InspectorPanel);
    }

    // What shows a panel: Suggest zooms

    [Fact]
    public async Task SuggestingZooms_ShowsTheZoomPanel()
    {
        var id = CreateProject(camera: true);
        Projects.SaveEvents(id, new StudioEvents { Clicks = [new StudioClickEvent { T = 3, X = 0.2, Y = 0.3 }] });
        var session = await OpenAsync(id);
        Assert.Equal(Scene, session.InspectorPanel);

        Assert.True(session.ApplyZoomSuggestions());
        Assert.Equal(Zoom, session.InspectorPanel);

        // Also when the suggestions are the ones that are there already.
        session.ShowInspectorPanel(Project);
        Assert.False(session.ApplyZoomSuggestions());
        Assert.Equal(Zoom, session.InspectorPanel);

        // Removing the suggestions is not taking hold of a zoom.
        session.ShowInspectorPanel(Project);
        Assert.True(session.RemoveZoomSuggestions());
        Assert.Equal(Project, session.InspectorPanel);
    }

    [Fact]
    public async Task SuggestingZooms_InARecordingWithoutClicks_ShowsNoPanel()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        Assert.False(session.HasClicks);
        session.ShowInspectorPanel(Project);

        // The window offers no Suggest zooms there. Asked all the same, the editor suggests nothing.
        Assert.False(session.ApplyZoomSuggestions());

        Assert.Equal(Project, session.InspectorPanel);
    }

    // What shows a panel: a scene that is gone to, and a split

    [Fact]
    public async Task GoingToAScene_ShowsTheScenePanel()
    {
        var session = await OpenAsync(CreateProjectWithScenes(ThreeScenes()));

        session.ShowInspectorPanel(Zoom);
        Assert.True(session.ShowScene(2));
        Assert.Equal(Scene, session.InspectorPanel);

        session.ShowInspectorPanel(Zoom);
        Assert.True(session.ShowPreviousScene());
        Assert.Equal(Scene, session.InspectorPanel);

        session.ShowInspectorPanel(Zoom);
        Assert.True(session.ShowNextScene());
        Assert.Equal(Scene, session.InspectorPanel);

        // The scene the playhead is in already is gone to as well: a press on its block.
        session.ShowInspectorPanel(Zoom);
        Assert.True(session.ShowScene(2));
        Assert.Equal(Scene, session.InspectorPanel);

        // There is no scene after the last, and none with that number: the panel stays.
        session.ShowInspectorPanel(Zoom);
        Assert.False(session.ShowNextScene());
        Assert.False(session.ShowScene(3));
        Assert.Equal(Zoom, session.InspectorPanel);
    }

    [Fact]
    public async Task SplittingAScene_ShowsTheScenePanel()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        session.ShowInspectorPanel(Zoom);
        session.Scrub(4);

        Assert.True(session.SplitSceneAtPlayhead().Changed);

        Assert.Equal(Scene, session.InspectorPanel);
    }

    [Fact]
    public async Task ASplitThatIsRefused_ShowsTheScenePanelToo_WhichSaysWhy()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        session.ShowInspectorPanel(Zoom);
        Changes.Clear();

        // At the very start a half would have no length.
        Assert.NotNull(session.SplitSceneExplanation);
        Assert.False(session.SplitSceneAtPlayhead().Changed);

        Assert.Equal(Scene, session.InspectorPanel);
        Assert.Equal(new[] { StudioEditorChanges.Inspector }, Changes);
        Assert.False(session.HasUnsavedEdits);
    }

    // What shows a panel: the camera, dragged in the preview

    [Fact]
    public async Task MovingTheCameraInThePreview_ShowsTheCameraPanel()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        Assert.Equal(Scene, session.InspectorPanel);

        session.BeginGesture();
        session.MoveBubbleTopLeft(100, 100, 1920, 1080);

        Assert.Equal(Camera, session.InspectorPanel);

        // The rail is still the user's while the drag goes on, until the camera moves again.
        session.ShowInspectorPanel(Background);
        Assert.Equal(Background, session.InspectorPanel);
        session.MoveBubbleTopLeft(120, 110, 1920, 1080);
        session.EndGesture();
        Assert.Equal(Camera, session.InspectorPanel);
    }

    // What does not change the panel

    [Fact]
    public async Task UndoAndRedo_LeaveThePanel()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        session.Scrub(2);
        session.AddZoomAtPlayhead();
        session.SetCanvasPadding(0.2);
        Assert.Equal(Zoom, session.InspectorPanel);

        // Through the padding, the zoom stays selected; undoing its adding takes it away, and
        // redoing that brings it back. None of it is taking hold of the zoom.
        session.ShowInspectorPanel(Background);
        session.Undo();
        Assert.Equal(0, session.SelectedZoomIndex);
        Assert.Equal(Background, session.InspectorPanel);
        session.Undo();
        Assert.Empty(session.Project!.Zooms);
        Assert.Equal(Background, session.InspectorPanel);
        session.Redo();
        Assert.Single(session.Project.Zooms);
        session.Redo();
        Assert.Equal(Background, session.InspectorPanel);

        // A split that is undone and redone does not bring the Scene panel either.
        session.Scrub(5);
        session.SplitSceneAtPlayhead();
        session.ShowInspectorPanel(Audio);
        session.Undo();
        session.Redo();
        Assert.Equal(2, session.SceneCount);
        Assert.Equal(Audio, session.InspectorPanel);
    }

    [Fact]
    public async Task AnEditThatIsNotToTheSelectedOne_LeavesThePanel()
    {
        var session = await OpenWithEverythingAsync();
        session.SelectSpeed(0);
        session.ShowInspectorPanel(Background);

        // The speed change stays selected through each of these, and is not what they change.
        session.SetCanvasPadding(0.2);
        session.SetMuted(true);
        session.SetTrimStartAtPlayhead();
        session.SetScreenCropInset(StudioCropEdge.Left, 0.1);

        Assert.Equal(0, session.SelectedSpeedIndex);
        Assert.Equal(Background, session.InspectorPanel);
    }

    [Fact]
    public async Task TheLayout_LeavesThePanel()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        session.ShowInspectorPanel(Zoom);

        foreach (var layout in new[] { StudioLayout.Screen, StudioLayout.SideBySide, StudioLayout.Camera, StudioLayout.Bubble })
        {
            session.SetLayout(layout);
            Assert.Equal(layout, session.Model!.EffectiveLayout);
            Assert.Equal(Zoom, session.InspectorPanel);
        }

        // Nor does the Camera panel go when the layout hides the camera.
        session.ShowInspectorPanel(Camera);
        session.SetLayout(StudioLayout.Screen);
        Assert.Equal(Camera, session.InspectorPanel);
    }

    [Fact]
    public async Task PlayingAndScrubbing_LeaveThePanel()
    {
        var session = await OpenAsync(CreateProjectWithScenes(ThreeScenes()));
        session.ShowInspectorPanel(Audio);
        Changes.Clear();

        // Playing, into the next scene.
        session.Scrub(3.5);
        session.TogglePlayback();
        Preview.RaisePosition(4.5);
        Pump();
        Assert.Equal(1, session.CurrentSceneIndex);
        session.Pause();

        // Scrubbing, as on the trim bar, into the last scene, and a step of a frame.
        session.Scrub(8);
        Assert.Equal(2, session.CurrentSceneIndex);
        session.StepFrames(-1);
        session.SetTrimStart(1);
        session.SetTrimEnd(9);

        Assert.Equal(Audio, session.InspectorPanel);
        Assert.DoesNotContain(Changes, change => change.HasFlag(StudioEditorChanges.Inspector));
    }

    [Fact]
    public async Task LettingGoOfWhatIsSelected_LeavesThePanel()
    {
        var session = await OpenWithEverythingAsync();

        // A press on an empty part of a lane.
        session.SelectCut(0);
        session.SelectNothing();
        Assert.Null(session.SelectedCutIndex);
        Assert.Equal(Cut, session.InspectorPanel);

        // A screen reader that takes the selection off a block.
        session.SelectZoom(1);
        session.SelectZoom(null);
        Assert.Null(session.SelectedZoomIndex);
        Assert.Equal(Zoom, session.InspectorPanel);

        // A place that has no zoom, cut or speed change selects none, and shows no panel.
        session.ShowInspectorPanel(Background);
        session.SelectZoom(9);
        session.SelectCut(9);
        session.SelectSpeed(9);
        session.SelectCut(null);
        session.SelectSpeed(null);
        Assert.Equal(Background, session.InspectorPanel);
    }

    [Fact]
    public async Task DeletingWhatIsSelected_LeavesThePanel()
    {
        var session = await OpenWithEverythingAsync();

        session.SelectZoom(0);
        session.ShowInspectorPanel(Background);
        Assert.True(session.RemoveSelectedZoom().Changed);
        Assert.Null(session.SelectedZoomIndex);
        Assert.Equal(Background, session.InspectorPanel);

        session.SelectCut(0);
        session.ShowInspectorPanel(Background);
        Assert.True(session.RemoveSelectedCut().Changed);
        Assert.Equal(Background, session.InspectorPanel);

        session.SelectSpeed(0);
        session.ShowInspectorPanel(Background);
        Assert.True(session.RemoveSelectedSpeed().Changed);
        Assert.Equal(Background, session.InspectorPanel);

        // Deleting on its own panel leaves that panel on show, now without a selected one.
        session.SelectZoom(0);
        Assert.Equal(Zoom, session.InspectorPanel);
        Assert.True(session.RemoveSelectedZoom().Changed);
        Assert.Equal(Zoom, session.InspectorPanel);
    }

    [Fact]
    public async Task DeletingAScene_LeavesThePanel()
    {
        var session = await OpenAsync(CreateProjectWithScenes(ThreeScenes()));
        session.Scrub(5);
        session.ShowInspectorPanel(Zoom);

        Assert.True(session.RemoveCurrentScene().Changed);

        Assert.Equal(2, session.SceneCount);
        Assert.Equal(Zoom, session.InspectorPanel);
    }

    [Fact]
    public async Task WhileTheProjectCannotBeEdited_WhatIsRefusedShowsNoPanel()
    {
        var id = CreateProjectWithScenes(ThreeScenes());
        Projects.SaveEvents(id, new StudioEvents { Clicks = [new StudioClickEvent { T = 3, X = 0.2, Y = 0.3 }] });
        var session = await OpenAsync(id);
        session.Scrub(2);
        session.ShowInspectorPanel(Audio);
        var export = session.ExportAsync(() => ExportPath, default);
        Assert.True(session.IsExporting);

        Assert.False(session.AddZoomAtPlayhead().Changed);
        Assert.False(session.AddCutAtPlayhead().Changed);
        Assert.False(session.AddSpeedAtPlayhead().Changed);
        Assert.False(session.ApplyZoomSuggestions());
        Assert.False(session.SplitSceneAtPlayhead().Changed);
        Assert.False(session.ShowScene(2));
        Assert.False(session.ShowNextScene());
        session.MoveBubbleTopLeft(100, 100, 1920, 1080);

        Assert.Equal(Audio, session.InspectorPanel);

        Exporter.Complete();
        await FinishAsync(export);
    }

    // The crop groups of the Screen and the Camera panel

    [Fact]
    public async Task ACropGroup_IsOpenWhileItsPictureIsCropped_UntilItsHeaderIsPressed()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        Assert.False(session.IsScreenCropOpen);
        Assert.False(session.IsCameraCropOpen);

        session.SetCameraCropInset(StudioCropEdge.Top, 0.1);
        Assert.True(session.IsCameraCropOpen);
        Assert.False(session.IsScreenCropOpen);

        session.SetScreenCropInset(StudioCropEdge.Left, 0.2);
        Assert.True(session.IsScreenCropOpen);

        // Reset crop, and Undo after it.
        session.ClearScreenCrop();
        Assert.False(session.IsScreenCropOpen);
        Assert.True(session.IsCameraCropOpen);
        session.Undo();
        Assert.True(session.IsScreenCropOpen);
        session.Undo();
        session.Undo();
        Assert.False(session.IsScreenCropOpen);
        Assert.False(session.IsCameraCropOpen);
    }

    [Fact]
    public async Task AProjectThatWasCropped_OpensWithItsCropGroupOpen()
    {
        var id = CreateProject(camera: true);
        var project = Projects.Load(id);
        Projects.Save(project with { Screen = project.Screen with { Crop = new StudioRect { X = 0.1, Y = 0, Width = 0.9, Height = 1 } } });

        var session = await OpenAsync(id);

        Assert.True(session.IsScreenCropOpen);
        Assert.False(session.IsCameraCropOpen);
    }

    [Fact]
    public async Task ACropGroup_ThatItsHeaderOpenedOrClosed_StaysAsItWasLeft()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        Changes.Clear();

        // Opened by hand over a picture that is not cropped.
        session.SetScreenCropOpen(true);
        Assert.True(session.IsScreenCropOpen);
        Assert.False(session.IsCameraCropOpen);
        Assert.Equal(new[] { StudioEditorChanges.Inspector }, Changes);
        session.SetScreenCropInset(StudioCropEdge.Left, 0.2);
        session.ClearScreenCrop();
        Assert.True(session.IsScreenCropOpen);

        // Closed by hand, and then cropped all the same, by Undo.
        session.SetScreenCropOpen(false);
        Assert.False(session.IsScreenCropOpen);
        session.Undo();
        Assert.NotNull(session.Project!.Screen.Crop);
        Assert.False(session.IsScreenCropOpen);

        // The camera's group has a header of its own, and says what it did as the screen's does.
        Changes.Clear();
        session.SetCameraCropOpen(true);
        Assert.True(session.IsCameraCropOpen);
        Assert.False(session.IsScreenCropOpen);
        Assert.Equal(new[] { StudioEditorChanges.Inspector }, Changes);
        session.SetCameraCropOpen(false);
        Assert.Equal(new[] { StudioEditorChanges.Inspector, StudioEditorChanges.Inspector }, Changes);
        session.SetCameraCropInset(StudioCropEdge.Top, 0.1);
        Assert.False(session.IsCameraCropOpen);
    }

    [Fact]
    public async Task TheStateACropGroupIsInAlready_SaysNothingNew_AndLeavesItFollowingTheCrop()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        Changes.Clear();

        // What a window hands back when it has only shown what it was told.
        session.SetScreenCropOpen(false);
        session.SetCameraCropOpen(false);
        Assert.Empty(Changes);

        session.SetScreenCropInset(StudioCropEdge.Left, 0.2);
        session.SetCameraCropInset(StudioCropEdge.Top, 0.1);
        Assert.True(session.IsScreenCropOpen);
        Assert.True(session.IsCameraCropOpen);

        Changes.Clear();
        session.SetScreenCropOpen(true);
        session.SetCameraCropOpen(true);
        Assert.Empty(Changes);
        session.ClearScreenCrop();
        session.ClearCameraCrop();
        Assert.False(session.IsScreenCropOpen);
        Assert.False(session.IsCameraCropOpen);
    }

    [Fact]
    public async Task OpeningACropGroup_IsNotAnEdit()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        var before = session.Model!.EditableState;
        Preview.Calls.Clear();
        Log.Clear();

        session.SetScreenCropOpen(true);
        session.SetCameraCropOpen(true);
        session.SetScreenCropOpen(false);
        Advance(StudioEditorSession.AutosaveDelay.TotalMilliseconds * 2);

        Assert.True(before.ContentEquals(session.Model.EditableState));
        Assert.False(session.HasUnsavedEdits);
        Assert.False(session.CanUndo);
        Assert.Empty(Preview.Calls);
        Assert.Empty(Log);
    }

    // Helpers

    /// <summary>
    /// A recording with a camera, 10 s long, with two zooms (from 1 s and from 4.5 s), a speed
    /// change from 6 s to 8 s and a cut from 8.2 s to 9.2 s, nothing selected, and the Scene
    /// panel on show.
    /// </summary>
    private async Task<StudioEditorSession> OpenWithEverythingAsync()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        Assert.True(session.AddZoom(1).Changed);
        Assert.True(session.AddZoom(4.5).Changed);
        Assert.True(session.AddSpeed(6).Changed);
        Assert.True(session.AddCut(8.2).Changed);
        session.SelectNothing();
        session.ShowInspectorPanel(Scene);
        Assert.Equal(2, session.Project!.Zooms.Length);
        Assert.Equal(1, session.SpeedCount);
        Assert.Equal(1, session.CutCount);
        return session;
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
}
