using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>
/// The way out for a recording Studio cannot show: its screen recording, saved as a video of its
/// own, and when an editor offers that.
/// </summary>
public sealed class StudioScreenRecordingTests : StudioEditorSessionTestBase
{
    private static readonly byte[] Recording = [.. Enumerable.Range(0, 5000).Select(static value => (byte)(value % 251))];

    private string VideoFolder => Path.GetDirectoryName(ExportPath)!;

    [Fact]
    public async Task Save_CopiesTheRecordingToTheNameItIsGiven_AndLeavesTheProjectAsItWas()
    {
        var id = CreateRecordedProject();
        var before = File.ReadAllText(Projects.GetPaths(id).ProjectJsonPath);
        var asked = 0;

        var saved = await StudioScreenRecording.SaveAsync(Projects, id, () =>
        {
            asked++;
            return ExportPath;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(ExportPath, saved);
        Assert.Equal(1, asked);
        Assert.Equal(Recording, File.ReadAllBytes(ExportPath));
        Assert.Equal(Recording, File.ReadAllBytes(Projects.GetPaths(id).ScreenPath));
        Assert.Equal([ExportPath], Directory.GetFiles(VideoFolder));

        // Still a draft: the copy is a video like any other, and the project knows nothing of it.
        Assert.Equal(before, File.ReadAllText(Projects.GetPaths(id).ProjectJsonPath));
        Assert.True(Projects.ListSummaries().Single().IsDraft);
        Assert.Null(Projects.FindProjectIdByExportPath(ExportPath));
    }

    [Fact]
    public async Task Save_OfAProjectThatCannotBeRead_StillSavesItsRecording()
    {
        var id = CreateRecordedProject();
        File.WriteAllText(Projects.GetPaths(id).ProjectJsonPath, "{ this is not a project");

        var saved = await StudioScreenRecording.SaveAsync(Projects, id, () => ExportPath, TestContext.Current.CancellationToken);

        Assert.Equal(Recording, File.ReadAllBytes(saved));
    }

    [Fact]
    public async Task Save_WhileAnEditorHasTheRecordingOpen_ReadsBesideIt()
    {
        var id = CreateRecordedProject();
        using var editor = new FileStream(Projects.GetPaths(id).ScreenPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var saved = await StudioScreenRecording.SaveAsync(Projects, id, () => ExportPath, TestContext.Current.CancellationToken);

        Assert.Equal(Recording, File.ReadAllBytes(saved));
    }

    [Fact]
    public async Task Save_NeverTakesThePlaceOfAFileThatGotTheNameInTheMeantime()
    {
        var id = CreateRecordedProject();
        var second = Path.Combine(VideoFolder, "TinyClips 2026-10-03 at 12.00.00 (2).mp4");
        var asked = 0;

        var saved = await StudioScreenRecording.SaveAsync(Projects, id, () =>
        {
            if (asked++ > 0)
            {
                return second;
            }

            // Something else is saved under the name while the recording is being copied.
            Directory.CreateDirectory(VideoFolder);
            File.WriteAllBytes(ExportPath, [42]);
            return ExportPath;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(second, saved);
        Assert.Equal(2, asked);
        Assert.Equal([42], File.ReadAllBytes(ExportPath));
        Assert.Equal(Recording, File.ReadAllBytes(second));
        Assert.Equal(2, Directory.GetFiles(VideoFolder).Length);
    }

    [Fact]
    public async Task Save_GivesUpWhenNoNameIsFree_AndLeavesNothingBehind()
    {
        var id = CreateRecordedProject();
        Directory.CreateDirectory(VideoFolder);
        File.WriteAllBytes(ExportPath, [42]);
        var asked = 0;

        var error = await Assert.ThrowsAsync<IOException>(() => StudioScreenRecording.SaveAsync(Projects, id, () =>
        {
            asked++;
            return ExportPath;
        }, TestContext.Current.CancellationToken));

        Assert.Equal(
            $"Another file was saved as {Path.GetFileName(ExportPath)} while the recording was being copied, and no free name was found for the video.",
            error.Message);
        Assert.Equal(5, asked);
        Assert.Equal([42], File.ReadAllBytes(ExportPath));
        Assert.Equal([ExportPath], Directory.GetFiles(VideoFolder));
        Assert.Equal(Recording, File.ReadAllBytes(Projects.GetPaths(id).ScreenPath));
    }

    [Fact]
    public async Task Save_WithNoRecordingInTheProject_SaysSoAndWritesNothing()
    {
        var withoutFile = CreateProject(writeScreenFile: false);
        var gone = "3f0013cf-ba10-4453-af91-792b7882dae6";
        var asked = 0;
        string CreatePath()
        {
            asked++;
            return ExportPath;
        }

        var token = TestContext.Current.CancellationToken;
        var first = await Assert.ThrowsAsync<FileNotFoundException>(() => StudioScreenRecording.SaveAsync(Projects, withoutFile, CreatePath, token));
        var second = await Assert.ThrowsAsync<FileNotFoundException>(() => StudioScreenRecording.SaveAsync(Projects, gone, CreatePath, token));

        Assert.Equal(StudioScreenRecording.NothingToSaveMessage, first.Message);
        Assert.Equal(StudioScreenRecording.NothingToSaveMessage, second.Message);
        Assert.Equal(0, asked);
        Assert.False(Directory.Exists(VideoFolder));
        Assert.False(Directory.Exists(Projects.GetPaths(gone).ProjectDirectory));
    }

    [Fact]
    public async Task Save_ThatIsCancelled_LeavesNoHalfCopiedFile()
    {
        var id = CreateRecordedProject();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => StudioScreenRecording.SaveAsync(Projects, id, () => ExportPath, cancellation.Token));

        Assert.Empty(Directory.GetFiles(VideoFolder));
    }

    [Fact]
    public async Task AnEditorThatCannotShowItsProject_SaysWhetherThereIsARecordingToSave()
    {
        // The preview cannot open what the recorder wrote: the recording is there to be saved.
        var undecodable = CreateRecordedProject();
        Previews.Failure = new InvalidOperationException("The screen recording could not be decoded.");
        var session = await OpenAsync(undecodable);
        Assert.Equal(StudioEditorLoadState.Unavailable, session.State);
        Assert.True(session.HasScreenRecordingToSave);
        Previews.Failure = null;

        // The project file cannot be read, and the recording lies next to it.
        var unreadable = CreateRecordedProject();
        File.WriteAllText(Projects.GetPaths(unreadable).ProjectJsonPath, "{ this is not a project");
        session = await OpenAsync(unreadable);
        Assert.Equal(StudioEditorLoadState.Unavailable, session.State);
        Assert.True(session.HasScreenRecordingToSave);

        // The recording itself is what is missing.
        session = await OpenAsync(CreateProject(writeScreenFile: false));
        Assert.Equal(StudioEditorLoadState.Unavailable, session.State);
        Assert.False(session.HasScreenRecordingToSave);

        // The whole project is gone.
        session = await OpenAsync("3f0013cf-ba10-4453-af91-792b7882dae6");
        Assert.Equal(StudioEditorLoadState.Unavailable, session.State);
        Assert.False(session.HasScreenRecordingToSave);
    }

    [Fact]
    public async Task AnEditorThatShowsItsProject_OffersNoWayOut_UntilItsPreviewFails()
    {
        var session = await OpenAsync(CreateRecordedProject());

        Assert.Equal(StudioEditorLoadState.Ready, session.State);
        Assert.False(session.HasScreenRecordingToSave);

        Preview.RaiseFailed("The screen recording stopped decoding.");
        Pump();

        Assert.Equal(StudioEditorLoadState.Unavailable, session.State);
        Assert.True(session.HasScreenRecordingToSave);
    }

    private string CreateRecordedProject()
    {
        var id = CreateProject();
        File.WriteAllBytes(Projects.GetPaths(id).ScreenPath, Recording);
        return id;
    }
}
