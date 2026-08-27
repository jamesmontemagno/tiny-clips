using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TinyClips.Core.Models.ClipsLibrary;

namespace TinyClips.Core.Services.ClipsLibrary;

/// <summary>
/// JSON-file backed <see cref="IClipMetadataStore"/>. Loads lazily on first access, coalesces
/// writes with a short debounce, and replaces the file atomically so a crash mid-write never
/// leaves a truncated index behind.
/// </summary>
public sealed partial class JsonClipMetadataStore : IClipMetadataStore, IDisposable
{
    private readonly string _filePath;
    private readonly TimeSpan _debounce;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly ITimer _saveTimer;
    private Dictionary<string, ClipMetadata>? _records;
    private bool _dirty;

    public event EventHandler? Changed;

    public JsonClipMetadataStore(string filePath, TimeProvider? timeProvider = null, TimeSpan? debounce = null)
    {
        _filePath = filePath;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(500);
        _saveTimer = _timeProvider.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public static string DefaultFilePath(string localAppDataDirectory) =>
        Path.Combine(localAppDataDirectory, "TinyClips", "clip-metadata.json");

    public ClipMetadata Get(string path)
    {
        lock (_gate)
        {
            return Records.TryGetValue(Normalize(path), out var metadata) ? metadata : ClipMetadata.Empty(path);
        }
    }

    public IReadOnlyCollection<ClipMetadata> GetAll()
    {
        lock (_gate)
        {
            return Records.Values.ToList();
        }
    }

    public void Upsert(ClipMetadata metadata)
    {
        lock (_gate)
        {
            var key = Normalize(metadata.Path);
            if (metadata.IsEmpty)
            {
                if (!Records.Remove(key))
                {
                    return;
                }
            }
            else
            {
                Records[key] = metadata.WithTags(metadata.Tags);
            }

            MarkDirty();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string path)
    {
        bool removed;
        lock (_gate)
        {
            removed = Records.Remove(Normalize(path));
            if (removed)
            {
                MarkDirty();
            }
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RenamePath(string oldPath, string newPath)
    {
        bool moved;
        lock (_gate)
        {
            var oldKey = Normalize(oldPath);
            moved = Records.Remove(oldKey, out var metadata);
            if (moved)
            {
                Records[Normalize(newPath)] = metadata! with { Path = newPath };
                MarkDirty();
            }
        }

        if (moved)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public int Prune(IEnumerable<string> existingPaths)
    {
        int removed;
        lock (_gate)
        {
            var keep = new HashSet<string>(existingPaths.Select(Normalize), StringComparer.OrdinalIgnoreCase);
            var stale = Records.Keys.Where(key => !keep.Contains(key)).ToList();
            foreach (var key in stale)
            {
                Records.Remove(key);
            }

            removed = stale.Count;
            if (removed > 0)
            {
                MarkDirty();
            }
        }

        if (removed > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    public void Flush()
    {
        List<ClipMetadata> snapshot;
        lock (_gate)
        {
            if (!_dirty || _records is null)
            {
                return;
            }

            _dirty = false;
            snapshot = _records.Values.OrderBy(record => record.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _filePath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(new MetadataFile(1, snapshot), ClipMetadataJsonContext.Default.MetadataFile));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"JsonClipMetadataStore: save failed: {ex}");
            lock (_gate)
            {
                _dirty = true;
            }
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Flush();
    }

    private Dictionary<string, ClipMetadata> Records => _records ??= Load();

    private void MarkDirty()
    {
        _dirty = true;
        _saveTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
    }

    private Dictionary<string, ClipMetadata> Load()
    {
        var records = new Dictionary<string, ClipMetadata>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_filePath))
            {
                return records;
            }

            var file = JsonSerializer.Deserialize(
                File.ReadAllText(_filePath),
                ClipMetadataJsonContext.Default.MetadataFile);
            foreach (var record in file?.Clips ?? [])
            {
                if (!string.IsNullOrWhiteSpace(record.Path) && !record.IsEmpty)
                {
                    records[Normalize(record.Path)] = record;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"JsonClipMetadataStore: load failed: {ex}");
        }

        return records;
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));

    private sealed record MetadataFile(int Version, List<ClipMetadata> Clips);

    [JsonSourceGenerationOptions(
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(MetadataFile))]
    private sealed partial class ClipMetadataJsonContext : JsonSerializerContext;
}
