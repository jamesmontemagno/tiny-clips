using System.Diagnostics;
using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Preview;
using TinyClips.Tools.StudioPreviewCheck.Media;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

/// <summary>How a run's previews count their clips' frames: what <c>--frame-times</c> names.</summary>
internal static class FrameTimes
{
    /// <summary>True for <c>--frame-times file</c>, false for <c>grid</c> and when it is not given.</summary>
    public static bool FromFile(CheckOptions options) => options.Text("frame-times", "grid").ToLowerInvariant() switch
    {
        "file" => true,
        "grid" => false,
        var other => throw new ArgumentException($"--frame-times takes file or grid, not '{other}'."),
    };
}

// Clips that have a recording's frame times (RecordedMedia), in numbers: what the engine makes of
// each while it plays, when it is paused, when it is put on a slot the recorder left empty, when
// it is stepped across one, and when a zoom moves over it.
//
// A frame's number in these clips' pixels is its place in the file, counted from 0. What is right
// is the export's rule: a frame of the timeline (a slot) shows the last frame of the file that
// began at or before its middle, and the first while none has. So a picture is right under a slot
// when the export has that frame in that slot. A frame that begins after one middle, with the next
// frame beginning before the next middle, is in no slot of the export at all: such a frame is
// counted apart, and is taken to be right under the slot the export has the frame after it in.
//
//   --investigate recordings --scenario table   one line a clip and a kind of hold-up, and a
//                                               file of them in out\, for the report's table
//   --only recordings                           the same measurement with nothing in the way,
//                                               judged
//
// Both go by --frame-times (how the engine counts) and --believe-positions (how it names), so
// that the same clips can be measured four ways.
internal sealed partial class HeadlessChecks
{
    // The clips the group goes through when none is named: one of each thing a recording does.
    private static readonly string[] RecordingsOfTheGroup =
    [
        "screen-into04", "screen-into20", "screen-into04-single", "screen-into20-single", "screen-into12-runs", "screen-shift",
        "screen-middle", "screen-as-recorded", "screen-gaps", "screen60-into08-single", "camera-stalls", "camera-15",
    ];

    /// <summary>
    /// What is true of a recorded clip in a session, worked out here from the clip's frame times
    /// and not asked of the engine.
    /// </summary>
    private sealed class RecordedTruth(RecordedClip clip, Session session)
    {
        public RecordedClip Clip => clip;

        public bool IsCamera => clip.Plan.IsCamera;

        /// <summary>The clip's track: 0 for a screen clip, 1 for a camera clip, which plays beside the usual screen clip.</summary>
        public int Track => IsCamera ? 1 : 0;

        /// <summary>The timeline's frames a second: the screen clip's.</summary>
        public double Fps { get; } = session.Folder.Screen.Fps;

        /// <summary>The timeline's frames: its slots.</summary>
        public int Slots { get; } = session.Folder.FrameCount;

        /// <summary>The slot <c>Position</c> names now.</summary>
        public int Slot() => (int)Math.Round(session.Engine.Position * Fps);

        /// <summary>Whether the clip is part of the picture in a slot. A camera is not before it began and after it ended.</summary>
        public bool Visible(int slot)
        {
            if (!IsCamera)
            {
                return true;
            }

            if (session.Folder.Project.Sources.Camera is not { } camera)
            {
                return false;
            }

            var own = ((slot + 0.5) / Fps) - camera.StartOffset;
            return own >= 0 && own <= camera.Duration;
        }

        /// <summary>The frame of the file the export has in a slot, or <see cref="FrameCode.Unreadable"/> where the clip is no part of the picture.</summary>
        public int Export(int slot) => slot >= 0 && slot < Slots && Visible(slot) ? clip.FrameOfSlot(slot, Fps) : FrameCode.Unreadable;

        /// <summary>A frame of a screen clip that the export has in no slot.</summary>
        public bool InNoSlot(int frame) => frame >= 0 && frame < clip.Frames && clip.SlotsOf(frame, Fps, Slots) is null;

        /// <summary>
        /// The slots a frame of a screen clip is right under: the ones the export has it in, or,
        /// for a frame the export has in no slot, the first slot whose middle it has begun by,
        /// which is where the export has the frame after it.
        /// </summary>
        public (int First, int Last) RightSlots(int frame)
        {
            if (frame < 0 || frame >= clip.Frames)
            {
                return (-1, -1);
            }

            if (clip.SlotsOf(frame, Fps, Slots) is { } range)
            {
                return range;
            }

            var slot = Math.Clamp(SlotMath.SlotWhoseMiddleIsAtOrAfter(clip.Times[frame], Fps), 0, Slots - 1);
            return (slot, slot);
        }

        public bool IsRightUnder(int frame, long slot)
        {
            var (first, last) = RightSlots(frame);
            return first >= 0 && slot >= first && slot <= last;
        }

        /// <summary>Where the export has a frame, in words.</summary>
        public string Where(int frame)
        {
            var (first, last) = RightSlots(frame);
            return first < 0 ? "the file has no such frame"
                : InNoSlot(frame) ? $"the export has it in no slot, and the frame after it in slot {first}"
                : first == last ? $"the export has it in slot {first}"
                : $"the export has it in slots {first} to {last}";
        }

        /// <summary>Which frame the clip's picture holds: the texture the scene is drawn from.</summary>
        public int Picture() => session.ReadClipFrame(Track);

        /// <summary>Which frame of the clip the scene shows, read from the surface.</summary>
        public int SceneShows()
        {
            var shown = session.ReadShown();
            return IsCamera ? shown.Camera : shown.Screen;
        }

        public string Name(int frame) =>
            frame == FrameCode.Unreadable ? "nothing" : frame < 0 || frame >= clip.Frames ? $"frame {frame}" : $"frame {frame} ({Ms(clip.Times[frame])} ms)";
    }

    /// <summary>What one recorded clip came to under one kind of hold-up.</summary>
    private sealed class RecordedRun(string clip, string holdUp)
    {
        private readonly List<string> _first = [];

        public string Clip => clip;

        public string HoldUp => holdUp;

        /// <summary>Why the measurement did not run to its end, or null.</summary>
        public string? Stopped { get; set; }

        /// <summary>The engine counts this clip in frames of its file.</summary>
        public bool CountsFileFrames { get; set; }

        /// <summary>What the engine says its frame numbers count, clip by clip.</summary>
        public string FrameTimes { get; set; } = string.Empty;

        public string HoldUps { get; set; } = string.Empty;

        public int Failures { get; set; }

        // While playing: the frames the clip's player handed over.
        public int Frames { get; set; }

        public int AtOnce { get; set; }

        public int Late { get; set; }

        public int Never { get; set; }

        public int LongestWithout { get; set; }

        public int Unsure { get; set; }

        /// <summary>
        /// Frames with a number that was wrong. A screen frame: shown under a slot the export
        /// does not have it in, or, counted in frames of the file, with a number that is not its
        /// place in the file. A camera frame, counted in frames of the file: the same of its number.
        /// </summary>
        public int Wrong { get; set; }

        /// <summary>Numbered screen frames that the export has in no slot.</summary>
        public int InNoSlot { get; set; }

        public long FetchedAnew { get; set; }

        /// <summary>Times the playing was started from after a stretch of more than 0.4 s without a frame, and not from in front of it.</summary>
        public int LongGapsSkipped { get; set; }

        // Pauses.
        public int Pauses { get; set; }

        /// <summary>When Pause() returned, Position was a slot the export does not have the picture in.</summary>
        public int PauseWrong { get; set; }

        /// <summary>When Pause() returned, the picture was a frame the export has in no slot, under the slot of the frame after it.</summary>
        public int PausesOnAFrameInNoSlot { get; set; }

        /// <summary>At rest after the pause, the picture was not the export's frame for Position.</summary>
        public int RestWrong { get; set; }

        public int ChangedAfterReturn { get; set; }

        /// <summary>At rest, an earlier frame than the last scene drawn while playing had shown.</summary>
        public int WentBack { get; set; }

        // Paused seeks to the slots around a slot that shows the frame before it shows.
        public int Rests { get; set; }

        public int RestsWrong { get; set; }

        public Samples RestMilliseconds { get; } = new();

        public long RestRepairs { get; set; }

        public long RestStrays { get; set; }

        public long RestSeeks { get; set; }

        // One slot at a time across such a slot, and back.
        public int Steps { get; set; }

        public int StepsWrong { get; set; }

        /// <summary>Moves of one slot during which a scene was drawn that showed another frame than the export's for the slot gone to.</summary>
        public int StepsShowedAnother { get; set; }

        public long Stepped { get; set; }

        public long StepFallbacks { get; set; }

        public long StepRepairs { get; set; }

        // A zoom that moves while the clip plays.
        public string? ZoomSkipped { get; set; }

        public int Scenes { get; set; }

        public int ScenesInTheMove { get; set; }

        public int ScenesWrong { get; set; }

        public int MoveSlots { get; set; }

        /// <summary>Slots of the move that some scene was drawn with the layout of.</summary>
        public int MoveSlotsDrawn { get; set; }

        public int ZoomRests { get; set; }

        public int ZoomRestsWrong { get; set; }

        /// <summary>Rests where the layouts of the slot and of the slot before it were less than the tolerance apart, so that the scene could not tell which it had.</summary>
        public int ZoomRestsCannotTell { get; set; }

        /// <summary>The first few things that were not as they should be, in words.</summary>
        public IReadOnlyList<string> First => _first;

        public void Note(string text)
        {
            if (_first.Count < 5)
            {
                _first.Add(text);
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // The table
    // ---------------------------------------------------------------------------------------

    private void RecordedTable(IReadOnlyList<RecordedPlan> plans)
    {
        var kinds = HoldUpKinds("none", "collector", "draw");
        var pauses = _options.Number("count", _quick ? 4 : 10);
        var naming = _options.Flag("believe-positions") ? "positions" : "rules";
        var counting = FrameTimes.FromFile(_options) ? "file" : "grid";
        _report.Section($"Clips with a recording's frame times, measured: the engine counting in frames of {(counting == "file" ? "the file" : "the grid")}, naming by {(naming == "rules" ? "its rules" : "position alone")}; {pauses} pauses a clip and hold-up");
        _report.Line("  A frame's number is its place in the file, read from the picture. Right is what the export does: a slot shows the last frame that began at or before its middle.");
        var path = Path.Combine(_output, $"recordings-table-{_report.Stamp}-{counting}-{naming}.csv");
        var lines = new List<string> { RecordedRunCsvHeader };
        foreach (var plan in plans)
        {
            RecordedClip clip;
            try
            {
                clip = RecordedMedia.Ensure(_media, plan, _report);
            }
            catch (Exception ex)
            {
                _report.Line($"{plan.Name}: could not be made: {Describe(ex)}");
                continue;
            }

            foreach (var kind in kinds)
            {
                var run = MeasureRecording(clip, kind, pauses, zoom: true);
                Tell(run, plan);
                lines.Add(Csv(run, counting, naming));

                // Written as it goes, so that a run that is cut short leaves what it had.
                try
                {
                    File.WriteAllLines(path, lines);
                }
                catch (IOException)
                {
                    // The report has it too.
                }
            }
        }

        _report.Line();
        _report.Line($"  the table: {path}");
    }

    private const string RecordedRunCsvHeader =
        "clip,hold_up,counting,naming,counts_file_frames,stopped,frames,at_once,late,never,longest_without,unsure,wrong,in_no_slot,fetched_anew,"
        + "pauses,pause_wrong,pause_on_frame_in_no_slot,rest_wrong,changed_after_return,went_back,"
        + "rests,rests_wrong,rest_ms_median,rest_ms_max,rest_repairs,rest_strays,rest_seeks,"
        + "steps,steps_wrong,steps_showed_another,stepped,step_fallbacks,step_repairs,"
        + "scenes,scenes_in_move,scenes_wrong,move_slots,move_slots_drawn,zoom_rests,zoom_rests_wrong,zoom_rests_cannot_tell,failures";

    // The tool runs with the invariant culture (Program.Main), so the numbers come out with a point.
    private static string Csv(RecordedRun run, string counting, string naming) => string.Join(
        ',',
        run.Clip, run.HoldUp, counting, naming, run.CountsFileFrames ? 1 : 0, run.Stopped is null ? 0 : 1, run.Frames, run.AtOnce, run.Late, run.Never, run.LongestWithout, run.Unsure, run.Wrong, run.InNoSlot, run.FetchedAnew,
        run.Pauses, run.PauseWrong, run.PausesOnAFrameInNoSlot, run.RestWrong, run.ChangedAfterReturn, run.WentBack,
        run.Rests, run.RestsWrong, F(run.RestMilliseconds.Percentile(50), "0"), F(run.RestMilliseconds.Max, "0"), run.RestRepairs, run.RestStrays, run.RestSeeks,
        run.Steps, run.StepsWrong, run.StepsShowedAnother, run.Stepped, run.StepFallbacks, run.StepRepairs,
        run.Scenes, run.ScenesInTheMove, run.ScenesWrong, run.MoveSlots, run.MoveSlotsDrawn, run.ZoomRests, run.ZoomRestsWrong, run.ZoomRestsCannotTell, run.Failures);

    /// <summary>One measurement in words.</summary>
    private void Tell(RecordedRun run, RecordedPlan plan)
    {
        _report.Line();
        _report.Line($"{run.Clip}, with {HoldUpInWords(run.HoldUp)}: {plan.What}");
        if (run.FrameTimes.Length > 0)
        {
            _report.Line($"  frame numbers: {run.FrameTimes}");
        }

        if (run.Stopped is not null)
        {
            _report.Line($"  STOPPED: {run.Stopped}");
        }

        _report.Line($"  playing: of {run.Frames} frames the {(plan.IsCamera ? "camera's" : "screen's")} player handed over, {run.AtOnce} were numbered at once, {run.Late} by the frame after them, {run.Never} never ({run.LongestWithout} in a row at most), {run.Unsure} were shown under the number of their position and called unsure; {run.Wrong} had a wrong number{(plan.IsCamera ? (run.CountsFileFrames ? " (not its place in the file)" : " (not judged: on the grid a camera's number is no frame of the file)") : $" (a slot the export does not have the frame in); {run.InNoSlot} were frames the export has in no slot")}");
        _report.Line($"  {run.Pauses} pauses: when Pause() returned, Position was a slot the export does not have the picture in {run.PauseWrong} times{(plan.IsCamera ? " (not judged for a camera, which is brought onto its frame afterwards)" : $", and {run.PausesOnAFrameInNoSlot} times the picture was a frame the export has in no slot")}; at rest the picture was not the export's for Position {run.RestWrong} times; picture or Position changed after Pause() had returned {run.ChangedAfterReturn} times; the rest was on an earlier frame than the last scene had shown {run.WentBack} times; frames fetched anew at rest: {run.FetchedAnew}{(run.LongGapsSkipped > 0 ? $"; {run.LongGapsSkipped} of the plays were started from after a stretch of more than 0.4 s without a frame, where the playhead would have waited" : string.Empty)}");
        _report.Line($"  {run.Rests} paused seeks to the first slots, the last one and the slots around those that show what the slot before shows: {run.RestsWrong} did not show the export's frame; the first scene after the call: {run.RestMilliseconds.Summary()}; {run.RestSeeks} seeks, {run.RestRepairs} frames fetched by a detour, {run.RestStrays} late answers");
        _report.Line($"  {run.Steps} moves of one slot, forward across such a slot and back: {run.StepsWrong} did not show the export's frame at rest, and during {run.StepsShowedAnother} a scene was drawn that showed another frame; {run.Stepped} were made by stepping a player, {run.StepFallbacks} of them fell back on a seek, {run.StepRepairs} frames fetched by a detour");
        if (!plan.IsCamera)
        {
            _report.Line(run.ZoomSkipped is { } skipped
                ? $"  a zoom that moves: not measured: {skipped}"
                : $"  a zoom that moves: {run.Scenes} scenes drawn, {run.ScenesInTheMove} of them of frames the export has inside the move; {run.ScenesWrong} did not have the layout of a slot the export has their picture in; {run.MoveSlotsDrawn} of the move's {run.MoveSlots} slots had a scene drawn with their layout; at rest in the move, {run.ZoomRestsWrong} of {run.ZoomRests} scenes did not have their own slot's layout{(run.ZoomRestsCannotTell > 0 ? $" ({run.ZoomRestsCannotTell} more could not tell: the layout moves less than a pixel there)" : string.Empty)}");
        }

        if (run.HoldUps.Length > 0 && run.HoldUp != "none")
        {
            _report.Line($"  {run.HoldUps}");
        }

        foreach (var first in run.First)
        {
            _report.Line($"  first: {first}");
        }
    }

    /// <summary>A kind of hold-up in words, without making the hold-up.</summary>
    private static string HoldUpInWords(string kind) => kind switch
    {
        "none" => "nothing holding the process up",
        "collector" => "the garbage collector at work",
        "draw" => "slow draws",
        "stopped" => "the whole process stopped now and then",
        _ => kind,
    };

    // ---------------------------------------------------------------------------------------
    // The group
    // ---------------------------------------------------------------------------------------

    private void Recordings()
    {
        var names = _options.Names("clip");
        var plans = (names.Length == 0 ? RecordingsOfTheGroup : names).Select(RecordedMedia.Plan).ToList();
        var pauses = _options.Number("count", _quick ? 4 : 10);
        var byFile = FrameTimes.FromFile(_options);
        _report.Section($"Clips with a recording's frame times: {plans.Count} clips, the engine counting in frames of {(byFile ? "the file" : "the grid, which is how it runs by itself; the checks on clips whose frames sit late in their slot or leave a slot empty are expected to fail")}");
        foreach (var plan in plans)
        {
            RecordedClip clip;
            try
            {
                clip = RecordedMedia.Ensure(_media, plan, _report);
            }
            catch (Exception ex)
            {
                _report.Check($"{plan.Name}: the clip was made", false, Describe(ex));
                continue;
            }

            var run = MeasureRecording(clip, "none", pauses, zoom: true);
            Tell(run, plan);
            var first = run.First.Count == 0 ? string.Empty : "; first: " + string.Join(" | ", run.First.Take(3));
            _report.Check($"{plan.Name}: the preview opened and the measurement ran to its end, without a failure reported", run.Stopped is null && run.Failures == 0, run.Stopped);
            _report.Check(
                $"{plan.Name}: every frame its player handed over while playing had its number at once",
                run.Frames > 0 && run.AtOnce == run.Frames,
                $"{run.AtOnce} of {run.Frames}; {run.Late} by the frame after, {run.Never} never, {run.Unsure} unsure");
            if (plan.IsCamera && !run.CountsFileFrames)
            {
                // Nothing to hold it to: the number decides nothing in the scene, and what it
                // does to seeks and steps is in the checks below.
                _report.Note($"{plan.Name}: the camera's numbers are not judged: on the grid a camera's number is no frame of its file");
            }
            else
            {
                _report.Check(
                    plan.IsCamera
                        ? $"{plan.Name}: no frame of the camera had a number that is not its place in the file"
                        : $"{plan.Name}: no frame was shown under a slot the export does not have it in",
                    run.Wrong == 0,
                    $"{run.Wrong} wrong{(plan.IsCamera ? string.Empty : $"; {run.InNoSlot} were frames the export has in no slot")}{first}");
            }
            _report.Check(
                plan.IsCamera
                    ? $"{plan.Name}: at rest after each pause the camera's picture is the export's frame for Position ({run.Pauses} pauses)"
                    : $"{plan.Name}: when Pause() returns, Position is a slot the export has the picture in, or the picture is a frame the export has in no slot; and at rest the picture is the export's frame for Position ({run.Pauses} pauses)",
                run.Pauses > 0 && run.PauseWrong == 0 && run.RestWrong == 0,
                $"at the return {run.PauseWrong} wrong, {run.PausesOnAFrameInNoSlot} on a frame the export has in no slot; at rest {run.RestWrong} wrong{first}");
            _report.Check(
                $"{plan.Name}: a paused seek to each of the first slots, the last one and the slots around those that show what the slot before shows, shows the export's frame ({run.Rests} seeks)",
                run.Rests > 0 && run.RestsWrong == 0,
                $"{run.RestsWrong} wrong; {run.RestRepairs} frames fetched by a detour{first}");
            _report.Check(
                $"{plan.Name}: one slot at a time forward across such a slot, and back, shows the export's frame each time, and no scene drawn on the way shows another ({run.Steps} moves)",
                run.Steps > 0 && run.StepsWrong == 0 && run.StepsShowedAnother == 0,
                $"{run.StepsWrong} wrong at rest, {run.StepsShowedAnother} showed another frame on the way; {run.Stepped} made by stepping a player, {run.StepFallbacks} fell back on a seek{first}");
            if (!plan.IsCamera && run.ZoomSkipped is null)
            {
                _report.Check(
                    $"{plan.Name}: every scene drawn while a zoom moves has the layout of a slot the export has its picture in, to within {F(MovingLayout.Tolerance)} px",
                    run.Scenes > 0 && run.ScenesWrong == 0,
                    $"{run.ScenesWrong} of {run.Scenes} wrong{first}");
                _report.Check(
                    $"{plan.Name}: at rest while the zoom moves, on a slot and its neighbours, the scene has the layout of the slot it rests on ({run.ZoomRests} rests)",
                    run.ZoomRests > 0 && run.ZoomRestsWrong == 0,
                    $"{run.ZoomRestsWrong} wrong, {run.ZoomRestsCannotTell} could not tell{first}");
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // The measurement
    // ---------------------------------------------------------------------------------------

    private RecordedRun MeasureRecording(RecordedClip clip, string kind, int pauses, bool zoom)
    {
        var run = new RecordedRun(clip.Name, kind);
        using var holdUps = new HoldUps(kind, HoldUpMilliseconds);
        Session session;
        try
        {
            // A camera clip plays beside the usual screen clip, whose frames are on the grid.
            session = clip.Plan.IsCamera
                ? OpenSession(clip.Spec, cameraOffset: clip.StartOffset, options: holdUps.With(Muted))
                : OpenSession(camera: null, options: holdUps.With(Muted), screen: clip.Spec);
        }
        catch (Exception ex)
        {
            run.Stopped = $"the preview did not open: {Describe(ex)}{DumpFailedOpen(ex, "recordings")}";
            return run;
        }

        try
        {
            var truth = new RecordedTruth(clip, session);
            var state = session.Engine.GetDiagnostics();
            run.CountsFileFrames = state.CountsFileFrames.Length > truth.Track && state.CountsFileFrames[truth.Track];
            run.FrameTimes = string.Join("; ", state.FrameTimes.Select((note, index) => $"the {(index == 0 ? "screen" : "camera")} by {note}"));
            holdUps.Begin();
            PlaysAndPauses(session, truth, run, pauses);
            RestsAroundRepeats(session, truth, run);
            StepsAcrossARepeat(session, truth, run);
            holdUps.Rest();
            run.Failures = session.Events.FailedEvents;
            if (run.Failures > 0)
            {
                run.Note("the preview reported a failure: " + string.Join("; ", session.Events.Failures()));
            }

            run.HoldUps = holdUps.Describe();
        }
        catch (Exception ex)
        {
            run.Stopped = $"{Describe(ex)}{Dump(session, "recordings")}";
        }
        finally
        {
            holdUps.Rest();
            Close(session);
        }

        if (zoom && !clip.Plan.IsCamera && run.Stopped is null)
        {
            try
            {
                ZoomOverARecording(clip, holdUps, run);
            }
            catch (Exception ex)
            {
                run.Stopped = $"the zoom: {Describe(ex)}{DumpFailedOpen(ex, "recordings-zoom")}";
            }
        }

        return run;
    }

    /// <summary>Puts the paused preview on a slot and waits for it to come to rest.</summary>
    private static bool RestOn(Session session, RecordedTruth truth, int slot)
    {
        session.Engine.Seek((slot + 0.5) / truth.Fps);
        return session.WaitForIdle();
    }

    /// <summary>What is not as the export has it with the preview at rest on a slot, or null.</summary>
    private static string? RestProblem(Session session, RecordedTruth truth, int slot, bool idle)
    {
        if (!idle)
        {
            return "the engine did not come to rest";
        }

        var position = truth.Slot();
        var picture = truth.Picture();
        var scene = truth.SceneShows();
        var export = truth.Export(slot);
        return position != slot ? $"Position was slot {position}"
            : export == FrameCode.Unreadable ? (scene == FrameCode.Unreadable ? null : $"the scene showed the camera's {truth.Name(scene)} where the camera is no part of the picture")
            : picture != export ? $"the picture was {truth.Name(picture)}; the export has {truth.Name(export)} there"
            : scene != picture ? $"the scene showed {truth.Name(scene)} and the picture was {truth.Name(picture)}"
            : null;
    }

    /// <summary>Plays and pauses, again and again: what the frames handed over were taken for, and what each pause left.</summary>
    private void PlaysAndPauses(Session session, RecordedTruth truth, RecordedRun run, int pauses)
    {
        // Not from the very start, where a recording may have no frame of its own, and not into the end.
        var from = (int)Math.Min(truth.Fps, truth.Slots / 4.0);
        var turnBack = truth.Slots - (int)(2.5 * truth.Fps);
        using var probe = new TruthProbe(session.Engine, session.Folder.Screen, session.Folder.Camera, capacity: 600);
        RestOn(session, truth, from);
        probe.Read();
        var before = session.Engine.GetDiagnostics();
        var late = new Dictionary<long, (long Frame, long Slot)>();
        var without = 0;
        for (var cycle = 0; cycle < pauses; cycle++)
        {
            if (truth.Slot() > turnBack)
            {
                RestOn(session, truth, from);
                probe.Read();
            }

            // Not from in front of a long stretch without a frame: the playhead waits on the
            // frame before such a stretch until the next frame comes, and a pause in it rests
            // there, so that every play from there would end where it began.
            var resting = truth.Slot();
            var next = resting + 1;
            while (!truth.IsCamera && next < truth.Slots && truth.Export(next) == truth.Export(resting))
            {
                next++;
            }

            if (next - resting > truth.Fps / 2.5 && next < turnBack)
            {
                run.LongGapsSkipped++;
                RestOn(session, truth, next);
                probe.Read();
            }

            session.Recorder.Drain();
            session.Engine.Play();
            Thread.Sleep(150 + _random.Next(350));
            var called = Stopwatch.GetTimestamp();
            session.Engine.Pause();

            // The moment Pause() has returned: what a caller reads, and what the picture holds.
            var slotAtReturn = truth.Slot();
            var pictureAtReturn = truth.Picture();
            var playing = session.Engine.IsPlaying;

            // When the engine has nothing left to do, and a moment after that.
            var idle = session.WaitForIdle();
            Thread.Sleep(60);
            idle &= session.WaitForIdle();
            var slot = truth.Slot();
            var picture = truth.Picture();
            var lastPlayed = session.Recorder.Drain()
                .Where(scene => scene.At < called)
                .Select(scene => truth.IsCamera ? scene.Camera : scene.Screen)
                .LastOrDefault(frame => frame != FrameCode.Unreadable, FrameCode.Unreadable);
            CountFrames(probe.Read(), truth, run, late, ref without);
            run.Pauses++;

            // What Pause() promises for the moment it returns, for the screen: the camera can
            // have stopped a frame apart, and is brought onto its frame afterwards.
            if (!truth.IsCamera)
            {
                var atReturn = truth.Export(slotAtReturn);
                if (playing)
                {
                    run.PauseWrong++;
                    run.Note($"pause {cycle + 1}: IsPlaying was still true when Pause() had returned");
                }
                else if (pictureAtReturn == atReturn)
                {
                    // As the export has it.
                }
                else if (truth.InNoSlot(pictureAtReturn) && truth.IsRightUnder(pictureAtReturn, slotAtReturn))
                {
                    run.PausesOnAFrameInNoSlot++;
                }
                else
                {
                    run.PauseWrong++;
                    run.Note($"pause {cycle + 1}: when Pause() had returned, Position was slot {slotAtReturn} and the picture {truth.Name(pictureAtReturn)}; {truth.Where(pictureAtReturn)}{Dump(session, "recordings-pause")}");
                }
            }

            if (RestProblem(session, truth, slot, idle) is { } problem)
            {
                run.RestWrong++;
                run.Note($"pause {cycle + 1}: at rest on slot {slot}, {problem}{Dump(session, "recordings-rest")}");
            }

            run.ChangedAfterReturn += slot != slotAtReturn || picture != pictureAtReturn ? 1 : 0;
            run.WentBack += lastPlayed != FrameCode.Unreadable && picture != FrameCode.Unreadable && picture < lastPlayed ? 1 : 0;
        }

        run.FetchedAnew += session.Engine.GetDiagnostics().RestsFetchedAnew - before.RestsFetchedAnew;
    }

    /// <summary>Takes in the frames a clip's player handed over while the clock ran: how each got its number, and whether the number was right.</summary>
    /// <param name="late">The frames that got their number from the frame after them, by which copy they were.</param>
    /// <param name="without">How many frames in a row have gone without a number, carried from call to call.</param>
    private static void CountFrames(List<ProbedFrame> frames, RecordedTruth truth, RecordedRun run, Dictionary<long, (long Frame, long Slot)> late, ref int without)
    {
        foreach (var frame in frames)
        {
            if (frame.Clip == truth.Track && frame.EarlierSerial != 0)
            {
                late[frame.EarlierSerial] = (frame.EarlierFrame, frame.EarlierTimelineFrame);
            }
        }

        foreach (var frame in frames.Where(f => f.Clip == truth.Track && f.Kind == StudioPreviewHandOverKind.Playback && f.Truth != FrameCode.Unreadable).OrderBy(f => f.Serial))
        {
            run.Frames++;
            long number;
            long slot;
            string how;
            if (frame.Knowledge == StudioPreviewFrameKnowledge.Known)
            {
                run.AtOnce++;
                (number, slot, how) = (frame.Frame, frame.TimelineFrame, frame.Rule ?? string.Empty);
            }
            else if (frame.Knowledge == StudioPreviewFrameKnowledge.Unsure)
            {
                run.Unsure++;
                (number, slot, how) = (frame.Frame, frame.TimelineFrame, "unsure, by position");
            }
            else if (late.TryGetValue(frame.Serial, out var later))
            {
                run.Late++;
                (number, slot, how) = (later.Frame, later.Slot, "by the frame after it");
            }
            else
            {
                run.Never++;
                run.LongestWithout = Math.Max(run.LongestWithout, ++without);
                continue;
            }

            without = 0;
            if (truth.IsCamera)
            {
                // A camera's number is no slot of the timeline. Where the engine counts in frames
                // of the file, it has to be the frame's place in the file.
                if (run.CountsFileFrames && number != frame.Truth)
                {
                    run.Wrong++;
                    run.Note($"camera copy {frame.Serial}: {truth.Name(frame.Truth)}, numbered {number} ({how}); its position was {Ms(frame.PositionTicks)} ms");
                }

                continue;
            }

            run.InNoSlot += truth.InNoSlot(frame.Truth) ? 1 : 0;
            if (!truth.IsRightUnder(frame.Truth, slot) || (run.CountsFileFrames && number != frame.Truth))
            {
                run.Wrong++;
                run.Note($"screen copy {frame.Serial}: {truth.Name(frame.Truth)}, numbered {number} ({how}) and shown under slot {slot}; {truth.Where(frame.Truth)}; its position was {Ms(frame.PositionTicks)} ms");
            }
        }
    }

    /// <summary>
    /// The slots to rest on: the first three and the last one, and the slots around the first few
    /// that show the frame the slot before shows (a slot the recorder left empty, a camera that
    /// stalled), with the end of each such run. Five slots a third of the way in where there is none.
    /// </summary>
    private static List<int> SlotsAroundRepeats(RecordedTruth truth, int runs, int from = 6, int? to = null)
    {
        var slots = new SortedSet<int>();
        var found = 0;
        var end = to ?? (truth.Slots - 6);
        for (var slot = Math.Max(1, from); slot < end && found < runs; slot++)
        {
            if (!truth.Visible(slot) || !truth.Visible(slot - 1) || truth.Export(slot) != truth.Export(slot - 1))
            {
                continue;
            }

            found++;
            for (var near = slot - 2; near <= slot + 2; near++)
            {
                slots.Add(near);
            }

            // On to the last slot that still shows that frame, and the two after it.
            while (slot + 1 < end && truth.Export(slot + 1) == truth.Export(slot))
            {
                slot++;
            }

            slots.Add(slot);
            slots.Add(slot + 1);
            slots.Add(slot + 2);
        }

        if (slots.Count == 0)
        {
            for (var near = (truth.Slots / 3) - 2; near <= (truth.Slots / 3) + 2; near++)
            {
                slots.Add(near);
            }
        }

        return [.. slots.Where(slot => slot >= 0 && slot < truth.Slots)];
    }

    private void RestsAroundRepeats(Session session, RecordedTruth truth, RecordedRun run)
    {
        var slots = new SortedSet<int>(SlotsAroundRepeats(truth, runs: 3)) { 0, 1, 2, truth.Slots - 1 };
        var before = session.Engine.GetDiagnostics();
        foreach (var slot in slots.Where(slot => slot >= 0 && slot < truth.Slots))
        {
            session.Recorder.Drain();
            var asked = Stopwatch.GetTimestamp();
            var idle = RestOn(session, truth, slot);
            var drawn = session.Recorder.Drain();
            if (drawn.Count > 0)
            {
                run.RestMilliseconds.Add(Stopwatch.GetElapsedTime(asked, drawn[0].At).TotalMilliseconds);
            }

            run.Rests++;
            if (RestProblem(session, truth, slot, idle) is { } problem)
            {
                run.RestsWrong++;
                run.Note($"a paused seek to slot {slot}: {problem}{Dump(session, "recordings-seek")}");
            }
        }

        var after = session.Engine.GetDiagnostics();
        run.RestRepairs += after.Repairs - before.Repairs;
        run.RestStrays += after.StrayFrames - before.StrayFrames;
        run.RestSeeks += after.SeeksIssued - before.SeeksIssued;
    }

    private void StepsAcrossARepeat(Session session, RecordedTruth truth, RecordedRun run)
    {
        // The first slot that shows what the slot before shows, or a slot a third of the way in.
        var around = SlotsAroundRepeats(truth, runs: 1);
        var repeat = around.Count > 2 ? around[2] : truth.Slots / 3;
        var start = Math.Max(0, repeat - 3);
        var end = Math.Min(truth.Slots - 1, repeat + 4);
        if (end <= start)
        {
            return;
        }

        if (!RestOn(session, truth, start))
        {
            run.Note($"the engine did not come to rest on slot {start}, where the moves of one slot start from");
        }

        var before = session.Engine.GetDiagnostics();
        foreach (var slot in Enumerable.Range(start + 1, end - start).Concat(Enumerable.Range(start, end - start).Reverse()))
        {
            session.Recorder.Drain();
            var idle = RestOn(session, truth, slot);
            run.Steps++;
            if (RestProblem(session, truth, slot, idle) is { } problem)
            {
                run.StepsWrong++;
                run.Note($"one slot on to slot {slot}: {problem}{Dump(session, "recordings-step")}");
            }

            // What was drawn on the way: a player that is stepped shows its next frame at once,
            // and is put right, if that was not the frame, only when the clock is brought along.
            var export = truth.Export(slot);
            var passing = session.Recorder.Drain()
                .Select(scene => truth.IsCamera ? scene.Camera : scene.Screen)
                .Where(frame => frame != FrameCode.Unreadable && frame != export)
                .Distinct()
                .ToList();
            if (export != FrameCode.Unreadable && passing.Count > 0)
            {
                run.StepsShowedAnother++;
                run.Note($"one slot on to slot {slot}: a scene drawn on the way showed {string.Join(" and ", passing.Select(truth.Name))}; the export has {truth.Name(export)} there");
            }
        }

        var after = session.Engine.GetDiagnostics();
        run.Stepped += after.StepsIssued - before.StepsIssued;
        run.StepFallbacks += after.StepFallbacks - before.StepFallbacks;
        run.StepRepairs += after.Repairs - before.Repairs;
    }

    /// <summary>
    /// Plays the clip through a zoom that moves, and rests on slots inside the move: whether each
    /// scene has the layout of a slot the export has its picture in.
    /// </summary>
    private void ZoomOverARecording(RecordedClip clip, HoldUps holdUps, RecordedRun run)
    {
        double fps = clip.Plan.Fps;

        // Three seconds to move in: from 4 s in a clip of twelve seconds, from 1 s in a shorter one.
        var start = clip.Duration >= 11 ? ZoomStart : 1.0;
        var zoomEnd = Math.Min(ZoomEnd, clip.Duration - 0.2);
        if (zoomEnd < start + ZoomEaseIn + 0.5)
        {
            run.ZoomSkipped = "the clip is too short for a zoom that takes three seconds to move in";
            return;
        }

        StudioProject WithZoom(StudioProject project) => project with
        {
            Zooms =
            [
                new StudioZoom
                {
                    Start = start,
                    End = zoomEnd,
                    Scale = 2,
                    Focus = new StudioZoomFocus { X = ZoomFocusX, Y = ZoomFocusY },
                    EaseIn = ZoomEaseIn,
                    EaseOut = 0.3,
                },
            ],
        };

        var session = OpenSession(camera: null, edit: WithZoom, options: holdUps.With(Muted), screen: clip.Spec);
        try
        {
            var truth = new RecordedTruth(clip, session);

            // The slots whose middles lie inside the move.
            var first = (int)Math.Floor((start * fps) - 0.5) + 1;
            var last = (int)Math.Ceiling(((start + ZoomEaseIn) * fps) - 0.5) - 1;
            var from = Math.Max(2, first - 20);
            var to = Math.Min(truth.Slots - 3, last + 7);
            var layout = new MovingLayout(session.Project, session.Folder.Screen, null, 1280, 720) { Fps = fps, SlotsOfFrame = truth.RightSlots };
            run.MoveSlots = last - first + 1;
            RestOn(session, truth, from);
            List<SceneFit> scenes;
            string? problem;
            using (var recorder = new MovingSceneRecorder(layout, from - 2, to + 30, capacity: (to - from) + 120))
            {
                // The tool keeps out of the preview's way while it plays.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var watch = Stopwatch.StartNew();
                var limit = ((to - from) / fps) + 6;
                var reached = false;
                session.Engine.AfterRender = recorder.OnRender;
                try
                {
                    holdUps.Begin();
                    session.Engine.Play();
                    while (watch.Elapsed.TotalSeconds < limit)
                    {
                        if (truth.Slot() >= to)
                        {
                            reached = true;
                            break;
                        }

                        Thread.Sleep(5);
                    }

                    session.Engine.Pause();
                    holdUps.Rest();
                    session.WaitForIdle();
                }
                finally
                {
                    holdUps.Rest();
                    session.Engine.AfterRender = session.Recorder.OnRender;
                }

                scenes = recorder.Read(from);
                problem = recorder.Failure is { } failure ? "the scenes could not be kept: " + failure
                    : recorder.Unkept > 0 ? $"{recorder.Unkept} scenes were drawn that there was no room to keep"
                    : !reached ? $"playback did not reach slot {to} in {F(limit)} s (Position slot {truth.Slot()})"
                    : null;

                // A picture of the first scene that is wrong, and the trace, for a look at it.
                var firstWrong = scenes.FindIndex(scene => scene.Screen == FrameCode.Unreadable || !scene.ScreenFit.Within(MovingLayout.Tolerance));
                if (firstWrong >= 0 && Interlocked.Increment(ref _dumps) <= 12)
                {
                    try
                    {
                        Directory.CreateDirectory(_failuresDirectory);
                        var stem = $"recordings-zoom-{clip.Name}-{_dumps}";
                        recorder.Save(firstWrong, Path.Combine(_failuresDirectory, $"{stem}-scene-{firstWrong}.png"));
                        File.WriteAllLines(Path.Combine(_failuresDirectory, $"{stem}-trace.txt"), session.Trace.Lines());
                        run.Note($"the first wrong scene of the zoom and the trace are in {Session.DisplayPath(_failuresDirectory)}\\{stem}-*");
                    }
                    catch (Exception)
                    {
                        // Evidence only.
                    }
                }
            }

            if (problem is not null)
            {
                run.ScenesWrong++;
                run.Note("the zoom: " + problem);
            }

            var drawn = new HashSet<int>();
            foreach (var scene in scenes)
            {
                run.Scenes++;
                if (scene.Screen == FrameCode.Unreadable)
                {
                    run.ScenesWrong++;
                    run.Note("a scene of the zoom whose frame could not be read");
                    continue;
                }

                var (rightFirst, rightLast) = truth.RightSlots(scene.Screen);
                run.ScenesInTheMove += rightLast >= first && rightFirst <= last ? 1 : 0;
                if (scene.ScreenFit.Within(MovingLayout.Tolerance))
                {
                    if (scene.Slot >= first && scene.Slot <= last)
                    {
                        drawn.Add(scene.Slot);
                    }

                    continue;
                }

                run.ScenesWrong++;
                run.Note($"a scene of the zoom showed {truth.Name(scene.Screen)} ({truth.Where(scene.Screen)}) with {(scene.FitsFrame is { } other ? $"the layout of slot {other}" : "a layout that is none of the slots' around it")}: an edge of its strip was {scene.ScreenFit} from where its own slot puts it");
            }

            run.MoveSlotsDrawn = drawn.Count;

            // At rest inside the move: on the slots around those that show what the slot before
            // shows, and on three in the middle where there are none. No new picture comes for
            // such a slot, and the scene has to have its layout all the same.
            var rests = SlotsAroundRepeats(truth, runs: 3, from: first + 15, to: last - 15).Where(slot => slot >= first + 10 && slot <= last - 10).ToList();
            if (rests.Count == 0)
            {
                var middle = (first + last) / 2;
                rests = [middle - 1, middle, middle + 1];
            }

            foreach (var slot in rests.Take(12))
            {
                var idle = RestOn(session, truth, slot);
                var picture = session.ReadPicture();
                var export = truth.Export(slot);
                run.ZoomRests++;
                if (!idle || picture is null || layout.ScreenMap(slot) is not { } map)
                {
                    run.ZoomRestsWrong++;
                    run.Note($"at rest on slot {slot} of the zoom: the engine did not come to rest, or there was no picture to read");
                    continue;
                }

                var shown = FrameCode.Decode(picture.Bgra, picture.Width, picture.Height, session.Folder.Screen, map);
                var fit = StripEdges.Measure(picture.Bgra, picture.Width, picture.Height, session.Folder.Screen, map, export);
                var before = layout.ScreenMap(slot - 1) is { } earlier ? StripEdges.Measure(picture.Bgra, picture.Width, picture.Height, session.Folder.Screen, earlier, export) : default;
                if (shown != export || !fit.Within(MovingLayout.Tolerance))
                {
                    run.ZoomRestsWrong++;
                    var read = layout.Read(picture, slot);
                    run.Note($"at rest on slot {slot} of the zoom: the scene showed {truth.Name(read.Screen)} with {(read.Slot >= 0 ? $"the layout of slot {read.Slot}" : "a layout that is none of the slots' around it")}; the export has {truth.Name(export)} there, and an edge of its strip was {fit} from where slot {slot} puts it{Dump(session, "recordings-zoom-rest")}");
                }
                else if (before.Within(MovingLayout.Tolerance))
                {
                    // The layout of the slot before fits as well: the scene cannot say which of the two it has.
                    run.ZoomRestsCannotTell++;
                }
            }

            if (session.Events.FailedEvents > 0)
            {
                run.Failures += session.Events.FailedEvents;
                run.Note("the preview reported a failure during the zoom: " + string.Join("; ", session.Events.Failures()));
            }
        }
        finally
        {
            holdUps.Rest();
            Close(session);
        }
    }
}
