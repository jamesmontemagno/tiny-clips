using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void SetAndGet_RoundTripsValue()
    {
        var settings = new SettingsService();

        settings.Set("SampleValue", 42);

        Assert.Equal(42, settings.Get("SampleValue", 0));
    }

    [Fact]
    public void Theme_PersistsThroughService()
    {
        var settings = new SettingsService();

        settings.Theme = AppTheme.Dark;

        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    [Fact]
    public void LargeText_RoundTripsThroughFileBackedStorage()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var value = new string('a', 16_384);
            new SettingsService(directory).SetLargeText("transcript", value);

            var reloaded = new SettingsService(directory);

            Assert.Equal(value, reloaded.GetLargeText("transcript", string.Empty));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LargeText_MigratesLegacyLocalSetting()
    {
        var directory = CreateTemporaryDirectory();
        var key = $"legacyTranscript-{Guid.NewGuid():N}";
        var settings = new SettingsService(directory);
        try
        {
            settings.Set(key, "Legacy script");

            Assert.Equal("Legacy script", settings.GetLargeText(key, string.Empty));
            Assert.Equal("Legacy script", new SettingsService(directory).GetLargeText(key, string.Empty));
        }
        finally
        {
            settings.Set(key, string.Empty);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LargeText_FallsBackInMemoryWhenFileWriteFails()
    {
        var invalidDirectory = Path.GetTempFileName();
        try
        {
            var settings = new SettingsService(invalidDirectory);

            settings.SetLargeText("transcript", "Unsaved script");

            Assert.Equal("Unsaved script", settings.GetLargeText("transcript", string.Empty));
        }
        finally
        {
            File.Delete(invalidDirectory);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"TinyClips.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
