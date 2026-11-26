using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TinyClips.Core.Capture;

/// <summary>
/// Coordinates a screenshot: capture the primary monitor, encode to the configured
/// image format (with optional scaling and JPEG/WebP quality) and write it to disk.
/// </summary>
public sealed class ScreenshotService : IScreenshotService
{
    private readonly IScreenCaptureService _capture;
    private readonly IMonitorService _monitors;
    private readonly IClipStorageService _storage;
    private readonly ICaptureSettings _settings;
    private readonly IClipAnalyticsService _analytics;

    public ScreenshotService(
        IScreenCaptureService capture,
        IMonitorService monitors,
        IClipStorageService storage,
        ICaptureSettings settings,
        IClipAnalyticsService analytics)
    {
        _capture = capture;
        _monitors = monitors;
        _storage = storage;
        _settings = settings;
        _analytics = analytics;
    }

    public async Task<string> CaptureFullScreenAsync(CancellationToken cancellationToken = default)
        => await CaptureAndSaveAsync(target: null, region: null, cancellationToken).ConfigureAwait(false);

    public async Task<string> CaptureRegionAsync(PixelRect region, CancellationToken cancellationToken = default)
        => await CaptureAndSaveAsync(target: null, region, cancellationToken).ConfigureAwait(false);

    public async Task<string> CaptureTargetAsync(CaptureTarget target, PixelRect? region = null, CancellationToken cancellationToken = default)
        => await CaptureAndSaveAsync(target, region, cancellationToken).ConfigureAwait(false);

    private async Task<string> CaptureAndSaveAsync(CaptureTarget? target, PixelRect? region, CancellationToken cancellationToken)
    {
        var captureTarget = target ?? CaptureTarget.Monitor(
            (_monitors.GetPrimaryMonitor()
                ?? throw new InvalidOperationException("No monitor was found to capture.")).HMonitor);

        var frame = await _capture
            .CaptureAsync(captureTarget, region, includeCursor: false, cancellationToken)
            .ConfigureAwait(false);
        CaptureFlowTrace.Mark("screenshot: frame captured");

        return await SaveFrameAsync(frame, applyBranding: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> SaveFrameAsync(CapturedFrame frame, bool applyBranding = true, CancellationToken cancellationToken = default)
    {
        var path = _storage.GenerateFilePath(CaptureType.Screenshot);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await WriteFrameAsync(frame, path, applyBranding, cancellationToken).ConfigureAwait(false);
        _analytics.RecordCapture(CaptureType.Screenshot);
        return path;
    }

    public async Task WriteFrameAsync(
        CapturedFrame frame, string path, bool applyBranding = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var encoded = await EncodeFrameAsync(frame, _settings.ImageFormat, applyBranding, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await File.WriteAllBytesAsync(path, encoded, cancellationToken).ConfigureAwait(false);
        CaptureFlowTrace.Mark("screenshot: written to disk");
    }

    public async Task WriteFrameAsync(
        CapturedFrame frame, Stream destination, bool applyBranding = true, CancellationToken cancellationToken = default)
        => await WriteFrameAsync(frame, destination, _settings.ImageFormat, applyBranding, cancellationToken).ConfigureAwait(false);

    public async Task WriteFrameAsync(
        CapturedFrame frame, Stream destination, ImageFormat format,
        bool applyBranding = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The destination must be writable.", nameof(destination));
        var encoded = await EncodeFrameAsync(frame, format, applyBranding, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await destination.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
        CaptureFlowTrace.Mark("screenshot: written to disk");
    }

    private async Task<byte[]> EncodeFrameAsync(
        CapturedFrame frame, ImageFormat format, bool applyBranding, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scale = _settings.ScreenshotScale;
        var quality = _settings.JpegQuality;
        if (applyBranding && _settings.ShowBrandingOverlay)
            new BrandingOverlayCompositor().Draw(frame.BgraPixels, frame.Width, frame.Height);
        var encoded = await EncodeAsync(frame, format, scale, quality).ConfigureAwait(false);
        CaptureFlowTrace.Mark("screenshot: encoded");
        return encoded;
    }

    private static async Task<byte[]> EncodeAsync(CapturedFrame frame, ImageFormat format, int scale, double jpegQuality)
    {
        if (format == ImageFormat.Webp)
        {
            return await Task.Run(() => WebpImageEncoder.Encode(
                frame.BgraPixels, frame.Width, frame.Height, scale, jpegQuality))
                .ConfigureAwait(false);
        }

        var isPng = format == ImageFormat.Png;
        var encoderId = isPng ? BitmapEncoder.PngEncoderId : BitmapEncoder.JpegEncoderId;

        using var stream = new InMemoryRandomAccessStream();

        BitmapEncoder encoder;
        if (isPng)
        {
            encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        }
        else
        {
            var quality = (float)Math.Clamp(jpegQuality, 0.1, 1.0);
            var propertySet = new BitmapPropertySet
            {
                { "ImageQuality", new BitmapTypedValue(quality, Windows.Foundation.PropertyType.Single) },
            };
            encoder = await BitmapEncoder.CreateAsync(encoderId, stream, propertySet);
        }

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            isPng ? BitmapAlphaMode.Premultiplied : BitmapAlphaMode.Ignore,
            (uint)frame.Width,
            (uint)frame.Height,
            96.0,
            96.0,
            frame.BgraPixels);

        if (scale is > 0 and < 100)
        {
            encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, frame.Width * scale / 100);
            encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, frame.Height * scale / 100);
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        await encoder.FlushAsync();

        stream.Seek(0);
        var size = checked((int)stream.Size);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)size);
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
