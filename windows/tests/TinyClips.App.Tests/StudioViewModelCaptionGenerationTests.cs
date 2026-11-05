using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Core.Studio.Transcription;

namespace TinyClips.App.Tests;

/// <summary>The Captions panel while it makes captions: the model, the progress, and what it says afterwards.</summary>
public sealed class StudioViewModelCaptionGenerationTests : StudioViewModelTestBase
{
    private static readonly StudioCaptionCue Heard = new() { Id = "heard", Start = 1, End = 2, Text = "Hello" };

    private sealed class Generator : IStudioCaptionGenerator
    {
        private readonly TaskCompletionSource<StudioGeneratedCaptions> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HashSet<string> Installed { get; } = [];

        public StudioCaptionModel? Requested { get; private set; }

        public IProgress<StudioCaptionProgress>? Progress { get; private set; }

        /// <summary>Goes on after being told to stop, as speech recognition does for a while.</summary>
        public bool StopsLate { get; set; }

        public bool IsModelInstalled(StudioCaptionModel model) => Installed.Contains(model.Id);

        public async Task<StudioGeneratedCaptions> GenerateAsync(
            StudioProject project,
            StudioProjectPaths paths,
            StudioCaptionModel model,
            IProgress<StudioCaptionProgress>? progress,
            CancellationToken cancellationToken)
        {
            Requested = model;
            Progress = progress;
            if (StopsLate)
            {
                return await _completion.Task.ConfigureAwait(false);
            }

            await using (cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken)))
            {
                return await _completion.Task.ConfigureAwait(false);
            }
        }

        public void Finish(params StudioCaptionCue[] cues) => _completion.SetResult(new StudioGeneratedCaptions("en", cues));

        public void Fail(string message) => _completion.SetException(new StudioCaptionException(message));
    }

    private async Task<(StudioViewModel ViewModel, Generator Generator)> OpenWithGeneratorAsync(Func<StudioProject, StudioProject>? change = null)
    {
        var generator = new Generator();
        CaptionGenerator = generator;
        return (await OpenAsync(CreateProject(change)), generator);
    }

    /// <summary>Waits for what ran beside the UI thread, and runs what it posted back.</summary>
    private async Task FinishAsync(Task task)
    {
        var limit = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < limit)
        {
            Pump();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await task;
        Pump();
    }

    [Fact]
    public async Task WithoutAGenerator_ThereIsNothingToStart()
    {
        var viewModel = await OpenAsync(CreateProject());

        Assert.False(viewModel.CanGenerateCaptions);
        Assert.True(viewModel.IsCaptionGenerateVisible);

        await viewModel.GenerateCaptionsAsync();

        Assert.False(viewModel.IsGeneratingCaptions);
        Assert.DoesNotContain(StudioCaptionText.GeneratingMessage, Announcements);
    }

    [Fact]
    public async Task ItUsesTheModelThatWasChosen_AndKeepsTheChoice()
    {
        var (viewModel, generator) = await OpenWithGeneratorAsync();
        var small = viewModel.CaptionModelOptions.Single(static option => option.Model.Id == "Small");
        var index = viewModel.CaptionModelOptions.ToList().IndexOf(small);

        viewModel.SelectedCaptionModelIndex = index;
        var generating = viewModel.GenerateCaptionsAsync();

        Assert.Equal("Small", Settings.StudioCaptionModelId);
        Assert.Equal(index, viewModel.SelectedCaptionModelIndex);
        Assert.Same(small.Model, generator.Requested);

        generator.Finish(Heard);
        await FinishAsync(generating);
    }

    [Fact]
    public async Task AModelThatWasRemovedFromTheList_FallsBackToTheDefault()
    {
        var (viewModel, _) = await OpenWithGeneratorAsync();

        Settings.StudioCaptionModelId = "NotAModel";

        Assert.Same(StudioCaptionModels.Default, viewModel.CaptionModelOptions[viewModel.SelectedCaptionModelIndex].Model);
    }

    [Fact]
    public async Task TheButtonAndTheNote_SayWhetherItDownloadsFirst()
    {
        var (viewModel, generator) = await OpenWithGeneratorAsync();
        var model = StudioCaptionModels.Default;

        Assert.Equal(StudioCaptionText.GetGenerateActionText(model, isInstalled: false), viewModel.CaptionGenerateActionText);
        Assert.Equal(StudioCaptionText.GetModelNote(model, isInstalled: false), viewModel.CaptionModelNote);

        generator.Installed.Add(model.Id);

        Assert.Equal(StudioCaptionText.GetGenerateActionText(model, isInstalled: true), viewModel.CaptionGenerateActionText);
        Assert.Equal(StudioCaptionText.GetModelNote(model, isInstalled: true), viewModel.CaptionModelNote);
    }

    [Fact]
    public async Task WhileItRuns_ProgressShows_AndWhatItWouldInterruptIsOff()
    {
        var (viewModel, generator) = await OpenWithGeneratorAsync();

        var generating = viewModel.GenerateCaptionsAsync();

        Assert.True(viewModel.IsGeneratingCaptions);
        Assert.False(viewModel.IsCaptionGenerateVisible);
        Assert.False(viewModel.CanGenerateCaptions);
        Assert.False(viewModel.CanSaveProject);
        Assert.Contains(StudioCaptionText.GeneratingMessage, Announcements);

        var progress = new StudioCaptionProgress(StudioCaptionStage.Transcribing, 0.5);
        generator.Progress!.Report(progress);
        Pump();

        Assert.Equal(50, viewModel.CaptionProgressPercent);
        Assert.Equal(StudioCaptionText.GetProgressText(progress), viewModel.CaptionProgressText);

        generator.Finish(Heard);
        await FinishAsync(generating);

        Assert.False(viewModel.IsGeneratingCaptions);
        Assert.True(viewModel.IsCaptionGenerateVisible);
        Assert.True(viewModel.CanSaveProject);
    }

    [Fact]
    public async Task WhenItFinishes_TheListHasTheCaptions_AndItSaysHowMany()
    {
        var (viewModel, generator) = await OpenWithGeneratorAsync();

        var generating = viewModel.GenerateCaptionsAsync();
        generator.Finish(Heard);
        await FinishAsync(generating);

        Assert.Equal(["Hello"], viewModel.CaptionRows.Select(static row => row.Text));
        Assert.Equal(StudioCaptionText.GetGeneratedMessage(1), viewModel.CaptionStatus);
        Assert.Contains(StudioCaptionText.GetGeneratedMessage(1), Announcements);
    }

    [Fact]
    public async Task WhenItFails_ItSaysWhy()
    {
        var (viewModel, generator) = await OpenWithGeneratorAsync();

        var generating = viewModel.GenerateCaptionsAsync();
        generator.Fail("This recording has no sound to make captions from.");
        await FinishAsync(generating);

        Assert.Equal("This recording has no sound to make captions from.", viewModel.ErrorMessage);
        Assert.Empty(viewModel.CaptionRows);
        Assert.True(viewModel.CanGenerateCaptions);
    }

    [Fact]
    public async Task CancellingSaysSo_AndChangesNothing()
    {
        var (viewModel, _) = await OpenWithGeneratorAsync();

        var generating = viewModel.GenerateCaptionsAsync();
        viewModel.CancelCaptionGeneration();
        await FinishAsync(generating);

        Assert.Equal(StudioCaptionText.CancelledMessage, viewModel.CaptionStatus);
        Assert.Empty(viewModel.CaptionRows);
        Assert.False(viewModel.IsGeneratingCaptions);
    }

    [Fact]
    public async Task WhileItIsStopping_ThePanelSaysSo_AndCancelIsOff()
    {
        var (viewModel, generator) = await OpenWithGeneratorAsync();
        generator.StopsLate = true;
        Assert.False(viewModel.CanCancelCaptionGeneration);

        var generating = viewModel.GenerateCaptionsAsync();

        Assert.True(viewModel.CanCancelCaptionGeneration);
        Assert.False(viewModel.IsStoppingCaptionGeneration);

        viewModel.CancelCaptionGeneration();
        viewModel.CancelCaptionGeneration();

        Assert.True(viewModel.IsStoppingCaptionGeneration);
        Assert.False(viewModel.CanCancelCaptionGeneration);
        Assert.True(viewModel.IsGeneratingCaptions);
        Assert.Equal(StudioCaptionText.StoppingMessage, viewModel.CaptionProgressText);
        Assert.Single(Announcements, static message => message == StudioCaptionText.StoppingMessage);

        generator.Progress!.Report(new StudioCaptionProgress(StudioCaptionStage.Transcribing, 0.9));
        Pump();

        Assert.Equal(StudioCaptionText.StoppingMessage, viewModel.CaptionProgressText);

        generator.Finish(Heard);
        await FinishAsync(generating);

        Assert.False(viewModel.IsStoppingCaptionGeneration);
        Assert.False(viewModel.CanCancelCaptionGeneration);
        Assert.Equal(StudioCaptionText.CancelledMessage, viewModel.CaptionStatus);
        Assert.Empty(viewModel.CaptionRows);
    }

    [Fact]
    public async Task TextBeingTyped_IsKeptBeforeItStarts()
    {
        var (viewModel, generator) = await OpenWithGeneratorAsync(static project => project with
        {
            Captions = new StudioCaptions { Cues = [new StudioCaptionCue { Id = "typo", Start = 1, End = 2, Text = "Typo" }] },
        });
        viewModel.SelectCaptionRow(0);
        viewModel.SetPendingCaptionText("Typed");

        var generating = viewModel.GenerateCaptionsAsync();

        Assert.Equal("Typed", viewModel.CaptionRows[0].Text);

        generator.Finish(Heard);
        await FinishAsync(generating);
        Assert.Equal("Hello", viewModel.CaptionRows[0].Text);
    }

    [Fact]
    public async Task ClosingTheWindowWhileItRuns_LeavesNoCaptions()
    {
        var (viewModel, _) = await OpenWithGeneratorAsync();
        var projectId = viewModel.ProjectId;

        var generating = viewModel.GenerateCaptionsAsync();
        await CloseAsync(viewModel);
        await FinishAsync(generating);

        Assert.Empty(Projects.Load(projectId).Captions.Cues);
        Assert.NotEqual(StudioCaptionText.CancelledMessage, viewModel.CaptionStatus);
    }
}
