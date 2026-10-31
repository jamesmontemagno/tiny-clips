using System.Text.Json;
using System.Text.Json.Serialization;
using TinyClips.Core.Models;

namespace TinyClips.Core.Services;

public sealed record RecentCapture(string Path, CaptureType Type, DateTimeOffset CapturedAt);

public interface IRecentCaptureService
{
    event EventHandler? Changed;
    IReadOnlyList<RecentCapture> GetRecentCaptures();
    void Record(string path, CaptureType type);
    void Remove(string path);
}

public sealed partial class RecentCaptureService : IRecentCaptureService
{
    private const string SettingsKey = "recentCapturesV1";
    private const int MaximumCount = 10;

    private readonly ISettingsService _settings;
    private readonly IFileSystem _fileSystem;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private List<RecentCapture> _captures;
    private long _revision;
    private bool _isPersisting;

    public event EventHandler? Changed;

    public RecentCaptureService(ISettingsService settings, IFileSystem fileSystem, TimeProvider timeProvider)
    {
        _settings = settings;
        _fileSystem = fileSystem;
        _timeProvider = timeProvider;
        _captures = Load();
    }

    public IReadOnlyList<RecentCapture> GetRecentCaptures()
    {
        RecentCapture[] snapshot;
        long revision;
        lock (_gate)
        {
            snapshot = _captures.ToArray();
            revision = _revision;
        }

        // A disconnected/moved capture folder must never block Record/Remove behind a disk
        // probe. A concurrent mutation wins over this older validation.
        var existing = snapshot.Where(capture => _fileSystem.FileExists(capture.Path)).Take(MaximumCount).ToList();
        lock (_gate)
        {
            if (revision != _revision)
            {
                return _captures.ToArray();
            }

            if (existing.Count == snapshot.Length)
            {
                return snapshot;
            }

            _captures = existing;
            _revision++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        Persist();
        return existing.ToArray();
    }

    public void Record(string path, CaptureType type)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (_gate)
        {
            _captures.RemoveAll(capture => string.Equals(capture.Path, path, StringComparison.OrdinalIgnoreCase));
            _captures.Insert(0, new RecentCapture(path, type, _timeProvider.GetLocalNow()));
            _captures = _captures.Take(MaximumCount).ToList();
            _revision++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        Persist();
    }

    public void Remove(string path)
    {
        lock (_gate)
        {
            if (_captures.RemoveAll(capture => string.Equals(capture.Path, path, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                _revision++;
            }
            else
            {
                return;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        Persist();
    }

    private List<RecentCapture> Load()
    {
        var json = _settings.Get(SettingsKey, string.Empty);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(json, RecentCaptureJsonContext.Default.ListRecentCapture) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Persist()
    {
        lock (_gate)
        {
            if (_isPersisting)
            {
                return;
            }

            _isPersisting = true;
        }

        // One caller writes, without holding the state lock during serialization/settings IO.
        // Mutations during that write are coalesced and flushed by the same caller in order.
        try
        {
            while (true)
            {
                List<RecentCapture> snapshot;
                long revision;
                lock (_gate)
                {
                    snapshot = [.. _captures];
                    revision = _revision;
                }

                _settings.Set(SettingsKey, JsonSerializer.Serialize(snapshot, RecentCaptureJsonContext.Default.ListRecentCapture));
                lock (_gate)
                {
                    if (revision == _revision)
                    {
                        _isPersisting = false;
                        return;
                    }
                }
            }
        }
        catch
        {
            lock (_gate)
            {
                _isPersisting = false;
            }

            throw;
        }
    }

    [JsonSerializable(typeof(List<RecentCapture>))]
    private sealed partial class RecentCaptureJsonContext : JsonSerializerContext;
}
