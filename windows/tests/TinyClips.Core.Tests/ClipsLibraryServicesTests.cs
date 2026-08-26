using TinyClips.Core.Models;
using TinyClips.Core.Models.ClipsLibrary;
using TinyClips.Core.Services;
using TinyClips.Core.Services.ClipsLibrary;

namespace TinyClips.Core.Tests;

public sealed class ClipsLibraryServicesTests
{
    private static readonly string CapturesDir = Path.Combine(@"C:\", "TinyClips");

    // ---------------------------------------------------------------- ClipLibraryService

    [Fact]
    public async Task GetClipsAsync_OnlyTinyClipsFiles_HidesForeignFiles()
    {
        var (storage, fs, ops) = Build();
        fs.Files[CapturesDir] =
        [
            Path.Combine(CapturesDir, "TinyClips 2026-01-01 at 10.00.00.png"),
            Path.Combine(CapturesDir, "vacation.png"),
        ];
        var service = new ClipLibraryService(storage, fs, ops);

        var all = await service.GetClipsAsync(onlyTinyClipsFiles: false);
        var ours = await service.GetClipsAsync(onlyTinyClipsFiles: true);

        Assert.Equal(2, all.Count);
        Assert.Single(ours);
        Assert.StartsWith("TinyClips", ours[0].FileName);
    }

    [Fact]
    public void GetLibraryDirectories_ReturnsDeduplicatedDirectories()
    {
        var (storage, fs, ops) = Build();
        var service = new ClipLibraryService(storage, fs, ops);

        var dirs = service.GetLibraryDirectories();

        Assert.Single(dirs);
        Assert.Equal(CapturesDir, dirs[0]);
    }

    [Fact]
    public void Rename_PreservesExtensionAndMovesFile()
    {
        var (storage, fs, ops) = Build();
        var service = new ClipLibraryService(storage, fs, ops);
        var source = Path.Combine(CapturesDir, "old name.mp4");

        var result = service.Rename(source, "New: name?");

        Assert.Equal(Path.Combine(CapturesDir, "New- name-.mp4"), result);
        Assert.Single(ops.Moves);
        Assert.Equal((source, result), ops.Moves[0]);
    }

    [Fact]
    public void Rename_ThrowsWhenTargetExists()
    {
        var (storage, fs, ops) = Build();
        var service = new ClipLibraryService(storage, fs, ops);
        fs.ExistingPaths.Add(Path.Combine(CapturesDir, "taken.png"));

        Assert.Throws<IOException>(() => service.Rename(Path.Combine(CapturesDir, "a.png"), "taken"));
        Assert.Empty(ops.Moves);
    }

    [Fact]
    public void Rename_RejectsBlankName()
    {
        var (storage, fs, ops) = Build();
        var service = new ClipLibraryService(storage, fs, ops);

        Assert.Throws<ArgumentException>(() => service.Rename(Path.Combine(CapturesDir, "a.png"), "   "));
    }

    [Fact]
    public void IsSupportedClipFile_RecognisesKnownExtensions()
    {
        Assert.True(ClipLibraryService.IsSupportedClipFile(@"C:\x\a.PNG"));
        Assert.True(ClipLibraryService.IsSupportedClipFile(@"C:\x\a.mp4"));
        Assert.True(ClipLibraryService.IsSupportedClipFile(@"C:\x\a.gif"));
        Assert.False(ClipLibraryService.IsSupportedClipFile(@"C:\x\a.txt"));
        Assert.Equal(CaptureType.Video, ClipLibraryService.CaptureTypeFor(@"C:\x\a.mp4"));
    }

    // ---------------------------------------------------------------- ClipArchiveService

    [Fact]
    public void ArchiveOlderThan_MovesOnlyOldClipsIntoArchiveFolder()
    {
        var fs = new FakeFileSystem();
        var ops = new FakeFileOperations();
        var service = new ClipArchiveService(fs, ops);
        var now = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        var clips = new[]
        {
            new ClipEntry(Path.Combine(CapturesDir, "old.png"), CaptureType.Screenshot, "old.png", now.AddDays(-45), 1),
            new ClipEntry(Path.Combine(CapturesDir, "new.png"), CaptureType.Screenshot, "new.png", now.AddDays(-2), 1),
        };

        var moved = service.ArchiveOlderThan(clips, 30, now);

        Assert.Single(moved);
        Assert.Equal(Path.Combine(CapturesDir, "Archive", "old.png"), moved[0].NewPath);
        Assert.Contains(Path.Combine(CapturesDir, "Archive"), fs.CreatedDirectories);
    }

    [Fact]
    public void Archive_AvoidsOverwritingExistingArchivedFile()
    {
        var fs = new FakeFileSystem();
        var ops = new FakeFileOperations();
        fs.ExistingPaths.Add(Path.Combine(CapturesDir, "Archive", "dup.png"));
        var service = new ClipArchiveService(fs, ops);

        var result = service.Archive(new ClipEntry(Path.Combine(CapturesDir, "dup.png"), CaptureType.Screenshot, "dup.png", DateTimeOffset.UtcNow, 1));

        Assert.Equal(Path.Combine(CapturesDir, "Archive", "dup (1).png"), result);
    }

    [Fact]
    public void ArchiveOlderThan_ZeroDays_IsNoOp()
    {
        var service = new ClipArchiveService(new FakeFileSystem(), new FakeFileOperations());

        Assert.Empty(service.ArchiveOlderThan([new ClipEntry(@"C:\a.png", CaptureType.Screenshot, "a.png", DateTimeOffset.MinValue, 1)], 0, DateTimeOffset.UtcNow));
    }

    // ---------------------------------------------------------------- Watcher / debounce

    [Fact]
    public void DebouncedSignal_CoalescesBurstIntoSingleCallback()
    {
        var time = new ManualTimeProvider();
        var fired = 0;
        using var signal = new DebouncedSignal(() => fired++, TimeSpan.FromMilliseconds(500), time);

        signal.Signal();
        time.Advance(TimeSpan.FromMilliseconds(200));
        signal.Signal();
        time.Advance(TimeSpan.FromMilliseconds(200));
        signal.Signal();
        Assert.Equal(0, fired);

        time.Advance(TimeSpan.FromMilliseconds(600));

        Assert.Equal(1, fired);
    }

    [Fact]
    public void DebouncedSignal_Cancel_PreventsCallback()
    {
        var time = new ManualTimeProvider();
        var fired = 0;
        using var signal = new DebouncedSignal(() => fired++, TimeSpan.FromMilliseconds(100), time);

        signal.Signal();
        signal.Cancel();
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, fired);
    }

    [Theory]
    [InlineData(@"C:\x\clip.mp4", true)]
    [InlineData(@"C:\x\clip.mp4.tmp", false)]
    [InlineData(@"C:\x\~clip.png", false)]
    [InlineData(@"C:\x\notes.txt", false)]
    public void ClipLibraryWatcher_IsRelevant(string path, bool expected)
    {
        Assert.Equal(expected, ClipLibraryWatcher.IsRelevant(path));
    }

    // ---------------------------------------------------------------- Settings

    [Fact]
    public void ClipsLibrarySettings_DefaultsMatchMacParity()
    {
        var settings = new ClipsLibrarySettings(new TestSettingsService());

        Assert.Equal(ClipsViewMode.Grid, settings.DefaultViewMode);
        Assert.Equal(ClipSortOption.NewestFirst, settings.DefaultSort);
        Assert.True(settings.ConfirmDelete);
        Assert.True(settings.RememberLastState);
        Assert.False(settings.ArchiveOldClips);
        Assert.Equal(30, settings.ArchiveAfterDays);
        Assert.Equal(0, settings.AutoRefreshSeconds);
    }

    [Fact]
    public void ClipsLibrarySettings_MigratesLegacyKeysOnce()
    {
        var backing = new TestSettingsService();
        backing.Set("clipsManagerViewMode", false);
        backing.Set("clipsManagerFilter", "Video");
        backing.Set("clipsManagerDateFilter", "Week");
        backing.Set("clipsManagerSort", 1);

        var settings = new ClipsLibrarySettings(backing);

        Assert.Equal(ClipsViewMode.List, settings.LastViewMode);
        Assert.Equal(ClipTypeFilter.Videos, settings.LastTypeFilter);
        Assert.Equal(ClipDateFilter.Last7Days, settings.LastDateFilter);
        Assert.Equal(ClipSortOption.OldestFirst, settings.LastSort);

        // A later change must not be clobbered by re-running migration.
        settings.LastViewMode = ClipsViewMode.Grid;
        var again = new ClipsLibrarySettings(backing);
        Assert.Equal(ClipsViewMode.Grid, again.LastViewMode);
    }

    [Fact]
    public void ClipsLibrarySettings_LastValuesFallBackToDefaults()
    {
        var settings = new ClipsLibrarySettings(new TestSettingsService());
        settings.DefaultSort = ClipSortOption.Name;

        Assert.Equal(ClipSortOption.Name, settings.LastSort);
    }

    [Fact]
    public void ClipsLibrarySettings_ClampsNumericValues()
    {
        var settings = new ClipsLibrarySettings(new TestSettingsService());

        settings.AutoRefreshSeconds = -5;
        settings.ArchiveAfterDays = 0;

        Assert.Equal(0, settings.AutoRefreshSeconds);
        Assert.Equal(1, settings.ArchiveAfterDays);
    }

    // ---------------------------------------------------------------- ClipMetadata

    [Fact]
    public void ClipMetadata_NormalizeTags_TrimsDedupesAndSorts()
    {
        var tags = ClipMetadata.NormalizeTags([" b ", "a", "B", "", "c"]);

        Assert.Equal(["a", " b ".Trim(), "c"], tags);
    }

    [Fact]
    public void LibraryClip_DisplayName_FallsBackToFileStem()
    {
        var entry = new ClipEntry(@"C:\x\TinyClips 2026.png", CaptureType.Screenshot, "TinyClips 2026.png", DateTimeOffset.UtcNow, 1);

        Assert.Equal("TinyClips 2026", new LibraryClip(entry, ClipMetadata.Empty(entry.Path)).DisplayName);
        Assert.Equal("Hero", new LibraryClip(entry, new ClipMetadata(entry.Path, DisplayName: "Hero")).DisplayName);
    }

    // ---------------------------------------------------------------- helpers

    private static (ClipStorageService Storage, FakeFileSystem Fs, FakeFileOperations Ops) Build()
    {
        var captureSettings = new CaptureSettings(new TestSettingsService());
        var storageFs = new FakeFileSystem
        {
            Folders =
            {
                [Environment.SpecialFolder.MyPictures] = @"C:\",
                [Environment.SpecialFolder.MyVideos]   = @"C:\",
            },
        };
        var storage = new ClipStorageService(captureSettings, new FileNameService(captureSettings), storageFs);
        return (storage, new FakeFileSystem(), new FakeFileOperations());
    }

    private sealed class TestSettingsService : ISettingsService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

        public AppTheme Theme { get; set; }
        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue)
        {
            if (!_values.TryGetValue(key, out var value))
            {
                return defaultValue;
            }

            if (value is T typed)
            {
                return typed;
            }

            if (value is string text && typeof(T).IsEnum)
            {
                return (T)Enum.Parse(typeof(T), text, true);
            }

            return defaultValue;
        }

        public void Set<T>(string key, T value) => _values[key] = value is Enum e ? e.ToString() : value is null ? string.Empty : value;
    }

    private sealed class FakeFileOperations : IClipFileOperations
    {
        public List<(string Source, string Destination)> Moves { get; } = [];

        public void MoveFile(string sourcePath, string destinationPath) => Moves.Add((sourcePath, destinationPath));

        public DateTimeOffset GetFileCreationTime(string path) => DateTimeOffset.MinValue;
    }

    private sealed class FakeFileSystem : IFileSystem
    {
        public Dictionary<Environment.SpecialFolder, string> Folders { get; } = [];
        public HashSet<string> ExistingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> CreatedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, IEnumerable<string>> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => ExistingPaths.Contains(path);
        public bool DirectoryExists(string path) => Files.ContainsKey(path);
        public void CreateDirectory(string path) => CreatedDirectories.Add(path);
        public string GetFolderPath(Environment.SpecialFolder folder) => Folders.TryGetValue(folder, out var v) ? v : string.Empty;
        public IEnumerable<string> EnumerateFiles(string directory) => Files.TryGetValue(directory, out var files) ? files : [];
        public DateTimeOffset GetFileLastWriteTime(string path) => DateTimeOffset.MinValue;
        public long GetFileSizeBytes(string path) => 0;
        public void DeleteFile(string path) { }
    }
}
