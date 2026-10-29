using System;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace TinyClips.App.Settings.Sections;

/// <summary>Gives every chart bucket an equal share of the available width, including empty buckets.</summary>
public sealed class AnalyticsChartPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width)
            ? availableSize.Width / Math.Max(1, Children.Count)
            : double.PositiveInfinity;
        var desiredWidth = 0.0;
        var height = 0.0;
        foreach (var child in Children)
        {
            child.Measure(new Size(width, availableSize.Height));
            desiredWidth = Math.Max(desiredWidth, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(
            double.IsFinite(availableSize.Width) ? availableSize.Width : desiredWidth * Children.Count,
            height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width / Math.Max(1, Children.Count);
        for (var index = 0; index < Children.Count; index++)
        {
            Children[index].Arrange(new Rect(index * width, 0, width, finalSize.Height));
        }

        return finalSize;
    }
}
