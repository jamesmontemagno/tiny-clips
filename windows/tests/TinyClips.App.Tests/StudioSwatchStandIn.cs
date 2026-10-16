using TinyClips.Core.Studio;

namespace TinyClips.App.Models.Studio;

/// <summary>
/// Stands in for the app's <c>StudioSwatch</c> in these tests. The real one takes its colours
/// from the screenshot editor's presets, which live in a class that draws with Win2D and cannot
/// be compiled here. The Studio view model asks a swatch only for what a project stores of it:
/// its id, whether it is a solid colour or a gradient, and its colours. Two are enough.
/// </summary>
public sealed class StudioSwatch
{
    /// <summary>The background "Show background" brings back, as in the app.</summary>
    public const string DefaultPresetId = "ocean";

    private static readonly StudioSwatch[] All =
    [
        new(DefaultPresetId, StudioBackgroundStyle.Gradient, "#2687E8", "#2EE0BF"),
        new("ink", StudioBackgroundStyle.Solid, "#1C1C1E", null),
    ];

    private StudioSwatch(string id, StudioBackgroundStyle style, string primaryHex, string? secondaryHex)
    {
        Id = id;
        Style = style;
        PrimaryHex = primaryHex;
        SecondaryHex = secondaryHex;
    }

    public string Id { get; }

    public StudioBackgroundStyle Style { get; }

    public string PrimaryHex { get; }

    public string? SecondaryHex { get; }

    public static StudioSwatch? Find(string? presetId) =>
        Array.Find(All, swatch => string.Equals(swatch.Id, presetId, StringComparison.Ordinal));
}
