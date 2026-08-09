using TinyClips.Core.Services;

namespace TinyClips.Core.Capture;

/// <summary>
/// Lightweight, best-effort file logger for diagnosing webcam capture/overlay failures
/// in the packaged app, where Debug.WriteLine is invisible. Writes timestamped lines to
/// <c>%LOCALAPPDATA%\TinyClips\Temp\webcam-diagnostics.log</c>. LocalApplicationData is used
/// (rather than Pictures) because it is writable without tripping Controlled Folder Access /
/// Windows Security prompts. All I/O failures are swallowed. The log is truncated at the start
/// of each recording so it always reflects the latest run.
/// </summary>
public static class WebcamDiagnostics
{
    private static readonly object Gate = new();
    private static string? _logPath;

    private static string ResolveLogPath()
    {
        if (_logPath is not null)
        {
            return _logPath;
        }

        _logPath = Path.Combine(TinyClipsTemporaryFiles.EnsureDirectoryExists(), "webcam-diagnostics.log");
        return _logPath;
    }

    /// <summary>Truncates the log and writes a header. Call once per recording start.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            try
            {
                var path = ResolveLogPath();
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, $"=== TinyClips webcam diagnostics — {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} ==={Environment.NewLine}");
            }
            catch
            {
                // Best-effort only.
            }
        }
    }

    /// <summary>Appends a timestamped line. Never throws.</summary>
    public static void Log(string message)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(ResolveLogPath(), $"{DateTimeOffset.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch
            {
                // Best-effort only.
            }
        }
    }
}
