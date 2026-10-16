using System.Diagnostics;

namespace StudioEngineSpike.Engine;

/// <summary>
/// The paused-seek policy the measurements led to, in a form the real preview can lift:
///
/// 1. One seek at a time. A new position is not assigned while an earlier one is still being
///    decoded; the newest request replaces any that are waiting. Overlapping seeks are what
///    produced stale and corrupt frames in the scrub tests.
/// 2. Positions are always the middle of a frame, so tick rounding at frame boundaries cannot pick
///    a neighbour.
/// 3. A player is expected to call back only when the request moves it to a different frame
///    (a camera parked before its first frame does not call back).
/// 4. Watchdog. MediaPlayer sometimes produces no frame for a paused seek (about 1 % here) while
///    reporting the new position. In those cases <c>PlaybackSession.SeekCompleted</c> still fires,
///    and early (4–11 ms), whereas on a normal seek the frame callback starts before it. So a
///    seek is declared lost when SeekCompleted has been raised and no frame callback has started
///    <see cref="Grace"/> later, with a plain <see cref="Timeout"/> behind that.
/// 5. Repair. Assigning the same position again, or another position inside the same frame, does
///    nothing. Seeking to the neighbouring frame and back brings the frame.
/// </summary>
internal sealed class SeekCoordinator : IDisposable
{
    private const int MaxRepairs = 3;

    private readonly PreviewEngine _engine;
    private readonly int _fps;
    private readonly int[] _lagFrames;
    private readonly int[] _frameCounts;
    private readonly int[] _shown = { -1, -1 };
    private readonly Thread _worker;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _statsGate = new();
    private volatile bool _stop;
    private int _requested = -1;
    private int _settled = -1;

    /// <param name="cameraLagFrames">Timeline frame N shows camera frame N − lag.</param>
    public SeekCoordinator(PreviewEngine engine, int fps, int screenFrames, int cameraFrames, int cameraLagFrames)
    {
        _engine = engine;
        _fps = fps;
        _lagFrames = new[] { 0, cameraLagFrames };
        _frameCounts = new[] { screenFrames, cameraFrames };
        _worker = new Thread(Run) { IsBackground = true, Name = "Spike.SeekCoordinator" };
        _worker.Start();
    }

    /// <summary>Use SeekCompleted to spot a lost seek early. Off = wait for <see cref="Timeout"/> only.</summary>
    public bool UseSeekCompleted { get; set; } = true;

    /// <summary>How long after SeekCompleted a frame callback may still start (largest seen on a good seek: 2.3 ms).</summary>
    public TimeSpan Grace { get; set; } = TimeSpan.FromMilliseconds(40);

    /// <summary>Upper bound for an expected callback, whatever SeekCompleted says.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMilliseconds(500);

    public long SeeksIssued { get; private set; }

    /// <summary>Requests that needed the neighbour-and-back repair.</summary>
    public long Repairs { get; private set; }

    /// <summary>Of those, how many were spotted through SeekCompleted rather than the timeout.</summary>
    public long LossesSeenEarly { get; private set; }

    /// <summary>Requests still without their frame after <see cref="MaxRepairs"/> repairs.</summary>
    public long RepairFailures { get; private set; }

    /// <summary>Time from taking up a request to the last expected callback, repairs included.</summary>
    public Samples LatencyMs { get; } = new();

    /// <summary>The same, for the requests that needed a repair.</summary>
    public Samples RepairedLatencyMs { get; } = new();

    /// <summary>The timeline frame both players have been brought to, or -1 while a seek is in flight.</summary>
    public int SettledFrame => Volatile.Read(ref _settled);

    /// <summary>Tells the coordinator which frames the players show (after the app positioned them itself).</summary>
    public void Reset(int timelineFrame)
    {
        _shown[0] = PlayerFrame(0, timelineFrame);
        _shown[1] = PlayerFrame(1, timelineFrame);
        Volatile.Write(ref _requested, timelineFrame);
        Volatile.Write(ref _settled, timelineFrame);
    }

    /// <summary>
    /// After playback stops, each player shows whatever frame it delivered last, which need not be
    /// the pair the clock implies. Give the frame each one shows (from its PlaybackSession.Position
    /// at its last callback) and the next <see cref="Request"/> brings both to the same timeline frame.
    /// </summary>
    public void ResetAfterPlayback(int screenFrameShown, int cameraFrameShown)
    {
        _shown[0] = screenFrameShown;
        _shown[1] = cameraFrameShown;
        Volatile.Write(ref _requested, -1);
        Volatile.Write(ref _settled, -1);
    }

    /// <summary>Asks for a timeline frame. Returns immediately; the newest request wins.</summary>
    public void Request(int timelineFrame)
    {
        Volatile.Write(ref _requested, timelineFrame);
        _wake.Set();
    }

    public bool WaitUntilSettled(int timelineFrame, TimeSpan timeout)
    {
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start) < timeout)
        {
            if (SettledFrame == timelineFrame)
            {
                return true;
            }

            PreciseTimer.Sleep(0.5);
        }

        return SettledFrame == timelineFrame;
    }

    private void Run()
    {
        while (!_stop)
        {
            var target = Volatile.Read(ref _requested);
            if (target < 0 || target == Volatile.Read(ref _settled))
            {
                _wake.WaitOne(50);
                continue;
            }

            Volatile.Write(ref _settled, -1);
            var start = Stopwatch.GetTimestamp();
            var delivered = Seek(target, out var early);
            var repaired = false;
            for (var repair = 0; !delivered && repair < MaxRepairs && !_stop; repair++)
            {
                if (repair == 0)
                {
                    repaired = true;
                    lock (_statsGate)
                    {
                        Repairs++;
                        LossesSeenEarly += early ? 1 : 0;
                    }
                }

                // Any frame that is different for every player will do; the neighbour is the cheapest.
                var neighbour = target > _lagFrames[1] + 1 ? target - 1 : target + 1;
                Seek(neighbour, out _);
                delivered = Seek(target, out _);
            }

            lock (_statsGate)
            {
                if (delivered)
                {
                    var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    LatencyMs.Add(elapsed);
                    if (repaired)
                    {
                        RepairedLatencyMs.Add(elapsed);
                    }
                }
                else
                {
                    RepairFailures++;
                }
            }

            // Only report "settled" if nobody asked for something else in the meantime; otherwise
            // stay unsettled and go round again for the newer request.
            if (Volatile.Read(ref _requested) == target)
            {
                Volatile.Write(ref _settled, target);
            }
        }
    }

    /// <summary>Assigns the position and waits for every player that has to change frame.</summary>
    /// <param name="lostEarly">True when a loss was recognised from SeekCompleted, before the timeout.</param>
    private bool Seek(int timelineFrame, out bool lostEarly)
    {
        lostEarly = false;
        var tracks = _engine.Tracks;
        var count = tracks.Length;
        var expects = new bool[count];
        var done = new bool[count];
        var lost = new bool[count];
        var framesBefore = new long[count];
        var startedBefore = new long[count];
        var seeksBefore = new long[count];
        var wanted = new int[count];
        for (var index = 0; index < count; index++)
        {
            wanted[index] = PlayerFrame(index, timelineFrame);
            expects[index] = wanted[index] != _shown[index];
            framesBefore[index] = tracks[index].FrameCount;
            startedBefore[index] = tracks[index].CallbacksStarted;
            seeksBefore[index] = tracks[index].SeekCompletedCount;
        }

        lock (_statsGate)
        {
            SeeksIssued++;
        }

        var start = Stopwatch.GetTimestamp();
        _engine.Controller.Position = TimeSpan.FromTicks((long)((timelineFrame + 0.5) * TimeSpan.TicksPerSecond / _fps));

        while (!_stop)
        {
            var pending = false;
            for (var index = 0; index < count; index++)
            {
                if (!expects[index] || done[index] || lost[index])
                {
                    continue;
                }

                var track = tracks[index];
                if (track.FrameCount > framesBefore[index])
                {
                    done[index] = true;
                }
                else if (UseSeekCompleted &&
                         track.SeekCompletedCount > seeksBefore[index] &&
                         track.CallbacksStarted == startedBefore[index] &&
                         Stopwatch.GetElapsedTime(track.LatestSeekCompleted) > Grace)
                {
                    lost[index] = true;
                    lostEarly = true;
                }
                else
                {
                    pending = true;
                }
            }

            if (!pending)
            {
                break;
            }

            if (Stopwatch.GetElapsedTime(start) > Timeout)
            {
                for (var index = 0; index < count; index++)
                {
                    lost[index] |= expects[index] && !done[index];
                }

                break;
            }

            PreciseTimer.Sleep(0.5);
        }

        // Let this seek's SeekCompleted land on every player (also one that had no frame to deliver)
        // before another position is assigned, so that it cannot be taken for the next seek's.
        // It follows the frame within a few ms.
        if (UseSeekCompleted)
        {
            var settle = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(settle).TotalMilliseconds < 60 && !_stop)
            {
                var waiting = false;
                for (var index = 0; index < count; index++)
                {
                    waiting |= tracks[index].SeekCompletedCount == seeksBefore[index];
                }

                if (!waiting)
                {
                    break;
                }

                PreciseTimer.Sleep(0.5);
            }
        }

        var all = true;
        for (var index = 0; index < count; index++)
        {
            if (!expects[index] || done[index])
            {
                _shown[index] = wanted[index];
            }
            else
            {
                all = false;
            }
        }

        return all;
    }

    private int PlayerFrame(int track, int timelineFrame) => Math.Clamp(timelineFrame - _lagFrames[track], 0, _frameCounts[track] - 1);

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _worker.Join(3000);
        _wake.Dispose();
    }
}
