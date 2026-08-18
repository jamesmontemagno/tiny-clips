using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.Graphics;

namespace TinyClips.App;

/// <summary>
/// First-run welcome wizard. Three steps introduce the app and its shortcuts, then mark
/// onboarding complete so it does not appear again. Raised via the tray on first launch.
/// </summary>
public sealed partial class OnboardingWindow : Window
{
    private const int LastStep = 2;

    // 480×520 DIP: step 2 (the densest step) holds an 88-DIP illustration, title, body,
    // shortcut card, and navigation footer; 520 keeps all content in-frame at 100% DPI.
    private const int MinimumWidthDip  = 480;
    private const int MinimumHeightDip = 520;

    private readonly ICaptureSettings _settings;
    private int _step;
    private readonly WindowChromeController _chromeController;

    public OnboardingWindow()
    {
        _settings = App.Services.GetRequiredService<ICaptureSettings>();

        InitializeComponent();

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

        var hotKeys = App.Services.GetRequiredService<IHotKeyService>();
        ScreenshotShortcut.Text = hotKeys.GetBinding(HotKeyAction.Screenshot).DisplayString;
        VideoShortcut.Text = hotKeys.GetBinding(HotKeyAction.RecordVideo).DisplayString;
        GifShortcut.Text = hotKeys.GetBinding(HotKeyAction.RecordGif).DisplayString;

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
        UpdateStep();
    }

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        if (_step > 0)
        {
            _step--;
            UpdateStep();
        }
    }

    private void OnSkipClicked(object sender, RoutedEventArgs e) => Complete();

    private void Complete()
    {
        _settings.HasCompletedOnboarding = true;
        Close();
    }

    private void UpdateStep()
    {
        Step0.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.Visibility = _step > 0 ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = _step >= LastStep ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = _step >= LastStep ? "Get started" : "Next";

        var active = (Style)RootGrid.Resources["DotActiveStyle"];
        var inactive = (Style)RootGrid.Resources["DotInactiveStyle"];
        Dot0.Style = _step == 0 ? active : inactive;
        Dot1.Style = _step == 1 ? active : inactive;
        Dot2.Style = _step == 2 ? active : inactive;
    }
}
