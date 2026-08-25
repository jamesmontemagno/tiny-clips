using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace TinyClips.App;

/// <summary>
/// Manages full-window chrome concerns for a standard <see cref="Window"/>:
/// <list type="bullet">
///   <item>Sets the taskbar / Alt+Tab <see cref="AppWindow"/> icon on the first activation.</item>
///   <item>Enforces DIP-based preferred minimum dimensions and updates them whenever the
///     <see cref="XamlRoot"/> rasterization scale changes, for example when the user moves the
///     window to a monitor with a different DPI.</item>
/// </list>
/// All event subscriptions are released when the window fires <see cref="Window.Closed"/>.
/// Construct one instance per window in the window's constructor, after
/// <see cref="Window.InitializeComponent"/> has run, and keep it alive as a field.
/// </summary>
internal sealed class WindowChromeController
{
    private readonly Window _window;
    private readonly FrameworkElement _rootElement;
    private readonly int _minWidthDip;
    private readonly int _minHeightDip;
    private XamlRoot? _xamlRoot;

    /// <param name="window">The window to manage.</param>
    /// <param name="rootElement">Root <see cref="FrameworkElement"/> of the window's content
    ///   tree, used to reach <see cref="XamlRoot"/> once the element tree is loaded.</param>
    /// <param name="minWidthDip">Preferred minimum width in device-independent pixels.</param>
    /// <param name="minHeightDip">Preferred minimum height in device-independent pixels.</param>
    public WindowChromeController(
        Window window,
        FrameworkElement rootElement,
        int minWidthDip,
        int minHeightDip)
    {
        _window = window;
        _rootElement = rootElement;
        _minWidthDip = minWidthDip;
        _minHeightDip = minHeightDip;

        // Apply an initial minimum size using the HWND-based scale before XamlRoot is available.
        // This is refined in OnRootElementLoaded once the rasterization scale is known.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        UpdatePreferredMinimumSize(AppWindowPlacement.GetScaleForWindow(hwnd));

        window.Activated += OnActivatedSetIcon;
        rootElement.Loaded += OnRootElementLoaded;
        window.Closed += OnWindowClosed;
    }

    // Sets the AppWindow icon once on first activation and self-unsubscribes.
    private void OnActivatedSetIcon(object sender, WindowActivatedEventArgs args)
    {
        _window.Activated -= OnActivatedSetIcon;
        WindowIcon.Apply(_window.AppWindow);
    }

    private void OnRootElementLoaded(object sender, RoutedEventArgs args)
    {
        _rootElement.Loaded -= OnRootElementLoaded;
        _xamlRoot = _rootElement.XamlRoot;

        if (_xamlRoot is null)
        {
            return;
        }

        _xamlRoot.Changed += OnXamlRootChanged;
        UpdatePreferredMinimumSize(_xamlRoot.RasterizationScale);
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        UpdatePreferredMinimumSize(sender.RasterizationScale);
    }

    private void UpdatePreferredMinimumSize(double scale)
    {
        if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth  = AppWindowPlacement.DipToPixels(_minWidthDip, scale);
            presenter.PreferredMinimumHeight = AppWindowPlacement.DipToPixels(_minHeightDip, scale);
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // Belt-and-suspenders: unsubscribe Loaded in case the window is closed before the
        // element tree is ever loaded (Loaded already self-unsubscribes in the normal path).
        _rootElement.Loaded -= OnRootElementLoaded;

        if (_xamlRoot is not null)
        {
            _xamlRoot.Changed -= OnXamlRootChanged;
            _xamlRoot = null;
        }

        // OnActivatedSetIcon is self-unsubscribing, but clean it up here in case the window
        // is closed before its first activation.
        _window.Activated -= OnActivatedSetIcon;
        _window.Closed -= OnWindowClosed;
    }
}
