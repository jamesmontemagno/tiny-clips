using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Studio.Editing;
using Windows.Foundation;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// Shows the Studio canvas inside whatever room the window gives it. Every child is arranged into
/// the same rectangle: the largest one with the canvas' shape that fits, centered. The preview goes
/// in first and the overlay with the camera handle on top of it.
/// </summary>
/// <remarks>
/// The rectangle is snapped to whole physical pixels, because a swap chain placed on a fraction of
/// a pixel is resampled and looks soft. The snapping is relative to this panel, whose own position
/// the layout system rounds to whole pixels as long as layout rounding is left on.
/// </remarks>
public sealed partial class StudioCanvasHost : Panel
{
    /// <summary>The canvas width divided by its height: the export width over the export height.</summary>
    public static readonly DependencyProperty CanvasAspectRatioProperty = DependencyProperty.Register(
        nameof(CanvasAspectRatio),
        typeof(double),
        typeof(StudioCanvasHost),
        new PropertyMetadata(16.0 / 9, OnCanvasAspectRatioChanged));

    private XamlRoot? _xamlRoot;
    private double _rasterizationScale = 1;

    public StudioCanvasHost()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public double CanvasAspectRatio
    {
        get => (double)GetValue(CanvasAspectRatioProperty);
        set => SetValue(CanvasAspectRatioProperty, value);
    }

    /// <summary>Where the canvas was last arranged, in effective pixels relative to this panel.</summary>
    public Rect CanvasRect { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        // With unlimited room there is no largest rectangle, so the panel asks for none. The window
        // always gives it a finite cell.
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        var height = double.IsFinite(availableSize.Height) ? availableSize.Height : 0;
        var canvas = Fit(width, height);
        foreach (var child in Children)
        {
            child.Measure(new Size(canvas.Width, canvas.Height));
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        CanvasRect = Fit(finalSize.Width, finalSize.Height);
        foreach (var child in Children)
        {
            child.Arrange(CanvasRect);
        }

        return finalSize;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StudioCanvasHostAutomationPeer(this);

    private Rect Fit(double width, double height)
    {
        var rect = StudioPreviewLayout.FitCanvas(width, height, CanvasAspectRatio, _rasterizationScale);
        return new Rect(rect.X, rect.Y, rect.Width, rect.Height);
    }

    private static void OnCanvasAspectRatioChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var host = (StudioCanvasHost)sender;
        host.InvalidateMeasure();
        host.InvalidateArrange();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _xamlRoot = XamlRoot;
        if (_xamlRoot is not null)
        {
            _xamlRoot.Changed += OnXamlRootChanged;
            UpdateRasterizationScale(_xamlRoot.RasterizationScale);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_xamlRoot is not null)
        {
            _xamlRoot.Changed -= OnXamlRootChanged;
            _xamlRoot = null;
        }
    }

    // The window moved to a display with another scale: the pixel grid is a different one now.
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) =>
        UpdateRasterizationScale(sender.RasterizationScale);

    private void UpdateRasterizationScale(double scale)
    {
        if (scale > 0 && scale != _rasterizationScale)
        {
            _rasterizationScale = scale;
            InvalidateMeasure();
            InvalidateArrange();
        }
    }
}

/// <summary>
/// Presents the canvas to screen readers as one image. Its name and description are set by the
/// window; the camera handle on top of it is a pointer affordance and stays out of the tree.
/// </summary>
public sealed partial class StudioCanvasHostAutomationPeer(StudioCanvasHost owner) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

    protected override string GetClassNameCore() => nameof(StudioCanvasHost);

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;
}
