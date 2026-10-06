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
    public double Rotation { get; set; }
    public bool IsRotated => Tool.StoresRotation() && Math.Abs(Rotation) > 0.001;

    public AnnotationSnapshot CaptureSnapshot() => new(
        Tool, Bounds, Color, FillColor, Thickness, Text, Number, SizeScale,
        Redaction, RedactStyle, ArrowStyle, Points.ToImmutableArray(), TextColor,
        FontSize, FontFamily, Bold, Italic, Underline, Strikethrough, Rotation);
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
