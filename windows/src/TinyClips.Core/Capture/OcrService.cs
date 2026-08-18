using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace TinyClips.Core.Capture;

public sealed class OcrService : IOcrService
{
    private readonly IScreenCaptureService _capture;

    public OcrService(IScreenCaptureService capture)
    {
        _capture = capture;
    }

    public async Task<string> RecognizeAsync(
        CaptureTarget target,
        PixelRect? region = null,
        CancellationToken cancellationToken = default)
    {
        var frame = await _capture
            .CaptureAsync(target, region, includeCursor: false, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("OCR is unavailable for the current Windows language settings.");
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            frame.BgraPixels.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            frame.Width,
            frame.Height,
            BitmapAlphaMode.Premultiplied);

        using var recognizable = await FitToMaxDimensionAsync(bitmap, OcrEngine.MaxImageDimension, cancellationToken)
            .ConfigureAwait(false);

        var result = await engine.RecognizeAsync(recognizable).AsTask(cancellationToken).ConfigureAwait(false);
        return string.Join(Environment.NewLine, result.Lines.Select(line => line.Text));
    }

    /// <summary>
    /// Downscales the bitmap (preserving aspect ratio) when either dimension exceeds
    /// <paramref name="maxDimension"/>, since <see cref="OcrEngine.RecognizeAsync(SoftwareBitmap)"/>
    /// rejects images larger than <see cref="OcrEngine.MaxImageDimension"/>.
    /// </summary>
    private static async Task<SoftwareBitmap> FitToMaxDimensionAsync(
        SoftwareBitmap bitmap,
        uint maxDimension,
        CancellationToken cancellationToken)
    {
        var width = (uint)bitmap.PixelWidth;
        var height = (uint)bitmap.PixelHeight;
        if (maxDimension == 0 || (width <= maxDimension && height <= maxDimension))
        {
            return SoftwareBitmap.Copy(bitmap);
        }

        var scale = Math.Min((double)maxDimension / width, (double)maxDimension / height);
        var scaledWidth = Math.Max(1u, (uint)Math.Floor(width * scale));
        var scaledHeight = Math.Max(1u, (uint)Math.Floor(height * scale));

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
        var transform = new BitmapTransform
        {
            ScaledWidth = scaledWidth,
            ScaledHeight = scaledHeight,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        return await decoder
            .GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
    }
}
