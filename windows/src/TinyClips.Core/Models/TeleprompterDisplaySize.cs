namespace TinyClips.Core.Models;

/// <summary>
/// Small / Medium / Large presets for the teleprompter overlay's text size and panel height.
/// Values mirror the macOS <c>TeleprompterDisplaySize</c> so both apps read the same way.
/// </summary>
public enum TeleprompterDisplaySize
{
    Small = 0,
    Medium = 1,
    Large = 2,
}

public static class TeleprompterDisplaySizeExtensions
{
    /// <summary>Vertical padding inside the panel (top + bottom) that is not part of the text viewport.</summary>
    public const double PanelVerticalPaddingDip = 24;

    /// <summary>Overlay transcript font size, in DIPs.</summary>
    public static double FontSize(this TeleprompterDisplaySize size) => size switch
    {
        TeleprompterDisplaySize.Small => 20,
        TeleprompterDisplaySize.Large => 30,
        _ => 24,
    };

    /// <summary>Total overlay panel height (including padding), in DIPs.</summary>
    public static double PanelHeight(this TeleprompterDisplaySize size) => size switch
    {
        TeleprompterDisplaySize.Small => 120,
        TeleprompterDisplaySize.Large => 220,
        _ => 140,
    };

    /// <summary>Height of the scrolling text viewport (panel height minus padding), in DIPs.</summary>
    public static double ViewportHeight(this TeleprompterDisplaySize size) =>
        size.PanelHeight() - PanelVerticalPaddingDip;

    public static string Label(this TeleprompterDisplaySize size) => size switch
    {
        TeleprompterDisplaySize.Small => "Small",
        TeleprompterDisplaySize.Large => "Large",
        _ => "Medium",
    };
}
