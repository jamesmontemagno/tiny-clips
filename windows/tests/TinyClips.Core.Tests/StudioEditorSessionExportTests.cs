using TinyClips.Core.Models;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>Exporting a project.</summary>
public sealed class StudioEditorSessionExportTests : StudioEditorSessionTestBase
{
    [Fact]
    public async Task Export_RendersWritesThePosterRecordsTheLink_ThenRaisesExported()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        Changes.Clear();

        var export = session.ExportAsync(() => ExportPath, VideoCodec.Hevc);

        Assert.True(session.IsExporting);
        Assert.Equal(0, session.ExportProgress, Precision);
        Assert.Equal(new[] { StudioEditorChanges.All }, Changes);
        var request = Assert.Single(Exporter.Exports);
        Assert.Equal(id, request.Project.Id);

        // Written next to where the video goes, under a name of its own until it is complete.
        Assert.Equal(Path.GetDirectoryName(ExportPath), Path.GetDirectoryName(request.OutputPath));
        Assert.EndsWith(".tcexport", request.OutputPath, StringComparison.Ordinal);
        Assert.Equal(VideoCodec.Hevc, request.Codec);
        Assert.Equal(Projects.GetPaths(id).ScreenPath, request.Paths.ScreenPath);
        Assert.False(export.IsCompleted);

        Exporter.Complete();

        // Rendered, but the session has not been back on its own thread to record it.
        Assert.True(session.IsExporting);
        Assert.Empty(ExportedPaths);
        Assert.False(File.Exists(ExportPath));
        Assert.Equal(StudioExportOutcome.Exported, await FinishAsync(export));

        Assert.False(session.IsExporting);
        Assert.Equal(1, session.ExportProgress, Precision);
        Assert.Equal(new[] { ExportPath }, ExportedPaths);
        Assert.Equal(ExportPath, Assert.Single(Projects.Load(id).Exports).Path);
        Assert.Equal(FakeExporter.VideoBytes, File.ReadAllBytes(ExportPath));
        Assert.False(File.Exists(request.OutputPath));
        Assert.False(session.HasNeverExported);
        Assert.False(session.Model!.HasUnexportedChanges);
        Assert.Single(Exporter.Posters);
        Assert.Empty(Errors);
        AssertLoggedInOrder("exporter.export", "exporter.poster", "store.recordExport");
    }

    [Fact]
    public async Task Export_GivesTheVideoItsNameAndTheProjectItsLinkInOneStep()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        var export = session.ExportAsync(() => ExportPath, default);

        // While the video is being made, no file has its name.
        Assert.False(File.Exists(ExportPath));

        Exporter.Complete();

        // Made, with its poster, and waiting under the other name for the session's thread.
        var waiting = Assert.Single(Exporter.Exports).OutputPath;
        Assert.Equal(FakeExporter.VideoBytes, File.ReadAllBytes(waiting));
        Assert.False(File.Exists(ExportPath));
        Assert.Empty(Projects.Load(id).Exports);
        Assert.Single(Exporter.Posters);
        Assert.Equal(1, PostedCount);

        // One action on that thread does both. The Clips Library looks a project's links up
        // when it sees a new video, and must not see the video before its link is there.
        Pump();

        Assert.True(File.Exists(ExportPath));
        Assert.Equal(ExportPath, Assert.Single(Projects.Load(id).Exports).Path);
        Assert.Equal(new[] { ExportPath }, ExportedPaths);
        Assert.Equal(StudioExportOutcome.Exported, await FinishAsync(export));
        Assert.Equal(new[] { Path.GetFileName(ExportPath) }, FilesInTheVideoFolder());
    }

    [Fact]
    public async Task Export_NeverTakesTheNameOfAFileThatWasSavedInTheMeantime()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        var names = new Queue<string>([ExportPath, LaterPath]);
        var asked = 0;
        var export = session.ExportAsync(
            () =>
            {
                asked++;
                return names.Dequeue();
            },
            default);
        Assert.Equal(1, asked);

        // A recording made while the video was being rendered. No file had the name yet, so the
        // recording was given the same one.
        byte[] recording = [9, 8, 7, 6, 5];
        Directory.CreateDirectory(Path.GetDirectoryName(ExportPath)!);
        File.WriteAllBytes(ExportPath, recording);

        Exporter.Complete();
        Assert.Equal(StudioExportOutcome.Exported, await FinishAsync(export));

        // The recording is as it was, and the video has the name a video saved now would get.
        Assert.Equal(recording, File.ReadAllBytes(ExportPath));
        Assert.Equal(FakeExporter.VideoBytes, File.ReadAllBytes(LaterPath));
        Assert.Equal(2, asked);
        Assert.Equal(new[] { LaterPath }, ExportedPaths);
        Assert.Equal(LaterPath, Assert.Single(Projects.Load(id).Exports).Path);
        Assert.Null(Projects.FindProjectIdByExportPath(ExportPath));
        Assert.Equal(new[] { Path.GetFileName(ExportPath), Path.GetFileName(LaterPath) }, FilesInTheVideoFolder());
        Assert.Empty(Errors);
    }

    [Fact]
    public async Task Export_ThatFindsNoFreeName_Fails_AndLeavesTheOtherFileAsItWas()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        var asked = 0;

        // Whatever makes the names here does not look at what is in the folder.
        var export = session.ExportAsync(
            () =>
            {
                asked++;
                return ExportPath;
            },
            default);
        byte[] recording = [9, 8, 7, 6, 5];
        Directory.CreateDirectory(Path.GetDirectoryName(ExportPath)!);
        File.WriteAllBytes(ExportPath, recording);

        Exporter.Complete();

        Assert.Equal(StudioExportOutcome.Failed, await FinishAsync(export));
        Assert.Equal(recording, File.ReadAllBytes(ExportPath));
        Assert.Equal(new[] { Path.GetFileName(ExportPath) }, FilesInTheVideoFolder());
        Assert.Equal(5, asked);
        Assert.Equal(
            $"Studio export failed: Another file was saved as {Path.GetFileName(ExportPath)} while the video was being made, and no free name was found for the video.",
            Assert.Single(Errors));
        Assert.Empty(Projects.Load(id).Exports);
        Assert.Empty(ExportedPaths);
        Assert.True(session.HasNeverExported);
        Assert.False(session.IsExporting);
        Assert.Equal(0, session.ExportProgress, Precision);
        Assert.True(session.IsEditable);
    }

    [Fact]
    public async Task Export_WhoseFinishedVideoIsGoneBeforeItHasItsName_Fails()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        var export = session.ExportAsync(() => ExportPath, default);
        Exporter.Complete();
        File.Delete(Assert.Single(Exporter.Exports).OutputPath);

        Assert.Equal(StudioExportOutcome.Failed, await FinishAsync(export));

        Assert.StartsWith("Studio export failed: ", Assert.Single(Errors));
        Assert.Empty(Projects.Load(id).Exports);
        Assert.Empty(ExportedPaths);
        Assert.Empty(FilesInTheVideoFolder());
    }

    [Fact]
    public async Task Export_Failure_IsAboutTheVideo_NotAboutTheNameItWasWrittenUnder()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        var export = session.ExportAsync(() => ExportPath, default);
        var writtenUnder = Assert.Single(Exporter.Exports).OutputPath;

        // The real exporter names the file it could not write.
        Exporter.Fail(new IOException($"The video could not be saved to {writtenUnder}. Access to the path is denied."));

        Assert.Equal(StudioExportOutcome.Failed, await FinishAsync(export));
        Assert.Equal(
            $"Studio export failed: The video could not be saved to {ExportPath}. Access to the path is denied.",
            Assert.Single(Errors));
    }

    [Fact]
    public async Task Export_PutsAnEditMadeDuringTheSaveDelayOnDiskFirst()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        session.SetCanvasPadding(0.2);
        Assert.Equal(0.06, Projects.Load(id).Canvas.Padding, Precision);
        double? paddingOnDiskWhenExportStarted = null;
        Exporter.ExportStarted = () => paddingOnDiskWhenExportStarted = Projects.Load(id).Canvas.Padding;

        var export = session.ExportAsync(() => ExportPath, default);

        Assert.Equal(0.2, Assert.NotNull(paddingOnDiskWhenExportStarted), Precision);
        Assert.Equal(0.2, Assert.Single(Exporter.Exports).Project.Canvas.Padding, Precision);
        Assert.False(session.HasUnsavedEdits);
        AssertLoggedInOrder("store.save", "exporter.export");

        Exporter.Complete();
        Assert.Equal(StudioExportOutcome.Exported, await FinishAsync(export));

        // The autosave that was pending when the export started does not run a second time.
        Advance(5000);
        Assert.Equal(1, LogCount("store.save"));
    }

    [Fact]
    public async Task Export_Failure_LeavesNoLink_AndReportsIt()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        var export = session.ExportAsync(() => ExportPath, default);

        Exporter.Fail(new IOException("There is not enough space on the disk."));

        Assert.Equal(StudioExportOutcome.Failed, await FinishAsync(export));
        Assert.False(session.IsExporting);
        Assert.Equal(0, session.ExportProgress, Precision);
        Assert.Equal("Studio export failed: There is not enough space on the disk.", Assert.Single(Errors));
        Assert.Equal(StudioEditorErrorKind.Export, Assert.Single(ErrorKinds));
        Assert.Empty(Projects.Load(id).Exports);
        Assert.True(session.HasNeverExported);
        Assert.True(session.Model!.HasUnexportedChanges);
        Assert.Empty(ExportedPaths);
        Assert.Empty(Exporter.Posters);
        Assert.True(session.IsEditable);
    }

    [Fact]
    public async Task Export_Cancellation_LeavesNoLink_AndSaysNothing()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        var export = session.ExportAsync(() => ExportPath, default);
        Exporter.Progress!.Report(0.4);
        Pump();

        session.CancelExport();

        Assert.True(Exporter.ExportCancellationToken.IsCancellationRequested);
        Assert.Equal(StudioExportOutcome.Cancelled, await FinishAsync(export));
        Assert.False(session.IsExporting);
        Assert.Equal(0, session.ExportProgress, Precision);
        Assert.Empty(Errors);
        Assert.Empty(Projects.Load(id).Exports);
        Assert.True(session.HasNeverExported);
        Assert.Empty(ExportedPaths);
        Assert.Empty(Exporter.Posters);
        Assert.Equal(StudioClosePrompt.NeverExported, session.GetClosePrompt());
    }

    [Fact]
    public async Task Export_ReportsProgressOnTheSessionThread()
    {
        var session = await OpenAsync(CreateProject());
        var export = session.ExportAsync(() => ExportPath, default);
        Changes.Clear();

        Exporter.Progress!.Report(0.25);
        Exporter.Progress.Report(0.5);
        Assert.Equal(0, session.ExportProgress, Precision);
        Assert.Equal(1, PostedCount);
        Pump();

        Assert.Equal(0.5, session.ExportProgress, Precision);
        Assert.Equal(new[] { StudioEditorChanges.Export }, Changes);

        Exporter.Progress.Report(7);
        Pump();
        Assert.Equal(1, session.ExportProgress, Precision);
        Exporter.Progress.Report(double.NaN);
        Pump();
        Assert.Equal(1, session.ExportProgress, Precision);

        session.CancelExport();
        await FinishAsync(export);

        // Progress that arrives after the export has ended belongs to nothing.
        Exporter.Progress.Report(0.9);
        Pump();
        Assert.Equal(0, session.ExportProgress, Precision);
    }

    [Fact]
    public async Task Export_IgnoresAPosterThatCannotBeWritten()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        Exporter.PosterFailure = new IOException("The poster could not be encoded.");
        var export = session.ExportAsync(() => ExportPath, default);

        Exporter.Complete();

        Assert.Equal(StudioExportOutcome.Exported, await FinishAsync(export));
        Assert.Empty(Errors);
        Assert.Single(Projects.Load(id).Exports);
        Assert.Equal(new[] { ExportPath }, ExportedPaths);
    }

    [Fact]
    public async Task Export_PausesPlaybackFirst()
    {
        var session = await OpenAsync(CreateProject());
        session.TogglePlayback();
        Preview.Calls.Clear();

        var export = session.ExportAsync(() => ExportPath, default);

        Assert.False(session.IsPlaying);
        Assert.Equal(new[] { "Pause" }, Preview.Calls);

        session.CancelExport();
        await FinishAsync(export);
    }

    [Fact]
    public async Task Export_IsRefusedWhileOneIsRunning()
    {
        var session = await OpenAsync(CreateProject());
        var first = session.ExportAsync(() => ExportPath, default);

        var second = session.ExportAsync(() => ExportPath, default);

        Assert.Equal(StudioExportOutcome.NotStarted, await FinishAsync(second));
        Assert.Single(Exporter.Exports);
        Assert.True(session.IsExporting);

        session.CancelExport();
        await FinishAsync(first);
    }

    [Fact]
    public async Task Export_IsRefusedWhenTheProjectIsUnavailable()
    {
        var session = await OpenAsync(CreateProject(writeScreenFile: false));

        Assert.Equal(StudioExportOutcome.NotStarted, await FinishAsync(session.ExportAsync(() => ExportPath, default)));

        Assert.Empty(Exporter.Exports);
        Assert.False(session.IsExporting);
    }

    [Fact]
    public async Task Export_DoesNotStartWhenTheEditsCannotBeSaved()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        session.SetCanvasPadding(0.2);
        Directory.Delete(Projects.GetPaths(id).ProjectDirectory, recursive: true);

        Assert.Equal(StudioExportOutcome.NotStarted, await FinishAsync(session.ExportAsync(() => ExportPath, default)));

        Assert.Empty(Exporter.Exports);
        Assert.False(session.IsExporting);
        Assert.StartsWith("Studio could not save this project: ", Assert.Single(Errors));
    }

    [Fact]
    public async Task Export_FailsWhenTheOutputPathCannotBeMade()
    {
        var session = await OpenAsync(CreateProject());

        var export = session.ExportAsync(() => throw new UnauthorizedAccessException("Access to the folder is denied."), default);

        Assert.Equal(StudioExportOutcome.Failed, await FinishAsync(export));
        Assert.Empty(Exporter.Exports);
        Assert.False(session.IsExporting);
        Assert.Equal("Studio export failed: Access to the folder is denied.", Assert.Single(Errors));
    }

    [Fact]
    public async Task Export_FailsWhenTheLinkCannotBeRecorded()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        var export = session.ExportAsync(() => ExportPath, default);
        Directory.Delete(Projects.GetPaths(id).ProjectDirectory, recursive: true);

        Exporter.Complete();

        Assert.Equal(StudioExportOutcome.Failed, await FinishAsync(export));
        Assert.False(session.IsExporting);
        Assert.StartsWith("Studio export failed: ", Assert.Single(Errors));
        Assert.Empty(ExportedPaths);
    }

    [Fact]
    public async Task AnEditAfterAnExport_CountsAsNotExported()
    {
        var session = await OpenAsync(CreateProject());
        var export = session.ExportAsync(() => ExportPath, default);
        Exporter.Complete();
        Assert.Equal(StudioExportOutcome.Exported, await FinishAsync(export));
        Assert.False(session.Model!.HasUnexportedChanges);

        session.SetCanvasPadding(0.3);

        Assert.True(session.Model.HasUnexportedChanges);
        Assert.False(session.HasNeverExported);

        session.Undo();
        Assert.False(session.Model.HasUnexportedChanges);
    }

    /// <summary>The name the app would give a video saved a few seconds after <see cref="StudioEditorSessionTestBase.ExportPath"/> was made.</summary>
    private string LaterPath => Path.Combine(Path.GetDirectoryName(ExportPath)!, "TinyClips 2026-10-03 at 12.00.07.mp4");

    /// <summary>The names of everything in the folder videos are saved to, in order.</summary>
    private string[] FilesInTheVideoFolder()
    {
        var folder = Path.GetDirectoryName(ExportPath)!;
        return Directory.Exists(folder)
            ? [.. Directory.GetFiles(folder).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)]
            : [];
    }
}
