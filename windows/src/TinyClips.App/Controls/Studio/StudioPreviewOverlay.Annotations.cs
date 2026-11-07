using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TinyClips.Core.Studio;
using Windows.Foundation;

namespace TinyClips.App.Controls.Studio;

// Pressing an annotation on the preview selects it and dragging moves it, and the selected one shows
// its handles: an arrow's tip and tail, a callout's tip, and an area's four corners, which resize it. Only for the pointer, like the camera's handle: the sliders under Advanced in the
// Annotate panel do the same from the keyboard. The same on the Mac (StudioAnnotationPreviewLayer).
public sealed partial class StudioPreviewOverlay
{
    private const double AnnotationHandleSize = 12;
    private const double AnnotationHandleTarget = 22;
    private const double ShortestArrowHitWidth = 14;
    private const double AnnotationDragStart = 2;

    private readonly SolidColorBrush _hitBrush = new(Colors.Transparent);
    private AnnotationGrip? _pressedAnnotation;
    private StudioAnnotation? _annotationAtPress;
    private Point _annotationPressPoint;
    private uint _annotationPointerId;
    private bool _isDraggingAnnotation;

    /// <summary>What a pointer has taken hold of. The layer is filled again as the project changes, so it holds on to this and not to the element.</summary>
    private sealed record AnnotationGrip(int Index, StudioAnnotationPart Part, StudioPoint? DrawnBoxCenter = null);

    private void UpdateAnnotations()
    {
        var layer = AnnotationLayer.Children;
        var pixelsPerDip = CanvasPixelsPerDip;
        IReadOnlyList<StudioAnnotationHandle> handles = pixelsPerDip > 0 ? _viewModel.AnnotationHandles : [];
        if (handles.Count == 0 && layer.Count == 0)
        {
            return;
        }

        layer.Clear();
        foreach (var handle in handles)
        {
            layer.Add(CreateHitArea(handle, pixelsPerDip));
        }

        foreach (var handle in handles)
        {
            if (handle.Index == _viewModel.SelectedAnnotationIndex)
            {
                AddSelection(handle, pixelsPerDip);
            }
        }
    }

    private FrameworkElement CreateHitArea(StudioAnnotationHandle handle, double pixelsPerDip)
    {
        FrameworkElement area;
        if (handle.Tail is { } tail)
        {
            area = new Line
            {
                X1 = tail.X / pixelsPerDip,
                Y1 = tail.Y / pixelsPerDip,
                X2 = handle.Tip.X / pixelsPerDip,
                Y2 = handle.Tip.Y / pixelsPerDip,
                Stroke = _hitBrush,
                StrokeThickness = Math.Max(ShortestArrowHitWidth, handle.Stroke * 3 / pixelsPerDip),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            return Grip(area, new AnnotationGrip(handle.Index, StudioAnnotationPart.Body));
        }

        var box = handle.Box.GetValueOrDefault();
        area = new Rectangle { Width = box.Width / pixelsPerDip, Height = box.Height / pixelsPerDip, Fill = _hitBrush };
        Canvas.SetLeft(area, box.X / pixelsPerDip);
        Canvas.SetTop(area, box.Y / pixelsPerDip);
        var center = new StudioPoint(box.X + (box.Width / 2), box.Y + (box.Height / 2));
        return Grip(area, new AnnotationGrip(handle.Index, StudioAnnotationPart.Box, center));
    }

    private void AddSelection(StudioAnnotationHandle handle, double pixelsPerDip)
    {
        if (handle.Box is { } box)
        {
            var outline = new Rectangle
            {
                Width = (box.Width / pixelsPerDip) + 4,
                Height = (box.Height / pixelsPerDip) + 4,
                RadiusX = (handle.Radius / pixelsPerDip) + 2,
                RadiusY = (handle.Radius / pixelsPerDip) + 2,
                Stroke = AnnotationGuideVertical.Fill,
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(outline, (box.X / pixelsPerDip) - 2);
            Canvas.SetTop(outline, (box.Y / pixelsPerDip) - 2);
            AnnotationLayer.Children.Add(outline);
        }

        if (StudioAnnotationKinds.IsArea(handle.Kind) && handle.Box is { } area)
        {
            // An area is moved by its inside, and each corner resizes it while the opposite one stays.
            var (left, top, right, bottom) = (area.X, area.Y, area.X + area.Width, area.Y + area.Height);
            AnnotationLayer.Children.Add(CreateHandle(new StudioPoint(left, top), new AnnotationGrip(handle.Index, StudioAnnotationPart.TopLeft), pixelsPerDip));
            AnnotationLayer.Children.Add(CreateHandle(new StudioPoint(right, top), new AnnotationGrip(handle.Index, StudioAnnotationPart.TopRight), pixelsPerDip));
            AnnotationLayer.Children.Add(CreateHandle(new StudioPoint(left, bottom), new AnnotationGrip(handle.Index, StudioAnnotationPart.BottomLeft), pixelsPerDip));
            AnnotationLayer.Children.Add(CreateHandle(new StudioPoint(right, bottom), new AnnotationGrip(handle.Index, StudioAnnotationPart.BottomRight), pixelsPerDip));
            return;
        }

        if (handle.Kind is not (StudioAnnotationKinds.Text or StudioAnnotationKinds.Emoji))
        {
            AnnotationLayer.Children.Add(CreateHandle(handle.Tip, new AnnotationGrip(handle.Index, StudioAnnotationPart.Tip), pixelsPerDip));
        }

        if (handle.Tail is { } tail)
        {
            AnnotationLayer.Children.Add(CreateHandle(tail, new AnnotationGrip(handle.Index, StudioAnnotationPart.Tail), pixelsPerDip));
        }
    }

    private Grid CreateHandle(StudioPoint point, AnnotationGrip grip, double pixelsPerDip)
    {
        var target = new Grid { Width = AnnotationHandleTarget, Height = AnnotationHandleTarget, Background = _hitBrush };
        target.Children.Add(new Ellipse
        {
            Width = AnnotationHandleSize,
            Height = AnnotationHandleSize,
            Fill = new SolidColorBrush(Colors.White),
            Stroke = AnnotationGuideVertical.Fill,
            StrokeThickness = 2,
            IsHitTestVisible = false,
        });
        Canvas.SetLeft(target, (point.X / pixelsPerDip) - (AnnotationHandleTarget / 2));
        Canvas.SetTop(target, (point.Y / pixelsPerDip) - (AnnotationHandleTarget / 2));
        return (Grid)Grip(target, grip);
    }

    private FrameworkElement Grip(FrameworkElement element, AnnotationGrip grip)
    {
        element.Tag = grip;
        AutomationProperties.SetAccessibilityView(element, AccessibilityView.Raw);
        element.PointerEntered += OnAnnotationPointerEntered;
        element.PointerExited += OnAnnotationPointerExited;
        return element;
    }

    private void OnAnnotationPointerEntered(object sender, PointerRoutedEventArgs e) =>
        ProtectedCursor = InputSystemCursor.Create((sender as FrameworkElement)?.Tag is AnnotationGrip grip ? grip.Part switch
        {
            StudioAnnotationPart.TopLeft or StudioAnnotationPart.BottomRight => InputSystemCursorShape.SizeNorthwestSoutheast,
            StudioAnnotationPart.TopRight or StudioAnnotationPart.BottomLeft => InputSystemCursorShape.SizeNortheastSouthwest,
            _ => InputSystemCursorShape.SizeAll,
        } : InputSystemCursorShape.SizeAll);

    private void OnAnnotationPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedAnnotation is null)
        {
            ProtectedCursor = null;
        }
    }

    private void OnAnnotationLayerPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (_pressedAnnotation is not null || !_viewModel.IsEditable || e.OriginalSource is not FrameworkElement { Tag: AnnotationGrip grip }
            || (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed)
            || !AnnotationLayer.CapturePointer(e.Pointer))
        {
            return;
        }

        _pressedAnnotation = grip;
        _annotationPointerId = e.Pointer.PointerId;
        _annotationPressPoint = point.Position;
        _annotationAtPress = _viewModel.BeginAnnotationDrag(grip.Index);
        e.Handled = true;
    }

    private void OnAnnotationLayerPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedAnnotation is not { } grip || _annotationAtPress is not { } start || e.Pointer.PointerId != _annotationPointerId)
        {
            return;
        }

        var position = e.GetCurrentPoint(this).Position;
        var deltaX = position.X - _annotationPressPoint.X;
        var deltaY = position.Y - _annotationPressPoint.Y;
        if (!_isDraggingAnnotation)
        {
            // A press that does not move only selects, and makes no undo step.
            if ((deltaX * deltaX) + (deltaY * deltaY) < AnnotationDragStart * AnnotationDragStart)
            {
                return;
            }

            _isDraggingAnnotation = true;
            _viewModel.BeginGesture();
        }

        var pixelsPerDip = CanvasPixelsPerDip;
        if (_viewModel.DragAnnotation(grip.Index, start, grip.Part, deltaX * pixelsPerDip, deltaY * pixelsPerDip, grip.DrawnBoxCenter) is { } move)
        {
            ShowAnnotationGuides(move.IsOnVerticalCenter, move.IsOnHorizontalCenter);
        }

        e.Handled = true;
    }

    private void OnAnnotationLayerPointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedAnnotation is not null && e.Pointer.PointerId == _annotationPointerId)
        {
            EndAnnotationPress();
        }
    }

    private void EndAnnotationPress()
    {
        if (_pressedAnnotation is null)
        {
            return;
        }

        _pressedAnnotation = null;
        _annotationAtPress = null;
        AnnotationLayer.ReleasePointerCaptures();
        ShowAnnotationGuides(false, false);
        ProtectedCursor = null;
        if (_isDraggingAnnotation)
        {
            _isDraggingAnnotation = false;
            _viewModel.EndGesture();
        }
    }

    /// <summary>The center lines of the canvas, while what is dragged has settled on one.</summary>
    private void ShowAnnotationGuides(bool vertical, bool horizontal)
    {
        AnnotationGuideVertical.Visibility = vertical ? Visibility.Visible : Visibility.Collapsed;
        AnnotationGuideHorizontal.Visibility = horizontal ? Visibility.Visible : Visibility.Collapsed;
        AnnotationGuideVertical.Height = ActualHeight;
        AnnotationGuideHorizontal.Width = ActualWidth;
        Canvas.SetLeft(AnnotationGuideVertical, ActualWidth / 2);
        Canvas.SetTop(AnnotationGuideHorizontal, ActualHeight / 2);
    }
}
