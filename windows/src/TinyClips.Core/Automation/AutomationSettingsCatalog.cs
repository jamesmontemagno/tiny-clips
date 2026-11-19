using System.Text.Json;
using System.Text.Json.Nodes;
using TinyClips.Core.Editing;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Automation;

/// <summary>
/// The settings agents may read and change through <c>settings.get</c> / <c>settings.set</c>.
/// Anything not listed here (save folders, agent control itself, credentials, hotkeys) is not reachable.
/// Keys match the macOS app where both apps have the setting.
/// </summary>
public sealed class AutomationSettingsCatalog
{
    private readonly IReadOnlyList<Entry> _entries;

    public AutomationSettingsCatalog(ICaptureSettings settings, Func<CaptureType, string>? outputDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var s = settings;
        var entries = new List<Entry>();
        if (outputDirectory is not null)
        {
            entries.Add(ReadOnly("screenshotFolder", () => outputDirectory(CaptureType.Screenshot)));
            entries.Add(ReadOnly("videoFolder", () => outputDirectory(CaptureType.Video)));
            entries.Add(ReadOnly("gifFolder", () => outputDirectory(CaptureType.Gif)));
        }

        entries.AddRange(
        [
            Text("fileNameTemplate", () => s.FileNameTemplate, v => s.FileNameTemplate = v),
            Text("studioExportResolution", () => StudioExportPresets.Id(s.StudioExportResolution),
                v => s.StudioExportResolution = StudioExportPresets.TryParseResolution(v, out var resolution) ? resolution : s.StudioExportResolution,
                allowed: [.. StudioExportPresets.ResolutionIds]),
            Text("studioExportQuality", () => StudioExportPresets.Id(s.StudioExportQuality),
                v => s.StudioExportQuality = StudioExportPresets.TryParseQuality(v, out var quality) ? quality : s.StudioExportQuality,
                allowed: [.. StudioExportPresets.QualityIds]),
            Text("screenshotFormat", () => s.ScreenshotFormat, v => s.ScreenshotFormat = v, allowed: ["jpg", "png", "webp"]),
            Int("screenshotScale", () => s.ScreenshotScale, v => s.ScreenshotScale = v, 25, 100),
            Double("jpegQuality", () => s.JpegQuality, v => s.JpegQuality = v, 0.1, 1),
            Bool("copyScreenshotToClipboard", () => s.CopyScreenshotToClipboard, v => s.CopyScreenshotToClipboard = v),
            Bool("copyVideoToClipboard", () => s.CopyVideoToClipboard, v => s.CopyVideoToClipboard = v),
            Bool("copyGifToClipboard", () => s.CopyGifToClipboard, v => s.CopyGifToClipboard = v),
            Bool("showInExplorer", () => s.ShowInExplorer, v => s.ShowInExplorer = v),
            Bool("showSaveNotifications", () => s.ShowSaveNotifications, v => s.ShowSaveNotifications = v),
            Bool("showScreenshotEditor", () => s.ShowScreenshotEditor, v => s.ShowScreenshotEditor = v),
            Text("screenshotEditorDefaultTool", () => s.ScreenshotEditorDefaultTool.SettingId(),
                v => s.ScreenshotEditorDefaultTool = EditToolExtensions.TryParseSettingId(v, out var tool) ? tool : s.ScreenshotEditorDefaultTool,
                allowed: [.. EditToolExtensions.SettingIds]),
            Bool("showTrimmer", () => s.ShowTrimmer, v => s.ShowTrimmer = v),
            Bool("showGifTrimmer", () => s.ShowGifTrimmer, v => s.ShowGifTrimmer = v),
            Bool("showScreenshotCapturePicker", () => s.ShowScreenshotCapturePicker, v => s.ShowScreenshotCapturePicker = v),
            Bool("showVideoCapturePicker", () => s.ShowVideoCapturePicker, v => s.ShowVideoCapturePicker = v),
            Bool("showGifCapturePicker", () => s.ShowGifCapturePicker, v => s.ShowGifCapturePicker = v),
            Bool("screenshotCountdownEnabled", () => s.ScreenshotCountdownEnabled, v => s.ScreenshotCountdownEnabled = v),
            Int("screenshotCountdownDuration", () => s.ScreenshotCountdownDuration, v => s.ScreenshotCountdownDuration = v, 1, 10),
            Bool("videoCountdownEnabled", () => s.VideoCountdownEnabled, v => s.VideoCountdownEnabled = v),
            Int("videoCountdownDuration", () => s.VideoCountdownDuration, v => s.VideoCountdownDuration = v, 1, 10),
            Bool("gifCountdownEnabled", () => s.GifCountdownEnabled, v => s.GifCountdownEnabled = v),
            Int("gifCountdownDuration", () => s.GifCountdownDuration, v => s.GifCountdownDuration = v, 1, 10),
            Int("videoFrameRate", () => s.VideoFrameRate, v => s.VideoFrameRate = v, 10, 60),
            Int("videoRecordingTimeLimitMinutes", () => s.VideoRecordingTimeLimitMinutes, v => s.VideoRecordingTimeLimitMinutes = v, 0, 180),
            Double("gifFrameRate", () => s.GifFrameRate, v => s.GifFrameRate = v, 5, 50),
            Int("gifMaxWidth", () => s.GifMaxWidth, v => s.GifMaxWidth = v, 240, 1920),
            Bool("recordAudio", () => s.RecordAudio, v => s.RecordAudio = v),
            Bool("recordMicrophone", () => s.RecordMicrophone, v => s.RecordMicrophone = v),
            Bool("webcamEnabled", () => s.WebcamEnabled, v => s.WebcamEnabled = v),
            Bool("showMouseClickVisualsInVideo", () => s.ShowMouseClickVisualsInVideo, v => s.ShowMouseClickVisualsInVideo = v),
            Bool("showMouseClickVisualsInGif", () => s.ShowMouseClickVisualsInGif, v => s.ShowMouseClickVisualsInGif = v),
            Bool("showRegionIndicator", () => s.ShowRegionIndicator, v => s.ShowRegionIndicator = v),
            Bool("includeTinyClipsInCapture", () => s.IncludeTinyClipsInCapture, v => s.IncludeTinyClipsInCapture = v),
            Bool("studioPreviewEnabled", () => s.StudioPreviewEnabled, v => s.StudioPreviewEnabled = v),
            Bool("teleprompterEnabled", () => s.TeleprompterEnabled, v => s.TeleprompterEnabled = v),
        ]);
        _entries = entries;
    }

    public IReadOnlyList<string> Keys => _entries.Select(e => e.Key).ToArray();

    public IReadOnlyList<string> WritableKeys => _entries.Where(e => e.Write is not null).Select(e => e.Key).ToArray();

    /// <summary><c>{settings: {key: value}, writable: [keys]}</c>.</summary>
    public JsonObject GetAll()
    {
        var values = new JsonObject();
        foreach (var entry in _entries)
        {
            values[entry.Key] = entry.Read();
        }

        return new JsonObject
        {
            ["settings"] = values,
            ["writable"] = new JsonArray(WritableKeys.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
        };
    }

    /// <summary><c>{key, value, writable}</c>; <c>invalid_params</c> for an unknown key.</summary>
    public JsonObject Get(string key)
    {
        var entry = Find(key, "Unknown setting {0}. Call settings.get without a key to list them.");
        return new JsonObject
        {
            ["key"] = entry.Key,
            ["value"] = entry.Read(),
            ["writable"] = entry.Write is not null,
        };
    }

    /// <summary>Validates and stores the value, returning <c>{key, value}</c>.</summary>
    public JsonObject Set(string key, JsonNode? value)
    {
        var entry = Find(key, "Unknown setting {0}.");
        if (entry.Write is null)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams, $"{entry.Key} is read-only for agents.");
        }

        if (value is null)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams, "value is required.");
        }

        entry.Write(value);
        return new JsonObject
        {
            ["key"] = entry.Key,
            ["value"] = entry.Read(),
        };
    }

    private Entry Find(string key, string unknownFormat) =>
        _entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.Ordinal))
        ?? throw new AutomationException(AutomationErrorCodes.InvalidParams, string.Format(System.Globalization.CultureInfo.InvariantCulture, unknownFormat, key));

    private sealed record Entry(string Key, Func<JsonNode?> Read, Action<JsonNode>? Write);

    private static Entry ReadOnly(string key, Func<string> read) =>
        new(key, () => JsonValue.Create(read()), null);

    private static Entry Bool(string key, Func<bool> read, Action<bool> write) =>
        new(key, () => JsonValue.Create(read()), node =>
        {
            var kind = node.GetValueKind();
            if (kind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new AutomationException(AutomationErrorCodes.InvalidParams, $"{key} must be true or false.");
            }

            write(kind == JsonValueKind.True);
        });

    private static Entry Int(string key, Func<int> read, Action<int> write, int min, int max) =>
        new(key, () => JsonValue.Create(read()), node =>
        {
            if (!TryGetNumber(node, out var number) || Math.Round(number) != number || number < min || number > max)
            {
                throw new AutomationException(AutomationErrorCodes.InvalidParams, $"{key} must be a whole number from {min} to {max}.");
            }

            write((int)number);
        });

    private static Entry Double(string key, Func<double> read, Action<double> write, double min, double max) =>
        new(key, () => JsonValue.Create(read()), node =>
        {
            if (!TryGetNumber(node, out var number) || double.IsNaN(number) || number < min || number > max)
            {
                throw new AutomationException(
                    AutomationErrorCodes.InvalidParams,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} must be a number from {1} to {2}.", key, min, max));
            }

            write(number);
        });

    private static Entry Text(string key, Func<string> read, Action<string> write, string[]? allowed = null, int maximumLength = 200) =>
        new(key, () => JsonValue.Create(read()), node =>
        {
            if (node.GetValueKind() != JsonValueKind.String || node.GetValue<string>() is not { } text || text.Length > maximumLength)
            {
                throw new AutomationException(AutomationErrorCodes.InvalidParams, $"{key} must be text of up to {maximumLength} characters.");
            }

            if (allowed is not null && !allowed.Contains(text, StringComparer.Ordinal))
            {
                throw new AutomationException(AutomationErrorCodes.InvalidParams, $"{key} must be one of: {string.Join(", ", allowed)}.");
            }

            if (allowed is null && string.IsNullOrWhiteSpace(text))
            {
                throw new AutomationException(AutomationErrorCodes.InvalidParams, $"{key} cannot be empty.");
            }

            write(text);
        });

    private static bool TryGetNumber(JsonNode node, out double number)
    {
        number = 0;
        if (node.GetValueKind() != JsonValueKind.Number || node is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue<double>(out number))
        {
            return true;
        }

        if (value.TryGetValue<JsonElement>(out var element) && element.TryGetDouble(out number))
        {
            return true;
        }

        if (value.TryGetValue<int>(out var i))
        {
            number = i;
            return true;
        }

        if (value.TryGetValue<long>(out var l))
        {
            number = l;
            return true;
        }

        return false;
    }
}
