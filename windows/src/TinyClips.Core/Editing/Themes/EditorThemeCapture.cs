using Windows.UI;

namespace TinyClips.Core.Editing.Themes;

/// <summary>
/// Annotation looks that were already on the canvas when a theme was saved, and so are not edits
/// to it. Without this, a document with mixed looks would read as Modified again after Update.
/// </summary>
public sealed class EditorThemeSettledStyles
{
    private readonly HashSet<object> _styles;

    internal EditorThemeSettledStyles(HashSet<object> styles)
    {
        _styles = styles;
    }

    internal bool Contains(object style) => _styles.Contains(style);
}

/// <summary>What the current document's look is compared with.</summary>
/// <param name="Theme">The theme as saved.</param>
/// <param name="Canvas">The canvas when the theme became active.</param>
/// <param name="ImageWidth">The image width when the theme was applied, which sets its pixel sizes.</param>
/// <param name="Settled">Looks to ignore; see <see cref="EditorThemeSettledStyles"/>.</param>
public sealed record EditorThemeBaseline(
    EditorTheme Theme,
    EditorThemeCanvasState Canvas,
    double ImageWidth,
    EditorThemeSettledStyles? Settled = null);

/// <summary>
/// Computes the look to save from the document rather than from hooks on each control. See
/// "Modified, Update, and Save as New" in plans/editor-themes.md.
/// </summary>
public static class EditorThemeCapture
{
    public static EditorTheme Capture(
        EditorThemeBaseline baseline,
        IEnumerable<ScreenshotAnnotation> annotations,
        EditorThemeDefaults defaults,
        EditorThemeCanvasState canvas)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        return Capture(baseline.Theme, annotations, defaults, canvas, baseline.Canvas, baseline.ImageWidth, baseline.Settled);
    }

    /// <summary>
    /// The theme with each group taken from the newest annotation that differs from it, otherwise
    /// from the defaults for new annotations when they differ, otherwise kept as saved.
    /// </summary>
    public static EditorTheme Capture(
        EditorTheme baseline,
        IEnumerable<ScreenshotAnnotation> annotations,
        EditorThemeDefaults defaults,
        EditorThemeCanvasState canvas,
        EditorThemeCanvasState canvasAtActivation,
        double imageWidth,
        EditorThemeSettledStyles? settled = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(defaults);

        var saved = baseline.Rounded();
        EditorThemeShape? shape = null;
        EditorThemeLine? line = null;
        EditorThemeText? text = null;
        EditorThemeNumber? number = null;

        bool Differs(object style, object savedStyle) => !style.Equals(savedStyle) && settled?.Contains(style) != true;

        var items = annotations as IReadOnlyList<ScreenshotAnnotation> ?? annotations.ToArray();
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var annotation = items[i];
            switch (EditorThemeStyling.GroupOf(annotation.Tool))
            {
                case EditorThemeGroup.Shape when shape is null:
                    var shapeStyle = ShapeStyle(annotation, baseline, imageWidth);
                    shape = Differs(shapeStyle, saved.Shape) ? shapeStyle : null;
                    break;
                case EditorThemeGroup.Line when line is null:
                    var lineStyle = LineStyle(annotation, baseline, imageWidth);
                    line = Differs(lineStyle, saved.Line) ? lineStyle : null;
                    break;
                case EditorThemeGroup.Text when text is null:
                    var textStyle = TextStyle(annotation, baseline, imageWidth);
                    text = Differs(textStyle, saved.Text) ? textStyle : null;
                    break;
                case EditorThemeGroup.Number when number is null:
                    var numberStyle = NumberStyle(annotation, baseline);
                    number = Differs(numberStyle, saved.Number) ? numberStyle : null;
                    break;
            }
        }

        var defaultShape = ShapeStyle(defaults.ShapeColor, defaults.ShapeWidth, defaults.ShapeFill, defaults.ShapeEffects, baseline, imageWidth);
        var defaultLine = new EditorThemeLine(
            Hex(defaults.LineColor, baseline.Line.Color),
            StrokeSize(defaults.LineWidth, imageWidth),
            EditorThemeStyling.NameOf(defaults.ArrowStyle));
        var defaultText = TextStyle(
            defaults.TextColor, defaults.FontFamily, defaults.FontSize, defaults.Bold, defaults.Italic, defaults.Underline, defaults.TextBox, baseline, imageWidth);
        var defaultNumber = NumberStyle(defaults.NumberColor, defaults.NumberTextColor, defaults.NumberScale, baseline);

        return baseline with
        {
            Shape = shape ?? (defaultShape != saved.Shape ? defaultShape : baseline.Shape),
            Line = line ?? (defaultLine != saved.Line ? defaultLine : baseline.Line),
            Text = text ?? (defaultText != saved.Text ? defaultText : baseline.Text),
            Number = number ?? (defaultNumber != saved.Number ? defaultNumber : baseline.Number),
            Canvas = CaptureCanvas(baseline.Canvas, canvas, canvasAtActivation),
        };
    }

    /// <summary>Whether a captured look differs from the saved theme, after rounding sizes to two decimals.</summary>
    public static bool IsModified(EditorTheme baseline, EditorTheme captured)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(captured);
        return baseline.Rounded() != captured.Rounded();
    }

    /// <summary>The looks present now, for a baseline that shouldn't read them as Modified.</summary>
    public static EditorThemeSettledStyles Settle(EditorTheme theme, IEnumerable<ScreenshotAnnotation> annotations, double imageWidth)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(annotations);
        var styles = new HashSet<object>();
        foreach (var annotation in annotations)
        {
            object? style = EditorThemeStyling.GroupOf(annotation.Tool) switch
            {
                EditorThemeGroup.Shape => ShapeStyle(annotation, theme, imageWidth),
                EditorThemeGroup.Line => LineStyle(annotation, theme, imageWidth),
                EditorThemeGroup.Text => TextStyle(annotation, theme, imageWidth),
                EditorThemeGroup.Number => NumberStyle(annotation, theme),
                _ => null,
            };
            if (style is not null)
            {
                styles.Add(style);
            }
        }

        return new EditorThemeSettledStyles(styles);
    }

    /// <summary>
    /// Only what changed since activation is taken, because a theme that didn't apply on open
    /// leaves the canvas showing something else. Image backgrounds aren't part of a theme.
    /// </summary>
    private static EditorThemeCanvas CaptureCanvas(EditorThemeCanvas theme, EditorThemeCanvasState now, EditorThemeCanvasState then) =>
        theme with
        {
            Background = now.Background != then.Background && now.Background != ExportBackgroundStyle.Image
                ? EditorThemeStyling.NameOf(now.Background)
                : theme.Background,
            Color = now.Color != then.Color ? EditorTheme.HexOf(now.Color) : theme.Color,
            SecondaryColor = now.SecondaryColor != then.SecondaryColor ? EditorTheme.HexOf(now.SecondaryColor) : theme.SecondaryColor,
            GradientDirection = now.GradientDirection != then.GradientDirection
                ? Size(now.GradientDirection, 0, EditorTheme.MaximumDirection)
                : theme.GradientDirection,
            Padding = now.Padding != then.Padding ? Size(now.Padding, 0, EditorTheme.MaximumCanvasPadding) : theme.Padding,
            CornerRadius = now.CornerRadius != then.CornerRadius
                ? Size(now.CornerRadius, 0, EditorTheme.MaximumCanvasCornerRadius)
                : theme.CornerRadius,
            Shadow = now.Shadow != then.Shadow ? Size(now.Shadow, 0, EditorTheme.MaximumCanvasShadow) : theme.Shadow,
            Frame = now.Frame != then.Frame ? EditorThemeStyling.NameOf(now.Frame) : theme.Frame,
            HorizontalAlignment = now.HorizontalAlignment != then.HorizontalAlignment
                ? EditorThemeStyling.NameOf(now.HorizontalAlignment)
                : theme.HorizontalAlignment,
            VerticalAlignment = now.VerticalAlignment != then.VerticalAlignment
                ? EditorThemeStyling.NameOf(now.VerticalAlignment)
                : theme.VerticalAlignment,
        };

    private static EditorThemeShape ShapeStyle(ScreenshotAnnotation annotation, EditorTheme theme, double imageWidth) =>
        ShapeStyle(annotation.Color, annotation.Thickness, annotation.FillColor, annotation.ShapeEffects, theme, imageWidth);

    private static EditorThemeShape ShapeStyle(Color stroke, double width, Color fill, ShapeEffects effects, EditorTheme theme, double imageWidth)
    {
        // A gradient without a fill and a shadow without a blur draw nothing, so their other
        // settings are not edits to the theme.
        var hasGradient = effects.IsGradientFill && fill.A > 0;
        var hasShadow = effects.DrawsShadow;
        return new EditorThemeShape(
            Hex(stroke, theme.Shape.StrokeColor),
            StrokeSize(width, imageWidth),
            Hex(fill, theme.Shape.FillColor),
            hasGradient ? EditorTheme.HexOf(effects.GradientEndColor) : null,
            hasGradient ? Size(effects.GradientDirectionDegrees, 0, EditorTheme.MaximumDirection) : EditorTheme.Round(theme.Shape.GradientDirection),
            hasShadow ? Size(EditorThemeUnits.StrokeUnits(effects.ShadowBlur, imageWidth), 0, EditorTheme.MaximumShadowBlur) : 0,
            hasShadow ? Hex(effects.ShadowColor, theme.Shape.ShadowColor) : theme.Shape.ShadowColor);
    }

    private static EditorThemeLine LineStyle(ScreenshotAnnotation annotation, EditorTheme theme, double imageWidth) =>
        new(
            Hex(annotation.Color, theme.Line.Color),
            StrokeSize(annotation.Thickness, imageWidth),
            annotation.Tool == EditTool.Arrow ? EditorThemeStyling.NameOf(annotation.ArrowStyle) : theme.Line.ArrowStyle);

    private static EditorThemeText TextStyle(ScreenshotAnnotation annotation, EditorTheme theme, double imageWidth) =>
        TextStyle(annotation.Color, annotation.FontFamily, annotation.FontSize, annotation.Bold, annotation.Italic, annotation.Underline, annotation.TextBoxStyle, theme, imageWidth);

    private static EditorThemeText TextStyle(
        Color color, string fontFamily, double fontSize, bool bold, bool italic, bool underline, TextBoxStyle box, EditorTheme theme, double imageWidth) =>
        new(
            Hex(color, theme.Text.Color),
            FontFamilyName(fontFamily, theme),
            Size(EditorThemeUnits.TextUnits(fontSize, imageWidth), EditorTheme.MinimumFontSize, EditorTheme.MaximumFontSize),
            bold,
            italic,
            underline,
            new EditorThemeTextBox(
                Hex(box.BackgroundColor, theme.Text.Box.BackgroundColor),
                Hex(box.BorderColor, theme.Text.Box.BorderColor),
                Size(EditorThemeUnits.TextUnits(box.BorderWidth, imageWidth), 0, EditorTheme.MaximumBoxBorderWidth),
                Size(EditorThemeUnits.TextUnits(box.Padding, imageWidth), 0, EditorTheme.MaximumBoxPadding),
                Size(EditorThemeUnits.TextUnits(box.CornerRadius, imageWidth), 0, EditorTheme.MaximumBoxCornerRadius)));

    private static EditorThemeNumber NumberStyle(ScreenshotAnnotation annotation, EditorTheme theme) =>
        NumberStyle(annotation.Color, annotation.TextColor, annotation.SizeScale, theme);

    private static EditorThemeNumber NumberStyle(Color badge, Color text, double scale, EditorTheme theme) =>
        new(
            Hex(badge, theme.Number.BadgeColor),
            Hex(text, theme.Number.TextColor),
            Size(scale, EditorTheme.MinimumNumberSize, EditorTheme.MaximumNumberSize));

    private static double StrokeSize(double pixels, double imageWidth) =>
        Size(EditorThemeUnits.StrokeUnits(pixels, imageWidth), 0, EditorTheme.MaximumStrokeWidth);

    /// <summary>A valid theme, whatever the image: sizes clamp to the format's range and round.</summary>
    private static double Size(double value, double minimum, double maximum) =>
        EditorTheme.Round(Math.Clamp(value, minimum, maximum));

    private static string? FontFamilyName(string fontFamily, EditorTheme theme)
    {
        if (fontFamily == EditorThemeStyling.FontFamilyOf(theme.Text))
        {
            return theme.Text.FontFamily;
        }

        return fontFamily == EditorThemeStyling.PlatformFontFamily ? null : fontFamily;
    }

    /// <summary>Every fully transparent color looks the same, so none differs from another.</summary>
    private static string Hex(Color color, string themeColor)
    {
        if (color.A != 0)
        {
            return EditorTheme.HexOf(color);
        }

        return EditorTheme.ColorOf(themeColor).A == 0 ? themeColor : "#00000000";
    }
}
