namespace TinyClips.Core.Studio.Preview;

/// <summary>
/// The live preview of one Studio project: it decodes the recording, draws the scene for the
/// current project, and shows it. All times are source time in seconds.
/// </summary>
/// <remarks>
/// <para>
/// The preview always plays the whole recording and ignores <see cref="StudioProject.Edits"/>.
/// The caller applies the trim: it decides where playback starts and pauses it at the trim end.
/// That way moving a trim handle never rebuilds anything here.
/// </para>
/// <para>
/// Every member may be called from any thread. Events are raised on a worker thread, never on the
/// caller's thread, so a UI caller has to switch threads before touching its controls.
/// </para>
/// </remarks>
public interface IStudioPreview : IAsyncDisposable
{
    /// <summary>The start time of the frame being shown.</summary>
    double Position { get; }

    bool IsPlaying { get; }

    /// <summary>
    /// Raised when <see cref="Position"/> changes: for every new frame while playing, and once a
    /// seek has landed.
    /// </summary>
    event EventHandler? PositionChanged;

    /// <summary>
    /// Raised when <see cref="IsPlaying"/> changes, including when playback stops by itself at the
    /// end of the recording.
    /// </summary>
    event EventHandler? IsPlayingChanged;

    /// <summary>Raised once when the preview cannot go on, for example because a file stopped decoding.</summary>
    event EventHandler<StudioPreviewFailedEventArgs>? Failed;

    /// <summary>
    /// Replaces the project being drawn and redraws the current frame. Only the editable parts of
    /// the project may differ from the one the preview was opened with (see
    /// <see cref="StudioEditableState"/>). <see cref="StudioAudio.Muted"/> silences the sound.
    /// </summary>
    void UpdateProject(StudioProject project);

    /// <summary>Starts playing from the most recently requested position.</summary>
    void Play();

    void Pause();

    /// <summary>
    /// Shows the frame that contains <paramref name="sourceTime"/>, clamped to the recording. It
    /// does not change whether the preview is playing. Requests that arrive faster than frames can
    /// be shown are coalesced, and the newest one wins.
    /// </summary>
    void Seek(double sourceTime);
}

public sealed class StudioPreviewFailedEventArgs(string message, Exception? exception = null) : EventArgs
{
    /// <summary>A sentence that can be shown to the user.</summary>
    public string Message { get; } = message;

    public Exception? Exception { get; } = exception;
}

/// <summary>Opens previews. One preview serves one editor window.</summary>
public interface IStudioPreviewFactory
{
    /// <summary>
    /// Opens the project's recording and returns once the first frame can be shown, paused at the
    /// start of the recording. Throws when a source file is missing or cannot be decoded.
    /// </summary>
    Task<IStudioPreview> OpenAsync(
        StudioProject project,
        StudioEvents events,
        StudioProjectPaths paths,
        CancellationToken cancellationToken = default);
}
