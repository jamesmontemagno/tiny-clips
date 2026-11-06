using Windows.UI;

namespace TinyClips.Core.Editing.Themes;

/// <summary>Which part of a theme styles an annotation.</summary>
public enum EditorThemeGroup
{
    Shape,
    Line,
    Text,
    Number,
}

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

/// <summary>The editor's defaults for new annotations, in image pixels.</summary>
public sealed record EditorThemeDefaults(
    Color ShapeColor,
    double ShapeWidth,
    Color ShapeFill,
    ShapeEffects ShapeEffects,
    Color LineColor,
    double LineWidth,
    ArrowStyle ArrowStyle,
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

    public static EditorThemeGroup? GroupOf(EditTool tool) => tool switch
    {
        EditTool.Rectangle or EditTool.Ellipse => EditorThemeGroup.Shape,
        EditTool.Arrow or EditTool.Line or EditTool.Pen => EditorThemeGroup.Line,
        EditTool.Text => EditorThemeGroup.Text,
        EditTool.Counter => EditorThemeGroup.Number,
        _ => null,
    };

    /// <summary>
    /// Restyles an annotation. Redactions and emoji are never touched, so a theme can't weaken a
    /// redaction. Text and pen bounds depend on the new style, so the caller re-measures them.
    /// </summary>
    public static void Apply(ScreenshotAnnotation annotation, EditorTheme theme, double imageWidth)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ArgumentNullException.ThrowIfNull(theme);
        switch (GroupOf(annotation.Tool))
        {
            case EditorThemeGroup.Shape:
                annotation.Color = EditorTheme.ColorOf(theme.Shape.StrokeColor);
                annotation.Thickness = EditorThemeUnits.StrokePixels(theme.Shape.StrokeWidth, imageWidth);
                annotation.FillColor = EditorTheme.ColorOf(theme.Shape.FillColor);
                annotation.ShapeEffects = ShapeEffectsOf(theme.Shape, imageWidth);
                break;

            case EditorThemeGroup.Line:
                annotation.Color = EditorTheme.ColorOf(theme.Line.Color);
                annotation.Thickness = EditorThemeUnits.StrokePixels(theme.Line.Width, imageWidth);
                if (annotation.Tool == EditTool.Arrow)
                {
                    annotation.ArrowStyle = ArrowStyleOf(theme.Line.ArrowStyle);
                }

                break;

            case EditorThemeGroup.Text:
                annotation.Color = EditorTheme.ColorOf(theme.Text.Color);
                annotation.FontFamily = FontFamilyOf(theme.Text);
                annotation.FontSize = EditorThemeUnits.TextPixels(theme.Text.FontSize, imageWidth);
                annotation.Bold = theme.Text.Bold;
                annotation.Italic = theme.Text.Italic;
                annotation.Underline = theme.Text.Underline;
                annotation.TextBoxStyle = TextBoxStyleOf(theme.Text.Box, imageWidth);
                break;

            case EditorThemeGroup.Number:
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
            EditorTheme.ColorOf(theme.Shape.StrokeColor),
            EditorThemeUnits.StrokePixels(theme.Shape.StrokeWidth, imageWidth),
            EditorTheme.ColorOf(theme.Shape.FillColor),
            ShapeEffectsOf(theme.Shape, imageWidth),
            EditorTheme.ColorOf(theme.Line.Color),
            EditorThemeUnits.StrokePixels(theme.Line.Width, imageWidth),
            ArrowStyleOf(theme.Line.ArrowStyle),
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

    public static ArrowStyle ArrowStyleOf(string name) => name switch
    {
        "curvedLeft" => ArrowStyle.Curved1,
        "curvedRight" => ArrowStyle.Curved2,
        _ => ArrowStyle.Straight,
    };

    public static string NameOf(ArrowStyle style) => style switch
    {
        ArrowStyle.Curved1 => "curvedLeft",
        ArrowStyle.Curved2 => "curvedRight",
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
