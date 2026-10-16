using System.Diagnostics;
using TinyClips.Core.Studio.Preview;
using TinyClips.Core.Studio.Rendering;
using TinyClips.Tools.StudioPreviewCheck.Media;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

// The engine on the software adapter, where it draws on a PC without usable graphics hardware.
internal sealed partial class HeadlessChecks
{
    private void SoftwareDevice()
    {
        _report.Section("The software adapter (WARP)");

        // What kind of PC this is, found out the way the engine and the players find out: by
        // asking for a device on the graphics hardware.
        bool hasHardware;
        string hardwareName;
        using (var device = StudioGraphicsDevice.CreateHardware())
        {
            hasHardware = !device.IsSoftware;
            hardwareName = device.AdapterName;
        }

        _report.Line(hasHardware
            ? $"This PC has graphics hardware ({hardwareName}). The engine is made to draw on the software adapter, which is where it draws on a PC without usable graphics hardware. The players are not told: each starts on the graphics hardware and moves over when its first frame is copied. So this group is NOT a PC without graphics hardware, and this PC cannot be made into one. What the group does show is said in the note below the open."
            : $"This PC has no usable graphics hardware: a device asked for on the hardware is the software adapter too ({hardwareName}). Every group of this run drew on it, and so did the players. This group is that PC, not an imitation of it.");

        // Opened and closed without the run's open and close times, which are the hardware device's.
        Session session;
        var folder = TestFolder.Create(_media, TestMedia.Camera, Late);
        try
        {
            session = Session.Open(Muted with { SoftwareDevice = true }, folder);
        }
        catch (Exception ex)
        {
            folder.Dispose();
            _report.Check("the preview opens on the software adapter", false, Describe(ex) + DumpFailedOpen(ex, "software-open"));
            return;
        }

        // Every check of this group leaves the engine's trace and the pictures behind when it does not hold.
        bool Judge(string name, bool ok, string? detail = null) =>
            _report.Check(name, ok, ok ? detail : detail + Dump(session, "software"));

        var graphics = session.Engine.GraphicsDevice;
        var opened = session.Engine.GetDiagnostics();
        Judge("the preview opens on the software adapter", graphics.IsSoftware, $"{graphics.AdapterName}, opened in {F(session.OpenMilliseconds, "0")} ms");
        _report.Note(DescribePlayers(opened, hasHardware));
        var expected = new Shown(0, session.ExpectedCamera(0));
        var shown = session.ReadShown();
        Thread.Sleep(100);
        session.WaitForIdle();
        var after = session.Recorder.Drain();
        var others = after.Where(scene => new Shown(scene.Screen, scene.Camera) != expected).Select(scene => $"[{new Shown(scene.Screen, scene.Camera)}]").ToList();
        Judge(
            "the picture shows the first frame of both clips, and no scene drawn after the open shows anything else",
            shown == expected && others.Count == 0,
            others.Count == 0 ? shown.ToString() : $"{shown}; of {after.Count} scenes drawn, {others.Count} showed {string.Join(" ", others.Take(4))}");

        // Paused seeks and steps, judged as on the hardware device.
        var last = session.Folder.FrameCount - 1;
        var targets = new List<int> { 100, last, 0, 61, 60, 200 };
        var count = _quick ? 12 : 30;
        while (targets.Count < count)
        {
            targets.Add(_random.Next(0, last + 1));
        }

        var watch = new SeekWatch();
        var wrong = new List<string>();
        var current = 0;
        foreach (var wanted in targets)
        {
            var target = wanted == current ? (wanted == last ? last - 2 : wanted + 1) : wanted;
            if (SeekAndWatch(session, current, target, 0.5, watch) is { } problem)
            {
                wrong.Add(problem);
            }

            current = target;
        }

        _report.Check($"{targets.Count} paused seeks show the requested frame on both clips, with PositionChanged at the call and at the landing", wrong.Count == 0, wrong.Count == 0 ? null : $"{wrong.Count} wrong; first: {string.Join(" | ", wrong.Take(3))}");
        _report.Note($"on the software adapter, Seek() to the picture drawn: {watch.Latency.Summary()}");

        session.SeekTo(100);
        var steps = _quick ? 8 : 20;
        var stepsWrong = StepMany(session, steps, +1, out var stepLatency, waitForIdle: false);
        session.WaitForIdle();
        _report.Check($"{steps} steps forward each show exactly the next frame on both clips", stepsWrong.Count == 0, stepsWrong.Count == 0 ? null : string.Join(" | ", stepsWrong.Take(3)));
        _report.Note($"on the software adapter, step forward to the picture drawn: {stepLatency.Summary()}");

        // A lost device. The new one is a software device too, so players that have settled on
        // the old one have their frames wanted on another device of the same adapter: what a
        // device loss does on a PC without graphics hardware, and the nearest this PC gets to one.
        session.SeekTo(120);
        var rebuildsBefore = session.Engine.GetDiagnostics().DeviceRebuilds;
        var lost = Stopwatch.StartNew();
        session.Engine.SimulateDeviceLoss();
        var rebuilt = session.WaitForIdle(10);
        var rebuildMilliseconds = lost.Elapsed.TotalMilliseconds;
        var pictureProblem = PictureProblem(session, 120, 1280, 720);
        var soughtAfter = session.SeekTo(200);
        var shownAfter = session.ReadShown();
        Judge(
            "a lost device (simulated) is rebuilt on the software adapter: the same frame is shown again, and a seek afterwards shows its frame on both clips",
            rebuilt && session.Engine.GetDiagnostics().DeviceRebuilds == rebuildsBefore + 1 && session.Engine.GraphicsDevice.IsSoftware && pictureProblem is null && soughtAfter && shownAfter == new Shown(200, session.ExpectedCamera(200)) && session.Events.FailedEvents == 0,
            $"{pictureProblem ?? "the same frame"}, back to idle after {F(rebuildMilliseconds, "0")} ms; after Seek to frame 200: {shownAfter}");

        // Playback: the order of what is drawn is judged; how much of it gets drawn is reported.
        const int start = 30;
        var seconds = _quick ? 2 : 4;
        session.SeekTo(start);
        session.Recorder.Drain();
        var played = Stopwatch.StartNew();
        session.Engine.Play();
        Thread.Sleep(seconds * 1000);
        var pausedAt = Stopwatch.GetTimestamp();
        session.Engine.Pause();
        var wall = played.Elapsed.TotalSeconds;
        var idle = session.WaitForIdle();
        var scenes = session.Recorder.Drain().Where(c => c.Screen != FrameCode.Unreadable).ToList();
        var backwards = 0;
        var worstApart = 0;
        for (var index = 0; index < scenes.Count; index++)
        {
            // The pause can take the picture back to the last frame the engine could tell; that is no playing backwards.
            backwards += index > 0 && scenes[index].Screen < scenes[index - 1].Screen && scenes[index].At < pausedAt ? 1 : 0;
            var camera = session.ExpectedCamera(scenes[index].Screen);
            if (camera != FrameCode.Unreadable && scenes[index].Camera != FrameCode.Unreadable)
            {
                worstApart = Math.Max(worstApart, Math.Abs(scenes[index].Camera - camera));
            }
        }

        var distinct = scenes.Select(c => c.Screen).Where(f => f > start).Distinct().Count();
        var reached = scenes.Count == 0 ? start : scenes.Max(c => c.Screen);
        shown = session.ReadShown();
        Judge(
            $"{seconds} s of playback: frames are drawn in order, the clock runs at 1x, and after Pause() both clips are on matching frames with Position on the frame shown",
            idle && scenes.Count > 10 && backwards == 0 && Math.Abs((reached - start) - (wall * Fps)) <= 0.1 * wall * Fps && shown.Screen == session.PositionFrame && shown.Camera == session.ExpectedCamera(shown.Screen),
            $"{scenes.Count} scenes, {backwards} went back, reached frame {reached} from {start} in {F(wall, "0.00")} s; at rest {shown}, Position frame {session.PositionFrame}");
        _report.Note($"on the software adapter, playback drew {distinct} of the {Math.Max(1, reached - start)} frames it passed ({F(100.0 * distinct / Math.Max(1, reached - start), "0")} %); the clips were at most {worstApart} frame(s) apart");
        Judge("no failure was reported", session.Events.FailedEvents == 0, string.Join("; ", session.Events.Failures()));
        session.Close();

        // Opening is where a PC with graphics hardware differs from one without, and where the
        // engine has the most to get right. Before it held the players' first frames back and
        // proved their pictures, about one preview in seven with a late camera failed to open
        // here (38 of 260) and 8 more opened on a blank picture or on frame 1; with the camera
        // from the start 8 of 30 failed to open; and a preview whose camera did not start late
        // showed a blank picture every time it did open (52 of 52). One open says little about
        // that; many do.
        var opens = _quick ? 6 : 15;
        var times = new Samples();
        var opensWrong = OpenMany(opens, Muted with { SoftwareDevice = true }, "software-open", times, out var states);
        _report.Check(
            $"{opens} previews opened on the software adapter one after the other, with the camera late, with the camera from the start and without a camera: each opens and shows the first frame, no scene drawn after it opened shows anything else, and no more than one needed a second attempt to open",
            opensWrong.Count == 0 && SecondAttemptCount(states) <= 1,
            opensWrong.Count == 0 ? SecondAttempts(states) : $"{opensWrong.Count} wrong; first: {string.Join(" | ", opensWrong.Take(3))}; {SecondAttempts(states)}");
        var rounds = new Samples();
        foreach (var state in states)
        {
            rounds.Add(state.ProofRounds);
        }

        var looked = states.Sum(state => state.FirstFramesLookedAt);
        var players = looked == 0
            ? "the engine did not look at the players' first frames"
            : $"a first frame came out empty, so its player came from another adapter, in {states.Count(state => state.FirstFramesEmpty > 0)} of {states.Count}, proven in {rounds.Summary("rounds")}; not proven within the limit: {states.Count(state => !state.ProofHeld)}; first frames the engine had to take itself: {states.Sum(state => state.FirstFramesPulled)}";
        _report.Note($"those opens: {times.Summary()}; {players}");

        // Once every player has a first frame, the engine waits for each to offer it again, which
        // a player does every hundredth of a second, and takes the frame of one that does not.
        // No player has needed that where this was measured, so the engine is made deaf to the
        // offers: otherwise that way in would never be run.
        if (Muted.TrustFirstFrames)
        {
            _report.Line("  not run with --trust-first-frames, which holds no first frames back: the previews whose first frames the engine has to take itself");
            return;
        }

        var kept = _quick ? 3 : 6;
        var keptTimes = new Samples();
        var keptWrong = OpenMany(kept, Muted with { SoftwareDevice = true, PlayersKeepFirstFrames = true }, "software-kept", keptTimes, out var keptStates);
        var taken = keptStates.Count(state => state.FirstFramesPulled == state.FramesCopied.Length);
        _report.Check(
            $"{kept} previews whose players do not hand their first frames over again (simulated): the engine takes every first frame itself, and each preview opens and shows the first frame as the others do",
            keptWrong.Count == 0 && taken == kept,
            $"every first frame taken by the engine in {taken} of {keptStates.Count} that opened; {keptTimes.Summary()}{(keptWrong.Count == 0 ? string.Empty : $"; {keptWrong.Count} wrong; first: {string.Join(" | ", keptWrong.Take(3))}")}");
    }

    /// <summary>
    /// What the players did when the preview opened on the software adapter, which is what tells a
    /// PC with graphics hardware from one without. Every case says what was seen, and the ones
    /// that were not seen when this was written say that too.
    /// </summary>
    private static string DescribePlayers(StudioPreviewDiagnostics opened, bool hasHardware)
    {
        var looked = opened.FirstFramesLookedAt;
        if (looked == 0)
        {
            return "the engine did not look at the players' first frames (--trust-first-frames), so this run does not say where the players started";
        }

        var proof = $"The engine sent the players between two frames {opened.ProofRounds} times before it {(opened.ProofHeld ? "had the same pictures twice and believed them" : "gave up comparing their pictures")}.";
        if (opened.FirstFramesEmpty > 0)
        {
            return hasHardware
                ? $"{opened.FirstFramesEmpty} of the {looked} players' first frames came out empty, which is what a player leaves that started on another adapter. A player starts decoding on the graphics hardware and moves to the software adapter when its first frame is copied there; from then on it decodes without the hardware. {proof} So this group shows the engine and the renderer on the software adapter, and how the engine deals with players that come from another adapter. It does not show a PC without graphics hardware, where the players start on the software adapter and have nowhere to move from"
                : $"{opened.FirstFramesEmpty} of the {looked} players' first frames came out empty, although this PC has no graphics hardware for a player to have started on. That was not expected when this check was written, and is worth reporting with this file. {proof}";
        }

        return hasHardware
            ? $"none of the {looked} players' first frames came out empty, although this PC has graphics hardware. Where this check was written, every first frame copied from the graphics hardware to the software adapter came out empty (14 of 14), so the players of this PC do something else: they may have started on the software adapter. Worth reporting with this file"
            : $"none of the {looked} players' first frames came out empty: the players started on the adapter the engine draws on, and there was nothing to prove. A PC without graphics hardware could not be tried when the engine's open was written, so this run is evidence the engine did not have: worth keeping";
    }
}