using System.Diagnostics;

namespace TinyClips.Core.Capture;

/// <summary>
/// A shared recording clock expressed in the system-relative QPC time domain used by
/// MediaFrameReference and WASAPI capture timestamps.
/// </summary>
internal sealed class RecordingTimeline
{
    private readonly object _gate = new();
    private TimeSpan _pausedDuration;
    private TimeSpan? _pauseStartedAt;
    private readonly List<(TimeSpan StartedAt, TimeSpan EndedAt)> _completedPauses = [];
    private int _pauseCount;

    private RecordingTimeline(TimeSpan origin)
    {
        Origin = origin;
    }

    public TimeSpan Origin { get; }

    public TimeSpan Elapsed => Normalize(GetSystemRelativeTime());

    public static RecordingTimeline StartNow() => new(GetSystemRelativeTime());

    internal static RecordingTimeline FromOrigin(TimeSpan origin) => new(origin);

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _pauseStartedAt is not null;
            }
        }
    }

    /// <summary>Number of completed or in-progress pauses.</summary>
    public int PauseCount
    {
        get
        {
            lock (_gate)
            {
                return _pauseCount;
            }
        }
    }

    /// <summary>Total time excluded from the timeline so far (including an in-progress pause).</summary>
    public TimeSpan PausedDuration
    {
        get
        {
            lock (_gate)
            {
                var paused = _pausedDuration;
                if (_pauseStartedAt is { } pausedAt)
                {
                    paused += GetSystemRelativeTime() - pausedAt;
                }

                return paused;
            }
        }
    }

    public void Pause() => Pause(GetSystemRelativeTime());

    public void Resume() => Resume(GetSystemRelativeTime());

    /// <summary>Pauses at an explicit system-relative time. Tests use this to avoid sleeping.</summary>
    internal void Pause(TimeSpan now)
    {
        lock (_gate)
        {
            if (_pauseStartedAt is null)
            {
                _pauseStartedAt = now;
                _pauseCount++;
            }
        }
    }

    /// <summary>Resumes at an explicit system-relative time. Tests use this to avoid sleeping.</summary>
    internal void Resume(TimeSpan now)
    {
        lock (_gate)
        {
            if (_pauseStartedAt is { } pausedAt)
            {
                var endedAt = now > pausedAt ? now : pausedAt;
                _completedPauses.Add((pausedAt, endedAt));
                _pausedDuration += endedAt - pausedAt;
                _pauseStartedAt = null;
            }
        }
    }

    public TimeSpan Normalize(TimeSpan sourceTimestamp)
    {
        lock (_gate)
        {
            var paused = _pausedDuration;
            if (_pauseStartedAt is { } pausedAt)
            {
                paused += sourceTimestamp > pausedAt ? sourceTimestamp - pausedAt : TimeSpan.Zero;
            }

            return sourceTimestamp - Origin - paused;
        }
    }

    /// <summary>
    /// Maps a timestamp from any point of the recording onto the timeline. Unlike
    /// <see cref="Normalize"/>, which assumes the timestamp is current, this subtracts only the
    /// pauses that ended before it, so it is correct for events converted after the fact. Returns
    /// false for a timestamp before the origin or inside a pause, where nothing was recorded.
    /// </summary>
    public bool TryNormalizeActive(TimeSpan sourceTimestamp, out TimeSpan normalized)
    {
        normalized = TimeSpan.Zero;
        lock (_gate)
        {
            if (sourceTimestamp < Origin)
            {
                return false;
            }

            var pausedBefore = TimeSpan.Zero;
            foreach (var pause in _completedPauses)
            {
                if (sourceTimestamp < pause.StartedAt)
                {
                    break;
                }

                if (sourceTimestamp < pause.EndedAt)
                {
                    return false;
                }

                pausedBefore += pause.EndedAt - pause.StartedAt;
            }

            if (_pauseStartedAt is { } pausedAt && sourceTimestamp >= pausedAt)
            {
                return false;
            }

            normalized = sourceTimestamp - Origin - pausedBefore;
            return true;
        }
    }

    private static TimeSpan GetSystemRelativeTime() =>
        Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp());

    internal static TimeSpan SystemRelativeNow() => GetSystemRelativeTime();
}
