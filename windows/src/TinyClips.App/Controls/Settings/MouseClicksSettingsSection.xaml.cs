using System;
using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TinyClips.App.Settings.Sections;

/// <summary>Mouse-click highlight visibility, size, opacity, and color for video and GIF.</summary>
public sealed partial class MouseClicksSettingsSection : UserControl
{
    private readonly IDisposable _realizationScope;

    public SettingsViewModel ViewModel { get; }

    public MouseClicksSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        _realizationScope = viewModel.BeginSectionRealization();
        InitializeComponent();
        SectionLifecycle.HookFirstLoad(this, viewModel, _realizationScope);
    }

    /// <summary>Used by the preview-ring x:Bind so the stroke brush updates live as the hex value changes.</summary>
    public static SolidColorBrush HexToBrush(string? hex) => new(ParseHexColor(hex));

    private void OnVideoColorFlyoutOpening(object? sender, object e)
    {
        VideoColorPicker.Color = ParseHexColor(ViewModel.VideoMouseClickColorHex);
    }

    private void OnVideoColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        ViewModel.VideoMouseClickColorHex = ToHex(args.NewColor);
    }

    private void OnGifColorFlyoutOpening(object? sender, object e)
    {
        GifColorPicker.Color = ParseHexColor(ViewModel.GifMouseClickColorHex);
    }

    private void OnGifColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        ViewModel.GifMouseClickColorHex = ToHex(args.NewColor);
    }

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color ParseHexColor(string? hex)
    {
        var s = (hex ?? string.Empty).Trim().TrimStart('#');
        if (s.Length == 8)
        {
            s = s[2..];
        }

        if (s.Length == 6 &&
            byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return Color.FromArgb(255, r, g, b);
        }

        return Color.FromArgb(255, 255, 214, 10);
    }
}
