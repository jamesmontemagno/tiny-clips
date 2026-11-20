using TinyClips.Core.Automation;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class AutomationStudioProjectCopyTests : StudioProjectFolderTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyIncludesSourcesAndEditsWithoutChangingTheLinkedDestination(bool imported)
    {
        var video = Path.Combine(Outside, "original.MOV");
        File.WriteAllText(video, "video");
        var id = imported
            ? Store.GetOrCreateFlatProject(video, new StudioRecordingSourceInfo(1920, 1080, 5), "1.0").Id
            : MakeProject(camera: true, events: true, poster: true);
        var project = Store.Load(id);
        Store.Save(project with { Edits = project.Edits with { TrimStart = 1 } });
        var linked = Path.Combine(Outside, "Linked.tinyclips");
        StudioProjectDestination.Remember(Store, id, linked);
        var storedJson = File.ReadAllText(Store.GetPaths(id).ProjectJsonPath);

        var result = await CopyAsync(id, "Copy");

        Assert.Equal(id, result["projectId"]!.GetValue<string>());
        Assert.Equal(Path.Combine(Outside, "Copy"), result["folderPath"]!.GetValue<string>());
        var file = result["projectFile"]!.GetValue<string>();
        Assert.Equal(Path.Combine(Outside, "Copy", "Copy.tinyclips"), file);
        var opened = Open(file);
        Assert.False(opened.Sources.Screen.External);
        Assert.Equal(1, opened.Edits.TrimStart);
        Assert.Equal(imported ? "video" : "screen", File.ReadAllText(Store.GetPaths(opened).ScreenPath));
        Assert.Equal(linked, StudioProjectDestination.Read(Store, id));
        Assert.Equal(storedJson, File.ReadAllText(Store.GetPaths(id).ProjectJsonPath));
        Assert.Equal("video", File.ReadAllText(video));
        Assert.DoesNotContain("saved-project.json", Directory.GetFiles(Path.GetDirectoryName(file)!).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("/absolute")]
    [InlineData("C:\\absolute")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("NUL")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("copy:stream")]
    public async Task CopyRejectsNamesThatAreNotPlainFolders(string name)
    {
        var id = MakeProject(camera: false);

        var error = await Assert.ThrowsAsync<AutomationException>(() => CopyAsync(id, name));

        Assert.Equal(AutomationErrorCodes.InvalidParams, error.Code);
        Assert.Empty(Names(Outside));
    }

    [Fact]
    public async Task CopyNeverReplacesAnExistingProjectOrFile()
    {
        var (folder, file) = SaveNewProject("Copy");
        var json = File.ReadAllText(file);
        var id = MakeProject(camera: true);
        var error = await Assert.ThrowsAsync<AutomationException>(() => CopyAsync(id, "Copy"));
        Assert.Equal(AutomationErrorCodes.InvalidParams, error.Code);
        Assert.Equal(json, File.ReadAllText(file));
        Assert.Equal(["Copy.tinyclips", "screen.mp4"], Names(folder));

        File.WriteAllText(Path.Combine(Outside, "Taken"), "mine");
        error = await Assert.ThrowsAsync<AutomationException>(() => CopyAsync(id, "Taken"));
        Assert.Equal(AutomationErrorCodes.InvalidParams, error.Code);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(Outside, "Taken")));
        Assert.Equal(["Copy", "Taken"], Names(Outside));
    }

    [Fact]
    public async Task CopyRefusesVideoFoldersInsideTheProjectIncludingAliases()
    {
        var id = MakeProject(camera: false);
        var projectDirectory = Store.GetPaths(id).ProjectDirectory;
        var child = Path.Combine(projectDirectory, "nested");
        Directory.CreateDirectory(child);
        var alias = Path.Combine(Outside, "alias");
        try
        {
            Directory.CreateSymbolicLink(alias, child);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This account cannot create a directory alias: {ex.Message}");
        }

        foreach (var videoFolder in new[] { projectDirectory, child, alias })
        {
            var error = await Assert.ThrowsAsync<AutomationException>(() =>
                AutomationStudioProjectCopy.SaveAsync(Store, id, "Copy", videoFolder, TestContext.Current.CancellationToken));
            Assert.Equal(AutomationErrorCodes.InvalidParams, error.Code);
        }

        Assert.Empty(Names(child));
    }

    [Fact]
    public async Task CopyCreatesMissingVideoFolderAndReportsMissingSourcesWithoutLeavingAPartialProject()
    {
        var id = MakeProject(camera: false);
        var missingFolder = Path.Combine(Outside, "missing");

        var copied = await AutomationStudioProjectCopy.SaveAsync(
            Store, id, "Copy", missingFolder, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(missingFolder, "Copy"), copied["folderPath"]!.GetValue<string>());
        Assert.True(File.Exists(copied["projectFile"]!.GetValue<string>()));
        Directory.Delete(missingFolder, recursive: true);

        File.Delete(Store.GetPaths(id).ScreenPath);
        var error = await Assert.ThrowsAsync<AutomationException>(() => CopyAsync(id, "Copy"));
        Assert.Equal(AutomationErrorCodes.Internal, error.Code);
        Assert.Empty(Names(Outside));
    }

    [Fact]
    public async Task CopyPropagatesCancellationWithoutCreatingADestination()
    {
        var id = MakeProject(camera: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AutomationStudioProjectCopy.SaveAsync(Store, id, "Copy", Outside, cancellation.Token));

        Assert.Empty(Names(Outside));
        Assert.Null(StudioProjectDestination.Read(Store, id));
    }

    private Task<System.Text.Json.Nodes.JsonObject> CopyAsync(string id, string name) =>
        AutomationStudioProjectCopy.SaveAsync(Store, id, name, Outside, TestContext.Current.CancellationToken);
}
