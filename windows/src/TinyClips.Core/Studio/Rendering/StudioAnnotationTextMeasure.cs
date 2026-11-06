using Vortice.DirectWrite;

namespace TinyClips.Core.Studio.Rendering;

/// <summary>
/// What the text of a callout or a text box measures, the way the renderer lays it out, for the
/// preview to let a pointer take hold of what is on the picture. Measuring is far slower than
/// asking again, and the size stays the same for as long as the annotation shows.
/// </summary>
public static class StudioAnnotationTextMeasure
{
    private const int MaxCached = 64;
    private static readonly Dictionary<(string Text, float Font, float MaxWidth), (float Width, float Height)> Measured = [];
    private static readonly Lock Gate = new();
    private static IDWriteFactory? s_factory;

    /// <summary>Where the box of a callout or a text goes on the canvas.</summary>
    public static StudioAnnotationBox LayoutBox(in StudioResolvedAnnotation annotation, double canvasWidth, double canvasHeight)
    {
        var (width, height) = Measure(annotation.Text, (float)annotation.Font, (float)annotation.MaxTextWidth);
        return StudioAnnotationGeometry.LayoutBox(annotation, width, height, canvasWidth, canvasHeight);
    }

    private static (float Width, float Height) Measure(string text, float font, float maxWidth)
    {
        var key = (text, font, maxWidth);
        lock (Gate)
        {
            if (Measured.TryGetValue(key, out var known))
            {
                return known;
            }

            if (Measured.Count >= MaxCached)
            {
                Measured.Clear();
            }

            s_factory ??= DWrite.DWriteCreateFactory<IDWriteFactory>();
            using var format = s_factory.CreateTextFormat("Segoe UI", FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, font);
            format.WordWrapping = WordWrapping.Wrap;
            format.TextAlignment = TextAlignment.Center;
            format.ParagraphAlignment = ParagraphAlignment.Near;
            using var layout = s_factory.CreateTextLayout(text, format, maxWidth, float.MaxValue);
            var metrics = layout.Metrics;
            var size = (metrics.Width, metrics.Height);
            Measured[key] = size;
            return size;
        }
    }
}
