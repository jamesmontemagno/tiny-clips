using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace TinyClips.App;

// A lightweight, borderless "quick access" popup (PowerToys-style) shown next to the
// system tray icon. It light-dismisses when it loses focus and hosts custom WinUI content.
internal sealed class TrayPopupWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly nint _hwnd;

    public TrayPopupWindow(UIElement content)
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        var id = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);

        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        _appWindow.SetPresenter(presenter);
        _appWindow.IsShownInSwitchers = false;

        SystemBackdrop = new DesktopAcrylicBackdrop();
        Content = content;

        // Never let the tray popup land in a screenshot/backdrop captured right after a click,
        // which lets the capture flow start immediately instead of waiting for it to dismiss.
        OverlayWindowHelpers.ExcludeFromCapture(_hwnd);

        Activated += OnActivated;
        Closed += OnClosed;
        _appWindow.Hide();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            _appWindow.Hide();
        }
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        // Unsubscribe so a stale Activated notification cannot reach a closed _appWindow.
        Activated -= OnActivated;
        Closed -= OnClosed;
    }

    public bool IsOpen => _appWindow.IsVisible;

    public void Hide() => _appWindow.Hide();

    // Shows the popup anchored just above-left of the cursor (the tray sits at the
    // bottom-right of the screen), clamped to the work area of the active monitor.
    public void ShowNearCursor(double logicalWidth, double logicalHeight)
    {
        RectInt32 area;
        if (GetCursorPos(out var pt))
        {
            area = DisplayArea.GetFromPoint(
                new PointInt32(pt.X, pt.Y),
                DisplayAreaFallback.Nearest).WorkArea;
        }
        else
        {
            area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            pt.X = area.X + area.Width;
            pt.Y = area.Y + area.Height;
        }

        var target = AppWindowPlacement.PrepareForTargetWorkArea(_appWindow, _hwnd, area);
        var width = AppWindowPlacement.DipToPixels(logicalWidth, target.Scale);
        var height = AppWindowPlacement.DipToPixels(logicalHeight, target.Scale);
        var rect = AppWindowPlacement.ClampToWorkArea(
            target.WorkArea,
            pt.X - width,
            pt.Y - height,
            width,
            height);

        _appWindow.MoveAndResize(rect);
        _appWindow.Show();
        Activate();
        SetForegroundWindow(_hwnd);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);
}
