using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace TinyClips.App.Settings;

/// <summary>
/// Shared helper for the lazy-realization pattern each Settings section follows: begin a
/// persistence-suppression scope before <c>InitializeComponent()</c> runs (so the compiled
/// x:Bind TwoWay targets' initial write-backs don't corrupt saved values), then complete it once
/// the section's root element has raised its first <c>Loaded</c> event.
/// </summary>
public static class SectionLifecycle
{
    /// <summary>
    /// Subscribes to <paramref name="element"/>'s first <c>Loaded</c> event and, when it fires,
    /// unsubscribes and calls <see cref="SettingsViewModel.CompleteSectionRealization"/> with
    /// <paramref name="realizationScope"/>. Call this immediately after
    /// <c>InitializeComponent()</c> in each section's constructor.
    /// </summary>
    /// <remarks>
    /// Also arms a one-shot dispatcher-queue fallback. Rapid navigation can swap
    /// <c>SectionHost.Content</c> to a different, never-before-visited section before a
    /// just-constructed section ever completes a layout pass in the live visual tree — in WinUI,
    /// an element detached before that point never raises <c>Loaded</c> at all. Without the
    /// fallback, that section's realization scope would stay open forever, permanently
    /// suppressing persistence for the orphaned section. The view model tracks overlapping
    /// realization scopes per section, so edits in cached sections remain safe. The fallback
    /// guarantees the scope always completes exactly once, whether or not the element ever loads.
    /// </remarks>
    public static void HookFirstLoad(FrameworkElement element, SettingsViewModel viewModel, System.IDisposable realizationScope)
    {
        var completed = false;

        RoutedEventHandler? handler = null;
        handler = (_, _) =>
        {
            element.Loaded -= handler;
            Complete();
        };
        element.Loaded += handler;

        element.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            element.Loaded -= handler;
            Complete();
        });

        void Complete()
        {
            if (completed)
            {
                return;
            }

            completed = true;
            viewModel.CompleteSectionRealization(realizationScope);
        }
    }
}
