using System.Reflection;
using System.Xml.Linq;
using TinyClips.App.Services.Studio;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;
using TinyClips.Core.Tests;

namespace TinyClips.App.Tests;

/// <summary>
/// A <c>.tinyclips</c> file opens the app: the file type in the two package manifests, and how
/// the app tells a project file from a picture. That Windows then starts the app with the
/// file, and what the app shows, nobody has tried: the package was not installed for this.
/// </summary>
public sealed class StudioProjectFileTypeTests
{
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";

    public static TheoryData<string> Manifests => ["Package.appxmanifest", "Package.Store.appxmanifest"];

    [Theory]
    [MemberData(nameof(Manifests))]
    public void BothFlavoursOfThePackage_OpenTinyclipsFiles_BesideThePictures(string manifest)
    {
        var associations = Read(manifest).Descendants(Uap + "FileTypeAssociation")
            .Select(association => (
                Name: association.Attribute("Name")?.Value,
                Display: association.Element(Uap + "DisplayName")?.Value,
                Types: association.Descendants(Uap + "FileType").Select(type => type.Value).ToArray()))
            .ToList();

        Assert.Equal(["image", "tinyclipsproject"], associations.Select(association => association.Name));
        Assert.Equal("Tiny Clips project", associations[1].Display);
        Assert.Equal([StudioProjectFolder.ProjectFileExtension], associations[1].Types);

        // The pictures are as they were, and are the ones the app takes for pictures.
        Assert.Equal("Tiny Clips Image", associations[0].Display);
        Assert.Equal(ActivatedFile.ImageExtensions, associations[0].Types);

        // Each association is an extension of its own, of the kind that opens files.
        Assert.All(
            Read(manifest).Descendants(Uap + "FileTypeAssociation"),
            association => Assert.Equal("windows.fileTypeAssociation", association.Parent?.Attribute("Category")?.Value));
    }

    [Theory]
    [InlineData(@"D:\Saved\My Demo\My Demo.tinyclips", "StudioProject")]
    [InlineData(@"D:\Saved\My Demo\MY DEMO.TINYCLIPS", "StudioProject")]
    [InlineData(@"D:\Pictures\shot.png", "Image")]
    [InlineData(@"D:\Pictures\shot.JPG", "Image")]
    [InlineData(@"D:\Pictures\shot.jpeg", "Image")]
    [InlineData(@"D:\Pictures\shot.webp", "Image")]
    [InlineData(@"D:\Saved\My Demo\screen.mp4", "None")]
    [InlineData(@"D:\Saved\My Demo\project.json", "None")]
    [InlineData(@"D:\Saved\My Demo.tinyclips\screen.mp4", "None")]
    [InlineData(@"D:\Saved\tinyclips", "None")]
    [InlineData("", "None")]
    [InlineData(null, "None")]
    public void AFileIsToldByItsExtension(string? path, string kind)
    {
        Assert.Equal(kind, ActivatedFile.KindOf(path).ToString());
    }

    [Fact]
    public void OfTheFilesHandedOver_TheFirstTheAppHasAnEditorFor_IsOpened()
    {
        Assert.Equal(
            (ActivatedFileKind.StudioProject, @"D:\b.tinyclips"),
            ActivatedFile.FirstSupported([null, @"D:\a.txt", @"D:\b.tinyclips", @"D:\c.png"]));
        Assert.Equal(
            (ActivatedFileKind.Image, @"D:\c.png"),
            ActivatedFile.FirstSupported([@"D:\c.png", @"D:\b.tinyclips"]));
        Assert.Null(ActivatedFile.FirstSupported([@"D:\a.txt", null]));
        Assert.Null(ActivatedFile.FirstSupported([]));
    }

    private static XDocument Read(string manifest)
    {
        using var stream = typeof(StudioProjectFileTypeTests).Assembly.GetManifestResourceStream("PackageManifest." + manifest)
            ?? throw new InvalidOperationException($"{manifest} is not in the test assembly. The project file puts it there.");
        return XDocument.Load(stream);
    }
}

/// <summary>
/// The Studio projects the tray's recent captures list: read ahead of time and off the thread
/// that asks, none while Studio is switched off, and mixed in with the saved captures by date.
/// The popup itself cannot be pictured, and was not opened for this.
/// </summary>
public sealed class StudioRecentDraftsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TinyClipsAppTests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
    private readonly StudioProjectStore _projects;
    private readonly IStudioProjectStore _store;
    private readonly CaptureSettings _settings = new(new MemorySettings()) { StudioPreviewEnabled = true };

    public StudioRecentDraftsTests()
    {
        _projects = new StudioProjectStore(Path.Combine(_directory, "Projects"), _time);
        _store = DispatchProxy.Create<IStudioProjectStore, CountingStore>();
        ((CountingStore)_store).Inner = _projects;
    }

    private int Listings => ((CountingStore)_store).CallsTo(nameof(IStudioProjectStore.ListSummaries));

    [Fact]
    public async Task NothingIsListed_UntilTheProjectsWereRead_AndTheReadIsNotOnTheThreadThatAsks()
    {
        var draft = Record("A draft");
        var drafts = new StudioRecentDrafts(_store, _settings);
        var changes = 0;
        drafts.Changed += (_, _) => changes++;

        // Built before anything was read: the popup does not wait for the disk.
        Assert.Empty(drafts.Projects);
        Assert.Empty(drafts.EntriesFor([], 5));
        Assert.Equal(0, Listings);

        // The store keeps its first list until the test lets go of it. The call comes back
        // all the same: the store is read on another thread than the one that asked.
        var counting = (CountingStore)_store;
        counting.HoldTheFirstListOfProjects();
        var read = drafts.ReloadAsync(5);
        Assert.False(read.IsCompleted);
        Assert.True(counting.FirstListWasRead.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Empty(drafts.Projects);
        counting.HandOverTheFirstListOfProjects();
        await read.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, Listings);
        Assert.Equal([draft], drafts.Projects.Select(project => project.Id));
        Assert.Equal([$"studio:{draft}"], drafts.EntriesFor([], 5).Select(entry => entry.Id));
        Assert.Equal(1, changes);

        // Read again and found the same: nobody is told.
        await drafts.ReloadAsync(5);
        Assert.Equal(2, Listings);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task WithStudioSwitchedOff_NothingIsRead_AndNothingIsListed_AlsoOfWhatWasReadBefore()
    {
        var draft = Record("A draft");
        var drafts = new StudioRecentDrafts(_store, _settings);
        await drafts.ReloadAsync(5);
        Assert.Single(drafts.EntriesFor([], 5));
        var capture = new RecentCapture(@"D:\Videos\clip.mp4", CaptureType.Video, _time.GetUtcNow());

        // Switched off after the read: the popup that is built now lists no project.
        _settings.StudioPreviewEnabled = false;
        Assert.Equal(["capture:D:\\Videos\\clip.mp4"], drafts.EntriesFor([capture], 5).Select(entry => entry.Id));

        // And the next read reads nothing and forgets what there was.
        var listings = Listings;
        await drafts.ReloadAsync(5);
        Assert.Equal(listings, Listings);
        Assert.Empty(drafts.Projects);
        Assert.Null(drafts.PosterOf(draft));

        _settings.StudioPreviewEnabled = true;
        await drafts.ReloadAsync(5);
        Assert.Equal([draft], drafts.Projects.Select(project => project.Id));
    }

    [Fact]
    public async Task DraftsAreMixedInWithTheSavedCapturesByDate_FiveLinesInAll_AndAnExportedProjectIsNotOneOfThem()
    {
        var start = _time.GetUtcNow();
        var oldest = Record("Oldest draft");
        _time.Advance(TimeSpan.FromHours(2));
        var exported = Record("Exported");
        var video = Path.Combine(_directory, "exported.mp4");
        File.WriteAllText(video, "a video");
        _projects.RecordExport(exported, video);
        _time.Advance(TimeSpan.FromHours(2));
        var newest = Record("Newest draft");
        var lost = Record("Lost its recording");
        File.Delete(_projects.GetPaths(lost).ScreenPath);
        RecentCapture[] captures =
        [
            new(@"D:\c5.png", CaptureType.Screenshot, start.AddHours(5)),
            new(video, CaptureType.Video, start.AddHours(3)),
            new(@"D:\c1.gif", CaptureType.Gif, start.AddHours(1)),
            new(@"D:\c0.png", CaptureType.Screenshot, start.AddHours(-1)),
            new(@"D:\c-2.png", CaptureType.Screenshot, start.AddHours(-2)),
        ];
        var drafts = new StudioRecentDrafts(_store, _settings);
        await drafts.ReloadAsync(5);

        var entries = drafts.EntriesFor(captures, 5);

        // The exported project is in the list as its video, which opens it; a project whose
        // recording is gone has nothing to open.
        Assert.Equal(
            ["capture:D:\\c5.png", $"studio:{newest}", $"capture:{video}", "capture:D:\\c1.gif", $"studio:{oldest}"],
            entries.Select(entry => entry.Id));
        Assert.Equal(4, drafts.Projects.Count);
        Assert.Equal(2, drafts.EntriesFor(captures, 2).Count);
    }

    [Fact]
    public async Task ADraftsPictureIsItsPoster_WhenItHasOne_AndOnlyTheNewestAreLookedFor()
    {
        var first = Record("First");
        _time.Advance(TimeSpan.FromHours(1));
        var second = Record("Second");
        _time.Advance(TimeSpan.FromHours(1));
        var third = Record("Third, without a poster");
        File.WriteAllText(_projects.GetPaths(first).PosterPath, "a poster");
        File.WriteAllText(_projects.GetPaths(second).PosterPath, "a poster");
        var drafts = new StudioRecentDrafts(_store, _settings);

        await drafts.ReloadAsync(2);

        // The two newest are the third and the second; the third has none.
        Assert.Null(drafts.PosterOf(third));
        Assert.Equal(_projects.GetPaths(second).PosterPath, drafts.PosterOf(second));
        Assert.Null(drafts.PosterOf(first));

        // A poster that is written later is found by the next read, which says that something changed.
        var changes = 0;
        drafts.Changed += (_, _) => changes++;
        File.WriteAllText(_projects.GetPaths(third).PosterPath, "a poster");
        await drafts.ReloadAsync(2);
        Assert.Equal(_projects.GetPaths(third).PosterPath, drafts.PosterOf(third));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task AskedForWhileARead_IsUnderWay_ItReadsOnceMore_SoThatTheLastRequestIsNotLost()
    {
        Record("A draft");
        var counting = (CountingStore)_store;
        var drafts = new StudioRecentDrafts(_store, _settings);
        counting.HoldTheFirstListOfProjects();

        var first = drafts.ReloadAsync(5);
        Assert.True(counting.FirstListWasRead.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        // A draft is recorded while the first read has its list already, and the popup asks again.
        var later = Record("Recorded meanwhile");
        var second = drafts.ReloadAsync(5);
        Assert.Same(first, second);
        counting.HandOverTheFirstListOfProjects();
        await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(2, Listings);
        Assert.Contains(later, drafts.Projects.Select(project => project.Id));
    }

    [Fact]
    public void AnEditorThatHasReadItsProject_IsAReasonToReadTheListsAgain_WhileItsProjectIsOpen()
    {
        // The lists are read when a window opens, a moment before its editor writes down
        // that the project was opened: read then, they have the project where it was.
        var tracker = new StudioProjectTracker();
        var changes = 0;
        tracker.Changed += (_, _) => changes++;

        tracker.MarkRead("a");
        Assert.Equal(0, changes);

        tracker.MarkOpened("a");
        tracker.MarkRead("a");
        Assert.Equal(2, changes);

        tracker.MarkClosed("a");
        tracker.MarkRead("a");
        Assert.Equal(3, changes);
    }

    /// <summary>A finished recording that was never exported, as the recorder leaves it.</summary>
    private string Record(string name)
    {
        var paths = _projects.BeginRecording();
        _projects.CompleteRecording(paths.ProjectId, new StudioProjectCreationRequest(
            name,
            new StudioRecordingSourceInfo(1920, 1080, 10),
            null,
            StudioAnchor.BottomRight,
            new StudioClickOverlay(),
            false,
            "1.9.0"));
        File.WriteAllBytes(paths.ScreenPath, [1, 2, 3]);
        return paths.ProjectId;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp folder that stays behind fails no test.
        }
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
