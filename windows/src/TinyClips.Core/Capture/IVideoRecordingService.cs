using TinyClips.Core.Models;

namespace TinyClips.Core.Capture;

/// <summary>
/// Records the primary monitor to an H.264 MP4 file using a continuous WGC capture
/// pipeline fed into a Media Foundation transcoder. Video-only for now (audio is a
/// later phase). Start/Stop are single-recording; <see cref="IsRecording"/> guards reentry.
/// </summary>
public interface IVideoRecordingService
{
    bool IsRecording { get; }

    bool IsPaused { get; }

    bool CanMuteSystemAudio { get; }

    bool CanMuteMicrophone { get; }

    bool IsSystemAudioMuted { get; }

    bool IsMicrophoneMuted { get; }

    /// <summary>Raised when a recording finishes (manual stop or time-limit), with the saved file path.</summary>
    event EventHandler<string?>? RecordingCompleted;

    /// <summary>
    /// Raised when the webcam overlay could not be started or was lost mid-recording, with a
    /// user-facing reason. Screen recording continues without the webcam; this lets the app
    /// surface why the webcam is missing instead of failing silently.
    /// </summary>
    event EventHandler<string>? WebcamCaptureFailed;

    /// <summary>
    /// Per-stage performance report of the most recently completed recording (null before the
    /// first recording finishes). Used by the benchmark harness and the diagnostics log.
    /// </summary>
    RecordingPerformanceReport? LastPerformanceReport { get; }

    /// <summary>
    /// Begins recording. When <paramref name="target"/> is null the primary monitor is
    /// recorded; pass a monitor or window target (and optional monitor-relative region)
    /// to record a specific screen, window, or region. Throws if already recording.
    /// </summary>
    Task StartAsync(CaptureTarget? target = null, PixelRect? region = null, double? timeLimitMinutesOverride = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pre-warms the recording pipeline (capture session, webcam, audio devices, encoder) for the
    /// given target without starting the recorded timeline. A following <see cref="StartAsync"/>
    /// with the same target/region begins emitting immediately instead of paying for setup. Call
    /// during the countdown or setup panel. Discard with <see cref="DiscardPreparedAsync"/> if
    /// the recording is cancelled before it starts.
    /// </summary>
    Task PrepareAsync(CaptureTarget? target = null, PixelRect? region = null, CancellationToken cancellationToken = default);

    /// <summary>Tears down a pipeline prepared by <see cref="PrepareAsync"/> that will not be started.</summary>
    Task DiscardPreparedAsync();

    /// <summary>Stops recording, finalizes the MP4 and returns the saved path (or null if nothing recorded).</summary>
    Task<string?> StopAsync();

    Task PauseAsync();

    Task ResumeAsync();

    /// <summary>Records a webcam corner change at the current pause-adjusted recording time.</summary>
    void SetWebcamCorner(WebcamCornerPosition corner);

    void SetSystemAudioMuted(bool muted);

    void SetMicrophoneMuted(bool muted);

    Task CancelAsync();
}
