using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StudioEngineSpike.Engine;

/// <summary>
/// Copied from <c>TinyClips.Core.Capture.FramePacer</c> (the spike cannot reference Core), with
/// per-tick lateness added for the encoder-load measurements. Drives a callback at a fixed
/// wall-clock cadence from a dedicated thread using a high-resolution waitable timer; ticks sit on
/// an absolute grid from the start instant, so a slow callback skips ahead instead of drifting.
/// </summary>
internal sealed partial class FramePacer : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly Action<long> _tick;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private nint _timer;
    private long _skippedTicks;

    /// <param name="tick">Receives the grid slot index (0-based, so slot × interval is the frame's PTS).</param>
    public FramePacer(TimeSpan interval, Action<long> tick, string name)
    {
        _interval = interval;
        _tick = tick;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = name,
            Priority = ThreadPriority.AboveNormal,
        };
    }

    /// <summary>Grid slots skipped because the previous tick overran.</summary>
    public long SkippedTicks => Interlocked.Read(ref _skippedTicks);

    /// <summary>How late each tick started relative to its grid slot, in milliseconds.</summary>
    public Samples Lateness { get; } = new();

    public void Start() => _thread.Start();

    private void Run()
    {
        _timer = PreciseTimer.Create();
        var intervalTicks = (long)(_interval.TotalSeconds * Stopwatch.Frequency);
        var start = Stopwatch.GetTimestamp();
        long slot = -1;

        while (!_stop.IsSet)
        {
            slot++;
            var due = start + ((slot + 1) * intervalTicks);
            var now = Stopwatch.GetTimestamp();
            if (due <= now)
            {
                // Overran: jump to the next slot that is still in the future so PTS stays wall-clock.
                var behind = ((now - due) / intervalTicks) + 1;
                Interlocked.Add(ref _skippedTicks, behind);
                slot += behind;
                due = start + ((slot + 1) * intervalTicks);
            }

            PreciseTimer.WaitUntil(_timer, due);
            if (_stop.IsSet)
            {
                break;
            }

            Lateness.Add((Stopwatch.GetTimestamp() - due) * 1000.0 / Stopwatch.Frequency);
            try
            {
                _tick(slot);
            }
            catch
            {
                // The owner handles per-frame failures; the pacer must keep running.
            }
        }

        PreciseTimer.Close(_timer);
        _timer = nint.Zero;
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join(5000);
        }

        _stop.Dispose();
    }
}

/// <summary>Sub-millisecond waits (Thread.Sleep rounds up to the 15.6 ms system tick by default).</summary>
internal static partial class PreciseTimer
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;

    [ThreadStatic]
    private static nint _threadTimer;

    public static nint Create()
    {
        var timer = CreateWaitableTimerExW(nint.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        return timer != nint.Zero ? timer : CreateWaitableTimerExW(nint.Zero, null, 0, TimerAllAccess);
    }

    public static void Close(nint timer)
    {
        if (timer != nint.Zero)
        {
            CloseHandle(timer);
        }
    }

    public static void WaitUntil(nint timer, long dueTimestamp)
    {
        var remaining = dueTimestamp - Stopwatch.GetTimestamp();
        if (remaining <= 0)
        {
            return;
        }

        if (timer != nint.Zero)
        {
            // Negative = relative, in 100 ns units.
            var hundredNs = -(remaining * 10_000_000L / Stopwatch.Frequency);
            if (SetWaitableTimer(timer, ref hundredNs, 0, nint.Zero, nint.Zero, false))
            {
                WaitForSingleObject(timer, 1000);
                return;
            }
        }

        Thread.Sleep(TimeSpan.FromTicks(remaining * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
    }

    /// <summary>Sleeps the calling thread for close to the requested time.</summary>
    public static void Sleep(double milliseconds)
    {
        if (_threadTimer == nint.Zero)
        {
            _threadTimer = Create();
        }

        WaitUntil(_threadTimer, Stopwatch.GetTimestamp() + (long)(milliseconds * Stopwatch.Frequency / 1000.0));
    }

    /// <summary>The system timer interrupt period right now, in milliseconds (15.625 unless someone asked for finer).</summary>
    public static double SystemTimerResolutionMs() =>
        NtQueryTimerResolution(out _, out _, out var current) == 0 ? current / 10_000.0 : double.NaN;

    /// <summary>Asks for a 1 ms system timer (what a media app or a browser does) until the returned scope is disposed.</summary>
    public static IDisposable RequestFineSystemTimer()
    {
        timeBeginPeriod(1);
        return new FineTimerScope();
    }

    private sealed class FineTimerScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                timeEndPeriod(1);
            }
        }
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);

    [LibraryImport("winmm.dll")]
    private static partial uint timeBeginPeriod(uint period);

    [LibraryImport("winmm.dll")]
    private static partial uint timeEndPeriod(uint period);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint desiredAccess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(nint timer, ref long dueTime, int period, nint completionRoutine, nint argToCompletionRoutine, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
