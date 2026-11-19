using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using TinyClips.App.Controls.Studio;
using TinyClips.Core.Editing;

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
    private string _strokeSliderLabel = "Stroke";
    private bool _bgInitializing;
    private bool _backgroundSelectionBusy;
    private ContentDialog? _backgroundColorDialog;
    private IAsyncOperation<ContentDialogResult>? _backgroundColorDialogOperation;

    public EditorInspector()
    {
        InitializeComponent();
    }

    internal event EventHandler? BackgroundImageRequested;

    /// <summary>Raised with the button to anchor the sensitive-text review flyout to.</summary>
    internal event EventHandler<FrameworkElement>? SensitiveTextRequested;

    private void OnFindSensitiveTextClick(object sender, RoutedEventArgs e) =>
        SensitiveTextRequested?.Invoke(this, FindSensitiveTextButton);

    internal void SetBackgroundSelectionBusy(bool busy)
    {
        _backgroundSelectionBusy = busy;
        SyncBackgroundControls();
    }

    internal bool HasOpenBackgroundColorDialog => _backgroundColorDialog is not null;

    internal async Task DismissBackgroundColorDialogAsync()
    {
        var dialog = _backgroundColorDialog;
        var showOperation = _backgroundColorDialogOperation;
        if (dialog is null || showOperation is null)
        {
            return;
        }

        dialog.Hide();
        await showOperation;
    }

    internal void Attach(EditorController controller)
    {
        _controller = controller;

        AnnotationColorPicker.Color = controller.StrokeColor;
        NumberColorPicker.Color = controller.NumberTextColor;
        RedactionChoice.SelectedIndex = (int)controller.RedactionLevelDefault;
        RedactStyleChoice.SelectedIndex = (int)controller.RedactionStyleDefault;

        InitializeInspectorControls();
        InitializeBackgroundControls();
        InitializeEmojiControls();

        controller.ToolChanged += (_, tool) => ShowForTool(tool);
        controller.EmojiChanged += (_, emoji) => SyncEmojiSelection(emoji);
        controller.AnnotationVisualInvalidated += (_, ann) =>
        {
            // Keep the rotation slider in step with the on-canvas rotation grip.
            // Keep the arrow sliders in step with the on-canvas bend handle.
            if (ann.Tool == EditTool.Arrow
                && ReferenceEquals(ann, _controller.SelectedAnnotation)
                && Math.Abs(ArrowArcSlider.Value - Math.Round(ann.Arrow.Bend * 100)) >= 1)
            {
                ShowArrowShape(ann.Arrow);
            }

            if (ann.Tool.StoresRotation()
                && ReferenceEquals(ann, _controller.SelectedAnnotation)
                && Math.Abs(RotationSlider.Value - ann.Rotation) > 0.5)
            {
                _inspectorInitializing = true;
                RotationSlider.Value = ann.Rotation;
                UpdateInspectorHeaders();
                _inspectorInitializing = false;
            }
        };
        controller.BackgroundChanged += (_, _) =>
        {
            SyncBackgroundControls();
            UpdateExportFrameControls();
        };
        controller.TextDefaultsChanged += (_, _) =>
        {
            if (_controller.Tool == EditTool.Text && _controller.SelectedAnnotation is null)
            {
                ShowForTool(EditTool.Text);
            }
        };
        controller.ImageChanged += (_, _) => UpdateExportFrameControls();
        controller.SelectionChanged += (_, ann) =>
        {
            if (ann is not null)
            {
                ShowForSelection(ann);
            }
            else
            {
                ShowForTool(_controller.Tool);
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
        SyncTextBoxStyle(_controller.TextBoxStyleDefault);
        FillCheck.IsChecked = _controller.FillEnabled;
        FillColorPicker.Color = _controller.FillEnabled ? _controller.FillColor : Colors.Transparent;
        UpdateInspectorHeaders();

        _inspectorInitializing = false;
    }

    private void UpdateInspectorHeaders()
    {
        // Read the sliders, which hold the selection's values while one is shown.
        StrokeSlider.Header = $"{_strokeSliderLabel} — {(int)StrokeSlider.Value} px";
        OutlineSlider.Header = OutlineSlider.Value > 0 ? $"Stroke — {(int)OutlineSlider.Value} px" : "Stroke — none";
        NumberSizeSlider.Header = $"Badge size — {(int)Math.Round(NumberSizeSlider.Value * 100)}%";
        FontSizeSlider.Header = $"Font size — {(int)FontSizeSlider.Value} px";
        RotationSlider.Header = $"Rotation — {(int)Math.Round(RotationSlider.Value)}°";
    }

    private void InitializeEmojiControls()
    {
        // Hand XAML a List<string>, not EmojiAnnotationMath.Common itself: that is a collection
        // expression typed as IReadOnlyList<string>, whose compiler-generated runtime type has no
        // WinRT collection interfaces under NativeAOT, so the ItemsSource setter throws.
        CommonEmojiGrid.ItemsSource = EmojiAnnotationMath.Common.ToList();
        SyncEmojiSelection(_controller.EmojiDefault);
    }

    private void SyncEmojiSelection(string emoji)
    {
        SelectedEmojiText.Text = emoji;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SelectedEmojiText, $"Selected emoji {emoji}");

        var recent = _controller.RecentEmoji;
        RecentEmojiGrid.ItemsSource = recent.ToList();
        RecentEmojiPanel.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        GradientDirectionSlider.Value = _controller.GradientDirectionDegrees;
        PaddingSlider.Value = _controller.CanvasPadding;
        CornerSlider.Value = _controller.CanvasCornerRadius;
        ShadowSlider.Value = _controller.CanvasShadow;
        ExportFrameCombo.SelectedIndex = (int)_controller.FramePreset;
        SyncAlignmentGrid();
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
        var showsEmoji = tool is EditTool.Emoji;
        var showsBackground = tool is EditTool.Background;

        ColorSection.Visibility = showsColor ? Visibility.Visible : Visibility.Collapsed;
        ShowColorLabel(tool);
        ShowOutline(tool, _controller.OutlineColorDefault, _controller.OutlineWidthDefault);
        StrokeSection.Visibility = showsStroke ? Visibility.Visible : Visibility.Collapsed;
        ArrowStyleSection.Visibility = showsArrowStyle ? Visibility.Visible : Visibility.Collapsed;
        ShowStrokeEnds(
            tool,
            _controller.StartCapDefault,
            _controller.EndCapDefault,
            _controller.LineJoinDefault,
            tool.UsesOutline() ? _controller.OutlineAlignmentDefault : _controller.StrokeAlignmentDefault);
        FillSection.Visibility = showsFill ? Visibility.Visible : Visibility.Collapsed;
        TextSection.Visibility = showsText ? Visibility.Visible : Visibility.Collapsed;
        CounterSection.Visibility = showsNumber ? Visibility.Visible : Visibility.Collapsed;
        RedactSection.Visibility = showsRedact ? Visibility.Visible : Visibility.Collapsed;
        SensitiveTextSection.Visibility = showsRedact ? Visibility.Visible : Visibility.Collapsed;
        EmojiSection.Visibility = showsEmoji ? Visibility.Visible : Visibility.Collapsed;
        BackgroundSection.Visibility = showsBackground ? Visibility.Visible : Visibility.Collapsed;
        RotationSection.Visibility = Visibility.Collapsed;

        if (showsEmoji)
        {
            SyncEmojiSelection(_controller.EmojiDefault);
        }

        if (showsArrowStyle)
        {
            ShowArrowShape(_controller.ArrowDefault);
        }

        // A selection shown earlier left its values in these controls; show what the tool draws.
        if (showsColor)
        {
            AnnotationColorPicker.Color = _controller.StrokeColor;
        }
        if (showsStroke)
        {
            StrokeSlider.Value = _controller.StrokeThickness;
        }
        if (showsFill)
        {
            FillCheck.IsChecked = _controller.FillEnabled;
            FillColorPicker.Color = _controller.FillEnabled ? _controller.FillColor : Colors.Transparent;
        }
        if (showsNumber)
        {
            NumberSizeSlider.Value = _controller.NumberScale;
            NumberColorPicker.Color = _controller.NumberTextColor;
        }

        if (showsRedact)
        {
            RedactStyleChoice.SelectedIndex = (int)_controller.RedactionStyleDefault;
            RedactionChoice.SelectedIndex = (int)_controller.RedactionLevelDefault;
        }
        if (showsText)
        {
            FontSizeSlider.Value = _controller.TextFontSize;
            SelectFontInCombo(_controller.TextFontFamily);
            SyncTextBoxStyle(_controller.TextBoxStyleDefault);
        }

        InspectorTitle.Text = tool switch
        {
            EditTool.Select => "Select & move",
            EditTool.Background => "Background",
            EditTool.Crop => "Crop",
            EditTool.Rectangle => "Rectangle",
            EditTool.Ellipse => "Ellipse",
            EditTool.Arrow => "Arrow",
            EditTool.Line => "Line",
            EditTool.Pen => "Draw",
            EditTool.Text => "Text",
            EditTool.Counter => "Number badge",
            EditTool.Emoji => "Emoji",
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
        var isEmoji = ann.Tool is EditTool.Emoji;
        var hasColor = isShape || isText || isCounter;

        ColorSection.Visibility = hasColor ? Visibility.Visible : Visibility.Collapsed;
        ShowColorLabel(ann.Tool);
        ShowOutline(ann.Tool, ann.OutlineColor, ann.OutlineWidth);
        StrokeSection.Visibility = isShape ? Visibility.Visible : Visibility.Collapsed;
        ArrowStyleSection.Visibility = isArrow ? Visibility.Visible : Visibility.Collapsed;
        ShowStrokeEnds(ann.Tool, ann.StartCap, ann.EndCap, ann.LineJoin, ann.StrokeAlignment);
        FillSection.Visibility = isFillable ? Visibility.Visible : Visibility.Collapsed;
        TextSection.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
        CounterSection.Visibility = isCounter ? Visibility.Visible : Visibility.Collapsed;
        RedactSection.Visibility = isRedact ? Visibility.Visible : Visibility.Collapsed;
        SensitiveTextSection.Visibility = Visibility.Collapsed;
        EmojiSection.Visibility = isEmoji ? Visibility.Visible : Visibility.Collapsed;
        BackgroundSection.Visibility = Visibility.Collapsed;
        var isRotatable = ann.Tool.StoresRotation();
        RotationSection.Visibility = isRotatable ? Visibility.Visible : Visibility.Collapsed;
        InspectorTitle.Text = $"{ann.Tool} (selected)";

        if (isRotatable)
        {
            RotationSlider.Value = ann.Rotation;
        }
        if (isEmoji)
        {
            SyncEmojiSelection(ann.Text);
        }

        if (isArrow)
        {
            ShowArrowShape(ann.Arrow);
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
            SyncTextBoxStyle(ann.TextBoxStyle);
        }
        if (isCounter)
        {
            NumberSizeSlider.Value = ann.SizeScale;
            NumberColorPicker.Color = ann.TextColor;
        }
        if (isRedact)
        {
            RedactionChoice.SelectedIndex = (int)ann.Redaction;
            RedactStyleChoice.SelectedIndex = (int)ann.RedactStyle;
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

    private void SyncTextBoxStyle(TextBoxStyle style)
    {
        var wasInitializing = _inspectorInitializing;
        _inspectorInitializing = true;
        PlainTextPreset.IsChecked = style.Preset == TextBoxPreset.Plain;
        LightTextPreset.IsChecked = style.Preset == TextBoxPreset.Light;
        DarkTextPreset.IsChecked = style.Preset == TextBoxPreset.Dark;
        AccentTextPreset.IsChecked = style.Preset == TextBoxPreset.Accent;
        TextBackgroundColorPicker.Color = style.BackgroundColor;
        TextBorderColorPicker.Color = style.BorderColor;
        TextBorderWidthSlider.Value = style.BorderWidth;
        TextPaddingSlider.Value = style.Padding;
        TextCornerRadiusSlider.Value = style.CornerRadius;
        TextBorderWidthSlider.Header = $"Border width — {(int)style.BorderWidth} px";
        TextPaddingSlider.Header = $"Padding — {(int)style.Padding} px";
        TextCornerRadiusSlider.Header = $"Corner radius — {(int)style.CornerRadius} px";
        _inspectorInitializing = wasInitializing;
    }

    // -- Style handlers ----------------------------------------------------------------------

    private void OnColorChanged(object? sender, Color color)
    {
        if (!_inspectorInitializing)
        {
            _controller.SetStrokeColor(color);
        }
    }

    private void OnNumberColorChanged(object? sender, Color color)
    {
        if (!_inspectorInitializing)
        {
            _controller.SetNumberColor(color);
        }
    }

    private void OnStrokeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_inspectorInitializing)
        {
            return;
        }

        _controller.SetStrokeThickness(e.NewValue);
        UpdateInspectorHeaders();
    }

    /// <summary>
    /// Names the color and width controls for the tool. Arrows and lines are filled bodies, so
    /// their color is the fill and their width the body's size, with the stroke as an outline;
    /// other shapes' color sits right above their stroke width.
    /// </summary>
    private void ShowColorLabel(EditTool tool)
    {
        var label = tool.UsesOutline() ? "Fill"
            : tool is EditTool.Rectangle or EditTool.Ellipse or EditTool.Pen ? "Stroke color"
            : "Color";
        ColorLabel.Text = label;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AnnotationColorPicker, label);
        _strokeSliderLabel = tool.UsesOutline() ? "Size" : "Stroke";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StrokeSlider, tool.UsesOutline() ? "Size" : "Stroke width");
    }

    // Callers hold _inspectorInitializing, so loading the controls doesn't write back.
    private void ShowOutline(EditTool tool, Color color, double width)
    {
        OutlineSection.Visibility = tool.UsesOutline() ? Visibility.Visible : Visibility.Collapsed;
        OutlineColorPicker.Color = color;
        OutlineSlider.Value = width;
    }

    private void OnOutlineColorChanged(object? sender, Color color)
    {
        if (!_inspectorInitializing)
        {
            _controller.SetOutlineColor(color);
        }
    }

    private void OnOutlineWidthChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_inspectorInitializing)
        {
            return;
        }

        _controller.SetOutlineWidth(e.NewValue);
        UpdateInspectorHeaders();
    }

    // Callers hold _inspectorInitializing, so loading the choices doesn't write back.
    private void ShowStrokeEnds(EditTool tool, StrokeCap startCap, StrokeCap endCap, StrokeJoin join, StrokeAlignment alignment)
    {
        StrokeAlignmentSection.Visibility = tool.UsesAlignment() ? Visibility.Visible : Visibility.Collapsed;
        StrokeAlignmentChoice.SelectedIndex = (int)alignment;
        StartCapSection.Visibility = tool.UsesStartCap() ? Visibility.Visible : Visibility.Collapsed;
        EndCapSection.Visibility = tool.UsesEndCap() ? Visibility.Visible : Visibility.Collapsed;
        StrokeAdvancedExpander.Visibility = tool.UsesJoin() ? Visibility.Visible : Visibility.Collapsed;
        ArrowAdvancedPanel.Visibility = tool == EditTool.Arrow ? Visibility.Visible : Visibility.Collapsed;
        StartCapChoice.SelectedIndex = (int)startCap;
        EndCapChoice.SelectedIndex = (int)endCap;
        LineJoinChoice.SelectedIndex = (int)join;
    }

    // The inspector showing values, or a choice putting its selection back the first time it
    // appears, isn't the user choosing. Only the user's choices are written to the document.
    private bool IsLoadingChoice(StudioSegmented choice) =>
        _inspectorInitializing || choice.IsApplyingTemplate || choice.SelectedIndex < 0;

    private void OnStrokeAlignmentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoadingChoice(StrokeAlignmentChoice))
        {
            return;
        }

        _controller.SetStrokeAlignment((StrokeAlignment)StrokeAlignmentChoice.SelectedIndex);
    }

    private void OnStartCapChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoadingChoice(StartCapChoice))
        {
            return;
        }

        _controller.SetStartCap((StrokeCap)StartCapChoice.SelectedIndex);
    }

    private void OnEndCapChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoadingChoice(EndCapChoice))
        {
            return;
        }

        _controller.SetEndCap((StrokeCap)EndCapChoice.SelectedIndex);
    }

    private void OnLineJoinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoadingChoice(LineJoinChoice))
        {
            return;
        }

        _controller.SetLineJoin((StrokeJoin)LineJoinChoice.SelectedIndex);
    }

    // Callers outside the change handlers hold _inspectorInitializing so loading doesn't write back.
    private void ShowArrowShape(ArrowShapeOptions shape)
    {
        var wasInitializing = _inspectorInitializing;
        _inspectorInitializing = true;
        ArrowPresetChoice.SelectedIndex = (int)shape.Preset;
        ArrowArcSlider.Value = Math.Round(shape.Bend * 100);
        ArrowStartWidthSlider.Value = Math.Round(shape.StartWidthScale * 100);
        ArrowEndWidthSlider.Value = Math.Round(shape.EndWidthScale * 100);
        ArrowHeadSizeSlider.Value = Math.Round(shape.HeadScale * 100);
        ArrowArcSlider.Header = $"Arc — {(int)ArrowArcSlider.Value}%";
        ArrowStartWidthSlider.Header = $"Start width — {(int)ArrowStartWidthSlider.Value}%";
        ArrowEndWidthSlider.Header = $"End width — {(int)ArrowEndWidthSlider.Value}%";
        ArrowHeadSizeSlider.Header = $"Head size — {(int)ArrowHeadSizeSlider.Value}%";
        _inspectorInitializing = wasInitializing;
    }

    private void OnArrowPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoadingChoice(ArrowPresetChoice))
        {
            return;
        }

        var preset = (ArrowPreset)ArrowPresetChoice.SelectedIndex;
        if (preset == ArrowPreset.Custom)
        {
            // Custom is whatever the sliders say, so choosing it opens them and leaves the shape alone.
            StrokeAdvancedExpander.IsExpanded = true;
            return;
        }

        var shape = ArrowShapeOptions.For(preset);
        _controller.SetArrowShape(shape);
        ShowArrowShape(shape);
    }

    private void OnArrowSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_inspectorInitializing)
        {
            return;
        }

        var shape = new ArrowShapeOptions(
            ArrowArcSlider.Value / 100,
            ArrowStartWidthSlider.Value / 100,
            ArrowEndWidthSlider.Value / 100,
            ArrowHeadSizeSlider.Value / 100);
        _controller.SetArrowShape(shape);
        ShowArrowShape(shape);
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

    private void OnTextPresetClick(object sender, RoutedEventArgs e)
    {
        if (_inspectorInitializing || sender is not ToggleButton { Tag: string tag }
            || !Enum.TryParse<TextBoxPreset>(tag, out var preset))
        {
            return;
        }

        var accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var resource)
            && resource is Color color
            ? color
            : Color.FromArgb(255, 0, 120, 212);
        _controller.ApplyTextBoxPreset(preset, accent);
        _inspectorInitializing = true;
        AnnotationColorPicker.Color = _controller.SelectedAnnotation is { Tool: EditTool.Text } ann
            ? ann.Color
            : _controller.StrokeColor;
        _inspectorInitializing = false;
        SyncTextBoxStyle(_controller.SelectedAnnotation is { Tool: EditTool.Text } selected
            ? selected.TextBoxStyle
            : _controller.TextBoxStyleDefault);
    }

    private void OnTextBackgroundColorChanged(object? sender, Color color)
    {
        if (_inspectorInitializing)
        {
            return;
        }
        _controller.SetTextBoxBackground(color);
        SyncTextBoxStyle(CurrentTextBoxStyle());
    }

    private void OnTextBorderColorChanged(object? sender, Color color)
    {
        if (_inspectorInitializing)
        {
            return;
        }
        _controller.SetTextBoxBorderColor(color);
        SyncTextBoxStyle(CurrentTextBoxStyle());
    }

    private void OnTextBorderWidthChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_inspectorInitializing)
        {
            return;
        }
        _controller.SetTextBoxBorderWidth(e.NewValue);
        SyncTextBoxStyle(CurrentTextBoxStyle());
    }

    private void OnTextPaddingChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_inspectorInitializing)
        {
            return;
        }
        _controller.SetTextBoxPadding(e.NewValue);
        SyncTextBoxStyle(CurrentTextBoxStyle());
    }

    private void OnTextCornerRadiusChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_inspectorInitializing)
        {
            return;
        }
        _controller.SetTextBoxCornerRadius(e.NewValue);
        SyncTextBoxStyle(CurrentTextBoxStyle());
    }

    private void OnTextStyleSliderPointerPressed(object sender, PointerRoutedEventArgs e) =>
        _controller.BeginSelectedTextStyleEdit();

    private void OnTextStyleSliderPointerReleased(object sender, PointerRoutedEventArgs e) =>
        _controller.EndSelectedTextStyleEdit();

    private void OnTextStyleSliderPointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        _controller.EndSelectedTextStyleEdit();

    private TextBoxStyle CurrentTextBoxStyle() =>
        _controller.SelectedAnnotation is { Tool: EditTool.Text } ann
            ? ann.TextBoxStyle
            : _controller.TextBoxStyleDefault;

    private void OnNumberSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_inspectorInitializing)
        {
            return;
        }

        _controller.SetNumberSize(e.NewValue);
        UpdateInspectorHeaders();
    }

    private void OnRotationChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_inspectorInitializing)
        {
            return;
        }

        _controller.SetRotation(e.NewValue);
        UpdateInspectorHeaders();
    }

    private void OnOpenEmojiPanelClick(object sender, RoutedEventArgs e)
    {
        // The system emoji panel inserts into whichever edit control has focus, so the
        // custom box must be focused first; OnCustomEmojiTextChanged then adopts the glyph.
        CustomEmojiBox.Focus(FocusState.Programmatic);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!TryShowEmojiPanelViaCoreInputView())
            {
                SendWinPeriodShortcut();
            }
        });
    }

    private static bool TryShowEmojiPanelViaCoreInputView()
    {
        try
        {
            return Windows.UI.ViewManagement.Core.CoreInputView.GetForCurrentView()
                .TryShow(Windows.UI.ViewManagement.Core.CoreInputViewKind.Emoji);
        }
        catch (Exception)
        {
            // CoreInputView is not always reachable from a desktop (non-CoreWindow) thread.
            return false;
        }
    }

    /// <summary>Synthesizes Win+. so the shell opens its emoji panel over the focused TextBox.</summary>
    private static void SendWinPeriodShortcut()
    {
        const ushort VK_LWIN = 0x5B;
        const ushort VK_OEM_PERIOD = 0xBE;
        const uint KEYEVENTF_KEYUP = 0x0002;

        var inputs = new NativeInput[]
        {
            NativeInput.Key(VK_LWIN, 0),
            NativeInput.Key(VK_OEM_PERIOD, 0),
            NativeInput.Key(VK_OEM_PERIOD, KEYEVENTF_KEYUP),
            NativeInput.Key(VK_LWIN, KEYEVENTF_KEYUP),
        };
        _ = SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeInput>());
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint cInputs, NativeInput[] pInputs, int cbSize);

    // INPUT is a tagged union; the MouseInput member sizes it correctly (40 bytes on 64-bit)
    // so SendInput accepts cbSize. The app only ships 64-bit (x64/ARM64) builds.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    private struct NativeInput
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public uint Type;
        [System.Runtime.InteropServices.FieldOffset(8)] public KeyboardInput Ki;
        [System.Runtime.InteropServices.FieldOffset(8)] public MouseInput Mi;

        public static NativeInput Key(ushort vk, uint flags) => new()
        {
            Type = 1, // INPUT_KEYBOARD
            Ki = new KeyboardInput { Vk = vk, Flags = flags },
        };
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private void OnEmojiItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is string emoji)
        {
            _controller.SetEmoji(emoji);
        }
    }

    private void OnCustomEmojiTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_inspectorInitializing)
        {
            return;
        }

        var emoji = EmojiAnnotationMath.ExtractEmoji(CustomEmojiBox.Text);
        if (emoji is null)
        {
            return;
        }

        _controller.SetEmoji(emoji);
        _inspectorInitializing = true;
        CustomEmojiBox.Text = string.Empty;
        _inspectorInitializing = false;
    }

    // Item order matches RedactionLevel and RedactionStyle.
    private void OnRedactionLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoadingChoice(RedactionChoice))
        {
            return;
        }

        _controller.SetRedactionLevel((RedactionLevel)RedactionChoice.SelectedIndex);
    }

    private void OnRedactionStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoadingChoice(RedactStyleChoice))
        {
            return;
        }

        _controller.SetRedactionStyle((RedactionStyle)RedactStyleChoice.SelectedIndex);
    }

    // -- Export background handlers -----------------------------------------------------------

    private void OnPresetSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BackgroundPreset preset })
        {
            _controller.ApplyPreset(preset);

            _bgInitializing = true;
            BgStyleCombo.SelectedIndex = preset.Style == ExportBackgroundStyle.Gradient ? 2 : 1;
            _bgInitializing = false;

            SyncBackgroundColorSwatches();
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
            3 => ExportBackgroundStyle.Image,
            _ => ExportBackgroundStyle.Transparent,
        };
        if (style == ExportBackgroundStyle.Image && !_controller.HasBackgroundImage)
        {
            SyncBackgroundControls();
            BackgroundImageRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        _controller.SetBackgroundStyle(style);
        UpdateBackgroundStyleUi();
    }

    private void SyncBackgroundControls()
    {
        var wasInitializing = _bgInitializing;
        _bgInitializing = true;
        try
        {
            BgStyleCombo.SelectedIndex = (int)_controller.BgStyle;
            BgStyleCombo.IsEnabled = !_backgroundSelectionBusy;
            BackgroundImagePlacementChoice.SelectedIndex = (int)_controller.ImagePlacement;
            BackgroundImagePlacementChoice.IsEnabled = !_backgroundSelectionBusy && _controller.HasBackgroundImage;
            ChooseBackgroundImageButton.IsEnabled = !_backgroundSelectionBusy;
            ChooseBackgroundImageButton.Content = _controller.HasBackgroundImage ? "Replace image..." : "Choose image...";
            RemoveBackgroundImageButton.IsEnabled = !_backgroundSelectionBusy && _controller.HasBackgroundImage;
            BackgroundImageName.Text = _backgroundSelectionBusy ? "Choosing image..." : _controller.BackgroundFileName ?? "No image selected";
            UpdateBackgroundStyleUi();
        }
        finally
        {
            _bgInitializing = wasInitializing;
        }
    }

    private void OnChooseBackgroundImage(object sender, RoutedEventArgs e) =>
        BackgroundImageRequested?.Invoke(this, EventArgs.Empty);

    private void OnRemoveBackgroundImage(object sender, RoutedEventArgs e) => _controller.RemoveBackgroundImage();

    private void OnBackgroundImagePlacementChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bgInitializing || IsLoadingChoice(BackgroundImagePlacementChoice)) return;
        _controller.SetBackgroundImagePlacement((BackgroundImagePlacement)BackgroundImagePlacementChoice.SelectedIndex);
    }

    private void UpdateBackgroundStyleUi()
    {
        var showSolid = _controller.BgStyle == ExportBackgroundStyle.Solid;
        var showGradient = _controller.BgStyle == ExportBackgroundStyle.Gradient;
        BackgroundImagePanel.Visibility = _controller.BgStyle == ExportBackgroundStyle.Image || _backgroundSelectionBusy
            ? Visibility.Visible : Visibility.Collapsed;

        SolidPresetHeader.Visibility = showSolid ? Visibility.Visible : Visibility.Collapsed;
        SolidPresetGrid.Visibility = showSolid ? Visibility.Visible : Visibility.Collapsed;
        GradientPresetHeader.Visibility = showGradient ? Visibility.Visible : Visibility.Collapsed;
        GradientPresetGrid.Visibility = showGradient ? Visibility.Visible : Visibility.Collapsed;
        GradientDirectionSlider.Visibility = showGradient ? Visibility.Visible : Visibility.Collapsed;
        CustomColorPanel.Visibility = showSolid ? Visibility.Visible : Visibility.Collapsed;
        CustomGradientColorPanel.Visibility = showGradient ? Visibility.Visible : Visibility.Collapsed;
        SyncBackgroundColorSwatches();
    }

    private void SyncBackgroundColorSwatches()
    {
        CustomSolidColorSwatch.Background = new SolidColorBrush(_controller.BgColor);
        GradientStartColorSwatch.Background = new SolidColorBrush(_controller.BgColor);
        GradientEndColorSwatch.Background = new SolidColorBrush(_controller.BgColor2);
    }

    private async void OnChooseCustomSolidColor(object sender, RoutedEventArgs e)
    {
        var color = await ShowBackgroundColorDialogAsync("Choose a solid background color", _controller.BgColor);
        if (color is not { } selectedColor)
        {
            return;
        }

        _controller.ApplyCustomSolidBackground(selectedColor);

        _bgInitializing = true;
        BgStyleCombo.SelectedIndex = 1;
        _bgInitializing = false;

        UpdateBackgroundStyleUi();
    }

    private async void OnChooseGradientStartColor(object sender, RoutedEventArgs e)
    {
        var color = await ShowBackgroundColorDialogAsync("Choose the first gradient color", _controller.BgColor);
        if (color is not { } selectedColor)
        {
            return;
        }

        _controller.SetCustomBgColor(selectedColor);
        SyncBackgroundColorSwatches();
    }

    private async void OnChooseGradientEndColor(object sender, RoutedEventArgs e)
    {
        var color = await ShowBackgroundColorDialogAsync("Choose the second gradient color", _controller.BgColor2);
        if (color is not { } selectedColor)
        {
            return;
        }

        _controller.SetCustomBgColor2(selectedColor);
        SyncBackgroundColorSwatches();
    }

    private async Task<Color?> ShowBackgroundColorDialogAsync(string title, Color initialColor)
    {
        var picker = new ColorPicker
        {
            Color = initialColor,
            ColorSpectrumShape = ColorSpectrumShape.Box,
            IsAlphaEnabled = false,
            IsColorChannelTextInputVisible = false,
            IsColorPreviewVisible = true,
            IsColorSliderVisible = true,
            IsHexInputVisible = true,
            IsMoreButtonVisible = false,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, title);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = picker,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
            RequestedTheme = this.ActualTheme,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "EditorBackgroundColorDialog");

        _backgroundColorDialog = dialog;
        try
        {
            var showOperation = dialog.ShowAsync();
            _backgroundColorDialogOperation = showOperation;
            return await showOperation == ContentDialogResult.Primary ? picker.Color : null;
        }
        finally
        {
            if (ReferenceEquals(_backgroundColorDialog, dialog))
            {
                _backgroundColorDialog = null;
                _backgroundColorDialogOperation = null;
            }
        }
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

    private void OnGradientDirectionChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_bgInitializing)
        {
            return;
        }

        _controller.SetGradientDirectionDegrees(e.NewValue);
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

    private void OnAlignmentCellClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton cell || cell.Tag is not string tag || !TryParseAlignmentTag(tag, out var h, out var v))
        {
            return;
        }

        if (!_bgInitializing)
        {
            _controller.SetHorizontalExportAlignment(h);
            _controller.SetVerticalExportAlignment(v);
        }

        // The grid is a single-selection control: the clicked cell is always the one checked,
        // even when the user clicks the already-selected cell (ToggleButton would uncheck it).
        SyncAlignmentGrid();
    }

    private static bool TryParseAlignmentTag(string tag, out ExportHorizontalAlignment h, out ExportVerticalAlignment v)
    {
        h = ExportHorizontalAlignment.Center;
        v = ExportVerticalAlignment.Center;
        var parts = tag.Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var hi) || !int.TryParse(parts[1], out var vi))
        {
            return false;
        }

        h = (ExportHorizontalAlignment)Math.Clamp(hi, 0, 2);
        v = (ExportVerticalAlignment)Math.Clamp(vi, 0, 2);
        return true;
    }

    private IEnumerable<ToggleButton> AlignmentCells => AlignmentGrid.Children.OfType<ToggleButton>();

    /// <summary>Checks exactly the cell matching the controller's current H+V alignment.</summary>
    private void SyncAlignmentGrid()
    {
        var h = _controller.HorizontalExportAlignment;
        var v = _controller.VerticalExportAlignment;
        foreach (var cell in AlignmentCells)
        {
            var selected = cell.Tag is string tag &&
                TryParseAlignmentTag(tag, out var cellH, out var cellV) &&
                cellH == h && cellV == v;
            if (cell.IsChecked != selected)
            {
                cell.IsChecked = selected;
            }
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
        GradientDirectionSlider.Header = $"Direction — {(int)_controller.GradientDirectionDegrees}°";
        CornerSlider.Header = $"Image corners — {(int)_controller.CanvasCornerRadius} px";
        ShadowSlider.Header = $"Shadow — {(int)_controller.CanvasShadow}";
    }

    private void UpdateExportFrameControls()
    {
        // Placement only matters when the export frame leaves room on at least one axis
        // (Original has none). A cell's other axis is harmless when that axis has no space.
        var enabled = _controller.HasHorizontalExportFrameSpace || _controller.HasVerticalExportFrameSpace;
        AlignmentHeader.Opacity = enabled ? 1.0 : 0.5;
        foreach (var cell in AlignmentCells)
        {
            cell.IsEnabled = enabled;
        }
    }
}
