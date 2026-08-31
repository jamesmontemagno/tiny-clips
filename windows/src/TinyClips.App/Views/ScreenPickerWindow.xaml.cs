using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Capture;
using Windows.Graphics;

namespace TinyClips.App;

public sealed record ScreenPickerItem(MonitorInfo Monitor, string Title, string Subtitle);

/// <summary>
/// A centered overlay that lets the user pick which physical display to capture when
/// more than one monitor is present. Resolves with the chosen monitor, or null on cancel.
/// </summary>
public sealed partial class ScreenPickerWindow : Window
{
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

        var items = new List<ScreenPickerItem>();
        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var title = m.IsPrimary ? $"Display {i + 1} (Primary)" : $"Display {i + 1}";
            items.Add(new ScreenPickerItem(m, title, $"{m.Width} × {m.Height}"));
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
        if (e.ClickedItem is ScreenPickerItem item)
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
        OverlayWindowHelpers.ApplyRoundedRegion(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            _windowWidth, _windowHeight, _windowScale, CornerRadiusDip);
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
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = AppWindowPlacement.GetScaleForWindow(hwnd);
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
