using Microsoft.Extensions.DependencyInjection;
using TinyClips.Core.Capture;

namespace TinyClips.App;

/// <summary>
/// The user's region choice. <see cref="Backdrop"/> is the frozen monitor frame that was shown
/// behind the overlay (null when backdrop capture failed); <see cref="Region"/> is relative to it.
/// </summary>
public readonly record struct RegionSelectResult(nint HMonitor, PixelRect Region, CapturedFrame? Backdrop);

public static class RegionSelectController
{
    /// <summary>
    /// Starts capturing a frozen frame of each monitor. Kick this off as early as possible (e.g.
    /// while the capture picker is still showing) so the overlay can appear without waiting.
    /// One task per monitor is returned (never aggregated) so each overlay can reveal as soon as
    /// its own frame arrives rather than waiting for the slowest monitor. Failures resolve to
    /// null rather than faulting.
    /// </summary>
    public static IReadOnlyList<Task<CapturedFrame?>> CaptureBackdropsAsync(IReadOnlyList<MonitorInfo> monitors)
    {
        var capture = App.Services.GetRequiredService<IScreenCaptureService>();
        return monitors.Select(async monitor =>
        {
            try
            {
                return (CapturedFrame?)await capture.CaptureMonitorAsync(monitor.HMonitor).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Region backdrop capture failed for {monitor.DeviceName}: {ex}");
                return null;
            }
        }).ToArray();
    }

    public static Task<RegionSelectResult?> RunAsync(IReadOnlyList<MonitorInfo> monitors) =>
        RunAsync(monitors, backdrops: null);

    /// <summary>
    /// Shows the region overlay on every monitor. When <paramref name="backdrops"/> is supplied
    /// (from <see cref="CaptureBackdropsAsync"/> for the same monitor list) the overlay windows
    /// are created immediately and paint the backdrop when it arrives; otherwise the capture is
    /// started here.
    /// </summary>
    public static async Task<RegionSelectResult?> RunAsync(
        IReadOnlyList<MonitorInfo> monitors,
        IReadOnlyList<Task<CapturedFrame?>>? backdrops)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        if (backdrops is null || backdrops.Count != monitors.Count)
        {
            backdrops = CaptureBackdropsAsync(monitors);
        }

        CaptureFlowTrace.Mark("region: overlay windows creating");

        var completion = new TaskCompletionSource<RegionSelectResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var windows = new List<RegionSelectWindow>(monitors.Count);
        var completed = 0;

        void Complete(RegionSelectResult? result)
        {
            if (Interlocked.Exchange(ref completed, 1) != 0)
            {
                return;
            }

            completion.TrySetResult(result);

            foreach (var window in windows)
            {
                window.CloseFromController();
            }
        }

        for (var i = 0; i < monitors.Count; i++)
        {
            windows.Add(new RegionSelectWindow(monitors[i], backdrops[i], Complete));
        }

        foreach (var window in windows)
        {
            window.Activate();
        }

        CaptureFlowTrace.Mark("region: overlay windows activated");
        return await completion.Task;
    }
}
