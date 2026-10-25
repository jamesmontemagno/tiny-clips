using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using TinyClips.Core.Capture;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace TinyClips.App;

/// <summary>
/// Floating always-on-top panel shown while a scrolling capture is running: live frame count,
/// status, and Done (Enter) / Cancel (Esc). Mirrors the macOS <c>ScrollingCapturePanel</c>.
/// </summary>
public sealed partial class ScrollingCaptureWindow : Window
{
    private const int RegionOutsideOffsetDip = 12;
    private const int BottomOffsetDip = 32;
    private const string DefaultStatus = "Scroll the page, then press Enter";

    // Remembered across captures within a session so a dragged panel stays where the user put it.
    private static PointInt32? _lastPosition;

    private readonly FloatingWindowDragger _dragger;
    private string _defaultStatus = DefaultStatus;
    private bool _completed;
    private bool _closed;

    public ScrollingCaptureWindow()
    {
        InitializeComponent();
        WindowCaptureVisibility.Register(this);

        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        _dragger = new FloatingWindowDragger(AppWindow);
        RootGrid.KeyDown += OnKeyDown;
        Activated += OnActivated;
        Closed += OnClosed;
    }

    public Action? StopRequested { get; set; }

    public Action? CancelRequested { get; set; }

    /// <summary>
    /// Switches the labels for auto-scroll capture, where Tiny Clips does the scrolling: Done
    /// stops early and keeps what was captured, Cancel discards it.
    /// </summary>
    public void ConfigureForAutoScroll()
    {
        _defaultStatus = "Scrolling… Enter to stop, Esc to cancel";
        StatusText.Text = _defaultStatus;
        ModeText.Text = "Auto-scroll";
        ModeIcon.Glyph = "\uE74B";
        AutomationProperties.SetName(RootGrid, "Auto-scroll capture in progress");
        AutomationProperties.SetName(ModePanel, "Auto-scroll capture mode");
        AutomationProperties.SetName(DoneButton, "Stop auto-scroll and keep the capture");
        AutomationProperties.SetHelpText(DoneButton, "Stops scrolling and stitches what has been captured so far.");
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(DoneButton, "Stop and keep what's captured (Enter)");
        AutomationProperties.SetName(CancelButton, "Cancel auto-scroll capture");
    }

    /// <summary>Shows the panel just outside the captured region (or at the bottom of the monitor's work area).</summary>
    public void ShowNear(MonitorInfo? monitor, PixelRect? regionInVirtualDesktop)
    {
        Position(monitor, regionInVirtualDesktop);
        AppWindow.Show(true);
        Activate();

        var hwnd = WindowNative.GetWindowHandle(this);
        PulseStoryboard.Begin();
    }

    public void UpdateFrameCount(int count)
    {
        var label = count == 1 ? "1 frame" : $"{count} frames";
        FrameCountText.Text = label;
        // The live region announces its name, so keep it in sync with the visible label.
        AutomationProperties.SetName(FrameCountText, $"Captured frames: {label}");
    }

    public void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
    }

    /// <summary>Disables the controls and shows "Saving…" while the frames are stitched and written.</summary>
    public void MarkFinishing()
    {
        _completed = true;
        DoneButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        PulseStoryboard.Stop();
        if (StatusText.Text == _defaultStatus)
        {
            StatusText.Text = "Saving…";
        }

        AutomationProperties.SetName(RootGrid, "Scrolling capture saving");
        AutomationProperties.SetHelpText(DoneButton, "Saving captured frames.");
        AutomationProperties.SetHelpText(CancelButton, "Saving captured frames.");
    }

    public void ClosePanel()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        StopRequested = null;
        CancelRequested = null;
        RememberPosition();
        Close();
    }

    private void OnDoneClick(object sender, RoutedEventArgs e) => Finish(stop: true);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Finish(stop: false);

    // Drag-anywhere support: the buttons mark their own pointer events handled, so a drag only
    // starts on the panel surface.
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e) => _dragger.OnPointerPressed(sender, e);

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e) => _dragger.OnPointerMoved(sender, e);

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) => _dragger.OnPointerReleased(sender, e);

    private void OnPointerCaptureEnded(object sender, PointerRoutedEventArgs e) => _dragger.OnPointerCaptureEnded(sender, e);

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                Finish(stop: true);
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                Finish(stop: false);
                e.Handled = true;
                break;
        }
    }

    private void Finish(bool stop)
    {
        if (_completed)
        {
            return;
        }

        if (stop)
        {
            MarkFinishing();
            StopRequested?.Invoke();
        }
        else
        {
            _completed = true;
            var cancel = CancelRequested;
            ClosePanel();
            cancel?.Invoke();
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            RootGrid.Focus(FocusState.Programmatic);
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_closed)
        {
            return;
        }

        // An external close (Alt+F4, shell) must not leave the capture running headless.
        _closed = true;
        var cancel = CancelRequested;
        StopRequested = null;
        CancelRequested = null;
        RememberPosition();
        if (!_completed)
        {
            _completed = true;
            cancel?.Invoke();
        }
    }

    private void RememberPosition()
    {
        try
        {
            _lastPosition = AppWindow.Position;
        }
        catch
        {
            // The window may already be gone.
        }
    }

    private void Position(MonitorInfo? monitor, PixelRect? regionInVirtualDesktop)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var target = AppWindowPlacement.PrepareForTargetMonitor(AppWindow, hwnd, monitor);
        var work = target.WorkArea;
        var scale = target.Scale;

        RootGrid.UpdateLayout();
        RootGrid.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Max((int)Math.Ceiling(RootGrid.DesiredSize.Width * scale), AppWindowPlacement.DipToPixels(420, scale));
        var height = Math.Max((int)Math.Ceiling(RootGrid.DesiredSize.Height * scale), AppWindowPlacement.DipToPixels(56, scale));

        int x;
        int y;
        if (_lastPosition is { } remembered)
        {
            x = remembered.X;
            y = remembered.Y;
        }
        else
        {
            var regionOutsideOffset = AppWindowPlacement.DipToPixels(RegionOutsideOffsetDip, scale);
            x = work.X + Math.Max(0, (work.Width - width) / 2);
            y = work.Y + Math.Max(0, work.Height - height - AppWindowPlacement.DipToPixels(BottomOffsetDip, scale));

            if (regionInVirtualDesktop is { Width: > 0, Height: > 0 } region)
            {
                x = region.X + Math.Max(0, (region.Width - width) / 2);
                var preferredBelow = region.Y + region.Height + regionOutsideOffset;
                var preferredAbove = region.Y - height - regionOutsideOffset;
                if (preferredBelow <= work.Y + Math.Max(0, work.Height - height))
                {
                    y = preferredBelow;
                }
                else if (preferredAbove >= work.Y)
                {
                    y = preferredAbove;
                }
            }
        }

        AppWindow.MoveAndResize(AppWindowPlacement.ClampToWorkArea(work, x, y, width, height));
    }
}
