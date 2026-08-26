using System.Globalization;
using System.Text;
using System.Text.Json;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

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

        await using var webcam = new WebcamCaptureService();
        var recorder = new VideoRecordingService(
            monitors,
            new TempClipStorage(outputDirectory),
            settings,
            new NoOpAnalytics(),
            webcam);

        PixelRect? region = null;
        if (options.Region is { } r)
        {
            // MonitorInfo is already in physical pixels, matching the WGC item size.
            var w = Math.Min(r.Width, primary.Width);
            var h = Math.Min(r.Height, primary.Height);
            region = new PixelRect((primary.Width - w) / 2, (primary.Height - h) / 2, w, h);
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
                        : $"pipeline={result.Report.Pipeline} cpu={result.Report.ProcessCpuPercent:F1}% effFps={result.Report.EffectiveFps:F1} dropped={result.Report.FramesDropped}");
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
        settings.WebcamEnabled = scenario.Overlays && options.Webcam;

        await recorder.StartAsync(target, region).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(options.Seconds)).ConfigureAwait(false);
        var path = await recorder.StopAsync().ConfigureAwait(false);

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

        return new ScenarioResult(label, scenario, recorder.LastPerformanceReport, bytes, null);
    }

    private static string BuildComparisonTable(IReadOnlyList<ScenarioResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("scenario               pipeline  size       cpu%   cores  effFps  emitted  encoded  dropped  alloc MB/s  gc0/1/2   gcPause%  composite avg/p99 ms  readback avg ms  produce avg ms  encWait avg ms  MB   encoder");
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
                $"{r.Label,-22} {rep.Pipeline,-8} {rep.Width}x{rep.Height,-5} {rep.ProcessCpuPercent,6:F1} {rep.ProcessCpuCores,6:F2} {rep.EffectiveFps,7:F1} {rep.FramesEmitted,8} {rep.FramesEncoded,8} {rep.FramesDropped,8} {rep.AllocationMbPerSecond,11:F1}  {rep.Gen0Collections}/{rep.Gen1Collections}/{rep.Gen2Collections,-6} {rep.GcPausePercent,7:F1}  {composite?.AverageMs ?? 0,8:F3}/{composite?.P99Ms ?? 0,-8:F3} {readback?.AverageMs ?? 0,15:F3} {produce?.AverageMs ?? 0,15:F3} {encWait?.AverageMs ?? 0,15:F3} {r.OutputBytes / 1024.0 / 1024.0,5:F1}  {rep.EncoderPath}"));
        }

        return sb.ToString();
    }

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
                        options.Seconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
                        if (options.Seconds < 1 || options.Seconds > 3600)
                        {
                            Console.Error.WriteLine("--seconds must be between 1 and 3600.");
                            return null;
                        }

                        break;
                    case "--fps" when i + 1 < args.Length:
                        options.Fps = int.Parse(args[++i], CultureInfo.InvariantCulture);
                        // Mirror VideoRecordingService's clamp so the label matches what is recorded.
                        if (options.Fps < 1 || options.Fps > 60)
                        {
                            Console.Error.WriteLine("--fps must be between 1 and 60 (the recorder's supported range).");
                            return null;
                        }

                        break;
                    case "--iterations" when i + 1 < args.Length:
                        options.Iterations = Math.Max(1, int.Parse(args[++i], CultureInfo.InvariantCulture));
                        break;
                    case "--scenarios" when i + 1 < args.Length:
                        scenarioNames = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                        break;
                    case "--region" when i + 1 < args.Length:
                        var parts = args[++i].Split('x');
                        options.Region = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
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
                // Grammar: (cpu|gpu)[+overlays][+sink][+hevc]
                var parts = name.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var gpu = parts[0].ToLowerInvariant() switch
                {
                    "gpu" => true,
                    "cpu" => false,
                    _ => throw new ArgumentException($"Unknown scenario '{name}'. Use cpu or gpu, optionally +overlays, +sink, +hevc."),
                };
                var overlays = parts.Skip(1).Contains("overlays", StringComparer.OrdinalIgnoreCase);
                var sink = parts.Skip(1).Contains("sink", StringComparer.OrdinalIgnoreCase);
                var hevc = parts.Skip(1).Contains("hevc", StringComparer.OrdinalIgnoreCase);
                options.Scenarios.Add(new Scenario(name, gpu, overlays, sink, hevc));
            }

            return options;
        }

        public static void PrintUsage()
        {
            Console.WriteLine("""
                Usage: RecordingBenchmark [--seconds N] [--fps N] [--iterations N] [--scenarios cpu,gpu,cpu+overlays,gpu+overlays]
                                          [--region WxH] [--webcam] [--audio] [--keep] [--json out.json]

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
