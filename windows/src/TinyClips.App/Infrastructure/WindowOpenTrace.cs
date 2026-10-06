using System.Diagnostics.Tracing;

namespace TinyClips.App;

internal enum WindowOpenKind
{
    Settings = 1,
    Library,
    ScreenshotEditor,
}

internal enum WindowOpenPhase
{
    ConstructorBody = 1,
    ServicesAndViewModel,
    Xaml,
    ControllerAndBindings,
    ChromeAndPlacement,
    ThemeAndSubscriptions,
    SettingsSection,
    NavigationRebuild,
    NativeActivation,
    ForegroundRequest,
    DeferredActivation,
    ContentLoad,
}

internal enum WindowOpenMilestone
{
    ConstructionRequested = 1,
    ConstructorEntered,
    ConstructorCompleted,
    RootLoaded,
    FirstActivated,
    LoadedDispatcherTurn,
    ActivatedDispatcherTurn,
    ContentReady,
    ContentFailed,
    Closed,
    DispatcherUnavailable,
}

[EventSource(Name = "TinyClips-WindowOpen")]
internal sealed class WindowOpenEventSource : EventSource
{
    public static readonly WindowOpenEventSource Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void Timing(long openId, int windowKind, int eventKind, int stage, double elapsedMs, double durationMs)
    {
        if (IsEnabled())
        {
            WriteEvent(1, openId, windowKind, eventKind, stage, elapsedMs, durationMs);
        }
    }
}

/// <summary>
/// Opt-in, fixed-schema timings. No paths, content, exception text, file I/O or startup warming.
/// Phase durations are inclusive elapsed time, not CPU time or proof of interactivity.
/// </summary>
internal sealed class WindowOpenTrace
{
    private static long _nextId;
    private readonly long _id = Interlocked.Increment(ref _nextId);
    private readonly WindowOpenKind _kind;
    private readonly TimeProvider _time;
    private readonly long _start;
    private bool _closed;

    private WindowOpenTrace(WindowOpenKind kind, TimeProvider time)
    {
        _kind = kind;
        _time = time;
        _start = time.GetTimestamp();
        Mark(WindowOpenMilestone.ConstructionRequested);
    }

    public static WindowOpenTrace? Start(WindowOpenKind kind, TimeProvider? time = null) =>
        WindowOpenEventSource.Log.IsEnabled() ? new(kind, time ?? TimeProvider.System) : null;

    public void Mark(WindowOpenMilestone milestone)
    {
        if (_closed)
        {
            return;
        }

        Write(eventKind: 0, (int)milestone, durationMs: 0);
        if (milestone == WindowOpenMilestone.Closed)
        {
            _closed = true;
        }
    }

    public IDisposable Measure(WindowOpenPhase phase)
    {
        Write(eventKind: 1, (int)phase, durationMs: 0);
        return new PhaseScope(this, phase, _time.GetTimestamp());
    }

    private void Write(int eventKind, int stage, double durationMs)
    {
        if (!_closed)
        {
            WindowOpenEventSource.Log.Timing(
                _id, (int)_kind, eventKind, stage,
                _time.GetElapsedTime(_start).TotalMilliseconds, durationMs);
        }
    }

    private sealed class PhaseScope(WindowOpenTrace trace, WindowOpenPhase phase, long start) : IDisposable
    {
        private WindowOpenTrace? _trace = trace;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _trace, null);
            owner?.Write(eventKind: 2, (int)phase, owner._time.GetElapsedTime(start).TotalMilliseconds);
        }
    }
}
