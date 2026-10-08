using System.Diagnostics;
using StudioEngineSpike.Engine;

namespace StudioEngineSpike.Modes;

internal sealed partial class PreviewSession
{
    // ---------------------------------------------------------------------------------------
    // Paused seeks
    // ---------------------------------------------------------------------------------------

    private void PausedSeeks()
    {
        const int count = 250;
        _report.Section($"Seek while paused: Controller.Position = middle of frame N, wait for both clips ({count} seeks, the next one issued 20 ms after both frames arrived)");
        var random = new Random(2026);
        var targets = new List<int> { 60, 59, 61, 120, 119, 7, 6, 898, 899, 450, 451, 449, 300, 30 };
        while (targets.Count < count)
        {
            targets.Add(random.Next(TestMedia.CameraFrameLag, TestMedia.FrameCount));
        }

        var results = new List<SeekResult>();
        foreach (var frame in targets)
        {
            var result = SeekAndWait(MidFrame(frame), frame, frame - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(2), settleMs: 20);
            results.Add(result);
            if (!result.Correct)
            {
                DiagnoseMiss(result);
            }
        }

        SavePng("seek.png");
        ReportSeeks(results, "seeks", listMisses: false);
        var keyframe = results.Where(r => r.TargetFrame % 60 == 0).Select(r => r.ScreenLatencyMs).Where(ms => ms >= 0).ToList();
        var late = results.Where(r => r.TargetFrame % 60 >= 50).Select(r => r.ScreenLatencyMs).Where(ms => ms >= 0).ToList();
        if (keyframe.Count > 0 && late.Count > 0)
        {
            _report.Line($"screen latency by position in the 60-frame GOP: on a keyframe {keyframe.Average().F("0")} ms (n={keyframe.Count}), 50+ frames in {late.Average().F("0")} ms (n={late.Count})");
        }

        // Latency against how many frames the decoder has to run forward from the keyframe.
        var perFrame = results.Where(r => r.ScreenLatencyMs >= 0 && r.TargetFrame % 60 >= 10).Select(r => r.ScreenLatencyMs / (r.TargetFrame % 60)).ToList();
        if (perFrame.Count > 0)
        {
            _report.Line($"screen latency per frame decoded forward from the keyframe: {perFrame.Average().F("0.00")} ms (2560x1440); the same seeks on the camera (1280x720): {results.Where(r => r.CameraLatencyMs >= 0 && r.ExpectedCamera % 60 >= 10).Select(r => r.CameraLatencyMs / (r.ExpectedCamera!.Value % 60)).DefaultIfEmpty().Average().F("0.00")} ms");
        }
    }

    /// <summary>What state is a player in after a seek that did not produce the requested frame, and what fixes it?</summary>
    private void DiagnoseMiss(SeekResult miss)
    {
        var position = MidFrame(miss.TargetFrame);
        _report.Line($"  MISS at target frame {miss.TargetFrame}: screen {miss.ScreenFrame} [{string.Join(' ', miss.ScreenFramesSeen)}], camera {miss.CameraFrame} (want {miss.ExpectedCamera}) [{string.Join(' ', miss.CameraFramesSeen)}]");
        _report.Line($"    controller {_engine.Controller.Position.TotalSeconds.F("0.0000")} s ({_engine.Controller.State}); session positions: screen {_engine.Screen.Player.PlaybackSession.Position.TotalSeconds.F("0.0000")} s, camera {_engine.Camera.Player.PlaybackSession.Position.TotalSeconds.F("0.0000")} s (target {position.TotalSeconds.F("0.0000")} s)");
        Thread.Sleep(1500);
        var later = CompositeNow(identify: true);
        var events = DrainFrameEvents();
        _report.Line($"    1.5 s later without touching anything: screen {later.ScreenFrame}, camera {later.CameraFrame} ({events.Count} more callbacks)");
        if (later.ScreenFrame == miss.TargetFrame && later.CameraFrame == miss.ExpectedCamera)
        {
            return;
        }

        var again = SeekAndWait(position, miss.TargetFrame, miss.ExpectedCamera, TimeSpan.FromSeconds(2));
        _report.Line($"    same position assigned again: screen {again.ScreenFrame}, camera {again.CameraFrame} {Verdict(again.Correct)}");
        if (again.Correct)
        {
            return;
        }

        var nudged = SeekAndWait(position + TimeSpan.FromTicks(1), miss.TargetFrame, miss.ExpectedCamera, TimeSpan.FromSeconds(2));
        _report.Line($"    position + 1 tick: screen {nudged.ScreenFrame}, camera {nudged.CameraFrame} {Verdict(nudged.Correct)}");
    }

    private void ReportSeeks(List<SeekResult> results, string noun, bool listMisses = true)
    {
        var correct = results.Count(r => r.Correct);
        var delivered = results.Count(r => r.ScreenDelivered && r.CameraDelivered);
        var screenLatency = new Samples();
        var cameraLatency = new Samples();
        var bothLatency = new Samples();
        foreach (var r in results)
        {
            if (r.ScreenLatencyMs >= 0)
            {
                screenLatency.Add(r.ScreenLatencyMs);
            }

            if (r.CameraLatencyMs >= 0)
            {
                cameraLatency.Add(r.CameraLatencyMs);
            }

            if (r.ScreenLatencyMs >= 0 && r.CameraLatencyMs >= 0)
            {
                bothLatency.Add(Math.Max(r.ScreenLatencyMs, r.CameraLatencyMs));
            }
        }

        _report.Line($"{results.Count} {noun}: final composite shows the requested frame on both clips in {correct} ({(correct * 100.0 / results.Count).F("0.0")} %); a new frame was delivered for both clips in {delivered}");
        _report.Line($"  time until the requested frame arrived: screen {screenLatency.Summary()}");
        _report.Line($"                                          camera {cameraLatency.Summary()}");
        _report.Line($"                                          both   {bothLatency.Summary()}");
        _report.Line($"  callbacks per {noun.TrimEnd('s')}: screen {Fmt.Histogram(results.Select(r => r.ScreenEvents))}; camera {Fmt.Histogram(results.Select(r => r.CameraEvents))}");
        var screenErrors = results.Select(r => r.ScreenFrame - r.TargetFrame).ToList();
        var cameraErrors = results.Where(r => r.ExpectedCamera is not null).Select(r => r.CameraFrame - r.ExpectedCamera!.Value).ToList();
        _report.Line($"  final frame − requested frame: screen {Fmt.Histogram(screenErrors)}; camera {Fmt.Histogram(cameraErrors)}");
        _failures += results.Count(r => !r.Correct);
        if (listMisses)
        {
            foreach (var r in results.Where(r => !r.Correct).Take(12))
            {
                _report.Line($"  MISS: target frame {r.TargetFrame}: screen {r.ScreenFrame} (delivered {r.ScreenDelivered}), camera {r.CameraFrame} expected {r.ExpectedCamera} (delivered {r.CameraDelivered}); frames seen screen [{string.Join(' ', r.ScreenFramesSeen)}] camera [{string.Join(' ', r.CameraFramesSeen)}]");
            }
        }
    }

    /// <summary>
    /// Which frame does a position near a frame boundary select? Frame N starts at N/30 s, which is
    /// not a whole number of 100 ns ticks, so "N × 333333 ticks" can land on either side.
    /// </summary>
    private void SeekBoundaries()
    {
        _report.Section("Seek while paused: positions at and around frame boundaries (screen clip)");
        _report.Line("position = a fraction of the way through frame N → frame shown, relative to N");
        var frames = new[] { 31, 62, 100, 200, 333, 457, 601, 777, 850 };
        foreach (var fraction in new[] { 0.02, 0.1, 0.5, 0.9, 0.98 })
        {
            var shown = new List<int>();
            foreach (var frame in frames)
            {
                var ticks = (long)Math.Round((frame + fraction) * TicksPerSecond / Fps);
                shown.Add(Probe(TimeSpan.FromTicks(ticks)) - frame);
            }

            _report.Line($"  N+{fraction.F("0.00")}: {Fmt.Histogram(shown)}");
        }

        _report.Line("position = floor(N × 10^7 / 30) + d ticks (333 and 777 start on a whole tick; the others start 1/3 or 2/3 of a tick after the floor)");
        _report.Line($"  {"N",-6}{string.Join(string.Empty, frames.Select(f => $"{f,6}"))}");
        foreach (var d in new[] { -3, -2, -1, 0, 1, 2 })
        {
            var shown = new List<int>();
            foreach (var frame in frames)
            {
                shown.Add(Probe(TimeSpan.FromTicks((frame * TicksPerSecond / Fps) + d)) - frame);
            }

            _report.Line($"  d={d,-4}{string.Join(string.Empty, shown.Select(s => $"{s,6}"))}");
        }

        // One probe: park somewhere else first so a "no new frame" outcome cannot look like a hit.
        int Probe(TimeSpan position)
        {
            SeekAndWait(MidFrame(15), 15, 15 - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(2), settleMs: 20);
            var result = SeekAndWait(position, expectedScreen: -1, expectedCamera: null, TimeSpan.FromSeconds(2), acceptAnyFrame: true, settleMs: 250);
            return result.ScreenFrame;
        }
    }

    /// <summary>Dragging the playhead: a new position every 33 ms without waiting for frames.</summary>
    private void Scrub()
    {
        _report.Section("Scrub while paused: 120 position changes 33.3 ms apart, no waiting (forward 3 frames each, then back)");
        SeekAndWait(MidFrame(200), 200, 200 - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(3));
        DrainFrameEvents();
        DrainComposites();
        var start = Stopwatch.GetTimestamp();
        var frame = 200;
        var setMs = new Samples();
        for (var index = 0; index < 120; index++)
        {
            frame += index < 60 ? 3 : -3;
            var before = Stopwatch.GetTimestamp();
            _engine.Controller.Position = MidFrame(frame);
            setMs.Add(Stopwatch.GetElapsedTime(before).TotalMilliseconds);
            PreciseTimer.Sleep(1000.0 / Fps);
        }

        var scrubMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var finalDeadline = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(finalDeadline).TotalSeconds < 3 &&
               (_engine.Screen.LatestFrame != frame || _engine.Camera.LatestFrame != frame - TestMedia.CameraFrameLag))
        {
            PreciseTimer.Sleep(1);
        }

        var settleMs = Stopwatch.GetElapsedTime(finalDeadline).TotalMilliseconds;
        Thread.Sleep(200);
        var events = DrainFrameEvents();
        var composites = DrainComposites();
        var final = CompositeNow(identify: true);
        var screenEvents = events.Count(e => e.Track == 0);
        var cameraEvents = events.Count(e => e.Track == 1);
        var offsets = composites.Where(c => c.ScreenFrame >= 0 && c.CameraFrame >= 0).Select(c => c.ScreenFrame - c.CameraFrame - TestMedia.CameraFrameLag).ToList();
        _report.Line($"{scrubMs.F("0")} ms of scrubbing; setting Controller.Position costs {setMs.Summary()}");
        _report.Line($"frames delivered while scrubbing: screen {screenEvents} ({(screenEvents / (scrubMs / 1000)).F("0.0")}/s), camera {cameraEvents} ({(cameraEvents / (scrubMs / 1000)).F("0.0")}/s)");
        _report.Line($"offset between clips in the {offsets.Count} composites made while scrubbing (frames): {Fmt.Histogram(offsets)}");
        var ok = final.ScreenFrame == frame && final.CameraFrame == frame - TestMedia.CameraFrameLag;
        _report.Line($"final position frame {frame}: screen {final.ScreenFrame}, camera {final.CameraFrame}, settled {settleMs.F("0")} ms after the last position change {Verdict(ok)}");
        if (!ok)
        {
            _failures++;
        }

        // The property that matters for a drag: whatever happened in between, does the LAST position win?
        const int bursts = 150;
        _report.Line($"last-position-wins check: {bursts} bursts of 2–8 position changes 0–50 ms apart, then wait up to 2 s");
        var random = new Random(777);
        var wrongScreen = 0;
        var wrongCamera = 0;
        var settle = new Samples();
        var examples = new List<string>();
        for (var burst = 0; burst < bursts; burst++)
        {
            var changes = 2 + random.Next(7);
            var target = 0;
            var gaps = new List<int>();
            DrainFrameEvents();
            for (var change = 0; change < changes; change++)
            {
                target = random.Next(TestMedia.CameraFrameLag + 1, TestMedia.FrameCount - 1);
                _engine.Controller.Position = MidFrame(target);
                if (change < changes - 1)
                {
                    var gap = random.Next(51);
                    gaps.Add(gap);
                    if (gap > 0)
                    {
                        PreciseTimer.Sleep(gap);
                    }
                }
            }

            var issued = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(issued).TotalSeconds < 2 &&
                   (_engine.Screen.LatestFrame != target || _engine.Camera.LatestFrame != target - TestMedia.CameraFrameLag))
            {
                PreciseTimer.Sleep(1);
            }

            var waited = Stopwatch.GetElapsedTime(issued).TotalMilliseconds;
            PreciseTimer.Sleep(30);
            var composite = CompositeNow(identify: true);
            var screenOk = composite.ScreenFrame == target;
            var cameraOk = composite.CameraFrame == target - TestMedia.CameraFrameLag;
            if (screenOk && cameraOk)
            {
                settle.Add(waited);
            }
            else
            {
                wrongScreen += screenOk ? 0 : 1;
                wrongCamera += cameraOk ? 0 : 1;
                if (composite.ScreenFrame < 0 || composite.CameraFrame < 0)
                {
                    // An unreadable strip: keep the picture so the report can say what was on screen.
                    SavePng($"burst-{burst}-unreadable.png");
                }

                if (examples.Count < 6)
                {
                    examples.Add($"    burst {burst}: gaps [{string.Join(' ', gaps)}] ms, last target {target}: screen {composite.ScreenFrame}, camera {composite.CameraFrame} (want {target - TestMedia.CameraFrameLag}); session positions screen {_engine.Screen.Player.PlaybackSession.Position.TotalSeconds.F("0.0000")} s camera {_engine.Camera.Player.PlaybackSession.Position.TotalSeconds.F("0.0000")} s, want {MidFrame(target).TotalSeconds.F("0.0000")} / {(MidFrame(target).TotalSeconds - TestMedia.CameraStartOffsetSeconds).F("0.0000")} s");
                }

                // Leave the players in a known state for the next burst.
                SeekAndWait(MidFrame(target), target, target - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(2));
            }
        }

        _report.Line($"  bursts where the final frame was wrong 2 s after the last change: screen {wrongScreen}, camera {wrongCamera} of {bursts}; when right, settled in {settle.Summary()}");
        foreach (var example in examples)
        {
            _report.Line(example);
        }

        if (wrongScreen + wrongCamera > 0)
        {
            _failures++;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Lost seeks
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A paused seek sometimes produces no frame at all. This measures how often, whether it
    /// depends on how soon the seek follows the previous one, and which remedy brings the frame back.
    /// </summary>
    private void SeekRace()
    {
        var perGap = _options.GetInt("race-seeks", 250);
        _report.Section($"Lost seeks: one seek at a time, the next issued G ms after both frames of the previous one arrived ({perGap} per G)");
        var remedies = new Dictionary<string, int>();
        var examples = new List<string>();
        var frameAfterSeekCompleted = new Samples();
        var goodWithoutSeekCompleted = 0;
        var goodTracks = 0;
        var lostTracks = 0;
        var lostWithSeekCompleted = 0;
        var lostSeekCompletedMs = new Samples();
        foreach (var gap in new[] { 0, 50, 150, 400 })
        {
            var random = new Random(1000 + gap);
            var previous = 100;
            SeekAndWait(MidFrame(previous), previous, previous - Lag, TimeSpan.FromSeconds(2));
            var lostSeeks = 0;
            var screenSilent = 0;
            var screenStale = 0;
            var cameraSilent = 0;
            var cameraStale = 0;
            var unreadable = 0;
            var latency = new Samples();
            for (var index = 0; index < perGap; index++)
            {
                int target;
                do
                {
                    target = random.Next(Lag + 1, TestMedia.FrameCount - 1);
                }
                while (target == previous);

                var result = SeekAndWait(MidFrame(target), target, target - Lag, TimeSpan.FromSeconds(1.2), settleMs: gap);
                var screenWrong = result.ScreenFrame != target;
                var cameraWrong = result.CameraFrame != target - Lag;

                // PlaybackSession.SeekCompleted: does it come, and how does it relate to the frame?
                foreach (var (wrong, frameMs, seekMs) in new[] { (screenWrong, result.ScreenLatencyMs, result.ScreenSeekCompletedMs), (cameraWrong, result.CameraLatencyMs, result.CameraSeekCompletedMs) })
                {
                    if (!wrong)
                    {
                        goodTracks++;
                        if (seekMs >= 0 && frameMs >= 0)
                        {
                            frameAfterSeekCompleted.Add(frameMs - seekMs);
                        }
                        else if (seekMs < 0)
                        {
                            goodWithoutSeekCompleted++;
                        }
                    }
                    else
                    {
                        lostTracks++;
                        if (seekMs >= 0)
                        {
                            lostWithSeekCompleted++;
                            lostSeekCompletedMs.Add(seekMs);
                        }
                    }
                }

                if (result.Correct)
                {
                    latency.Add(Math.Max(result.ScreenLatencyMs, result.CameraLatencyMs));
                }
                else
                {
                    lostSeeks++;
                    if (screenWrong)
                    {
                        _ = result.ScreenEvents == 0 ? screenSilent++ : screenStale++;
                    }

                    if (cameraWrong)
                    {
                        _ = result.CameraEvents == 0 ? cameraSilent++ : cameraStale++;
                    }

                    if (result.ScreenFrame < 0 || result.CameraFrame < 0)
                    {
                        unreadable++;
                        SavePng($"unreadable-{gap}-{index}.png");
                    }

                    var sessions = $"session positions screen {_engine.Screen.Player.PlaybackSession.Position.TotalSeconds.F("0.0000")} camera {_engine.Camera.Player.PlaybackSession.Position.TotalSeconds.F("0.0000")}, target {MidFrame(target).TotalSeconds.F("0.0000")}; SeekCompleted screen {(result.ScreenSeekCompletedMs < 0 ? "not raised" : result.ScreenSeekCompletedMs.F("0") + " ms")}, camera {(result.CameraSeekCompletedMs < 0 ? "not raised" : result.CameraSeekCompletedMs.F("0") + " ms")}";
                    var remedy = Recover(target, screenWrong, cameraWrong);
                    remedies[remedy] = remedies.GetValueOrDefault(remedy) + 1;
                    if (examples.Count < 8)
                    {
                        examples.Add($"  G={gap}: {previous}→{target}: screen {result.ScreenFrame} [{string.Join(' ', result.ScreenFramesSeen)}], camera {result.CameraFrame} [{string.Join(' ', result.CameraFramesSeen)}]; {sessions}; fixed by: {remedy}");
                    }
                }

                previous = target;
            }

            _report.Line($"G={gap,3} ms: {perGap} seeks, {lostSeeks} lost ({(lostSeeks * 100.0 / perGap).F("0.00")} %): screen {screenSilent} with no callback + {screenStale} with a wrong frame, camera {cameraSilent} + {cameraStale}; unreadable {unreadable}; when delivered, both arrived in {latency.Summary()}");
            _failures += lostSeeks > 0 ? 1 : 0;
        }

        foreach (var example in examples)
        {
            _report.Line(example);
        }

        _report.Line($"what brought the frame back (first remedy that worked, tried in this order: pull without a callback, same position, +1/4 frame, previous frame and back, 2 s away and back): {(remedies.Count == 0 ? "no seek was lost" : string.Join("; ", remedies.Select(pair => $"{pair.Key} ×{pair.Value}")))}");
        _report.Line($"PlaybackSession.SeekCompleted: on delivered seeks the frame callback started {frameAfterSeekCompleted.Summary()} min={frameAfterSeekCompleted.Min.F()} after it (negative = frame first); not raised for {goodWithoutSeekCompleted} of {goodTracks} delivered");
        _report.Line($"  on lost seeks it was raised for {lostWithSeekCompleted} of {lostTracks} players that produced no frame{(lostSeekCompletedMs.Count > 0 ? $", {lostSeekCompletedMs.Summary()} after the position change" : string.Empty)}");
    }

    private string Recover(int target, bool screenWrong, bool cameraWrong)
    {
        var expectedCamera = target - TestMedia.CameraFrameLag;
        bool Good(CompositeEvent c) => c.ScreenFrame == target && c.CameraFrame == expectedCamera;

        // 1. Maybe the player has the frame and only the event went missing: pull it.
        try
        {
            lock (_graphics.Gate)
            {
                if (screenWrong)
                {
                    _engine.Screen.Player.CopyFrameToVideoSurface(_engine.Screen.Surface);
                }

                if (cameraWrong)
                {
                    _engine.Camera.Player.CopyFrameToVideoSurface(_engine.Camera.Surface);
                }
            }

            if (Good(CompositeNow(identify: true)))
            {
                return "CopyFrameToVideoSurface without a callback";
            }
        }
        catch (Exception ex)
        {
            _report.Line($"  (pull without a callback threw 0x{ex.HResult:X8})");
        }

        // 2. The same position again.
        if (SeekAndWait(MidFrame(target), target, expectedCamera, TimeSpan.FromSeconds(1)).Correct)
        {
            return "same position assigned again";
        }

        // 3. A different position inside the same frame.
        var quarter = TimeSpan.FromTicks(TicksPerSecond / Fps / 4);
        if (SeekAndWait(MidFrame(target) + quarter, target, expectedCamera, TimeSpan.FromSeconds(1)).Correct)
        {
            return "position moved 1/4 frame (same frame)";
        }

        // 4. The neighbouring frame and back.
        SeekAndWait(MidFrame(target - 1), target - 1, expectedCamera - 1, TimeSpan.FromSeconds(1));
        if (SeekAndWait(MidFrame(target), target, expectedCamera, TimeSpan.FromSeconds(1)).Correct)
        {
            return "previous frame, then back";
        }

        // 5. Far away and back.
        var far = target > 450 ? target - 60 : target + 60;
        SeekAndWait(MidFrame(far), far, far - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(1));
        if (SeekAndWait(MidFrame(target), target, expectedCamera, TimeSpan.FromSeconds(1)).Correct)
        {
            return "2 s away, then back";
        }

        return "nothing worked";
    }

    // ---------------------------------------------------------------------------------------
    // Frame steps
    // ---------------------------------------------------------------------------------------

    private void Steps()
    {
        _report.Section("Single-frame steps while paused (Controller.Position = middle of the next/previous frame)");
        const int origin = 95;
        const int count = 50;
        SeekAndWait(MidFrame(origin), origin, origin - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(3));

        var forward = new List<SeekResult>();
        for (var step = 1; step <= count; step++)
        {
            forward.Add(SeekAndWait(MidFrame(origin + step), origin + step, origin + step - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(3), settleMs: 60));
        }

        SavePng("step-forward.png");
        ReportSeeks(forward, "forward steps");

        var backward = new List<SeekResult>();
        for (var step = count - 1; step >= 0; step--)
        {
            backward.Add(SeekAndWait(MidFrame(origin + step), origin + step, origin + step - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(3), settleMs: 60));
        }

        SavePng("step-backward.png");
        ReportSeeks(backward, "backward steps");
        var acrossKeyframe = backward.Where(r => r.TargetFrame % 60 == 59).Select(r => r.ScreenLatencyMs).ToList();
        if (acrossKeyframe.Count > 0)
        {
            _report.Line($"  backward step that lands on the last frame of the previous GOP (frame {backward.First(r => r.TargetFrame % 60 == 59).TargetFrame}): screen {acrossKeyframe[0].F("0")} ms");
        }
    }

    /// <summary>
    /// MediaPlayer has its own StepForwardOneFrame/StepBackwardOneFrame. They would be attractive
    /// for forward steps (decode one more frame instead of seeking from the keyframe) if they
    /// behave under a timeline controller.
    /// </summary>
    private void StepMethods()
    {
        _report.Section("MediaPlayer.StepForwardOneFrame / StepBackwardOneFrame on players attached to the controller");
        const int origin = 200;
        SeekAndWait(MidFrame(origin), origin, origin - Lag, TimeSpan.FromSeconds(3));
        var controllerBefore = _engine.Controller.Position;

        // Forward, at scale: every step checked from pixels.
        const int forwardSteps = 300;
        var forward = RunSteps(forwardSteps, forward: true);
        if (forward.Failure is not null)
        {
            _report.Line($"StepForwardOneFrame: {forward.Failure}");
            return;
        }

        _report.Line($"StepForwardOneFrame ×{forwardSteps} on both players (crossing {forwardSteps / 60} keyframes): screen moved by (frames) {Fmt.Histogram(forward.ScreenDeltas)}; camera {Fmt.Histogram(forward.CameraDeltas)}; clips apart afterwards {Fmt.Histogram(forward.Apart)}");
        _report.Line($"  both callbacks after {forward.Latency.Summary()}; steps with a missing callback: {forward.NoCallback}");
        _failures += forward.ScreenDeltas.Any(d => d != 1) || forward.CameraDeltas.Any(d => d != 1) ? 1 : 0;

        var shown = CompositeNow(identify: true);
        _report.Line($"  Controller.Position before the steps {controllerBefore.TotalSeconds.F("0.0000")} s, after {_engine.Controller.Position.TotalSeconds.F("0.0000")} s (it does not follow); session positions screen {_engine.Screen.Player.PlaybackSession.Position.TotalSeconds.F("0.0000")} s, camera {_engine.Camera.Player.PlaybackSession.Position.TotalSeconds.F("0.0000")} s; showing screen {shown.ScreenFrame}, camera {shown.CameraFrame}");

        // Bring the controller to where the players are, then play: does it carry on from the stepped frame?
        DrainFrameEvents();
        _engine.Controller.Position = MidFrame(shown.ScreenFrame);
        Thread.Sleep(300);
        var afterSync = CompositeNow(identify: true);
        var syncCallbacks = DrainFrameEvents().Count;
        _report.Line($"  Controller.Position = middle of frame {shown.ScreenFrame}: showing screen {afterSync.ScreenFrame}, camera {afterSync.CameraFrame} ({syncCallbacks} callbacks) {Verdict(afterSync.ScreenFrame == shown.ScreenFrame && afterSync.CameraFrame == shown.CameraFrame)}");
        _engine.Controller.Resume();
        Thread.Sleep(300);
        _engine.Controller.Pause();
        Thread.Sleep(300);
        var resumed = DrainFrameEvents().Where(e => e.Track == 0 && e.Frame >= 0).OrderBy(e => e.Timestamp).Select(e => e.Frame).ToList();
        var resumedOk = resumed.Count > 0 && resumed.First() == shown.ScreenFrame + 1;
        _report.Line($"  Resume() for 300 ms after that: screen frames delivered {(resumed.Count == 0 ? "(none)" : $"{resumed.First()}..{resumed.Last()}")} {Verdict(resumedOk)}");
        _failures += resumedOk ? 0 : 1;

        // Without that assignment the controller resumes from its own stale position.
        var here = CompositeNow(identify: true).ScreenFrame;
        SeekAndWait(MidFrame(here), here, here - Lag, TimeSpan.FromSeconds(3));
        RunSteps(10, forward: true);
        DrainFrameEvents();
        _engine.Controller.Resume();
        Thread.Sleep(300);
        _engine.Controller.Pause();
        Thread.Sleep(300);
        var stale = DrainFrameEvents().Where(e => e.Track == 0 && e.Frame >= 0).OrderBy(e => e.Timestamp).Select(e => e.Frame).ToList();
        _report.Line($"  10 more steps from frame {here}, then Resume() without assigning the position: screen frames delivered {(stale.Count == 0 ? "(none)" : $"{stale.First()}..{stale.Last()}")} (the players were showing {here + 10}; the controller was still in frame {here})");

        // Backward.
        const int backwardSteps = 60;
        var at = CompositeNow(identify: true).ScreenFrame;
        SeekAndWait(MidFrame(at), at, at - Lag, TimeSpan.FromSeconds(3));
        var backward = RunSteps(backwardSteps, forward: false);
        if (backward.Failure is not null)
        {
            _report.Line($"StepBackwardOneFrame: {backward.Failure}");
            return;
        }

        _report.Line($"StepBackwardOneFrame ×{backwardSteps} on both players: screen moved by (frames) {Fmt.Histogram(backward.ScreenDeltas)}; camera {Fmt.Histogram(backward.CameraDeltas)}; clips apart afterwards {Fmt.Histogram(backward.Apart)}");
        _report.Line($"  both callbacks after {backward.Latency.Summary()}; steps with a missing callback: {backward.NoCallback}");
    }

    private sealed record StepRun(List<int> ScreenDeltas, List<int> CameraDeltas, List<int> Apart, Samples Latency, int NoCallback, string? Failure);

    private StepRun RunSteps(int count, bool forward)
    {
        var screenDeltas = new List<int>();
        var cameraDeltas = new List<int>();
        var apart = new List<int>();
        var latency = new Samples();
        var noCallback = 0;
        for (var step = 0; step < count; step++)
        {
            var before = CompositeNow(identify: true);
            var screenCount = _engine.Screen.FrameCount;
            var cameraCount = _engine.Camera.FrameCount;
            var start = Stopwatch.GetTimestamp();
            try
            {
                foreach (var track in _engine.Tracks)
                {
                    if (forward)
                    {
                        track.Player.StepForwardOneFrame();
                    }
                    else
                    {
                        track.Player.StepBackwardOneFrame();
                    }
                }
            }
            catch (Exception ex)
            {
                return new StepRun(screenDeltas, cameraDeltas, apart, latency, noCallback, $"threw {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message.Trim()}");
            }

            while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 800 && (_engine.Screen.FrameCount == screenCount || _engine.Camera.FrameCount == cameraCount))
            {
                PreciseTimer.Sleep(0.25);
            }

            if (_engine.Screen.FrameCount == screenCount || _engine.Camera.FrameCount == cameraCount)
            {
                noCallback++;
            }
            else
            {
                // Until both frames are in their textures (the callbacks have finished copying).
                latency.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            PreciseTimer.Sleep(15);
            var after = CompositeNow(identify: true);
            screenDeltas.Add(after.ScreenFrame - before.ScreenFrame);
            cameraDeltas.Add(after.CameraFrame - before.CameraFrame);
            apart.Add(after.ScreenFrame - after.CameraFrame - Lag);
        }

        return new StepRun(screenDeltas, cameraDeltas, apart, latency, noCallback, null);
    }

    // ---------------------------------------------------------------------------------------
    // The seek policy (SeekCoordinator) checked at scale
    // ---------------------------------------------------------------------------------------

    private SeekCoordinator CreateCoordinator(int frame)
    {
        SeekAndWait(MidFrame(frame), frame, frame - Lag, TimeSpan.FromSeconds(3));
        var coordinator = new SeekCoordinator(_engine, Fps, TestMedia.FrameCount, TestMedia.FrameCount, Lag)
        {
            Timeout = TimeSpan.FromMilliseconds(_options.GetInt("seek-timeout-ms", 500)),
            UseSeekCompleted = !_options.Flag("no-seek-completed"),
        };
        coordinator.Reset(frame);
        return coordinator;
    }

    private void CoordinatorSeeks()
    {
        var count = _options.GetInt("coordinator-seeks", 800);
        _report.Section($"Seek policy (one seek at a time + watchdog repair): {count} random seeks, each checked from pixels");
        using var coordinator = CreateCoordinator(100);
        var random = new Random(99);
        var wrong = 0;
        var notSettled = 0;
        var previous = 100;
        for (var index = 0; index < count; index++)
        {
            int target;
            do
            {
                // Includes positions before the camera's first frame (timeline frames 0..5).
                target = random.Next(0, TestMedia.FrameCount);
            }
            while (target == previous);

            coordinator.Request(target);
            if (!coordinator.WaitUntilSettled(target, TimeSpan.FromSeconds(5)))
            {
                notSettled++;
            }

            // Idle for a while between seeks, as clicks on a timeline would.
            PreciseTimer.Sleep(random.Next(0, 250));
            var composite = CompositeNow(identify: true);
            var expectedCamera = Math.Max(0, target - Lag);
            if (composite.ScreenFrame != target || composite.CameraFrame != expectedCamera)
            {
                wrong++;
                if (wrong <= 5)
                {
                    _report.Line($"  WRONG: {previous}→{target}: screen {composite.ScreenFrame}, camera {composite.CameraFrame} (want {expectedCamera})");
                }
            }

            previous = target;
        }

        _report.Line($"{count} requests: {count - wrong} show the requested frame on both clips ({((count - wrong) * 100.0 / count).F("0.00")} %), {wrong} wrong, {notSettled} not settled within 5 s");
        _report.Line($"positions assigned {coordinator.SeeksIssued}; requests that needed the repair {coordinator.Repairs} ({(coordinator.Repairs * 100.0 / count).F("0.00")} %), {coordinator.LossesSeenEarly} of them recognised from SeekCompleted; still without a frame after 3 repairs {coordinator.RepairFailures}");
        _report.Line($"request → both frames, all requests: {coordinator.LatencyMs.Summary()}");
        _report.Line($"request → both frames, repaired requests only: {coordinator.RepairedLatencyMs.Summary()}");
        _failures += wrong > 0 ? 1 : 0;
    }

    private void CoordinatorSteps()
    {
        const int count = 150;
        _report.Section($"Seek policy: {count} single-frame steps forward, then {count} back, each checked from pixels");
        const int origin = 300;
        using var coordinator = CreateCoordinator(origin);
        var wrong = 0;
        var frame = origin;
        for (var index = 0; index < count * 2; index++)
        {
            frame += index < count ? 1 : -1;
            coordinator.Request(frame);
            coordinator.WaitUntilSettled(frame, TimeSpan.FromSeconds(5));
            var composite = CompositeNow(identify: true);
            if (composite.ScreenFrame != frame || composite.CameraFrame != frame - Lag)
            {
                wrong++;
                if (wrong <= 5)
                {
                    _report.Line($"  WRONG at step {index}: want {frame}: screen {composite.ScreenFrame}, camera {composite.CameraFrame}");
                }
            }
        }

        _report.Line($"{count * 2} steps: {count * 2 - wrong} correct, {wrong} wrong; repairs {coordinator.Repairs} ({coordinator.LossesSeenEarly} recognised from SeekCompleted), unrepaired {coordinator.RepairFailures}");
        _report.Line($"step → both frames, all steps: {coordinator.LatencyMs.Summary()}");
        _report.Line($"step → both frames, repaired steps only: {coordinator.RepairedLatencyMs.Summary()}");
        _failures += wrong > 0 ? 1 : 0;
    }

    /// <summary>A playhead drag: requests at 60 Hz that never wait, with pauses where the result is checked.</summary>
    private void CoordinatorScrub()
    {
        var seconds = _options.GetDouble("scrub-seconds", 60);
        _report.Section($"Seek policy under a drag: requests every 16.7 ms for {seconds:0} s (random walk with jumps), checked from pixels whenever the drag rests");
        using var coordinator = CreateCoordinator(300);
        var random = new Random(31337);
        var frame = 300;
        var rests = 0;
        var wrong = 0;
        var settle = new Samples();
        var screenBefore = _engine.Screen.FrameCount;
        var cameraBefore = _engine.Camera.FrameCount;
        var requests = 0;
        double draggingMs = 0;
        var total = Stopwatch.StartNew();
        while (total.Elapsed.TotalSeconds < seconds)
        {
            // Drag for a while…
            var dragFor = 300 + random.Next(1200);
            var drag = Stopwatch.StartNew();
            var velocity = random.Next(-6, 7);
            while (drag.ElapsedMilliseconds < dragFor)
            {
                if (random.Next(100) < 3)
                {
                    frame = random.Next(Lag + 1, TestMedia.FrameCount - 1);
                    velocity = random.Next(-6, 7);
                }
                else
                {
                    frame = Math.Clamp(frame + velocity, Lag + 1, TestMedia.FrameCount - 2);
                }

                coordinator.Request(frame);
                requests++;
                PreciseTimer.Sleep(1000.0 / 60);
            }

            draggingMs += drag.Elapsed.TotalMilliseconds;

            // …then rest and check what is on screen.
            var rest = Stopwatch.StartNew();
            var settled = coordinator.WaitUntilSettled(frame, TimeSpan.FromSeconds(5));
            settle.Add(rest.Elapsed.TotalMilliseconds);
            PreciseTimer.Sleep(20);
            var composite = CompositeNow(identify: true);
            rests++;
            if (!settled || composite.ScreenFrame != frame || composite.CameraFrame != frame - Lag)
            {
                wrong++;
                if (wrong <= 5)
                {
                    _report.Line($"  WRONG at rest {rests}: want {frame}: screen {composite.ScreenFrame}, camera {composite.CameraFrame}, settled {settled}");
                    if (composite.ScreenFrame < 0 || composite.CameraFrame < 0)
                    {
                        SavePng($"scrub-rest-{rests}-unreadable.png");
                    }
                }
            }
        }

        var screenFrames = _engine.Screen.FrameCount - screenBefore;
        var cameraFrames = _engine.Camera.FrameCount - cameraBefore;
        _report.Line($"{requests} requests, {coordinator.SeeksIssued} positions assigned ({(coordinator.SeeksIssued * 100.0 / requests).F("0")} % of requests; the rest were superseded while a seek was in flight)");
        _report.Line($"frames delivered: screen {screenFrames} ({(screenFrames / (total.Elapsed.TotalSeconds)).F("0.0")}/s overall), camera {cameraFrames}; repairs {coordinator.Repairs} ({coordinator.LossesSeenEarly} recognised from SeekCompleted), unrepaired {coordinator.RepairFailures}");
        _report.Line($"{rests} rests: {rests - wrong} show the last requested frame on both clips, {wrong} wrong; time from the last request to settled: {settle.Summary()}");
        _report.Line($"request → both frames while dragging (repairs included): {coordinator.LatencyMs.Summary()}");
        _failures += wrong > 0 ? 1 : 0;
    }
}
