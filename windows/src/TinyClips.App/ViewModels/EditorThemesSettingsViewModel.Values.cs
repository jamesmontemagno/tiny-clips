using TinyClips.Core.Editing.Themes;
using Windows.UI;

namespace TinyClips.App.ViewModels;

/// <summary>The theme's values as the form binds them. Sizes are the theme's own units.</summary>
public sealed partial class EditorThemesSettingsViewModel
{
    private static readonly Color DefaultGradientEnd = Color.FromArgb(255, 51, 133, 245);

    private EditorThemeShape Shape => (_theme ?? Placeholder).Shape;
    private EditorThemeLine Line => (_theme ?? Placeholder).Line;
    private EditorThemeText Text => (_theme ?? Placeholder).Text;
    private EditorThemeNumber Number => (_theme ?? Placeholder).Number;
    private EditorThemeCanvas Canvas => (_theme ?? Placeholder).Canvas;

    private static readonly EditorTheme Placeholder = EditorTheme.CreateDefault(string.Empty, "Theme");

    private static Color ToColor(string hex) => EditorTheme.ColorOf(hex);

    private static string ToHex(Color color) => EditorTheme.HexOf(color);

    /// <summary>Clearing a NumberBox gives NaN, which leaves the value as it was.</summary>
    private void EditNumber(double value, Func<EditorTheme, double, EditorTheme> change)
    {
        if (double.IsFinite(value))
        {
            Edit(theme => change(theme, value));
        }
    }

    /// <summary>What's typed stays as typed; a blank name isn't saved and leaves the saved one.</summary>
    public string NameText
    {
        get => _nameText;
        set
        {
            if (_nameText == value)
            {
                return;
            }

            _nameText = value;
            var name = value.Trim();
            if (name.Length == 0)
            {
                ErrorMessage = "A theme needs a name.";
                return;
            }

            Edit(theme => theme with { Name = name });
        }
    }

    // -- Shapes ----------------------------------------------------------------------------------

    public Color ShapeStrokeColor
    {
        get => ToColor(Shape.StrokeColor);
        set => Edit(t => t with { Shape = t.Shape with { StrokeColor = ToHex(value) } });
    }

    public double ShapeStrokeWidth
    {
        get => Shape.StrokeWidth;
        set => EditNumber(value, (t, v) => t with { Shape = t.Shape with { StrokeWidth = v } });
    }

    /// <summary>A gradient needs a fill, so clearing the fill also ends the gradient.</summary>
    public Color ShapeFillColor
    {
        get => ToColor(Shape.FillColor);
        set => Edit(t => t with
        {
            Shape = t.Shape with
            {
                FillColor = ToHex(value),
                GradientEndColor = value.A == 0 ? null : t.Shape.GradientEndColor,
            },
        });
    }

    public bool HasVisibleFill => ShapeFillColor.A > 0;

    public bool HasShapeGradient
    {
        get => Shape.GradientEndColor is not null;
        set => Edit(t => t with
        {
            Shape = t.Shape with
            {
                GradientEndColor = value && ToColor(t.Shape.FillColor).A > 0
                    ? t.Shape.GradientEndColor ?? ToHex(DefaultGradientEnd)
                    : null,
            },
        });
    }

    public Color ShapeGradientEndColor
    {
        get => Shape.GradientEndColor is { } end ? ToColor(end) : DefaultGradientEnd;
        set => Edit(t => t.Shape.GradientEndColor is null ? t : t with { Shape = t.Shape with { GradientEndColor = ToHex(value) } });
    }

    public double ShapeGradientDirection
    {
        get => Shape.GradientDirection;
        set => EditNumber(value, (t, v) => t with { Shape = t.Shape with { GradientDirection = v } });
    }

    public double ShapeShadowBlur
    {
        get => Shape.ShadowBlur;
        set => EditNumber(value, (t, v) => t with { Shape = t.Shape with { ShadowBlur = v } });
    }

    public Color ShapeShadowColor
    {
        get => ToColor(Shape.ShadowColor);
        set => Edit(t => t with { Shape = t.Shape with { ShadowColor = ToHex(value) } });
    }

    // -- Lines -----------------------------------------------------------------------------------

    public Color LineColor
    {
        get => ToColor(Line.Color);
        set => Edit(t => t with { Line = t.Line with { Color = ToHex(value) } });
    }

    public double LineWidth
    {
        get => Line.Width;
        set => EditNumber(value, (t, v) => t with { Line = t.Line with { Width = v } });
    }

    public int ArrowStyleIndex
    {
        get => Array.IndexOf(EditorTheme.ArrowStyles, Line.ArrowStyle);
        set => EditChoice(value, EditorTheme.ArrowStyles, (t, v) => t with { Line = t.Line with { ArrowStyle = v } });
    }

    // -- Text ------------------------------------------------------------------------------------

    public Color TextColor
    {
        get => ToColor(Text.Color);
        set => Edit(t => t with { Text = t.Text with { Color = ToHex(value) } });
    }

    /// <summary>Blank means the platform's default font.</summary>
    public string FontFamilyText
    {
        get => Text.FontFamily ?? string.Empty;
        set => Edit(t => t with { Text = t.Text with { FontFamily = string.IsNullOrWhiteSpace(value) ? null : value.Trim() } });
    }

    public double FontSize
    {
        get => Text.FontSize;
        set => EditNumber(value, (t, v) => t with { Text = t.Text with { FontSize = v } });
    }

    public bool Bold
    {
        get => Text.Bold;
        set => Edit(t => t with { Text = t.Text with { Bold = value } });
    }

    public bool Italic
    {
        get => Text.Italic;
        set => Edit(t => t with { Text = t.Text with { Italic = value } });
    }

    public bool Underline
    {
        get => Text.Underline;
        set => Edit(t => t with { Text = t.Text with { Underline = value } });
    }

    public Color TextBoxBackgroundColor
    {
        get => ToColor(Text.Box.BackgroundColor);
        set => Edit(t => t with { Text = t.Text with { Box = t.Text.Box with { BackgroundColor = ToHex(value) } } });
    }

    public Color TextBoxBorderColor
    {
        get => ToColor(Text.Box.BorderColor);
        set => Edit(t => t with { Text = t.Text with { Box = t.Text.Box with { BorderColor = ToHex(value) } } });
    }

    public double TextBoxBorderWidth
    {
        get => Text.Box.BorderWidth;
        set => EditNumber(value, (t, v) => t with { Text = t.Text with { Box = t.Text.Box with { BorderWidth = v } } });
    }

    public double TextBoxPadding
    {
        get => Text.Box.Padding;
        set => EditNumber(value, (t, v) => t with { Text = t.Text with { Box = t.Text.Box with { Padding = v } } });
    }

    public double TextBoxCornerRadius
    {
        get => Text.Box.CornerRadius;
        set => EditNumber(value, (t, v) => t with { Text = t.Text with { Box = t.Text.Box with { CornerRadius = v } } });
    }

    // -- Numbers ---------------------------------------------------------------------------------

    public Color NumberBadgeColor
    {
        get => ToColor(Number.BadgeColor);
        set => Edit(t => t with { Number = t.Number with { BadgeColor = ToHex(value) } });
    }

    public Color NumberTextColor
    {
        get => ToColor(Number.TextColor);
        set => Edit(t => t with { Number = t.Number with { TextColor = ToHex(value) } });
    }

    public double NumberSize
    {
        get => Number.Size;
        set => EditNumber(value, (t, v) => t with { Number = t.Number with { Size = v } });
    }

    // -- Canvas ----------------------------------------------------------------------------------

    public bool CanvasApplyOnOpen
    {
        get => Canvas.ApplyOnOpen;
        set => Edit(t => t with { Canvas = t.Canvas with { ApplyOnOpen = value } });
    }

    public int CanvasBackgroundIndex
    {
        get => Array.IndexOf(EditorTheme.Backgrounds, Canvas.Background);
        set => EditChoice(value, EditorTheme.Backgrounds, (t, v) => t with { Canvas = t.Canvas with { Background = v } });
    }

    public Color CanvasColor
    {
        get => ToColor(Canvas.Color);
        set => Edit(t => t with { Canvas = t.Canvas with { Color = ToHex(value) } });
    }

    public Color CanvasSecondaryColor
    {
        get => ToColor(Canvas.SecondaryColor);
        set => Edit(t => t with { Canvas = t.Canvas with { SecondaryColor = ToHex(value) } });
    }

    public double CanvasGradientDirection
    {
        get => Canvas.GradientDirection;
        set => EditNumber(value, (t, v) => t with { Canvas = t.Canvas with { GradientDirection = v } });
    }

    public double CanvasPadding
    {
        get => Canvas.Padding;
        set => EditNumber(value, (t, v) => t with { Canvas = t.Canvas with { Padding = v } });
    }

    public double CanvasCornerRadius
    {
        get => Canvas.CornerRadius;
        set => EditNumber(value, (t, v) => t with { Canvas = t.Canvas with { CornerRadius = v } });
    }

    public double CanvasShadow
    {
        get => Canvas.Shadow;
        set => EditNumber(value, (t, v) => t with { Canvas = t.Canvas with { Shadow = v } });
    }

    public int CanvasFrameIndex
    {
        get => Array.IndexOf(EditorTheme.Frames, Canvas.Frame);
        set => EditChoice(value, EditorTheme.Frames, (t, v) => t with { Canvas = t.Canvas with { Frame = v } });
    }

    public int CanvasHorizontalAlignmentIndex
    {
        get => Array.IndexOf(EditorTheme.HorizontalAlignments, Canvas.HorizontalAlignment);
        set => EditChoice(value, EditorTheme.HorizontalAlignments, (t, v) => t with { Canvas = t.Canvas with { HorizontalAlignment = v } });
    }

    public int CanvasVerticalAlignmentIndex
    {
        get => Array.IndexOf(EditorTheme.VerticalAlignments, Canvas.VerticalAlignment);
        set => EditChoice(value, EditorTheme.VerticalAlignments, (t, v) => t with { Canvas = t.Canvas with { VerticalAlignment = v } });
    }

    private void EditChoice(int index, string[] choices, Func<EditorTheme, string, EditorTheme> change)
    {
        if (index >= 0 && index < choices.Length)
        {
            Edit(theme => change(theme, choices[index]));
        }
    }
}
