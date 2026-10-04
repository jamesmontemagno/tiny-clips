using System.Diagnostics;

namespace TinyClips.Core.Studio.Preview;

/// <summary>What the seek policy asks of the players. The engine implements it over a <c>MediaTimelineController</c>.</summary>
internal interface IStudioPreviewTransport
{
    /// <summary>Puts the shared clock in the middle of a timeline frame. Every player on the clock seeks.</summary>
    void Seek(long timelineFrame);

    /// <summary>Moves one track's player forward by exactly one frame. The clock does not move.</summary>
    void StepForward(int track);

    /// <summary>Starts the clock. False when playing is no longer wanted and the clock was left stopped.</summary>
    bool Resume();

    /// <summary>Stops the clock. Harmless when it is already stopped.</summary>
    void Pause();

    /// <summary>
    /// A track's player is handing a frame over at this moment; its report is about to follow.
    /// On a busy machine that was measured to take a tenth of a second.
    /// </summary>
    bool IsDelivering(int track);
}

internal enum StudioPreviewLandingKind
{
    /// <summary>A requested position, reached with a seek.</summary>
    Seek,

    /// <summary>A requested position one frame ahead, reached by stepping the players.</summary>
    Step,

    /// <summary>The players brought onto one frame after the clock stopped.</summary>
    Snap,
}

/// <summary>The players have come to rest on a timeline frame.</summary>
/// <param name="Confirmed">False when a player never delivered its frame, even after the repairs.</param>
/// <param name="RequestedAt">Clock reading when the position was asked for.</param>
/// <param name="LandedAt">Clock reading when it was reached.</param>
/// <param name="Repairs">Detours taken on the way.</param>
internal readonly record struct StudioPreviewLanding(long Frame, bool Confirmed, StudioPreviewLandingKind Kind, long RequestedAt, long LandedAt, int Repairs);

/// <summary>Time limits of the seek policy. The defaults come from measurements of the players.</summary>
internal sealed record StudioPreviewSeekSettings
{
    /// <summary>Clock ticks per second; <see cref="Stopwatch.Frequency"/> unless a test supplies its own clock.</summary>
    public long TicksPerSecond { get; init; } = Stopwatch.Frequency;

    /// <summary>How long after <c>SeekCompleted</c> a frame may still arrive. Past that the seek is lost.</summary>
    public double GraceMilliseconds { get; init; } = 40;

    /// <summary>
    /// Upper bound for a frame of which nothing is heard at all, neither the frame nor
    /// <c>SeekCompleted</c>. Generous: on a machine that is busy a seek was measured to take over
    /// half a second, and calling it lost only puts more seeks in front of a player that is slow.
    /// </summary>
    public double TimeoutMilliseconds { get; init; } = 2000;

    /// <summary>
    /// How long, from the moment the clock was moved, the players that owe no new picture are given
    /// to answer anyway. A player redraws its frame after every position change that moves it
    /// (measured: 40 to 55 ms on average, 145 ms at most, later on a busy machine), and the next
    /// change must not be made before that answer is in. It is also how long a player at the end
    /// of its stream is given for the frame of the position that does not count.
    /// </summary>
    public double AnswerTimeoutMilliseconds { get; init; } = 400;

    /// <summary>
    /// How long a frame that was on its way when the clock stopped is given to arrive. A frame
    /// that is being handed over when the time is up is waited for, up to
    /// <see cref="PauseSettleLimitMilliseconds"/>.
    /// </summary>
    public double PauseSettleMilliseconds { get; init; } = 40;

    /// <summary>The longest a frame that is being handed over when the clock stops is waited for.</summary>
    public double PauseSettleLimitMilliseconds { get; init; } = 500;

    /// <summary>How long a stepped player is given for its next frame before the step is done again as a seek.</summary>
    public double StepTimeoutMilliseconds { get; init; } = 250;

    /// <summary>
    /// How long after a step the clock is left where it was, behind the players. A further step
    /// within that time starts at once; bringing the clock along first would take as long as a seek.
    /// </summary>
    public double StepSyncDelayMilliseconds { get; init; } = 150;

    /// <summary>Detours tried for one position before it is reported as not confirmed.</summary>
    public int MaxRepairs { get; init; } = 3;

    /// <summary>Reach a position one frame ahead by stepping the players instead of seeking.</summary>
    public bool StepForward { get; init; } = true;

    /// <summary>
    /// Let the picture be drawn each time a clip's frame arrives on the way to a paused position,
    /// as during playback. Off, the picture changes once, when every clip is on the new frame;
    /// on, most seeks show one clip's new frame over the other's old one for a moment (measured:
    /// two seeks in three, for 10 to 20 ms, and for as long as the repair takes when a frame is lost).
    /// </summary>
    public bool CompositeDuringSeeks { get; init; }

    internal long Ticks(double milliseconds) => (long)Math.Round(milliseconds * TicksPerSecond / 1000.0);
}

/// <summary>
/// Decides when the shared clock is moved and started. It exists because of what a paused
/// <c>MediaPlayer</c> does with position changes:
/// <list type="bullet">
/// <item>Which frame a texture holds is only known from the position the player reports when it
/// hands the frame over. A frame that arrives after a newer change was made usually still reports
/// the position it was made for, but not always: after the end of a stream a second frame was
/// seen to come with the new position and the old picture. And a frame handed over while a newer
/// change is already flushing the player comes out black.</item>
/// <item>A change sometimes produces no frame at all, and on a busy machine a frame can take half
/// a second.</item>
/// </list>
/// The rules that follow from that:
/// <list type="number">
/// <item>One position change is in flight at a time. The newest request replaces any that wait.</item>
/// <item>The position is reported as soon as every player that owes a new picture has delivered
/// it, and the picture is held until then, so it changes once and with every clip on the new
/// frame. The change stays in flight until the other players have answered as well, or have shown
/// that they will not.</item>
/// <item>A player owes a picture when the change moves it to another frame and its clip is in the
/// picture there. A camera outside its own time range is not drawn and is not waited for.</item>
/// <item>A frame answers a position change when the player reports the position the change gave
/// it. A frame that reports another position belongs to an earlier change; on a busy machine it
/// can arrive after the next change was made. It is not taken for an answer, but it has taken the
/// place of its clip's picture: on the way to a position the clip then owes its frame again, and
/// once the position was reported the picture is held and the frame is gone to once more.</item>
/// <item>A frame is lost when <c>SeekCompleted</c> has been raised and no frame followed within the
/// grace period, or when nothing came before the timeout. A frame that is being handed over at
/// that moment is not lost, however long the handover takes. A lost frame is fetched by a detour
/// to another frame and back, with the picture held meanwhile. Asking for the same position again
/// does nothing.</item>
/// <item>One frame forward is done by stepping the players, which is several times faster than a
/// seek. A step does not move the clock, so the clock is brought along afterwards: once no further
/// step has followed for a moment, and in any case before playing, or playback would stall until
/// the clock had caught up.</item>
/// <item>A stepped player draws its frame again when the clock is brought along, usually with
/// its <c>SeekCompleted</c> and sometimes a tenth of a second after it (measured: one time in
/// forty). A step made in between takes that frame for its own, and the picture stays on the
/// frame before for a quarter of a second, until the clock is brought along again. So while a
/// stepped player still owes that frame, the next frame is reached with a seek, whose answer is
/// known by its position.</item>
/// <item>When the clock stops, the players are brought onto the frame the screen shows, because
/// they can come to rest a frame apart.</item>
/// <item>A player that was playing can have its next frame ready when the clock stops, without
/// having handed it over. It then hands that frame over as its first answer to the next position
/// change, with the new position on it, and the frame of that position after it. Measured: a
/// player answered twice in about one pause in 150, and without this rule the picture showed the
/// next frame for a twentieth of a second and went back in about one pause in 200. So the first
/// position change after the clock ran is reported, and the picture let go, only when the players
/// have answered it and then said nothing more for the grace period.</item>
/// <item>Playing waits for a requested position to be reached, so playback starts on that frame.
/// A position requested while playing stops the clock, is reached the same way, and playing goes on.</item>
/// <item>A player that has read its stream to the end, by playing into it or by being put on its
/// last frame or after it, does not do its next seek properly: the frame comes out one late, or
/// playing on from it stops after two frames, or no frame comes at all. A player whose offset on
/// the clock was just changed loses its next position altogether. Either is given a position
/// that does not count first, with the picture held. What the player makes of that position is
/// waited for as any answer is, not as a frame that is owed: its <c>SeekCompleted</c> comes at
/// once and says nothing about the frame.</item>
/// </list>
/// Not thread-safe: the owner calls every member from one thread, or under one lock. Which frame
/// each player shows comes only from the frames it delivered, never from what was asked of it.
/// </summary>
internal sealed class StudioPreviewSeekPolicy
{
    private enum Operation
    {
        None,
        Seek,
        Detour,
        Spend,
        Return,
        Sync,
        Step,
    }

    // How far the detour goes, by attempt. The neighbouring frame is enough when every frame of the
    // file is distinct; the longer ones are for recordings where one picture covers several frames.
    private static readonly int[] DetourDistances = [1, 8, 30];
    private const int DetourSearchSpan = 90;

    // The detour for a player that has read its stream to the end: far enough that the frame it
    // lands on, one late, is not the frame wanted afterwards.
    private const int EndedDetourAttempt = 1;

    private readonly IStudioPreviewTransport _transport;
    private readonly StudioPreviewTimeline _timeline;
    private readonly StudioPreviewSeekSettings _settings;
    private readonly Func<long> _clock;
    private readonly int _trackCount;
    private readonly long[] _shown;
    private readonly bool[] _needs;
    private readonly bool[] _expects;
    private readonly bool[] _arrived;
    private readonly bool[] _completed;
    private readonly long[] _completedAt;
    private readonly long[] _stepFrom;
    private readonly bool[] _stepped;
    private readonly bool[] _ended;
    private readonly bool[] _completesEarly;
    private readonly bool[] _caughtUpWith;
    private readonly bool[] _redrawOwed;
    private readonly List<StudioPreviewLanding> _landings = [];

    // The position change in flight.
    private Operation _operation;
    private long _operationFrame;
    private long _operationStartedAt;
    private long _lastAnswerAt;
    private bool _operationSettles;
    private bool _landsWhenQuiet;
    private bool _answering;
    private long _resolvedAt;
    private bool _resolvedDelivered;
    private bool _resolvedLostEarly;

    // The position being reached, possibly over several changes.
    private long _target = -1;
    private StudioPreviewLandingKind _chainKind;
    private long _chainRequestedAt;
    private int _repairs;

    private long _requested = -1;
    private long _requestedAt;
    private bool _requestIsSnap;
    private bool _spendFirst;

    private long _settled = -1;
    private bool _settledConfirmed;

    // Where the clock is, in timeline seconds, and whether that is exactly the middle of a frame.
    private double _clockSeconds;
    private bool _clockExact;
    private bool _clockBehind;
    private long _syncAt;

    private bool _wantPlaying;
    private bool _playing;
    private bool _settling;
    private long _settleDeadline;
    private long _settleLimit;

    // The clock ran, and no position change has been answered and gone quiet since.
    private bool _clockRan;
    private bool _stopped;

    public StudioPreviewSeekPolicy(
        IStudioPreviewTransport transport,
        StudioPreviewTimeline timeline,
        StudioPreviewSeekSettings? settings = null,
        Func<long>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(timeline);
        _transport = transport;
        _timeline = timeline;
        _settings = settings ?? new StudioPreviewSeekSettings();
        _clock = clock ?? Stopwatch.GetTimestamp;
        _trackCount = timeline.TrackCount;
        _shown = new long[_trackCount];
        Array.Fill(_shown, -1);
        _needs = new bool[_trackCount];
        _expects = new bool[_trackCount];
        _arrived = new bool[_trackCount];
        _completed = new bool[_trackCount];
        _completedAt = new long[_trackCount];
        _stepFrom = new long[_trackCount];
        _stepped = new bool[_trackCount];
        _ended = new bool[_trackCount];
        _completesEarly = new bool[_trackCount];
        _caughtUpWith = new bool[_trackCount];
        _redrawOwed = new bool[_trackCount];
    }

    /// <summary>The clock is running.</summary>
    public bool IsPlaying => _playing;

    /// <summary>
    /// True while a frame that arrives belongs to playback: the clock is running, or it stopped so
    /// recently that a frame may still be on its way.
    /// </summary>
    public bool AcceptsPlaybackFrames => _playing || _settling;

    /// <summary>Nothing is in flight or waiting, and the clock is where the players are.</summary>
    public bool IsIdle => _operation == Operation.None && _requested < 0 && !_settling && !_clockBehind && (_playing || !_wantPlaying);

    /// <summary>
    /// A position is still to be reached: one is requested and not started on, or the players are
    /// on their way to one. False from the moment the position is reported with a landing, or was
    /// found to be the frame the players rest on already, even while the players' last answers
    /// are waited for and while the clock is brought along after a step.
    /// </summary>
    public bool IsSeeking => _requested >= 0 || _target >= 0;

    /// <summary>
    /// True while the textures do not hold one picture: on the way to a paused position, when one
    /// clip may be on the new frame and the other not yet, and during a detour, when they show
    /// frames nobody asked for. The owner does not draw until it is false again.
    /// </summary>
    public bool HoldPicture { get; private set; }

    /// <summary>The timeline frame every player is known to show, or -1 while that is not known.</summary>
    public long SettledFrame => _settled;

    /// <summary>Times the clock was given a position.</summary>
    public long SeeksIssued { get; private set; }

    public long StepsIssued { get; private set; }

    /// <summary>Positions that needed at least one detour because a frame was lost.</summary>
    public long Repairs { get; private set; }

    /// <summary>Of those, how many were recognised from <c>SeekCompleted</c> rather than the timeout.</summary>
    public long LossesSeenEarly { get; private set; }

    /// <summary>Positions still without their frame after every detour.</summary>
    public long RepairFailures { get; private set; }

    /// <summary>Steps that delivered nothing and were done again as a seek.</summary>
    public long StepFallbacks { get; private set; }

    /// <summary>
    /// Frames one ahead that were reached with a seek, because a stepped player had not yet drawn
    /// its frame again after the clock was brought along.
    /// </summary>
    public long StepsAvoided { get; private set; }

    /// <summary>Detours made because a player had read its stream to the end.</summary>
    public long EndRecoveries { get; private set; }

    /// <summary>Frames that arrived during a position change and reported another position: late answers to an earlier one.</summary>
    public long StrayFrames { get; private set; }

    /// <summary>Position changes that were given up waiting for a player's answer, at the limit.</summary>
    public long AnswersGivenUp { get; private set; }

    /// <summary>
    /// Positions that do not count for which a player handed over no frame. The way on was taken
    /// all the same: that position was only there to be spent.
    /// </summary>
    public long DetoursUnanswered { get; private set; }

    /// <summary>
    /// Times a player handed over a second frame for the first position change after the clock
    /// ran. The first of two can be the frame it had ready for playback, with the new position
    /// on it.
    /// </summary>
    public long SecondAnswers { get; private set; }

    /// <summary>The frame a track's player delivered last, or -1 before its first.</summary>
    public long ShownFrame(int track) => _shown[track];

    /// <summary>
    /// A track's texture no longer holds its frame (it was recreated and could not be refilled).
    /// The next request makes the player deliver again, even one for the frame it rests on.
    /// </summary>
    public void Forget(int track)
    {
        _shown[track] = -1;
        _settledConfirmed = false;
    }

    /// <summary>Asks for a timeline frame. Nothing moves until <see cref="Pump"/>.</summary>
    public void RequestSeek(long timelineFrame)
    {
        if (_stopped)
        {
            return;
        }

        _requested = StudioPreviewTimeMath.ClampFrame(timelineFrame, _timeline.FrameCount);
        _requestedAt = _clock();
        _requestIsSnap = false;
    }

    /// <summary>
    /// Asks for a timeline frame after the clips' offsets on the clock were set. The first
    /// position a player is given after its offset changed is lost on it, so one is spent on
    /// another frame before this one is gone to.
    /// </summary>
    public void RequestSeekAfterOffsetChange(long timelineFrame)
    {
        RequestSeek(timelineFrame);
        _spendFirst = !_stopped;
    }

    /// <summary>
    /// Says whether playing is wanted. Stopping stops the clock at once; starting waits for
    /// <see cref="Pump"/>, and there for any requested position.
    /// </summary>
    public void SetPlaying(bool playing)
    {
        if (_stopped)
        {
            return;
        }

        _wantPlaying = playing;
        if (!playing && _playing)
        {
            _transport.Pause();
            ClockStopped();
        }
    }

    /// <summary>
    /// The clock reached the end of the recording and stopped by itself. True when the players
    /// are now brought to rest on the last frame; false when a requested position is gone to
    /// instead, or the clock was not running.
    /// </summary>
    public bool OnEnded()
    {
        if (_stopped || !_playing)
        {
            return false;
        }

        _wantPlaying = false;

        // The screen's stream ends where the recording does.
        _ended[0] = true;
        ClockStopped();
        if (_requested >= 0)
        {
            return false;
        }

        // Rest on the last frame of the recording, whichever frame the players got to.
        _requested = _timeline.LastFrame;
        _requestedAt = _clock();
        _requestIsSnap = true;
        return true;
    }

    /// <summary>A player reached the end of its own stream while the clock was running.</summary>
    public void OnPlayerEnded(int track)
    {
        if (!_stopped)
        {
            _ended[track] = true;
        }
    }

    /// <summary>A player delivered a frame.</summary>
    /// <param name="playerFrame">The frame of the clip that contains the position the player reported.</param>
    /// <param name="startedAt">Clock reading when the delivery started.</param>
    public void OnFrame(int track, long playerFrame, long startedAt)
    {
        if (_stopped)
        {
            return;
        }

        _redrawOwed[track] = false;
        var answersOperation = _operation != Operation.None && startedAt >= _operationStartedAt;
        if (answersOperation && _operation == Operation.Step && _needs[track] && playerFrame - _stepFrom[track] is 0 or 1)
        {
            // A step shows the next frame by definition. The position reported for it is the one
            // the player had before, on or next to a frame boundary where rounding decides.
            _shown[track] = _stepFrom[track] + 1;
            _arrived[track] = true;
            return;
        }

        _shown[track] = playerFrame;
        if (answersOperation)
        {
            _lastAnswerAt = Math.Max(_lastAnswerAt, startedAt);
        }

        // The answer to a change is the frame at the position that change gave the player.
        var target = _operation != Operation.None ? _operationFrame : _settled;
        if (target < 0 || playerFrame == _timeline.PlayerFrame(track, target))
        {
            if (answersOperation && _landsWhenQuiet && _arrived[track])
            {
                SecondAnswers++;
            }

            _arrived[track] |= answersOperation && _operation != Operation.Step;
            return;
        }

        // Anything else is a late answer to an earlier change, and the texture now holds its
        // picture instead of the one that belongs there.
        StrayFrames++;
        if (Wanted(track, target) < 0)
        {
            return;
        }

        switch (_operation)
        {
            case Operation.Seek or Operation.Return when !_answering:
                // Not there yet: the clip owes its frame again.
                _needs[track] = true;
                _arrived[track] = false;
                break;

            case Operation.Seek or Operation.Return:
                // Reported as reached already. Not to be drawn; put right when the change is over.
                HoldPicture = true;
                break;

            case Operation.None when !_playing && !_settling && !_clockBehind:
                // At rest. Go to the frame again, which is by way of another one.
                HoldPicture = true;
                _settledConfirmed = false;
                if (_requested < 0)
                {
                    _requested = _settled;
                    _requestedAt = _clock();
                    _requestIsSnap = false;
                }

                break;
        }
    }

    /// <summary>A player raised <c>SeekCompleted</c>.</summary>
    public void OnSeekCompleted(int track, long at)
    {
        if (_stopped || _operation == Operation.None || at < _operationStartedAt)
        {
            return;
        }

        _lastAnswerAt = Math.Max(_lastAnswerAt, at);
        if (!_completed[track])
        {
            _completed[track] = true;
            _completedAt[track] = at;
        }
    }

    /// <summary>Moves every landing since the last call into <paramref name="destination"/>.</summary>
    public void TakeLandings(List<StudioPreviewLanding> destination)
    {
        destination.AddRange(_landings);
        _landings.Clear();
    }

    /// <summary>After this nothing is asked of the transport any more.</summary>
    public void Stop()
    {
        _stopped = true;
        _operation = Operation.None;
        _answering = false;
        _landsWhenQuiet = false;
        _requested = -1;
        _target = -1;
        _settling = false;
        _clockBehind = false;
        _clockRan = false;
        HoldPicture = false;
    }

    /// <summary>
    /// Does whatever can be done now. Returns the clock reading at which it wants to be called
    /// again even if nothing happens before, or <see cref="long.MaxValue"/>.
    /// </summary>
    public long Pump()
    {
        while (!_stopped)
        {
            var now = _clock();
            if (_operation != Operation.None)
            {
                if (!TryFinishOperation(now, out var deadline))
                {
                    return deadline;
                }

                continue;
            }

            if (_playing)
            {
                if (_requested < 0)
                {
                    return long.MaxValue;
                }

                // A position asked for while playing: stop the clock and go there the paused way,
                // where every frame is accounted for. Playing is still wanted, so it goes on from there.
                _transport.Pause();
                ClockStopped();
                continue;
            }

            if (_settling)
            {
                if (now < _settleDeadline)
                {
                    return _settleDeadline;
                }

                // A frame that is being handed over right now set out before the clock stopped.
                // Its report wakes the owner; until then, look again shortly.
                if (now < _settleLimit && AnyIsDelivering())
                {
                    return Math.Min(_settleLimit, now + _settings.Ticks(2));
                }

                _settling = false;
                if (_requested < 0)
                {
                    // The frame the screen shows, from the last frame it delivered.
                    _requested = StudioPreviewTimeMath.ClampFrame(_shown[0], _timeline.FrameCount);
                    _requestedAt = now;
                    _requestIsSnap = true;
                }
            }

            if (_requested >= 0)
            {
                StartChain();
                continue;
            }

            if (_clockBehind)
            {
                if (_settled < 0)
                {
                    _clockBehind = false;
                    continue;
                }

                // Stepped, and the clock is still on the frame before. Give a further step a moment
                // to arrive; playing cannot wait.
                if (!_wantPlaying && now < _syncAt)
                {
                    return _syncAt;
                }

                StartAssignment(Operation.Sync, _settled);
                continue;
            }

            if (!_wantPlaying)
            {
                return long.MaxValue;
            }

            if (_transport.Resume())
            {
                _playing = true;
                _clockExact = false;
                _settled = -1;
                _settledConfirmed = false;
                HoldPicture = false;
            }
            else
            {
                _wantPlaying = false;
            }
        }

        return long.MaxValue;
    }

    private void ClockStopped()
    {
        _playing = false;
        _clockRan = true;

        // Somewhere inside the frame the screen shows.
        _clockSeconds = _timeline.FrameMiddle(StudioPreviewTimeMath.ClampFrame(_shown[0], _timeline.FrameCount));
        _clockExact = false;
        _settling = true;
        var now = _clock();
        _settleDeadline = now + _settings.Ticks(_settings.PauseSettleMilliseconds);
        _settleLimit = now + _settings.Ticks(Math.Max(_settings.PauseSettleMilliseconds, _settings.PauseSettleLimitMilliseconds));
    }

    private void StartChain()
    {
        var target = _requested;
        var snap = _requestIsSnap;
        var requestedAt = _requestedAt;
        var spendFirst = _spendFirst;
        _requested = -1;
        _requestIsSnap = false;
        _spendFirst = false;

        var from = _settledConfirmed ? _settled : -1;
        if (!snap && target == from && !spendFirst)
        {
            // Already there. A clock left behind by a step is brought along by the pump.
            return;
        }

        _target = target;
        _chainKind = snap ? StudioPreviewLandingKind.Snap : StudioPreviewLandingKind.Seek;
        _chainRequestedAt = requestedAt;
        _repairs = 0;
        _settled = -1;
        _settledConfirmed = false;

        if (spendFirst && TryPickDetour(target, EndedDetourAttempt, out var away))
        {
            HoldPicture = true;
            StartAssignment(Operation.Spend, away);
            return;
        }

        if (AnyEndedPlayerIn(target) && TryPickDetour(target, EndedDetourAttempt, out away))
        {
            EndRecoveries++;
            HoldPicture = true;
            StartAssignment(Operation.Detour, away, afterEnd: true);
            return;
        }

        HoldPicture = !_settings.CompositeDuringSeeks;
        if (!snap && _settings.StepForward && from >= 0 && target == from + 1 && CanStepTo(target))
        {
            if (!AnyRedrawOwed())
            {
                StartStep(target);
                return;
            }

            // A frame that is still to come for the last move of the clock would pass for the step's.
            StepsAvoided++;
        }

        // Giving the clock the position it already has asks nothing of the players. When one of
        // them has to deliver all the same, go by way of another frame.
        if (ClockIsAt(target) && AnyNeeds(target) && TryPickDetour(target, 0, out away))
        {
            HoldPicture = true;
            StartAssignment(Operation.Detour, away);
            return;
        }

        StartAssignment(Operation.Seek, target);
    }

    /// <summary>
    /// One frame forward can be stepped when every player in the picture either moves one frame or
    /// stays, and each one that moves is inside its stream. The clock stays behind while steps
    /// follow each other, and with it a player that was not stepped: one the clock still holds
    /// before its stream does not step (measured: nothing came for a quarter of a second).
    /// </summary>
    private bool CanStepTo(long target)
    {
        var moves = false;
        for (var track = 0; track < _trackCount; track++)
        {
            var wanted = Wanted(track, target);
            if (wanted < 0)
            {
                continue;
            }

            var delta = wanted - _shown[track];
            if (_shown[track] < 0 || delta < 0 || delta > 1)
            {
                return false;
            }

            if (delta == 1 && !_stepped[track] && !_timeline.IsPlayerInside(track, _clockSeconds))
            {
                return false;
            }

            moves |= delta == 1;
        }

        return moves;
    }

    /// <summary>
    /// The frame a track's player has to show for a timeline frame, or -1 when the track is not in
    /// that frame's picture. A camera outside its own time range is not drawn, so whatever its
    /// player holds then is good enough; a player parked there often delivers nothing at all.
    /// </summary>
    private long Wanted(int track, long timelineFrame) =>
        _timeline.IsShown(track, timelineFrame) ? _timeline.PlayerFrame(track, timelineFrame) : -1;

    private bool AnyRedrawOwed()
    {
        for (var track = 0; track < _trackCount; track++)
        {
            if (_redrawOwed[track])
            {
                return true;
            }
        }

        return false;
    }

    private bool AnyNeeds(long timelineFrame)
    {
        for (var track = 0; track < _trackCount; track++)
        {
            var wanted = Wanted(track, timelineFrame);
            if (wanted >= 0 && wanted != _shown[track])
            {
                return true;
            }
        }

        return false;
    }

    private bool AnyEndedPlayerIn(long timelineFrame)
    {
        for (var track = 0; track < _trackCount; track++)
        {
            if (_ended[track] && Wanted(track, timelineFrame) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private bool ClockIsAt(long timelineFrame)
    {
        if (!_clockExact || _clockBehind || _clockSeconds != _timeline.FrameMiddle(timelineFrame))
        {
            return false;
        }

        for (var track = 0; track < _trackCount; track++)
        {
            if (_stepped[track])
            {
                return false;
            }
        }

        return true;
    }

    private void StartStep(long target)
    {
        _operation = Operation.Step;
        _operationFrame = target;
        _answering = false;
        _chainKind = StudioPreviewLandingKind.Step;
        for (var track = 0; track < _trackCount; track++)
        {
            _needs[track] = Wanted(track, target) == _shown[track] + 1;
            _expects[track] = _needs[track];
            _arrived[track] = false;
            _completed[track] = false;
            _stepFrom[track] = _shown[track];
        }

        _operationStartedAt = _clock();
        for (var track = 0; track < _trackCount; track++)
        {
            if (_needs[track])
            {
                _stepped[track] = true;
                _ended[track] |= _stepFrom[track] + 1 >= _timeline.Track(track).FrameCount - 1;
                _transport.StepForward(track);
            }
        }

        StepsIssued++;
    }

    private void StartAssignment(Operation operation, long frame, bool afterEnd = false)
    {
        _operation = operation;
        _operationFrame = frame;
        _answering = false;

        // A detour is there to get a player out of a state in which it does not answer as usual.
        // It is only left once the players have said nothing more for the grace period: measured
        // after the end of a stream, a second frame sometimes follows the first by a few
        // milliseconds, and it must not be taken for the answer to the way back. The first change
        // after the clock ran is left the same way, and only then is its position reported: the
        // first frame a player hands over for it can be the one it had ready for playback.
        _landsWhenQuiet = _clockRan && operation == Operation.Seek;
        _operationSettles = _landsWhenQuiet || operation is Operation.Detour or Operation.Spend;
        if (_operationSettles)
        {
            _clockRan = false;
        }

        if (_landsWhenQuiet)
        {
            HoldPicture = true;
        }
        var seconds = _timeline.FrameMiddle(frame);
        for (var track = 0; track < _trackCount; track++)
        {
            // Nothing is owed by a player that is expected to lose this position.
            var wanted = Wanted(track, frame);
            var spent = operation == Operation.Spend && _timeline.Track(track).StartOffset != 0;
            _needs[track] = wanted >= 0 && wanted != _shown[track] && !spent;

            // A player answers a position change with a frame when the change moves it, and it
            // does unless both the old and the new position lie before its stream or both after it.
            // What a player does with a position it is expected to lose is not waited for either.
            _expects[track] = !spent && (_needs[track]
                || _stepped[track]
                || _timeline.IsPlayerInside(track, _clockSeconds)
                || _timeline.IsPlayerInside(track, seconds));
            _arrived[track] = false;
            _completed[track] = false;

            // A stepped player is ahead of the clock, and draws its frame again when the clock comes to it.
            _caughtUpWith[track] = operation == Operation.Sync && _stepped[track];
            _stepped[track] = false;

            // A player at the end of its stream raises SeekCompleted for its next seek at once and
            // hands the frame over as late as any other, so there SeekCompleted says nothing
            // about when the frame comes.
            _completesEarly[track] = afterEnd && _ended[track];
            _ended[track] = (_ended[track] && !afterEnd) || _timeline.IsPlayerAtEnd(track, seconds);
        }

        _clockBehind = false;
        _clockSeconds = seconds;
        _clockExact = true;
        _operationStartedAt = _clock();
        _lastAnswerAt = _operationStartedAt;
        _transport.Seek(frame);
        SeeksIssued++;
    }

    private bool TryFinishOperation(long now, out long deadline)
    {
        deadline = long.MaxValue;
        if (_operation == Operation.Step)
        {
            var stepped = AllNeededArrived();
            var end = _operationStartedAt + _settings.Ticks(_settings.StepTimeoutMilliseconds);
            if (!stepped && now < end)
            {
                deadline = end;
                return false;
            }

            if (!stepped && now < end + _settings.Ticks(_settings.StepTimeoutMilliseconds) && AnyNeededIsDelivering())
            {
                deadline = now + _settings.Ticks(2);
                return false;
            }

            _operation = Operation.None;
            FinishStep(stepped, now);
            return true;
        }

        if (!_answering)
        {
            if (!FramesResolved(now, out deadline, out var delivered, out var lostEarly))
            {
                return false;
            }

            _answering = true;
            _resolvedAt = now;
            _resolvedDelivered = delivered;
            _resolvedLostEarly = lostEarly;
            DetoursUnanswered += !delivered && _operation is Operation.Detour or Operation.Spend ? 1 : 0;
            if (delivered && !_landsWhenQuiet && _operation is Operation.Seek or Operation.Return)
            {
                // The picture is complete, so say so now. What is still waited for only keeps the
                // next position change from overlapping this one.
                Land(confirmed: true, now);
            }
        }

        if (!AnswersAreIn(now, out deadline, out var gaveUp))
        {
            return false;
        }

        if (_operationSettles)
        {
            var quietAt = _lastAnswerAt + _settings.Ticks(_settings.GraceMilliseconds);
            if (now < quietAt)
            {
                deadline = quietAt;
                return false;
            }

            // A frame that is being handed over right now has not been heard of yet. Its report
            // wakes the owner; until then, look again shortly.
            if (!gaveUp && now < _operationStartedAt + _settings.Ticks(_settings.TimeoutMilliseconds) && AnyIsDelivering())
            {
                deadline = now + _settings.Ticks(2);
                return false;
            }
        }

        AnswersGivenUp += gaveUp ? 1 : 0;
        var finished = _operation;
        var landsNow = _landsWhenQuiet;
        _operation = Operation.None;
        _answering = false;
        _landsWhenQuiet = false;
        FinishAssignment(finished, _resolvedDelivered, _resolvedLostEarly, landsNow, now);
        return true;
    }

    /// <summary>
    /// Whether every player that owes a new picture has delivered it or lost it.
    /// <paramref name="delivered"/> is true when none was lost.
    /// </summary>
    private bool FramesResolved(long now, out long deadline, out bool delivered, out bool lostEarly)
    {
        var timeoutAt = _operationStartedAt + _settings.Ticks(_settings.TimeoutMilliseconds);
        var answersBy = _operationStartedAt + _settings.Ticks(_settings.AnswerTimeoutMilliseconds);
        var grace = _settings.Ticks(_settings.GraceMilliseconds);
        var pending = false;
        var lost = false;
        lostEarly = false;
        deadline = timeoutAt;

        for (var track = 0; track < _trackCount; track++)
        {
            if (!_needs[track] || _arrived[track])
            {
                continue;
            }

            // After SeekCompleted the frame follows within the grace period or not at all. A
            // player at the end of its stream raises it at once and hands the frame over as late
            // as any other (measured: a tenth of a second later), or not at all (measured: one
            // such seek in fifty). It is given the time any player has to answer.
            var frameBy = _completedAt[track] + grace;
            if (_completesEarly[track])
            {
                frameBy = Math.Max(frameBy, answersBy);
            }

            if (now >= timeoutAt)
            {
                lost = true;
            }
            else if (!_completed[track])
            {
                pending = true;
            }
            else if (now < frameBy)
            {
                pending = true;
                deadline = Math.Min(deadline, frameBy);
            }
            else if (_transport.IsDelivering(track))
            {
                // The frame is being handed over right now. Its report wakes the owner.
                pending = true;
                deadline = Math.Min(deadline, now + _settings.Ticks(2));
            }
            else
            {
                lost = true;
                lostEarly = true;
            }
        }

        delivered = !pending && !lost;
        return !pending;
    }

    /// <summary>
    /// Whether every player has answered the position change, or shown that it will not: a frame
    /// and its <c>SeekCompleted</c>, or <c>SeekCompleted</c> with no frame within the grace period.
    /// Bounded, because a player that was not moved says nothing at all.
    /// </summary>
    private bool AnswersAreIn(long now, out long deadline, out bool gaveUp)
    {
        var grace = _settings.Ticks(_settings.GraceMilliseconds);
        var end = Math.Max(_operationStartedAt + _settings.Ticks(_settings.AnswerTimeoutMilliseconds), _resolvedAt + grace);
        deadline = end;
        gaveUp = false;
        var waiting = false;
        for (var track = 0; track < _trackCount; track++)
        {
            if (!_expects[track])
            {
                continue;
            }

            if (_arrived[track])
            {
                // Its SeekCompleted follows within a few milliseconds.
                waiting |= !_completed[track];
            }
            else if (_needs[track])
            {
                // Given up on above.
            }
            else if (!_completed[track])
            {
                waiting = true;
            }
            else if (now < _completedAt[track] + grace)
            {
                waiting = true;
                deadline = Math.Min(deadline, _completedAt[track] + grace);
            }
        }

        if (!waiting)
        {
            return true;
        }

        gaveUp = now >= end;
        return gaveUp;
    }

    private void FinishStep(bool delivered, long now)
    {
        if (!delivered)
        {
            // A player that did not step in time may still hand that frame over, and a seek made
            // now would flush it in the middle of that. So the way is by another frame, which is
            // only left once the players have been quiet.
            StepFallbacks++;
            _chainKind = StudioPreviewLandingKind.Seek;
            HoldPicture = true;
            if (TryPickDetour(_target, 0, out var detour))
            {
                StartAssignment(Operation.Detour, detour);
            }
            else
            {
                StartAssignment(Operation.Seek, _target);
            }

            return;
        }

        // The players are on the frame and the picture is right. Only the clock is not there yet.
        _clockBehind = true;
        _syncAt = now + _settings.Ticks(_settings.StepSyncDelayMilliseconds);
        Land(confirmed: true, now);
    }

    private void FinishAssignment(Operation finished, bool delivered, bool lostEarly, bool landsNow, long now)
    {
        if (finished == Operation.Sync)
        {
            for (var track = 0; track < _trackCount; track++)
            {
                _redrawOwed[track] = _caughtUpWith[track] && !_arrived[track];
            }

            // The players were on the frame already and only the clock had to move. A player that
            // answered with another frame is put right by a seek of its own.
            if (_requested < 0 && !PlayersAreOn(_operationFrame))
            {
                _settledConfirmed = false;
                _requested = _operationFrame;
                _requestedAt = now;
                _requestIsSnap = false;
            }

            return;
        }

        if (finished is Operation.Detour or Operation.Spend)
        {
            if (IsSuperseded())
            {
                _target = -1;
                return;
            }

            StartAssignment(Operation.Return, _target);
            return;
        }

        if (delivered)
        {
            if (landsNow && IsSuperseded())
            {
                // Nobody waits for this frame any more, and the picture stays held for the next.
                // The players rest on it all the same, which is where a step starts from.
                if (PlayersAreOn(_operationFrame))
                {
                    _settled = _operationFrame;
                    _settledConfirmed = true;
                }

                _target = -1;
                return;
            }

            // A late frame of an earlier change may have taken a clip's picture; then the frame
            // is gone to again, with the picture held.
            if (!PlayersAreOn(_operationFrame))
            {
                if (_requested < 0)
                {
                    _settledConfirmed = false;
                    _requested = _operationFrame;
                    _requestedAt = now;
                    _requestIsSnap = false;
                }

                return;
            }

            if (landsNow)
            {
                // The first change after the clock ran: what the players hold now is what they
                // were asked for, so it is reported only now.
                Land(confirmed: true, now);
            }
            else if (_requested < 0)
            {
                // Landed when its frames came in. A frame that was held back since has been
                // followed by the one that belongs there.
                HoldPicture = false;
            }

            return;
        }

        if (IsSuperseded())
        {
            // Nobody waits for this frame any more; the next request starts from what the players really show.
            _target = -1;
            return;
        }

        if (_repairs == 0)
        {
            Repairs++;
            LossesSeenEarly += lostEarly ? 1 : 0;
        }

        if (_repairs < _settings.MaxRepairs && TryPickDetour(_target, _repairs, out var detour))
        {
            _repairs++;
            HoldPicture = true;
            StartAssignment(Operation.Detour, detour);
            return;
        }

        RepairFailures++;
        Land(confirmed: false, now);
    }

    private void Land(bool confirmed, long now)
    {
        _settled = _target;
        _settledConfirmed = confirmed;
        _landings.Add(new StudioPreviewLanding(_target, confirmed, _chainKind, _chainRequestedAt, now, _repairs));
        _target = -1;
        HoldPicture = false;
    }

    private bool IsSuperseded() => _requested >= 0 && (_requested != _target || _requestIsSnap);

    private bool PlayersAreOn(long timelineFrame)
    {
        for (var track = 0; track < _trackCount; track++)
        {
            var wanted = Wanted(track, timelineFrame);
            if (wanted >= 0 && _shown[track] != wanted)
            {
                return false;
            }
        }

        return true;
    }

    private bool AnyNeededIsDelivering()
    {
        for (var track = 0; track < _trackCount; track++)
        {
            if (_needs[track] && !_arrived[track] && _transport.IsDelivering(track))
            {
                return true;
            }
        }

        return false;
    }

    private bool AnyIsDelivering()
    {
        for (var track = 0; track < _trackCount; track++)
        {
            if (_transport.IsDelivering(track))
            {
                return true;
            }
        }

        return false;
    }

    private bool AllNeededArrived()
    {
        for (var track = 0; track < _trackCount; track++)
        {
            if (_needs[track] && !_arrived[track])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A frame to go to and come back from: one on which every player that has to deliver on the
    /// way back shows something other than what it has to show at <paramref name="target"/>, so
    /// that coming back moves it.
    /// </summary>
    private bool TryPickDetour(long target, int attempt, out long detour)
    {
        var start = DetourDistances[Math.Min(attempt, DetourDistances.Length - 1)];
        for (var distance = start; distance <= start + DetourSearchSpan; distance++)
        {
            if (IsUsefulDetour(target - distance, target))
            {
                detour = target - distance;
                return true;
            }

            if (IsUsefulDetour(target + distance, target))
            {
                detour = target + distance;
                return true;
            }
        }

        detour = -1;
        return false;
    }

    private bool IsUsefulDetour(long candidate, long target)
    {
        if (candidate < 0 || candidate > _timeline.LastFrame)
        {
            return false;
        }

        for (var track = 0; track < _trackCount; track++)
        {
            var wanted = Wanted(track, target);
            var hasToDeliver = wanted >= 0 && (_shown[track] != wanted || _ended[track]);
            if (hasToDeliver && _timeline.PlayerFrame(track, candidate) == wanted)
            {
                return false;
            }
        }

        return true;
    }
}
