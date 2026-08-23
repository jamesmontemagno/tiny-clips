using NAudio.Wave;

namespace TinyClips.Core.Capture;

/// <summary>
/// Places timestamped source packets on a shared recording timeline.
/// <para>
/// The first packet after <see cref="BeginTimeline(RecordingTimeline)"/> is aligned to the shared
/// origin (leading silence is inserted, or pre-origin frames are trimmed) so independent sources
/// that start at slightly different times stay in sync. Subsequent packets are appended
/// contiguously: re-deriving each packet's position from its (jittery, frame-rounded) timestamp
/// would insert or drop a sample or two on every ~10 ms packet, producing constant audible crackle.
/// </para>
/// <para>
/// Contiguous append alone, however, lets the stream silently diverge from the video clock whenever
/// WASAPI drops a packet (buffer overrun), the device clock drifts against QPC over a long
/// recording, or a pause/resume cycle removes packets. So every packet's expected position is still
/// compared against the frames written so far, and when the deviation exceeds
/// <see cref="DriftTolerance"/> (or the driver flags a discontinuity) the stream is corrected once:
/// silence is inserted for a gap, or frames are trimmed from the packet front for an overlap. The
/// tolerance is far above per-packet jitter, so ordinary packets are never touched.
/// </para>
/// </summary>
internal sealed class TimelineAlignedWaveProvider : IWaveProvider
{
    /// <summary>
    /// Deviation a source may accumulate before it is snapped back to the timeline. Well below
    /// lip-sync detectability (~45 ms) yet far above WASAPI/timer jitter (sub-millisecond).
    /// </summary>
    public static readonly TimeSpan DriftTolerance = TimeSpan.FromMilliseconds(30);

    private readonly BufferedWaveProvider _buffer;
    private readonly object _statsGate = new();
    private readonly string _sourceName;
    private RecordingTimeline? _timeline;
    private TimeSpan _latency;
    private TimeSpan _userOffset;
    private bool _timelineStarted;
    private bool _aligned;
    private bool _paused;
    private long _framesWritten;
    private long _correctionCount;
    private long _paddedFrames;
    private long _trimmedFrames;
    private long _underrunFrames;
    private long _droppedPreOriginPackets;
    private TimeSpan _lastDeviation;
    private TimeSpan _maxAbsDeviation;

    public TimelineAlignedWaveProvider(WaveFormat waveFormat, string? sourceName = null)
    {
        WaveFormat = waveFormat;
        _sourceName = sourceName ?? "audio";
        _buffer = new BufferedWaveProvider(waveFormat)
        {
            ReadFully = true,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(5),
        };
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>How much captured, timeline-aligned audio is currently buffered and ready to read.</summary>
    public TimeSpan BufferedDuration => _timelineStarted && _aligned ? _buffer.BufferedDuration : TimeSpan.Zero;

    /// <summary>
    /// The source's capture latency. Audio is advanced by this amount so the recorded sound lines up
    /// with video captured at the same wall-clock instant.
    /// </summary>
    public TimeSpan Latency
    {
        get => _latency;
        set => _latency = value;
    }

    /// <summary>
    /// User-configured correction. Positive values delay audio relative to video; negative values
    /// play it earlier (for devices such as Bluetooth headsets whose real latency WASAPI does not
    /// report).
    /// </summary>
    public TimeSpan UserOffset
    {
        get => _userOffset;
        set => _userOffset = value;
    }

    /// <summary>Total frames placed on the timeline so far, including inserted silence.</summary>
    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    public SyncStats GetStats()
    {
        lock (_statsGate)
        {
            return new SyncStats(
                _sourceName,
                WaveFormat.SampleRate,
                Interlocked.Read(ref _framesWritten),
                _correctionCount,
                _paddedFrames,
                _trimmedFrames,
                _underrunFrames,
                _droppedPreOriginPackets,
                _lastDeviation,
                _maxAbsDeviation);
        }
    }

    public void BeginTimeline(TimeSpan origin) => BeginTimeline(RecordingTimeline.FromOrigin(origin));

    public void BeginTimeline(RecordingTimeline timeline)
    {
        _timeline = timeline;
        _buffer.ClearBuffer();
        _timelineStarted = true;
        _aligned = false;
        _paused = false;
        Interlocked.Exchange(ref _framesWritten, 0);
        lock (_statsGate)
        {
            _correctionCount = 0;
            _paddedFrames = 0;
            _trimmedFrames = 0;
            _underrunFrames = 0;
            _droppedPreOriginPackets = 0;
            _lastDeviation = TimeSpan.Zero;
            _maxAbsDeviation = TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Stops accepting new packets. Audio already captured is retained: it belongs to pre-pause
    /// video time and the muxer still needs to drain it, otherwise every pause would shift the
    /// rest of the track earlier by the discarded amount.
    /// </summary>
    public void Pause()
    {
        _paused = true;
    }

    /// <summary>
    /// Resumes accepting packets. No explicit re-alignment is needed: the next packet's expected
    /// position (derived from the paused-adjusted timeline) is compared against frames written,
    /// and any gap or overlap beyond the tolerance is corrected like any other discontinuity.
    /// </summary>
    public void Resume()
    {
        _paused = false;
    }

    public void AddSamples(byte[] samples, int count, TimeSpan sourceTimestamp) =>
        AddSamples(samples, count, sourceTimestamp, discontinuity: false);

    /// <param name="discontinuity">
    /// True when the driver flagged this packet as following a gap (WASAPI
    /// <c>AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY</c>). Forces a correction regardless of tolerance.
    /// </param>
    public void AddSamples(byte[] samples, int count, TimeSpan sourceTimestamp, bool discontinuity)
    {
        var timeline = _timeline;
        if (!_timelineStarted || timeline is null || _paused || count <= 0)
        {
            return;
        }

        var blockAlign = WaveFormat.BlockAlign;
        var packetFrames = count / blockAlign;
        if (packetFrames <= 0)
        {
            return;
        }

        // Where this packet's first frame belongs on the output stream. The timeline subtracts the
        // shared origin and any paused time; the latency advance compensates for WASAPI stamping
        // the buffer read rather than the true acoustic capture instant; the user offset is an
        // explicit manual correction.
        var targetOffset = timeline.Normalize(sourceTimestamp) - _latency + _userOffset;
        var targetFrame = ToFrames(targetOffset);
        var deviationFrames = targetFrame - Interlocked.Read(ref _framesWritten);
        var byteOffset = 0;
        var firstPacket = false;

        if (!_aligned)
        {
            if (deviationFrames + packetFrames <= 0)
            {
                // The entire packet is before the (latency-compensated) origin. Drop it and keep
                // waiting: later packets carry later timestamps, so one of them will straddle the
                // origin. This discards ALL pre-origin pre-roll, not just the first packet's worth.
                lock (_statsGate)
                {
                    _droppedPreOriginPackets++;
                }

                return;
            }

            // The first packet is always positioned exactly (no tolerance): this is what anchors
            // the source to the shared origin.
            _aligned = true;
            firstPacket = true;
        }

        var deviation = ToTime(deviationFrames);
        var toleranceFrames = ToFrames(DriftTolerance);
        var forced = firstPacket || discontinuity;

        lock (_statsGate)
        {
            if (!firstPacket)
            {
                _lastDeviation = deviation;
                if (deviation.Duration() > _maxAbsDeviation)
                {
                    _maxAbsDeviation = deviation.Duration();
                }
            }
        }

        if (deviationFrames != 0 && (forced || Math.Abs(deviationFrames) > toleranceFrames))
        {
            var reason = firstPacket ? "aligned" : discontinuity ? "sync correction (driver discontinuity)" : "sync correction";
            if (deviationFrames > 0)
            {
                // Gap: the source starts (or fell) behind its expected position — late start,
                // dropped packet, pause, slow device clock. Insert exactly that much silence.
                AddSilence(deviationFrames);
                Interlocked.Add(ref _framesWritten, deviationFrames);
                lock (_statsGate)
                {
                    if (!firstPacket)
                    {
                        _correctionCount++;
                        _paddedFrames += deviationFrames;
                    }
                }

                WebcamDiagnostics.Log($"TimelineAlignedWaveProvider[{_sourceName}] {reason}: padded {deviation.TotalMilliseconds:F1} ms{Describe(sourceTimestamp, timeline)}.");
            }
            else
            {
                // Overlap: the source ran ahead — pre-origin frames in the straddling first packet,
                // fast device clock, or a resumed packet that began before the resume point. Trim
                // the excess from the front of this packet; if the packet is shorter than the
                // overlap, the next packet trims the remainder.
                var trimFrames = Math.Min(packetFrames, -deviationFrames);
                byteOffset = checked((int)(trimFrames * blockAlign));
                lock (_statsGate)
                {
                    if (!firstPacket)
                    {
                        _correctionCount++;
                        _trimmedFrames += trimFrames;
                    }
                }

                WebcamDiagnostics.Log($"TimelineAlignedWaveProvider[{_sourceName}] {reason}: trimmed {ToTime(trimFrames).TotalMilliseconds:F1} ms of {(-deviation).TotalMilliseconds:F1} ms overlap{Describe(sourceTimestamp, timeline)}.");
                if (byteOffset >= count)
                {
                    return;
                }
            }
        }
        else if (firstPacket)
        {
            WebcamDiagnostics.Log($"TimelineAlignedWaveProvider[{_sourceName}] aligned exactly{Describe(sourceTimestamp, timeline)}.");
        }

        var alignedCount = ((count - byteOffset) / blockAlign) * blockAlign;
        if (alignedCount > 0)
        {
            _buffer.AddSamples(samples, byteOffset, alignedCount);
            Interlocked.Add(ref _framesWritten, alignedCount / blockAlign);
        }
    }

    /// <summary>
    /// Reads mixed output. When the buffer runs dry the underlying provider pads zeros
    /// (<c>ReadFully</c>); those padded frames occupy real positions on the output timeline, so the
    /// written cursor advances to cover them. The next packet then sees an overlap and is trimmed,
    /// instead of landing late behind silence the muxer has already consumed.
    /// </summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        var bufferedBefore = _buffer.BufferedBytes;
        var read = _buffer.Read(buffer, offset, count);
        if (read > bufferedBefore)
        {
            var underrunFrames = (read - bufferedBefore) / WaveFormat.BlockAlign;
            if (underrunFrames > 0 && _timelineStarted)
            {
                Interlocked.Add(ref _framesWritten, underrunFrames);
                lock (_statsGate)
                {
                    _underrunFrames += underrunFrames;
                }
            }
        }

        return read;
    }

    private string Describe(TimeSpan sourceTimestamp, RecordingTimeline timeline) =>
        $" (sourceOffsetMs={(sourceTimestamp - timeline.Origin).TotalMilliseconds:F1} latencyMs={_latency.TotalMilliseconds:F1} userOffsetMs={_userOffset.TotalMilliseconds:F1})";

    private long ToFrames(TimeSpan time) =>
        (long)Math.Round(time.Ticks * WaveFormat.SampleRate / (double)TimeSpan.TicksPerSecond);

    private TimeSpan ToTime(long frames) =>
        TimeSpan.FromTicks((long)Math.Round(frames * (double)TimeSpan.TicksPerSecond / WaveFormat.SampleRate));

    private void AddSilence(long frameCount)
    {
        const int MaxChunkBytes = 16 * 1024;
        var blockAlign = WaveFormat.BlockAlign;
        var framesPerChunk = Math.Max(1, MaxChunkBytes / blockAlign);
        var silence = new byte[framesPerChunk * blockAlign];

        while (frameCount > 0)
        {
            var frames = (int)Math.Min(frameCount, framesPerChunk);
            _buffer.AddSamples(silence, 0, frames * blockAlign);
            frameCount -= frames;
        }
    }

    /// <summary>Per-source sync bookkeeping for the end-of-recording diagnostics report.</summary>
    public sealed record SyncStats(
        string SourceName,
        int SampleRate,
        long FramesWritten,
        long CorrectionCount,
        long PaddedFrames,
        long TrimmedFrames,
        long UnderrunFrames,
        long DroppedPreOriginPackets,
        TimeSpan LastDeviation,
        TimeSpan MaxAbsDeviation)
    {
        public TimeSpan Written => TimeSpan.FromSeconds(FramesWritten / (double)SampleRate);
        public TimeSpan Padded => TimeSpan.FromSeconds(PaddedFrames / (double)SampleRate);
        public TimeSpan Trimmed => TimeSpan.FromSeconds(TrimmedFrames / (double)SampleRate);
        public TimeSpan Underrun => TimeSpan.FromSeconds(UnderrunFrames / (double)SampleRate);

        public override string ToString() =>
            $"{SourceName}: written={Written.TotalSeconds:F3}s corrections={CorrectionCount} padded={Padded.TotalMilliseconds:F1}ms trimmed={Trimmed.TotalMilliseconds:F1}ms underrun={Underrun.TotalMilliseconds:F1}ms preOriginDropped={DroppedPreOriginPackets} lastDeviation={LastDeviation.TotalMilliseconds:F1}ms maxDeviation={MaxAbsDeviation.TotalMilliseconds:F1}ms";
    }
}
