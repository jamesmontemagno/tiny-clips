namespace TinyClips.Core.Studio.Editing;

// The way out for a project that cannot be shown: its screen recording, saved as an ordinary
// video (StudioScreenRecording). The window may close while the recording is being copied. The
// copy goes on then, and what came of it is still told: a video to whoever listens for
// ScreenRecordingSaved, and a failure as an error, because the line under the button that would
// have said it went with the window.
public sealed partial class StudioEditorSession
{
    // The save of the screen recording that was started last. It finishes once that save has
    // said what came of it, and never fails.
    private Task _screenRecordingSave = Task.CompletedTask;

    /// <summary>
    /// Raised once the screen recording of a project that cannot be shown has been saved as a
    /// video of its own, with the path of that video. Also by a session that has closed in the
    /// meantime: the video is there all the same.
    /// </summary>
    public event EventHandler<StudioExportedEventArgs>? ScreenRecordingSaved;

    /// <summary>True while the screen recording is being copied. Another save does not start then.</summary>
    public bool IsSavingScreenRecording { get; private set; }

    /// <summary>
    /// What came of saving the screen recording: that it is being saved, the name it got, or why
    /// it was not saved. Empty until a save has been started.
    /// </summary>
    public string ScreenRecordingStatus { get; private set; } = string.Empty;

    /// <summary>
    /// Saves the screen recording of a project that cannot be shown as an ordinary video
    /// (<see cref="StudioScreenRecording"/>), and says in <see cref="ScreenRecordingStatus"/>
    /// what came of it. Does nothing unless <see cref="HasScreenRecordingToSave"/>, nothing
    /// while a save is under way, and nothing once the session has closed. The project is left
    /// as it is.
    /// </summary>
    /// <remarks>
    /// The window may close while the recording is being copied. The copy goes on, and
    /// <see cref="CloseAsync"/> does not finish before it has said what came of it. A video
    /// that is saved then is reported like any other (<see cref="ScreenRecordingSaved"/>). A
    /// copy that fails then is reported as an error (<see cref="ErrorReported"/>, of the kind
    /// <see cref="StudioEditorErrorKind.ScreenRecording"/>), because nobody is left to read
    /// the status.
    /// </remarks>
    /// <param name="createOutputPath">
    /// Returns the full path to write, in the folder and with the name the app gives a saved
    /// video. Called once before the copy, and again when a file has taken that name by the
    /// time the copy is finished: on the session's thread, where that thread has a
    /// synchronization context, as the UI thread has.
    /// </param>
    /// <returns>How the save ended. The task itself never fails.</returns>
    public Task<StudioScreenRecordingOutcome> SaveScreenRecordingAsync(Func<string> createOutputPath)
    {
        ArgumentNullException.ThrowIfNull(createOutputPath);
        if (_isClosed || !HasScreenRecordingToSave || IsSavingScreenRecording)
        {
            return Task.FromResult(StudioScreenRecordingOutcome.NotStarted);
        }

        // What a close waits for. It is there before anything of the save is done, so that a
        // close finds it at whatever moment of the save it comes.
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _screenRecordingSave = finished.Task;
        return SaveScreenRecordingCoreAsync(createOutputPath, finished);
    }

    private async Task<StudioScreenRecordingOutcome> SaveScreenRecordingCoreAsync(
        Func<string> createOutputPath,
        TaskCompletionSource finished)
    {
        try
        {
            IsSavingScreenRecording = true;
            ScreenRecordingStatus = StudioScreenRecording.SavingMessage;
            RaiseChanged(StudioEditorChanges.ScreenRecording);

            string? savedPath = null;
            string status;
            try
            {
                // Not stopped by a close: the copy goes on when the window has gone.
                savedPath = await StudioScreenRecording.SaveAsync(_store, ProjectId, createOutputPath).ConfigureAwait(false);
                status = StudioScreenRecording.GetSavedMessage(savedPath);
            }
            catch (Exception ex)
            {
                status = StudioScreenRecording.GetNotSavedMessage(ex.Message);
            }

            var outcome = StudioScreenRecordingOutcome.Failed;
            await PostAsync(() => outcome = FinishScreenRecordingSave(savedPath, status)).ConfigureAwait(false);
            return outcome;
        }
        finally
        {
            finished.SetResult();
        }
    }

    /// <param name="savedPath">The video, or null when the recording could not be saved.</param>
    /// <param name="status">What came of it, in words.</param>
    private StudioScreenRecordingOutcome FinishScreenRecordingSave(string? savedPath, string status)
    {
        IsSavingScreenRecording = false;
        ScreenRecordingStatus = status;
        if (!_isClosed)
        {
            // Once the session has closed, no window is left to show any of this.
            RaiseChanged(StudioEditorChanges.ScreenRecording);
        }

        if (savedPath is not null)
        {
            // Also once the session has closed: the video is there, and it is a saved video
            // like any other, window or no window.
            ScreenRecordingSaved?.Invoke(this, new StudioExportedEventArgs(ProjectId, savedPath));
            return StudioScreenRecordingOutcome.Saved;
        }

        if (_isClosed)
        {
            // The window closed while the recording was being copied, and the line under its
            // button that would have said this went with it.
            ReportError(StudioEditorErrorKind.ScreenRecording, status);
        }

        return StudioScreenRecordingOutcome.Failed;
    }
}
