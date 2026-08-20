using TinyClips.Core.Models;

namespace TinyClips.Core.Capture;

public interface IScreenshotService
{
    /// <summary>
    /// Captures the full primary monitor, encodes it per the user's screenshot
    /// settings (PNG/JPEG, scale, quality) and saves it. Returns the saved file path.
    /// </summary>
    Task<string> CaptureFullScreenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures a monitor-relative region of the primary monitor (physical pixels),
    /// encodes it per the user's screenshot settings and saves it. Returns the saved path.
    /// </summary>
    Task<string> CaptureRegionAsync(PixelRect region, CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures the given target (a specific monitor or a window), with an optional
    /// monitor-relative region, encodes it per the user's screenshot settings and saves
    /// it. Returns the saved file path.
    /// </summary>
    Task<string> CaptureTargetAsync(CaptureTarget target, PixelRect? region = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Encodes an already-captured frame per the user's screenshot settings (branding overlay,
    /// PNG/JPEG, scale, quality) and saves it. Used when the region-select overlay's frozen
    /// backdrop is the screenshot source, so no second capture is needed. Returns the saved path.
    /// </summary>
    Task<string> SaveFrameAsync(CapturedFrame frame, bool applyBranding = true, CancellationToken cancellationToken = default);
}
