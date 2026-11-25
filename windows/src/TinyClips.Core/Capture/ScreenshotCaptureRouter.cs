using TinyClips.Core.Editing;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Capture;

public enum ScreenshotEditorOutcome
{
    NotRequested,
    Loaded,
    Closed,
    ClosedBeforeLoad,
    Failed,
}

/// <summary>Resolves an editor closed while the background capture file was still being written.</summary>
public static class ScreenshotEditorOutcomePolicy
{
    public static ScreenshotEditorOutcome AfterPendingWrite(
        ScreenshotEditorOutcome initial, bool isClosed)
    {
        if (isClosed && initial == ScreenshotEditorOutcome.Loaded)
            return ScreenshotEditorOutcome.Closed;
        return initial;
    }
}

/// <summary>The editor callback owns and must dispose the supplied temporary lease.</summary>
public sealed record ScreenshotEditorRequest(
    CapturedFrame Frame,
    Task<string> File,
    bool UseMemory,
    bool IsTemporary,
    ScreenshotTemporaryFileLease? Lease);

public sealed record ScreenshotRoutingResult(
    bool IsPermanent,
    string? Path,
    bool Copied,
    ScreenshotEditorOutcome Editor,
    bool EditorUnavailable,
    bool AnalyticsRecorded,
    Exception? WriteError,
    Exception? CopyError,
    Exception? EditorError)
{
    public bool HasOutput => (IsPermanent && Path is not null) || Copied ||
        Editor == ScreenshotEditorOutcome.Loaded;
    public bool EditorHandlesLifecycle => Editor is ScreenshotEditorOutcome.Loaded or
        ScreenshotEditorOutcome.Closed or ScreenshotEditorOutcome.ClosedBeforeLoad;
    public bool ShouldPresentSavedResult => IsPermanent && Path is not null && !EditorHandlesLifecycle;
}

/// <summary>Tracks incomplete outputs across retries without republishing a successful clipboard.</summary>
public sealed class ScreenshotRecoveryProgress(
    ScreenshotOutputSelection selection, bool fitsEditor, CapturedFrame? frame = null)
{
    private bool _editorFailed;

    public CapturedFrame? Frame { get; private set; } = frame;
    public bool Copied { get; private set; }
    public ScreenshotOutputSelection RetrySelection => new(false,
        selection.CopyToClipboard && !Copied, selection.OpenEditor && fitsEditor);
    public bool CanRetry => RetrySelection.CopyToClipboard || RetrySelection.OpenEditor;

    public void RecordResult(ScreenshotOutputSelection attempted, ScreenshotRoutingResult result)
    {
        if (result.IsPermanent && result.Path is not null) ReleaseFrame();
        Copied |= result.Copied;
        if (attempted.OpenEditor)
            _editorFailed = result.EditorUnavailable ||
                result.Editor is ScreenshotEditorOutcome.Failed or ScreenshotEditorOutcome.NotRequested;
    }

    public void MarkEditorFailed() => _editorFailed = true;

    public void ReleaseFrame() => Frame = null;

    public bool RequiresRecovery(ScreenshotRoutingResult result, bool editorIsOpen, bool editorHasPermanentSave)
    {
        if ((result.IsPermanent && result.Path is not null) || editorHasPermanentSave)
        {
            ReleaseFrame();
            return false;
        }
        if (_editorFailed) return true;
        if (Copied || editorIsOpen) return false;

        // Closing an editor-only capture is an intentional discard, not an output failure.
        return !(selection.OpenEditor && !selection.SaveToDisk && !selection.CopyToClipboard &&
            result.Editor is ScreenshotEditorOutcome.Closed or ScreenshotEditorOutcome.ClosedBeforeLoad);
    }
}

/// <summary>Routes interactive screenshots without treating a temporary file as a saved clip.</summary>
public sealed class ScreenshotCaptureRouter
{
    private readonly IScreenshotService _screenshots;
    private readonly IClipStorageService _storage;
    private readonly IClipAnalyticsService _analytics;
    private readonly IRecentCaptureService _recent;
    private readonly ScreenshotTemporaryFileStore _temporary;

    public ScreenshotCaptureRouter(
        IScreenshotService screenshots, IClipStorageService storage, IClipAnalyticsService analytics,
        IRecentCaptureService recent, ScreenshotTemporaryFileStore temporary)
    {
        _screenshots = screenshots;
        _storage = storage;
        _analytics = analytics;
        _recent = recent;
        _temporary = temporary;
    }

    public async Task<ScreenshotRoutingResult> RouteAsync(
        CapturedFrame frame,
        ScreenshotOutputSelection selection,
        bool fitsEditor,
        bool useMemoryEditor,
        bool alreadyBranded,
        Func<string, Task> copy,
        Func<ScreenshotEditorRequest, Task<ScreenshotEditorOutcome>> openEditor,
        bool recordCapture = true)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(openEditor);
        var plan = ScreenshotCapturePlan.Create(selection, fitsEditor);

        // An oversized editor-only capture cannot be delivered by the editor. Keep the frame
        // for explicit recovery rather than creating a permanent file or an unused temp file.
        if (!selection.SaveToDisk && !selection.CopyToClipboard && !plan.OpenEditor)
        {
            return new(false, null, false, ScreenshotEditorOutcome.NotRequested,
                plan.EditorUnavailable, false, null, null, null);
        }

        ScreenshotTemporaryFileLease? work = null;
        Task<string> file;
        try
        {
            if (selection.SaveToDisk)
            {
                file = SavePersistentAsync(frame, alreadyBranded);
            }
            else
            {
                work = _temporary.Reserve(_storage.FileExtensionFor(CaptureType.Screenshot));
                file = WriteTemporaryAsync(frame, work, alreadyBranded);
            }
        }
        catch (Exception ex)
        {
            work?.Dispose();
            return new(selection.SaveToDisk, null, false, ScreenshotEditorOutcome.NotRequested,
                plan.EditorUnavailable, false, ex, null, null);
        }

        using (work)
        {
            var copyTask = selection.CopyToClipboard
                ? CopyAsync(file, copy)
                : Task.FromResult<(bool Copied, Exception? Error)>((false, null));
            var editorTask = plan.OpenEditor
                ? OpenEditorAsync(frame, file, useMemoryEditor, work, openEditor)
                : Task.FromResult<(ScreenshotEditorOutcome Outcome, Exception? Error)>((ScreenshotEditorOutcome.NotRequested, null));

            string? path = null;
            Exception? writeError = null;
            try
            {
                path = await file;
            }
            catch (Exception ex)
            {
                writeError = ex;
            }
            var (copied, copyError) = await copyTask;
            var (editor, editorError) = await editorTask;
            var analyticsRecorded = recordCapture &&
                ((selection.SaveToDisk && path is not null) || copied ||
                    editor is ScreenshotEditorOutcome.Loaded or ScreenshotEditorOutcome.Closed);
            if (analyticsRecorded) _analytics.RecordCapture(CaptureType.Screenshot);
            return new(selection.SaveToDisk, path, copied, editor, plan.EditorUnavailable,
                analyticsRecorded, writeError, copyError, editorError);
        }
    }

    /// <summary>Writes only after the user explicitly chooses Save from recovery.</summary>
    public async Task<string> SaveRecoveredAsync(CapturedFrame frame, bool alreadyBranded, bool recordCapture)
    {
        var path = _storage.GenerateFilePath(CaptureType.Screenshot);
        await _screenshots.WriteFrameAsync(frame, path, applyBranding: !alreadyBranded);
        if (recordCapture) _analytics.RecordCapture(CaptureType.Screenshot);
        _recent.Record(path, CaptureType.Screenshot);
        return path;
    }

    /// <summary>Saves a recovered frame to a user-selected file, including a picker-created placeholder.</summary>
    public async Task<string> SaveRecoveredAsAsync(
        CapturedFrame frame, string path, bool alreadyBranded, bool recordCapture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        var format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => ImageFormat.Png,
            ".jpg" or ".jpeg" => ImageFormat.Jpeg,
            ".webp" => ImageFormat.Webp,
            _ => throw new IOException("Choose a PNG, JPEG, or WebP file."),
        };
        await ScreenshotExportFile.WriteAsync(path, async (temporaryPath, token) =>
        {
            await using var stream = new FileStream(temporaryPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.Read, 81920, useAsync: true);
            await _screenshots.WriteFrameAsync(frame, stream, format,
                applyBranding: !alreadyBranded, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        if (recordCapture) _analytics.RecordCapture(CaptureType.Screenshot);
        _recent.Record(path, CaptureType.Screenshot);
        return path;
    }

    private async Task<string> SavePersistentAsync(CapturedFrame frame, bool alreadyBranded)
    {
        var path = _storage.GenerateFilePath(CaptureType.Screenshot);
        await _screenshots.WriteFrameAsync(frame, path, applyBranding: !alreadyBranded);
        _recent.Record(path, CaptureType.Screenshot);
        return path;
    }

    private async Task<string> WriteTemporaryAsync(CapturedFrame frame, ScreenshotTemporaryFileLease lease, bool alreadyBranded)
    {
        try
        {
            await using var output = _temporary.OpenNewFile(lease);
            await _screenshots.WriteFrameAsync(frame, output, applyBranding: !alreadyBranded);
            return lease.Path;
        }
        catch
        {
            try { File.Delete(lease.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrashDiagnostics.Log("Screenshot temporary cleanup", ex, handled: true);
            }
            throw;
        }
    }

    private static async Task<(bool Copied, Exception? Error)> CopyAsync(Task<string> file, Func<string, Task> copy)
    {
        string path;
        try { path = await file; }
        catch { return (false, null); }
        try
        {
            await copy(path);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    private static async Task<(ScreenshotEditorOutcome Outcome, Exception? Error)> OpenEditorAsync(
        CapturedFrame frame, Task<string> file, bool useMemory, ScreenshotTemporaryFileLease? work,
        Func<ScreenshotEditorRequest, Task<ScreenshotEditorOutcome>> openEditor)
    {
        if (!useMemory)
        {
            try { await file; }
            catch { return (ScreenshotEditorOutcome.Failed, null); }
        }

        var lease = work?.Retain();
        try
        {
            return (await openEditor(new(frame, file, useMemory, work is not null, lease)), null);
        }
        catch (Exception ex)
        {
            lease?.Dispose();
            return (ScreenshotEditorOutcome.Failed, ex);
        }
    }
}
