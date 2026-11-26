using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.App;

/// <summary>Shows an output failure even when operating-system notifications are disabled.</summary>
internal sealed class ScreenshotOutputNoticeWindow : Window
{
    private readonly InfoBar _status;
    private readonly WindowChromeController _chromeController;

    public ScreenshotOutputNoticeWindow(string message, ICaptureSettings settings, string? actionLabel, Action? action)
    {
        Title = "Screenshot needs attention";
        _status = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
            Title = "Screenshot needs attention",
            Message = message,
        };
        AutomationProperties.SetAutomationId(_status, "ScreenshotOutputNoticeStatus");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_status, message);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (action is not null && actionLabel is not null)
        {
            var actionButton = new Button { Content = actionLabel };
            AutomationProperties.SetAutomationId(actionButton, "ScreenshotOutputNoticeActionButton");
            actionButton.Click += (_, _) =>
            {
                try
                {
                    action();
                    Close();
                }
                catch (Exception ex)
                {
                    CrashDiagnostics.Log("Screenshot output notice action", ex, handled: true);
                    _status.Message = "Couldn't open that action. Try again from the Tiny Clips tray menu.";
                    AutomationProperties.SetName(_status, _status.Message);
                    FrameworkElementAutomationPeer.FromElement(_status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                }
            };
            actions.Children.Add(actionButton);
        }
        var close = new Button { Content = "Close" };
        AutomationProperties.SetAutomationId(close, "ScreenshotOutputNoticeCloseButton");
        close.Click += (_, _) => Close();
        actions.Children.Add(close);
        var content = new StackPanel { Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(_status);
        content.Children.Add(actions);
        var root = new Grid { Padding = new Thickness(24) };
        root.Children.Add(content);
        root.RequestedTheme = settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        Content = root;
        WindowCaptureVisibility.Register(this);
        AppWindowPlacement.CenterInCurrentWorkAreaAtDipSize(
            AppWindow, WinRT.Interop.WindowNative.GetWindowHandle(this), 520, 280);
        _chromeController = new WindowChromeController(this, root, 420, 260);
    }
}
