using System.Collections.Concurrent;
using System.Diagnostics;
using TinyClips.Core.Studio.Preview;
using TinyClips.Tools.StudioPreviewCheck.Media;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

// What Position reports once Seek() has returned: the frame asked for, and never again one from before the call.
internal sealed partial class HeadlessChecks
{
    // How far playback may get in the short time a repetition lets it run.
    private const int PlayedSpan = 90;

    private enum Parked
    {
        /// <summary>The seek to the last frame has landed and the engine has nothing left to do.</summary>
        AtRest,

        /// <summary>The scene of the last frame has just been drawn: the news of that landing is on its way.</summary>
        AtTheLanding,

        /// <summary>The seek to the last frame was made a moment ago and is still on its way.</summary>
        InFlight,
    }

    private void PositionAfterSeek()
    {
        _report.Section("Position after Seek(): the frame asked for from the moment the call returns, never one from before it");
        _report.Line("Position is read by a PositionChanged handler, by a second thread that handles each event a little later (as a UI thread does), and by a thread that reads without pause. A read counts when it began after Seek() had returned.");
        var session = OpenSession(TestMedia.Camera);
        var before = session.Engine.GetDiagnostics();

        using (var watch = new PositionWatch(session.Engine))
        {
            SeekAndPlayFromTheLastFrame(session, watch);
            SeekAndPlayWhenPlaybackEnds(session, watch);
            SeekDuringAPausedSeek(session, watch);
            SeekDuringPlayback(session, watch);
            EditorAtTheEndOfItsRange(session, watch);
        }

        var after = session.Engine.GetDiagnostics();
        _report.Note($"frames of the screen clip that arrived as playback but had set out before the clock was started, and were not taken for the position: {after.FramesFromBeforeStart - before.FramesFromBeforeStart}; most VideoFrameAvailable callbacks of one player under way at once: screen {after.MostDeliveriesAtOnce[0]}, camera {after.MostDeliveriesAtOnce[1]} (of {after.CallbacksStarted[0]} and {after.CallbacksStarted[1]})");
        _report.Check("no failure was reported", session.Events.FailedEvents == 0, string.Join("; ", session.Events.Failures()));
        Close(session);
    }

    // ---------------------------------------------------------------------------------------
    // Parked on the last frame, then Seek(start) and Play()
    // ---------------------------------------------------------------------------------------

    private void SeekAndPlayFromTheLastFrame(Session session, PositionWatch watch)
    {
        var last = session.Folder.FrameCount - 1;
        var repetitions = _quick ? 10 : 40;
        var waits = new[] { 0, 1, 2, 4, 8, 15, 25, 40, 60, 90 };
        var notPlayingAfterPlay = 0;
        var playingAfterPause = 0;
        var calls = 0;
        foreach (var moment in new[] { Parked.AtRest, Parked.AtTheLanding, Parked.InFlight })
        {
            var wrong = new List<string>();
            var judged = new ReadCount();
            var parkedDrawnAfter = 0;
            var firstFrame = new Samples();
            var unansweredBefore = session.Engine.GetDiagnostics().DetoursUnanswered;
            for (var repetition = 0; repetition < repetitions; repetition++)
            {
                // Two starts far apart, so that a frame of the repetition before cannot pass for one of this.
                var start = repetition % 2 == 0 ? 0 : 150;
                var parked = Park(session, moment, last, waits[repetition % waits.Length]);
                session.Recorder.Drain();
                watch.Begin();
                session.Engine.Seek(TestFolder.TimeOf(start, 0.5));
                var returned = Stopwatch.GetTimestamp();
                var immediate = session.PositionFrame;
                session.Engine.Play();
                notPlayingAfterPlay += session.Engine.IsPlaying ? 0 : 1;
                var reached = WaitForFrames(session, start, 6);
                session.Engine.Pause();
                playingAfterPause += session.Engine.IsPlaying ? 1 : 0;
                calls++;
                var idle = session.WaitForIdle();
                var reads = watch.End();
                var scenes = session.Recorder.Drain().Where(c => c.At >= returned && c.Screen != FrameCode.Unreadable).ToList();

                var problem = !parked ? "the last frame was not reached before the calls"
                    : immediate != start ? $"Position was frame {immediate} when Seek() returned"
                    : !reached || !idle ? $"playback did not get going (Position frame {session.PositionFrame}, idle {idle})"
                    : reads.Problem(returned, long.MaxValue, start, start + PlayedSpan, ordered: true) ?? PlayedFrom(scenes, start, last);
                judged.Add(reads, returned, long.MaxValue);
                parkedDrawnAfter += scenes.Any(c => c.Screen == last) ? 1 : 0;
                if (problem is not null)
                {
                    wrong.Add($"repetition {repetition} (start {start}): {problem}{Dump(session, "position")}");
                }
                else
                {
                    firstFrame.Add(Stopwatch.GetElapsedTime(returned, scenes.First(c => c.Screen == start).At).TotalMilliseconds);
                }
            }

            var when = moment switch
            {
                Parked.AtRest => "at rest on the last frame",
                Parked.AtTheLanding => "the moment the last frame's scene is drawn",
                _ => "0 to 90 ms after a Seek to the last frame, which is still on its way",
            };
            _report.Check(
                $"Seek(start) then Play(), {when}: Position is the start when Seek() returns, no reader reads an older frame afterwards, and what is read never goes back ({repetitions} repetitions)",
                wrong.Count == 0,
                wrong.Count == 0 ? judged.ToString() : $"{wrong.Count} wrong; first: {string.Join(" | ", wrong.Take(3))}");
            var unanswered = session.Engine.GetDiagnostics().DetoursUnanswered - unansweredBefore;
            _report.Note($"{when}: the last frame's picture was still drawn after the calls in {parkedDrawnAfter} of {repetitions}; Seek() returned to the first frame of the new position drawn: {firstFrame.Summary()}; the screen's player handed over no frame for the seek that takes it off its last frame in {unanswered} of {repetitions}, which costs 0.4 s");
        }

        _report.Check(
            $"IsPlaying is what was asked for: true when Play() returns, while the seek before it is still on its way, and false when Pause() returns ({calls} times each)",
            notPlayingAfterPlay == 0 && playingAfterPause == 0,
            $"false after Play() {notPlayingAfterPlay} times, true after Pause() {playingAfterPause} times");
    }

    /// <summary>Takes the preview to the last frame, as far as <paramref name="moment"/> says. False when it did not get there.</summary>
    private static bool Park(Session session, Parked moment, int last, int waitMilliseconds)
    {
        switch (moment)
        {
            case Parked.AtRest:
                return session.SeekTo(last);

            case Parked.AtTheLanding:
            {
                var camera = session.ExpectedCamera(last);
                using var landed = session.Recorder.Expect(c => c.Screen == last && c.Camera == camera);
                try
                {
                    session.Engine.Seek(TestFolder.TimeOf(last, 0.5));
                    return landed.Wait(TimeSpan.FromSeconds(6));
                }
                finally
                {
                    session.Recorder.Forget(landed);
                }
            }

            default:
                session.Engine.Seek(TestFolder.TimeOf(last, 0.5));
                if (waitMilliseconds > 0)
                {
                    Thread.Sleep(waitMilliseconds);
                }

                return true;
        }
    }

    /// <summary>
    /// What is wrong with the scenes drawn after a Seek(start) and Play(), or null: the picture
    /// goes to the start and plays on from it in order. Until it does, what was there before may
    /// still be drawn (a seek to the parked frame that lands late, a frame that was copied just
    /// before a pause): frames from <paramref name="oldFrom"/> on, which lie after the frames
    /// playback gets to.
    /// </summary>
    private static string? PlayedFrom(List<Composite> scenes, int start, int oldFrom)
    {
        var drawn = $" [drawn: {string.Join(' ', scenes.Take(14).Select(c => c.Screen))}]";
        var first = scenes.FindIndex(c => c.Screen == start);
        if (first < 0 || scenes.Count - first < 3)
        {
            return $"{(first < 0 ? "the start frame was not drawn" : $"only {scenes.Count - first} scenes were drawn from the start frame on")}{drawn}";
        }

        for (var index = 0; index < first; index++)
        {
            if (scenes[index].Screen < oldFrom)
            {
                return $"frame {scenes[index].Screen} was drawn before the start frame{drawn}";
            }
        }

        for (var index = first + 1; index < scenes.Count; index++)
        {
            if (scenes[index].Screen < scenes[index - 1].Screen || scenes[index].Screen > start + PlayedSpan)
            {
                return $"frame {scenes[index].Screen} was drawn after frame {scenes[index - 1].Screen}{drawn}";
            }
        }

        return null;
    }

    // ---------------------------------------------------------------------------------------
    // Playback runs into the end of the recording, then Seek(start) and Play()
    // ---------------------------------------------------------------------------------------

    private void SeekAndPlayWhenPlaybackEnds(Session session, PositionWatch watch)
    {
        // How long after hearing that playback stopped the calls are made. 0 is from the
        // IsPlayingChanged handler itself, on the engine's event thread, while the players are
        // still being brought to rest on the last frame.
        var waits = new[] { 0, 0, 5, 20, 60, 150 };
        var last = session.Folder.FrameCount - 1;
        var loops = _quick ? 6 : 24;
        var wrong = new List<string>();
        var judged = new ReadCount();
        var firstFrame = new Samples();
        var unansweredBefore = session.Engine.GetDiagnostics().DetoursUnanswered;
        for (var loop = 0; loop < loops; loop++)
        {
            var start = loop % 2 == 0 ? 0 : 150;
            var wait = waits[loop % waits.Length];
            long returned = 0;
            var immediate = -1;
            var readAtStop = -1;
            var called = 0;
            using var stopped = new ManualResetEventSlim(false);

            void SeekAndPlay()
            {
                if (Interlocked.Exchange(ref called, 1) != 0)
                {
                    return;
                }

                session.Engine.Seek(TestFolder.TimeOf(start, 0.5));
                Volatile.Write(ref returned, Stopwatch.GetTimestamp());
                Volatile.Write(ref immediate, session.PositionFrame);
                session.Engine.Play();
            }

            void OnIsPlayingChanged(object? sender, EventArgs e)
            {
                if (session.Engine.IsPlaying || stopped.IsSet)
                {
                    return;
                }

                // Whoever hears that playback has stopped finds the position at the end.
                Volatile.Write(ref readAtStop, session.PositionFrame);
                if (wait == 0)
                {
                    SeekAndPlay();
                }

                stopped.Set();
            }

            // A third of a second before the end, and into it.
            session.SeekTo(last - 10);
            session.Recorder.Drain();
            session.Engine.IsPlayingChanged += OnIsPlayingChanged;
            bool ended;
            try
            {
                watch.Begin();
                session.Engine.Play();
                ended = stopped.Wait(TimeSpan.FromSeconds(6));
                if (ended && wait > 0)
                {
                    Thread.Sleep(wait);
                    SeekAndPlay();
                }
            }
            finally
            {
                // Before the pause below, which this handler would take for the end.
                Interlocked.Exchange(ref called, 1);
                session.Engine.IsPlayingChanged -= OnIsPlayingChanged;
            }

            var reached = ended && WaitForFrames(session, start, 6);
            session.Engine.Pause();
            var idle = session.WaitForIdle();
            var reads = watch.End();
            var since = Volatile.Read(ref returned);
            var scenes = session.Recorder.Drain().Where(c => c.At >= since && c.Screen != FrameCode.Unreadable).ToList();
            var problem = !ended ? "playback did not stop by itself at the end"
                : Volatile.Read(ref readAtStop) != last ? $"the IsPlayingChanged handler read frame {Volatile.Read(ref readAtStop)} from Position, and the last frame is {last}"
                : Volatile.Read(ref immediate) != start ? $"Position was frame {Volatile.Read(ref immediate)} when Seek() returned"
                : !reached || !idle ? $"playback did not get going again (Position frame {session.PositionFrame}, idle {idle})"
                : reads.Problem(since, long.MaxValue, start, start + PlayedSpan, ordered: true) ?? PlayedFrom(scenes, start, last - 10);
            judged.Add(reads, since, long.MaxValue);
            if (problem is not null)
            {
                wrong.Add($"loop {loop} (start {start}, {(wait == 0 ? "called from the IsPlayingChanged handler" : $"called {wait} ms after it")}): {problem}{Dump(session, "position")}");
            }
            else
            {
                firstFrame.Add(Stopwatch.GetElapsedTime(since, scenes.First(c => c.Screen == start).At).TotalMilliseconds);
            }
        }

        _report.Check(
            $"playback runs into the end of the recording and stops: the IsPlayingChanged handler reads the last frame from Position; Seek(start) then Play() from that handler, or up to 150 ms later: Position is the start when Seek() returns, no reader reads an older frame afterwards, and what is read never goes back ({loops} times)",
            wrong.Count == 0,
            wrong.Count == 0 ? judged.ToString() : $"{wrong.Count} wrong; first: {string.Join(" | ", wrong.Take(3))}");
        var unanswered = session.Engine.GetDiagnostics().DetoursUnanswered - unansweredBefore;
        _report.Note($"after the end of the recording: Seek() returned to the first frame of the new position drawn: {firstFrame.Summary()}; a player handed over no frame for a seek that takes it off the end of its stream {unanswered} times in these {loops} loops, which costs 0.4 s each");
    }

    // ---------------------------------------------------------------------------------------
    // A second paused Seek while the first is on its way
    // ---------------------------------------------------------------------------------------

    private void SeekDuringAPausedSeek(Session session, PositionWatch watch)
    {
        var repetitions = _quick ? 20 : 100;
        var waits = new[] { 0, 1, 2, 4, 8, 15, 25, 40, 60, 90 };
        var last = session.Folder.FrameCount - 1;
        var wrong = new List<string>();
        var judged = new ReadCount();
        var firstDrawnAfter = 0;
        var stuck = 0;
        session.SeekTo(100);
        for (var repetition = 0; repetition < repetitions; repetition++)
        {
            var current = session.PositionFrame;
            int first;
            do
            {
                first = _random.Next(0, last + 1);
            }
            while (Math.Abs(first - current) < 5);

            int second;
            do
            {
                second = _random.Next(0, last + 1);
            }
            while (Math.Abs(second - first) < 5 || Math.Abs(second - current) < 5);

            session.Recorder.Drain();
            watch.Begin();
            session.Engine.Seek(TestFolder.TimeOf(first, 0.5));
            var wait = waits[repetition % waits.Length];
            if (wait > 0)
            {
                Thread.Sleep(wait);
            }

            session.Engine.Seek(TestFolder.TimeOf(second, 0.5));
            var returned = Stopwatch.GetTimestamp();
            var immediate = session.PositionFrame;
            var idle = session.WaitForIdle();
            var reads = watch.End();
            var shown = session.ReadShown();
            var camera = session.ExpectedCamera(second);
            var scenes = session.Recorder.Drain().Where(c => c.At >= returned).ToList();
            firstDrawnAfter += scenes.Any(c => c.Screen == first) ? 1 : 0;
            stuck += session.Engine.GetDiagnostics().PositionPending ? 1 : 0;
            judged.Add(reads, returned, long.MaxValue);

            var problem = immediate != second ? $"Position was frame {immediate} when the second Seek() returned"
                : !idle ? "the engine did not come to rest"
                : shown.Screen != second || shown.Camera != camera ? $"the picture ended on {shown}"
                : session.PositionFrame != second ? $"Position ended on frame {session.PositionFrame}"
                : reads.Problem(returned, long.MaxValue, second, second, ordered: false);
            if (problem is not null)
            {
                wrong.Add($"repetition {repetition} ({current} -> Seek({first}), {wait} ms, Seek({second})): {problem}{Dump(session, "position")}");
            }
        }

        _report.Check(
            $"paused, a second Seek() 0 to 90 ms after a first that is still on its way: Position is the second frame when the call returns and every reader reads only that frame afterwards, through the first seek's landing and its own ({repetitions} repetitions)",
            wrong.Count == 0 && stuck == 0,
            wrong.Count == 0 ? $"{judged}{(stuck == 0 ? string.Empty : $"; the position was still marked as not reached when the engine had come to rest, {stuck} times")}" : $"{wrong.Count} wrong; first: {string.Join(" | ", wrong.Take(3))}");
        _report.Note($"the first seek's picture was drawn after the second call had returned, while Position named the second frame, in {firstDrawnAfter} of {repetitions}");
    }

    // ---------------------------------------------------------------------------------------
    // Seek while playing
    // ---------------------------------------------------------------------------------------

    private void SeekDuringPlayback(Session session, PositionWatch watch)
    {
        // Each far from where playback is when it is asked for.
        var round = new[] { 200, 40, 300, 20, 180, 60, 280, 100 };
        var seeks = _quick ? 8 : 32;
        var wrong = new List<string>();
        var judged = new ReadCount();
        session.SeekTo(100);
        session.Engine.Play();
        Thread.Sleep(300);
        for (var seek = 0; seek < seeks; seek++)
        {
            var to = round[seek % round.Length];
            var from = session.PositionFrame;
            watch.Begin();
            session.Engine.Seek(TestFolder.TimeOf(to, 0.5));
            var returned = Stopwatch.GetTimestamp();
            var immediate = session.PositionFrame;
            var reached = WaitForFrames(session, to, 8);
            var playing = session.Engine.IsPlaying;
            var reads = watch.End();
            judged.Add(reads, returned, long.MaxValue);
            var problem = immediate != to ? $"Position was frame {immediate} when Seek() returned"
                : !reached || !playing ? $"playback did not carry on from there (Position frame {session.PositionFrame}, IsPlaying {playing})"
                : reads.Problem(returned, long.MaxValue, to, to + PlayedSpan, ordered: true);
            if (problem is not null)
            {
                wrong.Add($"seek {seek} (playing at {from}, Seek({to})): {problem}{Dump(session, "position")}");
            }
        }

        session.Engine.Pause();
        session.WaitForIdle();
        _report.Check(
            $"Seek() while playing: Position is the frame asked for when the call returns, then the frames played on from it, and never a frame of the playback it interrupted ({seeks} seeks)",
            wrong.Count == 0,
            wrong.Count == 0 ? judged.ToString() : $"{wrong.Count} wrong; first: {string.Join(" | ", wrong.Take(3))}");
    }

    // ---------------------------------------------------------------------------------------
    // The editor at the end of its kept range
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// What the editor's session does with the preview, on one thread as the editor does it on its
    /// UI thread: it reads Position when a PositionChanged reaches it, and while it takes itself to
    /// be playing, a position at the end of the kept range makes it pause and park there. Space
    /// with the playhead at the end seeks to the start and plays.
    /// </summary>
    private sealed class EditorSession(StudioPreviewEngine engine, int trimStart, int trimEnd) : IDisposable
    {
        private bool _playing;

        /// <summary>Raised on the session's thread when it has paused at the end of the range.</summary>
        public ManualResetEventSlim Parked { get; } = new(false);

        /// <summary>When Space had made its calls, and when the session then began to pause at the end.</summary>
        public long SpaceReturnedAt { get; private set; }

        public long PauseBeganAt { get; private set; }

        /// <summary>The frame whose being read made the session pause.</summary>
        public int PausedOn { get; private set; }

        public void OnPosition(int frame)
        {
            if (!_playing || frame < trimEnd)
            {
                return;
            }

            PauseBeganAt = Stopwatch.GetTimestamp();
            PausedOn = frame;
            engine.Pause();
            engine.Seek(TestFolder.TimeOf(trimEnd, 0.5));
            _playing = false;
            Parked.Set();
        }

        public void Space()
        {
            Parked.Reset();
            engine.Seek(TestFolder.TimeOf(trimStart, 0.5));
            engine.Play();
            _playing = true;
            SpaceReturnedAt = Stopwatch.GetTimestamp();
        }

        public void Dispose() => Parked.Dispose();
    }

    private void EditorAtTheEndOfItsRange(Session session, PositionWatch watch)
    {
        const int trimStart = 60;
        const int trimEnd = 78;
        var repetitions = _quick ? 10 : 40;
        var gaps = new[] { 0, 2, 5, 10, 20, 35, 50, 70, 100, 150 };
        var rangeMilliseconds = (trimEnd - trimStart) * 1000.0 / Fps;
        var wrong = new List<string>();
        var judged = new ReadCount();
        var played = new Samples();
        using var editor = new EditorSession(session.Engine, trimStart, trimEnd);
        session.SeekTo(trimEnd);
        watch.OnLateRead = editor.OnPosition;
        try
        {
            for (var repetition = 0; repetition < repetitions; repetition++)
            {
                // Space this long after the session parked the playhead at the end. The first time it rests there.
                var gap = gaps[repetition % gaps.Length];
                if (repetition > 0 && gap > 0)
                {
                    Thread.Sleep(gap);
                }

                session.Recorder.Drain();
                watch.Begin();
                watch.OnLateThread(editor.Space);
                var parked = editor.Parked.Wait(TimeSpan.FromSeconds(6));
                var reads = watch.End();
                var scenes = session.Recorder.Drain().Where(c => c.At >= editor.SpaceReturnedAt && c.At <= editor.PauseBeganAt && c.Screen != FrameCode.Unreadable).ToList();
                var tookMilliseconds = Stopwatch.GetElapsedTime(editor.SpaceReturnedAt, editor.PauseBeganAt).TotalMilliseconds;
                judged.Add(reads, editor.SpaceReturnedAt, editor.PauseBeganAt);

                // Sent back at once is what a stale position does: the session reads the end frame
                // it has just left and pauses, and Space seems to do nothing.
                var drawnOfRange = scenes.Select(c => c.Screen).Where(f => f >= trimStart && f < trimEnd).Distinct().Count();
                var problem = !parked ? $"playback never reached the end of the range (Position frame {session.PositionFrame})"
                    : tookMilliseconds < 0.75 * rangeMilliseconds ? $"the session paused at the end {F(tookMilliseconds, "0")} ms after Space, having read frame {editor.PausedOn}; the range takes {F(rangeMilliseconds, "0")} ms to play"
                    : drawnOfRange < 0.6 * (trimEnd - trimStart) ? $"only {drawnOfRange} of the range's {trimEnd - trimStart} frames were drawn before the session paused"
                    : reads.Problem(editor.SpaceReturnedAt, editor.PauseBeganAt, trimStart, trimEnd + 8, ordered: true) ?? PlayedFrom(scenes, trimStart, trimEnd);
                if (problem is not null)
                {
                    wrong.Add($"repetition {repetition} (Space {(repetition == 0 ? "at rest" : gap + " ms after parking")}): {problem}{Dump(session, "position")}");
                }
                else
                {
                    played.Add(tookMilliseconds);
                }
            }
        }
        finally
        {
            watch.OnLateRead = null;
        }

        session.WaitForIdle();
        var shown = session.ReadShown();
        _report.Check(
            $"an editor that parks at the end of its kept range with Pause() and Seek(end), and on Space calls Seek(start) and Play(), plays the range again every time: it is never sent back by reading the end frame once more ({repetitions} times, Space 0 to 150 ms after it parked)",
            wrong.Count == 0 && shown.Screen == trimEnd && session.PositionFrame == trimEnd,
            wrong.Count == 0 ? $"{judged}; at the end the picture is on {shown}, Position on frame {session.PositionFrame}" : $"{wrong.Count} wrong; first: {string.Join(" | ", wrong.Take(3))}");
        _report.Note($"Space to the session pausing at the end of an {F(rangeMilliseconds, "0")} ms range: {played.Summary()}");
    }

    /// <summary>How many reads of Position were judged, by who made them.</summary>
    private sealed class ReadCount
    {
        private long _handler;
        private long _late;
        private long _polled;

        public void Add(PositionReads reads, long from, long until)
        {
            _handler += reads.Handler.Count(r => r.Before >= from && r.Before < until);
            _late += reads.Late.Count(r => r.Before >= from && r.Before < until);
            _polled += reads.Polled.Where(r => r.Last >= from && r.First < until).Sum(r => r.Count);
        }

        public override string ToString() =>
            $"reads judged: {_handler} in a PositionChanged handler, {_late} on a second thread a little later, {_polled / 1e6:0.0} million by a thread reading without pause";
    }
}

/// <summary>One read of the engine's position.</summary>
/// <param name="Before">Stopwatch timestamp taken before the read began.</param>
internal readonly record struct PositionRead(long Before, int Frame);

/// <summary>Consecutive reads by the polling thread that returned the same frame.</summary>
/// <param name="First">Timestamp taken before the first of them began.</param>
/// <param name="Last">Timestamp taken before the last of them began.</param>
internal readonly record struct PolledRun(int Frame, long First, long Last, long Count);

/// <summary>Every read of the position between <see cref="PositionWatch.Begin"/> and <see cref="PositionWatch.End"/>.</summary>
internal sealed record PositionReads(List<PositionRead> Handler, List<PositionRead> Late, List<PolledRun> Polled)
{
    /// <summary>
    /// What is wrong with the reads that began in a span of time, or null. Each reader's reads are
    /// judged in the order that reader made them; reads of different threads have no order.
    /// </summary>
    /// <param name="ordered">The frames a reader reads must not go back.</param>
    public string? Problem(long from, long until, int lowest, int highest, bool ordered)
    {
        var readers = new (string Name, List<int> Frames)[]
        {
            ("a PositionChanged handler", Handler.Where(r => r.Before >= from && r.Before < until).Select(r => r.Frame).ToList()),
            ("a thread that handles the event a little later", Late.Where(r => r.Before >= from && r.Before < until).Select(r => r.Frame).ToList()),
            ("a thread reading without pause", Polled.Where(r => r.Last >= from && r.First < until).Select(r => r.Frame).ToList()),
        };
        foreach (var (name, frames) in readers)
        {
            for (var index = 0; index < frames.Count; index++)
            {
                if (frames[index] < lowest || frames[index] > highest)
                {
                    return $"{name} read frame {frames[index]}, outside {lowest}..{highest} [it read: {string.Join(' ', frames.Take(14))}]";
                }

                if (ordered && index > 0 && frames[index] < frames[index - 1])
                {
                    return $"{name} read frame {frames[index]} after frame {frames[index - 1]} [it read: {string.Join(' ', frames.Take(14))}]";
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Reads an engine's position the three ways a caller can: in a <c>PositionChanged</c> handler on
/// the engine's event thread; on a thread of its own that takes each event up a little later, as
/// a UI thread does with what was posted to it; and on a thread that reads without pause, which
/// sees every value the position takes.
/// </summary>
internal sealed class PositionWatch : IDisposable
{
    // How long the second thread is busy with something else before it gets to an event, in turn.
    private static readonly int[] Lags = [0, 0, 1, 0, 3, 0, 0, 8, 0, 2];

    private readonly StudioPreviewEngine _engine;
    private readonly object _sync = new();
    private readonly BlockingCollection<Action> _posted = [];
    private readonly ManualResetEventSlim _pollWanted = new(false);
    private readonly ManualResetEventSlim _pollStopped = new(true);
    private readonly Thread _lateThread;
    private readonly Thread _pollThread;
    private List<PositionRead> _handler = [];
    private List<PositionRead> _late = [];
    private List<PolledRun> _polled = [];
    private volatile Action<int>? _onLateRead;
    private volatile bool _polling;
    private volatile bool _disposed;
    private int _lag;

    public PositionWatch(StudioPreviewEngine engine)
    {
        _engine = engine;
        _lateThread = new Thread(LateLoop) { IsBackground = true, Name = "StudioPreviewCheck.LateReader" };
        _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "StudioPreviewCheck.Poller" };
        _lateThread.Start();
        _pollThread.Start();
        engine.PositionChanged += OnPositionChanged;
    }

    /// <summary>Called on the second thread with the frame it has just read for an event.</summary>
    public Action<int>? OnLateRead
    {
        get => _onLateRead;
        set => _onLateRead = value;
    }

    /// <summary>Forgets what was read and starts the thread that reads without pause.</summary>
    public void Begin()
    {
        lock (_sync)
        {
            _handler = [];
            _late = [];
            _polled = [];
        }

        _pollStopped.Reset();
        _polling = true;
        _pollWanted.Set();
    }

    /// <summary>Stops the thread that reads without pause and returns everything read since <see cref="Begin"/>.</summary>
    public PositionReads End()
    {
        _polling = false;
        _pollStopped.Wait();
        lock (_sync)
        {
            return new PositionReads([.. _handler], [.. _late], [.. _polled]);
        }
    }

    /// <summary>Runs something on the second thread, after whatever it has in hand, and waits for it.</summary>
    public void OnLateThread(Action action)
    {
        using var done = new ManualResetEventSlim(false);
        _posted.Add(() =>
        {
            try
            {
                action();
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait();
    }

    public void Dispose()
    {
        _engine.PositionChanged -= OnPositionChanged;
        _disposed = true;
        _polling = false;
        _pollWanted.Set();
        _pollThread.Join();
        _posted.CompleteAdding();
        _lateThread.Join();
        _posted.Dispose();
        _pollWanted.Dispose();
        _pollStopped.Dispose();
    }

    private int Frame() => (int)Math.Round(_engine.Position * TestMedia.Fps);

    private void OnPositionChanged(object? sender, EventArgs e)
    {
        var before = Stopwatch.GetTimestamp();
        var frame = Frame();
        lock (_sync)
        {
            _handler.Add(new PositionRead(before, frame));
        }

        try
        {
            _posted.Add(ReadLate);
        }
        catch (InvalidOperationException)
        {
            // The watch was disposed while this event was on its way.
        }
    }

    private void ReadLate()
    {
        var lag = Lags[_lag++ % Lags.Length];
        if (lag > 0)
        {
            Thread.Sleep(lag);
        }

        var before = Stopwatch.GetTimestamp();
        var frame = Frame();
        lock (_sync)
        {
            _late.Add(new PositionRead(before, frame));
        }

        _onLateRead?.Invoke(frame);
    }

    private void LateLoop()
    {
        foreach (var action in _posted.GetConsumingEnumerable())
        {
            action();
        }
    }

    private void PollLoop()
    {
        while (true)
        {
            while (!_pollWanted.Wait(50))
            {
                if (_disposed)
                {
                    return;
                }
            }

            if (_disposed)
            {
                return;
            }

            var value = int.MinValue;
            long first = 0;
            long last = 0;
            long count = 0;
            var reads = 0;
            while (_polling)
            {
                var before = Stopwatch.GetTimestamp();
                var frame = Frame();
                if (frame != value)
                {
                    Keep(value, first, last, count);
                    value = frame;
                    first = before;
                    count = 0;
                }

                last = before;
                count++;

                // Leave the processor to whoever else wants it now and then; the machine is shared.
                if ((++reads & 0x3F) == 0)
                {
                    Thread.Yield();
                }
            }

            Keep(value, first, last, count);
            _pollWanted.Reset();
            _pollStopped.Set();
        }
    }

    private void Keep(int frame, long first, long last, long count)
    {
        if (count == 0)
        {
            return;
        }

        lock (_sync)
        {
            _polled.Add(new PolledRun(frame, first, last, count));
        }
    }
}
