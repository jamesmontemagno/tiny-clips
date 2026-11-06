using Microsoft.UI.Xaml;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// One annotation on the Studio annotation lane: a green block that carries the mark of its kind
/// and, where there is room, what it says.
/// </summary>
public sealed partial class StudioAnnotationBlock : StudioRangeBlock
{
    // Below these widths the mark, and then the words next to it, would not fit between the handles.
    private const double MarkMinimumWidth = 26;
    private const double LabelMinimumWidth = 64;

    // What the mark and the space around the words take up.
    private const double LabelInset = 40;

    internal StudioAnnotationBlock(StudioLane lane)
        : base(lane, "StudioAnnotation_", "annotation", "when")
    {
        InitializeComponent();
    }

    /// <summary>Shows an annotation in a block of the given width.</summary>
    internal void Update(int index, StudioAnnotation annotation, double width, bool isSelected)
    {
        KindIcon.Glyph = annotation.Kind switch
        {
            StudioAnnotationKinds.Arrow => StudioGlyphs.AddArrow,
            StudioAnnotationKinds.Callout => StudioGlyphs.AddCallout,
            StudioAnnotationKinds.Text => StudioGlyphs.AddText,
            _ => StudioInspectorPanels.GetGlyph(StudioInspectorPanel.Annotate),
        };
        KindIcon.Visibility = width >= MarkMinimumWidth ? Visibility.Visible : Visibility.Collapsed;
        Label.Text = StudioEditorText.GetAnnotationLabel(annotation);
        Label.MaxWidth = Math.Max(0, width - LabelInset);
        Label.Visibility = width >= LabelMinimumWidth ? Visibility.Visible : Visibility.Collapsed;
        ShowHandles(BlockHandles, width);
        Show(index, StudioEditorText.GetAnnotationDescription(annotation), isSelected);
    }
}
