using System.Diagnostics;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;

namespace TinyClips.App.Services.Studio;

/// <summary>
/// The Studio projects the tray's recent captures list beside the saved captures, read ahead
/// of time: listing the projects sizes every project folder, which the tray popup cannot wait
/// for. Read again when an editor opens or closes and when the popup is about to show, as the
/// Mac's <c>StudioRecentDrafts</c> is. Used on the UI thread; the reading is done off it.
/// </summary>
public sealed class StudioRecentDrafts
{
    private readonly IStudioProjectStore _store;
    private readonly ICaptureSettings _settings;
    private IReadOnlyDictionary<string, string> _posters = new Dictionary<string, string>(StringComparer.Ordinal);
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

    /// <summary>The poster image of a project that was listed at the last read, or null when it has none.</summary>
    public string? PosterOf(string projectId) => _posters.GetValueOrDefault(projectId);

    /// <summary>
    /// Reads the projects again, off the calling thread, and finishes when
    /// <see cref="Projects"/> is up to date. Asked for while a read is under way, it reads
    /// once more when that one is over, so that the last request is never lost. Never fails.
    /// </summary>
    /// <param name="pictureLimit">How many of the newest drafts have their poster looked for.</param>
    public Task ReloadAsync(int pictureLimit)
    {
        if (_reading is { IsCompleted: false } reading)
        {
            _readAgain = true;
            return reading;
        }

        return _reading = ReadAsync(pictureLimit);
    }

    private async Task ReadAsync(int pictureLimit)
    {
        do
        {
            _readAgain = false;
            IReadOnlyList<StudioProjectSummary> projects = [];
            IReadOnlyDictionary<string, string> posters = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_settings.StudioPreviewEnabled)
            {
                try
                {
                    (projects, posters) = await Task.Run(() => Read(pictureLimit));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"The Studio projects could not be listed for the recent captures: {ex.Message}");
                    continue;
                }
            }

            var isChanged = !projects.SequenceEqual(Projects) || !Same(posters, _posters);
            Projects = projects;
            _posters = posters;
            if (isChanged)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        while (_readAgain);
    }

    private (IReadOnlyList<StudioProjectSummary> Projects, IReadOnlyDictionary<string, string> Posters) Read(int pictureLimit)
    {
        var projects = _store.ListSummaries();
        var posters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var draft in StudioProjectSummary.MenuDrafts(projects).OrderByDescending(static draft => draft.LastUsedAt).Take(Math.Max(0, pictureLimit)))
        {
            try
            {
                var poster = _store.GetPaths(draft.Id).PosterPath;
                if (File.Exists(poster))
                {
                    posters[draft.Id] = poster;
                }
            }
            catch (Exception ex)
            {
                // A project that went between the listing and this has no picture.
                Debug.WriteLine($"No poster for the Studio project {draft.Id}: {ex.Message}");
            }
        }

        return (projects, posters);
    }

    private static bool Same(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second) =>
        first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var path) && path == pair.Value);
}
