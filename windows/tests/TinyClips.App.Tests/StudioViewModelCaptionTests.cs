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
        Assert.Equal(StudioCaptionText.NothingToImport, viewModel.ErrorMessage);
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
    }
}
