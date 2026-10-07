using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

/// <summary>
/// What a project saved as a folder has to answer for on Windows and the Mac's tests do not
/// ask: what Windows takes a name for, what a failed or cancelled copy leaves, files that may
/// only be read, and the sentences of section 14 the Mac's tests leave to its code.
/// </summary>
public sealed class StudioProjectFolderWindowsTests : StudioProjectFolderTestBase
{
    private const string RootedPathOfTheSecret = "<the full path of the secret>";
    private const string NameWithAControlCharacter = "<a name with a control character>";

    public static TheoryData<string> NamesThatAreNotAFileInTheFolder =>
    [
        "..",
        "../secret.mp4",
        "..\\secret.mp4",
        "sub/screen.mp4",
        "sub\\screen.mp4",
        RootedPathOfTheSecret,
        "C:secret.mp4",
        "\\\\server\\share\\secret.mp4",
        "screen.mp4:stream",
        "CON",
        "NUL.mp4",
        "COM1",
        "lpt1.mp4",
        "screen.mp4.",
        "screen.mp4 ",
        ".. ",
        "...",
        "screen?.mp4",
        "scr*en.mp4",
        "a|b.mp4",
        "\"q\".mp4",
        "<x>.mp4",
        NameWithAControlCharacter,
        "",
        "   ",
    ];

    // What Windows takes a name for

    [Theory]
    [MemberData(nameof(NamesThatAreNotAFileInTheFolder))]
    public void ANameThatIsNotThatOfAFileInTheFolder_IsNotFollowed_WhicheverRecordingItIsFor(string name)
    {
        var (folder, file) = SaveNewProject("Reaching", camera: true, events: true);
        var id = Assert.Single(Names(StoreRoot));
        var secret = Path.Combine(Outside, "secret.mp4");
        File.WriteAllText(secret, "secret");

        // Each of these is there to be found, so that it is the name that is refused and not
        // a file that happens to be missing.
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "screen.mp4"), "deeper");
        File.WriteAllText(Path.Combine(folder, "screen.mp4:stream"), "another stream of the same file");
        var before = Names(folder);
        var original = File.ReadAllText(file);
        name = name switch
        {
            RootedPathOfTheSecret => secret,
            NameWithAControlCharacter => "scr\u0001een.mp4",
            _ => name,
        };

        (string Property, Action<JsonObject> Name)[] recordings =
        [
            ("sources.screen.file", project => project["sources"]!["screen"]!["file"] = name),
            ("sources.camera.file", project => project["sources"]!["camera"]!["file"] = name),
            ("sources.events", project => project["sources"]!["events"] = name),
        ];
        foreach (var (property, write) in recordings)
        {
            File.WriteAllText(file, original);
            EditProjectFile(file, write);

            var refusal = AssertRefused(StudioProjectFolderProblem.FileOutsideFolder, () => Open(file));

            Assert.Equal(property, refusal.Property);
            Assert.Equal([id], Names(StoreRoot));
        }

        Assert.Equal(before, Names(folder));
        Assert.Equal("secret", File.ReadAllText(secret));
    }

    [Theory]
    [InlineData("../secret.png")]
    [InlineData("..\\secret.png")]
    [InlineData(RootedPathOfTheSecret)]
    [InlineData("backdrop.png:stream")]
    [InlineData("backdrop.png.")]
    [InlineData("backdrop.png ")]
    [InlineData("NUL")]
    public void ABackgroundImageThatIsNotAFileInTheFolder_IsNotCopied_AndTheProjectOpens(string image)
    {
        var (folder, file) = SaveNewProject("Pictured");
        var secret = Path.Combine(Outside, "secret.png");
        File.WriteAllText(secret, "secret");
        File.WriteAllText(Path.Combine(folder, "backdrop.png"), "image");
        File.WriteAllText(Path.Combine(folder, "backdrop.png:stream"), "another stream of the same file");
        EditProjectFile(file, project => project["canvas"]!["background"]!["image"] = image == RootedPathOfTheSecret ? secret : image);

        var opened = Open(file);

        Assert.Equal(["project.json", "screen.mp4"], Names(Store.GetPaths(opened.Id).ProjectDirectory));
    }

    [Fact]
    public void TwoNamesThatDifferOnlyInCase_AreOneFileOnWindows_AndTheProjectIsRefused()
    {
        var (_, file) = SaveNewProject("Twins", camera: true, events: true);
        var id = Assert.Single(Names(StoreRoot));
        var original = File.ReadAllText(file);

        EditProjectFile(file, project => project["sources"]!["camera"]!["file"] = "SCREEN.MP4");
        AssertRefused(StudioProjectFolderProblem.Unreadable, () => Open(file));

        File.WriteAllText(file, original);
        EditProjectFile(file, project => project["sources"]!["events"] = "Camera.mp4");
        AssertRefused(StudioProjectFolderProblem.Unreadable, () => Open(file));

        Assert.Equal([id], Names(StoreRoot));
    }

    [Fact]
    public void ARecordingUnderTheNameTheStoreKeepsTheProjectUnder_IsRefused()
    {
        var (folder, file) = SaveNewProject("Named Alike");
        var id = Assert.Single(Names(StoreRoot));
        File.Copy(Path.Combine(folder, "screen.mp4"), Path.Combine(folder, "project.json"));
        EditProjectFile(file, project => project["sources"]!["screen"]!["file"] = "Project.json");

        AssertRefused(StudioProjectFolderProblem.Unreadable, () => Open(file));

        Assert.Equal([id], Names(StoreRoot));
    }

    [Fact]
    public void ANameInAnotherCaseThanTheFileHas_IsFollowed_AndTheCopyHasTheNameTheProjectGives()
    {
        var (_, file) = SaveNewProject("Shouted");
        EditProjectFile(file, project => project["sources"]!["screen"]!["file"] = "SCREEN.MP4");

        var opened = Open(file);

        var paths = Store.GetPaths(opened);
        Assert.Equal("screen", File.ReadAllText(paths.ScreenPath));
        Assert.Equal(["SCREEN.MP4", "project.json"], Names(paths.ProjectDirectory));
    }

    [Fact]
    public void TheRecordingsKeepTheNamesTheProjectGivesThem_WhateverTheyAre()
    {
        var (folder, file) = SaveNewProject("Other Names", camera: true, events: true);
        File.Move(Path.Combine(folder, "screen.mp4"), Path.Combine(folder, "take 1.mov"));
        File.Move(Path.Combine(folder, "camera.mp4"), Path.Combine(folder, "face.mov"));
        File.Move(Path.Combine(folder, "events.json"), Path.Combine(folder, "moves.json"));
        EditProjectFile(file, project =>
        {
            project["sources"]!["screen"]!["file"] = "take 1.mov";
            project["sources"]!["camera"]!["file"] = "face.mov";
            project["sources"]!["events"] = "moves.json";
        });

        var opened = Open(folder);

        var paths = Store.GetPaths(opened);
        Assert.Equal(["face.mov", "moves.json", "project.json", "take 1.mov"], Names(paths.ProjectDirectory));
        Assert.Equal("screen", File.ReadAllText(paths.ScreenPath));
        Assert.Equal("camera", File.ReadAllText(paths.CameraPath!));
        Assert.Equal("{}", File.ReadAllText(paths.EventsPath));

        var again = Path.Combine(Outside, "Again");
        Save(opened.Id, again);

        Assert.Equal(["Again.tinyclips", "face.mov", "moves.json", "take 1.mov"], Names(again));
    }

    [Fact]
    public void ARecordingThatIsALinkToSomewhereElse_IsNotFollowed()
    {
        var (folder, file) = SaveNewProject("Linked");
        var id = Assert.Single(Names(StoreRoot));
        var secret = Path.Combine(Outside, "secret.mp4");
        File.WriteAllText(secret, "secret");
        File.Delete(Path.Combine(folder, "screen.mp4"));
        MakeLinkOrSkip(Path.Combine(folder, "screen.mp4"), secret);

        var refusal = AssertRefused(StudioProjectFolderProblem.FileOutsideFolder, () => Open(file));

        Assert.Equal("sources.screen.file", refusal.Property);
        Assert.Equal([id], Names(StoreRoot));
    }

    [Fact]
    public void ABackgroundImageThatIsALinkToSomewhereElse_IsNotCopied()
    {
        var (folder, file) = SaveNewProject("Linked Picture");
        var secret = Path.Combine(Outside, "secret.png");
        File.WriteAllText(secret, "secret");
        MakeLinkOrSkip(Path.Combine(folder, "backdrop.png"), secret);
        EditProjectFile(file, project => project["canvas"]!["background"]!["image"] = "backdrop.png");

        var opened = Open(file);

        Assert.Equal(["project.json", "screen.mp4"], Names(Store.GetPaths(opened.Id).ProjectDirectory));
    }

    // Reading the project file

    [Fact]
    public void AProjectFileThatStartsWithAByteOrderMark_IsRead()
    {
        var (_, file) = SaveNewProject("Marked");
        File.WriteAllBytes(file, [0xEF, 0xBB, 0xBF, .. File.ReadAllBytes(file)]);

        var opened = Open(file);

        Assert.Equal("Test", opened.Name);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{ \"id\": \"3f0013cf-ba10-4453-af91-792b7882dae6\" }")]
    [InlineData("{ \"id\": \"3f0013cf-ba10-4453-af91-792b7882dae6\", \"sources\": { \"screen\": { \"width\": 0, \"height\": 1080, \"duration\": 10 } } }")]
    [InlineData("{ \"id\": \"3f0013cf-ba10-4453-af91-792b7882dae6\", \"createdAt\": \"yesterday\", \"sources\": { \"screen\": { \"width\": 1920, \"height\": 1080, \"duration\": 10 } } }")]
    public void AFileThatIsNotAProject_IsRefusedAsUnreadable_AndAddsNothingToTheStore(string contents)
    {
        var (folder, file) = SaveNewProject("Damaged");
        var id = Assert.Single(Names(StoreRoot));
        File.WriteAllText(file, contents);

        AssertRefused(StudioProjectFolderProblem.Unreadable, () => Open(file));
        AssertRefused(StudioProjectFolderProblem.Unreadable, () => Open(folder));

        Assert.Equal([id], Names(StoreRoot));
    }

    [Fact]
    public void AProjectFileThatMayNotBeReadNow_IsRefusedAsUnreadable()
    {
        var (_, file) = SaveNewProject("Held");
        var id = Assert.Single(Names(StoreRoot));

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var refusal = AssertRefused(StudioProjectFolderProblem.Unreadable, () => Open(file));
            Assert.IsAssignableFrom<IOException>(refusal.InnerException);
        }

        Assert.Equal([id], Names(StoreRoot));
    }

    [Fact]
    public void AProjectFileLargerThanSixteenMegabytes_IsRefusedUnread_AndOneOfExactlyThatIsRead()
    {
        var (_, file) = SaveNewProject("Padded");
        var id = Assert.Single(Names(StoreRoot));
        var project = File.ReadAllBytes(file);

        // Spaces after a project are still that project, so only the size refuses it.
        var padded = new byte[StudioProjectFolder.LargestProjectFileBytes + 1];
        Array.Fill(padded, (byte)' ');
        project.CopyTo(padded, 0);
        File.WriteAllBytes(file, padded);

        AssertRefused(StudioProjectFolderProblem.NotAProjectFile, () => Open(file));
        Assert.Equal([id], Names(StoreRoot));

        File.WriteAllBytes(file, padded[..^1]);

        Assert.Equal("Test", Open(file).Name);
    }

    [Fact]
    public void AProjectFileThatSaysItsVideoIsKeptElsewhere_IsRefused_AndThatVideoIsNotCopied()
    {
        var (_, file) = SaveNewProject("Elsewhere");
        var id = Assert.Single(Names(StoreRoot));
        var secret = Path.Combine(Outside, "secret.mp4");
        File.WriteAllText(secret, "secret");
        EditProjectFile(file, project =>
        {
            project["sources"]!["screen"]!["file"] = secret;
            project["sources"]!["screen"]!["external"] = true;
        });

        AssertRefused(StudioProjectFolderProblem.ExternalSource, () => Open(file));

        Assert.Equal([id], Names(StoreRoot));
    }

    [Fact]
    public void WhereAnotherComputerExportedVideosTo_IsNotTakenOver_WhateverTheFileSays()
    {
        var (_, file) = SaveNewProject("Exported Elsewhere");
        EditProjectFile(file, project => project["exports"] = new JsonArray(new JsonObject
        {
            ["path"] = "C:\\Users\\someone-else\\Videos\\demo.mp4",
            ["exportedAt"] = "2026-10-01T10:00:00Z",
            ["bytes"] = 12,
        }));

        var opened = Open(file);

        Assert.Empty(opened.Exports);
        Assert.Empty(Store.Load(opened.Id).Exports);
        Assert.DoesNotContain("someone-else", File.ReadAllText(Store.GetPaths(opened.Id).ProjectJsonPath));
        Assert.Null(Store.FindProjectIdByExportPath("C:\\Users\\someone-else\\Videos\\demo.mp4"));
    }

    [Fact]
    public void WhatTheFileHasThatThisVersionDoesNotKnow_IsKept_SuchAsASecondSoundTrack()
    {
        var (_, file) = SaveNewProject("From Elsewhere");
        EditProjectFile(file, project =>
        {
            project["app"]!["platform"] = "macos";
            project["sources"]!["screen"]!["audioTracks"] = new JsonArray("system", "microphone");
        });

        var opened = Open(file);

        Assert.Equal("macos", opened.App.Platform);
        var written = JsonNode.Parse(File.ReadAllText(Store.GetPaths(opened.Id).ProjectJsonPath))!;
        Assert.Equal(["system", "microphone"], written["sources"]!["screen"]!["audioTracks"]!.AsArray().Select(track => track!.GetValue<string>()));
    }

    [Fact]
    public void AnOpenedProjectKeepsTheTimesItsFileHas_AndCleanupLeavesIt()
    {
        var id = MakeProject(camera: false);
        var folder = Path.Combine(Outside, "Timed");
        Save(id, folder);
        var saved = Store.Load(id);

        // A video that is where the file says one was exported to: with that taken over, the
        // project would be one that cleanup removes.
        EditProjectFile(Path.Combine(folder, "Timed.tinyclips"), project => project["exports"] = new JsonArray(new JsonObject
        {
            ["path"] = Path.Combine(folder, "screen.mp4"),
            ["exportedAt"] = "2026-10-01T10:00:00Z",
        }));
        Time.Advance(TimeSpan.FromDays(90));

        var opened = Open(folder);

        Assert.Equal(saved.CreatedAt, opened.CreatedAt);
        Assert.Equal(saved.ModifiedAt, opened.ModifiedAt);
        Assert.Equal(saved.LastOpenedAt, opened.LastOpenedAt);

        Store.Cleanup(new StudioCleanupOptions(RetentionDays: 1, SizeCapBytes: 1));

        Assert.True(Store.Exists(opened.Id));
    }

    // Finding the project file

    [Fact]
    public void AFolderWithTwoProjectFiles_IsNotOpenedAsAFolder_NorReplaced_AndEitherFileOpens()
    {
        var (folder, file) = SaveNewProject("Crowded");
        var second = Path.Combine(folder, "Second.tinyclips");
        File.Copy(file, second);
        var id = MakeProject(camera: false);

        AssertRefused(StudioProjectFolderProblem.NotAProjectFile, () => Open(folder));
        Assert.False(StudioProjectFolder.IsSavedProjectFolder(folder));
        AssertRefused(StudioProjectFolderProblem.DestinationExists, () => Save(id, folder, replaceSavedProject: true));
        Assert.Equal(["Crowded.tinyclips", "Second.tinyclips", "screen.mp4"], Names(folder));

        Assert.Equal("Test", Open(second).Name);
    }

    [Theory]
    [InlineData("._Counted.tinyclips", false)]
    [InlineData("Other.tinyclips", true)]
    public void AProjectFileThatIsHidden_IsCountedLikeAnyOther(string name, bool hide)
    {
        var (folder, file) = SaveNewProject("Counted");
        var hidden = Path.Combine(folder, name);
        File.WriteAllText(hidden, "what a Mac keeps beside a file on a disk that is not its own");
        if (hide)
        {
            File.SetAttributes(hidden, FileAttributes.Hidden);
        }

        AssertRefused(StudioProjectFolderProblem.NotAProjectFile, () => StudioProjectFolder.FindProjectFile(folder));
        Assert.False(StudioProjectFolder.IsSavedProjectFolder(folder));
        Assert.Equal(file, StudioProjectFolder.FindProjectFile(file));
    }

    [Fact]
    public void AFolderWhoseOnlyProjectFileIsAFolder_IsNotASavedProject()
    {
        var folder = Path.Combine(Outside, "Hollow");
        Directory.CreateDirectory(Path.Combine(folder, "Hollow.tinyclips"));

        AssertRefused(StudioProjectFolderProblem.NotAProjectFile, () => StudioProjectFolder.FindProjectFile(folder));
        AssertRefused(StudioProjectFolderProblem.NotAProjectFile, () => StudioProjectFolder.FindProjectFile(Path.Combine(folder, "Hollow.tinyclips")));
        Assert.False(StudioProjectFolder.IsSavedProjectFolder(folder));
    }

    [Fact]
    public void TheExtensionCountsInAnyCase_AndNothingChosenIsNotAProjectFile()
    {
        var (folder, file) = SaveNewProject("Loud");
        var loud = Path.Combine(folder, "LOUD.TINYCLIPS");
        File.Move(file, loud);

        Assert.Equal("LOUD.TINYCLIPS", Path.GetFileName(StudioProjectFolder.FindProjectFile(folder)));
        Assert.Equal("Test", Open(loud).Name);
        AssertRefused(StudioProjectFolderProblem.NotAProjectFile, () => StudioProjectFolder.FindProjectFile(null));
        AssertRefused(StudioProjectFolderProblem.NotAProjectFile, () => StudioProjectFolder.FindProjectFile("  "));
    }

    // The folder's name

    [Fact]
    public void AFolderNameIsOneWindowsCanGiveAFolder()
    {
        Assert.Equal("a-b-c-d-e-f-g", StudioProjectFolder.FolderName("a<b>c|d?e*f\"g"));
        Assert.Equal("tab-and-bell-", StudioProjectFolder.FolderName("tab\tand\u0007bell\u2028"));
        Assert.Equal("Demo", StudioProjectFolder.FolderName(" . Demo. . "));
        Assert.Equal("CON-", StudioProjectFolder.FolderName("CON"));
        Assert.Equal("nul-.take 2", StudioProjectFolder.FolderName("nul.take 2"));
        Assert.Equal("COM1-", StudioProjectFolder.FolderName("COM1"));
        Assert.Equal("Console", StudioProjectFolder.FolderName("Console"));
        Assert.Equal("Tiny Clips Project", StudioProjectFolder.FolderName(null));

        var longName = StudioProjectFolder.FolderName(new string('a', 300));
        Assert.Equal(new string('a', StudioProjectFolder.LongestFolderName), longName);

        // Cut before a character that takes two, not through it, and not left ending in a space.
        var cutAtTwo = StudioProjectFolder.FolderName(new string('a', StudioProjectFolder.LongestFolderName - 1) + "\U0001F3AC and more");
        Assert.Equal(new string('a', StudioProjectFolder.LongestFolderName - 1), cutAtTwo);
        var cutAtASpace = StudioProjectFolder.FolderName(new string('a', StudioProjectFolder.LongestFolderName - 1) + " and more");
        Assert.Equal(new string('a', StudioProjectFolder.LongestFolderName - 1), cutAtASpace);
    }

    // Saving

    [Fact]
    public void TheFolderAProjectIsSavedInto_HasToBeThere_AndIsNotMade()
    {
        var id = MakeProject(camera: false);
        var missing = Path.Combine(Outside, "Not Here");

        Assert.Throws<DirectoryNotFoundException>(() => Save(id, Path.Combine(missing, "Demo")));

        Assert.False(Directory.Exists(missing));
        Assert.Empty(Names(Outside));
    }

    [Fact]
    public void AFolderGivenWithASeparatorAtItsEnd_IsThatFolder()
    {
        var id = MakeProject(camera: false);
        var folder = Path.Combine(Outside, "Trailing");

        Save(id, folder + Path.DirectorySeparatorChar);

        Assert.Equal(["Trailing.tinyclips", "screen.mp4"], Names(folder));
    }

    [Fact]
    public void AFileNamedTwiceByTheProject_IsCopiedOnce()
    {
        var id = MakeProject(camera: false, poster: true);
        var project = Store.Load(id);
        Store.Save(project with
        {
            Canvas = project.Canvas with
            {
                Background = project.Canvas.Background with { Style = StudioBackgroundStyle.Image, Image = "poster.jpg" },
            },
        });
        var folder = Path.Combine(Outside, "Poster Twice");

        Save(id, folder);
        var opened = Open(folder);

        Assert.Equal(["Poster Twice.tinyclips", "poster.jpg", "screen.mp4"], Names(folder));
        Assert.Equal(["poster.jpg", "project.json", "screen.mp4"], Names(Store.GetPaths(opened.Id).ProjectDirectory));
    }

    [Fact]
    public void ASavedProjectWhoseFilesMayOnlyBeRead_IsReplaced_AndNothingOfItIsLeftBeside()
    {
        var first = MakeProject(camera: true);
        var second = MakeProject(camera: false);
        var folder = Path.Combine(Outside, "Read Only");
        Save(first, folder);
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
        }

        new DirectoryInfo(folder).Attributes |= FileAttributes.ReadOnly;

        Save(second, folder, replaceSavedProject: true);

        Assert.Equal(["Read Only.tinyclips", "screen.mp4"], Names(folder));
        Assert.Equal(["Read Only"], Names(Outside));
    }

    [Fact]
    public void ACopyIsOfWhatIsInAFile_SoOneThatMayOnlyBeReadCanBeDeletedWhereItWasCopiedTo()
    {
        var id = MakeProject(camera: false);
        File.SetAttributes(Store.GetPaths(id).ScreenPath, FileAttributes.ReadOnly);
        var folder = Path.Combine(Outside, "From Read Only");

        Save(id, folder);

        Assert.False(File.GetAttributes(Path.Combine(folder, "screen.mp4")).HasFlag(FileAttributes.ReadOnly));

        File.SetAttributes(Path.Combine(folder, "screen.mp4"), FileAttributes.ReadOnly);
        var opened = Open(folder);

        Assert.False(File.GetAttributes(Store.GetPaths(opened).ScreenPath).HasFlag(FileAttributes.ReadOnly));
        Store.Delete(opened.Id);
        Assert.False(Directory.Exists(Store.GetPaths(opened.Id).ProjectDirectory));
    }

    [Fact]
    public void SavingWhereNothingMayBeMade_IsTheSystemsRefusal_AndLeavesNothing()
    {
        var id = MakeProject(camera: false);
        var closed = Directory.CreateDirectory(Path.Combine(Outside, "Closed"));
        var rule = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateDirectories | FileSystemRights.CreateFiles,
            AccessControlType.Deny);
        var security = closed.GetAccessControl();
        security.AddAccessRule(rule);
        closed.SetAccessControl(security);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => Save(id, Path.Combine(closed.FullName, "Demo")));

            Assert.Empty(Names(closed.FullName));
        }
        finally
        {
            security.RemoveAccessRule(rule);
            closed.SetAccessControl(security);
        }
    }

    // What a save or an open that does not finish leaves: nothing

    [Fact]
    public void ASaveThatFailsHalfWay_LeavesNothing()
    {
        var id = MakeProject(camera: true);
        var folder = Path.Combine(Outside, "Half Copied");

        // The camera recording is there and cannot be read: the screen recording is copied
        // first, and then the copy fails.
        using (new FileStream(Store.GetPaths(Store.Load(id)).CameraPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => Save(id, folder));
        }

        Assert.Empty(Names(Outside));
    }

    [Fact]
    public void ASaveThatIsCancelled_LeavesNothing_BeforeItStartsOrHalfWay()
    {
        var id = MakeProject(camera: true);
        File.WriteAllBytes(Store.GetPaths(id).ScreenPath, new byte[3 << 20]);
        var folder = Path.Combine(Outside, "Cancelled");

        using var before = new CancellationTokenSource();
        before.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Store.SaveProjectFolder(id, folder, cancellationToken: before.Token));
        Assert.Empty(Names(Outside));

        using var halfWay = new CancellationTokenSource();
        var told = new List<double>();
        var progress = new Told(value =>
        {
            told.Add(value);
            halfWay.Cancel();
        });
        Assert.ThrowsAny<OperationCanceledException>(() => Store.SaveProjectFolder(id, folder, progress: progress, cancellationToken: halfWay.Token));

        // Given up after the first piece of the first file, not after the whole of it.
        Assert.InRange(Assert.Single(told), 0.01, 0.5);
        Assert.Empty(Names(Outside));
    }

    [Fact]
    public void AReplacementThatIsCancelled_LeavesTheSavedProjectAsItWas()
    {
        var first = MakeProject(camera: true, events: true);
        var second = MakeProject(camera: false);
        var folder = Path.Combine(Outside, "Kept");
        Save(first, folder);
        var before = Names(folder);
        var fileBefore = File.ReadAllBytes(Path.Combine(folder, "Kept.tinyclips"));

        using var cancelled = new CancellationTokenSource();
        var progress = new Told(_ => cancelled.Cancel());
        Assert.ThrowsAny<OperationCanceledException>(
            () => Store.SaveProjectFolder(second, folder, replaceSavedProject: true, progress, cancelled.Token));

        Assert.Equal(before, Names(folder));
        Assert.Equal(fileBefore, File.ReadAllBytes(Path.Combine(folder, "Kept.tinyclips")));
        Assert.Equal(["Kept"], Names(Outside));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhatTurnsUpUnderTheNameWhileTheCopyIsMade_IsLeftAlone_AndTheSaveIsRefused(bool replacing)
    {
        var id = MakeProject(camera: false);
        var folder = Path.Combine(Outside, "Taken Meanwhile");
        var progress = new Told(_ =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "keep.txt"), "mine");
        });

        AssertRefused(StudioProjectFolderProblem.DestinationExists, () => Save(id, folder, replacing, progress));

        Assert.Equal(["keep.txt"], Names(folder));
        Assert.Equal(["Taken Meanwhile"], Names(Outside));
    }

    [Fact]
    public void AFileThatIsStillBeingLookedAtWhenTheFolderIsPutInPlace_DoesNotStopTheSave()
    {
        var id = MakeProject(camera: true);
        var folder = Path.Combine(Outside, "Scanned");

        // What a virus scanner does with a file that was just written: it opens it, and lets
        // anyone rename or delete it meanwhile. A folder with such a file in it cannot be renamed.
        FileStream? looking = null;
        var progress = new Told(value =>
        {
            if (value >= 1 && looking is null)
            {
                var filling = Assert.Single(Directory.EnumerateDirectories(Outside));
                looking = new FileStream(Path.Combine(filling, "screen.mp4"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
        });

        try
        {
            Save(id, folder, progress: progress);

            Assert.NotNull(looking);
            Assert.Equal(["Scanned.tinyclips", "camera.mp4", "screen.mp4"], Names(folder));
            Assert.Equal(["Scanned"], Names(Outside));
        }
        finally
        {
            looking?.Dispose();
        }

        Assert.Equal("Test", Open(folder).Name);
    }

    [Fact]
    public void ASavedProjectThatIsInUse_IsNotReplaced_AndIsLeftAsItWas()
    {
        var first = MakeProject(camera: true);
        var second = MakeProject(camera: false);
        var folder = Path.Combine(Outside, "Playing");
        Save(first, folder);

        // A player has the recording open, and does not let it be renamed.
        using (new FileStream(Path.Combine(folder, "screen.mp4"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => Save(second, folder, replaceSavedProject: true));
        }

        Assert.Equal(["Playing.tinyclips", "camera.mp4", "screen.mp4"], Names(folder));
        Assert.Equal(["Playing"], Names(Outside));
    }

    [Fact]
    public void AnOpenThatFailsHalfWay_LeavesNothingInTheStore()
    {
        var (folder, file) = SaveNewProject("Half Opened", camera: true);
        var id = Assert.Single(Names(StoreRoot));

        using (new FileStream(Path.Combine(folder, "camera.mp4"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => Open(file));
        }

        Assert.Equal([id], Names(StoreRoot));
    }

    [Fact]
    public void AnOpenThatIsCancelled_LeavesNothingInTheStore_BeforeItStartsOrHalfWay()
    {
        var (folder, file) = SaveNewProject("Open Cancelled", camera: true);
        var id = Assert.Single(Names(StoreRoot));
        File.WriteAllBytes(Path.Combine(folder, "screen.mp4"), new byte[3 << 20]);

        using var before = new CancellationTokenSource();
        before.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Store.OpenProjectFolder(file, cancellationToken: before.Token));
        Assert.Equal([id], Names(StoreRoot));

        using var halfWay = new CancellationTokenSource();
        var told = new List<double>();
        var progress = new Told(value =>
        {
            told.Add(value);
            halfWay.Cancel();
        });
        Assert.ThrowsAny<OperationCanceledException>(() => Store.OpenProjectFolder(file, progress, halfWay.Token));

        Assert.InRange(Assert.Single(told), 0.01, 0.5);
        Assert.Equal([id], Names(StoreRoot));
    }

    [Fact]
    public void ACleanupThatRunsWhileAProjectIsBeingOpened_LeavesWhatIsCopiedSoFar()
    {
        var (_, file) = SaveNewProject("Cleaned Meanwhile", camera: true);
        var cleanups = 0;
        var progress = new Told(_ =>
        {
            Store.Cleanup(new StudioCleanupOptions(RetentionDays: 1, SizeCapBytes: 1));
            cleanups++;
        });

        var opened = Open(file, progress);

        Assert.True(cleanups > 0);
        var paths = Store.GetPaths(opened);
        Assert.Equal("screen", File.ReadAllText(paths.ScreenPath));
        Assert.Equal("camera", File.ReadAllText(paths.CameraPath!));
    }

    [Fact]
    public void HowFarTheCopyIs_IsToldPieceByPiece_UpToAllOfIt()
    {
        var id = MakeProject(camera: true);
        File.WriteAllBytes(Store.GetPaths(id).ScreenPath, new byte[(3 << 20) + 5]);
        var saved = new List<double>();
        var opened = new List<double>();
        var folder = Path.Combine(Outside, "Measured");

        Save(id, folder, progress: new Told(saved.Add));
        Open(folder, new Told(opened.Add));

        foreach (var told in new[] { saved, opened })
        {
            // Four pieces of the screen recording, the camera recording, and all of it.
            Assert.Equal(6, told.Count);
            Assert.Equal(told.Order(), told);
            Assert.InRange(told[0], 0.3, 0.34);
            Assert.Equal(1d, told[^1]);
        }
    }

    // What a summary says of the recording

    [Fact]
    public void ASummarySaysWhetherTheRecordingIsStillThereToOpen()
    {
        var id = MakeProject(camera: false);
        var video = Path.Combine(Outside, "finished.mp4");
        File.WriteAllText(video, "video");
        var flat = Store.GetOrCreateFlatProject(video, new StudioRecordingSourceInfo(1920, 1080, 5), "1.0");

        Assert.All(Store.ListSummaries(), summary => Assert.True(summary.SourceExists));

        File.Delete(Store.GetPaths(id).ScreenPath);
        File.Delete(video);

        var summaries = Store.ListSummaries();
        Assert.False(summaries.Single(summary => summary.Id == id).SourceExists);
        Assert.False(summaries.Single(summary => summary.Id == flat.Id).SourceExists);
    }

    // The tray menu's recent captures

    [Fact]
    public void TheTrayMenuListsNoProjectWhileStudioIsSwitchedOff_AndTheOnesWorkedOnLastWhileItIsOn()
    {
        var noon = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        StudioProjectSummary Project(string id, int created, int? opened = null, bool isDraft = true, bool isFlat = false, bool sourceExists = true, bool exportMissing = false) =>
            new(
                id,
                id,
                noon.AddMinutes(created),
                opened is { } minutes ? noon.AddMinutes(minutes) : DateTimeOffset.UnixEpoch,
                isDraft,
                isFlat,
                KeepSources: false,
                SizeBytes: 0,
                ExportMissing: exportMissing,
                SourceExists: sourceExists);

        RecentCapture[] captures =
        [
            new("C:\\Videos\\new.mp4", CaptureType.Video, noon.AddMinutes(50)),
            new("C:\\Pictures\\old.png", CaptureType.Screenshot, noon.AddMinutes(10)),
        ];
        StudioProjectSummary[] projects =
        [
            Project("recorded-earlier", created: 20),
            Project("exported", created: 60, isDraft: false),
            Project("opened-since", created: 0, opened: 40),
            Project("flat", created: 70, isFlat: true),
            Project("recording-gone", created: 80, sourceExists: false),
            Project("lost-its-export", created: 30, isDraft: false, exportMissing: true),
        ];

        Assert.Equal(
            ["capture:C:\\Videos\\new.mp4", "studio:opened-since", "studio:lost-its-export", "studio:recorded-earlier", "capture:C:\\Pictures\\old.png"],
            RecentMenuEntry.ForMenu(captures, projects, studioEnabled: true, limit: 5).Select(entry => entry.Id));
        Assert.Equal(
            ["capture:C:\\Videos\\new.mp4", "studio:opened-since"],
            RecentMenuEntry.ForMenu(captures, projects, studioEnabled: true, limit: 2).Select(entry => entry.Id));
        Assert.Equal(
            ["capture:C:\\Videos\\new.mp4", "capture:C:\\Pictures\\old.png"],
            RecentMenuEntry.ForMenu(captures, projects, studioEnabled: false, limit: 5).Select(entry => entry.Id));
    }

    [Fact]
    public void ACaptureSavedInLocalTimeAndAProjectInUniversalTime_AreMixedByTheInstant()
    {
        // The recent captures are kept with the local time and its offset, a project's times
        // are universal: half past twelve at two hours ahead is before eleven universal.
        var capture = new RecentCapture("C:\\Videos\\clip.mp4", CaptureType.Video, new DateTimeOffset(2026, 10, 7, 12, 30, 0, TimeSpan.FromHours(2)));
        var draft = new StudioProjectSummary(
            "draft",
            "draft",
            new DateTimeOffset(2026, 10, 7, 11, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UnixEpoch,
            IsDraft: true,
            IsFlat: false,
            KeepSources: false,
            SizeBytes: 0);

        var merged = RecentMenuEntry.Merged([capture], [draft], limit: 5);

        Assert.Equal(["studio:draft", "capture:C:\\Videos\\clip.mp4"], merged.Select(entry => entry.Id));
        Assert.Same(draft, merged[0].StudioDraft);
        Assert.Null(merged[0].Capture);
        Assert.Same(capture, merged[1].Capture);
        Assert.Equal(capture.CapturedAt, merged[1].Date);
    }

    [Fact]
    public void AFolderAProjectWasSavedToIsNoProject_SoItIsNeverInTheMenu_AndOneOpenedFromItIs()
    {
        var id = MakeProject(camera: false);
        var folder = Path.Combine(Outside, "A Copy");

        Save(id, folder);

        Assert.Equal([id], Store.ListSummaries().Select(summary => summary.Id));

        var opened = Open(folder);

        Assert.Equal(
            new[] { id, opened.Id }.Order(),
            StudioProjectSummary.MenuDrafts(Store.ListSummaries()).Select(draft => draft.Id).Order());

        // One whose recording is gone has nothing to open, and is not listed.
        File.Delete(Store.GetPaths(id).ScreenPath);

        Assert.Equal([opened.Id], StudioProjectSummary.MenuDrafts(Store.ListSummaries()).Select(draft => draft.Id));
    }

    private static void MakeLinkOrSkip(string path, string target)
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

    /// <summary>Is told how far a copy is on the thread that copies, so that a test can step in between two pieces.</summary>
    private sealed class Told(Action<double> told) : IProgress<double>
    {
        public void Report(double value) => told(value);
    }
}
