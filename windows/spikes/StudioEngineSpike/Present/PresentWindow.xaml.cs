using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace StudioEngineSpike.Present;

/// <summary>
/// The minimal WinUI 3 window for question 2: the two presentation paths side by side. All the
/// measuring is in <see cref="PresentSession"/>, which runs on its own thread and only comes to
/// the UI thread for what XAML requires.
/// </summary>
public sealed partial class PresentWindow : Window
{
    private readonly SpikeOptions _options;
    private PresentSession? _session;

    internal PresentWindow(SpikeOptions options)
    {
        _options = options;
        InitializeComponent();
        var (width, height) = options.GetSize("window", 3000, 1100);
        AppWindow.MoveAndResize(new RectInt32(60, 60, width, height));
        Root.Loaded += OnLoaded;
    }

    internal Grid RootElement => Root;

    internal Grid PanelHostElement => PanelHost;

    internal CanvasSwapChainPanel Win2DPanelElement => Win2DPanel;

    internal SwapChainPanel NativePanelElement => NativePanel;

    internal void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Which path sits in the left column. The resize test runs both ways round, because the right-hand panel also moves when the window is resized.</summary>
    internal void Arrange(bool win2dOnTheRight)
    {
        Grid.SetColumn(Win2DPanel, win2dOnTheRight ? 1 : 0);
        Grid.SetColumn(Win2DHeader, win2dOnTheRight ? 1 : 0);
        Grid.SetColumn(NativePanel, win2dOnTheRight ? 0 : 1);
        Grid.SetColumn(NativeHeader, win2dOnTheRight ? 0 : 1);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= OnLoaded;
        _session = new PresentSession(_options, this);
        _session.Start();
    }
}
