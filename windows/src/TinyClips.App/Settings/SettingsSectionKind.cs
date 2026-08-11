namespace TinyClips.App.Settings;

/// <summary>
/// Identifies each Settings section. Values intentionally match the <c>Tag</c> string set on the
/// corresponding <c>NavigationViewItem</c> in <c>SettingsWindow.xaml</c>, so the shell can resolve
/// the selected item's tag straight into a <see cref="SettingsSectionKind"/> via
/// <see cref="System.Enum.TryParse{TEnum}(string, out TEnum)"/>.
/// </summary>
public enum SettingsSectionKind
{
    General,
    Uploadcare,
    Analytics,
    Screenshot,
    Video,
    Gif,
    MouseClicks,
    Branding,
    Teleprompter,
    Hotkeys,
    About,
}
