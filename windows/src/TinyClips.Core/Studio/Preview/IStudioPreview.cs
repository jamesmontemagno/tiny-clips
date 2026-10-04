namespace TinyClips.Core.Studio.Preview;

/// <summary>
/// The live preview of one Studio project: it decodes the recording, draws the scene for the
/// current project, and shows it. All times are source time in seconds.
/// </summary>
/// <remarks>
/// <para>
/// The preview always plays the whole recording and ignores <see cref="StudioProject.Edits"/>.
/// The caller applies the trim: it decides where playback starts and pauses it at the trim end.
/// That way moving a trim handle never rebuilds anything here. The caller applies cuts and speed
/// changes the same way, with <see cref="Seek"/> and <see cref="SetPlaybackRate"/>.
/// </para>
/// <para>
/// Every member may be called from any thread. Events are raised on a worker thread, never on the
/// caller's thread, so a UI caller has to switch threads before touching its controls.
/// </para>
/// </remarks>
public interface IStudioPreview : IAsyncDisposable
{
    /// <summary>
    /// The start time of the frame being shown. After <see cref="Seek"/> returns it is the start of
    /// the frame that was asked for until that frame is shown, and never again a frame from before
    /// the call. A caller that seeks and then plays therefore cannot read a stale position.
    /// </summary>
    double Position { get; }

    /// <summary>
    /// Whether playback was asked for. It changes inside <see cref="Play"/> and <see cref="Pause"/>,
    /// before they return, and becomes false when playback stops by itself. A caller can therefore
    /// tell a stop it did not ask for from a late notice about its own pause.
    /// </summary>
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

    /// <summary>
    /// Stops playing. When it returns the picture no longer advances, and <see cref="Position"/>
    /// is the frame it stays on.
    /// </summary>
    void Pause();

    /// <summary>
    /// Shows the frame that contains <paramref name="sourceTime"/>, clamped to the recording. It
    /// does not change whether the preview is playing. Requests that arrive faster than frames can
    /// be shown are coalesced, and the newest one wins.
    /// </summary>
    void Seek(double sourceTime);

    /// <summary>
    /// Sets how fast playback runs, in seconds of the recording per second: 2 plays twice as
    /// fast. A preview opens at 1. The rate stays until it is set again, through
    /// <see cref="Pause"/>, <see cref="Play"/> and <see cref="Seek"/>, and it can be set while
    /// playing. Rates from <see cref="StudioTimeMap.SlowestRate"/> to
    /// <see cref="StudioTimeMap.FastestRate"/> are asked for. The recording's sound is heard only
    /// while the rate is 1, as an export leaves a stretch at another speed silent.
    /// </summary>
    /// <remarks>
    /// The body is here until the engine has its own, so a preview without one plays everything
    /// at the recording's own speed.
    /// </remarks>
    void SetPlaybackRate(double rate)
    {
    }
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
