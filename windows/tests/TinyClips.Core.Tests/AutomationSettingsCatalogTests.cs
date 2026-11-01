using System.Text.Json.Nodes;
using TinyClips.Core.Automation;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class AutomationSettingsCatalogTests
{
    [Fact]
    public void GetAll_ListsValuesAndWritableKeys()
    {
        var catalog = new AutomationSettingsCatalog(new CaptureSettings(new MemorySettings()), type => $"C:/out/{type}");

        var all = catalog.GetAll();

        Assert.Equal("C:/out/Screenshot", all["settings"]!["screenshotFolder"]!.GetValue<string>());
        Assert.Equal("jpg", all["settings"]!["screenshotFormat"]!.GetValue<string>());
        var writable = all["writable"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Contains("videoFrameRate", writable);
        Assert.DoesNotContain("screenshotFolder", writable);
    }

    [Fact]
    public void Set_ValidatesTypeAndRange()
    {
        var settings = new CaptureSettings(new MemorySettings());
        var catalog = new AutomationSettingsCatalog(settings);

        var result = catalog.Set("videoFrameRate", JsonNode.Parse("60"));
        Assert.Equal(60, result["value"]!.GetValue<int>());
        Assert.Equal(60, settings.VideoFrameRate);

        catalog.Set("copyScreenshotToClipboard", JsonValue.Create(false));
        Assert.False(settings.CopyScreenshotToClipboard);

        catalog.Set("screenshotFormat", JsonValue.Create("png"));
        Assert.Equal("png", settings.ScreenshotFormat);

        catalog.Set("gifFrameRate", JsonNode.Parse("12.5"));
        Assert.Equal(12.5, settings.GifFrameRate);

        AssertInvalid(() => catalog.Set("videoFrameRate", JsonNode.Parse("61")));
        AssertInvalid(() => catalog.Set("videoFrameRate", JsonNode.Parse("30.5")));
        AssertInvalid(() => catalog.Set("videoFrameRate", JsonValue.Create("30")));
        AssertInvalid(() => catalog.Set("copyScreenshotToClipboard", JsonNode.Parse("1")));
        AssertInvalid(() => catalog.Set("screenshotFormat", JsonValue.Create("bmp")));
        AssertInvalid(() => catalog.Set("uploadcarePublicKey", JsonValue.Create("x")));
        AssertInvalid(() => catalog.Set("videoFrameRate", null));
    }

    [Fact]
    public void ReadOnlySettings_CannotBeChanged()
    {
        var catalog = new AutomationSettingsCatalog(new CaptureSettings(new MemorySettings()), _ => "C:/out");

        AssertInvalid(() => catalog.Set("gifFolder", JsonValue.Create("C:/elsewhere")));
        Assert.False(catalog.Get("gifFolder")["writable"]!.GetValue<bool>());
        AssertInvalid(() => catalog.Get("automationEnabled"));
    }

    private static void AssertInvalid(Action action)
    {
        var ex = Assert.Throws<AutomationException>(action);
        Assert.Equal(AutomationErrorCodes.InvalidParams, ex.Code);
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

        public AppTheme Theme { get; set; }

        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;

        public void Set<T>(string key, T value) => _values[key] = value;
    }
}
