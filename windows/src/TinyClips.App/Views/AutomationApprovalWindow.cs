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
    private const int ScopeChoicesHeightDip = 640;

    private readonly TaskCompletionSource<IReadOnlyList<string>?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Button _denyButton;
    private readonly List<(string Name, CheckBox Box)> _scopeBoxes = [];
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
        AppWindowPlacement.CenterInCurrentWorkAreaAtDipSize(
            AppWindow, hwnd, WidthDip, content.ScopeChoices is null ? HeightDip : ScopeChoicesHeightDip);

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
        if (content.ListHeader is { } listHeader && (content.Items.Count > 0 || content.ScopeChoices is { Count: > 0 }))
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

        foreach (var choice in content.ScopeChoices ?? [])
        {
            body.Children.Add(CreateScopeCheckBox(choice));
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
        allowButton.Click += (_, _) => Finish(SelectedScopes());
        if (_scopeBoxes.Count > 0)
        {
            void UpdateAllow() => allowButton.IsEnabled = _scopeBoxes.Any(s => s.Box.IsChecked == true);
            foreach (var (_, box) in _scopeBoxes)
            {
                box.Checked += (_, _) => UpdateAllow();
                box.Unchecked += (_, _) => UpdateAllow();
            }

            UpdateAllow();
        }

        _denyButton = new Button { Content = "Deny", MinWidth = 96 };
        AutomationProperties.SetName(_denyButton, "Deny");
        AutomationProperties.SetAutomationId(_denyButton, "AgentApprovalDenyButton");
        _denyButton.Click += (_, _) => Finish(null);

        buttons.Children.Add(allowButton);
        buttons.Children.Add(_denyButton);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            args.Handled = true;
            Finish(null);
        };
        root.KeyboardAccelerators.Add(escape);

        Content = root;
        Activated += OnActivated;
        Closed += (_, _) => _result.TrySetResult(null);
    }

    /// <summary>
    /// Shows the prompt and returns null unless the user chose Allow. When allowed, returns the checked
    /// scopes, or an empty list if the prompt has no scope choices. Call on the UI thread.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> ShowAsync(AutomationApprovalContent content, CancellationToken cancellationToken)
    {
        var window = new AutomationApprovalWindow(content);
        using var registration = cancellationToken.Register(() =>
            window.DispatcherQueue.TryEnqueue(() => window.Finish(null)));
        ((App)Application.Current).ActivateAutomationWindow(window);
        return await window._result.Task;
    }

    private CheckBox CreateScopeCheckBox(AutomationScopeChoice choice)
    {
        var label = new StackPanel();
        label.Children.Add(new TextBlock
        {
            Text = choice.IsSensitive ? $"{choice.Name} (asks every time)" : choice.Name,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        label.Children.Add(new TextBlock
        {
            Text = choice.Description,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        var box = new CheckBox { Content = label, IsChecked = choice.IsRequested };
        AutomationProperties.SetName(box, $"{choice.Name}: {choice.Description}");
        AutomationProperties.SetAutomationId(box, $"AgentApprovalScope_{choice.Name}");
        _scopeBoxes.Add((choice.Name, box));
        return box;
    }

    private IReadOnlyList<string> SelectedScopes() =>
        _scopeBoxes.Where(s => s.Box.IsChecked == true).Select(s => s.Name).ToArray();

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_focused && args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _focused = true;
            _denyButton.Focus(FocusState.Programmatic);
        }
    }

    private void Finish(IReadOnlyList<string>? approvedScopes)
    {
        if (_result.TrySetResult(approvedScopes))
        {
            Close();
        }
    }
}
