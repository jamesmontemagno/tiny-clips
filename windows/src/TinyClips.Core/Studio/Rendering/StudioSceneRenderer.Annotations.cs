using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace TinyClips.Core.Studio.Rendering;

/// <summary>Arrows, callouts and text boxes, over the camera and under the badge (section 6.10 of the project format).</summary>
public sealed partial class StudioSceneRenderer
{
    private const int MaxCachedTextLayouts = 32;

    /// <summary>How far a pointer or a shaft runs into the shape it joins, so that no seam shows between them.</summary>
    private const float JoinOverlap = 1;

    private readonly Dictionary<(string Text, float Font, float MaxWidth), IDWriteTextLayout> _textLayouts = new();
    private readonly Dictionary<float, IDWriteTextFormat> _textFormats = new();
    private IDWriteFactory? _writeFactory;
    private ID2D1StrokeStyle? _arrowStroke;

    private void DrawAnnotations(StudioProject project, in StudioResolvedFrame frame, double time, int width, int height)
    {
        var annotations = project.Overlays.Annotations;
        for (var index = 0; index < annotations.Length; index++)
        {
            if (!StudioAnnotationGeometry.TryResolve(annotations[index], index, frame, time, width, height, out var resolved))
            {
                continue;
            }

            if (resolved.IsArrow)
            {
                DrawArrow(resolved);
            }
            else
            {
                DrawAnnotationBox(resolved, width, height);
            }
        }
    }

    private void DrawArrow(in StudioResolvedAnnotation arrow)
    {
        var tip = ToVector(arrow.Anchor);
        var tail = ToVector(arrow.Tail);
        var headBase = ToVector(arrow.HeadBase);
        var along = headBase - tip;
        var head = along.Length();
        if (!(head > 0))
        {
            return;
        }

        var direction = along / head;
        var across = new Vector2(-direction.Y, direction.X) * (float)arrow.HalfWidth;
        var stroke = (float)arrow.Stroke;
        var reach = stroke + (float)arrow.HalfWidth + 1;
        var bounds = new StudioFrameRect(
            Math.Min(tip.X, tail.X) - reach,
            Math.Min(tip.Y, tail.Y) - reach,
            Math.Abs(tip.X - tail.X) + (2 * reach),
            Math.Abs(tip.Y - tail.Y) + (2 * reach));

        _brush.Color = ParseColor(arrow.Fill, White);
        var fades = BeginFade((float)arrow.Opacity, bounds, null);
        try
        {
            _arrowStroke ??= _factory.CreateStrokeStyle(new StrokeStyleProperties
            {
                StartCap = CapStyle.Round,
                EndCap = CapStyle.Flat,
                DashCap = CapStyle.Flat,
                LineJoin = LineJoin.Round,
                MiterLimit = 10,
                DashStyle = DashStyle.Solid,
            });

            // Into the middle of the head, which hides the end of the line.
            if (arrow.HasShaft)
            {
                _context.DrawLine(tail, headBase - (direction * (head / 2)), _brush, stroke, _arrowStroke);
            }

            FillTriangle(tip, headBase + across, headBase - across);
        }
        finally
        {
            EndFade(fades);
        }
    }

    private void DrawAnnotationBox(in StudioResolvedAnnotation annotation, int width, int height)
    {
        var layout = GetTextLayout(annotation.Text, (float)annotation.Font, (float)annotation.MaxTextWidth);
        var metrics = layout.Metrics;
        var placed = StudioAnnotationGeometry.LayoutBox(annotation, metrics.Width, metrics.Height, width, height);
        var box = placed.Box;

        var left = box.X;
        var top = box.Y;
        var right = box.X + box.Width;
        var bottom = box.Y + box.Height;
        if (placed.HasPointer)
        {
            left = Math.Min(left, annotation.Anchor.X);
            top = Math.Min(top, annotation.Anchor.Y);
            right = Math.Max(right, annotation.Anchor.X);
            bottom = Math.Max(bottom, annotation.Anchor.Y);
        }

        var fill = ParseColor(annotation.Fill, White);
        var fades = BeginFade((float)annotation.Opacity, new StudioFrameRect(left, top, right - left, bottom - top), null);
        try
        {
            _brush.Color = fill;
            if (placed.HasPointer)
            {
                var a = ToVector(placed.PointerA);
                var b = ToVector(placed.PointerB);
                var tip = ToVector(annotation.Anchor);
                var midpoint = (a + b) / 2;
                var inward = midpoint - tip;
                var length = inward.Length();
                var overlap = length > 0 ? inward / length * JoinOverlap : Vector2.Zero;
                FillTriangle(tip, a + overlap, b + overlap);
            }

            var radius = (float)annotation.Radius;
            _context.FillRoundedRectangle(
                new RoundedRectangle(new RawRectF((float)box.X, (float)box.Y, (float)(box.X + box.Width), (float)(box.Y + box.Height)), radius, radius),
                _brush);

            // The layout is as wide as the most text can be and centers its lines in that, so
            // its middle goes on the middle of the box.
            _brush.Color = ParseColor(annotation.TextColor, Black);
            var origin = new Vector2(
                (float)(box.X + (box.Width / 2) - (annotation.MaxTextWidth / 2)),
                (float)placed.TextRect.Y - metrics.Top);
            _context.DrawTextLayout(origin, layout, _brush, DrawTextOptions.EnableColorFont);
        }
        finally
        {
            EndFade(fades);
        }
    }

    private void FillTriangle(Vector2 a, Vector2 b, Vector2 c)
    {
        using var geometry = _factory.CreatePathGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(a, FigureBegin.Filled);
            sink.AddLine(b);
            sink.AddLine(c);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        _context.FillGeometry(geometry, _brush);
    }

    /// <summary>
    /// Shaping text is far slower than drawing it, and an annotation's text stays the same for as
    /// long as it shows. Layouts belong to no device, so a lost device leaves them good.
    /// </summary>
    private IDWriteTextLayout GetTextLayout(string text, float font, float maxWidth)
    {
        var key = (text, font, maxWidth);
        if (_textLayouts.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_textLayouts.Count >= MaxCachedTextLayouts)
        {
            ForgetTextLayouts();
        }

        _writeFactory ??= DWrite.DWriteCreateFactory<IDWriteFactory>();
        if (!_textFormats.TryGetValue(font, out var format))
        {
            format = _writeFactory.CreateTextFormat("Segoe UI", FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, font);
            format.WordWrapping = WordWrapping.Wrap;
            format.TextAlignment = TextAlignment.Center;
            format.ParagraphAlignment = ParagraphAlignment.Near;
            _textFormats[font] = format;
        }

        var layout = _writeFactory.CreateTextLayout(text, format, maxWidth, float.MaxValue);
        _textLayouts[key] = layout;
        return layout;
    }

    private void ForgetTextLayouts()
    {
        foreach (var layout in _textLayouts.Values)
        {
            layout.Dispose();
        }

        _textLayouts.Clear();
        foreach (var format in _textFormats.Values)
        {
            format.Dispose();
        }

        _textFormats.Clear();
    }

    private void DisposeAnnotations()
    {
        ForgetTextLayouts();
        _arrowStroke?.Dispose();
        _writeFactory?.Dispose();
    }

    private static Vector2 ToVector(StudioPoint point) => new((float)point.X, (float)point.Y);
}
