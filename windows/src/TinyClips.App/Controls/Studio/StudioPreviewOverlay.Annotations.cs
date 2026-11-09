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
    private sealed record AnnotationGrip(int Index, StudioAnnotationPart Part, StudioPoint? DrawnBoxCenter = null,
        StudioPoint? Anchor = null, StudioPoint? HandlePoint = null, double Rotation = 0);

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
            tail = StudioAnnotationGeometry.Rotate(tail, handle.Tip, handle.Rotation);
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
        area.RenderTransform = new RotateTransform
        {
            Angle = handle.Rotation,
            CenterX = (handle.Tip.X - box.X) / pixelsPerDip,
            CenterY = (handle.Tip.Y - box.Y) / pixelsPerDip,
        };
        var center = new StudioPoint(box.X + (box.Width / 2), box.Y + (box.Height / 2));
        return Grip(area, new AnnotationGrip(handle.Index, StudioAnnotationPart.Box, center, Rotation: handle.Rotation));
    }

    private void AddSelection(StudioAnnotationHandle handle, double pixelsPerDip)
    {
        var box = StudioAnnotationGeometry.SelectionBounds(handle);
        var corners = new[]
        {
            new StudioPoint(box.X, box.Y), new StudioPoint(box.X + box.Width, box.Y),
            new StudioPoint(box.X + box.Width, box.Y + box.Height), new StudioPoint(box.X, box.Y + box.Height),
        };
        var parts = new[] { StudioAnnotationPart.TopLeft, StudioAnnotationPart.TopRight, StudioAnnotationPart.BottomRight, StudioAnnotationPart.BottomLeft };
        var outline = new Polygon { Stroke = AnnotationGuideVertical.Fill, StrokeThickness = 1.5, IsHitTestVisible = false };
        for (var i = 0; i < corners.Length; i++)
        {
            var point = StudioAnnotationGeometry.Rotate(corners[i], handle.Tip, handle.Rotation);
            outline.Points.Add(new Point(point.X / pixelsPerDip, point.Y / pixelsPerDip));
            AnnotationLayer.Children.Add(CreateHandle(point, new AnnotationGrip(handle.Index, parts[i],
                Anchor: handle.Tip, HandlePoint: point, Rotation: handle.Rotation), pixelsPerDip));
        }

        AnnotationLayer.Children.Add(outline);
        var top = StudioAnnotationGeometry.Rotate(new(box.X + box.Width / 2, box.Y), handle.Tip, handle.Rotation);
        var rotate = StudioAnnotationGeometry.Rotate(new(box.X + box.Width / 2, box.Y - 28 * pixelsPerDip), handle.Tip, handle.Rotation);
        AnnotationLayer.Children.Add(new Line
        {
            X1 = top.X / pixelsPerDip, Y1 = top.Y / pixelsPerDip,
            X2 = rotate.X / pixelsPerDip, Y2 = rotate.Y / pixelsPerDip,
            Stroke = AnnotationGuideVertical.Fill, StrokeThickness = 1.5, IsHitTestVisible = false,
        });
        AnnotationLayer.Children.Add(CreateHandle(rotate, new AnnotationGrip(handle.Index, StudioAnnotationPart.Rotate,
            Anchor: handle.Tip, HandlePoint: rotate), pixelsPerDip));

        if (handle.Kind is StudioAnnotationKinds.Arrow or StudioAnnotationKinds.Callout)
        {
            AnnotationLayer.Children.Add(CreateHandle(handle.Tip, new AnnotationGrip(handle.Index, StudioAnnotationPart.Tip), pixelsPerDip));
        }

        if (handle.Tail is { } tail)
        {
            tail = StudioAnnotationGeometry.Rotate(tail, handle.Tip, handle.Rotation);
            AnnotationLayer.Children.Add(CreateHandle(tail, new AnnotationGrip(handle.Index, StudioAnnotationPart.Tail, Rotation: handle.Rotation), pixelsPerDip));
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
        AutomationProperties.SetAutomationId(target, $"StudioAnnotation{grip.Part}Handle");
        ToolTipService.SetToolTip(target, grip.Part == StudioAnnotationPart.Rotate ? "Drag to rotate" :
            grip.Part is StudioAnnotationPart.TopLeft or StudioAnnotationPart.TopRight or StudioAnnotationPart.BottomLeft or StudioAnnotationPart.BottomRight
                ? "Drag to resize" : "Drag to move");
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
            StudioAnnotationPart.Rotate => InputSystemCursorShape.Cross,
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
        if (grip.Anchor is { } anchor && grip.HandlePoint is { } pressed &&
            (grip.Part == StudioAnnotationPart.Rotate || !StudioAnnotationKinds.IsArea(start.Kind)))
        {
            _viewModel.TransformAnnotation(grip.Index, start, grip.Part, anchor, pressed,
                new(pressed.X + deltaX * pixelsPerDip, pressed.Y + deltaY * pixelsPerDip));
            ShowAnnotationGuides(false, false);
            e.Handled = true;
            return;
        }

        var delta = new StudioPoint(deltaX * pixelsPerDip, deltaY * pixelsPerDip);
        if (grip.Part == StudioAnnotationPart.Tail || (grip.Part == StudioAnnotationPart.Box && !StudioAnnotationKinds.IsArea(start.Kind)))
        {
            delta = StudioAnnotationGeometry.Rotate(delta, default, -grip.Rotation);
        }

        if (_viewModel.DragAnnotation(grip.Index, start, grip.Part, delta.X, delta.Y, grip.DrawnBoxCenter) is { } move)
        {
            ShowAnnotationGuides(grip.Rotation == 0 && move.IsOnVerticalCenter, grip.Rotation == 0 && move.IsOnHorizontalCenter);
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
