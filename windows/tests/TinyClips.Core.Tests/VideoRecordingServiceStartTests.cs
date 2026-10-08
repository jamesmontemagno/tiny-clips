using System.Reflection;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;
using Windows.Graphics.Imaging;

namespace TinyClips.Core.Tests;

/// <summary>
/// What a start of the video recorder that fails does to the recording made before it. Each
/// start here is cancelled when the recorder first looks at its token, which is how a countdown
/// that is cancelled while the recorder is still getting ready ends. That is before the recorder
/// reads a setting, writes its log or asks Windows for anything, so nothing is captured, no
/// camera or microphone is opened, and no file outside the test's own folder is touched.
/// </summary>
public sealed class VideoRecordingServiceStartTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TinyClipsTests", Guid.NewGuid().ToString("N"));
    private readonly CancellingMonitors _monitors = new();
    private readonly NoSettings _saved = new();
    private readonly VideoRecordingService _recorder;
    private readonly string _lastRecording;

    public VideoRecordingServiceStartTests()
    {
        Directory.CreateDirectory(_directory);
        _lastRecording = Path.Combine(_directory, "TinyClips 2026-10-05 at 12.00.00.mp4");
        File.WriteAllBytes(_lastRecording, [1, 2, 3]);

        _recorder = new VideoRecordingService(
            _monitors,
            new NoNames(),
            new CaptureSettings(_saved),
            new NoAnalytics(),
            new NoCamera(),
            new StudioProjectStore(Path.Combine(_directory, "Projects")));
        _saved.MayBeRead = false;

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
        await CancelAsItBegins(token => _recorder.PrepareAsync(null, null, token));

        Assert.False(_recorder.IsRecording);
        Assert.True(File.Exists(_lastRecording), "The start that failed deleted the recording made before it.");
        Assert.Equal([1, 2, 3], File.ReadAllBytes(_lastRecording));
    }

    [Fact]
    public async Task AStartThatFailsBeforeItHasAFile_LeavesTheLastRecordingWhereItIs()
    {
        await CancelAsItBegins(token => _recorder.StartAsync(null, null, null, token));

        Assert.False(_recorder.IsRecording);
        Assert.True(File.Exists(_lastRecording), "The start that failed deleted the recording made before it.");
    }

    [Fact]
    public async Task ASecondStartThatFails_LeavesItToo()
    {
        await CancelAsItBegins(token => _recorder.PrepareAsync(null, null, token));
        await CancelAsItBegins(token => _recorder.StartAsync(null, null, null, token));

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
        await CancelAsItBegins(token => _recorder.PrepareAsync(null, null, token));

        await _recorder.CancelAsync();

        Assert.True(File.Exists(_lastRecording));
    }

    /// <summary>
    /// Makes one start and cancels it between the recorder taking its turn and its first look at
    /// the token: the recorder asks for the primary monitor in between, and that is where the
    /// token is cancelled. So the start gets into its preparation and fails at the first line
    /// that can fail.
    /// </summary>
    private async Task CancelAsItBegins(Func<CancellationToken, Task> start)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var askedBefore = _monitors.Asked;
        _monitors.CancelWhenAsked = cancel;

        await Assert.ThrowsAsync<OperationCanceledException>(() => start(cancel.Token));

        Assert.Equal(askedBefore + 1, _monitors.Asked);
    }

    // The settings, the file names, the capture counts and the camera may not be asked for
    // anything: the start fails before it gets to any of them. If it does get further, the test
    // fails on the wrong exception instead of capturing something.
    private static InvalidOperationException NotReached(string what) =>
        new($"The start was to fail before it asked for {what}.");

    /// <summary>The one thing a start asks for before it looks at its token, and where the tests cancel it.</summary>
    private sealed class CancellingMonitors : IMonitorService
    {
        public CancellationTokenSource? CancelWhenAsked { get; set; }

        public int Asked { get; private set; }

        public MonitorInfo? GetPrimaryMonitor()
        {
            Asked++;
            CancelWhenAsked?.Cancel();
            return new MonitorInfo
            {
                DeviceName = "A display that is never captured",
                X = 0,
                Y = 0,
                Width = 1920,
                Height = 1080,
                WorkAreaX = 0,
                WorkAreaY = 0,
                WorkAreaWidth = 1920,
                WorkAreaHeight = 1040,
                DpiX = 96,
                DpiY = 96,
                IsPrimary = true,
                HMonitor = 1,
            };
        }

        public IReadOnlyList<MonitorInfo> GetMonitors() => throw NotReached("the monitors");

        public MonitorInfo? GetMonitorUnderCursor() => throw NotReached("the monitor under the pointer");
    }

    /// <summary>Settings that may be read while the recorder is built, and not after.</summary>
    private sealed class NoSettings : ISettingsService
    {
        public bool MayBeRead { get; set; } = true;

        public AppTheme Theme { get; set; }

        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue) => MayBeRead ? defaultValue : throw NotReached($"the setting {key}");

        public void Set<T>(string key, T value)
        {
            if (!MayBeRead)
            {
                throw NotReached($"the setting {key}");
            }
        }
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
