using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI;

namespace TinyClips.App.ScreenshotEditor;

/// <summary>
/// Annotation tools available in the screenshot editor. Shared by <see cref="EditorController"/>,
/// <see cref="EditorToolbar"/>, <see cref="EditorInspector"/> and <see cref="EditorCanvas"/> so no
/// control needs a reference to the others — each one only depends on the controller.
/// </summary>
internal enum EditTool
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
    Redact,
}

internal enum AnnotationResizeHandle
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

internal enum RedactionLevel
{
    Light,
    Medium,
    Heavy,
}

internal enum RedactionStyle
{
    Blur,
    Pixelate,
    Solid,
}

internal enum ArrowStyle
{
    Straight,
    Curved1,
    Curved2,
}

internal enum ExportBackgroundStyle
{
    Transparent,
    Solid,
    Gradient,
}

internal enum ExportFramePreset
{
    Original,
    Square,
    LandscapeFourByThree,
    LandscapeSixteenByNine,
    PortraitThreeByFour,
    PortraitNineBySixteen,
}

internal enum ExportHorizontalAlignment
{
    Left,
    Center,
    Right,
}

internal enum ExportVerticalAlignment
{
    Top,
    Center,
    Bottom,
}

internal readonly record struct ExportFrameLayout(Size FrameSize, Rect ImageBounds)
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

/// <summary>
/// One annotation in image-pixel coordinates. Mutable in place so the editor canvas can retain a
/// live visual per instance and update it during drags instead of rebuilding the overlay.
/// </summary>
internal sealed class Annotation
{
    public EditTool Tool { get; set; }
    public Rect Bounds { get; set; }
    public Color Color { get; set; }
    public Color FillColor { get; set; } = Colors.Transparent;
    public double Thickness { get; set; }
    public string Text { get; set; } = string.Empty;
    public int Number { get; set; }
    public double SizeScale { get; set; } = 1.0;
    public RedactionLevel Redaction { get; set; } = RedactionLevel.Medium;
    public RedactionStyle RedactStyle { get; set; } = RedactionStyle.Blur;
    public ArrowStyle ArrowStyle { get; set; } = ArrowStyle.Straight;
    public List<Vector2> Points { get; } = new();

    // Text annotations: independent font + size; numbered badges: text color.
    public Color TextColor { get; set; } = Colors.White;
    public double FontSize { get; set; } = 28;
    public string FontFamily { get; set; } = "Segoe UI";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strikethrough { get; set; }

    // Cached blurred preview for redaction annotations (invalidated on move / level change).
    public SoftwareBitmapSource? RedactPreview { get; set; }
    public Rect RedactPreviewBounds { get; set; }
    public RedactionLevel RedactPreviewLevel { get; set; }
    public RedactionStyle RedactPreviewStyle { get; set; }
}

internal sealed record BackgroundPreset(string Id, string Label, ExportBackgroundStyle Style, Color Primary, Color? Secondary);

/// <summary>Font choices shared by the text-entry dialog and the inspector's font combo.</summary>
internal static class EditorFonts
{
    public static readonly string[] Choices =
    {
        "Segoe UI",
        "Segoe UI Semibold",
        "Arial",
        "Calibri",
        "Cambria",
        "Comic Sans MS",
        "Consolas",
        "Courier New",
        "Georgia",
        "Impact",
        "Times New Roman",
        "Trebuchet MS",
        "Verdana",
    };
}

/// <summary>Geometry for a straight or curved arrow, shared by the live preview and Win2D bake.</summary>
internal readonly record struct ArrowShape(
    bool Curved,
    Vector2 ShaftStart,
    Vector2 ShaftControl,
    Vector2 ShaftEnd,
    Vector2 Tip,
    Vector2 Head1,
    Vector2 Head2);
