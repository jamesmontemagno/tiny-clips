using System.Text;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;
using TinyClips.Core.Studio.Transcription;

namespace TinyClips.Core.Tests;

public sealed class StudioCaptionTranscriptionTests
{
    private const int Precision = 6;

    // Tokens as Whisper.net 1.9.1 gave them for a real recording, with token timestamps on and
    // segments of at most 84 characters.
    private static readonly StudioTranscriptToken[] Real =
    [
        new("[_BEG_]", 0, 0),
        new(" I", 0, 7),
        new(" believe", 24, 59),
        new(" space", 2057, 2097),
        new(".", 2104, 2104),
        new("[_TT_1052]", 2104, 2104),
    ];

    [Fact]
    public void MarkersAreNotWords_AndPunctuationJoinsTheWordBeforeIt()
    {
        var words = StudioCaptionTranscript.ToWords("I believe space.", Real, 60);

        Assert.Equal(["I", "believe", "space."], words.Select(static word => word.Text));
        Assert.Equal(0.24, words[1].Start, Precision);
        Assert.Equal(0.59, words[1].End, Precision);
        Assert.Equal(20.57, words[2].Start, Precision);
        Assert.Equal(21.04, words[2].End, Precision);
    }

    [Fact]
    public void WordsThatDoNotSpellOutTheTextAreLeftOut()
    {
        StudioTranscriptToken[] tokens = [new(" caf", 0, 10), new("\ufffd", 10, 20)];

        Assert.Empty(StudioCaptionTranscript.ToWords("café", tokens, 60));
    }

    [Fact]
    public void AWordInSeveralPiecesIsOneWord_AndNoTimeIsPastTheEnd()
    {
        StudioTranscriptToken[] tokens = [new(" Mon", 100, 120), new("tem", 120, 140), new("agno", 140, 900)];

        var word = Assert.Single(StudioCaptionTranscript.ToWords("Montemagno", tokens, 5));

        Assert.Equal("Montemagno", word.Text);
        Assert.Equal(1, word.Start, Precision);
        Assert.Equal(5, word.End, Precision);
    }

    [Theory]
    [InlineData(" [BLANK_AUDIO]")]
    [InlineData("   ")]
    [InlineData("")]
    public void ASegmentWithoutSpeechIsNoCue(string text) =>
        Assert.Null(StudioCaptionTranscript.ToCue(text, TimeSpan.Zero, TimeSpan.FromSeconds(10), [], 60));

    [Fact]
    public void ACueEndsWithTheRecording_AndOnePastItIsNoCue()
    {
        var cue = StudioCaptionTranscript.ToCue(" Hello.", TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(9), [], 5);

        Assert.NotNull(cue);
        Assert.Equal("Hello.", cue.Text);
        Assert.Equal(5, cue.End, Precision);
        Assert.True(Guid.TryParse(cue.Id, out _));
        Assert.Null(StudioCaptionTranscript.ToCue(" Late.", TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(9), [], 5));
    }

    [Fact]
    public void TheCatalogHasOneDefault_AndEveryModelItsOwnFile()
    {
        Assert.Equal("Base", StudioCaptionModels.Default.Id);
        Assert.Equal(StudioCaptionModels.All.Count, StudioCaptionModels.All.Select(static model => model.FileName).Distinct().Count());
        Assert.Same(StudioCaptionModels.Default, StudioCaptionModels.Find(" base "));
        Assert.Null(StudioCaptionModels.Find("nope"));
        Assert.Null(StudioCaptionModels.Find(null));
    }

    [Fact]
    public async Task ADownloadGetsItsNameWhenItIsComplete_AndIsDoneOnce()
    {
        using var folder = new TemporaryFolder();
        var downloads = 0;
        var store = new StudioCaptionModelStore(folder.Path, (_, _) =>
        {
            downloads++;
            return Task.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes("model")));
        });
        var model = StudioCaptionModels.Default;
        var reported = new List<double>();
        Assert.False(store.IsInstalled(model));
        Assert.Empty(store.GetInstalled());

        var path = await store.EnsureAsync(model, new ImmediateProgress<double>(reported.Add), TestContext.Current.CancellationToken);

        Assert.Equal("model", File.ReadAllText(path));
        Assert.Equal(0, reported[0]);
        Assert.Equal(1, reported[^1]);
        Assert.True(store.IsInstalled(model));
        Assert.Equal(5, store.GetInstalledBytes());
        Assert.Equal([model.FileName], Directory.GetFiles(folder.Path).Select(Path.GetFileName));

        await store.EnsureAsync(model, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, downloads);
    }

    [Fact]
    public async Task ADownloadThatStops_LeavesNothingThatLooksLikeAModel()
    {
        using var folder = new TemporaryFolder();
        var store = new StudioCaptionModelStore(folder.Path, (_, _) => Task.FromResult<Stream>(new FailingStream()));

        await Assert.ThrowsAsync<IOException>(() => store.EnsureAsync(StudioCaptionModels.Default, null, TestContext.Current.CancellationToken));

        Assert.False(store.IsInstalled(StudioCaptionModels.Default));
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task ACancelledDownloadLeavesNothing()
    {
        using var folder = new TemporaryFolder();
        using var cancellation = new CancellationTokenSource();
        var store = new StudioCaptionModelStore(folder.Path, (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult<Stream>(new MemoryStream(new byte[1024]));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.EnsureAsync(StudioCaptionModels.Default, null, cancellation.Token));

        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task DeleteAllTakesTheModelsAndWhatAStoppedDownloadLeft_AndNothingElse()
    {
        using var folder = new TemporaryFolder();
        var store = new StudioCaptionModelStore(folder.Path, (_, _) => Task.FromResult<Stream>(new MemoryStream(new byte[8])));
        await store.EnsureAsync(StudioCaptionModels.Default, null, TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(folder.Path, "ggml-base.bin.abc.download"), "left");
        File.WriteAllText(Path.Combine(folder.Path, "notes.txt"), "mine");

        Assert.Equal(1, store.DeleteAll());

        Assert.Equal(["notes.txt"], Directory.GetFiles(folder.Path).Select(Path.GetFileName));
        Assert.Equal(0, store.DeleteAll());
    }

    [Fact]
    public void DeleteAllOfAFolderThatIsNotThereDoesNothing()
    {
        var store = new StudioCaptionModelStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), (_, _) => throw new InvalidOperationException());

        Assert.Equal(0, store.DeleteAll());
        Assert.Equal(0, store.GetInstalledBytes());
    }

    [Theory]
    [InlineData(48000, 2)]
    [InlineData(44100, 2)]
    [InlineData(44100, 1)]
    [InlineData(16000, 1)]
    public void TheSoundBecomesOneChannelAt16Khz_AndAClickStaysWhereItIs(int rate, int channels)
    {
        const double clickAt = 1.25;
        const int seconds = 2;
        var frames = rate * seconds;
        var pcm = new short[frames * channels];
        var click = (int)(clickAt * rate);
        for (var frame = click; frame < click + (rate / 200); frame++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                pcm[(frame * channels) + channel] = 20000;
            }
        }

        var samples = StudioCaptionAudio.Read(new BlockSource(pcm, 4096 * channels), new StudioAudioFormat(rate, channels), frames, null, CancellationToken.None);

        Assert.InRange(samples.Length, (StudioCaptionAudio.SampleRate * seconds) - 200, StudioCaptionAudio.SampleRate * seconds);
        var loudest = samples.Select(Math.Abs).Max();
        Assert.InRange(loudest, 0.5f, 0.8f);
        var first = Array.FindIndex(samples, sample => Math.Abs(sample) > 0.1f);
        var heardAt = first / (double)StudioCaptionAudio.SampleRate;

        // A caption is on screen for seconds. Ten milliseconds cannot be seen.
        Assert.InRange(heardAt, clickAt - 0.01, clickAt + 0.01);
    }

    [Fact]
    public void WhatIsAboveWhatWhisperHearsDoesNotFoldBackIntoIt()
    {
        const int rate = 48000;
        var pcm = new short[rate];
        for (var frame = 0; frame < rate; frame++)
        {
            pcm[frame] = (short)(20000 * Math.Sin(2 * Math.PI * 12000 * frame / rate));
        }

        var samples = StudioCaptionAudio.Read(new BlockSource(pcm, 4096), new StudioAudioFormat(rate, 1), rate, null, CancellationToken.None);

        // Past the start, where the filter has settled: a tone at 12 kHz is gone at 16 kHz.
        Assert.True(samples.Skip(1000).Select(Math.Abs).Max() < 0.1f);
    }

    [Fact]
    public void ReadingSaysHowFarItIs_AndCanBeCancelled()
    {
        var pcm = new short[48000 * 4];
        var reported = new List<double>();

        StudioCaptionAudio.Read(new BlockSource(pcm, 4096), new StudioAudioFormat(48000, 1), pcm.Length, new ImmediateProgress<double>(reported.Add), CancellationToken.None);

        Assert.True(reported.Count > 1);
        Assert.Equal(reported.Order(), reported);
        Assert.InRange(reported[^1], 0.9, 1);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            StudioCaptionAudio.Read(new BlockSource(pcm, 4096), new StudioAudioFormat(48000, 1), pcm.Length, null, cancelled.Token));
    }

    [Fact]
    public void TheEditorsWordsForGenerating()
    {
        Assert.Equal("Recognizing speech: 40%", StudioCaptionText.GetProgressText(new StudioCaptionProgress(StudioCaptionStage.Transcribing, 0.4)));
        Assert.Equal("Downloading the model: 100%", StudioCaptionText.GetProgressText(new StudioCaptionProgress(StudioCaptionStage.DownloadingModel, 2)));
        Assert.Equal("Reading the sound: 0%", StudioCaptionText.GetProgressText(default(StudioCaptionProgress) with { Stage = StudioCaptionStage.ReadingSound }));
        Assert.Equal("Generated 1 caption.", StudioCaptionText.GetGeneratedMessage(1));
        Assert.Equal("Generated 12 captions.", StudioCaptionText.GetGeneratedMessage(12));
        var model = StudioCaptionModels.Default;
        Assert.Equal("Generate", StudioCaptionText.GetGenerateActionText(model, true));
        Assert.Equal("Download 142 MB and generate", StudioCaptionText.GetGenerateActionText(model, false));
        Assert.EndsWith("Already on this computer.", StudioCaptionText.GetModelNote(model, true), StringComparison.Ordinal);
        Assert.EndsWith("Downloads 142 MB once, from Hugging Face.", StudioCaptionText.GetModelNote(model, false), StringComparison.Ordinal);
        Assert.Equal("No models downloaded.", StudioCaptionText.GetInstalledModelsText(0, 0));
        Assert.Equal("1 model downloaded, using 141 MB.", StudioCaptionText.GetInstalledModelsText(1, 147951465));
        Assert.Equal("2 models downloaded, using 1.5 GB.", StudioCaptionText.GetInstalledModelsText(2, 1610612736));
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class BlockSource(short[] pcm, int blockSamples) : IStudioPcmSource
    {
        private readonly byte[] _block = new byte[blockSamples * sizeof(short)];
        private int _offset;

        public void Seek(long sample) => throw new NotSupportedException();

        public bool TryRead(out long startSample, out ReadOnlySpan<byte> block)
        {
            startSample = _offset;
            var samples = Math.Min(blockSamples, pcm.Length - _offset);
            if (samples <= 0)
            {
                block = default;
                return false;
            }

            Buffer.BlockCopy(pcm, _offset * sizeof(short), _block, 0, samples * sizeof(short));
            _offset += samples;
            block = _block.AsSpan(0, samples * sizeof(short));
            return true;
        }
    }

    private sealed class FailingStream : MemoryStream
    {
        public FailingStream()
            : base(new byte[16])
        {
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position == 0
                ? base.ReadAsync(buffer[..8], cancellationToken)
                : throw new IOException("The connection was lost.");
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("tinyclips-models-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
