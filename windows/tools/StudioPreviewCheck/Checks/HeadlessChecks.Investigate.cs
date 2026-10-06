using System.Diagnostics;
using System.Globalization;
using TinyClips.Core.Capture;
using TinyClips.Core.Studio.Rendering;
using TinyClips.Tools.StudioPreviewCheck.Media;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

// Experiments, not checks: --investigate <name> runs one and prints what it measures. Nothing
// here is judged. They are how it was found out what a MediaPlayer does when its frames are
// wanted on another graphics adapter than the one it decodes on, and they are kept so that the
// same questions can be asked of another PC: one with two graphics adapters, or with none.
internal sealed partial class HeadlessChecks
{
    private static readonly Guid H264Decoder = new("1b81be68-a0c7-11d3-b984-00c04f2e73c5");

    public void Investigate()
    {
        switch (_options.Text("investigate", string.Empty).ToLowerInvariant())
        {
            case "opens":
                InvestigateOpens();
                break;
            case "decoding":
                InvestigateDecoding();
                break;
            case "players":
                InvestigatePlayers();
                break;
            case "cycles":
                InvestigateCycles();
                break;
            case "names":
                InvestigateNames();
                break;
            case "reopens":
                InvestigateReopens();
                break;
            case "starts":
                InvestigateStarts();
                break;
            case "waits":
                InvestigateWaits();
                break;
            case "tails":
                InvestigateTails();
                break;
            case "exits":
                InvestigateExits();
                break;
            case "recordings":
                InvestigateRecordings();
                break;
            case "rates":
                InvestigateRates();
                break;
            default:
                throw new ArgumentException("--investigate takes opens, cycles, decoding, players, names, reopens, starts, waits, tails, exits, recordings or rates.");
        }
    }

    private bool OnSoftwareAdapter() =>
        _options.Text("device", "software").ToLowerInvariant() is "software" or "warp";

    // ---------------------------------------------------------------------------------------
    // Many previews opened one after the other, the engine's way or the way it used to
    // ---------------------------------------------------------------------------------------

    private void InvestigateOpens()
    {
        var count = _options.Number("count", 60);
        var software = OnSoftwareAdapter();
        var shape = _options.Text("camera", "mixed").ToLowerInvariant();
        if (shape is not ("late" or "start" or "none" or "mixed"))
        {
            throw new ArgumentException("--camera takes late, start, none or mixed.");
        }

        var how = _options.Flag("alternate") ? "alternately as the engine opens and trusting the players' first frames" : _options.Flag("trust-first-frames") ? "trusting the players' first frames" : "as the engine opens";
        var keeping = _options.Flag("keep-first-frames");
        if (keeping)
        {
            how += ", with the engine deaf to first frames offered again, so that it has to take them itself (only where they are held: on the software adapter)";
        }

        _report.Section($"Investigation: {count} previews opened on {(software ? "the software adapter" : "the graphics hardware")}, {how}; camera: {shape}");
        _report.Line("  Each line: the time to open, the picture read at once and 0.3 s later, the frame each player's texture holds, every scene drawn in between that showed something else, and what the engine says about its opening.");
        var byMode = new SortedDictionary<string, (Samples Times, int[] Counts)>(StringComparer.Ordinal);
        var keptRight = false;

        // Every picture that is not right is kept, not only the first few.
        _dumps = int.MinValue / 2;
        for (var index = 0; index < count; index++)
        {
            var kind = shape == "mixed" ? new[] { "late", "start", "none" }[index % 3] : shape;

            // Alternating goes through every pair of camera and way of opening.
            var trusting = _options.Flag("alternate") ? (shape == "mixed" ? index / 3 : index) % 2 == 1 : _options.Flag("trust-first-frames");
            var mode = $"camera {kind}, {(trusting ? "trusting the first frames" : "as the engine opens")}";
            if (!byMode.TryGetValue(mode, out var tally))
            {
                byMode[mode] = tally = (new Samples(), new int[3]);
            }

            tally.Counts[0]++;
            var folder = kind == "none" ? TestFolder.Create(_media, camera: null) : TestFolder.Create(_media, TestMedia.Camera, kind == "start" ? 0 : Late);
            Session session;
            var watch = Stopwatch.StartNew();
            try
            {
                session = Session.Open(Muted with { SoftwareDevice = software, TrustFirstFrames = trusting, PlayersKeepFirstFrames = keeping }, folder);
            }
            catch (Exception ex)
            {
                folder.Dispose();
                tally.Counts[1]++;
                _report.Line($"  open {index + 1} ({mode}): FAILED after {F(watch.Elapsed.TotalMilliseconds, "0")} ms: {Describe(ex)}{DumpFailedOpen(ex, "investigate-open")}");
                continue;
            }

            var expected = new Shown(0, session.ExpectedCamera(0));
            var shown = session.ReadShown();
            var screenTexture = session.ReadClipFrame(0);
            var cameraTexture = kind == "none" ? 0 : session.ReadClipFrame(1);
            Thread.Sleep(300);
            var later = session.ReadShown();
            var state = session.Engine.GetDiagnostics();
            var others = session.Recorder.Drain().Where(scene => new Shown(scene.Screen, scene.Camera) != expected).Select(scene => $"[{new Shown(scene.Screen, scene.Camera)}]").ToList();
            var ok = shown == expected && later == expected && screenTexture == 0 && cameraTexture == 0 && others.Count == 0;
            var text = $"  open {index + 1} ({mode}): {F(session.OpenMilliseconds, "0")} ms; picture {shown}, later {later}; textures screen {screenTexture}{(kind == "none" ? string.Empty : $", camera {cameraTexture}")}; other scenes: {(others.Count == 0 ? "none" : string.Join(" ", others))}; first frames empty {state.FirstFramesEmpty}, proof rounds {state.ProofRounds}{(state.ProofHeld ? string.Empty : " NOT PROVEN")}{(state.FirstFramesPulled > 0 ? $", {state.FirstFramesPulled} first frame(s) taken by the engine" : string.Empty)}{(state.OpenAttempts > 1 ? $", OPENED AT ATTEMPT {state.OpenAttempts}{session.Dump(_failuresDirectory, $"investigate-second-attempt-{index + 1}")}" : string.Empty)}";
            if (state.CopyFailures.Sum() > 0)
            {
                text += $"; COPY FAILURES {string.Join("+", state.CopyFailures)}{session.Dump(_failuresDirectory, $"investigate-copy-failed-{index + 1}")}";
            }

            if (!ok)
            {
                tally.Counts[2]++;
                text += " WRONG" + Dump(session, "investigate-wrong");
            }
            else if (!keptRight)
            {
                keptRight = true;
                text += session.Dump(_failuresDirectory, "investigate-right");
            }

            _report.Line(text);
            tally.Times.Add(session.OpenMilliseconds);
            session.Close();
        }

        _report.Line();
        foreach (var (mode, (times, counts)) in byMode)
        {
            _report.Line($"  {mode}: {counts[0]} opens, {counts[1]} failed, {counts[2]} opened on a wrong picture or drew one; open time {times.Summary()}");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Previews opened and closed one straight after the other, as the dispose checks do
    // ---------------------------------------------------------------------------------------

    private void InvestigateCycles()
    {
        var count = _options.Number("count", 200);
        var software = _options.Text("device", "hardware").ToLowerInvariant() is "software" or "warp";
        var options = Muted with { SoftwareDevice = software };
        _report.Section($"Investigation: {count} previews opened and closed one straight after the other on {(software ? "the software adapter" : "the graphics hardware")}, {(Muted.TrustFirstFrames ? "trusting the players' first frames" : "as the engine opens")}");
        _report.Line("  Each is closed while playing, during a seek, right after it opened or during a seek followed by Play, in turn, and its folder is deleted at once. Only what goes wrong is printed.");

        // The trace of every preview that does not open is kept, not only of the first few.
        _dumps = int.MinValue / 2;
        var moments = new[] { CloseMoment.WhilePlaying, CloseMoment.DuringSeek, CloseMoment.RightAfterOpen, CloseMoment.DuringSeekThenPlay };
        var failed = 0;
        var unclean = 0;
        for (var index = 0; index < count; index++)
        {
            var moment = moments[index % moments.Length];
            try
            {
                if (OpenAndClose(moment, out _, out _, options) is { } problem)
                {
                    unclean++;
                    _report.Line($"  cycle {index + 1} (closed {moment}): {problem}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                var before = index == 0 ? "the first" : $"the one before was closed {moments[(index - 1) % moments.Length]}";
                _report.Line($"  cycle {index + 1} ({before}): DID NOT OPEN: {Describe(ex)}{DumpFailedOpen(ex, "investigate-cycle")}");
            }
        }

        _report.Line();
        _report.Line($"  {count} cycles: {failed} did not open, {unclean} did not close cleanly; open time {_openTimes.Summary()}; DisposeAsync {_disposeTimes.Summary()}");
    }

    // ---------------------------------------------------------------------------------------
    // Where the players of a preview decode
    // ---------------------------------------------------------------------------------------

    private void InvestigateDecoding()
    {
        var software = OnSoftwareAdapter();
        var seconds = _options.Number("seconds", 12);
        _report.Section($"Investigation: where the players decode when the engine draws on {(software ? "the software adapter" : "the graphics hardware")}");
        _report.Line($"  graphics adapters: {Adapters()}");
        _report.Line($"  This is process {Environment.ProcessId}. Which adapter's engines it keeps busy can be read from outside while it plays, with: Get-Counter '\\GPU Engine(pid_{Environment.ProcessId}_*)\\Utilization Percentage'");
        using var memory = AdapterMemory.TryCreate();
        var modules = ModuleNames();
        _report.Line($"  before the preview opens: {memory?.Describe() ?? "no adapter"}");

        var session = Session.Open(Muted with { SoftwareDevice = software }, TestFolder.Create(_media, TestMedia.Camera, Late));
        var graphics = session.Engine.GraphicsDevice;
        var state = session.Engine.GetDiagnostics();
        _report.Line($"  the engine's device: {graphics.AdapterName}, {(graphics.IsSoftware ? "software" : "hardware")}, {DecoderProfiles(graphics)}");
        _report.Line($"  opened in {F(session.OpenMilliseconds, "0")} ms; first frames empty {state.FirstFramesEmpty} of {state.FramesCopied.Length}, proof rounds {state.ProofRounds}; at rest: {memory?.Describe()}");
        _report.Line($"  graphics and media modules loaded by the open: {NewModules(modules)}");

        session.SeekTo(30);
        _report.Line($"  playing with the surface attached, {seconds} s: {PlayAndMeasure(session, seconds, memory)}");
        session.SeekTo(30);
        session.Detach();
        _report.Line($"  playing with no surface, so the players decode and copy and nothing is drawn, {seconds} s: {PlayAndMeasure(session, seconds, memory)}");
        session.Close();
        _report.Line($"  straight after the preview is closed: {memory?.Describe()}");
        Thread.Sleep(1500);
        _report.Line($"  1.5 s later: {memory?.Describe()}");
    }

    private static string PlayAndMeasure(Session session, int seconds, AdapterMemory? memory)
    {
        using var process = Process.GetCurrentProcess();
        var before = session.Engine.GetDiagnostics();
        session.Recorder.Drain();
        var cpu = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        session.Engine.Play();
        Thread.Sleep(seconds * 500);
        var midway = memory?.Describe();
        Thread.Sleep(seconds * 500);
        session.Engine.Pause();
        var wall = watch.Elapsed.TotalSeconds;
        process.Refresh();
        var used = (process.TotalProcessorTime - cpu).TotalSeconds;
        session.WaitForIdle();
        var after = session.Engine.GetDiagnostics();
        var scenes = session.Recorder.Drain().Count;
        return $"process CPU {F(100 * used / wall, "0")} % of one core; {scenes} scenes drawn; frames copied {string.Join(" and ", after.FramesCopied.Zip(before.FramesCopied, (now, then) => now - then))}; halfway {midway}";
    }

    // ---------------------------------------------------------------------------------------
    // MediaPlayers by themselves, without the engine
    // ---------------------------------------------------------------------------------------

    private void InvestigatePlayers()
    {
        var kinds = _options.Text("device", "both").ToLowerInvariant() switch
        {
            "software" or "warp" => new[] { true },
            "hardware" => new[] { false },
            _ => new[] { true, false },
        };
        var repeat = _options.Number("count", 4);
        var scenario = _options.Text("scenario", "all").ToLowerInvariant();
        if (scenario is not ("all" or "first-copy" or "second-device" or "while-opening" or "both-ready" or "settle" or "before-source" or "reopen"))
        {
            throw new ArgumentException("--scenario takes first-copy, second-device, while-opening, both-ready, settle, before-source, reopen or all.");
        }

        _report.Section("Investigation: MediaPlayers in frame-server mode on one clock, as the engine sets them up, without the engine");
        _report.Line($"  graphics adapters: {Adapters()}");
        _report.Line("  A copy is written as: when it began, how long it took, and the frame the texture held afterwards (BLANK when no frame number can be read from it), with the frame of the position the player reported when that is another, and the references on the texture's device when the copy changed them.");
        using var folder = TestFolder.Create(_media, TestMedia.Camera);
        foreach (var software in kinds)
        {
            var name = software ? "software adapter" : "graphics hardware";
            void Run(string id, string title, Action<int> trial, int trials)
            {
                if (scenario != "all" && scenario != id)
                {
                    return;
                }

                _report.Line();
                _report.Line($"  [{name}] {id}: {title}");
                for (var index = 0; index < trials; index++)
                {
                    trial(index);
                }
            }

            Run("first-copy", "one player; its first frame is copied, then the clock is moved to frames 100, 101, 250, 7 and 30. The texture is filled with a marker colour before every copy.", index => FirstCopy(folder, software, camera: index % 2 == 1), repeat);
            Run("second-device", "one player; its frames are copied into a texture on one device, then into a texture on a second device of the same kind.", index => SecondDevice(folder, software, camera: index % 2 == 1), repeat);
            foreach (var delay in new[] { 0, 10, 20, 30, 40, 60, 80, 120, 200 })
            {
                Run("while-opening", $"the camera's first frame is copied {delay} ms after the screen's source was set, while the screen is still opening.", _ => WhileOpening(folder, software, delay), delay == 0 ? repeat : Math.Max(1, repeat / 2));
            }

            foreach (var delay in new[] { 0, 50, 400 })
            {
                Run("both-ready", $"both players have a first frame ready; one's is copied, the other's {delay} ms later; then the clock is moved to frame 30.", index => BothReady(folder, software, delay, screenFirst: index % 2 == 0), repeat);
            }

            foreach (var delay in new[] { 0, 30, 100, 400, 800 })
            {
                Run("settle", $"one player; its first frame is copied, and {delay} ms later the clock is moved to frame 100.", _ => Settle(folder, software, delay), Math.Max(1, repeat / 2));
            }

            Run("before-source", "one player, given a texture to copy into before it has a frame; then its first frame is copied and the clock moved to frames 100, 101 and 7.", index => BeforeSource(folder, software, camera: index % 2 == 1, afterSource: index % 4 >= 2), repeat);
            Run("reopen", "both players' first frames are copied, then each player is given its source a second time.", index => Reopen(folder, software, newSource: index % 2 == 1, together: index % 4 < 2), repeat);
        }
    }

    /// <summary>What a copy leaves in the texture, over the first copy and five position changes.</summary>
    private void FirstCopy(TestFolder folder, bool software, bool camera)
    {
        using var memory = AdapterMemory.TryCreate();
        using var rig = new Rig(folder, software);
        var probe = camera ? rig.Camera : rig.Screen;
        probe.Mark = true;
        probe.Start();
        if (!probe.WaitAnnounced(1, 5000))
        {
            _report.Line($"    the {probe.Label} announced no frame: {probe.Events()}");
            return;
        }

        Thread.Sleep(100);
        var ready = memory?.LocalMb() ?? 0;
        var origin = Stopwatch.GetTimestamp();
        probe.Copy = true;
        probe.WaitCopied(1, 3000);
        Thread.Sleep(300);
        foreach (var frame in new[] { 100, 101, 250, 7, 30 })
        {
            var copies = probe.Copied;
            rig.Controller.Position = TimeSpan.FromSeconds((frame + 0.5) / TestMedia.Fps);
            probe.WaitCopied(copies + 1, 3000);
            Thread.Sleep(500);
        }

        _report.Line($"    {probe.Label} (with its first frame ready and nothing copied, this process uses {F(ready, "0")} MB on the first adapter): {probe.Copies(origin)}{probe.Trouble()}");
        _report.Line($"      pixels B,G,R,A after each copy, over the marker ({string.Join(",", Probe.Marker)}): {probe.Pixels()}");
        _report.Line($"      events: {probe.Events(origin)}");
    }

    /// <summary>
    /// A player that has settled on one device is given a texture on a second device of the same
    /// kind. On the software adapter that is as near as a PC with graphics hardware gets to a PC
    /// without: the player works on a software device and its frames are wanted on another.
    /// </summary>
    private void SecondDevice(TestFolder folder, bool software, bool camera)
    {
        using var rig = new Rig(folder, software);
        var probe = camera ? rig.Camera : rig.Screen;
        probe.Start();
        if (!probe.WaitAnnounced(1, 5000))
        {
            _report.Line($"    the {probe.Label} announced no frame: {probe.Events()}");
            return;
        }

        Thread.Sleep(100);
        var origin = Stopwatch.GetTimestamp();
        probe.Copy = true;
        probe.WaitCopied(1, 3000);
        Thread.Sleep(300);
        foreach (var frame in new[] { 100, 101, 250 })
        {
            var copies = probe.Copied;
            rig.Controller.Position = TimeSpan.FromSeconds((frame + 0.5) / TestMedia.Fps);
            probe.WaitCopied(copies + 1, 3000);
            Thread.Sleep(400);
        }

        var onFirst = probe.Copies(origin);
        var before = probe.Copied;
        using var second = software ? StudioGraphicsDevice.CreateWarp() : StudioGraphicsDevice.CreateHardware();
        var switched = Stopwatch.GetTimestamp();
        probe.Retarget(second);
        foreach (var frame in new[] { 7, 30, 200 })
        {
            var copies = probe.Copied;
            rig.Controller.Position = TimeSpan.FromSeconds((frame + 0.5) / TestMedia.Fps);
            probe.WaitCopied(copies + 1, 3000);
            Thread.Sleep(400);
        }

        _report.Line($"    {probe.Label}, first device: {onFirst}");
        _report.Line($"      then frames 7, 30 and 200 into a texture on the second device: {probe.Copies(switched, before)}{probe.Trouble()}");

        // The player has to let go of the second device's texture before that device goes.
        probe.Dispose();
    }

    private void WhileOpening(TestFolder folder, bool software, int delay)
    {
        using var rig = new Rig(folder, software);
        rig.Camera.Start();
        if (!rig.Camera.WaitAnnounced(1, 5000))
        {
            _report.Line($"    the camera announced no frame: {rig.Camera.Events()}");
            return;
        }

        Thread.Sleep(150);
        var started = Stopwatch.GetTimestamp();
        rig.Screen.Start();
        Rig.Wait(started, delay);
        rig.Camera.Copy = true;
        var copied = rig.Camera.WaitCopied(1, 3000);
        var watch = Stopwatch.StartNew();
        while (rig.Screen.Failure is null && rig.Screen.Announced == 0 && watch.ElapsedMilliseconds < 3000)
        {
            Thread.Sleep(2);
        }

        var outcome = rig.Screen.Failure is { } failure
            ? $"FAILED {failure}"
            : rig.Screen.Announced == 0 ? "NO FRAME in 3 s" : "frame ready";
        if (rig.Screen.Failure is null && rig.Screen.Announced > 0)
        {
            rig.Screen.Copy = true;
            rig.Screen.WaitCopied(1, 2000);
            Thread.Sleep(500);
            outcome += $"; copies: {rig.Screen.Copies(started)}";
        }

        _report.Line($"    camera {(copied ? rig.Camera.Copies(started) : "NEVER COPIED")}; screen: {rig.Screen.Events(started)} -> {outcome}");
    }

    private void BothReady(TestFolder folder, bool software, int delay, bool screenFirst)
    {
        using var rig = new Rig(folder, software);
        var first = screenFirst ? rig.Screen : rig.Camera;
        var second = screenFirst ? rig.Camera : rig.Screen;
        rig.Screen.Start();
        rig.Camera.Start();
        if (!rig.Screen.WaitAnnounced(1, 5000) || !rig.Camera.WaitAnnounced(1, 5000))
        {
            _report.Line($"    not both ready: screen {rig.Screen.Events()}; camera {rig.Camera.Events()}");
            return;
        }

        Thread.Sleep(100);
        var origin = Stopwatch.GetTimestamp();
        first.Copy = true;
        first.WaitCopied(1, 3000);
        Rig.Wait(origin, delay);
        second.Copy = true;
        second.WaitCopied(1, 3000);
        Thread.Sleep(700);
        var settled = $"{first.Label}: {first.Copies(origin)}{first.Trouble()}; {second.Label}: {second.Copies(origin)}{second.Trouble()}";
        var copiesFirst = first.Copied;
        var copiesSecond = second.Copied;
        var sought = Stopwatch.GetTimestamp();
        rig.Controller.Position = TimeSpan.FromSeconds(30.5 / TestMedia.Fps);
        first.WaitCopied(copiesFirst + 1, 3000);
        second.WaitCopied(copiesSecond + 1, 3000);
        Thread.Sleep(400);
        _report.Line($"    {settled} | then frame 30: {first.Label} {first.Copies(sought, copiesFirst)}; {second.Label} {second.Copies(sought, copiesSecond)}");
    }

    private void Settle(TestFolder folder, bool software, int delay)
    {
        using var rig = new Rig(folder, software);
        rig.Screen.Start();
        if (!rig.Screen.WaitAnnounced(1, 5000))
        {
            _report.Line($"    the screen announced no frame: {rig.Screen.Events()}");
            return;
        }

        Thread.Sleep(100);
        var origin = Stopwatch.GetTimestamp();
        rig.Screen.Copy = true;
        rig.Screen.WaitCopied(1, 3000);
        Rig.Wait(origin, delay);
        var soughtAt = Stopwatch.GetElapsedTime(origin).TotalMilliseconds;
        rig.Controller.Position = TimeSpan.FromSeconds(100.5 / TestMedia.Fps);
        Thread.Sleep(1500);
        _report.Line($"    clock moved at +{F(soughtAt, "0")} ms; screen: {rig.Screen.Copies(origin)}{rig.Screen.Trouble()}; events {rig.Screen.Events(origin)}");
    }

    /// <summary>The player is given a texture before it has a source, or a frame: does it then start on that texture's device?</summary>
    private void BeforeSource(TestFolder folder, bool software, bool camera, bool afterSource)
    {
        using var memory = AdapterMemory.TryCreate();
        using var rig = new Rig(folder, software);
        var probe = camera ? rig.Camera : rig.Screen;
        var asked = afterSource ? null : probe.CopyWithoutFrame();
        probe.Start();
        asked ??= probe.CopyWithoutFrame();
        if (!probe.WaitAnnounced(1, 5000))
        {
            _report.Line($"    the {probe.Label} announced no frame: {probe.Events()}; the copy without a frame {asked}");
            return;
        }

        Thread.Sleep(100);
        var ready = memory?.LocalMb() ?? 0;
        var origin = Stopwatch.GetTimestamp();
        probe.Copy = true;
        probe.WaitCopied(1, 3000);
        Thread.Sleep(300);
        foreach (var frame in new[] { 100, 101, 7 })
        {
            var copies = probe.Copied;
            rig.Controller.Position = TimeSpan.FromSeconds((frame + 0.5) / TestMedia.Fps);
            probe.WaitCopied(copies + 1, 3000);
            Thread.Sleep(400);
        }

        _report.Line($"    {probe.Label}: the copy without a frame, {(afterSource ? "just after" : "before")} the source was set, {asked}; with a first frame ready this process uses {F(ready, "0")} MB on the first adapter; then: {probe.Copies(origin)}{probe.Trouble()}");
    }

    /// <summary>
    /// Both players' first frames are copied, which on another adapter moves them, and then each
    /// is given its source a second time: does it open on the device it has moved to, and cleanly?
    /// </summary>
    private void Reopen(TestFolder folder, bool software, bool newSource, bool together)
    {
        using var memory = AdapterMemory.TryCreate();
        using var rig = new Rig(folder, software);
        rig.Screen.Start();
        rig.Camera.Start();
        if (!rig.Screen.WaitAnnounced(1, 5000) || !rig.Camera.WaitAnnounced(1, 5000))
        {
            _report.Line($"    not both ready: screen {rig.Screen.Events()}; camera {rig.Camera.Events()}");
            return;
        }

        var origin = Stopwatch.GetTimestamp();
        var before = memory?.LocalMb() ?? 0;
        rig.Screen.Copy = true;
        rig.Camera.Copy = true;
        rig.Screen.WaitCopied(1, 3000);
        rig.Camera.WaitCopied(1, 3000);
        var first = $"screen {rig.Screen.Copies(origin)}; camera {rig.Camera.Copies(origin)}";

        var reopenAt = Stopwatch.GetTimestamp();
        rig.Screen.Reopen(newSource);
        if (!together)
        {
            rig.Screen.WaitAnnounced(1, 5000);
        }

        rig.Camera.Reopen(newSource);
        var ready = rig.Screen.WaitAnnounced(1, 5000) & rig.Camera.WaitAnnounced(1, 5000);
        var readyAfter = Stopwatch.GetElapsedTime(reopenAt).TotalMilliseconds;
        var atReady = memory?.LocalMb() ?? 0;
        var parts = new List<string>();
        foreach (var frame in new[] { -1, 100, 0 })
        {
            var copiesScreen = rig.Screen.Copied;
            var copiesCamera = rig.Camera.Copied;
            var at = Stopwatch.GetTimestamp();
            if (frame < 0)
            {
                rig.Screen.Copy = true;
                rig.Camera.Copy = true;
            }
            else
            {
                rig.Controller.Position = TimeSpan.FromSeconds((frame + 0.5) / TestMedia.Fps);
            }

            rig.Screen.WaitCopied(copiesScreen + 1, 3000);
            rig.Camera.WaitCopied(copiesCamera + 1, 3000);
            Thread.Sleep(300);
            parts.Add($"{(frame < 0 ? "first frames again" : $"frame {frame}")}: screen {rig.Screen.Copies(at, copiesScreen)}; camera {rig.Camera.Copies(at, copiesCamera)}");
        }

        _report.Line($"    first copies: {first} | sources set again ({(newSource ? "a new MediaSource" : "the same MediaSource")}, {(together ? "both at once" : "one after the other")}): {(ready ? $"ready after {F(readyAfter, "0")} ms" : "NOT READY")}, first adapter {F(before, "0")} -> {F(atReady, "0")} MB{rig.Screen.Trouble()}{rig.Camera.Trouble()}");
        _report.Line($"      {string.Join(" | ", parts)}");
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>Every adapter DXGI lists, in its order, with the identifier the system's GPU counters name it by.</summary>
    private static string Adapters()
    {
        var names = new List<string>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (var index = 0u; factory.EnumAdapters1(index, out var adapter).Success && adapter is not null; index++)
        {
            using (adapter)
            {
                var description = adapter.Description1;
                names.Add(string.Create(CultureInfo.InvariantCulture, $"{index}: {description.Description} (luid 0x{description.Luid.HighPart:X8}_0x{description.Luid.LowPart:X8}{((description.Flags & AdapterFlags.Software) != 0 ? ", software" : string.Empty)})"));
            }
        }

        return string.Join("; ", names);
    }

    private static uint RefCount(SharpGen.Runtime.ComObject instance)
    {
        instance.AddRef();
        return instance.Release();
    }

    private static string DecoderProfiles(StudioGraphicsDevice graphics)
    {
        try
        {
            using var video = graphics.Device.QueryInterfaceOrNull<ID3D11VideoDevice>();
            if (video is null)
            {
                return "no video device, so no hardware decoder";
            }

            var count = video.VideoDecoderProfileCount;
            var h264 = false;
            for (var index = 0u; index < count; index++)
            {
                h264 |= video.GetVideoDecoderProfile(index) == H264Decoder;
            }

            return $"{count} video decoder profile(s), H.264 {(h264 ? "among them" : "not among them")}";
        }
        catch (Exception ex)
        {
            return $"decoder profiles not readable (0x{ex.HResult:X8})";
        }
    }

    private static HashSet<string> ModuleNames()
    {
        using var process = Process.GetCurrentProcess();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProcessModule module in process.Modules)
        {
            names.Add(module.ModuleName);
        }

        return names;
    }

    /// <summary>
    /// The graphics drivers, Direct3D and media pipeline modules loaded since
    /// <paramref name="known"/> was taken, which is then brought up to date.
    /// </summary>
    private static string NewModules(HashSet<string> known)
    {
        string[] prefixes = ["amd", "ati", "nv", "ig", "d3d", "dxva", "msmpeg2", "msvproc", "mfmedia", "mfmp4", "mfh26", "msauddec", "colorcnv", "vidreszr", "windows.media.playback.mediaplayer"];
        var added = ModuleNames().Where(name => !known.Contains(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        known.UnionWith(added);
        var listed = added.Where(name => prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToList();
        return listed.Count == 0 ? "none" : string.Join(" ", listed);
    }

    /// <summary>The memory this process uses on the first graphics adapter, which is the hardware one when there is one.</summary>
    private sealed class AdapterMemory : IDisposable
    {
        private readonly IDXGIFactory1 _factory;
        private readonly IDXGIAdapter3 _adapter;
        private readonly string _name;

        private AdapterMemory(IDXGIFactory1 factory, IDXGIAdapter3 adapter, string name)
        {
            _factory = factory;
            _adapter = adapter;
            _name = name;
        }

        public static AdapterMemory? TryCreate()
        {
            var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            if (factory.EnumAdapters1(0, out var adapter).Failure || adapter is null)
            {
                factory.Dispose();
                return null;
            }

            using (adapter)
            {
                var adapter3 = adapter.QueryInterfaceOrNull<IDXGIAdapter3>();
                if (adapter3 is null)
                {
                    factory.Dispose();
                    return null;
                }

                return new AdapterMemory(factory, adapter3, adapter.Description1.Description);
            }
        }

        public double LocalMb() => _adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).CurrentUsage / 1048576.0;

        public string Describe()
        {
            var shared = _adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.NonLocal).CurrentUsage / 1048576.0;
            return string.Create(CultureInfo.InvariantCulture, $"this process uses {LocalMb():0} MB local + {shared:0} MB shared on {_name}");
        }

        public void Dispose()
        {
            _adapter.Dispose();
            _factory.Dispose();
        }
    }

    /// <summary>A device, a clock, and a player for each clip, as the engine sets them up.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(TestFolder folder, bool software)
        {
            Device = software ? StudioGraphicsDevice.CreateWarp() : StudioGraphicsDevice.CreateHardware();
            Screen = new Probe("screen", folder.Paths.ScreenPath, TestMedia.Screen, Device, Controller);
            Camera = new Probe("camera", folder.Paths.CameraPath!, TestMedia.Camera, Device, Controller);
        }

        public StudioGraphicsDevice Device { get; }

        public MediaTimelineController Controller { get; } = new();

        public Probe Screen { get; }

        public Probe Camera { get; }

        /// <summary>Waits until <paramref name="milliseconds"/> have passed since <paramref name="origin"/>, to the fraction of a millisecond.</summary>
        public static void Wait(long origin, int milliseconds)
        {
            while (Stopwatch.GetElapsedTime(origin).TotalMilliseconds < milliseconds - 2)
            {
                Thread.Sleep(1);
            }

            while (Stopwatch.GetElapsedTime(origin).TotalMilliseconds < milliseconds)
            {
                Thread.SpinWait(50);
            }
        }

        public void Dispose()
        {
            Screen.Dispose();
            Camera.Dispose();
            Thread.Sleep(50);
            Device.Dispose();
        }
    }

    /// <summary>
    /// One muted frame-server player and a texture of its own on a device. It notes what the
    /// player reports, and for every frame it copies: how long the copy took and which frame the
    /// texture then holds.
    /// </summary>
    private sealed class Probe : IDisposable
    {
        /// <summary>B, G, R, A of the colour the texture is filled with before a copy when <see cref="Mark"/> is set.</summary>
        public static readonly byte[] Marker = [30, 200, 10, 77];

        private readonly object _sync = new();
        private readonly ClipSpec _spec;
        private readonly string _path;
        private readonly List<(long At, string Text)> _events = [];
        private readonly List<(long At, double Milliseconds, int Shows, int Frame, uint Before, uint After)> _copies = [];
        private readonly List<string> _pixels = [];
        private StudioGraphicsDevice _device;
        private ID3D11Texture2D _texture;
        private IDirect3DSurface _surface;
        private ID3D11RenderTargetView? _view;
        private MediaSource? _second;
        private int _announced;
        private int _copied;
        private bool _closed;
        private volatile string? _failure;

        public Probe(string label, string path, ClipSpec spec, StudioGraphicsDevice device, MediaTimelineController controller)
        {
            Label = label;
            _path = path;
            _spec = spec;
            _device = device;
            _texture = device.CreateRenderTexture(spec.Width, spec.Height);
            _surface = WgcInterop.CreateDirect3DSurface(_texture);

            // As the engine makes its players, and never audible.
            Player = new MediaPlayer { AutoPlay = false, IsMuted = true, Volume = 0, IsVideoFrameServerEnabled = true };
            Player.CommandManager.IsEnabled = false;
            Player.TimelineController = controller;
            Source = MediaSource.CreateFromUri(new Uri(path));
            Player.VideoFrameAvailable += OnFrame;
            Player.MediaOpened += (_, _) => Note("opened");
            Player.MediaEnded += (_, _) => Note("ended");
            Player.MediaFailed += (_, e) =>
            {
                var text = $"{e.Error} 0x{e.ExtendedErrorCode?.HResult ?? 0:X8}";
                _failure ??= text;
                Note("FAILED " + text);
            };
            Player.PlaybackSession.SeekCompleted += (_, _) => Note("seek-completed");
            Player.PlaybackSession.NaturalVideoSizeChanged += (_, _) => Note("size-changed");
        }

        public string Label { get; }

        public MediaPlayer Player { get; }

        public MediaSource Source { get; }

        /// <summary>Copy every frame the player announces. While this is off the player announces its frame again every 10 ms.</summary>
        public volatile bool Copy;

        /// <summary>Fill the texture with the marker colour before every copy, and note three pixels after it.</summary>
        public volatile bool Mark;

        public int Announced => Volatile.Read(ref _announced);

        public int Copied => Volatile.Read(ref _copied);

        public string? Failure => _failure;

        public void Start() => Player.Source = Source;

        /// <summary>Takes the source away and gives it back: the player opens it a second time.</summary>
        public void Reopen(bool newSource)
        {
            Copy = false;
            Player.Source = null;
            if (newSource)
            {
                _second = MediaSource.CreateFromUri(new Uri(_path));
            }

            Interlocked.Exchange(ref _announced, 0);
            Note("source set again");
            Player.Source = _second ?? Source;
        }

        /// <summary>From now on the player's frames are copied into a texture on another device.</summary>
        public void Retarget(StudioGraphicsDevice device)
        {
            var texture = device.CreateRenderTexture(_spec.Width, _spec.Height);
            var surface = WgcInterop.CreateDirect3DSurface(texture);
            lock (_sync)
            {
                _view?.Dispose();
                _view = null;
                (_surface as IDisposable)?.Dispose();
                _texture.Dispose();
                _device = device;
                _texture = texture;
                _surface = surface;
            }
        }

        /// <summary>Asks the player for a frame it cannot have yet, to see what it does with the texture it is given.</summary>
        public string CopyWithoutFrame()
        {
            lock (_device.Gate)
            {
                var before = RefCount(_device.Device);
                var at = Stopwatch.GetTimestamp();
                string outcome;
                try
                {
                    Player.CopyFrameToVideoSurface(_surface);
                    outcome = "returned";
                }
                catch (Exception ex)
                {
                    outcome = $"threw 0x{ex.HResult:X8}";
                }

                return string.Create(CultureInfo.InvariantCulture, $"{outcome} after {Stopwatch.GetElapsedTime(at).TotalMilliseconds:0.0} ms, references on the device {before}->{RefCount(_device.Device)}");
            }
        }

        public bool WaitAnnounced(int count, int milliseconds)
        {
            var watch = Stopwatch.StartNew();
            while (Announced < count && _failure is null && watch.ElapsedMilliseconds < milliseconds)
            {
                Thread.Sleep(1);
            }

            return Announced >= count;
        }

        public bool WaitCopied(int count, int milliseconds)
        {
            var watch = Stopwatch.StartNew();
            while (Copied < count && watch.ElapsedMilliseconds < milliseconds)
            {
                Thread.Sleep(1);
            }

            return Copied >= count;
        }

        /// <summary>The copies made since a moment: when each began, how long it took, and what the texture held afterwards.</summary>
        public string Copies(long origin, int skip = 0)
        {
            lock (_sync)
            {
                var parts = _copies.Skip(skip).Select(c => string.Create(
                    CultureInfo.InvariantCulture,
                    $"+{Stopwatch.GetElapsedTime(origin, c.At).TotalMilliseconds:0} ms {c.Milliseconds:0.0} ms {(c.Shows == FrameCode.Unreadable ? "BLANK" : "f" + c.Shows)}{(c.Shows == c.Frame ? string.Empty : $" (position f{c.Frame})")}{(c.After != c.Before ? $" refs {c.Before}->{c.After}" : string.Empty)}"));
                var text = string.Join(", ", parts);
                return text.Length == 0 ? "no copy" : text;
            }
        }

        /// <summary>What three pixels held after each copy: the centre, a corner, and the middle of the frame-number strip.</summary>
        public string Pixels()
        {
            lock (_sync)
            {
                return string.Join(" | ", _pixels);
            }
        }

        public string Events(long origin = 0)
        {
            lock (_sync)
            {
                return _events.Count == 0
                    ? "nothing reported"
                    : string.Join(", ", _events.Select(e => origin == 0 ? e.Text : string.Create(CultureInfo.InvariantCulture, $"{e.Text} +{Stopwatch.GetElapsedTime(origin, e.At).TotalMilliseconds:0}")));
            }
        }

        public string Trouble() => _failure is null ? string.Empty : $" FAILED {_failure}";

        public void Dispose()
        {
            lock (_sync)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
            }

            try
            {
                Player.TimelineController = null;
                Player.Source = null;
            }
            catch (Exception)
            {
                // A failed player may refuse.
            }

            Player.Dispose();
            Source.Dispose();
            _second?.Dispose();
            _view?.Dispose();
            (_surface as IDisposable)?.Dispose();
            _texture.Dispose();
        }

        private void Note(string text)
        {
            lock (_sync)
            {
                _events.Add((Stopwatch.GetTimestamp(), text));
            }
        }

        private void OnFrame(MediaPlayer sender, object args)
        {
            Interlocked.Increment(ref _announced);
            if (!Copy)
            {
                return;
            }

            lock (_sync)
            {
                if (_closed)
                {
                    return;
                }

                var frame = (int)Math.Floor((sender.PlaybackSession.Position.TotalSeconds * TestMedia.Fps) + 1e-6);
                lock (_device.Gate)
                {
                    if (Mark)
                    {
                        _view ??= _device.Device.CreateRenderTargetView(_texture);
                        _device.Context.ClearRenderTargetView(_view, new Vortice.Mathematics.Color4(Marker[2] / 255f, Marker[1] / 255f, Marker[0] / 255f, Marker[3] / 255f));
                    }

                    var before = RefCount(_device.Device);
                    var at = Stopwatch.GetTimestamp();
                    try
                    {
                        sender.CopyFrameToVideoSurface(_surface);
                    }
                    catch (Exception ex)
                    {
                        _events.Add((at, $"COPY FAILED 0x{ex.HResult:X8} after {Stopwatch.GetElapsedTime(at).TotalMilliseconds:0} ms"));
                        return;
                    }

                    var took = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                    var after = RefCount(_device.Device);
                    var pixels = _device.ReadTexture(_texture);
                    var shows = FrameCode.Decode(pixels, _spec.Width, _spec.Height, _spec, ClipMap.Scaled(_spec, _spec.Width, _spec.Height));
                    _copies.Add((at, took, shows, frame, before, after));
                    if (Mark)
                    {
                        string At(int x, int y)
                        {
                            var offset = ((y * _spec.Width) + x) * 4;
                            return $"({pixels[offset]},{pixels[offset + 1]},{pixels[offset + 2]},{pixels[offset + 3]})";
                        }

                        _pixels.Add($"{(shows == FrameCode.Unreadable ? "BLANK" : "f" + shows)}: centre {At(_spec.Width / 2, _spec.Height / 2)} corner {At(2, 2)} strip {At(_spec.CodeX + (_spec.CodeWidth / 2), _spec.CodeY + (_spec.CodeHeight / 2))}");
                    }

                    Interlocked.Increment(ref _copied);
                }
            }
        }
    }
}
