using System.Runtime.InteropServices;

namespace TinyClips.Core.Studio.Rendering;

/// <summary>Sample-counted project audio. Speed ranges are silent; only dissolves overlap and ramp sound.</summary>
internal sealed class StudioTimelineAudio : IStudioPcmSource, IDisposable
{
    private const int BlockSamples = 2048;
    private readonly Entry[] _entries;
    private readonly Dictionary<string, Reader> _readers = [];
    private readonly double[] _mix = new double[BlockSamples * 2];
    private readonly short[] _output = new short[BlockSamples * 2];
    private readonly CancellationToken _cancellationToken;
    private long _cursor;

    public StudioTimelineAudio(StudioProject project, StudioProjectPaths paths, CancellationToken cancellationToken)
    {
        var map = StudioTimelineProjection.Map(project);
        _cancellationToken = cancellationToken;
        var entries = new List<Entry>();
        foreach (var clip in map.Clips)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var local = StudioTimelineProjection.ProjectForClip(project, clip.ClipId);
            if (local.Audio.Muted) continue;
            var path = StudioTimelineProjection.PathsForClip(project, paths, clip.ClipId).ScreenPath;
            using var probe = StudioAudioMixSource.TryOpen(path, "screen recording", local.Sources.Screen.Duration, Format);
            if (probe is null) continue;
            var offset = StudioRenderingMath.SecondsToSamples(clip.ProjectStart, Format.SampleRate);
            var ranges = StudioRenderingMath.BuildAudioPlan(local, Format.SampleRate).Ranges
                .Select(range => range with { OutputStartSample = range.OutputStartSample + offset }).ToArray();
            entries.Add(new(clip, local, path, ranges));
        }
        _entries = entries.ToArray();
        TotalSamples = StudioRenderingMath.SecondsToSamples(map.Duration, Format.SampleRate);
    }

    public StudioAudioFormat Format { get; } = new(48000, 2);
    public bool HasAudio => _entries.Length > 0;
    public long TotalSamples { get; }

    public void Seek(long sample)
    {
        _cursor = Math.Clamp(sample, 0, TotalSamples);
        ReleaseReaders();
    }

    public bool TryRead(out long startSample, out ReadOnlySpan<byte> pcm)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        startSample = _cursor;
        pcm = default;
        var count = (int)Math.Min(BlockSamples, TotalSamples - _cursor);
        if (count <= 0) return false;
        Array.Clear(_mix);
        var end = _cursor + count;
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in _entries)
        {
            foreach (var range in entry.Ranges)
            {
                var from = Math.Max(_cursor, range.OutputStartSample);
                var to = Math.Min(end, range.OutputStartSample + range.SampleCount);
                if (from >= to) continue;
                active.Add(entry.Clip.ClipId);
                if (!_readers.TryGetValue(entry.Clip.ClipId, out var reader))
                {
                    var source = StudioAudioMixSource.TryOpen(entry.Path, "screen recording", entry.Project.Sources.Screen.Duration, Format,
                        entry.Project.Sources.Screen, entry.Project.Audio)
                        ?? throw new StudioExportException("A timeline sound track disappeared while playing.");
                    reader = new Reader(source);
                    _readers.Add(entry.Clip.ClipId, reader);
                }
                var sourceStart = range.SourceStartSample + from - range.OutputStartSample;
                var volume = StudioSound.Volume(entry.Project.Audio);
                reader.Mix(sourceStart, checked((int)(to - from)), (samples, sourceOffset) =>
                {
                    var projectStart = from + sourceOffset;
                    for (var i = 0; i < samples.Length / 2; i++)
                    {
                        var gain = volume * StudioTimelineFrame.Gain(entry.Clip, (projectStart + i) / (double)Format.SampleRate);
                        var destination = checked((int)((projectStart + i - _cursor) * 2));
                        _mix[destination] += samples[i * 2] * gain;
                        _mix[destination + 1] += samples[i * 2 + 1] * gain;
                    }
                }, _cancellationToken);
            }
        }
        foreach (var id in _readers.Keys.Where(id => !active.Contains(id)).ToArray())
        {
            _readers[id].Dispose();
            _readers.Remove(id);
        }
        for (var i = 0; i < count * 2; i++)
            _output[i] = (short)Math.Clamp(Math.Round(_mix[i]), short.MinValue, short.MaxValue);
        _cursor = end;
        pcm = MemoryMarshal.AsBytes(_output.AsSpan(0, count * 2));
        return true;
    }

    public void Dispose() => ReleaseReaders();

    private void ReleaseReaders()
    {
        foreach (var reader in _readers.Values) reader.Dispose();
        _readers.Clear();
    }

    private sealed record Entry(StudioTimelineMapClip Clip, StudioProject Project, string Path, StudioAudioSampleRange[] Ranges);
    private delegate void MixSamples(ReadOnlySpan<int> samples, long offset);

    private sealed class Reader(StudioAudioMixSource source) : IDisposable
    {
        private int[] _samples = [];
        private long _start;
        private long _next = -1;
        private bool _ended;

        public void Mix(long start, int count, MixSamples mix, CancellationToken cancellationToken)
        {
            if (_next != start)
            {
                source.Seek(start);
                _samples = [];
                _ended = false;
            }
            var cursor = start;
            var end = start + count;
            while (cursor < end && !_ended)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var blockEnd = _start + _samples.Length / 2;
                if (_samples.Length == 0 || blockEnd <= cursor)
                {
                    if (!source.TryReadWide(out _start, out var block))
                    {
                        _ended = true;
                        break;
                    }
                    _samples = block.ToArray();
                    continue;
                }
                if (_start >= end) break;
                cursor = Math.Max(cursor, _start);
                var to = Math.Min(end, blockEnd);
                mix(_samples.AsSpan(checked((int)((cursor - _start) * 2)), checked((int)((to - cursor) * 2))), cursor - start);
                cursor = to;
            }
            _next = end;
        }

        public void Dispose() => source.Dispose();
    }
}
