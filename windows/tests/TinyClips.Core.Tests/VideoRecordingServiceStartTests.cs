using System.Reflection;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;
using Windows.Graphics.Imaging;

namespace TinyClips.Core.Tests;

/// <summary>
/// What a start of the video recorder that fails does to the recording made before it. Each
/// start here is made to fail on its first read of a setting, which is before it asks Windows
/// for anything, so nothing is captured and no camera or microphone is opened.
/// </summary>
public sealed class VideoRecordingServiceStartTests : IDisposable
{
    private const string StartFailure = "The setting could not be read.";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TinyClipsTests", Guid.NewGuid().ToString("N"));
    private readonly FailingSettings _saved = new();
    private readonly VideoRecordingService _recorder;
    private readonly string _lastRecording;

    public VideoRecordingServiceStartTests()
    {
        Directory.CreateDirectory(_directory);
        _lastRecording = Path.Combine(_directory, "TinyClips 2026-10-05 at 12.00.00.mp4");
        File.WriteAllBytes(_lastRecording, [1, 2, 3]);

        _recorder = new VideoRecordingService(
            new NoMonitors(),
            new NoNames(),
            new CaptureSettings(_saved),
            new NoAnalytics(),
            new NoCamera(),
            new StudioProjectStore(Path.Combine(_directory, "Projects")));

        // What a finished recording leaves behind: the recorder keeps the path of the video it
        // saved, for a discard that arrives after the stop.
        var outputPath = typeof(VideoRecordingService).GetField("_outputPath", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(outputPath is not null, "The recorder no longer keeps the path of its video in a field called _outputPath. These tests have to be told where it is.");
        outputPath.SetValue(_recorder, _lastRecording);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp folder that stays behind fails no test.
        }
    }

    [Fact]
    public async Task APreparationThatFailsBeforeItHasAFile_LeavesTheLastRecordingWhereItIs()
    {
        _saved.FailOn = "videoFrameRate";

        var failure = await Assert.ThrowsAsync<IOException>(
            () => _recorder.PrepareAsync(CaptureTarget.Window(1), null, TestContext.Current.CancellationToken));

        Assert.Equal(StartFailure, failure.Message);
        Assert.False(_recorder.IsRecording);
        Assert.True(File.Exists(_lastRecording), "The start that failed deleted the recording made before it.");
        Assert.Equal([1, 2, 3], File.ReadAllBytes(_lastRecording));
    }

    [Fact]
    public async Task AStartThatFailsBeforeItHasAFile_LeavesTheLastRecordingWhereItIs()
    {
        _saved.FailOn = "videoFrameRate";

        var failure = await Assert.ThrowsAsync<IOException>(
            () => _recorder.StartAsync(CaptureTarget.Window(1), null, null, TestContext.Current.CancellationToken));

        Assert.Equal(StartFailure, failure.Message);
        Assert.False(_recorder.IsRecording);
        Assert.True(File.Exists(_lastRecording), "The start that failed deleted the recording made before it.");
    }

    [Fact]
    public async Task ASecondStartThatFails_LeavesItToo()
    {
        _saved.FailOn = "videoFrameRate";

        await Assert.ThrowsAsync<IOException>(
            () => _recorder.PrepareAsync(CaptureTarget.Window(1), null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(
            () => _recorder.StartAsync(CaptureTarget.Window(1), null, null, TestContext.Current.CancellationToken));

        Assert.True(File.Exists(_lastRecording));
    }

    [Fact]
    public async Task ADiscardThatArrivesAfterTheStop_StillDeletesTheRecordingItWasMeantFor()
    {
        // Why the recorder keeps the path at all. A fix that forgot it at the stop would break this.
        await _recorder.CancelAsync();

        Assert.False(File.Exists(_lastRecording));
    }

    [Fact]
    public async Task OnceAnotherStartHasBegun_ADiscardNoLongerReachesTheRecordingBeforeIt()
    {
        _saved.FailOn = "videoFrameRate";
        await Assert.ThrowsAsync<IOException>(
            () => _recorder.PrepareAsync(CaptureTarget.Window(1), null, TestContext.Current.CancellationToken));

        await _recorder.CancelAsync();

        Assert.True(File.Exists(_lastRecording));
    }

    /// <summary>Settings that cannot be read at one key, which is how a start is made to fail.</summary>
    private sealed class FailingSettings : ISettingsService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? FailOn { get; set; }

        public AppTheme Theme { get; set; }

        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue)
        {
            if (string.Equals(key, FailOn, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(StartFailure);
            }

            return _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;
        }

        public void Set<T>(string key, T value) => _values[key] = value is null ? string.Empty : value;
    }

    // None of the four below may be asked for anything: the start fails before it gets that far.
    // If it does get further, the test fails on the wrong exception instead of capturing something.
    private static InvalidOperationException NotReached(string what) =>
        new($"The start was to fail before it asked for {what}.");

    private sealed class NoMonitors : IMonitorService
    {
        public IReadOnlyList<MonitorInfo> GetMonitors() => throw NotReached("the monitors");

        public MonitorInfo? GetPrimaryMonitor() => throw NotReached("the primary monitor");

        public MonitorInfo? GetMonitorUnderCursor() => throw NotReached("the monitor under the pointer");
    }

    private sealed class NoNames : IClipStorageService
    {
        public string FileExtensionFor(CaptureType type) => throw NotReached("a file extension");

        public string GenerateFilePath(CaptureType type, string? fileExtension = null, string? stemSuffix = null) =>
            throw NotReached("a file name");

        public string OutputDirectory(CaptureType type) => throw NotReached("the save folder");
    }

    private sealed class NoAnalytics : IClipAnalyticsService
    {
        public void RecordCapture(CaptureType type) => throw NotReached("the capture counts");

        public IReadOnlyList<DailyCaptureAnalytics> GetDailyCounts(int days) => throw NotReached("the capture counts");

        public LifetimeCaptureAnalytics GetLifetimeTotals() => throw NotReached("the capture counts");

        public IReadOnlyList<WeekdayCaptureTotal> GetWeekdayTotals(int days) => throw NotReached("the capture counts");

        public WeekdayCaptureTotal? GetBusiestWeekday(int days) => throw NotReached("the capture counts");

        public IReadOnlyList<HourCaptureTotal> GetHourlyTotals() => throw NotReached("the capture counts");

        public HourCaptureTotal? GetMostActiveHour() => throw NotReached("the capture counts");

        public void Clear() => throw NotReached("the capture counts");
    }

    /// <summary>A camera that is off, and that says so when the failed start tidies up.</summary>
    private sealed class NoCamera : IWebcamCaptureService
    {
        public bool IsRunning => false;

        public event EventHandler<WebcamCaptureFailedEventArgs>? CaptureFailed
        {
            add { }
            remove { }
        }

        public event EventHandler<WebcamFrameArrivedEventArgs>? FrameArrived
        {
            add { }
            remove { }
        }

        public Task StartAsync(string? deviceId, BitmapSize bitmapSize, CancellationToken cancellationToken = default) =>
            throw NotReached("the camera");

        public Task StopAsync() => Task.CompletedTask;

        public bool TryGetLatestFrame(out WebcamFrame? frame)
        {
            frame = null;
            return false;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
