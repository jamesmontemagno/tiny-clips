using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Core.Studio.Transcription;

/// <summary>
/// Turns a recording's sound into what Whisper takes: one channel of floats at 16 kHz. It reads
/// the mix the exporter reads, so its samples sit where the video's sound does, on the source
/// timeline. OutroKit asks ffmpeg for the same thing; the app has the decoder already.
/// </summary>
internal static class StudioCaptionAudio
{
    public const int SampleRate = 16000;

    /// <summary>All the samples of a source of <paramref name="sourceFrames"/> frames.</summary>
    /// <param name="progress">How far the reading is, from 0 to 1.</param>
    public static float[] Read(IStudioPcmSource source, StudioAudioFormat format, long sourceFrames, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(format);

        var mono = new MonoSamples(source, format);
        ISampleProvider provider = format.SampleRate == SampleRate
            ? mono
            : new WdlResamplingSampleProvider(mono, SampleRate);

        var capacity = checked((int)Math.Ceiling((double)sourceFrames * SampleRate / format.SampleRate));
        var samples = new float[capacity];
        var count = 0;
        while (count < capacity)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = provider.Read(samples.AsSpan(count, Math.Min(SampleRate, capacity - count)));
            if (read == 0)
            {
                break;
            }

            count += read;
            progress?.Report((double)count / capacity);
        }

        if (count < capacity)
        {
            Array.Resize(ref samples, count);
        }

        return samples;
    }

    // Blocks of 16-bit PCM of any number of channels, as one channel of floats.
    private sealed class MonoSamples(IStudioPcmSource source, StudioAudioFormat format) : ISampleProvider
    {
        private short[] _pending = [];
        private int _pendingOffset;
        private int _pendingCount;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, 1);

        public int Read(Span<float> buffer)
        {
            var channels = format.Channels;
            var written = 0;
            while (written < buffer.Length)
            {
                if (_pendingCount - _pendingOffset < channels && !TryFill())
                {
                    break;
                }

                while (written < buffer.Length && _pendingCount - _pendingOffset >= channels)
                {
                    var sum = 0;
                    for (var channel = 0; channel < channels; channel++)
                    {
                        sum += _pending[_pendingOffset + channel];
                    }

                    buffer[written] = sum / (float)channels / 32768f;
                    _pendingOffset += channels;
                    written++;
                }
            }

            return written;
        }

        private bool TryFill()
        {
            if (!source.TryRead(out _, out var pcm))
            {
                return false;
            }

            var samples = MemoryMarshal.Cast<byte, short>(pcm);
            if (_pending.Length < samples.Length)
            {
                _pending = new short[samples.Length];
            }

            samples.CopyTo(_pending);
            _pendingOffset = 0;
            _pendingCount = samples.Length;
            return true;
        }
    }
}
