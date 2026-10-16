using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>
/// The words the app has for a project saved as a folder, opened from one, or deleted: which
/// sentence for which refusal, the cases of a place that is already taken, and what the menus
/// list. The windows only show these.
/// </summary>
public sealed class StudioProjectFolderTextTests : StudioProjectFolderTestBase
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    // A zone that is never this computer's by accident, so that a date shown in the wrong
    // zone is seen: five and a half hours ahead of the time the summaries are given in.
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("test", new TimeSpan(5, 30, 0), "test", "test");

    // The store's sentence for each kind of refusal, and so the app's. Every member of the
    // enum is here: a new one fails the first test until it has words.
    public static TheoryData<StudioProjectFolderProblem, string> Sentences => new()
    {
        { StudioProjectFolderProblem.ExternalSource, "This project is built around a video that is kept somewhere else, so it cannot be saved as a folder or opened from one." },
        { StudioProjectFolderProblem.MissingFile, "camera.mp4 is not in the project's folder. A .tinyclips file opens only next to the recordings it was saved with." },
        { StudioProjectFolderProblem.DestinationExists, "There is already something with that name, and it is not a saved Tiny Clips project. Choose another name." },
        { StudioProjectFolderProblem.DestinationHasOtherFiles, "This folder holds budget.xlsx, which is not part of the project, so it was not replaced. Choose another name." },
        { StudioProjectFolderProblem.NotAProjectFile, "Choose a .tinyclips file, or the folder that holds one." },
        { StudioProjectFolderProblem.FileOutsideFolder, "This project file names a recording that is not a file in its own folder (sources.screen.file), so it was not opened." },
        { StudioProjectFolderProblem.NewerVersion, "This project was saved by a newer version of Tiny Clips. Update Tiny Clips to open it." },
        { StudioProjectFolderProblem.Unreadable, "This file could not be read as a Tiny Clips project. It may be damaged." },
    };

    [Fact]
    public void EveryKindOfRefusal_HasASentence()
    {
        Assert.Equal(
            Enum.GetValues<StudioProjectFolderProblem>().Order(),
            Sentences.Select(row => row.Data.Item1).Order());
    }

    [Theory]
    [MemberData(nameof(Sentences))]
    public void ARefusal_IsSaidInTheStoresSentence_AfterWhatTheAppWasDoing(StudioProjectFolderProblem problem, string sentence)
    {
        var refusal = Refusal(problem);

        Assert.Equal(problem, refusal.Problem);
        Assert.Equal($"Studio could not save this project: {sentence}", StudioProjectFolderText.GetSaveFailure(refusal));
        Assert.Equal($"Studio could not open this project: {sentence}", StudioProjectFolderText.GetOpenFailure(refusal));
    }

    [Fact]
    public void WhatTheSystemRefusedWith_IsPassedOnInItsOwnWords()
    {
        Assert.Equal(
            "Studio could not save this project: There is not enough space on the disk.",
            StudioProjectFolderText.GetSaveFailure(new IOException("There is not enough space on the disk. ")));
        Assert.Equal(
            @"Studio could not open this project: Access to the path 'D:\Demo\screen.mp4' is denied.",
            StudioProjectFolderText.GetOpenFailure(new UnauthorizedAccessException(@"Access to the path 'D:\Demo\screen.mp4' is denied.")));
        Assert.Equal(
            "Studio could not save this project: Something went wrong.",
            StudioProjectFolderText.GetSaveFailure(new IOException(" ")));
    }

    // The three cases of a place that is taken, and the one that is free.

    [Fact]
    public void WhereNothingIs_TheProjectIsSavedWithoutAQuestion()
    {
        var target = StudioProjectFolderText.CheckSaveTarget(Outside, "  My Demo ");

        Assert.Equal(StudioSaveTargetKind.Free, target.Kind);
        Assert.Equal(Path.Combine(Outside, "My Demo"), target.Folder);
        Assert.Equal(string.Empty, target.Message);
        Assert.True(target.CanSave);
        Assert.False(target.Replaces);
    }

    [Fact]
    public void ASavedProjectWithNothingElseInIt_IsAskedAbout_WithTheFoldersNameAndWhereItIs()
    {
        var (folder, _) = SaveNewProject("My Demo", camera: true, events: true, poster: true);
        File.WriteAllText(Path.Combine(folder, "desktop.ini"), "what Explorer leaves");

        var target = StudioProjectFolderText.CheckSaveTarget(Outside, "My Demo");

        Assert.Equal(StudioSaveTargetKind.SavedProject, target.Kind);
        Assert.Equal(folder, target.Folder);
        Assert.Equal(
            $"The folder \u201CMy Demo\u201D in {Outside} already holds a saved Tiny Clips project. Replacing it deletes the recordings and the project file that are in it now. This cannot be undone.",
            target.Message);
        Assert.Equal("Replace the saved project \u201CMy Demo\u201D?", StudioProjectFolderText.GetReplaceTitle(target.Folder));
        Assert.True(target.CanSave);
        Assert.True(target.Replaces);

        // What the question promises is what the store then does.
        Save(MakeProject(camera: false), target.Folder, replaceSavedProject: target.Replaces);
        Assert.Equal(["My Demo.tinyclips", "screen.mp4"], Names(folder));
    }

    [Fact]
    public void ASavedProjectThatHoldsSomethingElse_IsNotAskedAbout_AndTheSentenceNamesWhatItHolds()
    {
        var (folder, _) = SaveNewProject("My Demo");
        File.WriteAllText(Path.Combine(folder, "budget.xlsx"), "mine");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "mine too");

        var target = StudioProjectFolderText.CheckSaveTarget(Outside, "My Demo");

        Assert.Equal(StudioSaveTargetKind.SavedProjectWithOtherFiles, target.Kind);
        Assert.Equal(
            "This folder holds budget.xlsx, which is not part of the project, so it was not replaced. Choose another name.",
            target.Message);
        Assert.False(target.CanSave);
        Assert.False(target.Replaces);
        Assert.Equal(["My Demo.tinyclips", "budget.xlsx", "notes.txt", "screen.mp4"], Names(folder));
    }

    [Fact]
    public void SomethingElseOfThatName_TakesTheName()
    {
        Directory.CreateDirectory(Path.Combine(Outside, "A folder"));
        File.WriteAllText(Path.Combine(Outside, "A folder", "holiday.jpg"), "a picture");
        Directory.CreateDirectory(Path.Combine(Outside, "An empty folder"));
        File.WriteAllText(Path.Combine(Outside, "A file"), "a file without an extension");
        var (two, _) = SaveNewProject("Two project files");
        File.Copy(Path.Combine(two, "Two project files.tinyclips"), Path.Combine(two, "Another.tinyclips"));

        foreach (var name in new[] { "A folder", "An empty folder", "A file", "Two project files" })
        {
            var target = StudioProjectFolderText.CheckSaveTarget(Outside, name);

            Assert.True(target.Kind == StudioSaveTargetKind.Taken, $"{name}: {target.Kind}");
            Assert.Equal(
                "There is already something with that name, and it is not a saved Tiny Clips project. Choose another name.",
                target.Message);
            Assert.False(target.CanSave);
        }
    }

    [Fact]
    public void AFolderWhoseProjectFileCannotBeRead_TakesTheName_AndTheSentenceSaysWhy()
    {
        // The store calls such a folder a saved project, and still will not replace it: an
        // app that asked "Replace?" here would be refused after the answer.
        var (folder, file) = SaveNewProject("Damaged");
        File.WriteAllText(file, "{ this is not a project");
        Assert.True(StudioProjectFolder.IsSavedProjectFolder(folder));

        var target = StudioProjectFolderText.CheckSaveTarget(Outside, "Damaged");

        Assert.Equal(StudioSaveTargetKind.Taken, target.Kind);
        Assert.Equal(
            "There is already a folder with that name, and its project file cannot be read, so it was not replaced. Choose another name.",
            target.Message);
        Assert.False(target.CanSave);
    }

    [Theory]
    [InlineData(null, "Give the folder a name.")]
    [InlineData("   ", "Give the folder a name.")]
    [InlineData("a/b", "A folder cannot be called that. Try \u201Ca-b\u201D.")]
    [InlineData("What?", "A folder cannot be called that. Try \u201CWhat-\u201D.")]
    [InlineData("CON", "A folder cannot be called that. Try \u201CCON-\u201D.")]
    [InlineData("Ends with a dot.", "A folder cannot be called that. Try \u201CEnds with a dot\u201D.")]
    public void ANameAFolderCannotHave_IsRefusedBeforeAnythingIsLookedAt_WithOneThatCan(string? name, string sentence)
    {
        var target = StudioProjectFolderText.CheckSaveTarget(Outside, name);

        Assert.Equal(StudioSaveTargetKind.UnusableName, target.Kind);
        Assert.Equal(sentence, target.Message);
        Assert.False(target.CanSave);
        Assert.Empty(Names(Outside));
    }

    [Fact]
    public void TheNameThatIsSuggested_IsOneTheCheckLetsThrough()
    {
        foreach (var name in new[] { "My Demo", "Recording 2026-10-07 at 09.00.00", "a/b:c", "CON", " . ", new string('x', 200) })
        {
            var suggested = StudioProjectFolder.FolderName(name);
            Assert.True(StudioProjectFolderText.CheckSaveTarget(Outside, suggested).Kind == StudioSaveTargetKind.Free, $"{name} became {suggested}");
        }
    }

    [Fact]
    public void WithoutAPlace_OrWithOneThatIsGone_NothingIsSaved()
    {
        var gone = Path.Combine(Outside, "Gone");

        var none = StudioProjectFolderText.CheckSaveTarget(" ", "My Demo");
        var missing = StudioProjectFolderText.CheckSaveTarget(gone, "My Demo");

        Assert.Equal(StudioSaveTargetKind.NoPlace, none.Kind);
        Assert.Equal("Choose a folder to save the project in.", none.Message);
        Assert.Equal(StudioSaveTargetKind.NoPlace, missing.Kind);
        Assert.Equal($"The folder {gone} is not there any more. Choose another folder.", missing.Message);
        Assert.False(none.CanSave || missing.CanSave);
        Assert.False(Directory.Exists(gone));
    }

    // Delete, and the other questions

    [Theory]
    [InlineData("My Demo", "Delete \u201CMy Demo\u201D?")]
    [InlineData("  Padded  ", "Delete \u201CPadded\u201D?")]
    [InlineData("", "Delete \u201CUntitled recording\u201D?")]
    [InlineData(null, "Delete \u201CUntitled recording\u201D?")]
    public void DeleteAsks_WithTheProjectsName(string? name, string title)
    {
        Assert.Equal(title, StudioProjectFolderText.GetDeleteTitle(name));
    }

    [Fact]
    public void TheQuestionsAndNotices_SayWhatTheOwnerDecided()
    {
        // That exported videos and saved folders are not deleted, and that it cannot be undone.
        Assert.Equal(
            "The recording and every edit are removed from Tiny Clips Studio. Videos you exported and folders you saved this project to are not deleted. This cannot be undone.",
            StudioProjectFolderText.DeleteMessage);
        Assert.Equal("Saving project\u2026", StudioProjectFolderText.SavingMessage);
        Assert.Equal("The project is still being saved.", StudioProjectFolderText.CloseWhileSavingTitle);
        Assert.Equal(
            "Closing the window stops the save, and nothing is left of the folder that was being written. Your edits are kept.",
            StudioProjectFolderText.CloseWhileSavingMessage);
        Assert.Equal(
            "Tiny Clips Studio is switched off. Switch it on in Settings, under Studio, to open this project.",
            StudioProjectFolderText.StudioIsOffMessage);
        Assert.Equal(
            "Project saved to the folder My Demo.",
            StudioProjectFolderText.GetSavedMessage(Path.Combine(Outside, "My Demo", "My Demo.tinyclips")));
        Assert.Equal(
            "Opening My Demo.tinyclips. Its recordings are being copied into Tiny Clips Studio.",
            StudioProjectFolderText.GetOpeningMessage(Path.Combine(Outside, "My Demo", "My Demo.tinyclips")));
        Assert.Equal(
            "Opening My Demo. Its recordings are being copied into Tiny Clips Studio.",
            StudioProjectFolderText.GetOpeningMessage(Path.Combine(Outside, "My Demo") + Path.DirectorySeparatorChar));
    }

    // What the menus list

    [Fact]
    public void OpenRecent_ListsTheOtherProjectsThatCanBeOpened_TheOneOpenedLastFirst_EightAtMost()
    {
        var start = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        var summaries = Enumerable.Range(0, 12)
            .Select(index => Summary($"p{index:00}", $"Project {index}", start, opened: start.AddHours(index)))
            .Append(Summary("gone", "Lost its recording", start, opened: start.AddDays(3)) with { SourceExists = false })
            .ToArray();

        var recent = StudioProjectFolderText.GetRecentProjects(summaries, "p11");

        Assert.Equal(8, StudioProjectFolderText.RecentProjectLimit);
        Assert.Equal(["p10", "p09", "p08", "p07", "p06", "p05", "p04", "p03"], recent.Select(summary => summary.Id));
        Assert.Empty(StudioProjectFolderText.GetRecentProjects([summaries[11]], "p11"));
        Assert.Equal("No other projects", StudioProjectFolderText.NoOtherProjects);
    }

    [Fact]
    public void ALineOfOpenRecent_IsTheNameAndWhenItWasRecorded_InTheTimeOfThePlace()
    {
        var recorded = new DateTimeOffset(2026, 10, 7, 20, 15, 0, TimeSpan.Zero);
        var summary = Summary("a", "  My Demo ", recorded, opened: recorded.AddDays(2));

        // 20:15 in Greenwich is 1:45 the next morning five and a half hours east of it.
        Assert.Equal("My Demo, 10/8/2026 1:45 AM", StudioProjectFolderText.GetRecentTitle(summary, English, Zone));
        Assert.Equal("Untitled recording, 10/8/2026 1:45 AM", StudioProjectFolderText.GetRecentTitle(summary with { Name = " " }, English, Zone));
    }

    [Fact]
    public void ADraftInTheTraysRecentCaptures_IsItsNameThatItIsAStudioProject_AndWhenItWasLastWorkedOn()
    {
        var recorded = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
        var opened = new DateTimeOffset(2026, 10, 7, 20, 15, 0, TimeSpan.Zero);

        Assert.Equal(
            "My Demo \u2014 Studio project, 10/8/2026 1:45 AM",
            StudioProjectFolderText.GetTrayDraftTitle(Summary("a", "My Demo", recorded, opened), English, Zone));

        // Never opened: when it was recorded.
        Assert.Equal(
            "Untitled recording \u2014 Studio project, 10/5/2026 2:30 PM",
            StudioProjectFolderText.GetTrayDraftTitle(Summary("b", string.Empty, recorded, DateTimeOffset.UnixEpoch), English, Zone));
    }

    private static StudioProjectSummary Summary(string id, string name, DateTimeOffset recorded, DateTimeOffset opened) =>
        new(id, name, recorded, opened, IsDraft: true, IsFlat: false, KeepSources: false, SizeBytes: 1);

    /// <summary>A refusal of each kind, as the store makes it.</summary>
    private static StudioProjectFolderException Refusal(StudioProjectFolderProblem problem) => problem switch
    {
        StudioProjectFolderProblem.ExternalSource => StudioProjectFolderException.ExternalSource(),
        StudioProjectFolderProblem.MissingFile => StudioProjectFolderException.MissingFile("camera.mp4"),
        StudioProjectFolderProblem.DestinationExists => StudioProjectFolderException.DestinationExists(),
        StudioProjectFolderProblem.DestinationHasOtherFiles => StudioProjectFolderException.DestinationHasOtherFiles("budget.xlsx"),
        StudioProjectFolderProblem.NotAProjectFile => StudioProjectFolderException.NotAProjectFile(),
        StudioProjectFolderProblem.FileOutsideFolder => StudioProjectFolderException.FileOutsideFolder("sources.screen.file"),
        StudioProjectFolderProblem.NewerVersion => StudioProjectFolderException.NewerVersion(99),
        StudioProjectFolderProblem.Unreadable => StudioProjectFolderException.Unreadable(),
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, "A new kind of refusal: give it a sentence here and in Sentences."),
    };
}
