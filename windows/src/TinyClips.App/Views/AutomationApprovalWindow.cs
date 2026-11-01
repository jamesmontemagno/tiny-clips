using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.System;

namespace TinyClips.App;

/// <summary>
/// A small always-on-top window that asks the user to approve an agent request. Deny has focus,
/// Esc and closing the window deny, and the window closes itself when the request is cancelled
/// (for example after the 120 second approval timeout).
/// </summary>
internal sealed partial class AutomationApprovalWindow : Window
{
    private const int WidthDip = 480;
    private const int HeightDip = 380;

    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Button _denyButton;
    private bool _focused;

    private AutomationApprovalWindow(AutomationApprovalContent content)
    {
        Title = "Tiny Clips — Agent request";
        SystemBackdrop = new MicaBackdrop();
        WindowCaptureVisibility.Register(this, alwaysExcluded: true);
        WindowIcon.Apply(AppWindow);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindowPlacement.CenterInCurrentWorkAreaAtDipSize(AppWindow, hwnd, WidthDip, HeightDip);

        var root = new Grid
        {
            Padding = new Thickness(24),
            RowSpacing = 12,
            RequestedTheme = App.Services.GetRequiredService<ICaptureSettings>().Theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            },
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        header.Children.Add(new FontIcon
        {
            Glyph = "\uE99A",
            FontSize = 24,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var title = new TextBlock
        {
            Text = content.Title,
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        header.Children.Add(title);
        root.Children.Add(header);

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = content.Message, TextWrapping = TextWrapping.Wrap });
        if (content.ListHeader is { } listHeader && content.Items.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = listHeader,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                Margin = new Thickness(0, 4, 0, 0),
            });
            foreach (var item in content.Items)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "• " + item,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                });
            }
        }

        body.Children.Add(new TextBlock
        {
            Text = content.Footnote,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Margin = new Thickness(0, 4, 0, 0),
        });
        var scroller = new ScrollViewer { Content = body };
        Grid.SetRow(scroller, 1);
        root.Children.Add(scroller);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var allowButton = new Button
        {
            Content = content.AllowText,
            MinWidth = 96,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        AutomationProperties.SetName(allowButton, content.AllowText);
        AutomationProperties.SetAutomationId(allowButton, "AgentApprovalAllowButton");
        allowButton.Click += (_, _) => Finish(true);

        _denyButton = new Button { Content = "Deny", MinWidth = 96 };
        AutomationProperties.SetName(_denyButton, "Deny");
        AutomationProperties.SetAutomationId(_denyButton, "AgentApprovalDenyButton");
        _denyButton.Click += (_, _) => Finish(false);

        buttons.Children.Add(allowButton);
        buttons.Children.Add(_denyButton);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            args.Handled = true;
            Finish(false);
        };
        root.KeyboardAccelerators.Add(escape);

        Content = root;
        Activated += OnActivated;
        Closed += (_, _) => _result.TrySetResult(false);
    }

    /// <summary>Shows the prompt and returns true only if the user chose Allow. Call on the UI thread.</summary>
    public static async Task<bool> ShowAsync(AutomationApprovalContent content, CancellationToken cancellationToken)
    {
        var window = new AutomationApprovalWindow(content);
        using var registration = cancellationToken.Register(() =>
            window.DispatcherQueue.TryEnqueue(() => window.Finish(false)));
        ((App)Application.Current).ActivateAutomationWindow(window);
        return await window._result.Task;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_focused && args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _focused = true;
            _denyButton.Focus(FocusState.Programmatic);
        }
    }

    private void Finish(bool approved)
    {
        if (_result.TrySetResult(approved))
        {
            Close();
        }
    }
}
