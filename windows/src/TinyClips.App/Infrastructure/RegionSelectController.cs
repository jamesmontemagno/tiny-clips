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
    /// Failures resolve to null entries rather than faulting.
    /// </summary>
    public static Task<CapturedFrame?[]> CaptureBackdropsAsync(IReadOnlyList<MonitorInfo> monitors)
    {
        var capture = App.Services.GetRequiredService<IScreenCaptureService>();
        var tasks = monitors.Select(async monitor =>
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
        return Task.WhenAll(tasks);
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
        Task<CapturedFrame?[]>? backdrops)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        backdrops ??= CaptureBackdropsAsync(monitors);
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
            var index = i;
            var backdropTask = backdrops.ContinueWith(
                t => t.IsCompletedSuccessfully ? t.Result[index] : null,
                TaskScheduler.Default);
            windows.Add(new RegionSelectWindow(monitors[i], backdropTask, Complete));
        }

        foreach (var window in windows)
        {
            window.Activate();
        }

        CaptureFlowTrace.Mark("region: overlay windows activated");
        return await completion.Task;
    }
}
