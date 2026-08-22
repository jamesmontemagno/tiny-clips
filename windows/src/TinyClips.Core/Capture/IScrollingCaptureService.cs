namespace TinyClips.Core.Capture;

/// <summary>
/// Scrolling (panorama) screenshot capture: streams a region while the user scrolls and
/// stitches the frames into one tall image. Mirrors the macOS <c>ScrollingPanoramaCapture</c>.
/// One session at a time.
/// </summary>
public interface IScrollingCaptureService
{
    /// <summary>True between a successful <see cref="StartAsync"/> and the matching stop/cancel.</summary>
    bool IsActive { get; }

    /// <summary>Raised (on a background thread) with the accepted frame count each time a frame is stitched.</summary>
    event Action<int>? Progress;

    /// <summary>
    /// Raised once (on a background thread) when a guardrail stops growth; the caller should
    /// stop and keep what exists.
    /// </summary>
    event Action<PanoramaCaptureLimitReason>? LimitReached;

    /// <summary>Raised once (on a background thread) when the capture cannot continue; the caller should cancel.</summary>
    event Action<Exception>? Failed;

    /// <summary>Starts streaming the monitor-relative <paramref name="region"/> of <paramref name="target"/>.</summary>
    Task StartAsync(CaptureTarget target, PixelRect? region, PanoramaCaptureLimits limits, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops capturing and returns the stitched image. Throws <see cref="PanoramaCaptureException"/>
    /// when nothing usable was captured.
    /// </summary>
    Task<CapturedFrame> StopAsync();

    /// <summary>Stops capturing and discards everything. Safe to call when not active.</summary>
    void Cancel();
}
