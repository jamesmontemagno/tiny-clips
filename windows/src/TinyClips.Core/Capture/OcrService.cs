using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

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
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join(Environment.NewLine, result.Lines.Select(line => line.Text));
    }
}
