using Windows.UI;

namespace TinyClips.Core.Editing.Themes;

/// <summary>Converts theme sizes to image pixels and back. See shared/editor-themes/README.md.</summary>
public static class EditorThemeUnits
{
    public static double StrokeUnit(double imageWidth) => Math.Max(1, imageWidth / 900);

    public static double TextUnit(double imageWidth) => Math.Max(1, imageWidth) / 800;

    public static double StrokePixels(double units, double imageWidth) => units * StrokeUnit(imageWidth);

    public static double TextPixels(double units, double imageWidth) => units * TextUnit(imageWidth);

    public static double StrokeUnits(double pixels, double imageWidth) => pixels / StrokeUnit(imageWidth);

    public static double TextUnits(double pixels, double imageWidth) => pixels / TextUnit(imageWidth);
}

/// <summary>A tool's color and width for new annotations, in image pixels.</summary>
public readonly record struct EditorThemeStrokeDefaults(Color Color, double Width);

/// <summary>A rectangle's or an ellipse's defaults for new annotations, in image pixels.</summary>
public readonly record struct EditorThemeShapeDefaults(Color StrokeColor, double StrokeWidth, Color Fill, ShapeEffects Effects);

/// <summary>The editor's defaults for new annotations, one set per annotation type, in image pixels.</summary>
public sealed record EditorThemeDefaults(
    EditorThemeShapeDefaults Rectangle,
    EditorThemeShapeDefaults Ellipse,
    EditorThemeStrokeDefaults Arrow,
    ArrowStyle ArrowStyle,
    EditorThemeStrokeDefaults Line,
    EditorThemeStrokeDefaults Pen,
    Color TextColor,
    string FontFamily,
    double FontSize,
    bool Bold,
    bool Italic,
    bool Underline,
    TextBoxStyle TextBox,
    Color NumberColor,
    Color NumberTextColor,
    double NumberScale);

/// <summary>The part of the editor a theme's canvas settings map onto.</summary>
public readonly record struct EditorThemeCanvasState(
    ExportBackgroundStyle Background,
    Color Color,
    Color SecondaryColor,
    double GradientDirection,
    double Padding,
    double CornerRadius,
    double Shadow,
    ExportFramePreset Frame,
    ExportHorizontalAlignment HorizontalAlignment,
    ExportVerticalAlignment VerticalAlignment);

/// <summary>Maps themes onto annotations and editor state. Nothing here reads the theme store.</summary>
public static class EditorThemeStyling
{
    /// <summary>The font a theme with no font family uses on Windows.</summary>
    public const string PlatformFontFamily = "Segoe UI";

    /// <summary>Whether a theme has a section for this tool. Redaction and emoji never do.</summary>
    public static bool IsThemed(EditTool tool) =>
        tool is EditTool.Rectangle or EditTool.Ellipse or EditTool.Arrow or EditTool.Line or EditTool.Pen
            or EditTool.Text or EditTool.Counter;

    /// <summary>
    /// Restyles an annotation from its own section. Redactions and emoji are never touched, so a
    /// theme can't weaken a redaction. Text and pen bounds depend on the new style, so the caller
    /// re-measures them.
    /// </summary>
    public static void Apply(ScreenshotAnnotation annotation, EditorTheme theme, double imageWidth)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ArgumentNullException.ThrowIfNull(theme);
        switch (annotation.Tool)
        {
            case EditTool.Rectangle:
                ApplyShape(annotation, theme.Rectangle, imageWidth);
                break;

            case EditTool.Ellipse:
                ApplyShape(annotation, theme.Ellipse, imageWidth);
                break;

            case EditTool.Arrow:
                annotation.Color = EditorTheme.ColorOf(theme.Arrow.Color);
                annotation.Thickness = EditorThemeUnits.StrokePixels(theme.Arrow.Width, imageWidth);
                annotation.ArrowStyle = ArrowStyleOf(theme.Arrow.Style);
                break;

            case EditTool.Line:
                ApplyStroke(annotation, theme.Line, imageWidth);
                break;

            case EditTool.Pen:
                ApplyStroke(annotation, theme.Pen, imageWidth);
                break;

            case EditTool.Text:
                annotation.Color = EditorTheme.ColorOf(theme.Text.Color);
                annotation.FontFamily = FontFamilyOf(theme.Text);
                annotation.FontSize = EditorThemeUnits.TextPixels(theme.Text.FontSize, imageWidth);
                annotation.Bold = theme.Text.Bold;
                annotation.Italic = theme.Text.Italic;
                annotation.Underline = theme.Text.Underline;
                annotation.TextBoxStyle = TextBoxStyleOf(theme.Text.Box, imageWidth);
                break;

            case EditTool.Counter:
                annotation.Color = EditorTheme.ColorOf(theme.Number.BadgeColor);
                annotation.TextColor = EditorTheme.ColorOf(theme.Number.TextColor);
                annotation.SizeScale = theme.Number.Size;
                break;
        }
    }

    /// <summary>The defaults for new annotations a theme sets.</summary>
    public static EditorThemeDefaults DefaultsOf(EditorTheme theme, double imageWidth)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return new EditorThemeDefaults(
            ShapeDefaultsOf(theme.Rectangle, imageWidth),
            ShapeDefaultsOf(theme.Ellipse, imageWidth),
            new EditorThemeStrokeDefaults(EditorTheme.ColorOf(theme.Arrow.Color), EditorThemeUnits.StrokePixels(theme.Arrow.Width, imageWidth)),
            ArrowStyleOf(theme.Arrow.Style),
            StrokeDefaultsOf(theme.Line, imageWidth),
            StrokeDefaultsOf(theme.Pen, imageWidth),
            EditorTheme.ColorOf(theme.Text.Color),
            FontFamilyOf(theme.Text),
            EditorThemeUnits.TextPixels(theme.Text.FontSize, imageWidth),
            theme.Text.Bold,
            theme.Text.Italic,
            theme.Text.Underline,
            TextBoxStyleOf(theme.Text.Box, imageWidth),
            EditorTheme.ColorOf(theme.Number.BadgeColor),
            EditorTheme.ColorOf(theme.Number.TextColor),
            theme.Number.Size);
    }

    private static void ApplyShape(ScreenshotAnnotation annotation, EditorThemeShape shape, double imageWidth)
    {
        annotation.Color = EditorTheme.ColorOf(shape.StrokeColor);
        annotation.Thickness = EditorThemeUnits.StrokePixels(shape.StrokeWidth, imageWidth);
        annotation.FillColor = EditorTheme.ColorOf(shape.FillColor);
        annotation.ShapeEffects = ShapeEffectsOf(shape, imageWidth);
    }

    private static void ApplyStroke(ScreenshotAnnotation annotation, EditorThemeStroke stroke, double imageWidth)
    {
        annotation.Color = EditorTheme.ColorOf(stroke.Color);
        annotation.Thickness = EditorThemeUnits.StrokePixels(stroke.Width, imageWidth);
    }

    private static EditorThemeShapeDefaults ShapeDefaultsOf(EditorThemeShape shape, double imageWidth) => new(
        EditorTheme.ColorOf(shape.StrokeColor),
        EditorThemeUnits.StrokePixels(shape.StrokeWidth, imageWidth),
        EditorTheme.ColorOf(shape.FillColor),
        ShapeEffectsOf(shape, imageWidth));

    private static EditorThemeStrokeDefaults StrokeDefaultsOf(EditorThemeStroke stroke, double imageWidth) => new(
        EditorTheme.ColorOf(stroke.Color),
        EditorThemeUnits.StrokePixels(stroke.Width, imageWidth));

    /// <summary>
    /// The canvas a theme asks for. Themes leave out image backgrounds, so one already in place
    /// stays when <paramref name="keepsImageBackground"/> is set.
    /// </summary>
    public static EditorThemeCanvasState CanvasStateOf(EditorTheme theme, bool keepsImageBackground = false)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var canvas = theme.Canvas;
        return new EditorThemeCanvasState(
            keepsImageBackground ? ExportBackgroundStyle.Image : BackgroundOf(canvas.Background),
            EditorTheme.ColorOf(canvas.Color),
            EditorTheme.ColorOf(canvas.SecondaryColor),
            canvas.GradientDirection,
            canvas.Padding,
            canvas.CornerRadius,
            canvas.Shadow,
            FrameOf(canvas.Frame),
            HorizontalAlignmentOf(canvas.HorizontalAlignment),
            VerticalAlignmentOf(canvas.VerticalAlignment));
    }

    public static ShapeEffects ShapeEffectsOf(EditorThemeShape shape, double imageWidth)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var defaults = ShapeEffects.Default;
        return defaults with
        {
            IsGradientFill = shape.GradientEndColor is not null,
            GradientEndColor = shape.GradientEndColor is { } end ? EditorTheme.ColorOf(end) : defaults.GradientEndColor,
            GradientDirectionDegrees = shape.GradientDirection,
            HasShadow = shape.ShadowBlur > 0,
            ShadowColor = EditorTheme.ColorOf(shape.ShadowColor),
            ShadowBlur = shape.ShadowBlur > 0
                ? EditorThemeUnits.StrokePixels(shape.ShadowBlur, imageWidth)
                : defaults.ShadowBlur,
        };
    }

    public static TextBoxStyle TextBoxStyleOf(EditorThemeTextBox box, double imageWidth)
    {
        ArgumentNullException.ThrowIfNull(box);
        var background = EditorTheme.ColorOf(box.BackgroundColor);
        var border = EditorTheme.ColorOf(box.BorderColor);
        var isPlain = background.A == 0 && border.A == 0
            && box.BorderWidth == 0 && box.Padding == 0 && box.CornerRadius == 0;
        return new TextBoxStyle(
            isPlain ? TextBoxPreset.Plain : TextBoxPreset.Custom,
            background,
            border,
            EditorThemeUnits.TextPixels(box.BorderWidth, imageWidth),
            EditorThemeUnits.TextPixels(box.Padding, imageWidth),
            EditorThemeUnits.TextPixels(box.CornerRadius, imageWidth));
    }

    public static string FontFamilyOf(EditorThemeText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.FontFamily ?? PlatformFontFamily;
    }

    /// <summary>
    /// Curved1 bows along (-dy, dx), to the right of the arrow's travel on screen, and Curved2 to its
    /// left. The names match macOS, so a theme curves to the same side on both.
    /// </summary>
    public static ArrowStyle ArrowStyleOf(string name) => name switch
    {
        "curvedLeft" => ArrowStyle.Curved2,
        "curvedRight" => ArrowStyle.Curved1,
        _ => ArrowStyle.Straight,
    };

    public static string NameOf(ArrowStyle style) => style switch
    {
        ArrowStyle.Curved1 => "curvedRight",
        ArrowStyle.Curved2 => "curvedLeft",
        _ => "straight",
    };

    public static ExportBackgroundStyle BackgroundOf(string name) => name switch
    {
        "solid" => ExportBackgroundStyle.Solid,
        "gradient" => ExportBackgroundStyle.Gradient,
        _ => ExportBackgroundStyle.Transparent,
    };

    public static string NameOf(ExportBackgroundStyle style) => style switch
    {
        ExportBackgroundStyle.Solid => "solid",
        ExportBackgroundStyle.Gradient => "gradient",
        _ => "transparent",
    };

    public static ExportFramePreset FrameOf(string name) => name switch
    {
        "square" => ExportFramePreset.Square,
        "landscape4x3" => ExportFramePreset.LandscapeFourByThree,
        "landscape16x9" => ExportFramePreset.LandscapeSixteenByNine,
        "portrait3x4" => ExportFramePreset.PortraitThreeByFour,
        "portrait9x16" => ExportFramePreset.PortraitNineBySixteen,
        _ => ExportFramePreset.Original,
    };

    public static string NameOf(ExportFramePreset frame) => frame switch
    {
        ExportFramePreset.Square => "square",
        ExportFramePreset.LandscapeFourByThree => "landscape4x3",
        ExportFramePreset.LandscapeSixteenByNine => "landscape16x9",
        ExportFramePreset.PortraitThreeByFour => "portrait3x4",
        ExportFramePreset.PortraitNineBySixteen => "portrait9x16",
        _ => "original",
    };

    public static ExportHorizontalAlignment HorizontalAlignmentOf(string name) => name switch
    {
        "left" => ExportHorizontalAlignment.Left,
        "right" => ExportHorizontalAlignment.Right,
        _ => ExportHorizontalAlignment.Center,
    };

    public static string NameOf(ExportHorizontalAlignment alignment) => alignment switch
    {
        ExportHorizontalAlignment.Left => "left",
        ExportHorizontalAlignment.Right => "right",
        _ => "center",
    };

    public static ExportVerticalAlignment VerticalAlignmentOf(string name) => name switch
    {
        "top" => ExportVerticalAlignment.Top,
        "bottom" => ExportVerticalAlignment.Bottom,
        _ => ExportVerticalAlignment.Center,
    };

    public static string NameOf(ExportVerticalAlignment alignment) => alignment switch
    {
        ExportVerticalAlignment.Top => "top",
        ExportVerticalAlignment.Bottom => "bottom",
        _ => "center",
    };
}
