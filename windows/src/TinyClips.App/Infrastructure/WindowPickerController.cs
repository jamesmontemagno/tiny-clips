using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using TinyClips.Core.Capture;

namespace TinyClips.App;

/// <summary>
/// Runs the hover-to-highlight window picker: one frozen-backdrop overlay per monitor, sharing a
/// single front-to-back window list so the highlight follows the window that is actually on top
/// of the cursor, even across monitors.
/// </summary>
internal sealed class WindowPickerController
{
    private readonly IReadOnlyList<WindowPickerCandidate> _candidates;
    private readonly WindowPickerTarget[] _targets;
    private readonly PixelRect[] _monitorBounds;
    private readonly List<WindowPickerWindow> _windows = [];
    private readonly TaskCompletionSource<nint?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _hovered = -1;
    private int _completed;

    private WindowPickerController(IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<WindowPickerCandidate> candidates)
    {
        _candidates = candidates;
        _targets = candidates.Select(c => c.Target).ToArray();
        _monitorBounds = monitors.Select(m => new PixelRect(m.X, m.Y, m.Width, m.Height)).ToArray();
    }

    public bool HasSelectableWindows => _targets.Any(t => t.IsSelectable);

    /// <summary>
    /// Shows the picker on every monitor and resolves with the chosen window handle, or null on
    /// cancel. When <paramref name="backdrops"/> matches <paramref name="monitors"/> it is reused;
    /// otherwise fresh monitor frames are captured. Windows are enumerated before any overlay
    /// appears so the picker never lists itself.
    /// </summary>
    public static async Task<nint?> RunAsync(
        IReadOnlyList<MonitorInfo> monitors,
        IReadOnlyList<Task<CapturedFrame?>>? backdrops,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (monitors.Count == 0)
        {
            return null;
        }

        var candidates = WindowEnumerator.GetWindows();
        CaptureFlowTrace.Mark($"window: {candidates.Count(c => c.IsSelectable)} selectable windows");

        if (backdrops is null || backdrops.Count != monitors.Count)
        {
            backdrops = RegionSelectController.CaptureBackdropsAsync(monitors);
        }

        var controller = new WindowPickerController(monitors, candidates);
        try
        {
            for (var i = 0; i < monitors.Count; i++)
            {
                controller._windows.Add(new WindowPickerWindow(monitors[i], backdrops[i], controller));
            }

            // Activate the overlay under the cursor last so it keeps keyboard focus.
            var cursorMonitor = GetCursorPos(out var cursor)
                ? Array.FindIndex(controller._monitorBounds, b => WindowPickerGeometry.Contains(b, cursor.X, cursor.Y))
                : -1;
            for (var i = 0; i < controller._windows.Count; i++)
            {
                if (i != cursorMonitor)
                {
                    controller._windows[i].Activate();
                }
            }

            if (cursorMonitor >= 0)
            {
                controller._windows[cursorMonitor].Activate();
                controller.HoverAt(cursor.X, cursor.Y);
            }
        }
        catch
        {
            controller.CloseAll();
            throw;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            if (dispatcher.HasThreadAccess)
            {
                controller.Complete(null);
            }
            else if (!dispatcher.TryEnqueue(() => controller.Complete(null)))
            {
                controller._completion.TrySetCanceled(cancellationToken);
            }
        });

        CaptureFlowTrace.Mark("window: overlay windows activated");
        return await controller._completion.Task;
    }

    /// <summary>Highlights the window under a virtual-desktop physical-pixel point.</summary>
    public void HoverAt(int x, int y) => SetHovered(WindowPickerGeometry.HitTest(_targets, x, y), announce: false);

    /// <summary>Picks the window under a virtual-desktop physical-pixel point; empty space is ignored.</summary>
    public void SelectAt(int x, int y)
    {
        var index = WindowPickerGeometry.HitTest(_targets, x, y);
        if (index >= 0)
        {
            Complete(_candidates[index].Hwnd);
        }
    }

    public void Cycle(bool forward) => SetHovered(WindowPickerGeometry.Cycle(_targets, _hovered, forward), announce: true);

    public void SelectHovered()
    {
        if (_hovered >= 0)
        {
            Complete(_candidates[_hovered].Hwnd);
        }
    }

    public void Cancel() => Complete(null);

    /// <summary>Returns a screen-reader description of a window, e.g. "Inbox – Outlook, 1280 × 720".</summary>
    public static string Describe(WindowPickerCandidate candidate)
    {
        var app = string.IsNullOrEmpty(candidate.AppName) ? string.Empty : $" – {candidate.AppName}";
        return $"{candidate.Title}{app}, {WindowPickerGeometry.FormatSize(candidate.Bounds)}";
    }

    internal void OnWindowClosed() => Complete(null);

    /// <summary>
    /// A monitor's frozen snapshot could not be captured or painted. Revealing that overlay would
    /// cover the monitor with an opaque, image-less dim, so the whole picker is cancelled instead.
    /// </summary>
    internal void OnBackdropFailed(string monitorName)
    {
        if (Volatile.Read(ref _completed) != 0)
        {
            return;
        }

        CaptureFlowTrace.Mark($"window: backdrop failed for {monitorName}");
        Complete(null);
        App.ShowMessageNotification("Tiny Clips couldn't capture the screen to show the window picker. Please try again.");
    }

    private void SetHovered(int index, bool announce)
    {
        if (Volatile.Read(ref _completed) != 0)
        {
            return;
        }

        // With a single selectable window Tab lands on the same index again; still announce it
        // so keyboard and screen-reader users know what Enter will capture.
        if (index == _hovered)
        {
            if (announce && index >= 0)
            {
                AnnounceCandidate(_candidates[index]);
            }

            return;
        }

        _hovered = index;
        var candidate = index >= 0 ? _candidates[index] : null;
        var labelMonitor = candidate is null
            ? -1
            : WindowPickerGeometry.LabelMonitorIndex(candidate.Bounds, _monitorBounds);
        for (var i = 0; i < _windows.Count; i++)
        {
            _windows[i].ShowHighlight(candidate, showLabel: i == labelMonitor);
        }

        if (announce && candidate is not null)
        {
            AnnounceCandidate(candidate);
        }
    }

    private void AnnounceCandidate(WindowPickerCandidate candidate)
    {
        if (_windows.Count == 0)
        {
            return;
        }

        var labelMonitor = WindowPickerGeometry.LabelMonitorIndex(candidate.Bounds, _monitorBounds);
        var target = labelMonitor >= 0 ? _windows[labelMonitor] : _windows[0];
        target.Announce(Describe(candidate));
    }

    private void Complete(nint? hwnd)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        _completion.TrySetResult(hwnd);
        CloseAll();
    }

    private void CloseAll()
    {
        foreach (var window in _windows)
        {
            window.CloseFromController();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);
}
