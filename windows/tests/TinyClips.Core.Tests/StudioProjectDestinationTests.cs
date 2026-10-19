using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioProjectDestinationTests : StudioProjectFolderTestBase
{
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

    [Fact]
    public void NewAssetCannotOverwriteAnUnrelatedFile()
    {
        var (folder, file) = SaveNewProject("Demo");
        var opened = Open(file);
        var before = File.ReadAllBytes(file);
        File.WriteAllText(Path.Combine(folder, "background.png"), "unrelated");
        File.WriteAllText(Path.Combine(Store.GetPaths(opened).ProjectDirectory, "background.png"), "new asset");
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
}
