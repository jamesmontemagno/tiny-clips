using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

/// <summary>
/// What a save replaces: a saved project that holds nothing a save does not write, and no
/// other folder, whatever else it has in it. Decided by the owner on 7 October 2026: a folder
/// that only happens to have one <c>.tinyclips</c> file in it, Documents with a project file
/// copied into it, is not taken with everything it holds.
/// </summary>
public sealed class StudioProjectFolderReplacementTests : StudioProjectFolderTestBase
{
    [Fact]
    public void ASavedProjectThatHoldsOnlyWhatASaveWrites_IsReplaced()
    {
        var first = MakeProject(camera: true, events: true, poster: true);
        var project = Store.Load(first);
        Store.Save(project with
        {
            Canvas = project.Canvas with
            {
                Background = project.Canvas.Background with { Style = StudioBackgroundStyle.Image, Image = "backdrop.png" },
            },
        });
        File.WriteAllText(Path.Combine(Store.GetPaths(first).ProjectDirectory, "backdrop.png"), "image");
        var folder = Path.Combine(Outside, "Whole");
        Save(first, folder);
        Assert.Equal(["Whole.tinyclips", "backdrop.png", "camera.mp4", "events.json", "poster.jpg", "screen.mp4"], Names(folder));

        Assert.Null(StudioProjectFolder.WhyASaveWouldNotReplace(folder));

        Save(MakeProject(camera: false), folder, replaceSavedProject: true);

        Assert.Equal(["Whole.tinyclips", "screen.mp4"], Names(folder));
        Assert.Equal(["Whole"], Names(Outside));
    }

    [Fact]
    public void AFileThatIsNotPartOfTheProject_StopsTheReplacement_AndTheFolderIsByteForByteAsItWas()
    {
        var folder = SavedFolder("Mine");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "what I meant to say in this video");
        var id = MakeProject(camera: false);
        var before = Everything(folder);

        var asked = StudioProjectFolder.WhyASaveWouldNotReplace(folder);
        var refusal = AssertRefused(StudioProjectFolderProblem.DestinationHasOtherFiles, () => Save(id, folder, replaceSavedProject: true));

        Assert.Equal(StudioProjectFolderProblem.DestinationHasOtherFiles, asked?.Problem);
        Assert.Equal("notes.txt", asked?.FileName);
        Assert.Equal("notes.txt", refusal.FileName);
        Assert.Contains("notes.txt", refusal.Message);

        // Not asked to replace, it is the old refusal: the name is taken.
        AssertRefused(StudioProjectFolderProblem.DestinationExists, () => Save(id, folder));

        Assert.Equal(before, Everything(folder));
        Assert.Equal(["Mine"], Names(Outside));
        Assert.True(StudioProjectFolder.IsSavedProjectFolder(folder));
    }

    [Theory]
    [InlineData("takes")]
    [InlineData("poster.jpg")]
    [InlineData("._kept by a mac")]
    [InlineData("desktop.ini")]
    public void AFolderInIt_StopsTheReplacement_WhateverItIsCalled(string name)
    {
        var folder = SavedFolder("With A Folder");
        File.Delete(Path.Combine(folder, "poster.jpg"));
        Directory.CreateDirectory(Path.Combine(folder, name));
        File.WriteAllText(Path.Combine(folder, name, "take 2.mp4"), "another take");
        var id = MakeProject(camera: false);
        var before = Everything(folder);

        var refusal = AssertRefused(StudioProjectFolderProblem.DestinationHasOtherFiles, () => Save(id, folder, replaceSavedProject: true));

        Assert.Equal(name, refusal.FileName);
        Assert.Equal(before, Everything(folder));
        Assert.Equal(["With A Folder"], Names(Outside));
    }

    [Fact]
    public void ALinkInIt_StopsTheReplacement_EvenUnderANameTheProjectGives()
    {
        var folder = SavedFolder("With A Link");
        var elsewhere = Path.Combine(Outside, "elsewhere.jpg");
        File.WriteAllText(elsewhere, "kept somewhere else");
        File.Delete(Path.Combine(folder, "poster.jpg"));
        MakeLinkOrSkip(Path.Combine(folder, "poster.jpg"), elsewhere);
        var id = MakeProject(camera: false);

        var refusal = AssertRefused(StudioProjectFolderProblem.DestinationHasOtherFiles, () => Save(id, folder, replaceSavedProject: true));

        Assert.Equal("poster.jpg", refusal.FileName);
        Assert.Equal(["With A Link.tinyclips", "camera.mp4", "events.json", "poster.jpg", "screen.mp4"], Names(folder));
        Assert.Equal("kept somewhere else", File.ReadAllText(elsewhere));
        Assert.Equal(["With A Link", "elsewhere.jpg"], Names(Outside));
    }

    [Theory]
    [InlineData(".DS_Store")]
    [InlineData("Thumbs.db")]
    [InlineData("desktop.ini")]
    [InlineData("._screen.mp4")]
    [InlineData("._Littered.tinyclips")]
    [InlineData("._")]
    [InlineData(".ds_store")]
    [InlineData("THUMBS.DB")]
    [InlineData("Desktop.ini")]
    public void WhatASystemLeavesInAFolderByItself_DoesNotStopTheReplacement_AndGoesWithTheFolder(string name)
    {
        var folder = SavedFolder("Littered");
        var litter = Path.Combine(folder, name);
        File.WriteAllBytes(litter, [0, 5, 22, 7, 0, 2, 0, 0]);
        File.SetAttributes(litter, FileAttributes.Hidden | FileAttributes.System);

        Assert.True(StudioProjectFolder.IsSavedProjectFolder(folder));
        Assert.Null(StudioProjectFolder.WhyASaveWouldNotReplace(folder));

        Save(MakeProject(camera: false), folder, replaceSavedProject: true);

        Assert.Equal(["Littered.tinyclips", "screen.mp4"], Names(folder));
        Assert.Equal(["Littered"], Names(Outside));
    }

    [Theory]
    [InlineData(".Littered.tinyclips")]
    [InlineData(".notes")]
    [InlineData("_.DS_Store")]
    [InlineData("Thumbs.db.txt")]
    [InlineData("my desktop.ini")]
    public void ANameThatOnlyLooksLikeWhatASystemLeaves_StopsTheReplacement(string name)
    {
        var folder = SavedFolder("Almost Litter");
        File.WriteAllText(Path.Combine(folder, name), "mine");
        var before = Everything(folder);

        var refusal = AssertRefused(
            StudioProjectFolderProblem.DestinationHasOtherFiles,
            () => Save(MakeProject(camera: false), folder, replaceSavedProject: true));

        Assert.Equal(name, refusal.FileName);
        Assert.Equal(before, Everything(folder));
    }

    [Fact]
    public void TheNamesAreComparedWithoutCase()
    {
        var folder = SavedFolder("Shouted");
        File.Move(Path.Combine(folder, "screen.mp4"), Path.Combine(folder, "SCREEN.MP4"));
        File.Move(Path.Combine(folder, "camera.mp4"), Path.Combine(folder, "Camera.Mp4"));
        File.Move(Path.Combine(folder, "events.json"), Path.Combine(folder, "EVENTS.json"));
        File.Move(Path.Combine(folder, "poster.jpg"), Path.Combine(folder, "Poster.JPG"));
        File.Move(Path.Combine(folder, "Shouted.tinyclips"), Path.Combine(folder, "SHOUTED.TINYCLIPS"));
        Assert.Equal(["Camera.Mp4", "EVENTS.json", "Poster.JPG", "SCREEN.MP4", "SHOUTED.TINYCLIPS"], Names(folder));

        Assert.Null(StudioProjectFolder.WhyASaveWouldNotReplace(folder));

        Save(MakeProject(camera: false), folder, replaceSavedProject: true);

        Assert.Equal(["Shouted.tinyclips", "screen.mp4"], Names(folder));
    }

    [Fact]
    public void OnlyWhatTheProjectInTheFolderNames_IsPartOfIt()
    {
        // A camera recording beside a project that has no camera is not its own.
        var (noCamera, _) = SaveNewProject("No Camera");
        File.WriteAllText(Path.Combine(noCamera, "camera.mp4"), "someone else's");
        Assert.Equal("camera.mp4", StudioProjectFolder.WhyASaveWouldNotReplace(noCamera)?.FileName);

        // A project that names no events file may have the one every recording gets.
        var (unnamed, unnamedFile) = SaveNewProject("Unnamed Events", events: true);
        EditProjectFile(unnamedFile, project => project["sources"]!["events"] = null);
        Assert.Null(StudioProjectFolder.WhyASaveWouldNotReplace(unnamed));

        // One that names its events file otherwise has that one, and not the other.
        var (named, namedFile) = SaveNewProject("Named Events", events: true);
        EditProjectFile(namedFile, project => project["sources"]!["events"] = "moves.json");
        Assert.Equal("events.json", StudioProjectFolder.WhyASaveWouldNotReplace(named)?.FileName);
        File.Move(Path.Combine(named, "events.json"), Path.Combine(named, "moves.json"));
        Assert.Null(StudioProjectFolder.WhyASaveWouldNotReplace(named));

        // A picture is part of it only as the background the project names.
        var (pictured, picturedFile) = SaveNewProject("Pictured");
        File.WriteAllText(Path.Combine(pictured, "backdrop.png"), "image");
        Assert.Equal("backdrop.png", StudioProjectFolder.WhyASaveWouldNotReplace(pictured)?.FileName);
        EditProjectFile(picturedFile, project => project["canvas"]!["background"]!["image"] = "backdrop.png");
        Assert.Null(StudioProjectFolder.WhyASaveWouldNotReplace(pictured));

        // And the recordings under the names this project gives them, not under the usual ones.
        var (renamed, renamedFile) = SaveNewProject("Renamed");
        EditProjectFile(renamedFile, project => project["sources"]!["screen"]!["file"] = "take 1.mov");
        Assert.Equal("screen.mp4", StudioProjectFolder.WhyASaveWouldNotReplace(renamed)?.FileName);
        File.Move(Path.Combine(renamed, "screen.mp4"), Path.Combine(renamed, "take 1.mov"));
        Assert.Null(StudioProjectFolder.WhyASaveWouldNotReplace(renamed));
    }

    [Theory]
    [InlineData("damaged", StudioProjectFolderProblem.Unreadable)]
    [InlineData("later", StudioProjectFolderProblem.NewerVersion)]
    [InlineData("path", StudioProjectFolderProblem.FileOutsideFolder)]
    public void ASavedProjectWhoseFileCannotBeReadAsAProject_IsNotReplaced_ForNoOneCanSayWhatBelongsToIt(string how, StudioProjectFolderProblem why)
    {
        var (folder, file) = SaveNewProject("Unknown", camera: true);
        switch (how)
        {
            case "damaged":
                File.WriteAllText(file, "hello");
                break;
            case "later":
                EditProjectFile(file, project => project["schemaVersion"] = 2);
                break;
            default:
                EditProjectFile(file, project => project["sources"]!["screen"]!["file"] = "../screen.mp4");
                break;
        }

        var before = Everything(folder);

        var asked = StudioProjectFolder.WhyASaveWouldNotReplace(folder);
        var refusal = AssertRefused(
            StudioProjectFolderProblem.DestinationExists,
            () => Save(MakeProject(camera: false), folder, replaceSavedProject: true));

        Assert.Equal(StudioProjectFolderProblem.DestinationExists, asked?.Problem);
        Assert.Equal(why, Assert.IsType<StudioProjectFolderException>(asked?.InnerException).Problem);
        Assert.Equal(why, Assert.IsType<StudioProjectFolderException>(refusal.InnerException).Problem);
        Assert.True(StudioProjectFolder.IsSavedProjectFolder(folder));
        Assert.Equal(before, Everything(folder));
        Assert.Equal(["Unknown"], Names(Outside));
    }

    [Fact]
    public void WhatIsPutIntoTheFolderWhileTheCopyIsMade_StopsTheReplacement()
    {
        var folder = SavedFolder("Changed Meanwhile");
        var id = MakeProject(camera: false);
        var told = 0;
        var progress = new Told(_ =>
        {
            told++;
            File.WriteAllText(Path.Combine(folder, "added meanwhile.txt"), "mine");
        });

        var refusal = AssertRefused(StudioProjectFolderProblem.DestinationHasOtherFiles, () => Save(id, folder, replaceSavedProject: true, progress));

        // It was not there when the save began, so the copy was made, and it was seen before
        // the folder would have gone.
        Assert.True(told > 0);
        Assert.Equal("added meanwhile.txt", refusal.FileName);
        Assert.Equal(["Changed Meanwhile.tinyclips", "added meanwhile.txt", "camera.mp4", "events.json", "poster.jpg", "screen.mp4"], Names(folder));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(folder, "added meanwhile.txt")));
        Assert.Equal(["Changed Meanwhile"], Names(Outside));
    }

    [Fact]
    public void AFolderThatWillNotBeReplaced_IsRefusedBeforeAnythingIsCopied()
    {
        var folder = SavedFolder("Refused First");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "mine");
        var told = new List<double>();

        AssertRefused(
            StudioProjectFolderProblem.DestinationHasOtherFiles,
            () => Save(MakeProject(camera: false), folder, replaceSavedProject: true, new Told(told.Add)));

        Assert.Empty(told);
    }

    [Fact]
    public void AFolderOfOnesOwnWithAProjectFileCopiedIntoIt_IsNotReplaced()
    {
        // Documents, with one .tinyclips file that got into it: by its one project file it is
        // a saved project, and replacing it would take everything else in it along.
        var (_, strayFile) = SaveNewProject("Demo");
        var documents = Path.Combine(Outside, "Documents");
        Directory.CreateDirectory(Path.Combine(documents, "Taxes"));
        File.WriteAllText(Path.Combine(documents, "Taxes", "2025.pdf"), "a year of receipts");
        File.WriteAllText(Path.Combine(documents, "Letter.docx"), "a letter");
        File.WriteAllText(Path.Combine(documents, "Photo.jpg"), "a photograph");
        File.WriteAllText(Path.Combine(documents, "budget.xlsx"), "a budget");
        File.WriteAllText(Path.Combine(documents, "screen.mp4"), "a video that happens to have the name");
        File.Copy(strayFile, Path.Combine(documents, "Demo.tinyclips"));
        var id = MakeProject(camera: false);
        var before = Everything(documents);

        Assert.True(StudioProjectFolder.IsSavedProjectFolder(documents));
        var asked = StudioProjectFolder.WhyASaveWouldNotReplace(documents);
        var refusal = AssertRefused(StudioProjectFolderProblem.DestinationHasOtherFiles, () => Save(id, documents, replaceSavedProject: true));

        // The first by name, whatever case its first letter has.
        Assert.Equal("budget.xlsx", asked?.FileName);
        Assert.Equal("budget.xlsx", refusal.FileName);
        Assert.Equal(before, Everything(documents));
        Assert.Equal(["Demo", "Documents"], Names(Outside));
    }

    [Fact]
    public void WhatIsAskedBeforeTheQuestion_SaysWhatASaveWouldSay()
    {
        var folder = SavedFolder("Asked");
        var file = Path.Combine(Outside, "A File");
        File.WriteAllText(file, "mine");
        var plain = Path.Combine(Outside, "Plain");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "keep.txt"), "mine");

        Assert.Null(StudioProjectFolder.WhyASaveWouldNotReplace(folder));
        Assert.Null(StudioProjectFolder.WhyASaveWouldNotReplace(folder + Path.DirectorySeparatorChar));
        Assert.Null(StudioProjectFolder.WhyASaveWouldNotReplace(Path.Combine(Outside, "Nothing Here")));
        Assert.Null(StudioProjectFolder.WhyASaveWouldNotReplace(null));
        Assert.Equal(StudioProjectFolderProblem.DestinationExists, StudioProjectFolder.WhyASaveWouldNotReplace(file)?.Problem);
        Assert.Equal(StudioProjectFolderProblem.DestinationExists, StudioProjectFolder.WhyASaveWouldNotReplace(plain)?.Problem);

        // Two project files: no saved project, so not a matter of what else is in it.
        File.Copy(Path.Combine(folder, "Asked.tinyclips"), Path.Combine(folder, "Second.tinyclips"));
        Assert.Equal(StudioProjectFolderProblem.DestinationExists, StudioProjectFolder.WhyASaveWouldNotReplace(folder)?.Problem);
    }

    [Fact]
    public void AFolderThatCannotBeLookedInto_IsNotReplaced()
    {
        var folder = SavedFolder("Closed");
        var closed = new DirectoryInfo(folder);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = closed.GetAccessControl();
        security.AddAccessRule(rule);
        closed.SetAccessControl(security);
        try
        {
            var asked = StudioProjectFolder.WhyASaveWouldNotReplace(folder);

            Assert.Equal(StudioProjectFolderProblem.DestinationExists, asked?.Problem);
            Assert.IsType<UnauthorizedAccessException>(asked?.InnerException);
            AssertRefused(
                StudioProjectFolderProblem.DestinationExists,
                () => Save(MakeProject(camera: false), folder, replaceSavedProject: true));
        }
        finally
        {
            security.RemoveAccessRule(rule);
            closed.SetAccessControl(security);
        }

        Assert.Equal(["Closed.tinyclips", "camera.mp4", "events.json", "poster.jpg", "screen.mp4"], Names(folder));
        Assert.Equal(["Closed"], Names(Outside));
    }

    /// <summary>A project saved as a folder with everything a save writes: both recordings, the events and the poster.</summary>
    private string SavedFolder(string name) => SaveNewProject(name, camera: true, events: true, poster: true).Folder;

    /// <summary>
    /// Everything in a folder, to any depth: its name, what is in it, its attributes, and for
    /// a file when it was written. Not when a folder was written: in one run that was seen to
    /// move by a millisecond just after the test had put a file into the folder.
    /// </summary>
    private static string[] Everything(string folder) =>
        new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Select(entry => string.Join(
                " | ",
                Path.GetRelativePath(folder, entry.FullName),
                entry is FileInfo file ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.FullName))) : "a folder",
                entry is FileInfo ? entry.LastWriteTimeUtc.Ticks : 0,
                entry.Attributes))
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Is told how far a copy is on the thread that copies, so that a test can step in between two pieces.</summary>
    private sealed class Told(Action<double> told) : IProgress<double>
    {
        public void Report(double value) => told(value);
    }
}
