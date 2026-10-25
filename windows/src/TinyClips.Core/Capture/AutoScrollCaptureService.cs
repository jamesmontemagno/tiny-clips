namespace TinyClips.Core.Capture;

/// <summary>
/// <see cref="IAutoScrollCaptureService"/> backed by <see cref="AutoScrollEngine"/> and a
/// <see cref="WindowAutoScrollSurface"/>. Each capture runs on its own MTA thread because UI
/// Automation must not be called from the UI thread and its objects stay on the thread that
/// created them.
/// </summary>
public sealed class AutoScrollCaptureService : IAutoScrollCaptureService
{
    private int _active;

    public bool IsActive => Volatile.Read(ref _active) != 0;

    public nint FindTargetWindow(int screenX, int screenY)
    {
        var hit = AutoScrollNative.WindowFromPoint(new AutoScrollNative.POINT(screenX, screenY));
        var root = hit == 0 ? 0 : AutoScrollNative.GetAncestor(hit, AutoScrollNative.GaRoot);
        if (root == 0
            || AutoScrollNative.IsWindowVisible(root) == 0
            || AutoScrollNative.IsIconic(root) != 0)
        {
            return 0;
        }

        AutoScrollNative.GetWindowThreadProcessId(root, out var processId);
        return processId == (uint)Environment.ProcessId ? 0 : root;
    }

    public Task<AutoScrollResult> CaptureAsync(
        AutoScrollRequest request,
        PanoramaCaptureLimits limits,
        Action<int>? progress,
        CancellationToken stop)
    {
        if (Interlocked.Exchange(ref _active, 1) != 0)
        {
            throw new InvalidOperationException("An auto-scroll capture is already active.");
        }

        var completion = new TaskCompletionSource<AutoScrollResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            AutoScrollResult? result = null;
            Exception? error = null;
            var comInitialized = AutoScrollNative.CoInitializeEx(0, AutoScrollNative.CoInitMultithreaded) >= 0;
            try
            {
                using var surface = new WindowAutoScrollSurface(request);
                CaptureFlowTrace.Mark($"autoscroll: started (scroll position {(surface.HasScrollPosition ? "known" : "unknown")})");
                result = AutoScrollEngine.Run(surface, limits, request.FromTop, progress, stop);
                CaptureFlowTrace.Mark($"autoscroll: {result.Panorama.FrameCount} frames -> {result.Panorama.Image.Width}x{result.Panorama.OutputHeight}");
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                if (comInitialized)
                {
                    AutoScrollNative.CoUninitialize();
                }

                // Released before completing so the caller can start another capture right away.
                Volatile.Write(ref _active, 0);
            }

            if (error is not null)
            {
                completion.TrySetException(error);
            }
            else
            {
                completion.TrySetResult(result!);
            }
        })
        {
            IsBackground = true,
            Name = "TinyClips auto-scroll capture",
        };
        thread.SetApartmentState(ApartmentState.MTA);

        try
        {
            thread.Start();
        }
        catch
        {
            Volatile.Write(ref _active, 0);
            throw;
        }

        return completion.Task;
    }
}
