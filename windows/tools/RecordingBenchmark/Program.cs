using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace TinyClips.Tools.RecordingBenchmark;

/// <summary>
/// Headless A/B harness for the Windows recording pipeline. Each scenario records the primary
/// monitor (or a centred region) for a fixed duration through the production
/// <see cref="VideoRecordingService"/> and reports the <see cref="RecordingPerformanceReport"/>
/// the service produces, plus output-file size. Run from an interactive desktop session (WGC
/// needs the DWM); the GPU scenarios fall back to CPU automatically when unavailable, which the
/// report's <c>pipeline</c> column makes visible.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = BenchmarkOptions.Parse(args);
        if (options is null)
        {
            BenchmarkOptions.PrintUsage();
            return 2;
        }

        // Must precede MonitorService and any monitor/window geometry query.
        EnsurePhysicalPixelCoordinates();
        Console.WriteLine($"TinyClips recording benchmark — {options.Seconds}s per scenario @ {options.Fps} fps, region={(options.Region is null ? "full monitor" : $"{options.Region.Value.Width}x{options.Region.Value.Height}")}, webcam={(options.Webcam ? "on" : "off")}, audio={(options.Audio ? "on" : "off")}");
        Console.WriteLine($"Machine: {Environment.ProcessorCount} logical cores, {Environment.OSVersion}");
        Console.WriteLine();

        var settingsStore = new InMemorySettingsService();
        var settings = new CaptureSettings(settingsStore);
        settings.VideoFrameRate = options.Fps;
        settings.RecordAudio = options.Audio;
        settings.RecordMicrophone = false;
        settings.VideoRecordingTimeLimitMinutes = 0;
        settings.WebcamEnabled = false;

        var monitors = new MonitorService();
        var primary = monitors.GetPrimaryMonitor() ?? throw new InvalidOperationException("No primary monitor found.");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "TinyClipsBenchmark");
        Directory.CreateDirectory(outputDirectory);
        var projectsDirectory = Path.Combine(Path.GetTempPath(), "TinyClipsBenchmarkProjects");
        Directory.CreateDirectory(projectsDirectory);

        await using IWebcamCaptureService webcam = options.Studio
            ? new SyntheticWebcamCaptureService()
            : new WebcamCaptureService();
        var studioStore = new StudioProjectStore(projectsDirectory);
        var recorder = new VideoRecordingService(
            monitors,
            new TempClipStorage(outputDirectory),
            settings,
            new NoOpAnalytics(),
            webcam,
            studioStore);

        PixelRect? region = null;
        if (options.Region is { } r)
        {
            // MonitorInfo is already in physical pixels, matching the WGC item size.
            region = new PixelRect(Math.Max(0, (primary.Width - r.Width) / 2),
                Math.Max(0, (primary.Height - r.Height) / 2), r.Width, r.Height);
        }

        CaptureTarget? target = null;
        if (!string.IsNullOrEmpty(options.WindowTitle))
        {
            var hwnd = FindWindowByTitle(options.WindowTitle);
            if (hwnd == 0)
            {
                Console.Error.WriteLine($"No visible top-level window with a title containing '{options.WindowTitle}'.");
                return 2;
            }

            target = CaptureTarget.Window(hwnd);
            region = null;
            Console.WriteLine($"Recording window 0x{hwnd:X} ('{options.WindowTitle}') — resize it during the run to exercise letterboxing.");
        }

        var results = new List<ScenarioResult>();
        foreach (var scenario in options.Scenarios)
        {
            for (var iteration = 1; iteration <= options.Iterations; iteration++)
            {
                var label = options.Iterations > 1 ? $"{scenario.Name} #{iteration}" : scenario.Name;
                Console.Write($"[{label}] recording... ");
                try
                {
                    var result = await RunScenarioAsync(recorder, settings, scenario, label, target, region, options).ConfigureAwait(false);
                    results.Add(result);
                    Console.WriteLine(result.Report is null
                        ? "no report"
                        : $"requested={result.Report.RequestedPipeline} actual={result.Report.Pipeline} cpu={result.Report.ProcessCpuPercent:F1}% activeFps={result.Report.ActiveFps:F1} queueDrops={result.Report.QueueDrops} cpuSkipped={result.Report.CpuSkippedTickEvents} gpuMissedSlots={result.Report.GpuPacingMissedSlots}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
                    results.Add(new ScenarioResult(label, scenario, null, 0, ex.Message));
                }

                // Let the encoder/driver settle and clear managed garbage so runs don't bleed into each other.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
            }
        }

        Console.WriteLine();
        Console.WriteLine(BuildComparisonTable(results));
        Console.WriteLine();
        foreach (var result in results.Where(r => r.Report is not null))
        {
            Console.WriteLine($"=== {result.Label} ===");
            Console.WriteLine(result.Report!.ToTable());
        }

        if (!string.IsNullOrEmpty(options.JsonPath))
        {
            var json = JsonSerializer.Serialize(
                results.Select(r => new
                {
                    SchemaVersion = 2,
                    DpiAwareness = "PerMonitorV2",
                    RequestedRegion = options.Region is { } requested
                        ? new { requested.Width, requested.Height } : null,
                    r.Label,
                    Scenario = r.Scenario.Name,
                    r.Scenario.RequestGpu,
                    r.Scenario.Overlays,
                    r.Scenario.SinkWriter,
                    r.Scenario.Hevc,
                    r.OutputBytes,
                    r.Error,
                    r.Report,
                }),
                new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(options.JsonPath, json).ConfigureAwait(false);
            Console.WriteLine($"Wrote {options.JsonPath}");
        }

        return results.Any(r => r.Error is not null) ? 1 : 0;
    }

    private static async Task<ScenarioResult> RunScenarioAsync(
        VideoRecordingService recorder,
        CaptureSettings settings,
        Scenario scenario,
        string label,
        CaptureTarget? target,
        PixelRect? region,
        BenchmarkOptions options)
    {
        settings.UseGpuRecordingPipeline = scenario.RequestGpu;
        settings.VideoEncoderBackend = scenario.SinkWriter ? VideoEncoderBackend.SinkWriter : VideoEncoderBackend.Transcoder;
        settings.VideoCodec = scenario.Hevc ? VideoCodec.Hevc : VideoCodec.H264;
        settings.ShowBrandingOverlay = scenario.Overlays;
        settings.ShowMouseClickVisualsInVideo = scenario.Overlays;
        settings.WebcamEnabled = options.Studio || (scenario.Overlays && options.Webcam);

        string? studioProjectId = null;
        void OnStudioCompleted(object? _, string id) => studioProjectId = id;
        recorder.StudioRecordingCompleted += OnStudioCompleted;
        await recorder.StartAsync(
            target,
            region,
            null,
            options.Studio ? new VideoRecordingOptions { RecordForStudio = true, AppVersion = "benchmark" } : VideoRecordingOptions.Default).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(options.Seconds)).ConfigureAwait(false);
        var path = await recorder.StopAsync().ConfigureAwait(false);
        recorder.StudioRecordingCompleted -= OnStudioCompleted;

        long bytes = 0;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            bytes = new FileInfo(path).Length;
            if (!options.KeepFiles)
            {
                File.Delete(path);
            }
            else
            {
                Console.Write($"saved {path} ");
            }
        }

        if (options.Studio && studioProjectId is not null)
        {
            PrintStudioProject(studioProjectId);
        }

        return new ScenarioResult(label, scenario, recorder.LastPerformanceReport, bytes, null);
    }

    private static void PrintStudioProject(string projectId)
    {
        var store = new StudioProjectStore(Path.Combine(Path.GetTempPath(), "TinyClipsBenchmarkProjects"));
        var project = store.Load(projectId);
        var paths = store.GetPaths(project);
        var events = store.LoadEvents(projectId);
        var screen = MediaFileProbe.Probe(paths.ScreenPath, project.Sources.Screen.FrameRate);
        Console.WriteLine($"studio project={projectId} valid=true screen={screen.Width}x{screen.Height} duration={screen.Duration:F3}s fps={screen.FrameRate:F2}");
        if (project.Sources.Camera is not null && paths.CameraPath is not null)
        {
            var camera = MediaFileProbe.Probe(paths.CameraPath, 30);
            Console.WriteLine($"studio camera={camera.Width}x{camera.Height} duration={camera.Duration:F3}s fps={camera.FrameRate:F2} startOffset={project.Sources.Camera.StartOffset:F3}s");
        }
        else
        {
            Console.WriteLine("studio camera=none");
        }

        Console.WriteLine($"studio events clicks={events.Clicks.Length} cursor={events.Cursor.Length} cameraCorners={events.CameraCorners.Length}");
        store.Delete(projectId);
    }

    private static string BuildComparisonTable(IReadOnlyList<ScenarioResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("scenario               req/actual  size       cpu%   cores activeFps emitted submitted dropped* cpuSkip gpuEvents/slots alloc MB/s gc0/1/2 gcPause% composite avg/p99 ms readback avg ms produce avg ms encWait avg ms MB encoder (selection unverified)");
        foreach (var r in results)
        {
            if (r.Report is null)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{r.Label,-22} FAILED: {r.Error}"));
                continue;
            }

            var rep = r.Report;
            var composite = rep.Stages.FirstOrDefault(s => s.Stage == RecordingStage.Composite);
            var readback = rep.Stages.FirstOrDefault(s => s.Stage == RecordingStage.CaptureReadback);
            var produce = rep.Stages.FirstOrDefault(s => s.Stage == RecordingStage.FrameProduce);
            var encWait = rep.Stages.FirstOrDefault(s => s.Stage == RecordingStage.EncoderWait);
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{r.Label,-22} {rep.RequestedPipeline}/{rep.Pipeline,-6} {rep.Width}x{rep.Height,-5} {rep.ProcessCpuPercent,6:F1} {rep.ProcessCpuCores,6:F2} {rep.ActiveFps,7:F1} {rep.FramesEmitted,8} {rep.FramesSubmitted,9} {rep.FramesDropped,8} {rep.CpuSkippedTickEvents,7} {rep.GpuPacingOverrunEvents}/{rep.GpuPacingMissedSlots} {rep.AllocationMbPerSecond,11:F1}  {rep.Gen0Collections}/{rep.Gen1Collections}/{rep.Gen2Collections,-6} {rep.GcPausePercent,7:F1}  {composite?.AverageMs ?? 0,8:F3}/{composite?.P99Ms ?? 0,-8:F3} {readback?.AverageMs ?? 0,15:F3} {produce?.AverageMs ?? 0,15:F3} {encWait?.AverageMs ?? 0,15:F3} {r.OutputBytes / 1024.0 / 1024.0,5:F1}  {rep.EncoderPath}"));
        }

        sb.AppendLine("* dropped = queue + pool + production only; pacing events/slots are separate and must not be summed. Submitted is not decoded output.");
        return sb.ToString();
    }

    private static void EnsurePhysicalPixelCoordinates()
    {
        var perMonitorV2 = (nint)(-4);
        var established = SetProcessDpiAwarenessContext(perMonitorV2);
        var error = established ? 0 : Marshal.GetLastWin32Error();
        if (!established &&
            !AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), perMonitorV2))
        {
            throw new Win32Exception(error, "Benchmark requires Per-Monitor-V2 DPI awareness.");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);

    private static nint FindWindowByTitle(string titleFragment)
    {
        nint found = 0;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            var length = GetWindowTextLength(hwnd);
            if (length <= 0)
            {
                return true;
            }

            var buffer = new char[length + 1];
            GetWindowText(hwnd, buffer, buffer.Length);
            var title = new string(buffer, 0, length);
            if (title.Contains(titleFragment, StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
                return false;
            }

            return true;
        }, 0);
        return found;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, [System.Runtime.InteropServices.Out] char[] text, int maxCount);

    private sealed record Scenario(string Name, bool RequestGpu, bool Overlays, bool SinkWriter, bool Hevc);

    private sealed record ScenarioResult(string Label, Scenario Scenario, RecordingPerformanceReport? Report, long OutputBytes, string? Error);

    private sealed class BenchmarkOptions
    {
        public int Seconds { get; private set; } = 10;

        public int Fps { get; private set; } = 30;

        public int Iterations { get; private set; } = 1;

        public bool Webcam { get; private set; }

        public bool Audio { get; private set; }

        public bool Studio { get; private set; }

        public bool KeepFiles { get; private set; }

        public string? JsonPath { get; private set; }

        public (int Width, int Height)? Region { get; private set; }

        /// <summary>Substring of a top-level window title to record instead of the primary monitor.</summary>
        public string? WindowTitle { get; private set; }

        public List<Scenario> Scenarios { get; } = new();

        public static BenchmarkOptions? Parse(string[] args)
        {
            var options = new BenchmarkOptions();
            var scenarioNames = new List<string> { "cpu", "gpu", "cpu+overlays", "gpu+overlays" };
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--seconds" when i + 1 < args.Length:
                        if (!TryParseInRange(args[++i], "--seconds", 1, 3600, out var seconds))
                        {
                            return null;
                        }

                        options.Seconds = seconds;
                        break;
                    case "--fps" when i + 1 < args.Length:
                        // Mirror VideoRecordingService's clamp so the label matches what is recorded.
                        if (!TryParseInRange(args[++i], "--fps", 1, 60, out var fps))
                        {
                            return null;
                        }

                        options.Fps = fps;
                        break;
                    case "--iterations" when i + 1 < args.Length:
                        if (!TryParseInRange(args[++i], "--iterations", 1, 1000, out var iterations))
                        {
                            return null;
                        }

                        options.Iterations = iterations;
                        break;
                    case "--scenarios" when i + 1 < args.Length:
                        scenarioNames = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                        break;
                    case "--region" when i + 1 < args.Length:
                        var parts = args[++i].Split('x', StringSplitOptions.TrimEntries);
                        if (parts.Length != 2 ||
                            !TryParseInRange(parts[0], "--region width", 2, 16384, out var regionWidth) ||
                            !TryParseInRange(parts[1], "--region height", 2, 16384, out var regionHeight))
                        {
                            Console.Error.WriteLine("--region expects WxH, e.g. 1920x1080.");
                            return null;
                        }

                        options.Region = (regionWidth, regionHeight);
                        break;
                    case "--window" when i + 1 < args.Length:
                        options.WindowTitle = args[++i];
                        break;
                    case "--json" when i + 1 < args.Length:
                        options.JsonPath = args[++i];
                        break;
                    case "--webcam":
                        options.Webcam = true;
                        break;
                    case "--audio":
                        options.Audio = true;
                        break;
                    case "--studio":
                        options.Studio = true;
                        break;
                    case "--keep":
                        options.KeepFiles = true;
                        break;
                    case "-h":
                    case "--help":
                        return null;
                    default:
                        Console.Error.WriteLine($"Unknown argument: {args[i]}");
                        return null;
                }
            }

            foreach (var name in scenarioNames)
            {
                // Grammar: (cpu|gpu)[+overlays][+sink][+hevc]. Unknown modifiers are rejected rather
                // than ignored so a typo cannot run the wrong configuration under the right label.
                var parts = name.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0 || parts[0].ToLowerInvariant() is not ("cpu" or "gpu"))
                {
                    Console.Error.WriteLine($"Unknown scenario '{name}'. Use cpu or gpu, optionally +overlays, +sink, +hevc.");
                    return null;
                }

                var modifiers = parts.Skip(1).Select(m => m.ToLowerInvariant()).ToList();
                var unknown = modifiers.Where(m => m is not ("overlays" or "sink" or "hevc")).ToList();
                if (unknown.Count > 0)
                {
                    Console.Error.WriteLine($"Unknown scenario modifier(s) '{string.Join("', '", unknown)}' in '{name}'. Valid modifiers: overlays, sink, hevc.");
                    return null;
                }

                options.Scenarios.Add(new Scenario(
                    name,
                    parts[0].Equals("gpu", StringComparison.OrdinalIgnoreCase),
                    modifiers.Contains("overlays"),
                    modifiers.Contains("sink"),
                    modifiers.Contains("hevc")));
            }

            return options;
        }

        private static bool TryParseInRange(string text, string option, int min, int max, out int value)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                Console.Error.WriteLine($"{option}: '{text}' is not a whole number.");
                return false;
            }

            if (value < min || value > max)
            {
                Console.Error.WriteLine($"{option} must be between {min} and {max}.");
                return false;
            }

            return true;
        }
        public static void PrintUsage()
        {
            Console.WriteLine("""
                Usage: RecordingBenchmark [--seconds N] [--fps N] [--iterations N] [--scenarios cpu,gpu,cpu+overlays,gpu+overlays]
                                          [--region WxH] [--webcam] [--audio] [--studio] [--keep] [--json out.json]

                  --seconds     Recording length per scenario (default 10).
                  --fps         Target frame rate (default 30).
                  --iterations  Repeat each scenario N times (default 1).
                  --scenarios   Comma-separated list of (cpu|gpu)[+overlays][+sink][+hevc]. "+overlays" enables branding +
                                click visuals (+ webcam with --webcam); "+sink" uses the IMFSinkWriter encoder backend;
                                "+hevc" records H.265. Default: cpu,gpu,cpu+overlays,gpu+overlays.
                  --region      Record a centred WxH region of the primary monitor instead of the whole screen.
                  --window      Record the first visible window whose title contains this text (resize it to test letterboxing).
                  --webcam      Enable the webcam overlay in "+overlays" scenarios (needs a camera and permission).
                  --audio       Record system audio too (exercises the muxer's audio path).
                  --studio      Record Studio projects with a synthetic camera under %TEMP% and delete them after probing.
                  --keep        Keep the recorded MP4s in %TEMP%\TinyClipsBenchmark.
                  --json        Also write all reports as JSON.
                """);
        }
    }

    private sealed class InMemorySettingsService : ISettingsService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

        public AppTheme Theme { get; set; }

        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue)
        {
            if (_values.TryGetValue(key, out var value))
            {
                if (value is T typed)
                {
                    return typed;
                }

                if (value is string s && typeof(T).IsEnum)
                {
                    return (T)Enum.Parse(typeof(T), s, true);
                }
            }

            return defaultValue;
        }

        public void Set<T>(string key, T value) => _values[key] = value is null ? string.Empty : value;
    }

    private sealed class TempClipStorage : IClipStorageService
    {
        private readonly string _directory;

        public TempClipStorage(string directory)
        {
            _directory = directory;
        }

        public string FileExtensionFor(CaptureType type) => type switch
        {
            CaptureType.Video => ".mp4",
            CaptureType.Gif => ".gif",
            _ => ".png",
        };

        public string GenerateFilePath(CaptureType type, string? fileExtension = null, string? stemSuffix = null) =>
            Path.Combine(_directory, $"bench-{DateTime.Now:yyyyMMdd-HHmmss-fff}{stemSuffix}{fileExtension ?? FileExtensionFor(type)}");

        public string OutputDirectory(CaptureType type) => _directory;
    }

    private sealed class SyntheticWebcamCaptureService : IWebcamCaptureService
    {
        private CancellationTokenSource? _cts;
        private Task? _pump;
        private WebcamFrame? _latest;
        private readonly object _gate = new();

        public bool IsRunning { get; private set; }

        public event EventHandler<WebcamCaptureFailedEventArgs>? CaptureFailed
        {
            add { }
            remove { }
        }

        public event EventHandler<WebcamFrameArrivedEventArgs>? FrameArrived;

        public Task StartAsync(string? deviceId, BitmapSize bitmapSize, CancellationToken cancellationToken = default)
        {
            var width = (int)Math.Clamp(bitmapSize.Width, 2, 1920);
            var height = (int)Math.Clamp(bitmapSize.Height, 2, 1080);
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            IsRunning = true;
            _pump = Task.Run(() => PumpAsync(width, height, _cts.Token), CancellationToken.None);
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            IsRunning = false;
            _cts?.Cancel();
            if (_pump is not null)
            {
                try
                {
                    await _pump.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            _cts?.Dispose();
            _cts = null;
            _pump = null;
        }

        public bool TryGetLatestFrame(out WebcamFrame? frame)
        {
            lock (_gate)
            {
                frame = _latest;
                return frame is not null;
            }
        }

        public void SetPreferredDirect3DDevice(IDirect3DDevice? device)
        {
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
        }

        private async Task PumpAsync(int width, int height, CancellationToken cancellationToken)
        {
            var pixels = new byte[width * height * 4];
            var frameIndex = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / 30));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                FillFrame(pixels, width, height, frameIndex++);
                var copy = new byte[pixels.Length];
                Buffer.BlockCopy(pixels, 0, copy, 0, pixels.Length);
                var frame = new WebcamFrame(copy, width, height, SystemRelativeNow());
                lock (_gate)
                {
                    _latest = frame;
                }

                FrameArrived?.Invoke(this, new WebcamFrameArrivedEventArgs(frame));
            }
        }

        private static void FillFrame(byte[] pixels, int width, int height, int frameIndex)
        {
            var ballX = (frameIndex * 11) % width;
            var ballY = (frameIndex * 7) % height;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    pixels[i] = (byte)(40 + (x * 120 / Math.Max(1, width)));
                    pixels[i + 1] = (byte)(30 + (y * 120 / Math.Max(1, height)));
                    pixels[i + 2] = 80;
                    pixels[i + 3] = 255;
                    var dx = x - ballX;
                    var dy = y - ballY;
                    if (dx * dx + dy * dy < 80 * 80)
                    {
                        pixels[i] = 20;
                        pixels[i + 1] = 220;
                        pixels[i + 2] = 255;
                    }
                }
            }
        }

        private static TimeSpan SystemRelativeNow() =>
            Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp());
    }

    private sealed class NoOpAnalytics : IClipAnalyticsService
    {
        public void RecordCapture(CaptureType type)
        {
        }

        public IReadOnlyList<DailyCaptureAnalytics> GetDailyCounts(int days) => [];

        public LifetimeCaptureAnalytics GetLifetimeTotals() => new(0, 0, 0);

        public IReadOnlyList<WeekdayCaptureTotal> GetWeekdayTotals(int days) => [];

        public WeekdayCaptureTotal? GetBusiestWeekday(int days) => null;

        public IReadOnlyList<HourCaptureTotal> GetHourlyTotals() => [];

        public HourCaptureTotal? GetMostActiveHour() => null;

        public void Clear()
        {
        }
    }
}
