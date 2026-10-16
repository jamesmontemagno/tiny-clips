using System.Text.Json;
using System.Text.Json.Nodes;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

/// <summary>
/// What the tests of a project saved as a folder share: a store and a folder beside it that
/// stands for anywhere else on the disk, both under a temp folder of their own.
/// </summary>
public abstract class StudioProjectFolderTestBase : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TinyClipsTests", Guid.NewGuid().ToString("N"));

    private protected readonly ManualTimeProvider Time = new(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));

    protected StudioProjectFolderTestBase()
    {
        StoreRoot = Path.Combine(_root, "Projects");
        Outside = Path.Combine(_root, "Outside");
        Directory.CreateDirectory(StoreRoot);
        Directory.CreateDirectory(Outside);
        Store = new StudioProjectStore(StoreRoot, Time);
    }

    protected string StoreRoot { get; }

    /// <summary>A folder that is not the store: where projects are saved to and opened from.</summary>
    protected string Outside { get; }

    protected StudioProjectStore Store { get; }

    /// <summary>A recorded project in the store, with a few bytes for each of its files. Returns its id.</summary>
    protected string MakeProject(bool camera, bool events = false, bool poster = false)
    {
        var paths = Store.BeginRecording();
        File.WriteAllText(paths.ScreenPath, "screen");
        if (camera)
        {
            File.WriteAllText(paths.CameraPath!, "camera");
        }

        if (events)
        {
            File.WriteAllText(paths.EventsPath, "{}");
        }

        if (poster)
        {
            File.WriteAllText(paths.PosterPath, "poster");
        }

        Store.CompleteRecording(
            paths.ProjectId,
            new StudioProjectCreationRequest(
                "Test",
                new StudioRecordingSourceInfo(1920, 1080, 10),
                camera ? new StudioCameraSourceInfo(640, 480, 10) : null,
                StudioAnchor.BottomRight,
                new StudioClickOverlay(),
                false,
                "1.0"));
        return paths.ProjectId;
    }

    /// <summary>Saves a new project as a folder of that name and returns the folder and its <c>.tinyclips</c> file.</summary>
    protected (string Folder, string File) SaveNewProject(string name, bool camera = false, bool events = false, bool poster = false)
    {
        var folder = Path.Combine(Outside, name);
        Save(MakeProject(camera, events, poster), folder);
        return (folder, Path.Combine(folder, name + ".tinyclips"));
    }

    /// <summary>Saves a project as a folder, to be given up when the test run is.</summary>
    protected void Save(string projectId, string folder, bool replaceSavedProject = false, IProgress<double>? progress = null) =>
        Store.SaveProjectFolder(projectId, folder, replaceSavedProject, progress, TestContext.Current.CancellationToken);

    /// <summary>Opens a project file or the folder that holds one, to be given up when the test run is.</summary>
    protected StudioProject Open(string path, IProgress<double>? progress = null) =>
        Store.OpenProjectFolder(path, progress, TestContext.Current.CancellationToken);

    /// <summary>The names of everything in a folder, files and folders, in the order of their characters.</summary>
    protected static string[] Names(string directory) =>
        Directory.EnumerateFileSystemEntries(directory)
            .Select(entry => Path.GetFileName(entry))
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Changes a project file as JSON and writes it back.</summary>
    protected static void EditProjectFile(string file, Action<JsonObject> edit)
    {
        var project = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        edit(project);
        File.WriteAllText(file, project.ToJsonString());
    }

    /// <summary>Makes a symbolic link, or skips the test where this account may not make one.</summary>
    protected static void MakeLinkOrSkip(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This account may not make a symbolic link, so there is none to refuse: {ex.Message}");
        }
    }

    /// <summary>Runs what should be refused, and returns the refusal after checking its kind.</summary>
    protected static StudioProjectFolderException AssertRefused(StudioProjectFolderProblem problem, Action action)
    {
        var refusal = Assert.Throws<StudioProjectFolderException>(action);
        Assert.Equal(problem, refusal.Problem);
        return refusal;
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            // A test may have left a file that may only be read, which cannot be deleted as it is.
            foreach (var entry in new DirectoryInfo(_root).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                }
            }

            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }
}

/// <summary>
/// The tests of <c>mac/TinyClipsTests/StudioProjectFolderTests.swift</c>, one for one and under
/// the same names. What only Windows has to answer for is in
/// <see cref="StudioProjectFolderWindowsTests"/>.
/// </summary>
public sealed class StudioProjectFolderTests : StudioProjectFolderTestBase
{
    // Saving a folder

    [Fact]
    public void AFolderHasTheRecordingsAndAProjectFileNamedAfterIt()
    {
        var id = MakeProject(camera: true, events: true, poster: true);
        var folder = Path.Combine(Outside, "My Demo");

        Save(id, folder);

        Assert.Equal(["My Demo.tinyclips", "camera.mp4", "events.json", "poster.jpg", "screen.mp4"], Names(folder));
        Assert.Equal("screen", File.ReadAllText(Path.Combine(folder, "screen.mp4")));
        Assert.Equal("camera", File.ReadAllText(Path.Combine(folder, "camera.mp4")));
    }

    [Fact]
    public void AProjectWithoutACameraHasNoCameraFileInItsFolder()
    {
        var id = MakeProject(camera: false);
        var folder = Path.Combine(Outside, "Screen Only");

        Save(id, folder);

        Assert.Equal(["Screen Only.tinyclips", "screen.mp4"], Names(folder));
    }

    [Fact]
    public void TheProjectFileLeavesOutWhereVideosWereExportedAndTheStoreKeepsIt()
    {
        var id = MakeProject(camera: true);
        var exported = Path.Combine(Outside, "someone", "Movies", "demo.mp4");
        Store.RecordExport(id, exported);
        var folder = Path.Combine(Outside, "Copy");

        Save(id, folder);

        var text = File.ReadAllText(Path.Combine(folder, "Copy.tinyclips"));
        Assert.DoesNotContain("someone", text);
        Assert.Empty(StudioProjectJson.ReadProject(text).Exports);
        Assert.Equal([exported], Store.Load(id).Exports.Select(export => export.Path));
    }

    [Fact]
    public void ABackgroundImageGoesWithTheProject()
    {
        var id = MakeProject(camera: false);
        var project = Store.Load(id);
        Store.Save(project with
        {
            Canvas = project.Canvas with
            {
                Background = project.Canvas.Background with { Style = StudioBackgroundStyle.Image, Image = "backdrop.png" },
            },
        });
        File.WriteAllText(Path.Combine(Store.GetPaths(id).ProjectDirectory, "backdrop.png"), "image");
        var folder = Path.Combine(Outside, "Pictured");

        Save(id, folder);
        var opened = Open(Path.Combine(folder, "Pictured.tinyclips"));

        Assert.Contains("backdrop.png", Names(folder));
        Assert.Equal("image", File.ReadAllText(Path.Combine(Store.GetPaths(opened.Id).ProjectDirectory, "backdrop.png")));
    }

    [Fact]
    public void SavingOverSomethingThatIsNotASavedProjectIsRefusedAndLeavesItAlone()
    {
        var id = MakeProject(camera: false);
        var folder = Path.Combine(Outside, "Taken");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "keep.txt"), "mine");
        var file = Path.Combine(Outside, "A File");
        File.WriteAllText(file, "mine");

        foreach (var replacing in new[] { false, true })
        {
            AssertRefused(StudioProjectFolderProblem.DestinationExists, () => Save(id, folder, replacing));
            AssertRefused(StudioProjectFolderProblem.DestinationExists, () => Save(id, file, replacing));
        }

        Assert.Equal(["keep.txt"], Names(folder));
        Assert.Equal("mine", File.ReadAllText(file));

        // Windows fills a folder beside where it will be. Nothing of that is left either.
        Assert.Equal(["A File", "Taken"], Names(Outside));
    }

    [Fact]
    public void ASavedProjectIsReplacedOnlyWhenAskedTo()
    {
        var first = MakeProject(camera: true, events: true);
        var second = MakeProject(camera: false);
        Store.Save(Store.Load(second) with { Name = "Second" });
        var folder = Path.Combine(Outside, "Shared Name");
        Save(first, folder);

        AssertRefused(StudioProjectFolderProblem.DestinationExists, () => Save(second, folder));
        Assert.Contains("camera.mp4", Names(folder));

        Save(second, folder, replaceSavedProject: true);

        // What the first project had and the second does not is gone with the old folder.
        Assert.Equal(["Shared Name.tinyclips", "screen.mp4"], Names(folder));
        var written = StudioProjectJson.ReadProject(File.ReadAllText(Path.Combine(folder, "Shared Name.tinyclips")));
        Assert.Equal("Second", written.Name);
        Assert.Equal(["Shared Name"], Names(Outside));
    }

    [Fact]
    public void AFolderIsASavedProjectWhenItHasExactlyOneProjectFile()
    {
        var id = MakeProject(camera: false);
        var folder = Path.Combine(Outside, "Saved");
        Save(id, folder);

        Assert.True(StudioProjectFolder.IsSavedProjectFolder(folder));
        Assert.False(StudioProjectFolder.IsSavedProjectFolder(Outside));
        Assert.False(StudioProjectFolder.IsSavedProjectFolder(Path.Combine(folder, "Saved.tinyclips")));
        Assert.False(StudioProjectFolder.IsSavedProjectFolder(Path.Combine(Outside, "Nowhere")));
    }

    [Fact]
    public void AProjectWhoseRecordingIsGoneIsNotSavedAndLeavesNoFolder()
    {
        var id = MakeProject(camera: true);
        File.Delete(Store.GetPaths(Store.Load(id)).CameraPath!);
        var folder = Path.Combine(Outside, "Half");

        var refusal = AssertRefused(StudioProjectFolderProblem.MissingFile, () => Save(id, folder));

        Assert.Equal("camera.mp4", refusal.FileName);
        Assert.False(Directory.Exists(folder));
        Assert.Empty(Names(Outside));
    }

    [Fact]
    public void AProjectAroundAVideoKeptElsewhereIsNotSavedAsAFolder()
    {
        var video = Path.Combine(Outside, "finished.mp4");
        File.WriteAllText(video, "video");
        var flat = Store.GetOrCreateFlatProject(video, new StudioRecordingSourceInfo(1920, 1080, 5), "1.0");

        AssertRefused(StudioProjectFolderProblem.ExternalSource, () => Save(flat.Id, Path.Combine(Outside, "Flat")));

        Assert.Equal(["finished.mp4"], Names(Outside));
    }

    // Opening a folder

    [Fact]
    public void AFolderOpensAsANewProjectWithTheSameEditsAndNoExports()
    {
        var id = MakeProject(camera: true, events: true);
        var project = Store.Load(id) with
        {
            Name = "Round Trip",
            Zooms = [new StudioZoom { Start = 1, End = 3, Scale = 2 }],
            KeepSources = true,
        };
        project = project with { Edits = project.Edits with { Cuts = [new StudioTimeRange { Start = 4, End = 5 }] } };
        using (var later = JsonDocument.Parse("\"kept\""))
        {
            project.ExtensionData["fromALaterVersion"] = later.RootElement.Clone();
        }

        Store.Save(project);
        Store.RecordExport(id, Path.Combine(Outside, "someone", "Movies", "demo.mp4"));
        var folder = Path.Combine(Outside, "Round Trip");
        Save(id, folder);

        var opened = Open(Path.Combine(folder, "Round Trip.tinyclips"));

        Assert.NotEqual(id, opened.Id);
        Assert.Equal("Round Trip", opened.Name);
        Assert.Equal([(1d, 3d, 2d)], opened.Zooms.Select(zoom => (zoom.Start, zoom.End, zoom.Scale)));
        Assert.Equal([(4d, 5d)], opened.Edits.Cuts.Select(cut => (cut.Start, cut.End)));
        Assert.Equal("kept", opened.ExtensionData["fromALaterVersion"].GetString());
        Assert.Empty(opened.Exports);

        var onDisk = Store.Load(opened.Id);
        Assert.Equal(opened.Id, onDisk.Id);
        Assert.Equal([(1d, 3d, 2d)], onDisk.Zooms.Select(zoom => (zoom.Start, zoom.End, zoom.Scale)));
        var paths = Store.GetPaths(onDisk);
        Assert.Equal("screen", File.ReadAllText(paths.ScreenPath));
        Assert.Equal("camera", File.ReadAllText(paths.CameraPath!));
        Assert.Equal("{}", File.ReadAllText(paths.EventsPath));

        var summary = Assert.Single(Store.ListSummaries(), summary => summary.Id == opened.Id);
        Assert.True(summary.IsDraft);
        Assert.False(summary.IsRemovableByCleanup);

        // The project it was saved from is as it was.
        Assert.Single(Store.Load(id).Exports);
    }

    [Fact]
    public void OpeningTheSameFileTwiceMakesTwoProjectsAndLeavesTheFolderAlone()
    {
        var id = MakeProject(camera: false);
        var folder = Path.Combine(Outside, "Twice");
        Save(id, folder);
        var before = Names(folder);
        var file = Path.Combine(folder, "Twice.tinyclips");
        var textBefore = File.ReadAllBytes(file);

        var first = Open(file);
        var second = Open(file);

        Assert.Equal(3, new[] { id, first.Id, second.Id }.Distinct().Count());
        Assert.Equal(3, Store.ListSummaries().Count);
        Assert.Equal(before, Names(folder));
        Assert.Equal(textBefore, File.ReadAllBytes(file));
    }

    [Fact]
    public void AProjectFileWithoutItsScreenRecordingIsRefusedAndAddsNothingToTheStore()
    {
        var id = MakeProject(camera: true);
        var folder = Path.Combine(Outside, "Alone");
        Save(id, folder);
        File.Delete(Path.Combine(folder, "screen.mp4"));

        var refusal = AssertRefused(
            StudioProjectFolderProblem.MissingFile,
            () => Open(Path.Combine(folder, "Alone.tinyclips")));

        Assert.Equal("screen.mp4", refusal.FileName);
        Assert.Equal([id], Names(StoreRoot));
    }

    [Fact]
    public void AProjectFileWithoutTheCameraRecordingItNamesIsRefused()
    {
        var id = MakeProject(camera: true);
        var folder = Path.Combine(Outside, "No Camera");
        Save(id, folder);
        File.Delete(Path.Combine(folder, "camera.mp4"));

        var refusal = AssertRefused(
            StudioProjectFolderProblem.MissingFile,
            () => Open(Path.Combine(folder, "No Camera.tinyclips")));

        Assert.Equal("camera.mp4", refusal.FileName);
        Assert.Equal([id], Names(StoreRoot));
    }

    [Fact]
    public void AProjectFileCannotNameAFileOutsideItsFolder()
    {
        var id = MakeProject(camera: false);
        var folder = Path.Combine(Outside, "Reaching");
        Save(id, folder);
        File.WriteAllText(Path.Combine(Outside, "secret.mp4"), "secret");
        var file = Path.Combine(folder, "Reaching.tinyclips");
        var text = File.ReadAllText(file);
        Assert.Contains("\"screen.mp4\"", text);
        File.WriteAllText(file, text.Replace("\"screen.mp4\"", "\"../secret.mp4\""));

        var refusal = AssertRefused(StudioProjectFolderProblem.FileOutsideFolder, () => Open(file));

        Assert.Equal("sources.screen.file", refusal.Property);
        Assert.Equal([id], Names(StoreRoot));
    }

    [Fact]
    public void AProjectFileCannotNameAnEventsFileOutsideItsFolder()
    {
        var id = MakeProject(camera: false, events: true);
        var folder = Path.Combine(Outside, "Events");
        Save(id, folder);
        var file = Path.Combine(folder, "Events.tinyclips");
        var text = File.ReadAllText(file);
        Assert.Contains("\"events.json\"", text);
        File.WriteAllText(file, text.Replace("\"events.json\"", "\"../events.json\""));

        var refusal = AssertRefused(StudioProjectFolderProblem.FileOutsideFolder, () => Open(file));

        Assert.Equal("sources.events", refusal.Property);
        Assert.Equal([id], Names(StoreRoot));
    }

    [Fact]
    public void AProjectFileFromALaterVersionIsRefused()
    {
        var id = MakeProject(camera: false);
        var folder = Path.Combine(Outside, "Later");
        Save(id, folder);
        var file = Path.Combine(folder, "Later.tinyclips");
        var text = File.ReadAllText(file);
        Assert.Contains("\"schemaVersion\": 1", text);
        File.WriteAllText(file, text.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"));

        var refusal = AssertRefused(StudioProjectFolderProblem.NewerVersion, () => Open(file));

        Assert.Equal(2, refusal.SchemaVersion);
        Assert.Equal([id], Names(StoreRoot));
    }

    // Finding the project file

    [Fact]
    public void AProjectFileIsItselfAndAFolderIsTheOneProjectFileInIt()
    {
        var id = MakeProject(camera: false);
        var folder = Path.Combine(Outside, "Found");
        Save(id, folder);
        var file = Path.Combine(folder, "Found.tinyclips");

        Assert.Equal("Found.tinyclips", Path.GetFileName(StudioProjectFolder.FindProjectFile(file)));
        Assert.Equal("Found.tinyclips", Path.GetFileName(StudioProjectFolder.FindProjectFile(folder)));
    }

    [Fact]
    public void WhatIsNotAProjectFileOrAFolderWithExactlyOneIsRefused()
    {
        var id = MakeProject(camera: false);
        var folder = Path.Combine(Outside, "Crowded");
        Save(id, folder);
        var screen = Path.Combine(folder, "screen.mp4");

        foreach (var path in new[] { screen, Outside, Path.Combine(Outside, "nothing.tinyclips") })
        {
            AssertRefused(StudioProjectFolderProblem.NotAProjectFile, () => StudioProjectFolder.FindProjectFile(path));
        }

        File.WriteAllText(Path.Combine(folder, "Second.tinyclips"), "{}");
        AssertRefused(StudioProjectFolderProblem.NotAProjectFile, () => StudioProjectFolder.FindProjectFile(folder));
    }

    [Fact]
    public void AFolderNameIsTheProjectsNameWithoutWhatAPathCannotHold()
    {
        Assert.Equal("Demo recording", StudioProjectFolder.FolderName("Demo recording"));
        Assert.Equal("a-b-c-d", StudioProjectFolder.FolderName("  a/b:c\\d  "));
        Assert.Equal("two-lines", StudioProjectFolder.FolderName("two\nlines"));
        Assert.Equal("hidden", StudioProjectFolder.FolderName("..hidden"));
        Assert.Equal("Tiny Clips Project", StudioProjectFolder.FolderName(""));
        Assert.Equal("Tiny Clips Project", StudioProjectFolder.FolderName(" . "));
    }

    // Open recent

    [Fact]
    public void RecentProjectsAreTheLastOpenedFirstWithoutTheCurrentOneOrThoseWithoutARecording()
    {
        static StudioProjectSummary Summary(string id, long opened, bool sourceExists = true) =>
            new(
                id,
                id,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.FromUnixTimeSeconds(opened),
                IsDraft: true,
                IsFlat: false,
                KeepSources: false,
                SizeBytes: 0,
                SourceExists: sourceExists);

        StudioProjectSummary[] summaries =
        [
            Summary("old", opened: 10),
            Summary("current", opened: 99),
            Summary("gone", opened: 50, sourceExists: false),
            Summary("b-tied", opened: 30),
            Summary("new", opened: 40),
            Summary("a-tied", opened: 30),
        ];

        Assert.Equal(
            ["new", "a-tied", "b-tied", "old"],
            StudioProjectSummary.Recent(summaries, excludingId: "current", limit: 10).Select(summary => summary.Id));
        Assert.Equal(
            ["new", "a-tied"],
            StudioProjectSummary.Recent(summaries, excludingId: "current", limit: 2).Select(summary => summary.Id));
        Assert.Equal(
            ["current"],
            StudioProjectSummary.Recent(summaries, excludingId: null, limit: 1).Select(summary => summary.Id));
        Assert.Empty(StudioProjectSummary.Recent(summaries, excludingId: null, limit: 0));
        Assert.Empty(StudioProjectSummary.Recent([], excludingId: "current", limit: 5));
    }

    // Recent captures

    [Fact]
    public void TheMenuListsProjectsThatHoldTheOnlyCopyOfARecordingThatCanBeOpened()
    {
        StudioProjectSummary[] summaries =
        [
            MenuSummary("draft"),
            MenuSummary("exported", isDraft: false),
            MenuSummary("lost-its-export", isDraft: false, exportMissing: true),
            MenuSummary("no-recording", sourceExists: false),
            MenuSummary("flat", isFlat: true),
        ];

        Assert.Equal(["draft", "lost-its-export"], StudioProjectSummary.MenuDrafts(summaries).Select(summary => summary.Id));
    }

    [Fact]
    public void AProjectWasLastUsedWhenItWasOpenedOrFailingThatRecorded()
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(100), MenuSummary("a", created: 100, opened: 0).LastUsedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(250), MenuSummary("b", created: 100, opened: 250).LastUsedAt);
    }

    [Fact]
    public void RecentCapturesAndProjectsAreMixedByDateNewestFirst()
    {
        static RecentCapture Capture(string name, long at) =>
            new($"/tmp/{name}", CaptureType.Video, DateTimeOffset.FromUnixTimeSeconds(at));

        RecentCapture[] captures = [Capture("c3", at: 300), Capture("c2", at: 200), Capture("c1", at: 100)];
        StudioProjectSummary[] drafts = [MenuSummary("d-new", created: 250), MenuSummary("d-tied", created: 200), MenuSummary("d-old", created: 50)];

        var merged = RecentMenuEntry.Merged(captures, drafts, limit: 5);

        Assert.Equal(
            ["capture:/tmp/c3", "studio:d-new", "capture:/tmp/c2", "studio:d-tied", "capture:/tmp/c1"],
            merged.Select(entry => entry.Id));
        Assert.Equal(["capture:/tmp/c3", "studio:d-new"], RecentMenuEntry.Merged(captures, drafts, limit: 2).Select(entry => entry.Id));
        Assert.Equal(["studio:d-new", "studio:d-tied", "studio:d-old"], RecentMenuEntry.Merged([], drafts, limit: 5).Select(entry => entry.Id));
        Assert.Equal(3, RecentMenuEntry.Merged(captures, [], limit: 5).Count);
        Assert.Empty(RecentMenuEntry.Merged(captures, drafts, limit: 0));
    }

    private static StudioProjectSummary MenuSummary(
        string id,
        long created = 0,
        long opened = 0,
        bool isDraft = true,
        bool isFlat = false,
        bool sourceExists = true,
        bool exportMissing = false) =>
        new(
            id,
            id,
            DateTimeOffset.FromUnixTimeSeconds(created),
            DateTimeOffset.FromUnixTimeSeconds(opened),
            isDraft,
            isFlat,
            KeepSources: false,
            SizeBytes: 0,
            ExportMissing: exportMissing,
            SourceExists: sourceExists);
}
