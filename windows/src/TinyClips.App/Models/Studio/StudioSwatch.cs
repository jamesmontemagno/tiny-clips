using System.Globalization;
using TinyClips.App.ScreenshotEditor;
using TinyClips.Core.Editing;
using TinyClips.Core.Studio;
using Windows.UI;

namespace TinyClips.App.Models.Studio;

/// <summary>
/// One background the Studio inspector offers. These are the screenshot editor's solid and
/// gradient presets: a project stores the preset's id and its colors as <c>#RRGGBB</c>, and both
/// have to mean the same on Windows and on the Mac.
/// </summary>
public sealed class StudioSwatch
{
    /// <summary>The background a new project starts with, and the one "Show background" brings back.</summary>
    public const string DefaultPresetId = "ocean";

    private StudioSwatch(BackgroundPreset preset)
    {
        Id = preset.Id;
        Label = preset.Label;
        Style = preset.Style == ExportBackgroundStyle.Gradient ? StudioBackgroundStyle.Gradient : StudioBackgroundStyle.Solid;
        Primary = preset.Primary;
        Secondary = Style == StudioBackgroundStyle.Gradient ? preset.Secondary : null;
    }

    /// <summary>The solid presets, in the order the screenshot editor shows them.</summary>
    public static IReadOnlyList<StudioSwatch> Solid { get; } = Create(EditorController.SolidPresets, ExportBackgroundStyle.Solid);

    /// <summary>The gradient presets, in the order the screenshot editor shows them.</summary>
    public static IReadOnlyList<StudioSwatch> Gradient { get; } = Create(EditorController.GradientPresets, ExportBackgroundStyle.Gradient);

    public string Id { get; }

    public string Label { get; }

    public StudioBackgroundStyle Style { get; }

    public Color Primary { get; }

    public Color? Secondary { get; }

    public string PrimaryHex => ToHex(Primary);

    public string? SecondaryHex => Secondary is { } color ? ToHex(color) : null;

    /// <summary>What a screen reader calls the swatch, such as "Ocean background".</summary>
    public string AccessibleName => $"{Label} background";

    public static StudioSwatch? Find(string? presetId) =>
        presetId is null
            ? null
            : Solid.Concat(Gradient).FirstOrDefault(swatch => string.Equals(swatch.Id, presetId, StringComparison.Ordinal));

    private static StudioSwatch[] Create(IEnumerable<BackgroundPreset> presets, ExportBackgroundStyle style) =>
        presets.Where(preset => preset.Style == style).Select(preset => new StudioSwatch(preset)).ToArray();

    private static string ToHex(Color color) =>
        string.Create(CultureInfo.InvariantCulture, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
}
