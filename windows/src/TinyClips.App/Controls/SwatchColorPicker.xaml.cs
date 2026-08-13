using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Color = Windows.UI.Color;

namespace TinyClips.App;

/// <summary>
/// Reusable color control for the editor: a dropdown button that previews the current color and
/// opens a flyout with a grid of common preset color swatches plus a "Custom…" button that reveals
/// the platform-native full color picker. When <see cref="AllowTransparent"/> is set it also offers
/// a "None" (transparent) swatch so surfaces like shape fill can be cleared.
/// </summary>
/// <remarks>
/// Assigning <see cref="Color"/> programmatically re-syncs the picker, preview, and selection ring
/// without raising <see cref="ColorChanged"/>, so hosts can push state in (e.g. when selecting an
/// annotation) with no feedback loop. <see cref="ColorChanged"/> fires only for user input.
/// </remarks>
public sealed partial class SwatchColorPicker : UserControl
{
    public sealed record ColorSwatchPreset(string Name, Color Color);

    private static readonly ColorSwatchPreset[] Presets =
    {
        new("Black", Color.FromArgb(255, 0, 0, 0)),
        new("White", Color.FromArgb(255, 255, 255, 255)),
        new("Gray", Color.FromArgb(255, 140, 140, 145)),
        new("Red", Color.FromArgb(255, 230, 51, 46)),
        new("Orange", Color.FromArgb(255, 255, 148, 41)),
        new("Yellow", Color.FromArgb(255, 255, 214, 51)),
        new("Green", Color.FromArgb(255, 61, 179, 87)),
        new("Teal", Color.FromArgb(255, 0, 184, 184)),
        new("Blue", Color.FromArgb(255, 51, 133, 245)),
        new("Purple", Color.FromArgb(255, 140, 89, 230)),
        new("Pink", Color.FromArgb(255, 255, 92, 168)),
        new("Brown", Color.FromArgb(255, 153, 102, 61)),
    };

    private readonly List<Button> _swatchButtons = new();
    private Button? _transparentButton;
    private bool _syncing;

    public SwatchColorPicker()
    {
        InitializeComponent();
        BuildSwatches();
        SyncFromColor(Color);
    }

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color),
        typeof(Color),
        typeof(SwatchColorPicker),
        new PropertyMetadata(Colors.Red, OnColorPropertyChanged));

    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public static readonly DependencyProperty IsAlphaEnabledProperty = DependencyProperty.Register(
        nameof(IsAlphaEnabled),
        typeof(bool),
        typeof(SwatchColorPicker),
        new PropertyMetadata(false, OnIsAlphaEnabledChanged));

    public bool IsAlphaEnabled
    {
        get => (bool)GetValue(IsAlphaEnabledProperty);
        set => SetValue(IsAlphaEnabledProperty, value);
    }

    public static readonly DependencyProperty AllowTransparentProperty = DependencyProperty.Register(
        nameof(AllowTransparent),
        typeof(bool),
        typeof(SwatchColorPicker),
        new PropertyMetadata(false, OnAllowTransparentChanged));

    /// <summary>When true, a leading "None" (transparent) swatch is offered.</summary>
    public bool AllowTransparent
    {
        get => (bool)GetValue(AllowTransparentProperty);
        set => SetValue(AllowTransparentProperty, value);
    }

    /// <summary>Raised only when the user selects a preset swatch or changes the custom picker.</summary>
    public event EventHandler<Color>? ColorChanged;

    private static void OnColorPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((SwatchColorPicker)d).SyncFromColor((Color)e.NewValue);
    }

    private static void OnIsAlphaEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((SwatchColorPicker)d).PART_Picker.IsAlphaEnabled = (bool)e.NewValue;
    }

    private static void OnAllowTransparentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (SwatchColorPicker)d;
        control.BuildSwatches();
        control.SyncFromColor(control.Color);
    }

    private void BuildSwatches()
    {
        PART_Swatches.Children.Clear();
        _swatchButtons.Clear();
        _transparentButton = null;

        if (AllowTransparent)
        {
            var noneButton = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(48, 0, 0, 0)),
                Content = new FontIcon
                {
                    FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                    FontSize = 12,
                    Glyph = "\uE711",
                },
            };
            ToolTipService.SetToolTip(noneButton, "None (transparent)");
            AutomationProperties.SetName(noneButton, "None");
            noneButton.Click += OnTransparentClick;
            _transparentButton = noneButton;
            PART_Swatches.Children.Add(noneButton);
        }

        foreach (var preset in Presets)
        {
            var button = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(preset.Color),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(48, 0, 0, 0)),
                Tag = preset,
            };
            ToolTipService.SetToolTip(button, preset.Name);
            AutomationProperties.SetName(button, preset.Name);
            button.Click += OnSwatchClick;
            _swatchButtons.Add(button);
            PART_Swatches.Children.Add(button);
        }
    }

    private void OnSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ColorSwatchPreset preset })
        {
            ApplyColor(preset.Color);
        }
    }

    private void OnTransparentClick(object sender, RoutedEventArgs e)
    {
        ApplyColor(Colors.Transparent);
    }

    private void OnPickerColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_syncing)
        {
            return;
        }

        ApplyColor(args.NewColor);
    }

    private void ApplyColor(Color color)
    {
        if (!ColorsEqual(Color, color))
        {
            Color = color;
        }
        else
        {
            SyncFromColor(color);
        }

        ColorChanged?.Invoke(this, color);
    }

    private void SyncFromColor(Color color)
    {
        _syncing = true;

        var transparent = IsTransparent(color);

        if (!transparent && !ColorsEqual(PART_Picker.Color, color))
        {
            PART_Picker.Color = color;
        }

        PART_CurrentSwatch.Background = new SolidColorBrush(transparent ? Colors.Transparent : color);
        PART_CustomSwatch.Background = new SolidColorBrush(transparent ? Colors.Transparent : color);
        PART_TransparentGlyph.Visibility = transparent ? Visibility.Visible : Visibility.Collapsed;
        PART_ColorLabel.Text = DescribeColor(color, transparent);

        UpdateSelectionVisuals(color, transparent);

        _syncing = false;
    }

    private string DescribeColor(Color color, bool transparent)
    {
        if (transparent && AllowTransparent)
        {
            return "None";
        }

        foreach (var preset in Presets)
        {
            if (ColorsEqual(preset.Color, color))
            {
                return preset.Name;
            }
        }

        return "Custom";
    }

    private void UpdateSelectionVisuals(Color color, bool transparent)
    {
        var accent = AccentBrush();
        Brush Idle() => new SolidColorBrush(Windows.UI.Color.FromArgb(48, 0, 0, 0));

        if (_transparentButton is not null)
        {
            var selected = transparent && AllowTransparent;
            _transparentButton.BorderBrush = selected ? accent : Idle();
            _transparentButton.BorderThickness = new Thickness(selected ? 2 : 1);
        }

        foreach (var button in _swatchButtons)
        {
            if (button.Tag is ColorSwatchPreset preset)
            {
                var selected = !transparent && ColorsEqual(preset.Color, color);
                button.BorderBrush = selected ? accent : Idle();
                button.BorderThickness = new Thickness(selected ? 2 : 1);
            }
        }
    }

    private static Brush AccentBrush()
    {
        if (Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var value)
            && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Colors.DodgerBlue);
    }

    private static bool IsTransparent(Color color) => color.A == 0;

    private static bool ColorsEqual(Color a, Color b) =>
        a.A == b.A && a.R == b.R && a.G == b.G && a.B == b.B;
}
