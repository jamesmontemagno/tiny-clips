using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TinyClips.Core.Editing.Themes;

/// <summary>The store already holds <see cref="EditorThemeStore.MaximumThemes"/> themes.</summary>
public sealed class EditorThemeLimitException()
    : InvalidOperationException($"Tiny Clips can keep at most {EditorThemeStore.MaximumThemes} editor themes. Delete one first.");

/// <summary>
/// Saved editor themes and the active theme new editor windows use, persisted atomically to
/// <c>editor-themes.json</c>. Thread-safe; <see cref="Changed"/> may be raised on any thread.
/// Whatever can't be read is copied to <c>editor-themes.json.bak</c>.
/// </summary>
public sealed class EditorThemeStore
{
    public const string FileName = "editor-themes.json";
    public const int MaximumThemes = 200;

    private const int FileVersion = 1;

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _filePath;
    private List<EditorTheme> _themes = [];
    private string? _activeThemeId;

    public EditorThemeStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        ReadFile();
    }

    /// <summary>The default location next to the app's other local data.</summary>
    public static string DefaultFilePath(string localDataDirectory) =>
        Path.Combine(localDataDirectory, "TinyClips", FileName);

    public event EventHandler? Changed;

    public string FilePath => _filePath;

    public string BackupFilePath => _filePath + ".bak";

    public IReadOnlyList<EditorTheme> Themes
    {
        get
        {
            lock (_gate)
            {
                return _themes.ToArray();
            }
        }
    }

    public string? ActiveThemeId
    {
        get
        {
            lock (_gate)
            {
                return _activeThemeId;
            }
        }
    }

    public EditorTheme? ActiveTheme
    {
        get
        {
            lock (_gate)
            {
                return _activeThemeId is null ? null : Find(_activeThemeId);
            }
        }
    }

    public EditorTheme? Get(string? id)
    {
        lock (_gate)
        {
            return Find(id);
        }
    }

    /// <summary>
    /// Creates a theme, or replaces the one with the same id. A blank id gets a new one. Returns
    /// the validated theme as stored.
    /// </summary>
    /// <exception cref="EditorThemeFormatException">The theme is invalid.</exception>
    /// <exception cref="EditorThemeLimitException">The store is full.</exception>
    public EditorTheme Save(EditorTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (string.IsNullOrEmpty(theme.Id))
        {
            theme = theme with { Id = NewId() };
        }

        var validated = EditorTheme.Parse(theme.ToJson());
        lock (_gate)
        {
            var next = _themes.ToList();
            var index = next.FindIndex(t => t.Id == validated.Id);
            if (index >= 0)
            {
                next[index] = validated;
            }
            else
            {
                ThrowIfFull();
                next.Add(validated);
            }

            Commit(next, _activeThemeId);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return validated;
    }

    /// <summary>Chooses the theme new windows use; null or empty means None. Returns false for an unknown id.</summary>
    public bool SetActive(string? id)
    {
        var active = string.IsNullOrEmpty(id) ? null : id;
        lock (_gate)
        {
            if (active is not null && Find(active) is null)
            {
                return false;
            }

            if (_activeThemeId == active)
            {
                return true;
            }

            Commit(_themes, active);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Removes a theme, clearing the active theme if it was the one. Returns false for an unknown id.</summary>
    public bool Delete(string? id)
    {
        lock (_gate)
        {
            if (Find(id) is null)
            {
                return false;
            }

            Commit(_themes.Where(t => t.Id != id).ToList(), _activeThemeId == id ? null : _activeThemeId);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Adds a theme from exported JSON without disturbing anything already saved: a taken id gets a
    /// new one, and a taken name becomes "Name (2)".
    /// </summary>
    /// <exception cref="EditorThemeFormatException">The JSON is not a valid theme.</exception>
    /// <exception cref="EditorThemeLimitException">The store is full.</exception>
    public EditorTheme Import(string json)
    {
        var theme = EditorTheme.Parse(json);
        lock (_gate)
        {
            ThrowIfFull();
            if (string.IsNullOrEmpty(theme.Id) || Find(theme.Id) is not null)
            {
                theme = theme with { Id = NewId() };
            }

            theme = theme with { Name = UniqueName(theme.Name) };
            Commit([.. _themes, theme], _activeThemeId);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return theme;
    }

    /// <summary>The theme as indented JSON, or null for an unknown id.</summary>
    public string? Export(string? id) => Get(id)?.ToJson().ToJsonString(_jsonOptions);

    private EditorTheme? Find(string? id) => id is null ? null : _themes.Find(t => t.Id == id);

    private void ThrowIfFull()
    {
        if (_themes.Count >= MaximumThemes)
        {
            throw new EditorThemeLimitException();
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("D");

    private string UniqueName(string name)
    {
        var taken = _themes.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name))
        {
            return name;
        }

        for (var number = 2; ; number++)
        {
            var suffix = string.Create(CultureInfo.InvariantCulture, $" ({number})");
            var room = EditorTheme.MaximumNameLength - suffix.Length;
            var start = new StringInfo(name);
            var trimmed = start.LengthInTextElements > room ? start.SubstringByTextElements(0, room).TrimEnd() : name;
            var candidate = trimmed + suffix;
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Writes first, so a failed write leaves the store as it was.</summary>
    private void Commit(List<EditorTheme> themes, string? activeThemeId)
    {
        WriteFile(themes, activeThemeId);
        _themes = themes;
        _activeThemeId = activeThemeId;
    }

    private void WriteFile(List<EditorTheme> themes, string? activeThemeId)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var items = new JsonArray();
        foreach (var theme in themes)
        {
            items.Add(theme.ToJson());
        }

        var file = new JsonObject
        {
            ["version"] = FileVersion,
            ["activeThemeId"] = activeThemeId,
            ["themes"] = items,
        };
        var temporary = _filePath + ".tmp";
        File.WriteAllText(temporary, file.ToJsonString(_jsonOptions));
        File.Move(temporary, _filePath, overwrite: true);
    }

    private void ReadFile()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(_filePath)) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"EditorThemeStore: could not read {_filePath}: {ex.Message}");
            BackUp();
            return;
        }

        if (root?["themes"] is not JsonArray items || !IsCurrentVersion(root))
        {
            BackUp();
            return;
        }

        var themes = new List<EditorTheme>(Math.Min(items.Count, MaximumThemes));
        var skipped = false;
        foreach (var item in items)
        {
            try
            {
                var theme = EditorTheme.Parse(item);
                if (string.IsNullOrEmpty(theme.Id) || themes.Count >= MaximumThemes || themes.Exists(t => t.Id == theme.Id))
                {
                    skipped = true;
                    continue;
                }

                themes.Add(theme);
            }
            catch (EditorThemeFormatException ex)
            {
                System.Diagnostics.Debug.WriteLine($"EditorThemeStore: skipped a theme. {ex.Message}");
                skipped = true;
            }
        }

        if (skipped)
        {
            BackUp();
        }

        _themes = themes;
        var active = root["activeThemeId"] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;
        _activeThemeId = themes.Exists(t => t.Id == active) ? active : null;
    }

    private static bool IsCurrentVersion(JsonObject root) =>
        root["version"] is JsonValue value
        && value.GetValueKind() == JsonValueKind.Number
        && value.TryGetValue<double>(out var version)
        && version == FileVersion;

    private void BackUp()
    {
        try
        {
            File.Copy(_filePath, BackupFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"EditorThemeStore: could not back up {_filePath}: {ex.Message}");
        }
    }
}
