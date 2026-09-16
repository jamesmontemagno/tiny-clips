namespace TinyClips.Core.Services;

/// <summary>
/// Tracks a set of open editor windows in opening order, preserving the most recently opened
/// remaining window while allowing multiple windows to stay open at the same time. A caller can
/// ask to reopen the picker when a window closes; that request is remembered until the set
/// becomes empty, at which point it is reported once (if any close requested it) and cleared.
/// </summary>
public sealed class EditorWindowTracker<TWindow> where TWindow : class
{
    // Ordered (not a HashSet) so ActiveWindow deterministically reflects the most recently
    // opened window that is still open, regardless of which window closes first.
    private readonly List<TWindow> _windows = new();
    private bool _reopenRequested;

    public int Count => _windows.Count;

    public TWindow? ActiveWindow => _windows.Count == 0 ? null : _windows[^1];

    public void Track(TWindow window)
    {
        _windows.Remove(window);
        _windows.Add(window);
    }

    /// <summary>
    /// Removes <paramref name="window"/> from the tracked set. Returns <see langword="true"/>
    /// only once the set has become empty and at least one closed window (this one or an
    /// earlier one) asked to reopen the picker; that accumulated request is then cleared.
    /// </summary>
    public bool CloseOne(TWindow window, bool reopenPickerAfterClose)
    {
        if (!_windows.Remove(window))
        {
            return false;
        }

        _reopenRequested |= reopenPickerAfterClose;

        if (_windows.Count > 0)
        {
            return false;
        }

        var shouldReopen = _reopenRequested;
        _reopenRequested = false;
        return shouldReopen;
    }

    public void CloseAll(Action<TWindow> closeWindow)
    {
        foreach (var window in _windows.ToList())
        {
            closeWindow(window);
        }

        _windows.Clear();
        _reopenRequested = false;
    }
}
