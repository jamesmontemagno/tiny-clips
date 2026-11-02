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

    internal StudioAudioMixSource(IEnumerable<IStudioPcmSource> sources, StudioAudioFormat format, long totalSamples)
    {
        Format = format;
        _tracks = sources.Select(source => new Track(source)).ToArray();
        _totalSamples = totalSamples;
        _sum = new int[BlockSamples * format.Channels];
        _output = new short[_sum.Length];
    }

    public StudioAudioFormat Format { get; }

    public static StudioAudioMixSource? TryOpen(string path, string description, double duration)
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
            StudioAudioFormat? format = streams.Count > 1 ? new(48000, 2) : null;
            foreach (var stream in streams)
            {
                var source = StudioAudioSource.TryOpen(path, description, stream, format)
                    ?? throw new StudioExportException($"An audio track in the {description} could not be opened.");
                sources.Add(source);
                format = source.Format;
            }
            return new StudioAudioMixSource(sources, format!, (long)Math.Ceiling(duration * format!.SampleRate));
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
        startSample = _cursor;
        pcm = default;
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
                    _sum[targetOffset + i] += track.Samples[sourceOffset + i];
                }
                if (blockEnd >= end)
                {
                    break;
                }
                track.Samples = [];
            }
        }
        for (var i = 0; i < count * Format.Channels; i++)
        {
            _output[i] = (short)Math.Clamp(_sum[i], short.MinValue, short.MaxValue);
        }
        _cursor = end;
        pcm = MemoryMarshal.AsBytes(_output.AsSpan(0, count * Format.Channels));
        return true;
    }

    public void Dispose()
    {
        foreach (var track in _tracks)
        {
            (track.Source as IDisposable)?.Dispose();
        }
    }

    private sealed class Track(IStudioPcmSource source)
    {
        public IStudioPcmSource Source { get; } = source;
        public short[] Samples = [];
        public long Start;
        public bool Ended;
    }
}
