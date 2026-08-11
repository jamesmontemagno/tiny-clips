using System.Diagnostics;
using Windows.Storage;
using TinyClips.Core.Models;

namespace TinyClips.Core.Services;

public sealed class SettingsService : ISettingsService, ILargeTextSettingsService
{
    private readonly Dictionary<string, object> _fallbackValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _largeTextDirectory;

    public SettingsService()
    {
    }

    internal SettingsService(string largeTextDirectory)
    {
        _largeTextDirectory = largeTextDirectory;
    }

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
        if (_fallbackValues.TryGetValue(key, out var fallbackValue) &&
            fallbackValue is string fallbackText)
        {
            SetLargeText(key, fallbackText);
            return fallbackText;
        }

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
        var persistedValue = value ?? string.Empty;
        var path = GetLargeTextPath(key);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporaryPath, persistedValue);
            File.Move(temporaryPath, path, overwrite: true);
            _fallbackValues.Remove(key);
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"Failed to persist large text setting '{key}': {ex}");
            _fallbackValues[key] = persistedValue;
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"Failed to persist large text setting '{key}': {ex}");
            _fallbackValues[key] = persistedValue;
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
        }
    }

    private string GetLargeTextPath(string key) =>
        Path.Combine(_largeTextDirectory ?? ApplicationData.Current.LocalFolder.Path, $"{key}.txt");

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"Failed to remove temporary settings file '{path}': {ex}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"Failed to remove temporary settings file '{path}': {ex}");
        }
    }
}
