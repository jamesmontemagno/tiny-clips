using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using TinyClips.Core.Capture;
using Windows.Graphics;
using WinRT.Interop;
using TinyClips.Core.Models;

namespace TinyClips.App;

/// <summary>
/// Small always-on-top panel shown after the user stops a recording while the clip is being
/// encoded/finalized. Mirrors <see cref="RecordingIndicatorWindow"/>'s floating-panel recipe:
/// borderless, always-on-top, positioned near the top of the primary work area, and excluded
/// from screen capture.
/// </summary>
public sealed partial class ProcessingIndicatorWindow : Window
{
    private const int WidthDip = 260;
    private const int HeightDip = 64;
    private const int TopOffsetDip = 24;
    private const int RegionOutsideOffsetDip = 12;
    private const int CornerRadiusDip = 8;

    private bool _closed;

    public ProcessingIndicatorWindow(CaptureType type)
    {
        InitializeComponent();

        CaptionText.Text = type == CaptureType.Gif
            ? "Finalizing your GIF"
            : "Finalizing your video";

        ConfigurePresenter();
        Closed += OnClosed;
    }

    public void ShowNear()
    {
        ShowNear(null, null);
    }

    public void ShowNear(MonitorInfo? monitor, PixelRect? regionInVirtualDesktop)
    {
        PositionNearMonitorWorkArea(monitor, regionInVirtualDesktop);
        AppWindow.Show(false);

        // Keep the panel out of any concurrent capture.
        var hwnd = WindowNative.GetWindowHandle(this);
        OverlayWindowHelpers.ExcludeFromCapture(hwnd);
    }

    public void ClosePanel()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        Close();
    }

    private void ConfigurePresenter()
    {
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        AppWindow.IsShownInSwitchers = false;
    }

    private void PositionNearMonitorWorkArea(MonitorInfo? monitor, PixelRect? regionInVirtualDesktop)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var target = AppWindowPlacement.PrepareForTargetMonitor(AppWindow, hwnd, monitor);
        var work = target.WorkArea;
        var width = AppWindowPlacement.DipToPixels(WidthDip, target.Scale);
        var height = AppWindowPlacement.DipToPixels(HeightDip, target.Scale);
        var topOffset = AppWindowPlacement.DipToPixels(TopOffsetDip, target.Scale);
        var regionOutsideOffset = AppWindowPlacement.DipToPixels(RegionOutsideOffsetDip, target.Scale);
        var x = work.X + Math.Max(0, (work.Width - width) / 2);
        var y = work.Y + topOffset;

        // Mirror RecordingIndicatorWindow: sit just above the recorded region when there is room,
        // otherwise just below it, and only overlap the region as a last resort.
        if (regionInVirtualDesktop is { Width: > 0, Height: > 0 } region)
        {
            x = region.X + Math.Max(0, (region.Width - width) / 2);
            var preferredAbove = region.Y - height - regionOutsideOffset;
            var preferredBelow = region.Y + region.Height + regionOutsideOffset;
            if (preferredAbove >= work.Y)
            {
                y = preferredAbove;
            }
            else if (preferredBelow <= work.Y + Math.Max(0, work.Height - height))
            {
                y = preferredBelow;
            }
            else
            {
                y = region.Y + topOffset;
            }
        }

        AppWindow.MoveAndResize(AppWindowPlacement.ClampToWorkArea(work, x, y, width, height));

        // Clip the window to the card's rounded corners so the acrylic backdrop doesn't show as
        // a square frame around the rounded border.
        OverlayWindowHelpers.ApplyRoundedRegion(hwnd, width, height, target.Scale, CornerRadiusDip);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
    }

}
