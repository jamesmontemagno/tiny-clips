using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Capture;
using Windows.Graphics;

namespace TinyClips.App;

/// <summary>
/// A centered overlay that lets the user pick which physical display to capture when
/// more than one monitor is present. Resolves with the chosen monitor, or null on cancel.
/// </summary>
public sealed partial class ScreenPickerWindow : Window
{
    private sealed record ScreenItem(MonitorInfo Monitor, string Title, string Subtitle);
    private const int CornerRadiusDip = 8;

    private readonly TaskCompletionSource<MonitorInfo?> _result = new();
    private bool _completed;
    private bool _layoutApplied;
    private int _windowWidth;
    private int _windowHeight;
    private double _windowScale = 1.0;

    private ScreenPickerWindow(IReadOnlyList<MonitorInfo> monitors)
    {
        InitializeComponent();
        ConfigurePresenter();
        Activated += OnActivated;

        var items = new List<ScreenItem>();
        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var title = m.IsPrimary ? $"Display {i + 1} (Primary)" : $"Display {i + 1}";
            items.Add(new ScreenItem(m, title, $"{m.Width} × {m.Height}"));
        }

        ScreenList.ItemsSource = items;
        CenterOnPrimaryDisplay(560, 360);
    }

    public static Task<MonitorInfo?> RunAsync(IReadOnlyList<MonitorInfo> monitors)
    {
        var window = new ScreenPickerWindow(monitors);
        window.Activate();
        return window._result.Task;
    }

    private void OnScreenClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ScreenItem item)
        {
            Complete(item.Monitor);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Complete(null);

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated || _layoutApplied)
        {
            return;
        }

        _layoutApplied = true;
        ApplyRoundedRegion(_windowWidth, _windowHeight, _windowScale);
    }

    private void ConfigurePresenter()
    {
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        AppWindow.IsShownInSwitchers = false;
    }

    private void CenterOnPrimaryDisplay(int width, int height)
    {
        var scale = GetScale();
        var w = (int)Math.Round(width * scale);
        var h = (int)Math.Round(height * scale);

        if (DisplayArea.Primary?.WorkArea is { } work)
        {
            var x = work.X + Math.Max(0, (work.Width - w) / 2);
            var y = work.Y + Math.Max(0, (work.Height - h) / 2);
            AppWindow.Move(new PointInt32(x, y));
        }

        AppWindow.Resize(new SizeInt32(w, h));
        _windowWidth = w;
        _windowHeight = h;
        _windowScale = scale;
    }

    private void ApplyRoundedRegion(int width, int height, double scale)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var radius = (int)Math.Round(CornerRadiusDip * scale);
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius, radius);
        SetWindowRgn(hwnd, region, true);
    }

    private double GetScale()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        return dpi <= 0 ? 1.0 : dpi / 96.0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(nint hWnd, nint hRgn, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool bRedraw);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    private void Complete(MonitorInfo? monitor)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        Activated -= OnActivated;
        _result.TrySetResult(monitor);
        Close();
    }
}
