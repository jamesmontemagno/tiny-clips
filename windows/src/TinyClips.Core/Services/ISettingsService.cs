using TinyClips.Core.Models;

namespace TinyClips.Core.Services;

public interface ISettingsService
{
    T Get<T>(string key, T defaultValue);
    void Set<T>(string key, T value);

    AppTheme Theme { get; set; }
    string SaveDirectory { get; set; }
}

public interface ILargeTextSettingsService
{
    string GetLargeText(string key, string defaultValue);
    /// <summary>Reads editable text without substituting a default for an inaccessible persisted file.</summary>
    string GetLargeTextForEditing(string key, string defaultValue) => GetLargeText(key, defaultValue);
    void SetLargeText(string key, string value);
}
