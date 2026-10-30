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
    private sealed record Poster(string Path, long ModifiedTicks, long Length);
    private IReadOnlyDictionary<string, Poster> _posters = new Dictionary<string, Poster>(StringComparer.Ordinal);
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
    public string? PosterOf(string projectId) => _posters.GetValueOrDefault(projectId)?.Path;

    /// <summary>Changes when an editor replaces a poster at the same path.</summary>
    public string? PosterKeyOf(string projectId) => _posters.GetValueOrDefault(projectId) is { } poster
        ? $"{poster.Path}|{poster.ModifiedTicks}|{poster.Length}"
        : null;

    /// <summary>
    /// Reads the projects again, off the calling thread, and finishes when
    /// <see cref="Projects"/> is up to date. Asked for while a read is under way, it reads
    /// once more when that one is over, so that the last request is never lost. Never fails.
    /// </summary>
    /// <param name="pictureLimit">How many of the newest drafts have their poster looked for.</param>
    /// <param name="invalidate">False for a periodic probe that can reuse an in-flight read.</param>
    public Task ReloadAsync(int pictureLimit, bool invalidate = true)
    {
        if (_reading is { IsCompleted: false } reading)
        {
            _readAgain |= invalidate;
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
            IReadOnlyDictionary<string, Poster> posters = new Dictionary<string, Poster>(StringComparer.Ordinal);
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

            // A switch or a newer editor notification arrived during the read. Do not publish
            // its stale projects/posters, even briefly; the pending pass will read fresh state.
            if (_readAgain)
            {
                continue;
            }

            if (!_settings.StudioPreviewEnabled)
            {
                projects = [];
                posters = new Dictionary<string, Poster>(StringComparer.Ordinal);
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

    private (IReadOnlyList<StudioProjectSummary> Projects, IReadOnlyDictionary<string, Poster> Posters) Read(int pictureLimit)
    {
        var projects = _store.ListSummaries();
        var posters = new Dictionary<string, Poster>(StringComparer.Ordinal);
        foreach (var draft in StudioProjectSummary.MenuDrafts(projects).OrderByDescending(static draft => draft.LastUsedAt).Take(Math.Max(0, pictureLimit)))
        {
            try
            {
                var poster = _store.GetPaths(draft.Id).PosterPath;
                var file = new FileInfo(poster);
                if (file.Exists)
                {
                    posters[draft.Id] = new Poster(poster, file.LastWriteTimeUtc.Ticks, file.Length);
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

    private static bool Same(IReadOnlyDictionary<string, Poster> first, IReadOnlyDictionary<string, Poster> second) =>
        first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var path) && path == pair.Value);
}
