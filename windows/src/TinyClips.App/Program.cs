using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace TinyClips.App;

/// <summary>
/// Replaces the XAML-generated entry point (see DISABLE_XAML_GENERATED_MAIN in the project file)
/// so a second launch can be handed to the running instance before any XAML is created.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (SingleInstance.TryRedirectToRunningInstance())
        {
            return;
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
