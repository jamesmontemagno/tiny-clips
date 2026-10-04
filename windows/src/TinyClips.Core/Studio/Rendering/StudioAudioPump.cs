namespace TinyClips.Core.Studio.Rendering;

/// <summary>Receives a run of interleaved PCM and its position in the output track.</summary>
internal delegate void StudioPcmWriter(ReadOnlySpan<byte> pcm, long outputStartSample);

/// <summary>
/// Turns the decoded audio of the screen file into the audio of an export: for every kept range
/// it writes exactly the samples of that range, in order and without a gap in the output.
/// </summary>
/// <remarks>
/// <para>
/// Sample accuracy comes from counting. A decoder hands out blocks that start before the sample
/// asked for and end after the one needed last; the pump takes the part of each block that lies
/// inside the current range and nothing else. Where the file has no sound for part of a range
/// (the track starts late, has a hole, or ends early) the pump writes silence, so sound that
/// follows stays where it belongs.
/// </para>
/// <para>
/// A block is always used whole once it is read, so a call may write up to one block more than
/// it was asked to. Nothing read is ever thrown away because a call had "enough".
/// </para>
/// </remarks>
internal sealed class StudioAudioPump
{
    private const int SilenceSamples = 4096;

    private readonly IStudioPcmSource _source;
    private readonly IReadOnlyList<StudioAudioSampleRange> _ranges;
    private readonly int _blockAlign;
    private readonly StudioPcmWriter _write;
    private readonly byte[] _silence;
    private int _rangeIndex;
    private long _sourceCursor;
    private bool _rangeStarted;
    private bool _sourceEnded;

    public StudioAudioPump(IStudioPcmSource source, IReadOnlyList<StudioAudioSampleRange> ranges, int blockAlign, StudioPcmWriter write)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ranges);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockAlign, 1);
        _source = source;
        _ranges = ranges;
        _blockAlign = blockAlign;
        _write = write;
        _silence = new byte[SilenceSamples * blockAlign];
        foreach (var range in ranges)
        {
            TotalSamples += Math.Max(0, range.SampleCount);
        }
    }

    /// <summary>Samples the finished track has.</summary>
    public long TotalSamples { get; }

    /// <summary>Samples written so far.</summary>
    public long OutputSamples { get; private set; }

    public bool Done => _rangeIndex >= _ranges.Count;

    /// <summary>Writes until at least <paramref name="outputSample"/> samples are out, or the track is complete.</summary>
    public void PumpTo(long outputSample, CancellationToken cancellationToken)
    {
        while (!Done && OutputSamples < outputSample)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Step();
        }
    }

    public void PumpToEnd(CancellationToken cancellationToken) => PumpTo(long.MaxValue, cancellationToken);

    private void Step()
    {
        var range = _ranges[_rangeIndex];
        if (!_rangeStarted)
        {
            _rangeStarted = true;
            _sourceCursor = range.SourceStartSample;
            _sourceEnded = false;

            // The first range of an untrimmed export starts where the reader already is.
            if (_rangeIndex > 0 || range.SourceStartSample > 0)
            {
                _source.Seek(range.SourceStartSample);
            }
        }

        if (_sourceCursor >= range.SourceEndSample)
        {
            _rangeIndex++;
            _rangeStarted = false;
            return;
        }

        if (_sourceEnded || !_source.TryRead(out var blockStart, out var pcm))
        {
            _sourceEnded = true;
            WriteSilence(range.SourceEndSample - _sourceCursor);
            return;
        }

        var blockEnd = blockStart + (pcm.Length / _blockAlign);
        if (blockEnd <= _sourceCursor)
        {
            // Run-in before the range, or a block the decoder repeated.
            return;
        }

        if (blockStart > _sourceCursor)
        {
            WriteSilence(Math.Min(blockStart, range.SourceEndSample) - _sourceCursor);
            if (_sourceCursor >= range.SourceEndSample)
            {
                return;
            }
        }

        var from = _sourceCursor;
        var to = Math.Min(blockEnd, range.SourceEndSample);
        var slice = pcm.Slice(checked((int)((from - blockStart) * _blockAlign)), checked((int)((to - from) * _blockAlign)));
        _write(slice, OutputSamples);
        OutputSamples += to - from;
        _sourceCursor = to;
    }

    private void WriteSilence(long samples)
    {
        while (samples > 0)
        {
            var count = (int)Math.Min(samples, SilenceSamples);
            _write(_silence.AsSpan(0, count * _blockAlign), OutputSamples);
            OutputSamples += count;
            _sourceCursor += count;
            samples -= count;
        }
    }
}
