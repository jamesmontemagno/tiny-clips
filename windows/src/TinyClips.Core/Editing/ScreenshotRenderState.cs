using System.Collections.Immutable;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace TinyClips.Core.Editing;

public enum EditTool
{
    Select,
    Crop,
    Rectangle,
    Ellipse,
    Arrow,
    Line,
    Pen,
    Text,
    Counter,
    Emoji,
    Redact,
}

public enum RedactionLevel
{
    Light,
    Medium,
    Heavy,
}

public enum RedactionStyle
{
    Blur,
    Pixelate,
    Solid,
}

public enum ArrowStyle
{
    Straight,
    Curved1,
    Curved2,
}

public enum ExportBackgroundStyle
{
    Transparent,
    Solid,
    Gradient,
}

public enum ExportFramePreset
{
    Original,
    Square,
    LandscapeFourByThree,
    LandscapeSixteenByNine,
    PortraitThreeByFour,
    PortraitNineBySixteen,
}

public enum ExportHorizontalAlignment
{
    Left,
    Center,
    Right,
}

public enum ExportVerticalAlignment
{
    Top,
    Center,
    Bottom,
}

public enum TextBoxPreset
{
    Plain,
    Light,
    Dark,
    Accent,
    Custom,
}

public readonly record struct TextBoxStyle(
    TextBoxPreset Preset,
    Color BackgroundColor,
    Color BorderColor,
    double BorderWidth,
    double Padding,
    double CornerRadius)
{
    public const double MaximumBorderWidth = 12;
    public const double MaximumPadding = 48;
    public const double MaximumCornerRadius = 32;

    public static TextBoxStyle Plain => new(
        TextBoxPreset.Plain,
        Color.FromArgb(0, 0, 0, 0),
        Color.FromArgb(0, 0, 0, 0),
        0,
        0,
        0);

    public static (TextBoxStyle Style, Color TextColor) Resolve(
        TextBoxPreset preset,
        Color currentTextColor,
        Color accentColor) =>
        preset switch
        {
            TextBoxPreset.Plain => (Plain, currentTextColor),
            TextBoxPreset.Light => (
                new TextBoxStyle(
                    TextBoxPreset.Light,
                    Color.FromArgb(245, 243, 243, 245),
                    Color.FromArgb(46, 31, 31, 36),
                    1,
                    8,
                    4),
                Color.FromArgb(255, 20, 20, 23)),
            TextBoxPreset.Dark => (
                new TextBoxStyle(
                    TextBoxPreset.Dark,
                    Color.FromArgb(240, 30, 30, 34),
                    Color.FromArgb(56, 255, 255, 255),
                    1,
                    8,
                    4),
                Color.FromArgb(255, 255, 255, 255)),
            TextBoxPreset.Accent => (
                new TextBoxStyle(
                    TextBoxPreset.Accent,
                    accentColor,
                    Color.FromArgb(0, 0, 0, 0),
                    0,
                    8,
                    4),
                ContrastTextColor(accentColor)),
            _ => (Plain with { Preset = TextBoxPreset.Custom }, currentTextColor),
        };

    public TextBoxStyle AsCustom() => this with
    {
        Preset = TextBoxPreset.Custom,
        BorderWidth = Math.Clamp(BorderWidth, 0, MaximumBorderWidth),
        Padding = Math.Clamp(Padding, 0, MaximumPadding),
        CornerRadius = Math.Clamp(CornerRadius, 0, MaximumCornerRadius),
    };

    public TextBoxStyle Scale(double factor) => this with
    {
        Preset = Preset == TextBoxPreset.Plain ? TextBoxPreset.Plain : TextBoxPreset.Custom,
        BorderWidth = Math.Clamp(BorderWidth * factor, 0, MaximumBorderWidth),
        Padding = Math.Clamp(Padding * factor, 0, MaximumPadding),
        CornerRadius = Math.Clamp(CornerRadius * factor, 0, MaximumCornerRadius),
    };

    public Rect DecoratedBounds(Rect contentBounds)
    {
        var inset = Math.Max(0, Padding) + Math.Max(0, BorderWidth) / 2;
        return new Rect(
            contentBounds.X - inset,
            contentBounds.Y - inset,
            contentBounds.Width + inset * 2,
            contentBounds.Height + inset * 2);
    }

    public Rect ContentBounds(Rect decoratedBounds)
    {
        var inset = Math.Max(0, Padding) + Math.Max(0, BorderWidth) / 2;
        return new Rect(
            decoratedBounds.X + inset,
            decoratedBounds.Y + inset,
            Math.Max(0, decoratedBounds.Width - inset * 2),
            Math.Max(0, decoratedBounds.Height - inset * 2));
    }

    public static Color ContrastTextColor(Color color)
    {
        static double Linearize(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        var luminance = 0.2126 * Linearize(color.R)
            + 0.7152 * Linearize(color.G)
            + 0.0722 * Linearize(color.B);
        return luminance > 0.179
            ? Color.FromArgb(255, 0, 0, 0)
            : Color.FromArgb(255, 255, 255, 255);
    }
}

public static class EditToolExtensions
{
    public static bool SupportsRotation(this EditTool tool) =>
        tool is EditTool.Emoji or EditTool.Text or EditTool.Rectangle or EditTool.Ellipse or EditTool.Pen;

    public static bool StoresRotation(this EditTool tool) => tool.SupportsRotation() && tool != EditTool.Pen;
}

/// <summary>Mutable owning-thread annotation state. Capture before crossing an async boundary.</summary>
public class ScreenshotAnnotation
{
    public EditTool Tool { get; set; }
    public Rect Bounds { get; set; }
    public Color Color { get; set; }
    public Color FillColor { get; set; } = Color.FromArgb(0, 255, 255, 255);
    public double Thickness { get; set; }
    public string Text { get; set; } = string.Empty;
    public int Number { get; set; }
    public double SizeScale { get; set; } = 1.0;
    public RedactionLevel Redaction { get; set; } = RedactionLevel.Medium;
    public RedactionStyle RedactStyle { get; set; } = RedactionStyle.Blur;
    public ArrowStyle ArrowStyle { get; set; } = ArrowStyle.Straight;
    public List<Vector2> Points { get; } = new();
    public Color TextColor { get; set; } = Color.FromArgb(255, 255, 255, 255);
    public double FontSize { get; set; } = 28;
    public string FontFamily { get; set; } = "Segoe UI";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strikethrough { get; set; }
    public TextBoxStyle TextBoxStyle { get; set; } = TextBoxStyle.Plain;
    public double Rotation { get; set; }
    public bool IsRotated => Tool.StoresRotation() && Math.Abs(Rotation) > 0.001;

    public AnnotationSnapshot CaptureSnapshot() => new(
        Tool, Bounds, Color, FillColor, Thickness, Text, Number, SizeScale,
        Redaction, RedactStyle, ArrowStyle, Points.ToImmutableArray(), TextColor,
        FontSize, FontFamily, Bold, Italic, Underline, Strikethrough, TextBoxStyle, Rotation);
}

/// <summary>Immutable image-pixel annotation data; deliberately excludes live XAML previews.</summary>
public sealed record AnnotationSnapshot(
    EditTool Tool,
    Rect Bounds,
    Color Color,
    Color FillColor,
    double Thickness,
    string Text,
    int Number,
    double SizeScale,
    RedactionLevel Redaction,
    RedactionStyle RedactStyle,
    ArrowStyle ArrowStyle,
    ImmutableArray<Vector2> Points,
    Color TextColor,
    double FontSize,
    string FontFamily,
    bool Bold,
    bool Italic,
    bool Underline,
    bool Strikethrough,
    TextBoxStyle TextBoxStyle,
    double Rotation)
{
    public bool IsRotated => Tool.StoresRotation() && Math.Abs(Rotation) > 0.001;
}

public readonly record struct ExportFrameLayout(Size FrameSize, Rect ImageBounds)
{
    public static ExportFrameLayout Create(
        double imageWidth,
        double imageHeight,
        double padding,
        ExportFramePreset preset,
        ExportHorizontalAlignment horizontalAlignment,
        ExportVerticalAlignment verticalAlignment)
    {
        var safePadding = Math.Max(0, padding);
        var baseWidth = imageWidth + safePadding * 2;
        var baseHeight = imageHeight + safePadding * 2;
        var frameWidth = baseWidth;
        var frameHeight = baseHeight;
        var targetRatio = preset switch
        {
            ExportFramePreset.Square => 1.0,
            ExportFramePreset.LandscapeFourByThree => 4.0 / 3.0,
            ExportFramePreset.LandscapeSixteenByNine => 16.0 / 9.0,
            ExportFramePreset.PortraitThreeByFour => 3.0 / 4.0,
            ExportFramePreset.PortraitNineBySixteen => 9.0 / 16.0,
            _ => 0.0,
        };

        if (targetRatio > 0 && baseWidth > 0 && baseHeight > 0)
        {
            if (baseWidth / baseHeight < targetRatio)
            {
                frameWidth = Math.Ceiling(baseHeight * targetRatio);
            }
            else if (baseWidth / baseHeight > targetRatio)
            {
                frameHeight = Math.Ceiling(baseWidth / targetRatio);
            }
        }

        var horizontalFactor = horizontalAlignment switch
        {
            ExportHorizontalAlignment.Left => 0.0,
            ExportHorizontalAlignment.Right => 1.0,
            _ => 0.5,
        };
        var verticalFactor = verticalAlignment switch
        {
            ExportVerticalAlignment.Top => 0.0,
            ExportVerticalAlignment.Bottom => 1.0,
            _ => 0.5,
        };
        var extraHorizontalSpace = Math.Max(0, frameWidth - baseWidth);
        var extraVerticalSpace = Math.Max(0, frameHeight - baseHeight);
        return new ExportFrameLayout(
            new Size(frameWidth, frameHeight),
            new Rect(
                safePadding + extraHorizontalSpace * horizontalFactor,
                safePadding + extraVerticalSpace * verticalFactor,
                imageWidth,
                imageHeight));
    }
}

/// <summary>Uses the editor's established frame truncation and midpoint-to-even scale rounding.</summary>
public readonly record struct ScreenshotExportSize(int RenderWidth, int RenderHeight, int Width, int Height)
{
    public static ScreenshotExportSize Create(Size frameSize, int scalePercent)
    {
        if (!double.IsFinite(frameSize.Width) || !double.IsFinite(frameSize.Height)
            || frameSize.Width < 1 || frameSize.Height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(frameSize));
        }
        if (scalePercent is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(scalePercent));
        }

        var renderWidth = checked((int)(float)frameSize.Width);
        var renderHeight = checked((int)(float)frameSize.Height);
        return new ScreenshotExportSize(
            renderWidth,
            renderHeight,
            Math.Max(1, checked((int)Math.Round(renderWidth * scalePercent / 100d))),
            Math.Max(1, checked((int)Math.Round(renderHeight * scalePercent / 100d))));
    }
}

public sealed record ScreenshotRenderState(
    ImmutableArray<AnnotationSnapshot> Annotations,
    ExportFrameLayout Frame,
    ExportBackgroundStyle BackgroundStyle,
    Color BackgroundColor,
    Color BackgroundColor2,
    double CornerRadius,
    double Shadow,
    bool IncludeBackground,
    ScreenshotExportSize OutputSize);
