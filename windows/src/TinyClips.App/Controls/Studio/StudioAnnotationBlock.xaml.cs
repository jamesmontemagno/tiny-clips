using Microsoft.UI.Xaml;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// One annotation on the Studio annotation lane: a green block that carries the mark of its kind
/// and, where there is room, what it says. Of annotations that overlap, one shows its mark and
/// words with how many are there, and the others show none, so no words are drawn over others.
/// </summary>
public sealed partial class StudioAnnotationBlock : StudioRangeBlock
{
    // Below these widths the mark, and then the words next to it, would not fit between the handles.
    private const double MarkMinimumWidth = 26;
    private const double LabelMinimumWidth = 64;

    // A block that overlaps others also makes room for the count next to its words.
    private const double OverlapLabelMinimumWidth = 84;
    private const double OverlapBadgeMinimumWidth = 44;

    // What the mark and the space around the words take up.
    private const double LabelInset = 40;

    internal StudioAnnotationBlock(StudioLane lane)
        : base(lane, "StudioAnnotation_", "annotation", "when")
    {
        InitializeComponent();
    }

    private int _itemIndex = -1;
    private string _baseDescription = string.Empty;
    private double _width;
    private StudioLaneOverlap _overlap = new(0, 1, true);

    /// <summary>Shows an annotation in a block of the given width.</summary>
    internal void Update(int index, StudioAnnotation annotation, double width, bool isSelected)
    {
        KindIcon.Glyph = annotation.Kind switch
        {
            StudioAnnotationKinds.Arrow => StudioGlyphs.AddArrow,
            StudioAnnotationKinds.Callout => StudioGlyphs.AddCallout,
            StudioAnnotationKinds.Text => StudioGlyphs.AddText,
            StudioAnnotationKinds.Rectangle => StudioGlyphs.AddRectangle,
            StudioAnnotationKinds.Ellipse => StudioGlyphs.AddEllipse,
            StudioAnnotationKinds.Redact => StudioGlyphs.AddRedaction,
            StudioAnnotationKinds.Emoji => StudioGlyphs.AddEmoji,
            _ => StudioInspectorPanels.GetGlyph(StudioInspectorPanel.Annotate),
        };
        Label.Text = StudioEditorText.GetAnnotationLabel(annotation);
        ShowHandles(BlockHandles, width);
        _itemIndex = index;
        _width = width;
        _baseDescription = StudioEditorText.GetAnnotationDescription(annotation);
        ShowOverlapParts();
        Show(index, DescriptionWithOverlap(), isSelected);
    }

    /// <summary>
    /// Shows how the block shares its room with the annotations it overlaps: whether it carries the
    /// mark and words for all of them, and how many there are.
    /// </summary>
    internal void ShowOverlap(StudioLaneOverlap overlap)
    {
        if (overlap == _overlap)
        {
            return;
        }

        _overlap = overlap;
        ShowOverlapParts();
        if (_itemIndex >= 0)
        {
            Show(_itemIndex, DescriptionWithOverlap(), IsSelected);
        }
    }

    private void ShowOverlapParts()
    {
        var owns = _overlap.ShowsLabel;
        var overlaps = _overlap.OverlapsOthers;
        KindIcon.Visibility = owns && _width >= MarkMinimumWidth ? Visibility.Visible : Visibility.Collapsed;
        var badge = owns && overlaps && _width >= OverlapBadgeMinimumWidth;
        OverlapCount.Text = _overlap.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        OverlapBadge.Visibility = badge ? Visibility.Visible : Visibility.Collapsed;
        Label.MaxWidth = Math.Max(0, _width - LabelInset - (badge ? 28 : 0));
        Label.Visibility = owns && _width >= (overlaps ? OverlapLabelMinimumWidth : LabelMinimumWidth)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private string DescriptionWithOverlap() =>
        StudioEditorModel.GetLaneOverlapText(_overlap.Count) is { } text ? $"{_baseDescription}, {text}" : _baseDescription;
}
