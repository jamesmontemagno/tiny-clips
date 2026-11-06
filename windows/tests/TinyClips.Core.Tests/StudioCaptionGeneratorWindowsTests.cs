using System.Text;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Transcription;

namespace TinyClips.Core.Tests;

/// <summary>
/// The parts of generating captions that only exist on Windows: the decoder, and the native speech
/// libraries the app ships. A real model is too large to download on every build, so a damaged one
/// shows that the libraries load: they do, or it would not get as far as finding the model damaged.
/// </summary>
public sealed class StudioCaptionGeneratorWindowsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("TinyClipsCaptions").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A stereo recording at 48 kHz: a tone for the first half, and then silence.</summary>
    private string WriteRecording(int seconds)
    {
        const int Rate = 48000;
        const short Channels = 2;
        var frames = Rate * seconds;
        var path = Path.Combine(_directory, "screen.wav");
        using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
        var dataBytes = frames * Channels * sizeof(short);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(Channels);
        writer.Write(Rate);
        writer.Write(Rate * Channels * sizeof(short));
        writer.Write((short)(Channels * sizeof(short)));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (var frame = 0; frame < frames; frame++)
        {
            var sample = frame < frames / 2
                ? (short)(Math.Sin(2 * Math.PI * 440 * frame / Rate) * short.MaxValue * 0.5)
                : (short)0;
            writer.Write(sample);
            writer.Write(sample);
        }

        return path;
    }

    private static double Loudness(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    [Fact]
    public void TheSoundOfARecording_ComesOutAsSpeechRecognitionWantsIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is only on Windows.");
        var path = WriteRecording(seconds: 2);

        var samples = StudioCaptionGenerator.ReadSound(path, 2, progress: null, TestContext.Current.CancellationToken);

        Assert.InRange(samples.Length, 31_900, 32_100);
        Assert.InRange(Loudness(samples.AsSpan(1000, 14_000)), 0.2, 0.5);
        Assert.InRange(Loudness(samples.AsSpan(17_000, 14_000)), 0, 0.001);
    }

    [Fact]
    public void AFileThatIsNotARecording_SaysItCouldNotBeRead()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is only on Windows.");
        var path = Path.Combine(_directory, "screen.mp4");
        File.WriteAllText(path, "not a recording");

        Assert.Throws<StudioCaptionException>(() =>
            StudioCaptionGenerator.ReadSound(path, 2, progress: null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheNativeLibraries_AreBesideTheApp_AndLoad()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows libraries are what the app ships.");
        var damaged = Path.Combine(_directory, "ggml-base.bin");
        await File.WriteAllBytesAsync(damaged, new byte[1024], TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<StudioCaptionException>(() => StudioCaptionGenerator.TranscribeAsync(
            damaged,
            StudioCaptionModels.Default,
            new float[16_000],
            1,
            progress: null,
            TestContext.Current.CancellationToken));

        Assert.StartsWith("The Base model could not be loaded.", failure.Message);
    }
}
