using Windows.Storage;
using TinyClips.Core.Models;

namespace TinyClips.Core.Services;

public sealed class SettingsService : ISettingsService, ILargeTextSettingsService
{
    private readonly Dictionary<string, object> _fallbackValues = new(StringComparer.OrdinalIgnoreCase);

    public AppTheme Theme
    {
        get => Get("Theme", AppTheme.Default);
        set => Set("Theme", value);
    }

    public string SaveDirectory
    {
        get => Get("SaveDirectory", string.Empty);
        set => Set("SaveDirectory", value);
    }

    public T Get<T>(string key, T defaultValue)
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out var storedValue))
            {
                if (storedValue is T typedValue)
                {
                    return typedValue;
                }

                if (storedValue is string stringValue && typeof(T).IsEnum)
                {
                    return (T)Enum.Parse(typeof(T), stringValue, true);
                }
            }
        }
        catch
        {
        }

        if (_fallbackValues.TryGetValue(key, out var fallbackValue))
        {
            if (fallbackValue is T typedValue)
            {
                return typedValue;
            }

            if (fallbackValue is string fallbackString && typeof(T).IsEnum)
            {
                return (T)Enum.Parse(typeof(T), fallbackString, true);
            }
        }

        return defaultValue;
    }

    public void Set<T>(string key, T value)
    {
        object persistedValue = value is null ? string.Empty : value;

        if (value is Enum enumValue)
        {
            persistedValue = enumValue.ToString();
        }

        try
        {
            ApplicationData.Current.LocalSettings.Values[key] = persistedValue;
        }
        catch
        {
            _fallbackValues[key] = persistedValue;
        }
    }

    public string GetLargeText(string key, string defaultValue)
    {
        try
        {
            var path = GetLargeTextPath(key);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }

            var legacyValue = Get(key, defaultValue);
            if (!string.Equals(legacyValue, defaultValue, StringComparison.Ordinal))
            {
                SetLargeText(key, legacyValue);
            }

            return legacyValue;
        }
        catch (IOException)
        {
            return Get(key, defaultValue);
        }
        catch (UnauthorizedAccessException)
        {
            return Get(key, defaultValue);
        }
    }

    public void SetLargeText(string key, string value)
    {
        File.WriteAllText(GetLargeTextPath(key), value ?? string.Empty);
    }

    private static string GetLargeTextPath(string key) =>
        Path.Combine(ApplicationData.Current.LocalFolder.Path, $"{key}.txt");
}
