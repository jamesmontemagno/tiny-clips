namespace TinyClips.Core.Capture;

public sealed record RecordingPhaseTimes(
    TimeSpan Preparation,
    TimeSpan PreparedWait,
    TimeSpan ActiveRecording,
    TimeSpan Paused,
    TimeSpan Finalization,
    TimeSpan? FirstFrameLatency);

internal sealed class RecordingPhaseAccounting(TimeProvider clock)
{
    private enum Phase { Idle, Preparation, Prepared, Active, Paused, Finalizing, Complete }
    private readonly object _gate = new();
    private readonly TimeSpan[] _durations = new TimeSpan[7];
    private Phase _phase;
    private long _since;
    private long _epoch;
    private TimeSpan? _firstFrame;

    public long CadenceEpoch
    {
        get { lock (_gate) { return _phase == Phase.Active ? _epoch : 0; } }
    }

    public void BeginPreparation() => Transition(Phase.Preparation);
    public void Prepared() => Transition(Phase.Prepared);
    public void Start() => Transition(Phase.Active);
    public void Pause() => Transition(Phase.Paused);
    public void Resume() => Transition(Phase.Active);
    public void BeginFinalization() => Transition(Phase.Finalizing);

    public void FrameEmitted()
    {
        lock (_gate)
        {
            if (_phase == Phase.Active)
            {
                _firstFrame ??= _durations[(int)Phase.Active] + clock.GetElapsedTime(_since);
            }
        }
    }

    public RecordingPhaseTimes Complete()
    {
        Transition(Phase.Complete);
        lock (_gate)
        {
            return new(_durations[(int)Phase.Preparation], _durations[(int)Phase.Prepared],
                _durations[(int)Phase.Active], _durations[(int)Phase.Paused],
                _durations[(int)Phase.Finalizing], _firstFrame);
        }
    }

    private void Transition(Phase next)
    {
        lock (_gate)
        {
            if (_phase == next || _phase == Phase.Complete)
            {
                return;
            }

            var now = clock.GetTimestamp();
            if (_phase != Phase.Idle)
            {
                _durations[(int)_phase] += clock.GetElapsedTime(_since, now);
            }

            _phase = next;
            _since = now;
            _epoch++;
        }
    }
}
