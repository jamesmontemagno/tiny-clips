using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace TinyClips.App;

/// <summary>
/// A centered overlay listing visible top-level windows so the user can pick one to
/// capture. Resolves with the chosen window handle, or null on cancel.
/// </summary>
public sealed partial class WindowPickerWindow : Window
{
    private const int CornerRadiusDip = 8;
    private readonly TaskCompletionSource<nint?> _result = new();
    private bool _completed;
    private bool _layoutApplied;
    private int _windowWidth;
    private int _windowHeight;
    private double _windowScale = 1.0;

    private WindowPickerWindow()
    {
        InitializeComponent();
        ConfigurePresenter();
        Activated += OnActivated;

        var ownHwnd = WindowNative.GetWindowHandle(this);
        WindowList.ItemsSource = WindowEnumerator.GetWindows(ownHwnd);
        CenterOnPrimaryDisplay(520, 640);
    }

    public static Task<nint?> RunAsync()
    {
        var window = new WindowPickerWindow();
        window.Activate();
        return window._result.Task;
    }

    private void OnWindowClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WindowEntry entry)
        {
            Complete(entry.Hwnd);
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
            WindowNative.GetWindowHandle(this),
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
        var hwnd = WindowNative.GetWindowHandle(this);
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

    private void Complete(nint? hwnd)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        Activated -= OnActivated;
        _result.TrySetResult(hwnd);
        Close();
    }
}
