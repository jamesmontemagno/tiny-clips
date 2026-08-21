namespace TinyClips.Core.Capture;

/// <summary>
/// Records the primary monitor to an animated GIF. Frames are accumulated during
/// recording and encoded (with frame delays, infinite loop and max-width scaling)
/// when recording stops.
/// </summary>
public interface IGifRecordingService
{
    bool IsRecording { get; }

    bool IsPaused { get; }

    /// <summary>Raised when a recording finishes (manual stop or time-limit), with the saved file path.</summary>
    event EventHandler<string?>? RecordingCompleted;

    /// <summary>
    /// Begins recording. When <paramref name="target"/> is null the primary monitor is
    /// recorded; pass a monitor or window target (and optional monitor-relative region)
    /// to record a specific screen, window, or region. Throws if already recording.
    /// </summary>
    Task StartAsync(CaptureTarget? target = null, PixelRect? region = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pre-warms the capture session for the given target without starting the recording, so a
    /// following <see cref="StartAsync"/> with the same target/region begins immediately.
    /// </summary>
    Task PrepareAsync(CaptureTarget? target = null, PixelRect? region = null, CancellationToken cancellationToken = default);

    /// <summary>Tears down a pipeline prepared by <see cref="PrepareAsync"/> that will not be started.</summary>
    Task DiscardPreparedAsync();

    /// <summary>Stops recording, encodes the GIF and returns the saved path (or null if nothing recorded).</summary>
    Task<string?> StopAsync();

    Task PauseAsync();

    Task ResumeAsync();

    Task CancelAsync();
}
