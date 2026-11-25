using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace TinyClips.App;

internal static class ScreenshotOutputToggleFeedback
{
    public static void Reconcile(ToggleSwitch toggle, bool requested, bool actual, InfoBar notice)
    {
        if (requested == actual)
        {
            return;
        }

        // Reassert the source value even if a binding update arrived during the Toggled event.
        toggle.IsOn = actual;
        var peer = FrameworkElementAutomationPeer.FromElement(notice)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(notice);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
