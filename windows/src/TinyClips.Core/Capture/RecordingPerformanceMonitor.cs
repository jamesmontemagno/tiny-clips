using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TinyClips.Core.Capture;

/// <summary>
/// Named stages of the recording frame path. Each stage is timed per frame by
/// <see cref="RecordingPerformanceMonitor"/> so a recording produces a per-stage cost
/// breakdown (average / p99 / max) instead of a single opaque "CPU was high" signal.
/// </summary>
public enum RecordingStage
{
    /// <summary>WGC frame arrival → frame cached (CPU path: GPU→CPU staging map + copy; GPU path: GPU CopyResource).</summary>
    CaptureReadback,

    /// <summary>Pump tick: producing the frame to emit (CPU path: byte[] clone; GPU path: pooled texture acquire + CopySubresourceRegion).</summary>
    FrameProduce,

    /// <summary>Overlay compositing (click rings, branding badge, webcam PiP) — total per frame.</summary>
    Composite,

    /// <summary>Click-ring overlay portion of <see cref="Composite"/>.</summary>
    OverlayClicks,

    /// <summary>Branding-badge portion of <see cref="Composite"/>.</summary>
    OverlayBranding,

    /// <summary>Webcam picture-in-picture portion of <see cref="Composite"/> (includes the GPU upload on the GPU path).</summary>
    OverlayWebcam,

    /// <summary>Converting the composited frame into an encoder sample (CPU path: bottom-up flip copy; GPU path: surface wrap).</summary>
    SamplePrepare,

    /// <summary>Time the encoder's SampleRequested waited for a frame to become available.</summary>
    EncoderWait,

    /// <summary>GPU path only: time from sample hand-off to MediaStreamSample.Processed (encoder hold time).</summary>
    EncoderHold,
}

/// <summary>Immutable summary of one stage's timing distribution (milliseconds).</summary>
public sealed record RecordingStageStats(
    RecordingStage Stage,
    long Count,
    double AverageMs,
    double P99Ms,
    double MaxMs,
    double TotalMs);

/// <summary>
/// End-of-recording performance report. Produced by <see cref="RecordingPerformanceMonitor.Complete"/>
/// and surfaced through <see cref="IVideoRecordingService.LastPerformanceReport"/> so the
/// benchmark harness and diagnostics log can compare pipelines on equal terms.
/// </summary>
public sealed record RecordingPerformanceReport(
    string Pipeline,
    string EncoderPath,
    int Width,
    int Height,
    int TargetFps,
    TimeSpan WallClock,
    long FramesEmitted,
    long FramesEncoded,
    long FramesDropped,
    double ProcessCpuPercent,
    double ProcessCpuCores,
    long ManagedAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    TimeSpan GcPauseTotal,
    long PeakWorkingSetBytes,
    IReadOnlyList<RecordingStageStats> Stages)
{
    public double EffectiveFps => WallClock.TotalSeconds > 0 ? FramesEncoded / WallClock.TotalSeconds : 0;

    public double DropPercent => FramesEmitted > 0 ? 100.0 * FramesDropped / FramesEmitted : 0;

    /// <summary>Share of wall-clock time during which the runtime had all managed threads suspended for GC.</summary>
    public double GcPausePercent => WallClock.TotalSeconds > 0 ? 100.0 * GcPauseTotal.TotalSeconds / WallClock.TotalSeconds : 0;

    /// <summary>Managed allocation rate over the recording, in MB/s.</summary>
    public double AllocationMbPerSecond => WallClock.TotalSeconds > 0
        ? ManagedAllocatedBytes / 1024.0 / 1024.0 / WallClock.TotalSeconds
        : 0;

    public string ToSummaryLine() => string.Create(
        CultureInfo.InvariantCulture,
        $"pipeline={Pipeline} encoder='{EncoderPath}' {Width}x{Height}@{TargetFps} wall={WallClock.TotalSeconds:F2}s emitted={FramesEmitted} encoded={FramesEncoded} dropped={FramesDropped} ({DropPercent:F1}%) effFps={EffectiveFps:F1} cpu={ProcessCpuPercent:F1}% ({ProcessCpuCores:F2} cores) alloc={AllocationMbPerSecond:F1}MB/s gc0/1/2={Gen0Collections}/{Gen1Collections}/{Gen2Collections} gcPause={GcPauseTotal.TotalMilliseconds:F0}ms ({GcPausePercent:F1}%) peakWS={PeakWorkingSetBytes / 1024 / 1024}MB");

    public string ToTable()
    {
        var sb = new StringBuilder();
        sb.AppendLine(ToSummaryLine());
        sb.AppendLine("  stage             count      avg ms     p99 ms     max ms   total ms");
        foreach (var s in Stages)
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {s.Stage,-16} {s.Count,6} {s.AverageMs,11:F3} {s.P99Ms,10:F3} {s.MaxMs,10:F3} {s.TotalMs,10:F1}"));
        }

        return sb.ToString();
    }
}

/// <summary>
/// Low-overhead per-recording profiler. Stage timings are recorded with <see cref="Stopwatch"/>
/// ticks into fixed-size reservoirs (no per-frame allocation on the hot path); process CPU time,
/// GC counters and working set are sampled at start and stop. One instance per recording.
/// </summary>
public sealed class RecordingPerformanceMonitor
{
    private const int ReservoirSize = 4096;

    private readonly StageBucket[] _buckets;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Stopwatch _wall = new();
    private TimeSpan _cpuAtStart;
    private long _allocAtStart;
    private int _gen0AtStart;
    private int _gen1AtStart;
    private int _gen2AtStart;
    private TimeSpan _gcPauseAtStart;
    private long _framesEmitted;
    private long _framesEncoded;
    private long _framesDropped;

    public RecordingPerformanceMonitor(string pipeline, int width, int height, int targetFps)
    {
        Pipeline = pipeline;
        Width = width;
        Height = height;
        TargetFps = targetFps;
        var stages = Enum.GetValues<RecordingStage>();
        _buckets = new StageBucket[stages.Length];
        for (var i = 0; i < stages.Length; i++)
        {
            _buckets[i] = new StageBucket(stages[i]);
        }
    }

    public string Pipeline { get; }

    public int Width { get; set; }

    public int Height { get; set; }

    public int TargetFps { get; }

    public string EncoderPath { get; set; } = "unknown";

    public bool IsRunning => _wall.IsRunning;

    public void Start()
    {
        _process.Refresh();
        _cpuAtStart = _process.TotalProcessorTime;
        _allocAtStart = GC.GetTotalAllocatedBytes(precise: false);
        _gen0AtStart = GC.CollectionCount(0);
        _gen1AtStart = GC.CollectionCount(1);
        _gen2AtStart = GC.CollectionCount(2);
        _gcPauseAtStart = GC.GetTotalPauseDuration();
        _peakWorkingSet = _process.WorkingSet64;
        _lastWorkingSetSampleTicks = Stopwatch.GetTimestamp();
        _wall.Restart();
    }

    private long _peakWorkingSet;
    private long _lastWorkingSetSampleTicks;

    /// <summary>
    /// Samples the working set about once a second so the report's peak reflects this recording,
    /// not the process lifetime (<c>Process.PeakWorkingSet64</c> is monotonic across sequential
    /// benchmark runs and would make later scenarios inherit earlier peaks).
    /// </summary>
    private void SampleWorkingSet(bool force = false)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && now - Volatile.Read(ref _lastWorkingSetSampleTicks) < Stopwatch.Frequency)
        {
            return;
        }

        Volatile.Write(ref _lastWorkingSetSampleTicks, now);
        try
        {
            _process.Refresh();
            var current = _process.WorkingSet64;
            long peak;
            while (current > (peak = Volatile.Read(ref _peakWorkingSet)) &&
                   Interlocked.CompareExchange(ref _peakWorkingSet, current, peak) != peak)
            {
            }
        }
        catch
        {
            // Diagnostics only.
        }
    }

    /// <summary>Returns a Stopwatch timestamp to pair with <see cref="End"/>.</summary>
    public static long Begin() => Stopwatch.GetTimestamp();

    public void End(RecordingStage stage, long beginTimestamp)
    {
        if (!_wall.IsRunning)
        {
            return;
        }

        _buckets[(int)stage].Add(Stopwatch.GetTimestamp() - beginTimestamp);
    }

    /// <summary>Records an already-measured duration (e.g. from a timestamp captured on another thread).</summary>
    public void Record(RecordingStage stage, long elapsedTicks)
    {
        if (_wall.IsRunning && elapsedTicks >= 0)
        {
            _buckets[(int)stage].Add(elapsedTicks);
        }
    }

    public void FrameEmitted() => Interlocked.Increment(ref _framesEmitted);

    public void FrameEncoded()
    {
        Interlocked.Increment(ref _framesEncoded);
        if (_wall.IsRunning)
        {
            SampleWorkingSet();
        }
    }

    public void FrameDropped() => Interlocked.Increment(ref _framesDropped);

    public void SetDroppedFrames(long dropped) => Interlocked.Exchange(ref _framesDropped, dropped);

    public RecordingPerformanceReport Complete()
    {
        _wall.Stop();
        SampleWorkingSet(force: true);
        _process.Refresh();
        var cpu = _process.TotalProcessorTime - _cpuAtStart;
        var wall = _wall.Elapsed;
        var cores = wall.TotalSeconds > 0 ? cpu.TotalSeconds / wall.TotalSeconds : 0;
        var percent = 100.0 * cores / Math.Max(1, Environment.ProcessorCount);

        var stages = new List<RecordingStageStats>(_buckets.Length);
        foreach (var bucket in _buckets)
        {
            if (bucket.Count > 0)
            {
                stages.Add(bucket.ToStats());
            }
        }

        return new RecordingPerformanceReport(
            Pipeline,
            EncoderPath,
            Width,
            Height,
            TargetFps,
            wall,
            Interlocked.Read(ref _framesEmitted),
            Interlocked.Read(ref _framesEncoded),
            Interlocked.Read(ref _framesDropped),
            percent,
            cores,
            GC.GetTotalAllocatedBytes(precise: false) - _allocAtStart,
            GC.CollectionCount(0) - _gen0AtStart,
            GC.CollectionCount(1) - _gen1AtStart,
            GC.CollectionCount(2) - _gen2AtStart,
            GC.GetTotalPauseDuration() - _gcPauseAtStart,
            Volatile.Read(ref _peakWorkingSet),
            stages);
    }

    private sealed class StageBucket
    {
        private readonly long[] _reservoir = new long[ReservoirSize];
        private readonly object _gate = new();
        private long _count;
        private long _totalTicks;
        private long _maxTicks;

        public StageBucket(RecordingStage stage)
        {
            Stage = stage;
        }

        public RecordingStage Stage { get; }

        public long Count => Interlocked.Read(ref _count);

        public void Add(long ticks)
        {
            lock (_gate)
            {
                // Reservoir sampling (Algorithm R) keeps p99 representative without unbounded memory.
                var n = _count++;
                if (n < ReservoirSize)
                {
                    _reservoir[n] = ticks;
                }
                else
                {
                    var j = Random.Shared.NextInt64(n + 1);
                    if (j < ReservoirSize)
                    {
                        _reservoir[j] = ticks;
                    }
                }

                _totalTicks += ticks;
                if (ticks > _maxTicks)
                {
                    _maxTicks = ticks;
                }
            }
        }

        public RecordingStageStats ToStats()
        {
            lock (_gate)
            {
                var sampleCount = (int)Math.Min(_count, ReservoirSize);
                var samples = new long[sampleCount];
                Array.Copy(_reservoir, samples, sampleCount);
                Array.Sort(samples);
                // Nearest-rank percentile: the value at rank ceil(0.99·n), 1-based.
                var p99 = sampleCount > 0 ? samples[Math.Clamp((int)Math.Ceiling(sampleCount * 0.99) - 1, 0, sampleCount - 1)] : 0;
                return new RecordingStageStats(
                    Stage,
                    _count,
                    _count > 0 ? ToMs(_totalTicks) / _count : 0,
                    ToMs(p99),
                    ToMs(_maxTicks),
                    ToMs(_totalTicks));
            }
        }

        private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}
