namespace TinyClips.App;

internal sealed class CaptureVisibilityPolicy(Action<nint, bool> setExcluded)
{
    private readonly Dictionary<nint, bool> _windows = [];

    // Last affinity successfully applied per window. Re-applying an unchanged affinity makes DWM
    // re-render the window; on a just-hidden backdrop window that leaves a stale white surface.
    private readonly Dictionary<nint, bool> _applied = [];

    public bool IncludeTinyClips { get; private set; }

    public void Register(nint hwnd, bool alwaysExcluded = false)
    {
        _windows.Add(hwnd, alwaysExcluded);
        // Private overlays apply exclusion themselves before showing, with fail-closed handling.
        if (!alwaysExcluded)
        {
            Apply(hwnd, !IncludeTinyClips);
        }
    }

    public void Unregister(nint hwnd)
    {
        _windows.Remove(hwnd);
        _applied.Remove(hwnd);
    }

    public bool CanSelectWindow(bool isOwnProcess, nint hwnd, nint pickerHwnd) =>
        hwnd != pickerHwnd
        && (!isOwnProcess || (IncludeTinyClips && !_windows.GetValueOrDefault(hwnd)));

    public void BeginCapture(bool includeTinyClips, bool recordingActive)
    {
        // Display affinity is process/window-wide, not scoped to a WGC session.
        if (recordingActive)
        {
            return;
        }

        IncludeTinyClips = includeTinyClips;
        foreach (var (hwnd, alwaysExcluded) in _windows)
        {
            Apply(hwnd, alwaysExcluded || !includeTinyClips);
        }
    }

    private void Apply(nint hwnd, bool excluded)
    {
        if (_applied.TryGetValue(hwnd, out var current) && current == excluded)
        {
            return;
        }

        // Forget the state first so a failed call is retried on the next capture.
        _applied.Remove(hwnd);
        setExcluded(hwnd, excluded);
        _applied[hwnd] = excluded;
    }
}
