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
    private bool _isEdit;
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
        _isEdit = isEdit;
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
        SyncTextBoxControls();
        BoxExpander.IsExpanded = _textBoxStyle.Preset == TextBoxPreset.Custom;

        _initializing = false;
        IsPrimaryButtonEnabled = _isEdit || !string.IsNullOrWhiteSpace(EntryBox.Text);
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

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        IsPrimaryButtonEnabled = _isEdit || !string.IsNullOrWhiteSpace(EntryBox.Text);
        UpdatePreview();
    }

    private void OnColorChanged(object? sender, Color color)
    {
        if (_initializing || color == ResultColor)
        {
            return;
        }
        ResultColor = color;
        if (_textBoxStyle.Preset != TextBoxPreset.Plain)
        {
            _textBoxStyle = _textBoxStyle with { Preset = TextBoxPreset.Custom };
            SyncPresetChoice();
        }
        UpdatePreview();
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || TextStyleChoice.IsApplyingTemplate || TextStyleChoice.SelectedIndex < 0)
        {
            return;
        }

        var preset = (TextBoxPreset)TextStyleChoice.SelectedIndex;
        var accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var resource)
            && resource is Color color
            ? color
            : Color.FromArgb(255, 0, 120, 212);
        var resolved = TextBoxStyle.Resolve(preset, ResultColor, accent);
        _textBoxStyle = resolved.Style;
        ResultColor = resolved.TextColor;
        BoxExpander.IsExpanded = false;

        SyncTextBoxControls();
        UpdatePreview();
    }

    private void OnBackgroundColorChanged(object? sender, Color color)
    {
        if (_initializing)
        {
            return;
        }
        _textBoxStyle = (_textBoxStyle with { BackgroundColor = color }).AsCustom();
        SyncTextBoxControls();
        UpdatePreview();
    }

    private void OnBackgroundOpacityChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }
        var background = AnnotationColorMath.WithOpacity(
            _textBoxStyle.BackgroundColor,
            e.NewValue / 100,
            Color.FromArgb(255, 245, 245, 248));
        _textBoxStyle = (_textBoxStyle with { BackgroundColor = background }).AsCustom();
        SyncTextBoxControls();
        UpdatePreview();
    }

    private void OnBorderColorChanged(object? sender, Color color)
    {
        if (_initializing)
        {
            return;
        }
        _textBoxStyle = (_textBoxStyle with { BorderColor = color }).AsCustom();
        SyncTextBoxControls();
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
        SyncPresetChoice();
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
        BackgroundOpacitySlider.Value = _textBoxStyle.BackgroundColor.A / 255.0 * 100;
        TextColorPicker.Color = ResultColor;
        SyncPresetChoice();
        _initializing = wasInitializing;
    }

    private void SyncPresetChoice()
    {
        var wasInitializing = _initializing;
        _initializing = true;
        TextStyleChoice.SelectedIndex = _textBoxStyle.Preset == TextBoxPreset.Custom
            ? -1
            : (int)_textBoxStyle.Preset;
        TextStyleCustomLabel.Visibility = _textBoxStyle.Preset == TextBoxPreset.Custom
            ? Visibility.Visible
            : Visibility.Collapsed;
        _initializing = wasInitializing;
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
        PreviewText.FontSize = SizeBox.Value > 0 ? Math.Clamp(SizeBox.Value, 14, 36) : 28;
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
