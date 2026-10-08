namespace TinyClips.Core.Studio.Editing;

// The project saved as a folder (section 14 of the project format): the recordings and a
// .tinyclips file that opens them in Studio again. The store saves what is on disk, so the
// edits are written first. The copy takes as long as the recordings are large and runs off
// the session's thread; the editor takes no edits meanwhile, as while it exports, and the
// save can be stopped, which leaves nothing of it.
public sealed partial class StudioEditorSession
{
    private CancellationTokenSource? _projectFolderCancellation;

    // The save that was started last. It finishes once that save has ended, and never fails.
    private Task _projectFolderSave = Task.CompletedTask;

    /// <summary>
    /// True while the project is being copied into a folder. The session is not editable then
    /// (<see cref="IsEditable"/>), and another save does not start.
    /// </summary>
    public bool IsSavingProjectFolder { get; private set; }

    /// <summary>How much of the recordings the running save has copied, from 0 to 1.</summary>
    public double ProjectFolderProgress { get; private set; }

    /// <summary>
    /// Saves a copy of the project as a folder: the recordings, and a <c>.tinyclips</c> file of
    /// the folder's name. Does nothing unless <see cref="IsEditable"/>. The edits are written
    /// into the project first, because the store saves the project as it is on disk; when that
    /// fails, the error is reported and nothing is copied. The project in the store stays as
    /// it is and goes on being the one that is edited.
    /// </summary>
    /// <param name="folder">The folder to make. Its parent has to be there.</param>
    /// <param name="replaceSavedProject">
    /// Whether a project that was saved there before may be replaced. The store replaces only
    /// a folder that holds nothing a save does not write.
    /// </param>
    /// <returns>
    /// How the save ended. The task itself never fails: what the store or the system refused
    /// with is in the result, for whoever asked to put into words.
    /// </returns>
    public Task<StudioProjectFolderSaveResult> SaveProjectFolderAsync(string folder, bool replaceSavedProject = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!IsEditable || Model is null)
        {
            return Task.FromResult(new StudioProjectFolderSaveResult(StudioProjectFolderSaveOutcome.NotStarted));
        }

        Pause();
        if (!SaveNow())
        {
            return Task.FromResult(new StudioProjectFolderSaveResult(StudioProjectFolderSaveOutcome.NotStarted));
        }

        // What a close waits for. It is there before anything of the save is done.
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _projectFolderSave = finished.Task;
        return SaveProjectFolderCoreAsync(folder, replaceSavedProject, finished);
    }

    /// <summary>Stops the save that is under way. Nothing is left of it, and nothing is reported.</summary>
    public void CancelProjectFolderSave() => _projectFolderCancellation?.Cancel();

    private async Task<StudioProjectFolderSaveResult> SaveProjectFolderCoreAsync(
        string folder,
        bool replaceSavedProject,
        TaskCompletionSource finished)
    {
        try
        {
            var cancellation = new CancellationTokenSource();
            _projectFolderCancellation = cancellation;
            IsSavingProjectFolder = true;
            ProjectFolderProgress = 0;
            RaiseChanged(StudioEditorChanges.All);

            Exception? failure = null;
            var isCancelled = false;
            try
            {
                var progress = new ProjectFolderProgressRelay(this, cancellation);
                var token = cancellation.Token;
                await Task.Run(
                    () => _store.SaveProjectFolder(ProjectId, folder, replaceSavedProject, progress, token),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                isCancelled = true;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            var result = new StudioProjectFolderSaveResult(StudioProjectFolderSaveOutcome.Cancelled);
            await PostAsync(() => result = FinishProjectFolderSave(cancellation, folder, isCancelled, failure)).ConfigureAwait(false);
            return result;
        }
        finally
        {
            finished.SetResult();
        }
    }

    private StudioProjectFolderSaveResult FinishProjectFolderSave(
        CancellationTokenSource cancellation,
        string folder,
        bool isCancelled,
        Exception? failure)
    {
        if (ReferenceEquals(_projectFolderCancellation, cancellation))
        {
            _projectFolderCancellation = null;
        }

        cancellation.Dispose();
        IsSavingProjectFolder = false;
        ProjectFolderProgress = failure is null && !isCancelled ? 1 : 0;
        if (!_isClosed)
        {
            // Once the session has closed, no window is left to show any of this.
            RaiseChanged(StudioEditorChanges.All);
        }

        if (isCancelled)
        {
            return new StudioProjectFolderSaveResult(StudioProjectFolderSaveOutcome.Cancelled);
        }

        if (failure is not null)
        {
            return new StudioProjectFolderSaveResult(StudioProjectFolderSaveOutcome.Failed, Failure: failure);
        }

        // The store names the file after the folder.
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var projectFile = Path.Combine(target, Path.GetFileName(target) + StudioProjectFolder.ProjectFileExtension);
        return new StudioProjectFolderSaveResult(StudioProjectFolderSaveOutcome.Saved, projectFile);
    }

    private void ApplyProjectFolderProgress(CancellationTokenSource cancellation, double value)
    {
        if (!IsSavingProjectFolder || !ReferenceEquals(_projectFolderCancellation, cancellation) || !double.IsFinite(value))
        {
            return;
        }

        ProjectFolderProgress = Math.Min(Math.Max(value, 0), 1);
        RaiseChanged(StudioEditorChanges.ProjectFolder);
    }

    /// <summary>Brings the progress of the copy, reported on the thread that copies, to the session's thread.</summary>
    private sealed class ProjectFolderProgressRelay(StudioEditorSession session, CancellationTokenSource cancellation) : IProgress<double>
    {
        private double _latest;
        private int _isPosted;

        public void Report(double value)
        {
            Volatile.Write(ref _latest, value);
            if (Interlocked.Exchange(ref _isPosted, 1) != 0)
            {
                return;
            }

            session._post(() =>
            {
                Volatile.Write(ref _isPosted, 0);
                session.ApplyProjectFolderProgress(cancellation, Volatile.Read(ref _latest));
            });
        }
    }
}
