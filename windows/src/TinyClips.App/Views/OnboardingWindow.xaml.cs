using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.Windows.AppNotifications;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.App;

/// <summary>
/// First-run preferences and capture shortcuts. Explicit changes persist immediately.
/// </summary>
public sealed partial class OnboardingWindow : Window
{
    private const int LaunchAtLoginStep = 2;
    private const int LastStep = 4;

    private const int MinimumWidthDip  = 480;
    private const int MinimumHeightDip = 520;

    private readonly ICaptureSettings _settings;
    private int _step;
    private readonly WindowChromeController _chromeController;
    private readonly IHotKeyService _hotKeys;
    private readonly HotKeyEditOperation _hotKeyEdit;
    private readonly Dictionary<HotKeyAction, TextBlock> _shortcutLabels = new();

    public OnboardingViewModel ViewModel { get; }

    public OnboardingWindow()
    {
        _settings = App.Services.GetRequiredService<ICaptureSettings>();
        _hotKeys = App.Services.GetRequiredService<IHotKeyService>();
        _hotKeyEdit = new HotKeyEditOperation(
            _hotKeys, () => ((App)Application.Current).ReapplyGlobalHotKeys(), _hotKeys.SetBinding);
        ViewModel = new OnboardingViewModel(App.Services.GetRequiredService<ILaunchAtLoginService>(), _settings);

        InitializeComponent();
        WindowCaptureVisibility.Register(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindowPlacement.CenterInCurrentWorkAreaAtDipSize(AppWindow, hwnd, 720, 640);

        // WindowChromeController owns: icon-on-activation, DIP minimum enforcement, XamlRoot
        // scale tracking, and cleanup of all three on Closed. The window's own Close() calls
        // are not lifecycle subscriptions, so no additive handler is needed.
        _chromeController = new WindowChromeController(this, RootGrid, MinimumWidthDip, MinimumHeightDip);

        RootGrid.RequestedTheme = _settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        AddShortcutRow(HotKeyAction.Screenshot);
        AddShortcutRow(HotKeyAction.RecordVideo);
        AddShortcutRow(HotKeyAction.RecordGif);
        RefreshShortcuts();

        // A change can still finish after the wizard closes; it no longer has a toggle to update.
        Closed += (_, _) => Bindings.StopTracking();
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                RefreshLaunchAtLoginOnItsStep();
                RefreshShortcuts();
            }
        };

        _ = ViewModel.RefreshAsync();
        UpdateStep();
    }

    private void OnNextClicked(object sender, RoutedEventArgs e)
    {
        if (_step >= LastStep)
        {
            Complete();
            return;
        }

        _step++;
        RefreshLaunchAtLoginOnItsStep();
        UpdateStep();
    }

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        if (_step > 0)
        {
            _step--;
            RefreshLaunchAtLoginOnItsStep();
            UpdateStep();
        }
    }

    private void OnSkipClicked(object sender, RoutedEventArgs e) => Complete();

    private void Complete()
    {
        _settings.HasCompletedOnboarding = true;
        Close();
    }

    // The state can change outside the wizard, such as from Settings or Windows Settings.
    private void RefreshLaunchAtLoginOnItsStep()
    {
        if (_step == LaunchAtLoginStep)
        {
            ViewModel.RefreshPreferences();
            RefreshNotificationStatus();
            _ = ViewModel.RefreshAsync();
        }
    }

    private void UpdateStep()
    {
        Step0.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;
        RefreshShortcuts();

        BackButton.Visibility = _step > 0 ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = _step >= LastStep ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = _step >= LastStep ? "Get started" : "Next";

        var active = (Style)RootGrid.Resources["DotActiveStyle"];
        var inactive = (Style)RootGrid.Resources["DotInactiveStyle"];
        Dot0.Style = _step == 0 ? active : inactive;
        Dot1.Style = _step == 1 ? active : inactive;
        Dot2.Style = _step == 2 ? active : inactive;
        Dot3.Style = _step == 3 ? active : inactive;
        Dot4.Style = _step == 4 ? active : inactive;
    }

    private void AddShortcutRow(HotKeyAction action)
    {
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _shortcutLabels.Add(action, label);
        var row = new StackPanel { Spacing = 8 };
        row.Children.Add(label);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var edit = new Button { Content = "Edit" };
        var reset = new Button { Content = "Reset" };
        var clear = new Button { Content = "Clear" };
        foreach (var button in new[] { edit, reset, clear })
        {
            AutomationProperties.SetName(button, $"{button.Content} {HotKeyEditOperation.ActionName(action)} shortcut");
            AutomationProperties.SetAutomationId(button, $"Onboarding{action}{button.Content}Shortcut");
            buttons.Children.Add(button);
        }
        edit.Click += (_, _) => HotKeyRecorder.Show(edit, _hotKeys, _hotKeyEdit, action, ReportShortcut);
        reset.Click += (_, _) => ApplyShortcut(action, _hotKeys.DefaultFor(action));
        clear.Click += (_, _) => ApplyShortcut(action, new HotKeyDefinition(HotKeyModifiers.None, 0));
        row.Children.Add(buttons);
        ShortcutRows.Children.Add(new Border { Style = (Style)Application.Current.Resources["SettingCardStyle"], Child = row });
    }

    private void ApplyShortcut(HotKeyAction action, HotKeyDefinition candidate)
    {
        if (_hotKeyEdit.TryApply(action, candidate, out var error))
        {
            ReportShortcut($"{HotKeyEditOperation.ActionName(action)} shortcut: {candidate.DisplayString}.", InfoBarSeverity.Success);
        }
        else
        {
            ReportShortcut(error, InfoBarSeverity.Error);
        }
    }

    private void ReportShortcut(string message, InfoBarSeverity severity)
    {
        RefreshShortcuts();
        ShortcutStatus.Message = message;
        ShortcutStatus.Severity = severity;
        ShortcutStatus.IsOpen = true;
        var peer = FrameworkElementAutomationPeer.FromElement(ShortcutStatus)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(ShortcutStatus);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void RefreshShortcuts()
    {
        foreach (var (action, label) in _shortcutLabels)
        {
            label.Text = $"{HotKeyEditOperation.ActionName(action)}: {_hotKeys.GetBinding(action).DisplayString}";
        }
        ScreenshotShortcut.Text = _hotKeys.GetBinding(HotKeyAction.Screenshot).DisplayString;
        VideoShortcut.Text = _hotKeys.GetBinding(HotKeyAction.RecordVideo).DisplayString;
        GifShortcut.Text = _hotKeys.GetBinding(HotKeyAction.RecordGif).DisplayString;
    }

    private void RefreshNotificationStatus()
    {
        try
        {
            var setting = AppNotificationManager.Default.Setting;
            NotificationStatus.Text = setting == AppNotificationSetting.Enabled
                ? "Windows allows notifications. Do not disturb can still silence alerts."
                : $"Windows notification availability: {setting}. Turning on save notifications does not override Windows settings.";
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to read notification availability: {ex}");
            NotificationStatus.Text = "Couldn't check Windows notification availability. Check Windows notification settings.";
        }
    }

    private async void OnNotificationSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:notifications")))
        {
            NotificationStatus.Text = "Couldn't open Windows notification settings. Open Windows Settings → System → Notifications.";
        }
    }
}
