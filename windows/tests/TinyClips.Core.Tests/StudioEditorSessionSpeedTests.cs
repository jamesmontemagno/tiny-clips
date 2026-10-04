using TinyClips.Core.Models;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>Speed changes in the editor session: adding and selecting one, its ends, and its rate.</summary>
public sealed class StudioEditorSessionSpeedTests : StudioEditorSessionTestBase
{
    private const StudioEditorChanges Edited = StudioEditorChanges.Project | StudioEditorChanges.Playback;

    // Adding

    [Fact]
    public async Task AddSpeedAtPlayhead_SelectsTheSpeedChange_AndLeavesThePlayheadOnItsStart()
    {
        var session = await OpenAsync(CreateProject());
        session.Scrub(4);
        Changes.Clear();
        Preview.Seeks.Clear();
        Assert.True(session.CanAddSpeedAtPlayhead);
        Assert.Equal(0, session.SpeedCount);

        Assert.Equal(new StudioSpeedEditResult(true, 0), session.AddSpeedAtPlayhead());

        Assert.Equal(1, session.SpeedCount);
        Assert.Equal(0, session.SelectedSpeedIndex);
        Assert.Equal(4, session.SelectedSpeed!.Start, Precision);
        Assert.Equal(6, session.SelectedSpeed.End, Precision);
        Assert.Equal(2, session.SelectedSpeed.Rate, Precision);
        Assert.Equal(0, session.GetSpeedIndexAt(5.5));
        Assert.Null(session.GetSpeedIndexAt(6));
        Assert.Single(Preview.LastProject!.Edits.Speed);
        Assert.Equal(4, session.Playhead, Precision);
        Assert.Empty(Preview.Seeks);
        Assert.Equal(new[] { Edited | StudioEditorChanges.Selection }, Changes);
        Assert.True(session.HasUnsavedEdits);

        // The video is a second shorter, and its time passes half as fast inside the stretch.
        Assert.Equal("0:04.0 / 0:09.0", session.TimeText);
        session.Scrub(5);
        Assert.Equal("0:04.5 / 0:09.0", session.TimeText);

        // Asked for again inside it, the speed change that is there is the answer.
        Changes.Clear();
        Assert.Equal(new StudioSpeedEditResult(false, 0), session.AddSpeedAtPlayhead());
        Assert.Empty(Changes);
        Assert.Equal(1, session.SpeedCount);

        // Undo takes it away, and the selection with it.
        session.Undo();
        Assert.Equal(0, session.SpeedCount);
        Assert.Null(session.SelectedSpeedIndex);
        Assert.Null(session.SelectedSpeed);
        Assert.Equal(new[] { Edited | StudioEditorChanges.Selection }, Changes);
    }

    [Fact]
    public async Task AddSpeed_WhereOneAlreadyIs_SelectsThatOne()
    {
        var session = await OpenAsync(CreateProjectWithSpeed((2, 3, 2), (5, 6, 4)));
        Changes.Clear();

        Assert.Equal(new StudioSpeedEditResult(false, 1), session.AddSpeed(5.5));

        Assert.Equal(1, session.SelectedSpeedIndex);
        Assert.Equal(new[] { StudioEditorChanges.Selection }, Changes);
        Assert.False(session.HasUnsavedEdits);

        // Where none fits, nothing is selected and nothing changes.
        Changes.Clear();
        Assert.Equal(new StudioSpeedEditResult(false, null), session.AddSpeed(9.95));
        Assert.Equal(1, session.SelectedSpeedIndex);
        Assert.Empty(Changes);
        session.Scrub(9.95);
        Assert.False(session.CanAddSpeedAtPlayhead);
    }

    // One selection

    [Fact]
    public async Task AZoomACutOrASpeedChange_IsSelected_NeverTwoOfThem()
    {
        var session = await OpenAsync(CreateProject());
        session.AddZoom(1);
        Assert.Equal(0, session.SelectedZoomIndex);

        // Adding a speed change selects it and lets go of the zoom.
        Changes.Clear();
        session.AddSpeed(6);
        Assert.Equal(0, session.SelectedSpeedIndex);
        Assert.Null(session.SelectedZoomIndex);
        Assert.Equal(new[] { Edited | StudioEditorChanges.Selection }, Changes);

        // Adding a cut lets go of the speed change, and adding a zoom of the cut.
        session.AddCut(9);
        Assert.Equal(0, session.SelectedCutIndex);
        Assert.Null(session.SelectedSpeedIndex);
        session.SelectSpeed(0);
        session.AddZoom(4.5);
        Assert.Equal(1, session.SelectedZoomIndex);
        Assert.Null(session.SelectedSpeedIndex);
        Assert.Null(session.SelectedCutIndex);

        // Selecting one lets go of the others, and says so once.
        Changes.Clear();
        session.SelectSpeed(0);
        Assert.Equal((null, null, 0), (session.SelectedZoomIndex, session.SelectedCutIndex, session.SelectedSpeedIndex));
        session.SelectCut(0);
        Assert.Equal((null, 0, null), (session.SelectedZoomIndex, session.SelectedCutIndex, session.SelectedSpeedIndex));
        session.SelectSpeed(0);
        session.SelectZoom(0);
        Assert.Equal((0, null, null), (session.SelectedZoomIndex, session.SelectedCutIndex, session.SelectedSpeedIndex));
        Assert.Equal(Enumerable.Repeat(StudioEditorChanges.Selection, 4), Changes);

        // What is selected already, and what is not there, change nothing.
        session.SelectSpeed(0);
        Changes.Clear();
        session.SelectSpeed(0);
        session.SelectZoom(5);
        session.SelectCut(5);
        Assert.Equal(0, session.SelectedSpeedIndex);
        Assert.Empty(Changes);

        // Selecting none of its own kind lets go of a speed change, and leaves the others alone.
        session.SelectZoom(null);
        session.SelectCut(null);
        Assert.Equal(0, session.SelectedSpeedIndex);
        session.SelectSpeed(null);
        Assert.Null(session.SelectedSpeedIndex);
        Assert.Equal(new[] { StudioEditorChanges.Selection }, Changes);
        session.SelectZoom(0);
        session.SelectSpeed(7);
        session.SelectSpeed(null);
        Assert.Equal(0, session.SelectedZoomIndex);
    }

    [Fact]
    public async Task SelectNothing_LetsGoOfASpeedChange_AndSaysSoOnce()
    {
        var session = await OpenAsync(CreateProjectWithSpeed((2, 3, 2), (5, 6, 4)));
        session.Scrub(4);
        Preview.Seeks.Clear();
        session.SelectSpeed(1);

        Changes.Clear();
        session.SelectNothing();
        Assert.Null(session.SelectedSpeedIndex);
        Assert.Equal(new[] { StudioEditorChanges.Selection }, Changes);

        // With nothing selected it says nothing.
        Changes.Clear();
        session.SelectNothing();
        Assert.Empty(Changes);

        // The playhead stays, and nothing was edited.
        Assert.Equal(4, session.Playhead, Precision);
        Assert.Empty(Preview.Seeks);
        Assert.Equal(2, session.SpeedCount);
        Assert.False(session.HasUnsavedEdits);
    }

    [Fact]
    public async Task TheSelection_StaysOnItsSpeedChange()
    {
        var session = await OpenAsync(CreateProjectWithSpeed((2, 3, 2), (5, 6, 4), (8, 9, 0.5)));
        session.SelectSpeed(1);

        // The one before it goes: it is the first now.
        Assert.Equal(new StudioSpeedEditResult(true, null), session.RemoveSpeed(0));
        Assert.Equal(0, session.SelectedSpeedIndex);
        Assert.Equal(5, session.SelectedSpeed!.Start, Precision);

        // Undo brings that one back, and the selection is on the same speed change still.
        session.Undo();
        Assert.Equal(1, session.SelectedSpeedIndex);
        Assert.Equal(5, session.SelectedSpeed!.Start, Precision);

        // An edit to the selected one keeps it selected, and an edit to another one too.
        Assert.Equal(new StudioSpeedEditResult(true, 1), session.MoveSpeed(1, 6.5));
        Assert.Equal(1, session.SelectedSpeedIndex);
        Assert.Equal(new StudioSpeedEditResult(true, 1), session.SetSpeedRate(1, 8));
        Assert.Equal(1, session.SelectedSpeedIndex);
        session.MoveSpeed(2, 8.5);
        session.SetSpeedRate(0, 4);
        Assert.Equal(1, session.SelectedSpeedIndex);
        Assert.Equal(6.5, session.SelectedSpeed!.Start, Precision);
        Assert.Equal(8, session.SelectedSpeed.Rate, Precision);

        // Undoing the change of rate of another one leaves the selection where it is.
        session.Undo();
        Assert.Equal(2, session.Project!.Edits.Speed[0].Rate, Precision);
        Assert.Equal(1, session.SelectedSpeedIndex);

        // A zoom that is selected is not let go by an edit to a speed change that is not, and a
        // cut neither.
        session.AddZoom(0.5);
        Assert.Equal(0, session.SelectedZoomIndex);
        session.MoveSpeed(0, 1.5);
        session.SetSpeedRate(0, 4);
        Assert.Equal(0, session.SelectedZoomIndex);
        Assert.Null(session.SelectedSpeedIndex);
        session.AddCut(4);
        session.MoveSpeed(0, 2);
        Assert.Equal(0, session.SelectedCutIndex);
        Assert.Null(session.SelectedSpeedIndex);

        // A speed change that is selected is not let go by an edit to a zoom or a cut.
        session.SelectSpeed(2);
        session.MoveCut(0, 4.2);
        session.MoveZoom(0, 0.2);
        Assert.Equal(2, session.SelectedSpeedIndex);

        // Deleting the selected speed change lets go of it.
        session.SelectSpeed(1);
        Changes.Clear();
        Assert.Equal(new StudioSpeedEditResult(true, null), session.RemoveSelectedSpeed());
        Assert.Null(session.SelectedSpeedIndex);
        Assert.Equal(2, session.SpeedCount);
        Assert.Equal(2, session.Project!.Edits.Speed[0].Start, Precision);
        Assert.Equal(8.5, session.Project.Edits.Speed[1].Start, Precision);
        Assert.Equal(new[] { Edited | StudioEditorChanges.Selection }, Changes);
        Assert.Equal(new StudioSpeedEditResult(false, null), session.RemoveSelectedSpeed());
        Assert.Equal(2, session.SpeedCount);
    }

    // The ends and the rate

    [Fact]
    public async Task SetSpeedStartAndEnd_ShowThePictureThere_AndTheRateLeavesThePlayhead()
    {
        var session = await OpenAsync(CreateProjectWithSpeed((2, 3, 2), (5, 6, 4), (8, 9, 0.5)));
        Preview.Seeks.Clear();

        Assert.Equal(new StudioSpeedEditResult(true, 1), session.SetSpeedEnd(1, 7));
        Assert.Equal(7, session.Playhead, Precision);
        Assert.Equal(7, Preview.Seeks[^1], Precision);

        // An end that runs into the next one stops there, and that is where the playhead goes.
        session.SetSpeedEnd(1, 9.5);
        Assert.Equal(8, session.Project!.Edits.Speed[1].End, Precision);
        Assert.Equal(8, session.Playhead, Precision);

        Assert.Equal(new StudioSpeedEditResult(true, 1), session.SetSpeedStart(1, 4));
        Assert.Equal(4, session.Playhead, Precision);
        session.SetSpeedStart(1, 1);
        Assert.Equal(3, session.Project.Edits.Speed[1].Start, Precision);
        Assert.Equal(3, session.Playhead, Precision);

        // At the playhead, the playhead stays.
        session.Scrub(4.5);
        Preview.Seeks.Clear();
        Assert.Equal(new StudioSpeedEditResult(true, 1), session.SetSpeedStartAtPlayhead(1));
        Assert.Equal(4.5, session.Project.Edits.Speed[1].Start, Precision);
        Assert.Empty(Preview.Seeks);
        session.Scrub(6);
        Preview.Seeks.Clear();
        Assert.Equal(new StudioSpeedEditResult(true, 1), session.SetSpeedEndAtPlayhead(1));
        Assert.Equal(6, session.Project.Edits.Speed[1].End, Precision);
        Assert.Empty(Preview.Seeks);

        // A whole speed change is moved without the playhead, and its rate is set without it.
        Assert.Equal(new StudioSpeedEditResult(true, 1), session.MoveSpeed(1, 5));
        Assert.Equal(5, session.Project.Edits.Speed[1].Start, Precision);
        Assert.Equal(6.5, session.Project.Edits.Speed[1].End, Precision);
        Changes.Clear();
        Assert.Equal(new StudioSpeedEditResult(true, 1), session.SetSpeedRate(1, 0.5));
        Assert.Equal(0.5, session.Project.Edits.Speed[1].Rate, Precision);
        Assert.Equal(0.5, Preview.LastProject!.Edits.Speed[1].Rate, Precision);
        Assert.Equal(new[] { Edited }, Changes);
        Assert.Empty(Preview.Seeks);
        Assert.Equal(6, session.Playhead, Precision);

        // A rate that is none, and the rate it has, change nothing.
        Changes.Clear();
        Assert.Equal(new StudioSpeedEditResult(false, 1), session.SetSpeedRate(1, 1));
        Assert.Equal(new StudioSpeedEditResult(false, 1), session.SetSpeedRate(1, 0.5));
        Assert.Empty(Changes);

        // No such speed change: nothing moves.
        Assert.Equal(new StudioSpeedEditResult(false, null), session.SetSpeedStart(7, 1));
        Assert.Equal(new StudioSpeedEditResult(false, null), session.SetSpeedEnd(7, 1));
        Assert.Equal(new StudioSpeedEditResult(false, null), session.SetSpeedRate(7, 2));
        Assert.Empty(Preview.Seeks);
    }

    // From one to the next

    [Fact]
    public async Task SelectNextAndPreviousSpeed_GoToWhereEachStarts()
    {
        var session = await OpenAsync(CreateProjectWithSpeed((2, 3, 2), (5, 6, 4), (8, 9, 0.5)));

        Assert.True(session.SelectNextSpeed());
        Assert.Equal(0, session.SelectedSpeedIndex);
        Assert.Equal(2, session.Playhead, Precision);
        Assert.True(session.SelectNextSpeed());
        Assert.True(session.SelectNextSpeed());
        Assert.Equal(2, session.SelectedSpeedIndex);
        Assert.Equal(8, session.Playhead, Precision);
        Assert.False(session.SelectNextSpeed());
        Assert.Equal(2, session.SelectedSpeedIndex);

        Assert.True(session.SelectPreviousSpeed());
        Assert.Equal(1, session.SelectedSpeedIndex);
        Assert.Equal(5, session.Playhead, Precision);

        // With nothing selected, the one the playhead is in.
        session.SelectSpeed(null);
        session.Scrub(5.5);
        Assert.True(session.SelectPreviousSpeed());
        Assert.Equal(1, session.SelectedSpeedIndex);
        Assert.Equal(5, session.Playhead, Precision);
        Assert.False(session.HasUnsavedEdits);

        var none = await OpenAsync(CreateProject());
        Assert.False(none.SelectNextSpeed());
        Assert.False(none.SelectPreviousSpeed());
        Assert.False(none.SelectAndShowSpeed(0));
    }

    [Fact]
    public async Task ASpeedChangeIsSelectedAndShownByItsPlace_FromAnywhere()
    {
        var session = await OpenAsync(CreateProjectWithSpeed((2, 3, 2), (5, 6, 4), (8, 9, 0.5)));
        session.Scrub(4);
        Preview.Seeks.Clear();
        Changes.Clear();

        // The last one, whatever is selected and wherever the playhead is.
        Assert.True(session.SelectAndShowSpeed(2));
        Assert.Equal(2, session.SelectedSpeedIndex);
        Assert.Equal(8, session.Playhead, Precision);
        Assert.Equal(new[] { StudioEditorChanges.Selection, StudioEditorChanges.Playback }, Changes);

        Assert.True(session.SelectAndShowSpeed(0));
        Assert.Equal(0, session.SelectedSpeedIndex);
        Assert.Equal(2, session.Playhead, Precision);

        // The selected one again: it stays selected, and the playhead goes back to its start.
        session.Scrub(2.5);
        Changes.Clear();
        Assert.True(session.SelectAndShowSpeed(0));
        Assert.Equal(0, session.SelectedSpeedIndex);
        Assert.Equal(2, session.Playhead, Precision);
        Assert.Equal(new[] { StudioEditorChanges.Playback }, Changes);
        Assert.Equal(new[] { 8.0, 2.0, 2.5, 2.0 }, Preview.Seeks);
        Assert.False(session.HasUnsavedEdits);
    }

    [Fact]
    public async Task APlaceWithNoSpeedChange_IsNotSelectedOrShown()
    {
        var session = await OpenAsync(CreateProjectWithSpeed((2, 3, 2), (5, 6, 4)));
        session.SelectSpeed(1);
        session.Scrub(4);
        Preview.Seeks.Clear();
        Changes.Clear();

        Assert.False(session.SelectAndShowSpeed(2));
        Assert.False(session.SelectAndShowSpeed(-1));
        Assert.False(session.SelectAndShowSpeed(null));

        Assert.Equal(1, session.SelectedSpeedIndex);
        Assert.Equal(4, session.Playhead, Precision);
        Assert.Empty(Changes);
        Assert.Empty(Preview.Seeks);
    }

    // Playing

    [Fact]
    public async Task Playback_TellsThePreviewTheRateOfEachStretch_OnceForEach()
    {
        var session = await OpenAsync(CreateProjectWithSpeed((2, 3, 2), (5, 6, 0.5)));
        session.Scrub(1);
        Preview.Calls.Clear();

        // A preview opens at the recording's own speed, so before a stretch at that speed it is
        // told nothing.
        session.TogglePlayback();
        Assert.Equal(new[] { "Play" }, Preview.Calls);
        Preview.RaisePosition(1.5);
        Pump();
        Assert.Empty(Preview.Rates);

        // The first position inside a faster stretch sets its rate, and the next ones do not.
        Preview.RaisePosition(2);
        Pump();
        Assert.Equal(new[] { 2.0 }, Preview.Rates);
        Preview.RaisePosition(2.5);
        Pump();
        Preview.RaisePosition(2.97);
        Pump();
        Assert.Equal(new[] { 2.0 }, Preview.Rates);

        // Its end belongs to the stretch after it.
        Preview.RaisePosition(3);
        Pump();
        Assert.Equal(new[] { 2.0, 1.0 }, Preview.Rates);

        Preview.RaisePosition(5.2);
        Pump();
        Assert.Equal(new[] { 2.0, 1.0, 0.5 }, Preview.Rates);
        Preview.RaisePosition(6.1);
        Pump();
        Assert.Equal(new[] { 2.0, 1.0, 0.5, 1.0 }, Preview.Rates);

        // None of it stopped the preview or sent it anywhere.
        Assert.True(session.IsPlaying);
        Assert.Equal(6.1, session.Playhead, Precision);
        Assert.Equal(new[] { "Play", "Rate", "Rate", "Rate", "Rate" }, Preview.Calls);
    }

    [Fact]
    public async Task Play_SetsTheRateOfWhereItStarts_BeforeThePreviewPlays()
    {
        var session = await OpenAsync(CreateProjectWithSpeed((2, 3, 2), (5, 6, 0.5)));
        session.Scrub(2.5);
        Preview.Calls.Clear();

        session.TogglePlayback();
        Assert.Equal(new[] { "Rate", "Play" }, Preview.Calls);
        Assert.Equal(new[] { 2.0 }, Preview.Rates);

        // A preview keeps its rate while it is paused: started again there, it is not told again.
        session.TogglePlayback();
        Assert.False(session.IsPlaying);
        session.TogglePlayback();
        Assert.True(session.IsPlaying);
        Assert.Equal(new[] { 2.0 }, Preview.Rates);

        // Started somewhere else, it is told first.
        session.Scrub(4);
        Preview.Calls.Clear();
        session.TogglePlayback();
        Assert.Equal(new[] { "Rate", "Play" }, Preview.Calls);
        Assert.Equal(new[] { 2.0, 1.0 }, Preview.Rates);
    }

    [Fact]
    public async Task Play_FromJustBeforeTheTrimStart_TakesTheRateOfTheVideosFirstStretch()
    {
        var session = await OpenAsync(CreateProjectWithSpeed((2, 3, 4)));
        session.SetTrimStart(2);

        // Within half a frame of where the video starts, the preview is not sent there, and what
        // it plays first is the stretch that starts there.
        session.Scrub(2 - Frame / 4);
        Preview.Calls.Clear();
        session.TogglePlayback();
        Assert.Equal(new[] { "Rate", "Play" }, Preview.Calls);
        Assert.Equal(new[] { 4.0 }, Preview.Rates);
    }

    [Fact]
    public async Task Playback_OverACut_TakesTheRateOfTheStretchAfterIt_WithTheJump()
    {
        var id = CreateProjectWithSpeed((3, 4, 4));
        var project = Projects.Load(id);
        Projects.Save(project with
        {
            Edits = project.Edits with { Cuts = [new StudioTimeRange { Start = 2, End = 3 }] },
        });
        var session = await OpenAsync(id);
        session.Scrub(1);
        session.TogglePlayback();
        Preview.Calls.Clear();

        // The cut is reached: the preview is sent to its end and told the rate there at once.
        Preview.RaisePosition(2);
        Pump();
        Assert.Equal(new[] { "Seek", "Rate" }, Preview.Calls);
        Assert.Equal(3, Preview.Seeks[^1], Precision);
        Assert.Equal(new[] { 4.0 }, Preview.Rates);

        // Positions from before the jump landed are inside the cut. They change nothing.
        Preview.RaisePosition(2.03);
        Pump();
        Preview.RaisePosition(2.07);
        Pump();
        Assert.Equal(new[] { 4.0 }, Preview.Rates);

        Preview.RaisePosition(3.1);
        Pump();
        Assert.Equal(new[] { 4.0 }, Preview.Rates);
        Preview.RaisePosition(4);
        Pump();
        Assert.Equal(new[] { 4.0, 1.0 }, Preview.Rates);
        Assert.True(session.IsPlaying);
    }

    [Fact]
    public async Task Playback_FollowsAnEditToTheStretchItIsIn_AtTheNextPosition()
    {
        var session = await OpenAsync(CreateProjectWithSpeed((2, 6, 2)));
        session.Scrub(2.5);
        session.TogglePlayback();
        Assert.Equal(new[] { 2.0 }, Preview.Rates);

        Assert.Equal(new StudioSpeedEditResult(true, 0), session.SetSpeedRate(0, 4));
        Assert.True(session.IsPlaying);
        Assert.Equal(new[] { 2.0 }, Preview.Rates);
        Preview.RaisePosition(2.6);
        Pump();
        Assert.Equal(new[] { 2.0, 4.0 }, Preview.Rates);

        session.Undo();
        Preview.RaisePosition(2.8);
        Pump();
        Assert.Equal(new[] { 2.0, 4.0, 2.0 }, Preview.Rates);

        session.RemoveSpeed(0);
        Preview.RaisePosition(3);
        Pump();
        Assert.Equal(new[] { 2.0, 4.0, 2.0, 1.0 }, Preview.Rates);
        Assert.True(session.IsPlaying);
    }

    [Fact]
    public async Task Playback_ThatEndsInAFasterStretch_StartsOverAtTheRateOfTheStart()
    {
        var session = await OpenAsync(CreateProjectWithSpeed((8, 10, 2)));
        session.Scrub(9);
        session.TogglePlayback();
        Assert.Equal(new[] { 2.0 }, Preview.Rates);

        // The end of the video: the preview is paused and sent there, and its rate is left alone.
        Preview.RaisePosition(10);
        Pump();
        Assert.False(session.IsPlaying);
        Assert.Equal(new[] { 2.0 }, Preview.Rates);

        // Play starts over, at the speed the video starts with.
        Preview.Calls.Clear();
        session.TogglePlayback();
        Assert.Equal(new[] { "Seek", "Rate", "Play" }, Preview.Calls);
        Assert.Equal(0, Preview.Seeks[^1], Precision);
        Assert.Equal(new[] { 2.0, 1.0 }, Preview.Rates);
    }

    // While the project cannot be edited

    [Fact]
    public async Task WhileExporting_SpeedChangesAreLeftAlone()
    {
        var session = await OpenAsync(CreateProjectWithSpeed((2, 3, 2), (5, 6, 4)));
        session.Scrub(7);
        var export = session.ExportAsync(() => ExportPath, default);
        Assert.True(session.IsExporting);
        Preview.Seeks.Clear();

        Assert.False(session.CanAddSpeedAtPlayhead);
        Assert.Equal(new StudioSpeedEditResult(false, null), session.AddSpeedAtPlayhead());
        Assert.Equal(new StudioSpeedEditResult(false, 0), session.RemoveSpeed(0));
        Assert.Equal(new StudioSpeedEditResult(false, 0), session.SetSpeedEnd(0, 4));
        Assert.Equal(new StudioSpeedEditResult(false, 0), session.SetSpeedStart(0, 1));
        Assert.Equal(new StudioSpeedEditResult(false, 0), session.SetSpeedRate(0, 8));
        Assert.Equal(new StudioSpeedEditResult(false, 1), session.MoveSpeed(1, 6));

        // There is one before the playhead to step to, and the playhead does not go there.
        Assert.False(session.SelectPreviousSpeed());
        Assert.False(session.SelectAndShowSpeed(0));
        Assert.Null(session.SelectedSpeedIndex);
        Assert.Equal(7, session.Playhead, Precision);
        Assert.Empty(Preview.Seeks);
        Assert.Equal(2, session.SpeedCount);
        Assert.Equal(3, session.Project!.Edits.Speed[0].End, Precision);
        Assert.Equal(2, session.Project.Edits.Speed[0].Rate, Precision);

        Exporter.Complete();
        await FinishAsync(export);
    }

    // Keys

    [Fact]
    public void TheRKey_AddsASpeedChange_AndDeleteRemovesTheSelectedOne()
    {
        Assert.Equal(StudioShortcutAction.AddSpeed, StudioShortcuts.Resolve(Press(StudioShortcutKey.R)));

        // Held down, with a modifier, or while text is being typed it does nothing.
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.R) with { IsRepeat = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.R) with { IsControlDown = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.R) with { IsShiftDown = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.R) with { IsAltDown = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.R) with { IsTextInputFocused = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.R) with { IsTypeToSearchFocused = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.R) with { IsExporting = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.R) with { IsReady = false }));

        // Delete removes the selected speed change, as it does a selected zoom or cut. A scene
        // that has the focus comes first.
        Assert.Equal(StudioShortcutAction.RemoveSelectedSpeed, StudioShortcuts.Resolve(Press(StudioShortcutKey.Delete) with { HasSelectedSpeed = true }));
        Assert.Equal(StudioShortcutAction.RemoveSelectedCut, StudioShortcuts.Resolve(Press(StudioShortcutKey.Delete) with { HasSelectedCut = true }));
        Assert.Equal(StudioShortcutAction.RemoveSelectedZoom, StudioShortcuts.Resolve(Press(StudioShortcutKey.Delete) with { HasSelectedZoom = true }));
        Assert.Equal(
            StudioShortcutAction.RemoveCurrentScene,
            StudioShortcuts.Resolve(Press(StudioShortcutKey.Delete) with { HasSelectedSpeed = true, IsSceneFocused = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.Delete) with { HasSelectedSpeed = true, IsRepeat = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Press(StudioShortcutKey.Delete) with { HasSelectedSpeed = true, IsExporting = true }));
    }

    // Helpers

    private string CreateProjectWithSpeed(params (double Start, double End, double Rate)[] speed)
    {
        var id = CreateProject();
        var project = Projects.Load(id);
        Projects.Save(project with
        {
            Edits = project.Edits with
            {
                Speed = [.. speed.Select(entry => new StudioSpeedRange { Start = entry.Start, End = entry.End, Rate = entry.Rate })],
            },
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
