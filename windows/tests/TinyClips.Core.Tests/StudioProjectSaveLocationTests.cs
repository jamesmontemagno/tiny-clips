using TinyClips.Core.Automation;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;
using Xunit;

namespace TinyClips.Core.Tests;

public sealed class StudioProjectSaveLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TinyClipsSaveLocation-" + Guid.NewGuid().ToString("N"));

    public StudioProjectSaveLocationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative\\folder")]
    public void Resolve_WithoutAChosenFolder_IsStudioInsideTheVideosFolder(string? configured)
    {
        var videos = Path.Combine(_root, "Videos");

        Assert.False(StudioProjectSaveLocation.IsCustom(configured));
        Assert.Equal(Path.Combine(videos, "Studio"), StudioProjectSaveLocation.Resolve(configured, videos));
        Assert.False(Directory.Exists(videos));
    }

    [Fact]
    public void Resolve_WithAChosenFolder_IsThatFolder()
    {
        var chosen = Path.Combine(_root, "Projects");

        Assert.True(StudioProjectSaveLocation.IsCustom(chosen));
        Assert.Equal(chosen, StudioProjectSaveLocation.Resolve(chosen, Path.Combine(_root, "Videos")));
    }

    [Fact]
    public void Prepare_CreatesTheDefaultFolder()
    {
        var videos = Path.Combine(_root, "Videos");

        var folder = StudioProjectSaveLocation.Prepare(string.Empty, videos);

        Assert.Equal(Path.Combine(videos, "Studio"), folder);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void Prepare_UsesAChosenFolderThatExists_AndTheDefaultWhenItIsGone()
    {
        var chosen = Directory.CreateDirectory(Path.Combine(_root, "Projects")).FullName;
        var videos = Path.Combine(_root, "Videos");

        Assert.Equal(chosen, StudioProjectSaveLocation.Prepare(chosen, videos));
        Assert.False(Directory.Exists(Path.Combine(videos, "Studio")));

        Directory.Delete(chosen);
        Assert.Equal(Path.Combine(videos, "Studio"), StudioProjectSaveLocation.Prepare(chosen, videos));
        Assert.False(Directory.Exists(chosen));
    }

    [Fact]
    public void ChosenFolderWithTrailingUnicodeWhitespace_IsPreservedForSavingAndAgents()
    {
        var videos = Path.Combine(_root, "Videos");
        Directory.CreateDirectory(Path.Combine(_root, "Projects"));
        var chosen = Directory.CreateDirectory(Path.Combine(_root, "Projects\u00A0")).FullName;
        var settings = new CaptureSettings(new MemorySettings()) { StudioProjectFolder = chosen, UseDefaultSaveDirectories = false };
        var catalog = new AutomationSettingsCatalog(settings, _ => videos);

        Assert.Equal(chosen, settings.StudioProjectFolder);
        Assert.Equal(chosen, StudioProjectSaveLocation.Resolve(chosen, videos));
        Assert.Equal(chosen, StudioProjectSaveLocation.Prepare(chosen, videos));
        Assert.Equal(chosen, (string?)catalog.Get("studioProjectFolder")["value"]);
    }

    [Fact]
    public void Prepare_FallsBackToTheVideosFolder_WhenStudioCannotBeCreated()
    {
        var videos = Directory.CreateDirectory(Path.Combine(_root, "Videos")).FullName;
        File.WriteAllText(Path.Combine(videos, "Studio"), "a file where the folder would go");

        Assert.Equal(videos, StudioProjectSaveLocation.Prepare(null, videos));
    }

    [Fact]
    public void AgentSetting_IsTheResolvedFolder_AndReadOnly()
    {
        var settings = new CaptureSettings(new MemorySettings());
        var videos = Path.Combine(_root, "Videos");
        var catalog = new AutomationSettingsCatalog(settings, _ => videos);

        var setting = catalog.Get("studioProjectFolder");
        Assert.Equal(Path.Combine(videos, "Studio"), (string?)setting["value"]);
        Assert.False((bool)setting["writable"]!);

        var chosen = Path.Combine(_root, "Projects");
        settings.StudioProjectFolder = chosen;
        Assert.Equal(Path.Combine(videos, "Studio"), (string?)catalog.Get("studioProjectFolder")["value"]);
        settings.UseDefaultSaveDirectories = false;
        Assert.Equal(chosen, (string?)catalog.Get("studioProjectFolder")["value"]);
        settings.UseDefaultSaveDirectories = true;
        Assert.Equal(Path.Combine(videos, "Studio"), (string?)catalog.Get("studioProjectFolder")["value"]);
        Assert.Equal(chosen, settings.StudioProjectFolder);
        settings.UseDefaultSaveDirectories = false;

        var error = Assert.Throws<AutomationException>(() => catalog.Set("studioProjectFolder", System.Text.Json.Nodes.JsonValue.Create(_root)));
        Assert.Equal(AutomationErrorCodes.InvalidParams, error.Code);
        Assert.Equal(chosen, settings.StudioProjectFolder);
        Assert.False(Directory.Exists(Path.Combine(videos, "Studio")));
    }

    [Fact]
    public void DefaultsTemporarilyOverrideTheChosenFolderForSaving()
    {
        var chosen = Directory.CreateDirectory(Path.Combine(_root, "Projects")).FullName;
        var videos = Path.Combine(_root, "Videos");
        var expected = Path.Combine(videos, "Studio");

        Assert.Equal(expected, StudioProjectSaveLocation.Resolve(chosen, videos, useDefaults: true));
        Assert.Equal(expected, StudioProjectSaveLocation.Prepare(chosen, videos, useDefaults: true));
        Assert.Equal(chosen, StudioProjectSaveLocation.Resolve(chosen, videos, useDefaults: false));
        Assert.Equal(chosen, StudioProjectSaveLocation.Prepare(chosen, videos, useDefaults: false));
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
