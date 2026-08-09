using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace TinyClips.App.ScreenshotEditor;

/// <summary>
/// The right-hand property panel: color/stroke/fill/text/counter/redaction controls plus the
/// export background/padding/corner/shadow section. Every control edit is forwarded to the shared
/// <see cref="EditorController"/>, which decides whether it applies to the selected annotation or
/// becomes the new tool default.
/// </summary>
public sealed partial class EditorInspector : UserControl
{
    private EditorController _controller = null!;
    private bool _inspectorInitializing = true;
    private bool _bgInitializing;

    public EditorInspector()
    {
        InitializeComponent();
    }

    internal void Attach(EditorController controller)
    {
        _controller = controller;

        AnnotationColorPicker.Color = controller.StrokeColor;
        NumberColorPicker.Color = controller.NumberTextColor;
        RedactionCombo.SelectedIndex = (int)controller.RedactionLevelDefault;
        RedactStyleCombo.SelectedIndex = (int)controller.RedactionStyleDefault;

        InitializeInspectorControls();
        InitializeBackgroundControls();

        controller.ToolChanged += (_, tool) => ShowForTool(tool);
        controller.SelectionChanged += (_, ann) =>
        {
            // Matches the original behavior: deselecting (clicking empty space with the Select
            // tool) leaves whichever panel was last shown rather than clearing it — tool changes
            // already reset the panel via ToolChanged/ShowForTool.
            if (ann is not null)
            {
                ShowForSelection(ann);
            }
        };

        ShowForTool(controller.Tool);
    }

    // -- Initialization --------------------------------------------------------------------

    private void InitializeInspectorControls()
    {
        _inspectorInitializing = true;

        foreach (var font in EditorFonts.Choices)
        {
            FontFamilyCombo.Items.Add(new ComboBoxItem { Content = font, Tag = font });
        }
        FontFamilyCombo.SelectedIndex = 0;

        StrokeSlider.Value = _controller.StrokeThickness;
        NumberSizeSlider.Value = _controller.NumberScale;
        FontSizeSlider.Value = _controller.TextFontSize;
        FillCheck.IsChecked = _controller.FillEnabled;
        FillColorPicker.Color = _controller.FillEnabled ? _controller.FillColor : Colors.Transparent;
        UpdateInspectorHeaders();

        _inspectorInitializing = false;
    }

    private void UpdateInspectorHeaders()
    {
        StrokeSlider.Header = $"Stroke — {(int)_controller.StrokeThickness} px";
        NumberSizeSlider.Header = $"Badge size — {(int)Math.Round(_controller.NumberScale * 100)}%";
        FontSizeSlider.Header = $"Font size — {(int)_controller.TextFontSize} px";
    }

    private void InitializeBackgroundControls()
    {
        _bgInitializing = true;

        foreach (var preset in EditorController.SolidPresets)
        {
            SolidPresetGrid.Items.Add(CreatePresetSwatch(preset));
        }

        foreach (var preset in EditorController.GradientPresets)
        {
            GradientPresetGrid.Items.Add(CreatePresetSwatch(preset));
        }

        BgStyleCombo.SelectedIndex = 0;
        BgColorPicker.Color = _controller.BgColor;
        PaddingSlider.Value = _controller.CanvasPadding;
        CornerSlider.Value = _controller.CanvasCornerRadius;
        ShadowSlider.Value = _controller.CanvasShadow;
        ExportFrameCombo.SelectedIndex = (int)_controller.FramePreset;
        HorizontalAlignmentCombo.SelectedIndex = (int)_controller.HorizontalExportAlignment;
        VerticalAlignmentCombo.SelectedIndex = (int)_controller.VerticalExportAlignment;
        UpdateSliderHeaders();
        UpdateBackgroundStyleUi();
        UpdateExportFrameControls();

        _bgInitializing = false;
    }

    private Button CreatePresetSwatch(BackgroundPreset preset)
    {
        Brush fill = preset.Style == ExportBackgroundStyle.Gradient && preset.Secondary is { } secondary
            ? new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = preset.Primary, Offset = 0 },
                    new GradientStop { Color = secondary, Offset = 1 },
                },
            }
            : new SolidColorBrush(preset.Primary);

        var button = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = fill,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
            Tag = preset,
        };
        ToolTipService.SetToolTip(button, preset.Label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{preset.Label} background");
        button.Click += OnPresetSwatchClick;
        return button;
    }

    // -- Tool / selection panels -------------------------------------------------------------

    private void ShowForTool(EditTool tool)
    {
        _inspectorInitializing = true;

        var showsStroke = tool is EditTool.Rectangle or EditTool.Ellipse or EditTool.Arrow
            or EditTool.Line or EditTool.Pen;
        var showsFill = tool is EditTool.Rectangle or EditTool.Ellipse;
        var showsColor = tool is EditTool.Rectangle or EditTool.Ellipse or EditTool.Arrow
            or EditTool.Line or EditTool.Pen or EditTool.Text or EditTool.Counter;
        var showsText = tool is EditTool.Text;
        var showsNumber = tool is EditTool.Counter;
        var showsRedact = tool is EditTool.Redact;
        var showsArrowStyle = tool is EditTool.Arrow;

        ColorSection.Visibility = showsColor ? Visibility.Visible : Visibility.Collapsed;
        StrokeSection.Visibility = showsStroke ? Visibility.Visible : Visibility.Collapsed;
        ArrowStyleSection.Visibility = showsArrowStyle ? Visibility.Visible : Visibility.Collapsed;
        FillSection.Visibility = showsFill ? Visibility.Visible : Visibility.Collapsed;
        TextSection.Visibility = showsText ? Visibility.Visible : Visibility.Collapsed;
        CounterSection.Visibility = showsNumber ? Visibility.Visible : Visibility.Collapsed;
        RedactSection.Visibility = showsRedact ? Visibility.Visible : Visibility.Collapsed;

        if (showsArrowStyle)
        {
            ArrowStyleCombo.SelectedIndex = (int)_controller.ArrowStyleDefault;
        }

        if (showsRedact)
        {
            RedactStyleCombo.SelectedIndex = (int)_controller.RedactionStyleDefault;
            RedactionCombo.SelectedIndex = (int)_controller.RedactionLevelDefault;
        }

        InspectorTitle.Text = tool switch
        {
            EditTool.Select => "Select & move",
            EditTool.Crop => "Crop",
            EditTool.Rectangle => "Rectangle",
            EditTool.Ellipse => "Ellipse",
            EditTool.Arrow => "Arrow",
            EditTool.Line => "Line",
            EditTool.Pen => "Draw",
            EditTool.Text => "Text",
            EditTool.Counter => "Number badge",
            EditTool.Redact => "Redact",
            _ => "Tool",
        };

        UpdateInspectorHeaders();
        _inspectorInitializing = false;
    }

    // While the Select tool is active, reveal the property controls for the chosen annotation
    // and load its current values so the user can tweak color, size, font, etc.
    private void ShowForSelection(Annotation ann)
    {
        _inspectorInitializing = true;

        var isShape = ann.Tool is EditTool.Rectangle or EditTool.Ellipse or EditTool.Arrow
            or EditTool.Line or EditTool.Pen;
        var isFillable = ann.Tool is EditTool.Rectangle or EditTool.Ellipse;
        var isText = ann.Tool is EditTool.Text;
        var isCounter = ann.Tool is EditTool.Counter;
        var isRedact = ann.Tool is EditTool.Redact;
        var isArrow = ann.Tool is EditTool.Arrow;
        var hasColor = isShape || isText || isCounter;

        ColorSection.Visibility = hasColor ? Visibility.Visible : Visibility.Collapsed;
        StrokeSection.Visibility = isShape ? Visibility.Visible : Visibility.Collapsed;
        ArrowStyleSection.Visibility = isArrow ? Visibility.Visible : Visibility.Collapsed;
        FillSection.Visibility = isFillable ? Visibility.Visible : Visibility.Collapsed;
        TextSection.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
        CounterSection.Visibility = isCounter ? Visibility.Visible : Visibility.Collapsed;
        RedactSection.Visibility = isRedact ? Visibility.Visible : Visibility.Collapsed;
        InspectorTitle.Text = $"{ann.Tool} (selected)";

        if (isArrow)
        {
            ArrowStyleCombo.SelectedIndex = (int)ann.ArrowStyle;
        }

        if (hasColor)
        {
            AnnotationColorPicker.Color = ann.Color;
        }
        if (isShape)
        {
            StrokeSlider.Value = ann.Thickness;
        }
        if (isFillable)
        {
            var hasFill = ann.FillColor.A > 0;
            FillCheck.IsChecked = hasFill;
            FillColorPicker.Color = hasFill ? ann.FillColor : Colors.Transparent;
        }
        if (isText)
        {
            FontSizeSlider.Value = ann.FontSize;
            SelectFontInCombo(ann.FontFamily);
        }
        if (isCounter)
        {
            NumberSizeSlider.Value = ann.SizeScale;
            NumberColorPicker.Color = ann.TextColor;
        }
        if (isRedact)
        {
            RedactionCombo.SelectedIndex = (int)ann.Redaction;
            RedactStyleCombo.SelectedIndex = (int)ann.RedactStyle;
        }

        UpdateInspectorHeaders();
        _inspectorInitializing = false;
    }

    private void SelectFontInCombo(string font)
    {
        for (var i = 0; i < FontFamilyCombo.Items.Count; i++)
        {
            if (FontFamilyCombo.Items[i] is ComboBoxItem { Tag: string f } && f == font)
            {
                FontFamilyCombo.SelectedIndex = i;
                return;
            }
        }
    }

    // -- Style handlers ----------------------------------------------------------------------

    private void OnColorChanged(object? sender, Color color) => _controller.SetStrokeColor(color);

    private void OnNumberColorChanged(object? sender, Color color) => _controller.SetNumberColor(color);

    private void OnStrokeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_inspectorInitializing)
        {
            return;
        }

        _controller.SetStrokeThickness(e.NewValue);
        UpdateInspectorHeaders();
    }

    private void OnArrowStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_inspectorInitializing)
        {
            return;
        }

        _controller.SetArrowStyle((ArrowStyle)Math.Clamp(ArrowStyleCombo.SelectedIndex, 0, 2));
    }

    private void OnFillToggled(object sender, RoutedEventArgs e)
    {
        if (_inspectorInitializing)
        {
            return;
        }

        var enabled = FillCheck.IsChecked == true;
        _controller.SetFillEnabled(enabled);
        FillColorPicker.Color = enabled ? _controller.FillColor : Colors.Transparent;
    }

    private void OnFillColorChanged(object? sender, Color color)
    {
        if (_inspectorInitializing)
        {
            return;
        }

        _controller.SetFillColor(color);
        if (color.A == 0)
        {
            FillCheck.IsChecked = false;
        }
        else
        {
            FillCheck.IsChecked = true;
        }
    }

    private void OnFontFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_inspectorInitializing)
        {
            return;
        }

        if (FontFamilyCombo.SelectedItem is ComboBoxItem { Tag: string font })
        {
            _controller.SetFontFamily(font);
        }
    }

    private void OnFontSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_inspectorInitializing)
        {
            return;
        }

        _controller.SetFontSize(e.NewValue);
        UpdateInspectorHeaders();
    }

    private void OnNumberSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_inspectorInitializing)
        {
            return;
        }

        _controller.SetNumberSize(e.NewValue);
        UpdateInspectorHeaders();
    }

    private void OnRedactionLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RedactionCombo.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<RedactionLevel>(tag, out var level))
        {
            _controller.SetRedactionLevel(level);
        }
    }

    private void OnRedactionStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RedactStyleCombo.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<RedactionStyle>(tag, out var style))
        {
            _controller.SetRedactionStyle(style);
        }
    }

    // -- Export background handlers -----------------------------------------------------------

    private void OnPresetSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BackgroundPreset preset })
        {
            _controller.ApplyPreset(preset);

            _bgInitializing = true;
            BgStyleCombo.SelectedIndex = preset.Style == ExportBackgroundStyle.Gradient ? 2 : 1;
            BgColorPicker.Color = preset.Primary;
            _bgInitializing = false;

            UpdateBackgroundStyleUi();
        }
    }

    private void OnBgStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bgInitializing)
        {
            return;
        }

        var style = BgStyleCombo.SelectedIndex switch
        {
            1 => ExportBackgroundStyle.Solid,
            2 => ExportBackgroundStyle.Gradient,
            _ => ExportBackgroundStyle.Transparent,
        };
        _controller.SetBackgroundStyle(style);
        UpdateBackgroundStyleUi();
    }

    private void UpdateBackgroundStyleUi()
    {
        SolidPresetGrid.Visibility = _controller.BgStyle == ExportBackgroundStyle.Solid ? Visibility.Visible : Visibility.Collapsed;
        GradientPresetGrid.Visibility = _controller.BgStyle == ExportBackgroundStyle.Gradient ? Visibility.Visible : Visibility.Collapsed;
        CustomColorPanel.Visibility = _controller.BgStyle == ExportBackgroundStyle.Solid ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBgCustomColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_bgInitializing)
        {
            return;
        }

        _controller.SetCustomBgColor(args.NewColor);
    }

    private void OnApplyCustomSolidBackground(object sender, RoutedEventArgs e)
    {
        _controller.ApplyCustomSolidBackground(BgColorPicker.Color);

        _bgInitializing = true;
        BgStyleCombo.SelectedIndex = 1;
        _bgInitializing = false;

        UpdateBackgroundStyleUi();
    }

    private void OnPaddingChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_bgInitializing)
        {
            return;
        }

        _controller.SetPadding(e.NewValue);
        UpdateSliderHeaders();
    }

    private void OnExportFrameChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bgInitializing)
        {
            return;
        }

        _controller.SetExportFramePreset((ExportFramePreset)ExportFrameCombo.SelectedIndex);
        UpdateExportFrameControls();
    }

    private void OnHorizontalAlignmentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_bgInitializing)
        {
            _controller.SetHorizontalExportAlignment((ExportHorizontalAlignment)HorizontalAlignmentCombo.SelectedIndex);
        }
    }

    private void OnVerticalAlignmentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_bgInitializing)
        {
            _controller.SetVerticalExportAlignment((ExportVerticalAlignment)VerticalAlignmentCombo.SelectedIndex);
        }
    }

    private void OnCornerChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_bgInitializing)
        {
            return;
        }

        _controller.SetCornerRadius(e.NewValue);
        UpdateSliderHeaders();
    }

    private void OnShadowChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_bgInitializing)
        {
            return;
        }

        _controller.SetShadow(e.NewValue);
        UpdateSliderHeaders();
    }

    private void UpdateSliderHeaders()
    {
        PaddingSlider.Header = $"Padding — {(int)_controller.CanvasPadding} px";
        CornerSlider.Header = $"Image corners — {(int)_controller.CanvasCornerRadius} px";
        ShadowSlider.Header = $"Shadow — {(int)_controller.CanvasShadow}";
    }

    private void UpdateExportFrameControls()
    {
        var hasAdditionalFrameSpace = _controller.FramePreset != ExportFramePreset.Original;
        HorizontalAlignmentCombo.IsEnabled = hasAdditionalFrameSpace;
        VerticalAlignmentCombo.IsEnabled = hasAdditionalFrameSpace;
    }
}
