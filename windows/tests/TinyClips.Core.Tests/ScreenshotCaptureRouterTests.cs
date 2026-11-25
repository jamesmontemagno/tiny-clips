using TinyClips.Core.Capture;
using TinyClips.Core.Editing;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class ScreenshotCaptureRouterTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-artifacts", Guid.NewGuid().ToString("N"));
    private readonly MemorySettings _memory = new();
    private readonly CaptureSettings _settings;
    private readonly CountingStorage _storage;
    private readonly ClipAnalyticsService _analytics;
    private readonly RecentCaptureService _recent;
    private readonly ScreenshotTemporaryFileStore _temporary;
    private readonly IScreenshotService _screenshots;
    private readonly ScreenshotCaptureRouter _router;
    private readonly CapturedFrame _frame = new(new byte[8 * 8 * 4], 8, 8);

    public ScreenshotCaptureRouterTests()
    {
        _settings = new CaptureSettings(_memory)
        {
            UseDefaultSaveDirectories = false,
            ScreenshotSaveDirectory = Path.Combine(_root, "captures"),
            ImageFormat = ImageFormat.Png,
        };
        var files = new TinyClips.Core.Services.FileSystem();
        _storage = new CountingStorage(new ClipStorageService(_settings, new FileNameService(_settings), files));
        _analytics = new ClipAnalyticsService(_memory);
        _recent = new RecentCaptureService(_memory, files, TimeProvider.System);
        _temporary = new ScreenshotTemporaryFileStore(Path.Combine(_root, "temporary"));
        _screenshots = new ScreenshotService(new ScreenCaptureService(), new MonitorService(), _storage, _settings, _analytics);
        _router = new ScreenshotCaptureRouter(_screenshots, _storage, _analytics, _recent, _temporary);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task Route_AllValidOutputsCreateOnlyTheirRequestedEffects(int mask)
    {
        var selection = new ScreenshotOutputSelection((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
        string? copiedPath = null;
        ScreenshotEditorRequest? editor = null;
        var result = await _router.RouteAsync(_frame, selection, fitsEditor: true, useMemoryEditor: true,
            alreadyBranded: true,
            path => { copiedPath = path; return Task.CompletedTask; },
            request => { editor = request; return Task.FromResult(ScreenshotEditorOutcome.Loaded); });

        Assert.True(result.HasOutput);
        Assert.Equal(selection.SaveToDisk, result.IsPermanent);
        Assert.Equal(selection.SaveToDisk && !selection.OpenEditor, result.ShouldPresentSavedResult);
        Assert.Equal(selection.CopyToClipboard, result.Copied);
        Assert.Equal(selection.OpenEditor ? ScreenshotEditorOutcome.Loaded : ScreenshotEditorOutcome.NotRequested, result.Editor);
        Assert.Equal(selection.SaveToDisk ? 1 : 0, _storage.GenerateCalls);
        Assert.Equal(selection.SaveToDisk, Directory.Exists(_settings.ScreenshotSaveDirectory));
        Assert.Equal(selection.SaveToDisk ? 1 : 0, _recent.GetRecentCaptures().Count);
        Assert.Equal(1, _analytics.GetLifetimeTotals().ScreenshotCount);
        Assert.True(result.AnalyticsRecorded);
        Assert.Equal(selection.SaveToDisk || selection.OpenEditor, File.Exists(result.Path));
        if (selection.CopyToClipboard) Assert.Equal(result.Path, copiedPath);
        else Assert.Null(copiedPath);
        if (selection.OpenEditor)
        {
            Assert.NotNull(editor);
            Assert.Same(_frame, editor.Frame);
            Assert.True(editor.UseMemory);
            Assert.Equal(!selection.SaveToDisk, editor.IsTemporary);
            Assert.Equal(result.Path, await editor.File);
            Assert.Equal(!selection.SaveToDisk, editor.Lease is not null);
            editor.Lease?.Dispose();
        }
        else Assert.Null(editor);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task Route_OversizedScrollingNeverImplicitlySaves(int mask)
    {
        var selection = new ScreenshotOutputSelection((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
        var editorCalled = false;
        var result = await _router.RouteAsync(_frame, selection, fitsEditor: false, useMemoryEditor: true,
            alreadyBranded: true, _ => Task.CompletedTask,
            _ => { editorCalled = true; return Task.FromResult(ScreenshotEditorOutcome.Loaded); });

        Assert.False(editorCalled);
        Assert.Equal(selection.OpenEditor, result.EditorUnavailable);
        Assert.Equal(selection.SaveToDisk, Directory.Exists(_settings.ScreenshotSaveDirectory));
        Assert.Equal(selection.SaveToDisk ? 1 : 0, _storage.GenerateCalls);
        Assert.Equal(selection.SaveToDisk ? 1 : 0, _recent.GetRecentCaptures().Count);
        Assert.Equal(selection.SaveToDisk || selection.CopyToClipboard, result.HasOutput);
        Assert.Equal(selection.SaveToDisk || selection.CopyToClipboard ? 1 : 0,
            _analytics.GetLifetimeTotals().ScreenshotCount);
    }

    [Fact]
    public async Task CopyFailure_RetryDoesNotCountTwiceOrSave()
    {
        var selection = new ScreenshotOutputSelection(false, true, false);
        var failed = await _router.RouteAsync(_frame, selection, true, true, true,
            _ => Task.FromException(new IOException("clipboard unavailable")), NeverOpenEditor);

        Assert.False(failed.HasOutput);
        Assert.IsType<IOException>(failed.CopyError);
        Assert.False(failed.AnalyticsRecorded);
        Assert.Equal(0, _analytics.GetLifetimeTotals().ScreenshotCount);
        var retry = await _router.RouteAsync(_frame, selection, true, true, true,
            _ => Task.CompletedTask, NeverOpenEditor, recordCapture: !failed.AnalyticsRecorded);

        Assert.True(retry.Copied);
        Assert.True(retry.HasOutput);
        Assert.Equal(1, _analytics.GetLifetimeTotals().ScreenshotCount);
        Assert.Equal(0, _storage.GenerateCalls);
        Assert.False(Directory.Exists(_settings.ScreenshotSaveDirectory));
        Assert.Empty(_recent.GetRecentCaptures());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedTemporaryEditor_WithFailedCopyOrPendingWriteRequiresRecovery(bool failWrite)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new HeldScreenshotService(_screenshots, release.Task, failStream: failWrite);
        var router = new ScreenshotCaptureRouter(service, _storage, _analytics, _recent, _temporary);
        var selection = new ScreenshotOutputSelection(false, true, true);
        var progress = new ScreenshotRecoveryProgress(selection, fitsEditor: true);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var routing = router.RouteAsync(_frame, selection, true, true, true,
            _ => Task.FromException(new IOException("Synthetic clipboard failure")),
            request =>
            {
                request.Lease?.Dispose();
                closed.SetResult();
                return Task.FromResult(ScreenshotEditorOutcome.Closed);
            });
        await closed.Task.WaitAsync(TestContext.Current.CancellationToken);
        release.SetResult();
        var result = await routing.WaitAsync(TestContext.Current.CancellationToken);
        progress.RecordResult(selection, result);

        Assert.False(result.HasOutput);
        if (failWrite)
        {
            Assert.IsType<IOException>(result.WriteError);
            Assert.Null(result.CopyError);
        }
        else Assert.IsType<IOException>(result.CopyError);
        Assert.True(progress.RequiresRecovery(result, editorIsOpen: false, editorHasPermanentSave: false));
        Assert.False(progress.RequiresRecovery(result, editorIsOpen: false, editorHasPermanentSave: true));
    }

    [Fact]
    public async Task ClosedEditorOnlyCaptureRemainsAnIntentionalDiscardWhenTheWriteFails()
    {
        var service = new HeldScreenshotService(_screenshots, Task.CompletedTask, failStream: true);
        var router = new ScreenshotCaptureRouter(service, _storage, _analytics, _recent, _temporary);
        var selection = new ScreenshotOutputSelection(false, false, true);
        var progress = new ScreenshotRecoveryProgress(selection, fitsEditor: true);
        var result = await router.RouteAsync(_frame, selection, true, true, true, _ => Task.CompletedTask,
            request => { request.Lease?.Dispose(); return Task.FromResult(ScreenshotEditorOutcome.Closed); });
        progress.RecordResult(selection, result);

        Assert.IsType<IOException>(result.WriteError);
        Assert.False(result.HasOutput);
        Assert.False(progress.RequiresRecovery(result, editorIsOpen: false, editorHasPermanentSave: false));
    }

    [Fact]
    public async Task FailedEditorAfterSuccessfulCopy_RetriesEditorWithoutRepublishingClipboard()
    {
        var selection = new ScreenshotOutputSelection(false, true, true);
        var progress = new ScreenshotRecoveryProgress(selection, fitsEditor: true);
        var copies = 0;
        var editorAttempts = 0;
        Task Copy(string _) { copies++; return Task.CompletedTask; }
        Task<ScreenshotEditorOutcome> Open(ScreenshotEditorRequest request)
        {
            request.Lease?.Dispose();
            return Task.FromResult(++editorAttempts < 3 ? ScreenshotEditorOutcome.Failed : ScreenshotEditorOutcome.Loaded);
        }

        var first = await _router.RouteAsync(_frame, selection, true, true, true, Copy, Open);
        progress.RecordResult(selection, first);
        Assert.True(first.Copied);
        Assert.True(progress.RequiresRecovery(first, editorIsOpen: false, editorHasPermanentSave: false));
        Assert.False(progress.RequiresRecovery(first, editorIsOpen: false, editorHasPermanentSave: true));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var retrySelection = progress.RetrySelection;
            Assert.False(retrySelection.CopyToClipboard);
            Assert.True(retrySelection.OpenEditor);
            var retry = await _router.RouteAsync(_frame, retrySelection, true, true, true, Copy, Open, recordCapture: false);
            progress.RecordResult(retrySelection, retry);
            Assert.Equal(attempt == 0,
                progress.RequiresRecovery(retry, editorIsOpen: retry.Editor == ScreenshotEditorOutcome.Loaded,
                    editorHasPermanentSave: false));
        }
        Assert.Equal(1, copies);
        Assert.Equal(1, _analytics.GetLifetimeTotals().ScreenshotCount);
        Assert.False(Directory.Exists(_settings.ScreenshotSaveDirectory));
        progress.MarkEditorFailed();
        Assert.True(progress.RequiresRecovery(first, editorIsOpen: false, editorHasPermanentSave: false));
        Assert.False(progress.RetrySelection.CopyToClipboard);
    }

    [Fact]
    public async Task OversizedEditorFailureAfterCopyRetry_KeepsRecoveryWithoutRecopying()
    {
        var selection = new ScreenshotOutputSelection(false, true, true);
        var progress = new ScreenshotRecoveryProgress(selection, fitsEditor: false);
        var copies = 0;
        Task Copy(string _)
        {
            copies++;
            return copies == 1 ? Task.FromException(new IOException("Synthetic clipboard failure")) : Task.CompletedTask;
        }
        var first = await _router.RouteAsync(_frame, selection, false, true, true, Copy, NeverOpenEditor);
        progress.RecordResult(selection, first);
        Assert.True(progress.RequiresRecovery(first, editorIsOpen: false, editorHasPermanentSave: false));

        var retrySelection = progress.RetrySelection;
        Assert.True(retrySelection.CopyToClipboard);
        Assert.False(retrySelection.OpenEditor);
        var retry = await _router.RouteAsync(_frame, retrySelection, false, true, true, Copy, NeverOpenEditor,
            recordCapture: !first.AnalyticsRecorded);
        progress.RecordResult(retrySelection, retry);
        Assert.True(retry.Copied);
        Assert.True(progress.RequiresRecovery(retry, editorIsOpen: false, editorHasPermanentSave: false));
        Assert.False(progress.CanRetry);

        var folder = Path.Combine(_root, "chosen");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "recovered.png");
        await File.WriteAllTextAsync(destination, "picker placeholder", TestContext.Current.CancellationToken);
        await _router.SaveRecoveredAsAsync(_frame, destination, alreadyBranded: true,
            recordCapture: false, TestContext.Current.CancellationToken);
        Assert.Equal((8, 8), ScreenshotBackgroundDecoder.ReadDimensions(destination, TestContext.Current.CancellationToken));
        Assert.Equal(2, copies);
        Assert.Equal(1, _analytics.GetLifetimeTotals().ScreenshotCount);
    }

    [Fact]
    public async Task OversizedEditorAfterSuccessfulCopy_OffersSaveWithoutRetry()
    {
        var selection = new ScreenshotOutputSelection(false, true, true);
        var progress = new ScreenshotRecoveryProgress(selection, fitsEditor: false);
        var result = await _router.RouteAsync(_frame, selection, false, true, true,
            _ => Task.CompletedTask, NeverOpenEditor);
        progress.RecordResult(selection, result);

        Assert.True(result.Copied);
        Assert.True(result.EditorUnavailable);
        Assert.True(progress.RequiresRecovery(result, editorIsOpen: false, editorHasPermanentSave: false));
        Assert.False(progress.CanRetry);
        Assert.False(progress.RetrySelection.CopyToClipboard);
        Assert.False(Directory.Exists(_settings.ScreenshotSaveDirectory));
        var saved = await _router.SaveRecoveredAsync(_frame, alreadyBranded: true, recordCapture: false);
        Assert.True(File.Exists(saved));
        Assert.Equal(1, _analytics.GetLifetimeTotals().ScreenshotCount);
    }

    [Fact]
    public async Task PersistentRetry_DoesNotCountAnAlreadyRecordedCaptureTwice()
    {
        var first = await _router.RouteAsync(_frame, new(false, true, false), true, true, true,
            _ => Task.FromException(new IOException("clipboard unavailable")), NeverOpenEditor);
        Assert.False(first.AnalyticsRecorded);

        var retry = await _router.RouteAsync(_frame, new(true, false, false), true, true, true,
            _ => Task.CompletedTask, NeverOpenEditor, recordCapture: !first.AnalyticsRecorded);

        Assert.True(retry.IsPermanent);
        Assert.True(File.Exists(retry.Path));
        Assert.Equal(1, _analytics.GetLifetimeTotals().ScreenshotCount);
        Assert.Single(_recent.GetRecentCaptures());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentWriteFailure_CountsCaptureOnceAcrossRetryOrRecoverySave(bool explicitSave)
    {
        var failing = new HeldScreenshotService(_screenshots, Task.CompletedTask, failPath: true);
        var router = new ScreenshotCaptureRouter(failing, _storage, _analytics, _recent, _temporary);
        var selection = new ScreenshotOutputSelection(true, false, false);

        var failed = await router.RouteAsync(_frame, selection, true, true, true,
            _ => Task.CompletedTask, NeverOpenEditor);

        Assert.False(failed.HasOutput);
        Assert.IsType<IOException>(failed.WriteError);
        Assert.False(failed.AnalyticsRecorded);
        Assert.Equal(0, _analytics.GetLifetimeTotals().ScreenshotCount);
        Assert.Empty(_recent.GetRecentCaptures());

        if (explicitSave)
        {
            var saved = await _router.SaveRecoveredAsync(_frame, alreadyBranded: true,
                recordCapture: !failed.AnalyticsRecorded);
            Assert.True(File.Exists(saved));
        }
        else
        {
            var retry = await _router.RouteAsync(_frame, selection, true, true, true,
                _ => Task.CompletedTask, NeverOpenEditor, recordCapture: !failed.AnalyticsRecorded);
            Assert.True(retry.HasOutput);
        }

        Assert.Equal(1, _analytics.GetLifetimeTotals().ScreenshotCount);
        Assert.Single(_recent.GetRecentCaptures());
    }

    [Fact]
    public async Task OversizedEditorOnly_ExplicitRecoverySaveCreatesOnePermanentCapture()
    {
        var result = await _router.RouteAsync(_frame, new(false, false, true), false, true, true,
            _ => Task.CompletedTask, NeverOpenEditor);
        Assert.False(result.HasOutput);
        Assert.True(result.EditorUnavailable);
        Assert.False(Directory.Exists(_settings.ScreenshotSaveDirectory));

        var path = await _router.SaveRecoveredAsync(_frame, alreadyBranded: true, recordCapture: !result.AnalyticsRecorded);

        Assert.True(File.Exists(path));
        Assert.Equal(_settings.ScreenshotSaveDirectory, Path.GetDirectoryName(path));
        Assert.Equal(path, Assert.Single(_recent.GetRecentCaptures()).Path);
        Assert.Equal(1, _storage.GenerateCalls);
        Assert.Equal(1, _analytics.GetLifetimeTotals().ScreenshotCount);
    }

    [Theory]
    [InlineData(".png", ImageFormat.Jpeg)]
    [InlineData(".jpg", ImageFormat.Png)]
    [InlineData(".webp", ImageFormat.Png)]
    public async Task RecoverySaveAs_ReplacesPickerPlaceholderWithChosenFormatAndScale(
        string extension, ImageFormat configuredFormat)
    {
        _settings.ImageFormat = configuredFormat;
        _settings.ScreenshotScale = 50;
        var outputDirectory = Path.Combine(_root, "chosen");
        Directory.CreateDirectory(outputDirectory);
        var destination = Path.Combine(outputDirectory, "recovered" + extension);
        await File.WriteAllTextAsync(destination, "picker-created placeholder", TestContext.Current.CancellationToken);

        var saved = await _router.SaveRecoveredAsAsync(_frame, destination, alreadyBranded: true,
            recordCapture: false, TestContext.Current.CancellationToken);

        Assert.Equal(destination, saved);
        var bytes = await File.ReadAllBytesAsync(saved, TestContext.Current.CancellationToken);
        if (extension == ".png") Assert.Equal(new byte[] { 137, 80, 78, 71 }, bytes[..4]);
        if (extension == ".jpg") Assert.Equal(new byte[] { 255, 216 }, bytes[..2]);
        if (extension == ".webp")
        {
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        }
        Assert.Equal((4, 4), ScreenshotBackgroundDecoder.ReadDimensions(saved, TestContext.Current.CancellationToken));
        Assert.Equal(destination, Assert.Single(_recent.GetRecentCaptures()).Path);
        Assert.Equal(0, _storage.GenerateCalls);
        Assert.Equal(0, _analytics.GetLifetimeTotals().ScreenshotCount);
        Assert.Empty(Directory.GetFiles(outputDirectory, ".tinyclips-export-*"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoverySaveAs_ForwardsTheBrandingDecision(bool alreadyBranded)
    {
        var observed = new HeldScreenshotService(_screenshots, Task.CompletedTask);
        var router = new ScreenshotCaptureRouter(observed, _storage, _analytics, _recent, _temporary);
        var folder = Path.Combine(_root, "chosen");
        Directory.CreateDirectory(folder);

        await router.SaveRecoveredAsAsync(_frame, Path.Combine(folder, "image.png"),
            alreadyBranded, recordCapture: false, TestContext.Current.CancellationToken);

        Assert.Equal(!alreadyBranded, observed.LastApplyBranding);
    }

    [Fact]
    public async Task RecoverySaveAs_LeavesFrameAvailableWhenWritingFails()
    {
        var failure = new IOException("Synthetic encoding failure");
        var failing = new HeldScreenshotService(_screenshots, Task.CompletedTask) { StreamFailure = failure };
        var router = new ScreenshotCaptureRouter(failing, _storage, _analytics, _recent, _temporary);
        var folder = Path.Combine(_root, "chosen");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "selected.png");
        await File.WriteAllTextAsync(destination, "original", TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<IOException>(() => router.SaveRecoveredAsAsync(
            _frame, destination, alreadyBranded: true, recordCapture: false, TestContext.Current.CancellationToken));

        Assert.Same(failure, error);
        Assert.Equal(8 * 8 * 4, _frame.BgraPixels.Length);
        Assert.Empty(_recent.GetRecentCaptures());
        Assert.Empty(Directory.GetFiles(folder, ".tinyclips-export-*"));

        failing.StreamFailure = null;
        await router.SaveRecoveredAsAsync(_frame, destination,
            alreadyBranded: true, recordCapture: false, TestContext.Current.CancellationToken);
        Assert.Equal((8, 8), ScreenshotBackgroundDecoder.ReadDimensions(destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TemporaryEditorLeaseSurvivesRoutingUntilEditorCloses()
    {
        ScreenshotTemporaryFileLease? editorLease = null;
        var result = await _router.RouteAsync(_frame, new(false, false, true), true, true, true,
            _ => Task.CompletedTask,
            request =>
            {
                editorLease = request.Lease;
                return Task.FromResult(ScreenshotEditorOutcome.Loaded);
            });
        Assert.NotNull(editorLease);
        Assert.Equal(new TemporaryFilesPurgeResult(0, 1),
            _temporary.Purge(TimeSpan.Zero, DateTimeOffset.UtcNow.AddSeconds(1)));
        editorLease.Dispose();
        Assert.Equal(new TemporaryFilesPurgeResult(0, 0),
            _temporary.Purge(TimeSpan.Zero, DateTimeOffset.UtcNow.AddSeconds(1)));
        Assert.False(File.Exists(result.Path));
    }

    [Fact]
    public async Task EncodingAndClipboardPublicationRemainLeasedUntilCopyCompletes()
    {
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var copyStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCopy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new HeldScreenshotService(_screenshots, releaseWrite.Task);
        var router = new ScreenshotCaptureRouter(held, _storage, _analytics, _recent, _temporary);
        var routing = router.RouteAsync(_frame, new(false, true, false), true, true, true,
            async path =>
            {
                copyStarted.TrySetResult(path);
                await releaseCopy.Task;
            }, NeverOpenEditor);

        Assert.Equal(new TemporaryFilesPurgeResult(0, 1),
            _temporary.Purge(TimeSpan.Zero, DateTimeOffset.UtcNow.AddSeconds(1)));
        releaseWrite.TrySetResult();
        var path = await copyStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new TemporaryFilesPurgeResult(0, 1),
            _temporary.Purge(TimeSpan.Zero, DateTimeOffset.UtcNow.AddSeconds(1)));
        releaseCopy.TrySetResult();
        Assert.True((await routing.WaitAsync(TestContext.Current.CancellationToken)).Copied);

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_temporary.DirectoryPath));
    }

    [Fact]
    public async Task TemporarySetupFailureRetainsFrameForRecoveryWithoutGeneratingCapturePath()
    {
        Directory.CreateDirectory(_root);
        var blocked = Path.Combine(_root, "not-a-directory");
        await File.WriteAllTextAsync(blocked, "synthetic blocker", TestContext.Current.CancellationToken);
        var temporary = new ScreenshotTemporaryFileStore(blocked);
        var screenshots = new ScreenshotService(new ScreenCaptureService(), new MonitorService(), _storage, _settings, _analytics);
        var router = new ScreenshotCaptureRouter(screenshots, _storage, _analytics, _recent, temporary);
        var result = await router.RouteAsync(_frame, new(false, true, false), true, true, true,
            _ => Task.CompletedTask, NeverOpenEditor);

        Assert.False(result.HasOutput);
        Assert.IsType<IOException>(result.WriteError);
        Assert.False(result.AnalyticsRecorded);
        Assert.Equal(0, _analytics.GetLifetimeTotals().ScreenshotCount);
        Assert.False(Directory.Exists(_settings.ScreenshotSaveDirectory));
        Assert.Equal(0, _storage.GenerateCalls);
        _settings.ScreenshotSaveDirectory = Path.Combine(_root, "recovered-captures");
        var saved = await router.SaveRecoveredAsync(_frame, alreadyBranded: true, recordCapture: !result.AnalyticsRecorded);
        Assert.True(File.Exists(saved));
        Assert.Equal(1, _analytics.GetLifetimeTotals().ScreenshotCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EditorMemoryPathCanOpenBeforeWritingButScaledPathWaits(bool useMemory)
    {
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = new TaskCompletionSource<ScreenshotEditorRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new HeldScreenshotService(_screenshots, releaseWrite.Task);
        var router = new ScreenshotCaptureRouter(held, _storage, _analytics, _recent, _temporary);
        var routing = router.RouteAsync(_frame, new(false, false, true), true, useMemory, true,
            _ => Task.CompletedTask,
            request =>
            {
                opened.TrySetResult(request);
                return Task.FromResult(ScreenshotEditorOutcome.Loaded);
            });

        if (useMemory)
        {
            var request = await opened.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(request.File.IsCompleted);
            Assert.Equal(new TemporaryFilesPurgeResult(0, 1),
                _temporary.Purge(TimeSpan.Zero, DateTimeOffset.UtcNow.AddSeconds(1)));
        }
        else Assert.False(opened.Task.IsCompleted);
        releaseWrite.TrySetResult();
        var result = await routing.WaitAsync(TestContext.Current.CancellationToken);
        var editor = await opened.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(result.HasOutput);
        Assert.Equal(useMemory, editor.UseMemory);
        Assert.Equal(result.Path, await editor.File);
        editor.Lease?.Dispose();
    }

    [Fact]
    public async Task FailedEditorReleasesTemporarySourceAndCanBeExplicitlySaved()
    {
        var result = await _router.RouteAsync(_frame, new(false, false, true), true, true, true,
            _ => Task.CompletedTask,
            request =>
            {
                request.Lease?.Dispose();
                return Task.FromResult(ScreenshotEditorOutcome.Failed);
            });
        Assert.False(result.HasOutput);
        Assert.False(File.Exists(result.Path));
        Assert.False(result.AnalyticsRecorded);
        Assert.Equal(0, _analytics.GetLifetimeTotals().ScreenshotCount);

        var saved = await _router.SaveRecoveredAsync(_frame, alreadyBranded: true, recordCapture: !result.AnalyticsRecorded);

        Assert.NotEqual(result.Path, saved);
        Assert.Equal((8, 8), ScreenshotBackgroundDecoder.ReadDimensions(saved, TestContext.Current.CancellationToken));
        Assert.Equal(saved, Assert.Single(_recent.GetRecentCaptures()).Path);
        Assert.Equal(1, _analytics.GetLifetimeTotals().ScreenshotCount);
    }

    [Theory]
    [InlineData(true, ScreenshotEditorOutcome.Loaded, ScreenshotEditorOutcome.Closed)]
    [InlineData(false, ScreenshotEditorOutcome.Loaded, ScreenshotEditorOutcome.Loaded)]
    [InlineData(true, ScreenshotEditorOutcome.Failed, ScreenshotEditorOutcome.Failed)]
    [InlineData(true, ScreenshotEditorOutcome.ClosedBeforeLoad, ScreenshotEditorOutcome.ClosedBeforeLoad)]
    public void EditorCloseWhileFileIsPending_PreservesWhetherTheImageWasDelivered(
        bool closed, ScreenshotEditorOutcome initial, ScreenshotEditorOutcome expected)
    {
        Assert.Equal(expected, ScreenshotEditorOutcomePolicy.AfterPendingWrite(
            initial, closed));
    }

    [Theory]
    [InlineData(ScreenshotEditorOutcome.ClosedBeforeLoad, 0)]
    [InlineData(ScreenshotEditorOutcome.Failed, 0)]
    [InlineData(ScreenshotEditorOutcome.Closed, 1)]
    [InlineData(ScreenshotEditorOutcome.Loaded, 1)]
    public async Task EditorOnlyAnalyticsRequireImageDelivery(ScreenshotEditorOutcome outcome, int expected)
    {
        var result = await _router.RouteAsync(_frame, new(false, false, true), true, true, true,
            _ => Task.CompletedTask,
            request =>
            {
                request.Lease?.Dispose();
                return Task.FromResult(outcome);
            });
        Assert.Equal(expected == 1, result.AnalyticsRecorded);
        Assert.Equal(expected, _analytics.GetLifetimeTotals().ScreenshotCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedTemporaryWriteKeepsTheImageRecoverable(bool openEditor)
    {
        var failed = new HeldScreenshotService(_screenshots, Task.CompletedTask, failStream: true);
        var router = new ScreenshotCaptureRouter(failed, _storage, _analytics, _recent, _temporary);
        ScreenshotTemporaryFileLease? editorLease = null;
        var result = await router.RouteAsync(_frame, new(false, !openEditor, openEditor), true, true, true,
            _ => Task.CompletedTask,
            request =>
            {
                editorLease = request.Lease;
                return Task.FromResult(ScreenshotEditorOutcome.Loaded);
            });

        Assert.IsType<IOException>(result.WriteError);
        Assert.Equal(openEditor, result.HasOutput);
        Assert.False(Directory.Exists(_settings.ScreenshotSaveDirectory));
        Assert.Empty(Directory.GetFiles(_temporary.DirectoryPath));
        Assert.Equal(openEditor ? 1 : 0, _analytics.GetLifetimeTotals().ScreenshotCount);
        editorLease?.Dispose();
        var saved = await router.SaveRecoveredAsync(_frame, alreadyBranded: true, recordCapture: !result.AnalyticsRecorded);
        Assert.True(File.Exists(saved));
        Assert.Equal(1, _analytics.GetLifetimeTotals().ScreenshotCount);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task RoutingForwardsTheBrandingDecisionToBothPermanentAndTemporaryEncoders(bool save, bool alreadyBranded)
    {
        var observed = new HeldScreenshotService(_screenshots, Task.CompletedTask);
        var router = new ScreenshotCaptureRouter(observed, _storage, _analytics, _recent, _temporary);
        var result = await router.RouteAsync(_frame, new(save, !save, false), true, true, alreadyBranded,
            _ => Task.CompletedTask, NeverOpenEditor);

        Assert.True(result.HasOutput);
        Assert.Equal(!alreadyBranded, observed.LastApplyBranding);
    }

    private static Task<ScreenshotEditorOutcome> NeverOpenEditor(ScreenshotEditorRequest _) =>
        throw new InvalidOperationException("The editor should not have been opened.");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class CountingStorage(IClipStorageService inner) : IClipStorageService
    {
        public int GenerateCalls { get; private set; }
        public string FileExtensionFor(CaptureType type) => inner.FileExtensionFor(type);
        public string OutputDirectory(CaptureType type) => inner.OutputDirectory(type);
        public string GenerateFilePath(CaptureType type, string? fileExtension = null, string? stemSuffix = null)
        {
            GenerateCalls++;
            return inner.GenerateFilePath(type, fileExtension, stemSuffix);
        }
    }

    private sealed class HeldScreenshotService(
        IScreenshotService inner, Task release, bool failStream = false, bool failPath = false) : IScreenshotService
    {
        public bool? LastApplyBranding { get; private set; }
        public Exception? StreamFailure { get; set; }
        public Task<string> CaptureFullScreenAsync(CancellationToken cancellationToken = default) =>
            inner.CaptureFullScreenAsync(cancellationToken);
        public Task<string> CaptureRegionAsync(PixelRect region, CancellationToken cancellationToken = default) =>
            inner.CaptureRegionAsync(region, cancellationToken);
        public Task<string> CaptureTargetAsync(CaptureTarget target, PixelRect? region = null, CancellationToken cancellationToken = default) =>
            inner.CaptureTargetAsync(target, region, cancellationToken);
        public async Task<string> SaveFrameAsync(CapturedFrame frame, bool applyBranding = true, CancellationToken cancellationToken = default)
        {
            LastApplyBranding = applyBranding;
            await release;
            return await inner.SaveFrameAsync(frame, applyBranding, cancellationToken);
        }
        public async Task WriteFrameAsync(CapturedFrame frame, string path, bool applyBranding = true, CancellationToken cancellationToken = default)
        {
            LastApplyBranding = applyBranding;
            await release;
            if (failPath) throw new IOException("Synthetic persistent write failure");
            await inner.WriteFrameAsync(frame, path, applyBranding, cancellationToken);
        }
        public async Task WriteFrameAsync(CapturedFrame frame, Stream destination, bool applyBranding = true, CancellationToken cancellationToken = default)
        {
            LastApplyBranding = applyBranding;
            await release;
            if (failStream)
            {
                await destination.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
                throw new IOException("Synthetic temporary write failure");
            }
            await inner.WriteFrameAsync(frame, destination, applyBranding, cancellationToken);
        }
        public async Task WriteFrameAsync(CapturedFrame frame, Stream destination, ImageFormat format, bool applyBranding = true, CancellationToken cancellationToken = default)
        {
            LastApplyBranding = applyBranding;
            await release;
            if (StreamFailure is { } failure)
            {
                await destination.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
                throw failure;
            }
            if (failStream) throw new IOException("Synthetic temporary write failure");
            await inner.WriteFrameAsync(frame, destination, format, applyBranding, cancellationToken);
        }
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);
        public AppTheme Theme { get; set; }
        public string SaveDirectory { get; set; } = string.Empty;
        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;
        public void Set<T>(string key, T value) => _values[key] = value is null ? string.Empty : value;
    }
}
