using System.Diagnostics;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Preview;
using TinyClips.Tools.StudioPreviewCheck.Media;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

// What the preview does while this process is held up, as it is on a PC that is busy: pauses, and
// a zoom that moves while the preview plays. A player says nothing about the frame it hands over
// except where it is at that moment, and a hand-over that comes late finds the position moved on.
// With --believe-positions the engine takes every frame for the one its position names, as it did
// before, and these checks fail: that is their negative control.
internal sealed partial class HeadlessChecks
{
    // One zoom that starts at 4 s and takes three seconds to move in, on the part around
    // (0.30, 0.28), which keeps the screen's strip in the picture. The frames 120 to 209 have
    // their middles inside the move.
    private const double ZoomStart = 4;
    private const double ZoomEaseIn = 3;
    private const double ZoomEnd = 10;
    private const double ZoomFocusX = 0.30;
    private const double ZoomFocusY = 0.28;
    private const int ZoomPlayedFrom = 100;
    private const int ZoomMovingFirst = 120;
    private const int ZoomMovingLast = 209;
    private const int ZoomPlayedTo = 216;

    /// <summary>The kinds of hold-up a group goes through: what <c>--stalls</c> names, or the usual ones in turn.</summary>
    private string[] HoldUpKinds(params string[] usual)
    {
        var asked = HoldUps.Parse(_options.Text("stalls", string.Empty));
        return asked.Length > 0 ? asked : usual;
    }

    private int HoldUpMilliseconds => _options.Number("stall-ms", 60);

    /// <summary>
    /// What the frames handed over during a check come to. It is told when the hold-ups stopped
    /// the whole process: a wrong number that such a stop explains is counted apart, and so is
    /// one that came from the namer's rule 3, unless <c>--judge-limits</c> asks for both to be
    /// judged like any other.
    /// </summary>
    private ProbeTotals NewTotals(HoldUps? holdUps) => new()
    {
        ProcessStops = holdUps is null ? null : () => holdUps.ProcessStops,
        JudgeLimits = _options.Flag("judge-limits"),
    };

    // What the checks of the numbers are called: every number has to be right, but for the one
    // rule that has been seen wrong and whose numbers the engine itself calls inferred.
    private const string NumbersHold = "no frame was given a number that its pixels do not show (but by rule 3, a hand-over the collector kept waiting: at most one, or one in 20,000)";

    private int _tracesKept;

    // ---------------------------------------------------------------------------------------
    // Pauses
    // ---------------------------------------------------------------------------------------

    private void PausesHeldUp()
    {
        var count = _options.Number("count", _quick ? 12 : 30);
        foreach (var kind in HoldUpKinds("none", "collector", "draw", "stopped", "afterstop"))
        {
            PausesHeldUp(kind, count);
        }
    }

    private void PausesHeldUp(string kind, int count)
    {
        using var holdUps = new HoldUps(kind, HoldUpMilliseconds);
        _report.Section($"Pauses during playback, with {holdUps.Name}: {count} pauses");
        var session = OpenSession(TestMedia.Camera, options: holdUps.With(Muted));
        try
        {
            var last = session.Folder.FrameCount - 1;
            session.SeekTo(30);
            using var probe = new TruthProbe(session.Engine, session.Folder.Screen, session.Folder.Camera, capacity: 300);
            var numbers = NewTotals(holdUps);
            var stepNumbers = NewTotals(holdUps);
            var steppedBack = new Samples();
            var before = session.Engine.GetDiagnostics();
            var wrong = new List<string>();
            var wrongAtRest = new List<string>();
            var stepsWrong = new List<string>();
            var pauseCall = new Samples();
            var untilFinal = new Samples();
            var untilIdle = new Samples();
            var untilIdleFetched = new Samples();
            var fetchedAnew = 0;
            var pausedAt = new List<long>();
            var apartAtReturn = 0;
            var drawnAfterReturn = 0;
            var playersElsewhere = 0;
            var lateAfter = new Samples();
            var lateLater = 0;
            var lateInPauses = 0;
            var numberDumps = new List<string>();
            holdUps.Begin();
            for (var cycle = 0; cycle < count; cycle++)
            {
                if (session.PositionFrame > last - 45)
                {
                    session.SeekTo(30);
                }

                session.Recorder.Drain();
                var fetchedBefore = session.Engine.GetDiagnostics().RestsFetchedAnew;
                session.Engine.Play();
                Thread.Sleep(150 + _random.Next(350));
                var called = Stopwatch.GetTimestamp();
                session.Engine.Pause();
                var returned = Stopwatch.GetTimestamp();

                // The moment Pause() has returned: what a caller reads, and what the pictures hold.
                var position = session.PositionFrame;
                var playing = session.Engine.IsPlaying;
                var screen = session.ReadClipFrame(0);
                var camera = session.ReadClipFrame(1);
                var wantedCamera = session.ExpectedCamera(position);
                pausedAt.Add(called);
                pauseCall.Add(Stopwatch.GetElapsedTime(called, returned).TotalMilliseconds);
                apartAtReturn += camera != wantedCamera ? 1 : 0;

                // When the engine has nothing left to do, and a moment after that.
                var idle = session.WaitForIdle();
                var idleAfter = Stopwatch.GetElapsedTime(called).TotalMilliseconds;
                if (session.Engine.GetDiagnostics().RestsFetchedAnew > fetchedBefore)
                {
                    // The clock stopped on a frame whose number rested on an inference, and the
                    // engine fetched that frame anew to be certain.
                    fetchedAnew++;
                    untilIdleFetched.Add(idleAfter);
                }
                else
                {
                    untilIdle.Add(idleAfter);
                }

                Thread.Sleep(60);
                idle &= session.WaitForIdle();
                var shown = session.ReadShown();
                var positionLater = session.PositionFrame;
                var screenLater = session.ReadClipFrame(0);
                var cameraLater = session.ReadClipFrame(1);
                var drawn = session.Recorder.Drain();
                var scenes = drawn.Where(c => c.At >= returned).ToList();
                drawnAfterReturn += scenes.Count > 0 ? 1 : 0;

                // What the players still handed over once the clock had been stopped: how long
                // after Pause() was called it set out, and whether it was a later frame than
                // the position it came with names, which is the position the clock stopped on.
                var lateBefore = lateAfter.Count;
                var wrongBefore = numbers.WrongNumbers + numbers.InferredWrong + numbers.StoppedInTheMiddle;
                var handedOver = probe.Read(numbers);
                if (numbers.WrongNumbers + numbers.InferredWrong + numbers.StoppedInTheMiddle > wrongBefore)
                {
                    // The trace of the playing that gave a frame a wrong number, while it is
                    // still there: also for a number that is counted apart and not judged.
                    numberDumps.Add(Dump(session, "heldup-number"));
                }

                foreach (var frame in handedOver)
                {
                    if (frame.Kind == StudioPreviewHandOverKind.Late && frame.Truth != FrameCode.Unreadable)
                    {
                        lateAfter.Add(Stopwatch.GetElapsedTime(called, frame.StartedAt).TotalMilliseconds);
                        lateLater += frame.Truth > frame.NameByPosition ? 1 : 0;
                    }
                }

                lateInPauses += lateAfter.Count > lateBefore ? 1 : 0;

                // The latest frame the scene showed before Pause() was called, against the one
                // it stopped on: later, when the scene showed a frame without a number.
                var latest = drawn.Where(c => c.At < called && c.Screen != FrameCode.Unreadable).Select(c => c.Screen).DefaultIfEmpty(position).Max();
                steppedBack.Add(Math.Max(0, latest - position));
                untilFinal.Add(Stopwatch.GetElapsedTime(called, scenes.Count > 0 ? Math.Max(returned, scenes[^1].At) : returned).TotalMilliseconds);

                // What the players themselves hold at the end. Not judged: a player that was
                // already where the clock was put hands nothing over, and that is no fault.
                if (session.ReadClipFrame(0, copyTarget: true) != position || session.ReadClipFrame(1, copyTarget: true) != wantedCamera)
                {
                    playersElsewhere++;
                }

                // What Pause() promises for the moment it returns.
                var problem = playing ? "IsPlaying was still true when Pause() had returned"
                    : screen == FrameCode.Unreadable ? "the screen's picture could not be read when Pause() had returned"
                    : screen != position ? $"when Pause() had returned, Position was frame {position} and the screen's picture showed frame {screen}"
                    : positionLater != position ? $"Position went from frame {position} to frame {positionLater} after Pause() had returned"
                    : screenLater != screen ? $"the screen's picture went from frame {screen} to frame {screenLater} after Pause() had returned"
                    : scenes.Any(c => c.Screen != position) ? $"Position was frame {position} when Pause() returned, and the scenes drawn after that showed screen frames [{string.Join(' ', scenes.Select(c => c.Screen))}]"
                    : null;
                if (problem is not null)
                {
                    wrong.Add($"pause {cycle + 1}: {problem}{Dump(session, "heldup-pause")}");
                }

                // And what the preview has come to when it has nothing left to do: by itself
                // this is what a frame fetched anew puts right.
                var wantedLater = session.ExpectedCamera(positionLater);
                var restProblem = !idle ? "the engine did not come to rest"
                    : screenLater != positionLater ? $"at rest Position was frame {positionLater} and the screen's picture showed frame {screenLater}"
                    : shown != new Shown(positionLater, wantedLater) ? $"at rest Position was frame {positionLater} and the scene showed {shown}; the camera frame that goes with it is {wantedLater}"
                    : cameraLater != wantedLater ? $"the camera's picture came to rest on frame {cameraLater}; the frame that goes with screen frame {positionLater} is {wantedLater}"
                    : null;
                if (restProblem is not null)
                {
                    wrongAtRest.Add($"pause {cycle + 1}: {restProblem}{Dump(session, "heldup-rest")}");
                }

                // One frame on. An engine that is wrong about the frame its players rest on
                // shows another frame than the one asked for here.
                var next = position + 1;
                session.Engine.Seek(TestFolder.TimeOf(next, 0.5));
                var stepped = session.WaitForIdle();
                var after = session.ReadShown();
                if (!stepped || after != new Shown(next, session.ExpectedCamera(next)) || session.PositionFrame != next)
                {
                    stepsWrong.Add($"pause {cycle + 1}: stopped on frame {position}; Seek to frame {next} showed {after}, Position frame {session.PositionFrame}, idle {stepped}{Dump(session, "heldup-step")}");
                }

                // What the players hand over for that is kept apart: a player that is stepped
                // reports the position it had before, with the next frame.
                probe.Read(stepNumbers);
            }

            holdUps.Rest();
            var end = session.Engine.GetDiagnostics();
            _report.Check(
                $"with {holdUps.Name}: when Pause() returns, Position is the frame the screen's picture shows; neither changes afterwards; and no scene drawn after the return shows another screen frame ({count} pauses)",
                wrong.Count == 0,
                wrong.Count == 0 ? $"0 of {count} disagreed" : $"{wrong.Count} of {count} disagreed; first: {string.Join(" | ", wrong.Take(3))}");
            _report.Check(
                $"with {holdUps.Name}: at rest after each pause, Position is the frame the screen's picture and the scene show, and the camera is on the frame that goes with it ({count} pauses)",
                wrongAtRest.Count == 0,
                wrongAtRest.Count == 0 ? $"0 of {count} disagreed" : $"{wrongAtRest.Count} of {count} disagreed; first: {string.Join(" | ", wrongAtRest.Take(3))}");
            _report.Check(
                $"with {holdUps.Name}: a Seek to the next frame after each pause shows that frame on both clips ({count} times)",
                stepsWrong.Count == 0,
                stepsWrong.Count == 0 ? null : $"{stepsWrong.Count} of {count} wrong; first: {string.Join(" | ", stepsWrong.Take(3))}");
            _report.Check(
                $"with {holdUps.Name}: {NumbersHold}",
                numbers.Hold && numbers.Lost == 0 && numbers.Numbered > 0,
                numbers.Describe() + (numbers.Wrong.Count == 0 ? string.Empty : "; first: " + string.Join(" | ", numbers.Wrong.Take(3))) + string.Concat(numberDumps.Take(2)));
            _report.Check($"with {holdUps.Name}: no failure was reported", session.Events.FailedEvents == 0, string.Join("; ", session.Events.Failures()));
            _report.Note($"with {holdUps.Name}: after the pauses and before anything else was asked for, the players handed over {numbers.Answers} frames while the clock stood, for the position the engine gave them to bring the clips together; {numbers.AnswersWrong} of them did not show the frame of the position they came with{(numbers.WrongAnswers.Count == 0 ? string.Empty : ": " + string.Join(" | ", numbers.WrongAnswers.Take(3)))}");
            _report.Note($"with {holdUps.Name}: in {lateInPauses} of the {count} pauses a player still handed a frame over once the clock had been stopped, and it was left out: {lateAfter.Count} frames{(lateAfter.Count == 0 ? string.Empty : $", which set out {lateAfter.Summary()} after Pause() was called; {lateLater} of them showed a later frame than the position they came with named, the position the clock had stopped on")}");
            _report.Note($"with {holdUps.Name}: the scene had shown a later frame than the one it stopped on, one without a number, before {count - steppedBack.CountOf(0)} of the {count} pauses: later by {steppedBack.Summary("frames")}");
            _report.Note($"with {holdUps.Name}: {holdUps.Describe()}; {holdUps.CountNear(pausedAt, 100)} of the {count} pauses came during a hold-up or within 100 ms after one");
            _report.Note($"with {holdUps.Name}: the Pause() call took {pauseCall.Summary()}; from the call until picture and Position were final: {untilFinal.Summary()}; until the engine had nothing left to do: {untilIdle.Summary()}. A scene was drawn after Pause() had returned in {drawnAfterReturn} of {count}; the camera was not on the frame that goes with the screen's when Pause() returned in {apartAtReturn}; a player's own texture held another frame than the picture at the end in {playersElsewhere}");
            _report.Note($"with {holdUps.Name}: the clock stopped on a frame whose number rested on an inference about a player in {fetchedAnew} of the {count} pauses, and the engine fetched the frame anew{(fetchedAnew == 0 ? string.Empty : $": until it had nothing left to do then, {untilIdleFetched.Summary()}")}");
            _report.Note($"with {holdUps.Name}: {Naming(before, end)}");
        }
        finally
        {
            holdUps.Rest();
            Close(session);
        }
    }

    /// <summary>What became of the frames the players handed over between two readings of the engine's counters.</summary>
    private static string Naming(StudioPreviewDiagnostics before, StudioPreviewDiagnostics after)
    {
        static long Sum(long[] a, long[] b) => a.Zip(b, (x, y) => x - y).Sum();
        var without = Sum(after.FramesWithoutNumber, before.FramesWithoutNumber);
        var shown = Sum(after.FramesShownWithoutNumber, before.FramesShownWithoutNumber);
        var late = Sum(after.FramesNumberedLate, before.FramesNumberedLate);
        return $"the engine's own count: of {Sum(after.FramesCopied, before.FramesCopied)} frames the players handed over, it could not tell which frame {without} were; {shown} of those were shown all the same, the scene being the same whichever they were, {late} got their number from the frame after them, and the rest ({Math.Max(0, without - shown - late)} at least) were not shown. {Sum(after.FramesShownUnsure, before.FramesShownUnsure)} were shown under a number that was not certain, {Sum(after.FramesPassedOver, before.FramesPassedOver)} were written over before they were drawn, and {after.LateFramesDiscarded - before.LateFramesDiscarded} were left out because of a Pause()";
    }

    // ---------------------------------------------------------------------------------------
    // A zoom that moves while the preview plays
    // ---------------------------------------------------------------------------------------

    private static StudioProject WithMovingZoom(StudioProject project) => project with
    {
        Zooms =
        [
            new StudioZoom
            {
                Start = ZoomStart,
                End = ZoomEnd,
                Scale = 2,
                Focus = new StudioZoomFocus { X = ZoomFocusX, Y = ZoomFocusY },
                EaseIn = ZoomEaseIn,
                EaseOut = 0.5,
            },
        ],
    };

    private void ZoomWhilePlaying()
    {
        var plays = _options.Number("count", _quick ? 1 : 2);
        foreach (var kind in HoldUpKinds("none", "collector", "draw", "stopped"))
        {
            ZoomWhilePlaying(kind, plays);
        }

        EditorPlaysThroughAZoom();
    }

    private void ZoomWhilePlaying(string kind, int plays)
    {
        using var holdUps = new HoldUps(kind, HoldUpMilliseconds, _options.Number("stall-gap", 0));

        // Asked for with --from: the frame the playing starts on, to have a stretch without a
        // move to compare the move with; and with --camera none, a project without a camera.
        var from = Math.Clamp(_options.Number("from", ZoomPlayedFrom), 10, ZoomPlayedFrom);
        var camera = _options.Text("camera", "late").Equals("none", StringComparison.OrdinalIgnoreCase) ? null : TestMedia.Camera;
        _report.Section($"A zoom that moves in while the preview plays, with {holdUps.Name}: played {plays} times");
        var session = OpenSession(camera, edit: WithMovingZoom, options: holdUps.With(Muted));
        try
        {
            var layout = new MovingLayout(session.Project, session.Folder.Screen, session.Folder.Camera, 1280, 720);
            _report.Line($"  the layout before the zoom: {layout.Describe(ZoomMovingFirst - 1)}; half way: {layout.Describe((ZoomMovingFirst + ZoomMovingLast) / 2)}; moved in: {layout.Describe(ZoomMovingLast + 1)}");
            var played = new List<PlayedScenes>();
            var numbers = NewTotals(holdUps);
            for (var play = 0; play < plays; play++)
            {
                played.Add(PlayAndRead(
                    session,
                    layout,
                    from,
                    ZoomPlayedTo,
                    () => session.SeekTo(from),
                    session.Engine.Play,
                    session.Engine.Pause,
                    holdUps,
                    $"zoom-{kind}",
                    numbers));
            }

            JudgeMove($"with {holdUps.Name}", session, played, [(ZoomMovingFirst, ZoomMovingLast)], heldUp: kind != "none", numbers);
            if (from <= ZoomMovingFirst - 60)
            {
                ComparePace($"with {holdUps.Name}", played, from + 15, ZoomMovingFirst - 1, ZoomMovingFirst, ZoomMovingLast);
            }
            _report.Note($"with {holdUps.Name}: {holdUps.Describe()}");
            _report.Check($"with {holdUps.Name}: no failure was reported", session.Events.FailedEvents == 0, string.Join("; ", session.Events.Failures()));
        }
        finally
        {
            holdUps.Rest();
            Close(session);
        }
    }

    /// <summary>Writes the frames a probe read as one line each, with when the playing began and ended and what held the process up.</summary>
    private static void WriteProbedFrames(string path, List<ProbedFrame> frames, long playedAt, long pausedAt, HoldUps? holdUps)
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        var origin = playedAt - Stopwatch.Frequency;
        double Ms(long timestamp) => Stopwatch.GetElapsedTime(origin, timestamp).TotalMilliseconds;
        using var writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(false));
        writer.WriteLine("kind,pass,clip,index,start_ms,position_ms,controller_ms,read_ms,gate_wait_ms,copy_ms,position_after_ms,return_ms,gc_pause_ms,gc_return_ms,truth,name,name_after,overlap,fps,how,knowledge,frame,rule,earlier_serial,earlier_frame");
        writer.WriteLine(string.Create(c, $"resume,1,,,{Ms(playedAt):0.000},0,,,,,,{Ms(playedAt):0.000},,,,,,,"));
        writer.WriteLine(string.Create(c, $"pause,1,,,{Ms(pausedAt):0.000},0,,,,,,{Ms(pausedAt):0.000},,,,,,,"));
        foreach (var stall in holdUps?.Stalls ?? [])
        {
            if (stall.To >= playedAt && stall.From <= pausedAt)
            {
                writer.WriteLine(string.Create(c, $"stall-{holdUps!.Kind},1,,,{Ms(stall.From):0.000},{Stopwatch.GetElapsedTime(stall.From, stall.To).TotalMilliseconds:0.000},,,,,,{Ms(stall.To):0.000},,,,,,,"));
            }
        }

        foreach (var frame in frames)
        {
            var fps = TestMedia.Fps;
            var nameAfter = frame.PositionAfterTicks == 0 ? frame.NameByPosition : (long)Math.Floor((frame.PositionAfterTicks * fps / 10_000_000.0) + 1e-6);
            writer.WriteLine(string.Create(
                c,
                $"copy,1,{(frame.Clip == 0 ? "screen" : "camera")},{frame.Serial},{Ms(frame.StartedAt):0.000},{frame.PositionTicks / 10000.0:0.000},,,{Stopwatch.GetElapsedTime(frame.StartedAt, frame.CopyBeganAt).TotalMilliseconds:0.000},{frame.CopyMilliseconds:0.000},{(frame.PositionAfterTicks == 0 ? frame.PositionTicks : frame.PositionAfterTicks) / 10000.0:0.000},{Ms(frame.CopiedAt):0.000},{frame.CollectorBefore:0.000},{frame.CollectorAfter:0.000},{frame.Truth},{frame.NameByPosition},{nameAfter},1,{fps},{frame.Kind},{frame.Knowledge},{frame.Frame},{frame.Rule},{frame.EarlierSerial},{frame.EarlierFrame}"));
        }
    }

    /// <summary>
    /// How evenly a stretch of frames was drawn: how many never were, and how many came more
    /// than a frame's time later than the frames between them and the one drawn before account for.
    /// </summary>
    private static (int Dropped, int Late, double LongestMilliseconds) Pace(List<SceneFit> scenes, int first, int last)
    {
        var firstDrawn = new SortedDictionary<int, long>();
        foreach (var scene in scenes)
        {
            if (scene.Screen >= first - 1 && scene.Screen <= last)
            {
                firstDrawn.TryAdd(scene.Screen, scene.At);
            }
        }

        var dropped = Enumerable.Range(first, last - first + 1).Count(frame => !firstDrawn.ContainsKey(frame));
        var late = 0;
        double longest = 0;
        KeyValuePair<int, long>? previous = null;
        foreach (var drawn in firstDrawn)
        {
            if (previous is { } earlier && drawn.Key >= first)
            {
                var took = Stopwatch.GetElapsedTime(earlier.Value, drawn.Value).TotalMilliseconds;
                var due = (drawn.Key - earlier.Key) * 1000.0 / Fps;
                late += took - due > 1000.0 / Fps ? 1 : 0;
                longest = Math.Max(longest, took);
            }

            previous = drawn;
        }

        return (dropped, late, longest);
    }

    /// <summary>
    /// Reports how the frames of a move were drawn against the frames before it, where nothing
    /// moves, play by play: whether moving costs frames that standing still does not.
    /// </summary>
    private void ComparePace(string label, List<PlayedScenes> played, int plainFirst, int plainLast, int movingFirst, int movingLast)
    {
        var worse = 0;
        var lines = new List<string>();
        var droppedMore = new Samples();
        var lateMore = new Samples();
        foreach (var play in played)
        {
            var moving = Pace(play.Scenes, movingFirst, movingLast);
            var plain = Pace(play.Scenes, plainFirst, plainLast);
            droppedMore.Add(moving.Dropped - plain.Dropped);
            lateMore.Add(moving.Late - plain.Late);
            var isWorse = moving.Dropped > plain.Dropped + 2 || moving.Late > plain.Late + 2;
            worse += isWorse ? 1 : 0;
            if (isWorse || lines.Count < 3)
            {
                lines.Add($"move {moving.Dropped} never drawn, {moving.Late} late, longest wait {F(moving.LongestMilliseconds, "0")} ms; before it {plain.Dropped}, {plain.Late}, {F(plain.LongestMilliseconds, "0")} ms{(isWorse ? " (MORE THAN TWO MORE)" + play.Dump : string.Empty)}");
            }
        }

        _report.Note($"{label}: the {movingLast - movingFirst + 1} frames of the move against the {plainLast - plainFirst + 1} frames before it, in {played.Count} plays: frames never drawn, the move's more than the others': {droppedMore.Summary("frames")}; frames that came more than a frame's time late: {lateMore.Summary("frames")}; the move was more than two worse in {worse} plays. {string.Join(" | ", lines.Take(8))}");
    }

    /// <summary>What a stretch of playback showed, scene by scene.</summary>
    /// <param name="Scenes">Every scene drawn from the moment playing was asked for until the engine had come to rest after the pause.</param>
    /// <param name="Problem">Why the scenes are not all there, or null.</param>
    /// <param name="Dump">Where the pictures of this play's wrong scenes and the engine's trace were written, or empty.</param>
    /// <param name="PausedAt">When the pause was asked for, as a Stopwatch timestamp.</param>
    private sealed record PlayedScenes(List<SceneFit> Scenes, string? Problem, double Seconds, StudioPreviewDiagnostics Before, StudioPreviewDiagnostics After, string Dump, long PausedAt);

    /// <summary>
    /// Plays from one frame to another and keeps every scene the engine draws on the way, then
    /// reads them: which frame each shows and whether its clips are where the layout of that
    /// frame puts them.
    /// </summary>
    /// <param name="goToStart">Brings the paused preview to <paramref name="from"/>.</param>
    /// <param name="numbers">Takes what every frame the players hand over on the way was taken for, against what its pixels show.</param>
    private PlayedScenes PlayAndRead(Session session, MovingLayout layout, int from, int to, Action goToStart, Action play, Action pause, HoldUps? holdUps, string name, ProbeTotals numbers)
    {
        goToStart();
        session.WaitForIdle();
        using var recorder = new MovingSceneRecorder(layout, from - 2, to + 30, capacity: (to - from) + 90);
        using var probe = new TruthProbe(session.Engine, session.Folder.Screen, session.Folder.Camera, capacity: ((to - from) * 3) + 150);

        // The tool keeps out of the preview's way while it plays: what it will fill is made now,
        // and what was left behind is collected now.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = session.Engine.GetDiagnostics();
        var watch = Stopwatch.StartNew();
        var limit = ((to - from) / (double)Fps) + 6;
        var reached = false;
        long pausedAt;
        long playedAt;
        session.Engine.AfterRender = recorder.OnRender;
        try
        {
            holdUps?.Begin();
            playedAt = Stopwatch.GetTimestamp();
            play();
            while (watch.Elapsed.TotalSeconds < limit)
            {
                if (session.PositionFrame >= to)
                {
                    reached = true;
                    break;
                }

                Thread.Sleep(5);
            }

            pausedAt = Stopwatch.GetTimestamp();
            pause();
            holdUps?.Rest();
            session.WaitForIdle();
        }
        finally
        {
            session.Engine.AfterRender = session.Recorder.OnRender;
        }

        var wrongBefore = numbers.WrongNumbers + numbers.InferredWrong + numbers.StoppedInTheMiddle;
        var frames = probe.Read(numbers);
        if (numbers.WrongNumbers + numbers.InferredWrong + numbers.StoppedInTheMiddle > wrongBefore && Interlocked.Increment(ref _dumps) <= 12)
        {
            // A frame of this play was given a number its pixels do not show, judged or counted
            // apart: the engine's trace and every frame handed over are kept, to say why.
            try
            {
                Directory.CreateDirectory(_failuresDirectory);
                var stem = Path.Combine(_failuresDirectory, $"{name}-{_dumps}-number");
                File.WriteAllLines(stem + "-trace.txt", session.Trace.Lines());
                WriteProbedFrames(stem + "-frames.csv", frames, playedAt, pausedAt, holdUps);
            }
            catch (Exception)
            {
                // Evidence only.
            }
        }

        var seconds = watch.Elapsed.TotalSeconds;
        var after = session.Engine.GetDiagnostics();
        var scenes = recorder.Read(from);
        var problem = recorder.Failure is { } failure ? "the scenes could not be kept: " + failure
            : recorder.Unkept > 0 ? $"{recorder.Unkept} scenes were drawn that there was no room to keep"
            : !reached ? $"playback did not reach frame {to} in {F(limit)} s (Position frame {session.PositionFrame})"
            : null;

        // Pictures of the first scenes that are wrong, and the trace, for a look at them.
        var dump = string.Empty;
        var firstWrong = scenes.FindIndex(s => s.Screen == FrameCode.Unreadable || !s.ScreenFit.Within(MovingLayout.Tolerance));
        if (firstWrong >= 0 && Interlocked.Increment(ref _dumps) <= 12)
        {
            try
            {
                Directory.CreateDirectory(_failuresDirectory);
                var stem = $"{name}-{_dumps}";
                recorder.Save(firstWrong, Path.Combine(_failuresDirectory, $"{stem}-scene-{firstWrong}.png"));
                File.WriteAllLines(Path.Combine(_failuresDirectory, $"{stem}-trace.txt"), session.Trace.Lines());
                dump = $" [the first wrong scene and the trace in {Session.DisplayPath(_failuresDirectory)}\\{stem}-*]";
            }
            catch (Exception ex)
            {
                dump = $" [nothing kept: {ex.Message}]";
            }
        }

        if (_options.Flag("keep-traces"))
        {
            // Asked for: the engine's trace of every play, right or wrong.
            var directory = Path.Combine(_output, "traces");
            Directory.CreateDirectory(directory);
            var stem = Path.Combine(directory, $"{_report.Stamp}-{name}-{Interlocked.Increment(ref _tracesKept)}");
            File.WriteAllLines(stem + ".txt", session.Trace.Lines());

            // And every frame the players handed over, as the investigation of the names writes
            // them, so that the same scripts read both.
            WriteProbedFrames(stem + "-frames.csv", frames, playedAt, pausedAt, holdUps);
        }

        return new PlayedScenes(scenes, problem, seconds, before, after, dump, pausedAt);
    }

    /// <summary>
    /// Judges plays through a stretch in which the layout moves: every scene has to show its
    /// frame with the layout of that frame, on both clips, and the move has to have been drawn.
    /// </summary>
    /// <param name="moves">The frames whose middles lie inside a move, for each move. Every play goes through one of them, and they are of one length.</param>
    /// <param name="heldUp">The process was held up: fewer frames of the move have to have been drawn.</param>
    /// <param name="numbers">What the frames the players handed over during the plays were taken for.</param>
    private void JudgeMove(string label, Session session, List<PlayedScenes> played, (int First, int Last)[] moves, bool heldUp, ProbeTotals numbers)
    {
        var wrong = new List<string>();
        var cameraWrong = new List<string>();
        var problems = played.Where(p => p.Problem is not null).Select(p => p.Problem!).ToList();
        var fits = new Samples();
        var cameraFits = new Samples();
        var stills = new Samples();
        var moving = 0;
        var total = 0;
        var unreadable = 0;
        var backwards = 0;
        var apart = 0;
        var furthestApart = 0;
        var fewest = int.MaxValue;
        var afterAStop = 0;

        // The players are their own masters and can be a frame apart. While the process is held
        // up and the layout moves, a screen frame of which it cannot be told which frame it is
        // is not shown, and the camera goes on without it for as long: half a second at the most.
        var mostApart = heldUp ? Fps / 2 : 1;
        foreach (var play in played)
        {
            var drawn = new HashSet<int>();
            var previous = FrameCode.Unreadable;
            long previousAt = 0;
            foreach (var scene in play.Scenes)
            {
                total++;
                var sinceLast = previousAt == 0 ? 0 : Stopwatch.GetElapsedTime(previousAt, scene.At).TotalMilliseconds;
                if (previousAt != 0)
                {
                    stills.Add(sinceLast);
                }

                previousAt = scene.At;
                if (scene.Screen == FrameCode.Unreadable)
                {
                    unreadable++;
                    wrong.Add($"a scene whose frame could not be read, drawn {F(sinceLast, "0")} ms after frame {previous}{play.Dump}");
                    continue;
                }

                // When the clock is stopped the scene goes back to the last frame that has a
                // number, if it showed one without: that is not playing backwards.
                backwards += previous != FrameCode.Unreadable && scene.Screen < previous && scene.At < play.PausedAt ? 1 : 0;
                previous = scene.Screen;
                if (moves.Any(move => scene.Screen >= move.First && scene.Screen <= move.Last))
                {
                    moving++;
                    drawn.Add(scene.Screen);
                }

                if (scene.ScreenFit.Within(MovingLayout.Tolerance))
                {
                    fits.Add(scene.ScreenFit.Worst);
                }
                else if (numbers.LimitFrames.Contains((0, scene.Screen)))
                {
                    // A frame the engine took for another: one a player put in the place of
                    // the one it had announced when the whole process was stopped, or one whose
                    // number rested on an inference. It is drawn with the layout of the frame it
                    // was taken for. The check of the numbers counts both, and bounds the second.
                    afterAStop++;
                }
                else
                {
                    wrong.Add($"frame {scene.Screen}: an edge of the screen's strip is {scene.ScreenFit} from where the layout of that frame puts it; {(scene.FitsFrame is { } other ? $"the edges fit the layout of frame {other}" : "the edges fit the layout of none of the frames around it")}; drawn {F(sinceLast, "0")} ms after the scene before it{play.Dump}");
                }

                if (!scene.CameraExpected)
                {
                    continue;
                }

                var wanted = session.Folder.ExpectedCamera(scene.Screen, session.Project);
                if (scene.Camera == FrameCode.Unreadable || !scene.CameraFit.Within(MovingLayout.Tolerance) || Math.Abs(scene.Camera - wanted) > mostApart)
                {
                    cameraWrong.Add($"screen frame {scene.Screen}: camera {(scene.Camera == FrameCode.Unreadable ? "not readable where the layout of that frame puts it" : $"frame {scene.Camera}, its edges {scene.CameraFit} from their places")}; the camera frame that goes with it is {wanted}{play.Dump}");
                }
                else
                {
                    cameraFits.Add(scene.CameraFit.Worst);
                    apart += scene.Camera != wanted ? 1 : 0;
                    furthestApart = Math.Max(furthestApart, Math.Abs(scene.Camera - wanted));
                }
            }

            fewest = Math.Min(fewest, drawn.Count);
        }

        // A hold-up of 60 ms and the doubt that follows it take up to seven frames in a row.
        var frames = moves[0].Last - moves[0].First + 1;
        var needed = (int)Math.Ceiling(frames * (!heldUp ? 0.9 : frames >= 60 ? 0.6 : 0.3));
        _report.Check(
            $"{label}: every scene drawn shows its frame with the layout of that same frame, to within {F(MovingLayout.Tolerance)} px, and none goes back ({total} scenes in {played.Count} plays, {moving} of them while the layout moved)",
            wrong.Count == 0 && problems.Count == 0 && backwards == 0 && total > 0,
            (wrong.Count == 0 && problems.Count == 0 && backwards == 0
                ? $"the edge furthest from its place: {fits.Summary("px")}"
                : $"{wrong.Count} of {total} scenes wrong, {unreadable} of them unreadable; {backwards} went back{(problems.Count == 0 ? string.Empty : "; " + string.Join("; ", problems))}{(wrong.Count == 0 ? string.Empty : "; first: " + string.Join(" | ", wrong.Take(3)))}")
            + (afterAStop == 0 ? string.Empty : $"; {afterAStop} more scenes showed a frame with the layout of another, the frame having been taken for that one by an inference or after a stop of the whole process: counted and bounded by the check of the numbers, not judged here"));
        if (session.Folder.Camera is not null)
        {
            _report.Check(
                $"{label}: the camera's picture is where the layout of the screen's frame puts it, and never more than {(mostApart == 1 ? "one frame" : mostApart + " frames")} from the frame that goes with the screen's",
                cameraWrong.Count == 0 && cameraFits.Count > 0,
                cameraWrong.Count == 0 ? $"the edge furthest from its place: {cameraFits.Summary("px")}; not on the frame that goes with the screen's in {apart} of {cameraFits.Count} scenes, {furthestApart} frames from it at the most" : $"{cameraWrong.Count} wrong; first: {string.Join(" | ", cameraWrong.Take(3))}");
        }
        _report.Check(
            $"{label}: the move was drawn: at least {needed} of the {frames} frames of a move in every play",
            fewest >= needed,
            $"fewest {(fewest == int.MaxValue ? 0 : fewest)}");
        _report.Check(
            $"{label}: {NumbersHold}",
            numbers.Hold && numbers.Lost == 0 && numbers.Numbered > 0,
            numbers.Describe() + (numbers.Wrong.Count == 0 ? string.Empty : "; first: " + string.Join(" | ", numbers.Wrong.Take(3))));
        _report.Note($"{label}: between two scenes {stills.Summary()}; {string.Join("; ", played.Select((p, index) => $"play {index + 1} ({F(p.Seconds)} s): {Naming(p.Before, p.After)}"))}");
    }
}
