using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// What a button of the Studio editor that has words shows: a glyph of the icon font, and then
/// its words. The glyph is one of <c>StudioGlyphs</c>, so that two buttons that do one thing
/// show one picture.
/// </summary>
/// <remarks>
/// The glyph is decoration and the words are the button's name already, so both are kept out of
/// what a screen reader walks, as the text of a button that holds only words is. The button is
/// given its name in the markup, so that it is its words whatever it holds.
/// </remarks>
public sealed partial class StudioButtonLabel : StackPanel
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(StudioButtonLabel),
        new PropertyMetadata(string.Empty, static (sender, e) => ((StudioButtonLabel)sender)._icon.Glyph = e.NewValue as string ?? string.Empty));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(StudioButtonLabel),
        new PropertyMetadata(string.Empty, static (sender, e) => ((StudioButtonLabel)sender)._words.Text = e.NewValue as string ?? string.Empty));

    // As large and as far from the words as the icon of Export, in the editor's header.
    private readonly FontIcon _icon = new() { FontSize = 14 };
    private readonly TextBlock _words = new();

    public StudioButtonLabel()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        AutomationProperties.SetAccessibilityView(_icon, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_words, AccessibilityView.Raw);
        Children.Add(_icon);
        Children.Add(_words);
    }

    /// <summary>The glyph of Segoe Fluent Icons before the words.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>The button's words.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
