namespace TinyClips.App.Services.Studio;

/// <summary>
/// Tracks the Studio projects that are in use, meaning open in an editor window, so cleanup never
/// deletes one of them. <see cref="StudioWindowService"/> keeps the set up to date.
/// </summary>
public sealed class StudioProjectTracker
{
    private readonly object _gate = new();
    private readonly HashSet<string> _openProjectIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Raised after a project was opened or closed, on the thread that did it (the UI thread).
    /// </summary>
    public event EventHandler? Changed;

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

    public bool IsOpen(string projectId)
    {
        lock (_gate)
        {
            return _openProjectIds.Contains(projectId);
        }
    }

    public void MarkOpened(string projectId)
    {
        bool isChanged;
        lock (_gate)
        {
            isChanged = _openProjectIds.Add(projectId);
        }

        if (isChanged)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void MarkClosed(string projectId)
    {
        bool isChanged;
        lock (_gate)
        {
            isChanged = _openProjectIds.Remove(projectId);
        }

        if (isChanged)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
