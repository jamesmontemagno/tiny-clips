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
        Assert.Equal(ExportPath, request.OutputPath);
        Assert.Equal(VideoCodec.Hevc, request.Codec);
        Assert.Equal(Projects.GetPaths(id).ScreenPath, request.Paths.ScreenPath);
        Assert.False(export.IsCompleted);

        Exporter.Complete();

        // Rendered, but the session has not been back on its own thread to record it.
        Assert.True(session.IsExporting);
        Assert.Empty(ExportedPaths);
        Assert.Equal(StudioExportOutcome.Exported, await FinishAsync(export));

        Assert.False(session.IsExporting);
        Assert.Equal(1, session.ExportProgress, Precision);
        Assert.Equal(new[] { ExportPath }, ExportedPaths);
        Assert.Equal(ExportPath, Assert.Single(Projects.Load(id).Exports).Path);
        Assert.False(session.HasNeverExported);
        Assert.False(session.Model!.HasUnexportedChanges);
        Assert.Single(Exporter.Posters);
        Assert.Empty(Errors);
        AssertLoggedInOrder("exporter.export", "exporter.poster", "store.recordExport");
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
}
