using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace TinyClips.Core.Studio.Rendering;

/// <summary>Arrows, callouts, text boxes, rectangles, ellipses, redactions and emoji, over the camera and under the badge (section 6.10 of the project format).</summary>
public sealed partial class StudioSceneRenderer
{
    private const int MaxCachedTextLayouts = 32;

    /// <summary>How far a pointer or a shaft runs into the shape it joins, so that no seam shows between them.</summary>
    private const float JoinOverlap = 1;

    private readonly Dictionary<(string Text, float Font, float MaxWidth), IDWriteTextLayout> _textLayouts = new();
    private readonly Dictionary<float, IDWriteTextFormat> _textFormats = new();
    /// <summary>The most a redaction is blurred, in pixels of standard deviation, which keeps the effect fast.</summary>
    private const float MaxRedactBlur = 250;

    /// <summary>How dark an emoji's shadow is (section 6.10).</summary>
    private const float EmojiShadowOpacity = 0.45f;

    private readonly Dictionary<(string Text, float Font), IDWriteTextLayout> _emojiLayouts = new();

    // An emoji with a shadow, drawn by itself before the frame, for its shadow to be made from.
    // Drawing it into a command list in the middle of a frame fails in Direct2D once another color
    // glyph has been drawn in that frame.
    private readonly Dictionary<(string Text, float Font), (ID2D1Bitmap1 Picture, float Margin)> _emojiPictures = new();
    private IDWriteFactory? _annotationWriteFactory;
    private ID2D1StrokeStyle? _arrowStroke;

    // What is under a redaction is copied here, and then blurred or pixelated back over it.
    private ID2D1Bitmap1? _redactPicture;
    private Crop? _redactCrop;
    private Border? _redactBorder;
    private GaussianBlur? _redactBlur;
    private AffineTransform2D? _redactShrink;
    private Crop? _redactCells;
    private AffineTransform2D? _redactGrow;

    private void DrawAnnotations(StudioProject project, in StudioResolvedFrame frame, double time, int width, int height, ID2D1Bitmap1 target)
    {
        var annotations = project.Overlays.Annotations;
        for (var index = 0; index < annotations.Length; index++)
        {
            if (!StudioAnnotationGeometry.TryResolve(annotations[index], index, frame, time, width, height, out var resolved))
            {
                continue;
            }

            if (resolved.Kind == StudioAnnotationKinds.Redact)
            {
                DrawRedaction(resolved, target, width, height);
                continue;
            }

            var transform = _context.Transform;
            _context.Transform = Matrix3x2.CreateRotation((float)(resolved.Rotation * Math.PI / 180), ToVector(resolved.Anchor)) * transform;
            try
            {
                if (resolved.IsArea)
                {
                    DrawShape(resolved);
                }
                else if (resolved.IsArrow)
                {
                    DrawArrow(resolved);
                }
                else if (resolved.IsEmoji)
                {
                    DrawEmoji(resolved, width, height);
                }
                else
                {
                    DrawAnnotationBox(resolved, width, height);
                }
            }
            finally
            {
                _context.Transform = transform;
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

    /// <summary>
    /// An emoji in color, with its cached bitmap blurred into a shadow underneath.
    /// </summary>
    private void DrawEmoji(in StudioResolvedAnnotation emoji, int width, int height)
    {
        var layout = GetEmojiLayout(emoji.Text, (float)emoji.Font);
        var metrics = layout.Metrics;
        var rect = StudioAnnotationGeometry.LayoutEmoji(emoji, metrics.Width, metrics.Height, width, height);
        var origin = new Vector2((float)rect.X - metrics.Left, (float)rect.Y - metrics.Top);
        var (shadowOffset, shadowBlur) = StudioAnnotationGeometry.GetEmojiShadow(emoji.Font);
        var hasShadow = emoji.HasShadow;
        var reach = hasShadow ? (shadowBlur * 3) + shadowOffset : 0;
        var bounds = new StudioFrameRect(rect.X - reach, rect.Y - reach, rect.Width + (2 * reach), rect.Height + (2 * reach));

        var fades = BeginFade((float)emoji.Opacity, bounds, null);
        Shadow? shadow = null;
        try
        {
            if (hasShadow)
            {
                var (picture, margin) = _emojiPictures[(emoji.Text, (float)emoji.Font)];

                // One effect per emoji: Direct2D draws later than it is asked to, and an effect shared
                // by two emoji in a frame would have its input swapped before the first was drawn.
                shadow = new Shadow(_context) { Optimization = ShadowOptimization.Balanced, Color = new Vector4(0, 0, 0, EmojiShadowOpacity) };
                shadow.SetInput(0, picture, true);
                shadow.BlurStandardDeviation = Math.Min(MaxRedactBlur, (float)shadowBlur);
                _context.DrawImage(shadow, new Vector2((float)rect.X - margin, (float)(rect.Y + shadowOffset) - margin), InterpolationMode.Linear, CompositeMode.SourceOver);
            }

            _brush.Color = Black;
            _context.DrawTextLayout(origin, layout, _brush, DrawTextOptions.EnableColorFont);
        }
        finally
        {
            EndFade(fades);
            shadow?.Dispose();
        }
    }

    /// <summary>
    /// Has the picture of every emoji with a shadow in this frame ready. Outside BeginDraw, because
    /// each is drawn into a target of its own.
    /// </summary>
    private void PrepareEmojiPictures(StudioProject project, in StudioResolvedFrame frame, double time, int width, int height)
    {
        var annotations = project.Overlays.Annotations;
        HashSet<(string Text, float Font)>? used = null;
        for (var index = 0; index < annotations.Length; index++)
        {
            if (!annotations[index].Shadow
                || !StudioAnnotationGeometry.TryResolve(annotations[index], index, frame, time, width, height, out var emoji)
                || !emoji.IsEmoji
                || !emoji.HasShadow)
            {
                continue;
            }

            var key = (emoji.Text, (float)emoji.Font);
            (used ??= []).Add(key);
            if (_emojiPictures.ContainsKey(key))
            {
                continue;
            }

            var layout = GetEmojiLayout(emoji.Text, key.Item2);
            var metrics = layout.Metrics;

            // A glyph's ink may reach a little past its layout box.
            var margin = MathF.Ceiling(key.Item2 * 0.1f) + 1;
            var picture = CreateLayerBitmap(
                Math.Max(1, (int)MathF.Ceiling(metrics.Width + (2 * margin))),
                Math.Max(1, (int)MathF.Ceiling(metrics.Height + (2 * margin))));
            try
            {
                DrawInto(picture, () =>
                {
                    _context.Clear(new Color4(0, 0, 0, 0));
                    _brush.Color = Black;
                    _context.DrawTextLayout(new Vector2(margin - metrics.Left, margin - metrics.Top), layout, _brush, DrawTextOptions.EnableColorFont);
                });
            }
            catch
            {
                picture.Dispose();
                throw;
            }

            _emojiPictures[key] = (picture, margin);
        }

        var keep = Math.Max(MaxCachedTextLayouts, used?.Count ?? 0);
        if (_emojiPictures.Count > keep)
        {
            foreach (var key in _emojiPictures.Keys.ToArray())
            {
                if (used?.Contains(key) == true)
                {
                    continue;
                }

                _emojiPictures[key].Picture.Dispose();
                _emojiPictures.Remove(key);
                if (_emojiPictures.Count <= keep)
                {
                    break;
                }
            }
        }
    }

    private void ForgetEmojiPictures()
    {
        foreach (var (picture, _) in _emojiPictures.Values)
        {
            picture.Dispose();
        }

        _emojiPictures.Clear();
    }

    /// <summary>A rectangle or an ellipse: its inside, if it has one, and then its outline over it, centered on its edge.</summary>
    private void DrawShape(in StudioResolvedAnnotation shape)
    {
        var rect = shape.Rect;
        var stroke = (float)shape.Stroke;
        var reach = (stroke / 2) + 1;
        var bounds = new StudioFrameRect(rect.X - reach, rect.Y - reach, rect.Width + (2 * reach), rect.Height + (2 * reach));
        var edges = new RawRectF((float)rect.X, (float)rect.Y, (float)(rect.X + rect.Width), (float)(rect.Y + rect.Height));
        var isEllipse = shape.Kind == StudioAnnotationKinds.Ellipse;
        var radius = (float)shape.Radius;
        var ellipse = new Ellipse(
            new Vector2((float)(rect.X + (rect.Width / 2)), (float)(rect.Y + (rect.Height / 2))),
            (float)(rect.Width / 2),
            (float)(rect.Height / 2));
        var rounded = new RoundedRectangle(edges, radius, radius);

        var fades = BeginFade((float)shape.Opacity, bounds, null);
        try
        {
            if (shape.ShapeFill.Length > 0 && shape.ShapeFillOpacity > 0)
            {
                var inside = ParseColor(shape.ShapeFill, White);
                _brush.Color = new Color4(inside.R, inside.G, inside.B, (float)shape.ShapeFillOpacity);
                if (isEllipse)
                {
                    _context.FillEllipse(ellipse, _brush);
                }
                else
                {
                    _context.FillRoundedRectangle(rounded, _brush);
                }
            }

            if (stroke > 0)
            {
                _brush.Color = ParseColor(shape.Fill, White);
                if (isEllipse)
                {
                    _context.DrawEllipse(ellipse, _brush, stroke);
                }
                else
                {
                    _context.DrawRoundedRectangle(rounded, _brush, stroke);
                }
            }
        }
        finally
        {
            EndFade(fades);
        }
    }

    /// <summary>
    /// A redaction. A solid one fills its area. A blurred or pixelated one copies what has been
    /// drawn under it so far out of the target, and draws it back over its area through effects
    /// that read nothing outside the area: its edges are repeated outward before it is blurred or
    /// made into cells.
    /// </summary>
    private void DrawRedaction(in StudioResolvedAnnotation redaction, ID2D1Bitmap1 target, int width, int height)
    {
        var rect = redaction.Rect;
        var anchor = redaction.Anchor;
        var rotation = redaction.Rotation;
        var corners = new[]
        {
            StudioAnnotationGeometry.Rotate(new(rect.X, rect.Y), anchor, rotation),
            StudioAnnotationGeometry.Rotate(new(rect.X + rect.Width, rect.Y), anchor, rotation),
            StudioAnnotationGeometry.Rotate(new(rect.X + rect.Width, rect.Y + rect.Height), anchor, rotation),
            StudioAnnotationGeometry.Rotate(new(rect.X, rect.Y + rect.Height), anchor, rotation),
        };
        using var mask = _factory.CreatePathGeometry();
        using (var sink = mask.Open())
        {
            sink.BeginFigure(ToVector(corners[0]), FigureBegin.Filled);
            for (var i = 1; i < corners.Length; i++)
            {
                sink.AddLine(ToVector(corners[i]));
            }

            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        var minX = corners.Min(p => p.X);
        var minY = corners.Min(p => p.Y);
        rect = new(minX, minY, corners.Max(p => p.X) - minX, corners.Max(p => p.Y) - minY);
        var clip = new RawRectF((float)rect.X, (float)rect.Y, (float)(rect.X + rect.Width), (float)(rect.Y + rect.Height));
        if (redaction.RedactStyle == StudioRedactStyle.Solid || !(redaction.RedactAmount > 0))
        {
            _brush.Color = ParseColor(redaction.Fill, Black);
            var solid = BeginFade((float)redaction.Opacity, rect, null);
            try
            {
                _context.FillGeometry(mask, _brush);
            }
            finally
            {
                EndFade(solid);
            }

            return;
        }

        var left = Math.Max(0, (int)Math.Floor(rect.X));
        var top = Math.Max(0, (int)Math.Floor(rect.Y));
        var right = Math.Min(width, (int)Math.Ceiling(rect.X + rect.Width));
        var bottom = Math.Min(height, (int)Math.Ceiling(rect.Y + rect.Height));
        if (right <= left || bottom <= top)
        {
            return;
        }

        var areaWidth = right - left;
        var areaHeight = bottom - top;

        // Before any layer is pushed: what is drawn into a layer is not in the target until it is popped.
        CopyUnderRedaction(target, left, top, areaWidth, areaHeight);
        _redactCrop ??= new Crop(_context);
        _redactCrop.SetInput(0, _redactPicture!, true);
        _redactCrop.Rectangle = new Vector4(0, 0, areaWidth, areaHeight);
        _redactBorder ??= new Border(_context) { EdgeModeX = BorderEdgeMode.Clamp, EdgeModeY = BorderEdgeMode.Clamp };
        _redactBorder.SetInput(0, _redactCrop.Output, true);

        ID2D1Image output;
        if (redaction.RedactStyle == StudioRedactStyle.Pixelate)
        {
            var cell = (float)Math.Max(1, redaction.RedactAmount);
            _redactShrink ??= new AffineTransform2D(_context) { InterPolationMode = AffineTransform2DInterpolationMode.HighQualityCubic, BorderMode = BorderMode.Hard };
            _redactShrink.SetInput(0, _redactBorder.Output, true);
            _redactShrink.TransformMatrix = Matrix3x2.CreateScale(1 / cell);
            _redactCells ??= new Crop(_context);
            _redactCells.SetInput(0, _redactShrink.Output, true);
            _redactCells.Rectangle = new Vector4(0, 0, MathF.Ceiling(areaWidth / cell), MathF.Ceiling(areaHeight / cell));
            _redactGrow ??= new AffineTransform2D(_context) { InterPolationMode = AffineTransform2DInterpolationMode.NearestNeighbor, BorderMode = BorderMode.Hard };
            _redactGrow.SetInput(0, _redactCells.Output, true);
            _redactGrow.TransformMatrix = Matrix3x2.CreateScale(cell);
            output = _redactGrow.Output;
        }
        else
        {
            _redactBlur ??= new GaussianBlur(_context) { BorderMode = BorderMode.Hard, Optimization = GaussianBlurOptimization.Balanced };
            _redactBlur.SetInput(0, _redactBorder.Output, true);
            _redactBlur.StandardDeviation = Math.Min(MaxRedactBlur, (float)redaction.RedactAmount);
            output = _redactBlur.Output;
        }

        var fades = BeginFade((float)redaction.Opacity, rect, null);
        try
        {
            _context.PushLayer(new LayerParameters1
            {
                ContentBounds = clip,
                GeometricMask = mask,
                MaskAntialiasMode = AntialiasMode.PerPrimitive,
                MaskTransform = Matrix3x2.Identity,
                Opacity = 1,
                LayerOptions = LayerOptions1.None,
            }, null!);
            try
            {
                _context.DrawImage(output, new Vector2(left, top), new RawRectF(0, 0, areaWidth, areaHeight), InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
            }
            finally
            {
                _context.PopLayer();
            }
        }
        finally
        {
            EndFade(fades);
            output.Dispose();

            // The effects would otherwise hold on to the picture while they wait for the next redaction.
            _redactCrop.SetInput(0, null!, true);
        }
    }

    /// <summary>Copies a part of the target, with everything drawn into it so far, to the top left of <see cref="_redactPicture"/>.</summary>
    private void CopyUnderRedaction(ID2D1Bitmap1 target, int left, int top, int areaWidth, int areaHeight)
    {
        if (_redactPicture is null || _redactPicture.PixelSize.Width < areaWidth || _redactPicture.PixelSize.Height < areaHeight)
        {
            // Grown in steps, so that a redaction being resized does not make a picture for every size.
            var pictureWidth = Math.Max(areaWidth, ((areaWidth + 255) / 256) * 256);
            var pictureHeight = Math.Max(areaHeight, ((areaHeight + 255) / 256) * 256);
            if (_redactPicture is { } old)
            {
                pictureWidth = Math.Max(pictureWidth, old.PixelSize.Width);
                pictureHeight = Math.Max(pictureHeight, old.PixelSize.Height);
                _redactCrop?.SetInput(0, null!, true);
                old.Dispose();
                _redactPicture = null;
            }

            _redactPicture = _context.CreateBitmap(
                new SizeI(pictureWidth, pictureHeight),
                nint.Zero,
                0,
                new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
        }

        _redactPicture.CopyFromBitmap(new System.Drawing.Point(0, 0), target, new System.Drawing.Rectangle(left, top, areaWidth, areaHeight)).CheckError();
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

        _annotationWriteFactory ??= DWrite.DWriteCreateFactory<IDWriteFactory>();
        if (!_textFormats.TryGetValue(font, out var format))
        {
            format = _annotationWriteFactory.CreateTextFormat("Segoe UI", FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, font);
            format.WordWrapping = WordWrapping.Wrap;
            format.TextAlignment = TextAlignment.Center;
            format.ParagraphAlignment = ParagraphAlignment.Near;
            _textFormats[font] = format;
        }

        var layout = _annotationWriteFactory.CreateTextLayout(text, format, maxWidth, float.MaxValue);
        _textLayouts[key] = layout;
        return layout;
    }

    /// <summary>An emoji's text, unwrapped, with each line centered on the widest.</summary>
    private IDWriteTextLayout GetEmojiLayout(string text, float font)
    {
        var key = (text, font);
        if (_emojiLayouts.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_emojiLayouts.Count >= MaxCachedTextLayouts)
        {
            ForgetTextLayouts();
        }

        _annotationWriteFactory ??= DWrite.DWriteCreateFactory<IDWriteFactory>();
        var layout = StudioAnnotationTextMeasure.CreateEmojiLayout(_annotationWriteFactory, text, font);
        _emojiLayouts[key] = layout;
        return layout;
    }

    private void ForgetTextLayouts()
    {
        foreach (var layout in _emojiLayouts.Values)
        {
            layout.Dispose();
        }

        _emojiLayouts.Clear();
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
        ForgetEmojiPictures();
        _arrowStroke?.Dispose();
        DisposeRedaction();
        _annotationWriteFactory?.Dispose();
    }

    /// <summary>The picture and the effects of redactions belong to the device, and go with it.</summary>
    private void DisposeRedaction()
    {
        _redactGrow?.Dispose();
        _redactGrow = null;
        _redactCells?.Dispose();
        _redactCells = null;
        _redactShrink?.Dispose();
        _redactShrink = null;
        _redactBlur?.Dispose();
        _redactBlur = null;
        _redactBorder?.Dispose();
        _redactBorder = null;
        _redactCrop?.Dispose();
        _redactCrop = null;
        _redactPicture?.Dispose();
        _redactPicture = null;
    }

    private static Vector2 ToVector(StudioPoint point) => new((float)point.X, (float)point.Y);
}
