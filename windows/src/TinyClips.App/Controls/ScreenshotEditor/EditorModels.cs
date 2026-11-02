using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI;
using TinyClips.Core.Editing;

namespace TinyClips.App.ScreenshotEditor;

internal enum AnnotationResizeHandle
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// One annotation in image-pixel coordinates. Mutable in place so the editor canvas can retain a
/// live visual per instance and update it during drags instead of rebuilding the overlay.
/// </summary>
internal sealed class Annotation : ScreenshotAnnotation
{
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
