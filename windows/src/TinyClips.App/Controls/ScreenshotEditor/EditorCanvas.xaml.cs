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
using TinyClips.Core.Editing;
using TinyClips.Core.Services;
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
    /// annotation's lifetime except for Redact (placeholder Rectangle swaps to Image once a
    /// blurred preview is ready). Brushes are allocated once and mutated in place to avoid
    /// hot-path allocation.</summary>
    private sealed class AnnotationVisual
    {
        public UIElement Primary = null!;
        public UIElement? Secondary;
        public SolidColorBrush? StrokeBrush;
        public SolidColorBrush? FillBrush;
        public SolidColorBrush? TextBrush;
        // Arrows and lines hold their body and outline in one container, so the outline can
        // move behind the body for an outside stroke.
        public Canvas? Group;
    }

    private EditorController _controller = null!;
    private ICaptureSettings? _settings;
    private XamlRoot? _observedXamlRoot;
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
    private TextBoxStyle _resizeOriginalTextBoxStyle;
    private Annotation? _endpointAnnotation;
    private bool _movingStartEndpoint;
    private Annotation? _bendAnnotation;
    private Annotation? _rotatingAnnotation;
    private bool _spacePressed;
    private bool _panning;
    private bool _nativeSizeSelected;
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
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
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
    internal void Attach(EditorController controller, ICaptureSettings settings)
    {
        _controller = controller;
        _settings = settings;
        _nativeSizeSelected = settings.ScreenshotEditorNativeSize;
        NativeSizeToggleButton.IsChecked = _nativeSizeSelected;
        _controller.ImageChanged += OnControllerImageChanged;
        _controller.AnnotationsStructureChanged += (_, _) => FullRebuild();
        _controller.AnnotationVisualInvalidated += OnControllerAnnotationVisualInvalidated;
        _controller.SelectionChanged += (_, ann) => PositionMarquee(ann);
        _controller.ToolChanged += OnControllerToolChanged;
        _controller.ActiveAnnotationDiscarded += OnControllerActiveAnnotationDiscarded;
        _controller.BackgroundChanged += OnControllerBackgroundChanged;

        // Sync the hint text to the controller's initial tool (Select).
        OnControllerToolChanged(this, _controller.Tool);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(_observedXamlRoot, XamlRoot))
        {
            if (_observedXamlRoot is not null)
            {
                _observedXamlRoot.Changed -= OnXamlRootChanged;
            }

            _observedXamlRoot = XamlRoot;
            if (_observedXamlRoot is not null)
            {
                _observedXamlRoot.Changed += OnXamlRootChanged;
            }
        }

        ApplyNativeSizeZoom();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_observedXamlRoot is not null)
        {
            _observedXamlRoot.Changed -= OnXamlRootChanged;
            _observedXamlRoot = null;
        }
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) =>
        ApplyNativeSizeZoom();

    // -- Controller reactions ----------------------------------------------------------------

    private void OnControllerImageChanged(object? sender, EventArgs e)
    {
        PreviewImage.Source = _controller.PreviewSource;
        ClearCropSelection();
        LayoutCanvas();
        FullRebuild();
        ApplyNativeSizeZoom();
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
        _bendAnnotation = null;
        _rotatingAnnotation = null;

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
            EditTool.Select => "Click an annotation to select it; drag inside to move, drag a handle to resize, or drag the grip above it to rotate. Del removes it.",
            EditTool.Text => "Click where you want text to open the editor; double-click text to edit it.",
            EditTool.Counter => "Click to drop a numbered badge.",
            EditTool.Emoji => "Pick an emoji in the panel, then click to place it. Use Select to move, resize, or rotate it.",
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
        ApplyNativeSizeZoom();
    }

    // -- Layout ------------------------------------------------------------------------------

    private void OnImageHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // XAML lays out a window's content even when the window's constructor threw before
        // Attach ran, so this can fire with no controller. Crashing here hid the real failure.
        if (_controller is null)
        {
            return;
        }

        LayoutCanvas();
        RepositionAll();
        ApplyNativeSizeZoom();
    }

    private void OnViewportViewChanged(ScrollView sender, object args)
    {
        if (Math.Abs(sender.ZoomFactor - _zoomFactor) > 0.001f
            && _nativeSizeSelected)
        {
            SetNativeSizeMode(false);
            SetZoomBounds(MinZoomFactor, MaxZoomFactor);
        }

        _zoomFactor = sender.ZoomFactor;
        ZoomPercentageButton.Content = $"{Math.Round(sender.ZoomFactor * 100):0}%";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ZoomPercentageButton,
            $"Zoom percentage, {Math.Round(sender.ZoomFactor * 100):0}%");
    }

    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomOut();

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomIn();

    private void OnFit(object sender, RoutedEventArgs e) => Fit();

    private void OnNativeSize(object sender, RoutedEventArgs e)
    {
        var useNativeSize = NativeSizeToggleButton.IsChecked == true;
        SetNativeSizeMode(useNativeSize);

        if (useNativeSize)
        {
            ApplyNativeSizeZoom();
        }
        else
        {
            Fit();
        }
    }

    private void OnZoomPreset(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string value }
            && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var zoom))
        {
            SetZoom(zoom);
        }
    }

    private void SetZoom(float zoom, Point? focalPoint = null, bool keepNativeSizeMode = false)
    {
        var minimumZoomFactor = keepNativeSizeMode ? Math.Min(MinZoomFactor, zoom) : MinZoomFactor;
        var maximumZoomFactor = keepNativeSizeMode ? Math.Max(MaxZoomFactor, zoom) : MaxZoomFactor;
        var clamped = Math.Clamp(zoom, minimumZoomFactor, maximumZoomFactor);
        var currentZoomFactor = Math.Max(0.001f, ViewportScrollView.ZoomFactor);
        var focal = focalPoint ?? new Point(
            (ViewportScrollView.HorizontalOffset + ViewportScrollView.ViewportWidth / 2.0) / currentZoomFactor,
            (ViewportScrollView.VerticalOffset + ViewportScrollView.ViewportHeight / 2.0) / currentZoomFactor);

        SetNativeSizeMode(keepNativeSizeMode);
        _zoomFactor = clamped;
        SetZoomBounds(minimumZoomFactor, maximumZoomFactor);
        ViewportScrollView.ZoomTo(
            clamped,
            new Vector2((float)focal.X, (float)focal.Y),
            ZoomOptions);
    }

    private void SetNativeSizeMode(bool selected)
    {
        _nativeSizeSelected = selected;
        NativeSizeToggleButton.IsChecked = selected;
        if (_settings is not null)
        {
            _settings.ScreenshotEditorNativeSize = selected;
        }
    }

    private void SetZoomBounds(float minimumZoomFactor, float maximumZoomFactor)
    {
        ViewportScrollView.MinZoomFactor = minimumZoomFactor;
        ViewportScrollView.MaxZoomFactor = maximumZoomFactor;
    }

    private void ApplyNativeSizeZoom()
    {
        if (!_nativeSizeSelected
            || _controller?.Bitmap is null
            || ImageHost.ActualWidth <= 0
            || ImageHost.ActualHeight <= 0)
        {
            return;
        }

        var layout = HostLayout();
        var rasterizationScale = XamlRoot?.RasterizationScale ?? 1.0;
        var nativeZoomFactor = ScreenshotEditorZoomMath.NativeSizeZoomFactor(layout.Scale, rasterizationScale);
        if (nativeZoomFactor is not { } zoomFactor || zoomFactor > float.MaxValue)
        {
            return;
        }

        SetZoom((float)zoomFactor, keepNativeSizeMode: true);
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

        var showBackground = _controller.BgStyle is ExportBackgroundStyle.Solid or ExportBackgroundStyle.Gradient;
        CanvasBackground.Visibility = showBackground ? Visibility.Visible : Visibility.Collapsed;
        if (showBackground)
        {
            Canvas.SetLeft(CanvasBackground, frameOffX);
            Canvas.SetTop(CanvasBackground, frameOffY);
            CanvasBackground.Width = frame.FrameSize.Width * scale;
            CanvasBackground.Height = frame.FrameSize.Height * scale;
            CanvasBackground.Background = MakeBackgroundBrush(frame.FrameSize);
        }

        var showImage = _controller.BgStyle == ExportBackgroundStyle.Image;
        BackgroundImageLayer.Visibility = showImage ? Visibility.Visible : Visibility.Collapsed;
        BackgroundImage.Source = showImage ? _controller.BackgroundPreviewSource : null;
        if (showImage)
        {
            var bounds = ScreenshotBackgroundLayout.Create(_controller.BackgroundImageSize.Width,
                _controller.BackgroundImageSize.Height, frame.FrameSize, _controller.ImagePlacement);
            Canvas.SetLeft(BackgroundImageLayer, frameOffX);
            Canvas.SetTop(BackgroundImageLayer, frameOffY);
            BackgroundImageLayer.Width = frame.FrameSize.Width * scale;
            BackgroundImageLayer.Height = frame.FrameSize.Height * scale;
            BackgroundImageLayer.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, BackgroundImageLayer.Width, BackgroundImageLayer.Height),
            };
            Canvas.SetLeft(BackgroundImage, bounds.X * scale);
            Canvas.SetTop(BackgroundImage, bounds.Y * scale);
            BackgroundImage.Width = bounds.Width * scale;
            BackgroundImage.Height = bounds.Height * scale;
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

    private Brush MakeBackgroundBrush(Size frameSize)
    {
        if (_controller.BgStyle == ExportBackgroundStyle.Gradient)
        {
            var endpoints = ScreenshotGradientMath.Endpoints(
                _controller.GradientDirectionDegrees, frameSize.Width, frameSize.Height);
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(endpoints.Start.X, endpoints.Start.Y),
                EndPoint = new Point(endpoints.End.X, endpoints.End.Y),
            };
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
        PositionSensitiveTextHighlights();
    }

    private IReadOnlyList<Rect> _sensitiveTextBounds = Array.Empty<Rect>();
    private Rect? _activeSensitiveTextBounds;

    internal void SetSensitiveTextHighlights(IReadOnlyList<Rect> bounds, Rect? activeBounds = null)
    {
        _sensitiveTextBounds = bounds;
        _activeSensitiveTextBounds = activeBounds;
        PositionSensitiveTextHighlights();
        if (activeBounds is not null && SensitiveTextLayer.Children.Count > 0)
            ((FrameworkElement)SensitiveTextLayer.Children[^1]).StartBringIntoView();
    }

    private void PositionSensitiveTextHighlights()
    {
        SensitiveTextLayer.Children.Clear();
        if (_controller.Bitmap is null) return;
        var (scale, offX, offY) = HostLayout();
        foreach (var bounds in _sensitiveTextBounds)
            AddHighlight(bounds, false);
        if (_activeSensitiveTextBounds is { } active) AddHighlight(active, true);

        void AddHighlight(Rect bounds, bool isActive)
        {
            var rectangle = new Rectangle
            {
                Style = (Style)Resources["SensitiveTextHighlightStyle"],
                Width = bounds.Width * scale,
                Height = bounds.Height * scale,
            };
            if (isActive)
            {
                rectangle.Fill = null;
                rectangle.Opacity = 1;
                rectangle.StrokeThickness = 3;
            }
            Canvas.SetLeft(rectangle, offX + bounds.X * scale);
            Canvas.SetTop(rectangle, offY + bounds.Y * scale);
            SensitiveTextLayer.Children.Add(rectangle);
        }
    }

    private void RemoveVisual(Annotation ann)
    {
        if (_visuals.Remove(ann, out var visual))
        {
            if (visual.Group is not null)
            {
                OverlayCanvas.Children.Remove(visual.Group);
                return;
            }
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

        if (ann.Tool == EditTool.Redact)
        {
            var isMoving = ReferenceEquals(ann, _movingAnnotation);
            if (!isMoving && ann.RedactStyle != RedactionStyle.Solid)
            {
                _controller.EnsureRedactPreview(ann);
            }
            var wantsImage = ann.RedactStyle != RedactionStyle.Solid && !isMoving
                && !ReferenceEquals(ann, _controller.ActiveAnnotation) && ann.RedactPreview is not null;
            var isImage = exists && visual!.Primary is Image;
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
    /// Assigns a stable <c>Canvas.ZIndex</c> derived from this annotation's position in the
    /// controller's ordered <see cref="EditorController.Annotations"/> list (or, for the
    /// not-yet-committed drag preview, just above the topmost committed annotation). Recreating a
    /// visual — a redaction's placeholder Rectangle swapping for its blurred Image — changes elements in
    /// <see cref="OverlayCanvas"/>'s Children collection. Pinning ZIndex to list order keeps preview
    /// stacking stable and matching export/undo order no matter how many times an element is
    /// replaced. The fixed crop-selection and selection-marquee adorners are pinned to a much
    /// higher ZIndex in XAML so they always stay above every annotation visual.
    /// </summary>
    private void ApplyZOrder(Annotation ann, AnnotationVisual visual)
    {
        var z = AnnotationZIndex(ann);
        if (visual.Group is not null)
        {
            Canvas.SetZIndex(visual.Group, z);
            return;
        }
        Canvas.SetZIndex(visual.Primary, z);
        if (visual.Secondary is not null)
        {
            // Same ZIndex as Primary: ties are broken by Children collection order, and Primary
            // is always added before Secondary (see CreateVisual), so e.g. a rectangle's stroke
            // ring stays above its own fill.
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
                // The fill and the stroke ring are separate so the ring can sit inside, centered on,
                // or outside the edge with the same corners the export draws.
                var stroke = new SolidColorBrush();
                var fill = new SolidColorBrush();
                var rect = new Rectangle { Fill = fill };
                MakeRotatable(rect);
                var ring = new ShapesPath { Fill = stroke, RenderTransform = new RotateTransform() };
                OverlayCanvas.Children.Add(rect);
                OverlayCanvas.Children.Add(ring);
                return new AnnotationVisual { Primary = rect, Secondary = ring, StrokeBrush = stroke, FillBrush = fill };
            }
            case EditTool.Ellipse:
            {
                var stroke = new SolidColorBrush();
                var fill = new SolidColorBrush();
                var ellipse = new Ellipse { Stroke = stroke, Fill = fill };
                MakeRotatable(ellipse);
                OverlayCanvas.Children.Add(ellipse);
                return new AnnotationVisual { Primary = ellipse, StrokeBrush = stroke, FillBrush = fill };
            }
            case EditTool.Line:
            case EditTool.Arrow:
            {
                // One filled body (shaft, head, and caps), so a translucent color stays even, plus
                // an optional outline drawn around it.
                var fill = new SolidColorBrush();
                var stroke = new SolidColorBrush();
                var body = new Polygon { Fill = fill, FillRule = FillRule.Nonzero };
                var outline = new Polygon { Stroke = stroke };
                var group = new Canvas();
                group.Children.Add(body);
                group.Children.Add(outline);
                OverlayCanvas.Children.Add(group);
                return new AnnotationVisual { Primary = body, Secondary = outline, Group = group, StrokeBrush = stroke, FillBrush = fill };
            }
            case EditTool.Pen:
            {
                // One filled, tapered outline, so a translucent color stays even.
                var fill = new SolidColorBrush();
                var stroke = new ShapesPath { Fill = fill };
                OverlayCanvas.Children.Add(stroke);
                return new AnnotationVisual { Primary = stroke, FillBrush = fill };
            }
            case EditTool.Redact:
            {
                var isMoving = ReferenceEquals(ann, _movingAnnotation);
                var wantsImage = ann.RedactStyle != RedactionStyle.Solid && !isMoving
                    && !ReferenceEquals(ann, _controller.ActiveAnnotation) && ann.RedactPreview is not null;
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
                var fillBrush = new SolidColorBrush();
                var borderBrush = new SolidColorBrush();
                var text = new TextBlock
                {
                    Foreground = textBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var border = new Border
                {
                    Child = text,
                    Background = fillBrush,
                    BorderBrush = borderBrush,
                };
                MakeRotatable(border);
                OverlayCanvas.Children.Add(border);
                return new AnnotationVisual
                {
                    Primary = border,
                    TextBrush = textBrush,
                    FillBrush = fillBrush,
                    StrokeBrush = borderBrush,
                };
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
            case EditTool.Emoji:
            {
                var grid = new Grid();
                MakeRotatable(grid);
                grid.Children.Add(new TextBlock
                {
                    FontFamily = new FontFamily("Segoe UI Emoji"),
                    IsColorFontEnabled = true,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextLineBounds = TextLineBounds.Tight,
                });
                OverlayCanvas.Children.Add(grid);
                return new AnnotationVisual { Primary = grid };
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(ann), ann.Tool, "Unsupported annotation tool.");
        }
    }

    /// <summary>The XAML twin of the export's ring geometry: the outer outline minus the inner rectangle.</summary>
    private static PathGeometry CreateRingGeometry(RectStrokeRing ring)
    {
        var geometry = new PathGeometry { FillRule = FillRule.EvenOdd };
        geometry.Figures.Add(OuterRingFigure(ring.Outer, ring.OuterJoin, ring.CornerSize));
        var i = ring.Inner;
        geometry.Figures.Add(PolygonFigure(
            new Point(i.Left, i.Top),
            new Point(i.Right, i.Top),
            new Point(i.Right, i.Bottom),
            new Point(i.Left, i.Bottom)));
        return geometry;
    }

    private static PathFigure OuterRingFigure(RectD o, StrokeJoin join, double c)
    {
        if (c <= 0 || join == StrokeJoin.Miter)
        {
            return PolygonFigure(
                new Point(o.Left, o.Top),
                new Point(o.Right, o.Top),
                new Point(o.Right, o.Bottom),
                new Point(o.Left, o.Bottom));
        }

        if (join == StrokeJoin.Bevel)
        {
            return PolygonFigure(
                new Point(o.Left + c, o.Top),
                new Point(o.Right - c, o.Top),
                new Point(o.Right, o.Top + c),
                new Point(o.Right, o.Bottom - c),
                new Point(o.Right - c, o.Bottom),
                new Point(o.Left + c, o.Bottom),
                new Point(o.Left, o.Bottom - c),
                new Point(o.Left, o.Top + c));
        }

        var figure = new PathFigure { StartPoint = new Point(o.Left + c, o.Top), IsClosed = true, IsFilled = true };
        void Corner(Point lineTo, Point arcTo)
        {
            figure.Segments.Add(new LineSegment { Point = lineTo });
            figure.Segments.Add(new ArcSegment { Point = arcTo, Size = new Size(c, c), SweepDirection = SweepDirection.Clockwise });
        }
        Corner(new Point(o.Right - c, o.Top), new Point(o.Right, o.Top + c));
        Corner(new Point(o.Right, o.Bottom - c), new Point(o.Right - c, o.Bottom));
        Corner(new Point(o.Left + c, o.Bottom), new Point(o.Left, o.Bottom - c));
        Corner(new Point(o.Left, o.Top + c), new Point(o.Left + c, o.Top));
        return figure;
    }

    private static PathFigure PolygonFigure(params Point[] points)
    {
        var figure = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = true };
        foreach (var point in points.Skip(1))
        {
            figure.Segments.Add(new LineSegment { Point = point });
        }
        return figure;
    }

    /// <summary>Gives a visual a center-anchored <see cref="RotateTransform"/> so <see cref="ApplyRotation"/> can turn it in place.</summary>
    private static void MakeRotatable(FrameworkElement element)
    {
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = new RotateTransform();
    }

    private static void ApplyRotation(FrameworkElement element, double degrees)
    {
        if (element.RenderTransform is RotateTransform rotate)
        {
            rotate.Angle = degrees;
        }
    }

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
                visual.StrokeBrush!.Color = ann.Color;
                visual.FillBrush!.Color = ann.FillColor;
                ApplyRotation(rect, ann.Rotation);

                var bounds = new RectD(tl.X, tl.Y, b.Width * scale, b.Height * scale);
                var ring = (ShapesPath)visual.Secondary!;
                ring.Data = CreateRingGeometry(
                    StrokeAlignmentGeometry.RectangleRing(bounds, thickness, ann.StrokeAlignment, ann.LineJoin));
                if (ring.RenderTransform is RotateTransform ringRotation)
                {
                    ringRotation.CenterX = bounds.Center.X;
                    ringRotation.CenterY = bounds.Center.Y;
                    ringRotation.Angle = ann.Rotation;
                }
                break;
            }
            case EditTool.Ellipse:
            {
                var b = EditorController.NormalizedBounds(ann);
                var tl = ToCanvas(new Point(b.X, b.Y), scale, offX, offY);
                var ellipse = (Ellipse)visual.Primary;
                // XAML cannot clip to an ellipse, so inside and outside strokes are approximated
                // by stroking a smaller or larger ellipse; the export is exact.
                var centerline = StrokeAlignmentGeometry.EllipseCenterline(
                    new RectD(tl.X, tl.Y, b.Width * scale, b.Height * scale), thickness, ann.StrokeAlignment);
                Canvas.SetLeft(ellipse, centerline.X);
                Canvas.SetTop(ellipse, centerline.Y);
                ellipse.Width = centerline.Width;
                ellipse.Height = centerline.Height;
                ellipse.StrokeThickness = thickness;
                visual.StrokeBrush!.Color = ann.Color;
                visual.FillBrush!.Color = ann.FillColor;
                ApplyRotation(ellipse, ann.Rotation);
                break;
            }
            case EditTool.Line:
            {
                var (s, en) = EditorController.Segment(ann);
                var start = ToCanvas(new Point(s.X, s.Y), scale, offX, offY);
                var end = ToCanvas(new Point(en.X, en.Y), scale, offX, offY);
                var outline = LineGeometry.Outline(
                    new PointD(start.X, start.Y), new PointD(end.X, end.Y), ann.Thickness, ann.StartCap, ann.EndCap, scale);
                PositionBody(ann, visual, outline, scale);
                break;
            }
            case EditTool.Arrow:
                PositionArrow(ann, visual, scale, offX, offY);
                break;
            case EditTool.Pen:
                PositionPen(ann, visual, scale, offX, offY);
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
            case EditTool.Emoji:
                PositionEmoji(ann, visual, scale, offX, offY);
                break;
        }
    }

    private static void PositionEmoji(Annotation ann, AnnotationVisual visual, double scale, double offX, double offY)
    {
        var b = ann.Bounds;
        var tl = ToCanvas(new Point(b.X, b.Y), scale, offX, offY);
        var side = b.Width * scale;
        var grid = (Grid)visual.Primary;
        grid.Width = side;
        grid.Height = side;
        ApplyRotation(grid, ann.Rotation);

        var textBlock = (TextBlock)grid.Children[0];
        textBlock.Text = ann.Text;
        textBlock.FontSize = EmojiAnnotationMath.GlyphFontSize(side);

        Canvas.SetLeft(grid, tl.X);
        Canvas.SetTop(grid, tl.Y);
    }

    private static void PositionArrow(Annotation ann, AnnotationVisual visual, double scale, double offX, double offY)
    {
        var (s, en) = EditorController.Segment(ann);
        var start = ToCanvas(new Point(s.X, s.Y), scale, offX, offY);
        var end = ToCanvas(new Point(en.X, en.Y), scale, offX, offY);
        // Sizes come from the image-pixel width and scale with the view, matching the export.
        var outline = ArrowGeometry.Build(
            new PointD(start.X, start.Y),
            new PointD(end.X, end.Y),
            ann.Thickness,
            ann.Arrow,
            ann.StartCap,
            ann.LineJoin,
            scale);

        PositionBody(ann, visual, outline.Points, scale);
    }

    /// <summary>
    /// Shows an arrow's or line's filled body and its outline. A centered outline is drawn over
    /// the body and an outside one at double width behind it, both exact. XAML cannot clip to
    /// the body, so an inside outline is shown centered here; the export draws it exactly.
    /// </summary>
    private static void PositionBody(Annotation ann, AnnotationVisual visual, IReadOnlyList<PointD> points, double scale)
    {
        visual.FillBrush!.Color = ann.Color;
        var body = (Polygon)visual.Primary;
        SetPoints(body, points);

        var outline = (Polygon)visual.Secondary!;
        var hasOutline = ann.OutlineWidth > 0 && points.Count >= 3;
        outline.Visibility = hasOutline ? Visibility.Visible : Visibility.Collapsed;
        if (!hasOutline)
        {
            return;
        }
        var isOutside = ann.StrokeAlignment == StrokeAlignment.Outside;
        visual.StrokeBrush!.Color = ann.OutlineColor;
        outline.StrokeThickness = ann.OutlineWidth * scale * (isOutside ? 2 : 1);
        outline.StrokeLineJoin = ann.LineJoin switch
        {
            StrokeJoin.Miter => PenLineJoin.Miter,
            StrokeJoin.Bevel => PenLineJoin.Bevel,
            _ => PenLineJoin.Round,
        };
        Canvas.SetZIndex(outline, isOutside ? 0 : 2);
        Canvas.SetZIndex(body, 1);
        SetPoints(outline, points);
    }

    private static void SetPoints(Polygon polygon, IReadOnlyList<PointD> points)
    {
        polygon.Points.Clear();
        foreach (var point in points)
        {
            polygon.Points.Add(new Point(point.X, point.Y));
        }
    }

    private static void PositionPen(Annotation ann, AnnotationVisual visual, double scale, double offX, double offY)
    {
        visual.FillBrush!.Color = ann.Color;
        // The outline is built in image pixels, where its thresholds are defined, then placed.
        var outline = PenGeometry.Outline(ann.Points.Select(p => new PointD(p.X, p.Y)).ToArray(), ann.Thickness);
        var geometry = new PathGeometry();
        if (outline.Count >= 3)
        {
            Point Place(PointD p) => ToCanvas(new Point(p.X, p.Y), scale, offX, offY);
            var figure = new PathFigure { StartPoint = Place(outline[0]), IsClosed = true, IsFilled = true };
            // Quadratic curves through each pair of points' midpoints keep the edge from looking faceted.
            for (var i = 0; i < outline.Count; i++)
            {
                var point = outline[i];
                var next = outline[(i + 1) % outline.Count];
                figure.Segments.Add(new QuadraticBezierSegment
                {
                    Point1 = Place(point),
                    Point2 = Place(new PointD((point.X + next.X) / 2, (point.Y + next.Y) / 2)),
                });
            }
            geometry.Figures.Add(figure);
        }
        ((ShapesPath)visual.Primary).Data = geometry;
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
            // Solid redactions are opaque immediately, including while moving/drawing. Do not
            // briefly expose pixels through the rounded translucent blur-preview placeholder.
            var isSolid = ann.RedactStyle == RedactionStyle.Solid;
            rect.Fill = new SolidColorBrush(isSolid ? Microsoft.UI.Colors.Black : Color.FromArgb(200, 40, 40, 40));
            rect.RadiusX = rect.RadiusY = isSolid ? 0 : 4;
            Canvas.SetLeft(rect, tl.X);
            Canvas.SetTop(rect, tl.Y);
            rect.Width = w;
            rect.Height = h;
        }
    }

    private static void PositionText(Annotation ann, AnnotationVisual visual, double scale, double offX, double offY)
    {
        var decorated = ann.TextBoxStyle.DecoratedBounds(ann.Bounds);
        var tl = ToCanvas(new Point(decorated.X, decorated.Y), scale, offX, offY);
        var border = (Border)visual.Primary;
        var text = (TextBlock)border.Child;
        text.Text = ann.Text;
        visual.TextBrush!.Color = ann.Color;
        visual.FillBrush!.Color = ann.TextBoxStyle.BackgroundColor;
        visual.StrokeBrush!.Color = ann.TextBoxStyle.BorderColor;
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
        border.Width = Math.Max(1, decorated.Width * scale);
        border.Height = Math.Max(1, decorated.Height * scale);
        border.Padding = new Thickness(0);
        border.BorderThickness = new Thickness(Math.Max(0, ann.TextBoxStyle.BorderWidth * scale));
        border.CornerRadius = new CornerRadius(Math.Max(0, ann.TextBoxStyle.CornerRadius * scale));
        ApplyRotation(border, ann.Rotation);

        Canvas.SetLeft(border, tl.X);
        Canvas.SetTop(border, tl.Y);
    }

    private static void PositionCounter(Annotation ann, AnnotationVisual visual, double scale, double offX, double offY)
    {
        var b = EditorController.InteractionBounds(ann);
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
            if (ann.Tool == EditTool.Arrow)
            {
                var handle = ArrowGeometry.Handle(new PointD(start.X, start.Y), new PointD(end.X, end.Y), ann.Arrow.Bend);
                PositionHandle(ArrowBendHandle, ToCanvas(new Point(handle.X, handle.Y), scale, offX, offY));
                ArrowBendHandle.Visibility = Visibility.Visible;
            }
            return;
        }

        var b = EditorController.NormalizedBounds(ann);
        var tl = ToCanvas(new Point(b.X, b.Y), scale, offX, offY);

        if (EditorController.RotationFrame(ann) is { } frame)
        {
            PositionRotatedMarquee(frame.Bounds, frame.Rotation, scale, offX, offY);
            return;
        }

        SetMarqueeRotation(0);
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
        ArrowBendHandle.Visibility = Visibility.Collapsed;
        RotationHandle.Visibility = Visibility.Collapsed;
        RotationHandleStem.Visibility = Visibility.Collapsed;
    }

    /// <summary>Rotates the dashed marquee (and the square corner grips) so they hug a rotated sticker.</summary>
    private void SetMarqueeRotation(double degrees)
    {
        foreach (var element in new FrameworkElement[]
                 {
                     SelectionMarquee, TopLeftResizeHandle, TopRightResizeHandle, BottomLeftResizeHandle, BottomRightResizeHandle,
                 })
        {
            if (element.RenderTransform is not RotateTransform rotate)
            {
                rotate = new RotateTransform();
                element.RenderTransformOrigin = new Point(0.5, 0.5);
                element.RenderTransform = rotate;
            }
            rotate.Angle = degrees;
        }
    }

    /// <summary>Canvas-space geometry shared by the marquee, corner grips, and the rotation grip.</summary>
    private (RectD Bounds, double Offset) CanvasStickerGeometry(Rect pixelBounds, double scale, double offX, double offY)
    {
        var tl = ToCanvas(new Point(pixelBounds.X, pixelBounds.Y), scale, offX, offY);
        var bounds = new RectD(tl.X, tl.Y, pixelBounds.Width * scale, pixelBounds.Height * scale);
        return (bounds, RotatableAnnotationGeometry.RotationHandleOffset(bounds.Height));
    }

    private void PositionRotatedMarquee(Rect pixelBounds, double rotation, double scale, double offX, double offY)
    {
        var (bounds, offset) = CanvasStickerGeometry(pixelBounds, scale, offX, offY);
        var center = bounds.Center;

        SetMarqueeRotation(rotation);
        SelectionMarquee.Width = Math.Max(bounds.Width, 8) + 12;
        SelectionMarquee.Height = Math.Max(bounds.Height, 8) + 12;
        Canvas.SetLeft(SelectionMarquee, center.X - SelectionMarquee.Width / 2);
        Canvas.SetTop(SelectionMarquee, center.Y - SelectionMarquee.Height / 2);
        SelectionMarquee.Visibility = Visibility.Visible;

        var corners = RotatableAnnotationGeometry.Corners(bounds, rotation);
        PositionHandle(TopLeftResizeHandle, new Point(corners[0].X, corners[0].Y));
        PositionHandle(TopRightResizeHandle, new Point(corners[1].X, corners[1].Y));
        PositionHandle(BottomLeftResizeHandle, new Point(corners[2].X, corners[2].Y));
        PositionHandle(BottomRightResizeHandle, new Point(corners[3].X, corners[3].Y));
        TopLeftResizeHandle.Visibility = Visibility.Visible;
        TopRightResizeHandle.Visibility = Visibility.Visible;
        BottomLeftResizeHandle.Visibility = Visibility.Visible;
        BottomRightResizeHandle.Visibility = Visibility.Visible;

        var grip = RotatableAnnotationGeometry.RotationHandle(bounds, rotation, offset);
        var topCenter = RotatableAnnotationGeometry.Rotate(new PointD(center.X, bounds.Top), center, rotation);
        RotationHandleStem.X1 = topCenter.X;
        RotationHandleStem.Y1 = topCenter.Y;
        RotationHandleStem.X2 = grip.X;
        RotationHandleStem.Y2 = grip.Y;
        RotationHandleStem.Visibility = Visibility.Visible;
        PositionHandle(RotationHandle, new Point(grip.X, grip.Y));
        RotationHandle.Visibility = Visibility.Visible;
    }

    private bool IsRotationHandleAt(Point point, Annotation ann)
    {
        if (EditorController.RotationFrame(ann) is not { } frame)
        {
            return false;
        }
        var (scale, offX, offY) = HostLayout();
        var (bounds, offset) = CanvasStickerGeometry(frame.Bounds, scale, offX, offY);
        var grip = RotatableAnnotationGeometry.RotationHandle(bounds, frame.Rotation, offset);
        return RotatableAnnotationGeometry.Distance(new PointD(point.X, point.Y), grip) <= 11;
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

        if (ann.Tool == EditTool.Emoji || ann.IsRotated)
        {
            var (stickerBounds, _) = CanvasStickerGeometry(EditorController.InteractionBounds(ann), scale, offX, offY);
            var corners = RotatableAnnotationGeometry.Corners(stickerBounds, ann.Rotation);
            var order = new[]
            {
                AnnotationResizeHandle.TopLeft, AnnotationResizeHandle.TopRight,
                AnnotationResizeHandle.BottomLeft, AnnotationResizeHandle.BottomRight,
            };
            for (var i = 0; i < corners.Length; i++)
            {
                if (RotatableAnnotationGeometry.Distance(new PointD(point.X, point.Y), corners[i]) <= 9)
                {
                    return order[i];
                }
            }
            return null;
        }

        var b = EditorController.InteractionBounds(ann);
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

    /// <param name="allowBend">Only an arrow that was already selected shows its bend handle, so
    /// only then may pressing near its middle bend it rather than move it.</param>
    private bool TryBeginEndpointDrag(Point point, Annotation ann, bool allowBend)
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
        if (allowBend && ann.Tool == EditTool.Arrow)
        {
            // The bend handle sits on the curve's midpoint, between the two endpoint handles.
            var handle = ArrowGeometry.Handle(new PointD(start.X, start.Y), new PointD(end.X, end.Y), ann.Arrow.Bend);
            var handleCanvas = ToCanvas(new Point(handle.X, handle.Y), scale, offX, offY);
            var bendDistance = Math.Sqrt(Math.Pow(point.X - handleCanvas.X, 2) + Math.Pow(point.Y - handleCanvas.Y, 2));
            if (bendDistance <= 10 && bendDistance < Math.Min(startDistance, endDistance))
            {
                _bendAnnotation = ann;
                return true;
            }
        }
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

        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
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
                if (IsRotationHandleAt(p, selected))
                {
                    _rotatingAnnotation = selected;
                    _resizeOriginalBounds = EditorController.InteractionBounds(selected);
                    _resizeOriginalPoints = new List<Vector2>(selected.Points);
                    OverlayCanvas.CapturePointer(e.Pointer);
                    _capturedPointer = e.Pointer;
                    return;
                }
                if (ResizeHandleAt(p, selected) is { } handle)
                {
                    _resizingAnnotation = selected;
                    _movingAnnotation = selected;
                    _resizeHandle = handle;
                    _resizeOriginalBounds = EditorController.InteractionBounds(selected);
                    _resizeOriginalPoints = new List<Vector2>(selected.Points);
                    _resizeOriginalFontSize = selected.FontSize;
                    _resizeOriginalSizeScale = selected.SizeScale;
                    _resizeOriginalTextBoxStyle = selected.TextBoxStyle;
                    OverlayCanvas.CapturePointer(e.Pointer);
                    _capturedPointer = e.Pointer;
                    return;
                }
                if (TryBeginEndpointDrag(p, selected, allowBend: true))
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
                if (TryBeginEndpointDrag(p, hit, allowBend: false))
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

        if (tool == EditTool.Emoji)
        {
            var center = _controller.CanvasToPixel(p, ImageHost.ActualWidth, ImageHost.ActualHeight);
            _controller.AddEmojiAnnotation(center);
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

        if (tool == EditTool.Select && _rotatingAnnotation is not null)
        {
            var pixel = _controller.CanvasToPixel(p, ImageHost.ActualWidth, ImageHost.ActualHeight);
            _controller.RotateAnnotationToward(
                _rotatingAnnotation,
                _resizeOriginalBounds,
                _resizeOriginalPoints,
                pixel,
                EditorController.IsShiftDown());
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
                    _resizeOriginalTextBoxStyle,
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

        if (tool == EditTool.Select && _bendAnnotation is not null)
        {
            var pixel = _controller.CanvasToPixel(p, ImageHost.ActualWidth, ImageHost.ActualHeight);
            var (scale, _, _) = HostLayout();
            // Snap straight within a few screen pixels of the chord.
            _controller.MoveArrowBend(_bendAnnotation, pixel, 6 / Math.Max(scale, 0.0001));
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
            _bendAnnotation = null;
            _rotatingAnnotation = null;
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
            _controller.TextBoxStyleDefault,
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
            dialog.ResultBold, dialog.ResultItalic, dialog.ResultUnderline, dialog.ResultStrikethrough,
            dialog.ResultTextBoxStyle);

        var pixel = _controller.CanvasToPixel(canvasPoint, ImageHost.ActualWidth, ImageHost.ActualHeight);
        _controller.AddTextAnnotation(
            pixel, dialog.ResultText, dialog.ResultFont, dialog.ResultSize, dialog.ResultColor,
            dialog.ResultBold, dialog.ResultItalic, dialog.ResultUnderline, dialog.ResultStrikethrough,
            dialog.ResultTextBoxStyle);
    }

    private async void EditTextAnnotation(Annotation ann)
    {
        var dialog = new TextEntryDialog(
            EditorFonts.Choices, ann.Text, ann.FontFamily, ann.FontSize, ann.Color,
            ann.Bold, ann.Italic, ann.Underline, ann.Strikethrough, ann.TextBoxStyle, isEdit: true)
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
            dialog.ResultBold, dialog.ResultItalic, dialog.ResultUnderline, dialog.ResultStrikethrough,
            dialog.ResultTextBoxStyle);
    }
}
