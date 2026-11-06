using TinyClips.App.Models.Studio;
using TinyClips.Core.Studio;

namespace TinyClips.App.Tests;

/// <summary>The Captions panel's values: its list, what is typed into the box, and importing and exporting.</summary>
public sealed class StudioViewModelCaptionTests : StudioViewModelTestBase
{
    private const string TwoCues = "1\n00:00:03,000 --> 00:00:04,000\nSecond\n\n2\n00:00:01,000 --> 00:00:02,000\nFirst\n";

    private static StudioProject WithCues(StudioProject project) => project with
    {
        Captions = new StudioCaptions
        {
            Cues =
            [
                new StudioCaptionCue { Id = "later", Start = 3, End = 4, Text = "Second" },
                new StudioCaptionCue { Id = "earlier", Start = 1, End = 2, Text = "First" },
            ],
        },
    };

    [Fact]
    public async Task TheListIsInTimeOrder_AndIsTheSameListUntilACueChanges()
    {
        var viewModel = await OpenAsync(CreateProject(WithCues));

        var rows = viewModel.CaptionRows;

        Assert.Equal(["First", "Second"], rows.Select(static row => row.Text));
        Assert.Equal([1, 0], rows.Select(static row => row.Index));
        Assert.Equal("1.0 to 2.0 seconds: First", rows[0].ToString());
        Assert.Same(rows, viewModel.CaptionRows);
        Assert.Equal("2 captions", viewModel.CaptionCountText);
        Assert.False(viewModel.IsCaptionEmptyHintVisible);
    }

    [Fact]
    public async Task AProjectWithNoCaptions_SaysHowToAddThem_AndHasNothingToExportOrFix()
    {
        var viewModel = await OpenAsync(CreateProject());

        Assert.Empty(viewModel.CaptionRows);
        Assert.Equal("No captions yet", viewModel.CaptionCountText);
        Assert.True(viewModel.IsCaptionEmptyHintVisible);
        Assert.True(viewModel.CanImportCaptions);
        Assert.False(viewModel.CanExportCaptions);
        Assert.False(viewModel.CanFixCaptions);
        Assert.False(viewModel.HasCaptions);
    }

    [Fact]
    public async Task ChoosingARow_SelectsItsCue_AndMovesThePlayheadThere()
    {
        var viewModel = await OpenAsync(CreateProject(WithCues));

        viewModel.SelectCaptionRow(1);

        Assert.True(viewModel.HasSelectedCaption);
        Assert.Equal(1, viewModel.SelectedCaptionRow);
        Assert.Equal("Second", viewModel.SelectedCaptionText);
        Assert.Equal("3.0 to 4.0 seconds", viewModel.SelectedCaptionTimeText);
        Assert.Equal(3, viewModel.Playhead, Precision);

        viewModel.SelectCaptionRow(-1);
        Assert.False(viewModel.HasSelectedCaption);
        Assert.Equal(-1, viewModel.SelectedCaptionRow);
    }

    [Fact]
    public async Task WhatIsTyped_GoesInWhenTheBoxIsLeft_AsOneUndoStep()
    {
        var viewModel = await OpenAsync(CreateProject(WithCues));
        viewModel.SelectCaptionRow(0);

        viewModel.SetPendingCaptionText("F");
        viewModel.SetPendingCaptionText("Fi");
        viewModel.SetPendingCaptionText("Changed");
        Assert.Equal("First", viewModel.Project!.Captions.Cues[1].Text);

        // A refresh of the box while someone types in it does not put the old text back.
        Assert.Equal("Changed", viewModel.SelectedCaptionText);

        viewModel.CommitCaptionText();

        Assert.Equal("Changed", viewModel.Project!.Captions.Cues[1].Text);
        Assert.Equal(0, viewModel.SelectedCaptionRow);
        Assert.Equal("Changed", viewModel.CaptionRows[0].Text);

        viewModel.Undo();
        Assert.Equal("First", viewModel.Project!.Captions.Cues[1].Text);
    }

    [Fact]
    public async Task WhatIsTyped_GoesToTheCueItWasTypedFor_WhenAnotherIsChosen()
    {
        var viewModel = await OpenAsync(CreateProject(WithCues));
        viewModel.SelectCaptionRow(0);
        viewModel.SetPendingCaptionText("Changed");

        viewModel.SelectCaptionRow(1);

        Assert.Equal("Changed", viewModel.Project!.Captions.Cues[1].Text);
        Assert.Equal("Second", viewModel.Project!.Captions.Cues[0].Text);
        Assert.Equal("Second", viewModel.SelectedCaptionText);
    }

    [Fact]
    public async Task TheBoxBeingRefreshed_IsNothingTyped_SoARedoShowsInIt()
    {
        var viewModel = await OpenAsync(CreateProject(WithCues));
        viewModel.SelectCaptionRow(0);
        viewModel.SetPendingCaptionText("Changed");
        viewModel.CommitCaptionText();
        viewModel.Undo();

        // The box is given the cue's text again, which raises its TextChanged.
        viewModel.SetPendingCaptionText("First");
        viewModel.Redo();

        Assert.Equal("Changed", viewModel.SelectedCaptionText);
        viewModel.CommitCaptionText();
        Assert.Equal("Changed", viewModel.Project!.Captions.Cues[1].Text);
    }

    [Fact]
    public async Task WhatIsTyped_IsNotLost_WhenTheWindowAsksWhatToSayOnClosing()
    {
        var viewModel = await OpenAsync(CreateProject(WithCues));
        viewModel.SelectCaptionRow(0);
        viewModel.SetPendingCaptionText("Changed");

        _ = viewModel.GetClosePrompt();

        Assert.Equal("Changed", viewModel.Project!.Captions.Cues[1].Text);
    }

    [Fact]
    public async Task DeletingTheSelectedCaption_DropsWhatWasTypedForIt()
    {
        var viewModel = await OpenAsync(CreateProject(WithCues));
        viewModel.SelectCaptionRow(0);
        viewModel.SetPendingCaptionText("Changed");

        viewModel.RemoveSelectedCaption();
        viewModel.CommitCaptionText();

        Assert.Equal(["Second"], viewModel.CaptionRows.Select(static row => row.Text));
        Assert.False(viewModel.HasSelectedCaption);
    }

    [Fact]
    public async Task Importing_PutsTheCuesOfTheChosenFileInPlace_AndSaysHowMany()
    {
        var viewModel = await OpenAsync(CreateProject());
        var file = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.srt");
        File.WriteAllText(file, TwoCues + "\nnot a block\n");
        var heard = new List<string>();
        viewModel.Announced += (_, e) => heard.Add(e.Message);
        viewModel.ChooseCaptionFile = () => Task.FromResult<string?>(file);
        try
        {
            await viewModel.ImportCaptionsAsync(StudioCaptionTiming.Recording);
        }
        finally
        {
            File.Delete(file);
        }

        Assert.Equal(["First", "Second"], viewModel.CaptionRows.Select(static row => row.Text));
        Assert.Contains("Imported 2 captions. 1 block could not be read.", heard);
        Assert.True(viewModel.CanExportCaptions);
    }

    [Fact]
    public async Task AFileWithNoCues_ChangesNothing_AndSaysSo()
    {
        var viewModel = await OpenAsync(CreateProject(WithCues));
        var file = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.srt");
        File.WriteAllText(file, "Just some words.\n");
        viewModel.ChooseCaptionFile = () => Task.FromResult<string?>(file);
        try
        {
            await viewModel.ImportCaptionsAsync(StudioCaptionTiming.Recording);
        }
        finally
        {
            File.Delete(file);
        }

        Assert.Equal(2, viewModel.CaptionRows.Count);
        Assert.Equal(StudioCaptionNoticeKind.Problem, viewModel.CaptionNoticeKind);
        Assert.Equal(StudioCaptionText.NothingToImport, viewModel.CaptionNoticeMessage);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task ChoosingNoFile_ChangesNothing()
    {
        var viewModel = await OpenAsync(CreateProject(WithCues));
        viewModel.ChooseCaptionFile = () => Task.FromResult<string?>(null);

        await viewModel.ImportCaptionsAsync(StudioCaptionTiming.Recording);

        Assert.Equal(2, viewModel.CaptionRows.Count);
        Assert.True(viewModel.CanImportCaptions);
    }

    [Fact]
    public async Task Exporting_WritesTheCaptionsTheVideoHas_InTheFormatAskedFor()
    {
        var viewModel = await OpenAsync(CreateProject(WithCues));
        var folder = Directory.CreateTempSubdirectory("tinyclips-captions-").FullName;
        var asked = new List<string>();
        viewModel.ChooseCaptionExportFile = (_, name, _) =>
        {
            asked.Add(name);
            return Task.FromResult<string?>(Path.Combine(folder, name));
        };
        try
        {
            await viewModel.ExportCaptionsAsync(StudioCaptionFormat.Vtt);

            Assert.EndsWith(".vtt", asked[0], StringComparison.Ordinal);
            var text = File.ReadAllText(Path.Combine(folder, asked[0]));
            Assert.StartsWith("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nFirst\n", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Rows_MarkTheirProblems_AndTheHeaderCountsTheCaptionsWithThem()
    {
        var viewModel = await OpenAsync(CreateProject(project => project with
        {
            Captions = new StudioCaptions
            {
                Cues =
                [
                    new StudioCaptionCue { Start = 1, End = 1.2, Text = "  Hi  " },
                    new StudioCaptionCue { Start = 3, End = 5, Text = "Fine" },
                    new StudioCaptionCue { Start = 6, End = 8, Text = "Extra  space" },
                ],
            },
        }));

        var rows = viewModel.CaptionRows;
        Assert.True(rows[0].HasProblems);
        Assert.Equal("2", rows[0].ProblemBadgeText);
        Assert.False(rows[1].HasProblems);
        Assert.Equal(string.Empty, rows[2].ProblemBadgeText);
        Assert.True(rows[2].HasProblems);
        Assert.Equal("2", viewModel.CaptionProblemCountText);
        Assert.StartsWith(viewModel.CaptionCheckText, viewModel.CaptionProblemsToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoFix_ChangesTheCues_AndSaysWhatItDid()
    {
        var viewModel = await OpenAsync(CreateProject(project => project with
        {
            Captions = new StudioCaptions { Cues = [new StudioCaptionCue { Start = 1, End = 1.2, Text = "Hi" }] },
        }));
        var heard = new List<string>();
        viewModel.Announced += (_, e) => heard.Add(e.Message);
        Assert.True(viewModel.CanFixCaptions);
        Assert.Equal("1 too short", viewModel.CaptionCheckText);

        viewModel.FixCaptions();

        Assert.Equal(StudioCaptionText.NoProblems, viewModel.CaptionCheckText);
        Assert.False(viewModel.CanFixCaptions);
        Assert.Contains("Made 1 fix.", heard);
        Assert.Equal("Made 1 fix.", viewModel.CaptionNoticeMessage);

        Time.Advance(StudioCaptionNotice.ShownFor);
        Pump();
        Assert.False(viewModel.IsCaptionNoticeOpen);
    }

    [Fact]
    public async Task EachRow_SaysWhatTheChecksFoundInIt()
    {
        var viewModel = await OpenAsync(CreateProject(static project => project with
        {
            Captions = new StudioCaptions
            {
                Cues =
                [
                    new StudioCaptionCue { Id = "short", Start = 1, End = 1.2, Text = "Hi" },
                    new StudioCaptionCue { Id = "fine", Start = 3, End = 5, Text = "Fine" },
                ],
            },
        }));

        var rows = viewModel.CaptionRows;

        Assert.Equal("Too short to read", rows[0].ProblemText);
        Assert.Equal("0:01.0 \u2013 0:01.2", rows[0].TimeText);
        Assert.Equal("1.0 to 1.2 seconds: Hi. Too short to read", rows[0].ToString());
        Assert.False(rows[1].HasProblems);

        viewModel.SelectCaptionRow(0);
        Assert.Equal("Too short to read", viewModel.SelectedCaptionProblemText);
        Assert.Equal("0:01.0", viewModel.SelectedCaptionStartText);
        Assert.Equal("0:01.2", viewModel.SelectedCaptionEndText);
    }

    [Fact]
    public async Task Searching_ShowsOnlyTheCuesThatHaveIt_AndMarksWhereItIs()
    {
        var viewModel = await OpenAsync(CreateProject(WithCues));
        viewModel.SelectCaptionRow(1);

        viewModel.SetCaptionSearchText("  S\u00C9C ");

        var found = Assert.Single(viewModel.CaptionRows);
        Assert.Equal("Second", found.Text);
        Assert.Equal([new StudioTextRange(0, 3)], found.Highlights);
        Assert.Equal("1 of 2", viewModel.CaptionSearchCountText);
        Assert.Equal(0, viewModel.SelectedCaptionRow);
        Assert.False(viewModel.IsCaptionNoMatchesVisible);

        viewModel.SetCaptionSearchText("nothing");

        Assert.Empty(viewModel.CaptionRows);
        Assert.Equal(-1, viewModel.SelectedCaptionRow);
        Assert.True(viewModel.HasSelectedCaption);
        Assert.True(viewModel.IsCaptionNoMatchesVisible);
        Assert.Equal("No captions contain \u201Cnothing\u201D.", viewModel.CaptionNoMatchesText);

        viewModel.SetCaptionSearchText(string.Empty);

        Assert.Equal(["First", "Second"], viewModel.CaptionRows.Select(static row => row.Text));
        Assert.All(viewModel.CaptionRows, static row => Assert.Empty(row.Highlights));
        Assert.Equal(string.Empty, viewModel.CaptionSearchCountText);
    }

    [Fact]
    public async Task TheRowAtThePlayhead_IsMarked_AndSaidOnlyWhenItChanges()
    {
        var viewModel = await OpenAsync(CreateProject(WithCues));
        var rows = viewModel.CaptionRows;
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.Scrub(1.5);
        viewModel.Scrub(1.6);

        Assert.Same(rows[0], viewModel.CurrentCaptionRow);
        Assert.True(rows[0].IsCurrent);
        Assert.Single(raised, static name => name == nameof(TinyClips.App.ViewModels.Studio.StudioViewModel.CurrentCaptionRow));

        viewModel.Scrub(2.5);

        Assert.Null(viewModel.CurrentCaptionRow);
        Assert.All(rows, static row => Assert.False(row.IsCurrent));
    }

    [Fact]
    public async Task TheSelectedCaption_CanBeSplitMergedRetimedAndDeleted_EachSaidAloud()
    {
        var viewModel = await OpenAsync(CreateProject(static project => project with
        {
            Captions = new StudioCaptions
            {
                Cues =
                [
                    new StudioCaptionCue { Id = "long", Start = 1, End = 3, Text = "Hello there" },
                    new StudioCaptionCue { Id = "next", Start = 4, End = 5, Text = "Bye" },
                ],
            },
        }));
        viewModel.SelectCaptionRow(0);
        viewModel.Scrub(2);
        Assert.True(viewModel.CanSplitSelectedCaption);

        viewModel.SplitSelectedCaption();

        Assert.Equal(["Hello", "there", "Bye"], viewModel.CaptionRows.Select(static row => row.Text));
        Assert.Equal(1, viewModel.SelectedCaptionRow);
        Assert.Contains(StudioCaptionPanelText.SplitMessage, Announcements);

        viewModel.SetPendingCaptionText("there you");
        viewModel.MergeSelectedCaption();

        Assert.Equal(["Hello", "there you Bye"], viewModel.CaptionRows.Select(static row => row.Text));
        Assert.Contains(StudioCaptionPanelText.MergedMessage, Announcements);
        Assert.False(viewModel.CanMergeSelectedCaption);

        viewModel.NudgeCaptionStartLater();

        Assert.Equal("0:02.1", viewModel.SelectedCaptionStartText);
        Assert.Contains(viewModel.SelectedCaptionTimeText, Announcements);

        viewModel.Scrub(4.5);
        viewModel.MoveCaptionEndToPlayhead();

        Assert.Equal("0:04.5", viewModel.SelectedCaptionEndText);
        Assert.Equal(4.5, viewModel.Playhead, Precision);

        viewModel.RemoveSelectedCaption();

        Assert.Equal(["Hello"], viewModel.CaptionRows.Select(static row => row.Text));
        Assert.Contains(StudioCaptionPanelText.DeletedMessage, Announcements);
        Assert.False(viewModel.CanEditSelectedCaption);
    }

    [Fact]
    public async Task Done_KeepsWhatWasTyped_AndLetsGoOfTheCaption()
    {
        var viewModel = await OpenAsync(CreateProject(WithCues));
        viewModel.SelectCaptionRow(0);
        viewModel.SetPendingCaptionText("Typed");

        viewModel.DeselectCaption();

        Assert.False(viewModel.HasSelectedCaption);
        Assert.Equal("Typed", viewModel.CaptionRows[0].Text);
    }

    [Fact]
    public async Task AnAgentsEdit_WhileSomethingIsBeingTyped_KeepsWhatWasTyped_AndIsTurnedAway()
    {
        var viewModel = await OpenAsync(CreateProject(WithCues));
        var read = viewModel.Project!;
        viewModel.SelectCaptionRow(0);
        viewModel.SetPendingCaptionText("Typed");

        Assert.False(viewModel.ApplyAutomationProject(read with { Captions = new StudioCaptions() }, read.ModifiedAt));
        Assert.Equal("Typed", viewModel.CaptionRows[0].Text);

        var current = viewModel.Project!;
        Assert.True(viewModel.ApplyAutomationProject(current with { Captions = new StudioCaptions() }, current.ModifiedAt));
        Assert.Empty(viewModel.CaptionRows);
    }

    [Fact]
    public async Task DrawingCaptions_IsOffUntilAskedFor_WithTheDefaultsOfTheFormat()
    {
        var viewModel = await OpenAsync(CreateProject(WithCues));

        Assert.False(viewModel.AreCaptionsDrawn);
        Assert.Equal((int)StudioCaptionSize.Medium, viewModel.CaptionSizeIndex);
        Assert.Equal((int)StudioCaptionPosition.Bottom, viewModel.CaptionPositionIndex);
        Assert.Equal((int)StudioCaptionAppearance.ClassicOutline, viewModel.CaptionAppearanceIndex);
    }

    [Fact]
    public async Task EachChoiceOfHowCaptionsAreDrawn_GoesInTheProject_AsOneEdit_ThatCanBeUndone()
    {
        var viewModel = await OpenAsync(CreateProject(WithCues));

        viewModel.AreCaptionsDrawn = true;
        Pump();
        viewModel.CaptionSizeIndex = (int)StudioCaptionSize.Large;
        Pump();
        viewModel.CaptionPositionIndex = (int)StudioCaptionPosition.Top;
        Pump();
        viewModel.CaptionAppearanceIndex = (int)StudioCaptionAppearance.BoldImpact;
        Pump();

        Assert.Equal(4, Preview.Updates.Count);
        var overlay = viewModel.Project!.Overlays.Captions;
        Assert.True(overlay.Enabled);
        Assert.Equal(StudioCaptionSize.Large, overlay.Size);
        Assert.Equal(StudioCaptionPosition.Top, overlay.Position);
        Assert.Equal(StudioCaptionAppearance.BoldImpact, overlay.Appearance);

        viewModel.Undo();
        Pump();

        Assert.Equal((int)StudioCaptionAppearance.ClassicOutline, viewModel.CaptionAppearanceIndex);
        Assert.Equal((int)StudioCaptionPosition.Top, viewModel.CaptionPositionIndex);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public async Task AChoiceThatIsNotInTheList_ChangesNothing(int choice)
    {
        var viewModel = await OpenAsync(CreateProject(WithCues));

        viewModel.CaptionSizeIndex = choice;
        viewModel.CaptionPositionIndex = choice;
        viewModel.CaptionAppearanceIndex = choice;
        Pump();

        Assert.Empty(Preview.Updates);
        Assert.Equal((int)StudioCaptionSize.Medium, viewModel.CaptionSizeIndex);
    }
}
