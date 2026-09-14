namespace TinyClips.Core.Services;

/// <summary>
/// Tracks a set of open editor windows, preserving the most recently opened one while allowing
/// multiple windows to remain open at the same time. The picker is reopened only when the last
/// window closes and the caller requested that behavior.
/// </summary>
public sealed class EditorWindowTracker<TWindow> where TWindow : class
{
    private readonly HashSet<TWindow> _windows = new();

    public int Count => _windows.Count;

    public TWindow? ActiveWindow { get; private set; }

    public void Track(TWindow window)
    {
        _windows.Add(window);
        ActiveWindow = window;
    }

    public bool CloseOne(TWindow window, bool reopenPickerAfterClose)
    {
        if (!_windows.Remove(window))
        {
            return false;
        }

        ActiveWindow = null;
        foreach (var active in _windows)
        {
            ActiveWindow = active;
            break;
        }

        return _windows.Count == 0 && reopenPickerAfterClose;
    }

    public void CloseAll(Action<TWindow> closeWindow)
    {
        foreach (var window in _windows.ToList())
        {
            closeWindow(window);
        }

        _windows.Clear();
        ActiveWindow = null;
    }
}
