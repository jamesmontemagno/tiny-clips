using System.Diagnostics;
using TinyClips.Core.Studio.Preview;
using TinyClips.Tools.StudioPreviewCheck.Media;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

// "rates" asks what the players do when the clock they follow runs at another speed, before the
// engine is taught to play at one (IStudioPreview.SetPlaybackRate, which it does not have yet).
// The clock's rate is set from outside, through a door that is there for this experiment only
// (StudioPreviewEngine.SetClockRateForExperiment); nothing in the engine knows of it, so what
// the engine makes of the frames is what its rules for a clock at 1 make of them.
//
// For each rate, played from frame 30: how fast the recording really went, how many frames a
// player handed over a second and how many it left out, how far past a frame's own time the
// position was that came with it, what the engine took each frame for and how often that was
// right, where the camera was beside the screen, and what Pause() left. Then the rate changed
// while playing, and playing into the end at 8.
//
//   --seconds N   how long each play lasts at most, in seconds of the wall clock (3)
//   --count N     plays for each rate (2)
internal sealed partial class HeadlessChecks
{
    private static readonly double[] RatesLookedAt = [1, 0.25, 0.5, 1.5, 2, 4, 8];

    private void InvestigateRates()
    {
        var seconds = Math.Clamp(_options.Number("seconds", 3), 1, 10);
        var plays = Math.Clamp(_options.Number("count", 2), 1, 20);
        _report.Section($"The players with the clock at another speed: each rate played {plays} times from frame 30, for {seconds} s at most. The engine's rules take the clock to run at 1");
        var session = OpenSession(TestMedia.Camera, options: Muted);
        try
        {
            var last = session.Folder.FrameCount - 1;
            using var probe = new TruthProbe(session.Engine, session.Folder.Screen, session.Folder.Camera, capacity: 1200);
            var state = session.Engine.GetDiagnostics();
            _report.Line($"  the players' sound, which this experiment leaves alone: muted {string.Join(", ", state.PlayerMuted)}, volume {string.Join(", ", state.PlayerVolume.Select(volume => F(volume, "0.##")))}");
            foreach (var rate in RatesLookedAt)
            {
                for (var play = 0; play < plays; play++)
                {
                    session.Engine.SetClockRateForExperiment(1);
                    session.SeekTo(30);
                    probe.Read();
                    session.Recorder.Drain();

                    // Not into the end: that is looked at by itself below.
                    var wall = Math.Min(seconds, (last - 30 - 30) / (Fps * rate));
                    session.Engine.SetClockRateForExperiment(rate);
                    var started = Stopwatch.GetTimestamp();
                    session.Engine.Play();
                    Thread.Sleep(TimeSpan.FromSeconds(wall));
                    var called = Stopwatch.GetTimestamp();
                    session.Engine.Pause();
                    var atReturn = (Position: session.PositionFrame, Picture: session.ReadClipFrame(0), Playing: session.Engine.IsPlaying);
                    var idle = session.WaitForIdle();
                    var atRest = (Position: session.PositionFrame, Picture: session.ReadClipFrame(0), Camera: session.ReadClipFrame(1));
                    var frames = probe.Read();
                    var scenes = session.Recorder.Drain().Where(scene => scene.At < called).ToList();
                    _report.Line();
                    _report.Line($"rate {F(rate, "0.##")}, play {play + 1}: {F(wall, "0.00")} s of the wall clock");
                    foreach (var clip in new[] { 0, 1 })
                    {
                        _report.Line("  " + AtARate(frames, clip, rate, started));
                    }

                    _report.Line("  " + CameraBesideScreen(session, frames));
                    var drawn = scenes.Select(scene => scene.Screen).Where(frame => frame != FrameCode.Unreadable).ToList();
                    var back = drawn.Zip(drawn.Skip(1), (a, b) => b < a ? 1 : 0).Sum();
                    _report.Line($"  scenes drawn while playing: {scenes.Count}, {F(scenes.Count / wall, "0.0")} a second; {drawn.Distinct().Count()} different screen frames; {back} showed an earlier frame than the scene before");
                    _report.Line($"  Pause(): returned with Position on frame {atReturn.Position} and the screen's picture on frame {atReturn.Picture}{(atReturn.Position == atReturn.Picture ? string.Empty : " <- not the same")}{(atReturn.Playing ? ", IsPlaying still true" : string.Empty)}; at rest{(idle ? string.Empty : " (the engine did not come to rest)")}: Position {atRest.Position}, picture {atRest.Picture}{(atRest.Position == atRest.Picture ? string.Empty : " <- not the same")}, camera {atRest.Camera} where {session.ExpectedCamera(atRest.Position)} goes with it");
                }
            }

            RateChangedWhilePlaying(session, probe);
            IntoTheEndAtARate(session, probe, 8);
            session.Engine.SetClockRateForExperiment(1);
            _report.Line();
            _report.Line($"  failures reported: {session.Events.FailedEvents}{(session.Events.FailedEvents == 0 ? string.Empty : ": " + string.Join("; ", session.Events.Failures()))}");
        }
        finally
        {
            Close(session);
        }
    }

    /// <summary>What one clip's player handed over during a play at a rate, in a line.</summary>
    private static string AtARate(List<ProbedFrame> all, int clip, double rate, long started)
    {
        var name = clip == 0 ? "screen" : "camera";
        var frames = all.Where(f => f.Clip == clip && f.Kind == StudioPreviewHandOverKind.Playback && f.Truth != FrameCode.Unreadable).OrderBy(f => f.Serial).ToList();
        if (frames.Count < 2)
        {
            return $"{name}: {frames.Count} frames handed over";
        }

        var wall = Stopwatch.GetElapsedTime(frames[0].StartedAt, frames[^1].StartedAt).TotalSeconds;
        var went = (frames[^1].Truth - frames[0].Truth) / (double)Fps;
        var ones = 0;
        var more = 0;
        var most = 0;
        var notOn = 0;
        var gaps = new Samples();
        for (var index = 1; index < frames.Count; index++)
        {
            var step = frames[index].Truth - frames[index - 1].Truth;
            ones += step == 1 ? 1 : 0;
            more += step > 1 ? 1 : 0;
            most = Math.Max(most, step);
            notOn += step <= 0 ? 1 : 0;
            gaps.Add(Stopwatch.GetElapsedTime(frames[index - 1].StartedAt, frames[index].StartedAt).TotalMilliseconds);
        }

        // How far past the frame's own time the position was, in milliseconds of the recording
        // and in frames: what the position names, less the frame the pixels show.
        var past = new Samples();
        var ahead = new Samples();
        foreach (var frame in frames)
        {
            past.Add((frame.PositionTicks / 10000.0) - (frame.Truth * 1000.0 / Fps));
            ahead.Add(frame.NameByPosition - frame.Truth);
        }

        // What a rule for a faster clock would have to go by. A frame boundary that falls
        // between the player's look and the reading of the position makes the position name the
        // frame after the one handed over. How long before the reading the named frame came due,
        // in milliseconds of the wall clock, says how long that can take: for the frames their
        // position named, and for those whose position named the frame after them. And whether
        // the hand-overs still begin a whole number of looks apart, a hundredth of a second each.
        var dueBeforeOwn = new Samples();
        var dueBeforeNext = new Samples();
        var furtherOn = 0;
        var beforeItsTime = 0;
        var offTheLooks = new Samples();
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            var on = frame.NameByPosition - frame.Truth;
            var sinceDue = frame.IntoMilliseconds / rate;
            if (on == 0)
            {
                dueBeforeOwn.Add(sinceDue);
            }
            else if (on == 1)
            {
                dueBeforeNext.Add(sinceDue);
            }
            else if (on > 1)
            {
                furtherOn++;
            }
            else
            {
                beforeItsTime++;
            }

            if (index > 0)
            {
                var since = Stopwatch.GetElapsedTime(frames[index - 1].StartedAt, frame.StartedAt).TotalMilliseconds % 10;
                offTheLooks.Add(Math.Min(since, 10 - since));
            }
        }

        var forARule = $"\n      for a rule: the position named the frame itself {dueBeforeOwn.Count} times, read {dueBeforeOwn.Summary()} of the wall clock after that frame came due; the frame after it {dueBeforeNext.Count} times, read {dueBeforeNext.Summary()} after that one came due; a frame further on {furtherOn} times; an earlier frame than the one handed over {beforeItsTime} times (a frame before its time); the hand-overs began {offTheLooks.Summary()} off a whole number of hundredths of a second after the one before";
        var known = frames.Count(f => f.Knowledge == StudioPreviewFrameKnowledge.Known);
        var knownWrong = frames.Count(f => f.Knowledge == StudioPreviewFrameKnowledge.Known && f.Frame != f.Truth);
        var unsure = frames.Count(f => f.Knowledge == StudioPreviewFrameKnowledge.Unsure);
        var unsureWrong = frames.Count(f => f.Knowledge == StudioPreviewFrameKnowledge.Unsure && f.Frame != f.Truth);
        var unknown = frames.Count(f => f.Knowledge == StudioPreviewFrameKnowledge.Unknown);
        var first = Stopwatch.GetElapsedTime(started, frames[0].StartedAt).TotalMilliseconds;
        return $"{name}: {frames.Count} frames handed over, the first {F(first, "0")} ms after Play(); the recording went {F(went, "0.000")} s in {F(wall, "0.000")} s, which is a rate of {F(wall > 0 ? went / wall : 0, "0.000")} where {F(rate, "0.###")} was set; {F((frames.Count - 1) / Math.Max(wall, 0.001), "0.0")} frames a second, {gaps.Summary()} apart; "
            + $"the next frame {ones} times, frames left out {more} times (the furthest {most} on), not a later frame {notOn} times; "
            + $"the position that came with a frame was {past.Summary()} of the recording past the frame's own time, and named a frame {ahead.Summary("frames")} on; "
            + $"the engine: {known} numbered ({knownWrong} wrongly), {unknown} without a number, {unsure} shown under their position's number and called unsure ({unsureWrong} wrongly)"
            + forARule;
    }

    /// <summary>Where the camera was beside the screen: for each frame the screen handed over, the camera frame handed over nearest to it in time.</summary>
    private static string CameraBesideScreen(Session session, List<ProbedFrame> all)
    {
        var screen = all.Where(f => f.Clip == 0 && f.Kind == StudioPreviewHandOverKind.Playback && f.Truth != FrameCode.Unreadable).ToList();
        var camera = all.Where(f => f.Clip == 1 && f.Kind == StudioPreviewHandOverKind.Playback && f.Truth != FrameCode.Unreadable).OrderBy(f => f.StartedAt).ToList();
        if (screen.Count == 0 || camera.Count == 0)
        {
            return "the camera beside the screen: not enough frames to say";
        }

        var apart = new Samples();
        foreach (var frame in screen)
        {
            var wanted = session.ExpectedCamera(frame.Truth);
            if (wanted == FrameCode.Unreadable)
            {
                continue;
            }

            var nearest = camera.MinBy(other => Math.Abs(other.StartedAt - frame.StartedAt));
            apart.Add(nearest.Truth - wanted);
        }

        return $"the camera beside the screen: the camera frame handed over nearest in time to each screen frame was {apart.Summary("frames")} from the one that goes with it ({apart.CountOf(0)} of {apart.Count} exactly it)";
    }

    /// <summary>The rate changed three times in one play: how long after each call the frames came at the new pace.</summary>
    private void RateChangedWhilePlaying(Session session, TruthProbe probe)
    {
        _report.Line();
        _report.Line("the rate changed while playing: 1, then 2, then 0.5, then 1 again");
        session.Engine.SetClockRateForExperiment(1);
        session.SeekTo(30);
        probe.Read();
        var changes = new List<(double Rate, long At)>();
        session.Engine.Play();
        Thread.Sleep(1000);
        foreach (var rate in (ReadOnlySpan<double>)[2, 0.5, 1])
        {
            changes.Add((rate, Stopwatch.GetTimestamp()));
            session.Engine.SetClockRateForExperiment(rate);
            Thread.Sleep(1500);
        }

        session.Engine.Pause();
        session.WaitForIdle();
        var frames = probe.Read().Where(f => f.Clip == 0 && f.Kind == StudioPreviewHandOverKind.Playback && f.Truth != FrameCode.Unreadable).OrderBy(f => f.Serial).ToList();
        for (var change = 0; change < changes.Count; change++)
        {
            var (rate, at) = changes[change];
            var until = change + 1 < changes.Count ? changes[change + 1].At : long.MaxValue;

            // The pace between one hand-over and the next: the recording's time over the wall
            // clock's. At the new rate when three in a row are within a quarter of it.
            var after = frames.Where(f => f.StartedAt >= at && f.StartedAt < until).ToList();
            double? settled = null;
            for (var index = 0; index + 3 < after.Count && settled is null; index++)
            {
                var all = true;
                for (var pair = index; pair < index + 3; pair++)
                {
                    var wall = Stopwatch.GetElapsedTime(after[pair].StartedAt, after[pair + 1].StartedAt).TotalSeconds;
                    var pace = wall > 0 ? (after[pair + 1].Truth - after[pair].Truth) / (double)Fps / wall : 0;
                    all &= Math.Abs(pace - rate) <= rate / 4;
                }

                if (all)
                {
                    settled = Stopwatch.GetElapsedTime(at, after[index].StartedAt).TotalMilliseconds;
                }
            }

            var went = after.Count > 1 ? (after[^1].Truth - after[0].Truth) / (double)Fps / Math.Max(0.001, Stopwatch.GetElapsedTime(after[0].StartedAt, after[^1].StartedAt).TotalSeconds) : 0;
            var known = after.Count(f => f.Knowledge == StudioPreviewFrameKnowledge.Known);
            var wrong = after.Count(f => f.Knowledge != StudioPreviewFrameKnowledge.Unknown && f.Frame != f.Truth);
            _report.Line($"  to {F(rate, "0.##")}: {after.Count} screen frames until the next change, at a rate of {F(went, "0.000")} over all of them; three hand-overs in a row at the new pace began {(settled is { } ms ? $"{F(ms, "0")} ms after the call" : "never")}; the engine numbered {known} and was wrong about {wrong}");
        }

        _report.Line($"  Pause() at the end: Position frame {session.PositionFrame}, the screen's picture frame {session.ReadClipFrame(0)}");
    }

    /// <summary>Played into the end of the recording at a rate: whether playback stops there by itself, and on what.</summary>
    private void IntoTheEndAtARate(Session session, TruthProbe probe, double rate)
    {
        var last = session.Folder.FrameCount - 1;
        _report.Line();
        _report.Line($"into the end of the recording at {F(rate, "0.##")}: from frame {last - 240}");
        session.Engine.SetClockRateForExperiment(1);
        session.SeekTo(last - 240);
        probe.Read();
        session.Events.Clear();
        session.Engine.SetClockRateForExperiment(rate);
        var started = Stopwatch.GetTimestamp();
        session.Engine.Play();
        while (session.Engine.IsPlaying && Stopwatch.GetElapsedTime(started).TotalSeconds < 12)
        {
            Thread.Sleep(5);
        }

        var stoppedAfter = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var stopped = !session.Engine.IsPlaying;
        if (!stopped)
        {
            session.Engine.Pause();
        }

        var idle = session.WaitForIdle();
        var frames = probe.Read().Where(f => f.Clip == 0 && f.Kind == StudioPreviewHandOverKind.Playback && f.Truth != FrameCode.Unreadable).OrderBy(f => f.Serial).ToList();
        _report.Line($"  playback {(stopped ? $"stopped by itself after {F(stoppedAfter, "0.00")} s, where {F(240 / (Fps * rate), "0.00")} s is the length of that stretch at this rate" : "did not stop by itself in 12 s")}; {frames.Count} screen frames handed over, the last one frame {(frames.Count == 0 ? "none" : frames[^1].Truth.ToString(System.Globalization.CultureInfo.InvariantCulture))}; at rest{(idle ? string.Empty : " (the engine did not come to rest)")}: Position frame {session.PositionFrame}, the screen's picture frame {session.ReadClipFrame(0)}, the last frame of the recording being {last}");
    }
}
