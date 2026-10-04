using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>Edits, undo, and the autosave that follows them.</summary>
public sealed class StudioEditorSessionEditTests : StudioEditorSessionTestBase
{
    [Fact]
    public async Task Edit_MarksUnsaved_UpdatesThePreview_AndIsSavedAfterTheDelay()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        Preview.Calls.Clear();
        Changes.Clear();

        session.SetCanvasPadding(0.2);

        Assert.True(session.HasUnsavedEdits);
        Assert.Equal(new[] { "UpdateProject" }, Preview.Calls);
        Assert.Equal(0.2, Preview.LastProject!.Canvas.Padding, Precision);
        Assert.Equal(new[] { StudioEditorChanges.Project | StudioEditorChanges.Playback }, Changes);
        Assert.Equal(0.06, Projects.Load(id).Canvas.Padding, Precision);

        Advance(599);
        Assert.Equal(0, LogCount("store.save"));

        Advance(1);
        Assert.Equal(1, LogCount("store.save"));
        Assert.False(session.HasUnsavedEdits);
        Assert.Equal(0.2, Projects.Load(id).Canvas.Padding, Precision);
        Assert.Empty(Errors);
    }

    [Fact]
    public async Task Edit_ThatChangesNothing_DoesNothing()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        Preview.Calls.Clear();
        Changes.Clear();

        session.SetCanvasPadding(0.06);
        session.SetLayout(StudioLayout.Bubble);
        session.SetMuted(false);
        session.SetCameraBubbleSize(0.24);
        session.Undo();
        session.Redo();
        session.BeginGesture();
        session.SetScreenShadow(0.5);
        session.EndGesture();

        Assert.False(session.HasUnsavedEdits);
        Assert.False(session.CanUndo);
        Assert.Empty(Preview.Calls);
        Assert.Empty(Changes);

        Advance(5000);
        Assert.Equal(0, LogCount("store.save"));
    }

    [Fact]
    public async Task SeveralEdits_GiveOneSave_AfterTheLastOne()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);

        session.SetCanvasPadding(0.1);
        Advance(300);
        session.SetScreenShadow(0.9);
        Advance(300);
        session.SetScreenCornerRadius(0.1);
        Advance(599);
        Assert.Equal(0, LogCount("store.save"));

        Advance(1);

        Assert.Equal(1, LogCount("store.save"));
        var saved = Projects.Load(id);
        Assert.Equal(0.1, saved.Canvas.Padding, Precision);
        Assert.Equal(0.9, saved.Screen.Shadow, Precision);
        Assert.Equal(0.1, saved.Screen.CornerRadius, Precision);

        Advance(5000);
        Assert.Equal(1, LogCount("store.save"));
    }

    [Fact]
    public async Task Save_KeepsWhatAnotherPartOfTheAppWroteInBetween()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        session.SetCanvasPadding(0.2);

        Projects.RecordExport(id, ExportPath);
        Projects.Save(Projects.Load(id) with { Name = "Renamed in the library" });
        Advance(600);

        var saved = Projects.Load(id);
        Assert.Equal(0.2, saved.Canvas.Padding, Precision);
        Assert.Equal(ExportPath, Assert.Single(saved.Exports).Path);
        Assert.Equal("Renamed in the library", saved.Name);

        // The session picks the bookkeeping up from what it saved.
        Assert.Equal("Renamed in the library", session.ClipName);
        Assert.False(session.HasNeverExported);
    }

    [Fact]
    public async Task SaveNow_SavesAtOnce_AndCancelsTheScheduledSave()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        session.SetCanvasPadding(0.2);

        Assert.True(session.SaveNow());

        Assert.Equal(1, LogCount("store.save"));
        Assert.Equal(0.2, Projects.Load(id).Canvas.Padding, Precision);
        Advance(5000);
        Assert.Equal(1, LogCount("store.save"));
    }

    [Fact]
    public async Task SaveNow_WithNothingToSave_DoesNotTouchTheStore()
    {
        var session = await OpenAsync(CreateProject());
        Log.Clear();

        Assert.True(session.SaveNow());

        Assert.Empty(Log);
    }

    [Fact]
    public async Task SaveNow_ReturnsFalseAndReportsTheError_WhenTheProjectCannotBeSaved()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        session.SetCanvasPadding(0.2);
        Directory.Delete(Projects.GetPaths(id).ProjectDirectory, recursive: true);

        Assert.False(session.SaveNow());

        Assert.StartsWith("Studio could not save this project: ", Assert.Single(Errors));
        Assert.Equal(StudioEditorErrorKind.Save, Assert.Single(ErrorKinds));
        Assert.True(session.HasUnsavedEdits);
    }

    [Fact]
    public async Task ASaveThatFailed_IsMadeUpForByTheNextOne()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        var file = Path.Combine(Projects.GetPaths(id).ProjectDirectory, "project.json");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            session.SetCanvasPadding(0.2);
            Advance(600);

            Assert.Equal(StudioEditorErrorKind.Save, Assert.Single(ErrorKinds));
            Assert.True(session.HasUnsavedEdits);
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Changes.Clear();
        session.SetScreenShadow(0.9);
        Advance(600);

        // The window takes the save error away on this notice: nothing is unsaved any more.
        Assert.Equal(StudioEditorChanges.Project, Changes[^1]);
        Assert.False(session.HasUnsavedEdits);
        Assert.Single(Errors);
        var saved = Projects.Load(id);
        Assert.Equal(0.2, saved.Canvas.Padding, Precision);
        Assert.Equal(0.9, saved.Screen.Shadow, Precision);
    }

    [Fact]
    public async Task Gesture_IsOneUndoStep()
    {
        var session = await OpenAsync(CreateProject());

        session.BeginGesture();
        session.SetCanvasPadding(0.1);
        session.SetCanvasPadding(0.2);
        session.SetCanvasPadding(0.3);
        Assert.False(session.CanUndo);
        Changes.Clear();
        session.EndGesture();

        Assert.True(session.CanUndo);
        Assert.Equal(new[] { StudioEditorChanges.Project }, Changes);

        session.Undo();
        Assert.Equal(0.06, session.Project!.Canvas.Padding, Precision);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public async Task EditsOutsideAGesture_AreOneUndoStepEach()
    {
        var session = await OpenAsync(CreateProject());

        session.SetCanvasPadding(0.1);
        session.SetCanvasPadding(0.2);

        session.Undo();
        Assert.Equal(0.1, session.Project!.Canvas.Padding, Precision);
        session.Undo();
        Assert.Equal(0.06, session.Project.Canvas.Padding, Precision);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public async Task UndoAndRedo_AreEditsLikeAnyOther()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        session.SetCanvasPadding(0.2);
        Advance(600);
        Preview.Calls.Clear();

        session.Undo();

        Assert.True(session.HasUnsavedEdits);
        Assert.Equal(new[] { "UpdateProject" }, Preview.Calls);
        Assert.Equal(0.06, Preview.LastProject!.Canvas.Padding, Precision);
        Advance(600);
        Assert.Equal(0.06, Projects.Load(id).Canvas.Padding, Precision);

        session.Redo();
        Assert.Equal(0.2, session.Project!.Canvas.Padding, Precision);
        Advance(600);
        Assert.Equal(0.2, Projects.Load(id).Canvas.Padding, Precision);
        Assert.Equal(3, LogCount("store.save"));
    }

    [Fact]
    public async Task TrimHandles_ShowTheFrameAtTheNewEdge()
    {
        var session = await OpenAsync(CreateProject());
        session.TogglePlayback();
        Preview.Calls.Clear();
        Preview.Seeks.Clear();

        session.SetTrimStart(2);

        Assert.Equal(2, session.Model!.TrimStart, Precision);
        Assert.False(session.IsPlaying);
        Assert.Equal(2, session.Playhead, Precision);
        Assert.Equal(new[] { "UpdateProject", "Pause", "Seek" }, Preview.Calls);

        session.SetTrimEnd(8);

        Assert.Equal(8, session.Model.TrimEnd, Precision);
        Assert.Equal(8, session.Playhead, Precision);
        Assert.Equal(new[] { 2.0, 8.0 }, Preview.Seeks);
    }

    [Fact]
    public async Task TrimHandle_ThatCannotMove_StillShowsItsFrame()
    {
        var session = await OpenAsync(CreateProject());
        session.Scrub(5);
        Changes.Clear();

        session.SetTrimStart(-4);

        Assert.Equal(0, session.Model!.TrimStart, Precision);
        Assert.Equal(0, session.Playhead, Precision);
        Assert.False(session.HasUnsavedEdits);
        Assert.Equal(new[] { StudioEditorChanges.Playback }, Changes);
    }

    [Fact]
    public async Task TrimAtThePlayhead_DoesNotMoveThePlayhead()
    {
        var session = await OpenAsync(CreateProject());
        session.Scrub(3);
        Preview.Calls.Clear();

        session.SetTrimStartAtPlayhead();

        Assert.Equal(3, session.Model!.TrimStart, Precision);
        Assert.Equal(3, session.Playhead, Precision);
        Assert.Equal(new[] { "UpdateProject" }, Preview.Calls);

        session.Scrub(7);
        Preview.Calls.Clear();
        session.SetTrimEndAtPlayhead();

        Assert.Equal(7, session.Model.TrimEnd, Precision);
        Assert.Equal(7, session.Playhead, Precision);
        Assert.Equal(new[] { "UpdateProject" }, Preview.Calls);
        Assert.Equal("0:04.0 / 0:04.0", session.TimeText);
    }

    [Fact]
    public async Task ZoomEdits_UseLoadedEvents_AndReachThePreview()
    {
        var id = CreateProject();
        Projects.SaveEvents(id, new StudioEvents
        {
            Cursor =
            [
                new StudioCursorSample { T = 2, X = 0.25, Y = 0.75 },
                new StudioCursorSample { T = 3, X = 0.25, Y = 0.75 },
            ],
        });
        var session = await OpenAsync(id);
        Preview.Calls.Clear();

        var added = session.AddZoom(2.5);
        var scaled = session.SetZoomScale(added.Index!.Value, 4);

        Assert.True(added.Changed);
        Assert.True(scaled.Changed);
        var zoom = Assert.Single(session.Project!.Zooms);
        Assert.Equal(0.25, zoom.Focus.X, Precision);
        Assert.Equal(0.75, zoom.Focus.Y, Precision);
        Assert.Equal(4, zoom.Scale, Precision);
        Assert.Equal(new[] { "UpdateProject", "UpdateProject" }, Preview.Calls);
        Assert.Equal(4, Preview.LastProject!.Zooms[0].Scale, Precision);
    }

    [Fact]
    public async Task ApplyZoomSuggestions_ReplacesAutoZoomsFromLoadedClicks()
    {
        var id = CreateProject();
        Projects.Save(Projects.Load(id) with
        {
            Zooms = [new StudioZoom { Start = 7, End = 8, Origin = StudioZoomOrigin.Auto }],
        });
        Projects.SaveEvents(id, new StudioEvents
        {
            Clicks = [new StudioClickEvent { T = 3, X = 0.2, Y = 0.3 }],
        });
        var session = await OpenAsync(id);

        Assert.True(session.ApplyZoomSuggestions());

        var zoom = Assert.Single(session.Project!.Zooms);
        Assert.Equal(2.4, zoom.Start, Precision);
        Assert.Equal(4.5, zoom.End, Precision);
        Assert.Equal(StudioZoomOrigin.Auto, zoom.Origin);
        Assert.Equal(2.4, Preview.LastProject!.Zooms[0].Start, Precision);
    }

    [Fact]
    public async Task ZoomEdits_AreRefusedWhileExporting_AndTheZoomStaysWhereItWas()
    {
        var session = await OpenAsync(CreateProject());
        session.AddZoom(2);
        var export = session.ExportAsync(() => ExportPath, default);
        Preview.Calls.Clear();

        Assert.Equal(new StudioZoomEditResult(false, 0), session.SetZoomScale(0, 4));
        Assert.Equal(new StudioZoomEditResult(false, 0), session.SetZoomStart(0, 1));
        Assert.Equal(new StudioZoomEditResult(false, 0), session.SetZoomEnd(0, 9));
        Assert.Equal(new StudioZoomEditResult(false, 0), session.SetZoomFocusMode(0, StudioZoomFocusMode.Cursor));
        Assert.Equal(new StudioZoomEditResult(false, 0), session.SetZoomFocusPoint(0, 0.1, 0.1));
        Assert.Equal(new StudioZoomEditResult(false, 0), session.SetZoomEaseIn(0, 2));
        Assert.Equal(new StudioZoomEditResult(false, 0), session.SetZoomEaseOut(0, 2));
        Assert.Equal(new StudioZoomEditResult(false, 0), session.RemoveZoom(0));
        Assert.Equal(new StudioZoomEditResult(false, null), session.AddZoom(7));
        Assert.False(session.ApplyZoomSuggestions());

        var zoom = Assert.Single(session.Project!.Zooms);
        Assert.Equal(2, zoom.Scale, Precision);
        Assert.Equal(5, zoom.End, Precision);
        Assert.Empty(Preview.Calls);
        Assert.Equal(0, session.GetZoomIndexAt(3));

        session.CancelExport();
        await FinishAsync(export);
    }

    [Fact]
    public async Task ADragOfAZoom_IsOneUndoStep_AndEveryStepReachesThePreview()
    {
        var session = await OpenAsync(CreateProject());
        session.AddZoom(2);
        Preview.Calls.Clear();

        session.BeginGesture();
        session.SetZoomEnd(0, 5.5);
        session.SetZoomEnd(0, 6);
        session.SetZoomEnd(0, 7);
        session.EndGesture();

        Assert.Equal(new[] { "UpdateProject", "UpdateProject", "UpdateProject" }, Preview.Calls);
        Assert.Equal(7, session.Project!.Zooms[0].End, Precision);
        Assert.Equal(7, Preview.LastProject!.Zooms[0].End, Precision);

        session.Undo();
        Assert.Equal(5, session.Project.Zooms[0].End, Precision);
        session.Undo();
        Assert.Empty(session.Project.Zooms);
        Assert.Empty(Preview.LastProject!.Zooms);
    }

    [Fact]
    public async Task ACrop_ReachesThePreview_AndIsSaved()
    {
        var id = CreateProject(camera: true);
        var session = await OpenAsync(id);
        Preview.Calls.Clear();

        session.SetScreenCrop(new StudioRect(0.25, 0, 0.5, 1));
        session.SetCameraCrop(new StudioRect(0.1, 0.1, 0.8, 0.8));
        session.ClearCameraCrop();

        Assert.Equal(new[] { "UpdateProject", "UpdateProject", "UpdateProject" }, Preview.Calls);
        Assert.Equal(0.25, Preview.LastProject!.Screen.Crop!.X, Precision);
        Assert.Null(Preview.LastProject.Camera.Crop);
        Assert.True(session.HasUnsavedEdits);

        await FinishAsync(session.CloseAsync());

        Assert.Equal(0.5, Projects.Load(id).Screen.Crop!.Width, Precision);
        Assert.Null(Projects.Load(id).Camera.Crop);
    }

    [Fact]
    public async Task Edit_WhilePlaying_PausesWhenThePlayheadIsNowAtThePlaybackEnd()
    {
        var session = await OpenAsync(CreateProject());
        session.Scrub(5);
        session.TogglePlayback();
        Preview.RaisePosition(6);
        Pump();

        session.SetCanvasPadding(0.2);
        Assert.True(session.IsPlaying);

        Preview.Calls.Clear();
        session.SetTrimEndAtPlayhead();

        Assert.False(session.IsPlaying);
        Assert.Equal(6, session.Playhead, Precision);
        Assert.Equal(new[] { "UpdateProject", "Pause" }, Preview.Calls);
    }

    [Fact]
    public async Task Edits_AreRefusedWhileExporting_AndAfterClosing()
    {
        var session = await OpenAsync(CreateProject());
        session.SetCanvasPadding(0.2);
        var export = session.ExportAsync(() => ExportPath, default);

        session.SetCanvasPadding(0.3);
        session.Undo();
        session.BeginGesture();

        Assert.Equal(0.2, session.Project!.Canvas.Padding, Precision);
        Assert.False(session.Model!.IsGroupingEdits);

        session.CancelExport();
        await FinishAsync(export);
        await FinishAsync(session.CloseAsync());

        session.SetCanvasPadding(0.3);
        Assert.Equal(0.2, session.Project.Canvas.Padding, Precision);
    }

    [Fact]
    public async Task SaveDefaultLook_StoresTheStylingWithoutCrops()
    {
        var id = CreateProject(camera: true);
        Projects.Save(Projects.Load(id) with { Screen = new StudioScreenStyle { Crop = new StudioRect(0.1, 0.1, 0.5, 0.5) } });
        var session = await OpenAsync(id);
        session.SetCanvasPadding(0.12);
        session.SetBackgroundPreset(StudioBackgroundStyle.Solid, "coral", "#FF7A6B");
        session.SetScreenCornerRadius(0.05);
        session.SetCameraShape(StudioCameraShape.Squircle);
        Assert.Null(Settings.StudioDefaultLook);

        session.SaveDefaultLook();

        var look = Settings.StudioDefaultLook;
        Assert.NotNull(look);
        Assert.Equal(0.12, look.Canvas.Padding, Precision);
        Assert.Equal(StudioBackgroundStyle.Solid, look.Canvas.Background.Style);
        Assert.Equal("coral", look.Canvas.Background.Preset);
        Assert.Equal("#FF7A6B", look.Canvas.Background.Primary);
        Assert.Equal(0.05, look.Screen.CornerRadius, Precision);
        Assert.Null(look.Screen.Crop);
        Assert.Equal(StudioCameraShape.Squircle, look.Camera.Shape);
        Assert.NotNull(session.Project!.Screen.Crop);
    }
}
