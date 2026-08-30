using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;
using TinyClips.Core.Editing;

namespace TinyClips.App.ScreenshotEditor;

/// <summary>
/// Owns all screenshot-editor domain state: the loaded bitmap, annotations, per-tool style
/// defaults, export/background settings, coordinate math, and Win2D baking/redaction. This is the
/// "shared editor state/controller" the toolbar, inspector, and canvas each hold a reference to
/// instead of reaching into each other or into the window. It has no dependency on any XAML
/// element — <see cref="EditorCanvas"/> owns pointer input and visuals, <see cref="EditorToolbar"/>
/// and <see cref="EditorInspector"/> own their own controls, and all three react to this
/// controller's narrow events.
/// </summary>
internal sealed class EditorController : IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;

    private SoftwareBitmap? _bitmap;
    private CanvasBitmap? _canvasSource;

    private readonly List<Annotation> _annotations = new();
    private int _counterValue = 1;

    public static readonly BackgroundPreset[] SolidPresets =
    {
        new("white", "White", ExportBackgroundStyle.Solid, Color.FromArgb(255, 255, 255, 255), null),
        new("ink", "Ink", ExportBackgroundStyle.Solid, Color.FromArgb(255, 20, 23, 26), null),
        new("coral", "Coral", ExportBackgroundStyle.Solid, Color.FromArgb(255, 255, 122, 107), null),
        new("lemon", "Lemon", ExportBackgroundStyle.Solid, Color.FromArgb(255, 255, 224, 64), null),
        new("mint", "Mint", ExportBackgroundStyle.Solid, Color.FromArgb(255, 105, 219, 158), null),
        new("sky", "Sky", ExportBackgroundStyle.Solid, Color.FromArgb(255, 87, 171, 245), null),
        new("lilac", "Lilac", ExportBackgroundStyle.Solid, Color.FromArgb(255, 179, 148, 240), null),
        new("bubblegum", "Bubblegum", ExportBackgroundStyle.Solid, Color.FromArgb(255, 255, 107, 194), null),
        new("tangerine", "Tangerine", ExportBackgroundStyle.Solid, Color.FromArgb(255, 255, 143, 41), null),
        new("lagoon", "Lagoon", ExportBackgroundStyle.Solid, Color.FromArgb(255, 0, 184, 199), null),
        new("plum", "Plum", ExportBackgroundStyle.Solid, Color.FromArgb(255, 99, 46, 148), null),
        new("slate", "Slate", ExportBackgroundStyle.Solid, Color.FromArgb(255, 86, 101, 115), null),
    };

    public static readonly BackgroundPreset[] GradientPresets =
    {
        new("sunset", "Sunset", ExportBackgroundStyle.Gradient, Color.FromArgb(255, 255, 122, 94), Color.FromArgb(255, 255, 219, 79)),
        new("ocean", "Ocean", ExportBackgroundStyle.Gradient, Color.FromArgb(255, 38, 135, 232), Color.FromArgb(255, 46, 224, 191)),
        new("candy", "Candy", ExportBackgroundStyle.Gradient, Color.FromArgb(255, 255, 107, 173), Color.FromArgb(255, 140, 199, 255)),
        new("forest", "Forest", ExportBackgroundStyle.Gradient, Color.FromArgb(255, 41, 143, 89), Color.FromArgb(255, 184, 224, 107)),
        new("ember", "Ember", ExportBackgroundStyle.Gradient, Color.FromArgb(255, 56, 20, 13), Color.FromArgb(255, 255, 115, 41)),
        new("aurora", "Aurora", ExportBackgroundStyle.Gradient, Color.FromArgb(255, 71, 240, 184), Color.FromArgb(255, 133, 107, 255)),
        new("peach", "Peach", ExportBackgroundStyle.Gradient, Color.FromArgb(255, 255, 184, 133), Color.FromArgb(255, 250, 107, 138)),
        new("glacier", "Glacier", ExportBackgroundStyle.Gradient, Color.FromArgb(255, 186, 240, 255), Color.FromArgb(255, 107, 148, 245)),
        new("neon", "Neon", ExportBackgroundStyle.Gradient, Color.FromArgb(255, 13, 255, 138), Color.FromArgb(255, 255, 20, 179)),
        new("mango", "Mango", ExportBackgroundStyle.Gradient, Color.FromArgb(255, 255, 199, 51), Color.FromArgb(255, 255, 66, 46)),
        new("midnight", "Midnight", ExportBackgroundStyle.Gradient, Color.FromArgb(255, 13, 18, 46), Color.FromArgb(255, 0, 148, 209)),
        new("prism", "Prism", ExportBackgroundStyle.Gradient, Color.FromArgb(255, 250, 41, 97), Color.FromArgb(255, 46, 219, 237)),
    };

    public EditorController(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    // -- State ------------------------------------------------------------------------------

    public SoftwareBitmap? Bitmap => _bitmap;

    public IReadOnlyList<Annotation> Annotations => _annotations;

    /// <summary>
    /// True when there are annotations that have not been persisted via Save/Save a copy. Used by
    /// the window to guard closing (parity with macOS's <c>hasUnsavedChanges</c> exit
    /// confirmation). Does not by itself account for an in-progress, not-yet-applied crop
    /// selection — the window combines this with its own crop-selection tracking.
    /// </summary>
    public bool HasUnsavedChanges => _annotations.Count > 0;

    public Annotation? SelectedAnnotation { get; private set; }

    public Annotation? ActiveAnnotation { get; private set; }

    public EditTool Tool { get; private set; } = EditTool.Crop;

    public Color StrokeColor { get; private set; } = Colors.Red;

    public double StrokeThickness { get; private set; } = 6;

    public bool FillEnabled { get; private set; }

    public Color FillColor { get; private set; } = Color.FromArgb(96, 255, 213, 0);

    public double NumberScale { get; private set; } = 1.0;

    public Color NumberTextColor { get; private set; } = Colors.White;

    public double TextFontSize { get; private set; } = 28;

    public string TextFontFamily { get; private set; } = "Segoe UI";

    public bool TextBold { get; private set; }

    public bool TextItalic { get; private set; }

    public bool TextUnderline { get; private set; }

    public bool TextStrikethrough { get; private set; }

    public RedactionLevel RedactionLevelDefault { get; private set; } = RedactionLevel.Medium;

    public RedactionStyle RedactionStyleDefault { get; private set; } = RedactionStyle.Blur;

    public ArrowStyle ArrowStyleDefault { get; private set; } = ArrowStyle.Straight;

    public string EmojiDefault { get; private set; } = EmojiAnnotationMath.DefaultEmoji;

    // Shared across editor windows for the lifetime of the process so the "Recent" row survives
    // closing and reopening the editor.
    private static List<string> s_recentEmoji = new();

    public IReadOnlyList<string> RecentEmoji => s_recentEmoji;

    public ExportBackgroundStyle BgStyle { get; private set; } = ExportBackgroundStyle.Transparent;

    public Color BgColor { get; private set; } = Color.FromArgb(255, 245, 245, 250);

    public Color BgColor2 { get; private set; } = Color.FromArgb(255, 214, 230, 252);

    public double CanvasPadding { get; private set; }

    public double CanvasCornerRadius { get; private set; }

    public double CanvasShadow { get; private set; }

    public ExportFramePreset FramePreset { get; private set; } = ExportFramePreset.Original;

    public ExportHorizontalAlignment HorizontalExportAlignment { get; private set; } = ExportHorizontalAlignment.Center;

    public ExportVerticalAlignment VerticalExportAlignment { get; private set; } = ExportVerticalAlignment.Center;

    // -- Events -------------------------------------------------------------------------------

    /// <summary>The loaded bitmap changed (initial load, crop, or reset). Full relayout/redraw.</summary>
    public event EventHandler? ImageChanged;

    /// <summary>Annotations were added/removed (create, undo, delete, crop, reset). Full redraw.</summary>
    public event EventHandler? AnnotationsStructureChanged;

    /// <summary>
    /// One annotation's geometry/style changed in place (drag, move, inspector edit, redact
    /// preview ready). Consumers should update only this annotation's retained visual.
    /// </summary>
    public event EventHandler<Annotation>? AnnotationVisualInvalidated;

    public event EventHandler<EditTool>? ToolChanged;

    public event EventHandler<Annotation?>? SelectionChanged;

    /// <summary>The default emoji (or the selected sticker's emoji) changed via <see cref="SetEmoji"/>.</summary>
    public event EventHandler<string>? EmojiChanged;

    /// <summary>
    /// Raised when an in-progress (not yet committed) annotation is discarded without being
    /// added to <see cref="Annotations"/> or the undo history — e.g. the user switches tools
    /// with a keyboard shortcut while a drag is still in flight. Consumers must drop any
    /// retained preview visual for this annotation; it will never appear in
    /// <see cref="AnnotationsStructureChanged"/>.
    /// </summary>
    public event EventHandler<Annotation>? ActiveAnnotationDiscarded;

    /// <summary>Export background/padding/corner/shadow changed — affects canvas layout.</summary>
    public event EventHandler? BackgroundChanged;

    // -- Image lifecycle ---------------------------------------------------------------------

    public async Task LoadAsync(string filePath)
    {
        var file = await StorageFile.GetFileFromPathAsync(filePath);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        await SetBitmapFromCaptureAsync(bitmap);
    }

    /// <summary>
    /// Loads a freshly captured bitmap (already BGRA8 premultiplied) as the document, resetting
    /// annotations — the in-memory equivalent of <see cref="LoadAsync"/>.
    /// </summary>
    public async Task SetBitmapFromCaptureAsync(SoftwareBitmap bitmap)
    {
        await SetBitmapAsync(bitmap);
        _annotations.Clear();
        _counterValue = 1;
        SelectedAnnotation = null;
        AnnotationsStructureChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetBitmapAsync(SoftwareBitmap bitmap)
    {
        _bitmap?.Dispose();
        _bitmap = bitmap;

        _canvasSource?.Dispose();
        _canvasSource = CanvasBitmap.CreateFromSoftwareBitmap(CanvasDevice.GetSharedDevice(), bitmap);

        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);
        PreviewSource = source;

        ImageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Bindable image source for the preview <c>Image</c> element; refreshed by <see cref="SetBitmapAsync"/>.</summary>
    public SoftwareBitmapSource? PreviewSource { get; private set; }

    // -- Tool / selection ----------------------------------------------------------------------

    public void SetTool(EditTool tool)
    {
        // A tool hotkey (or toolbar click) can land mid-drag — e.g. the user starts drawing a
        // rectangle, then presses "A" for Arrow before releasing the mouse. Discard any
        // in-progress annotation up front so it never gets committed just because the tool
        // changed, and so it can never outlive the drag as an orphaned retained visual.
        CancelActiveAnnotation();

        Tool = tool;
        SelectedAnnotation = null;
        ToolChanged?.Invoke(this, tool);
        SelectionChanged?.Invoke(this, null);
    }

    public void Select(Annotation? annotation)
    {
        SelectedAnnotation = annotation;
        SelectionChanged?.Invoke(this, annotation);
    }

    public Annotation? HitTest(Point pixelPoint)
    {
        for (var i = _annotations.Count - 1; i >= 0; i--)
        {
            var ann = _annotations[i];
            if (ann.Tool == EditTool.Emoji || ann.IsRotated)
            {
                if (RotatableAnnotationGeometry.Contains(
                        new PointD(pixelPoint.X, pixelPoint.Y),
                        ToRectD(NormalizedBounds(ann)),
                        ann.Rotation,
                        padding: ann.Thickness + 6))
                {
                    return ann;
                }
                continue;
            }

            var b = NormalizedBounds(ann);
            var pad = ann.Thickness + 6;
            var inflated = new Rect(b.X - pad, b.Y - pad, b.Width + pad * 2, b.Height + pad * 2);
            if (inflated.Contains(pixelPoint))
            {
                return ann;
            }
        }
        return null;
    }

    internal static RectD ToRectD(Rect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    // -- Style defaults / selected-annotation edits --------------------------------------------
    // Each setter mirrors the original inline handlers: if an applicable annotation is selected,
    // the change applies to it (and only its retained visual is invalidated); otherwise it becomes
    // the default used for the next annotation created with that tool.

    public void SetStrokeColor(Color color)
    {
        StrokeColor = color;
        if (SelectedAnnotation is not null)
        {
            SelectedAnnotation.Color = color;
            AnnotationVisualInvalidated?.Invoke(this, SelectedAnnotation);
        }
    }

    public void SetNumberColor(Color color)
    {
        NumberTextColor = color;
        if (SelectedAnnotation is { Tool: EditTool.Counter } ann)
        {
            ann.TextColor = color;
            AnnotationVisualInvalidated?.Invoke(this, ann);
        }
    }

    public void SetStrokeThickness(double thickness)
    {
        StrokeThickness = thickness;
        if (SelectedAnnotation is { Tool: not EditTool.Counter and not EditTool.Text } ann)
        {
            ann.Thickness = thickness;
            AnnotationVisualInvalidated?.Invoke(this, ann);
        }
    }

    public void SetArrowStyle(ArrowStyle style)
    {
        if (SelectedAnnotation is { Tool: EditTool.Arrow } ann)
        {
            ann.ArrowStyle = style;
            AnnotationVisualInvalidated?.Invoke(this, ann);
        }
        else
        {
            ArrowStyleDefault = style;
            if (ActiveAnnotation is { Tool: EditTool.Arrow } active)
            {
                active.ArrowStyle = style;
                AnnotationVisualInvalidated?.Invoke(this, active);
            }
        }
    }

    public void SetFillEnabled(bool enabled)
    {
        FillEnabled = enabled;
        if (SelectedAnnotation is { Tool: EditTool.Rectangle or EditTool.Ellipse } ann)
        {
            ann.FillColor = enabled ? FillColor : Colors.Transparent;
            AnnotationVisualInvalidated?.Invoke(this, ann);
        }
    }

    /// <summary>Returns true when the fill color represents "None" (alpha 0), matching the swatch behavior.</summary>
    public void SetFillColor(Color color)
    {
        if (color.A == 0)
        {
            FillEnabled = false;
            if (SelectedAnnotation is { Tool: EditTool.Rectangle or EditTool.Ellipse } cleared)
            {
                cleared.FillColor = Colors.Transparent;
                AnnotationVisualInvalidated?.Invoke(this, cleared);
            }
            return;
        }

        FillColor = color;
        FillEnabled = true;
        if (SelectedAnnotation is { Tool: EditTool.Rectangle or EditTool.Ellipse } ann)
        {
            ann.FillColor = color;
            AnnotationVisualInvalidated?.Invoke(this, ann);
        }
    }

    public void SetFontFamily(string font)
    {
        TextFontFamily = font;
        if (SelectedAnnotation is { Tool: EditTool.Text } ann)
        {
            ann.FontFamily = font;
            AnnotationVisualInvalidated?.Invoke(this, ann);
        }
    }

    public void SetFontSize(double size)
    {
        TextFontSize = size;
        if (SelectedAnnotation is { Tool: EditTool.Text } ann)
        {
            ann.FontSize = size;
            AnnotationVisualInvalidated?.Invoke(this, ann);
        }
    }

    public void SetNumberSize(double scale)
    {
        NumberScale = scale;
        if (SelectedAnnotation is { Tool: EditTool.Counter } ann)
        {
            ann.SizeScale = scale;
            var center = new Point(ann.Bounds.X + ann.Bounds.Width / 2, ann.Bounds.Y + ann.Bounds.Height / 2);
            var radius = CounterRadius(scale);
            ann.Bounds = new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2);
            AnnotationVisualInvalidated?.Invoke(this, ann);
        }
    }

    public void SetRedactionLevel(RedactionLevel level)
    {
        RedactionLevelDefault = level;
        if (SelectedAnnotation is { Tool: EditTool.Redact } ann)
        {
            ann.Redaction = level;
            ann.RedactPreview = null;
            AnnotationVisualInvalidated?.Invoke(this, ann);
        }
    }

    public void SetRedactionStyle(RedactionStyle style)
    {
        RedactionStyleDefault = style;
        if (SelectedAnnotation is { Tool: EditTool.Redact } ann)
        {
            ann.RedactStyle = style;
            ann.RedactPreview = null;
            AnnotationVisualInvalidated?.Invoke(this, ann);
        }
    }

    /// <summary>
    /// Remembers the styling chosen in the text-entry dialog as the new tool defaults for the
    /// next text annotation (mirrors the dialog also doubling as the "current" stroke color).
    /// </summary>
    public void UpdateTextDefaults(string fontFamily, double fontSize, Color color, bool bold, bool italic, bool underline, bool strikethrough)
    {
        TextFontFamily = fontFamily;
        TextFontSize = fontSize;
        StrokeColor = color;
        TextBold = bold;
        TextItalic = italic;
        TextUnderline = underline;
        TextStrikethrough = strikethrough;
    }

    public static double CounterRadius(double scale) => Math.Max(12, 22 * scale);

    /// <summary>
    /// Picks the emoji used for new stickers, or swaps the glyph on the selected sticker, and
    /// records it in the recent list.
    /// </summary>
    public void SetEmoji(string emoji)
    {
        if (string.IsNullOrEmpty(emoji))
        {
            return;
        }

        EmojiDefault = emoji;
        s_recentEmoji = EmojiAnnotationMath.PushRecent(s_recentEmoji, emoji);
        if (SelectedAnnotation is { Tool: EditTool.Emoji } ann && ann.Text != emoji)
        {
            ann.Text = emoji;
            AnnotationVisualInvalidated?.Invoke(this, ann);
        }
        EmojiChanged?.Invoke(this, emoji);
    }

    /// <summary>Sets the selected annotation's rotation (degrees, clockwise) — used by the inspector slider.</summary>
    public void SetRotation(double degrees)
    {
        if (SelectedAnnotation is not { } ann || !ann.Tool.StoresRotation())
        {
            return;
        }

        var normalized = RotatableAnnotationGeometry.NormalizeDegrees(degrees);
        if (Math.Abs(ann.Rotation - normalized) < 0.001)
        {
            return;
        }

        ann.Rotation = normalized;
        AnnotationVisualInvalidated?.Invoke(this, ann);
    }

    /// <summary>
    /// Bounds and angle the rotation grip is anchored to, or null for tools that can't rotate.
    /// Pen strokes use their point bounds and always report 0° because rotation is baked into the
    /// points.
    /// </summary>
    public static (Rect Bounds, double Rotation)? RotationFrame(Annotation ann)
    {
        if (!ann.Tool.SupportsRotation())
        {
            return null;
        }
        return ann.Tool == EditTool.Pen
            ? (NormalizedBounds(ann), 0)
            : (NormalizedBounds(ann), ann.Rotation);
    }

    /// <summary>
    /// Points the annotation's rotation grip at <paramref name="pixelPoint"/>; Shift snaps to 15°
    /// steps. <paramref name="originalBounds"/>/<paramref name="originalPoints"/> are captured at
    /// drag start so pen strokes (whose bounds move as their points turn) rotate about a fixed center.
    /// </summary>
    public void RotateAnnotationToward(
        Annotation ann,
        Rect originalBounds,
        IReadOnlyList<Vector2> originalPoints,
        Point pixelPoint,
        bool snap)
    {
        var center = ToRectD(originalBounds).Center;
        var angle = RotatableAnnotationGeometry.AngleDegrees(center, new PointD(pixelPoint.X, pixelPoint.Y));
        var snapped = RotatableAnnotationGeometry.NormalizeDegrees(RotatableAnnotationGeometry.Snap(angle, 15, snap));

        if (ann.Tool == EditTool.Pen)
        {
            ann.Points.Clear();
            foreach (var original in originalPoints)
            {
                var rotated = RotatableAnnotationGeometry.Rotate(new PointD(original.X, original.Y), center, snapped);
                ann.Points.Add(new Vector2((float)rotated.X, (float)rotated.Y));
            }
            UpdatePenBounds(ann);
        }
        else
        {
            ann.Rotation = snapped;
        }
        AnnotationVisualInvalidated?.Invoke(this, ann);
    }

    public Annotation AddEmojiAnnotation(Point pixelCenter)
    {
        var imageWidth = _bitmap?.PixelWidth ?? 1000;
        var imageHeight = _bitmap?.PixelHeight ?? 1000;
        var side = EmojiAnnotationMath.DefaultSidePixels(imageWidth, imageHeight);
        var bounds = EmojiAnnotationMath.SquareBounds(new PointD(pixelCenter.X, pixelCenter.Y), side);
        var ann = new Annotation
        {
            Tool = EditTool.Emoji,
            Color = StrokeColor,
            Thickness = 0,
            Text = EmojiDefault,
            Bounds = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height),
        };
        s_recentEmoji = EmojiAnnotationMath.PushRecent(s_recentEmoji, EmojiDefault);
        _annotations.Add(ann);
        AnnotationsStructureChanged?.Invoke(this, EventArgs.Empty);
        EmojiChanged?.Invoke(this, EmojiDefault);
        return ann;
    }

    // -- Background / export ---------------------------------------------------------------

    public void ApplyPreset(BackgroundPreset preset)
    {
        BgStyle = preset.Style;
        BgColor = preset.Primary;
        BgColor2 = preset.Secondary ?? preset.Primary;
        BackgroundChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetBackgroundStyle(ExportBackgroundStyle style)
    {
        BgStyle = style;
        BackgroundChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetCustomBgColor(Color color)
    {
        BgColor = color;
        if (BgStyle == ExportBackgroundStyle.Solid)
        {
            BackgroundChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ApplyCustomSolidBackground(Color color)
    {
        BgStyle = ExportBackgroundStyle.Solid;
        BgColor = color;
        BackgroundChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetPadding(double padding)
    {
        CanvasPadding = padding;
        BackgroundChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetCornerRadius(double corner)
    {
        CanvasCornerRadius = corner;
        BackgroundChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetShadow(double shadow)
    {
        CanvasShadow = shadow;
        BackgroundChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetExportFramePreset(ExportFramePreset preset)
    {
        FramePreset = preset;
        BackgroundChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetHorizontalExportAlignment(ExportHorizontalAlignment alignment)
    {
        HorizontalExportAlignment = alignment;
        BackgroundChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetVerticalExportAlignment(ExportVerticalAlignment alignment)
    {
        VerticalExportAlignment = alignment;
        BackgroundChanged?.Invoke(this, EventArgs.Empty);
    }

    public ExportFrameLayout GetExportFrameLayout()
    {
        var imageWidth = _bitmap?.PixelWidth ?? 0;
        var imageHeight = _bitmap?.PixelHeight ?? 0;
        return ExportFrameLayout.Create(
            imageWidth,
            imageHeight,
            CanvasPadding,
            FramePreset,
            HorizontalExportAlignment,
            VerticalExportAlignment);
    }

    public bool HasHorizontalExportFrameSpace
    {
        get
        {
            if (_bitmap is null)
            {
                return false;
            }

            var layout = GetExportFrameLayout();
            return layout.FrameSize.Width > _bitmap.PixelWidth + Math.Max(0, CanvasPadding) * 2;
        }
    }

    public bool HasVerticalExportFrameSpace
    {
        get
        {
            if (_bitmap is null)
            {
                return false;
            }

            var layout = GetExportFrameLayout();
            return layout.FrameSize.Height > _bitmap.PixelHeight + Math.Max(0, CanvasPadding) * 2;
        }
    }

    // -- Coordinate mapping (pure; parameterized by the current host size) ------------------

    public (double Scale, double OffsetX, double OffsetY) ImageLayout(double hostW, double hostH)
    {
        double imgW = _bitmap?.PixelWidth ?? 1;
        double imgH = _bitmap?.PixelHeight ?? 1;
        if (imgW <= 0 || imgH <= 0 || hostW <= 0 || hostH <= 0)
        {
            return (1, 0, 0);
        }

        var frame = GetExportFrameLayout();
        var scale = Math.Min(hostW / frame.FrameSize.Width, hostH / frame.FrameSize.Height);
        var frameOffsetX = (hostW - frame.FrameSize.Width * scale) / 2.0;
        var frameOffsetY = (hostH - frame.FrameSize.Height * scale) / 2.0;
        // Offsets returned are the image card's top-left (inside the padded frame),
        // so annotation pixel<->canvas mapping stays aligned with the screenshot.
        var offsetX = frameOffsetX + frame.ImageBounds.X * scale;
        var offsetY = frameOffsetY + frame.ImageBounds.Y * scale;
        return (scale, offsetX, offsetY);
    }

    public Point CanvasToPixel(Point canvas, double hostW, double hostH)
    {
        var (scale, offX, offY) = ImageLayout(hostW, hostH);
        if (scale <= 0)
        {
            return canvas;
        }
        return new Point((canvas.X - offX) / scale, (canvas.Y - offY) / scale);
    }

    public Point PixelToCanvas(Point pixel, double hostW, double hostH)
    {
        var (scale, offX, offY) = ImageLayout(hostW, hostH);
        return new Point(pixel.X * scale + offX, pixel.Y * scale + offY);
    }

    // -- Annotation creation / drag lifecycle -----------------------------------------------

    public Annotation BeginAnnotation(EditTool tool, Point pixelStart)
    {
        var ann = new Annotation
        {
            Tool = tool,
            Color = StrokeColor,
            FillColor = (tool is EditTool.Rectangle or EditTool.Ellipse && FillEnabled) ? FillColor : Colors.Transparent,
            Thickness = StrokeThickness,
            Redaction = RedactionLevelDefault,
            RedactStyle = RedactionStyleDefault,
            ArrowStyle = ArrowStyleDefault,
            Bounds = new Rect(pixelStart.X, pixelStart.Y, 0, 0),
        };

        if (tool == EditTool.Pen)
        {
            ann.Points.Add(new Vector2((float)pixelStart.X, (float)pixelStart.Y));
        }
        else if (tool is EditTool.Line or EditTool.Arrow)
        {
            // Lines and arrows are stored as directed endpoints so they can point any direction.
            ann.Points.Add(new Vector2((float)pixelStart.X, (float)pixelStart.Y));
            ann.Points.Add(new Vector2((float)pixelStart.X, (float)pixelStart.Y));
        }

        ActiveAnnotation = ann;
        return ann;
    }

    public void UpdateActiveAnnotationDrag(Point startPixel, Point currentPixel, bool shiftDown)
    {
        var ann = ActiveAnnotation;
        if (ann is null)
        {
            return;
        }

        if (ann.Tool == EditTool.Pen)
        {
            ann.Points.Add(new Vector2((float)currentPixel.X, (float)currentPixel.Y));
        }

        if (ann.Tool is EditTool.Line or EditTool.Arrow)
        {
            // Store the directed endpoints; keep Bounds as a normalized box for hit-testing.
            var end = currentPixel;
            if (shiftDown)
            {
                end = ConstrainToAxisOrDiagonal(startPixel, end);
            }
            if (ann.Points.Count < 2)
            {
                ann.Points.Add(new Vector2((float)end.X, (float)end.Y));
            }
            else
            {
                ann.Points[1] = new Vector2((float)end.X, (float)end.Y);
            }
            var nx = Math.Min(startPixel.X, end.X);
            var ny = Math.Min(startPixel.Y, end.Y);
            ann.Bounds = new Rect(nx, ny, Math.Abs(end.X - startPixel.X), Math.Abs(end.Y - startPixel.Y));
        }
        else
        {
            var endX = currentPixel.X;
            var endY = currentPixel.Y;
            // Shift constrains rectangles/ellipses to a perfect square/circle.
            if (shiftDown && ann.Tool is EditTool.Rectangle or EditTool.Ellipse)
            {
                var side = Math.Max(Math.Abs(currentPixel.X - startPixel.X), Math.Abs(currentPixel.Y - startPixel.Y));
                endX = startPixel.X + Math.Sign(currentPixel.X - startPixel.X) * side;
                endY = startPixel.Y + Math.Sign(currentPixel.Y - startPixel.Y) * side;
            }
            var x = Math.Min(endX, startPixel.X);
            var y = Math.Min(endY, startPixel.Y);
            ann.Bounds = new Rect(x, y, Math.Abs(endX - startPixel.X), Math.Abs(endY - startPixel.Y));
        }

        AnnotationVisualInvalidated?.Invoke(this, ann);
    }

    /// <summary>Commits the active drag annotation if it is large/long enough to be meaningful.</summary>
    public void CommitActiveAnnotation()
    {
        var ann = ActiveAnnotation;
        if (ann is null)
        {
            return;
        }

        if (ann.Tool == EditTool.Pen)
        {
            UpdatePenBounds(ann);
        }

        var b = ann.Bounds;
        var significant = ann.Tool == EditTool.Pen
            ? ann.Points.Count > 1
            : Math.Abs(b.Width) > 3 || Math.Abs(b.Height) > 3;

        ActiveAnnotation = null;

        if (significant)
        {
            _annotations.Add(ann);
            AnnotationsStructureChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Discards the in-progress drag annotation (if any) without committing it to
    /// <see cref="Annotations"/> or the undo history. Safe to call even when there is no active
    /// annotation. Used when the interaction is interrupted rather than released normally — e.g.
    /// a tool-change keyboard shortcut fires mid-drag — so a hotkey press never silently commits
    /// (or leaves behind a retained-but-uncommitted) annotation.
    /// </summary>
    public void CancelActiveAnnotation()
    {
        var ann = ActiveAnnotation;
        if (ann is null)
        {
            return;
        }

        ActiveAnnotation = null;
        ActiveAnnotationDiscarded?.Invoke(this, ann);
    }

    public Annotation AddCounterAnnotation(Point pixelCenter)
    {
        var radius = CounterRadius(NumberScale);
        var ann = new Annotation
        {
            Tool = EditTool.Counter,
            Color = StrokeColor,
            Thickness = StrokeThickness,
            SizeScale = NumberScale,
            TextColor = NumberTextColor,
            Number = _counterValue++,
            Bounds = new Rect(pixelCenter.X - radius, pixelCenter.Y - radius, radius * 2, radius * 2),
        };
        _annotations.Add(ann);
        AnnotationsStructureChanged?.Invoke(this, EventArgs.Empty);
        return ann;
    }

    public Annotation AddTextAnnotation(
        Point pixelPoint, string text, string fontFamily, double fontSize, Color color,
        bool bold, bool italic, bool underline, bool strikethrough)
    {
        var ann = new Annotation
        {
            Tool = EditTool.Text,
            Color = color,
            Thickness = StrokeThickness,
            Text = text,
            FontSize = fontSize,
            FontFamily = fontFamily,
            Bold = bold,
            Italic = italic,
            Underline = underline,
            Strikethrough = strikethrough,
            Bounds = new Rect(pixelPoint.X, pixelPoint.Y, 0, 0),
        };
        UpdateTextBounds(ann);
        _annotations.Add(ann);
        AnnotationsStructureChanged?.Invoke(this, EventArgs.Empty);
        return ann;
    }

    /// <summary>Updates an existing text annotation, or removes it if the new text is blank.</summary>
    public void UpdateOrRemoveTextAnnotation(
        Annotation ann, string text, string fontFamily, double fontSize, Color color,
        bool bold, bool italic, bool underline, bool strikethrough)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _annotations.Remove(ann);
            if (ReferenceEquals(SelectedAnnotation, ann))
            {
                SelectedAnnotation = null;
            }
            AnnotationsStructureChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        ann.Text = text;
        ann.FontFamily = fontFamily;
        ann.FontSize = fontSize;
        ann.Color = color;
        ann.Bold = bold;
        ann.Italic = italic;
        ann.Underline = underline;
        ann.Strikethrough = strikethrough;
        UpdateTextBounds(ann);
        AnnotationVisualInvalidated?.Invoke(this, ann);
    }

    public void MoveAnnotationBy(Annotation ann, double dx, double dy)
    {
        ann.Bounds = new Rect(ann.Bounds.X + dx, ann.Bounds.Y + dy, ann.Bounds.Width, ann.Bounds.Height);
        for (var i = 0; i < ann.Points.Count; i++)
        {
            ann.Points[i] = new Vector2(ann.Points[i].X + (float)dx, ann.Points[i].Y + (float)dy);
        }
        AnnotationVisualInvalidated?.Invoke(this, ann);
    }

    public void ResizeAnnotation(
        Annotation ann,
        Rect originalBounds,
        IReadOnlyList<Vector2> originalPoints,
        double originalFontSize,
        double originalSizeScale,
        AnnotationResizeHandle handle,
        Point pixelPoint)
    {
        if (ann.Tool == EditTool.Emoji || ann.IsRotated)
        {
            ResizeRotatedAboutCenter(ann, originalBounds, originalFontSize, handle, pixelPoint);
            return;
        }

        var oppositeX = handle is AnnotationResizeHandle.TopLeft or AnnotationResizeHandle.BottomLeft
            ? originalBounds.Right
            : originalBounds.Left;
        var oppositeY = handle is AnnotationResizeHandle.TopLeft or AnnotationResizeHandle.TopRight
            ? originalBounds.Bottom
            : originalBounds.Top;
        var resized = new Rect(
            Math.Min(pixelPoint.X, oppositeX),
            Math.Min(pixelPoint.Y, oppositeY),
            Math.Max(Math.Abs(pixelPoint.X - oppositeX), 1),
            Math.Max(Math.Abs(pixelPoint.Y - oppositeY), 1));
        if (ann.Tool is EditTool.Text or EditTool.Counter)
        {
            var clampedX = handle is AnnotationResizeHandle.TopLeft or AnnotationResizeHandle.BottomLeft
                ? Math.Min(pixelPoint.X, oppositeX - 1)
                : Math.Max(pixelPoint.X, oppositeX + 1);
            var clampedY = handle is AnnotationResizeHandle.TopLeft or AnnotationResizeHandle.TopRight
                ? Math.Min(pixelPoint.Y, oppositeY - 1)
                : Math.Max(pixelPoint.Y, oppositeY + 1);
            var originalDiagonal = Math.Sqrt(
                originalBounds.Width * originalBounds.Width + originalBounds.Height * originalBounds.Height);
            var draggedDiagonal = Math.Sqrt(
                Math.Pow(clampedX - oppositeX, 2) + Math.Pow(clampedY - oppositeY, 2));
            var minimumScale = ann.Tool == EditTool.Text
                ? 10 / Math.Max(originalFontSize, 1)
                : 0.5 / Math.Max(originalSizeScale, 0.001);
            var maximumScale = ann.Tool == EditTool.Text
                ? 200 / Math.Max(originalFontSize, 1)
                : 4 / Math.Max(originalSizeScale, 0.001);
            var scale = Math.Clamp(
                draggedDiagonal / Math.Max(originalDiagonal, 1),
                minimumScale,
                maximumScale);
            var width = originalBounds.Width * scale;
            var height = originalBounds.Height * scale;
            resized = new Rect(
                handle is AnnotationResizeHandle.TopLeft or AnnotationResizeHandle.BottomLeft ? oppositeX - width : oppositeX,
                handle is AnnotationResizeHandle.TopLeft or AnnotationResizeHandle.TopRight ? oppositeY - height : oppositeY,
                width,
                height);
        }

        if (ann.Tool == EditTool.Pen)
        {
            var width = Math.Max(originalBounds.Width, 1);
            var height = Math.Max(originalBounds.Height, 1);
            ann.Points.Clear();
            foreach (var original in originalPoints)
            {
                ann.Points.Add(new Vector2(
                    (float)(resized.Left + ((original.X - originalBounds.Left) / width) * resized.Width),
                    (float)(resized.Top + ((original.Y - originalBounds.Top) / height) * resized.Height)));
            }
            ann.Bounds = resized;
        }
        else if (ann.Tool == EditTool.Text)
        {
            ann.FontSize = Math.Max(8, originalFontSize * resized.Width / Math.Max(originalBounds.Width, 1));
            ann.Bounds = new Rect(resized.X, resized.Y, resized.Width, resized.Height);
            UpdateTextBounds(ann);
            var measured = ann.Bounds;
            ann.Bounds = new Rect(
                handle is AnnotationResizeHandle.TopLeft or AnnotationResizeHandle.BottomLeft
                    ? oppositeX - measured.Width
                    : oppositeX,
                handle is AnnotationResizeHandle.TopLeft or AnnotationResizeHandle.TopRight
                    ? oppositeY - measured.Height
                    : oppositeY,
                measured.Width,
                measured.Height);
        }
        else
        {
            ann.Bounds = resized;
            if (ann.Tool == EditTool.Counter)
            {
                ann.SizeScale = originalSizeScale * resized.Width / Math.Max(originalBounds.Width, 1);
            }
        }

        ann.RedactPreview = null;
        AnnotationVisualInvalidated?.Invoke(this, ann);
    }

    /// <summary>
    /// Scales a rotated annotation uniformly about its center by comparing the dragged corner's
    /// distance from the center with the original corner's, so the gesture works identically at
    /// any rotation.
    /// </summary>
    private void ResizeRotatedAboutCenter(
        Annotation ann,
        Rect originalBounds,
        double originalFontSize,
        AnnotationResizeHandle handle,
        Point pixelPoint)
    {
        var original = ToRectD(originalBounds);
        var center = original.Center;
        var corners = RotatableAnnotationGeometry.Corners(original, ann.Rotation);
        var originalCorner = corners[handle switch
        {
            AnnotationResizeHandle.TopLeft => 0,
            AnnotationResizeHandle.TopRight => 1,
            AnnotationResizeHandle.BottomLeft => 2,
            _ => 3,
        }];
        var originalDistance = Math.Max(RotatableAnnotationGeometry.Distance(originalCorner, center), 0.5);
        var draggedDistance = RotatableAnnotationGeometry.Distance(new PointD(pixelPoint.X, pixelPoint.Y), center);
        var scale = draggedDistance / originalDistance;

        RectD resized;
        switch (ann.Tool)
        {
            case EditTool.Emoji:
            {
                var imageWidth = _bitmap?.PixelWidth ?? 1000;
                var imageHeight = _bitmap?.PixelHeight ?? 1000;
                var side = EmojiAnnotationMath.ClampSidePixels(original.Width * scale, imageWidth, imageHeight);
                resized = EmojiAnnotationMath.SquareBounds(center, side);
                break;
            }
            case EditTool.Text:
            {
                scale = Math.Clamp(scale, 10 / Math.Max(originalFontSize, 1), 200 / Math.Max(originalFontSize, 1));
                ann.FontSize = originalFontSize * scale;
                // Re-measure so the box matches the new glyph size, then keep the center fixed.
                ann.Bounds = new Rect(original.X, original.Y, 0, 0);
                UpdateTextBounds(ann);
                var measured = ann.Bounds;
                resized = new RectD(center.X - measured.Width / 2, center.Y - measured.Height / 2, measured.Width, measured.Height);
                break;
            }
            default:
            {
                var minimumScale = Math.Max(2 / Math.Max(original.Width, 1), 2 / Math.Max(original.Height, 1));
                scale = Math.Max(scale, minimumScale);
                var width = original.Width * scale;
                var height = original.Height * scale;
                resized = new RectD(center.X - width / 2, center.Y - height / 2, width, height);
                break;
            }
        }

        ann.Bounds = new Rect(resized.X, resized.Y, resized.Width, resized.Height);
        AnnotationVisualInvalidated?.Invoke(this, ann);
    }

    public void MoveAnnotationEndpoint(Annotation ann, bool start, Point pixelPoint)
    {
        var (segmentStart, segmentEnd) = Segment(ann);
        if (ann.Points.Count < 2)
        {
            ann.Points.Clear();
            ann.Points.Add(segmentStart);
            ann.Points.Add(segmentEnd);
        }
        ann.Points[start ? 0 : 1] = new Vector2((float)pixelPoint.X, (float)pixelPoint.Y);
        var updatedStart = ann.Points[0];
        var updatedEnd = ann.Points[1];
        ann.Bounds = new Rect(
            Math.Min(updatedStart.X, updatedEnd.X),
            Math.Min(updatedStart.Y, updatedEnd.Y),
            Math.Abs(updatedEnd.X - updatedStart.X),
            Math.Abs(updatedEnd.Y - updatedStart.Y));
        AnnotationVisualInvalidated?.Invoke(this, ann);
    }

    public void Undo()
    {
        if (_annotations.Count == 0)
        {
            return;
        }

        var last = _annotations[^1];
        if (last.Tool == EditTool.Counter)
        {
            _counterValue = Math.Max(1, _counterValue - 1);
        }
        _annotations.RemoveAt(_annotations.Count - 1);
        SelectedAnnotation = null;
        AnnotationsStructureChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteSelected()
    {
        if (SelectedAnnotation is null)
        {
            return;
        }

        _annotations.Remove(SelectedAnnotation);
        SelectedAnnotation = null;
        AnnotationsStructureChanged?.Invoke(this, EventArgs.Empty);
    }

    // -- Crop -------------------------------------------------------------------------------

    public BitmapBounds? MapSelectionToPixels(Rect selectionCanvasRect, double hostW, double hostH)
    {
        if (_bitmap is null)
        {
            return null;
        }

        double imgW = _bitmap.PixelWidth;
        double imgH = _bitmap.PixelHeight;
        var (scale, offsetX, offsetY) = ImageLayout(hostW, hostH);
        if (scale <= 0)
        {
            return null;
        }

        var pxLeft = Math.Clamp((selectionCanvasRect.X - offsetX) / scale, 0, imgW);
        var pxTop = Math.Clamp((selectionCanvasRect.Y - offsetY) / scale, 0, imgH);
        var pxRight = Math.Clamp((selectionCanvasRect.X + selectionCanvasRect.Width - offsetX) / scale, 0, imgW);
        var pxBottom = Math.Clamp((selectionCanvasRect.Y + selectionCanvasRect.Height - offsetY) / scale, 0, imgH);

        return new BitmapBounds
        {
            X = (uint)Math.Round(pxLeft),
            Y = (uint)Math.Round(pxTop),
            Width = (uint)Math.Round(pxRight - pxLeft),
            Height = (uint)Math.Round(pxBottom - pxTop),
        };
    }

    public async Task ApplyCropAsync(BitmapBounds bounds)
    {
        if (_bitmap is null || bounds.Width < 1 || bounds.Height < 1)
        {
            return;
        }

        // Bake annotations first so they crop with the image, then crop.
        // The export background is applied at save time, not during crop.
        var flattened = await RenderToBitmapAsync(includeBackground: false);
        var cropped = await CropAsync(flattened, bounds);
        flattened.Dispose();
        await SetBitmapAsync(cropped);
        _annotations.Clear();
        _counterValue = 1;
        SelectedAnnotation = null;
        AnnotationsStructureChanged?.Invoke(this, EventArgs.Empty);
    }

    private static async Task<SoftwareBitmap> CropAsync(SoftwareBitmap source, BitmapBounds bounds)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(source);
        await encoder.FlushAsync();

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform { Bounds = bounds };
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
    }

    // -- Win2D baking of annotations ----------------------------------------------------------

    /// <summary>
    /// Flattens the current bitmap plus all annotations into a new <see cref="SoftwareBitmap"/>
    /// at full resolution using Win2D. Returns a copy even when there are no annotations.
    /// </summary>
    public async Task<SoftwareBitmap> RenderToBitmapAsync(bool includeBackground = true)
    {
        if (_bitmap is null)
        {
            throw new InvalidOperationException("No bitmap loaded.");
        }

        await Task.CompletedTask;
        var device = CanvasDevice.GetSharedDevice();
        using var source = CanvasBitmap.CreateFromSoftwareBitmap(device, _bitmap);
        var imgW = (float)_bitmap.PixelWidth;
        var imgH = (float)_bitmap.PixelHeight;

        var hasBackground = BgStyle != ExportBackgroundStyle.Transparent
            || CanvasPadding > 0
            || CanvasCornerRadius > 0
            || CanvasShadow > 0
            || FramePreset != ExportFramePreset.Original;

        // Simple path: no background/padding/corners/shadow, or caller opted out (crop pre-bake).
        if (!includeBackground || !hasBackground)
        {
            using var flatTarget = new CanvasRenderTarget(device, imgW, imgH, 96);
            using (var ds = flatTarget.CreateDrawingSession())
            {
                ds.Clear(Colors.Transparent);
                ds.DrawImage(source);
                // Single stable-order pass over the annotation list so bake/export z-order always
                // matches the canvas's live preview (which stacks by list order via ApplyZOrder) —
                // rather than drawing every redaction underneath every other annotation regardless
                // of when it was added.
                foreach (var ann in _annotations)
                {
                    DrawAnnotation(ds, source, ann);
                }
            }

            return SoftwareBitmap.CreateCopyFromBuffer(
                flatTarget.GetPixelBytes().AsBuffer(),
                BitmapPixelFormat.Bgra8,
                (int)imgW,
                (int)imgH,
                BitmapAlphaMode.Premultiplied);
        }

        // Composited path: padded background frame, rounded screenshot card, optional shadow.
        var frame = GetExportFrameLayout();
        var corner = (float)CanvasCornerRadius;
        var outW = (float)frame.FrameSize.Width;
        var outH = (float)frame.FrameSize.Height;

        using var target = new CanvasRenderTarget(device, outW, outH, 96);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Colors.Transparent);

            var fullRect = new Rect(0, 0, outW, outH);
            if (BgStyle == ExportBackgroundStyle.Solid)
            {
                ds.FillRectangle(fullRect, BgColor);
            }
            else if (BgStyle == ExportBackgroundStyle.Gradient)
            {
                using var brush = new CanvasLinearGradientBrush(device, BgColor, BgColor2)
                {
                    StartPoint = new Vector2(0, 0),
                    EndPoint = new Vector2(outW, outH),
                };
                ds.FillRectangle(fullRect, brush);
            }

            using var cardGeo = CanvasGeometry.CreateRoundedRectangle(
                device,
                (float)frame.ImageBounds.X,
                (float)frame.ImageBounds.Y,
                imgW,
                imgH,
                corner,
                corner);

            if (CanvasShadow > 0)
            {
                using var shadowList = new CanvasCommandList(device);
                using (var sds = shadowList.CreateDrawingSession())
                {
                    sds.FillGeometry(cardGeo, Colors.Black);
                }

                using var shadow = new ShadowEffect
                {
                    Source = shadowList,
                    BlurAmount = (float)CanvasShadow,
                    ShadowColor = Color.FromArgb(120, 0, 0, 0),
                };
                ds.DrawImage(shadow, new Vector2(0, (float)(CanvasShadow * 0.35)));
            }

            using (ds.CreateLayer(1f, cardGeo))
            {
                ds.Transform = Matrix3x2.CreateTranslation((float)frame.ImageBounds.X, (float)frame.ImageBounds.Y);
                ds.DrawImage(source);
                // Same single stable-order pass as the simple path above (kept in sync
                // deliberately — see the comment there).
                foreach (var ann in _annotations)
                {
                    DrawAnnotation(ds, source, ann);
                }
                ds.Transform = Matrix3x2.Identity;
            }
        }

        return SoftwareBitmap.CreateCopyFromBuffer(
            target.GetPixelBytes().AsBuffer(),
            BitmapPixelFormat.Bgra8,
            (int)outW,
            (int)outH,
            BitmapAlphaMode.Premultiplied);
    }

    // -- Redaction ----------------------------------------------------------------------------

    private static float BlurAmountFor(RedactionLevel level) => level switch
    {
        RedactionLevel.Light => 6f,
        RedactionLevel.Medium => 12f,
        RedactionLevel.Heavy => 22f,
        _ => 12f,
    };

    private static int PixelSizeFor(RedactionLevel level) => level switch
    {
        RedactionLevel.Light => 8,
        RedactionLevel.Medium => 16,
        RedactionLevel.Heavy => 28,
        _ => 16,
    };

    /// <summary>
    /// Ensures <paramref name="ann"/> has an up-to-date blurred/pixelated preview bitmap for live
    /// display. Committed redactions get a real preview computed off the UI thread; the
    /// in-progress drag (<see cref="ActiveAnnotation"/>) is left to the caller's lightweight
    /// frosted-rectangle fallback since recomputing the blur every pointer move is too costly.
    /// </summary>
    public void EnsureRedactPreview(Annotation ann)
    {
        if (_canvasSource is null || ReferenceEquals(ann, ActiveAnnotation))
        {
            return;
        }

        var b = NormalizedBounds(ann);
        if (b.Width < 1 || b.Height < 1)
        {
            ann.RedactPreview = null;
            return;
        }

        // Reuse the cached preview when nothing relevant changed.
        if (ann.RedactPreview is not null
            && ann.RedactPreviewLevel == ann.Redaction
            && ann.RedactPreviewStyle == ann.RedactStyle
            && SameRect(ann.RedactPreviewBounds, b))
        {
            return;
        }

        try
        {
            var device = CanvasDevice.GetSharedDevice();
            var w = (int)Math.Round(b.Width);
            var h = (int)Math.Round(b.Height);
            if (w < 1 || h < 1)
            {
                return;
            }

            var srcRect = new Rect(b.X, b.Y, w, h);
            using var rt = new CanvasRenderTarget(device, w, h, 96);
            using (var ds = rt.CreateDrawingSession())
            {
                ds.Clear(Colors.Transparent);
                using var effect = BuildRedactEffect(_canvasSource, srcRect, ann.Redaction, ann.RedactStyle);
                ds.DrawImage(effect, new Rect(0, 0, w, h), srcRect);
            }

            var sb = SoftwareBitmap.CreateCopyFromBuffer(
                rt.GetPixelBytes().AsBuffer(),
                BitmapPixelFormat.Bgra8,
                w,
                h,
                BitmapAlphaMode.Premultiplied);

            var preview = new SoftwareBitmapSource();
            ann.RedactPreview = preview;
            ann.RedactPreviewBounds = b;
            ann.RedactPreviewLevel = ann.Redaction;
            ann.RedactPreviewStyle = ann.RedactStyle;
            // sb is a native SoftwareBitmap copy owned only by this method — SetBitmapAsync copies
            // its pixels into the SoftwareBitmapSource, so sb must be disposed once that completes,
            // whether it succeeds or throws, or every redaction preview leaks native memory. Guard
            // the call itself too, in case SetBitmapAsync throws synchronously before returning a
            // task (the outer catch below logs it; sb must still be disposed here first).
            try
            {
                _ = preview.SetBitmapAsync(sb).AsTask().ContinueWith(
                    t =>
                    {
                        sb.Dispose();
                        if (t.IsFaulted)
                        {
                            System.Diagnostics.Debug.WriteLine($"Redact preview bitmap set failed: {t.Exception}");
                        }
                        _dispatcherQueue.TryEnqueue(() => AnnotationVisualInvalidated?.Invoke(this, ann));
                    },
                    TaskScheduler.Default);
            }
            catch
            {
                sb.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Redact preview failed: {ex}");
        }
    }

    private static bool SameRect(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5
        && Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;

    private static ICanvasImage BuildRedactEffect(CanvasBitmap source, Rect region, RedactionLevel level, RedactionStyle style)
    {
        if (style == RedactionStyle.Solid)
        {
            return new ColorSourceEffect { Color = Colors.Black };
        }

        var crop = new CropEffect { Source = source, SourceRectangle = region };
        // Clamp the edges so the effect doesn't pull transparency in from outside the crop.
        var border = new BorderEffect
        {
            Source = crop,
            ExtendX = CanvasEdgeBehavior.Clamp,
            ExtendY = CanvasEdgeBehavior.Clamp,
        };

        if (style == RedactionStyle.Pixelate)
        {
            // Win2D has no pixelate effect: downscale (linear) then upscale (nearest-neighbor)
            // about the region center so the geometry is unchanged but the detail is quantized
            // into blocks.
            float size = PixelSizeFor(level);
            var center = new Vector2(
                (float)(region.X + region.Width / 2),
                (float)(region.Y + region.Height / 2));
            var down = new ScaleEffect
            {
                Source = border,
                Scale = new Vector2(1f / size, 1f / size),
                CenterPoint = center,
                InterpolationMode = CanvasImageInterpolation.Linear,
            };
            return new ScaleEffect
            {
                Source = down,
                Scale = new Vector2(size, size),
                CenterPoint = center,
                InterpolationMode = CanvasImageInterpolation.NearestNeighbor,
            };
        }

        return new GaussianBlurEffect
        {
            Source = border,
            BlurAmount = BlurAmountFor(level),
            BorderMode = EffectBorderMode.Hard,
        };
    }

    private static void DrawRedaction(CanvasDrawingSession ds, CanvasBitmap source, Annotation ann)
    {
        var b = NormalizedBounds(ann);
        if (b.Width < 1 || b.Height < 1)
        {
            return;
        }

        var rect = new Rect(b.X, b.Y, b.Width, b.Height);
        using var effect = BuildRedactEffect(source, rect, ann.Redaction, ann.RedactStyle);
        ds.DrawImage(effect, rect, rect);
    }

    /// <summary>
    /// Single dispatch point used by every bake/flatten/export/crop pass so redactions and normal
    /// annotations are drawn in one stable list-order walk instead of two separate passes (which
    /// used to draw every redaction underneath every other annotation regardless of list order,
    /// mismatching the canvas's live list-order stacking).
    /// </summary>
    private static void DrawAnnotation(CanvasDrawingSession ds, CanvasBitmap source, Annotation ann)
    {
        if (ann.Tool == EditTool.Redact)
        {
            DrawRedaction(ds, source, ann);
        }
        else
        {
            DrawAnnotationToSession(ds, ann);
        }
    }

    private static void DrawAnnotationToSession(CanvasDrawingSession ds, Annotation ann)
    {
        var color = ann.Color;
        var thickness = (float)ann.Thickness;

        // Rotated shapes/text draw through a rotation about their center composed with whatever
        // transform the export path already applied (e.g. the padded-frame translation).
        var previousTransform = ds.Transform;
        if (ann.IsRotated)
        {
            var b = NormalizedBounds(ann);
            var center = new Vector2((float)(b.X + b.Width / 2), (float)(b.Y + b.Height / 2));
            ds.Transform = Matrix3x2.CreateRotation((float)(ann.Rotation * Math.PI / 180.0), center) * previousTransform;
        }

        try
        {
            DrawAnnotationCore(ds, ann, color, thickness);
        }
        finally
        {
            ds.Transform = previousTransform;
        }
    }

    private static void DrawAnnotationCore(CanvasDrawingSession ds, Annotation ann, Color color, float thickness)
    {
        switch (ann.Tool)
        {
            case EditTool.Rectangle:
            {
                var b = NormalizedBounds(ann);
                if (ann.FillColor.A > 0)
                {
                    ds.FillRectangle((float)b.X, (float)b.Y, (float)b.Width, (float)b.Height, ann.FillColor);
                }
                ds.DrawRectangle((float)b.X, (float)b.Y, (float)b.Width, (float)b.Height, color, thickness);
                break;
            }
            case EditTool.Ellipse:
            {
                var b = NormalizedBounds(ann);
                if (ann.FillColor.A > 0)
                {
                    ds.FillEllipse(
                        (float)(b.X + b.Width / 2),
                        (float)(b.Y + b.Height / 2),
                        (float)(b.Width / 2),
                        (float)(b.Height / 2),
                        ann.FillColor);
                }
                ds.DrawEllipse(
                    (float)(b.X + b.Width / 2),
                    (float)(b.Y + b.Height / 2),
                    (float)(b.Width / 2),
                    (float)(b.Height / 2),
                    color,
                    thickness);
                break;
            }
            case EditTool.Line:
            {
                var (s, en) = Segment(ann);
                ds.DrawLine(
                    s,
                    en,
                    color,
                    thickness,
                    new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round });
                break;
            }
            case EditTool.Arrow:
            {
                DrawArrowToSession(ds, ann, color, thickness);
                break;
            }
            case EditTool.Pen:
            {
                if (ann.Points.Count > 1)
                {
                    var style = new CanvasStrokeStyle
                    {
                        StartCap = CanvasCapStyle.Round,
                        EndCap = CanvasCapStyle.Round,
                        LineJoin = CanvasLineJoin.Round,
                    };
                    for (var i = 1; i < ann.Points.Count; i++)
                    {
                        ds.DrawLine(ann.Points[i - 1], ann.Points[i], color, thickness, style);
                    }
                }
                break;
            }
            case EditTool.Text:
            {
                var fontSize = (float)ann.FontSize;
                using var format = new CanvasTextFormat
                {
                    FontSize = fontSize,
                    FontFamily = ann.FontFamily,
                    FontWeight = ann.Bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                    FontStyle = ann.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                    WordWrapping = CanvasWordWrapping.NoWrap,
                };
                using var layout = new CanvasTextLayout(ds, ann.Text, format, 0, 0);
                if (ann.Underline)
                {
                    layout.SetUnderline(0, ann.Text.Length, true);
                }
                if (ann.Strikethrough)
                {
                    layout.SetStrikethrough(0, ann.Text.Length, true);
                }
                ds.DrawTextLayout(layout, new Vector2((float)ann.Bounds.X, (float)ann.Bounds.Y), color);
                break;
            }
            case EditTool.Counter:
            {
                var b = NormalizedBounds(ann);
                var cx = (float)(b.X + b.Width / 2);
                var cy = (float)(b.Y + b.Height / 2);
                var radius = (float)(b.Width / 2);
                ds.FillCircle(cx, cy, radius, color);
                var fontSize = radius;
                using var format = new CanvasTextFormat
                {
                    FontSize = fontSize,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    HorizontalAlignment = CanvasHorizontalAlignment.Center,
                    VerticalAlignment = CanvasVerticalAlignment.Center,
                };
                ds.DrawText(ann.Number.ToString(), new Rect(b.X, b.Y, b.Width, b.Height), ann.TextColor, format);
                break;
            }
            case EditTool.Emoji:
            {
                var b = ann.Bounds;
                using var format = new CanvasTextFormat
                {
                    FontSize = (float)EmojiAnnotationMath.GlyphFontSize(b.Height),
                    FontFamily = "Segoe UI Emoji",
                    HorizontalAlignment = CanvasHorizontalAlignment.Center,
                    VerticalAlignment = CanvasVerticalAlignment.Center,
                    WordWrapping = CanvasWordWrapping.NoWrap,
                    Options = CanvasDrawTextOptions.EnableColorFont,
                };
                ds.DrawText(ann.Text, b, Colors.Black, format);
                break;
            }
        }
    }

    private static void DrawArrowToSession(CanvasDrawingSession ds, Annotation ann, Color color, float thickness)
    {
        var (start, end) = Segment(ann);
        var shape = BuildArrow(start, end, thickness, ann.ArrowStyle);
        var strokeStyle = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };

        if (shape.Curved)
        {
            using var pb = new CanvasPathBuilder(ds.Device);
            pb.BeginFigure(shape.ShaftStart);
            pb.AddQuadraticBezier(shape.ShaftControl, shape.ShaftEnd);
            pb.EndFigure(CanvasFigureLoop.Open);
            using var geo = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(geo, color, thickness, strokeStyle);
        }
        else
        {
            ds.DrawLine(shape.ShaftStart, shape.ShaftEnd, color, thickness, strokeStyle);
        }

        using var head = CanvasGeometry.CreatePolygon(ds.Device, new[] { shape.Tip, shape.Head1, shape.Head2 });
        ds.FillGeometry(head, color);
    }

    // -- Geometry helpers (also used by EditorCanvas for the live XAML preview) ----------------

    public static Rect NormalizedBounds(Annotation ann)
    {
        var b = ann.Bounds;
        var x = b.Width < 0 ? b.X + b.Width : b.X;
        var y = b.Height < 0 ? b.Y + b.Height : b.Y;
        return new Rect(x, y, Math.Abs(b.Width), Math.Abs(b.Height));
    }

    // Freehand strokes track their path in Points but not in Bounds; recompute the bounding box
    // (padded by half the stroke width) so hit-testing and the selection marquee cover the whole
    // drawing rather than just the start point.
    public static void UpdatePenBounds(Annotation ann)
    {
        if (ann.Points.Count == 0)
        {
            return;
        }

        float minX = ann.Points[0].X, minY = ann.Points[0].Y, maxX = minX, maxY = minY;
        foreach (var pt in ann.Points)
        {
            minX = Math.Min(minX, pt.X);
            minY = Math.Min(minY, pt.Y);
            maxX = Math.Max(maxX, pt.X);
            maxY = Math.Max(maxY, pt.Y);
        }

        var half = ann.Thickness / 2.0;
        ann.Bounds = new Rect(
            minX - half,
            minY - half,
            (maxX - minX) + ann.Thickness,
            (maxY - minY) + ann.Thickness);
    }

    // Text annotations are created with a zero-size box; measure the laid-out text so Bounds
    // matches what's rendered, giving the selection marquee and hit-test the full text area.
    public static void UpdateTextBounds(Annotation ann)
    {
        if (ann.Tool != EditTool.Text || string.IsNullOrEmpty(ann.Text))
        {
            return;
        }

        try
        {
            var device = CanvasDevice.GetSharedDevice();
            using var format = new CanvasTextFormat
            {
                FontSize = (float)ann.FontSize,
                FontFamily = ann.FontFamily,
                FontWeight = ann.Bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                FontStyle = ann.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                WordWrapping = CanvasWordWrapping.NoWrap,
            };
            using var layout = new CanvasTextLayout(device, ann.Text, format, 0, 0);
            if (ann.Underline)
            {
                layout.SetUnderline(0, ann.Text.Length, true);
            }
            if (ann.Strikethrough)
            {
                layout.SetStrikethrough(0, ann.Text.Length, true);
            }

            // Union the layout box with the (possibly overhanging, e.g. italic) ink bounds.
            var lb = layout.LayoutBounds;
            var db = layout.DrawBounds;
            var width = Math.Max(lb.Width, db.X + db.Width);
            var height = Math.Max(lb.Height, db.Y + db.Height);
            ann.Bounds = new Rect(ann.Bounds.X, ann.Bounds.Y, width, height);
        }
        catch
        {
            // Measurement is best-effort; leave bounds as-is on failure.
        }
    }

    public static (Vector2 Start, Vector2 End) Segment(Annotation ann)
    {
        if (ann.Points.Count >= 2)
        {
            return (ann.Points[0], ann.Points[^1]);
        }
        var s = new Vector2((float)ann.Bounds.X, (float)ann.Bounds.Y);
        return (s, s);
    }

    public static bool IsShiftDown() =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static Point ConstrainToAxisOrDiagonal(Point start, Point end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var adx = Math.Abs(dx);
        var ady = Math.Abs(dy);
        // Snap to the nearest of horizontal, vertical, or 45° diagonal.
        if (adx > ady * 2)
        {
            return new Point(end.X, start.Y);
        }
        if (ady > adx * 2)
        {
            return new Point(start.X, end.Y);
        }
        var len = Math.Max(adx, ady);
        return new Point(start.X + Math.Sign(dx) * len, start.Y + Math.Sign(dy) * len);
    }

    public static ArrowShape BuildArrow(Vector2 start, Vector2 end, double thickness, ArrowStyle style)
    {
        var dir = end - start;
        var len = dir.Length();
        var headLen = (float)Math.Max(12, thickness * 3.5);
        const double spread = Math.PI / 7;

        if (len < 0.001f)
        {
            return new ArrowShape(false, start, start, start, end, end, end);
        }

        if (style == ArrowStyle.Straight)
        {
            var u = dir / len;
            var trim = Math.Min(headLen * 0.9f, len * 0.98f);
            var shaftEnd = end - u * trim;
            var angle = Math.Atan2(dir.Y, dir.X);
            var h1 = new Vector2(
                (float)(end.X - headLen * Math.Cos(angle - spread)),
                (float)(end.Y - headLen * Math.Sin(angle - spread)));
            var h2 = new Vector2(
                (float)(end.X - headLen * Math.Cos(angle + spread)),
                (float)(end.Y - headLen * Math.Sin(angle + spread)));
            return new ArrowShape(false, start, shaftEnd, shaftEnd, end, h1, h2);
        }

        // Curved: quadratic bezier bowed perpendicular to the start→end line.
        var mid = (start + end) * 0.5f;
        var perp = new Vector2(-dir.Y, dir.X) / len;
        var bow = (float)(len * 0.22);
        if (style == ArrowStyle.Curved2)
        {
            bow = -bow;
        }
        var control = mid + perp * bow;

        // Tangent at the tip of a quadratic bezier is proportional to (end - control).
        var tipTangent = end - control;
        var tlen = tipTangent.Length();
        tipTangent = tlen < 0.001f ? dir / len : tipTangent / tlen;
        var tipAngle = Math.Atan2(tipTangent.Y, tipTangent.X);
        var ch1 = new Vector2(
            (float)(end.X - headLen * Math.Cos(tipAngle - spread)),
            (float)(end.Y - headLen * Math.Sin(tipAngle - spread)));
        var ch2 = new Vector2(
            (float)(end.X - headLen * Math.Cos(tipAngle + spread)),
            (float)(end.Y - headLen * Math.Sin(tipAngle + spread)));

        // de Casteljau split so the shaft stops short of the tip and the cap never pokes through.
        var trimC = Math.Min(headLen * 0.9f, len * 0.6f);
        var t = (float)Math.Clamp(1.0 - trimC / len, 0.02, 0.98);
        var a = Vector2.Lerp(start, control, t);
        var b = Vector2.Lerp(control, end, t);
        var bt = Vector2.Lerp(a, b, t);
        return new ArrowShape(true, start, a, bt, end, ch1, ch2);
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _canvasSource?.Dispose();
        _canvasSource = null;
    }
}
