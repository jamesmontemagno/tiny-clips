using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio;
using Windows.Foundation;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// Lies over the Studio preview, at exactly the canvas' rectangle, and holds the handle that moves
/// the camera bubble. The handle follows the bubble, shows a dashed outline under the pointer and
/// while it is dragged, and makes one drag one undo step.
/// </summary>
/// <remarks>
/// The handle is for the pointer only. It is not a tab stop and screen readers do not see it: the
/// Position and offset controls in the inspector make the same change from the keyboard.
/// </remarks>
public sealed partial class StudioPreviewOverlay : UserControl
{
    private const double MinimumHandleSize = 16;

    private readonly StudioViewModel _viewModel;
    private StudioFrameRect _dragStartBubble;
    private Point _pressPoint;
    private uint _pointerId;
    private bool _isPointerOver;
    private bool _isPressed;
    private bool _isDragging;

    public StudioPreviewOverlay(StudioViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    /// <summary>Canvas pixels per effective pixel. The overlay is as wide as the canvas is drawn.</summary>
    private double CanvasPixelsPerDip =>
        ActualWidth > 0 && _viewModel.CanvasWidth > 0 ? _viewModel.CanvasWidth / ActualWidth : 0;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateHandle();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        EndPress();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateHandle();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(StudioViewModel.BubbleRect))
        {
            UpdateHandle();
        }
    }

    private void UpdateHandle()
    {
        var pixelsPerDip = CanvasPixelsPerDip;
        if (_viewModel.BubbleRect is not { } bubble || !(pixelsPerDip > 0))
        {
            // Hiding the handle also takes the pointer away from it, which ends a drag.
            BubbleHandle.Visibility = Visibility.Collapsed;
            return;
        }

        BubbleHandle.Width = Math.Max(MinimumHandleSize, bubble.Width / pixelsPerDip);
        BubbleHandle.Height = Math.Max(MinimumHandleSize, bubble.Height / pixelsPerDip);
        Canvas.SetLeft(BubbleHandle, bubble.X / pixelsPerDip);
        Canvas.SetTop(BubbleHandle, bubble.Y / pixelsPerDip);
        BubbleHandle.Visibility = Visibility.Visible;
    }

    private void UpdateOutline() => BubbleOutline.Opacity = _isPointerOver || _isPressed ? 1 : 0;

    private void OnHandlePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        UpdateOutline();
    }

    private void OnHandlePointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        UpdateOutline();
    }

    private void OnHandlePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isPressed || !_viewModel.IsEditable || _viewModel.BubbleRect is not { } bubble)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (!BubbleHandle.CapturePointer(e.Pointer))
        {
            return;
        }

        _isPressed = true;
        _pointerId = e.Pointer.PointerId;
        _pressPoint = point.Position;
        _dragStartBubble = bubble;
        UpdateOutline();
        e.Handled = true;
    }

    private void OnHandlePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPressed || e.Pointer.PointerId != _pointerId)
        {
            return;
        }

        // Measured in the overlay's space, which stays still while the handle moves.
        var position = e.GetCurrentPoint(this).Position;
        var deltaX = position.X - _pressPoint.X;
        var deltaY = position.Y - _pressPoint.Y;
        if (!_isDragging)
        {
            // A press that does not move is not a drag, and makes no undo step.
            if (deltaX * deltaX + deltaY * deltaY < 1)
            {
                return;
            }

            _isDragging = true;
            _viewModel.BeginGesture();
        }

        var pixelsPerDip = CanvasPixelsPerDip;
        _viewModel.MoveBubbleTopLeft(
            _dragStartBubble.X + deltaX * pixelsPerDip,
            _dragStartBubble.Y + deltaY * pixelsPerDip);
        e.Handled = true;
    }

    private void OnHandlePointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (_isPressed && e.Pointer.PointerId == _pointerId)
        {
            EndPress();
        }
    }

    private void EndPress()
    {
        if (!_isPressed)
        {
            return;
        }

        _isPressed = false;
        BubbleHandle.ReleasePointerCaptures();
        if (_isDragging)
        {
            _isDragging = false;
            _viewModel.EndGesture();
        }

        UpdateOutline();
    }
}
