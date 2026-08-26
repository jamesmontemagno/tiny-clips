using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace TinyClips.App.Controls.ClipsLibrary;

/// <summary>
/// Minimal horizontal wrap layout for <see cref="ItemsRepeater"/> (tag chips). WinUI ships no
/// WrapPanel and the toolkit's lives in a package this project does not reference.
/// </summary>
public sealed partial class WrapLayout : NonVirtualizingLayout
{
    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing), typeof(double), typeof(WrapLayout), new PropertyMetadata(0d, OnSpacingChanged));

    public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
        nameof(VerticalSpacing), typeof(double), typeof(WrapLayout), new PropertyMetadata(0d, OnSpacingChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((WrapLayout)d).InvalidateMeasure();

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        double x = 0, y = 0, rowHeight = 0, width = 0;
        foreach (var child in context.Children)
        {
            child.Measure(availableSize);
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width)
            {
                y += rowHeight + VerticalSpacing;
                x = 0;
                rowHeight = 0;
            }

            x += size.Width + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, size.Height);
            width = Math.Max(width, x - HorizontalSpacing);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), y + rowHeight);
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in context.Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                y += rowHeight + VerticalSpacing;
                x = 0;
                rowHeight = 0;
            }

            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        return finalSize;
    }
}
