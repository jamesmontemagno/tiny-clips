using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class FileNameServiceTests
{
    [Fact]
    public void GeneratedFileName_ReplacesTokensAndLowercasesExtension()
    {
        var settings = CreateSettings();
        settings.FileNameTemplate = "{app} {type} {date} {time} {datetime}";
        var service = new FileNameService(settings);
        var date = new DateTime(2026, 1, 2, 15, 4, 5);

        var screenshot = service.GeneratedFileName(CaptureType.Screenshot, ".PNG", date);
        var video = service.GeneratedFileName(CaptureType.Video, "MP4 ", date);
        var gif = service.GeneratedFileName(CaptureType.Gif, "GIF", date);

        Assert.Equal("TinyClips Screenshot 2026-01-02 15.04.05 2026-01-02_15.04.05.png", screenshot);
        Assert.Equal("TinyClips Video 2026-01-02 15.04.05 2026-01-02_15.04.05.mp4", video);
        Assert.Equal("TinyClips GIF 2026-01-02 15.04.05 2026-01-02_15.04.05.gif", gif);
    }

    [Fact]
    public void GeneratedFileName_FallsBackToDefaultTemplateWhenEmpty()
    {
        var settings = CreateSettings();
        settings.FileNameTemplate = "   ";
        var service = new FileNameService(settings);

        var name = service.GeneratedFileName(CaptureType.Video, "mp4", new DateTime(2026, 1, 2, 15, 4, 5));

        Assert.Equal("TinyClips 2026-01-02 at 15.04.05.mp4", name);
    }

    [Fact]
    public void GeneratedFileName_SanitizesAndRemovesInvalidCharacters()
    {
        var settings = CreateSettings();
        settings.FileNameTemplate = " bad / : ? * \" < > | name  ";
        var service = new FileNameService(settings);

        var name = service.GeneratedFileName(CaptureType.Screenshot, "png", new DateTime(2026, 1, 2, 15, 4, 5));

        Assert.DoesNotContain("  ", name);
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain(":", name);
        Assert.DoesNotContain("?", name);
        Assert.DoesNotContain("*", name);
        Assert.DoesNotContain("\"", name);
        Assert.DoesNotContain("<", name);
        Assert.DoesNotContain(">", name);
        Assert.DoesNotContain("|", name);

        foreach (var invalidChar in Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '?', '*', '"', '<', '>', '|' }))
        {
            Assert.DoesNotContain(invalidChar, name);
        }
    }

    [Fact]
    public void GeneratedFileName_TrimsExtensionWhitespaceAndDots()
    {
        var settings = CreateSettings();
        var service = new FileNameService(settings);

        var name = service.GeneratedFileName(CaptureType.Screenshot, ".PNG ", new DateTime(2026, 1, 2, 15, 4, 5));

        Assert.EndsWith(".png", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileExtensionFor_WebpScreenshot_MatchesNamingPreview()
    {
        var settings = CreateSettings();
        settings.ImageFormat = ImageFormat.Webp;
        var service = new FileNameService(settings);

        Assert.Equal("webp", service.FileExtensionFor(CaptureType.Screenshot));
        Assert.EndsWith(".webp", service.NamingPreview(CaptureType.Screenshot));
        Assert.Equal("mp4", service.FileExtensionFor(CaptureType.Video));
        Assert.Equal("gif", service.FileExtensionFor(CaptureType.Gif));
    }

    private static ICaptureSettings CreateSettings() => new CaptureSettings(new TestSettingsService());

    private sealed class TestSettingsService : ISettingsService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

        public AppTheme Theme { get; set; }

        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue)
        {
            if (_values.TryGetValue(key, out var value) && value is T typedValue)
            {
                return typedValue;
            }

            return defaultValue;
        }

        public void Set<T>(string key, T value)
        {
            _values[key] = value is null ? string.Empty : value;
        }
    }
}
