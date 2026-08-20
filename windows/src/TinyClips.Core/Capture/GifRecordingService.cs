using System.Text;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TinyClips.Core.Capture;

/// <summary>
/// Records the primary monitor to an animated GIF. A continuous WGC capture session
/// accumulates throttled BGRA frames; on stop they are encoded with per-frame delays,
/// an infinite-loop application extension and optional max-width downscaling.
/// </summary>
public sealed class GifRecordingService : IGifRecordingService
{
    // Cap memory: ~30s at the default GIF frame rate.
    private const int MaxFrames = 900;

    private readonly IMonitorService _monitors;
    private readonly IClipStorageService _storage;
    private readonly ICaptureSettings _settings;
    private readonly IClipAnalyticsService _analytics;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _frameLock = new();

    private ContinuousCaptureSession? _capture;
    private List<CapturedFrame>? _frames;
    private double _fps;
    private int _stopping;
    private int _discardRequested;
    private CaptureTarget? _preparedTarget;
    private PixelRect? _preparedRegion;

    private MouseClickMonitor? _clickMonitor;
    private MouseClickOverlayStyle _clickStyle;
    private int _clickOriginX;
    private int _clickOriginY;
    private BrandingOverlayCompositor? _branding;

    public GifRecordingService(
        IMonitorService monitors,
        IClipStorageService storage,
        ICaptureSettings settings,
        IClipAnalyticsService analytics)
    {
        _monitors = monitors;
        _storage = storage;
        _settings = settings;
        _analytics = analytics;
    }

    public bool IsRecording { get; private set; }

    public bool IsPaused { get; private set; }

    public event EventHandler<string?>? RecordingCompleted;

    public async Task PrepareAsync(CaptureTarget? target = null, PixelRect? region = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRecording)
            {
                throw new InvalidOperationException("A GIF recording is already in progress.");
            }

            var captureTarget = ResolveTarget(target);
            if (_preparedTarget is { } existing && Matches(existing, _preparedRegion, captureTarget, region))
            {
                return;
            }

            DiscardPreparedCore();
            PrepareCore(captureTarget, region);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DiscardPreparedAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRecording)
            {
                DiscardPreparedCore();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartAsync(CaptureTarget? target = null, PixelRect? region = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRecording)
            {
                throw new InvalidOperationException("A GIF recording is already in progress.");
            }

            var captureTarget = ResolveTarget(target);
            if (_preparedTarget is null || !Matches(_preparedTarget, _preparedRegion, captureTarget, region))
            {
                DiscardPreparedCore();
                PrepareCore(captureTarget, region);
            }
            else
            {
                CaptureFlowTrace.Mark("gif: using pre-warmed capture session");
            }

            Interlocked.Exchange(ref _discardRequested, 0);
            _frames = new List<CapturedFrame>();
            _preparedTarget = null;
            _preparedRegion = null;

            StartMouseClickOverlay(captureTarget, region);
            _branding = _settings.ShowBrandingOverlay ? new BrandingOverlayCompositor() : null;

            _capture!.BeginEmitting();
            IsRecording = true;
            CaptureFlowTrace.Mark("gif: recording started (emitting)");
        }
        finally
        {
            _gate.Release();
        }
    }

    private CaptureTarget ResolveTarget(CaptureTarget? target) => target ?? CaptureTarget.Monitor(
        (_monitors.GetPrimaryMonitor()
            ?? throw new InvalidOperationException("No monitor was found to record.")).HMonitor);

    private static bool Matches(CaptureTarget a, PixelRect? aRegion, CaptureTarget b, PixelRect? bRegion) =>
        a.HMonitor == b.HMonitor && a.Hwnd == b.Hwnd && aRegion == bRegion;

    private void PrepareCore(CaptureTarget captureTarget, PixelRect? region)
    {
        _fps = Math.Clamp(_settings.GifFrameRate, 1, 50);
        _capture = new ContinuousCaptureSession(captureTarget, region, (int)Math.Round(_fps), includeCursor: true);
        _capture.FrameReady += OnFrameReady;
        try
        {
            _capture.Start();
        }
        catch
        {
            _capture.Dispose();
            _capture = null;
            throw;
        }

        _preparedTarget = captureTarget;
        _preparedRegion = region;
        CaptureFlowTrace.Mark("gif: capture session started");
    }

    private void DiscardPreparedCore()
    {
        _preparedTarget = null;
        _preparedRegion = null;
        if (!IsRecording && _capture is not null)
        {
            _capture.FrameReady -= OnFrameReady;
            _capture.Dispose();
            _capture = null;
        }
    }

    private void OnFrameReady(CapturedFrame frame, TimeSpan pts)
    {
        if (IsPaused)
        {
            return;
        }

        if (_clickMonitor is { } monitor)
        {
            MouseClickOverlayCompositor.Draw(
                frame.BgraPixels,
                frame.Width,
                frame.Height,
                pts.TotalSeconds,
                monitor.GetClicks(),
                _clickOriginX,
                _clickOriginY,
                _clickStyle);
        }

        _branding?.Draw(frame.BgraPixels, frame.Width, frame.Height);

        lock (_frameLock)
        {
            if (_frames is { Count: < MaxFrames })
            {
                _frames.Add(frame);
            }
        }
    }

    private void StartMouseClickOverlay(CaptureTarget target, PixelRect? region)
    {
        if (target.IsWindow || !_settings.ShouldShowMouseClickVisuals(CaptureType.Gif))
        {
            return;
        }

        var monitor = _monitors.GetMonitors().FirstOrDefault(m => m.HMonitor == target.HMonitor)
            ?? _monitors.GetPrimaryMonitor();
        if (monitor == null)
        {
            return;
        }

        _clickOriginX = monitor.X + (region?.X ?? 0);
        _clickOriginY = monitor.Y + (region?.Y ?? 0);
        _clickStyle = _settings.MouseClickOverlayStyleFor(CaptureType.Gif);
        _clickMonitor = new MouseClickMonitor();
        _clickMonitor.Start();
    }

    public Task<string?> StopAsync() => StopAsync(discard: false);

    public async Task PauseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRecording || IsPaused)
            {
                return;
            }

            _capture?.PauseEmitting();
            IsPaused = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResumeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRecording || !IsPaused)
            {
                return;
            }

            _capture?.ResumeEmitting();
            IsPaused = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelAsync()
    {
        await StopAsync(discard: true).ConfigureAwait(false);
    }

    private async Task<string?> StopAsync(bool discard)
    {
        if (discard)
        {
            Interlocked.Exchange(ref _discardRequested, 1);
        }

        if (Interlocked.Exchange(ref _stopping, 1) == 1)
        {
            return null;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRecording)
            {
                IsPaused = false;
                DiscardPreparedCore();
                if (ConsumeDiscardRequested(discard))
                {
                    lock (_frameLock)
                    {
                        _frames = null;
                    }
                }

                return null;
            }

            _capture?.Stop();
            _clickMonitor?.Dispose();
            _clickMonitor = null;
            _branding = null;
            IsPaused = false;

            List<CapturedFrame> frames;
            lock (_frameLock)
            {
                frames = _frames ?? new List<CapturedFrame>();
                _frames = null;
            }

            _capture?.Dispose();
            _capture = null;
            IsRecording = false;

            if (ConsumeDiscardRequested(discard))
            {
                return null;
            }

            if (frames.Count == 0)
            {
                return null;
            }

            var path = _storage.GenerateFilePath(CaptureType.Gif);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var bytes = await EncodeGifAsync(frames).ConfigureAwait(false);
            if (ConsumeDiscardRequested(discard))
            {
                return null;
            }

            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            if (ConsumeDiscardRequested(discard))
            {
                DeleteOutputFileIfPresent(path);
                return null;
            }

            _analytics.RecordCapture(CaptureType.Gif);
            RecordingCompleted?.Invoke(this, path);
            return path;
        }
        finally
        {
            Interlocked.Exchange(ref _stopping, 0);
            _gate.Release();
        }
    }

    private bool ConsumeDiscardRequested(bool discard)
    {
        var latched = Interlocked.Exchange(ref _discardRequested, 0) == 1;
        return discard || latched;
    }

    private static void DeleteOutputFileIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of discarded output.
        }
    }

    private async Task<byte[]> EncodeGifAsync(List<CapturedFrame> frames)
    {
        var first = frames[0];
        var maxWidth = Math.Max(16, _settings.GifMaxWidth);

        uint scaledWidth = (uint)first.Width;
        uint scaledHeight = (uint)first.Height;
        if (first.Width > maxWidth)
        {
            var scale = (double)maxWidth / first.Width;
            scaledWidth = (uint)maxWidth;
            scaledHeight = (uint)Math.Max(1, Math.Round(first.Height * scale));
        }

        var delayHundredths = (ushort)Math.Clamp(Math.Round(100.0 / _fps), 2, 65535);

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId, stream);

        // Infinite-loop application extension (NETSCAPE2.0).
        try
        {
            var loopProps = new BitmapPropertySet
            {
                { "/appext/application", new BitmapTypedValue(Encoding.ASCII.GetBytes("NETSCAPE2.0"), PropertyType.UInt8Array) },
                { "/appext/data", new BitmapTypedValue(new byte[] { 3, 1, 0, 0, 0 }, PropertyType.UInt8Array) },
            };
            await encoder.BitmapProperties.SetPropertiesAsync(loopProps);
        }
        catch
        {
            // Loop metadata is best-effort; a non-looping GIF is still valid.
        }

        for (int i = 0; i < frames.Count; i++)
        {
            if (i > 0)
            {
                await encoder.GoToNextFrameAsync();
            }

            var frame = frames[i];
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                (uint)frame.Width,
                (uint)frame.Height,
                96.0,
                96.0,
                frame.BgraPixels);

            if (scaledWidth != (uint)frame.Width || scaledHeight != (uint)frame.Height)
            {
                encoder.BitmapTransform.ScaledWidth = scaledWidth;
                encoder.BitmapTransform.ScaledHeight = scaledHeight;
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            }

            var delayProps = new BitmapPropertySet
            {
                { "/grctlext/Delay", new BitmapTypedValue(delayHundredths, PropertyType.UInt16) },
            };
            await encoder.BitmapProperties.SetPropertiesAsync(delayProps);
        }

        await encoder.FlushAsync();

        stream.Seek(0);
        var size = checked((int)stream.Size);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)size);
        var result = new byte[size];
        reader.ReadBytes(result);
        return result;
    }
}
