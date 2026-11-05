using SharpGen.Runtime;
using TinyClips.Core.Studio.Rendering;
using Whisper.net;

namespace TinyClips.Core.Studio.Transcription;

/// <summary>What generating captions is doing.</summary>
public enum StudioCaptionStage
{
    DownloadingModel,
    ReadingSound,
    Transcribing,
}

/// <summary>How far generating captions is: its stage, and how far that is, from 0 to 1.</summary>
public readonly record struct StudioCaptionProgress(StudioCaptionStage Stage, double Fraction);

/// <summary>What was heard in a recording.</summary>
/// <param name="Language">The language Whisper took it for, or null.</param>
/// <param name="Cues">The cues in time order, in source time.</param>
public sealed record StudioGeneratedCaptions(string? Language, StudioCaptionCue[] Cues);

/// <summary>Generating captions failed, for a reason a user can read in this message.</summary>
public sealed class StudioCaptionException : Exception
{
    public StudioCaptionException(string message)
        : base(message)
    {
    }

    public StudioCaptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Makes captions from the sound of a recording, on this computer.</summary>
public interface IStudioCaptionGenerator
{
    bool IsModelInstalled(StudioCaptionModel model);

    /// <summary>
    /// Hears a recording, after downloading the model if it is not here. Whoever calls this has
    /// asked for the download first. Calls may come from any thread, and the work is not on the
    /// caller's.
    /// </summary>
    /// <exception cref="StudioCaptionException">It failed, for a reason a user can read.</exception>
    /// <exception cref="OperationCanceledException">It was cancelled.</exception>
    Task<StudioGeneratedCaptions> GenerateAsync(
        StudioProject project,
        StudioProjectPaths paths,
        StudioCaptionModel model,
        IProgress<StudioCaptionProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Whisper.net, used as OutroKit uses it, with the sound from the app's own decoder. No sound
/// leaves the computer. The only thing that goes over the network is the download of a model.
/// </summary>
public sealed class StudioCaptionGenerator(StudioCaptionModelStore models) : IStudioCaptionGenerator
{
    private const int MostThreads = 8;

    public bool IsModelInstalled(StudioCaptionModel model) => models.IsInstalled(model);

    public Task<StudioGeneratedCaptions> GenerateAsync(
        StudioProject project,
        StudioProjectPaths paths,
        StudioCaptionModel model,
        IProgress<StudioCaptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(model);
        return Task.Run(() => GenerateCoreAsync(project, paths, model, progress, cancellationToken), cancellationToken);
    }

    private async Task<StudioGeneratedCaptions> GenerateCoreAsync(
        StudioProject project,
        StudioProjectPaths paths,
        StudioCaptionModel model,
        IProgress<StudioCaptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var duration = project.Sources.Screen.Duration;

        // The sound first: a recording without any should not cost a download.
        var samples = ReadSound(paths.ScreenPath, duration, progress, cancellationToken);

        string modelPath;
        try
        {
            modelPath = await models.EnsureAsync(
                model,
                progress is null ? null : new Progress(progress, StudioCaptionStage.DownloadingModel),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            throw new StudioCaptionException($"The {model.Name} model could not be downloaded: {ex.Message}", ex);
        }

        return await TranscribeAsync(modelPath, model, samples, duration, progress, cancellationToken).ConfigureAwait(false);
    }

    internal static float[] ReadSound(string path, double duration, IProgress<StudioCaptionProgress>? progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new StudioCaptionException("The screen recording is missing, so it cannot be listened to.");
        }

        try
        {
            using (StudioMediaFoundation.Startup())
            {
                using var sound = StudioAudioMixSource.TryOpen(path, "screen recording", duration)
                    ?? throw new StudioCaptionException("This recording has no sound to make captions from.");
                return StudioCaptionAudio.Read(
                    sound,
                    sound.Format,
                    (long)Math.Ceiling(duration * sound.Format.SampleRate),
                    progress is null ? null : new Progress(progress, StudioCaptionStage.ReadingSound),
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is StudioExportException or SharpGenException)
        {
            throw new StudioCaptionException("The sound of the screen recording could not be read.", ex);
        }
    }

    internal static async Task<StudioGeneratedCaptions> TranscribeAsync(
        string modelPath,
        StudioCaptionModel model,
        float[] samples,
        double duration,
        IProgress<StudioCaptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var cues = new List<StudioCaptionCue>();
        string? language = null;
        try
        {
            using var factory = WhisperFactory.FromPath(modelPath);

            // Disposed asynchronously and first: a plain Dispose throws while Whisper is still
            // working, which is what a cancellation leaves it doing.
            await using var processor = factory.CreateBuilder()
                .WithLanguage("auto")
                .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, MostThreads))
                .WithTokenTimestamps()
                .SplitOnWord()
                .WithMaxSegmentLength(StudioCaptionTranscript.LongestCue)
                .Build();

            progress?.Report(new StudioCaptionProgress(StudioCaptionStage.Transcribing, 0));
            await foreach (var segment in processor.ProcessAsync(samples.AsMemory(), cancellationToken).ConfigureAwait(false))
            {
                language ??= segment.Language;
                if (duration > 0)
                {
                    progress?.Report(new StudioCaptionProgress(StudioCaptionStage.Transcribing, Math.Clamp(segment.End.TotalSeconds / duration, 0, 1)));
                }

                var tokens = segment.Tokens
                    .Select(static token => new StudioTranscriptToken(token.Text ?? string.Empty, token.Start, token.End))
                    .ToArray();
                if (StudioCaptionTranscript.ToCue(segment.Text, segment.Start, segment.End, tokens, duration) is { } cue)
                {
                    cues.Add(cue);
                }
            }
        }
        catch (WhisperModelLoadException ex)
        {
            // Not deleted here: a load also fails for lack of memory, and the file may be a good
            // download of several gigabytes.
            throw new StudioCaptionException(
                $"The {model.Name} model could not be loaded. It may be damaged, or too large for this computer. Choose a smaller model, or delete the downloaded models in Settings, under Studio, to download it again.",
                ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not StudioCaptionException)
        {
            throw new StudioCaptionException($"The speech could not be recognized: {ex.Message}", ex);
        }

        if (cues.Count == 0)
        {
            throw new StudioCaptionException("No speech was found in this recording.");
        }

        return new StudioGeneratedCaptions(language, StudioCaptionQuality.Fix(cues, duration).Cues);
    }

    private sealed class Progress(IProgress<StudioCaptionProgress> target, StudioCaptionStage stage) : IProgress<double>
    {
        public void Report(double value) => target.Report(new StudioCaptionProgress(stage, value));
    }
}
