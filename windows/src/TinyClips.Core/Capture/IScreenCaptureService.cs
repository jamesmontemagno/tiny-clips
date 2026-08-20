namespace TinyClips.Core.Capture;

/// <summary>A rectangle in physical pixels, relative to a monitor's top-left.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

public interface IScreenCaptureService
{
    /// <summary>
    /// Creates the shared Direct3D capture device ahead of time so the first capture of a
    /// session does not pay for device creation. Safe to call repeatedly; never throws.
    /// </summary>
    void WarmUp();

    /// <summary>
    /// Captures a single frame of the given monitor using Windows.Graphics.Capture.
    /// When <paramref name="region"/> is supplied the frame is cropped to that
    /// monitor-relative rectangle. Pixels are returned tightly packed as BGRA8.
    /// </summary>
    Task<CapturedFrame> CaptureMonitorAsync(
        nint hMonitor,
        PixelRect? region = null,
        bool includeCursor = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures a single frame of the given target (monitor or window) using
    /// Windows.Graphics.Capture. When <paramref name="region"/> is supplied (monitor
    /// targets only) the frame is cropped to that monitor-relative rectangle.
    /// Pixels are returned tightly packed as BGRA8.
    /// </summary>
    Task<CapturedFrame> CaptureAsync(
        CaptureTarget target,
        PixelRect? region = null,
        bool includeCursor = false,
        CancellationToken cancellationToken = default);
}
