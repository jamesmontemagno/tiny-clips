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
    void SetLargeText(string key, string value);
}
