using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioTimelineMediaStoreTests : StudioProjectFolderTestBase
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private StudioProjectStore FakeStore(Func<string, StudioRecordingSourceInfo>? probe = null) =>
        new(StoreRoot, Time, probe ?? (path => File.ReadAllText(path) == "bad"
            ? throw new InvalidDataException("Bad video")
            : new(640, 360, 10, 24)));

    private string Video(string name, string content = "video")
    {
        var path = Path.Combine(Outside, name + ".mp4");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task BatchSnapshotsNativeLayersAndFlatFilesInOrderAndReopensWithoutOriginals()
    {
        var store = FakeStore();
        var destination = Store.Load(MakeProject(camera: false));
        var recording = Store.Load(MakeProject(camera: true, events: true));
        var recordingPaths = Store.GetPaths(recording);
        using (var image = new Bitmap(4, 4))
            image.Save(Path.Combine(recordingPaths.ProjectDirectory, "backdrop.png"), ImageFormat.Png);
        // These unsaved edits, not the on-disk recording or an export, are what is imported.
        recording = recording with
        {
            Name = "Native snapshot",
            Edits = new() { TrimStart = 1, TrimEnd = 9 },
            Zooms = [new() { Start = 2, End = 4 }],
            Canvas = recording.Canvas with { Background = recording.Canvas.Background with { Image = "backdrop.png" } },
            Sources = recording.Sources with { Camera = recording.Sources.Camera! with { StartOffset = 0.125 } },
        };
        var first = Video("First");
        var second = Video("Second");
        using var prepared = await store.StageTimelineAsync(destination,
            [new(first), new(recordingPaths.ScreenPath, recording), new(second)], Token);
        Assert.Equal(["First", "Native snapshot", "Second"], prepared.ImportedClips.Select(clip => clip.Name));
        Assert.Equal(1, store.Load(destination.Id).SchemaVersion);
        var saved = prepared.Commit(prepared.ProposedProject, Token);
        Assert.Equal(4, saved.Timeline!.Clips.Length);
        Assert.Equal(1, saved.Timeline.Clips[2].Edits.TrimStart);
        Assert.Single(saved.Timeline.Clips[2].Zooms!);
        Assert.Equal(0.125, saved.MediaSources![2].Sources.Camera!.StartOffset);
        Assert.NotEqual("backdrop.png", saved.MediaSources[2].ExtensionData["recordingBackgroundImage"].GetString());
        Assert.Equal(JsonSerializer.Serialize(destination.Canvas), JsonSerializer.Serialize(saved.Canvas));
        Assert.Equal(StudioLayout.Screen, saved.Timeline.Clips[1].Scenes![0].Layout);
        Assert.Null(saved.MediaSources[1].Sources.Camera);
        Assert.Null(saved.MediaSources[1].Sources.Events);

        File.Move(first, first + ".moved");
        File.Move(second, second + ".moved");
        store.Delete(recording.Id);
        var copy = Path.Combine(Outside, "Portable");
        Save(saved.Id, copy);
        Assert.Null(StudioProjectFolder.WhyASaveWouldNotReplace(copy));
        var copyManifest = Path.Combine(copy, "Portable.tinyclips");
        // Linked Save follows the same full-registry inventory.
        StudioProjectDestination.Update(store, saved.Id, copyManifest, cancellationToken: Token);
        Save(saved.Id, copy, replaceSavedProject: true);
        var opened = Open(copy);
        Assert.Equal(4, opened.Timeline!.Clips.Length);
        var reopenedStore = new StudioProjectStore(StoreRoot, Time);
        var reopened = reopenedStore.Load(opened.Id);
        foreach (var name in StudioProjectDestination.OwnedFiles(reopened).Where(name => name != "poster.jpg" && name != "events.json"))
            Assert.True(File.Exists(Path.Combine(Store.GetPaths(reopened).ProjectDirectory, name)), name);
        var native = reopened.MediaSources![2];
        Assert.Equal("camera", File.ReadAllText(Path.Combine(Store.GetPaths(reopened).ProjectDirectory, native.Sources.Camera!.File)));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(Store.GetPaths(reopened).ProjectDirectory, native.Sources.Events!)));
    }

    [Fact]
    public async Task EmptyBatchAdoptsV1ExternalOriginalWithoutChangingCanvasAndFrameRate()
    {
        var path = Video("Portrait");
        var store = FakeStore(_ => new(1920, 1080, 10, 60) { RotationDegrees = 90 });
        var original = store.GetOrCreateFlatProject(path, new(1080, 1920, 10, 30), "1.0");
        using var prepared = await store.StageTimelineAsync(original, [], Token);
        Assert.True(store.Load(original.Id).Sources.Screen.External);
        Assert.Single(prepared.ProposedProject.Timeline!.Clips);
        Assert.Empty(prepared.ImportedClips);
        var saved = prepared.Commit(prepared.ProposedProject, Token);
        Assert.False(saved.Sources.Screen.External);
        Assert.Equal((1080, 1920, 30d), (saved.Sources.Screen.Width, saved.Sources.Screen.Height, saved.Sources.Screen.FrameRate));
        var source = Assert.Single(saved.MediaSources!);
        Assert.Equal(StudioMediaSourceKind.FlatVideo, source.Kind);
        Assert.Equal((1920, 1080, 60d, 90d), (source.Sources.Screen.Width, source.Sources.Screen.Height,
            source.Sources.Screen.FrameRate, source.Sources.Screen.RotationDegrees));
        Assert.Equal(saved.Sources.Screen.File, source.Sources.Screen.File);
        File.Move(path, path + ".moved");
        Assert.Equal("video", File.ReadAllText(store.GetPaths(store.Load(saved.Id)).ScreenPath));
        Assert.Empty(Directory.GetDirectories(store.GetPaths(saved).ProjectDirectory));
    }

    [Fact]
    public async Task BadSecondItemLeavesManifestAndAllExistingFilesUntouched()
    {
        var store = FakeStore();
        var project = store.Load(MakeProject(camera: false));
        var paths = store.GetPaths(project);
        var before = File.ReadAllBytes(paths.ProjectJsonPath);
        var names = Names(paths.ProjectDirectory);
        var good = Video("Good");
        var bad = Video("Bad", "bad");
        await Assert.ThrowsAsync<StudioProjectInvalidException>(() => store.StageTimelineAsync(project, [new(good), new(bad)], Token));
        Assert.Equal(before, File.ReadAllBytes(paths.ProjectJsonPath));
        Assert.Equal(names, Names(paths.ProjectDirectory));
        Assert.Equal("video", File.ReadAllText(good));
        Assert.Equal("bad", File.ReadAllText(bad));
        store.Delete(project.Id); // failed staging released its use lease
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringStagingOrCommitLeavesTheOriginalIntact(bool atCommit)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var probeCount = 0;
        var store = FakeStore(_ =>
        {
            if (!atCommit && ++probeCount == 2) cancellation.Cancel();
            return new(640, 360, 10);
        });
        var project = store.Load(MakeProject(camera: false));
        var paths = store.GetPaths(project);
        var before = File.ReadAllBytes(paths.ProjectJsonPath);
        if (atCommit)
        {
            using var prepared = await store.StageTimelineAsync(project, [new(Video("One")), new(Video("Two"))], Token);
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => prepared.Commit(prepared.ProposedProject, cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                store.StageTimelineAsync(project, [new(Video("One")), new(Video("Two"))], cancellation.Token));
        }
        Assert.Equal(before, File.ReadAllBytes(paths.ProjectJsonPath));
        Assert.Equal(["project.json", "screen.mp4"], Names(paths.ProjectDirectory));
    }

    [Fact]
    public async Task AbandoningAPreparedEditCleansOnlyItsFilesAndReleasesTheLease()
    {
        var store = FakeStore();
        var project = store.Load(MakeProject(camera: false));
        var path = Video("Incoming");
        var prepared = await store.StageTimelineAsync(project, [new(path)], Token);
        Assert.Throws<StudioProjectInUseException>(() => store.Delete(project.Id));
        Assert.Throws<StudioProjectInUseException>(() => new StudioProjectStore(StoreRoot).Delete(project.Id));
        Assert.Equal(1, store.Load(project.Id).SchemaVersion);
        prepared.Dispose();
        prepared.Dispose();
        Assert.Throws<ObjectDisposedException>(() => prepared.Commit(prepared.ProposedProject, Token));
        Assert.Equal(["project.json", "screen.mp4"], Names(store.GetPaths(project).ProjectDirectory));
        Assert.Equal("video", File.ReadAllText(path));
        store.Delete(project.Id);
    }

    [Fact]
    public async Task ExactDiskRevisionRejectsEvenAWhitespaceOnlyChangeAndKeepsThatChange()
    {
        var store = FakeStore();
        var project = store.Load(MakeProject(camera: false));
        using var prepared = await store.StageTimelineAsync(project, [new(Video("Incoming"))], Token);
        var path = store.GetPaths(project).ProjectJsonPath;
        File.AppendAllText(path, " ");
        var changed = File.ReadAllBytes(path);
        Assert.Throws<StudioProjectStaleException>(() => prepared.Commit(prepared.ProposedProject, Token));
        Assert.Equal(changed, File.ReadAllBytes(path));
        Assert.Equal(["project.json", "screen.mp4"], Names(store.GetPaths(project).ProjectDirectory));
    }

    [Fact]
    public async Task ApprovedInputIdentityCannotBeReplacedBySameSizedFileBeforeStaging()
    {
        var probes = 0;
        var store = FakeStore(_ => { probes++; return new(640, 360, 10); });
        var project = store.Load(MakeProject(camera: false));
        var path = Video("Selected");
        var expected = StudioMediaFileIdentity.Capture(path);
        var time = File.GetLastWriteTimeUtc(path);
        File.Move(path, path + ".original");
        File.WriteAllText(path, "other");
        File.SetLastWriteTimeUtc(path, time);
        await Assert.ThrowsAsync<StudioMediaFileChangedException>(() =>
            store.StageTimelineAsync(project, [new(path, ExpectedVideoIdentity: expected)], Token));
        Assert.Equal(0, probes);
        Assert.Equal("video", File.ReadAllText(path + ".original"));
        Assert.Equal("other", File.ReadAllText(path));
        Assert.Equal(["project.json", "screen.mp4"], Names(store.GetPaths(project).ProjectDirectory));
    }

    [Fact]
    public async Task FailedSecondInstallationRollsBackFirstAndPreservesTheConflictingUserFile()
    {
        var store = FakeStore();
        var project = store.Load(MakeProject(camera: false));
        var paths = store.GetPaths(project);
        var before = File.ReadAllBytes(paths.ProjectJsonPath);
        using var prepared = await store.StageTimelineAsync(project, [new(Video("First")), new(Video("Second"))], Token);
        var conflicting = prepared.ImportedSources[1].Sources.Screen.File;
        File.WriteAllText(Path.Combine(paths.ProjectDirectory, conflicting), "user file");
        Assert.Throws<IOException>(() => prepared.Commit(prepared.ProposedProject, Token));
        Assert.Equal("user file", File.ReadAllText(Path.Combine(paths.ProjectDirectory, conflicting)));
        Assert.Equal(before, File.ReadAllBytes(paths.ProjectJsonPath));
        Assert.Equal(new[] { conflicting, "project.json", "screen.mp4" }.Order(StringComparer.Ordinal), Names(paths.ProjectDirectory));
    }

    [Fact]
    public async Task ManifestWriteFailureRollsBackInstalledFilesAndTemporaryManifest()
    {
        var store = FakeStore();
        var project = store.Load(MakeProject(camera: false));
        var paths = store.GetPaths(project);
        var before = File.ReadAllBytes(paths.ProjectJsonPath);
        using var prepared = await store.StageTimelineAsync(project, [new(Video("Incoming"))], Token);
        using (File.Open(paths.ProjectJsonPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Record.Exception(() => prepared.Commit(prepared.ProposedProject, Token));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        }
        Assert.Equal(before, File.ReadAllBytes(paths.ProjectJsonPath));
        Assert.Equal(["project.json", "screen.mp4"], Names(paths.ProjectDirectory));
    }

    [Fact]
    public async Task ProposalCannotSubstituteUnprobedMediaMetadata()
    {
        var store = FakeStore();
        var project = store.Load(MakeProject(camera: false));
        using var prepared = await store.StageTimelineAsync(project, [new(Video("Incoming"))], Token);
        var proposal = prepared.ProposedProject;
        proposal.MediaSources![1] = proposal.MediaSources[1] with
        {
            Sources = proposal.MediaSources[1].Sources with { Screen = proposal.MediaSources[1].Sources.Screen with { Width = 999 } },
        };
        Assert.Throws<StudioProjectInvalidException>(() => prepared.Commit(proposal, Token));
        Assert.Equal(["project.json", "screen.mp4"], Names(store.GetPaths(project).ProjectDirectory));
    }

    [Fact]
    public async Task UnusedRegistrySourcesSurvivePortableSaveAndAreProtectedFromOverwrite()
    {
        var store = FakeStore();
        var project = store.Load(MakeProject(camera: false));
        using var prepared = await store.StageTimelineAsync(project, [new(Video("Incoming"))], Token);
        var proposal = prepared.ProposedProject;
        proposal = proposal with { Timeline = proposal.Timeline! with { Clips = [proposal.Timeline.Clips[0]] } };
        var saved = prepared.Commit(proposal, Token);
        var unused = saved.MediaSources![1].Sources.Screen.File;
        var folder = Path.Combine(Outside, "Unused");
        Save(saved.Id, folder);
        Assert.Contains(unused, Names(folder));
        Assert.Null(StudioProjectFolder.WhyASaveWouldNotReplace(folder));
        var opened = Open(folder);
        Assert.Equal(2, opened.MediaSources!.Length);
        Assert.Throws<IOException>(() => StudioExportTarget.Capture(Path.Combine(folder, unused))
            .Validate(saved, store.GetPaths(saved), Path.Combine(folder, "Unused.tinyclips")));
        File.WriteAllText(Path.Combine(folder, "personal.txt"), "keep");
        AssertRefused(StudioProjectFolderProblem.DestinationHasOtherFiles, () => Save(saved.Id, folder, true));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(folder, "personal.txt")));
    }

    [Fact]
    public async Task RegistryBackgroundPathsCannotEscapeOrUseTheLocalDestinationRecord()
    {
        foreach (var name in new[] { "..\\outside.png", "SAVED-PROJECT.JSON", "project.json" })
        {
            var store = FakeStore();
            var project = store.Load(MakeProject(camera: false));
            using var prepared = await store.StageTimelineAsync(project, [], Token);
            var saved = prepared.Commit(prepared.ProposedProject, Token);
            saved.MediaSources![0].ExtensionData["recordingBackgroundImage"] = JsonSerializer.SerializeToElement(name);
            store.Save(saved);
            var folder = Path.Combine(Outside, Guid.NewGuid().ToString("N"));
            AssertRefused(StudioProjectFolderProblem.FileOutsideFolder, () => Save(saved.Id, folder));
            Assert.False(Directory.Exists(folder));
        }
    }

    [Fact]
    public async Task ReferencedRegistryEventsCannotSilentlyDisappearAndCaseAliasesAreRefused()
    {
        var store = FakeStore();
        var project = store.Load(MakeProject(camera: false, events: true));
        using var prepared = await store.StageTimelineAsync(project, [], Token);
        var saved = prepared.Commit(prepared.ProposedProject, Token);
        File.Delete(store.GetPaths(saved).EventsPath);
        var folder = Path.Combine(Outside, "MissingEvents");
        AssertRefused(StudioProjectFolderProblem.MissingFile, () => Save(saved.Id, folder));
        File.WriteAllText(store.GetPaths(saved).EventsPath, "{}");
        saved.MediaSources![0] = saved.MediaSources[0] with
        {
            Sources = saved.MediaSources[0].Sources with
            {
                Screen = saved.MediaSources[0].Sources.Screen with { File = "SCREEN.MP4" },
            },
        };
        store.Save(saved);
        AssertRefused(StudioProjectFolderProblem.Unreadable, () => Save(saved.Id, folder));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task PortableFlatMarkerIsPreservedDuringAdoptionAndLinkedSnapshot()
    {
        var store = FakeStore();
        var flat = store.GetOrCreateFlatProject(Video("Original"), new(640, 360, 10), "1.0");
        var folder = Path.Combine(Outside, "Flat");
        Save(flat.Id, folder);
        var portable = Open(folder);
        Assert.Equal("flatVideo", portable.Sources.ExtensionData["mediaKind"].GetString());
        using (var conversion = await store.StageTimelineAsync(portable, [], Token))
        {
            Assert.Equal(StudioMediaSourceKind.FlatVideo, conversion.BaseProject.MediaSources![0].Kind);
            Assert.Null(conversion.BaseProject.MediaSources[0].Sources.Events);
        }
        var destination = store.Load(MakeProject(camera: false));
        using var prepared = await store.StageTimelineAsync(destination, [new(store.GetPaths(portable).ScreenPath, portable)], Token);
        Assert.Equal(StudioMediaSourceKind.FlatVideo, prepared.ImportedSources[0].Kind);
        Assert.Null(prepared.ImportedSources[0].Sources.Camera);
        Assert.Null(prepared.ImportedSources[0].Sources.Events);
    }

    [Fact]
    public async Task StagedMediaCannotBeChangedBetweenProbeAndPublication()
    {
        var store = FakeStore();
        var project = store.Load(MakeProject(camera: false));
        using var prepared = await store.StageTimelineAsync(project, [new(Video("Selected"))], Token);
        var directory = store.GetPaths(project).ProjectDirectory;
        var staging = Assert.Single(Directory.GetDirectories(directory));
        File.WriteAllText(Path.Combine(staging, prepared.ImportedSources[0].Sources.Screen.File), "changed");
        Assert.Throws<StudioMediaFileChangedException>(() => prepared.Commit(prepared.ProposedProject, Token));
        Assert.Equal(["project.json", "screen.mp4"], Names(directory));
    }

    [Fact]
    public async Task RealMediaFoundationProbePersistsCodedDimensionsRotationAndPlayableSources()
    {
        var project = Store.Load(MakeProject(camera: false));
        var recording = Store.Load(MakeProject(camera: true, events: true));
        var recordingPaths = Store.GetPaths(recording);
        var nativeFixture = Path.Combine(AppContext.BaseDirectory, "StudioMediaFixtures", "native.mp4");
        File.Copy(nativeFixture, recordingPaths.ScreenPath, overwrite: true);
        File.Copy(nativeFixture, recordingPaths.CameraPath!, overwrite: true);
        var nativeInfo = MediaFileProbe.Probe(nativeFixture);
        recording = recording with
        {
            Sources = recording.Sources with
            {
                Screen = recording.Sources.Screen with
                {
                    Width = nativeInfo.Width, Height = nativeInfo.Height, Duration = nativeInfo.Duration,
                    FrameRate = 24,
                },
                Camera = recording.Sources.Camera! with { Duration = nativeInfo.Duration, StartOffset = 0.125 },
            },
            Edits = new() { TrimStart = 0.25 },
        };
        var fixture = Path.Combine(AppContext.BaseDirectory, "StudioMediaFixtures", "flat.mp4");
        var selected = Path.Combine(Outside, "Rotated.mp4");
        File.Copy(fixture, selected);
        var info = MediaFileProbe.Probe(selected);
        Assert.Equal(270, info.RotationDegrees);
        using var prepared = await Store.StageTimelineAsync(project,
            [new(selected, ExpectedVideoIdentity: StudioMediaFileIdentity.Capture(selected)),
             new(recordingPaths.ScreenPath, recording, StudioMediaFileIdentity.Capture(recordingPaths.ScreenPath))], Token);
        var saved = prepared.Commit(prepared.ProposedProject, Token);
        var imported = saved.MediaSources![1].Sources.Screen;
        Assert.Equal(info.Width, imported.Width);
        Assert.Equal(info.Height, imported.Height);
        Assert.Equal(info.RotationDegrees, imported.RotationDegrees);
        Assert.True(imported.Duration > 0);
        File.Move(selected, selected + ".moved");
        Store.Delete(recording.Id);
        var folder = Path.Combine(Outside, "Real");
        Save(saved.Id, folder);
        var opened = Open(folder);
        var copied = Path.Combine(Store.GetPaths(opened).ProjectDirectory, opened.MediaSources![1].Sources.Screen.File);
        Assert.Equal(info, MediaFileProbe.Probe(copied));
        var native = opened.MediaSources[2].Sources;
        Assert.Equal(24, native.Screen.FrameRate);
        Assert.Equal(0.125, native.Camera!.StartOffset);
        Assert.Equal(0.25, opened.Timeline!.Clips[2].Edits.TrimStart);
        Assert.Equal(nativeInfo, MediaFileProbe.Probe(Path.Combine(Store.GetPaths(opened).ProjectDirectory, native.Camera.File)));
    }
}
