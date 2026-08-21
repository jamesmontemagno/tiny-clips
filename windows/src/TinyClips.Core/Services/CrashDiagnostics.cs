namespace TinyClips.Core.Services;

/// <summary>
/// Best-effort persistent logger for unhandled exceptions in the packaged app, where
/// <c>Debug.WriteLine</c> is invisible. Writes to <c>%LOCALAPPDATA%\TinyClips\Logs\crash.log</c>,
/// a sibling of the temporary-files folder so the Settings "purge temporary files" action never
/// removes it. The file is capped and rolled so it cannot grow without bound. Never throws.
/// </summary>
public static class CrashDiagnostics
{
    private const string ApplicationFolderName = "TinyClips";
    private const string LogsFolderName = "Logs";
    private const string LogFileName = "crash.log";
    private const string PreviousLogFileName = "crash.previous.log";
    private const long MaxLogBytes = 512 * 1024;

    private static readonly object Gate = new();

    /// <summary>The directory that holds persisted crash logs.</summary>
    public static string DirectoryPath
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var rootDirectory = string.IsNullOrWhiteSpace(localAppData)
                ? Path.GetTempPath()
                : localAppData;

            return Path.Combine(rootDirectory, ApplicationFolderName, LogsFolderName);
        }
    }

    /// <summary>The active crash log file path.</summary>
    public static string LogPath => Path.Combine(DirectoryPath, LogFileName);

    /// <summary>Appends a timestamped entry describing an exception to the default crash log.</summary>
    public static void Log(string source, object? exception, bool handled) =>
        Log(LogPath, source, exception, handled);

    /// <summary>Appends a timestamped entry describing an exception to the given crash log.</summary>
    public static void Log(string logPath, string source, object? exception, bool handled)
    {
        lock (Gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                RollIfNeeded(logPath);

                var entry =
                    $"=== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} | {source} | {(handled ? "handled" : "unhandled")} | pid {Environment.ProcessId}{Environment.NewLine}" +
                    $"{exception ?? "(no exception object)"}{Environment.NewLine}{Environment.NewLine}";
                File.AppendAllText(logPath, entry);
            }
            catch
            {
                // Best-effort only.
            }
        }
    }

    private static void RollIfNeeded(string logPath)
    {
        var file = new FileInfo(logPath);
        if (!file.Exists || file.Length < MaxLogBytes)
        {
            return;
        }

        var directory = Path.GetDirectoryName(logPath) ?? string.Empty;
        var previousPath = Path.Combine(directory, PreviousLogFileName);
        File.Move(logPath, previousPath, overwrite: true);
    }
}
