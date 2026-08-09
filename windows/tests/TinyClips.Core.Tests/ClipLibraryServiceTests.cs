using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class ClipLibraryServiceTests
{
    // OutputDirectory with MyPictures = MyVideos = @"C:\" gives Path.Combine(@"C:\", "TinyClips").
    private static readonly string CapturesDir = Path.Combine(@"C:\", "TinyClips");

    [Fact]
    public async Task GetClipsAsync_ReturnsClipsFromKnownExtensionsOnly()
    {
        var (storage, libFs) = BuildDefaults();
        libFs.Files[CapturesDir] =
        [
            Path.Combine(CapturesDir, "shot.png"),
            Path.Combine(CapturesDir, "video.mp4"),
            Path.Combine(CapturesDir, "anim.gif"),
            Path.Combine(CapturesDir, "readme.txt"),   // ignored
            Path.Combine(CapturesDir, "data.bin"),     // ignored
        ];

        var service = new ClipLibraryService(storage, libFs);
        var clips = await service.GetClipsAsync();

        Assert.Equal(3, clips.Count);
        Assert.Contains(clips, c => c.FileName == "shot.png"  && c.Type == CaptureType.Screenshot);
        Assert.Contains(clips, c => c.FileName == "video.mp4" && c.Type == CaptureType.Video);
        Assert.Contains(clips, c => c.FileName == "anim.gif"  && c.Type == CaptureType.Gif);
    }

    [Fact]
    public async Task GetClipsAsync_SortsNewestFirst()
    {
        var (storage, libFs) = BuildDefaults();
        var oldTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newTime = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var oldPath = Path.Combine(CapturesDir, "old.png");
        var newPath = Path.Combine(CapturesDir, "new.png");
        libFs.Files[CapturesDir] = [oldPath, newPath];
        libFs.LastWriteTimes[oldPath] = oldTime;
        libFs.LastWriteTimes[newPath] = newTime;

        var service = new ClipLibraryService(storage, libFs);
        var clips = await service.GetClipsAsync();

        Assert.Equal(2, clips.Count);
        Assert.Equal("new.png", clips[0].FileName);
        Assert.Equal("old.png", clips[1].FileName);
    }

    [Fact]
    public async Task GetClipsAsync_DeduplicatesDirsWhenCustomSaveDirectoryIsSet()
    {
        const string customDir = @"C:\Custom";

        // All three types map to the same custom dir — scanning must happen only once.
        var settingsSvc = new TestSettingsService { SaveDirectory = customDir };
        var captureSettings = new CaptureSettings(settingsSvc);
        var storage = new ClipStorageService(captureSettings, new FileNameService(captureSettings), new StubFileSystem());

        var libFs = new FakeFileSystem();
        libFs.Files[customDir] =
        [
            Path.Combine(customDir, "a.png"),
            Path.Combine(customDir, "b.mp4"),
        ];

        var service = new ClipLibraryService(storage, libFs);
        var clips = await service.GetClipsAsync();

        Assert.Equal(2, clips.Count);
    }

    [Fact]
    public async Task GetClipsAsync_SkipsMissingDirectories()
    {
        var (storage, libFs) = BuildDefaults();
        // No Files entries → DirectoryExists returns false → no clips.

        var service = new ClipLibraryService(storage, libFs);
        var clips = await service.GetClipsAsync();

        Assert.Empty(clips);
    }

    [Fact]
    public async Task GetClipsAsync_RecordsFileSizeAndCapturedAt()
    {
        var (storage, libFs) = BuildDefaults();
        var time = new DateTimeOffset(2026, 5, 15, 10, 30, 0, TimeSpan.Zero);
        var snapPath = Path.Combine(CapturesDir, "snap.png");
        libFs.Files[CapturesDir] = [snapPath];
        libFs.LastWriteTimes[snapPath] = time;
        libFs.FileSizes[snapPath] = 1_048_576;

        var service = new ClipLibraryService(storage, libFs);
        var clips = await service.GetClipsAsync();

        var clip = Assert.Single(clips);
        Assert.Equal(1_048_576, clip.FileSizeBytes);
        Assert.Equal(time, clip.CapturedAt);
    }

    [Fact]
    public void Delete_DelegatesToFileSystem()
    {
        var (storage, libFs) = BuildDefaults();
        var targetPath = Path.Combine(CapturesDir, "delete-me.png");

        var service = new ClipLibraryService(storage, libFs);
        service.Delete(targetPath);

        Assert.Contains(targetPath, libFs.DeletedPaths);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static (IClipStorageService, FakeFileSystem) BuildDefaults()
    {
        var settings = new TestSettingsService();
        var captureSettings = new CaptureSettings(settings);
        var storageFs = new StubFileSystem(
            folders: new Dictionary<Environment.SpecialFolder, string>
            {
                [Environment.SpecialFolder.MyPictures] = @"C:\",
                [Environment.SpecialFolder.MyVideos]   = @"C:\",
            });
        var storage = new ClipStorageService(captureSettings, new FileNameService(captureSettings), storageFs);
        return (storage, new FakeFileSystem());
    }

    private sealed class TestSettingsService : ISettingsService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

        public AppTheme Theme { get; set; }
        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typedValue ? typedValue : defaultValue;

        public void Set<T>(string key, T value) => _values[key] = value is null ? string.Empty : value;
    }

    /// <summary>Minimal IFileSystem used as the ClipStorageService's dependency for directory resolution.</summary>
    private sealed class StubFileSystem : IFileSystem
    {
        private readonly Dictionary<Environment.SpecialFolder, string> _folders;

        public StubFileSystem(Dictionary<Environment.SpecialFolder, string>? folders = null)
        {
            _folders = folders ?? [];
        }

        public bool FileExists(string path) => false;
        public bool DirectoryExists(string path) => false;
        public void CreateDirectory(string path) { }
        public string GetFolderPath(Environment.SpecialFolder folder) =>
            _folders.TryGetValue(folder, out var v) ? v : string.Empty;
        public IEnumerable<string> EnumerateFiles(string directory) => [];
        public DateTimeOffset GetFileLastWriteTime(string path) => DateTimeOffset.MinValue;
        public long GetFileSizeBytes(string path) => 0;
        public void DeleteFile(string path) { }
    }

    /// <summary>FakeFileSystem used as the ClipLibraryService's scanning dependency.</summary>
    private sealed class FakeFileSystem : IFileSystem
    {
        public HashSet<string> ExistingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DeletedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, IEnumerable<string>> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DateTimeOffset> LastWriteTimes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> FileSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => ExistingPaths.Contains(path);
        public bool DirectoryExists(string path) => Files.ContainsKey(path);
        public void CreateDirectory(string path) { }
        public string GetFolderPath(Environment.SpecialFolder folder) => string.Empty;
        public IEnumerable<string> EnumerateFiles(string directory) =>
            Files.TryGetValue(directory, out var files) ? files : [];
        public DateTimeOffset GetFileLastWriteTime(string path) =>
            LastWriteTimes.TryGetValue(path, out var t) ? t : DateTimeOffset.MinValue;
        public long GetFileSizeBytes(string path) =>
            FileSizes.TryGetValue(path, out var size) ? size : 0;
        public void DeleteFile(string path) => DeletedPaths.Add(path);
    }
}
