namespace TinyClips.Core.Capture;

/// <summary>What to auto-scroll: a top-level window, the screen point that picks its pane, and an optional crop.</summary>
/// <param name="Window">Top-level window (HWND) that owns the pane.</param>
/// <param name="PointX">Screen x (physical pixels) inside the pane; wheel messages are aimed here.</param>
/// <param name="PointY">Screen y (physical pixels) inside the pane.</param>
/// <param name="ScreenRegion">Optional crop in virtual-desktop pixels; null captures the whole window.</param>
/// <param name="FromTop">Scroll to the top before capturing.</param>
public sealed record AutoScrollRequest(nint Window, int PointX, int PointY, PixelRect? ScreenRegion, bool FromTop = true);

/// <summary>
/// Auto-scroll capture: scrolls a window's pane by itself and stitches one tall image, in the
/// background, without moving the mouse or taking focus. Complements the manual
/// <see cref="IScrollingCaptureService"/>, which follows the user's own scrolling and works in
/// every app (including browsers, which throttle rendering when they are not visible).
/// </summary>
public interface IAutoScrollCaptureService
{
    bool IsActive { get; }

    /// <summary>
    /// Top-level window under a screen point that auto-scroll can drive, or 0 when there is none
    /// (no window, minimized, or one of this app's own windows).
    /// </summary>
    nint FindTargetWindow(int screenX, int screenY);

    /// <summary>
    /// Runs a capture on a background thread. Cancelling <paramref name="stop"/> ends it early and
    /// still returns what was captured. <paramref name="progress"/> receives the frame count on a
    /// background thread.
    /// </summary>
    Task<AutoScrollResult> CaptureAsync(
        AutoScrollRequest request,
        PanoramaCaptureLimits limits,
        Action<int>? progress,
        CancellationToken stop);
}
