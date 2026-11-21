using System.Diagnostics;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioProjectDestinationTests : StudioProjectFolderTestBase
{
    [Fact]
    public void LibraryOpensTheSavedFolderOrFallsBackToTheRecordingFolder()
    {
        var id = MakeProject(camera: false);
        var paths = Store.GetPaths(id);
        Assert.Equal(paths.ProjectDirectory, StudioProjectDestination.LibraryFolder(Store, id));
        var saved = Path.Combine(Outside, "Demo");
        Save(id, saved);
        StudioProjectDestination.Remember(Store, id, Path.Combine(saved, "Demo.tinyclips"));
        Assert.Equal(saved, StudioProjectDestination.LibraryFolder(Store, id));
        Directory.Move(saved, Path.Combine(Outside, "Moved"));
        Assert.Equal(paths.ProjectDirectory, StudioProjectDestination.LibraryFolder(Store, id));
        File.WriteAllText(paths.ProjectJsonPath, "{");
        Assert.Equal(paths.ProjectDirectory, StudioProjectDestination.LibraryFolder(Store, id));
    }

    [Fact]
    public void LibraryRemovalLeavesTheSavedFolderAndItsExportAlone()
    {
        var (folder, file) = SaveNewProject("Saved");
        var id = Open(file).Id;
        var export = Path.Combine(folder, "Export.mp4");
        File.WriteAllText(export, "export");
        using (Store.AcquireProjectUse(id))
            Assert.Throws<StudioProjectInUseException>(() => Store.Delete(id));
        Assert.True(Store.Exists(id));
        File.WriteAllText(Store.GetPaths(id).ProjectJsonPath, "{");
        Store.Delete(id);
        Assert.False(Store.Exists(id));
        Assert.True(File.Exists(file));
        Assert.Equal("export", File.ReadAllText(export));
        Assert.Throws<DirectoryNotFoundException>(() => StudioProjectDestination.LibraryFolder(Store, id));
    }

    [Fact]
    public void ImportedDestinationSurvivesRestartAndNeverTravelsWithSnapshot()
    {
        var (folder, file) = SaveNewProject("Original");
        var opened = Open(file);
        var restarted = new StudioProjectStore(StoreRoot);
        Assert.Equal(file, StudioProjectDestination.Read(restarted, opened.Id));
        Save(opened.Id, Path.Combine(Outside, "Copy"));
        Assert.False(File.Exists(Path.Combine(Outside, "Copy", "saved-project.json")));
        Assert.DoesNotContain(Outside, File.ReadAllText(Path.Combine(Outside, "Copy", "Copy.tinyclips")), StringComparison.Ordinal);
        Assert.Equal("screen", File.ReadAllText(Path.Combine(folder, "screen.mp4")));
    }

    [Fact]
    public void RememberingTheDestinationDoesNotOverwriteAnAssetWithATemporaryFilename()
    {
        var (_, file) = SaveNewProject("Demo");
        var opened = Open(file);
        const string asset = "saved-project.json.tmp";
        var path = Path.Combine(Store.GetPaths(opened).ProjectDirectory, asset);
        File.WriteAllText(path, "project asset");
        Store.Save(opened with { Canvas = opened.Canvas with
        {
            Background = opened.Canvas.Background with { Image = asset },
        } });

        StudioProjectDestination.Remember(Store, opened.Id, file);

        Assert.Equal("project asset", File.ReadAllText(path));
        Assert.Equal(file, StudioProjectDestination.Read(Store, opened.Id));
    }

    [Fact]
    public void LinkedSaveUpdatesTheExactFileAndPreservesExportsAndUnrelatedContent()
    {
        var (folder, file) = SaveNewProject("Demo", camera: true);
        var exact = Path.Combine(folder, "Custom.tinyclips");
        File.Move(file, exact);
        var opened = Open(exact);
        File.WriteAllText(Path.Combine(folder, "Demo.mp4"), "export");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "notes");
        Directory.CreateDirectory(Path.Combine(folder, "Other"));
        File.WriteAllText(Path.Combine(folder, "Other", "keep.txt"), "keep");
        Store.Save(opened with { Name = "Updated" });

        StudioProjectDestination.Update(Store, opened.Id, exact, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Updated", StudioProjectFolder.ReadProjectFile(exact).Name);
        Assert.Equal("export", File.ReadAllText(Path.Combine(folder, "Demo.mp4")));
        Assert.Equal("notes", File.ReadAllText(Path.Combine(folder, "notes.txt")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(folder, "Other", "keep.txt")));
        Assert.False(File.Exists(file));
        Assert.DoesNotContain(Directory.GetDirectories(folder), path => Path.GetFileName(path).StartsWith(".tinyclips", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataOnlySaveNeitherStagesNorReplacesTheLinkedRecordings(bool external)
    {
        var folder = Path.Combine(Outside, "Demo");
        var file = Path.Combine(folder, "Demo.tinyclips");
        var original = Path.Combine(Outside, "Long recording.MOV");
        File.WriteAllText(original, "video");
        var id = external
            ? Store.GetOrCreateFlatProject(original, new StudioRecordingSourceInfo(1920, 1080, 5), "1.0").Id
            : MakeProject(camera: true, events: true, poster: true);
        Save(id, folder);
        if (!external) id = Open(file).Id;
        var screen = Path.Combine(folder, external ? "screen.mov" : "screen.mp4");
        // A reader allows verification but not deletion or replacement.
        using (File.OpenRead(screen))
        {
            Store.Save(Store.Load(id) with { Name = "Renamed" });
            StudioProjectDestination.Update(Store, id, file, progress: null, cancellationToken: TestContext.Current.CancellationToken,
                beforeCommit: () => Assert.Equal(["Demo.tinyclips"], Names(Assert.Single(Directory.GetDirectories(folder)))));
        }

        var saved = StudioProjectFolder.ReadProjectFile(file);
        Assert.Equal("Renamed", saved.Name);
        Assert.False(saved.Sources.Screen.External);
        if (external)
        {
            File.WriteAllText(original, "edited original");
            Assert.Equal("video", File.ReadAllText(screen));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MatchingLengthNameAndWriteTimeDoNotHideAChangedRecording(bool changeSource)
    {
        var (folder, file) = SaveNewProject("Demo", camera: true);
        var opened = Open(file);
        var source = Store.GetPaths(opened).ScreenPath;
        var destination = Path.Combine(folder, "screen.mp4");
        var timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var path in new[] { source, destination })
        {
            using (var stream = File.OpenWrite(path)) stream.SetLength((1 << 20) + 1);
            File.SetLastWriteTimeUtc(path, timestamp);
        }
        StudioProjectDestination.Update(Store, opened.Id, file, cancellationToken: TestContext.Current.CancellationToken);

        var changed = changeSource ? source : destination;
        using (var stream = File.OpenWrite(changed))
        {
            stream.Seek(-1, SeekOrigin.End);
            stream.WriteByte(42);
        }
        File.SetLastWriteTimeUtc(changed, timestamp);

        using var cameraReader = File.OpenRead(Path.Combine(folder, "camera.mp4"));
        StudioProjectDestination.Update(Store, opened.Id, file, cancellationToken: TestContext.Current.CancellationToken);

        using var saved = File.OpenRead(destination);
        saved.Seek(-1, SeekOrigin.End);
        Assert.Equal(changeSource ? 42 : 0, saved.ReadByte());
    }

    [Fact]
    public void MissingLinkedRecordingIsRestoredButMissingSourceDoesNotCommitEdits()
    {
        var (folder, file) = SaveNewProject("Demo", camera: true);
        var opened = Open(file);
        var camera = Path.Combine(folder, "camera.mp4");
        File.Delete(camera);
        using var screenReader = File.OpenRead(Path.Combine(folder, "screen.mp4"));
        StudioProjectDestination.Update(Store, opened.Id, file, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("camera", File.ReadAllText(camera));

        var before = File.ReadAllBytes(file);
        Store.Save(opened with { Name = "Uncommitted" });
        File.Delete(Store.GetPaths(opened).CameraPath!);
        AssertRefused(StudioProjectFolderProblem.MissingFile, () => StudioProjectDestination.Update(Store, opened.Id, file,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal("camera", File.ReadAllText(camera));
        Assert.Empty(Directory.GetDirectories(folder));
    }

    [Fact]
    public void NewAssetCannotOverwriteAnUnrelatedFile()
    {
        var (folder, file) = SaveNewProject("Demo");
        var opened = Open(file);
        var before = File.ReadAllBytes(file);
        File.WriteAllText(Path.Combine(folder, "background.png"), "unrelated");
        // Even identical bytes do not make a new asset name ours to own or replace.
        File.WriteAllText(Path.Combine(Store.GetPaths(opened).ProjectDirectory, "background.png"), "unrelated");
        Store.Save(opened with { Canvas = opened.Canvas with
        {
            Background = opened.Canvas.Background with { Image = "background.png" },
        } });

        Assert.Throws<IOException>(() => StudioProjectDestination.Update(Store, opened.Id, file,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(folder, "background.png")));
    }

    [Fact]
    public void ANewAssetCollisionAfterPreflightIsPreservedAndTheSaveRollsBack()
    {
        var (folder, file) = SaveNewProject("Demo");
        var opened = Open(file);
        var before = File.ReadAllBytes(file);
        var screen = File.ReadAllBytes(Path.Combine(folder, "screen.mp4"));
        File.WriteAllText(Store.GetPaths(opened).ScreenPath, "changed recording to roll back");
        var background = Path.Combine(folder, "background.png");
        File.WriteAllText(Path.Combine(Store.GetPaths(opened).ProjectDirectory, "background.png"), "new asset");
        Store.Save(opened with { Canvas = opened.Canvas with
        {
            Background = opened.Canvas.Background with { Image = "background.png" },
        } });

        Assert.Throws<IOException>(() => StudioProjectDestination.Update(Store, opened.Id, file,
            progress: null, cancellationToken: TestContext.Current.CancellationToken,
            beforeCommit: () => File.WriteAllText(background, "late unrelated file")));

        Assert.Equal("late unrelated file", File.ReadAllText(background));
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(screen, File.ReadAllBytes(Path.Combine(folder, "screen.mp4")));
        Assert.Empty(Directory.GetDirectories(folder));
    }

    [Fact]
    public void CancelledSaveLeavesPreviousSnapshotAndDestination()
    {
        var (_, file) = SaveNewProject("Demo");
        var opened = Open(file);
        var before = File.ReadAllBytes(file);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => StudioProjectDestination.Update(Store, opened.Id, file,
            cancellationToken: cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(file, StudioProjectDestination.Read(Store, opened.Id));
    }

    [Fact]
    public void CanCancelWhileVerifyingUnchangedMediaBeforeStaging()
    {
        var (folder, file) = SaveNewProject("Demo");
        var opened = Open(file);
        var before = File.ReadAllBytes(file);
        foreach (var path in new[] { Store.GetPaths(opened).ScreenPath, Path.Combine(folder, "screen.mp4") })
        {
            using var stream = File.OpenWrite(path);
            stream.SetLength(3 << 20);
        }
        Store.Save(opened with { Name = "Uncommitted" });
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelOnProgress(cancellation);

        Assert.Throws<OperationCanceledException>(() => StudioProjectDestination.Update(Store, opened.Id, file, progress, cancellation.Token));

        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Empty(Directory.GetDirectories(folder));
    }

    private sealed class CancelOnProgress(CancellationTokenSource cancellation) : IProgress<double>
    {
        public void Report(double value)
        {
            if (value is > 0 and < 1) cancellation.Cancel();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitExportCommitsOnlyTheSelectedApprovedFile(bool existing)
    {
        var file = Path.Combine(Outside, "Chosen.mp4");
        if (existing) File.WriteAllText(file, "previous");
        var target = StudioExportTarget.Capture(file);
        var staged = Path.Combine(Outside, "staged.mp4");
        File.WriteAllText(staged, "rendered");
        target.Commit(staged);
        Assert.Equal("rendered", File.ReadAllText(file));
        Assert.False(File.Exists(staged));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AChangedOutputIsNotReplacedOrRenamed(bool existing)
    {
        var file = Path.Combine(Outside, "Chosen.mp4");
        if (existing) File.WriteAllText(file, "previous");
        var target = StudioExportTarget.Capture(file);
        File.WriteAllText(file, "another user's file");
        var staged = Path.Combine(Outside, "staged.mp4");
        File.WriteAllText(staged, "rendered");
        Assert.Throws<IOException>(() => target.Commit(staged));
        Assert.Equal("another user's file", File.ReadAllText(file));
        Assert.Equal("rendered", File.ReadAllText(staged));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingSizeAndTimestampDoNotAuthorizeAReplacementOrInPlaceEdit(bool inPlace)
    {
        var file = Path.Combine(Outside, "Chosen.mp4");
        File.WriteAllText(file, "first!");
        File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var target = StudioExportTarget.Capture(file);
        if (!inPlace)
        {
            File.Move(file, Path.Combine(Outside, "Previous.mp4"));
        }
        else
        {
            // Some filesystems expose change time at one-second precision.
            Thread.Sleep(TimeSpan.FromSeconds(1.1));
        }
        File.WriteAllText(file, "other!");
        File.SetLastWriteTimeUtc(file, target.LastWrite!.Value);
        var changed = StudioExportTarget.Capture(file);
        Assert.Equal(target.Length, changed.Length);
        Assert.Equal(target.LastWrite, changed.LastWrite);
        var staged = Path.Combine(Outside, "staged.mp4");
        File.WriteAllText(staged, "rendered");

        Assert.Throws<IOException>(() => target.Commit(staged));

        Assert.Equal("other!", File.ReadAllText(file));
        Assert.Equal("rendered", File.ReadAllText(staged));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DirectoryAliasesCannotHideProtectedExportPaths(bool aliasInProtectedPath, bool newOutput)
    {
        var project = Store.Load(MakeProject(camera: false));
        var paths = Store.GetPaths(project);
        var original = File.ReadAllBytes(paths.ScreenPath);
        var alias = Path.Combine(Outside, "AliasToProject");
        await MakeJunctionAsync(alias, aliasInProtectedPath ? StoreRoot : paths.ProjectDirectory);
        try
        {
            var protectedPaths = aliasInProtectedPath ? new StudioProjectStore(alias).GetPaths(project) : paths;
            var outputDirectory = aliasInProtectedPath ? paths.ProjectDirectory : alias;
            var output = Path.Combine(outputDirectory, newOutput ? "new.mp4" : "screen.mp4");
            var target = StudioExportTarget.Capture(output);

            Assert.Throws<IOException>(() => target.Validate(project, protectedPaths, savedProjectFile: null));

            Assert.Equal(original, File.ReadAllBytes(paths.ScreenPath));
            if (newOutput) Assert.False(File.Exists(output));
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryAliasDestinationIsTrackedThroughCommit(bool retarget)
    {
        var project = Store.Load(MakeProject(camera: false));
        var originalFolder = Path.Combine(Outside, "Exports");
        var otherFolder = Path.Combine(Outside, "Other");
        Directory.CreateDirectory(originalFolder);
        Directory.CreateDirectory(otherFolder);
        var alias = Path.Combine(Outside, "Alias");
        await MakeJunctionAsync(alias, originalFolder);
        try
        {
            var target = StudioExportTarget.Capture(Path.Combine(alias, "Chosen.mp4"));
            target.Validate(project, Store.GetPaths(project), savedProjectFile: null);
            var staged = Path.Combine(Outside, "staged.mp4");
            File.WriteAllText(staged, "rendered");
            if (retarget)
            {
                Directory.Delete(alias);
                await MakeJunctionAsync(alias, otherFolder);
                Assert.Throws<IOException>(() => target.Commit(staged));
                Assert.Equal("rendered", File.ReadAllText(staged));
                Assert.False(File.Exists(Path.Combine(originalFolder, "Chosen.mp4")));
                Assert.False(File.Exists(Path.Combine(otherFolder, "Chosen.mp4")));
            }
            else
            {
                target.Commit(staged);
                Assert.Equal("rendered", File.ReadAllText(Path.Combine(originalFolder, "Chosen.mp4")));
                Assert.False(File.Exists(staged));
            }
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    private static async Task MakeJunctionAsync(string alias, string target)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec")
            ?? throw new InvalidOperationException("The Windows command processor is unavailable."))
        {
            Arguments = $"/d /s /c \"mklink /J \"{alias}\" \"{target}\"\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The junction command did not start.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, $"The directory junction could not be created: {await output} {await error}");
    }
}
