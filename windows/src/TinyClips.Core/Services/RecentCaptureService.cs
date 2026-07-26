using System.Text.Json;
using TinyClips.Core.Models;

namespace TinyClips.Core.Services;

public sealed record RecentCapture(string Path, CaptureType Type, DateTimeOffset CapturedAt);

public interface IRecentCaptureService
{
    IReadOnlyList<RecentCapture> GetRecentCaptures();
    void Record(string path, CaptureType type);
    void Remove(string path);
}

public sealed class RecentCaptureService : IRecentCaptureService
{
    private const string SettingsKey = "recentCapturesV1";
    private const int MaximumCount = 10;

    private readonly ISettingsService _settings;
    private readonly IFileSystem _fileSystem;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private List<RecentCapture> _captures;

    public RecentCaptureService(ISettingsService settings, IFileSystem fileSystem, TimeProvider timeProvider)
    {
        _settings = settings;
        _fileSystem = fileSystem;
        _timeProvider = timeProvider;
        _captures = Load();
    }

    public IReadOnlyList<RecentCapture> GetRecentCaptures()
    {
        lock (_gate)
        {
            var existing = _captures.Where(capture => _fileSystem.FileExists(capture.Path)).Take(MaximumCount).ToList();
            if (existing.Count != _captures.Count)
            {
                _captures = existing;
                Persist();
            }

            return _captures.ToArray();
        }
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
            Persist();
        }
    }

    public void Remove(string path)
    {
        lock (_gate)
        {
            if (_captures.RemoveAll(capture => string.Equals(capture.Path, path, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                Persist();
            }
        }
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
            return JsonSerializer.Deserialize<List<RecentCapture>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Persist() => _settings.Set(SettingsKey, JsonSerializer.Serialize(_captures));
}
