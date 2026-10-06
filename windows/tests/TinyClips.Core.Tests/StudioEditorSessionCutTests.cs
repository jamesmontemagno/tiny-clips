using TinyClips.Core.Models;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>Cuts in the editor session: adding and selecting one, its ends, and playing over it.</summary>
public sealed class StudioEditorSessionCutTests : StudioEditorSessionTestBase
{
    private const StudioEditorChanges Edited = StudioEditorChanges.Project | StudioEditorChanges.Playback;

    // Adding

    [Fact]
    public async Task AddCutAtPlayhead_SelectsTheCut_AndLeavesThePlayheadOnItsStart()
    {
        var session = await OpenAsync(CreateProject());
        session.Scrub(4);
        Changes.Clear();
        Preview.Seeks.Clear();
        Assert.True(session.CanAddCutAtPlayhead);
        Assert.Equal(0, session.CutCount);

        Assert.Equal(new StudioCutEditResult(true, 0), session.AddCutAtPlayhead());

        Assert.Equal(1, session.CutCount);
        Assert.Equal(0, session.SelectedCutIndex);
        Assert.Equal(4, session.SelectedCut!.Start, Precision);
        Assert.Equal(5, session.SelectedCut.End, Precision);
        Assert.Equal(0, session.GetCutIndexAt(4.5));
        Assert.Single(Preview.LastProject!.Edits.Cuts);
        Assert.Equal(4, session.Playhead, Precision);
        Assert.Empty(Preview.Seeks);
        Assert.Equal(new[] { Edited | StudioEditorChanges.Selection, StudioEditorChanges.Inspector }, Changes);
        Assert.True(session.HasUnsavedEdits);

        // The video is a second shorter, and its time stands still inside the cut.
        Assert.Equal("0:04.0 / 0:09.0", session.TimeText);
        session.Scrub(4.6);
        Assert.Equal("0:04.0 / 0:09.0", session.TimeText);

        // Asked for again inside the cut, the cut that is there is the answer.
        Changes.Clear();
        Assert.Equal(new StudioCutEditResult(false, 0), session.AddCutAtPlayhead());
        Assert.Empty(Changes);
        Assert.Equal(1, session.CutCount);

        // Undo takes the cut away, and the selection with it.
        session.Undo();
        Assert.Equal(0, session.CutCount);
        Assert.Null(session.SelectedCutIndex);
        Assert.Null(session.SelectedCut);
        Assert.Equal(new[] { Edited | StudioEditorChanges.Selection }, Changes);
    }

    [Fact]
    public async Task AddCut_WhereACutAlreadyIs_SelectsThatOne()
    {
        var session = await OpenAsync(CreateProjectWithCuts((2, 3), (5, 6)));
        Changes.Clear();

        Assert.Equal(new StudioCutEditResult(false, 1), session.AddCut(5.5));

        Assert.Equal(1, session.SelectedCutIndex);
        Assert.Equal(new[] { StudioEditorChanges.Selection, StudioEditorChanges.Inspector }, Changes);
        Assert.False(session.HasUnsavedEdits);

        // Where no cut fits, nothing is selected and nothing changes.
        Changes.Clear();
        Assert.Equal(new StudioCutEditResult(false, null), session.AddCut(9.95));
        Assert.Equal(1, session.SelectedCutIndex);
        Assert.Empty(Changes);
        session.Scrub(9.95);
        Assert.False(session.CanAddCutAtPlayhead);
    }

    // One selection

    [Fact]
    public async Task AZoomOrACut_IsSelected_NeverBoth()
    {
        var session = await OpenAsync(CreateProject());
        session.AddZoom(1);
        Assert.Equal(0, session.SelectedZoomIndex);

        // Adding a cut selects it and lets go of the zoom.
        Changes.Clear();
        session.AddCut(6);
        Assert.Equal(0, session.SelectedCutIndex);
        Assert.Null(session.SelectedZoomIndex);
        Assert.Equal(new[] { Edited | StudioEditorChanges.Selection, StudioEditorChanges.Inspector }, Changes);

        // Selecting one lets go of the other, and says so once.
        Changes.Clear();
        session.SelectZoom(0);
        Assert.Equal(0, session.SelectedZoomIndex);
        Assert.Null(session.SelectedCutIndex);
        session.SelectCut(0);
        Assert.Equal(0, session.SelectedCutIndex);
        Assert.Null(session.SelectedZoomIndex);
        Assert.Equal(
            new[] { StudioEditorChanges.Selection, StudioEditorChanges.Inspector, StudioEditorChanges.Selection, StudioEditorChanges.Inspector },
            Changes);

        // What is selected already, and what is not there, change nothing.
        Changes.Clear();
        session.SelectCut(0);
        session.SelectZoom(5);
        Assert.Equal(0, session.SelectedCutIndex);
        Assert.Empty(Changes);

        session.SelectCut(null);
        Assert.Null(session.SelectedCutIndex);
        Assert.Equal(new[] { StudioEditorChanges.Selection }, Changes);
        session.SelectZoom(0);
        session.SelectCut(7);
        Assert.Equal(0, session.SelectedZoomIndex);

        // Adding a zoom lets go of the cut the same way.
        session.SelectCut(0);
        session.AddZoom(8);
        Assert.Equal(1, session.SelectedZoomIndex);
        Assert.Null(session.SelectedCutIndex);
    }

    [Fact]
    public async Task SelectNothing_LetsGoOfAZoomOrACut_AndSaysSoOnce()
    {
        var session = await OpenAsync(CreateProjectWithCuts((2, 3), (5, 6)));
        session.AddZoom(7);
        session.Scrub(4);
        Preview.Seeks.Clear();

        // Selecting no zoom leaves a selected cut alone, which is why there is this.
        session.SelectCut(1);
        session.SelectZoom(null);
        Assert.Equal(1, session.SelectedCutIndex);

        Changes.Clear();
        session.SelectNothing();
        Assert.Null(session.SelectedCutIndex);
        Assert.Null(session.SelectedZoomIndex);
        Assert.Equal(new[] { StudioEditorChanges.Selection }, Changes);

        // With nothing selected it says nothing.
        Changes.Clear();
        session.SelectNothing();
        Assert.Empty(Changes);

        session.SelectZoom(0);
        Changes.Clear();
        session.SelectNothing();
        Assert.Null(session.SelectedZoomIndex);
        Assert.Null(session.SelectedCutIndex);
        Assert.Equal(new[] { StudioEditorChanges.Selection }, Changes);

        // The playhead stays, and nothing was edited beyond the zoom that was added.
        Assert.Equal(4, session.Playhead, Precision);
        Assert.Empty(Preview.Seeks);
        Assert.Equal(2, session.CutCount);
        Assert.Single(session.Project!.Zooms);
    }

    [Fact]
    public async Task TheSelection_StaysOnItsCut()
    {
        var session = await OpenAsync(CreateProjectWithCuts((2, 3), (5, 6), (8, 9)));
        session.SelectCut(1);

        // The cut before it goes: it is the first now.
        Assert.Equal(new StudioCutEditResult(true, null), session.RemoveCut(0));
        Assert.Equal(0, session.SelectedCutIndex);
        Assert.Equal(5, session.SelectedCut!.Start, Precision);

        // Undo brings that cut back, and the selection is on the same cut still.
        session.Undo();
        Assert.Equal(1, session.SelectedCutIndex);
        Assert.Equal(5, session.SelectedCut!.Start, Precision);

        // An edit to the selected cut keeps it selected, and an edit to another one too.
        Assert.Equal(new StudioCutEditResult(true, 1), session.MoveCut(1, 6.5));
        Assert.Equal(1, session.SelectedCutIndex);
        session.MoveCut(2, 8.5);
        Assert.Equal(1, session.SelectedCutIndex);
        Assert.Equal(6.5, session.SelectedCut!.Start, Precision);

        // A zoom that is selected is not let go by an edit to a cut that is not.
        session.AddZoom(0.5);
        Assert.Equal(0, session.SelectedZoomIndex);
        session.MoveCut(0, 1.5);
        Assert.Equal(0, session.SelectedZoomIndex);
        Assert.Null(session.SelectedCutIndex);

        // Deleting the selected cut lets go of it.
        session.SelectCut(1);
        Changes.Clear();
        Assert.Equal(new StudioCutEditResult(true, null), session.RemoveSelectedCut());
        Assert.Null(session.SelectedCutIndex);
        Assert.Equal(2, session.CutCount);
        Assert.Equal(new[] { Edited | StudioEditorChanges.Selection }, Changes);
        Assert.Equal(new StudioCutEditResult(false, null), session.RemoveSelectedCut());
        Assert.Equal(2, session.CutCount);
    }

    // The ends

    [Fact]
    public async Task SetCutStartAndEnd_ShowThePictureThere()
    {
        var session = await OpenAsync(CreateProjectWithCuts((2, 3), (5, 6), (8, 9)));
        Preview.Seeks.Clear();

        Assert.Equal(new StudioCutEditResult(true, 1), session.SetCutEnd(1, 7));
        Assert.Equal(7, session.Playhead, Precision);
        Assert.Equal(7, Preview.Seeks[^1], Precision);

        // An end that runs into the next cut stops there, and that is where the playhead goes.
        session.SetCutEnd(1, 9.5);
        Assert.Equal(8, session.Project!.Edits.Cuts[1].End, Precision);
        Assert.Equal(8, session.Playhead, Precision);

        Assert.Equal(new StudioCutEditResult(true, 1), session.SetCutStart(1, 4));
        Assert.Equal(4, session.Playhead, Precision);
        session.SetCutStart(1, 1);
        Assert.Equal(3, session.Project.Edits.Cuts[1].Start, Precision);
        Assert.Equal(3, session.Playhead, Precision);

        // At the playhead, the playhead stays.
        session.Scrub(4.5);
        Preview.Seeks.Clear();
        Assert.Equal(new StudioCutEditResult(true, 1), session.SetCutStartAtPlayhead(1));
        Assert.Equal(4.5, session.Project.Edits.Cuts[1].Start, Precision);
        Assert.Empty(Preview.Seeks);
        session.Scrub(6);
        Preview.Seeks.Clear();
        Assert.Equal(new StudioCutEditResult(true, 1), session.SetCutEndAtPlayhead(1));
        Assert.Equal(6, session.Project.Edits.Cuts[1].End, Precision);
        Assert.Empty(Preview.Seeks);

        // A whole cut is moved without the playhead.
        Assert.Equal(new StudioCutEditResult(true, 1), session.MoveCut(1, 5));
        Assert.Equal(5, session.Project.Edits.Cuts[1].Start, Precision);
        Assert.Equal(6.5, session.Project.Edits.Cuts[1].End, Precision);
        Assert.Empty(Preview.Seeks);
        Assert.Equal(6, session.Playhead, Precision);

        // No such cut: nothing moves.
        Assert.Equal(new StudioCutEditResult(false, null), session.SetCutStart(7, 1));
        Assert.Equal(new StudioCutEditResult(false, null), session.SetCutEnd(7, 1));
        Assert.Empty(Preview.Seeks);
    }

    // From cut to cut

    [Fact]
    public async Task SelectNextAndPreviousCut_GoToWhereEachStarts()
    {
        var session = await OpenAsync(CreateProjectWithCuts((2, 3), (5, 6), (8, 9)));

        Assert.True(session.SelectNextCut());
        Assert.Equal(0, session.SelectedCutIndex);
        Assert.Equal(2, session.Playhead, Precision);
        Assert.True(session.SelectNextCut());
        Assert.True(session.SelectNextCut());
        Assert.Equal(2, session.SelectedCutIndex);
        Assert.Equal(8, session.Playhead, Precision);
        Assert.False(session.SelectNextCut());
        Assert.Equal(2, session.SelectedCutIndex);

        Assert.True(session.SelectPreviousCut());
        Assert.Equal(1, session.SelectedCutIndex);
        Assert.Equal(5, session.Playhead, Precision);

        // With nothing selected, the cut the playhead is in.
        session.SelectCut(null);
        session.Scrub(5.5);
        Assert.True(session.SelectPreviousCut());
        Assert.Equal(1, session.SelectedCutIndex);
        Assert.Equal(5, session.Playhead, Precision);
        Assert.False(session.HasUnsavedEdits);

        var none = await OpenAsync(CreateProject());
        Assert.False(none.SelectNextCut());
        Assert.False(none.SelectPreviousCut());
        Assert.False(none.SelectAndShowCut(0));
    }

    [Fact]
    public async Task ACutIsSelectedAndShownByItsPlace_FromAnywhere()
    {
        var session = await OpenAsync(CreateProjectWithCuts((2, 3), (5, 6), (8, 9)));
        session.Scrub(4);
        Preview.Seeks.Clear();
        Changes.Clear();

        // The last one, whatever is selected and wherever the playhead is.
        Assert.True(session.SelectAndShowCut(2));
        Assert.Equal(2, session.SelectedCutIndex);
        Assert.Equal(8, session.Playhead, Precision);
        Assert.Equal(new[] { StudioEditorChanges.Selection, StudioEditorChanges.Inspector, StudioEditorChanges.Playback }, Changes);

        Assert.True(session.SelectAndShowCut(0));
        Assert.Equal(0, session.SelectedCutIndex);
        Assert.Equal(2, session.Playhead, Precision);

        // The selected cut again: it stays selected, and the playhead goes back to its start.
        session.Scrub(2.5);
        Changes.Clear();
        Assert.True(session.SelectAndShowCut(0));
        Assert.Equal(0, session.SelectedCutIndex);
        Assert.Equal(2, session.Playhead, Precision);
        Assert.Equal(new[] { StudioEditorChanges.Playback }, Changes);
        Assert.Equal(new[] { 8.0, 2.0, 2.5, 2.0 }, Preview.Seeks);
        Assert.False(session.HasUnsavedEdits);
    }

    [Fact]
    public async Task APlaceWithNoCut_IsNotSelectedOrShown()
    {
        var session = await OpenAsync(CreateProjectWithCuts((2, 3), (5, 6)));
        session.SelectCut(1);
        session.Scrub(4);
        Preview.Seeks.Clear();
        Changes.Clear();

        Assert.False(session.SelectAndShowCut(2));
        Assert.False(session.SelectAndShowCut(-1));
        Assert.False(session.SelectAndShowCut(null));

        Assert.Equal(1, session.SelectedCutIndex);
        Assert.Equal(4, session.Playhead, Precision);
        Assert.Empty(Changes);
        Assert.Empty(Preview.Seeks);
    }

    // Playing

    [Fact]
    public async Task Playback_JumpsOverACut_AndEndsWhereTheVideoDoes()
    {
        var session = await OpenAsync(CreateProjectWithCuts((2, 3), (9, 10)));
        session.Scrub(1);
        session.TogglePlayback();
        Preview.Seeks.Clear();

        Preview.RaisePosition(1.5);
        Pump();
        Assert.Equal(1.5, session.Playhead, Precision);
        Assert.Empty(Preview.Seeks);

        // The cut is reached: playback goes on from its end.
        Preview.RaisePosition(2);
        Pump();
        Assert.Equal(3, Assert.Single(Preview.Seeks), Precision);
        Assert.Equal(3, session.Playhead, Precision);
        Assert.True(session.IsPlaying);

        // A position from before the jump landed changes nothing.
        Preview.RaisePosition(2.03);
        Pump();
        Assert.Single(Preview.Seeks);
        Assert.Equal(3, session.Playhead, Precision);
        Assert.True(session.IsPlaying);

        Preview.RaisePosition(3.03);
        Pump();
        Assert.Equal(3.03, session.Playhead, Precision);
        Assert.Single(Preview.Seeks);

        // The second cut runs up to the end of the recording, so the video ends where it starts.
        Preview.RaisePosition(9);
        Pump();
        Assert.False(session.IsPlaying);
        Assert.Equal(9, session.Playhead, Precision);
        Assert.Equal(9, Preview.Seeks[^1], Precision);

        // Play at the end starts over.
        session.TogglePlayback();
        Assert.True(session.IsPlaying);
        Assert.Equal(0, Preview.Seeks[^1], Precision);

        // Play inside a cut starts after it.
        session.Scrub(2.5);
        session.TogglePlayback();
        Assert.Equal(3, Preview.Seeks[^1], Precision);
        Assert.Equal(3, session.Playhead, Precision);

        // The same cut is jumped again when playback comes to it a second time, also when the
        // first jump was cut short by a pause.
        session.Scrub(1.9);
        session.TogglePlayback();
        Preview.RaisePosition(2);
        Pump();
        session.Pause();
        session.Scrub(1.9);
        session.TogglePlayback();
        Preview.Seeks.Clear();
        Preview.RaisePosition(2);
        Pump();
        Assert.Equal(3, Assert.Single(Preview.Seeks), Precision);
    }

    [Fact]
    public async Task PreviewStoppingByItself_AFrameBeforeACutThatEndsTheVideo_LeavesThePlayheadWhereTheVideoEnds()
    {
        var session = await OpenAsync(CreateProjectWithCuts((9, 10)));
        session.Scrub(8);
        session.TogglePlayback();
        Preview.Seeks.Clear();

        Preview.Position = 9 - Frame;
        Preview.RaiseIsPlaying(false);
        Pump();

        Assert.False(session.IsPlaying);
        Assert.Equal(9, session.Playhead, Precision);
        Assert.Equal(9, Assert.Single(Preview.Seeks), Precision);
    }

    // While the project cannot be edited

    [Fact]
    public async Task WhileExporting_CutsAreLeftAlone()
    {
        var session = await OpenAsync(CreateProjectWithCuts((2, 3), (5, 6)));
        session.Scrub(7);
        var export = session.ExportAsync(() => ExportPath, default);
        Assert.True(session.IsExporting);
        Preview.Seeks.Clear();

        Assert.False(session.CanAddCutAtPlayhead);
        Assert.Equal(new StudioCutEditResult(false, null), session.AddCutAtPlayhead());
        Assert.Equal(new StudioCutEditResult(false, 0), session.RemoveCut(0));
        Assert.Equal(new StudioCutEditResult(false, 0), session.SetCutEnd(0, 4));
        Assert.Equal(new StudioCutEditResult(false, 0), session.SetCutStart(0, 1));
        Assert.Equal(new StudioCutEditResult(false, 1), session.MoveCut(1, 6));

        // There is a cut before the playhead to step to, and the playhead does not go there.
        Assert.False(session.SelectPreviousCut());
        Assert.False(session.SelectAndShowCut(0));
        Assert.Null(session.SelectedCutIndex);
        Assert.Equal(7, session.Playhead, Precision);
        Assert.Empty(Preview.Seeks);
        Assert.Equal(2, session.CutCount);
        Assert.Equal(3, session.Project!.Edits.Cuts[0].End, Precision);

        Exporter.Complete();
        await FinishAsync(export);
    }

    // Keys

    [Fact]
    public void TheXKey_AddsACut_AndDeleteRemovesTheSelectedOne()
    {
        Assert.Equal(StudioShortcutAction.AddCut, StudioShortcuts.Resolve(Press(StudioShortcutKey.X)));

        // Held down, with a modifier, or while text is being typed it does nothing.
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.X) with { IsRepeat = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.X) with { IsControlDown = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.X) with { IsShiftDown = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.X) with { IsAltDown = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.X) with { IsTextInputFocused = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.X) with { IsTypeToSearchFocused = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.X) with { IsExporting = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.X) with { IsReady = false }));

        // Delete removes the selected cut, as it does a selected zoom. A scene that has the focus
        // comes first.
        Assert.Equal(StudioShortcutAction.RemoveSelectedCut, StudioShortcuts.Resolve(Press(StudioShortcutKey.Delete) with { HasSelectedCut = true }));
        Assert.Equal(StudioShortcutAction.RemoveSelectedZoom, StudioShortcuts.Resolve(Press(StudioShortcutKey.Delete) with { HasSelectedZoom = true }));
        Assert.Equal(
            StudioShortcutAction.RemoveCurrentScene,
            StudioShortcuts.Resolve(Press(StudioShortcutKey.Delete) with { HasSelectedCut = true, IsSceneFocused = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.Delete)));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.Delete) with { HasSelectedCut = true, IsRepeat = true }));
    }

    // Helpers

    private string CreateProjectWithCuts(params (double Start, double End)[] cuts)
    {
        var id = CreateProject();
        var project = Projects.Load(id);
        Projects.Save(project with
        {
            Edits = project.Edits with { Cuts = [.. cuts.Select(cut => new StudioTimeRange { Start = cut.Start, End = cut.End })] },
        });
        return id;
    }

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
