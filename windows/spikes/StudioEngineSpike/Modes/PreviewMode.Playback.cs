using System.Diagnostics;
using System.Globalization;
using StudioEngineSpike.Engine;

namespace StudioEngineSpike.Modes;

/// <summary>One playback measurement.</summary>
/// <param name="IdentifyCallbacks">
/// Read the strip in every VideoFrameAvailable callback. Gives exact per-clip drop accounting but
/// adds a GPU sync of about 5 ms per 1440p frame to the callback, which matters at 60 frames/s.
/// </param>
/// <param name="IdentifyComposites">Read both strips out of every composited frame (what a viewer would see).</param>
/// <param name="Composite">Run the render thread at all.</param>
/// <param name="PairingWindowMs">How long the render thread waits for the other clip's frame before compositing.</param>
internal sealed record PlayRun(
    string Title,
    double Rate,
    double StartSeconds,
    double WallSeconds,
    bool IdentifyCallbacks,
    bool IdentifyComposites,
    bool Composite = true,
    double PairingWindowMs = 0,
    string? PngName = null);

internal sealed partial class PreviewSession
{
    private void Play(PlayRun run)
    {
        _report.Section(run.Title);
        var startFrame = (int)(run.StartSeconds * Fps);
        SeekAndWait(MidFrame(startFrame), startFrame, startFrame - Lag, TimeSpan.FromSeconds(3));
        DrainFrameEvents();
        DrainComposites();
        while (_engine.StateChanges.TryDequeue(out _))
        {
        }

        _engine.IdentifyFrames = run.IdentifyCallbacks;
        _identifyComposites = run.IdentifyComposites;
        _pairingWindowMs = run.PairingWindowMs;
        _renderPaused = !run.Composite;
        Interlocked.Exchange(ref _pairingTimeouts, 0);
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var screenBefore = _engine.Screen.FrameCount;
        var cameraBefore = _engine.Camera.FrameCount;
        var positionBefore = _engine.Controller.Position;

        _engine.Controller.ClockRate = run.Rate;
        var wall = Stopwatch.StartNew();
        _engine.Controller.Resume();

        if (run.PngName is not null)
        {
            // Grab the composite halfway through with a GPU-side copy only; the readback and PNG
            // encode wait until playback has been measured.
            Thread.Sleep(TimeSpan.FromSeconds(run.WallSeconds / 2));
            Snapshot();
            Thread.Sleep(TimeSpan.FromSeconds(Math.Max(0, run.WallSeconds - wall.Elapsed.TotalSeconds)));
        }
        else
        {
            Thread.Sleep(TimeSpan.FromSeconds(run.WallSeconds));
        }

        _engine.Controller.Pause();
        var pauseTimestamp = Stopwatch.GetTimestamp();
        wall.Stop();
        var positionAfter = _engine.Controller.Position;
        Thread.Sleep(600);
        process.Refresh();
        var cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds;

        var events = DrainFrameEvents();
        var composites = DrainComposites();
        var pairingTimeouts = Interlocked.Read(ref _pairingTimeouts);
        _engine.Controller.ClockRate = 1.0;
        _engine.IdentifyFrames = true;
        _identifyComposites = true;
        _pairingWindowMs = 0;
        _renderPaused = false;

        var mediaSeconds = (positionAfter - positionBefore).TotalSeconds;
        var expectedFrames = mediaSeconds * Fps;
        _report.Line($"wall {wall.Elapsed.TotalSeconds.F("0.00")} s, controller advanced {mediaSeconds.F("0.000")} s (measured rate {(mediaSeconds / wall.Elapsed.TotalSeconds).F("0.000")}), {expectedFrames.F("0")} source frames per clip, {(expectedFrames / wall.Elapsed.TotalSeconds).F("0.0")} frames/s wanted");
        _report.Line($"process CPU: {(cpuSeconds / wall.Elapsed.TotalSeconds * 100).F("0")} % of one core; controller states: {string.Join(", ", _engine.StateChanges.Select(change => change.State.ToString()))}");

        foreach (var track in _engine.Tracks)
        {
            var delivered = track.Index == 0 ? _engine.Screen.FrameCount - screenBefore : _engine.Camera.FrameCount - cameraBefore;
            var trackEvents = events.Where(e => e.Track == track.Index).OrderBy(e => e.Timestamp).ToList();
            var playing = trackEvents.Where(e => e.Timestamp <= pauseTimestamp).ToList();
            var intervals = new Samples();
            for (var index = 1; index < playing.Count; index++)
            {
                intervals.Add(Stopwatch.GetElapsedTime(playing[index - 1].Timestamp, playing[index].Timestamp).TotalMilliseconds);
            }

            var copy = new Samples();
            var read = new Samples();
            foreach (var e in playing)
            {
                copy.Add(e.CopyMs);
                read.Add(e.ReadMs);
            }

            var longGaps = intervals.Values.Count(ms => ms > 1.6 * 1000.0 / Fps / run.Rate);
            _report.Line($"{track.Name}: {delivered} callbacks = {(delivered / expectedFrames * 100).F("0.0")} % of source frames ({trackEvents.Count - playing.Count} after Pause()); interval {intervals.Summary()}, {longGaps} longer than 1.6 frame times");
            _report.Line($"  CopyFrameToVideoSurface (CPU side): {copy.Summary()}");
            if (!run.IdentifyCallbacks)
            {
                continue;
            }

            _report.Line($"  strip readback, i.e. until the GPU finished that copy: {read.Summary()}");
            var valid = playing.Where(e => e.Frame >= 0).Select(e => e.Frame).ToList();
            if (valid.Count < 2)
            {
                continue;
            }

            var distinct = valid.Distinct().Count();
            var span = valid.Max() - valid.Min() + 1;
            var backwards = valid.Zip(valid.Skip(1), (a, b) => b < a).Count(x => x);
            var missing = Enumerable.Range(valid.Min(), span).Except(valid).ToList();
            _report.Line($"  frames {valid.Min()}..{valid.Max()}: {distinct} distinct of {span} => {missing.Count} dropped ({(missing.Count * 100.0 / span).F("0.00")} %), {valid.Count - distinct} repeated, {backwards} out of order, {playing.Count - valid.Count} unreadable");
            if (missing.Count > 0)
            {
                _report.Line($"  dropped: {string.Join(' ', missing.Take(30))}{(missing.Count > 30 ? " …" : string.Empty)}");
            }

            // How late (in media time) each frame reached us relative to the controller clock, and
            // whether PlaybackSession.Position at the callback names the frame that was delivered.
            var lag = track.Index == 1 ? Lag : 0;
            var lateness = new Samples();
            var sessionNamesFrame = 0;
            foreach (var e in playing.Where(e => e.Frame >= 0))
            {
                lateness.Add((e.ControllerTicks - ((long)(e.Frame + lag) * TicksPerSecond / Fps)) / 10_000.0);
                if ((int)Math.Floor(e.SessionTicks * (double)Fps / TicksPerSecond) == e.Frame)
                {
                    sessionNamesFrame++;
                }
            }

            _report.Line($"  controller position − frame start, at the callback (media ms): {lateness.Summary()} min={lateness.Min.F()}");
            _report.Line($"  floor(PlaybackSession.Position × fps) at the callback equals the frame in the pixels for {sessionNamesFrame} of {valid.Count} callbacks ({(sessionNamesFrame * 100.0 / valid.Count).F("0.0")} %)");
        }

        if (run.IdentifyComposites && run.Composite)
        {
            ReportSync(events, composites, pauseTimestamp, run, pairingTimeouts);
            var final = CompositeNow(identify: true);
            var expected = (int)Math.Floor(positionAfter.TotalSeconds * Fps);
            _report.Line($"after Pause() at {positionAfter.TotalSeconds.F("0.0000")} s (inside frame {expected}): screen {final.ScreenFrame}, camera {final.CameraFrame} → clips {final.ScreenFrame - final.CameraFrame - Lag} frame(s) apart, screen {final.ScreenFrame - expected} frame(s) from the clock");
        }
        else if (run.Composite)
        {
            var total = new Samples();
            foreach (var c in composites.Where(c => c.Timestamp <= pauseTimestamp))
            {
                total.Add(c.TotalMs);
            }

            _report.Line($"composites: {total.Count} ({(total.Count / wall.Elapsed.TotalSeconds).F("0.0")}/s), each incl. GPU wait {total.Summary()}");
        }

        if (events.Count > 0)
        {
            var origin = events.Min(e => e.Timestamp);
            File.WriteAllLines(Path.Combine(_output, Slug(run.Title) + "-frames.csv"), new[] { "track,ms,controller_s,session_s,frame,copy_ms,read_ms" }.Concat(events.OrderBy(e => e.Timestamp).Select(e =>
                string.Create(CultureInfo.InvariantCulture, $"{e.Track},{Stopwatch.GetElapsedTime(origin, e.Timestamp).TotalMilliseconds:0.000},{e.ControllerTicks / 1e7:0.0000},{e.SessionTicks / 1e7:0.0000},{e.Frame},{e.CopyMs:0.000},{e.ReadMs:0.000}"))));
        }

        if (run.PngName is not null)
        {
            SaveSnapshot(run.PngName + "-mid.png");
        }
    }

    /// <summary>How far apart the two clips were while playing: by arrival time and by what was composited.</summary>
    private void ReportSync(List<FrameEvent> events, List<CompositeEvent> composites, long pauseTimestamp, PlayRun run, long pairingTimeouts)
    {
        // (1) Arrival skew: when did the camera's frame N−6 arrive relative to the screen's frame N?
        if (run.IdentifyCallbacks)
        {
            var screenArrival = new Dictionary<int, long>();
            var cameraArrival = new Dictionary<int, long>();
            foreach (var e in events.Where(e => e.Frame >= 0 && e.Timestamp <= pauseTimestamp))
            {
                (e.Track == 0 ? screenArrival : cameraArrival).TryAdd(e.Frame, e.Timestamp);
            }

            var skew = new Samples();
            var absoluteSkew = new Samples();
            foreach (var (frame, timestamp) in screenArrival)
            {
                if (cameraArrival.TryGetValue(frame - Lag, out var cameraTimestamp))
                {
                    var milliseconds = (cameraTimestamp - timestamp) * 1000.0 / Stopwatch.Frequency;
                    skew.Add(milliseconds);
                    absoluteSkew.Add(Math.Abs(milliseconds));
                }
            }

            _report.Line($"sync by arrival: the camera's matching frame starts its callback {skew.Mean.F()} ms after the screen's on average; |skew| {absoluteSkew.Summary()} over {skew.Count} pairs");
        }

        // (2) What a viewer sees: frame numbers decoded from every composited frame.
        var playing = composites.Where(c => c.Timestamp <= pauseTimestamp).OrderBy(c => c.Timestamp).ToList();
        var readable = playing.Where(c => c.ScreenFrame >= 0 && c.CameraFrame >= 0).ToList();
        if (readable.Count < 2)
        {
            _report.Line("sync as composited: too few composites");
            return;
        }

        var offsets = readable.Select(c => c.ScreenFrame - c.CameraFrame - Lag).ToList();
        var total = new Samples();
        foreach (var c in playing)
        {
            total.Add(c.TotalMs);
        }

        // Time-weighted: a mismatched composite stays up until the next composite replaces it.
        double mismatchedMs = 0;
        double longestMs = 0;
        double currentMs = 0;
        for (var index = 0; index < readable.Count - 1; index++)
        {
            var duration = (readable[index + 1].Timestamp - readable[index].Timestamp) * 1000.0 / Stopwatch.Frequency;
            if (offsets[index] != 0)
            {
                mismatchedMs += duration;
                currentMs += duration;
                longestMs = Math.Max(longestMs, currentMs);
            }
            else
            {
                currentMs = 0;
            }
        }

        var spanMs = (readable[^1].Timestamp - readable[0].Timestamp) * 1000.0 / Stopwatch.Frequency;
        var screenFrames = readable.Select(c => c.ScreenFrame).Distinct().Count();
        var screenSpan = readable.Max(c => c.ScreenFrame) - readable.Min(c => c.ScreenFrame) + 1;
        _report.Line($"sync as composited{(run.PairingWindowMs > 0 ? $" (pairing window {run.PairingWindowMs.F("0")} ms, expired {pairingTimeouts} times)" : string.Empty)}: {playing.Count} composites ({(playing.Count / (spanMs / 1000)).F("0.0")}/s), {playing.Count - readable.Count} unreadable; clips apart by (frames): {Fmt.Histogram(offsets)}");
        _report.Line($"  time with the clips on different frames: {mismatchedMs.F("0.0")} ms of {spanMs.F("0")} ms = {(mismatchedMs / spanMs * 100).F("0.00")} %, longest stretch {longestMs.F("0.0")} ms");
        _report.Line($"  screen frames that reached a composite: {screenFrames} of {screenSpan} ({((screenSpan - screenFrames) * 100.0 / screenSpan).F("0.00")} % never shown)");
        _report.Line($"  composite incl. GPU wait: {total.Summary()}");

        // What a display would have latched at each of its refreshes.
        foreach (var hertz in new[] { 60.0, 100.0 })
        {
            var period = Stopwatch.Frequency / hertz;
            var sampled = new List<int>();
            var cursor = 0;
            for (var tick = readable[0].Timestamp + period; tick < readable[^1].Timestamp; tick += period)
            {
                while (cursor + 1 < readable.Count && readable[cursor + 1].Timestamp <= tick)
                {
                    cursor++;
                }

                sampled.Add(offsets[cursor]);
            }

            _report.Line($"  latched at {hertz:0} Hz refreshes: {Fmt.Histogram(sampled)} → {(sampled.Count(o => o != 0) * 100.0 / Math.Max(1, sampled.Count)).F("0.00")} % of refreshes show mismatched clips");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Pause
    // ---------------------------------------------------------------------------------------

    private void PauseCycles()
    {
        const int cycles = 40;
        _report.Section($"Pause: {cycles} times play for a random 0.5–1.4 s, Pause(), wait 500 ms, read the composite");
        var random = new Random(12345);
        SeekAndWait(MidFrame(90), 90, 90 - Lag, TimeSpan.FromSeconds(3));
        var between = new List<int>();
        var againstClock = new List<int>();
        var againstSession = new List<int>();
        var late = new List<int>();
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            if (_engine.Controller.Position.TotalSeconds > 24)
            {
                // Stay clear of the end of the clips; that case is covered by the "edges" test.
                SeekAndWait(MidFrame(90), 90, 90 - Lag, TimeSpan.FromSeconds(3));
            }

            DrainFrameEvents();
            _engine.Controller.Resume();
            Thread.Sleep(500 + random.Next(900));
            var pause = Stopwatch.GetTimestamp();
            _engine.Controller.Pause();
            var position = _engine.Controller.Position;
            Thread.Sleep(500);
            var events = DrainFrameEvents();
            late.Add(events.Count(e => e.Timestamp > pause));

            var composite = CompositeNow(identify: true);
            var expected = (int)Math.Floor(position.TotalSeconds * Fps);
            between.Add(composite.ScreenFrame - composite.CameraFrame - Lag);
            againstClock.Add(composite.ScreenFrame - expected);

            // What the app can know without reading pixels: the session position at the last callback.
            var lastScreen = events.Where(e => e.Track == 0).OrderBy(e => e.Timestamp).Cast<FrameEvent?>().LastOrDefault();
            if (lastScreen is { } last)
            {
                againstSession.Add(composite.ScreenFrame - (int)Math.Floor(last.SessionTicks * (double)Fps / TicksPerSecond));
            }

            if (cycle == 0)
            {
                SavePng("pause.png");
            }
        }

        _report.Line($"clips apart after the pause (frames): {Fmt.Histogram(between)}");
        _report.Line($"frame on screen − frame containing Controller.Position: {Fmt.Histogram(againstClock)}");
        _report.Line($"frame on screen − floor(PlaybackSession.Position × fps) taken at the last callback: {Fmt.Histogram(againstSession)}");
        _report.Line($"callbacks arriving after Pause() returned: {Fmt.Histogram(late)}");
        if (between.Any(offset => offset != 0))
        {
            _report.Line("=> a bare Pause() can leave the clips on different frames, and the picture one frame behind the clock");
        }

        PauseWithSnap();
    }

    /// <summary>
    /// The remedy for the above: after Pause(), note which frame each player shows (from its session
    /// position at its last callback) and seek both, through the seek policy, to the screen's frame.
    /// </summary>
    private void PauseWithSnap()
    {
        const int cycles = 60;
        var random = new Random(54321);
        SeekAndWait(MidFrame(90), 90, 90 - Lag, TimeSpan.FromSeconds(3));
        using var coordinator = new SeekCoordinator(_engine, Fps, TestMedia.FrameCount, TestMedia.FrameCount, Lag);
        var wrong = 0;
        var clockOff = 0;
        var snaps = new Samples();
        var needed = 0;
        _engine.IdentifyFrames = false;
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            if (_engine.Controller.Position.TotalSeconds > 24)
            {
                SeekAndWait(MidFrame(90), 90, 90 - Lag, TimeSpan.FromSeconds(3));
            }

            DrainFrameEvents();
            _engine.Controller.Resume();
            Thread.Sleep(500 + random.Next(900));
            _engine.Controller.Pause();

            // Let any callback that was already on its way land (longest seen: one display refresh).
            Thread.Sleep(60);
            var events = DrainFrameEvents();
            var lastScreen = events.Where(e => e.Track == 0).OrderBy(e => e.Timestamp).Last();
            var lastCamera = events.Where(e => e.Track == 1).OrderBy(e => e.Timestamp).Last();
            var screenShown = (int)Math.Floor(lastScreen.SessionTicks * (double)Fps / TicksPerSecond);
            var cameraShown = (int)Math.Floor(lastCamera.SessionTicks * (double)Fps / TicksPerSecond);
            if (cameraShown != screenShown - Lag || (int)Math.Floor(_engine.Controller.Position.TotalSeconds * Fps) != screenShown)
            {
                needed++;
            }

            var start = Stopwatch.GetTimestamp();
            coordinator.ResetAfterPlayback(screenShown, cameraShown);
            coordinator.Request(screenShown);
            coordinator.WaitUntilSettled(screenShown, TimeSpan.FromSeconds(3));
            snaps.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);

            PreciseTimer.Sleep(30);
            var composite = CompositeNow(identify: true);
            if (composite.ScreenFrame != screenShown || composite.CameraFrame != screenShown - Lag)
            {
                wrong++;
                if (wrong <= 5)
                {
                    _report.Line($"  WRONG after snap {cycle}: want {screenShown}: screen {composite.ScreenFrame}, camera {composite.CameraFrame}");
                }
            }

            if ((int)Math.Floor(_engine.Controller.Position.TotalSeconds * Fps) != composite.ScreenFrame)
            {
                clockOff++;
            }
        }

        _engine.IdentifyFrames = true;
        _report.Line($"Pause() then snap to the frame the screen shows, {cycles} times: clips on matching frames and equal to the snapped frame in {cycles - wrong}; Controller.Position inside the frame on screen in {cycles - clockOff}");
        _report.Line($"  pauses where the snap had something to fix (clips apart, or clock in a different frame): {needed}; snap took {snaps.Summary()}; repairs {coordinator.Repairs}");
        _failures += wrong > 0 || clockOff > 0 ? 1 : 0;
    }

    // ---------------------------------------------------------------------------------------
    // Seeking without pausing
    // ---------------------------------------------------------------------------------------

    private void SeeksDuringPlayback()
    {
        const int count = 60;
        _report.Section($"Seek during playback: {count} position changes 0.5–1.1 s apart while running at 1x");
        var random = new Random(4242);
        SeekAndWait(MidFrame(100), 100, 100 - Lag, TimeSpan.FromSeconds(3));
        _engine.IdentifyFrames = false;
        while (_engine.StateChanges.TryDequeue(out _))
        {
        }

        _engine.Controller.Resume();
        Thread.Sleep(700);
        var resync = new Samples();
        var firstFrame = new Samples();
        var never = 0;
        var staleShown = 0;
        for (var index = 0; index < count; index++)
        {
            var target = random.Next(60, 700);
            DrainComposites();
            var start = Stopwatch.GetTimestamp();
            _engine.Controller.Position = MidFrame(target);

            // Watch the composites: when is the first one that shows both clips at the new place and in step?
            double firstNewMs = -1;
            double inSyncMs = -1;
            var deadline = TimeSpan.FromSeconds(2);
            while (Stopwatch.GetElapsedTime(start) < deadline && inSyncMs < 0)
            {
                while (_composites.TryDequeue(out var c))
                {
                    if (c.Timestamp < start || c.ScreenFrame < 0)
                    {
                        continue;
                    }

                    var elapsedFrames = Stopwatch.GetElapsedTime(start, c.Timestamp).TotalSeconds * Fps;
                    var atNewPlace = c.ScreenFrame >= target && c.ScreenFrame <= target + elapsedFrames + 3;
                    if (atNewPlace && firstNewMs < 0)
                    {
                        firstNewMs = Stopwatch.GetElapsedTime(start, c.Timestamp).TotalMilliseconds;
                    }

                    if (atNewPlace && c.CameraFrame == c.ScreenFrame - Lag)
                    {
                        inSyncMs = Stopwatch.GetElapsedTime(start, c.Timestamp).TotalMilliseconds;
                        break;
                    }
                }

                PreciseTimer.Sleep(1);
            }

            if (inSyncMs < 0)
            {
                never++;
            }
            else
            {
                resync.Add(inSyncMs);
                firstFrame.Add(firstNewMs);
            }

            // Then let it run: once resynced, do the clips stay together until the next seek?
            var runFor = 500 + random.Next(600);
            Thread.Sleep(runFor);
            var after = DrainComposites().Where(c => c.ScreenFrame >= 0 && c.CameraFrame >= 0 && Stopwatch.GetElapsedTime(start, c.Timestamp).TotalMilliseconds > Math.Max(inSyncMs, 0) + 100).ToList();
            staleShown += after.Count(c => Math.Abs(c.ScreenFrame - c.CameraFrame - Lag) > 1);
        }

        _engine.Controller.Pause();
        Thread.Sleep(400);
        _engine.IdentifyFrames = true;
        var states = _engine.StateChanges.Select(change => change.State).ToList();
        _report.Line($"first composite at the new position: {firstFrame.Summary()} after the position change");
        _report.Line($"first composite with both clips at the new position and on matching frames: {resync.Summary()}; never within 2 s: {never} of {count}");
        _report.Line($"composites more than one frame apart later than 100 ms after resync: {staleShown}");
        _report.Line($"controller state changes during the run: {states.Count} ({string.Join(", ", states.GroupBy(s => s).Select(g => $"{g.Key}×{g.Count()}"))})");
        if (never > 0 || staleShown > 0)
        {
            _failures++;
        }

        var final = CompositeNow(identify: true);
        _report.Line($"after Pause(): screen {final.ScreenFrame}, camera {final.CameraFrame} → clips {final.ScreenFrame - final.CameraFrame - Lag} frame(s) apart");
    }
}
