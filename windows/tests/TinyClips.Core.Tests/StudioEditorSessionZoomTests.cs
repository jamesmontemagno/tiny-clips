using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>The selected zoom: what selects one, and how the selection stays on its zoom.</summary>
public sealed class StudioEditorSessionZoomTests : StudioEditorSessionTestBase
{
    private const StudioEditorChanges Edited = StudioEditorChanges.Project | StudioEditorChanges.Playback;

    // Selecting

    [Fact]
    public async Task NothingIsSelected_UntilAZoomIs()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(1, 3), Zoom(5, 7)));
        Changes.Clear();

        Assert.Null(session.SelectedZoomIndex);
        Assert.Null(session.SelectedZoom);

        session.SelectZoom(1);

        Assert.Equal(1, session.SelectedZoomIndex);
        Assert.Equal(5, session.SelectedZoom!.Start, Precision);
        Assert.Equal(new[] { StudioEditorChanges.Selection, StudioEditorChanges.Inspector }, Changes);
        Assert.False(session.HasUnsavedEdits);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public async Task SelectingWhatIsSelected_OrAPlaceWithNoZoom_SaysNothingNew()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(1, 3), Zoom(5, 7)));
        session.SelectZoom(1);
        Changes.Clear();

        session.SelectZoom(1);
        Assert.Empty(Changes);

        // A place with no zoom is no selection.
        session.SelectZoom(2);
        Assert.Null(session.SelectedZoomIndex);
        session.SelectZoom(-1);
        session.SelectZoom(null);
        Assert.Equal(new[] { StudioEditorChanges.Selection }, Changes);
    }

    [Fact]
    public async Task SelectingNothing_LetsGoOfTheSelectedZoom()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(1, 3), Zoom(5, 7)));
        session.SelectZoom(0);
        Changes.Clear();

        session.SelectZoom(null);

        Assert.Null(session.SelectedZoomIndex);
        Assert.Null(session.SelectedZoom);
        Assert.Equal(new[] { StudioEditorChanges.Selection }, Changes);
        Assert.Equal(2, session.Project!.Zooms.Length);
    }

    [Fact]
    public async Task SelectingAZoom_DoesNotMoveThePlayhead()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(1, 3), Zoom(5, 7)));
        session.Scrub(9);
        Preview.Calls.Clear();

        session.SelectZoom(0);

        Assert.Equal(9, session.Playhead, Precision);
        Assert.Empty(Preview.Calls);
    }

    // Stepping through the zooms

    [Fact]
    public async Task TheNextAndPreviousZoom_AreSelected_AndShownWhereTheyHaveMovedIn()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(1, 3), Zoom(5, 7, easeIn: 1), Zoom(8, 9.5)));
        session.Scrub(4);
        Preview.Seeks.Clear();
        Changes.Clear();

        // Nothing selected and the playhead between two zooms: the one after it.
        Assert.True(session.SelectNextZoom());
        Assert.Equal(1, session.SelectedZoomIndex);
        Assert.Equal(6, session.Playhead, Precision);
        Assert.Equal(new[] { StudioEditorChanges.Selection, StudioEditorChanges.Inspector, StudioEditorChanges.Playback }, Changes);

        Assert.True(session.SelectNextZoom());
        Assert.Equal(2, session.SelectedZoomIndex);
        Assert.Equal(8.5, session.Playhead, Precision);

        // There is none after the last. The selection and the playhead stay.
        Assert.False(session.SelectNextZoom());
        Assert.Equal(2, session.SelectedZoomIndex);
        Assert.Equal(8.5, session.Playhead, Precision);

        Assert.True(session.SelectPreviousZoom());
        Assert.True(session.SelectPreviousZoom());
        Assert.Equal(0, session.SelectedZoomIndex);
        Assert.Equal(1.5, session.Playhead, Precision);
        Assert.False(session.SelectPreviousZoom());
        Assert.Equal(new[] { 6, 8.5, 6, 1.5 }, Preview.Seeks);
    }

    [Fact]
    public async Task WithNothingSelected_ThePreviousZoomIsTheOneAtOrBeforeThePlayhead()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(1, 3), Zoom(5, 7)));
        session.Scrub(4);

        Assert.True(session.SelectPreviousZoom());

        Assert.Equal(0, session.SelectedZoomIndex);
    }

    [Fact]
    public async Task SteppingToAZoom_PausesAPlayingPreview()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(5, 7)));
        session.TogglePlayback();
        Assert.True(session.IsPlaying);

        Assert.True(session.SelectNextZoom());

        Assert.False(session.IsPlaying);
        Assert.Equal(5.5, session.Playhead, Precision);
    }

    [Fact]
    public async Task WithoutZooms_ThereIsNothingToStepTo()
    {
        var session = await OpenAsync(CreateProject());
        Changes.Clear();

        Assert.False(session.SelectNextZoom());
        Assert.False(session.SelectPreviousZoom());
        Assert.False(session.SelectAndShowZoom(0));

        Assert.Null(session.SelectedZoomIndex);
        Assert.Empty(Changes);
    }

    [Fact]
    public async Task AZoomIsSelectedAndShownByItsPlace_FromAnywhere()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(1, 3), Zoom(5, 7, easeIn: 1), Zoom(8, 9.5)));
        session.Scrub(4);
        Preview.Seeks.Clear();
        Changes.Clear();

        // The last one, whatever is selected and wherever the playhead is.
        Assert.True(session.SelectAndShowZoom(2));
        Assert.Equal(2, session.SelectedZoomIndex);
        Assert.Equal(8.5, session.Playhead, Precision);
        Assert.Equal(new[] { StudioEditorChanges.Selection, StudioEditorChanges.Inspector, StudioEditorChanges.Playback }, Changes);

        Assert.True(session.SelectAndShowZoom(0));
        Assert.Equal(0, session.SelectedZoomIndex);
        Assert.Equal(1.5, session.Playhead, Precision);

        // The selected zoom again: it stays selected, and the playhead goes back to where it has moved in.
        session.Scrub(2.5);
        Changes.Clear();
        Assert.True(session.SelectAndShowZoom(0));
        Assert.Equal(0, session.SelectedZoomIndex);
        Assert.Equal(1.5, session.Playhead, Precision);
        Assert.Equal(new[] { StudioEditorChanges.Playback }, Changes);
        Assert.Equal(new[] { 8.5, 1.5, 2.5, 1.5 }, Preview.Seeks);
    }

    [Fact]
    public async Task APlaceWithNoZoom_IsNotSelectedOrShown()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(1, 3), Zoom(5, 7)));
        session.SelectZoom(1);
        session.Scrub(4);
        Preview.Seeks.Clear();
        Changes.Clear();

        Assert.False(session.SelectAndShowZoom(2));
        Assert.False(session.SelectAndShowZoom(-1));
        Assert.False(session.SelectAndShowZoom(null));

        Assert.Equal(1, session.SelectedZoomIndex);
        Assert.Equal(4, session.Playhead, Precision);
        Assert.Empty(Changes);
        Assert.Empty(Preview.Seeks);
    }

    // Adding and removing

    [Fact]
    public async Task AddingAZoomAtThePlayhead_SelectsIt_AndShowsItMovedIn()
    {
        var session = await OpenAsync(CreateProject());
        session.Scrub(2);
        Preview.Calls.Clear();
        Changes.Clear();

        var result = session.AddZoomAtPlayhead();

        Assert.Equal(new StudioZoomEditResult(true, 0), result);
        Assert.Equal(0, session.SelectedZoomIndex);
        Assert.Equal(2, session.SelectedZoom!.Start, Precision);

        // The zoom starts where the playhead was. The playhead is then half a second on, where
        // the zoom has finished moving in.
        Assert.Equal(2.5, session.Playhead, Precision);
        Assert.Equal(new[] { "UpdateProject", "Pause", "Seek" }, Preview.Calls);
        Assert.Equal(new[] { Edited | StudioEditorChanges.Selection, StudioEditorChanges.Inspector, StudioEditorChanges.Playback }, Changes);
    }

    [Fact]
    public async Task AddingAZoomWhereOneIs_SelectsThatOne_AndLeavesThePlayhead()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(1, 3), Zoom(5, 7)));
        session.Scrub(6.75);
        Preview.Calls.Clear();
        Changes.Clear();

        var result = session.AddZoomAtPlayhead();

        Assert.Equal(new StudioZoomEditResult(false, 1), result);
        Assert.Equal(1, session.SelectedZoomIndex);
        Assert.Equal(6.75, session.Playhead, Precision);
        Assert.Empty(Preview.Calls);
        Assert.Equal(new[] { StudioEditorChanges.Selection, StudioEditorChanges.Inspector }, Changes);
        Assert.False(session.HasUnsavedEdits);
    }

    [Fact]
    public async Task AddingAZoomWhereThereIsNoRoom_KeepsTheSelection()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(1, 3), Zoom(3.2, 7)));
        session.SelectZoom(1);
        session.Scrub(3.1);
        Changes.Clear();

        Assert.False(session.CanAddZoomAtPlayhead);
        var result = session.AddZoomAtPlayhead();

        Assert.Equal(new StudioZoomEditResult(false, null), result);
        Assert.Equal(1, session.SelectedZoomIndex);
        Assert.Empty(Changes);
    }

    [Fact]
    public async Task AZoomCanBeAdded_WhereOneFits_OrOneIsAlreadyThere()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(1, 3), Zoom(3.2, 7)));

        // At the start of the recording, with a second before the first zoom.
        Assert.True(session.CanAddZoomAtPlayhead);

        // Inside a zoom, which adding selects.
        session.Scrub(2);
        Assert.True(session.CanAddZoomAtPlayhead);

        // In a gap of 0.2 seconds.
        session.Scrub(3.05);
        Assert.False(session.CanAddZoomAtPlayhead);

        // At the very end of the recording.
        session.Scrub(10);
        Assert.False(session.CanAddZoomAtPlayhead);

        session.Scrub(8);
        Assert.True(session.CanAddZoomAtPlayhead);
        var export = session.ExportAsync(() => ExportPath, default);
        Assert.False(session.CanAddZoomAtPlayhead);

        session.CancelExport();
        await FinishAsync(export);
    }

    [Fact]
    public async Task AddingAZoomWhilePlaying_DoesNotStopOrMoveThePreview()
    {
        var session = await OpenAsync(CreateProject());
        session.Scrub(2);
        session.TogglePlayback();
        Preview.Calls.Clear();

        var result = session.AddZoomAtPlayhead();

        Assert.True(result.Changed);
        Assert.Equal(0, session.SelectedZoomIndex);
        Assert.True(session.IsPlaying);
        Assert.Equal(new[] { "UpdateProject" }, Preview.Calls);
    }

    [Fact]
    public async Task AnAddedZoom_TakesTheSelection_AndUndoingItLeavesNothingSelected()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(5, 7)));
        session.SelectZoom(0);

        session.AddZoom(1);

        Assert.Equal(0, session.SelectedZoomIndex);
        Assert.Equal(1, session.SelectedZoom!.Start, Precision);

        // The added zoom is gone, and no zoom shares its time.
        session.Undo();
        Assert.Null(session.SelectedZoomIndex);
        Assert.Equal(5, Assert.Single(session.Project!.Zooms).Start, Precision);
    }

    [Fact]
    public async Task RemovingTheSelectedZoom_LeavesNothingSelected()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(1, 3), Zoom(5, 7)));
        session.SelectZoom(0);
        Changes.Clear();

        Assert.Equal(new StudioZoomEditResult(true, null), session.RemoveSelectedZoom());

        Assert.Null(session.SelectedZoomIndex);
        Assert.Equal(5, Assert.Single(session.Project!.Zooms).Start, Precision);
        Assert.Equal(new[] { Edited | StudioEditorChanges.Selection }, Changes);

        // With nothing selected there is nothing to remove.
        Changes.Clear();
        Assert.Equal(new StudioZoomEditResult(false, null), session.RemoveSelectedZoom());
        Assert.Single(session.Project.Zooms);
        Assert.Empty(Changes);

        // Undo brings the zoom back, but not the selection.
        session.Undo();
        Assert.Equal(2, session.Project.Zooms.Length);
        Assert.Null(session.SelectedZoomIndex);
    }

    [Fact]
    public async Task RemovingTheSelectedZoom_DoesNotMoveTheSelectionToAZoomThatOverlappedIt()
    {
        // Written by hand: the editor does not make zooms that overlap.
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(1, 5), Zoom(2, 3)));
        session.SelectZoom(1);

        Assert.Equal(new StudioZoomEditResult(true, null), session.RemoveSelectedZoom());

        Assert.Null(session.SelectedZoomIndex);
        Assert.Equal(1, Assert.Single(session.Project!.Zooms).Start, Precision);
    }

    [Fact]
    public async Task RemovingAnotherZoom_KeepsTheSelectionOnItsZoom()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(1, 3), Zoom(5, 7), Zoom(8, 9)));
        session.SelectZoom(2);
        Changes.Clear();

        session.RemoveZoom(0);

        // The same zoom, now one place earlier.
        Assert.Equal(1, session.SelectedZoomIndex);
        Assert.Equal(8, session.SelectedZoom!.Start, Precision);
        Assert.Equal(new[] { Edited | StudioEditorChanges.Selection }, Changes);

        session.Undo();
        Assert.Equal(2, session.SelectedZoomIndex);
        Assert.Equal(8, session.SelectedZoom!.Start, Precision);
    }

    // Edits to the selected zoom

    [Fact]
    public async Task EditsToTheSelectedZoom_KeepItSelected_AndSayNothingAboutTheSelection()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(1, 3), Zoom(5, 7)));
        session.SelectZoom(1);
        Changes.Clear();

        session.SetZoomScale(1, 3);
        session.SetZoomStart(1, 4);
        session.SetZoomEnd(1, 8);
        session.MoveZoom(1, 5.5);
        session.SetZoomFocusMode(1, StudioZoomFocusMode.Cursor);
        session.SetZoomFocusPoint(1, 0.2, 0.3);
        session.SetZoomFocusOnPad(1, 0.6, 0.7);
        session.SetZoomEaseIn(1, 1);
        session.SetZoomEaseOut(1, 0);

        Assert.Equal(1, session.SelectedZoomIndex);
        Assert.Equal(Enumerable.Repeat(Edited, 9), Changes);

        var zoom = session.SelectedZoom!;
        Assert.Equal(3, zoom.Scale, Precision);
        Assert.Equal(5.5, zoom.Start, Precision);
        Assert.Equal(9.5, zoom.End, Precision);
        Assert.Equal(StudioZoomFocusMode.Cursor, zoom.Focus.Mode);
        Assert.Equal(0.6, zoom.Focus.X, Precision);
        Assert.Equal(0.7, zoom.Focus.Y, Precision);
        Assert.Equal(1, zoom.EaseIn, Precision);
        Assert.Equal(0, zoom.EaseOut, Precision);
    }

    [Fact]
    public async Task ASelectedZoomMovedFarAway_StaysSelected_ThroughUndoAndRedoToo()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(1, 2), Zoom(4, 5)));
        session.SelectZoom(0);
        Changes.Clear();

        // To where it shares no time with what it was. The edit itself says where the zoom went.
        session.MoveZoom(0, 2.5);
        Assert.Equal(0, session.SelectedZoomIndex);
        Assert.Equal(2.5, session.SelectedZoom!.Start, Precision);

        // Undo does not say, but nothing else in the list has changed.
        session.Undo();
        Assert.Equal(0, session.SelectedZoomIndex);
        Assert.Equal(1, session.SelectedZoom!.Start, Precision);

        session.Redo();
        Assert.Equal(0, session.SelectedZoomIndex);
        Assert.Equal(2.5, session.SelectedZoom!.Start, Precision);
        Assert.Equal(new[] { Edited, Edited, Edited }, Changes);
    }

    [Fact]
    public async Task UndoingAChangeToTheSelectedZoom_KeepsItSelected()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(1, 3), Zoom(5, 7)));
        session.SelectZoom(1);
        session.SetZoomScale(1, 4);
        session.SetZoomEnd(1, 9);
        Changes.Clear();

        session.Undo();
        session.Undo();

        Assert.Equal(1, session.SelectedZoomIndex);
        Assert.Equal(2, session.SelectedZoom!.Scale, Precision);
        Assert.Equal(7, session.SelectedZoom.End, Precision);
        Assert.Equal(new[] { Edited, Edited }, Changes);

        session.Redo();
        session.Redo();
        Assert.Equal(1, session.SelectedZoomIndex);
        Assert.Equal(9, session.SelectedZoom!.End, Precision);
    }

    [Fact]
    public async Task AnEditToAnotherZoom_OrToSomethingElse_KeepsTheSelectionWhereItIs()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(1, 3), Zoom(5, 7)));
        session.SelectZoom(0);
        Changes.Clear();

        session.SetZoomScale(1, 4);
        session.SetCanvasPadding(0.2);
        session.SetTrimStart(2);

        Assert.Equal(0, session.SelectedZoomIndex);
        Assert.DoesNotContain(Changes, change => (change & StudioEditorChanges.Selection) != 0);
    }

    // Suggestions

    [Fact]
    public async Task NewSuggestions_KeepTheSelectionOnAZoomTheUserMade_AndOnASuggestionThatIsStillThere()
    {
        var id = CreateProjectWithZooms(Zoom(7, 9));
        Projects.SaveEvents(id, new StudioEvents
        {
            Clicks = [new StudioClickEvent { T = 3, X = 0.2, Y = 0.3 }],
        });
        var session = await OpenAsync(id);
        session.SelectZoom(0);

        // The suggestion is from 2.4 to 4.5, before the user's zoom, which moves one place on.
        Assert.True(session.ApplyZoomSuggestions());
        Assert.Equal(1, session.SelectedZoomIndex);
        Assert.Equal(7, session.SelectedZoom!.Start, Precision);

        // Asked again there is nothing new, and the selected suggestion stays selected.
        session.SelectZoom(0);
        Assert.False(session.ApplyZoomSuggestions());
        Assert.Equal(0, session.SelectedZoomIndex);

        Assert.True(session.RemoveZoomSuggestions());
        Assert.Null(session.SelectedZoomIndex);
        Assert.Equal(7, Assert.Single(session.Project!.Zooms).Start, Precision);
        Assert.False(session.RemoveZoomSuggestions());

        // One undo step each.
        session.Undo();
        Assert.Equal(2, session.Project.Zooms.Length);
        session.Undo();
        Assert.Single(session.Project.Zooms);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public async Task ARecordingSaysWhetherItHasClicksAndPointerPositions()
    {
        var plain = await OpenAsync(CreateProject());

        Assert.False(plain.HasClicks);
        Assert.False(plain.HasPointerPositions);

        var id = CreateProject();
        Projects.SaveEvents(id, new StudioEvents
        {
            Clicks = [new StudioClickEvent { T = 3, X = 0.2, Y = 0.3 }],
            Cursor = [new StudioCursorSample { T = 1, X = 0.5, Y = 0.5 }],
        });
        var session = await OpenAsync(id);

        Assert.True(session.HasClicks);
        Assert.True(session.HasPointerPositions);
    }

    // Refused edits

    [Fact]
    public async Task WhileExporting_ZoomsCanBeSelected_ButNotAddedMovedOrSteppedTo()
    {
        var session = await OpenAsync(CreateProjectWithZooms(Zoom(1, 3), Zoom(5, 7)));
        session.SelectZoom(0);
        session.Scrub(4);
        var export = session.ExportAsync(() => ExportPath, default);
        Preview.Calls.Clear();

        Assert.Equal(new StudioZoomEditResult(false, null), session.AddZoomAtPlayhead());
        Assert.Equal(new StudioZoomEditResult(false, 0), session.MoveZoom(0, 2));
        Assert.Equal(new StudioZoomEditResult(false, 0), session.SetZoomFocusOnPad(0, 0.1, 0.1));
        Assert.Equal(new StudioZoomEditResult(false, 0), session.RemoveSelectedZoom());
        Assert.False(session.RemoveZoomSuggestions());
        Assert.False(session.SelectNextZoom());
        Assert.False(session.SelectPreviousZoom());
        Assert.False(session.SelectAndShowZoom(1));
        session.SetScreenCropInset(StudioCropEdge.Left, 0.2);

        Assert.Equal(0, session.SelectedZoomIndex);
        Assert.Equal(2, session.Project!.Zooms.Length);
        Assert.Null(session.Project.Screen.Crop);
        Assert.Equal(4, session.Playhead, Precision);
        Assert.Empty(Preview.Calls);

        session.SelectZoom(1);
        Assert.Equal(1, session.SelectedZoomIndex);

        session.CancelExport();
        await FinishAsync(export);
    }

    // Crops

    [Fact]
    public async Task ACropEdge_ReachesThePreview_AndADragIsOneUndoStep()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        Preview.Calls.Clear();

        session.BeginGesture();
        session.SetScreenCropInset(StudioCropEdge.Left, 0.1);
        session.SetScreenCropInset(StudioCropEdge.Left, 0.2);
        session.EndGesture();
        session.SetCameraCropInset(StudioCropEdge.Bottom, 0.25);

        Assert.Equal(new[] { "UpdateProject", "UpdateProject", "UpdateProject" }, Preview.Calls);
        Assert.Equal(new StudioCropInsets(0.2, 0, 0, 0), session.Model!.ScreenCropInsets);
        Assert.Equal(new StudioCropInsets(0, 0, 0, 0.25), session.Model.CameraCropInsets);
        Assert.Equal(0.2, Preview.LastProject!.Screen.Crop!.X, Precision);
        Assert.Equal(0.75, Preview.LastProject.Camera.Crop!.Height, Precision);

        session.Undo();
        Assert.Null(session.Project!.Camera.Crop);
        session.Undo();
        Assert.Null(session.Project.Screen.Crop);
        Assert.False(session.CanUndo);
    }

    // Helpers

    private string CreateProjectWithZooms(params StudioZoom[] zooms)
    {
        var id = CreateProject();
        Projects.Save(Projects.Load(id) with { Zooms = zooms });
        return id;
    }

    private static StudioZoom Zoom(double start, double end, double easeIn = 0.5) => new()
    {
        Start = start,
        End = end,
        EaseIn = easeIn,
    };
}
