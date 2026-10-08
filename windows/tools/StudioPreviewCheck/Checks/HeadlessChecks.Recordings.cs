using System.Diagnostics;
using System.Globalization;
using TinyClips.Core.Studio.Preview;
using TinyClips.Tools.StudioPreviewCheck.Media;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

// "recordings" looks at clips that have a recording's frame times (RecordedMedia): frames some
// milliseconds into their slot, slots without a frame, a camera's own times. Nothing is judged.
//
//   --scenario files     what such a file is once the app's writer has written it: what its
//                        index says, what Media Foundation reads from it, what the app's probe
//                        makes of it, and whether each frame has the time it was written with
//   --scenario players   what a player makes of it, through the engine: which frame it shows
//                        at rest in the first slots and around a slot without a frame, and what
//                        position it reports with each frame it hands over while playing
//   --scenario table     the same clips in numbers, one line a clip and a kind of hold-up
//                        (HeadlessChecks.RecordingsTable.cs)
//   --clip a,b           only these clips (default: all of them)
//
// With --frame-times file the engine reads each clip's frame times from its file and counts in
// frames of the file; "files" says for every clip whether that reading gives the times Media
// Foundation's own reader hands out, which is what it rests on.
internal sealed partial class HeadlessChecks
{
    private void InvestigateRecordings()
    {
        var names = _options.Names("clip");
        var scenario = _options.Text("scenario", "files").ToLowerInvariant();

        // The clips whose frames are stored out of the order they are shown in are looked at as
        // files, and by a player only when they are named: nothing else here is made for them.
        IReadOnlyList<RecordedPlan> plans = names.Length > 0 ? names.Select(RecordedMedia.Plan).ToList()
            : scenario == "files" ? RecordedMedia.Plans
            : RecordedMedia.Plans.Where(plan => !plan.IsReordered).ToList();
        switch (scenario)
        {
            case "files":
                RecordedFiles(plans);
                if (names.Length == 0)
                {
                    UsualFiles();
                }

                break;
            case "players":
                RecordedPlayers(plans);
                break;
            case "table":
                RecordedTable(plans);
                break;
            default:
                throw new ArgumentException("--investigate recordings takes --scenario files, players or table.");
        }
    }

    // ---------------------------------------------------------------------------------------
    // What the file is
    // ---------------------------------------------------------------------------------------

    private void RecordedFiles(IReadOnlyList<RecordedPlan> plans)
    {
        _report.Section("Clips with a recording's frame times: what each file is once the app's writer has written it");
        var ffprobe = true;
        foreach (var plan in plans)
        {
            _report.Line();
            _report.Line($"{plan.Name}: {plan.What}");
            RecordedClip clip;
            try
            {
                clip = RecordedMedia.Ensure(_media, plan, _report);
            }
            catch (Exception ex)
            {
                _report.Line($"  could not be made: {Describe(ex)}");
                continue;
            }

            var path = TestMedia.PathOf(_media, clip.Spec);
            var asked = plan.Asked;
            _report.Line($"  written with: {asked.Length} frames, the first at {Ms(asked[0])} ms and the last at {Ms(asked[^1])} ms{(plan.IsCamera ? " of the camera's own time" : string.Empty)}; two frames are {Spacing(asked)} apart");

            // What the file's own index says, before anything has read it as a video.
            long[]? indexed = null;
            try
            {
                var index = Mp4Index.Read(path);
                foreach (var line in index.Describe())
                {
                    _report.Line($"  index: {line}");
                }

                if (index.Video is { } video)
                {
                    indexed = video.Times(index.Timescale);
                    if (plan.IsReordered)
                    {
                        // Held against the times asked for in the order the frames are shown in, which is the order asked for.
                        _report.Line($"  index, the video's frames in the order stored: the first three at {string.Join(", ", indexed.Take(3).Select(Ms))} ms; the times {(IsAscending(indexed) ? "only go up: the frames are NOT stored out of order" : "do not only go up: the frames are stored out of order")}");
                        indexed = [.. indexed.Order()];
                    }

                    _report.Line($"  index, the video's frames{(plan.IsReordered ? " in the order shown" : string.Empty)}: {Compare(asked, indexed)}");
                    var shift = video.EditShift(index.Timescale);
                    if (shift != 0)
                    {
                        _report.Line($"  index: the edit list moves the video by {Ms(shift)} ms on the movie's timeline; without it the first frame would be at {Ms(indexed[0] - shift)} ms");
                    }
                }

                if (index.Audio is { } audio)
                {
                    var sound = audio.Times(index.Timescale);
                    _report.Line($"  index, the sound: {sound.Length} pieces, the first at {(sound.Length == 0 ? "-" : Ms(sound[0]))} ms of the movie");
                }
            }
            catch (Exception ex)
            {
                _report.Line($"  index: could not be read: {Describe(ex)}");
            }

            // What Media Foundation hands out: the times a player and the exporter go by.
            long[]? handedOut = null;
            try
            {
                var (times, durations) = RecordedMedia.ReadVideoSampleTimes(path);
                handedOut = times;
                long[] shown = plan.IsReordered ? [.. times.Order()] : times;
                if (plan.IsReordered)
                {
                    _report.Line($"  Media Foundation's reader, the video's frames in the order it hands them out: the first three at {string.Join(", ", times.Take(3).Select(Ms))} ms");
                }

                _report.Line($"  Media Foundation's reader, the video's frames{(plan.IsReordered ? " in the order shown" : string.Empty)}: {Compare(asked, shown)}{(indexed is null ? string.Empty : "; against the index: " + Compare(indexed, shown))}");
                var lengths = durations.Distinct().OrderBy(d => d).ToList();
                var sound = RecordedMedia.ReadFirstAudioSampleTime(path);
                _report.Line($"  Media Foundation's reader, the sound: {(sound is { } first ? $"the first piece at {Ms(first)} ms" : "none")}");
                _report.Line($"  Media Foundation's reader, how long a frame says it lasts: {(lengths.Count <= 4 ? string.Join(", ", lengths.Select(Ms)) : $"{Ms(lengths[0])} to {Ms(lengths[^1])}, {lengths.Count} different lengths")} ms; in the order stored the times {(IsAscending(times) ? "only go up" : "do not only go up")}");
            }
            catch (Exception ex)
            {
                _report.Line($"  Media Foundation's reader: could not read the file: {Describe(ex)}");
            }

            _report.Line($"  {EnginesReading(path, handedOut)}");
            var slots = clip.Spec.FrameCount;
            var empty = Enumerable.Range(1, Math.Max(0, slots - 1)).Count(slot => clip.FrameOfSlot(slot, plan.Fps) == clip.FrameOfSlot(slot - 1, plan.Fps));
            var never = Enumerable.Range(0, clip.Frames).Count(frame => clip.SlotsOf(frame, plan.Fps, slots) is null);
            _report.Line($"  the app's probe: {F(clip.Duration, "0.0000")} s and {F(clip.ProbedFrameRate, "0.###")} frames a second{(plan.IsCamera ? $"; the camera's recorder says its first frame is {F(clip.StartOffset * 1000)} ms into the recording" : string.Empty)}. At {plan.Fps} frames a second that is {slots} slots: {empty} of them show the frame the slot before shows, and {never} of the {clip.Frames} frames are in no slot (by the rule of the export: a slot shows the last frame that began at or before its middle)");

            if (ffprobe)
            {
                try
                {
                    var c = CultureInfo.InvariantCulture;
                    var stream = TestMedia.Run("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=start_time,time_base,r_frame_rate,avg_frame_rate,nb_frames", "-of", "default=nw=1", path]);
                    var packets = TestMedia.Run("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", path])
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(line => line.TrimEnd(','))
                        .Where(line => double.TryParse(line, NumberStyles.Float, c, out _))
                        .Select(line => (long)Math.Round(double.Parse(line, c) * 10_000_000))
                        .ToArray();
                    _report.Line($"  ffprobe, the video's packets: {Compare(asked, packets)}; the stream: {string.Join(", ", stream.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))}");
                }
                catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception)
                {
                    ffprobe = false;
                    _report.Line("  ffprobe is not on PATH: the file was not read a third way");
                }
                catch (Exception ex)
                {
                    _report.Line($"  ffprobe: {Shorten(ex.Message)}");
                }
            }
        }
    }

    /// <summary>
    /// What the engine's own reading of a file's index gives (<c>StudioPreviewFrameTimes</c>,
    /// which is what it counts by with <c>--frame-times file</c>), against the times Media
    /// Foundation's reader hands out for the same file: the times a player goes by. They have
    /// to be the same for the engine to count a clip in frames of its file.
    /// </summary>
    private static string EnginesReading(string path, long[]? handedOut)
    {
        var own = StudioPreviewFrameTimes.TryRead(path, out var problem);
        if (own is null)
        {
            return $"the engine's reading of the index: none, because {problem}: it plays such a clip on the grid";
        }

        var starts = Enumerable.Range(0, own.Count).Select(frame => own.Start(frame)).ToArray();
        var text = $"the engine's reading of the index: {own.Count} frames, the first at {Ms(starts[0])} ms, usually {Ms(own.TypicalSpacing)} ms apart, the last one ending at {Ms(own.End)} ms";
        if (handedOut is null)
        {
            return text + "; not held against Media Foundation's reader, which did not read the file";
        }

        // Media Foundation's reader hands the frames out as they are stored; the engine has them as they are shown.
        var shown = handedOut.OrderBy(time => time).ToArray();
        if (shown.Length != starts.Length)
        {
            return $"{text}; Media Foundation's reader hands out {shown.Length} frames: NOT THE SAME";
        }

        long worst = 0;
        var at = 0;
        for (var index = 0; index < starts.Length; index++)
        {
            if (Math.Abs(starts[index] - shown[index]) > Math.Abs(worst))
            {
                worst = starts[index] - shown[index];
                at = index;
            }
        }

        if (worst == 0)
        {
            return $"{text}; the same as Media Foundation's reader hands out, to the last 100 ns";
        }

        // The engine allows nothing at a frame's edge where it counts by these times: a frame has
        // begun when the time has reached its start, as in the export. So a difference of one
        // unit is a difference, and how many frames have it, and which way, is what to know: a
        // time that is 0.67 of a unit past a whole one is rounded up here, and cut off by a reader that cuts.
        var differing = Enumerable.Range(0, starts.Length).Where(index => starts[index] != shown[index]).ToArray();
        var least = differing.Min(index => starts[index] - shown[index]);
        var most = differing.Max(index => starts[index] - shown[index]);
        return Math.Abs(worst) <= 100
            ? $"{text}; NOT the same to the last 100 ns: {differing.Length} of the {starts.Length} frames are {least} to {most} units of 100 ns from where Media Foundation's reader has them (here less there; the first of them frame {differing[0]}, at {starts[differing[0]]} here and {shown[differing[0]]} there)"
            : $"{text}; NOT what Media Foundation's reader hands out: frame {at} is at {Ms(starts[at])} ms here and at {Ms(shown[at])} ms there, and the first frame at {Ms(starts[0])} ms here and {Ms(shown[0])} ms there";
    }

    /// <summary>
    /// The same question for the clips every other check plays, which ffmpeg made: with
    /// <c>--frame-times file</c> the engine reads their index too, and has to find each frame
    /// at the start of its slot there.
    /// </summary>
    private void UsualFiles()
    {
        _report.Section("The usual clips: what the engine's reading of their index gives");
        foreach (var clip in TestMedia.All.Append(TestMedia.Screen60))
        {
            var path = TestMedia.PathOf(_media, clip);
            if (!File.Exists(path))
            {
                _report.Line($"{clip.FileName}: not made");
                continue;
            }

            _report.Line($"{clip.FileName}:");
            try
            {
                foreach (var line in Mp4Index.Read(path).Describe())
                {
                    _report.Line($"  index: {line}");
                }
            }
            catch (Exception ex)
            {
                _report.Line($"  index: could not be read: {Describe(ex)}");
            }

            long[]? handedOut = null;
            try
            {
                handedOut = RecordedMedia.ReadVideoSampleTimes(path).Times;
            }
            catch (Exception ex)
            {
                _report.Line($"  Media Foundation's reader: could not read the file: {Describe(ex)}");
            }

            _report.Line($"  {EnginesReading(path, handedOut)}");
            var own = StudioPreviewFrameTimes.TryRead(path, out _);
            if (own is not null)
            {
                // On the grid: frame N at N frames of the clip's rate, to within a tick of rounding.
                long off = 0;
                for (var frame = 0; frame < own.Count; frame++)
                {
                    off = Math.Max(off, Math.Abs(own.Start(frame) - (long)Math.Round(frame * 10_000_000.0 / clip.Fps, MidpointRounding.AwayFromZero)));
                }

                _report.Line($"  against the grid of {clip.Fps} frames a second: {own.Count} frames where the clip has {clip.FrameCount}; the frame furthest from the start of its slot is {Ms(off)} ms from it");
            }
        }
    }

    /// <summary>How a file's frame times stand against the times the frames were written with.</summary>
    private static string Compare(long[] asked, long[] got)
    {
        if (got.Length == 0)
        {
            return "no frames";
        }

        var text = $"{got.Length} frames, the first at {Ms(got[0])} ms";
        if (got.Length != asked.Length)
        {
            return $"{text}: not the {asked.Length} that were written";
        }

        // Each frame against what it was written with, and against that less what the first one differs by.
        var first = got[0] - asked[0];
        long worst = 0;
        long worstMoved = 0;
        var at = 0;
        for (var index = 0; index < got.Length; index++)
        {
            var difference = got[index] - asked[index];
            if (Math.Abs(difference) > Math.Abs(worst))
            {
                worst = difference;
                at = index;
            }

            worstMoved = Math.Max(worstMoved, Math.Abs(difference - first));
        }

        if (worst == 0)
        {
            return $"{text}; every frame has the time it was written with, to the last 100 ns";
        }

        return first != 0 && worstMoved < Math.Abs(first)
            ? $"{text}, where {Ms(asked[0])} ms was written: every frame is {Ms(Math.Abs(first))} ms {(first < 0 ? "earlier" : "later")} than it was written with, to within {Ms(worstMoved)} ms"
            : $"{text}; the frames are within {Ms(Math.Abs(worst))} ms of the times they were written with (the most at frame {at}: {Ms(got[at])} ms for {Ms(asked[at])} ms)";
    }

    private static string Spacing(long[] times)
    {
        if (times.Length < 2)
        {
            return "-";
        }

        var shortest = long.MaxValue;
        var longest = long.MinValue;
        for (var index = 1; index < times.Length; index++)
        {
            shortest = Math.Min(shortest, times[index] - times[index - 1]);
            longest = Math.Max(longest, times[index] - times[index - 1]);
        }

        return $"{Ms(shortest)} to {Ms(longest)} ms";
    }

    private static bool IsAscending(long[] times)
    {
        for (var index = 1; index < times.Length; index++)
        {
            if (times[index] <= times[index - 1])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A time in 100 ns units as milliseconds.</summary>
    private static string Ms(long ticks) => (ticks / 10000.0).ToString("0.0###", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------------------------------
    // What a player makes of it
    // ---------------------------------------------------------------------------------------

    private void RecordedPlayers(IReadOnlyList<RecordedPlan> plans)
    {
        _report.Section("Clips with a recording's frame times: what a player shows at rest and reports while playing, through the engine");
        _report.Line("  A frame's number here is its place in the file, counted from 0, read from the picture. The export puts a frame of the file in a slot when it is the last that began at or before the middle of the slot; that is what 'the export has' means below.");
        foreach (var plan in plans)
        {
            _report.Line();
            _report.Line($"{plan.Name}: {plan.What}");
            RecordedClip clip;
            try
            {
                clip = RecordedMedia.Ensure(_media, plan, _report);
            }
            catch (Exception ex)
            {
                _report.Line($"  could not be made: {Describe(ex)}");
                continue;
            }

            Session session;
            try
            {
                // A camera clip goes with the usual screen clip, whose frames are on the grid.
                session = plan.IsCamera
                    ? OpenSession(clip.Spec, cameraOffset: clip.StartOffset)
                    : OpenSession(camera: null, screen: clip.Spec);
            }
            catch (Exception ex)
            {
                _report.Line($"  the preview did not open: {Describe(ex)}{DumpFailedOpen(ex, "recordings")}");
                continue;
            }

            try
            {
                RecordedPlayer(session, clip);
            }
            catch (Exception ex)
            {
                _report.Line($"  stopped: {Describe(ex)}{Dump(session, "recordings")}");
            }
            finally
            {
                Close(session);
            }
        }
    }

    private void RecordedPlayer(Session session, RecordedClip clip)
    {
        var plan = clip.Plan;
        var track = plan.IsCamera ? 1 : 0;

        // The timeline is the screen's: the recorded clip's own, or the usual screen clip's for a camera.
        var fps = session.Folder.Screen.Fps;
        var slots = session.Folder.FrameCount;
        using var probe = new TruthProbe(session.Engine, session.Folder.Screen, session.Folder.Camera, capacity: 800);
        int Slot() => (int)Math.Round(session.Engine.Position * fps);
        int Showing() => plan.IsCamera ? session.ReadShown().Camera : session.ReadShown().Screen;
        string Frame(int frame) => frame == FrameCode.Unreadable ? "nothing" : frame < 0 || frame >= clip.Frames ? $"frame {frame}" : $"frame {frame} ({Ms(clip.Times[frame])} ms)";
        bool Visible(int slot) => !plan.IsCamera || session.Folder.Project.Sources.Camera is not { } camera || ((slot + 0.5) / fps) - camera.StartOffset is var own && own >= 0 && own <= camera.Duration;
        string Export(int slot) => Visible(slot) ? Frame(clip.FrameOfSlot(slot, fps)) : "no camera";

        var state = session.Engine.GetDiagnostics();
        var truth = new RecordedTruth(clip, session);
        var countsFileFrames = state.CountsFileFrames.Length > track && state.CountsFileFrames[track];
        _report.Line($"  frame numbers: {string.Join("; ", state.FrameTimes.Select((note, index) => $"the {(index == 0 ? "screen" : "camera")} by {note}"))}");
        _report.Line($"  the engine: {F(session.Engine.Duration, "0.0000")} s at {F(session.Engine.FrameRate, "0.###")} frames a second, {slots} slots{(plan.IsCamera ? $"; it takes the camera for {F(clip.ProbedFrameRate, "0.###")} frames a second, which is what the probe reads from the file, and its first frame for {F(clip.StartOffset * 1000)} ms into the recording" : string.Empty)}; the file has {clip.Frames} frames from {Ms(clip.Times[0])} to {Ms(clip.Times[^1])} ms");
        _report.Line($"  opened: Position is slot {Slot()}; the picture shows {Frame(Showing())}; the export has {Export(0)} in slot 0; opened at attempt {state.OpenAttempts}");

        // At rest: the first slots, the slots around the first one that shows what the slot before shows, the last ones.
        var wanted = new SortedSet<int>(Enumerable.Range(0, Math.Min(6, slots)));
        var repeat = Enumerable.Range(6, Math.Max(0, slots - 12)).FirstOrDefault(slot => Visible(slot) && Visible(slot - 1) && clip.FrameOfSlot(slot, fps) == clip.FrameOfSlot(slot - 1, fps), -1);
        if (repeat >= 0)
        {
            for (var slot = repeat - 2; slot <= repeat + 3; slot++)
            {
                wanted.Add(slot);
            }
        }

        if (plan.IsCamera)
        {
            // Where the camera comes in.
            var first = (int)Math.Ceiling((clip.StartOffset * fps) - 0.5);
            for (var slot = Math.Max(0, first - 1); slot <= first + 3; slot++)
            {
                wanted.Add(slot);
            }
        }

        for (var slot = Math.Max(0, slots - 3); slot < slots; slot++)
        {
            wanted.Add(slot);
        }

        var notTheExports = 0;
        foreach (var slot in wanted)
        {
            probe.Read();
            var asked = Stopwatch.GetTimestamp();
            session.Engine.Seek((slot + 0.5) / fps);
            var idle = session.WaitForIdle();
            var took = Stopwatch.GetElapsedTime(asked).TotalMilliseconds;
            var answers = probe.Read().Where(f => f.Clip == track && f.Kind != StudioPreviewHandOverKind.Playback).ToList();
            var shown = Showing();
            var export = Visible(slot) ? clip.FrameOfSlot(slot, fps) : FrameCode.Unreadable;
            notTheExports += shown == export ? 0 : 1;
            _report.Line($"  at rest in slot {slot} (the clock at {F((slot + 0.5) * 1000.0 / fps, "0.0#")} ms): the export has {Export(slot)}; the preview shows {Frame(shown)}{(shown == export ? string.Empty : " <- not the export's")}; Position is slot {Slot()}{(idle ? string.Empty : ", and the engine did not come to rest")}; {F(took, "0")} ms; the player handed over {(answers.Count == 0 ? "nothing" : string.Join(", ", answers.Select(f => $"{Frame(f.Truth)} at position {Ms(f.PositionTicks)} ms")))}");
        }

        _report.Line($"  at rest: {wanted.Count - notTheExports} of {wanted.Count} slots showed the frame the export has there");

        // One frame forward at a time, across the first slot that shows what the slot before shows.
        var from = repeat >= 0 ? Math.Max(0, repeat - 3) : 10;
        var before = session.Engine.GetDiagnostics();
        session.Engine.Seek((from + 0.5) / fps);
        session.WaitForIdle();
        var stepsWrong = 0;
        var steps = new List<string>();
        for (var slot = from + 1; slot <= Math.Min(slots - 1, from + 7); slot++)
        {
            session.Recorder.Drain();
            var asked = Stopwatch.GetTimestamp();
            session.Engine.Seek((slot + 0.5) / fps);
            session.WaitForIdle();
            var drawn = session.Recorder.Drain();
            var shown = Showing();
            var export = Visible(slot) ? clip.FrameOfSlot(slot, fps) : FrameCode.Unreadable;
            stepsWrong += shown == export ? 0 : 1;
            steps.Add($"to slot {slot}: {Frame(shown)}{(shown == export ? string.Empty : $" <- the export has {Export(slot)}")}, {(drawn.Count == 0 ? "no scene drawn" : $"first scene after {F(Stopwatch.GetElapsedTime(asked, drawn[0].At).TotalMilliseconds, "0")} ms")}");
        }

        var after = session.Engine.GetDiagnostics();
        _report.Line($"  one frame forward at a time from slot {from}: {string.Join("; ", steps)}");
        _report.Line($"  those {steps.Count} moves: {after.StepsIssued - before.StepsIssued} were made by stepping the player, {after.StepFallbacks - before.StepFallbacks} of them fell back on a seek, {after.SeeksIssued - before.SeeksIssued} seeks, {after.Repairs - before.Repairs} frames had to be fetched by a detour; {stepsWrong} ended on another frame than the export's");

        // Playing from the first slot: what the player says with each frame it hands over.
        session.Engine.Seek(0.5 / fps);
        session.WaitForIdle();
        probe.Read();
        session.Recorder.Drain();
        // Long enough to come past the first slots without a frame, and not into the end.
        var playFor = Math.Min(4.5, Math.Max(1, session.Engine.Duration - 0.5));
        var started = Stopwatch.GetTimestamp();
        session.Engine.Play();
        Thread.Sleep(TimeSpan.FromSeconds(playFor));
        var pausing = Stopwatch.GetTimestamp();
        session.Engine.Pause();
        var slotAtReturn = Slot();
        var shownAtReturn = Showing();
        session.WaitForIdle();
        var lastPlayed = session.Recorder.Drain().Where(c => c.At < pausing).Select(c => plan.IsCamera ? c.Camera : c.Screen).LastOrDefault(frame => frame != FrameCode.Unreadable, FrameCode.Unreadable);
        var frames = probe.Read().Where(f => f.Clip == track && f.Kind == StudioPreviewHandOverKind.Playback && f.Truth != FrameCode.Unreadable).OrderBy(f => f.Serial).ToList();
        var numberedLate = new Dictionary<long, (long Frame, long Slot)>();
        foreach (var frame in frames)
        {
            if (frame.EarlierSerial != 0)
            {
                numberedLate[frame.EarlierSerial] = (frame.EarlierFrame, frame.EarlierTimelineFrame);
            }
        }

        var atOnce = 0;
        var late = 0;
        var never = 0;
        var unsure = 0;
        var wrong = 0;
        var without = 0;
        var longestWithout = 0;
        var lines = new List<string>();
        var odd = new List<string>();
        var afterTheirTime = new Samples();
        var into = new Samples();

        // Whether a player ever hands a frame over with a position before the frame's own time,
        // and how near to it at the nearest: where the engine counts by a file's frame times it
        // allows nothing at a frame's edge, so a position one unit early would name the frame before.
        var aheadOfTheirTime = 0;
        long mostAhead = 0;
        var nearestAfter = long.MaxValue;
        foreach (var frame in frames)
        {
            // The number the frame was given, at once or by the frame after it, and the slot of
            // the timeline a screen frame of that number is shown under.
            var hasLater = numberedLate.TryGetValue(frame.Serial, out var later);
            long? number = frame.Knowledge != StudioPreviewFrameKnowledge.Unknown ? frame.Frame : hasLater ? later.Frame : null;
            var shownUnder = frame.Knowledge != StudioPreviewFrameKnowledge.Unknown ? frame.TimelineFrame : hasLater ? later.Slot : -1;
            var how = frame.Knowledge switch
            {
                StudioPreviewFrameKnowledge.Known => $"numbered {frame.Frame} ({frame.Rule})",
                StudioPreviewFrameKnowledge.Unsure => $"shown as {frame.Frame}, unsure",
                _ when number is not null => $"numbered {number} by the frame after it",
                _ => $"no number ({frame.Rule})",
            };
            if (!plan.IsCamera && number is not null && countsFileFrames)
            {
                how += $", shown under slot {shownUnder}";
            }
            atOnce += frame.Knowledge == StudioPreviewFrameKnowledge.Known ? 1 : 0;
            unsure += frame.Knowledge == StudioPreviewFrameKnowledge.Unsure ? 1 : 0;
            late += frame.Knowledge == StudioPreviewFrameKnowledge.Unknown && number is not null ? 1 : 0;
            never += frame.Knowledge == StudioPreviewFrameKnowledge.Unknown && number is null ? 1 : 0;
            without = frame.Knowledge == StudioPreviewFrameKnowledge.Unknown && number is null ? without + 1 : 0;
            longestWithout = Math.Max(longestWithout, without);

            // On the grid the engine numbers a clip's frames by the clip's own slots: the
            // screen's are the timeline's, the camera's those of the rate the probe read, which
            // are no frames of anything. Counting in frames of the file, a number is the frame's
            // place in the file. Either way a screen frame is shown under a slot of the
            // timeline, and that slot has to be one the export has the frame in.
            var right = number is null
                || (plan.IsCamera
                    ? !countsFileFrames || number == frame.Truth
                    : truth.IsRightUnder(frame.Truth, shownUnder) && (!countsFileFrames || number == frame.Truth));
            wrong += right ? 0 : 1;
            if (frame.Truth >= 0 && frame.Truth < clip.Frames)
            {
                var afterItsTime = frame.PositionTicks - clip.Times[frame.Truth];
                afterTheirTime.Add(afterItsTime / 10000.0);
                if (afterItsTime < 0)
                {
                    aheadOfTheirTime++;
                    mostAhead = Math.Max(mostAhead, -afterItsTime);
                }
                else
                {
                    nearestAfter = Math.Min(nearestAfter, afterItsTime);
                }
            }

            into.Add(frame.IntoMilliseconds);
            var text = $"    {F(Stopwatch.GetElapsedTime(started, frame.StartedAt).TotalMilliseconds),7} ms after Play(): position {Ms(frame.PositionTicks)} ms, which is {F(frame.IntoMilliseconds)} ms into {(countsFileFrames ? "frame" : plan.IsCamera ? "the camera's own slot" : "slot")} {frame.NameByPosition}; the picture is {Frame(frame.Truth)}{(plan.IsCamera ? string.Empty : $" ({truth.Where(frame.Truth)})")}; the engine: {how}{(right ? string.Empty : " <- not right")}";
            if (lines.Count < 14)
            {
                lines.Add(text);
            }
            else if (odd.Count < 12 && (!right || frame.Knowledge != StudioPreviewFrameKnowledge.Known || frame.Rule != "next"))
            {
                // Later on: the frames that were not simply the next one, numbered at once and rightly.
                odd.Add(text);
            }
        }

        _report.Line($"  playing from slot 0 for {F(playFor)} s, the first {lines.Count} of the {frames.Count} frames the player handed over:");
        foreach (var line in lines)
        {
            _report.Line(line);
        }

        if (odd.Count > 0)
        {
            _report.Line($"  and the first {odd.Count} after those that were not numbered at once as the next frame, or not rightly:");
            foreach (var line in odd)
            {
                _report.Line(line);
            }
        }

        var stateAfter = session.Engine.GetDiagnostics();
        _report.Line($"  of those {frames.Count} frames: {atOnce} numbered at once, {late} by the frame after them, {never} never ({longestWithout} in a row at most), {unsure} shown under the number of their position and called unsure{(plan.IsCamera ? (countsFileFrames ? $"; {wrong} had a number that is not their place in the file" : string.Empty) : $"; {wrong} were shown under a slot the export does not have them in{(countsFileFrames ? ", or had a number that is not their place in the file" : string.Empty)}")}. The position that came with a frame was {afterTheirTime.Summary()} after the frame's own time, and {into.Summary()} into its slot");
        _report.Line($"  in units of 100 ns: {aheadOfTheirTime} of those positions were before the frame's own time as Media Foundation's reader has it{(aheadOfTheirTime > 0 ? $" (by {mostAhead} units at most)" : string.Empty)}, and the nearest after it was {(nearestAfter == long.MaxValue ? "none" : $"{nearestAfter} units")} after");
        var restSlot = Slot();
        var rest = Showing();
        var restExport = Visible(restSlot) ? clip.FrameOfSlot(restSlot, fps) : FrameCode.Unreadable;
        _report.Line($"  the last scene drawn while playing showed {Frame(lastPlayed)}; Pause() returned with Position on slot {slotAtReturn} and the picture on {Frame(shownAtReturn)}; at rest: slot {restSlot}, {Frame(rest)}; the export has {Export(restSlot)} there{(rest == restExport ? string.Empty : " <- not the export's")}{(rest != shownAtReturn ? "; the picture changed after Pause() had returned" : string.Empty)}; rests fetched anew {stateAfter.RestsFetchedAnew - after.RestsFetchedAnew}");
    }
}
