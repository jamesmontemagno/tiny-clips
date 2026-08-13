using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Devices.Input;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.UI;
using ShapesPath = Microsoft.UI.Xaml.Shapes.Path;

namespace TinyClips.App.ScreenshotEditor;

/// <summary>
/// The editor's drawing surface: hosts the live preview image, the crop-selection rectangle, and
/// every annotation visual. This is the performance-critical control — annotation visuals are
/// retained across pointer-move events (created once, then mutated in place) instead of being torn
/// down and rebuilt on every move, which is what <c>ScreenshotEditorWindow</c> used to do.
/// </summary>
public sealed partial class EditorCanvas : UserControl
{
    private const float MinZoomFactor = 0.25f;
    private const float MaxZoomFactor = 4.0f;
    private static readonly float[] ZoomPresets = [0.25f, 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f, 3.0f, 4.0f];
    private static readonly InputSystemCursor MoveCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private static readonly InputSystemCursor ArrowCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private static readonly ScrollingZoomOptions ZoomOptions =
        new(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore);
    private static readonly ScrollingScrollOptions ScrollOptions =
        new(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore);

    /// <summary>A retained visual for one annotation. The element type is fixed for the
    /// annotation's lifetime except for Arrow (its Primary shaft swaps Line/Path if the style
    /// changes between straight/curved) and Redact (placeholder Rectangle swaps to Image once a
    /// blurred preview is ready). Arrow's Secondary head and brushes survive shaft swaps. Brushes
    /// are allocated once and mutated in place to avoid hot-path allocation.</summary>
    private sealed class AnnotationVisual
    {
        public UIElement Primary = null!;
        public UIElement? Secondary;
        public SolidColorBrush? StrokeBrush;
        public SolidColorBrush? FillBrush;
        public SolidColorBrush? TextBrush;

        // Pen-stroke incremental sync bookkeeping: lets a growing stroke append only the new
        // points instead of rebuilding the whole polyline every pointer move.
        public int SyncedPointCount;
        public double SyncedScale = double.NaN;
        public double SyncedOffsetX = double.NaN;
        public double SyncedOffsetY = double.NaN;
        // The first point's pixel-space value at the last sync — lets a whole-stroke translation
        // (moving a completed Pen annotation with the Select tool, which keeps the point count
        // but shifts every point by dx/dy) be detected cheaply and trigger a full point resync,
        // since the append-only fast path above would otherwise miss it (count doesn't change).
        public Vector2? SyncedFirstPoint;
    }

    private EditorController _controller = null!;
    private readonly Dictionary<Annotation, AnnotationVisual> _visuals = new();

    private bool _dragging;
    private Point _dragStart;
    private Annotation? _movingAnnotation;
    private Point _moveOffset;
    private Annotation? _resizingAnnotation;
    private AnnotationResizeHandle? _resizeHandle;
    private Rect _resizeOriginalBounds;
    private List<Vector2> _resizeOriginalPoints = new();
    private double _resizeOriginalFontSize;
    private double _resizeOriginalSizeScale;
    private Annotation? _endpointAnnotation;
    private bool _movingStartEndpoint;
    private bool _spacePressed;
    private bool _panning;
    private float _zoomFactor = 1.0f;
    private Point _panStart;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;

    // Tracks whichever pointer currently has capture (crop drag, annotation move, or annotation
    // draw) so an interrupted interaction — e.g. a tool-change keyboard shortcut firing mid-drag —
    // can release capture even though no PointerReleased event will ever arrive for it.
    private Pointer? _capturedPointer;

    public EditorCanvas()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the crop-selection rectangle becomes big enough (or too small) to
    /// apply — the window uses this to enable/disable its "Apply crop" command.</summary>
    public event EventHandler<bool>? CropSelectionAvailabilityChanged;

    public void ZoomIn()
    {
        foreach (var preset in ZoomPresets)
        {
            if (preset > _zoomFactor + 0.001f)
            {
                SetZoom(preset);
                return;
            }
        }
    }

    public void ZoomOut()
    {
        for (var i = ZoomPresets.Length - 1; i >= 0; i--)
        {
            if (ZoomPresets[i] < _zoomFactor - 0.001f)
            {
                SetZoom(ZoomPresets[i]);
                return;
            }
        }
    }

    public void Fit() => SetZoom(1.0f);

    public void SetSpacePressed(bool pressed)
    {
        _spacePressed = pressed;
        ProtectedCursor = pressed ? MoveCursor : ArrowCursor;
        if (pressed)
        {
            OverlayCanvas.Focus(FocusState.Programmatic);
        }
        if (!pressed && _panning)
        {
            CancelLocalInteraction();
        }
    }

    /// <summary>Wires this control to the shared editor state. Called once by the window right
    /// after construction (mirrors the constructor-injected shared view model used by the Settings
    /// sections, but this control is declared in XAML so it can't take a constructor argument).</summary>
    internal void Attach(EditorController controller)
    {
        _controller = controller;
        _controller.ImageChanged += OnControllerImageChanged;
        _controller.AnnotationsStructureChanged += (_, _) => FullRebuild();
        _controller.AnnotationVisualInvalidated += OnControllerAnnotationVisualInvalidated;
        _controller.SelectionChanged += (_, ann) => PositionMarquee(ann);
        _controller.ToolChanged += OnControllerToolChanged;
        _controller.ActiveAnnotationDiscarded += OnControllerActiveAnnotationDiscarded;
        _controller.BackgroundChanged += OnControllerBackgroundChanged;

        // Sync the hint text to the controller's initial tool (Crop) — mirrors the original
        // window's ctor calling SelectTool(EditTool.Crop) right after wiring everything up.
        OnControllerToolChanged(this, _controller.Tool);
    }

    // -- Controller reactions ----------------------------------------------------------------

    private void OnControllerImageChanged(object? sender, EventArgs e)
    {
        PreviewImage.Source = _controller.PreviewSource;
        ClearCropSelection();
        LayoutCanvas();
        FullRebuild();
    }

    private void OnControllerAnnotationVisualInvalidated(object? sender, Annotation ann)
    {
        // Guards against a stale async callback (e.g. a redaction blur finishing after the
        // annotation was deleted/undone) resurrecting a visual for an annotation that is no
        // longer live. An annotation is live either once committed to the list, or while it is
        // still the in-progress drag preview (not yet committed).
        if (!Contains(_controller.Annotations, ann) && !ReferenceEquals(ann, _controller.ActiveAnnotation))
        {
            return;
        }

        UpdateVisual(ann);
        if (ReferenceEquals(ann, _controller.SelectedAnnotation))
        {
            PositionMarquee(ann);
        }
    }

    private void OnControllerActiveAnnotationDiscarded(object? sender, Annotation ann)
    {
        // The controller already dropped ActiveAnnotation (and will never add this annotation
        // to Annotations/undo history) — just drop the now-orphaned retained preview visual, if
        // one was created for it, so nothing keeps rendering it.
        RemoveVisual(ann);
    }

    /// <summary>
    /// Cancels whichever pointer interaction is currently in flight: an in-progress drag
    /// annotation is discarded without ever being committed to
    /// <see cref="EditorController.Annotations"/> or the undo history, and an in-progress
    /// annotation move is stopped in place. Safe to call at any time, including when nothing is
    /// in flight — it is intentionally narrow (it never touches undo history or the annotation
    /// list itself). Used by <see cref="OnOverlayPointerCaptureLost"/>/
    /// <see cref="OnOverlayPointerCanceled"/> for involuntary pointer-capture loss, and by
    /// <see cref="ScreenshotEditorWindow"/>'s Undo/Delete commands so a still-in-flight
    /// draw/move never turns into a pointer-tracking "ghost" of an annotation the controller is
    /// about to remove.
    /// </summary>
    public void CancelActiveInteraction()
    {
        _controller.CancelActiveAnnotation();
        CancelLocalInteraction();
    }

    /// <summary>Resets this canvas's own pointer-interaction bookkeeping (capture, drag/move
    /// flags) when an interaction is interrupted rather than released normally — e.g. a tool
    /// hotkey fires mid-drag. The controller has already discarded any in-progress annotation (see
    /// <see cref="EditorController.SetTool"/> / <see cref="OnControllerActiveAnnotationDiscarded"/>);
    /// this only needs to undo this control's own state for whichever interaction was in flight.</summary>
    private void CancelLocalInteraction()
    {
        if (_capturedPointer is { } pointer)
        {
            // Null the field *before* releasing capture: ReleasePointerCapture synchronously
            // raises PointerCaptureLost on OverlayCanvas, and OnOverlayPointerCaptureLost checks
            // this field to tell "we just released capture ourselves" apart from "capture was
            // lost involuntarily" — seeing null here lets it no-op instead of recursing back into
            // this same cleanup.
            _capturedPointer = null;
            OverlayCanvas.ReleasePointerCapture(pointer);
        }

        _dragging = false;
        _panning = false;
        _resizingAnnotation = null;
        _resizeHandle = null;
        _endpointAnnotation = null;

        var moved = _movingAnnotation;
        _movingAnnotation = null;
        if (moved is { Tool: EditTool.Redact } && Contains(_controller.Annotations, moved))
        {
            // Mirrors OnPointerReleased: a moved redact block only shows the lightweight
            // placeholder while dragging; re-blur it now that the move has been interrupted.
            // Guarded on list membership because Undo/Delete cancel the interaction (see
            // ScreenshotEditorWindow) before removing the annotation — if it's already gone,
            // UpdateVisual must not resurrect a visual for it.
            UpdateVisual(moved);
        }
    }

    private void OnOverlayPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_capturedPointer is null)
        {
            // This control released capture itself (OnPointerReleased or CancelLocalInteraction
            // already nulled the field before calling ReleasePointerCapture) — that expected,
            // synchronous PointerCaptureLost has nothing left to clean up. Bailing out here is
            // what avoids double cleanup / recursion between the explicit release and this event.
            return;
        }

        // Capture was lost involuntarily — e.g. a system flyout/dialog stealing the pointer, or
        // the window losing focus mid-drag. Treat it exactly like any other interrupted
        // interaction.
        CancelActiveInteraction();
    }

    private void OnOverlayPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        // PointerCanceled (e.g. touch/pen input becoming unavailable mid-gesture) doesn't always
        // guarantee a PointerCaptureLost follows, so cancel the interaction directly here too;
        // CancelActiveInteraction/CancelLocalInteraction are idempotent when nothing is in flight.
        CancelActiveInteraction();
    }

    private void OnControllerToolChanged(object? sender, EditTool tool)
    {
        CancelLocalInteraction();
        ClearCropSelection();
        HintText.Text = tool switch
        {
            EditTool.Crop => "Drag to select an area, then choose Apply crop.",
            EditTool.Select => "Click an annotation to select it; drag inside to move or drag a handle to resize. Del removes it.",
            EditTool.Text => "Click where you want text to open the editor; double-click text to edit it.",
            EditTool.Counter => "Click to drop a numbered badge.",
            EditTool.Pen => "Drag to draw freehand.",
            EditTool.Redact => "Drag over content to redact it.",
            EditTool.Rectangle or EditTool.Ellipse => "Drag to draw. Hold Shift for a perfect shape.",
            EditTool.Line or EditTool.Arrow => "Drag in any direction. Hold Shift to snap.",
            _ => "Drag on the image to draw.",
        };
    }

    private void OnControllerBackgroundChanged(object? sender, EventArgs e)
    {
        LayoutCanvas();
        RepositionAll();
    }

    // -- Layout ------------------------------------------------------------------------------

    private void OnImageHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        LayoutCanvas();
        RepositionAll();
    }

    private void OnViewportViewChanged(ScrollView sender, object args)
    {
        _zoomFactor = sender.ZoomFactor;
        ZoomPercentageButton.Content = $"{Math.Round(sender.ZoomFactor * 100):0}%";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ZoomPercentageButton,
            $"Zoom percentage, {Math.Round(sender.ZoomFactor * 100):0}%");
    }

    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomOut();

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomIn();

    private void OnFit(object sender, RoutedEventArgs e) => Fit();

    private void OnZoomPreset(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string value }
            && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var zoom))
        {
            SetZoom(zoom);
        }
    }

    private void SetZoom(float zoom, Point? focalPoint = null)
    {
        var clamped = Math.Clamp(zoom, MinZoomFactor, MaxZoomFactor);
        _zoomFactor = clamped;
        var focal = focalPoint ?? new Point(
            ViewportScrollView.ViewportWidth / 2.0,
            ViewportScrollView.ViewportHeight / 2.0);
        ViewportScrollView.ZoomTo(
            clamped,
            new Vector2((float)focal.X, (float)focal.Y),
            ZoomOptions);
    }

    private (double Scale, double OffsetX, double OffsetY) HostLayout() =>
        _controller.ImageLayout(ImageHost.ActualWidth, ImageHost.ActualHeight);

    private void LayoutCanvas()
    {
        if (_controller.Bitmap is null)
        {
            return;
        }

        double imgW = _controller.Bitmap.PixelWidth;
        double imgH = _controller.Bitmap.PixelHeight;
        var (scale, imageOffX, imageOffY) = HostLayout();
        var frame = _controller.GetExportFrameLayout();
        var frameOffX = imageOffX - frame.ImageBounds.X * scale;
        var frameOffY = imageOffY - frame.ImageBounds.Y * scale;

        var showBackground = _controller.BgStyle != ExportBackgroundStyle.Transparent;
        CanvasBackground.Visibility = showBackground ? Visibility.Visible : Visibility.Collapsed;
        if (showBackground)
        {
            Canvas.SetLeft(CanvasBackground, frameOffX);
            Canvas.SetTop(CanvasBackground, frameOffY);
            CanvasBackground.Width = frame.FrameSize.Width * scale;
            CanvasBackground.Height = frame.FrameSize.Height * scale;
            CanvasBackground.Background = MakeBackgroundBrush();
        }

        Canvas.SetLeft(ImageCard, imageOffX);
        Canvas.SetTop(ImageCard, imageOffY);
        ImageCard.Width = imgW * scale;
        ImageCard.Height = imgH * scale;
        var scaledCornerRadius = Math.Min(
            _controller.CanvasCornerRadius * scale,
            Math.Min(ImageCard.Width, ImageCard.Height) / 2.0);
        ImageCard.CornerRadius = new CornerRadius(Math.Max(0, scaledCornerRadius));
        ImageCard.Translation = new Vector3(0, 0, (float)(_controller.CanvasShadow > 0 ? Math.Max(8, _controller.CanvasShadow) : 0));
    }

    private Brush MakeBackgroundBrush()
    {
        if (_controller.BgStyle == ExportBackgroundStyle.Gradient)
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            brush.GradientStops.Add(new GradientStop { Color = _controller.BgColor, Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = _controller.BgColor2, Offset = 1 });
            return brush;
        }
        return new SolidColorBrush(_controller.BgColor);
    }

    // -- Retained-visual rebuild / reposition ------------------------------------------------

    /// <summary>Structural pass: reconcile the visual set against the annotation list (add
    /// missing, remove stale) then reposition everything. Only runs for add/delete/undo/crop/reset
    /// — never on pointer move.</summary>
    private void FullRebuild()
    {
        if (_visuals.Count > 0)
        {
            var live = new HashSet<Annotation>(_controller.Annotations);
            List<Annotation>? stale = null;
            foreach (var key in _visuals.Keys)
            {
                if (!live.Contains(key))
                {
                    (stale ??= new List<Annotation>()).Add(key);
                }
            }
            if (stale is not null)
            {
                foreach (var ann in stale)
                {
                    RemoveVisual(ann);
                }
            }
        }

        RepositionAll();
    }

    private void RepositionAll()
    {
        foreach (var ann in _controller.Annotations)
        {
            UpdateVisual(ann);
        }
        PositionMarquee(_controller.SelectedAnnotation);
    }

    private void RemoveVisual(Annotation ann)
    {
        if (_visuals.Remove(ann, out var visual))
        {
            OverlayCanvas.Children.Remove(visual.Primary);
            if (visual.Secondary is not null)
            {
                OverlayCanvas.Children.Remove(visual.Secondary);
            }
        }
    }

    /// <summary>Targeted update for one annotation: creates its visual on first use, otherwise
    /// mutates the existing element(s) in place. This is the hot path invoked on every pointer-move
    /// while drawing/dragging/moving an annotation.</summary>
    private void UpdateVisual(Annotation ann)
    {
        var exists = _visuals.TryGetValue(ann, out var visual);

        if (exists && ann.Tool == EditTool.Arrow)
        {
            EnsureArrowShaft(ann, visual!);
        }

        if (exists && ann.Tool == EditTool.Redact)
        {
            var isMoving = ReferenceEquals(ann, _movingAnnotation);
            if (!isMoving)
            {
                _controller.EnsureRedactPreview(ann);
            }
            var wantsImage = !isMoving && !ReferenceEquals(ann, _controller.ActiveAnnotation) && ann.RedactPreview is not null;
            var isImage = visual!.Primary is Image;
            if (wantsImage != isImage)
            {
                RemoveVisual(ann);
                exists = false;
            }
        }

        var v = exists ? visual! : GetOrCreateVisual(ann);
        ApplyZOrder(ann, v);
        PositionVisual(ann, v);
    }

    /// <summary>
    /// Replaces only an arrow's polymorphic shaft when its style crosses the straight/curved
    /// boundary. The retained head and brushes stay intact, and preserving the old child index
    /// keeps the head after (and therefore above) the shaft when their ZIndex values tie.
    /// </summary>
    private void EnsureArrowShaft(Annotation ann, AnnotationVisual visual)
    {
        var wantsCurved = ann.ArrowStyle != ArrowStyle.Straight;
        if ((wantsCurved && visual.Primary is ShapesPath)
            || (!wantsCurved && visual.Primary is Line))
        {
            return;
        }

        var oldShaft = visual.Primary;
        var childIndex = OverlayCanvas.Children.IndexOf(oldShaft);
        if (childIndex >= 0)
        {
            OverlayCanvas.Children.RemoveAt(childIndex);
        }
        else if (visual.Secondary is { } head)
        {
            childIndex = OverlayCanvas.Children.IndexOf(head);
        }

        var newShaft = CreateArrowShaft(wantsCurved, visual.StrokeBrush!);
        if (childIndex >= 0)
        {
            OverlayCanvas.Children.Insert(childIndex, newShaft);
        }
        else
        {
            OverlayCanvas.Children.Add(newShaft);
        }
        visual.Primary = newShaft;
    }

    /// <summary>
    /// Assigns a stable <c>Canvas.ZIndex</c> derived from this annotation's position in the
    /// controller's ordered <see cref="EditorController.Annotations"/> list (or, for the
    /// not-yet-committed drag preview, just above the topmost committed annotation). Recreating a
    /// visual — an arrow's shaft swapping Line/Path between straight/curved, or a redaction's
    /// placeholder Rectangle swapping for its blurred Image — changes elements in
    /// <see cref="OverlayCanvas"/>'s Children collection. Pinning ZIndex to list order keeps preview
    /// stacking stable and matching export/undo order no matter how many times an element is
    /// replaced. The fixed crop-selection and selection-marquee adorners are pinned to a much
    /// higher ZIndex in XAML so they always stay above every annotation visual.
    /// </summary>
    private void ApplyZOrder(Annotation ann, AnnotationVisual visual)
    {
        var z = AnnotationZIndex(ann);
        Canvas.SetZIndex(visual.Primary, z);
        if (visual.Secondary is not null)
        {
            // Same ZIndex as Primary: ties are broken by Children collection order, and Primary
            // is always added before Secondary (see CreateVisual), so e.g. an arrow's head stays
            // above its own shaft even after the shaft is replaced.
            Canvas.SetZIndex(visual.Secondary, z);
        }
    }

    private int AnnotationZIndex(Annotation ann)
    {
        var list = _controller.Annotations;
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], ann))
            {
                return i;
            }
        }

        // Not committed yet — this is the in-progress drag preview (ActiveAnnotation). Keep it
        // above every committed annotation so the shape currently being drawn is always visible.
        return list.Count;
    }

    private AnnotationVisual GetOrCreateVisual(Annotation ann)
    {
        var visual = CreateVisual(ann);
        _visuals[ann] = visual;
        return visual;
    }

    private AnnotationVisual CreateVisual(Annotation ann)
    {
        switch (ann.Tool)
        {
            case EditTool.Rectangle:
            {
                var stroke = new SolidColorBrush();
                var fill = new SolidColorBrush();
                var rect = new Rectangle { Stroke = stroke, Fill = fill };
                OverlayCanvas.Children.Add(rect);
                return new AnnotationVisual { Primary = rect, StrokeBrush = stroke, FillBrush = fill };
            }
            case EditTool.Ellipse:
            {
                var stroke = new SolidColorBrush();
                var fill = new SolidColorBrush();
                var ellipse = new Ellipse { Stroke = stroke, Fill = fill };
                OverlayCanvas.Children.Add(ellipse);
                return new AnnotationVisual { Primary = ellipse, StrokeBrush = stroke, FillBrush = fill };
            }
            case EditTool.Line:
            {
                var stroke = new SolidColorBrush();
                var line = new Line { Stroke = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                OverlayCanvas.Children.Add(line);
                return new AnnotationVisual { Primary = line, StrokeBrush = stroke };
            }
            case EditTool.Arrow:
            {
                var stroke = new SolidColorBrush();
                var fill = new SolidColorBrush();
                var shaft = CreateArrowShaft(ann.ArrowStyle != ArrowStyle.Straight, stroke);
                var head = new Polygon { Fill = fill };
                OverlayCanvas.Children.Add(shaft);
                OverlayCanvas.Children.Add(head);
                return new AnnotationVisual { Primary = shaft, Secondary = head, StrokeBrush = stroke, FillBrush = fill };
            }
            case EditTool.Pen:
            {
                var stroke = new SolidColorBrush();
                var poly = new Polyline
                {
                    Stroke = stroke,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                OverlayCanvas.Children.Add(poly);
                return new AnnotationVisual { Primary = poly, StrokeBrush = stroke };
            }
            case EditTool.Redact:
            {
                var isMoving = ReferenceEquals(ann, _movingAnnotation);
                var wantsImage = !isMoving && !ReferenceEquals(ann, _controller.ActiveAnnotation) && ann.RedactPreview is not null;
                if (wantsImage)
                {
                    var img = new Image { Stretch = Stretch.Fill, Source = ann.RedactPreview };
                    OverlayCanvas.Children.Add(img);
                    return new AnnotationVisual { Primary = img };
                }
                var placeholder = new Rectangle
                {
                    Fill = new SolidColorBrush(Color.FromArgb(200, 40, 40, 40)),
                    RadiusX = 4,
                    RadiusY = 4,
                };
                OverlayCanvas.Children.Add(placeholder);
                return new AnnotationVisual { Primary = placeholder };
            }
            case EditTool.Text:
            {
                var textBrush = new SolidColorBrush();
                var text = new TextBlock { Foreground = textBrush };
                OverlayCanvas.Children.Add(text);
                return new AnnotationVisual { Primary = text, TextBrush = textBrush };
            }
            case EditTool.Counter:
            {
                var circleBrush = new SolidColorBrush();
                var textBrush = new SolidColorBrush();
                var grid = new Grid();
                grid.Children.Add(new Ellipse { Fill = circleBrush });
                grid.Children.Add(new TextBlock
                {
                    Foreground = textBrush,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                OverlayCanvas.Children.Add(grid);
                return new AnnotationVisual { Primary = grid, StrokeBrush = circleBrush, TextBrush = textBrush };
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(ann), ann.Tool, "Unsupported annotation tool.");
        }
    }

    private static UIElement CreateArrowShaft(bool curved, SolidColorBrush stroke) =>
        curved
            ? new ShapesPath { Stroke = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round }
            : new Line { Stroke = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };

    private void PositionVisual(Annotation ann, AnnotationVisual visual)
    {
        var (scale, offX, offY) = HostLayout();
        var thickness = Math.Max(1, ann.Thickness * scale);

        switch (ann.Tool)
        {
            case EditTool.Rectangle:
            {
                var b = EditorController.NormalizedBounds(ann);
                var tl = ToCanvas(new Point(b.X, b.Y), scale, offX, offY);
                var rect = (Rectangle)visual.Primary;
                Canvas.SetLeft(rect, tl.X);
                Canvas.SetTop(rect, tl.Y);
                rect.Width = b.Width * scale;
                rect.Height = b.Height * scale;
                rect.StrokeThickness = thickness;
                visual.StrokeBrush!.Color = ann.Color;
                visual.FillBrush!.Color = ann.FillColor;
                break;
            }
            case EditTool.Ellipse:
            {
                var b = EditorController.NormalizedBounds(ann);
                var tl = ToCanvas(new Point(b.X, b.Y), scale, offX, offY);
                var ellipse = (Ellipse)visual.Primary;
                Canvas.SetLeft(ellipse, tl.X);
                Canvas.SetTop(ellipse, tl.Y);
                ellipse.Width = b.Width * scale;
                ellipse.Height = b.Height * scale;
                ellipse.StrokeThickness = thickness;
                visual.StrokeBrush!.Color = ann.Color;
                visual.FillBrush!.Color = ann.FillColor;
                break;
            }
            case EditTool.Line:
            {
                var (s, en) = EditorController.Segment(ann);
                var start = ToCanvas(new Point(s.X, s.Y), scale, offX, offY);
                var end = ToCanvas(new Point(en.X, en.Y), scale, offX, offY);
                var line = (Line)visual.Primary;
                line.X1 = start.X;
                line.Y1 = start.Y;
                line.X2 = end.X;
                line.Y2 = end.Y;
                line.StrokeThickness = thickness;
                visual.StrokeBrush!.Color = ann.Color;
                break;
            }
            case EditTool.Arrow:
                PositionArrow(ann, visual, scale, offX, offY, thickness);
                break;
            case EditTool.Pen:
                PositionPen(ann, visual, scale, offX, offY, thickness);
                break;
            case EditTool.Redact:
                PositionRedact(ann, visual, scale, offX, offY);
                break;
            case EditTool.Text:
                PositionText(ann, visual, scale, offX, offY);
                break;
            case EditTool.Counter:
                PositionCounter(ann, visual, scale, offX, offY);
                break;
        }
    }

    private static void PositionArrow(Annotation ann, AnnotationVisual visual, double scale, double offX, double offY, double thickness)
    {
        var (s, en) = EditorController.Segment(ann);
        var start = ToCanvas(new Point(s.X, s.Y), scale, offX, offY);
        var end = ToCanvas(new Point(en.X, en.Y), scale, offX, offY);
        var shape = EditorController.BuildArrow(
            new Vector2((float)start.X, (float)start.Y),
            new Vector2((float)end.X, (float)end.Y),
            thickness,
            ann.ArrowStyle);

        visual.StrokeBrush!.Color = ann.Color;
        visual.FillBrush!.Color = ann.Color;

        // The retained element reflects the requested style. BuildArrow deliberately reports a
        // zero-length curved arrow as non-curved because there is no meaningful tangent yet, so
        // branching on shape.Curved here would incorrectly cast its retained Path to Line during
        // the first/tiniest pointer moves.
        if (visual.Primary is ShapesPath path)
        {
            path.StrokeThickness = thickness;
            var figure = new PathFigure { StartPoint = new Point(shape.ShaftStart.X, shape.ShaftStart.Y) };
            figure.Segments.Add(new QuadraticBezierSegment
            {
                Point1 = new Point(shape.ShaftControl.X, shape.ShaftControl.Y),
                Point2 = new Point(shape.ShaftEnd.X, shape.ShaftEnd.Y),
            });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            path.Data = geometry;
        }
        else if (visual.Primary is Line line)
        {
            line.StrokeThickness = thickness;
            line.X1 = shape.ShaftStart.X;
            line.Y1 = shape.ShaftStart.Y;
            line.X2 = shape.ShaftEnd.X;
            line.Y2 = shape.ShaftEnd.Y;
        }
        else
        {
            throw new InvalidOperationException($"Unsupported arrow shaft visual: {visual.Primary.GetType().FullName}");
        }

        if (visual.Secondary is not Polygon head)
        {
            throw new InvalidOperationException("Arrow visual is missing its polygon head.");
        }
        head.Points.Clear();
        head.Points.Add(new Point(shape.Tip.X, shape.Tip.Y));
        head.Points.Add(new Point(shape.Head1.X, shape.Head1.Y));
        head.Points.Add(new Point(shape.Head2.X, shape.Head2.Y));
    }

    private static void PositionPen(Annotation ann, AnnotationVisual visual, double scale, double offX, double offY, double thickness)
    {
        var poly = (Polyline)visual.Primary;
        poly.StrokeThickness = thickness;
        visual.StrokeBrush!.Color = ann.Color;

        var layoutChanged = visual.SyncedScale != scale || visual.SyncedOffsetX != offX || visual.SyncedOffsetY != offY;
        // Detects a whole-stroke translation (moving an already-drawn Pen annotation with the
        // Select tool): the point count stays the same but every point shifts by the same dx/dy,
        // so the append-only fast path below would otherwise never notice and the polyline would
        // stay frozen at its pre-move position.
        var translated = !layoutChanged
            && ann.Points.Count == visual.SyncedPointCount
            && ann.Points.Count > 0
            && visual.SyncedFirstPoint is { } first
            && first != ann.Points[0];
        if (layoutChanged || translated || ann.Points.Count < visual.SyncedPointCount)
        {
            poly.Points.Clear();
            foreach (var pt in ann.Points)
            {
                poly.Points.Add(ToCanvas(new Point(pt.X, pt.Y), scale, offX, offY));
            }
            visual.SyncedPointCount = ann.Points.Count;
        }
        else if (ann.Points.Count > visual.SyncedPointCount)
        {
            // The common case during an active pen stroke: append just the new point(s) instead
            // of rebuilding the whole polyline — O(1) amortized per pointer move instead of O(n).
            for (var i = visual.SyncedPointCount; i < ann.Points.Count; i++)
            {
                var pt = ann.Points[i];
                poly.Points.Add(ToCanvas(new Point(pt.X, pt.Y), scale, offX, offY));
            }
            visual.SyncedPointCount = ann.Points.Count;
        }

        visual.SyncedScale = scale;
        visual.SyncedOffsetX = offX;
        visual.SyncedOffsetY = offY;
        visual.SyncedFirstPoint = ann.Points.Count > 0 ? ann.Points[0] : null;
    }

    private static void PositionRedact(Annotation ann, AnnotationVisual visual, double scale, double offX, double offY)
    {
        var b = EditorController.NormalizedBounds(ann);
        var tl = ToCanvas(new Point(b.X, b.Y), scale, offX, offY);
        var w = b.Width * scale;
        var h = b.Height * scale;

        if (visual.Primary is Image img)
        {
            Canvas.SetLeft(img, tl.X);
            Canvas.SetTop(img, tl.Y);
            img.Width = w;
            img.Height = h;
            img.Source = ann.RedactPreview;
        }
        else if (visual.Primary is Rectangle rect)
        {
            Canvas.SetLeft(rect, tl.X);
            Canvas.SetTop(rect, tl.Y);
            rect.Width = w;
            rect.Height = h;
        }
    }

    private static void PositionText(Annotation ann, AnnotationVisual visual, double scale, double offX, double offY)
    {
        var tl = ToCanvas(new Point(ann.Bounds.X, ann.Bounds.Y), scale, offX, offY);
        var text = (TextBlock)visual.Primary;
        text.Text = ann.Text;
        visual.TextBrush!.Color = ann.Color;
        text.FontSize = ann.FontSize * scale;
        text.FontFamily = new FontFamily(ann.FontFamily);
        text.FontWeight = ann.Bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        text.FontStyle = ann.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;

        var decorations = Windows.UI.Text.TextDecorations.None;
        if (ann.Underline)
        {
            decorations |= Windows.UI.Text.TextDecorations.Underline;
        }
        if (ann.Strikethrough)
        {
            decorations |= Windows.UI.Text.TextDecorations.Strikethrough;
        }
        text.TextDecorations = decorations;

        Canvas.SetLeft(text, tl.X);
        Canvas.SetTop(text, tl.Y);
    }

    private static void PositionCounter(Annotation ann, AnnotationVisual visual, double scale, double offX, double offY)
    {
        var b = EditorController.NormalizedBounds(ann);
        var tl = ToCanvas(new Point(b.X, b.Y), scale, offX, offY);
        var diameter = b.Width * scale;
        var grid = (Grid)visual.Primary;
        grid.Width = diameter;
        grid.Height = diameter;
        visual.StrokeBrush!.Color = ann.Color;

        var textBlock = (TextBlock)grid.Children[1];
        textBlock.Text = ann.Number.ToString();
        textBlock.FontSize = diameter * 0.5;
        visual.TextBrush!.Color = ann.TextColor;

        Canvas.SetLeft(grid, tl.X);
        Canvas.SetTop(grid, tl.Y);
    }

    private void PositionMarquee(Annotation? ann)
    {
        HideSelectionHandles();
        if (ann is null)
        {
            SelectionMarquee.Visibility = Visibility.Collapsed;
            return;
        }

        var (scale, offX, offY) = HostLayout();
        if (ann.Tool is EditTool.Line or EditTool.Arrow)
        {
            SelectionMarquee.Visibility = Visibility.Collapsed;
            var (start, end) = EditorController.Segment(ann);
            PositionHandle(StartEndpointHandle, ToCanvas(new Point(start.X, start.Y), scale, offX, offY));
            PositionHandle(EndEndpointHandle, ToCanvas(new Point(end.X, end.Y), scale, offX, offY));
            StartEndpointHandle.Visibility = Visibility.Visible;
            EndEndpointHandle.Visibility = Visibility.Visible;
            return;
        }

        var b = EditorController.NormalizedBounds(ann);
        var tl = ToCanvas(new Point(b.X, b.Y), scale, offX, offY);
        SelectionMarquee.Width = Math.Max(b.Width * scale, 8) + 12;
        SelectionMarquee.Height = Math.Max(b.Height * scale, 8) + 12;
        Canvas.SetLeft(SelectionMarquee, tl.X - 6);
        Canvas.SetTop(SelectionMarquee, tl.Y - 6);
        SelectionMarquee.Visibility = Visibility.Visible;
        PositionHandle(TopLeftResizeHandle, tl);
        PositionHandle(TopRightResizeHandle, new Point(tl.X + b.Width * scale, tl.Y));
        PositionHandle(BottomLeftResizeHandle, new Point(tl.X, tl.Y + b.Height * scale));
        PositionHandle(BottomRightResizeHandle, new Point(tl.X + b.Width * scale, tl.Y + b.Height * scale));
        TopLeftResizeHandle.Visibility = Visibility.Visible;
        TopRightResizeHandle.Visibility = Visibility.Visible;
        BottomLeftResizeHandle.Visibility = Visibility.Visible;
        BottomRightResizeHandle.Visibility = Visibility.Visible;
    }

    private void HideSelectionHandles()
    {
        TopLeftResizeHandle.Visibility = Visibility.Collapsed;
        TopRightResizeHandle.Visibility = Visibility.Collapsed;
        BottomLeftResizeHandle.Visibility = Visibility.Collapsed;
        BottomRightResizeHandle.Visibility = Visibility.Collapsed;
        StartEndpointHandle.Visibility = Visibility.Collapsed;
        EndEndpointHandle.Visibility = Visibility.Collapsed;
    }

    private static void PositionHandle(FrameworkElement handle, Point center)
    {
        Canvas.SetLeft(handle, center.X - handle.Width / 2);
        Canvas.SetTop(handle, center.Y - handle.Height / 2);
    }

    private AnnotationResizeHandle? ResizeHandleAt(Point point, Annotation ann)
    {
        if (ann.Tool is EditTool.Line or EditTool.Arrow)
        {
            return null;
        }
        var (scale, offX, offY) = HostLayout();
        var b = EditorController.NormalizedBounds(ann);
        var tl = ToCanvas(new Point(b.Left, b.Top), scale, offX, offY);
        var br = ToCanvas(new Point(b.Right, b.Bottom), scale, offX, offY);
        var handles = new[]
        {
            (AnnotationResizeHandle.TopLeft, tl),
            (AnnotationResizeHandle.TopRight, new Point(br.X, tl.Y)),
            (AnnotationResizeHandle.BottomLeft, new Point(tl.X, br.Y)),
            (AnnotationResizeHandle.BottomRight, br),
        };
        foreach (var (handle, center) in handles)
        {
            if (Math.Abs(point.X - center.X) <= 8 && Math.Abs(point.Y - center.Y) <= 8)
            {
                return handle;
            }
        }
        return null;
    }

    private bool TryBeginEndpointDrag(Point point, Annotation ann)
    {
        if (ann.Tool is not (EditTool.Line or EditTool.Arrow))
        {
            return false;
        }
        var (scale, offX, offY) = HostLayout();
        var (start, end) = EditorController.Segment(ann);
        var startCanvas = ToCanvas(new Point(start.X, start.Y), scale, offX, offY);
        var endCanvas = ToCanvas(new Point(end.X, end.Y), scale, offX, offY);
        var startDistance = Math.Sqrt(Math.Pow(point.X - startCanvas.X, 2) + Math.Pow(point.Y - startCanvas.Y, 2));
        var endDistance = Math.Sqrt(Math.Pow(point.X - endCanvas.X, 2) + Math.Pow(point.Y - endCanvas.Y, 2));
        if (Math.Min(startDistance, endDistance) > 10)
        {
            return false;
        }
        _endpointAnnotation = ann;
        _movingStartEndpoint = startDistance < endDistance;
        return true;
    }

    private static Point ToCanvas(Point pixel, double scale, double offX, double offY) =>
        new(pixel.X * scale + offX, pixel.Y * scale + offY);

    // -- Crop selection (pure UI state; never becomes an annotation) -------------------------

    public void ClearCropSelection()
    {
        SelectionRect.Visibility = Visibility.Collapsed;
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        CropSelectionAvailabilityChanged?.Invoke(this, false);
    }

    public BitmapBounds? GetCropSelectionPixelBounds()
    {
        if (SelectionRect.Visibility != Visibility.Visible)
        {
            return null;
        }

        var rect = new Rect(Canvas.GetLeft(SelectionRect), Canvas.GetTop(SelectionRect), SelectionRect.Width, SelectionRect.Height);
        return _controller.MapSelectionToPixels(rect, ImageHost.ActualWidth, ImageHost.ActualHeight);
    }

    // -- Pointer input -------------------------------------------------------------------------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_controller.Bitmap is null)
        {
            return;
        }

        OverlayCanvas.Focus(FocusState.Pointer);

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            return;
        }

        if (_spacePressed)
        {
            _panning = true;
            _panStart = e.GetCurrentPoint(ViewportScrollView).Position;
            _panStartHorizontalOffset = ViewportScrollView.HorizontalOffset;
            _panStartVerticalOffset = ViewportScrollView.VerticalOffset;
            OverlayCanvas.CapturePointer(e.Pointer);
            _capturedPointer = e.Pointer;
            e.Handled = true;
            return;
        }

        var p = e.GetCurrentPoint(OverlayCanvas).Position;
        var tool = _controller.Tool;

        if (tool == EditTool.Crop)
        {
            _dragging = true;
            _dragStart = p;
            Canvas.SetLeft(SelectionRect, p.X);
            Canvas.SetTop(SelectionRect, p.Y);
            SelectionRect.Width = 0;
            SelectionRect.Height = 0;
            SelectionRect.Visibility = Visibility.Visible;
            OverlayCanvas.CapturePointer(e.Pointer);
            _capturedPointer = e.Pointer;
            return;
        }

        if (tool == EditTool.Select)
        {
            if (_controller.SelectedAnnotation is { } selected)
            {
                if (ResizeHandleAt(p, selected) is { } handle)
                {
                    _resizingAnnotation = selected;
                    _movingAnnotation = selected;
                    _resizeHandle = handle;
                    _resizeOriginalBounds = EditorController.NormalizedBounds(selected);
                    _resizeOriginalPoints = new List<Vector2>(selected.Points);
                    _resizeOriginalFontSize = selected.FontSize;
                    _resizeOriginalSizeScale = selected.SizeScale;
                    OverlayCanvas.CapturePointer(e.Pointer);
                    _capturedPointer = e.Pointer;
                    return;
                }
                if (TryBeginEndpointDrag(p, selected))
                {
                    OverlayCanvas.CapturePointer(e.Pointer);
                    _capturedPointer = e.Pointer;
                    return;
                }
            }
            var hit = _controller.HitTest(_controller.CanvasToPixel(p, ImageHost.ActualWidth, ImageHost.ActualHeight));
            _controller.Select(hit);
            if (hit is not null)
            {
                if (TryBeginEndpointDrag(p, hit))
                {
                    OverlayCanvas.CapturePointer(e.Pointer);
                    _capturedPointer = e.Pointer;
                    return;
                }
                _movingAnnotation = hit;
                var origin = _controller.PixelToCanvas(new Point(hit.Bounds.X, hit.Bounds.Y), ImageHost.ActualWidth, ImageHost.ActualHeight);
                _moveOffset = new Point(p.X - origin.X, p.Y - origin.Y);
                OverlayCanvas.CapturePointer(e.Pointer);
                _capturedPointer = e.Pointer;
            }
            return;
        }

        if (tool == EditTool.Text)
        {
            BeginTextEntry(p);
            return;
        }

        if (tool == EditTool.Counter)
        {
            var center = _controller.CanvasToPixel(p, ImageHost.ActualWidth, ImageHost.ActualHeight);
            _controller.AddCounterAnnotation(center);
            return;
        }

        // Shape / line / arrow / pen / redact: begin a drag.
        _dragging = true;
        _dragStart = p;
        var pixel = _controller.CanvasToPixel(p, ImageHost.ActualWidth, ImageHost.ActualHeight);
        _controller.BeginAnnotation(tool, pixel);
        OverlayCanvas.CapturePointer(e.Pointer);
        _capturedPointer = e.Pointer;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_controller.Bitmap is null)
        {
            return;
        }

        if (_panning)
        {
            var panningPoint = e.GetCurrentPoint(ViewportScrollView).Position;
            ViewportScrollView.ScrollTo(
                _panStartHorizontalOffset - (panningPoint.X - _panStart.X),
                _panStartVerticalOffset - (panningPoint.Y - _panStart.Y),
                ScrollOptions);
            e.Handled = true;
            return;
        }

        var p = e.GetCurrentPoint(OverlayCanvas).Position;
        var tool = _controller.Tool;

        if (tool == EditTool.Crop && _dragging)
        {
            var x = Math.Min(p.X, _dragStart.X);
            var y = Math.Min(p.Y, _dragStart.Y);
            Canvas.SetLeft(SelectionRect, x);
            Canvas.SetTop(SelectionRect, y);
            SelectionRect.Width = Math.Abs(p.X - _dragStart.X);
            SelectionRect.Height = Math.Abs(p.Y - _dragStart.Y);
            return;
        }

        if (tool == EditTool.Select && _movingAnnotation is not null)
        {
            if (_resizingAnnotation is not null && _resizeHandle is { } handle)
            {
                var pixel = _controller.CanvasToPixel(p, ImageHost.ActualWidth, ImageHost.ActualHeight);
                _controller.ResizeAnnotation(
                    _resizingAnnotation,
                    _resizeOriginalBounds,
                    _resizeOriginalPoints,
                    _resizeOriginalFontSize,
                    _resizeOriginalSizeScale,
                    handle,
                    pixel);
                return;
            }
            var targetCanvas = new Point(p.X - _moveOffset.X, p.Y - _moveOffset.Y);
            var targetPixel = _controller.CanvasToPixel(targetCanvas, ImageHost.ActualWidth, ImageHost.ActualHeight);
            var b = _movingAnnotation.Bounds;
            _controller.MoveAnnotationBy(_movingAnnotation, targetPixel.X - b.X, targetPixel.Y - b.Y);
            return;
        }

        if (tool == EditTool.Select && _endpointAnnotation is not null)
        {
            var pixel = _controller.CanvasToPixel(p, ImageHost.ActualWidth, ImageHost.ActualHeight);
            _controller.MoveAnnotationEndpoint(_endpointAnnotation, _movingStartEndpoint, pixel);
            return;
        }

        if (_dragging && _controller.ActiveAnnotation is not null)
        {
            var startPixel = _controller.CanvasToPixel(_dragStart, ImageHost.ActualWidth, ImageHost.ActualHeight);
            var currentPixel = _controller.CanvasToPixel(p, ImageHost.ActualWidth, ImageHost.ActualHeight);
            _controller.UpdateActiveAnnotationDrag(startPixel, currentPixel, EditorController.IsShiftDown());
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // Null the field before releasing capture — see the matching comment in
        // CancelLocalInteraction: ReleasePointerCapture synchronously raises PointerCaptureLost,
        // and OnOverlayPointerCaptureLost uses this field being null to recognize "we just
        // released capture ourselves" and skip its own (redundant, and here actively wrong —
        // this method already committed/settled the interaction) cleanup pass.
        _capturedPointer = null;
        OverlayCanvas.ReleasePointerCapture(e.Pointer);

        if (_panning)
        {
            _panning = false;
            e.Handled = true;
            return;
        }

        var tool = _controller.Tool;

        if (tool == EditTool.Crop && _dragging)
        {
            _dragging = false;
            CropSelectionAvailabilityChanged?.Invoke(this, SelectionRect.Width > 4 && SelectionRect.Height > 4);
            return;
        }

        if (tool == EditTool.Select)
        {
            var moved = _movingAnnotation;
            _movingAnnotation = null;
            _resizingAnnotation = null;
            _resizeHandle = null;
            _endpointAnnotation = null;
            // A moved redact block only shows the lightweight placeholder while dragging (see
            // UpdateVisual); re-blur it now that the drag has settled. Guarded on list
            // membership in case the annotation was deleted/undone mid-drag — Undo/Delete cancel
            // the interaction first (see ScreenshotEditorWindow), but this stays defensive.
            if (moved is { Tool: EditTool.Redact } && Contains(_controller.Annotations, moved))
            {
                UpdateVisual(moved);
            }
            return;
        }

        if (_dragging && _controller.ActiveAnnotation is not null)
        {
            _dragging = false;
            var justActive = _controller.ActiveAnnotation;
            _controller.CommitActiveAnnotation();
            if (_controller.ActiveAnnotation is null && !Contains(_controller.Annotations, justActive))
            {
                // Drag was too small to commit (e.g. a click-sized rectangle) — drop its preview visual.
                RemoveVisual(justActive);
            }
        }
    }

    private static bool Contains(IReadOnlyList<Annotation> list, Annotation ann)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], ann))
            {
                return true;
            }
        }
        return false;
    }

    private void OnOverlayDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_controller.Bitmap is null)
        {
            return;
        }

        var p = e.GetPosition(OverlayCanvas);
        var pixel = _controller.CanvasToPixel(p, ImageHost.ActualWidth, ImageHost.ActualHeight);
        if (_controller.HitTest(pixel) is { Tool: EditTool.Text } textAnn)
        {
            _controller.Select(textAnn);
            EditTextAnnotation(textAnn);
        }
    }

    // -- Text entry ----------------------------------------------------------------------------

    private async void BeginTextEntry(Point canvasPoint)
    {
        var dialog = new TextEntryDialog(
            EditorFonts.Choices,
            string.Empty,
            _controller.TextFontFamily,
            _controller.TextFontSize,
            _controller.StrokeColor,
            _controller.TextBold,
            _controller.TextItalic,
            _controller.TextUnderline,
            _controller.TextStrikethrough,
            isEdit: false)
        {
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(dialog.ResultText))
        {
            return;
        }

        _controller.UpdateTextDefaults(
            dialog.ResultFont, dialog.ResultSize, dialog.ResultColor,
            dialog.ResultBold, dialog.ResultItalic, dialog.ResultUnderline, dialog.ResultStrikethrough);

        var pixel = _controller.CanvasToPixel(canvasPoint, ImageHost.ActualWidth, ImageHost.ActualHeight);
        _controller.AddTextAnnotation(
            pixel, dialog.ResultText, dialog.ResultFont, dialog.ResultSize, dialog.ResultColor,
            dialog.ResultBold, dialog.ResultItalic, dialog.ResultUnderline, dialog.ResultStrikethrough);
    }

    private async void EditTextAnnotation(Annotation ann)
    {
        var dialog = new TextEntryDialog(
            EditorFonts.Choices, ann.Text, ann.FontFamily, ann.FontSize, ann.Color,
            ann.Bold, ann.Italic, ann.Underline, ann.Strikethrough, isEdit: true)
        {
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        _controller.UpdateOrRemoveTextAnnotation(
            ann, dialog.ResultText, dialog.ResultFont, dialog.ResultSize, dialog.ResultColor,
            dialog.ResultBold, dialog.ResultItalic, dialog.ResultUnderline, dialog.ResultStrikethrough);
    }
}
