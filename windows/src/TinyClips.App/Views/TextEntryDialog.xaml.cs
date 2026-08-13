using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TinyClips.App;

/// <summary>
/// Modal text-entry dialog for the screenshot editor. Replaces the fragile inline
/// overlay text box: it lets the user type multi-line text and toggle bold, italic,
/// underline and strikethrough, pick a font, size and color, with a live preview,
/// then confirm with OK. Used both to add new text and to edit an existing label.
/// </summary>
public sealed partial class TextEntryDialog : ContentDialog
{
    private bool _initializing = true;

    public TextEntryDialog(
        IEnumerable<string> fonts,
        string text,
        string fontFamily,
        double fontSize,
        Color color,
        bool bold,
        bool italic,
        bool underline,
        bool strikethrough,
        bool isEdit)
    {
        InitializeComponent();

        Title = isEdit ? "Edit text" : "Add text";

        foreach (var font in fonts)
        {
            FontCombo.Items.Add(new ComboBoxItem { Content = font, Tag = font });
        }
        SelectFont(fontFamily);

        EntryBox.Text = text;
        SizeBox.Value = fontSize;
        BoldToggle.IsChecked = bold;
        ItalicToggle.IsChecked = italic;
        UnderlineToggle.IsChecked = underline;
        StrikeToggle.IsChecked = strikethrough;

        ResultColor = color;
        TextColorPicker.Color = color;
        ColorSwatch.Background = new SolidColorBrush(color);

        _initializing = false;
        UpdatePreview();

        Opened += (_, _) => EntryBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    public string ResultText => EntryBox.Text;

    public string ResultFont =>
        FontCombo.SelectedItem is ComboBoxItem { Tag: string f } ? f : "Segoe UI";

    public double ResultSize => SizeBox.Value;

    public Color ResultColor { get; private set; }

    public bool ResultBold => BoldToggle.IsChecked == true;

    public bool ResultItalic => ItalicToggle.IsChecked == true;

    public bool ResultUnderline => UnderlineToggle.IsChecked == true;

    public bool ResultStrikethrough => StrikeToggle.IsChecked == true;

    private void SelectFont(string font)
    {
        for (var i = 0; i < FontCombo.Items.Count; i++)
        {
            if (FontCombo.Items[i] is ComboBoxItem { Tag: string f } && f == font)
            {
                FontCombo.SelectedIndex = i;
                return;
            }
        }

        FontCombo.SelectedIndex = 0;
    }

    private void OnFormattingChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => UpdatePreview();

    private void OnFontChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void OnSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdatePreview();

    private void OnTextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void OnColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        ResultColor = args.NewColor;
        ColorSwatch.Background = new SolidColorBrush(args.NewColor);
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_initializing)
        {
            return;
        }

        var text = EntryBox.Text;
        PreviewText.Text = string.IsNullOrEmpty(text) ? "Preview" : text;
        PreviewText.Foreground = new SolidColorBrush(ResultColor);
        PreviewText.FontSize = SizeBox.Value > 0 ? SizeBox.Value : 28;
        PreviewText.FontFamily = new FontFamily(ResultFont);
        PreviewText.FontWeight = ResultBold ? FontWeights.Bold : FontWeights.Normal;
        PreviewText.FontStyle = ResultItalic
            ? Windows.UI.Text.FontStyle.Italic
            : Windows.UI.Text.FontStyle.Normal;

        var decorations = Windows.UI.Text.TextDecorations.None;
        if (ResultUnderline)
        {
            decorations |= Windows.UI.Text.TextDecorations.Underline;
        }
        if (ResultStrikethrough)
        {
            decorations |= Windows.UI.Text.TextDecorations.Strikethrough;
        }
        PreviewText.TextDecorations = decorations;
    }
}
