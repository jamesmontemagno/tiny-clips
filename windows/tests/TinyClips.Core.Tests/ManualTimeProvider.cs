namespace TinyClips.Core.Tests;

/// <summary>
/// Deterministic <see cref="TimeProvider"/> with manually advanced time and timers that fire
/// synchronously from <see cref="Advance"/>. Shared by the Clips Library tests.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? now = null)
    {
        _now = now ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan elapsed)
    {
        var target = _now + elapsed;
        while (true)
        {
            ManualTimer? next;
            lock (_timers)
            {
                next = _timers.Where(t => t.DueAt is not null && t.DueAt <= target).MinBy(t => t.DueAt);
            }

            if (next is null)
            {
                break;
            }

            _now = next.DueAt!.Value;
            next.Fire();
        }

        _now = target;
    }

    private void Remove(ManualTimer timer)
    {
        lock (_timers)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }

        public void Fire()
        {
            DueAt = _period == Timeout.InfiniteTimeSpan ? null : owner._now + _period;
            callback(state);
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
