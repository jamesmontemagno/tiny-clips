using System.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace TinyClips.Core.Capture;

/// <summary>
/// A frame that lives entirely on the GPU: a pooled BGRA render-target texture plus its WinRT
/// <see cref="IDirect3DSurface"/> wrapper, ready for <c>MediaStreamSample.CreateFromDirect3D11Surface</c>.
/// Call <see cref="Release"/> when the encoder reports the sample processed so the texture returns
/// to the pool.
/// </summary>
internal sealed class GpuFrame
{
    private readonly GpuFrameTexturePool _pool;
    private int _released;

    internal GpuFrame(GpuFrameTexturePool pool, ID3D11Texture2D texture, IDirect3DSurface surface, int width, int height)
    {
        _pool = pool;
        Texture = texture;
        Surface = surface;
        Width = width;
        Height = height;
    }

    public ID3D11Texture2D Texture { get; }

    public IDirect3DSurface Surface { get; }

    public int Width { get; }

    public int Height { get; }

    public TimeSpan Pts { get; internal set; }

    /// <summary>Stopwatch timestamp when the frame was handed to the encoder (for hold-time stats).</summary>
    public long HandedOffTimestamp { get; internal set; }

    internal void Rented()
    {
        Volatile.Write(ref _released, 0);
    }

    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _pool.Return(this);
        }
    }
}

/// <summary>
/// Pool of encoder-ready textures that grows on demand up to a hard cap. Bounding the pool (not
/// just the channel) bounds VRAM: at 4K each BGRA frame is ~33 MB. The encoder pipeline holds
/// input surfaces for its look-ahead / B-frame window (measured 50–200 ms on AMD VCN), so the pool
/// must cover that latency at the target frame rate or frames are dropped at the source.
/// </summary>
internal sealed class GpuFrameTexturePool : IDisposable
{
    private readonly Stack<GpuFrame> _free = new();
    private readonly List<GpuFrame> _all = new();
    private readonly object _gate = new();
    private readonly ID3D11Device _device;
    private readonly int _width;
    private readonly int _height;
    private bool _disposed;

    public GpuFrameTexturePool(ID3D11Device device, int width, int height, int initialCapacity, int maxCapacity)
    {
        _device = device;
        _width = width;
        _height = height;
        MaxCapacity = Math.Max(initialCapacity, maxCapacity);
        for (var i = 0; i < initialCapacity; i++)
        {
            _free.Push(CreateFrame());
        }
    }

    public int MaxCapacity { get; }

    /// <summary>Textures allocated so far (high-water mark of concurrent in-flight frames).</summary>
    public int Allocated
    {
        get
        {
            lock (_gate)
            {
                return _all.Count;
            }
        }
    }

    public int Available
    {
        get
        {
            lock (_gate)
            {
                return _free.Count;
            }
        }
    }

    public IReadOnlyList<GpuFrame> All => _all;

    public bool TryRent(out GpuFrame frame)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                frame = null!;
                return false;
            }

            if (_free.Count == 0)
            {
                if (_all.Count >= MaxCapacity)
                {
                    frame = null!;
                    return false;
                }

                _free.Push(CreateFrame());
            }

            frame = _free.Pop();
            frame.Rented();
            return true;
        }
    }

    private GpuFrame CreateFrame()
    {
        var texture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            // RenderTarget for Direct2D compositing; ShaderResource for the encoder's
            // colour-space converter. No CPU access so the driver keeps it in VRAM.
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });
        var surface = WgcInterop.CreateDirect3DSurface(texture);
        var frame = new GpuFrame(this, texture, surface, _width, _height);
        _all.Add(frame);
        return frame;
    }

    internal void Return(GpuFrame frame)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _free.Push(frame);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _free.Clear();
        }

        // Textures may still be referenced by in-flight MediaStreamSamples; releasing our COM
        // reference is safe because the sample holds its own on the IDirect3DSurface.
        List<GpuFrame> all;
        lock (_gate)
        {
            all = new List<GpuFrame>(_all);
            _all.Clear();
        }

        foreach (var frame in all)
        {
            frame.Surface.Dispose();
            frame.Texture.Dispose();
        }
    }
}

/// <summary>
/// GPU-resident counterpart of <see cref="ContinuousCaptureSession"/>. WGC frames are copied
/// GPU→GPU into a "latest frame" texture; the steady-rate pump then copies the (optionally
/// cropped) region into a pooled encoder texture, lets the owner composite overlays onto it with
/// Direct2D, and emits it. No frame pixels ever cross to system memory, eliminating the staging
/// map, the per-tick byte[] clone, CPU alpha blending and the bottom-up flip of the CPU path.
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
    private GpuFrameTexturePool? _pool;

    private RecordingTimeline? _timeline;
    private FramePacer? _pump;
    private bool _hasLatest;
    private int _fullWidth;
    private int _fullHeight;
    private TimeSpan _lastEmittedPts = TimeSpan.MinValue;
    private long _emittedFrameCount;
    private long _poolExhaustedDrops;
    private volatile bool _running;
    private volatile bool _emittingPaused;

    /// <summary>
    /// Raised on the pump thread with a rented, cropped, overlay-composited frame. The handler
    /// owns the frame and must eventually call <see cref="GpuFrame.Release"/>.
    /// </summary>
    public event Action<GpuFrame>? FrameReady;

    /// <summary>
    /// Raised on the pump thread after the screen copy and before <see cref="FrameReady"/>, while
    /// the texture is exclusively ours. Draw overlays here.
    /// </summary>
    public event Action<GpuFrame>? Compose;

    public GpuCaptureSession(
        CaptureTarget target,
        PixelRect? region,
        int targetFps,
        bool includeCursor,
        int initialPoolCapacity = 4,
        int maxPoolCapacity = 16,
        RecordingPerformanceMonitor? perf = null)
    {
        _target = target;
        _region = region;
        _includeCursor = includeCursor;
        var fps = Math.Clamp(targetFps, 1, 120);
        _frameInterval = TimeSpan.FromSeconds(1.0 / fps);
        _initialPoolCapacity = Math.Clamp(initialPoolCapacity, 2, 32);
        _maxPoolCapacity = Math.Clamp(maxPoolCapacity, _initialPoolCapacity, 64);
        _perf = perf;
    }

    private readonly int _initialPoolCapacity;
    private readonly int _maxPoolCapacity;

    public int OutputWidth { get; private set; }

    public int OutputHeight { get; private set; }

    public TimeSpan LastEmittedPts => _lastEmittedPts;

    public long EmittedFrameCount => Interlocked.Read(ref _emittedFrameCount);

    /// <summary>Frames skipped because every pooled texture was still held by the encoder.</summary>
    public long PoolExhaustedDrops => Interlocked.Read(ref _poolExhaustedDrops);

    /// <summary>Textures allocated so far — the peak number of frames the encoder held concurrently.</summary>
    public int PoolHighWaterMark => _pool?.Allocated ?? 0;

    public int PoolMaxCapacity => _maxPoolCapacity;

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
        _fullWidth = size.Width;
        _fullHeight = size.Height;

        var outW = _region?.Width ?? size.Width;
        var outH = _region?.Height ?? size.Height;
        OutputWidth = Math.Max(2, outW - (outW % 2));
        OutputHeight = Math.Max(2, outH - (outH % 2));

        _pool = new GpuFrameTexturePool(d3dDevice, OutputWidth, OutputHeight, _initialPoolCapacity, _maxPoolCapacity);

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

    public void BeginEmitting(RecordingTimeline? timeline = null)
    {
        lock (_sync)
        {
            if (!_running || _pump is not null)
            {
                return;
            }

            _timeline = timeline ?? RecordingTimeline.StartNow();
            _emittingPaused = false;
            _lastEmittedPts = TimeSpan.MinValue;
            Interlocked.Exchange(ref _emittedFrameCount, 0);
            _pump = new FramePacer(_frameInterval, OnPump, "TinyClips.GpuCapturePump");
            _pump.Start();
        }
    }

    /// <summary>Pacer grid slots skipped because a pump tick overran its frame interval.</summary>
    public long PumpOverruns => _pump?.SkippedTicks ?? _pumpOverrunsAtStop;

    private long _pumpOverrunsAtStop;

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
            if (!_running || !_hasLatest || _latest is null || _context is null || _pool is null)
            {
                return;
            }

            var produce = Stopwatch.GetTimestamp();
            if (!_pool.TryRent(out frame))
            {
                Interlocked.Increment(ref _poolExhaustedDrops);
                _perf?.FrameDropped();
                return;
            }

            var x = 0;
            var y = 0;
            if (_region is { } r)
            {
                x = Math.Clamp(r.X, 0, _fullWidth);
                y = Math.Clamp(r.Y, 0, _fullHeight);
            }

            var width = Math.Clamp(OutputWidth, 1, _fullWidth - x);
            var height = Math.Clamp(OutputHeight, 1, _fullHeight - y);
            _context.CopySubresourceRegion(
                frame.Texture,
                0,
                0,
                0,
                0,
                _latest,
                0,
                new Box(x, y, 0, x + width, y + height, 1));
            _perf?.Record(RecordingStage.FrameProduce, Stopwatch.GetTimestamp() - produce);

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
            _context.Flush();
            _perf?.Record(RecordingStage.Composite, Stopwatch.GetTimestamp() - compose);
        }

        Interlocked.Increment(ref _emittedFrameCount);
        _perf?.FrameEmitted();
        FrameReady?.Invoke(frame);
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
            _pool?.Dispose();
            _pool = null;
            _latest?.Dispose();
            _latest = null;
            _hasLatest = false;
            _device = null;
            _d3dDevice = null;
            _context = null;
        }
    }
}
