using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>Captions in the editor session: importing, selecting and editing them, and fixing them.</summary>
public sealed class StudioEditorSessionCaptionTests : StudioEditorSessionTestBase
{
    private const string TwoCues = "1\n00:00:01,000 --> 00:00:02,000\nFirst\n\n2\n00:00:03,000 --> 00:00:04,000\nSecond\n";

    [Fact]
    public async Task ImportCaptions_ReplacesTheCues_AsOneEditThatUndoTakesBack()
    {
        var session = await OpenAsync(CreateProject());

        var result = session.ImportCaptions(TwoCues, StudioCaptionTiming.Recording);

        Assert.Equal(new StudioCaptionImportResult(2, 0), result);
        Assert.Equal(["First", "Second"], session.Model!.Project.Captions.Cues.Select(static cue => cue.Text));
        Assert.True(session.HasUnsavedEdits);

        session.ImportCaptions("1\n00:00:05,000 --> 00:00:06,000\nThird\n", StudioCaptionTiming.Recording);
        Assert.Equal(["Third"], session.Model.Project.Captions.Cues.Select(static cue => cue.Text));

        session.Undo();
        Assert.Equal(["First", "Second"], session.Model.Project.Captions.Cues.Select(static cue => cue.Text));
        session.Undo();
        Assert.Empty(session.Model.Project.Captions.Cues);
    }

    [Fact]
    public async Task ImportCaptions_OfAFileWithNoCues_LeavesTheCaptionsAlone_AndCountsWhatItSkipped()
    {
        var session = await OpenAsync(CreateProject());
        session.ImportCaptions(TwoCues, StudioCaptionTiming.Recording);

        var result = session.ImportCaptions("Not a caption file.\n", StudioCaptionTiming.Recording);

        Assert.Equal(new StudioCaptionImportResult(0, 1), result);
        Assert.Equal(2, session.Model!.Project.Captions.Cues.Length);
    }

    [Fact]
    public async Task ImportCaptions_TimedToTheEditedVideo_GoesThroughTheTrim()
    {
        var session = await OpenAsync(CreateProject());
        session.Scrub(2);
        session.SetTrimStartAtPlayhead();

        session.ImportCaptions(TwoCues, StudioCaptionTiming.EditedVideo);

        var cues = session.Model!.Project.Captions.Cues;
        Assert.Equal(3, cues[0].Start, Precision);
        Assert.Equal(4, cues[0].End, Precision);
        Assert.Equal(1, session.GetResolvedCaptions()[0].Start, Precision);
    }

    [Fact]
    public async Task SelectCaption_MovesThePlayheadToItsStart_AndLetsGoOfTheTimelinesSelection()
    {
        var session = await OpenAsync(CreateProject());
        session.ImportCaptions(TwoCues, StudioCaptionTiming.Recording);
        session.Scrub(6);
        session.AddCutAtPlayhead();
        Assert.NotNull(session.SelectedCutIndex);

        Assert.True(session.SelectCaption(1));

        Assert.Equal(1, session.SelectedCaptionIndex);
        Assert.Equal("Second", session.SelectedCaption!.Text);
        Assert.Equal(3, session.Playhead, Precision);
        Assert.Null(session.SelectedCutIndex);

        Assert.False(session.SelectCaption(9));
        Assert.Null(session.SelectedCaptionIndex);
    }

    [Fact]
    public async Task EditingTheSelectedCaption_KeepsItSelected_EmptiesItsWords_AndIsOneUndoStep()
    {
        var id = CreateProject();
        Projects.Save(Projects.Load(id) with
        {
            Captions = new StudioCaptions
            {
                Cues = [new StudioCaptionCue { Id = "a", Start = 1, End = 2, Text = "Hi", Words = [new StudioCaptionWord { Start = 1, End = 2, Text = "Hi" }] }],
            },
        });
        var session = await OpenAsync(id);
        session.SelectCaption(0);

        session.SetSelectedCaptionText("  Hi  \n");
        Assert.False(session.HasUnsavedEdits);
        Assert.Single(session.SelectedCaption!.Words);

        session.SetSelectedCaptionText("Hello");

        Assert.Equal(0, session.SelectedCaptionIndex);
        Assert.Equal("Hello", session.SelectedCaption!.Text);
        Assert.Empty(session.SelectedCaption.Words);
        Assert.Equal("a", session.SelectedCaption.Id);

        // Undo brings back the old text and its words, and lets go of the selection.
        session.Undo();
        Assert.Null(session.SelectedCaptionIndex);
        Assert.Equal("Hi", session.Model!.Project.Captions.Cues[0].Text);
        Assert.Single(session.Model.Project.Captions.Cues[0].Words);
    }

    [Fact]
    public async Task RemoveSelectedCaption_TakesItOut_AndLetsGo()
    {
        var session = await OpenAsync(CreateProject());
        session.ImportCaptions(TwoCues, StudioCaptionTiming.Recording);
        session.SelectCaption(0);

        session.RemoveSelectedCaption();

        Assert.Equal(["Second"], session.Model!.Project.Captions.Cues.Select(static cue => cue.Text));
        Assert.Null(session.SelectedCaptionIndex);
        session.Undo();
        Assert.Equal(2, session.Model.Project.Captions.Cues.Length);
    }

    [Fact]
    public async Task FixCaptions_IsOneUndoStep_AndChangesNothingWhenThereIsNothingToFix()
    {
        var session = await OpenAsync(CreateProject());
        session.ImportCaptions("1\n00:00:01,000 --> 00:00:01,200\nHi\n\n2\n00:00:03,000 --> 00:00:03,200\nYes\n", StudioCaptionTiming.Recording);
        Assert.Equal(2, session.CheckCaptions().Length);

        Assert.Equal(2, session.FixCaptions());

        Assert.Empty(session.CheckCaptions());
        Assert.Equal(2, session.Model!.Project.Captions.Cues[0].End, Precision);
        Assert.Equal(0, session.FixCaptions());

        session.Undo();
        Assert.Equal(2, session.CheckCaptions().Length);
    }

    [Fact]
    public async Task SplittingMergingAndMovingTheSelectedCaption_KeepWhatTheyMadeSelected_AndAreEachOneUndoStep()
    {
        var session = await OpenAsync(CreateProject());
        session.ImportCaptions("1\n00:00:01,000 --> 00:00:03,000\nOne two\n\n2\n00:00:04,000 --> 00:00:05,000\nThree\n", StudioCaptionTiming.Recording);
        var original = session.Model!.Project.Captions.Cues;
        session.SelectCaption(0);
        session.Scrub(2);

        Assert.True(session.CanSplitSelectedCaption);
        Assert.True(session.SplitSelectedCaptionAtPlayhead());
        Assert.Equal(["One", "two", "Three"], session.Model.Project.Captions.Cues.Select(static cue => cue.Text));
        Assert.Equal("two", session.SelectedCaption!.Text);
        session.Undo();
        Assert.Equal(original, session.Model.Project.Captions.Cues);

        session.SelectCaption(0);
        Assert.True(session.CanMergeSelectedCaption);
        Assert.True(session.MergeSelectedCaptionWithNext());
        Assert.Equal("One two Three", session.SelectedCaption!.Text);
        Assert.False(session.CanMergeSelectedCaption);
        session.Undo();
        Assert.Equal(original, session.Model.Project.Captions.Cues);

        session.SelectCaption(1);
        Assert.True(session.RetimeSelectedCaption(StudioCaptionEdge.Start, 0));
        Assert.Equal(0, session.SelectedCaptionIndex);
        Assert.Equal("Three", session.SelectedCaption!.Text);
        Assert.Equal(4, session.Playhead, Precision);
        Assert.False(session.RetimeSelectedCaption(StudioCaptionEdge.Start, 0));
        session.Undo();
        Assert.Equal(original, session.Model.Project.Captions.Cues);

        session.SelectCaption(1);
        session.Scrub(4.5);
        Assert.False(session.CanSplitSelectedCaption);
        Assert.False(session.SplitSelectedCaptionAtPlayhead());
        Assert.False(session.MergeSelectedCaptionWithNext());
    }

    [Fact]
    public async Task TheCaptionsAreSaved_AndRead_WithTheProject()
    {
        var session = await OpenAsync(CreateProject());
        session.ImportCaptions(TwoCues, StudioCaptionTiming.Recording);

        Assert.True(session.SaveNow());

        Assert.Equal(2, Projects.Load(session.ProjectId).Captions.Cues.Length);
    }

    [Fact]
    public void WhileTheProjectCannotBeEdited_NothingIsImported()
    {
        var session = CreateSession(CreateProject());

        Assert.Null(session.ImportCaptions(TwoCues, StudioCaptionTiming.Recording));
    }
}
