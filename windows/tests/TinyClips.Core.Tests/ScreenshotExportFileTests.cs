using TinyClips.Core.Editing;

namespace TinyClips.Core.Tests;

public sealed class ScreenshotExportFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TinyClipsTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ConcurrentNewExports_NeverReplaceEachOtherOrAnExistingFile()
    {
        var path = CreateDestination();
        var bothEncoded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var encoded = 0;
        async Task<string> ExportAsync(string contents) =>
            await ScreenshotExportFile.WriteNewAsync(path, async (temporary, token) =>
            {
                await File.WriteAllTextAsync(temporary, contents, token);
                if (Interlocked.Increment(ref encoded) == 2) bothEncoded.TrySetResult();
                await bothEncoded.Task.WaitAsync(token);
            }, TestContext.Current.CancellationToken);

        var outputs = await Task.WhenAll(ExportAsync("first"), ExportAsync("second"));

        Assert.NotEqual(outputs[0], outputs[1]);
        Assert.Equal("original synthetic export", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal("first", await File.ReadAllTextAsync(outputs[0], TestContext.Current.CancellationToken));
        Assert.Equal("second", await File.ReadAllTextAsync(outputs[1], TestContext.Current.CancellationToken));
        Assert.Equal(3, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public async Task CanceledNewExport_PreservesExistingFileAndRemovesStagingFile()
    {
        var path = CreateDestination();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScreenshotExportFile.WriteNewAsync(path, async (temporary, token) =>
        {
            await File.WriteAllTextAsync(temporary, "partial synthetic export", token);
            cancellation.Cancel();
        }, cancellation.Token));

        Assert.Equal("original synthetic export", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task SuccessfulEncode_ReplacesCompleteFileAndRemovesStagingFile()
    {
        var path = CreateDestination();
        await ScreenshotExportFile.WriteAsync(path,
            (temporary, token) => File.WriteAllTextAsync(temporary, "complete synthetic export", token), TestContext.Current.CancellationToken);

        Assert.Equal("complete synthetic export", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CanceledEncode_PreservesDestinationAndRemovesPartialFile()
    {
        var path = CreateDestination();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScreenshotExportFile.WriteAsync(path, async (temporary, token) =>
        {
            await File.WriteAllTextAsync(temporary, "partial synthetic export", token);
            cancellation.Cancel();
        }, cancellation.Token));

        Assert.Equal("original synthetic export", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task FailedEncode_PreservesDestinationAndRemovesPartialFile()
    {
        var path = CreateDestination();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ScreenshotExportFile.WriteAsync(path, async (temporary, token) =>
        {
            await File.WriteAllTextAsync(temporary, "partial synthetic export", token);
            throw new InvalidOperationException("Synthetic encoding failure");
        }, TestContext.Current.CancellationToken));

        Assert.Equal("original synthetic export", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CancellationBeforeStart_DoesNotInvokeEncoderOrTouchDestination()
    {
        var path = CreateDestination();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var called = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScreenshotExportFile.WriteAsync(path, (_, _) =>
        {
            called = true;
            return Task.CompletedTask;
        }, cancellation.Token));

        Assert.False(called);
        Assert.Equal("original synthetic export", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task SnapshotSave_FinishesWithoutMarkingLaterEditsClean()
    {
        var path = CreateDestination();
        var document = new ScreenshotDocumentRevision();
        document.ReplaceDocument();
        document.MarkEdited();
        var revision = document.Current;
        await ScreenshotExportFile.WriteAsync(path, async (temporary, token) =>
        {
            document.MarkEdited();
            await File.WriteAllTextAsync(temporary, "older synthetic snapshot", token);
        }, TestContext.Current.CancellationToken);

        Assert.False(document.MarkSaved(revision));
        Assert.True(document.IsDirty);
        Assert.Equal("older synthetic snapshot", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    private string CreateDestination()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "synthetic-export.png");
        File.WriteAllText(path, "original synthetic export");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
