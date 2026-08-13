using System.Runtime.InteropServices;
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
    private const int WidthDip = 220;
    private const int HeightDip = 64;
    private const int TopOffsetDip = 24;

    private const uint WdaExcludeFromCapture = 0x11;

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
        SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
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
        var x = work.X + Math.Max(0, (work.Width - width) / 2);
        var y = work.Y + topOffset;

        if (regionInVirtualDesktop is { Width: > 0, Height: > 0 } region)
        {
            x = region.X + Math.Max(0, (region.Width - width) / 2);
            y = region.Y + topOffset;
        }

        AppWindow.MoveAndResize(AppWindowPlacement.ClampToWorkArea(work, x, y, width, height));
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);
}
