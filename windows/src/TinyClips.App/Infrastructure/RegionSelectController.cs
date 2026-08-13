using Microsoft.Extensions.DependencyInjection;
using TinyClips.Core.Capture;

namespace TinyClips.App;

public readonly record struct RegionSelectResult(nint HMonitor, PixelRect Region);

public static class RegionSelectController
{
    public static async Task<RegionSelectResult?> RunAsync(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        var capture = App.Services.GetRequiredService<IScreenCaptureService>();
        var backdropTasks = monitors.Select(async monitor =>
        {
            try
            {
                return await capture.CaptureMonitorAsync(monitor.HMonitor);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Region backdrop capture failed for {monitor.DeviceName}: {ex}");
                return null;
            }
        }).ToArray();

        var backdrops = await Task.WhenAll(backdropTasks);

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
            var window = new RegionSelectWindow(monitors[i], backdrops[i], Complete);
            windows.Add(window);
        }

        foreach (var window in windows)
        {
            window.Activate();
        }

        return await completion.Task;
    }
}
