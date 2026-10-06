using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioProjectStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TinyClipsTests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 22, 41, 0, TimeSpan.Zero));

    public static TheoryData<string, Action<StudioProjectStore, string>> IdValidatingMethods => new()
    {
        { "CompleteRecording", (store, id) => store.CompleteRecording(id, CreateRequest()) },
        { "GetPathsId", (store, id) => store.GetPaths(id) },
        { "GetPathsProject", (store, id) => store.GetPaths(new StudioProject { Id = id }) },
        { "Exists", (store, id) => store.Exists(id) },
        { "Load", (store, id) => store.Load(id) },
        { "Save", (store, id) => store.Save(Project(id)) },
        { "Delete", (store, id) => store.Delete(id) },
        { "MarkOpened", (store, id) => store.MarkOpened(id) },
        { "SetKeepSources", (store, id) => store.SetKeepSources(id, true) },
        { "FindScreenRecording", (store, id) => store.FindScreenRecording(id) },
        { "RecordExport", (store, id) => store.RecordExport(id, Path.Combine(Path.GetTempPath(), "export.mp4")) },
        { "LoadEvents", (store, id) => store.LoadEvents(id) },
        { "SaveEvents", (store, id) => store.SaveEvents(id, new StudioEvents()) },
        { "CleanupInUse", (store, id) => store.Cleanup(inUseProjectIds: [id]) },
    };

    [Theory]
    [MemberData(nameof(IdValidatingMethods))]
    public void PublicProjectIdMethods_RejectTraversalAndRootedIds(string methodName, Action<StudioProjectStore, string> invoke)
    {
        var store = CreateStore();

        Assert.False(string.IsNullOrWhiteSpace(methodName));
        Assert.Throws<ArgumentException>(() => invoke(store, "..\\..\\x"));
        Assert.Throws<ArgumentException>(() => invoke(store, Path.Combine(Path.GetPathRoot(_directory)!, "evil")));
        Assert.Throws<ArgumentException>(() => invoke(store, Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void BeginRecordingCreatesFolderWithoutProjectThenCompleteRecordingWritesDefaultProject()
    {
        var store = CreateStore();
        var paths = store.BeginRecording();

        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", paths.ProjectId);
        Assert.True(Directory.Exists(paths.ProjectDirectory));
        Assert.False(File.Exists(paths.ProjectJsonPath));
        Assert.Equal(Path.Combine(paths.ProjectDirectory, "screen.mp4"), paths.ScreenPath);
        Assert.Equal(Path.Combine(paths.ProjectDirectory, "camera.mp4"), paths.CameraPath);
        Assert.Equal(Path.Combine(paths.ProjectDirectory, "events.json"), paths.EventsPath);
        Assert.Equal(Path.Combine(paths.ProjectDirectory, "poster.jpg"), paths.PosterPath);

        var project = store.CompleteRecording(paths.ProjectId, CreateRequest());

        Assert.True(File.Exists(paths.ProjectJsonPath));
        Assert.Equal(paths.ProjectId, project.Id);
        Assert.Equal(StudioLayout.Bubble, project.Scenes[0].Layout);
        Assert.Equal(0.25, project.Edits.TrimStart);
        Assert.True(project.Overlays.Branding);
    }

    [Fact]
    public void LoadAndList_UseFolderNameAsIdAndIgnoreNonIdFolders()
    {
        var store = CreateStore();
        var folderId = ValidId("00000000-0000-0000-0000-000000000001");
        var hostileJsonId = ValidId("00000000-0000-0000-0000-000000000002");
        WriteProject(folderId, Project(hostileJsonId));

        var ignoredDirectory = Path.Combine(_directory, "not-a-project-id");
        Directory.CreateDirectory(ignoredDirectory);
        File.WriteAllText(Path.Combine(ignoredDirectory, StudioProjectStore.ProjectFileName), StudioProjectJson.WriteProject(Project(hostileJsonId)));

        var loaded = store.Load(folderId);
        var summary = Assert.Single(store.ListSummaries());

        Assert.Equal(folderId, loaded.Id);
        Assert.Equal(folderId, summary.Id);

        store.Cleanup(new StudioCleanupOptions(RetentionDays: 1, SizeCapBytes: 1));
        Assert.True(Directory.Exists(ignoredDirectory));
    }

    [Fact]
    public void SaveUpdatesModifiedAtAndMarkOpenedLeavesModifiedAtUnchanged()
    {
        var store = CreateStore();
        var project = CreateCompletedProject(store);

        _time.Advance(TimeSpan.FromMinutes(1));
        var saved = store.Save(project with { Name = "Edited" });

        Assert.Equal(_time.GetUtcNow(), saved.ModifiedAt);

        _time.Advance(TimeSpan.FromMinutes(1));
        var opened = store.MarkOpened(project.Id);

        Assert.Equal(_time.GetUtcNow(), opened.LastOpenedAt);
        Assert.Equal(saved.ModifiedAt, opened.ModifiedAt);
    }

    [Fact]
    public void DeleteRemovesOnlyValidatedProjectFolder()
    {
        var store = CreateStore();
        var project = CreateCompletedProject(store);

        Assert.True(store.Exists(project.Id));

        store.Delete(project.Id);

        Assert.False(store.Exists(project.Id));
        Assert.Empty(store.ListSummaries());
    }

    [Fact]
    public void GetPaths_ReturnsNormalAndFlatProjectPaths()
    {
        var store = CreateStore();
        var project = CreateCompletedProject(store);
        var normal = store.GetPaths(project);

        Assert.Equal(Path.Combine(normal.ProjectDirectory, "screen.mp4"), normal.ScreenPath);
        Assert.Equal(Path.Combine(normal.ProjectDirectory, "camera.mp4"), normal.CameraPath);
        Assert.Equal(Path.Combine(normal.ProjectDirectory, "events.json"), normal.EventsPath);
        Assert.Equal(Path.Combine(normal.ProjectDirectory, "poster.jpg"), normal.PosterPath);

        var videoPath = Path.Combine(_directory, "external.mp4");
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(videoPath, [1, 2, 3]);
        var flat = store.GetOrCreateFlatProject(videoPath, new StudioRecordingSourceInfo(640, 360, 12, 24), "1.9.0");
        var flatPaths = store.GetPaths(flat);

        Assert.Equal(Path.GetFullPath(videoPath), flatPaths.ScreenPath);
        Assert.Null(flatPaths.CameraPath);
        Assert.Equal(Path.Combine(flatPaths.ProjectDirectory, "events.json"), flatPaths.EventsPath);
    }

    [Fact]
    public void ExportIndex_ReplacesStealsUpdatesRemovesAndRebuilds()
    {
        var store = CreateStore();
        var first = CreateCompletedProject(store);
        var second = CreateCompletedProject(store);
        var exportPath = Path.Combine(_directory, "Exports", "Clip.MP4");
        var movedPath = Path.Combine(_directory, "Exports", "Moved.mp4");

        var firstExport = store.RecordExport(first.Id, exportPath);
        _time.Advance(TimeSpan.FromMinutes(1));
        var replaced = store.RecordExport(first.Id, exportPath.ToLowerInvariant());
        var stolen = store.RecordExport(second.Id, exportPath);

        Assert.Single(firstExport.Exports);
        Assert.Single(replaced.Exports);
        Assert.Empty(store.Load(first.Id).Exports);
        Assert.Single(stolen.Exports);
        Assert.Equal(second.Id, store.FindProjectIdByExportPath(exportPath.ToLowerInvariant()));
        Assert.True(store.UpdateExportPath(exportPath.ToUpperInvariant(), movedPath));
        Assert.Null(store.FindProjectIdByExportPath(exportPath));
        Assert.Equal(second.Id, store.FindProjectIdByExportPath(movedPath.ToUpperInvariant()));

        var reopened = CreateStore();
        Assert.Equal(second.Id, reopened.FindProjectIdByExportPath(movedPath.ToLowerInvariant()));

        Assert.True(reopened.RemoveExportPath(movedPath));
        Assert.Null(reopened.FindProjectIdByExportPath(movedPath));
    }

    [Fact]
    public void UpdateExportPath_TakesTheNewPathFromAnyOtherProject()
    {
        var store = CreateStore();
        var first = CreateCompletedProject(store);
        var second = CreateCompletedProject(store);
        var firstPath = Path.Combine(_directory, "Exports", "First.mp4");
        var secondPath = Path.Combine(_directory, "Exports", "Second.mp4");
        store.RecordExport(first.Id, firstPath);
        store.RecordExport(second.Id, secondPath);

        // Moving the first export onto the second one's path overwrites that file.
        Assert.True(store.UpdateExportPath(firstPath, secondPath));

        Assert.Equal(first.Id, store.FindProjectIdByExportPath(secondPath));
        Assert.Null(store.FindProjectIdByExportPath(firstPath));
        Assert.Single(store.Load(first.Id).Exports);
        Assert.Empty(store.Load(second.Id).Exports);
        Assert.Equal(first.Id, CreateStore().FindProjectIdByExportPath(secondPath));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("..\\outside.mp4")]
    [InlineData("C:\\outside.mp4")]
    public void GetPaths_RejectsSourceFileNamesThatLeaveTheProjectFolder(string fileName)
    {
        var store = CreateStore();
        var project = CreateCompletedProject(store);
        var sources = project.Sources;

        Assert.Throws<StudioProjectInvalidException>(() => store.GetPaths(
            project with { Sources = sources with { Screen = sources.Screen with { File = fileName } } }));
        Assert.Throws<StudioProjectInvalidException>(() => store.GetPaths(
            project with { Sources = sources with { Camera = sources.Camera! with { File = fileName } } }));
        Assert.Throws<StudioProjectInvalidException>(() => store.GetPaths(
            project with { Sources = sources with { Events = fileName } }));
    }

    [Fact]
    public void GetOrCreateFlatProject_ReturnsOneProjectPerExternalVideoPathWithCallerMetadata()
    {
        var store = CreateStore();
        var videoPath = Path.Combine(_directory, "external.mp4");
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(videoPath, [1, 2, 3]);

        var first = store.GetOrCreateFlatProject(videoPath.ToUpperInvariant(), new StudioRecordingSourceInfo(640, 360, 12, 24), "1.9.0");
        var second = store.GetOrCreateFlatProject(videoPath.ToLowerInvariant(), new StudioRecordingSourceInfo(1, 1, 0), "1.9.0");

        Assert.Equal(first.Id, second.Id);
        Assert.True(first.Sources.Screen.External);
        Assert.Equal(Path.GetFullPath(videoPath.ToUpperInvariant()), first.Sources.Screen.File);
        Assert.Equal(640, first.Sources.Screen.Width);
        Assert.Equal(360, first.Sources.Screen.Height);
        Assert.Equal(12, first.Sources.Screen.Duration);
        Assert.Equal(24, first.Sources.Screen.FrameRate);
        Assert.Null(first.Sources.Camera);
        Assert.Null(first.Sources.Events);
        Assert.True(store.ListSummaries().Single(summary => summary.Id == first.Id).IsFlat);
    }

    [Fact]
    public void Events_LoadMissingDefaultsAndSaveRoundTrips()
    {
        var store = CreateStore();
        var project = CreateCompletedProject(store);

        var defaults = store.LoadEvents(project.Id);
        Assert.Equal(1, defaults.SchemaVersion);
        Assert.Equal(1, defaults.Capture.Scale);
        Assert.Empty(defaults.Clicks);
        Assert.Empty(defaults.Cursor);
        Assert.Empty(defaults.CameraCorners);
        Assert.Empty(defaults.Markers);

        var events = new StudioEvents
        {
            Capture = new StudioCaptureInfo { Width = 100, Height = 50, Scale = 2, Kind = StudioCaptureKind.Region },
            Clicks = [new StudioClickEvent { T = 1.2, X = 0.3, Y = 0.4, Button = StudioMouseButton.Right }],
            Cursor = [new StudioCursorSample { T = 0, X = 0.5, Y = 0.6 }],
            CameraCorners = [new StudioCameraCornerEvent { T = 0, Corner = StudioAnchor.TopLeft }],
        };

        store.SaveEvents(project.Id, events);
        var loaded = store.LoadEvents(project.Id);

        Assert.Equal(100, loaded.Capture.Width);
        Assert.Equal(StudioCaptureKind.Region, loaded.Capture.Kind);
        Assert.Equal(StudioMouseButton.Right, loaded.Clicks.Single().Button);
        Assert.Equal(StudioAnchor.TopLeft, loaded.CameraCorners.Single().Corner);
    }

    [Fact]
    public void Cleanup_DeletesOldUnfinishedRecordingsButKeepsYoungAndInUse()
    {
        var store = CreateStore();
        var young = store.BeginRecording();
        var old = store.BeginRecording();
        var inUseOld = store.BeginRecording();
        Directory.SetCreationTimeUtc(young.ProjectDirectory, (_time.GetUtcNow() - TimeSpan.FromHours(23)).UtcDateTime);
        Directory.SetCreationTimeUtc(old.ProjectDirectory, (_time.GetUtcNow() - TimeSpan.FromHours(25)).UtcDateTime);
        Directory.SetCreationTimeUtc(inUseOld.ProjectDirectory, (_time.GetUtcNow() - TimeSpan.FromHours(25)).UtcDateTime);

        var result = store.Cleanup(inUseProjectIds: [inUseOld.ProjectId]);

        Assert.Equal([old.ProjectId], result.ProjectIdsDeleted);
        Assert.True(Directory.Exists(young.ProjectDirectory));
        Assert.False(Directory.Exists(old.ProjectDirectory));
        Assert.True(Directory.Exists(inUseOld.ProjectDirectory));
    }

    [Fact]
    public void Cleanup_LeavesUnreadableProjectJsonAloneAndContinuesAfterLockedFolder()
    {
        var store = CreateStore();
        var locked = CreateCompletedProject(store);
        var deletable = CreateCompletedProject(store);
        store.RecordExport(locked.Id, WriteExportedVideo("locked"));
        store.RecordExport(deletable.Id, WriteExportedVideo("deletable"));
        store.Save(store.Load(locked.Id) with { LastOpenedAt = _time.GetUtcNow() - TimeSpan.FromDays(90) });
        store.Save(store.Load(deletable.Id) with { LastOpenedAt = _time.GetUtcNow() - TimeSpan.FromDays(90) });
        var lockedPath = Path.Combine(store.GetPaths(locked.Id).ProjectDirectory, "locked.bin");
        File.WriteAllBytes(lockedPath, [1]);

        var unreadableId = ValidId("00000000-0000-0000-0000-000000000099");
        var unreadableDirectory = Path.Combine(_directory, unreadableId);
        Directory.CreateDirectory(unreadableDirectory);
        File.WriteAllText(Path.Combine(unreadableDirectory, StudioProjectStore.ProjectFileName), "{");

        using var stream = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = store.Cleanup(new StudioCleanupOptions(RetentionDays: 30, SizeCapBytes: 0));

        Assert.Contains(deletable.Id, result.ProjectIdsDeleted);
        Assert.True(Directory.Exists(store.GetPaths(locked.Id).ProjectDirectory));
        Assert.True(Directory.Exists(unreadableDirectory));
    }

    [Fact]
    public void Cleanup_LeavesAProjectThatCameIntoUseAfterTheListOfThoseInUseWasMade()
    {
        var store = CreateStore();
        var opened = CreateOldExportedProject(store, "opened");
        var unused = CreateOldExportedProject(store, "unused");

        // The list is from before the cleanup started, and names nothing. One of the two has
        // been opened in an editor since.
        var result = store.Cleanup(
            new StudioCleanupOptions(RetentionDays: 30, SizeCapBytes: 0),
            inUseProjectIds: [],
            isInUse: id => id == opened.Id);

        Assert.Equal([unused.Id], result.ProjectIdsDeleted);
        Assert.True(store.Exists(opened.Id));
        Assert.True(File.Exists(store.GetPaths(opened.Id).ScreenPath));
        Assert.False(Directory.Exists(store.GetPaths(unused.Id).ProjectDirectory));
    }

    [Fact]
    public void Cleanup_AsksAboutAProjectWhenItsTurnToBeDeletedHasCome_AndAboutNoOther()
    {
        var store = CreateStore();
        var first = CreateOldExportedProject(store, "first");
        var second = CreateOldExportedProject(store, "second");
        var draft = CreateCompletedProject(store);
        var asked = new List<string>();
        var otherWasStillThere = new List<bool>();

        var result = store.Cleanup(
            new StudioCleanupOptions(RetentionDays: 30, SizeCapBytes: 0),
            isInUse: id =>
            {
                var other = id == first.Id ? second.Id : first.Id;
                asked.Add(id);
                otherWasStillThere.Add(Directory.Exists(store.GetPaths(other).ProjectDirectory));
                return false;
            });

        // Asked one at a time, each when the one before it is already gone: an answer given for
        // all of them beforehand would be as old as the list is.
        Assert.Equal(2, result.DeletedProjectCount);
        Assert.Equal(2, asked.Count);
        Assert.Contains(first.Id, asked);
        Assert.Contains(second.Id, asked);
        Assert.DoesNotContain(draft.Id, asked);
        Assert.Equal(new[] { true, false }, otherWasStillThere);
        Assert.True(store.Exists(draft.Id));
    }

    [Fact]
    public void Cleanup_AsksAboutARecordingThatWasNeverFinishedAsWell()
    {
        var store = CreateStore();
        var recording = store.BeginRecording();
        Directory.SetCreationTimeUtc(recording.ProjectDirectory, (_time.GetUtcNow() - TimeSpan.FromHours(25)).UtcDateTime);

        var whileRecording = store.Cleanup(isInUse: id => id == recording.ProjectId);

        Assert.Empty(whileRecording.ProjectIdsDeleted);
        Assert.True(Directory.Exists(recording.ProjectDirectory));

        var afterwards = store.Cleanup(isInUse: _ => false);

        Assert.Equal([recording.ProjectId], afterwards.ProjectIdsDeleted);
        Assert.False(Directory.Exists(recording.ProjectDirectory));
    }

    [Fact]
    public void Summary_SaysWhenEveryExportedVideoIsGone()
    {
        var store = CreateStore();
        var draft = CreateCompletedProject(store);
        var exported = CreateCompletedProject(store);
        var first = WriteExportedVideo("first");
        var second = WriteExportedVideo("second");
        store.RecordExport(exported.Id, first);
        store.RecordExport(exported.Id, second);

        StudioProjectSummary Summary(string id) => store.ListSummaries().Single(summary => summary.Id == id);

        Assert.True(Summary(draft.Id).IsDraft);
        Assert.False(Summary(draft.Id).ExportMissing);
        Assert.False(Summary(draft.Id).IsRemovableByCleanup);
        Assert.False(Summary(exported.Id).IsDraft);
        Assert.False(Summary(exported.Id).ExportMissing);
        Assert.True(Summary(exported.Id).IsRemovableByCleanup);

        // One of the two videos is enough for the recording to exist outside the project.
        File.Delete(first);
        Assert.False(Summary(exported.Id).ExportMissing);
        Assert.True(Summary(exported.Id).IsRemovableByCleanup);

        File.Delete(second);
        Assert.False(Summary(exported.Id).IsDraft);
        Assert.True(Summary(exported.Id).ExportMissing);
        Assert.False(Summary(exported.Id).IsRemovableByCleanup);

        // Nothing was written down: the project still names both, and is found by either.
        Assert.Equal(2, store.Load(exported.Id).Exports.Length);
        Assert.Equal(exported.Id, store.FindProjectIdByExportPath(first));
    }

    [Fact]
    public void Cleanup_KeepsAnExportedProjectWhoseVideoIsGone_AndTakesItOnceTheVideoIsBack()
    {
        var store = CreateStore();
        var project = CreateOldExportedProject(store, "only");
        var video = ExportedVideoPath("only");
        var rules = new StudioCleanupOptions(RetentionDays: 30, SizeCapBytes: 1);

        // The video was deleted, so the project is the one copy of the recording that is left.
        File.Delete(video);

        Assert.Empty(store.Cleanup(rules).ProjectIdsDeleted);
        Assert.True(File.Exists(store.GetPaths(project.Id).ScreenPath));

        // Back where it was saved, from the Recycle Bin or with the drive it is on.
        File.WriteAllBytes(video, [7, 8, 9]);

        Assert.Equal([project.Id], store.Cleanup(rules).ProjectIdsDeleted);
    }

    [Fact]
    public void RecordExport_WritesHowLargeTheVideoIs_WhereThereIsOne()
    {
        var store = CreateStore();
        var project = CreateCompletedProject(store);
        var there = WriteExportedVideo("there");
        var notThere = ExportedVideoPath("not-there");

        store.RecordExport(project.Id, there);
        var saved = store.RecordExport(project.Id, notThere);

        Assert.Equal(new long?[] { 3, null }, saved.Exports.Select(export => export.Bytes));
        Assert.Equal(new long?[] { 3, null }, CreateStore().Load(project.Id).Exports.Select(export => export.Bytes));

        // Where it is not known it is left out of the file.
        var json = File.ReadAllText(store.GetPaths(project.Id).ProjectJsonPath);
        Assert.Contains("\"bytes\": 3", json);
        Assert.Equal(json.IndexOf("\"bytes\"", StringComparison.Ordinal), json.LastIndexOf("\"bytes\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Summary_AFileOfAnotherSizeUnderTheVideosName_IsNotTheProjectsVideo()
    {
        var store = CreateStore();
        var project = CreateOldExportedProject(store, "reused");
        var video = ExportedVideoPath("reused");
        var rules = new StudioCleanupOptions(RetentionDays: 30, SizeCapBytes: 0);
        StudioProjectSummary Summary() => store.ListSummaries().Single();
        Assert.False(Summary().ExportMissing);
        Assert.True(Summary().IsRemovableByCleanup);

        // The video was deleted, and another recording was saved under its name.
        File.WriteAllBytes(video, [1, 2, 3, 4, 5]);

        Assert.True(Summary().ExportMissing);
        Assert.False(Summary().IsRemovableByCleanup);
        Assert.Empty(store.Cleanup(rules).ProjectIdsDeleted);
        Assert.True(store.Exists(project.Id));

        // The link from a video to its project goes by the path alone.
        Assert.Equal(project.Id, store.FindProjectIdByExportPath(video));

        // The project's own video again.
        File.WriteAllBytes(video, [7, 8, 9]);

        Assert.False(Summary().ExportMissing);
        Assert.Equal([project.Id], store.Cleanup(rules).ProjectIdsDeleted);
    }

    [Fact]
    public void Summary_AnExportThatDoesNotSayHowLargeItWas_GoesByThePathAlone()
    {
        var store = CreateStore();
        var project = CreateCompletedProject(store);
        var video = ExportedVideoPath("no-size");
        StudioProjectSummary Summary() => store.ListSummaries().Single();

        // No file is there yet, so no size is written: what a project from before sizes were
        // written has.
        store.RecordExport(project.Id, video);
        Assert.Null(Assert.Single(store.Load(project.Id).Exports).Bytes);
        Assert.True(Summary().ExportMissing);

        Directory.CreateDirectory(Path.GetDirectoryName(video)!);
        File.WriteAllBytes(video, [1]);
        Assert.False(Summary().ExportMissing);

        File.WriteAllBytes(video, [1, 2, 3, 4]);
        Assert.False(Summary().ExportMissing);
    }

    [Fact]
    public void UpdateExportPath_KeepsHowLargeTheVideoIs()
    {
        var store = CreateStore();
        var project = CreateCompletedProject(store);
        var video = WriteExportedVideo("moved");
        var movedTo = ExportedVideoPath("moved-to");
        store.RecordExport(project.Id, video);

        File.Move(video, movedTo);
        Assert.True(store.UpdateExportPath(video, movedTo));

        Assert.Equal(3, Assert.Single(store.Load(project.Id).Exports).Bytes);
        Assert.False(store.ListSummaries().Single().ExportMissing);
    }

    [Fact]
    public void Summary_AProjectThatDoesNotSayWhenItWasLastOpened_IsNotRemovable_UntilItIsOpened()
    {
        var store = CreateStore();
        var project = CreateOldExportedProject(store, "undated");
        var path = store.GetPaths(project.Id).ProjectJsonPath;

        // The time is taken out of the file, as a damaged or a hand-made one might be without it.
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(json.Remove("lastOpenedAt"));
        File.WriteAllText(path, json.ToJsonString());
        var reopened = CreateStore();

        var summary = reopened.ListSummaries().Single();
        Assert.Equal(DateTimeOffset.UnixEpoch, summary.LastOpenedAt);
        Assert.False(summary.ExportMissing);
        Assert.False(summary.IsRemovableByCleanup);
        Assert.Empty(reopened.Cleanup(new StudioCleanupOptions(RetentionDays: 1, SizeCapBytes: 1)).ProjectIdsDeleted);
        Assert.True(reopened.Exists(project.Id));

        // Opening it writes the time, and it is a project like any other from then on.
        reopened.MarkOpened(project.Id);

        Assert.True(reopened.ListSummaries().Single().IsRemovableByCleanup);
    }

    [Fact]
    public void Cleanup_StorageLimitLeavesTheProjectOpenedLast_HoweverLargeItIs()
    {
        var store = CreateStore();
        var project = CreateCompletedProject(store);
        File.WriteAllBytes(store.GetPaths(project.Id).ScreenPath, new byte[400_000]);
        store.RecordExport(project.Id, WriteExportedVideo("large"));
        var rules = new StudioCleanupOptions(RetentionDays: 0, SizeCapBytes: 250_000);
        Assert.True(store.ListSummaries().Single().IsRemovableByCleanup);

        // What the cleanup that follows its editor closing would have taken.
        Assert.Empty(store.Cleanup(rules).ProjectIdsDeleted);
        Assert.True(store.Exists(project.Id));
    }

    [Fact]
    public void Cleanup_StorageLimitCountsWhatCleanupMayRemove_SoDraftsCostNoExportedProjectItsSources()
    {
        var store = CreateStore();
        var rules = new StudioCleanupOptions(RetentionDays: 0, SizeCapBytes: 250_000);
        StudioProject CreateWithRecording(int bytes)
        {
            var project = CreateCompletedProject(store);
            File.WriteAllBytes(store.GetPaths(project.Id).ScreenPath, new byte[bytes]);
            return project;
        }

        StudioProject Export(StudioProject project, string name, int daysSinceOpened)
        {
            store.RecordExport(project.Id, WriteExportedVideo(name));
            return store.Save(store.Load(project.Id) with { LastOpenedAt = _time.GetUtcNow() - TimeSpan.FromDays(daysSinceOpened) });
        }

        // The drafts alone are over the limit, and so is the pinned project.
        var draft = CreateWithRecording(400_000);
        var pinned = Export(CreateWithRecording(400_000), "pinned", daysSinceOpened: 9);
        store.SetKeepSources(pinned.Id, true);
        var oldest = Export(CreateWithRecording(100_000), "oldest", daysSinceOpened: 3);
        var newer = Export(CreateWithRecording(100_000), "newer", daysSinceOpened: 2);

        // Two exported projects of 100,000 bytes are under 250,000. Counting everything, as the
        // limit once did, both would go: 1,000,000 bytes, and only they can be removed.
        Assert.Empty(store.Cleanup(rules).ProjectIdsDeleted);

        var newest = Export(CreateWithRecording(100_000), "newest", daysSinceOpened: 1);

        // Three are over it, and the one opened longest ago goes.
        Assert.Equal([oldest.Id], store.Cleanup(rules).ProjectIdsDeleted);
        Assert.True(store.Exists(draft.Id));
        Assert.True(store.Exists(pinned.Id));
        Assert.True(store.Exists(newer.Id));
        Assert.True(store.Exists(newest.Id));
    }

    [Fact]
    public void SetKeepSources_PinsTheProjectAndChangesNothingElse()
    {
        var store = CreateStore();
        var project = CreateOldExportedProject(store, "kept");
        var before = store.Load(project.Id);
        var rules = new StudioCleanupOptions(RetentionDays: 30, SizeCapBytes: 0);
        _time.Advance(TimeSpan.FromMinutes(5));

        var pinned = store.SetKeepSources(project.Id, true);

        Assert.True(pinned.KeepSources);
        Assert.True(store.Load(project.Id).KeepSources);
        Assert.Equal(before.ModifiedAt, pinned.ModifiedAt);
        Assert.Equal(before.LastOpenedAt, pinned.LastOpenedAt);
        Assert.Equal(
            StudioProjectJson.WriteProject(before with { KeepSources = true }),
            StudioProjectJson.WriteProject(store.Load(project.Id)));
        Assert.True(store.ListSummaries().Single().KeepSources);
        Assert.Empty(store.Cleanup(rules).ProjectIdsDeleted);

        // Asked for what it already is: the same project, nothing written.
        Assert.True(store.SetKeepSources(project.Id, true).KeepSources);

        Assert.False(store.SetKeepSources(project.Id, false).KeepSources);
        Assert.Equal([project.Id], store.Cleanup(rules).ProjectIdsDeleted);
    }

    [Fact]
    public void SetKeepSources_OfAProjectThatIsGone_Throws()
    {
        var store = CreateStore();
        var project = CreateCompletedProject(store);
        store.Delete(project.Id);

        Assert.ThrowsAny<IOException>(() => store.SetKeepSources(project.Id, true));
        Assert.False(Directory.Exists(store.GetPaths(project.Id).ProjectDirectory));
    }

    [Fact]
    public void FindScreenRecording_IsTheRecordingInTheProjectsOwnFolder()
    {
        var store = CreateStore();
        var project = CreateCompletedProject(store);
        var paths = store.GetPaths(project.Id);

        // Not written yet, as after a recording that failed before its first frame.
        Assert.Null(store.FindScreenRecording(project.Id));

        File.WriteAllBytes(paths.ScreenPath, [1, 2, 3]);
        Assert.Equal(paths.ScreenPath, store.FindScreenRecording(project.Id));

        // The project says which file it is.
        var renamed = Path.Combine(paths.ProjectDirectory, "take-two.mp4");
        File.Move(paths.ScreenPath, renamed);
        var sources = project.Sources;
        store.Save(project with { Sources = sources with { Screen = sources.Screen with { File = "take-two.mp4" } } });
        Assert.Equal(renamed, store.FindScreenRecording(project.Id));

        store.Delete(project.Id);
        Assert.Null(store.FindScreenRecording(project.Id));
    }

    [Fact]
    public void FindScreenRecording_OfAProjectAroundAVideoKeptElsewhere_IsNothing()
    {
        var store = CreateStore();
        var videoPath = Path.Combine(_directory, "external.mp4");
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(videoPath, [1, 2, 3]);
        var flat = store.GetOrCreateFlatProject(videoPath, new StudioRecordingSourceInfo(640, 360, 12, 24), "1.9.0");

        // That video is the user's own file already. There is nothing of it to save out.
        Assert.Null(store.FindScreenRecording(flat.Id));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{ "schemaVersion": 99 }""")]
    public void FindScreenRecording_OfAProjectThatCannotBeRead_LooksUnderTheNameEveryRecordingGets(string projectJson)
    {
        var store = CreateStore();
        var project = CreateCompletedProject(store);
        var paths = store.GetPaths(project.Id);
        File.WriteAllBytes(paths.ScreenPath, [1, 2, 3]);
        File.WriteAllText(paths.ProjectJsonPath, projectJson);

        Assert.ThrowsAny<Exception>(() => store.Load(project.Id));
        Assert.Equal(paths.ScreenPath, store.FindScreenRecording(project.Id));

        File.Delete(paths.ScreenPath);
        Assert.Null(store.FindScreenRecording(project.Id));
    }

    [Fact]
    public void ListUnreadableProjects_IsWhatListSummariesLeavesOut()
    {
        var store = CreateStore();
        var readable = CreateCompletedProject(store);
        var damaged = CreateCompletedProject(store);
        var newer = CreateCompletedProject(store);
        var unfinished = store.BeginRecording();
        File.WriteAllBytes(store.GetPaths(damaged.Id).ScreenPath, new byte[1000]);
        File.WriteAllText(store.GetPaths(damaged.Id).ProjectJsonPath, "{");
        File.WriteAllText(store.GetPaths(newer.Id).ProjectJsonPath, """{ "schemaVersion": 99 }""");
        var damagedCreated = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var newerCreated = new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);
        Directory.SetCreationTimeUtc(store.GetPaths(damaged.Id).ProjectDirectory, damagedCreated);
        Directory.SetCreationTimeUtc(store.GetPaths(newer.Id).ProjectDirectory, newerCreated);

        var unreadable = store.ListUnreadableProjects();

        Assert.Equal([readable.Id], store.ListSummaries().Select(summary => summary.Id));
        Assert.Equal([damaged.Id, newer.Id], unreadable.Select(project => project.Id));
        Assert.DoesNotContain(unfinished.ProjectId, unreadable.Select(project => project.Id));
        Assert.True(unreadable[0].HasScreenRecording);
        Assert.False(unreadable[1].HasScreenRecording);
        Assert.Equal(new DateTimeOffset(damagedCreated), unreadable[0].CreatedAt);
        Assert.Equal(new DateTimeOffset(newerCreated), unreadable[1].CreatedAt);
        Assert.Equal(1001, unreadable[0].SizeBytes);

        // Cleanup still leaves them alone, whatever the rules.
        Assert.Empty(store.Cleanup(new StudioCleanupOptions(RetentionDays: 1, SizeCapBytes: 1)).ProjectIdsDeleted);
        Assert.Equal(2, store.ListUnreadableProjects().Count);

        // The way out and the way to be rid of it.
        Assert.Equal(store.GetPaths(damaged.Id).ScreenPath, store.FindScreenRecording(damaged.Id));
        store.Delete(damaged.Id);
        Assert.Equal([newer.Id], store.ListUnreadableProjects().Select(project => project.Id));
    }

    [Fact]
    public void CompleteRecording_AppliesSavedLookWithoutCrops()
    {
        var store = CreateStore();
        var paths = store.BeginRecording();
        var look = new StudioLook(
            new StudioCanvas
            {
                Padding = 0,
                Background = new StudioBackground { Style = StudioBackgroundStyle.None, Preset = null, Secondary = null },
            },
            new StudioScreenStyle { CornerRadius = 0, Shadow = 0, Crop = new StudioRect(0.1, 0.1, 0.5, 0.5) },
            new StudioCameraStyle { Shape = StudioCameraShape.Rectangle, Mirror = false, Shadow = 0, Crop = new StudioRect(0.2, 0.2, 0.5, 0.5) });

        var project = store.CompleteRecording(paths.ProjectId, CreateRequest(look: look));

        Assert.Equal(StudioBackgroundStyle.None, project.Canvas.Background.Style);
        Assert.Equal(0, project.Canvas.Padding);
        Assert.Equal(0, project.Screen.CornerRadius);
        Assert.Equal(StudioCameraShape.Rectangle, project.Camera.Shape);
        Assert.False(project.Camera.Mirror);
        Assert.Null(project.Screen.Crop);
        Assert.Null(project.Camera.Crop);
    }

    private StudioProjectStore CreateStore() => new(_directory, _time);

    private StudioProject CreateCompletedProject(StudioProjectStore store)
    {
        var paths = store.BeginRecording();
        return store.CompleteRecording(paths.ProjectId, CreateRequest());
    }

    /// <summary>
    /// A project with a screen recording whose video was exported, is still where it was saved,
    /// and that was last opened 90 days ago: what the age rule deletes.
    /// </summary>
    private StudioProject CreateOldExportedProject(StudioProjectStore store, string name)
    {
        var project = CreateCompletedProject(store);
        File.WriteAllBytes(store.GetPaths(project.Id).ScreenPath, [1, 2, 3]);
        store.RecordExport(project.Id, WriteExportedVideo(name));
        return store.Save(store.Load(project.Id) with { LastOpenedAt = _time.GetUtcNow() - TimeSpan.FromDays(90) });
    }

    /// <summary>Writes a file where a video would have been exported to, and returns its path.</summary>
    private string WriteExportedVideo(string name)
    {
        var path = ExportedVideoPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [7, 8, 9]);
        return path;
    }

    // Not a project folder: the store only looks into folders named like a project id.
    private string ExportedVideoPath(string name) => Path.Combine(_directory, "Videos", name + "-export.mp4");

    private void WriteProject(string folderId, StudioProject project)
    {
        var directory = Path.Combine(_directory, folderId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, StudioProjectStore.ProjectFileName), StudioProjectJson.WriteProject(project));
    }

    private static string ValidId(string id) => id;

    private static StudioProject Project(string id) =>
        new()
        {
            Id = id,
            Sources = new StudioSources
            {
                Screen = new StudioScreenSource { Width = 1920, Height = 1080, Duration = 10 },
            },
        };

    private static StudioProjectCreationRequest CreateRequest(StudioLook? look = null) =>
        new(
            "Recording",
            new StudioRecordingSourceInfo(1920, 1080, 10, 60),
            new StudioCameraSourceInfo(1280, 720, 9, 0.25),
            StudioAnchor.TopLeft,
            new StudioClickOverlay { Enabled = true, Color = "#112233", Size = 30, StrokeWidth = 2, Opacity = 0.5, Duration = 0.3 },
            true,
            "1.9.0",
            look);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
