using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.Core.Capture;
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
    /// <summary>Screenshot only: select a region, scroll, and stitch the frames into one tall image.</summary>
    Scrolling,
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

    // A single hidden instance is kept alive between captures: creating a WinUI window with an
    // acrylic backdrop costs 100-250 ms, which used to be paid on every hotkey press.
    private static CapturePickerWindow? _pooled;

    private TaskCompletionSource<CapturePickerResult?> _result = new();
    private bool _countdownEnabled;
    private int _countdownDuration;
    private double _videoTimeLimitMinutes;
    private bool _completed;
    private bool _isShowing;
    private bool _pendingRegionApply;
    private int _windowWidth;
    private int _windowHeight;
    private double _windowScale = 1.0;

    private readonly FloatingWindowDragger _dragger;

    private CapturePickerWindow()
    {
        InitializeComponent();

        BuildTimerFlyout();
        BuildLimitFlyout();

        _dragger = new FloatingWindowDragger(AppWindow);

        ConfigurePresenter();
        OverlayWindowHelpers.ExcludeFromCapture(WinRT.Interop.WindowNative.GetWindowHandle(this));

        RootGrid.KeyDown += OnKeyDown;
        Activated += OnActivated;
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_pooled, this))
            {
                _pooled = null;
            }

            Complete(null);
        };
    }

    private void Configure(CaptureType captureType, bool countdownEnabled, int countdownDuration, double videoTimeLimitMinutes)
    {
        _countdownEnabled = countdownEnabled;
        _countdownDuration = countdownDuration <= 0 ? 3 : countdownDuration;
        _videoTimeLimitMinutes = videoTimeLimitMinutes < 0 ? 0 : videoTimeLimitMinutes;
        _completed = false;
        _result = new TaskCompletionSource<CapturePickerResult?>();

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

        UpdateTimerLabel();
        LimitButton.Visibility = captureType == CaptureType.Video ? Visibility.Visible : Visibility.Collapsed;
        ScrollButton.Visibility = captureType == CaptureType.Screenshot ? Visibility.Visible : Visibility.Collapsed;
        UpdateLimitLabel();
    }

    private bool IsScrollingAvailable => ScrollButton.Visibility == Visibility.Visible;

    public static Task<CapturePickerResult?> RunAsync(CaptureType captureType, bool countdownEnabled, int countdownDuration, double videoTimeLimitMinutes = 0)
    {
        var window = _pooled;
        if (window is null || window._isShowing)
        {
            window = new CapturePickerWindow();
            _pooled ??= window;
        }

        window.Configure(captureType, countdownEnabled, countdownDuration, videoTimeLimitMinutes);
        window.PositionNearTopOfPrimaryDisplay();
        window._isShowing = true;
        window._pendingRegionApply = true;
        window.AppWindow.Show();
        window.Activate();
        CaptureFlowTrace.Mark("picker: activated");
        return window._result.Task;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        if (_pendingRegionApply)
        {
            _pendingRegionApply = false;
            OverlayWindowHelpers.ApplyRoundedRegion(
                WinRT.Interop.WindowNative.GetWindowHandle(this),
                _windowWidth, _windowHeight, _windowScale, CornerRadiusDip);
        }

        RootGrid.Focus(FocusState.Programmatic);
    }

    private void PositionNearTopOfPrimaryDisplay()
    {
        // The pooled window may still sit on a previous primary monitor while hidden. Move it to
        // the target work area first so the DPI transition happens before measuring.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var work = DisplayArea.Primary?.WorkArea
            ?? DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var target = AppWindowPlacement.PrepareForTargetWorkArea(AppWindow, hwnd, work);
        var scale = target.Scale;

        RootGrid.UpdateLayout();
        RootGrid.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = (int)Math.Ceiling(RootGrid.DesiredSize.Width * scale);
        var height = (int)Math.Ceiling(RootGrid.DesiredSize.Height * scale);
        width = Math.Max(width, (int)(360 * scale));
        height = Math.Max(height, (int)(64 * scale));

        var x = work.X + ((work.Width - width) / 2);
        var y = work.Y + (int)(72 * scale);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));

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

    private void OnScrolling(object sender, RoutedEventArgs e) => Complete(CapturePickerMode.Scrolling);

    private void OnCancel(object sender, RoutedEventArgs e) => Complete(null);

    // Drag-anywhere support: the R / S / W / timer / cancel buttons handle their own
    // pointer events (marking them handled), so a drag only starts on the bar background.
    // Delegates to FloatingWindowDragger which anchors movement to absolute cursor position.
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e) => _dragger.OnPointerPressed(sender, e);

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e) => _dragger.OnPointerMoved(sender, e);

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) => _dragger.OnPointerReleased(sender, e);

    private void OnPointerCaptureEnded(object sender, PointerRoutedEventArgs e) => _dragger.OnPointerCaptureEnded(sender, e);

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
            case VirtualKey.P when IsScrollingAvailable:
                Complete(CapturePickerMode.Scrolling);
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
        _isShowing = false;
        _result.TrySetResult(mode is { } m ? new CapturePickerResult(m, _countdownEnabled, _countdownDuration, _videoTimeLimitMinutes) : null);

        // Hide rather than close so the next capture reuses this window instantly.
        if (ReferenceEquals(_pooled, this))
        {
            try
            {
                AppWindow.Hide();
                return;
            }
            catch
            {
                _pooled = null;
            }
        }

        Close();
    }

    /// <summary>Closes the cached instance (e.g. on app exit).</summary>
    internal static void ReleasePooled()
    {
        var pooled = _pooled;
        _pooled = null;
        pooled?.Close();
    }
}
