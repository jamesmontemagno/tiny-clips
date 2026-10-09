using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using TinyClips.Core.Editing;

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
    private TextBoxStyle _textBoxStyle;

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
        TextBoxStyle textBoxStyle,
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
        _textBoxStyle = textBoxStyle;

        ResultColor = color;
        TextColorPicker.Color = color;
        ColorSwatch.Background = new SolidColorBrush(color);
        SyncTextBoxControls();

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

    public TextBoxStyle ResultTextBoxStyle => _textBoxStyle;

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
        if (_initializing || args.NewColor == ResultColor)
        {
            return;
        }
        ResultColor = args.NewColor;
        ColorSwatch.Background = new SolidColorBrush(args.NewColor);
        if (_textBoxStyle.Preset != TextBoxPreset.Plain)
        {
            _textBoxStyle = _textBoxStyle with { Preset = TextBoxPreset.Custom };
            SyncPresetButtons();
        }
        UpdatePreview();
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not ToggleButton { Tag: string tag }
            || !Enum.TryParse<TextBoxPreset>(tag, out var preset))
        {
            return;
        }

        var accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var resource)
            && resource is Color color
            ? color
            : Color.FromArgb(255, 0, 120, 212);
        var resolved = TextBoxStyle.Resolve(preset, ResultColor, accent);
        _textBoxStyle = resolved.Style;
        ResultColor = resolved.TextColor;
        _initializing = true;
        TextColorPicker.Color = ResultColor;
        ColorSwatch.Background = new SolidColorBrush(ResultColor);
        _initializing = false;
        SyncTextBoxControls();
        UpdatePreview();
    }

    private void OnBackgroundColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_initializing)
        {
            return;
        }
        _textBoxStyle = (_textBoxStyle with { BackgroundColor = args.NewColor }).AsCustom();
        SyncPresetButtons();
        UpdatePreview();
    }

    private void OnBorderColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_initializing)
        {
            return;
        }
        _textBoxStyle = (_textBoxStyle with { BorderColor = args.NewColor }).AsCustom();
        SyncPresetButtons();
        UpdatePreview();
    }

    private void OnBoxMetricChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_initializing)
        {
            return;
        }
        _textBoxStyle = (_textBoxStyle with
        {
            BorderWidth = BorderWidthBox.Value,
            Padding = PaddingBox.Value,
            CornerRadius = CornerRadiusBox.Value,
        }).AsCustom();
        SyncPresetButtons();
        UpdatePreview();
    }

    private void SyncTextBoxControls()
    {
        var wasInitializing = _initializing;
        _initializing = true;
        BackgroundColorPicker.Color = _textBoxStyle.BackgroundColor;
        BorderColorPicker.Color = _textBoxStyle.BorderColor;
        BorderWidthBox.Value = _textBoxStyle.BorderWidth;
        PaddingBox.Value = _textBoxStyle.Padding;
        CornerRadiusBox.Value = _textBoxStyle.CornerRadius;
        SyncPresetButtons();
        _initializing = wasInitializing;
    }

    private void SyncPresetButtons()
    {
        PlainPreset.IsChecked = _textBoxStyle.Preset == TextBoxPreset.Plain;
        LightPreset.IsChecked = _textBoxStyle.Preset == TextBoxPreset.Light;
        DarkPreset.IsChecked = _textBoxStyle.Preset == TextBoxPreset.Dark;
        AccentPreset.IsChecked = _textBoxStyle.Preset == TextBoxPreset.Accent;
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
        PreviewBox.Background = new SolidColorBrush(_textBoxStyle.BackgroundColor);
        PreviewBox.BorderBrush = new SolidColorBrush(_textBoxStyle.BorderColor);
        PreviewBox.BorderThickness = new Thickness(_textBoxStyle.BorderWidth);
        PreviewBox.Padding = new Thickness(_textBoxStyle.Padding);
        PreviewBox.CornerRadius = new CornerRadius(_textBoxStyle.CornerRadius);
    }
}
