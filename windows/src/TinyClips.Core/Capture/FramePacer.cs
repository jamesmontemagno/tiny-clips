using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TinyClips.Core.Capture;

/// <summary>
/// Drives a callback at a fixed wall-clock cadence from a dedicated thread, using a
/// high-resolution waitable timer (sub-millisecond on Windows 10 1803+). Replaces
/// <see cref="System.Threading.Timer"/> for the GPU pump, whose ~15.6 ms default granularity
/// and thread-pool dispatch could neither hold 30/60 fps precisely nor guarantee the callback
/// runs one-at-a-time. Ticks are scheduled on an absolute grid from the start instant, so a slow
/// callback skips ahead to the next grid slot instead of accumulating drift.
/// </summary>
internal sealed partial class FramePacer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint WaitObject0 = 0;

    private readonly TimeSpan _interval;
    private readonly Action _tick;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private nint _timer;
    private long _skippedTicks;

    public FramePacer(TimeSpan interval, Action tick, string name)
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

    /// <summary>Grid slots skipped because the previous tick overran (diagnostic).</summary>
    public long SkippedTicks => Interlocked.Read(ref _skippedTicks);

    public void Start() => _thread.Start();

    private void Run()
    {
        _timer = CreateWaitableTimerExW(nint.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (_timer == nint.Zero)
        {
            // Pre-1803 or restricted: fall back to the legacy timer (1 ms resolution with timeBeginPeriod).
            _timer = CreateWaitableTimerExW(nint.Zero, null, 0, TimerAllAccess);
        }

        var intervalTicks = (long)(_interval.TotalSeconds * Stopwatch.Frequency);
        var start = Stopwatch.GetTimestamp();
        long slot = 0;

        while (!_stop.IsSet)
        {
            slot++;
            var due = start + (slot * intervalTicks);
            var now = Stopwatch.GetTimestamp();
            if (due <= now)
            {
                // Overran: jump to the next slot that is still in the future so PTS stays wall-clock.
                var behind = (now - due) / intervalTicks + 1;
                Interlocked.Add(ref _skippedTicks, behind);
                slot += behind;
                due = start + (slot * intervalTicks);
            }

            WaitUntil(due);
            if (_stop.IsSet)
            {
                break;
            }

            try
            {
                _tick();
            }
            catch
            {
                // The owner handles per-frame failures; the pacer must keep running.
            }
        }

        if (_timer != nint.Zero)
        {
            CloseHandle(_timer);
            _timer = nint.Zero;
        }
    }

    private void WaitUntil(long dueTimestamp)
    {
        var remaining = dueTimestamp - Stopwatch.GetTimestamp();
        if (remaining <= 0)
        {
            return;
        }

        if (_timer != nint.Zero)
        {
            // Negative = relative, in 100 ns units.
            var hundredNs = -(remaining * 10_000_000L / Stopwatch.Frequency);
            if (SetWaitableTimer(_timer, ref hundredNs, 0, nint.Zero, nint.Zero, false))
            {
                WaitForSingleObject(_timer, 1000);
                return;
            }
        }

        Thread.Sleep(TimeSpan.FromTicks(remaining * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join(2000);
        }

        _stop.Dispose();
    }

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
