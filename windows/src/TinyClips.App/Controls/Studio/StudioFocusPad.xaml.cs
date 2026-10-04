using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.Core.Studio;
using Windows.Foundation;

namespace TinyClips.App.Controls.Studio;

/// <summary>A place on the focus pad: 0 to 1 from its left edge to its right, and from its top to its bottom.</summary>
public readonly record struct StudioFocusPadPoint(double X, double Y);

/// <summary>
/// A small picture of the screen, or of its crop, with the part a zoom holds drawn as a rectangle
/// and the point it looks at as a dot. Pressing or dragging in it moves the point, and one press
/// or drag is reported as one gesture.
/// </summary>
/// <remarks>
/// <para>
/// The pad keeps no state of its own. It asks for a point through <see cref="FocusRequested"/>,
/// and whoever owns it calls <see cref="Update"/> with what the editor ended up with.
/// </para>
/// <para>
/// It takes the shape of what it stands for: as wide as it is given room for, and never higher
/// than <see cref="MaximumHeight"/>.
/// </para>
/// <para>
/// What a pointer does is in <see cref="PressAt"/>, <see cref="DragTo"/> and
/// <see cref="EndPress"/>. The pointer handlers only capture the pointer and call them.
/// </para>
/// </remarks>
public sealed partial class StudioFocusPad : UserControl
{
    private const double MaximumHeight = 160;
    private const double DefaultAspectRatio = 16.0 / 9;
    private const double DisabledOpacity = 0.4;

    private StudioZoomPad? _pad;
    private uint _pointerId;
    private bool _isPointerDown;
    private bool _isPressed;

    public StudioFocusPad()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
        IsEnabledChanged += OnIsEnabledChanged;
    }

    /// <summary>Raised when a pointer goes down on the pad. Everything until it lets go is one change.</summary>
    public event EventHandler? GestureStarted;

    /// <summary>Raised when the pointer lets go of the pad.</summary>
    public event EventHandler? GestureCompleted;

    /// <summary>Raised with the place on the pad the zoom is asked to look at.</summary>
    public event EventHandler<StudioFocusPadPoint>? FocusRequested;

    /// <summary>Shows what a zoom holds and where it looks. Null shows an empty pad.</summary>
    public void Update(StudioZoomPad? pad)
    {
        var shapeChanged = GetAspectRatio(pad) != GetAspectRatio(_pad);
        _pad = pad;
        if (shapeChanged)
        {
            InvalidateMeasure();
        }

        PlaceParts();
    }

    /// <summary>A pointer went down at a place on the pad, in the pad's own coordinates.</summary>
    internal void PressAt(double x, double y)
    {
        if (_isPressed || !IsEnabled || _pad is null)
        {
            return;
        }

        _isPressed = true;
        GestureStarted?.Invoke(this, EventArgs.Empty);
        Request(x, y);
    }

    /// <summary>The pointer that went down moved to a place on the pad, or past its edge.</summary>
    internal void DragTo(double x, double y)
    {
        if (_isPressed)
        {
            Request(x, y);
        }
    }

    /// <summary>The pointer was let go, or taken away.</summary>
    internal void EndPress()
    {
        if (_isPressed)
        {
            _isPressed = false;
            GestureCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Worked out here because the inspector scrolls, and where there is no limit on the
        // height a shape that only fits its width would grow as high as that width allows.
        var ratio = GetAspectRatio(_pad);
        var room = double.IsFinite(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width : MaximumHeight * ratio;
        var height = Math.Min(MaximumHeight, room / ratio);
        var size = new Size(height * ratio, height);
        base.MeasureOverride(size);
        return size;
    }

    private static double GetAspectRatio(StudioZoomPad? pad) =>
        pad is { AspectRatio: > 0 } shown && double.IsFinite(shown.AspectRatio) ? shown.AspectRatio : DefaultAspectRatio;

    private void OnUnloaded(object sender, RoutedEventArgs e) => ReleasePointer();

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) => PlaceParts();

    // The parts keep their accent color, so while the editor is disabled they are dimmed instead.
    private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        Parts.Opacity = IsEnabled ? 1 : DisabledOpacity;

    private void PlaceParts()
    {
        var width = Root.ActualWidth;
        var height = Root.ActualHeight;
        if (_pad is not { } pad || !(width > 0) || !(height > 0))
        {
            Parts.Visibility = Visibility.Collapsed;
            return;
        }

        Parts.Visibility = Visibility.Visible;
        var left = Clamp(pad.Window.X) * width;
        var top = Clamp(pad.Window.Y) * height;
        var windowWidth = Math.Max(0, Math.Min(pad.Window.Width * width, width - left));
        var windowHeight = Math.Max(0, Math.Min(pad.Window.Height * height, height - top));
        foreach (var rectangle in new[] { WindowFill, WindowOutline })
        {
            rectangle.Width = windowWidth;
            rectangle.Height = windowHeight;
            Canvas.SetLeft(rectangle, left);
            Canvas.SetTop(rectangle, top);
        }

        Canvas.SetLeft(FocusDot, (Clamp(pad.FocusX) * width) - (FocusDot.Width / 2));
        Canvas.SetTop(FocusDot, (Clamp(pad.FocusY) * height) - (FocusDot.Height / 2));
    }

    private void Request(double x, double y)
    {
        var width = Root.ActualWidth;
        var height = Root.ActualHeight;
        if (width > 0 && height > 0)
        {
            FocusRequested?.Invoke(this, new StudioFocusPadPoint(Clamp(x / width), Clamp(y / height)));
        }
    }

    private static double Clamp(double value) => double.IsFinite(value) ? Math.Min(Math.Max(value, 0), 1) : 0;

    // The pointer

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (_isPointerDown || !IsPrimaryPress(e, point) || !Root.CapturePointer(e.Pointer))
        {
            return;
        }

        _isPointerDown = true;
        _pointerId = e.Pointer.PointerId;
        PressAt(point.Position.X, point.Position.Y);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isPointerDown && e.Pointer.PointerId == _pointerId)
        {
            var position = e.GetCurrentPoint(Root).Position;
            DragTo(position.X, position.Y);
            e.Handled = true;
        }
    }

    // Letting go, losing the pointer and a cancelled press all end here.
    private void OnPointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (_isPointerDown && e.Pointer.PointerId == _pointerId)
        {
            ReleasePointer();
        }
    }

    private void ReleasePointer()
    {
        if (_isPointerDown)
        {
            _isPointerDown = false;
            Root.ReleasePointerCaptures();
        }

        EndPress();
    }

    private static bool IsPrimaryPress(PointerRoutedEventArgs e, PointerPoint point) =>
        e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || point.Properties.IsLeftButtonPressed;
}
