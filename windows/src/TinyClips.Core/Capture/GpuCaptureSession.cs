using System.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace TinyClips.Core.Capture;

/// <summary>
/// Source rectangle (in capture-surface pixels) the pump wants rendered into a fixed-size encoder
/// frame when the two differ in size — i.e. after the captured window was resized.
/// </summary>
internal readonly record struct GpuBlitRequest(ID3D11Texture2D Source, int X, int Y, int Width, int Height, GpuFrame Target);

/// <summary>
/// GPU-resident counterpart of <see cref="ContinuousCaptureSession"/>. WGC frames are copied
/// GPU→GPU into a "latest frame" texture; the steady-rate pump then copies the (optionally
/// cropped) region into an encoder frame obtained from the attached <see cref="IGpuFrameAllocator"/>,
/// lets the owner composite overlays onto it with Direct2D, and emits it. No frame pixels ever
/// cross to system memory, eliminating the staging map, the per-tick byte[] clone, CPU alpha
/// blending and the bottom-up flip of the CPU path.
///
/// The encoder frame size is fixed for the recording (encoders cannot change size mid-stream).
/// When the capture item's content size changes — a recorded window being resized — the WGC frame
/// pool is recreated at the new size and the pump asks <see cref="ScaledBlit"/> to letterbox the
/// new content into the fixed frame instead of cropping it.
/// </summary>
internal sealed class GpuCaptureSession : IDisposable
{
    private readonly CaptureTarget _target;
    private readonly PixelRect? _region;
    private readonly bool _includeCursor;
    private readonly TimeSpan _frameInterval;
    private readonly RecordingPerformanceMonitor? _perf;
    private readonly object _sync = new();

    private ID3D11Device? _d3dDevice;
    private IDirect3DDevice? _device;
    private ID3D11DeviceContext? _context;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _latest;
    private IGpuFrameAllocator? _allocator;

    private RecordingTimeline? _timeline;
    private FramePacer? _pump;
    private bool _hasLatest;
    private SizeInt32 _poolSize;
    private int _contentWidth;
    private int _contentHeight;
    private TimeSpan _lastEmittedPts = TimeSpan.MinValue;
    private long _emittedFrameCount;
    private long _poolExhaustedDrops;
    private long _contentResizes;
    private long _pumpOverrunsAtStop;
    private volatile bool _running;
    private volatile bool _emittingPaused;

    /// <summary>
    /// Raised on the pump thread with an acquired, cropped, overlay-composited frame. The handler
    /// owns the frame and must eventually call <see cref="GpuFrame.Release"/>.
    /// </summary>
    public event Action<GpuFrame>? FrameReady;

    /// <summary>
    /// Raised on the pump thread after the screen copy and before <see cref="FrameReady"/>, while
    /// the texture is exclusively ours. Draw overlays here.
    /// </summary>
    public event Action<GpuFrame>? Compose;

    /// <summary>
    /// Scales/letterboxes a source rectangle into the target frame when the captured content no
    /// longer matches the encoder size. Returns false to fall back to a top-left crop.
    /// </summary>
    public Func<GpuBlitRequest, bool>? ScaledBlit { get; set; }

    public GpuCaptureSession(
        CaptureTarget target,
        PixelRect? region,
        int targetFps,
        bool includeCursor,
        RecordingPerformanceMonitor? perf = null)
    {
        _target = target;
        _region = region;
        _includeCursor = includeCursor;
        var fps = Math.Clamp(targetFps, 1, 120);
        _frameInterval = TimeSpan.FromSeconds(1.0 / fps);
        _perf = perf;
    }

    public int OutputWidth { get; private set; }

    public int OutputHeight { get; private set; }

    public TimeSpan LastEmittedPts => _lastEmittedPts;

    public long EmittedFrameCount => Interlocked.Read(ref _emittedFrameCount);

    /// <summary>Frames skipped because every encoder frame was still held by the encoder.</summary>
    public long PoolExhaustedDrops => Interlocked.Read(ref _poolExhaustedDrops);

    /// <summary>Times the captured content changed size mid-recording (window resized).</summary>
    public long ContentResizes => Interlocked.Read(ref _contentResizes);

    /// <summary>Frames allocated so far — the peak number of frames the encoder held concurrently.</summary>
    public int PoolHighWaterMark => _allocator?.Allocated ?? 0;

    public int PoolMaxCapacity => _allocator?.MaxCapacity ?? 0;

    /// <summary>Pacer grid slots skipped because a pump tick overran its frame interval.</summary>
    public long PumpOverruns => _pump?.SkippedTicks ?? _pumpOverrunsAtStop;

    public ID3D11Device D3DDevice => _d3dDevice ?? throw new InvalidOperationException("Session not started.");

    public void Start()
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new NotSupportedException("Windows.Graphics.Capture is not supported on this device.");
        }

        var (d3dDevice, device) = WgcInterop.GetSharedDevice();
        _d3dDevice = d3dDevice;
        _device = device;
        _context = d3dDevice.ImmediateContext;

        var item = _target.CreateItem()
            ?? throw new InvalidOperationException("Failed to create a GraphicsCaptureItem for the target.");

        var size = item.Size;
        _poolSize = size;
        _contentWidth = size.Width;
        _contentHeight = size.Height;

        var outW = _region?.Width ?? size.Width;
        var outH = _region?.Height ?? size.Height;
        OutputWidth = Math.Max(2, outW - (outW % 2));
        OutputHeight = Math.Max(2, outH - (outH % 2));

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            size);

        _session = _framePool.CreateCaptureSession(item);
        WgcInterop.TryConfigureSession(_session, _includeCursor);

        _running = true;
        _framePool.FrameArrived += OnFrameArrived;
        _session.StartCapture();
    }

    /// <summary>
    /// Supplies the encoder-frame allocator. Must be called before <see cref="BeginEmitting"/>;
    /// the session disposes it. Separate from <see cref="Start"/> because the allocator depends
    /// on the encoder backend, which in turn needs <see cref="OutputWidth"/>/<see cref="OutputHeight"/>.
    /// </summary>
    public void AttachAllocator(IGpuFrameAllocator allocator)
    {
        lock (_sync)
        {
            _allocator?.Dispose();
            _allocator = allocator;
        }
    }

    public void BeginEmitting(RecordingTimeline? timeline = null)
    {
        lock (_sync)
        {
            if (!_running || _pump is not null)
            {
                return;
            }

            if (_allocator is null)
            {
                throw new InvalidOperationException("AttachAllocator must be called before BeginEmitting.");
            }

            _timeline = timeline ?? RecordingTimeline.StartNow();
            _emittingPaused = false;
            _lastEmittedPts = TimeSpan.MinValue;
            Interlocked.Exchange(ref _emittedFrameCount, 0);
            _pump = new FramePacer(_frameInterval, OnPump, "TinyClips.GpuCapturePump");
            _pump.Start();
        }
    }

    public void PauseEmitting() => _emittingPaused = true;

    public void ResumeEmitting() => _emittingPaused = false;

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object? args)
    {
        if (!_running)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            using var frame = pool.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            var contentSize = frame.ContentSize;
            var needsRecreate = false;
            lock (_sync)
            {
                if (!_running || _context is null || _d3dDevice is null)
                {
                    return;
                }

                using var frameTexture = WgcInterop.GetTextureFromFrame(frame);
                var desc = frameTexture.Description;

                if (_latest is null || _latest.Description.Width != desc.Width || _latest.Description.Height != desc.Height)
                {
                    _latest?.Dispose();
                    _latest = _d3dDevice.CreateTexture2D(new Texture2DDescription
                    {
                        Width = desc.Width,
                        Height = desc.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = desc.Format,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource,
                        CPUAccessFlags = CpuAccessFlags.None,
                        MiscFlags = ResourceOptionFlags.None,
                    });
                }

                // GPU→GPU. Flush so the copy is submitted before the WGC frame is disposed: the
                // frame pool (2 buffers) recycles this surface for the capture service to overwrite,
                // and an un-submitted copy sitting in the command buffer until the next pump tick
                // would read a torn mix of two frames. (The CPU path never hit this because Map()
                // blocked until its copy completed.)
                _context.CopyResource(_latest, frameTexture);
                _context.Flush();
                _hasLatest = true;

                // The surface is pool-sized; real content occupies the top-left ContentSize. A
                // resized window first shows up as a ContentSize change on an old-size surface.
                _contentWidth = Math.Clamp(contentSize.Width, 1, (int)desc.Width);
                _contentHeight = Math.Clamp(contentSize.Height, 1, (int)desc.Height);

                if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
                {
                    needsRecreate = contentSize.Width > 0 && contentSize.Height > 0;
                }
            }

            if (needsRecreate)
            {
                // Recreate at the new content size so subsequent frames arrive full-resolution
                // instead of being clipped to the old pool size. Allowed from inside FrameArrived.
                _poolSize = contentSize;
                Interlocked.Increment(ref _contentResizes);
                pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, contentSize);
            }
        }
        catch
        {
            // A single failed frame must not tear down the recording.
        }
        finally
        {
            _perf?.Record(RecordingStage.CaptureReadback, Stopwatch.GetTimestamp() - started);
        }
    }

    private void OnPump()
    {
        if (!_running || _emittingPaused)
        {
            return;
        }

        // Single pacer thread, so blocking here (rather than TryEnter-and-skip) only ever waits
        // for the WGC thread's ~1 ms CopyResource — skipping cost the Timer-based pump ~15% of
        // its ticks at 30 fps.
        GpuFrame frame;
        lock (_sync)
        {
            if (!_running || !_hasLatest || _latest is null || _context is null || _allocator is null)
            {
                return;
            }

            var produce = Stopwatch.GetTimestamp();
            if (!_allocator.TryAcquire(out frame))
            {
                Interlocked.Increment(ref _poolExhaustedDrops);
                _perf?.FrameDropped();
                return;
            }

            // From here on the frame is ours until FrameReady hands it over; any failure must
            // return it, or the pool/allocator leaks one slot per failed tick.
            try
            {
                ProduceFrame(frame, produce);
            }
            catch
            {
                frame.Release();
                _perf?.FrameDropped();
                return;
            }
        }

        Interlocked.Increment(ref _emittedFrameCount);
        _perf?.FrameEmitted();
        FrameReady?.Invoke(frame);
    }

    private void ProduceFrame(GpuFrame frame, long produceTimestamp)
    {
        {
            // Source rectangle in capture-surface pixels: the region (clamped to current content)
            // or the whole content area.
            int x = 0, y = 0, width = _contentWidth, height = _contentHeight;
            if (_region is { } r)
            {
                x = Math.Clamp(r.X, 0, Math.Max(0, _contentWidth - 1));
                y = Math.Clamp(r.Y, 0, Math.Max(0, _contentHeight - 1));
                width = Math.Clamp(OutputWidth, 1, _contentWidth - x);
                height = Math.Clamp(OutputHeight, 1, _contentHeight - y);
            }

            var scaled = false;
            var mismatch = width != OutputWidth || height != OutputHeight;

            // OutputWidth/Height are rounded down to even for the encoder, so an odd-sized window is
            // legitimately 1px larger than the frame: that is a crop, not a resize. Only scale when
            // the content is actually smaller than the frame (or more than a pixel larger).
            var needsScale = mismatch &&
                (width < OutputWidth || height < OutputHeight || width > OutputWidth + 1 || height > OutputHeight + 1);
            if (needsScale)
            {
                // Content no longer matches the encoder frame (window resized): letterbox it.
                scaled = ScaledBlit?.Invoke(new GpuBlitRequest(_latest!, x, y, width, height, frame)) == true;
            }

            if (!scaled)
            {
                var copyW = Math.Min(width, OutputWidth);
                var copyH = Math.Min(height, OutputHeight);
                _context!.CopySubresourceRegion(
                    frame.Texture,
                    0,
                    0,
                    0,
                    0,
                    _latest,
                    0,
                    new Box(x, y, 0, x + copyW, y + copyH, 1));
            }

            _perf?.Record(RecordingStage.FrameProduce, Stopwatch.GetTimestamp() - produceTimestamp);

            var pts = _timeline?.Elapsed ?? TimeSpan.Zero;
            if (pts < TimeSpan.Zero)
            {
                pts = TimeSpan.Zero;
            }

            if (_lastEmittedPts != TimeSpan.MinValue && pts <= _lastEmittedPts)
            {
                pts = _lastEmittedPts + TimeSpan.FromTicks(1);
            }

            _lastEmittedPts = pts;
            frame.Pts = pts;

            var compose = Stopwatch.GetTimestamp();
            try
            {
                Compose?.Invoke(frame);
            }
            catch
            {
                // Overlay failures degrade to a plain screen frame rather than a lost frame.
            }

            // Submit the copy + overlay work before the encoder (possibly on its own context) reads it.
            _context!.Flush();
            _perf?.Record(RecordingStage.Composite, Stopwatch.GetTimestamp() - compose);
        }
    }

    public void Stop()
    {
        _running = false;
        var pump = _pump;
        _pump = null;
        if (pump is not null)
        {
            // Snapshot before disposing: the end-of-recording report reads this after Stop().
            _pumpOverrunsAtStop = pump.SkippedTicks;
            pump.Dispose();
        }

        _timeline = null;
        lock (_sync)
        {
            if (_framePool is not null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
            }

            _session?.Dispose();
            _session = null;
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_sync)
        {
            _framePool?.Dispose();
            _framePool = null;
            _allocator?.Dispose();
            _allocator = null;
            _latest?.Dispose();
            _latest = null;
            _hasLatest = false;
            _device = null;
            _d3dDevice = null;
            _context = null;
        }
    }
}
