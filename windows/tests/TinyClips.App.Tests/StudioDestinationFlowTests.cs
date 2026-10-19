using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Tests;

public sealed class StudioDestinationFlowTests : StudioViewModelTestBase
{
    [Fact]
    public async Task CancelledExportAsksWithConfiguredDefaultsAndStartsNothing()
    {
        var model = await OpenAsync(CreateProject());
        string? suggestedFolder = null;
        string? suggestedName = null;
        model.ChooseExportFile = (folder, name) =>
        {
            suggestedFolder = folder;
            suggestedName = name;
            return Task.FromResult<string?>(null);
        };
        var outcome = await model.ExportAsync();
        Assert.Equal(StudioExportOutcome.Cancelled, outcome);
        Assert.EndsWith("Videos", suggestedFolder, StringComparison.Ordinal);
        Assert.Equal("Recording.mp4", suggestedName);
        Assert.False(model.IsExporting);
        Assert.Empty(Projects.Load(model.ProjectId).Exports);
    }

    [Fact]
    public async Task SavedAndReopenedProjectDefaultsToItsOwnFolder()
    {
        var id = CreateProject();
        var folder = Path.Combine(Projects.RootDirectory, "Saved");
        Projects.SaveProjectFolder(id, folder, cancellationToken: TestContext.Current.CancellationToken);
        var file = Path.Combine(folder, "Saved.tinyclips");
        StudioProjectDestination.Remember(Projects, id, file);
        var model = await OpenAsync(id);
        model.ChooseExportFile = (place, name) =>
        {
            Assert.Equal(folder, place);
            Assert.Equal("Recording.mp4", name);
            return Task.FromResult<string?>(null);
        };
        Assert.Equal(StudioExportOutcome.Cancelled, await model.ExportAsync());
        Assert.Equal(file, model.SavedProjectFile);
    }

    [Fact]
    public async Task OverlappingPickerRequestsAreCoalesced()
    {
        var model = await OpenAsync(CreateProject());
        var selection = new TaskCompletionSource<string?>();
        var asks = 0;
        model.ChooseExportFile = (_, _) =>
        {
            asks++;
            return selection.Task;
        };
        var first = model.ExportAsync();
        Assert.Equal(StudioExportOutcome.NotStarted, await model.ExportAsync());
        selection.SetResult(null);
        Assert.Equal(StudioExportOutcome.Cancelled, await first);
        Assert.Equal(1, asks);
    }

    [Fact]
    public async Task SaveAsHasADistinctCommand()
    {
        var model = await OpenAsync(CreateProject());
        var save = 0;
        var saveAs = 0;
        model.SaveProjectRequested += (_, _) => save++;
        model.SaveProjectAsRequested += (_, _) => saveAs++;
        model.Run(StudioShortcutAction.SaveProject);
        model.Run(StudioShortcutAction.SaveProjectAs);
        Assert.Equal(1, save);
        Assert.Equal(1, saveAs);
    }
}
