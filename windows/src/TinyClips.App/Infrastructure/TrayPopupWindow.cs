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

        Activated += OnActivated;
        _appWindow.Hide();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            _appWindow.Hide();
        }
    }

    public bool IsOpen => _appWindow.IsVisible;

    public void Hide() => _appWindow.Hide();

    // Shows the popup anchored just above-left of the cursor (the tray sits at the
    // bottom-right of the screen), clamped to the work area of the active monitor.
    public void ShowNearCursor(double logicalWidth, double logicalHeight)
    {
        var dpi = GetDpiForWindow(_hwnd);
        var scale = dpi <= 0 ? 1.0 : dpi / 96.0;
        var w = (int)(logicalWidth * scale);
        var h = (int)(logicalHeight * scale);

        var x = w;
        var y = h;
        if (GetCursorPos(out var pt))
        {
            x = pt.X - w;
            y = pt.Y - h;

            var area = DisplayArea.GetFromPoint(new PointInt32(pt.X, pt.Y), DisplayAreaFallback.Nearest).WorkArea;
            x = Math.Max(area.X, Math.Min(x, area.X + area.Width - w));
            y = Math.Max(area.Y, Math.Min(y, area.Y + area.Height - h));
        }

        _appWindow.MoveAndResize(new RectInt32(x, y, w, h));
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
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);
}
