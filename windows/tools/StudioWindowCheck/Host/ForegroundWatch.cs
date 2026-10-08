using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace TinyClips.Tools.StudioWindowCheck.Host;

/// <summary>
/// Watches which window is in front for as long as the tool runs, so the report can say whether a
/// window of the tool ever was. <see cref="ForegroundGuard"/> is there to make that impossible.
/// Should it happen all the same, the window is taken off the screen at once, which gives the
/// foreground back, and the run is stopped.
/// </summary>
/// <remarks>
/// It watches in two ways, each on a thread of its own. Windows is asked to report every change
/// of the foreground window, which leaves none out however short it is. And the foreground window
/// is read about once a millisecond, which is what notices a window of the tool soonest. Both
/// only listen and read: nothing is sent to any window but the tool's own, and no system setting
/// is changed (the timer is a high-resolution timer of this process).
/// </remarks>
internal sealed unsafe partial class ForegroundWatch : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WmQuit = 0x0012;
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;

    // One millisecond, in the 100 ns units a waitable timer counts in. Negative means from now.
    private const long SampleInterval = -10_000;

    private static ForegroundWatch? _current;

    private readonly Thread _sampler;
    private readonly Thread _listener;
    private readonly ManualResetEventSlim _listening = new(false);
    private readonly object _gate = new();
    private readonly List<string> _seen = [];
    private readonly List<string> _own = [];
    private readonly long _started = Stopwatch.GetTimestamp();
    private volatile bool _stop;
    private volatile bool _stoppedTheRun;
    private uint _listenerThread;
    private bool _isListening;
    private nint _last = -1;
    private long _samples;
    private long _longestGap;
    private string _longestGapDuring = string.Empty;
    private long _ended;
    private int _changes;

    public ForegroundWatch()
    {
        _current = this;
        _listener = new Thread(Listen) { IsBackground = true, Name = "StudioWindowCheck.ForegroundEvents" };
        _listener.Start();
        _listening.Wait(TimeSpan.FromSeconds(5));
        // Above the checks' own threads, so that work on all processors does not keep it waiting.
        _sampler = new Thread(Sample) { IsBackground = true, Name = "StudioWindowCheck.Foreground", Priority = ThreadPriority.Highest };
        _sampler.Start();
    }

    /// <summary>True once a window of the tool was in front. The checks stop when they see it.</summary>
    public bool StoppedTheRun => _stoppedTheRun;

    /// <summary>Each window that was in front, in order, as "title (process id)".</summary>
    public string[] Seen()
    {
        lock (_gate)
        {
            return [.. _seen];
        }
    }

    /// <summary>The moments a window of this process was in front. Has to stay empty.</summary>
    public string[] OwnWindowsInFront()
    {
        lock (_gate)
        {
            return [.. _own];
        }
    }

    /// <summary>How closely the foreground window was watched, in words, with the numbers measured.</summary>
    public string Describe()
    {
        var samples = Interlocked.Read(ref _samples);
        var end = Interlocked.Read(ref _ended) is var ended and not 0 ? ended : Stopwatch.GetTimestamp();
        var seconds = Stopwatch.GetElapsedTime(_started, end).TotalSeconds;
        var mean = samples == 0 ? 0 : seconds * 1000 / samples;
        var longest = Interlocked.Read(ref _longestGap) * 1000.0 / Stopwatch.Frequency;
        var reported = _isListening
            ? $"Windows reported {Volatile.Read(ref _changes)} change(s) of the foreground window during the run"
            : "Windows could not be asked to report the changes of the foreground window";
        return string.Create(CultureInfo.InvariantCulture, $"{reported}; the foreground window was also read {samples} times in {seconds:0.0} s, on average {mean:0.00} ms apart, the longest gap {longest:0.0} ms ({Volatile.Read(ref _longestGapDuring)})");
    }

    public void Dispose()
    {
        _stop = true;
        _sampler.Join();
        Interlocked.Exchange(ref _ended, Stopwatch.GetTimestamp());
        while (!_listener.Join(20))
        {
            PostThreadMessage(_listenerThread, WmQuit, 0, 0);
        }

        _current = null;
        _listening.Dispose();
    }

    /// <summary>One look at the foreground window, from either thread.</summary>
    private void Saw(nint window, string how)
    {
        var self = (uint)Environment.ProcessId;
        var process = Native.ProcessOf(window);
        if (process == self)
        {
            // Before anything else: off the screen, so the window that was in front is again.
            Native.Hide(window);
            _stoppedTheRun = true;
        }

        lock (_gate)
        {
            if (window == _last)
            {
                return;
            }

            _last = window;
            var text = $"\"{Native.TitleOf(window)}\" (process {process}) from {ForegroundGuard.Elapsed:0.00} s";
            if (_seen.Count < 40)
            {
                _seen.Add(text);
            }

            if (process == self)
            {
                _own.Add($"{text}, {how}, class {Native.ClassOf(window)}, during: {ForegroundGuard.Step}");
            }
        }
    }

    private void Sample()
    {
        var timer = CreateWaitableTimerEx(0, 0, CreateWaitableTimerHighResolution, TimerAllAccess);
        var previous = Stopwatch.GetTimestamp();
        var paused = GC.GetTotalPauseDuration();
        try
        {
            while (!_stop)
            {
                Saw(Native.Foreground(), "read");
                Interlocked.Increment(ref _samples);
                var now = Stopwatch.GetTimestamp();
                var pausedNow = GC.GetTotalPauseDuration();
                if (now - previous > Interlocked.Read(ref _longestGap))
                {
                    // A garbage collection stops every thread of the process, this one too.
                    Interlocked.Exchange(ref _longestGap, now - previous);
                    Volatile.Write(ref _longestGapDuring, string.Create(CultureInfo.InvariantCulture, $"at {ForegroundGuard.Elapsed:0.0} s, during: {ForegroundGuard.Step}; {(pausedNow - paused).TotalMilliseconds:0.0} ms of it a garbage collection"));
                }

                previous = now;
                paused = pausedNow;
                if (timer == 0 || !SetWaitableTimer(timer, SampleInterval, 0, 0, 0, false))
                {
                    Thread.Sleep(1);
                }
                else
                {
                    WaitForSingleObject(timer, 100);
                }
            }
        }
        finally
        {
            if (timer != 0)
            {
                CloseHandle(timer);
            }
        }
    }

    // Windows delivers the reports as messages to the thread that asked for them.
    private void Listen()
    {
        _listenerThread = GetCurrentThreadId();
        var hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, &OnForegroundChanged, 0, 0, WineventOutOfContext);
        _isListening = hook != 0;
        _listening.Set();
        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            DispatchMessage(message);
        }

        if (hook != 0)
        {
            UnhookWinEvent(hook);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnForegroundChanged(nint hook, uint @event, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (_current is { } watch && window != 0)
        {
            Interlocked.Increment(ref watch._changes);
            watch.Saw(window, "reported by Windows");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint module, delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback, uint process, uint thread, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out Message message, nint window, uint minimum, uint maximum);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(in Message message);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint thread, uint message, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW")]
    private static partial nint CreateWaitableTimerEx(nint attributes, nint name, uint flags, uint access);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(nint timer, in long dueTime, int period, nint completion, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
