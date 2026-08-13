using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace TinyClips.App;

/// <summary>
/// Keeps a WinUI Automation peer available for tray-first capture status announcements.
/// </summary>
internal sealed class AutomationNotificationAnnouncer : Window
{
    private const string AnchorName = "Tiny Clips capture status";

    private readonly TextBlock _anchor;

    public AutomationNotificationAnnouncer()
    {
        _anchor = new TextBlock
        {
            Width = 1,
            Height = 1,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        AutomationProperties.SetName(_anchor, AnchorName);
        AutomationProperties.SetAutomationId(_anchor, "TinyClipsCaptureStatus");
        Content = _anchor;

        var presenter = OverlappedPresenter.CreateForContextMenu();
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Resize(new SizeInt32(1, 1));
        AppWindow.Move(new PointInt32(-32000, -32000));
        AppWindow.Show(false);
    }

    public void Announce(
        AutomationNotificationKind kind,
        AutomationNotificationProcessing processing,
        string message,
        string activityId)
    {
        _anchor.Text = message;
        var peer = FrameworkElementAutomationPeer.FromElement(_anchor)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(_anchor);
        peer?.RaiseNotificationEvent(kind, processing, message, activityId);
    }
}
