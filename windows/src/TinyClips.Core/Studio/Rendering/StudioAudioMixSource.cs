using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace TinyClips.Core.Studio.Rendering;

/// <summary>Mixes all sound tracks on their source timestamps, before cuts, speed and volume.</summary>
internal sealed class StudioAudioMixSource : IStudioPcmSource, IDisposable
{
    private const int BlockSamples = 4096;
    private readonly Track[] _tracks;
    private readonly long _totalSamples;
    private readonly int[] _sum;
    private readonly short[] _output;
    private long _cursor;

    internal StudioAudioMixSource(IEnumerable<IStudioPcmSource> sources, StudioAudioFormat format, long totalSamples, double[]? gains = null)
    {
        Format = format;
        _tracks = sources.Select((source, index) => new Track(source, gains?[index] ?? 1)).ToArray();
        _totalSamples = totalSamples;
        _sum = new int[BlockSamples * format.Channels];
        _output = new short[_sum.Length];
    }

    public StudioAudioFormat Format { get; }

    public static StudioAudioMixSource? TryOpen(string path, string description, double duration, StudioAudioFormat? requestedFormat = null,
        StudioScreenSource? screen = null, StudioAudio? audio = null)
    {
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
        var streams = new List<SourceReaderIndex>();
        for (var index = 0; ; index++)
        {
            try
            {
                using var native = reader.GetNativeMediaType((SourceReaderIndex)index, 0);
                if (native.GetGUID(MediaTypeAttributeKeys.MajorType) == MediaTypeGuids.Audio)
                {
                    streams.Add((SourceReaderIndex)index);
                }
            }
            catch (SharpGenException ex) when (ex.HResult == StudioMediaFoundation.InvalidStreamNumber)
            {
                break;
            }
        }
        if (streams.Count == 0)
        {
            return null;
        }

        var sources = new List<StudioAudioSource>();
        try
        {
            // A single track retains its format. Multiple tracks use identical PCM for mixing.
            StudioAudioFormat? format = requestedFormat ?? (streams.Count > 1 ? new(48000, 2) : null);
            foreach (var stream in streams)
            {
                var source = StudioAudioSource.TryOpen(path, description, stream, format)
                    ?? throw new StudioExportException($"An audio track in the {description} could not be opened.");
                sources.Add(source);
                format = source.Format;
            }
            double[]? gains = null;
            if (screen is not null && audio is not null
                && screen.ExtensionData.TryGetValue("audioTracks", out var labels)
                && labels.ValueKind == System.Text.Json.JsonValueKind.Array && labels.GetArrayLength() == streams.Count)
            {
                gains = labels.EnumerateArray().Select(label => label.ValueKind == System.Text.Json.JsonValueKind.String
                    ? label.GetString() switch
                    {
                        "system" => StudioSound.Volume(audio.SystemVolume),
                        "microphone" => StudioSound.Volume(audio.MicrophoneVolume),
                        _ => 1,
                    } : 1).ToArray();
            }
            return new StudioAudioMixSource(sources, format!, (long)Math.Ceiling(duration * format!.SampleRate), gains);
        }
        catch
        {
            foreach (var source in sources)
            {
                source.Dispose();
            }
            throw;
        }
    }

    public void Seek(long sample)
    {
        _cursor = sample;
        foreach (var track in _tracks)
        {
            track.Source.Seek(sample);
            track.Samples = [];
            track.Ended = false;
        }
    }

    public bool TryRead(out long startSample, out ReadOnlySpan<byte> pcm)
    {
        pcm = default;
        if (!TryReadWide(out startSample, out var samples))
        {
            return false;
        }
        for (var i = 0; i < samples.Length; i++)
        {
            _output[i] = (short)Math.Clamp(samples[i], short.MinValue, short.MaxValue);
        }
        pcm = MemoryMarshal.AsBytes(_output.AsSpan(0, samples.Length));
        return true;
    }

    internal bool TryReadWide(out long startSample, out ReadOnlySpan<int> samples)
    {
        startSample = _cursor;
        samples = default;
        var count = (int)Math.Min(BlockSamples, _totalSamples - _cursor);
        if (count <= 0)
        {
            return false;
        }
        Array.Clear(_sum);
        var end = _cursor + count;
        foreach (var track in _tracks)
        {
            while (!track.Ended)
            {
                var blockEnd = track.Start + track.Samples.Length / Format.Channels;
                if (track.Samples.Length == 0 || blockEnd <= _cursor)
                {
                    if (!track.Source.TryRead(out track.Start, out var block))
                    {
                        track.Ended = true;
                        break;
                    }
                    track.Samples = MemoryMarshal.Cast<byte, short>(block).ToArray();
                    continue;
                }
                if (track.Start >= end)
                {
                    break;
                }
                var from = Math.Max(_cursor, track.Start);
                var to = Math.Min(end, blockEnd);
                var sourceOffset = checked((int)((from - track.Start) * Format.Channels));
                var targetOffset = checked((int)((from - _cursor) * Format.Channels));
                var values = checked((int)((to - from) * Format.Channels));
                for (var i = 0; i < values; i++)
                {
                    _sum[targetOffset + i] += (int)Math.Round(track.Samples[sourceOffset + i] * track.Gain);
                }
                if (blockEnd >= end)
                {
                    break;
                }
                track.Samples = [];
            }
        }
        _cursor = end;
        samples = _sum.AsSpan(0, count * Format.Channels);
        return true;
    }

    public void Dispose()
    {
        foreach (var track in _tracks)
        {
            (track.Source as IDisposable)?.Dispose();
        }
    }

    private sealed class Track(IStudioPcmSource source, double gain)
    {
        public IStudioPcmSource Source { get; } = source;
        public double Gain { get; } = gain;
        public short[] Samples = [];
        public long Start;
        public bool Ended;
    }
}
