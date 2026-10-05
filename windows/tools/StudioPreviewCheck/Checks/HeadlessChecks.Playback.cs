using System.Diagnostics;
using TinyClips.Tools.StudioPreviewCheck.Media;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

// Playing, pausing, and the end of the recording.
internal sealed partial class HeadlessChecks
{
    // ---------------------------------------------------------------------------------------
    // Seek(t) immediately followed by Play()
    // ---------------------------------------------------------------------------------------

    private void SeekThenPlay()
    {
        var pairs = _quick
            ? new[] { (50, 200), (300, 20) }
            : new[] { (50, 200), (300, 20), (10, 0), (200, 201), (120, 119), (340, 100), (0, 330), (150, 61) };
        _report.Section($"Seek(t) then Play() at once: playback starts at t ({pairs.Length} cases)");
        var session = OpenSession(TestMedia.Camera);
        var wrong = new List<string>();
        var startLatency = new Samples();
        foreach (var (from, to) in pairs)
        {
            session.SeekTo(from);
            session.Recorder.Drain();
            session.Events.Clear();
            var called = Stopwatch.GetTimestamp();
            session.Engine.Seek(TestFolder.TimeOf(to, 0.5));
            session.Engine.Play();
            WaitForFrames(session, to, 15);
            session.Engine.Pause();
            session.WaitForIdle();

            // Everything drawn after the calls: the still picture until the seek lands, then t, t+1, ...
            var frames = session.Recorder.Drain().Where(c => c.At >= called && c.Screen != FrameCode.Unreadable).ToList();
            var moving = frames.Where(c => c.Screen != from).ToList();
            var problem = string.Empty;
            if (moving.Count < 3)
            {
                problem = $"only {moving.Count} frames were drawn after the seek";
            }
            else if (moving[0].Screen != to)
            {
                problem = $"the first frame drawn after the old one was {moving[0].Screen}";
            }
            else
            {
                var previous = to;
                foreach (var composite in moving)
                {
                    // In order, never back to the old position, and nothing from far beyond.
                    if (composite.Screen < previous || composite.Screen > to + 60)
                    {
                        problem = $"frame {composite.Screen} was drawn after frame {previous}";
                        break;
                    }

                    previous = composite.Screen;
                }
            }

            // The positions reported say the same.
            var positions = session.Events.Positions().Select(p => (int)Math.Round(p.Position * Fps)).ToList();
            if (problem.Length == 0 && (positions.Count == 0 || positions[0] != to))
            {
                problem = $"the first position reported was frame {(positions.Count == 0 ? -1 : positions[0])}";
            }

            if (problem.Length > 0)
            {
                wrong.Add($"{from} -> Seek({to}), Play(): {problem} [drawn: {string.Join(' ', frames.Select(c => c.Screen))}]{Dump(session, "seekplay")}");
            }
            else
            {
                startLatency.Add(Stopwatch.GetElapsedTime(called, moving[0].At).TotalMilliseconds);
            }
        }

        _report.Check("the first frame drawn after the old picture is the frame asked for, and the following ones are in order", wrong.Count == 0, wrong.Count == 0 ? null : string.Join(" | ", wrong.Take(3)));
        _report.Note($"Seek()+Play() to the first frame of the new position drawn: {startLatency.Summary()}");

        // Seek while playing, as when the playhead is dragged during playback: it carries on from there.
        var targets = _quick ? new[] { 200, 40 } : new[] { 200, 40, 300, 41, 150, 10 };
        var jumpWrong = new List<string>();
        var jumpLatency = new Samples();
        var scenes = 0;
        var blank = 0;
        var mixed = 0;
        session.SeekTo(100);
        session.Recorder.Drain();
        session.Engine.Play();
        Thread.Sleep(300);
        foreach (var to in targets)
        {
            var called = Stopwatch.GetTimestamp();
            session.Engine.Seek(TestFolder.TimeOf(to, 0.5));
            WaitForFrames(session, to, 12);
            var playing = session.Engine.IsPlaying;
            var drawn = session.Recorder.Drain();
            scenes += drawn.Count;
            blank += drawn.Count(c => c.Screen == FrameCode.Unreadable);

            // The targets are far from where playback was, so a frame near the target is a new one.
            var after = drawn.Where(c => c.At >= called && c.Screen != FrameCode.Unreadable).ToList();
            bool IsNew(Composite c) => c.Screen >= to && c.Screen <= to + 60;
            var first = after.FindIndex(IsNew);
            var problem = string.Empty;
            if (!playing)
            {
                problem = "playback stopped";
            }
            else if (first < 0 || after.Count - first < 3)
            {
                // Few, on a machine that is busy; none, or one that stays, would be a stall.
                problem = $"only {(first < 0 ? 0 : after.Count - first)} frames were drawn at the new position";
            }
            else if (after[first].Screen != to)
            {
                problem = $"the first frame drawn there was {after[first].Screen}";
            }
            else
            {
                var previous = to;
                foreach (var composite in after.Skip(first))
                {
                    if (!IsNew(composite) || composite.Screen < previous)
                    {
                        problem = $"frame {composite.Screen} was drawn after frame {previous}";
                        break;
                    }

                    var camera = session.ExpectedCamera(composite.Screen);
                    mixed += composite.Camera == camera || (composite.Camera != FrameCode.Unreadable && camera != FrameCode.Unreadable && Math.Abs(composite.Camera - camera) <= 1) ? 0 : 1;
                    previous = composite.Screen;
                }
            }

            if (problem.Length > 0)
            {
                jumpWrong.Add($"Seek({to}) while playing: {problem} [drawn: {string.Join(' ', after.Take(14).Select(c => c.Screen))}]");
            }
            else
            {
                jumpLatency.Add(Stopwatch.GetElapsedTime(called, after[first].At).TotalMilliseconds);
            }
        }

        session.Engine.Pause();
        session.WaitForIdle();
        _report.Check($"Seek while playing ({targets.Length} times): playback carries on from the frame asked for, in order, and never goes back to the old position", jumpWrong.Count == 0, jumpWrong.Count == 0 ? null : string.Join(" | ", jumpWrong.Take(3)));
        _report.Check("and no scene drawn on the way is blank", blank == 0, $"{blank} of {scenes} scenes had an unreadable screen clip");
        _report.Note($"Seek() while playing to the first frame of the new position drawn: {jumpLatency.Summary()}; scenes at the new position with the camera more than a frame off: {mixed}");
        _report.Check("no failure was reported", session.Events.FailedEvents == 0, string.Join("; ", session.Events.Failures()));
        Close(session);
    }

    /// <summary>
    /// Waits until playback has got some frames past a frame, however long the seek before it took:
    /// on a machine that is busy with other work a seek can take half a second.
    /// </summary>
    private static bool WaitForFrames(Session session, int from, int frames, double seconds = 6)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            var position = session.PositionFrame;
            if (position >= from + frames && position <= from + frames + 40)
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return false;
    }

    // ---------------------------------------------------------------------------------------
    // Playback at 1x
    // ---------------------------------------------------------------------------------------

    private void Playback()
    {
        var seconds = _quick ? 4 : 10;
        var runs = _quick ? 1 : 2;
        _report.Section($"Playback at 1x for {seconds} s ({runs} run(s)), every drawn frame read back");
        var session = OpenSession(TestMedia.Camera);
        for (var run = 0; run < runs; run++)
        {
            const int start = 15;
            session.SeekTo(start);
            session.Recorder.Drain();
            session.Events.Clear();
            var before = session.Engine.GetDiagnostics();
            var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            var watch = Stopwatch.StartNew();
            session.Engine.Play();
            Thread.Sleep(seconds * 1000);
            var pausedAt = Stopwatch.GetTimestamp();
            session.Engine.Pause();
            var wall = watch.Elapsed.TotalSeconds;
            var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalSeconds;
            session.WaitForIdle();
            var after = session.Engine.GetDiagnostics();
            var composites = session.Recorder.Drain();
            var positions = session.Events.Positions().Select(p => (int)Math.Round(p.Position * Fps)).ToList();
            ReportPlayback($"run {run + 1}", session, composites, positions, start, wall, pausedAt);
            _report.Note($"run {run + 1}: players delivered screen {after.FramesCopied[0] - before.FramesCopied[0]}, camera {after.FramesCopied[1] - before.FramesCopied[1]} frames in {F(wall, "0.00")} s; process CPU {F(100 * cpu / wall, "0")} % of one core; copy targets screen {after.CopyTargets[0].Width}x{after.CopyTargets[0].Height}, camera {after.CopyTargets[1].Width}x{after.CopyTargets[1].Height}");
        }

        _report.Check("no failure was reported", session.Events.FailedEvents == 0, string.Join("; ", session.Events.Failures()));
        Close(session);
    }

    /// <summary>Judges what was drawn during one stretch of playback: order and correctness fail the check, drops and brief mismatches are reported.</summary>
    /// <param name="pausedAt">
    /// When the pause that ended it was asked for. A pause can take the picture back, to the
    /// last frame the engine could tell, when the scene showed a later one of which it could
    /// not: that is no playing backwards, and it is reported.
    /// </param>
    private void ReportPlayback(string label, Session session, List<Composite> composites, List<int> positions, int start, double wallSeconds, long pausedAt)
    {
        var readable = composites.Where(c => c.Screen != FrameCode.Unreadable).ToList();
        var unreadable = composites.Count - readable.Count;
        if (readable.Count < 10)
        {
            _report.Check($"{label}: frames were drawn", false, $"{readable.Count} readable of {composites.Count}");
            return;
        }

        // Order: the screen frame never goes back while it plays.
        var backwards = 0;
        var takenBack = 0;
        var worstApart = 0;
        var apart = 0;
        double apartMilliseconds = 0;
        for (var index = 0; index < readable.Count; index++)
        {
            if (index > 0 && readable[index].Screen < readable[index - 1].Screen)
            {
                if (readable[index].At < pausedAt)
                {
                    backwards++;
                }
                else
                {
                    takenBack = Math.Max(takenBack, readable[index - 1].Screen - readable[index].Screen);
                }
            }

            // The camera frame that belongs with this screen frame, against the one drawn.
            var expected = session.ExpectedCamera(readable[index].Screen);
            var difference = 0;
            if (expected != FrameCode.Unreadable && readable[index].Camera != FrameCode.Unreadable)
            {
                difference = Math.Abs(readable[index].Camera - expected);
            }
            else if (expected != readable[index].Camera)
            {
                // Shown when it should be hidden or the reverse: the frame on either side of the
                // camera's start or end, which is one frame apart.
                difference = 1;
            }

            worstApart = Math.Max(worstApart, difference);
            if (difference != 0)
            {
                apart++;
                if (index + 1 < readable.Count)
                {
                    apartMilliseconds += Stopwatch.GetElapsedTime(readable[index].At, readable[index + 1].At).TotalMilliseconds;
                }
            }
        }

        var first = readable[0].Screen;
        var last = readable.Max(c => c.Screen);
        var distinct = readable.Select(c => c.Screen).Distinct().Count();
        var span = last - Math.Max(first, start + 1) + 1;
        var shownOfSpan = readable.Select(c => c.Screen).Where(f => f > start).Distinct().Count();
        var dropped = Math.Max(0, span - shownOfSpan);
        var totalMilliseconds = Stopwatch.GetElapsedTime(readable[0].At, readable[^1].At).TotalMilliseconds;
        var intervals = new Samples();
        for (var index = 1; index < readable.Count; index++)
        {
            intervals.Add(Stopwatch.GetElapsedTime(readable[index - 1].At, readable[index].At).TotalMilliseconds);
        }

        _report.Check($"{label}: frames were drawn in order, none after a later one", backwards == 0, $"{backwards} went back");
        _report.Check($"{label}: the two clips were never more than one frame apart", worstApart <= 1, $"worst {worstApart} frames");
        _report.Check($"{label}: playback ran at 1x", Math.Abs((last - start) - (wallSeconds * Fps)) <= 0.08 * wallSeconds * Fps, $"reached frame {last} from {start} in {F(wallSeconds, "0.00")} s ({F((last - start) / wallSeconds)} frames per second)");

        // One PositionChanged per frame shown, never going back.
        var positionsBack = 0;
        for (var index = 1; index < positions.Count; index++)
        {
            positionsBack += positions[index] < positions[index - 1] ? 1 : 0;
        }

        var playing = positions.Where(p => p > start).Distinct().Count();
        _report.Check($"{label}: PositionChanged was raised for every frame shown, in order", positionsBack == 0 && Math.Abs(playing - shownOfSpan) <= 3, $"{positions.Count} events naming {playing} frames after the start, {shownOfSpan} frames drawn, {positionsBack} went back");
        _report.Note($"{label}: {readable.Count} scenes drawn ({F(readable.Count / (totalMilliseconds / 1000))} per second), {unreadable} unreadable; screen frames {first}..{last}: {shownOfSpan} of {span} drawn, {dropped} never drawn ({F(100.0 * dropped / Math.Max(1, span), "0.00")} %); interval between drawn scenes {intervals.Summary()}");
        _report.Note($"{label}: clips one frame apart in {apart} scenes, for {F(apartMilliseconds, "0")} ms of {F(totalMilliseconds, "0")} ms ({F(100 * apartMilliseconds / Math.Max(1, totalMilliseconds), "0.00")} % of the time); {distinct} distinct screen frames");
        if (takenBack > 0)
        {
            _report.Note($"{label}: the pause that ended it took the picture back by {takenBack} frame(s), to the last frame the engine could tell: the scene had shown a later one of which it could not");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Pause
    // ---------------------------------------------------------------------------------------

    private void PauseCycles()
    {
        var cycles = _quick ? 10 : 40;
        _report.Section($"Pause during playback, {cycles} cycles");
        var session = OpenSession(TestMedia.Camera);
        session.SeekTo(30);
        var before = session.Engine.GetDiagnostics();
        var wrong = new List<string>();
        var notFinal = new List<string>();
        var arrivedAfter = 0;
        var fixedBySnap = 0;
        var pauseCall = new Samples();
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            if (session.PositionFrame > 250)
            {
                session.SeekTo(30);
            }

            session.Engine.Play();
            Thread.Sleep(450 + _random.Next(500));
            session.Recorder.Drain();
            var started = Stopwatch.GetTimestamp();
            session.Engine.Pause();
            var returned = Stopwatch.GetTimestamp();
            var positionAtReturn = session.PositionFrame;
            pauseCall.Add(Stopwatch.GetElapsedTime(started, returned).TotalMilliseconds);

            // What the players' own textures hold the moment Pause() has returned.
            var screenAtReturn = session.ReadClipFrame(0);
            var cameraAtReturn = session.ReadClipFrame(1);
            var playingAtReturn = session.Engine.IsPlaying;
            Thread.Sleep(500);
            var idle = session.WaitForIdle();

            // Half a second later: the picture, and the players' textures again.
            var shown = session.ReadShown();
            var screenLater = session.ReadClipFrame(0);
            var camera = session.ExpectedCamera(shown.Screen);
            if (screenLater != screenAtReturn)
            {
                arrivedAfter++;
                wrong.Add($"cycle {cycle}: the screen player's texture held frame {screenAtReturn} when Pause() returned and {screenLater} half a second later{Dump(session, "pause")}");
            }

            if (cameraAtReturn != session.ExpectedCamera(screenAtReturn))
            {
                fixedBySnap++;
            }

            if (!idle || playingAtReturn || shown.Screen == FrameCode.Unreadable || shown.Screen != screenLater || shown.Camera != camera || session.PositionFrame != shown.Screen)
            {
                wrong.Add($"cycle {cycle}: idle {idle}, IsPlaying after Pause() {playingAtReturn}, picture {shown}, wanted camera {camera}, Position frame {session.PositionFrame}, screen texture {screenAtReturn} then {screenLater}{Dump(session, "pause")}");
            }

            // A caller that reads Position when Pause() has returned must find the frame the
            // picture stays on, and nothing drawn afterwards may show another screen frame.
            var drawnAfter = session.Recorder.Drain().Where(c => c.At >= returned && c.Screen != FrameCode.Unreadable).Select(c => c.Screen).ToList();
            if (positionAtReturn != shown.Screen || drawnAfter.Any(frame => frame != positionAtReturn))
            {
                notFinal.Add($"cycle {cycle}: Position was frame {positionAtReturn} when Pause() returned, the picture came to rest on frame {shown.Screen}, and the scenes drawn after the return showed [{string.Join(' ', drawnAfter)}]{Dump(session, "pause")}");
            }
        }

        var after = session.Engine.GetDiagnostics();
        _report.Check($"after each of {cycles} pauses both clips are on matching frames and Position is the frame shown", wrong.Count == 0, wrong.Count == 0 ? null : $"{wrong.Count} wrong; first: {string.Join(" | ", wrong.Take(3))}");
        _report.Check($"when Pause() returns, Position is already the frame the picture stays on, and no scene drawn afterwards shows another screen frame ({cycles} pauses)", notFinal.Count == 0, notFinal.Count == 0 ? null : $"{notFinal.Count} of {cycles}; first: {string.Join(" | ", notFinal.Take(3))}");
        _report.Check("no frame reached the picture after Pause() had returned", arrivedAfter == 0 && after.FramesAfterPause == before.FramesAfterPause, $"the screen player's texture changed after Pause() returned in {arrivedAfter} of {cycles}; frames the players still handed over after the clock had stopped, left out by the engine: {after.LateFramesDiscarded - before.LateFramesDiscarded}");
        _report.Note($"pauses that left the clips a frame apart, put right by the snap: {fixedBySnap} of {cycles}; the Pause() call took {pauseCall.Summary()}");

        // Pause() and Seek(t) at once, as a scrub begins or the editor parks at the end of its
        // range. A player can have its next frame ready when the clock stops, and hands it over
        // as its first answer to the seek, with the seek's position on it. It must not be drawn.
        var last = session.Folder.FrameCount - 1;
        var seeks = new List<string>();
        var toTheSame = 0;
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            if (session.PositionFrame > last - 60)
            {
                session.SeekTo(30);
            }

            session.Engine.Play();
            Thread.Sleep(350 + _random.Next(400));
            session.Recorder.Drain();
            session.Engine.Pause();
            var returned = Stopwatch.GetTimestamp();
            var stoppedOn = session.PositionFrame;

            // Every other time to the frame the picture stopped on, which must then not change at all.
            var same = cycle % 2 == 0;
            toTheSame += same ? 1 : 0;
            var target = same ? stoppedOn : stoppedOn > 150 ? stoppedOn - 80 : stoppedOn + 80;
            session.Engine.Seek(TestFolder.TimeOf(target, 0.5));
            var idle = session.WaitForIdle();
            var shown = session.ReadShown();
            var drawn = session.Recorder.Drain().Where(c => c.At >= returned && c.Screen != FrameCode.Unreadable).Select(c => c.Screen).ToList();
            if (!idle || shown.Screen != target || shown.Camera != session.ExpectedCamera(target) || session.PositionFrame != target || drawn.Any(frame => frame != stoppedOn && frame != target))
            {
                seeks.Add($"cycle {cycle}: stopped on frame {stoppedOn}, Seek({target}): idle {idle}, picture {shown}, Position frame {session.PositionFrame}, scenes drawn after Pause() returned showed [{string.Join(' ', drawn)}]{Dump(session, "pause")}");
            }
        }

        _report.Check(
            $"Pause() then Seek(t) at once ({cycles} times, {toTheSame} of them to the frame the picture stopped on): the picture ends on t, and no scene drawn on the way shows any frame but the one it stopped on and t",
            seeks.Count == 0,
            seeks.Count == 0 ? null : $"{seeks.Count} wrong; first: {string.Join(" | ", seeks.Take(3))}");

        // The same hazard, looked for where it is: pauses timed to fall where the clock has
        // reached the next frame and the player has not handed it over yet, 16 to 32 ms after a
        // scene was drawn.
        var timed = _quick ? 27 : 90;
        var timedWrong = new List<string>();
        var wrongByDelay = new SortedDictionary<int, int>();
        var discardedBefore = session.Engine.GetDiagnostics().LateFramesDiscarded;
        for (var cycle = 0; cycle < timed; cycle++)
        {
            if (session.PositionFrame > last - 60)
            {
                session.SeekTo(30);
            }

            session.Engine.Play();
            Thread.Sleep(250);
            var next = session.Recorder.Expect(_ => true);
            try
            {
                next.Wait(TimeSpan.FromSeconds(1));
            }
            finally
            {
                session.Recorder.Forget(next);
                next.Dispose();
            }

            // Not a sleep: that is good to a timer tick, which is half of what is aimed at here.
            var delay = 16 + (cycle % 9 * 2);
            var until = Stopwatch.GetTimestamp() + (delay * Stopwatch.Frequency / 1000);
            while (Stopwatch.GetTimestamp() < until)
            {
                Thread.SpinWait(40);
            }

            session.Recorder.Drain();
            session.Engine.Pause();
            var returned = Stopwatch.GetTimestamp();
            var stoppedOn = session.PositionFrame;
            var idle = session.WaitForIdle();
            var shown = session.ReadShown();
            var drawn = session.Recorder.Drain().Where(c => c.At >= returned && c.Screen != FrameCode.Unreadable).Select(c => c.Screen).ToList();
            if (!idle || shown.Screen != stoppedOn || shown.Camera != session.ExpectedCamera(stoppedOn) || session.PositionFrame != stoppedOn || drawn.Any(frame => frame != stoppedOn))
            {
                wrongByDelay[delay] = wrongByDelay.GetValueOrDefault(delay) + 1;
                timedWrong.Add($"cycle {cycle} ({delay} ms after a scene): Position was frame {stoppedOn} when Pause() returned; idle {idle}, picture {shown}, Position frame {session.PositionFrame}, scenes drawn after the return showed [{string.Join(' ', drawn)}]{Dump(session, "pause")}");
            }
        }

        _report.Check(
            $"{timed} pauses timed to fall where a frame is due, 16 to 32 ms after a scene: the picture does not move after Pause() has returned",
            timedWrong.Count == 0,
            timedWrong.Count == 0 ? null : $"{timedWrong.Count} wrong ({string.Join(", ", wrongByDelay.Select(d => $"{d.Value} at {d.Key} ms"))}); first: {string.Join(" | ", timedWrong.Take(3))}");
        var atTheEnd = session.Engine.GetDiagnostics();
        _report.Note($"of those {timed} pauses, the players still handed a frame over after the clock had stopped, and it was left out, in {atTheEnd.LateFramesDiscarded - discardedBefore}");
        _report.Note($"in all {(2 * cycles) + timed} pauses of this group, a player handed over two frames for the first position change afterwards {atTheEnd.SecondAnswers - before.SecondAnswers} times. The first of two can be the frame it had ready for playback, with the new position on it; the picture is held until the players have gone quiet, so it is not drawn");
        _report.Check("no failure was reported", session.Events.FailedEvents == 0, string.Join("; ", session.Events.Failures()));
        Close(session);

        PauseThenPlayWhileTheRenderThreadIsHeld();
    }

    /// <summary>
    /// Pause() and Play() one after the other while the render thread is kept in one draw for
    /// longer than Pause() waits for it. The render thread then finds playing wanted, as it was
    /// when it last looked, and a clock that Pause() has stopped. Playback has to go on.
    /// </summary>
    private void PauseThenPlayWhileTheRenderThreadIsHeld()
    {
        const int times = 6;

        // Longer than Pause() waits in all: 50 ms for the hand-overs, 100 ms for a pass, 500 ms for the round.
        var hold = TimeSpan.FromMilliseconds(1100);
        var armed = 0;
        using var held = new ManualResetEventSlim(false);
        var options = Muted with
        {
            RenderDelay = () =>
            {
                if (Interlocked.Exchange(ref armed, 0) == 0)
                {
                    return TimeSpan.Zero;
                }

                held.Set();
                return hold;
            },
        };

        var session = OpenSession(TestMedia.Camera, options: options);
        var wrong = new List<string>();
        var pauseCall = new Samples();
        var unheard = 0;
        for (var cycle = 0; cycle < times; cycle++)
        {
            session.SeekTo(30 + (cycle * 20));
            session.Engine.Play();
            Thread.Sleep(400);
            held.Reset();
            Interlocked.Exchange(ref armed, 1);
            if (!held.Wait(TimeSpan.FromSeconds(2)))
            {
                wrong.Add($"cycle {cycle}: no scene was drawn within two seconds of playing");
                session.Engine.Pause();
                session.WaitForIdle();
                continue;
            }

            // The render thread sleeps in its draw now, with its round and the device held.
            var started = Stopwatch.GetTimestamp();
            session.Engine.Pause();
            var returned = Stopwatch.GetTimestamp();
            var stoppedOn = session.PositionFrame;
            session.Engine.Play();
            var took = Stopwatch.GetElapsedTime(started, returned);
            pauseCall.Add(took.TotalMilliseconds);

            // Whether the render thread was still in that draw when Play() came: only then has
            // it heard of neither call.
            unheard += took < hold - TimeSpan.FromMilliseconds(100) ? 1 : 0;
            Thread.Sleep(hold + TimeSpan.FromMilliseconds(1500));
            var playing = session.Engine.IsPlaying;
            var movedOn = session.PositionFrame;
            session.Recorder.Drain();
            Thread.Sleep(500);
            var scenes = session.Recorder.Drain().Count(c => c.Screen != FrameCode.Unreadable);
            session.Engine.Pause();
            var idle = session.WaitForIdle();
            var shown = session.ReadShown();
            if (!playing || movedOn < stoppedOn + 20 || scenes < 8 || !idle || shown.Screen != session.PositionFrame || shown.Camera != session.ExpectedCamera(shown.Screen))
            {
                wrong.Add($"cycle {cycle}: Pause() took {F(took.TotalMilliseconds, "0")} ms and returned on frame {stoppedOn}; {F((hold + TimeSpan.FromMilliseconds(1500)).TotalSeconds)} s after Play(): IsPlaying {playing}, Position frame {movedOn}, {scenes} scenes in the half second after; after the last pause: idle {idle}, picture {shown}, Position frame {session.PositionFrame}{Dump(session, "pause-play")}");
            }
        }

        _report.Check(
            $"Pause() then Play() at once, with the render thread held in a draw for {F(hold.TotalMilliseconds, "0")} ms ({times} times): playback goes on",
            wrong.Count == 0 && unheard > 0,
            wrong.Count == 0 ? $"the render thread was still held when Play() came in {unheard} of {times}; the Pause() call took {pauseCall.Summary()}" : $"{wrong.Count} wrong; first: {string.Join(" | ", wrong.Take(3))}");
        _report.Check("no failure was reported", session.Events.FailedEvents == 0, string.Join("; ", session.Events.Failures()));
        Close(session);
    }

    // ---------------------------------------------------------------------------------------
    // The end of the recording
    // ---------------------------------------------------------------------------------------

    private void EndOfRecording()
    {
        _report.Section("The end of the recording");
        var session = OpenSession(TestMedia.Camera);
        var last = session.Folder.FrameCount - 1;
        session.SeekTo(last - 40);
        session.Recorder.Drain();
        session.Events.Clear();
        session.Engine.Play();
        var watch = Stopwatch.StartNew();
        while (session.Engine.IsPlaying && watch.Elapsed.TotalSeconds < 6)
        {
            Thread.Sleep(5);
        }

        var stoppedAfter = watch.Elapsed.TotalSeconds;
        var stoppedByItself = !session.Engine.IsPlaying;
        session.WaitForIdle();
        Thread.Sleep(300);
        var playing = session.Events.Playing();
        var shown = session.ReadShown();
        var composites = session.Recorder.Drain().Where(c => c.Screen != FrameCode.Unreadable).ToList();
        var backwards = 0;
        for (var index = 1; index < composites.Count; index++)
        {
            backwards += composites[index].Screen < composites[index - 1].Screen ? 1 : 0;
        }

        _report.Check("playback stops by itself at the end", stoppedByItself, $"IsPlaying false {F(stoppedAfter, "0.00")} s after Play() from {F(41.0 / Fps, "0.00")} s before the end");
        _report.Check("IsPlayingChanged is raised once for the start and once for the end", playing.Count == 2 && playing[0].IsPlaying && !playing[1].IsPlaying, string.Join(", ", playing.Select(p => p.IsPlaying)));
        var readAtStop = playing.Count == 2 ? (int)Math.Round(playing[1].Position * Fps) : -1;
        _report.Check("the handler of the IsPlayingChanged for the end reads the last frame from Position", readAtStop == last, $"frame {readAtStop}, last frame {last}");
        _report.Check("the last frame stays up, with Position on it", shown.Screen == last && shown.Camera == session.ExpectedCamera(last) && session.PositionFrame == last, $"{shown}, Position frame {session.PositionFrame}, last frame {last}");
        _report.Check("frames were drawn in order up to the end", backwards == 0 && composites.Count > 30, $"{composites.Count} scenes, {backwards} went back");
        Thread.Sleep(500);
        var still = session.ReadShown();
        _report.Check("it is still there half a second later", still == shown && !session.Engine.IsPlaying, still.ToString());
        _report.Note($"at rest after the end: {Transport(session)}");

        // Seek(0) and Play() straight after the end.
        session.Recorder.Drain();
        session.Events.Clear();
        var called = Stopwatch.GetTimestamp();
        session.Engine.Seek(0);
        session.Engine.Play();
        Thread.Sleep(800);
        var playingAgain = session.Engine.IsPlaying;
        var whilePlaying = Transport(session);
        var replayPausedAt = Stopwatch.GetTimestamp();
        session.Engine.Pause();
        session.WaitForIdle();
        var replay = session.Recorder.Drain().Where(c => c.At >= called && c.At < replayPausedAt && c.Screen != FrameCode.Unreadable && c.Screen != last).Select(c => c.Screen).ToList();
        var ordered = replay.Count > 5 && replay[0] == 0 && replay.Zip(replay.Skip(1), (a, b) => b >= a).All(x => x);
        _report.Check("Seek(0) and Play() after the end play from the first frame", playingAgain && ordered && replay[^1] > 10, $"drawn: {string.Join(' ', replay.Take(10))} … {(replay.Count == 0 ? -1 : replay[^1])}; while playing: {whilePlaying}");

        // To the end once more, and this time seeks. A player that has run into the end of its
        // stream lands its next seek a frame late; the engine spends that seek itself.
        session.SeekTo(last - 20);
        session.Engine.Play();
        watch.Restart();
        while (session.Engine.IsPlaying && watch.Elapsed.TotalSeconds < 6)
        {
            Thread.Sleep(5);
        }

        session.WaitForIdle();
        var recoveries = session.Engine.GetDiagnostics().EndRecoveries;
        var seeksWrong = new List<string>();
        foreach (var frame in new[] { 100, 101, 200, 50, last, 0 })
        {
            var landed = session.SeekTo(frame);
            var seen = session.ReadShown();
            if (!landed || seen.Screen != frame || seen.Camera != session.ExpectedCamera(frame))
            {
                seeksWrong.Add($"{frame}: landed {landed}, {seen}; {Transport(session)}");
            }
        }

        _report.Check("seeks after the end show exactly the frames asked for, the first one included", seeksWrong.Count == 0, seeksWrong.Count == 0 ? $"the engine made {recoveries} seek(s) of its own at the ends so far" : string.Join(" | ", seeksWrong));

        // Play() on the last frame: nothing is left to play, so it stops again at once.
        session.SeekTo(last);
        session.Events.Clear();
        session.Engine.Play();
        watch.Restart();
        while (session.Engine.IsPlaying && watch.Elapsed.TotalSeconds < 3)
        {
            Thread.Sleep(5);
        }

        session.WaitForIdle();
        shown = session.ReadShown();
        var endEvents = session.Events.Playing();
        var readAtSecondStop = endEvents.Count == 2 ? (int)Math.Round(endEvents[1].Position * Fps) : -1;
        _report.Check("Play() on the last frame stops again by itself, still on the last frame", !session.Engine.IsPlaying && shown.Screen == last && session.PositionFrame == last && endEvents.Count == 2 && readAtSecondStop == last, $"{shown}, stopped after {F(watch.Elapsed.TotalMilliseconds, "0")} ms, IsPlayingChanged x{endEvents.Count}, its handler read frame {readAtSecondStop}");
        _report.Check("no failure was reported", session.Events.FailedEvents == 0, string.Join("; ", session.Events.Failures()));
        Close(session);
        EndOfRecordingHeldUp();
    }

    /// <summary>
    /// Playing into the end of the recording while the process is held up. When the clock runs
    /// out, a clip can be showing a frame of which the engine could not tell which one it is.
    /// The players are then brought to the last frame, and until that is there the picture has
    /// to stay as it is: it must not go back to an earlier frame first.
    /// </summary>
    private void EndOfRecordingHeldUp()
    {
        var times = _options.Number("count", _quick ? 3 : 6);
        foreach (var kind in HoldUpKinds("collector", "draw"))
        {
            if (kind == "none")
            {
                continue;
            }

            using var holdUps = new HoldUps(kind, HoldUpMilliseconds);
            _report.Section($"The end of the recording, with {holdUps.Name}: played into {times} times");
            var session = OpenSession(TestMedia.Camera, options: holdUps.With(Muted));
            try
            {
                var last = session.Folder.FrameCount - 1;
                var wrong = new List<string>();
                var fetched = 0;
                var before = session.Engine.GetDiagnostics();
                holdUps.Begin();
                for (var time = 0; time < times; time++)
                {
                    session.SeekTo(last - 40);
                    session.Recorder.Drain();
                    var fetchedBefore = session.Engine.GetDiagnostics().RestsFetchedAnew;
                    session.Engine.Play();
                    var watch = Stopwatch.StartNew();
                    while (session.Engine.IsPlaying && watch.Elapsed.TotalSeconds < 8)
                    {
                        Thread.Sleep(5);
                    }

                    var stopped = !session.Engine.IsPlaying;
                    var idle = session.WaitForIdle();
                    var shown = session.ReadShown();
                    var position = session.PositionFrame;
                    var frames = session.Recorder.Drain().Where(c => c.Screen != FrameCode.Unreadable).Select(c => c.Screen).ToList();
                    fetched += session.Engine.GetDiagnostics().RestsFetchedAnew > fetchedBefore ? 1 : 0;
                    var drawn = frames.Where(f => f > last - 40).Distinct().Count();
                    var problem = !stopped ? "playback did not stop by itself"
                        : !idle ? "the engine did not come to rest"
                        : shown != new Shown(last, session.ExpectedCamera(last)) || position != last ? $"at rest the picture showed {shown} and Position was frame {position}; the last frame is {last}"
                        : Backwards(frames) is { } back ? back
                        : drawn < 12 ? $"only {drawn} of the last 40 frames were drawn"
                        : null;
                    if (problem is not null)
                    {
                        wrong.Add($"time {time + 1}: {problem}; the last scenes showed [{string.Join(' ', frames.TakeLast(8))}]{Dump(session, "end")}");
                    }
                }

                holdUps.Rest();
                var end = session.Engine.GetDiagnostics();
                _report.Check(
                    $"with {holdUps.Name}: playing into the end of the recording ({times} times), playback stops by itself, the picture comes to rest on the last frame with Position on it, and no scene on the way shows an earlier frame after a later one",
                    wrong.Count == 0,
                    wrong.Count == 0 ? null : $"{wrong.Count} of {times} wrong; first: {string.Join(" | ", wrong.Take(3))}");
                _report.Check($"with {holdUps.Name}: no failure was reported", session.Events.FailedEvents == 0, string.Join("; ", session.Events.Failures()));
                _report.Note($"with {holdUps.Name}: when the clock ran out, a clip showed a frame without a number, or one whose number was inferred, in {fetched} of the {times} plays, and the last frame was fetched anew; {holdUps.Describe()}");
                _report.Note($"with {holdUps.Name}: {Naming(before, end)}");
            }
            finally
            {
                holdUps.Rest();
                Close(session);
            }
        }
    }
}
