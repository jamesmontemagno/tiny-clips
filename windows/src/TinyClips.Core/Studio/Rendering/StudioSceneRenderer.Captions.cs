using System.Numerics;
using SharpGen.Runtime;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DFontStyle = Vortice.DirectWrite.FontStyle;
using D2DLineJoin = Vortice.Direct2D1.LineJoin;

namespace TinyClips.Core.Studio.Rendering;

// Section 15.4 of the project format. A caption is drawn once into a bitmap of its own, which then
// goes on the canvas for as many frames as it shows.
public sealed partial class StudioSceneRenderer
{
    /// <summary>The system's sans-serif. DirectWrite finds another font for what this one has no letters for.</summary>
    private const string CaptionFont = "Segoe UI";

    private const string UnknownCaptionLocale = "en-us";

    private IDWriteFactory? _writeFactory;
    private ID2D1StrokeStyle? _captionStroke;
    private ID2D1Bitmap1? _caption;
    private CaptionKey _captionKey;
    private RawRectF _captionPlace;

    /// <summary>What makes one caption's picture another's.</summary>
    private readonly record struct CaptionKey(
        string? Text,
        string? Language,
        StudioCaptionSize Size,
        StudioCaptionPosition Position,
        StudioCaptionAppearance Appearance,
        int CanvasWidth,
        int CanvasHeight);

    /// <summary>
    /// Has the picture of <paramref name="text"/> ready. Outside BeginDraw, because it draws into a
    /// target of its own.
    /// </summary>
    private void PrepareCaption(string text, StudioProject project, int width, int height)
    {
        var overlay = project.Overlays.Captions;
        var key = new CaptionKey(text, project.Captions.Language, overlay.Size, overlay.Position, overlay.Appearance, width, height);
        if (_caption is not null && key == _captionKey)
        {
            return;
        }

        _caption?.Dispose();
        _caption = null;
        _captionKey = default;

        var style = StudioCaptionStyle.For(overlay, width, height);
        _writeFactory ??= DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        using var format = _writeFactory.CreateTextFormat(
            CaptionFont,
            null,
            style.IsBold ? FontWeight.Bold : FontWeight.Regular,
            D2DFontStyle.Normal,
            FontStretch.Normal,
            (float)style.TextHeight,
            project.Captions.Language ?? UnknownCaptionLocale);
        format.TextAlignment = TextAlignment.Center;
        format.WordWrapping = WordWrapping.Wrap;
        using var layout = _writeFactory.CreateTextLayout(text, format, (float)style.LongestLine, height);
        var metrics = layout.Metrics;

        // Where the layout's box goes on the canvas, and the whole pixels around all that is drawn.
        var layoutLeft = (width - style.LongestLine) / 2;
        var layoutTop = style.TopOfText(overlay.Position, metrics.Height, height);
        var reach = Math.Ceiling(style.Reach) + 2;
        var left = (int)Math.Clamp(Math.Floor(layoutLeft + metrics.Left - reach), 0, width - 1);
        var top = (int)Math.Clamp(Math.Floor(layoutTop + metrics.Top - reach), 0, height - 1);
        var right = (int)Math.Clamp(Math.Ceiling(layoutLeft + metrics.Left + metrics.Width + reach), left + 1, width);
        var bottom = (int)Math.Clamp(Math.Ceiling(layoutTop + metrics.Top + metrics.Height + reach), top + 1, height);

        var origin = new Vector2((float)(layoutLeft - left), (float)(layoutTop - top));
        var box = new RawRectF(origin.X + metrics.Left, origin.Y + metrics.Top, origin.X + metrics.Left + metrics.Width, origin.Y + metrics.Top + metrics.Height);
        var picture = CreateLayerBitmap(right - left, bottom - top);
        try
        {
            DrawInto(picture, () =>
            {
                _context.Clear(new Color4(0, 0, 0, 0));
                DrawCaptionPicture(layout, style, origin, box, right - left, bottom - top);
            });
        }
        catch
        {
            picture.Dispose();
            throw;
        }

        _caption = picture;
        _captionKey = key;
        _captionPlace = new RawRectF(left, top, right, bottom);
    }

    private void DrawCaption()
    {
        if (_caption is not null)
        {
            _context.DrawBitmap(_caption, _captionPlace, 1f, BitmapInterpolationMode.NearestNeighbor, null);
        }
    }

    /// <summary>From the back: the panel, the shadow, the outline, and the letters.</summary>
    private void DrawCaptionPicture(IDWriteTextLayout layout, in StudioCaptionStyle style, Vector2 origin, RawRectF box, int width, int height)
    {
        if (style.PanelReach > 0)
        {
            var reach = (float)style.PanelReach;
            SetCaptionPaint(style.Panel);
            _context.FillRectangle(new RawRectF(box.Left - reach, box.Top - reach, box.Right + reach, box.Bottom + reach), _brush);
        }

        if (style.OutlineWidth > 0 || style.ShadowOffset > 0)
        {
            using var outlines = new CaptionOutlines(_factory);
            try
            {
                layout.Draw(outlines, origin.X, origin.Y);
                var strokeWidth = (float)(style.OutlineWidth * 2);
                _captionStroke ??= _factory.CreateStrokeStyle(new StrokeStyleProperties
                {
                    StartCap = CapStyle.Round,
                    EndCap = CapStyle.Round,
                    DashCap = CapStyle.Round,
                    LineJoin = D2DLineJoin.Round,
                    MiterLimit = 1,
                    DashStyle = DashStyle.Solid,
                });

                // A paint that is not solid is laid on as one sheet, or it would darken wherever
                // two letters' outlines, or a shadow's own outline and letters, overlap.
                if (style.ShadowOffset > 0)
                {
                    var offset = new Vector2((float)style.ShadowOffset);
                    SetCaptionPaint(style.Shadow with { Opacity = 1 });
                    PushCaptionSheet(style.Shadow.Opacity, width, height);
                    foreach (var run in outlines.Runs)
                    {
                        _context.Transform = Matrix3x2.CreateTranslation(run.Origin + offset);
                        if (strokeWidth > 0)
                        {
                            _context.DrawGeometry(run.Geometry, _brush, strokeWidth, _captionStroke);
                        }

                        _context.FillGeometry(run.Geometry, _brush);
                    }

                    _context.PopLayer();
                }

                if (style.OutlineWidth > 0)
                {
                    SetCaptionPaint(style.Outline with { Opacity = 1 });
                    PushCaptionSheet(style.Outline.Opacity, width, height);
                    foreach (var run in outlines.Runs)
                    {
                        _context.Transform = Matrix3x2.CreateTranslation(run.Origin);
                        _context.DrawGeometry(run.Geometry, _brush, strokeWidth, _captionStroke);
                    }

                    _context.PopLayer();
                }
            }
            finally
            {
                _context.Transform = Matrix3x2.Identity;
            }
        }

        // A transparent target has nothing for ClearType to blend with.
        var antialias = _context.TextAntialiasMode;
        _context.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        SetCaptionPaint(style.Text);
        _context.DrawTextLayout(origin, layout, _brush, DrawTextOptions.EnableColorFont);
        _context.TextAntialiasMode = antialias;
    }

    private void PushCaptionSheet(double opacity, int width, int height) =>
        _context.PushLayer(
            new LayerParameters1
            {
                ContentBounds = new RawRectF(0, 0, width, height),
                GeometricMask = null,
                MaskAntialiasMode = AntialiasMode.PerPrimitive,
                MaskTransform = Matrix3x2.Identity,
                Opacity = (float)opacity,
                OpacityBrush = null,
                LayerOptions = LayerOptions1.None,
            },
            null!);

    private void SetCaptionPaint(in StudioCaptionPaint paint) =>
        _brush.Color = new Color4(paint.Red / 255f, paint.Green / 255f, paint.Blue / 255f, (float)paint.Opacity);

    private void DisposeCaption()
    {
        _caption?.Dispose();
        _captionStroke?.Dispose();
        _writeFactory?.Dispose();
    }

    /// <summary>Collects the shapes of the letters of a laid-out text, run by run.</summary>
    private sealed class CaptionOutlines(ID2D1Factory1 factory) : TextRendererBase
    {
        public List<(ID2D1PathGeometry Geometry, Vector2 Origin)> Runs { get; } = [];

        public override void DrawGlyphRun(
            nint clientDrawingContext,
            float baselineOriginX,
            float baselineOriginY,
            MeasuringMode measuringMode,
            GlyphRun glyphRun,
            GlyphRunDescription glyphRunDescription,
            IUnknown clientDrawingEffect)
        {
            if (glyphRun.Indices is not { Length: > 0 } || glyphRun.FontFace is null)
            {
                return;
            }

            var geometry = factory.CreatePathGeometry();
            Runs.Add((geometry, new Vector2(baselineOriginX, baselineOriginY)));
            using var sink = geometry.Open();
            sink.SetFillMode(FillMode.Winding);
            glyphRun.FontFace.GetGlyphRunOutline(
                glyphRun.FontEmSize,
                glyphRun.Indices,
                glyphRun.Advances,
                glyphRun.Offsets,
                glyphRun.IsSideways,
                (glyphRun.BidiLevel & 1) != 0,
                sink);
            sink.Close();
        }

        public override RawBool IsPixelSnappingDisabled(nint clientDrawingContext) => true;

        public override float GetPixelsPerDip(nint clientDrawingContext) => 1;

        public override Matrix3x2 GetCurrentTransform(nint clientDrawingContext) => Matrix3x2.Identity;

        protected override void DisposeCore(bool disposing)
        {
            foreach (var run in Runs)
            {
                run.Geometry.Dispose();
            }

            Runs.Clear();
            base.DisposeCore(disposing);
        }
    }
}
