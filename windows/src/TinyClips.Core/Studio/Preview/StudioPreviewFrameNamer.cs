using System.Diagnostics;

namespace TinyClips.Core.Studio.Preview;

/// <summary>Limits of the frame namer. The defaults come from measurements of the players.</summary>
internal sealed record StudioPreviewNamingSettings
{
    /// <summary>Clock ticks per second; <see cref="Stopwatch.Frequency"/> unless a test supplies its own clock.</summary>
    public long TicksPerSecond { get; init; } = Stopwatch.Frequency;

    /// <summary>
    /// How often a player looks whether a frame is due to be handed over. Measured: every
    /// hundredth of a second, at times a hundredth of a second apart to within a few tenths of a
    /// millisecond, whatever the recording's frame rate.
    /// </summary>
    public double TickMilliseconds { get; init; } = 10;

    /// <summary>
    /// The least time between the end of one hand-over and the beginning of the next for the
    /// player to have looked for a frame in between and found none due: one tick and a margin.
    /// </summary>
    public double IdleGapMilliseconds { get; init; } = 12;

    /// <summary>How far from a whole number of ticks after the hand-over before a hand-over may begin and still count as begun on time.</summary>
    public double TickToleranceMilliseconds { get; init; } = 1.5;

    /// <summary>
    /// How far into its frame the position may be when a frame is handed over on time: one tick
    /// and a margin. Measured without anything holding the process up: 12.8 ms at most.
    /// </summary>
    public double OnTimeMilliseconds { get; init; } = 13;

    /// <summary>
    /// A copy that took this long or longer, not counting what the garbage collector kept it
    /// waiting on its way back, was held up while the player made it. When a frame came due in
    /// that time it may hold that frame and not the one the player had announced. Measured:
    /// 1.5 to 4.5 ms as a rule.
    /// </summary>
    public double PromptCopyMilliseconds { get; init; } = 8;

    /// <summary>Margin on the time the garbage collector accounts for, when a hand-over was kept waiting by it.</summary>
    public double CollectorSlackMilliseconds { get; init; } = 5;

    /// <summary>
    /// How long the frames may stay without a number before they are shown under the number
    /// their position gives, right or not, so that the picture does not stand still on a PC that
    /// cannot keep up. Measured with the process held up every third of a second, by the garbage
    /// collector, by a slow draw, or by being stopped altogether: 240 ms at most, 410 ms with all
    /// three at once.
    /// </summary>
    public double UnsureAfterMilliseconds { get; init; } = 500;

    /// <summary>How many frames in a row have to have a number again before the next doubt is waited out as the first was.</summary>
    public int KnownFramesToTrustAgain { get; init; } = 30;

    /// <summary>
    /// Takes every frame for the one its position names, as the engine did before. For the checks
    /// that show what that does when the process is held up.
    /// </summary>
    public bool BelievePositions { get; init; }

    /// <summary>
    /// Calls every number inferred, so that the clock never stops on a frame without that frame
    /// being fetched anew. For the checks: together with <see cref="BelievePositions"/> it shows
    /// what the fetch does for a number that is wrong.
    /// </summary>
    public bool FetchEveryRestingFrame { get; init; }

    /// <summary>
    /// Lets the rule for a hand-over the garbage collector kept waiting (rule 3) speak only when
    /// the time since the hand-over before it that the collector does not account for is at
    /// least <see cref="IdleGapMilliseconds"/>: when the player had its thread back for a look
    /// before the collector struck. Without it, a collection that begins in the instant a
    /// hand-over gives its thread back, after the namer was told that the hand-over is over, is
    /// taken for one that kept the next hand-over waiting at the door. In truth it kept the
    /// player's thread, and the player may be behind when it has it back. Off: it is here so
    /// that what it mends and what it costs, in frames that then have no number, can be measured.
    /// </summary>
    public bool CollectorRuleNeedsIdleGap { get; init; }

    internal double Milliseconds(long ticks) => ticks * 1000.0 / TicksPerSecond;
}

/// <summary>What became known, at the beginning of a hand-over, about the frame handed over before it.</summary>
internal enum StudioPreviewEarlierFrame : byte
{
    /// <summary>Nothing new: it has its number already, or still has none.</summary>
    Nothing,

    /// <summary>It is the frame given. It is still in its texture and has to be kept before the next copy goes over it.</summary>
    Known,

    /// <summary>
    /// It is the frame given if the copy that follows was made promptly. To be kept aside now and
    /// shown only if <see cref="StudioPreviewFrameNamer.End"/> says so.
    /// </summary>
    KnownIfPrompt,
}

internal enum StudioPreviewFrameKnowledge : byte
{
    /// <summary>
    /// Which frame it is cannot be told: a later one than the frame before it, and no later than
    /// the one given, which is the frame its position names. It has no place in anything that
    /// depends on which frame it is.
    /// </summary>
    Unknown,

    /// <summary>It is the frame given.</summary>
    Known,

    /// <summary>
    /// It is probably the frame given: the one its position names, or one or two before it. The
    /// frames have been without a number for too long to keep the picture waiting.
    /// </summary>
    Unsure,
}

/// <param name="Frame">The clip's own frame number, not clamped to the clip. For <see cref="StudioPreviewFrameKnowledge.Unknown"/>, the latest frame it can be.</param>
/// <param name="EarlierConfirmed">The frame kept aside as <see cref="StudioPreviewEarlierFrame.KnownIfPrompt"/> is the frame it was said to be.</param>
/// <param name="Rule">Which rule gave the number, for the trace.</param>
/// <param name="Inferred">
/// The number rests on what a player does when nothing but this process holds it up
/// (<see cref="StudioPreviewFrameNamer.Inferred"/>). Good enough to draw by. When the clock
/// stops on such a frame, the frame is to be fetched anew.
/// </param>
internal readonly record struct StudioPreviewNamedFrame(StudioPreviewFrameKnowledge Knowledge, long Frame, bool EarlierConfirmed, string Rule, bool Inferred = false);

/// <summary>
/// Tells which frame a player hands over while the clock runs.
/// <para>
/// A player says nothing about the frame it hands over. All there is to go by is the position it
/// reports at that moment, and that names the right frame only while nothing holds the process
/// up. Measured on two players at once, 30 and 60 frames a second, over 39,000 frames: without
/// anything in the way every frame was the one its position named. With the garbage collector
/// stopping every thread for 60 ms three times a second, 4 % of the frames were one to five
/// frames older than their position said; with the render thread keeping the device for 40 to
/// 150 ms, 1 to 2 %; with the whole process stopped for 30 to 130 ms, 5 to 8 %. Never newer.
/// </para>
/// <para>What was measured about a player, and what follows from it:</para>
/// <list type="number">
/// <item>It looks for a frame to hand over every hundredth of a second, and hands over one at a
/// time. A frame is never handed over before its time, so the frame is the one the position
/// names or an earlier one; and it holds on to a frame it has announced until that frame has been
/// copied, however long the copy is kept waiting. So a hand-over that waits for the device still
/// gets the frame it was begun for. (Never before its time while the clock runs. Once the clock
/// has been stopped a player can go on by itself for a moment, and the position it reports is
/// the stopped clock's: what it hands over then is not asked about here, see
/// <see cref="StudioPreviewHandOverKinds"/>.) Measured again with a hand-over kept waiting
/// for the device for an eighth of a second: the frame announced, 60 times of 60, and as often
/// with the whole process stopped from outside during the wait.</item>
/// <item>The frames come in order, each later than the one before. So a frame whose position
/// names the frame right after the one before it is that frame, whatever kept it.</item>
/// <item>A hand-over that the garbage collector kept waiting at the door was begun for the frame
/// that was next in line: the player's own threads are not stopped by the collector, and
/// announced that frame when it was due. That is so only when the player had its thread back
/// from the hand-over before in time: one that was itself kept, by the collector or by anything
/// else, until a later frame was due leaves the player behind, and what it hands over next is
/// then not always the next in line (seen: the collector twice in a row, the first time inside
/// a hand-over, the second at the door of the next, which then held the frame two after).</item>
/// <item>When the player is behind, it hands its frames over at every look, a hundredth of a
/// second apart, some of them late and some left out, and nothing says which. A frame handed over
/// then has no number, unless the frame after it comes with the very next number that is left for
/// it, which gives both theirs.</item>
/// <item>When a hand-over begins more than a hundredth of a second after the one before it ended,
/// on time, for the very next frame and with nothing to show that the process was held up in
/// between, the player looked for a frame in between and had none due. So the frame before it
/// was not late: it was the frame its position named. That gives the frames their numbers back
/// after a hold-up.</item>
/// <item>A copy that took long, while a frame came due, may hold that frame and not the one that
/// was announced (seen once, with the process stopped in the middle of a copy), and has no
/// number. Not so when the time was the garbage collector's, which keeps the engine's thread
/// waiting on its way back from the player and not the player; nor when no frame came due
/// meanwhile, for then the player had no other frame to give.</item>
/// </list>
/// <para>
/// What comes of it is one of three things (<see cref="StudioPreviewFrameKnowledge"/>). A frame
/// with its number is drawn and reported as that frame. A frame without one is told to nobody
/// as a frame: the engine shows it only where the scene comes out the same whichever frame it
/// is, and never rests on it. And when no frame has had a number for half a second, as on a PC
/// that cannot keep up, the frames are shown under the number of their position and called
/// unsure.
/// </para>
/// <para>
/// Rules 1, 2 and 4 follow from the order of the frames. Rules 3, 5 and 6 rest on what a player
/// does while nothing but this process holds it up, and a number they give is called inferred
/// (<see cref="Inferred"/>): it is drawn by, and when the clock stops on it the frame is fetched
/// anew. On the measurements above the rules gave no frame a wrong number while one thing held
/// the process up (31,500 frames). With the collector, the render thread and stops of the whole
/// process all at once, 6 of 7,500, each by rule 3 with the process stopped during a
/// collection; believing every position, 860 of those 7,500 were wrong. In the engine's own
/// checks since, rule 3 has also been wrong with the collector alone at work, about once in
/// 100,000 frames: the hand-over held the frame after the one next in line. Why is not known;
/// a collection begun within 40 millionths of a second of the copy before did not bring it
/// about in 300 tries.
/// </para>
/// <para>
/// One thing the rules cannot see, and what it does. When the whole process is stopped from
/// outside in the instant a player announces a frame, within about a fifth of a millisecond of
/// it, the player puts a later frame in the place of the announced one when it runs again: the
/// frame two after it, every time it was seen. The hand-over that was under way has read its
/// position, makes its copy when the process runs again, and looks like one that waited for the
/// device: its frame is given the number of its position, by rule 2, and shows the frame two
/// after. Seen 5 times in 540 plays with a stop of a tenth of a second aimed at the first frames
/// of playback, and once in some 50,000 frames with the process stopped at random twice a second;
/// not with the garbage collector, slow draws or busy processors, which do not stop a player.
/// The picture and its number are then two frames apart until the frames have numbers again:
/// 40 and 70 ms in the two plays that were looked at. Telling it would take a witness that the
/// process ran, such as a thread that does nothing but look at the clock; there is none.
/// </para>
/// Not thread-safe: the owner calls every member under one lock.
/// </summary>
internal sealed class StudioPreviewFrameNamer
{
    private readonly StudioPreviewNamingSettings _settings;
    private readonly double _frameMilliseconds;

    // What is known of the frame handed over last: it is _floor when _exact, and at least _floor
    // otherwise. Nothing at all while !_known.
    private bool _known;
    private bool _exact;
    private long _floor;

    // The hand-over before the one in progress. Where it ended, what its position named then and
    // the collector's total are those of the moment the player got its thread back, when Exit
    // was called, and of the end of the copy otherwise.
    private bool _hasEarlier;
    private bool _earlierIsStart;
    private long _earlierStartedAt;
    private long _earlierEndedAt;
    private long _earlierName;
    private long _earlierNameAfter;
    private bool _earlierPrompt;
    private bool _earlierShown;
    private double _earlierCollectorPause;

    // The hand-over in progress, between Begin and End.
    private long _startedAt;
    private long _name;
    private double _gapMilliseconds;
    private double _collectorMilliseconds;
    private double _collectorAtBegin;
    private StudioPreviewEarlierFrame _promised;

    // Frames shown without being sure of their number.
    private long _doubtSince = long.MinValue;
    private bool _showsUnsure;
    private int _knownInARow;

    // A number since the clock started came from rule 3 or 5, or from a copy whose time was laid
    // to the collector, and too few frames have come on time and one by one since.
    private bool _inferred;
    private int _onTimeInARow;
    private double _intoMilliseconds;

    public StudioPreviewFrameNamer(double frameRate, StudioPreviewNamingSettings? settings = null)
    {
        _settings = settings ?? new StudioPreviewNamingSettings();
        _frameMilliseconds = 1000.0 / StudioPreviewTimeMath.NormalizeFrameRate(frameRate);
    }

    /// <summary>Frames are being shown under numbers that are not certain, because none had been for too long.</summary>
    public bool ShowsUnsure => _showsUnsure;

    /// <summary>
    /// The numbers given now rest on an inference: that a player whose hand-over the collector
    /// kept waiting had announced its frame when it was due (rule 3), that a player which let a
    /// look go by had no frame due (rule 5), that a copy which took long took long only on its
    /// way back (rule 6), or that a frame which came with the position of the frame before it
    /// was that frame once more. Each holds when this process is what holds the player up, and
    /// not when the player itself cannot keep up: a player that is starved of processor time
    /// hands over frames that are older than its position says, at any time. Seen with every
    /// processor kept busy and the players handing nothing over for four seconds: a frame 22
    /// frames older than the number rule 5 then gave it, before that rule asked for the very
    /// next frame. Nothing a player says tells the two apart; a frame that is fetched anew
    /// does. True from such a number until
    /// <see cref="StudioPreviewNamingSettings.KnownFramesToTrustAgain"/> frames have come on
    /// time and one by one, or the clock starts again from a frame that was fetched.
    /// </summary>
    public bool Inferred => _inferred || _settings.FetchEveryRestingFrame;

    /// <summary>
    /// The clock is about to start, with the player at rest.
    /// </summary>
    /// <param name="restingFrame">The frame the player rests on; -1 when it has yet to reach its first frame; below that when it is not known.</param>
    /// <param name="now">Clock reading.</param>
    /// <param name="collectorPause">How long the garbage collector has stopped the process so far, in milliseconds.</param>
    public void Start(long restingFrame, long now, double collectorPause)
    {
        _known = restingFrame >= -1;
        _exact = _known;
        _floor = restingFrame;
        _hasEarlier = _known;
        _earlierIsStart = true;
        _earlierStartedAt = now;
        _earlierEndedAt = now;
        _earlierName = restingFrame;
        _earlierNameAfter = restingFrame;
        _earlierPrompt = true;
        _earlierShown = true;
        _earlierCollectorPause = collectorPause;
        _promised = StudioPreviewEarlierFrame.Nothing;

        // Whether frames are shown unsure is not forgotten: a PC that could not keep up before
        // the clock stopped will not keep up now, and is not to be kept waiting again.
        _doubtSince = long.MinValue;

        // The frame the player rests on was fetched: nothing is inferred about it.
        _inferred = false;
        _onTimeInARow = 0;
    }

    /// <summary>Nothing is known any more: a copy failed, or the frame went somewhere it cannot be shown from.</summary>
    public void Forget()
    {
        _known = false;
        _exact = false;
        _hasEarlier = false;
        _promised = StudioPreviewEarlierFrame.Nothing;
    }

    /// <summary>
    /// A hand-over begins. To be called before the copy, with what was read first thing.
    /// </summary>
    /// <param name="startedAt">Clock reading when the hand-over began.</param>
    /// <param name="name">The frame that contains the position the player reported then, not clamped to the clip.</param>
    /// <param name="intoMilliseconds">How far into that frame the position was.</param>
    /// <param name="collectorPause">The garbage collector's total in milliseconds, read after the position.</param>
    /// <param name="earlier">The frame handed over before, when the result says something about it.</param>
    public StudioPreviewEarlierFrame Begin(long startedAt, long name, double intoMilliseconds, double collectorPause, out long earlier)
    {
        _startedAt = startedAt;
        _name = name;
        _intoMilliseconds = intoMilliseconds;
        _gapMilliseconds = _hasEarlier ? _settings.Milliseconds(startedAt - _earlierEndedAt) : double.PositiveInfinity;
        _collectorMilliseconds = _hasEarlier ? collectorPause - _earlierCollectorPause : 0;
        _collectorAtBegin = collectorPause;
        _promised = StudioPreviewEarlierFrame.Nothing;
        earlier = -1;
        if (_settings.BelievePositions || !_hasEarlier || _earlierIsStart)
        {
            return _promised;
        }

        // Rule 5. The player looked for a frame after the hand-over before this one, and had none
        // due: so that frame was not late, and was the one its position named. Only when this
        // one is the very next frame, on time: a player that hands over nothing while frame
        // after frame comes due is not waiting for its next frame, it cannot keep up.
        if (!(_known && _exact)
            && _gapMilliseconds >= _settings.IdleGapMilliseconds
            && _collectorMilliseconds == 0
            && _earlierPrompt
            && _earlierNameAfter == _earlierName
            && name == _earlierName + 1
            && intoMilliseconds <= _settings.OnTimeMilliseconds
            && BeganOnATick(startedAt))
        {
            _known = true;
            _exact = true;
            _floor = _earlierName;
            _inferred = true;
            _onTimeInARow = 0;
            if (!_earlierShown)
            {
                earlier = _earlierName;
                _promised = StudioPreviewEarlierFrame.Known;
            }

            return _promised;
        }

        // Rule 4. The frame before is at least _floor, and this one is later and no later than its
        // position names. When that leaves this one only one number, the one before has only one too.
        if (_known && !_exact && !_earlierShown && name == _floor + 1 && _floor >= 0)
        {
            earlier = _floor;
            _promised = StudioPreviewEarlierFrame.KnownIfPrompt;
        }

        return _promised;
    }

    /// <summary>The copy of the hand-over in progress has been made.</summary>
    /// <param name="endedAt">Clock reading now.</param>
    /// <param name="nameAfter">The frame that contains the position the player reports now, not clamped.</param>
    /// <param name="copyMilliseconds">How long the player's copy took.</param>
    /// <param name="collectorPause">The garbage collector's total in milliseconds, read now.</param>
    public StudioPreviewNamedFrame End(long endedAt, long nameAfter, double copyMilliseconds, double collectorPause)
    {
        var name = _name;

        // Rule 6. Prompt, or as good as: the collector's time is spent on the way back from the
        // player, with the copy made; and where no frame came due, there was none to mistake it for.
        var laidToCollector = nameAfter != name
            && copyMilliseconds >= _settings.PromptCopyMilliseconds
            && copyMilliseconds - (collectorPause - _collectorAtBegin) < _settings.PromptCopyMilliseconds;
        var prompt = copyMilliseconds < _settings.PromptCopyMilliseconds || nameAfter == name || laidToCollector;
        var plain = false;
        var knowledge = StudioPreviewFrameKnowledge.Unknown;
        var frame = name;
        var rule = "no number";
        var confirmed = false;
        if (_settings.BelievePositions)
        {
            knowledge = StudioPreviewFrameKnowledge.Known;
            frame = name;
            rule = "position";
            _known = true;
        }
        else if (_known)
        {
            var next = _floor + 1;
            if (!prompt)
            {
                // It is later than the one before, and that is all.
                rule = "copy held up";
            }
            else if (name == next)
            {
                // Rule 2.
                knowledge = StudioPreviewFrameKnowledge.Known;
                frame = name;
                rule = _exact ? "next" : "only one left";
                confirmed = _promised == StudioPreviewEarlierFrame.KnownIfPrompt;
                plain = !laidToCollector && _intoMilliseconds <= _settings.OnTimeMilliseconds;
                _inferred |= laidToCollector;
            }
            else if (_exact && name == _floor)
            {
                // The frame that is shown already, handed over once more. Or the frame after
                // it, if the one shown was an earlier frame than it was taken for: a player
                // hands no frame over twice as a rule (never, in 70,000 frames of the checks).
                knowledge = StudioPreviewFrameKnowledge.Known;
                frame = name;
                rule = "again";
                _inferred = true;
            }
            else if (_exact
                && _hasEarlier
                && name > next
                && _collectorMilliseconds > 0
                && _earlierNameAfter == _floor
                && _gapMilliseconds < _frameMilliseconds + _collectorMilliseconds + _settings.CollectorSlackMilliseconds
                && (!_settings.CollectorRuleNeedsIdleGap || _gapMilliseconds - _collectorMilliseconds >= _settings.IdleGapMilliseconds))
            {
                // Rule 3. The player had its thread back while the frame before was still the
                // one due, so it was not behind when this one came due.
                knowledge = StudioPreviewFrameKnowledge.Known;
                frame = next;
                rule = "kept by the collector";
                _inferred = true;
            }
            else if (name < next)
            {
                // A position before the frame that is due: what was taken for known was not.
                _known = false;
                _exact = false;
                rule = "out of order";
            }
        }

        if (knowledge == StudioPreviewFrameKnowledge.Known)
        {
            _floor = frame;
            _exact = true;
            _doubtSince = long.MinValue;
            if (_showsUnsure && ++_knownInARow >= _settings.KnownFramesToTrustAgain)
            {
                _showsUnsure = false;
            }

            // A player that has handed over its frames on time and one by one for this long is
            // keeping up, whatever it did before.
            _onTimeInARow = plain ? _onTimeInARow + 1 : 0;
            if (_inferred && _onTimeInARow >= _settings.KnownFramesToTrustAgain)
            {
                _inferred = false;
            }
        }
        else
        {
            if (_known)
            {
                _floor++;
                _exact = false;
            }

            _knownInARow = 0;
            _onTimeInARow = 0;
            if (_doubtSince == long.MinValue)
            {
                _doubtSince = _startedAt;
            }

            _showsUnsure |= _settings.Milliseconds(endedAt - _doubtSince) >= _settings.UnsureAfterMilliseconds;

            // A frame is never handed over before its time. A copy that was held up may hold
            // the frame that came due while it was made.
            frame = prompt ? name : Math.Max(name, nameAfter);
            if (_showsUnsure)
            {
                knowledge = StudioPreviewFrameKnowledge.Unsure;
                frame = name;
                rule = "unsure, by position";
            }
        }

        _hasEarlier = true;
        _earlierIsStart = false;
        _earlierStartedAt = _startedAt;
        _earlierEndedAt = endedAt;
        _earlierName = name;
        _earlierNameAfter = nameAfter;
        _earlierPrompt = prompt;
        _earlierShown = knowledge != StudioPreviewFrameKnowledge.Unknown;
        _earlierCollectorPause = collectorPause;
        _promised = StudioPreviewEarlierFrame.Nothing;
        return new StudioPreviewNamedFrame(knowledge, frame, confirmed, rule, (_inferred || _settings.FetchEveryRestingFrame) && knowledge == StudioPreviewFrameKnowledge.Known);
    }

    /// <summary>
    /// The hand-over is over and the player gets its thread back. To be called last thing: what
    /// keeps the thread after this is taken for something that kept the next hand-over waiting
    /// at the door, with the player free to announce its frames on time.
    /// </summary>
    /// <param name="exitedAt">Clock reading now.</param>
    /// <param name="nameAtExit">The frame that contains the position the player reports now, not clamped.</param>
    /// <param name="collectorPause">The garbage collector's total in milliseconds, read now.</param>
    public void Exit(long exitedAt, long nameAtExit, double collectorPause)
    {
        if (!_hasEarlier || _earlierIsStart)
        {
            return;
        }

        _earlierEndedAt = exitedAt;
        _earlierNameAfter = nameAtExit;
        _earlierCollectorPause = collectorPause;
    }

    /// <summary>Whether a hand-over began a whole number of the player's ticks after the one before it began.</summary>
    private bool BeganOnATick(long startedAt)
    {
        var tick = _settings.TickMilliseconds;
        var since = _settings.Milliseconds(startedAt - _earlierStartedAt) % tick;
        return Math.Min(since, tick - since) <= _settings.TickToleranceMilliseconds;
    }
}
