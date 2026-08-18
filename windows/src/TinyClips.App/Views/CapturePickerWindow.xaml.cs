using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.Core.Models;
using Windows.Graphics;
using Windows.System;

namespace TinyClips.App;

/// <summary>How the user chose to scope a capture.</summary>
public enum CapturePickerMode
{
    Region,
    Screen,
    Window,
    RecognizeText,
}

/// <summary>The user's choice from the capture picker bar.</summary>
public sealed record CapturePickerResult(CapturePickerMode Mode, bool CountdownEnabled, int CountdownDuration, double VideoTimeLimitMinutes);

/// <summary>
/// A floating, borderless picker bar shown near the top of the primary display when a
/// capture starts. Lets the user choose Region / Screen / Window and a countdown, with
/// R / S / W / Esc keyboard shortcuts — mirroring the macOS CapturePickerPanel.
/// </summary>
public sealed partial class CapturePickerWindow : Window
{
    private static readonly int[] CountdownOptions = { 1, 2, 3, 5, 10 };
    private static readonly int[] LimitOptions = { 0, 1, 2, 5, 10, 15, 30 };
    private const int CornerRadiusDip = 8;

    private readonly TaskCompletionSource<CapturePickerResult?> _result = new();
    private bool _countdownEnabled;
    private int _countdownDuration;
    private double _videoTimeLimitMinutes;
    private bool _completed;
    private int _windowWidth;
    private int _windowHeight;
    private double _windowScale = 1.0;

    private readonly FloatingWindowDragger _dragger;

    private CapturePickerWindow(CaptureType captureType, bool countdownEnabled, int countdownDuration, double videoTimeLimitMinutes)
    {
        InitializeComponent();

        _countdownEnabled = countdownEnabled;
        _countdownDuration = countdownDuration <= 0 ? 3 : countdownDuration;
        _videoTimeLimitMinutes = videoTimeLimitMinutes < 0 ? 0 : videoTimeLimitMinutes;

        ModeIcon.Glyph = captureType switch
        {
            CaptureType.Video => "\uE714",
            CaptureType.Gif => "\uE8B9",
            _ => "\uE722",
        };
        ModeLabel.Text = captureType switch
        {
            CaptureType.Video => "Video",
            CaptureType.Gif => "GIF",
            _ => "Screenshot",
        };

        BuildTimerFlyout();
        UpdateTimerLabel();

        if (captureType == CaptureType.Video)
        {
            LimitButton.Visibility = Visibility.Visible;
            BuildLimitFlyout();
            UpdateLimitLabel();
        }
        else
        {
            LimitButton.Visibility = Visibility.Collapsed;
        }

        _dragger = new FloatingWindowDragger(AppWindow);

        ConfigurePresenter();
        PositionNearTopOfPrimaryDisplay();

        RootGrid.KeyDown += OnKeyDown;
        Activated += OnActivated;
    }

    public static Task<CapturePickerResult?> RunAsync(CaptureType captureType, bool countdownEnabled, int countdownDuration, double videoTimeLimitMinutes = 0)
    {
        var window = new CapturePickerWindow(captureType, countdownEnabled, countdownDuration, videoTimeLimitMinutes);
        window.Activate();
        return window._result.Task;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }
        Activated -= OnActivated;
        OverlayWindowHelpers.ApplyRoundedRegion(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            _windowWidth, _windowHeight, _windowScale, CornerRadiusDip);
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void PositionNearTopOfPrimaryDisplay()
    {
        RootGrid.UpdateLayout();
        RootGrid.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var scale = AppWindowPlacement.GetScaleForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var width = (int)Math.Ceiling(RootGrid.DesiredSize.Width * scale);
        var height = (int)Math.Ceiling(RootGrid.DesiredSize.Height * scale);
        width = Math.Max(width, (int)(360 * scale));
        height = Math.Max(height, (int)(64 * scale));

        AppWindow.Resize(new SizeInt32(width, height));
        if (DisplayArea.Primary?.WorkArea is { } work)
        {
            var x = work.X + ((work.Width - width) / 2);
            var y = work.Y + (int)(72 * scale);
            AppWindow.Move(new PointInt32(x, y));
        }

        _windowWidth = width;
        _windowHeight = height;
        _windowScale = scale;
    }

    private void BuildTimerFlyout()
    {
        var off = new MenuFlyoutItem { Text = "Off" };
        off.Click += (_, _) =>
        {
            _countdownEnabled = false;
            UpdateTimerLabel();
        };
        TimerFlyout.Items.Add(off);
        TimerFlyout.Items.Add(new MenuFlyoutSeparator());

        foreach (var seconds in CountdownOptions)
        {
            var item = new MenuFlyoutItem { Text = $"{seconds}s" };
            item.Click += (_, _) =>
            {
                _countdownEnabled = true;
                _countdownDuration = seconds;
                UpdateTimerLabel();
            };
            TimerFlyout.Items.Add(item);
        }
    }

    private void UpdateTimerLabel()
    {
        TimerLabel.Text = _countdownEnabled ? $"{_countdownDuration}s" : "Off";
        AutomationProperties.SetName(TimerButton, $"Countdown timer, {(_countdownEnabled ? _countdownDuration + " seconds" : "off")}");
    }

    private void BuildLimitFlyout()
    {
        foreach (var minutes in LimitOptions)
        {
            var item = new MenuFlyoutItem { Text = minutes == 0 ? "No limit" : $"{minutes} min" };
            item.Click += (_, _) =>
            {
                _videoTimeLimitMinutes = minutes;
                UpdateLimitLabel();
            };
            LimitFlyout.Items.Add(item);
        }
    }

    private void UpdateLimitLabel()
    {
        LimitLabel.Text = _videoTimeLimitMinutes <= 0
            ? "No limit"
            : $"{_videoTimeLimitMinutes:0.##} min";
        AutomationProperties.SetName(
            LimitButton,
            $"Recording time limit, {(_videoTimeLimitMinutes <= 0 ? "no limit" : _videoTimeLimitMinutes + " minutes")}");
    }

    private void ConfigurePresenter()
    {
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        AppWindow.IsShownInSwitchers = false;
    }

    private void OnRegion(object sender, RoutedEventArgs e) => Complete(CapturePickerMode.Region);

    private void OnScreen(object sender, RoutedEventArgs e) => Complete(CapturePickerMode.Screen);

    private void OnWindow(object sender, RoutedEventArgs e) => Complete(CapturePickerMode.Window);

    private void OnRecognizeText(object sender, RoutedEventArgs e) => Complete(CapturePickerMode.RecognizeText);

    private void OnCancel(object sender, RoutedEventArgs e) => Complete(null);

    // Drag-anywhere support: the R / S / W / timer / cancel buttons handle their own
    // pointer events (marking them handled), so a drag only starts on the bar background.
    // Delegates to FloatingWindowDragger which anchors movement to absolute cursor position.
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e) => _dragger.OnPointerPressed(sender, e);

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e) => _dragger.OnPointerMoved(sender, e);

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) => _dragger.OnPointerReleased(sender, e);

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                Complete(null);
                break;
            case VirtualKey.R:
                Complete(CapturePickerMode.Region);
                break;
            case VirtualKey.S:
                Complete(CapturePickerMode.Screen);
                break;
            case VirtualKey.W:
                Complete(CapturePickerMode.Window);
                break;
            case VirtualKey.T:
                Complete(CapturePickerMode.RecognizeText);
                break;
        }
    }

    private void Complete(CapturePickerMode? mode)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _result.TrySetResult(mode is { } m ? new CapturePickerResult(m, _countdownEnabled, _countdownDuration, _videoTimeLimitMinutes) : null);
        Close();
    }
}
