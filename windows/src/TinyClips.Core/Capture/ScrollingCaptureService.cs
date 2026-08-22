using System.Diagnostics;
using System.Threading.Channels;

namespace TinyClips.Core.Capture;

/// <summary>
/// WGC-backed <see cref="IScrollingCaptureService"/>. Frames arrive on the WGC thread only when
/// the screen changes; they are throttled to ~12 fps and handed to a single consumer task that
/// owns the <see cref="PanoramaAccumulator"/>, so stitching never blocks frame delivery.
/// All per-capture state lives in a <see cref="Session"/> so a stop or cancel can never be
/// confused with a later capture.
/// </summary>
public sealed class ScrollingCaptureService : IScrollingCaptureService, IDisposable
{
    private static readonly TimeSpan MinFrameInterval = TimeSpan.FromSeconds(1.0 / 12);

    private readonly object _gate = new();
    private Session? _current;

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _current is not null;
            }
        }
    }

    public event Action<int>? Progress;

    public event Action<PanoramaCaptureLimitReason>? LimitReached;

    public event Action<Exception>? Failed;

    public Task StartAsync(CaptureTarget target, PixelRect? region, PanoramaCaptureLimits limits, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_current is not null)
            {
                throw new InvalidOperationException("A scrolling capture is already active.");
            }

            var session = new Session(this, target, region, limits);
            session.Start();
            _current = session;
        }

        CaptureFlowTrace.Mark("scrolling: capture started");
        return Task.CompletedTask;
    }

    public async Task<CapturedFrame> StopAsync()
    {
        Session session;
        lock (_gate)
        {
            session = _current ?? throw new PanoramaCaptureException(PanoramaCaptureError.Cancelled);
            _current = null;
        }

        // Stop WGC, then let the consumer drain every frame that already arrived before finishing.
        var result = await session.StopAndStitchAsync().ConfigureAwait(false);
        CaptureFlowTrace.Mark($"scrolling: stitched {result.FrameCount} frames -> {result.Image.Width}x{result.OutputHeight}");
        return result.Image;
    }

    public void Cancel()
    {
        Session? session;
        lock (_gate)
        {
            session = _current;
            _current = null;
        }

        if (session is null)
        {
            return;
        }

        session.Cancel();
        CaptureFlowTrace.Mark("scrolling: cancelled");
    }

    public void Dispose() => Cancel();

    private void RaiseProgress(Session session, int count)
    {
        if (IsCurrent(session))
        {
            Progress?.Invoke(count);
        }
    }

    private void RaiseLimit(Session session, PanoramaCaptureLimitReason reason)
    {
        if (IsCurrent(session))
        {
            LimitReached?.Invoke(reason);
        }
    }

    private void RaiseFailed(Session session, Exception error)
    {
        if (IsCurrent(session))
        {
            Failed?.Invoke(error);
        }
    }

    private bool IsCurrent(Session session)
    {
        lock (_gate)
        {
            return ReferenceEquals(_current, session);
        }
    }

    /// <summary>One scrolling capture: WGC session, frame channel, consumer task and accumulator.</summary>
    private sealed class Session
    {
        private readonly ScrollingCaptureService _owner;
        private readonly ContinuousCaptureSession _capture;
        private readonly PanoramaAccumulator _accumulator;
        private readonly Channel<CapturedFrame> _channel;
        private readonly CancellationTokenSource _cancellation = new();
        private Task _consumer = Task.CompletedTask;
        private long _lastEnqueuedTimestamp;
        private int _stopped;
        private bool _reportedLimit;
        private bool _reportedFailure;

        public Session(ScrollingCaptureService owner, CaptureTarget target, PixelRect? region, PanoramaCaptureLimits limits)
        {
            _owner = owner;
            _accumulator = new PanoramaAccumulator(limits);
            _channel = Channel.CreateBounded<CapturedFrame>(new BoundedChannelOptions(4)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
            _capture = new ContinuousCaptureSession(target, region, targetFps: 12, includeCursor: false);
        }

        public void Start()
        {
            _capture.FrameArrived += OnFrameArrived;
            try
            {
                _capture.Start();
            }
            catch
            {
                _capture.FrameArrived -= OnFrameArrived;
                _capture.Dispose();
                _cancellation.Dispose();
                throw;
            }

            _consumer = Task.Run(ConsumeAsync, CancellationToken.None);
        }

        /// <summary>Stops WGC, drains every queued frame into the accumulator, and materializes the panorama.</summary>
        public async Task<PanoramaResult> StopAndStitchAsync()
        {
            StopCapture();
            await _consumer.ConfigureAwait(false);
            try
            {
                return _accumulator.Finish();
            }
            finally
            {
                _cancellation.Dispose();
            }
        }

        /// <summary>Stops WGC and abandons queued frames. The consumer exits on its own; no events are raised afterwards.</summary>
        public void Cancel()
        {
            _cancellation.Cancel();
            StopCapture();
            _consumer.ContinueWith(_ => _cancellation.Dispose(), TaskScheduler.Default);
        }

        private void StopCapture()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }

            _capture.FrameArrived -= OnFrameArrived;
            try
            {
                _capture.Stop();
                _capture.Dispose();
            }
            catch
            {
                // Best-effort teardown.
            }

            _channel.Writer.TryComplete();
        }

        private void OnFrameArrived(CapturedFrame frame)
        {
            if (Volatile.Read(ref _stopped) != 0)
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

            _channel.Writer.TryWrite(frame);
        }

        private async Task ConsumeAsync()
        {
            var token = _cancellation.Token;
            await foreach (var captured in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (token.IsCancellationRequested || _accumulator.ReachedLimit)
                {
                    continue;
                }

                try
                {
                    Process(captured);
                }
                catch (Exception ex)
                {
                    ReportFailure(ex);
                }
            }
        }

        private void Process(CapturedFrame captured)
        {
            var frame = new PanoramaFrame(captured);
            if (_accumulator.PreviousFrame is { } previous && !PanoramaAccumulator.AreMeaningfullyDifferent(previous, frame))
            {
                // The region repainted (caret blink, spinner) without scrolling. Never give up
                // here: the user may simply be pausing, and Done/Cancel remain available.
                return;
            }

            var outcome = _accumulator.Append(frame);
            switch (outcome.Status)
            {
                case PanoramaAppendStatus.Accepted:
                    _owner.RaiseProgress(this, _accumulator.AcceptedFrameCount);
                    break;

                case PanoramaAppendStatus.LimitReached:
                    _owner.RaiseProgress(this, _accumulator.AcceptedFrameCount);
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
            _owner.RaiseFailed(this, error);
        }

        private void ReportLimit(PanoramaCaptureLimitReason reason)
        {
            if (_reportedLimit)
            {
                return;
            }

            _reportedLimit = true;
            _owner.RaiseLimit(this, reason);
        }
    }
}
