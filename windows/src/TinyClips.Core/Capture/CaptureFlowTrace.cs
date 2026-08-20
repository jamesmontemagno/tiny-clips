using System.Diagnostics;
using TinyClips.Core.Services;

namespace TinyClips.Core.Capture;

/// <summary>
/// Lightweight latency instrumentation for the capture flow (hotkey → picker → overlay →
/// capture → editor/recorder). Each <see cref="Mark"/> records the elapsed time since
/// <see cref="Begin"/> and since the previous mark, so transitions that feel laggy can be
/// measured instead of guessed. Output goes to <see cref="Debug"/> always, and additionally to
/// <c>%LOCALAPPDATA%\TinyClips\Temp\capture-flow-trace.log</c> when the
/// <c>TINYCLIPS_CAPTURE_TRACE</c> environment variable is set (or in DEBUG builds), so packaged
/// builds can be profiled without a debugger. All I/O is best-effort and never throws.
/// </summary>
public static class CaptureFlowTrace
{
    private static readonly object Gate = new();
    private static readonly bool FileLoggingEnabled = ResolveFileLogging();
    private static long _flowStart;
    private static long _lastMark;
    private static string _flowName = string.Empty;

    private static bool ResolveFileLogging()
    {
#if DEBUG
        return true;
#else
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TINYCLIPS_CAPTURE_TRACE"));
#endif
    }

    /// <summary>Starts a new timed flow (e.g. "screenshot", "video"). Resets the clock.</summary>
    public static void Begin(string flowName)
    {
        lock (Gate)
        {
            _flowName = flowName;
            _flowStart = Stopwatch.GetTimestamp();
            _lastMark = _flowStart;
        }

        Write($"[{flowName}] begin");
    }

    /// <summary>Records a named point in the current flow with total and delta elapsed time.</summary>
    public static void Mark(string label)
    {
        long now = Stopwatch.GetTimestamp();
        double totalMs;
        double deltaMs;
        string flow;
        lock (Gate)
        {
            if (_flowStart == 0)
            {
                _flowStart = now;
                _lastMark = now;
            }

            totalMs = Stopwatch.GetElapsedTime(_flowStart, now).TotalMilliseconds;
            deltaMs = Stopwatch.GetElapsedTime(_lastMark, now).TotalMilliseconds;
            _lastMark = now;
            flow = _flowName;
        }

        Write($"[{flow}] +{deltaMs,7:F1} ms  (t={totalMs,8:F1} ms)  {label}");
    }

    private static void Write(string line)
    {
        Debug.WriteLine($"CaptureFlowTrace {line}");
        if (!FileLoggingEnabled)
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                var path = Path.Combine(TinyClipsTemporaryFiles.EnsureDirectoryExists(), "capture-flow-trace.log");
                File.AppendAllText(path, $"{DateTimeOffset.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}");
            }
            catch
            {
                // Best-effort only.
            }
        }
    }
}
