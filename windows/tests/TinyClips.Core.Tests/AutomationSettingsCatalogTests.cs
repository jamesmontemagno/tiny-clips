using System.Text.Json.Nodes;
using TinyClips.Core.Automation;
using TinyClips.Core.Editing;
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
        Assert.Equal(10, all["settings"]!["recentCapturesLimit"]!.GetValue<int>());
        var writable = all["writable"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Contains("recentCapturesLimit", writable);
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

        var recentLimit = catalog.Set("recentCapturesLimit", JsonValue.Create(30));
        Assert.Equal(30, recentLimit["value"]!.GetValue<int>());
        Assert.Equal(30, settings.RecentCapturesLimit);

        catalog.Set("screenshotFormat", JsonValue.Create("png"));
        Assert.Equal("png", settings.ScreenshotFormat);

        catalog.Set("gifFrameRate", JsonNode.Parse("12.5"));
        Assert.Equal(12.5, settings.GifFrameRate);

        AssertInvalid(() => catalog.Set("videoFrameRate", JsonNode.Parse("61")));
        AssertInvalid(() => catalog.Set("videoFrameRate", JsonNode.Parse("30.5")));
        AssertInvalid(() => catalog.Set("videoFrameRate", JsonValue.Create("30")));
        AssertInvalid(() => catalog.Set("copyScreenshotToClipboard", JsonNode.Parse("1")));
        AssertInvalid(() => catalog.Set("screenshotFormat", JsonValue.Create("bmp")));
        AssertInvalid(() => catalog.Set("recentCapturesLimit", JsonValue.Create(4)));
        AssertInvalid(() => catalog.Set("recentCapturesLimit", JsonValue.Create(31)));
        AssertInvalid(() => catalog.Set("recentCapturesLimit", JsonValue.Create(10.5)));
        AssertInvalid(() => catalog.Set("uploadcarePublicKey", JsonValue.Create("x")));
        AssertInvalid(() => catalog.Set("videoFrameRate", null));
    }

    [Fact]
    public void FirstSaveRevealIsOptInAndResetRestoresItInTheLiveCatalog()
    {
        var store = new MemorySettings();
        var settings = new CaptureSettings(store);
        var catalog = new AutomationSettingsCatalog(settings);
        const string key = "studioRevealAfterFirstSave";
        Assert.False(catalog.Get(key)["value"]!.GetValue<bool>());
        catalog.Set(key, JsonValue.Create(true));
        Assert.True(new CaptureSettings(store).StudioRevealAfterFirstSave);
        settings.ResetToDefaults();
        Assert.False(catalog.Get(key)["value"]!.GetValue<bool>());
    }

    [Fact]
    public void ScreenshotEditorDefaultTool_UsesTheToolNamesSharedWithMac()
    {
        var settings = new CaptureSettings(new MemorySettings());
        var catalog = new AutomationSettingsCatalog(settings);

        Assert.Equal("background", catalog.Get("screenshotEditorDefaultTool")["value"]!.GetValue<string>());
        Assert.Equal(
            ["move", "background", "crop", "rectangle", "ellipse", "arrow", "line", "pen", "text", "number", "emoji", "redact"],
            EditToolExtensions.SettingIds);

        catalog.Set("screenshotEditorDefaultTool", JsonValue.Create("number"));
        Assert.Equal(EditTool.Counter, settings.ScreenshotEditorDefaultTool);
        catalog.Set("screenshotEditorDefaultTool", JsonValue.Create("move"));
        Assert.Equal(EditTool.Select, settings.ScreenshotEditorDefaultTool);

        AssertInvalid(() => catalog.Set("screenshotEditorDefaultTool", JsonValue.Create("Select")));
        AssertInvalid(() => catalog.Set("screenshotEditorDefaultTool", JsonValue.Create("counter")));
        Assert.Equal(EditTool.Select, settings.ScreenshotEditorDefaultTool);
    }

    [Fact]
    public void ReadOnlySettings_CannotBeChanged()
    {
        var catalog = new AutomationSettingsCatalog(new CaptureSettings(new MemorySettings()), _ => "C:/out");

        AssertInvalid(() => catalog.Set("gifFolder", JsonValue.Create("C:/elsewhere")));
        Assert.False(catalog.Get("gifFolder")["writable"]!.GetValue<bool>());
        AssertInvalid(() => catalog.Get("automationEnabled"));
    }

    [Fact]
    public void StudioPreviewSwitch_IsNotExposedToAgents()
    {
        var catalog = new AutomationSettingsCatalog(new CaptureSettings(new MemorySettings()));

        Assert.DoesNotContain("studioPreviewEnabled", catalog.Keys);
        AssertInvalid(() => catalog.Get("studioPreviewEnabled"));
        AssertInvalid(() => catalog.Set("studioPreviewEnabled", JsonValue.Create(false)));
    }

    [Fact]
    public void RecentLimitChangesNotifyOnlyWhenTheEffectiveValueChanges()
    {
        var settings = new CaptureSettings(new MemorySettings());
        var changes = 0;
        settings.RecentCapturesLimitChanged += (_, _) => changes++;

        settings.RecentCapturesLimit = 12;
        settings.RecentCapturesLimit = 12;
        settings.RecentCapturesLimit = 2;

        Assert.Equal(5, settings.RecentCapturesLimit);
        Assert.Equal(2, changes);
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
