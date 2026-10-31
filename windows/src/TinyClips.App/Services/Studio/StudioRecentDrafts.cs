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
    private readonly ICaptureSettings _settings;
    private Task? _reading;
    private bool _readAgain;

    public StudioRecentDrafts(IStudioProjectStore store, ICaptureSettings settings)
    {
        _store = store;
        _settings = settings;
    }

    /// <summary>
    /// Raised when a read has found other projects than the last one, on the thread that asked
    /// for the read.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Every project of the store, as it was read last. Empty until the first read, and while
    /// Studio is switched off, when nothing of Studio is shown.
    /// </summary>
    public IReadOnlyList<StudioProjectSummary> Projects { get; private set; } = [];

    /// <summary>
    /// The lines of the tray's recent captures: the saved captures and, while Studio is
    /// switched on, the projects that hold the only copy of a recording, mixed by date within
    /// one limit (<see cref="RecentMenuEntry.ForMenu"/>). Asked on the UI thread; reads nothing.
    /// </summary>
    public IReadOnlyList<RecentMenuEntry> EntriesFor(IEnumerable<RecentCapture> captures, int limit) =>
        RecentMenuEntry.ForMenu(captures, Projects, _settings.StudioPreviewEnabled, limit);

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
            IReadOnlyList<StudioProjectSummary> projects = [];
            if (_settings.StudioPreviewEnabled)
            {
                try
                {
                    projects = await Task.Run(_store.ListSummaries);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"The Studio projects could not be listed for the recent captures: {ex.Message}");
                    continue;
                }
            }

            // A switch or a newer editor notification arrived during the read. Do not publish
            // its stale projects, even briefly; the pending pass will read fresh state.
            if (_readAgain)
            {
                continue;
            }

            if (!_settings.StudioPreviewEnabled)
            {
                projects = [];
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