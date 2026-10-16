using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using StudioEngineSpike.Engine;
using StudioEngineSpike.Present;

namespace StudioEngineSpike.Modes;

/// <summary>
/// Question 2: presenting the composited frame in a WinUI 3 window, (a) through a Win2D
/// CanvasSwapChainPanel on the shared device and (b) through a plain SwapChainPanel with a DXGI
/// composition swap chain. Opens a window on the interactive desktop for about a minute.
/// </summary>
internal static class PresentMode
{
    public static int Run(SpikeOptions options)
    {
        if (!TestMedia.Exists(options.Root))
        {
            Console.Error.WriteLine("Test media is missing. Run `StudioEngineSpike media` first.");
            return 3;
        }

        // The XAML compiler's own Main is switched off (DISABLE_XAML_GENERATED_MAIN), so this is it.
        if (options.Flag("trace-exceptions"))
        {
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
                Console.Error.WriteLine($"[first chance] 0x{e.Exception.HResult:X8} {e.Exception.GetType().Name}: {e.Exception.Message}{Environment.NewLine}{Environment.StackTrace}");
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            var app = new PresentApp(options);
            GC.KeepAlive(app);
        });
        return PresentApp.ExitCode;
    }
}
