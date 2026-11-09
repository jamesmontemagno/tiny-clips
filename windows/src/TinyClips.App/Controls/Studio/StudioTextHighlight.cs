using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using TinyClips.Core.Studio;
using Windows.UI;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// Marks parts of a text block's text, as the Captions panel marks what a search found in each
/// cue. The mark is the accent colour, see-through, so the letters keep their own colour in either theme.
/// </summary>
public static class StudioTextHighlight
{
    private const byte MarkAlpha = 0x66;

    public static readonly DependencyProperty RangesProperty = DependencyProperty.RegisterAttached(
        "Ranges",
        typeof(object),
        typeof(StudioTextHighlight),
        new PropertyMetadata(null, OnRangesChanged));

    public static object? GetRanges(TextBlock target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.GetValue(RangesProperty);
    }

    public static void SetRanges(TextBlock target, object? value)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.SetValue(RangesProperty, value);
    }

    private static void OnRangesChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBlock text)
        {
            return;
        }

        text.TextHighlighters.Clear();
        if (e.NewValue is not IReadOnlyList<StudioTextRange> { Count: > 0 } ranges)
        {
            return;
        }

        var accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var found) && found is Color color
            ? color
            : Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
        var highlighter = new TextHighlighter
        {
            Background = new SolidColorBrush(Color.FromArgb(MarkAlpha, accent.R, accent.G, accent.B)),
        };
        if (text.Foreground is SolidColorBrush foreground)
        {
            highlighter.Foreground = foreground;
        }

        foreach (var range in ranges)
        {
            highlighter.Ranges.Add(new TextRange { StartIndex = range.Start, Length = range.Length });
        }

        text.TextHighlighters.Add(highlighter);
    }
}
