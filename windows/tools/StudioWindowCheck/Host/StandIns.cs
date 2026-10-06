using Microsoft.UI.Windowing;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;

// The stand-ins for what the tool leaves out of the app. Each is listed in README.md.
namespace TinyClips.App
{
    /// <summary>
    /// Stands in for the app's <c>Infrastructure\WindowIcon.cs</c>, which WindowChromeController
    /// calls when a window is first activated. The app's class writes to the app's crash log when
    /// the icon cannot be set, and this tool must never write there. Its windows are kept out of
    /// the taskbar, so they need no icon.
    /// </summary>
    internal static class WindowIcon
    {
        public static void Apply(AppWindow appWindow)
        {
        }
    }
}

namespace TinyClips.Tools.StudioWindowCheck.Host
{
    /// <summary>Settings that live as long as the object does. Nothing is read from or written to the machine.</summary>
    internal sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();

        public AppTheme Theme { get; set; }

        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue)
        {
            lock (_gate)
            {
                return _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;
            }
        }

        public void Set<T>(string key, T value)
        {
            lock (_gate)
            {
                _values[key] = value is null ? string.Empty : value;
            }
        }
    }

    /// <summary>
    /// Stands in for the app's ClipStorageService: every exported video goes into one folder under
    /// the tool's temp folder, never into the Videos folder.
    /// </summary>
    internal sealed class TempClipStorage(string directory) : IClipStorageService
    {
        private int _serial;
        private string? _nextPath;

        public string Directory { get; } = directory;

        /// <summary>Makes the next export write to this path instead, once: for an export that has to fail.</summary>
        public void SendNextTo(string path) => Volatile.Write(ref _nextPath, path);

        public string FileExtensionFor(CaptureType type) => type switch
        {
            CaptureType.Video => "mp4",
            CaptureType.Gif => "gif",
            _ => "png",
        };

        public string OutputDirectory(CaptureType type) => Directory;

        public string GenerateFilePath(CaptureType type, string? fileExtension = null, string? stemSuffix = null)
        {
            if (Interlocked.Exchange(ref _nextPath, null) is { } elsewhere)
            {
                return elsewhere;
            }

            System.IO.Directory.CreateDirectory(Directory);
            var extension = string.IsNullOrWhiteSpace(fileExtension) ? FileExtensionFor(type) : fileExtension.Trim('.');
            var serial = Interlocked.Increment(ref _serial);
            return Path.Combine(Directory, $"StudioWindowCheck export {serial}{(string.IsNullOrWhiteSpace(stemSuffix) ? string.Empty : " " + stemSuffix.Trim())}.{extension}");
        }
    }

    /// <summary>Stands in for the recorder, which the cleanup service asks whether a Studio recording is running. None ever is.</summary>
    internal sealed class NoRecorder : IVideoRecordingService
    {
#pragma warning disable CS0067 // Nothing is ever recorded, so nothing is ever raised.
        public event EventHandler<string?>? RecordingCompleted;

        public event EventHandler<string>? StudioRecordingCompleted;

        public event EventHandler<string>? WebcamCaptureFailed;
#pragma warning restore CS0067

        public bool IsRecording => false;

        public string? ActiveStudioProjectId => null;

        public bool IsPaused => false;

        public bool CanMuteSystemAudio => false;

        public bool CanMuteMicrophone => false;

        public bool IsSystemAudioMuted => false;

        public bool IsMicrophoneMuted => false;

        public RecordingPerformanceReport? LastPerformanceReport => null;

        public Task StartAsync(CaptureTarget? target = null, PixelRect? region = null, double? timeLimitMinutesOverride = null, CancellationToken cancellationToken = default) => throw NotHere();

        public Task StartAsync(CaptureTarget? target, PixelRect? region, double? timeLimitMinutesOverride, VideoRecordingOptions options, CancellationToken cancellationToken = default) => throw NotHere();

        public Task PrepareAsync(CaptureTarget? target = null, PixelRect? region = null, CancellationToken cancellationToken = default) => throw NotHere();

        public Task PrepareAsync(CaptureTarget? target, PixelRect? region, VideoRecordingOptions options, CancellationToken cancellationToken = default) => throw NotHere();

        public Task DiscardPreparedAsync() => Task.CompletedTask;

        public Task<string?> StopAsync() => Task.FromResult<string?>(null);

        public Task PauseAsync() => Task.CompletedTask;

        public Task ResumeAsync() => Task.CompletedTask;

        public void SetWebcamCorner(WebcamCornerPosition corner)
        {
        }

        public void SetSystemAudioMuted(bool muted)
        {
        }

        public void SetMicrophoneMuted(bool muted)
        {
        }

        public Task CancelAsync() => Task.CompletedTask;

        private static NotSupportedException NotHere() => new("StudioWindowCheck records nothing.");
    }

    /// <summary>
    /// The real project store on the tool's temp folder, with three additions: it refuses a root
    /// outside the temp folder, it counts how often a delete was tried and how often it failed, and
    /// it never lets a cleanup throw. The app's cleanup service writes a cleanup that threw to the
    /// app's crash log, and this tool must never write there.
    /// </summary>
    internal sealed class GuardedStore : IStudioProjectStore
    {
        private readonly StudioProjectStore _inner;
        private readonly object _gate = new();
        private readonly List<string> _cleanupFailures = [];
        private readonly List<string> _cleanedUp = [];
        private int _deletes;
        private int _failedDeletes;
        private int _cleanups;

        public GuardedStore(string rootDirectory)
        {
            var root = Path.GetFullPath(rootDirectory);
            var temp = Path.GetFullPath(Path.GetTempPath());
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || root.Length <= temp.Length)
            {
                throw new InvalidOperationException($"The project store has to be inside {temp}, and {root} is not.");
            }

            _inner = new StudioProjectStore(root);
        }

        public int Deletes => Volatile.Read(ref _deletes);

        public int FailedDeletes => Volatile.Read(ref _failedDeletes);

        /// <summary>How often the cleanup that follows a closed window has run.</summary>
        public int Cleanups => Volatile.Read(ref _cleanups);

        public string RootDirectory => _inner.RootDirectory;

        /// <summary>The ids of the projects a cleanup deleted, and what a cleanup threw. Both should stay empty.</summary>
        public (string[] Deleted, string[] Failures) CleanupOutcome()
        {
            lock (_gate)
            {
                return ([.. _cleanedUp], [.. _cleanupFailures]);
            }
        }

        public StudioProjectPaths BeginRecording() => _inner.BeginRecording();

        public StudioProject CompleteRecording(string projectId, StudioProjectCreationRequest request) => _inner.CompleteRecording(projectId, request);

        public StudioProjectPaths GetPaths(string projectId) => _inner.GetPaths(projectId);

        public StudioProjectPaths GetPaths(StudioProject project) => _inner.GetPaths(project);

        public bool Exists(string projectId) => _inner.Exists(projectId);

        public StudioProject Load(string projectId) => _inner.Load(projectId);

        public StudioProject Save(StudioProject project) => _inner.Save(project);

        public void Delete(string projectId)
        {
            Interlocked.Increment(ref _deletes);
            try
            {
                _inner.Delete(projectId);
            }
            catch
            {
                Interlocked.Increment(ref _failedDeletes);
                throw;
            }
        }

        public StudioProject MarkOpened(string projectId) => _inner.MarkOpened(projectId);

        public StudioProject SetKeepSources(string projectId, bool keepSources) => _inner.SetKeepSources(projectId, keepSources);

        public IReadOnlyList<StudioProjectSummary> ListSummaries() => _inner.ListSummaries();

        public IReadOnlyList<StudioUnreadableProject> ListUnreadableProjects() => _inner.ListUnreadableProjects();

        public string? FindScreenRecording(string projectId) => _inner.FindScreenRecording(projectId);

        public StudioStorageSummary GetStorageSummary() => _inner.GetStorageSummary();

        public StudioProject RecordExport(string projectId, string exportedPath) => _inner.RecordExport(projectId, exportedPath);

        public string? FindProjectIdByExportPath(string exportedPath) => _inner.FindProjectIdByExportPath(exportedPath);

        public bool UpdateExportPath(string oldPath, string newPath) => _inner.UpdateExportPath(oldPath, newPath);

        public bool RemoveExportPath(string exportedPath) => _inner.RemoveExportPath(exportedPath);

        public StudioProject GetOrCreateFlatProject(string videoPath, StudioRecordingSourceInfo video, string appVersion) => _inner.GetOrCreateFlatProject(videoPath, video, appVersion);

        public StudioEvents LoadEvents(string projectId) => _inner.LoadEvents(projectId);

        public void SaveEvents(string projectId, StudioEvents events) => _inner.SaveEvents(projectId, events);

        public StudioCleanupResult Cleanup(
            StudioCleanupOptions? options = null,
            IReadOnlyCollection<string>? inUseProjectIds = null,
            Func<string, bool>? isInUse = null)
        {
            try
            {
                var result = _inner.Cleanup(options, inUseProjectIds, isInUse);
                lock (_gate)
                {
                    _cleanedUp.AddRange(result.ProjectIdsDeleted);
                }

                return result;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _cleanupFailures.Add(ex.ToString());
                }

                return new StudioCleanupResult(0, []);
            }
            finally
            {
                Interlocked.Increment(ref _cleanups);
            }
        }
    }
}
