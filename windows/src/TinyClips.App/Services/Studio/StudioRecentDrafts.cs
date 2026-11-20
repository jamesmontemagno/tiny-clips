using System.Diagnostics;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;

namespace TinyClips.App.Services.Studio;

/// <summary>
/// The Studio projects the tray's recent captures list beside the saved captures, read ahead
/// of time: listing the projects sizes every project folder, which the tray popup cannot wait
/// for. Read again when an editor opens or closes and by the tray's bounded external-state
/// refresh. Used on the UI thread; the reading is done off it.
/// </summary>
public sealed class StudioRecentDrafts
{
    private readonly IStudioProjectStore _store;
    private Task? _reading;
    private bool _readAgain;

    public StudioRecentDrafts(IStudioProjectStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Raised when a read has found other projects than the last one, on the thread that asked
    /// for the read.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Every project of the store, as it was read last. Empty until the first read.
    /// </summary>
    public IReadOnlyList<StudioProjectSummary> Projects { get; private set; } = [];

    /// <summary>
    /// The lines of the tray's recent captures: the saved captures and the projects that hold
    /// the only copy of a recording, mixed by date within
    /// one limit (<see cref="RecentMenuEntry.ForMenu"/>). Asked on the UI thread; reads nothing.
    /// </summary>
    public IReadOnlyList<RecentMenuEntry> EntriesFor(IEnumerable<RecentCapture> captures, int limit) =>
        RecentMenuEntry.ForMenu(captures, Projects, limit);

    /// <summary>
    /// Reads the projects again, off the calling thread, and finishes when
    /// <see cref="Projects"/> is up to date. Asked for while a read is under way, it reads
    /// once more when that one is over, so that the last request is never lost. Never fails.
    /// </summary>
    /// <param name="invalidate">False for a periodic probe that can reuse an in-flight read.</param>
    public Task ReloadAsync(bool invalidate = true)
    {
        if (_reading is { IsCompleted: false } reading)
        {
            _readAgain |= invalidate;
            return reading;
        }

        return _reading = ReadAsync();
    }

    private async Task ReadAsync()
    {
        do
        {
            _readAgain = false;
            IReadOnlyList<StudioProjectSummary> projects;
            try
            {
                projects = await Task.Run(_store.ListSummaries);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"The Studio projects could not be listed for the recent captures: {ex.Message}");
                continue;
            }

            // A newer editor notification arrived during the read; the pending pass will
            // read fresh state.
            if (_readAgain)
            {
                continue;
            }

            var isChanged = !projects.SequenceEqual(Projects);
            Projects = projects;
            if (isChanged)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        while (_readAgain);
    }
}