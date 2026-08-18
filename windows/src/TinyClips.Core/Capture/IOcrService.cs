namespace TinyClips.Core.Capture;

public interface IOcrService
{
    Task<string> RecognizeAsync(
        CaptureTarget target,
        PixelRect? region = null,
        CancellationToken cancellationToken = default);
}
