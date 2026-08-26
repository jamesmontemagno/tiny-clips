using System.Diagnostics;

namespace TinyClips.Core.Services.ClipsLibrary;

public interface IClipLibraryWatcher : IDisposable
{
    /// <summary>Raised (debounced, possibly on a background thread) when watched folders change.</summary>
    event EventHandler? Changed;

    /// <summary>While true, file events are swallowed (used during our own batch operations).</summary>
    bool IsPaused { get; set; }

    void Watch(IEnumerable<string> directories);

    void Stop();
}

/// <summary>
/// Coalesces a burst of signals into a single callback once they go quiet for
/// <see cref="Delay"/>. Pure timer logic so it can be tested with a fake <see cref="TimeProvider"/>.
/// </summary>
public sealed class DebouncedSignal : IDisposable
{
    private readonly ITimer _timer;
    private readonly Action _callback;

    public DebouncedSignal(Action callback, TimeSpan delay, TimeProvider? timeProvider = null)
    {
        _callback = callback;
        Delay = delay;
        _timer = (timeProvider ?? TimeProvider.System).CreateTimer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public TimeSpan Delay { get; }

    public int FireCount { get; private set; }

    public void Signal() => _timer.Change(Delay, Timeout.InfiniteTimeSpan);

    public void Cancel() => _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    public void Dispose() => _timer.Dispose();

    private void Fire()
    {
        FireCount++;
        _callback();
    }
}

/// <summary>
/// Watches the library's capture directories with <see cref="FileSystemWatcher"/> and raises a
/// single debounced <see cref="Changed"/> after activity settles. Ignores in-progress temp files
/// and non-clip file types so recording finalization does not trigger churn.
/// </summary>
public sealed class ClipLibraryWatcher : IClipLibraryWatcher
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(750);

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly DebouncedSignal _signal;
    private readonly object _gate = new();

    public event EventHandler? Changed;

    public ClipLibraryWatcher(TimeProvider? timeProvider = null, TimeSpan? debounce = null)
    {
        _signal = new DebouncedSignal(() => Changed?.Invoke(this, EventArgs.Empty), debounce ?? DefaultDebounce, timeProvider);
    }

    public bool IsPaused { get; set; }

    public void Watch(IEnumerable<string> directories)
    {
        lock (_gate)
        {
            Stop();
            foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                try
                {
                    var watcher = new FileSystemWatcher(directory)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    };
                    watcher.Created += OnFileEvent;
                    watcher.Deleted += OnFileEvent;
                    watcher.Renamed += OnRenamed;
                    watcher.Changed += OnFileEvent;
                    watcher.Error += (_, _) => _signal.Signal();
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ClipLibraryWatcher: cannot watch '{directory}': {ex.Message}");
                }
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            _watchers.Clear();
            _signal.Cancel();
        }
    }

    public void Dispose()
    {
        Stop();
        _signal.Dispose();
    }

    /// <summary>True when a path change should be surfaced to the library.</summary>
    public static bool IsRelevant(string path) =>
        ClipLibraryService.IsSupportedClipFile(path)
        && !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        && !Path.GetFileName(path).StartsWith("~", StringComparison.Ordinal);

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (!IsPaused && IsRelevant(e.FullPath))
        {
            _signal.Signal();
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsPaused && (IsRelevant(e.FullPath) || IsRelevant(e.OldFullPath)))
        {
            _signal.Signal();
        }
    }
}
