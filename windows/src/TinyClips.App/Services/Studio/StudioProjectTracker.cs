namespace TinyClips.App.Services.Studio;

/// <summary>
/// Tracks the Studio projects that are in use, meaning open in an editor window, so cleanup never
/// deletes one of them. Nothing opens a project yet, so the set stays empty until the Studio
/// window calls <see cref="MarkOpened"/> and <see cref="MarkClosed"/>.
/// </summary>
public sealed class StudioProjectTracker
{
    private readonly object _gate = new();
    private readonly HashSet<string> _openProjectIds = new(StringComparer.Ordinal);

    /// <summary>A snapshot of the ids of the projects that are open right now. Safe to read from any thread.</summary>
    public IReadOnlyCollection<string> OpenProjectIds
    {
        get
        {
            lock (_gate)
            {
                return _openProjectIds.ToArray();
            }
        }
    }

    public void MarkOpened(string projectId)
    {
        lock (_gate)
        {
            _openProjectIds.Add(projectId);
        }
    }

    public void MarkClosed(string projectId)
    {
        lock (_gate)
        {
            _openProjectIds.Remove(projectId);
        }
    }
}
