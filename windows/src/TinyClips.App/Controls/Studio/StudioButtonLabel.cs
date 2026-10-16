using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// What a button of the Studio editor that has words shows: a glyph of the icon font, and then
/// its words. The glyph is one of <c>StudioGlyphs</c>, so that two buttons that do one thing
/// show one picture.
/// </summary>
/// <remarks>
/// <para>
/// The glyph is decoration and the words are the button's name already, so both are kept out of
/// what a screen reader walks, as the text of a button that holds only words is. The button is
/// given its name in the markup, so that it is its words whatever it holds.
/// </para>
/// <para>
/// The words come first. Where the label is given less room than its glyph and its words need,
/// as a button of a panel is at a large text size, the glyph is left out, and the button is as
/// wide as it was before it had one. A label that is given all the room it asks for, because
/// its button stands beside others, is told by its row instead (<see cref="StudioGlyphRow"/>).
/// The glyph is measured also while it is left out, so that it is known what it would take.
/// </para>
/// </remarks>
public sealed partial class StudioButtonLabel : Panel
{
    /// <summary>The room between the glyph and the words: as much as Export has, in the editor's header.</summary>
    public const double Gap = 6;

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

    public static readonly DependencyProperty ShowsGlyphProperty = DependencyProperty.Register(
        nameof(ShowsGlyph),
        typeof(bool),
        typeof(StudioButtonLabel),
        new PropertyMetadata(true, static (sender, _) => ((StudioButtonLabel)sender).InvalidateMeasure()));

    // As large as the icon of Export.
    private readonly FontIcon _icon = new() { FontSize = 14 };
    private readonly TextBlock _words = new();
    private bool _hasRoom = true;

    public StudioButtonLabel()
    {
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

    /// <summary>False when the row the button is in has no room for its glyphs. The words show either way.</summary>
    public bool ShowsGlyph
    {
        get => (bool)GetValue(ShowsGlyphProperty);
        set => SetValue(ShowsGlyphProperty, value);
    }

    /// <summary>Whether the glyph is drawn: it may be, and there is room for it.</summary>
    public bool IsGlyphShown => ShowsGlyph && _hasRoom;

    /// <summary>What the glyph and the gap after it take when they show, as last measured.</summary>
    public double GlyphWidth => _icon.DesiredSize.Width + Gap;

    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        _icon.Measure(unbounded);
        _words.Measure(unbounded);
        _hasRoom = _words.DesiredSize.Width + GlyphWidth <= availableSize.Width;
        return IsGlyphShown
            ? new Size(GlyphWidth + _words.DesiredSize.Width, Math.Max(_icon.DesiredSize.Height, _words.DesiredSize.Height))
            : _words.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Left out, the glyph has no place and is not drawn; it stays measured.
        var shown = IsGlyphShown;
        _icon.Opacity = shown ? 1 : 0;
        _icon.Arrange(shown ? new Rect(0, 0, _icon.DesiredSize.Width, finalSize.Height) : new Rect(0, 0, 0, 0));
        var left = shown ? GlyphWidth : 0;
        _words.Arrange(new Rect(left, 0, Math.Max(0, finalSize.Width - left), finalSize.Height));
        return finalSize;
    }
}
