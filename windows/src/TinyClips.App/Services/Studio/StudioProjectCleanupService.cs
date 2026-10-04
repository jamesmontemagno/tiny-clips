using System.Diagnostics;
using TinyClips.Core.Capture;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;

namespace TinyClips.App.Services.Studio;

/// <summary>
/// Runs the Studio project cleanup rules (age, then the storage limit) off the calling thread. Does
/// nothing while the Studio preview is switched off.
/// </summary>
public sealed class StudioProjectCleanupService
{
    private readonly IStudioProjectStore _store;
    private readonly ICaptureSettings _settings;
    private readonly StudioProjectTracker _tracker;
    private readonly IVideoRecordingService _recorder;

    public StudioProjectCleanupService(
        IStudioProjectStore store,
        ICaptureSettings settings,
        StudioProjectTracker tracker,
        IVideoRecordingService recorder)
    {
        _store = store;
        _settings = settings;
        _tracker = tracker;
        _recorder = recorder;
    }

    /// <summary>
    /// Raised after a cleanup has run, with what it deleted. It comes on a background thread, so a
    /// UI listener has to switch threads.
    /// </summary>
    public event EventHandler<StudioCleanupResult>? CleanupCompleted;

    /// <summary>
    /// Deletes what the current settings allow, skipping every project that is open in an editor
    /// or being recorded into. Returns null when Studio is switched off or the cleanup failed. A
    /// failure is logged, never thrown.
    /// </summary>
    public async Task<StudioCleanupResult?> RunAsync()
    {
        try
        {
            if (!_settings.StudioPreviewEnabled)
            {
                return null;
            }

            var options = _settings.GetStudioCleanupOptions();
            var inUseProjectIds = new List<string>(_tracker.OpenProjectIds);
            if (_recorder.ActiveStudioProjectId is { } recordingProjectId)
            {
                // A recording that runs for more than a day would otherwise look abandoned.
                inUseProjectIds.Add(recordingProjectId);
            }

            var result = await Task.Run(() => _store.Cleanup(options, inUseProjectIds)).ConfigureAwait(false);
            CleanupCompleted?.Invoke(this, result);
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Studio project cleanup failed: {ex}");
            CrashDiagnostics.Log("Studio project cleanup", ex, handled: true);
            return null;
        }
    }
}
