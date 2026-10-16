using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>Pinning a project against automatic cleanup from its editor.</summary>
public sealed class StudioEditorSessionKeepTests : StudioEditorSessionTestBase
{
    [Fact]
    public async Task SetKeepSources_IsWrittenAtOnce_AndIsNotAnEdit()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        var modifiedAt = Projects.Load(id).ModifiedAt;
        Time.Advance(TimeSpan.FromMinutes(5));
        Preview.Calls.Clear();
        Changes.Clear();
        Log.Clear();

        Assert.False(session.KeepSources);
        Assert.True(session.SetKeepSources(true));

        Assert.True(session.KeepSources);
        Assert.True(Projects.Load(id).KeepSources);
        Assert.Equal(modifiedAt, Projects.Load(id).ModifiedAt);
        Assert.Equal(new[] { "store.setKeepSources" }, Log);
        Assert.Equal(new[] { StudioEditorChanges.Project }, Changes);

        // Nothing to undo, nothing to save, and nothing the preview draws differently.
        Assert.False(session.CanUndo);
        Assert.False(session.HasUnsavedEdits);
        Assert.Empty(Preview.Calls);
        Advance(5000);
        Assert.Equal(0, LogCount("store.save"));
        Assert.Empty(Errors);
    }

    [Fact]
    public async Task SetKeepSources_ToWhatItAlreadyIs_DoesNothing()
    {
        var session = await OpenAsync(CreateProject());
        Changes.Clear();
        Log.Clear();

        Assert.True(session.SetKeepSources(false));

        Assert.Empty(Log);
        Assert.Empty(Changes);
    }

    [Fact]
    public async Task KeepSources_StaysThroughEditsUndoAndTheSavesThatFollow()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);

        session.SetCanvasPadding(0.2);
        session.SetKeepSources(true);
        session.Undo();
        Advance(600);

        // Undo took the padding back and left the pin, and the save wrote both.
        Assert.True(session.KeepSources);
        Assert.Equal(0.06, session.Project!.Canvas.Padding, Precision);
        Assert.True(Projects.Load(id).KeepSources);
        Assert.Equal(0.06, Projects.Load(id).Canvas.Padding, Precision);

        session.Redo();
        Assert.True(session.SetKeepSources(false));
        Advance(600);

        Assert.False(Projects.Load(id).KeepSources);
        Assert.Equal(0.2, Projects.Load(id).Canvas.Padding, Precision);
    }

    [Fact]
    public async Task KeepSources_IsReadFromTheProject_AndFollowsWhatWasWrittenElsewhere()
    {
        var id = CreateProject();
        Projects.SetKeepSources(id, true);
        var session = await OpenAsync(id);

        Assert.True(session.KeepSources);

        // Another part of the app lets go of it; the session sees it with its next save.
        Projects.SetKeepSources(id, false);
        session.SetCanvasPadding(0.2);
        Advance(600);

        Assert.False(session.KeepSources);
        Assert.False(Projects.Load(id).KeepSources);
    }

    [Fact]
    public async Task SetKeepSources_DoesNotWaitForAnExportToEnd()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        var export = session.ExportAsync(() => ExportPath, default);
        Assert.False(session.IsEditable);

        Assert.True(session.SetKeepSources(true));

        Exporter.Complete();
        Assert.Equal(StudioExportOutcome.Exported, await FinishAsync(export));
        var saved = Projects.Load(id);
        Assert.True(saved.KeepSources);
        Assert.Single(saved.Exports);
        Assert.True(session.KeepSources);
    }

    [Fact]
    public async Task SetKeepSources_ThatCannotBeWritten_ReportsItAndKeepsWhatWasThere()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        Directory.Delete(Projects.GetPaths(id).ProjectDirectory, recursive: true);
        Changes.Clear();

        Assert.False(session.SetKeepSources(true));

        Assert.False(session.KeepSources);
        Assert.StartsWith("Studio could not change whether this project is kept: ", Assert.Single(Errors));
        Assert.Equal(StudioEditorErrorKind.Keep, Assert.Single(ErrorKinds));
        Assert.Empty(Changes);
        Assert.False(Directory.Exists(Projects.GetPaths(id).ProjectDirectory));
    }

    [Fact]
    public async Task SetKeepSources_IsRefusedOnceClosed_AndWhereNoProjectWasRead()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        await FinishAsync(session.CloseAsync());
        Log.Clear();

        Assert.False(session.SetKeepSources(true));
        Assert.Empty(Log);
        Assert.False(Projects.Load(id).KeepSources);

        var gone = await OpenAsync("3f0013cf-ba10-4453-af91-792b7882dae6");
        Log.Clear();

        Assert.False(gone.SetKeepSources(true));
        Assert.False(gone.KeepSources);
        Assert.Empty(Log);
        Assert.Empty(Errors);
    }

    [Fact]
    public async Task APinnedProject_IsLeftByTheCleanupThatFollowsItsEditorClosing()
    {
        var id = CreateProject();
        Projects.RecordExport(id, ExportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(ExportPath)!);
        File.WriteAllBytes(ExportPath, [7, 8, 9]);
        var session = await OpenAsync(id);
        session.SetKeepSources(true);
        await FinishAsync(session.CloseAsync());
        var rules = new StudioCleanupOptions(RetentionDays: 30, SizeCapBytes: 1);

        Time.Advance(TimeSpan.FromDays(90));

        Assert.Empty(Projects.Cleanup(rules).ProjectIdsDeleted);

        Projects.SetKeepSources(id, false);

        Assert.Equal([id], Projects.Cleanup(rules).ProjectIdsDeleted);
    }
}
