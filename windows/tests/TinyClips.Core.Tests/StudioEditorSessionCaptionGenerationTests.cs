using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Core.Studio.Transcription;

namespace TinyClips.Core.Tests;

/// <summary>A generator that finishes or fails when a test says so.</summary>
internal sealed class FakeCaptionGenerator : IStudioCaptionGenerator
{
    private readonly TaskCompletionSource<StudioGeneratedCaptions> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HashSet<string> Installed { get; } = [];

    public List<StudioCaptionModel> Requests { get; } = [];

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
        Requests.Add(model);
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

    public void Finish(string? language, params StudioCaptionCue[] cues) =>
        _completion.SetResult(new StudioGeneratedCaptions(language, cues));

    public void Fail(Exception exception) => _completion.SetException(exception);
}

/// <summary>Generating captions in the editor session: what it blocks, and what comes of it.</summary>
public sealed class StudioEditorSessionCaptionGenerationTests : StudioEditorSessionTestBase
{
    private static readonly StudioCaptionCue Heard = new()
    {
        Id = "heard",
        Start = 1,
        End = 2,
        Text = "Hello",
        Words = [new StudioCaptionWord { Start = 1, End = 2, Text = "Hello" }],
    };

    private async Task<(StudioEditorSession Session, FakeCaptionGenerator Generator)> OpenWithGeneratorAsync()
    {
        var generator = new FakeCaptionGenerator();
        CaptionGenerator = generator;
        return (await OpenAsync(CreateProject()), generator);
    }

    [Fact]
    public async Task WithoutAGenerator_ItCannotStart()
    {
        var session = await OpenAsync(CreateProject());

        Assert.False(session.CanGenerateCaptions);
        Assert.False(session.IsCaptionModelInstalled(StudioCaptionModels.Default));
        var result = await session.GenerateCaptionsAsync(StudioCaptionModels.Default);
        Assert.Equal(StudioCaptionGenerationOutcome.NotStarted, result.Outcome);
    }

    [Fact]
    public async Task WhatWasHeard_ReplacesTheCaptions_WithItsLanguage_AsOneEdit()
    {
        var (session, generator) = await OpenWithGeneratorAsync();
        session.ImportCaptions("1\n00:00:05,000 --> 00:00:06,000\nImported\n", StudioCaptionTiming.Recording);
        var model = StudioCaptionModels.Find("Small")!;

        var generating = session.GenerateCaptionsAsync(model);

        Assert.True(session.IsGeneratingCaptions);
        Assert.False(session.CanGenerateCaptions);
        Assert.False(session.CanExport);
        Assert.Same(model, Assert.Single(generator.Requests));
        Assert.Equal("Imported", session.Model!.Project.Captions.Cues[0].Text);

        generator.Finish("en", Heard);
        await PumpUntilCompletedAsync(generating);

        var result = await generating;
        Assert.Equal(StudioCaptionGenerationOutcome.Generated, result.Outcome);
        Assert.Equal(1, result.Count);
        Assert.False(session.IsGeneratingCaptions);
        Assert.True(session.CanGenerateCaptions);
        Assert.True(session.CanExport);
        Assert.Equal("en", session.Model.Project.Captions.Language);
        Assert.Equal("Hello", session.Model.Project.Captions.Cues[0].Text);
        Assert.Single(session.Model.Project.Captions.Cues[0].Words);

        session.Undo();
        Assert.Null(session.Model.Project.Captions.Language);
        Assert.Equal("Imported", session.Model.Project.Captions.Cues[0].Text);
    }

    [Fact]
    public async Task EditingGoesOnWhileItRuns_AndTheEditIsKept()
    {
        var (session, generator) = await OpenWithGeneratorAsync();

        var generating = session.GenerateCaptionsAsync(StudioCaptionModels.Default);
        session.Scrub(4);
        session.AddCutAtPlayhead();
        generator.Finish(null, Heard);
        await PumpUntilCompletedAsync(generating);

        Assert.Single(session.Model!.Project.Edits.Cuts);
        Assert.Equal("Hello", session.Model.Project.Captions.Cues[0].Text);
    }

    [Fact]
    public async Task ItSaysHowFarItIs()
    {
        var (session, generator) = await OpenWithGeneratorAsync();
        var generating = session.GenerateCaptionsAsync(StudioCaptionModels.Default);
        Changes.Clear();

        generator.Progress!.Report(new StudioCaptionProgress(StudioCaptionStage.ReadingSound, 0.2));
        generator.Progress.Report(new StudioCaptionProgress(StudioCaptionStage.Transcribing, 0.5));
        Pump();

        Assert.Equal(new StudioCaptionProgress(StudioCaptionStage.Transcribing, 0.5), session.CaptionGenerationProgress);
        Assert.Equal([StudioEditorChanges.Captions], Changes);

        generator.Finish(null, Heard);
        await PumpUntilCompletedAsync(generating);
    }

    [Fact]
    public async Task AFailureSaysWhy_AndLeavesTheCaptionsAlone()
    {
        var (session, generator) = await OpenWithGeneratorAsync();
        session.ImportCaptions("1\n00:00:05,000 --> 00:00:06,000\nImported\n", StudioCaptionTiming.Recording);

        var generating = session.GenerateCaptionsAsync(StudioCaptionModels.Default);
        generator.Fail(new StudioCaptionException("No speech was found in this recording."));
        await PumpUntilCompletedAsync(generating);

        var result = await generating;
        Assert.Equal(StudioCaptionGenerationOutcome.Failed, result.Outcome);
        Assert.Equal("No speech was found in this recording.", result.Message);
        Assert.False(session.IsGeneratingCaptions);
        Assert.Equal("Imported", session.Model!.Project.Captions.Cues[0].Text);
    }

    [Fact]
    public async Task AnUnexpectedFailureStillEndsIt()
    {
        var (session, generator) = await OpenWithGeneratorAsync();

        var generating = session.GenerateCaptionsAsync(StudioCaptionModels.Default);
        generator.Fail(new InvalidOperationException("boom"));
        await PumpUntilCompletedAsync(generating);

        var result = await generating;
        Assert.Equal(StudioCaptionGenerationOutcome.Failed, result.Outcome);
        Assert.Equal("Captions could not be generated: boom", result.Message);
        Assert.True(session.CanGenerateCaptions);
    }

    [Fact]
    public async Task CancellingLeavesTheCaptionsAlone()
    {
        var (session, _) = await OpenWithGeneratorAsync();
        session.ImportCaptions("1\n00:00:05,000 --> 00:00:06,000\nImported\n", StudioCaptionTiming.Recording);

        var generating = session.GenerateCaptionsAsync(StudioCaptionModels.Default);
        session.CancelCaptionGeneration();
        await PumpUntilCompletedAsync(generating);

        Assert.Equal(StudioCaptionGenerationOutcome.Cancelled, (await generating).Outcome);
        Assert.False(session.IsGeneratingCaptions);
        Assert.Equal("Imported", session.Model!.Project.Captions.Cues[0].Text);
    }

    [Fact]
    public async Task WhileItIsStopping_ItSaysSo_AndWhatItThenHeardIsNotUsed()
    {
        var (session, generator) = await OpenWithGeneratorAsync();
        generator.StopsLate = true;
        session.ImportCaptions("1\n00:00:05,000 --> 00:00:06,000\nImported\n", StudioCaptionTiming.Recording);
        var generating = session.GenerateCaptionsAsync(StudioCaptionModels.Default);
        Assert.False(session.IsStoppingCaptionGeneration);
        Changes.Clear();

        session.CancelCaptionGeneration();
        session.CancelCaptionGeneration();

        Assert.True(session.IsStoppingCaptionGeneration);
        Assert.True(session.IsGeneratingCaptions);
        Assert.False(session.CanGenerateCaptions);
        Assert.Equal([StudioEditorChanges.Captions], Changes);

        generator.Finish("en", Heard);
        await PumpUntilCompletedAsync(generating);

        Assert.Equal(StudioCaptionGenerationOutcome.Cancelled, (await generating).Outcome);
        Assert.False(session.IsStoppingCaptionGeneration);
        Assert.False(session.IsGeneratingCaptions);
        Assert.Equal("Imported", session.Model!.Project.Captions.Cues[0].Text);
    }

    [Fact]
    public async Task StoppingWhenNothingIsRunning_DoesNothing()
    {
        var (session, generator) = await OpenWithGeneratorAsync();

        session.CancelCaptionGeneration();

        Assert.False(session.IsStoppingCaptionGeneration);

        var generating = session.GenerateCaptionsAsync(StudioCaptionModels.Default);
        generator.Finish(null, Heard);
        await PumpUntilCompletedAsync(generating);

        Assert.Equal(StudioCaptionGenerationOutcome.Generated, (await generating).Outcome);
    }

    [Fact]
    public async Task ASecondOneDoesNotStart()
    {
        var (session, generator) = await OpenWithGeneratorAsync();

        var generating = session.GenerateCaptionsAsync(StudioCaptionModels.Default);
        var second = await session.GenerateCaptionsAsync(StudioCaptionModels.Default);

        Assert.Equal(StudioCaptionGenerationOutcome.NotStarted, second.Outcome);
        Assert.Single(generator.Requests);

        generator.Finish(null, Heard);
        await PumpUntilCompletedAsync(generating);
    }

    [Fact]
    public async Task ClosingStopsIt_AndNothingIsPutInTheProject()
    {
        var (session, _) = await OpenWithGeneratorAsync();

        var generating = session.GenerateCaptionsAsync(StudioCaptionModels.Default);
        var closing = session.CloseAsync();
        await PumpUntilCompletedAsync(generating);
        await PumpUntilCompletedAsync(closing);

        Assert.Equal(StudioCaptionGenerationOutcome.Cancelled, (await generating).Outcome);
        Assert.Empty(Projects.Load(session.ProjectId).Captions.Cues);
    }

    [Fact]
    public async Task DeletingTheProject_WaitsUntilItHasLetGoOfTheRecording()
    {
        var (session, _) = await OpenWithGeneratorAsync();

        var generating = session.GenerateCaptionsAsync(StudioCaptionModels.Default);
        var closing = session.CloseAsync(deleteProject: true);
        await PumpUntilCompletedAsync(generating);
        await PumpUntilCompletedAsync(closing);

        Assert.False(Projects.Exists(session.ProjectId));
    }
}
