using Windows.UI;

namespace TinyClips.Core.Editing.Themes;

/// <summary>
/// Annotation looks that were already on the canvas when a theme was saved, and so are not edits
/// to it. Without this, a document with mixed looks would read as Modified again after Update.
/// </summary>
public sealed class EditorThemeSettledStyles
{
    private readonly HashSet<(EditTool Tool, object Style)> _styles;

    internal EditorThemeSettledStyles(HashSet<(EditTool Tool, object Style)> styles)
    {
        _styles = styles;
    }

    // Keyed by tool: a look settled on a rectangle is still an edit when an ellipse takes it.
    internal bool Contains(EditTool tool, object style) => _styles.Contains((tool, style));
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
    /// The theme with each annotation type's section taken from the newest annotation of that type
    /// that differs from it, otherwise from that type's defaults when they differ, otherwise kept
    /// as saved.
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
        var items = annotations as IReadOnlyList<ScreenshotAnnotation> ?? annotations.ToArray();

        // The section to save: the newest edited annotation, then the defaults, then the theme.
        T Section<T>(EditTool tool, T savedSection, T themeSection, T fromDefaults, Func<ScreenshotAnnotation, T> styleOf)
            where T : class
        {
            for (var i = items.Count - 1; i >= 0; i--)
            {
                if (items[i].Tool == tool)
                {
                    var style = styleOf(items[i]);
                    if (!style.Equals(savedSection) && settled?.Contains(tool, style) != true)
                    {
                        return style;
                    }
                }
            }

            return fromDefaults.Equals(savedSection) ? themeSection : fromDefaults;
        }

        return baseline with
        {
            Rectangle = Section(
                EditTool.Rectangle, saved.Rectangle, baseline.Rectangle,
                ShapeStyle(defaults.Rectangle, baseline.Rectangle, imageWidth),
                annotation => ShapeStyle(annotation, baseline.Rectangle, imageWidth)),
            Ellipse = Section(
                EditTool.Ellipse, saved.Ellipse, baseline.Ellipse,
                ShapeStyle(defaults.Ellipse, baseline.Ellipse, imageWidth),
                annotation => ShapeStyle(annotation, baseline.Ellipse, imageWidth)),
            Arrow = Section(
                EditTool.Arrow, saved.Arrow, baseline.Arrow,
                ArrowSection(defaults.Arrow.Color, defaults.Arrow.Width, defaults.ArrowStyle, baseline.Arrow, imageWidth),
                annotation => ArrowSection(annotation.Color, annotation.Thickness, annotation.ArrowStyle, baseline.Arrow, imageWidth)),
            Line = Section(
                EditTool.Line, saved.Line, baseline.Line,
                StrokeStyle(defaults.Line.Color, defaults.Line.Width, baseline.Line, imageWidth),
                annotation => StrokeStyle(annotation.Color, annotation.Thickness, baseline.Line, imageWidth)),
            Pen = Section(
                EditTool.Pen, saved.Pen, baseline.Pen,
                StrokeStyle(defaults.Pen.Color, defaults.Pen.Width, baseline.Pen, imageWidth),
                annotation => StrokeStyle(annotation.Color, annotation.Thickness, baseline.Pen, imageWidth)),
            Text = Section(
                EditTool.Text, saved.Text, baseline.Text,
                TextStyle(defaults.TextColor, defaults.FontFamily, defaults.FontSize, defaults.Bold, defaults.Italic, defaults.Underline, defaults.TextBox, baseline, imageWidth),
                annotation => TextStyle(annotation, baseline, imageWidth)),
            Number = Section(
                EditTool.Counter, saved.Number, baseline.Number,
                NumberStyle(defaults.NumberColor, defaults.NumberTextColor, defaults.NumberScale, baseline),
                annotation => NumberStyle(annotation, baseline)),
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
        var styles = new HashSet<(EditTool Tool, object Style)>();
        foreach (var annotation in annotations)
        {
            object? style = annotation.Tool switch
            {
                EditTool.Rectangle => ShapeStyle(annotation, theme.Rectangle, imageWidth),
                EditTool.Ellipse => ShapeStyle(annotation, theme.Ellipse, imageWidth),
                EditTool.Arrow => ArrowSection(annotation.Color, annotation.Thickness, annotation.ArrowStyle, theme.Arrow, imageWidth),
                EditTool.Line => StrokeStyle(annotation.Color, annotation.Thickness, theme.Line, imageWidth),
                EditTool.Pen => StrokeStyle(annotation.Color, annotation.Thickness, theme.Pen, imageWidth),
                EditTool.Text => TextStyle(annotation, theme, imageWidth),
                EditTool.Counter => NumberStyle(annotation, theme),
                _ => null,
            };
            if (style is not null)
            {
                styles.Add((annotation.Tool, style));
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

    private static EditorThemeShape ShapeStyle(ScreenshotAnnotation annotation, EditorThemeShape section, double imageWidth) =>
        ShapeStyle(annotation.Color, annotation.Thickness, annotation.FillColor, annotation.ShapeEffects, section, imageWidth);

    private static EditorThemeShape ShapeStyle(EditorThemeShapeDefaults defaults, EditorThemeShape section, double imageWidth) =>
        ShapeStyle(defaults.StrokeColor, defaults.StrokeWidth, defaults.Fill, defaults.Effects, section, imageWidth);

    private static EditorThemeShape ShapeStyle(Color stroke, double width, Color fill, ShapeEffects effects, EditorThemeShape section, double imageWidth)
    {
        // A gradient without a fill and a shadow without a blur draw nothing, so their other
        // settings are not edits to the theme.
        var hasGradient = effects.IsGradientFill && fill.A > 0;
        var hasShadow = effects.DrawsShadow;
        return new EditorThemeShape(
            Hex(stroke, section.StrokeColor),
            StrokeSize(width, imageWidth),
            Hex(fill, section.FillColor),
            hasGradient ? EditorTheme.HexOf(effects.GradientEndColor) : null,
            hasGradient ? Size(effects.GradientDirectionDegrees, 0, EditorTheme.MaximumDirection) : EditorTheme.Round(section.GradientDirection),
            hasShadow ? Size(EditorThemeUnits.StrokeUnits(effects.ShadowBlur, imageWidth), 0, EditorTheme.MaximumShadowBlur) : 0,
            hasShadow ? Hex(effects.ShadowColor, section.ShadowColor) : section.ShadowColor);
    }

    private static EditorThemeArrow ArrowSection(Color color, double width, ArrowStyle style, EditorThemeArrow section, double imageWidth) =>
        new(Hex(color, section.Color), StrokeSize(width, imageWidth), EditorThemeStyling.NameOf(style));

    private static EditorThemeStroke StrokeStyle(Color color, double width, EditorThemeStroke section, double imageWidth) =>
        new(Hex(color, section.Color), StrokeSize(width, imageWidth));

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
