using System.Diagnostics;
using System.Threading.Channels;

namespace TinyClips.Core.Capture;

/// <summary>
/// WGC-backed <see cref="IScrollingCaptureService"/>. Frames arrive on the WGC thread only when
/// the screen changes; they are throttled to ~12 fps and handed to a single consumer task that
/// owns the <see cref="PanoramaAccumulator"/>, so stitching never blocks frame delivery.
/// </summary>
public sealed class ScrollingCaptureService : IScrollingCaptureService, IDisposable
{
    private static readonly TimeSpan MinFrameInterval = TimeSpan.FromSeconds(1.0 / 12);

    private readonly object _gate = new();

    private ContinuousCaptureSession? _session;
    private PanoramaAccumulator? _accumulator;
    private Channel<CapturedFrame>? _channel;
    private Task? _consumer;
    private long _lastEnqueuedTimestamp;
    private volatile bool _finished = true;
    private bool _reportedLimit;
    private bool _reportedFailure;

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    public event Action<int>? Progress;

    public event Action<PanoramaCaptureLimitReason>? LimitReached;

    public event Action<Exception>? Failed;

    public Task StartAsync(CaptureTarget target, PixelRect? region, PanoramaCaptureLimits limits, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ContinuousCaptureSession session;
        lock (_gate)
        {
            if (_session is not null)
            {
                throw new InvalidOperationException("A scrolling capture is already active.");
            }

            _accumulator = new PanoramaAccumulator(limits);
            _channel = Channel.CreateBounded<CapturedFrame>(new BoundedChannelOptions(4)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
            _finished = false;
            _reportedLimit = false;
            _reportedFailure = false;
            _lastEnqueuedTimestamp = 0;

            session = new ContinuousCaptureSession(target, region, targetFps: 12, includeCursor: false);
            session.FrameArrived += OnFrameArrived;
            try
            {
                session.Start();
            }
            catch
            {
                session.FrameArrived -= OnFrameArrived;
                session.Dispose();
                _channel = null;
                _accumulator = null;
                _finished = true;
                throw;
            }

            _session = session;
            var reader = _channel.Reader;
            var accumulator = _accumulator;
            _consumer = Task.Run(() => ConsumeAsync(reader, accumulator), CancellationToken.None);
        }

        CaptureFlowTrace.Mark("scrolling: capture started");
        return Task.CompletedTask;
    }

    public async Task<CapturedFrame> StopAsync()
    {
        PanoramaAccumulator accumulator;
        Task? consumer;
        lock (_gate)
        {
            if (_session is null || _accumulator is null)
            {
                throw new PanoramaCaptureException(PanoramaCaptureError.Cancelled);
            }

            accumulator = _accumulator;
            consumer = _consumer;
            TearDownSessionLocked();
        }

        if (consumer is not null)
        {
            await consumer.ConfigureAwait(false);
        }

        var result = accumulator.Finish();
        CaptureFlowTrace.Mark($"scrolling: stitched {result.FrameCount} frames -> {result.Image.Width}x{result.OutputHeight}");
        return result.Image;
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_session is null)
            {
                return;
            }

            TearDownSessionLocked();
        }

        CaptureFlowTrace.Mark("scrolling: cancelled");
    }

    public void Dispose() => Cancel();

    /// <summary>Stops WGC, completes the frame channel and clears all per-session state. Caller holds <see cref="_gate"/>.</summary>
    private void TearDownSessionLocked()
    {
        _finished = true;
        if (_session is { } session)
        {
            session.FrameArrived -= OnFrameArrived;
            try
            {
                session.Stop();
                session.Dispose();
            }
            catch
            {
                // Best-effort teardown.
            }
        }

        _session = null;
        _channel?.Writer.TryComplete();
        _channel = null;
        _accumulator = null;
        _consumer = null;
    }

    private void OnFrameArrived(CapturedFrame frame)
    {
        if (_finished)
        {
            return;
        }

        // WGC can deliver at the display refresh rate; ~12 fps is plenty for stitching.
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastEnqueuedTimestamp);
        if (last != 0 && Stopwatch.GetElapsedTime(last, now) < MinFrameInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastEnqueuedTimestamp, now, last) != last)
        {
            return;
        }

        _channel?.Writer.TryWrite(frame);
    }

    private async Task ConsumeAsync(ChannelReader<CapturedFrame> reader, PanoramaAccumulator accumulator)
    {
        await foreach (var captured in reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_finished || accumulator.ReachedLimit)
            {
                continue;
            }

            try
            {
                Process(accumulator, captured);
            }
            catch (Exception ex)
            {
                ReportFailure(ex);
            }
        }
    }

    private void Process(PanoramaAccumulator accumulator, CapturedFrame captured)
    {
        var frame = new PanoramaFrame(captured);
        if (accumulator.PreviousFrame is { } previous && !PanoramaAccumulator.AreMeaningfullyDifferent(previous, frame))
        {
            // The region repainted (caret blink, spinner) without scrolling. Never give up here:
            // the user may simply be pausing, and Done/Cancel remain available at all times.
            return;
        }

        var outcome = accumulator.Append(frame);
        switch (outcome.Status)
        {
            case PanoramaAppendStatus.Accepted:
                Progress?.Invoke(accumulator.AcceptedFrameCount);
                break;

            case PanoramaAppendStatus.LimitReached:
                Progress?.Invoke(accumulator.AcceptedFrameCount);
                ReportLimit(outcome.LimitReason ?? PanoramaCaptureLimitReason.Memory);
                break;
        }
    }

    private void ReportFailure(Exception error)
    {
        if (_reportedFailure)
        {
            return;
        }

        _reportedFailure = true;
        Failed?.Invoke(error);
    }

    private void ReportLimit(PanoramaCaptureLimitReason reason)
    {
        if (_reportedLimit)
        {
            return;
        }

        _reportedLimit = true;
        LimitReached?.Invoke(reason);
    }
}
