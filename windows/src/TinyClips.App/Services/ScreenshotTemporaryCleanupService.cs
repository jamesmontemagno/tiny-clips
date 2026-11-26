using TinyClips.Core.Capture;
using TinyClips.Core.Services;

namespace TinyClips.App;

internal sealed class ScreenshotTemporaryCleanupService(ScreenshotTemporaryFileStore screenshots)
{
    public Task<TemporaryFilesPurgeResult> PurgeAsync() => Task.Run(() =>
    {
        var images = screenshots.Purge(TimeSpan.Zero, DateTimeOffset.UtcNow);
        var diagnostics = TinyClipsTemporaryFiles.Purge(WebcamDiagnostics.ActiveFilePaths);
        return new TemporaryFilesPurgeResult(
            images.RemovedFileCount + diagnostics.RemovedFileCount,
            images.SkippedFileCount + diagnostics.SkippedFileCount);
    });

    public Task<TemporaryFilesPurgeResult> PurgeStaleAsync() =>
        Task.Run(() => screenshots.Purge(TimeSpan.FromDays(1), DateTimeOffset.UtcNow));
}
