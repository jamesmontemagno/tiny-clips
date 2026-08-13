namespace TinyClips.App.Settings.Sections;

/// <summary>
/// Implemented by section controls that need to know when the owning <c>SettingsWindow</c> has
/// closed, even if the section is currently cached but not the visible content (e.g. an in-flight
/// permission prompt or device-enumeration continuation that must stop touching UI state).
/// </summary>
public interface ISettingsSectionLifecycle
{
    /// <summary>Called by the shell when the Settings window closes.</summary>
    void NotifyWindowClosed();
}
