using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace TinyClips.Core.Capture;

/// <summary>
/// A continuous Windows.Graphics.Capture session that pumps BGRA8 frames to a callback
/// at a steady target frame rate. WGC only raises FrameArrived when the screen content
/// changes, so a separate timer "pump" re-emits the most recently captured frame at the
/// target cadence — this keeps the encoded video at a true constant frame rate (no
/// stretched/squished playback on a static desktop) and lets a per-frame webcam overlay
/// stay smooth even when the screen itself is idle. Frames are delivered tightly packed
/// and cropped to the optional region; presentation timestamps are relative to the first
/// emitted frame. Capture starts on <see cref="Start"/>, but no frames are emitted until
/// <see cref="BeginEmitting"/> is called, so callers can warm up the encoder (and webcam)
/// first and keep capture/encoder warm-up out of the recorded timeline. Used by the video
/// and GIF recorders.
/// </summary>
internal sealed class ContinuousCaptureSession : IDisposable
{
    private readonly CaptureTarget _target;
    private readonly PixelRect? _region;
    private readonly bool _includeCursor;
    private readonly TimeSpan _frameInterval;
    private readonly object _sync = new();

    private ID3D11Device? _d3dDevice;
    private IDirect3DDevice? _device;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _stagingTexture;
    private ID3D11DeviceContext? _context;

    private RecordingTimeline? _timeline;
    private Timer? _pump;
    private byte[]? _latestPixels;
    private int _latestWidth;
    private int _latestHeight;
    private TimeSpan _lastEmittedPts = TimeSpan.MinValue;
    private bool _loggedFirstEmit;
    private int _fullWidth;
    private int _fullHeight;
    private volatile bool _running;
    private volatile bool _emittingPaused;

    /// <summary>Raised at the target frame rate: tightly-packed BGRA8 + relative PTS.</summary>
    public event Action<CapturedFrame, TimeSpan>? FrameReady;

    /// <summary>
    /// Raised whenever WGC delivers a new frame (i.e. only when the screen content changes),
    /// independent of <see cref="BeginEmitting"/>. Consumers that only care about change —
    /// such as the scrolling capture — subscribe here and never start the steady-rate pump.
    /// Raised on the WGC frame-pool thread; handlers must return quickly.
    /// </summary>
    public event Action<CapturedFrame>? FrameArrived;

    /// <summary>Output width in pixels (region width, or full monitor width), rounded down to even.</summary>
    public int OutputWidth { get; private set; }

    /// <summary>Output height in pixels (region height, or full monitor height), rounded down to even.</summary>
    public int OutputHeight { get; private set; }

    /// <summary>Presentation timestamp of the most recently emitted frame (MinValue if none).</summary>
    public TimeSpan LastEmittedPts => _lastEmittedPts;

    /// <summary>Frames handed to <see cref="FrameReady"/> since <see cref="BeginEmitting"/>.</summary>
    public long EmittedFrameCount => Interlocked.Read(ref _emittedFrameCount);

    private long _emittedFrameCount;

    public ContinuousCaptureSession(CaptureTarget target, PixelRect? region, int targetFps, bool includeCursor, RecordingPerformanceMonitor? perf = null)
    {
        _target = target;
        _region = region;
        _includeCursor = includeCursor;
        _perf = perf;
        var fps = Math.Clamp(targetFps, 1, 120);
        _frameInterval = TimeSpan.FromSeconds(1.0 / fps);
    }

    private readonly RecordingPerformanceMonitor? _perf;

    public void Start()
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new NotSupportedException("Windows.Graphics.Capture is not supported on this device.");
        }

        var (d3dDevice, device) = WgcInterop.GetSharedDevice();
        _d3dDevice = d3dDevice;
        _device = device;
        _context = _d3dDevice.ImmediateContext;

        var item = _target.CreateItem()
            ?? throw new InvalidOperationException("Failed to create a GraphicsCaptureItem for the target.");

        var size = item.Size;
        _fullWidth = size.Width;
        _fullHeight = size.Height;

        var outW = _region?.Width ?? size.Width;
        var outH = _region?.Height ?? size.Height;
        // H.264 requires even dimensions; GIF tolerates any but even keeps both happy.
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
    /// Begins emitting frames at the target cadence. The screen is captured (and the latest
    /// frame cached) from <see cref="Start"/>, but no frames are emitted — and the presentation
    /// clock does not start — until this is called. Callers invoke it only once the rest of the
    /// pipeline (encoder, webcam overlay) is ready to consume frames, so the recorded timeline
    /// starts cleanly at the real "recording started" moment instead of baking in capture /
    /// encoder / camera warm-up as dead pre-roll at the front of the clip.
    /// </summary>
    public void BeginEmitting(RecordingTimeline? timeline = null)
    {
        lock (_sync)
        {
            if (!_running || _pump is not null)
            {
                return;
            }

            _timeline = timeline ?? RecordingTimeline.StartNow();
            _loggedFirstEmit = false;
            _emittingPaused = false;
            _lastEmittedPts = TimeSpan.MinValue;
            Interlocked.Exchange(ref _emittedFrameCount, 0);

            // Steady-rate pump: re-emits the latest captured frame even when WGC is idle.
            _pump = new Timer(OnPump, null, TimeSpan.Zero, _frameInterval);
        }
    }

    /// <summary>
    /// Stops emitting frames. WGC keeps capturing into the cached latest frame so that, on
    /// resume, the pump can emit the current screen immediately instead of waiting for the next
    /// content change (which on a static desktop might never come).
    /// </summary>
    public void PauseEmitting()
    {
        _emittingPaused = true;
    }

    public void ResumeEmitting()
    {
        _emittingPaused = false;
    }

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

            CapturedFrame captured;
            lock (_sync)
            {
                if (!_running || _context is null || _d3dDevice is null)
                {
                    return;
                }

                using var frameTexture = WgcInterop.GetTextureFromFrame(frame);
                var desc = frameTexture.Description;

                if (_stagingTexture is null)
                {
                    _stagingTexture = _d3dDevice.CreateTexture2D(new Texture2DDescription
                    {
                        Width = desc.Width,
                        Height = desc.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = desc.Format,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        MiscFlags = ResourceOptionFlags.None,
                    });
                }

                _context.CopyResource(_stagingTexture, frameTexture);

                captured = ReadStaging((int)desc.Width, (int)desc.Height);
                _latestPixels = captured.BgraPixels;
                _latestWidth = captured.Width;
                _latestHeight = captured.Height;
            }

            // The pump clones _latestPixels before emitting, so handing the same buffer to
            // FrameArrived subscribers is safe as long as they treat it as read-only.
            FrameArrived?.Invoke(captured);
        }
        catch
        {
            // A single dropped/failed frame must not tear down the recording.
        }
        finally
        {
            _perf?.Record(RecordingStage.CaptureReadback, Stopwatch.GetTimestamp() - started);
        }
    }

    private void OnPump(object? state)
    {
        if (!_running || _emittingPaused)
        {
            return;
        }

        byte[] copy;
        int width;
        int height;
        TimeSpan pts;

        if (!Monitor.TryEnter(_sync))
        {
            return;
        }

        var produce = Stopwatch.GetTimestamp();
        try
        {
            if (!_running || _latestPixels is null)
            {
                // No screen frame captured yet; nothing to emit.
                return;
            }

            width = _latestWidth;
            height = _latestHeight;
            copy = (byte[])_latestPixels.Clone();

            // Screen frames use the same QPC origin as webcam and audio.
            pts = _timeline?.Elapsed ?? TimeSpan.Zero;
            if (pts < TimeSpan.Zero)
            {
                pts = TimeSpan.Zero;
            }
            if (_lastEmittedPts != TimeSpan.MinValue && pts <= _lastEmittedPts)
            {
                pts = _lastEmittedPts + TimeSpan.FromTicks(1);
            }

            _lastEmittedPts = pts;
        }
        finally
        {
            Monitor.Exit(_sync);
        }

        _perf?.Record(RecordingStage.FrameProduce, Stopwatch.GetTimestamp() - produce);

        if (!_loggedFirstEmit)
        {
            _loggedFirstEmit = true;
            WebcamDiagnostics.Log($"First screen frame emitted: ptsMs={pts.TotalMilliseconds:F1}.");
        }

        Interlocked.Increment(ref _emittedFrameCount);
        _perf?.FrameEmitted();

        // Raise outside the lock so heavy per-frame compositing doesn't stall WGC delivery.
        FrameReady?.Invoke(new CapturedFrame(copy, width, height), pts);
    }

    private unsafe CapturedFrame ReadStaging(int frameWidth, int frameHeight)
    {
        int x = 0, y = 0;
        int width = OutputWidth, height = OutputHeight;

        if (_region is { } r)
        {
            x = Math.Clamp(r.X, 0, frameWidth);
            y = Math.Clamp(r.Y, 0, frameHeight);
        }

        width = Math.Clamp(width, 1, frameWidth - x);
        height = Math.Clamp(height, 1, frameHeight - y);

        var mapped = _context!.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var pixels = new byte[width * height * 4];
            var src = (byte*)mapped.DataPointer;
            int srcPitch = (int)mapped.RowPitch;
            int rowBytes = width * 4;

            fixed (byte* dst = pixels)
            {
                if (x == 0 && srcPitch == rowBytes)
                {
                    Buffer.MemoryCopy(src + ((long)y * srcPitch), dst, pixels.Length, (long)height * rowBytes);
                }
                else
                {
                    for (int row = 0; row < height; row++)
                    {
                        Buffer.MemoryCopy(
                            src + ((long)(y + row) * srcPitch) + (x * 4),
                            dst + ((long)row * rowBytes),
                            rowBytes,
                            rowBytes);
                    }
                }
            }

            return new CapturedFrame(pixels, width, height);
        }
        finally
        {
            _context!.Unmap(_stagingTexture!, 0);
        }
    }

    public void Stop()
    {
        _running = false;
        _pump?.Dispose();
        _pump = null;
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
            _stagingTexture?.Dispose();
            _stagingTexture = null;
            // The device pair is process-shared (WgcInterop.GetSharedDevice); only drop our references.
            _device = null;
            _d3dDevice = null;
            _context = null;
            _latestPixels = null;
        }
    }
}
