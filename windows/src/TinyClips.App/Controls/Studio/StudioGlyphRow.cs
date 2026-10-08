using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// Keeps a row of buttons to the room it has. Buttons side by side are each given all the width
/// they ask for, so a <see cref="StudioButtonLabel"/> in one of them cannot tell that the row is
/// full: this adds up what the row's items take and tells the labels
/// (<see cref="StudioGlyphRowLayout"/>). The timeline's row of buttons and the two buttons side
/// by side in the Zoom panel are such rows.
/// </summary>
internal static class StudioGlyphRow
{
    /// <summary>
    /// Watches a row, which is a grid with an item to a column or a stack, and everything in it,
    /// for a change of size: of the window, of the text size, or of what an item says.
    /// </summary>
    public static void Attach(Panel row)
    {
        void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply(row);

        row.SizeChanged += OnSizeChanged;
        foreach (var item in row.Children.OfType<FrameworkElement>())
        {
            item.SizeChanged += OnSizeChanged;
            if (item is Panel holder)
            {
                foreach (var part in holder.Children.OfType<FrameworkElement>())
                {
                    part.SizeChanged += OnSizeChanged;
                }
            }
        }
    }

    private static void Apply(Panel row)
    {
        var labels = new List<StudioButtonLabel>();
        var (itemsWidth, glyphsWidth, items) = (0.0, 0.0, 0);
        foreach (var item in row.Children.OfType<FrameworkElement>())
        {
            if (item.Visibility != Visibility.Visible)
            {
                continue;
            }

            items++;
            itemsWidth += WidthOf(item);
            if (item is ContentControl { Content: StudioButtonLabel label })
            {
                labels.Add(label);
                glyphsWidth += label.GlyphWidth;
            }
        }

        if (labels.Count == 0)
        {
            return;
        }

        // A grid keeps the gap beside a column whose item is not shown; a stack does not.
        var gaps = row switch
        {
            Grid grid => grid.ColumnSpacing * Math.Max(0, grid.ColumnDefinitions.Count - 1),
            StackPanel stack => stack.Spacing * Math.Max(0, items - 1),
            _ => 0,
        };
        var shown = labels[0].ShowsGlyph;
        var show = StudioGlyphRowLayout.ShowsGlyphs(row.ActualWidth, itemsWidth + gaps, glyphsWidth, shown);
        if (show != shown)
        {
            foreach (var label in labels)
            {
                label.ShowsGlyph = show;
            }
        }
    }

    // An item is as wide as it is drawn. One that takes what the others leave, as the time of
    // the timeline's row does, is in a stack that fills that room, and is as wide as what it holds.
    private static double WidthOf(FrameworkElement item)
    {
        if (item is not StackPanel { Orientation: Orientation.Horizontal } holder)
        {
            return item.ActualWidth + item.Margin.Left + item.Margin.Right;
        }

        var parts = holder.Children.OfType<FrameworkElement>().Where(part => part.Visibility == Visibility.Visible).ToArray();
        return parts.Sum(part => part.ActualWidth + part.Margin.Left + part.Margin.Right) + (holder.Spacing * Math.Max(0, parts.Length - 1));
    }
}
