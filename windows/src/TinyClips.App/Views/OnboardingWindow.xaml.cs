using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

    private readonly ICaptureSettings _settings;
    private int _step;

    public OnboardingWindow()
    {
        _settings = App.Services.GetRequiredService<ICaptureSettings>();

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(720, 640));

        RootGrid.RequestedTheme = _settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        var hotKeys = App.Services.GetRequiredService<IHotKeyService>();
        ScreenshotShortcut.Text = hotKeys.GetBinding(CaptureType.Screenshot).DisplayString;
        VideoShortcut.Text = hotKeys.GetBinding(CaptureType.Video).DisplayString;
        GifShortcut.Text = hotKeys.GetBinding(CaptureType.Gif).DisplayString;

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

        var active = (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var inactive = (SolidColorBrush)Application.Current.Resources["ControlStrongFillColorDefaultBrush"];
        Dot0.Fill = _step == 0 ? active : inactive;
        Dot1.Fill = _step == 1 ? active : inactive;
        Dot2.Fill = _step == 2 ? active : inactive;
    }
}
