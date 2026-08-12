using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class ClipStorageServiceTests
{
    [Fact]
    public void FileExtensionFor_ReturnsExpectedExtensionByTypeAndFormat()
    {
        var settings = CreateSettings();
        settings.ImageFormat = ImageFormat.Png;
        var service = CreateService(settings, new FakeFileSystem());

        Assert.Equal("png", service.FileExtensionFor(CaptureType.Screenshot));
        Assert.Equal("jpg", CreateService(CreateSettings(), new FakeFileSystem()).FileExtensionFor(CaptureType.Screenshot));
        Assert.Equal("mp4", service.FileExtensionFor(CaptureType.Video));
        Assert.Equal("gif", service.FileExtensionFor(CaptureType.Gif));
    }

    [Fact]
    public void OutputDirectory_UsesCustomDirectoriesOrTypeSpecificDefaults()
    {
        var fakeFileSystem = new FakeFileSystem
        {
            FolderPaths =
            {
                [Environment.SpecialFolder.MyPictures] = "C:\\Users\\Test\\Pictures",
                [Environment.SpecialFolder.MyVideos] = "C:\\Users\\Test\\Videos",
            },
        };

        var settings = CreateSettings();
        settings.UseDefaultSaveDirectories = false;
        settings.ScreenshotSaveDirectory = "C:\\Temp\\Screenshots";
        settings.VideoSaveDirectory = "C:\\Temp\\Videos";
        settings.GifSaveDirectory = "C:\\Temp\\Gifs";
        var saveDirectoryService = CreateService(settings, fakeFileSystem);

        Assert.Equal("C:\\Temp\\Screenshots", saveDirectoryService.OutputDirectory(CaptureType.Screenshot));
        Assert.Equal("C:\\Temp\\Videos", saveDirectoryService.OutputDirectory(CaptureType.Video));
        Assert.Equal("C:\\Temp\\Gifs", saveDirectoryService.OutputDirectory(CaptureType.Gif));

        settings.UseDefaultSaveDirectories = true;
        var defaultService = CreateService(settings, fakeFileSystem);

        var expectedVideos = Path.Combine("C:\\Users\\Test\\Videos", "TinyClips");
        var expectedPictures = Path.Combine("C:\\Users\\Test\\Pictures", "TinyClips");
        Assert.Equal(expectedPictures, defaultService.OutputDirectory(CaptureType.Screenshot));
        Assert.Equal(expectedVideos, defaultService.OutputDirectory(CaptureType.Gif));
        Assert.Equal(expectedVideos, defaultService.OutputDirectory(CaptureType.Video));
    }

    [Fact]
    public void GenerateFilePath_UsesUniqueSuffixesAndStemSuffix()
    {
        var fakeFileSystem = new FakeFileSystem();
        var settings = CreateSettings();
        settings.UseDefaultSaveDirectories = false;
        settings.ScreenshotSaveDirectory = "C:\\Temp\\TinyClips";
        var deterministicNames = new FixedFileNameService();
        var service = new ClipStorageService(settings, deterministicNames, fakeFileSystem);

        var firstPath = Path.Combine("C:\\Temp\\TinyClips", "Preset.png");
        fakeFileSystem.ExistingPaths.Add(firstPath);
        var uniquePath = service.GenerateFilePath(CaptureType.Screenshot, "png");

        Assert.EndsWith("Preset 2.png", uniquePath, StringComparison.OrdinalIgnoreCase);

        fakeFileSystem.ExistingPaths.Add(Path.Combine("C:\\Temp\\TinyClips", "Preset 2.png"));
        var nextPath = service.GenerateFilePath(CaptureType.Screenshot, "png");

        Assert.EndsWith("Preset 3.png", nextPath, StringComparison.OrdinalIgnoreCase);

        var suffixedPath = service.GenerateFilePath(CaptureType.Screenshot, "png", "(trimmed)");
        Assert.Contains("(trimmed)", Path.GetFileNameWithoutExtension(suffixedPath));
        Assert.EndsWith(".png", suffixedPath, StringComparison.OrdinalIgnoreCase);
    }

    private static ICaptureSettings CreateSettings() => new CaptureSettings(new TestSettingsService());

    private static ClipStorageService CreateService(ICaptureSettings settings, FakeFileSystem fileSystem) =>
        new(settings, new FileNameService(settings), fileSystem);

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

    private sealed class FakeFileSystem : IFileSystem
    {
        public HashSet<string> ExistingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<Environment.SpecialFolder, string> FolderPaths { get; } = new();

        public bool FileExists(string path) => ExistingPaths.Contains(path);
        public bool DirectoryExists(string path) => false;
        public void CreateDirectory(string path) => ExistingPaths.Add(path);
        public string GetFolderPath(Environment.SpecialFolder folder) => FolderPaths.TryGetValue(folder, out var value) ? value : string.Empty;
        public IEnumerable<string> EnumerateFiles(string directory) => [];
        public DateTimeOffset GetFileLastWriteTime(string path) => DateTimeOffset.MinValue;
        public long GetFileSizeBytes(string path) => 0;
        public void DeleteFile(string path) { }
    }

    private sealed class FixedFileNameService : IFileNameService
    {
        public string GeneratedFileName(CaptureType type, string fileExtension, DateTime? date = null) => type switch
        {
            CaptureType.Screenshot => "Preset.png",
            CaptureType.Video => "Preset.mp4",
            CaptureType.Gif => "Preset.gif",
            _ => "Preset.bin",
        };

        public string NamingPreview(CaptureType type) => GeneratedFileName(type, FileExtensionFor(type));

        public string FileExtensionFor(CaptureType type) => type switch
        {
            CaptureType.Screenshot => "png",
            CaptureType.Video => "mp4",
            CaptureType.Gif => "gif",
            _ => string.Empty,
        };
    }
}
