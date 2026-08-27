using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using TinyClips.Core.Models;

namespace TinyClips.Core.Services;

public sealed partial class ClipAnalyticsService : IClipAnalyticsService
{
    private const string StorageKey = "captureAnalyticsHistoryV1";
    private const string LifetimeStorageKey = "captureAnalyticsLifetimeV1";
    private const string HourlyStorageKey = "captureAnalyticsHourlyV1";
    private const int RetainedDays = 30;

    private readonly ISettingsService _settings;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private Dictionary<string, DailyCountsState> _history;
    private LifetimeCountsState _lifetime;
    private Dictionary<int, int> _hourly;

    public ClipAnalyticsService(ISettingsService settings, TimeProvider? timeProvider = null)
    {
        _settings = settings;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _history = Load();
        _lifetime = LoadLifetime();
        _hourly = LoadHourly();
        PruneAndPersistIfNeeded(_timeProvider.GetLocalNow().Date);
    }

    public void RecordCapture(CaptureType type)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetLocalNow();
            var today = now.Date;
            PruneInternal(today);

            var key = ToDayKey(today);
            if (!_history.TryGetValue(key, out var counts))
            {
                counts = new DailyCountsState();
            }

            switch (type)
            {
                case CaptureType.Screenshot:
                    counts.ScreenshotCount++;
                    _lifetime.ScreenshotCount++;
                    break;
                case CaptureType.Video:
                    counts.VideoCount++;
                    _lifetime.VideoCount++;
                    break;
                case CaptureType.Gif:
                    counts.GifCount++;
                    _lifetime.GifCount++;
                    break;
            }

            _history[key] = counts;

            var hour = now.Hour;
            _hourly[hour] = _hourly.GetValueOrDefault(hour) + 1;

            PersistInternal();
        }
    }

    public IReadOnlyList<DailyCaptureAnalytics> GetDailyCounts(int days)
    {
        var clampedDays = Math.Clamp(days, 1, RetainedDays);

        lock (_gate)
        {
            var today = _timeProvider.GetLocalNow().Date;
            PruneInternal(today);

            var start = today.AddDays(-(clampedDays - 1));
            var results = new List<DailyCaptureAnalytics>(clampedDays);
            for (var date = start; date <= today; date = date.AddDays(1))
            {
                var key = ToDayKey(date);
                _history.TryGetValue(key, out var counts);
                counts ??= new DailyCountsState();

                results.Add(new DailyCaptureAnalytics(
                    date,
                    counts.ScreenshotCount,
                    counts.VideoCount,
                    counts.GifCount));
            }

            return results;
        }
    }

    public LifetimeCaptureAnalytics GetLifetimeTotals()
    {
        lock (_gate)
        {
            return new LifetimeCaptureAnalytics(_lifetime.ScreenshotCount, _lifetime.VideoCount, _lifetime.GifCount);
        }
    }

    public IReadOnlyList<WeekdayCaptureTotal> GetWeekdayTotals(int days)
    {
        var dailyCounts = GetDailyCounts(days);
        var totalsByWeekday = new Dictionary<DayOfWeek, int>();
        foreach (var day in dailyCounts)
        {
            var weekday = day.Date.DayOfWeek;
            totalsByWeekday[weekday] = totalsByWeekday.GetValueOrDefault(weekday) + day.TotalCount;
        }

        return Enum.GetValues<DayOfWeek>()
            .Select(weekday => new WeekdayCaptureTotal(weekday, totalsByWeekday.GetValueOrDefault(weekday)))
            .ToList();
    }

    public WeekdayCaptureTotal? GetBusiestWeekday(int days)
    {
        var totals = GetWeekdayTotals(days);
        var busiest = totals.Aggregate((best, next) => next.Count > best.Count ? next : best);
        return busiest.Count > 0 ? busiest : null;
    }

    public IReadOnlyList<HourCaptureTotal> GetHourlyTotals()
    {
        lock (_gate)
        {
            return Enumerable.Range(0, 24)
                .Select(hour => new HourCaptureTotal(hour, _hourly.GetValueOrDefault(hour)))
                .ToList();
        }
    }

    public HourCaptureTotal? GetMostActiveHour()
    {
        var totals = GetHourlyTotals();
        var busiest = totals.Aggregate((best, next) => next.Count > best.Count ? next : best);
        return busiest.Count > 0 ? busiest : null;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _history.Clear();
            _lifetime = new LifetimeCountsState();
            _hourly.Clear();
            _settings.Set(StorageKey, string.Empty);
            _settings.Set(LifetimeStorageKey, string.Empty);
            _settings.Set(HourlyStorageKey, string.Empty);
        }
    }

    private Dictionary<string, DailyCountsState> Load()
    {
        var raw = _settings.Get(StorageKey, string.Empty);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Dictionary<string, DailyCountsState>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize(raw, AnalyticsJsonContext.Default.DictionaryStringDailyCountsState)
                ?? new Dictionary<string, DailyCountsState>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new Dictionary<string, DailyCountsState>(StringComparer.Ordinal);
        }
    }

    private LifetimeCountsState LoadLifetime()
    {
        var raw = _settings.Get(LifetimeStorageKey, string.Empty);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new LifetimeCountsState();
        }

        try
        {
            return JsonSerializer.Deserialize(raw, AnalyticsJsonContext.Default.LifetimeCountsState)
                ?? new LifetimeCountsState();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new LifetimeCountsState();
        }
    }

    private Dictionary<int, int> LoadHourly()
    {
        var raw = _settings.Get(HourlyStorageKey, string.Empty);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Dictionary<int, int>();
        }

        try
        {
            return JsonSerializer.Deserialize(raw, AnalyticsJsonContext.Default.DictionaryInt32Int32)
                ?? new Dictionary<int, int>();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new Dictionary<int, int>();
        }
    }

    private void PruneAndPersistIfNeeded(DateTime today)
    {
        lock (_gate)
        {
            if (PruneInternal(today))
            {
                PersistInternal();
            }
        }
    }

    private bool PruneInternal(DateTime today)
    {
        var earliestDay = today.AddDays(-(RetainedDays - 1));
        var removed = false;

        foreach (var key in _history.Keys.ToArray())
        {
            if (!DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                date < earliestDay)
            {
                _history.Remove(key);
                removed = true;
            }
        }

        return removed;
    }

    private void PersistInternal()
    {
        var raw = JsonSerializer.Serialize(_history, AnalyticsJsonContext.Default.DictionaryStringDailyCountsState);
        _settings.Set(StorageKey, raw);

        var lifetimeRaw = JsonSerializer.Serialize(_lifetime, AnalyticsJsonContext.Default.LifetimeCountsState);
        _settings.Set(LifetimeStorageKey, lifetimeRaw);

        var hourlyRaw = JsonSerializer.Serialize(_hourly, AnalyticsJsonContext.Default.DictionaryInt32Int32);
        _settings.Set(HourlyStorageKey, hourlyRaw);
    }

    private static string ToDayKey(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class DailyCountsState
    {
        public int ScreenshotCount { get; set; }
        public int VideoCount { get; set; }
        public int GifCount { get; set; }
    }

    private sealed class LifetimeCountsState
    {
        public int ScreenshotCount { get; set; }
        public int VideoCount { get; set; }
        public int GifCount { get; set; }
    }

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Dictionary<string, DailyCountsState>))]
    [JsonSerializable(typeof(LifetimeCountsState))]
    [JsonSerializable(typeof(Dictionary<int, int>))]
    private sealed partial class AnalyticsJsonContext : JsonSerializerContext;
}
