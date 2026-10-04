using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Core.Studio.Preview;

namespace TinyClips.Core.Tests;

/// <summary>
/// What every <see cref="StudioEditorSession"/> test needs: a real project store on a temporary
/// folder, a preview and an exporter the test controls, a clock it advances by hand, and a queue
/// standing in for the UI thread. Nothing here sleeps.
/// </summary>
public abstract class StudioEditorSessionTestBase : IDisposable
{
    protected const int Precision = 9;
    protected const double Frame = 1.0 / 30;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TinyClipsTests", Guid.NewGuid().ToString("N"));
    private readonly List<Action> _posted = [];

    protected StudioEditorSessionTestBase()
    {
        Time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        Projects = new StudioProjectStore(Path.Combine(_directory, "Projects"), Time);
        Store = new RecordingStore(Projects, Log);
        Previews = new FakePreviewFactory(Log);
        Exporter = new FakeExporter(Log);
        Settings = new CaptureSettings(new TestSettingsService(), null, null);
    }

    internal ManualTimeProvider Time { get; }

    /// <summary>The store itself, for what "another part of the app" does to a project.</summary>
    protected StudioProjectStore Projects { get; }

    /// <summary>The same store as the session sees it, with every call written to <see cref="Log"/>.</summary>
    private protected RecordingStore Store { get; }

    private protected FakePreviewFactory Previews { get; }

    private protected FakeExporter Exporter { get; }

    protected CaptureSettings Settings { get; }

    /// <summary>Store, preview and exporter calls in the order they happened.</summary>
    protected List<string> Log { get; } = [];

    protected List<string> Errors { get; } = [];

    /// <summary>What the session was doing for each entry of <see cref="Errors"/>.</summary>
    protected List<StudioEditorErrorKind> ErrorKinds { get; } = [];

    protected List<string> ExportedPaths { get; } = [];

    protected List<StudioEditorChanges> Changes { get; } = [];

    /// <summary>Actions the session has posted to its thread and that have not run yet.</summary>
    protected int PostedCount => _posted.Count;

    private protected FakePreview Preview => Previews.Opened[^1];

    protected string ExportPath => Path.Combine(_directory, "Videos", "TinyClips 2026-10-03 at 12.00.00.mp4");

    /// <summary>Creates a finished recording: a project with placeholder media files next to it.</summary>
    protected string CreateProject(
        bool camera = false,
        double duration = 10,
        double frameRate = 30,
        double cameraStartOffset = 0,
        bool writeScreenFile = true,
        string name = "Recording")
    {
        var paths = Projects.BeginRecording();
        Projects.CompleteRecording(paths.ProjectId, new StudioProjectCreationRequest(
            name,
            new StudioRecordingSourceInfo(1920, 1080, duration, frameRate),
            camera ? new StudioCameraSourceInfo(1280, 720, duration, cameraStartOffset) : null,
            StudioAnchor.BottomRight,
            new StudioClickOverlay(),
            false,
            "1.9.0"));
        if (writeScreenFile)
        {
            File.WriteAllBytes(paths.ScreenPath, [1, 2, 3]);
        }

        if (camera)
        {
            File.WriteAllBytes(paths.CameraPath!, [4, 5, 6]);
        }

        return paths.ProjectId;
    }

    protected StudioEditorSession CreateSession(string projectId)
    {
        var session = new StudioEditorSession(projectId, Store, Previews, Exporter, Settings, _posted.Add, Time);
        session.Changed += (_, e) => Changes.Add(e.Changes);
        session.ErrorReported += (_, e) =>
        {
            Errors.Add(e.Message);
            ErrorKinds.Add(e.Kind);
        };
        session.Exported += (_, e) => ExportedPaths.Add(e.Path);
        return session;
    }

    /// <summary>Opens a project and runs everything the session posted, so it is ready or unavailable.</summary>
    protected async Task<StudioEditorSession> OpenAsync(string projectId)
    {
        var session = CreateSession(projectId);
        await FinishAsync(session.LoadAsync());
        return session;
    }

    /// <summary>Runs what the session posted to its thread, including anything those actions post.</summary>
    protected void Pump()
    {
        while (_posted.Count > 0)
        {
            var action = _posted[0];
            _posted.RemoveAt(0);
            action();
        }
    }

    /// <summary>Pumps, then waits for a task that should now be finished. A hang becomes a failure.</summary>
    protected async Task FinishAsync(Task task)
    {
        Pump();
        await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    protected async Task<T> FinishAsync<T>(Task<T> task)
    {
        Pump();
        return await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>Advances the clock, which fires due timers, then runs what they posted.</summary>
    protected void Advance(double milliseconds)
    {
        Time.Advance(TimeSpan.FromMilliseconds(milliseconds));
        Pump();
    }

    protected int LogCount(string entry) => Log.Count(item => item == entry);

    protected void AssertLoggedInOrder(params string[] entries)
    {
        var position = -1;
        foreach (var entry in entries)
        {
            var next = Log.FindIndex(position + 1, item => item == entry);
            Assert.True(next >= 0, $"'{entry}' was not logged after position {position}. Log: {string.Join(", ", Log)}");
            position = next;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private sealed class TestSettingsService : ISettingsService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

        public AppTheme Theme { get; set; }

        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typedValue ? typedValue : defaultValue;

        public void Set<T>(string key, T value) => _values[key] = value is null ? string.Empty : value;
    }
}

/// <summary>A preview that records what it is told and raises events only when a test says so.</summary>
internal sealed class FakePreview(List<string> log) : IStudioPreview
{
    public event EventHandler? PositionChanged;

    public event EventHandler? IsPlayingChanged;

    public event EventHandler<StudioPreviewFailedEventArgs>? Failed;

    public double Position { get; set; }

    public bool IsPlaying { get; set; }

    /// <summary>Play, Pause, Seek, Rate and UpdateProject, in order.</summary>
    public List<string> Calls { get; } = [];

    public List<double> Seeks { get; } = [];

    /// <summary>Every rate the preview was told to play at, in order.</summary>
    public List<double> Rates { get; } = [];

    public StudioProject? LastProject { get; private set; }

    public bool IsDisposed { get; private set; }

    /// <summary>When set, disposing waits for it, as a real preview waits for its decoders.</summary>
    public TaskCompletionSource? DisposeGate { get; set; }

    public bool HasSubscribers => PositionChanged is not null || IsPlayingChanged is not null || Failed is not null;

    public void UpdateProject(StudioProject project)
    {
        LastProject = project;
        Calls.Add("UpdateProject");
    }

    public void Play()
    {
        IsPlaying = true;
        Calls.Add("Play");
    }

    public void Pause()
    {
        IsPlaying = false;
        Calls.Add("Pause");
    }

    public void Seek(double sourceTime)
    {
        // A preview reports a requested position from the moment the call returns.
        Position = sourceTime;
        Seeks.Add(sourceTime);
        Calls.Add("Seek");
    }

    public void SetPlaybackRate(double rate)
    {
        Rates.Add(rate);
        Calls.Add("Rate");
    }

    public async ValueTask DisposeAsync()
    {
        log.Add("preview.dispose");
        if (DisposeGate is { } gate)
        {
            await gate.Task;
        }

        IsDisposed = true;
        log.Add("preview.disposed");
    }

    // The contract does not promise a sender, so none is passed.
    public void RaisePosition(double position)
    {
        Position = position;
        PositionChanged?.Invoke(null, EventArgs.Empty);
    }

    public void RaiseIsPlaying(bool isPlaying)
    {
        IsPlaying = isPlaying;
        IsPlayingChanged?.Invoke(null, EventArgs.Empty);
    }

    public void RaiseFailed(string message) => Failed?.Invoke(null, new StudioPreviewFailedEventArgs(message));
}

internal sealed record PreviewRequest(StudioProject Project, StudioEvents Events, StudioProjectPaths Paths);

internal sealed class FakePreviewFactory(List<string> log) : IStudioPreviewFactory
{
    public List<PreviewRequest> Requests { get; } = [];

    public List<FakePreview> Opened { get; } = [];

    public Exception? Failure { get; set; }

    /// <summary>When set, opening waits for it.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public CancellationToken LastCancellationToken { get; private set; }

    public async Task<IStudioPreview> OpenAsync(
        StudioProject project,
        StudioEvents events,
        StudioProjectPaths paths,
        CancellationToken cancellationToken = default)
    {
        log.Add("preview.open");
        Requests.Add(new PreviewRequest(project, events, paths));
        LastCancellationToken = cancellationToken;
        if (Gate is { } gate)
        {
            await gate.Task;
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        var preview = new FakePreview(log);
        Opened.Add(preview);
        return preview;
    }
}

internal sealed record ExportRequest(
    StudioProject Project,
    StudioEvents Events,
    StudioProjectPaths Paths,
    string OutputPath,
    VideoCodec Codec);

/// <summary>An exporter that finishes, fails or is cancelled when a test says so. It writes nothing.</summary>
internal sealed class FakeExporter(List<string> log) : IStudioExportService
{
    private readonly TaskCompletionSource _completion = new();

    public List<ExportRequest> Exports { get; } = [];

    public List<StudioProject> Posters { get; } = [];

    public IProgress<double>? Progress { get; private set; }

    public CancellationToken ExportCancellationToken { get; private set; }

    public Exception? PosterFailure { get; set; }

    /// <summary>Runs when the export is asked for, before it returns.</summary>
    public Action? ExportStarted { get; set; }

    public Task ExportAsync(
        StudioProject project,
        StudioEvents events,
        StudioProjectPaths paths,
        string outputPath,
        VideoCodec codec,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        log.Add("exporter.export");
        Exports.Add(new ExportRequest(project, events, paths, outputPath, codec));
        Progress = progress;
        ExportCancellationToken = cancellationToken;
        ExportStarted?.Invoke();
        cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
        return _completion.Task;
    }

    public Task WritePosterAsync(
        StudioProject project,
        StudioEvents events,
        StudioProjectPaths paths,
        CancellationToken cancellationToken)
    {
        log.Add("exporter.poster");
        Posters.Add(project);
        return PosterFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }

    public void Complete() => _completion.TrySetResult();

    public void Fail(Exception exception) => _completion.TrySetException(exception);
}

/// <summary>Passes every call to a real store and writes the ones a session makes to a log.</summary>
internal sealed class RecordingStore(StudioProjectStore inner, List<string> log) : IStudioProjectStore
{
    public string RootDirectory => inner.RootDirectory;

    public StudioProjectPaths BeginRecording() => inner.BeginRecording();

    public StudioProject CompleteRecording(string projectId, StudioProjectCreationRequest request) =>
        inner.CompleteRecording(projectId, request);

    public StudioProjectPaths GetPaths(string projectId) => inner.GetPaths(projectId);

    public StudioProjectPaths GetPaths(StudioProject project) => inner.GetPaths(project);

    public bool Exists(string projectId) => inner.Exists(projectId);

    public StudioProject Load(string projectId)
    {
        log.Add("store.load");
        return inner.Load(projectId);
    }

    public StudioProject Save(StudioProject project)
    {
        log.Add("store.save");
        return inner.Save(project);
    }

    public void Delete(string projectId)
    {
        log.Add("store.delete");
        inner.Delete(projectId);
    }

    public StudioProject MarkOpened(string projectId)
    {
        log.Add("store.markOpened");
        return inner.MarkOpened(projectId);
    }

    public IReadOnlyList<StudioProjectSummary> ListSummaries() => inner.ListSummaries();

    public StudioStorageSummary GetStorageSummary() => inner.GetStorageSummary();

    public StudioProject RecordExport(string projectId, string exportedPath)
    {
        log.Add("store.recordExport");
        return inner.RecordExport(projectId, exportedPath);
    }

    public string? FindProjectIdByExportPath(string exportedPath) => inner.FindProjectIdByExportPath(exportedPath);

    public bool UpdateExportPath(string oldPath, string newPath) => inner.UpdateExportPath(oldPath, newPath);

    public bool RemoveExportPath(string exportedPath) => inner.RemoveExportPath(exportedPath);

    public StudioProject GetOrCreateFlatProject(string videoPath, StudioRecordingSourceInfo video, string appVersion) =>
        inner.GetOrCreateFlatProject(videoPath, video, appVersion);

    public StudioEvents LoadEvents(string projectId)
    {
        log.Add("store.loadEvents");
        return inner.LoadEvents(projectId);
    }

    public void SaveEvents(string projectId, StudioEvents events) => inner.SaveEvents(projectId, events);

    public StudioCleanupResult Cleanup(StudioCleanupOptions? options = null, IReadOnlyCollection<string>? inUseProjectIds = null) =>
        inner.Cleanup(options, inUseProjectIds);
}
