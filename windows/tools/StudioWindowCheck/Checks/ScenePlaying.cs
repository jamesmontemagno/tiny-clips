using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Dispatching;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Capture;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 12, continued. A scene that the playhead comes into while the preview plays: what the preview
// draws on the way, what the window shows of the new scene, and what following it costs. And the
// handle on the camera in the preview, which is the current scene's.
internal sealed partial class WindowChecks
{
    /// <summary>
    /// How long the UI thread takes to answer, sampled every millisecond from another thread.
    /// What keeps it busy for a while, such as the layout of an inspector whose controls have
    /// changed, shows as an answer that comes late. So does a run of the garbage collector,
    /// which stops every thread; how long it did between a question and its answer is noted
    /// with the answer and taken off, because it is this tool's heap that makes it long.
    /// </summary>
    private sealed class UiThreadProbe : IDisposable
    {
        private readonly DispatcherQueue _dispatcher;
        private readonly long[] _sent = new long[1 << 15];
        private readonly long[] _pausedWhenSent = new long[1 << 15];
        private readonly (long Sent, long Answered, long Paused)[] _answers = new (long, long, long)[1 << 15];
        private readonly Thread _thread;
        private readonly DispatcherQueueHandler _answer;
        private volatile bool _stopped;
        private int _asked;
        private int _answered;

        public UiThreadProbe(DispatcherQueue dispatcher)
        {
            _dispatcher = dispatcher;
            _answer = Answer;
            _thread = new Thread(Ask) { IsBackground = true, Name = "StudioWindowCheck.UiProbe" };
            _thread.Start();
        }

        /// <summary>
        /// The longest the UI thread took to answer a question asked between two instants, in
        /// milliseconds, not counting the time the garbage collector held it; and the longest
        /// the garbage collector held it between one of those questions and its answer.
        /// </summary>
        public (double Busy, double Collecting) LongestBetween(long from, long to)
        {
            double longest = 0;
            double collecting = 0;
            var count = Volatile.Read(ref _answered);
            for (var index = 0; index < count; index++)
            {
                var (sent, answered, paused) = _answers[index];
                if (answered >= from && sent <= to)
                {
                    var held = TimeSpan.FromTicks(paused).TotalMilliseconds;
                    longest = Math.Max(longest, Stopwatch.GetElapsedTime(sent, answered).TotalMilliseconds - held);
                    collecting = Math.Max(collecting, held);
                }
            }

            return (longest, collecting);
        }

        public void Dispose()
        {
            _stopped = true;
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        // One question a millisecond. The answers come back in the order the questions were asked.
        private void Ask()
        {
            while (!_stopped && _asked < _sent.Length)
            {
                var now = Stopwatch.GetTimestamp();
                _pausedWhenSent[_asked] = GC.GetTotalPauseDuration().Ticks;
                _sent[_asked++] = now;
                if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.High, _answer))
                {
                    return;
                }

                var next = now + (Stopwatch.Frequency / 1000);
                while (!_stopped && Stopwatch.GetTimestamp() < next)
                {
                    Thread.SpinWait(40);
                }
            }
        }

        private void Answer()
        {
            var index = _answered;
            if (index < _answers.Length)
            {
                _answers[index] = (_sent[index], Stopwatch.GetTimestamp(), GC.GetTotalPauseDuration().Ticks - _pausedWhenSent[index]);
                Volatile.Write(ref _answered, index + 1);
            }
        }
    }

    /// <summary>
    /// How many frames of a range were never drawn, how many came more than a frame's time late,
    /// and the longest wait between two; and which frames were never drawn, as words to put
    /// after their number, empty when there are none.
    /// </summary>
    private static (int Dropped, int Late, double LongestMs, string Which) PaceOf(IEnumerable<SceneReading> scenes, int first, int last)
    {
        var firstDrawn = new SortedDictionary<int, long>();
        foreach (var scene in scenes.Where(scene => scene.Frame >= first - 1 && scene.Frame <= last))
        {
            firstDrawn.TryAdd(scene.Frame, scene.At);
        }

        var missing = Enumerable.Range(first, last - first + 1).Where(frame => !firstDrawn.ContainsKey(frame)).ToArray();
        var late = 0;
        double longest = 0;
        KeyValuePair<int, long>? previous = null;
        foreach (var drawn in firstDrawn)
        {
            if (previous is { } earlier && drawn.Key >= first)
            {
                // More than a frame's time later than the frames between it and the one before it account for.
                var took = Stopwatch.GetElapsedTime(earlier.Value, drawn.Value).TotalMilliseconds;
                var due = (drawn.Key - earlier.Key) * 1000.0 / Fps;
                late += took - due > 1000.0 / Fps ? 1 : 0;
                longest = Math.Max(longest, took);
            }

            previous = drawn;
        }

        return (missing.Length, late, longest, missing.Length == 0 ? string.Empty : $" (frame{(missing.Length == 1 ? string.Empty : "s")} {string.Join(", ", missing)})");
    }

    /// <summary>
    /// Reads one picture the preview drew of a project with scenes: which frame it shows, from
    /// the screen's frame strip, looked for where each of the frames around the last one has the
    /// screen; and then both layers, against the layout of that frame.
    /// </summary>
    /// <param name="edgesOff">
    /// Takes the reading of every picture, by its frame, that is right but for one or two edges
    /// that are further from their places than they may be. Such a picture is not reported as
    /// wrong here: see <see cref="LookAgainPaused"/>.
    /// </param>
    /// <param name="ahead">How many frames after the last one are tried: more than a cut the preview plays over leaves out.</param>
    /// <param name="cameraNextTo">
    /// Takes every picture in which the camera shows the frame before or after the one that
    /// goes with the screen's frame: the screen's frame, and by how many frames the camera's is
    /// later. The preview has a player for each recording, and while it plays the two do not
    /// hand their frames over at the same moment, so a picture can be drawn with the new frame
    /// of the one and still the old frame of the other. Such a picture is read with the camera's
    /// frame as it is, and is not reported as wrong for it. Two frames apart is.
    /// </param>
    private static SceneReading ReadDrawnScene(Shot shot, TestFolder folder, StudioProject project, long at, ref int last, Dictionary<int, LayoutReading> edgesOff, int ahead = 40, List<(int Frame, int Later)>? cameraNextTo = null)
    {
        var canvas = new Box(0, 0, shot.Width, shot.Height);
        int StripFor(int candidate) =>
            StudioLayoutResolver.Resolve(project, MiddleOf(candidate), canvas.Width, canvas.Height).Screen is { } screen
                ? StripAt(shot, folder.Screen, Aligned(screen.Rect), screen.Source, mirror: false)
                : FrameCode.Unreadable;

        var frame = FrameCode.Unreadable;
        for (var candidate = Math.Max(0, last - 2); candidate <= last + ahead && frame == FrameCode.Unreadable; candidate++)
        {
            var read = StripFor(candidate);
            if (read != FrameCode.Unreadable && (read == candidate || StripFor(read) == read))
            {
                frame = read;
            }
        }

        if (frame == FrameCode.Unreadable)
        {
            return new SceneReading(at, frame, double.NaN, 0, "the screen's frame strip does not read where any of the frames around the last one has it");
        }

        last = frame;
        var reading = ReadLayout(shot, canvas, folder, project, frame);
        if (cameraNextTo is not null && reading.Camera is { StripIsInside: true, Strip: var cameraStrip } && cameraStrip != FrameCode.Unreadable && reading.CameraFrame != FrameCode.Unreadable && Math.Abs(cameraStrip - reading.CameraFrame) == 1)
        {
            cameraNextTo.Add((frame, cameraStrip - reading.CameraFrame));
            reading = ReadLayout(shot, canvas, folder, project, frame, cameraShows: cameraStrip);
        }

        var wrong = JudgeLayout(reading);
        if (wrong is not null && EdgesOff(reading) is { Count: > 0 and <= 2 })
        {
            edgesOff[frame] = reading;
            wrong = null;
        }

        if (wrong is null)
        {
            return new SceneReading(at, frame, reading.Worst, reading.Edges, null);
        }

        // A picture that is wrong: is it its frame, laid out as one of the frames around it is?
        (int Frame, double Worst)? fits = null;
        for (var other = Math.Max(0, frame - 8); other <= frame + 8; other++)
        {
            if (other != frame && ReadLayout(shot, canvas, folder, project, frame, laidOutAs: other) is var against && JudgeLayout(against) is null && (fits is not { } best || against.Worst < best.Worst))
            {
                fits = (other, against.Worst);
            }
        }

        var told = fits is { } found
            ? string.Create(CultureInfo.InvariantCulture, $"it is frame {frame} laid out as frame {found.Frame} is: read against that frame's layout every edge is within {found.Worst:0.00} px. Against its own: ")
            : "it is laid out as none of the eight frames before and after it is. Against its own layout: ";
        return new SceneReading(at, frame, reading.Worst, reading.Edges, $"frame {frame}: {told}{wrong}", fits);
    }

    /// <summary>
    /// The edges of a reading that are further from their places than they may be, when they are
    /// all that is wrong with it: without them the reading shows its layout. Null when more is
    /// wrong, and empty when nothing is.
    /// </summary>
    private static List<(bool IsCamera, EdgeReading Edge)>? EdgesOff(LayoutReading reading)
    {
        static PartReading? Without(PartReading? part) => part is null ? null : part with { Edges = [.. part.Edges.Where(edge => edge.Off <= part.Allowed)] };
        static IEnumerable<EdgeReading> Off(PartReading? part) => part is null ? [] : part.Edges.Where(edge => edge.Off > part.Allowed);
        List<(bool IsCamera, EdgeReading Edge)> off = [.. Off(reading.Screen).Select(edge => (false, edge)), .. Off(reading.Camera).Select(edge => (true, edge))];
        return JudgeLayout(reading with { Screen = Without(reading.Screen), Camera = Without(reading.Camera) }) is null ? off : null;
    }

    /// <summary>
    /// The test clips are ffmpeg's moving test picture with the landmarks drawn over it. At some
    /// frames what moves in it lies against a landmark, and that one edge then reads up to a
    /// pixel from its place, in a picture that is otherwise exact. That is the clip, and the
    /// same whenever that frame is shown. A layer that was drawn in the wrong place while the
    /// preview played has every edge off, and reads differently from the paused preview.
    /// So each frame of which a drawn picture had one or two edges off is looked at again here,
    /// with the preview paused on it: it was drawn right when the paused picture has the same
    /// edges just as far off, within a quarter of a pixel. Returns what is wrong with the
    /// pictures for which that is not so, and what was found for the others.
    /// </summary>
    private (List<string> Wrong, string Seen) LookAgainPaused(Editor editor, Dictionary<int, LayoutReading> edgesOff)
    {
        var wrong = new List<string>();
        var seen = new List<string>();
        foreach (var (frame, drawn) in edgesOff.OrderBy(entry => entry.Key).Take(12))
        {
            SetSlider(editor, "StudioPlayhead", MiddleOf(frame));
            bool Shows(LayoutSight? sight) => sight is not null && (sight.Reading.Screen is { } screen ? screen.Strip == frame : sight.Reading.Camera?.Strip == sight.Reading.CameraFrame);
            var paused = Until(() => LookAtLayout(editor, frame), Shows, 3, 30);
            Thread.Sleep(120);
            paused = LookAtLayout(editor, frame) ?? paused;
            var differences = new List<string>();
            foreach (var (isCamera, edge) in EdgesOff(drawn) ?? [])
            {
                var part = isCamera ? paused?.Reading.Camera : paused?.Reading.Screen;
                var same = part?.Edges.Where(found => found.Name == edge.Name).Select(found => (EdgeReading?)found).FirstOrDefault();
                var (played, rested) = (edge.Found - edge.Expected, same is { } there ? there.Found - there.Expected : double.NaN);
                var told = string.Create(CultureInfo.InvariantCulture, $"{edge.Name} of the {(isCamera ? "camera" : "screen")} {played:+0.00;-0.00} px while playing and {(double.IsNaN(rested) ? "not found" : rested.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + " px")} paused");
                if (double.IsNaN(rested) || Math.Abs(played - rested) > 0.25)
                {
                    differences.Add(told);
                }
                else
                {
                    seen.Add($"frame {frame}: {told}");
                }
            }

            if (paused is null || !Shows(paused) || differences.Count > 0)
            {
                wrong.Add($"frame {frame}: {(paused is null || !Shows(paused) ? "the paused preview could not be read at that frame" : "drawn differently from the paused preview: " + string.Join(", ", differences))}");
            }
        }

        if (edgesOff.Count > 12)
        {
            wrong.Add($"{edgesOff.Count} pictures had an edge off, which is more than the clip accounts for");
        }

        return (wrong, seen.Count == 0 ? string.Empty : $"at {edgesOff.Count} frame(s) an edge reads further off than it may, paused as well as playing, which is what moves in the test clip lying against it: {string.Join("; ", seen)}");
    }

    /// <summary>
    /// Looks until the preview rests on the frame a play-through starts from, in the layout of
    /// that frame, or five seconds are up. Returns the last look, and what is wrong with it, or
    /// null when nothing is.
    /// <para>
    /// At some frames what moves in the test clip lies against a landmark (see
    /// <see cref="LookAgainPaused"/>). Frame 90 of the screen recording is one: on a screen of
    /// 1019 × 573 the top of the lime patch reads 0.85 px from its place, paused and playing
    /// alike, which the play-through into a scene reported in every run while the window with
    /// a camera had a screen of that size. Since the speed lane made the preview smaller, the
    /// window without a camera has it, and playing over a cut starts at that frame: the check
    /// that the preview rests there failed in every run of the evening of 5 October 2026.
    /// </para>
    /// <para>
    /// Such a picture is at rest here, and a note says which edges were off and by how much.
    /// That was prepared while no check could be run. The runs of 7 October 2026 showed what is
    /// at the edge: the band of the test picture, on two of the three lines across the lime
    /// patch's top, as worked out. The reasoning for it: a play-through holds its own picture
    /// of such a frame against the paused one, so the paused one cannot be asked for more than
    /// the play-through's rule allows, and a layer that is in the wrong place has all its edges
    /// off, not one or two. With <c>--as-before</c> such a picture is not at rest, as it was not
    /// in the runs that failed: what is wrong then says that the picture is in its layout but
    /// for those edges.
    /// </para>
    /// </summary>
    /// <param name="playing">What the play-through is, for the note.</param>
    /// <param name="keptAs">The name under which the picture is kept when it is not at rest.</param>
    private LayoutSight? RestsOn(Editor editor, int frame, string playing, string keptAs, out string? wrong)
    {
        static bool ButForEdges(LayoutReading reading) => JudgeLayout(reading) is not null && EdgesOff(reading) is { Count: > 0 and <= 2 };
        var prepared = AsPrepared;
        var sight = Until(
            () => LookAtLayout(editor, frame),
            found => found is not null && (JudgeLayout(found.Reading) is null || (prepared && ButForEdges(found.Reading))),
            5,
            30);
        if (sight is null)
        {
            wrong = "no screenshot";
            return null;
        }

        var judged = JudgeLayout(sight.Reading);
        if (judged is null)
        {
            wrong = null;
            return sight;
        }

        if (!ButForEdges(sight.Reading) || EdgesOff(sight.Reading) is not { } off)
        {
            wrong = judged + KeptLayout(editor, sight, keptAs);
            return sight;
        }

        var edges = string.Join(", ", off.Select(found => string.Create(
            CultureInfo.InvariantCulture,
            $"{found.Edge.Name} of the {(found.IsCamera ? "camera" : "screen")} {found.Edge.Found - found.Edge.Expected:+0.00;-0.00} px")));
        if (prepared)
        {
            _report.Note(
                $"{playing}: the picture the playing starts from, frame {frame}, is in its layout but for {edges}, further off than an edge may be, paused. "
                + $"That is taken as what moves in the test clip lying against it, as at the frames of a play-through that are looked at again paused; read as {sight.Reading}");
            wrong = null;
            return sight;
        }

        wrong = $"{judged}. But for {edges} the picture is in its layout: that is what a play-through looks at again paused in the pictures it draws, and what the tool lets pass here without --as-before"
            + KeptLayout(editor, sight, keptAs);
        return sight;
    }

    private void SceneComesWhilePlaying()
    {
        Timeline.Mark("12: a scene that is come into while the preview plays");

        // The second scene starts at 4.0 s, side by side, and is entered by moving for 0.35 s:
        // the frames 120 to 129 have their instants inside the move.
        const int MovingFirst = 120;
        const int MovingLast = 129;
        const int Last = 165;

        // Playing starts at frame 81, and the layout of what is drawn is read from five frames
        // after the start. It started at frame 75 until 7 October 2026.
        //
        // At frame 75 the camera shows its frame 69. Worked out from the test picture's
        // definition, and read in the runs of 7 October 2026: in that frame its slanted band
        // runs through the gap
        // between the camera's red and lime patch and over the top of the lime patch, on two
        // of the three lines that are read across the red patch's right edge, the lime patch's
        // left edge and the lime patch's top. Since the speed lane made the preview smaller,
        // the camera is 244 pixels across and not 260, and in every run of the evening of
        // 5 October 2026 those three edges were not found at this frame, which leaves too few
        // upright edges. The band is on lines of the camera's patches while the screen shows
        // its frames 62 to 80, and on lines of the screen's own patches from 82 to 92. At
        // frame 81 it is past the one and not yet on the other: of the lines read across a
        // patch's edge it still touches one, of the three across the top of the camera's red
        // patch.
        //
        // So the playing starts at frame 81. With --as-before it starts at frame 75, as it did
        // when the check failed, and when it fails a note says what frame 81 reads.
        const int PreparedFrom = 81;
        var from = AsPrepared ? PreparedFrom : 75;
        var plainFirst = from + 5;

        // Two thirds of the pictures before the move have to be there: 20 of the frames 80 to 109.
        var plainWanted = 2 * (MovingFirst - 10 - plainFirst) / 3;
        var folder = NewCameraProject("A scene that is come into", p =>
        {
            p = ForScenePictures(p);
            return p with { Scenes = [p.Scenes[0], p.Scenes[0] with { Start = 4, Layout = StudioLayout.SideBySide, Transition = new StudioTransition { Kind = StudioTransitionKind.Morph } }] };
        });
        if (OpenReady(folder, "a scene that is come into") is not { } editor)
        {
            return;
        }

        var project = editor.Expected;
        SetSlider(editor, "StudioPlayhead", MiddleOf(from));
        var rest = RestsOn(editor, from, "playing into a scene", "scene-rest", out var notAtRest);
        var engine = EngineOf(editor);
        if (rest is null || notAtRest is not null || engine is null)
        {
            _report.Check("the preview rests on the frame the playing starts from, in the first scene's layout", false, notAtRest ?? "the window's preview is not the preview engine");
            if (!AsPrepared && engine is not null)
            {
                // What the prepared way would start from.
                SetSlider(editor, "StudioPlayhead", MiddleOf(PreparedFrom));
                var other = Until(() => LookAtLayout(editor, PreparedFrom), found => found is not null && JudgeLayout(found.Reading) is null, 3, 30);
                _report.Note(
                    $"without --as-before playing into a scene starts at frame {PreparedFrom}, where the slanted band of the test picture should be past the camera's patches and not yet on the screen's. Paused there, the picture "
                    + (other is null
                        ? "could not be taken"
                        : JudgeLayout(other.Reading) is { } judged
                            ? $"is not in the first scene's layout either: {judged}{KeptLayout(editor, other, "scene-rest-prepared")}"
                            : $"is in the first scene's layout: {other.Reading}"));
            }

            CloseQuietly(editor);
            return;
        }

        if (!AsPrepared)
        {
            _report.Note($"--as-before: playing into a scene starts at frame {from} and not at {PreparedFrom}, and the layout of what is drawn is read from frame {plainFirst}; at least {plainWanted} pictures of the frames {plainFirst} to {MovingFirst - 11} are asked for");
        }

        // Every picture the engine draws, through the hook it has for its check tools.
        var scenes = new List<SceneReading>();
        var edgesOff = new Dictionary<int, LayoutReading>();
        var cameraNextTo = new List<(int Frame, int Later)>();
        var lastScene = from;
        using var recorder = new SceneRecorder((shot, at) => scenes.Add(ReadDrawnScene(shot, editor.Folder, project, at, ref lastScene, edgesOff, cameraNextTo: cameraNextTo)));

        // What the view model raises, and what the window says of the split while it plays. Read on the UI thread, where both happen.
        var raised = new List<(long At, string Name)>(8192);
        var notes = new List<(string Explanation, bool IsShown)>(64);
        var viewModel = OnUi(() => editor.Window.ViewModel);

        // The Scene panel is read while this plays. It is brought on show beforehand: a panel
        // that had to be shown in the middle would take the UI thread's time, which is measured here.
        ShowPanel(editor, StudioInspectorPanel.Scene);

        // Where the playhead is, for this thread to follow without asking the window: a question
        // through UI Automation is answered on the UI thread, and takes it tens of milliseconds
        // of the very time that is measured here.
        double[] playhead = [MiddleOf(from)];
        PropertyChangedEventHandler onRaised = (_, e) =>
        {
            var name = e.PropertyName ?? string.Empty;
            raised.Add((Stopwatch.GetTimestamp(), name));
            if (name == nameof(StudioViewModel.PlayheadText))
            {
                Volatile.Write(ref playhead[0], viewModel.Playhead);
            }
            else if (name == nameof(StudioViewModel.SplitSceneExplanation))
            {
                notes.Add((viewModel.SplitSceneExplanation, viewModel.IsSplitSceneExplanationVisible));
            }
        };
        int[] Placements() => OnUi(() => new[] { SceneLane, ZoomLane, CutLane, SpeedLane }.Select(lane => LaneOf(editor, lane)?.BlockPlacements ?? -1).ToArray());

        // The tool keeps out of the preview's way while it plays: see ZoomPlaying.cs.
        byte[]? shotBuffer = null;
        editor.Camera.Take(ref shotBuffer);
        recorder.Reserve(rest.Canvas.Width, rest.Canvas.Height, 8);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var placedBefore = Placements();
        var diagnosticsBefore = engine.GetDiagnostics();
        OnUi(() => viewModel.PropertyChanged += onRaised);
        (string Lane, string Position, string? Layout, bool NoteShown)? whilePlaying = null;
        double played;
        long pressed;

        // What the tool's own garbage collector does while the preview plays: it stops every
        // thread of the process, the preview's among them, and a frame that is due then is late
        // or left out. It is said next to the frames that were, so that it can be told from them.
        var collectionsAtStart = GC.CollectionCount(0);
        var heldAtStart = GC.GetTotalPauseDuration();
        (int Runs, double Ms) collected;
        var watch = Stopwatch.StartNew();
        using (var probe = new UiThreadProbe(_dispatcher))
        {
            engine.AfterRender = recorder.OnRender;
            try
            {
                pressed = Stopwatch.GetTimestamp();
                Invoke(editor, "StudioPlayPauseButton");
                while (watch.Elapsed.TotalSeconds < 8 && FrameOf(Volatile.Read(ref playhead[0])) < Last)
                {
                    // Once the playhead is well inside the second scene, and well after what is
                    // measured: what the window shows of the scene, while it still plays.
                    if (whilePlaying is null && FrameOf(Volatile.Read(ref playhead[0])) >= 145)
                    {
                        whilePlaying = (LaneText(editor, SceneLane), NameOf(editor, "StudioScenePositionText", 0), Selected(editor, "StudioLayoutSideBySide"), editor.Root.Find("StudioSplitSceneNote") is not null);
                    }

                    Thread.Sleep(10);
                }

                Invoke(editor, "StudioPlayPauseButton");
                played = watch.Elapsed.TotalSeconds;
                collected = (GC.CollectionCount(0) - collectionsAtStart, (GC.GetTotalPauseDuration() - heldAtStart).TotalMilliseconds);
                Until(() => NameOf(editor, "StudioPlayPauseButton", 0), name => name == "Play", 2);
                engine.WaitForIdle(TimeSpan.FromSeconds(5));
            }
            finally
            {
                engine.AfterRender = null;
                recorder.Finish();
                OnUi(() => viewModel.PropertyChanged -= onRaised);
            }

            // What following the scene cost the UI thread, against an ordinary frame of the same playing.
            var sceneChanges = raised.Where(entry => entry.Name == nameof(StudioViewModel.CurrentSceneIndex)).ToArray();
            var refreshes = raised.Count(entry => entry.Name.Length == 0);
            var placedAfter = Placements();
            var tenth = Stopwatch.Frequency / 10;
            var change = sceneChanges.Length > 0 ? sceneChanges[0].At : 0;

            // From a frame's time before the change to three after it, against the 0.6 s of playing before that.
            var (busyAtChange, collectingAtChange) = sceneChanges.Length > 0 ? probe.LongestBetween(change - (tenth / 3), change + tenth) : (double.NaN, 0.0);
            var (busyBefore, collectingBefore) = sceneChanges.Length > 0 ? probe.LongestBetween(change - (8 * tenth), change - (2 * tenth)) : (double.NaN, 0.0);

            // The names raised with the change of scene: from it to the playhead's own notification, which ends every frame's.
            var withChange = sceneChanges.Length > 0 ? raised.SkipWhile(entry => entry.At < change).TakeWhile(entry => entry.Name != nameof(StudioViewModel.PlayheadText)).Count() : 0;
            var perFrame = raised.Count(entry => entry.Name == nameof(StudioViewModel.PlayheadText));
            // The time is for what nobody thought of: the three things that are counted here
            // would each show in it as well. On a quiet machine it was 15 to 19 ms, with 1.5 to
            // 2.5 ms for the slowest of the frames before the change. About 10 ms of it is the
            // first showing of the controls that only the second scene has, which happens once
            // in a window. A tenth of a second would be three frames of the playhead standing
            // still. On a machine that is busy with something else every answer comes late,
            // those before the change as well: 72 ms was seen with 18 ms before it. So a time
            // above a tenth of a second is still let pass when the frames before the change
            // were slow enough to account for it, at fifteen times their slowest. The ratio was
            // 7 to 11 on a quiet machine and 4 to 11 on a busy one.
            var isKept = busyAtChange >= 100 && busyAtChange >= 15 * busyBefore;
            _report.Check(
                "coming into the second scene while playing refreshes what shows the current scene once, and lays out none of the four lanes again: the scene changes once, nothing asks for everything to be refreshed, the blocks of the lanes are placed as often as before, and the UI thread is not kept for a tenth of a second, or for no more than fifteen times as long as the slowest frame before the change kept it",
                sceneChanges.Length == 1 && refreshes == 0 && placedAfter.SequenceEqual(placedBefore) && placedBefore.All(count => count > 0) && withChange is > 10 and < 80 && !isKept && !double.IsNaN(busyAtChange),
                string.Create(CultureInfo.InvariantCulture, $"the current scene was reported {sceneChanges.Length} time(s) and everything {refreshes} time(s) during {perFrame} reports of the playhead; with the change of scene {withChange} values were reported; the lanes' blocks were placed {string.Join("/", placedBefore)} times before and {string.Join("/", placedAfter)} after (scenes/zooms/cuts/speed changes); ")
                    + string.Create(CultureInfo.InvariantCulture, $"asked every millisecond, the UI thread took at most {busyAtChange:0.0} ms to answer around the change of scene, and at most {busyBefore:0.0} ms in the 0.6 s of playing before it")
                    + (collectingAtChange + collectingBefore > 0 ? string.Create(CultureInfo.InvariantCulture, $"; not counted in that, the tool's garbage collector held every thread for up to {collectingAtChange:0.0} ms around the change and {collectingBefore:0.0} ms before it") : string.Empty));
        }

        var diagnosticsAfter = engine.GetDiagnostics();

        // At rest, where the playing was paused: looked at now, before anything else moves the playhead.
        var head = Playhead(editor);
        var parked = LookForLayout(editor, FrameOf(head), 3);

        // What each of those pictures shows.
        var moving = scenes.Where(scene => scene.Frame is >= MovingFirst and <= MovingLast).ToList();
        var plain = scenes.Where(scene => scene.Frame >= plainFirst && scene.Frame < MovingFirst - 10).ToList();
        var after = scenes.Where(scene => scene.Frame is > MovingLast and <= Last - 5).ToList();
        var counted = scenes.SkipWhile(scene => scene.Frame < plainFirst).TakeWhile(scene => scene.Frame <= Last - 5).ToList();
        var unreadable = counted.Count(scene => scene.Frame == FrameCode.Unreadable);
        var readable = counted.Where(scene => scene.Frame != FrameCode.Unreadable).ToList();
        var backwards = readable.Zip(readable.Skip(1), (a, b) => b.Frame < a.Frame).Count(wentBack => wentBack);

        // A picture with an edge off is held against the paused preview's picture of its frame.
        var again = LookAgainPaused(editor, edgesOff);
        var wrong = counted.Where(scene => scene.Problem is not null).Select(scene => scene.Problem!).Concat(again.Wrong).ToList();
        double WorstOf(List<SceneReading> read) => read.Where(scene => !double.IsNaN(scene.Worst) && !edgesOff.ContainsKey(scene.Frame)).Select(scene => scene.Worst).DefaultIfEmpty(double.NaN).Max();
        _report.Check(
            "while the preview plays into the second scene, every picture it draws shows the layout the format gives for the frame it shows: the bubble layout up to the line, the layers on their way during the move, and side by side after it",
            recorder.Failure is null && recorder.Unread == 0 && moving.Count >= 6 && plain.Count >= plainWanted && after.Count >= 20 && wrong.Count == 0 && unreadable == 0 && backwards == 0,
            (wrong.Count == 0 ? string.Empty : $"{wrong.Count} of {counted.Count} pictures are wrong: {string.Join(" | ", wrong.Take(4))}. ")
                + string.Create(CultureInfo.InvariantCulture, $"{moving.Count} pictures of the {MovingLast - MovingFirst + 1} frames of the move, the edge furthest from its place {WorstOf(moving):0.00} px from it; {plain.Count} pictures before it, the furthest {WorstOf(plain):0.00} px; {after.Count} after it, the furthest {WorstOf(after):0.00} px; ")
                + $"{unreadable} pictures did not read, {backwards} went back to an earlier frame; {scenes.Count} pictures were drawn in all"
                + (again.Seen.Length == 0 ? string.Empty : "; " + again.Seen)
                + (recorder.Failure is null ? string.Empty : "; reading a picture back failed: " + recorder.Failure)
                + (recorder.Unread == 0 ? string.Empty : $"; {recorder.Unread} pictures could not be kept to be read"));
        if (moving.Count > 0 && wrong.Count == 0)
        {
            _layoutErrors.Add(WorstOf(moving));
        }

        // Reported, not judged: see ReadDrawnScene.
        _report.Note(cameraNextTo.Count == 0
            ? $"in each of the {counted.Count} pictures the preview drew, the camera showed the frame that goes with the screen's frame"
            : $"in {cameraNextTo.Count} of the {counted.Count} pictures the preview drew, the camera showed the frame next to the one that goes with the screen's frame: {string.Join(", ", cameraNextTo.Take(8).Select(scene => $"with screen frame {scene.Frame} the camera's frame {(scene.Later > 0 ? "after" : "before")} it"))}. The preview has a player for each recording, and this is the two not handing a frame over at the same moment while it plays; a picture with the camera two frames off would have failed the check above");

        // The thirty frames around the line, against the frames before them. Those begin with
        // the frame after the one that was on screen when Play was pressed: what the preview
        // does with that one is in the note below, and is not a frame of the playing.
        var around = PaceOf(scenes, 105, 134);
        var before = PaceOf(scenes, from + 1, 104);
        _report.Check(
            "playing into another scene drops and delays no more frames than playing inside one, allowing two",
            around.Dropped <= before.Dropped + 2 && around.Late <= before.Late + 2 && moving.Count >= 6,
            string.Create(CultureInfo.InvariantCulture, $"of the 30 frames around the line, 105 to 134, {around.Dropped} were never drawn{around.Which} and {around.Late} came more than a frame's time late, the longest wait between two frames {around.LongestMs:0} ms; of the {104 - from} before them, {from + 1} to 104, {before.Dropped}{before.Which} and {before.Late}, the longest wait {before.LongestMs:0} ms; ")
                + $"during the {F(played, "0.0")} s of playing the engine drew {diagnosticsAfter.FramesDrawn - diagnosticsBefore.FramesDrawn} pictures and discarded {diagnosticsAfter.LateFramesDiscarded - diagnosticsBefore.LateFramesDiscarded} late frames, "
                + string.Create(CultureInfo.InvariantCulture, $"and the tool's garbage collector ran {collected.Runs} time(s) and held every thread for {collected.Ms:0} ms in all"));

        // How the playing started. Reported, not judged: the layout of the first pictures is
        // not read (see plainFirst), and nothing else looks at them.
        var drawn = scenes.Where(scene => scene.Frame != FrameCode.Unreadable).ToList();
        var leftOut = Enumerable.Range(from + 1, plainFirst - from - 1).Where(frame => drawn.All(scene => scene.Frame != frame)).ToArray();
        _report.Note(drawn.Count == 0
            ? $"playing was started with frame {from} on screen, and no picture that could be read was drawn after it"
            : string.Create(CultureInfo.InvariantCulture, $"playing was started with frame {from} on screen: the first picture the preview drew after the tool pressed Play showed frame {drawn[0].Frame}, {Stopwatch.GetElapsedTime(pressed, drawn[0].At).TotalMilliseconds:0} ms after the press; frame {from} was drawn {drawn.Count(scene => scene.Frame == from)} more time(s); ")
                + $"of the frames {from + 1} to {plainFirst - 1}, {(leftOut.Length == 0 ? "each was drawn" : string.Join(", ", leftOut) + " was never drawn")}");

        // What the window showed of the second scene while it was still playing.
        _report.Check(
            "while it still plays, the window shows the scene the playhead has come into: the lane marks the second scene, the Scene panel names it, and Layout shows Side by side",
            whilePlaying is { } seen && seen.Lane == SceneLaneWanted(editor, 1) && seen.Position == "Scene 2 of 2" && seen.Layout is null,
            whilePlaying is { } shown ? $"the lane: {shown.Lane}; \"{shown.Position}\"; {shown.Layout ?? "Side by side is chosen"}" : "the playhead did not get that far");

        // No scene can be split in the first 0.3 s of a scene, nor while it still moves, and the
        // playhead comes past both. The reason is not shown while the preview plays.
        var reasons = notes.Where(note => note.Explanation.Length > 0).Select(note => note.Explanation).Distinct().ToArray();
        _report.Check(
            "while the preview plays past where no scene can be split, the reason is known to the Split buttons and is not written into the inspector, where it would push everything under it down and up again",
            reasons.Length >= 1 && notes.All(note => !note.IsShown) && whilePlaying is { NoteShown: false },
            $"while playing the reason changed {notes.Count} time(s): {string.Join(" / ", notes.Select(note => $"\"{note.Explanation}\"{(note.IsShown ? " (shown)" : string.Empty)}"))}");

        // At rest again, in the second scene.
        _report.Check(
            "paused after the move, the preview rests on the playhead's frame, side by side",
            parked is not null && JudgeLayout(parked.Reading) is null && parked.Reading.Resolved.Layout == StudioLayout.SideBySide && FrameOf(head) > MovingLast,
            parked is null ? "no screenshot" : $"playhead {Seconds(head)} s (frame {FrameOf(head)}): {JudgeLayout(parked.Reading) ?? parked.Reading.ToString()}");
        CloseQuietly(editor);
    }

    /// <summary>
    /// The handle on the camera in the preview belongs to the scene the playhead is in: it lies
    /// where that scene has the bubble at rest, also while the picture is still moving there,
    /// and moving it changes that scene and no other.
    /// </summary>
    private void BubbleHandleFollowsTheScene()
    {
        Timeline.Mark("12: the camera's handle follows the scene");
        var folder = NewCameraProject("A bubble in every scene", p =>
        {
            p = OnLemon(p);
            return p with
            {
                Scenes =
                [
                    p.Scenes[0],
                    p.Scenes[0] with { Start = 4, Bubble = new StudioBubble { Anchor = StudioAnchor.TopLeft, Size = 0.3 }, Transition = new StudioTransition { Kind = StudioTransitionKind.Morph } },
                    p.Scenes[0] with { Start = 8, Layout = StudioLayout.SideBySide },
                ],
            };
        });
        if (OpenReady(folder, "a bubble in every scene") is not { } editor)
        {
            return;
        }

        (StudioFrameRect? Handle, LayoutSight? Sight) At(int frame)
        {
            SetSlider(editor, "StudioPlayhead", MiddleOf(frame));
            var sight = LookForLayout(editor, frame, 3);
            return (sight is null ? null : Until(() => HandleRect(editor, sight.Shot), _ => true, 0), sight);
        }

        // In the first scene, in the second while it is still moving in, in the second at rest, and in the third, which has no bubble.
        var first = At(60);
        var moving = At(124);
        var second = At(150);
        var third = At(250);
        StudioFrameRect? Camera(LayoutSight? sight) => sight?.Reading.CameraLayer;
        double Apart(StudioFrameRect? a, StudioFrameRect? b) => a is { } one && b is { } other ? EdgeDistance(one, other) : double.NaN;
        var pictures = new[] { first, moving, second, third }.Select(at => at.Sight is null ? "no screenshot" : JudgeLayout(at.Sight.Reading)).ToArray();
        _report.Check(
            "the handle lies over the camera where the scene the playhead is in has it at rest: bottom right in the first scene, top left in the second, and nowhere in the third, which is side by side",
            pictures.All(wrong => wrong is null) && Apart(first.Handle, Camera(first.Sight)) <= 2 && Apart(second.Handle, Camera(second.Sight)) <= 2 && third.Handle is null && Apart(Camera(first.Sight), Camera(second.Sight)) > 100,
            $"in the first scene the handle is {(first.Handle is { } a ? R(a) : "hidden")} and the camera {(Camera(first.Sight) is { } b ? R(b) : "nowhere")}; in the second {(second.Handle is { } c ? R(c) : "hidden")} and {(Camera(second.Sight) is { } d ? R(d) : "nowhere")}; "
                + $"in the third the handle is {(third.Handle is { } e ? R(e) : "hidden")}{(pictures.All(wrong => wrong is null) ? string.Empty : "; the pictures: " + string.Join(" | ", pictures.Where(wrong => wrong is not null)))}");
        _report.Check(
            "paused inside the move into the second scene, the picture shows the camera on its way and the handle already lies where the second scene has it at rest, because that is what dragging it changes",
            pictures[1] is null && Apart(moving.Handle, Camera(second.Sight)) <= 2 && Apart(moving.Handle, Camera(moving.Sight)) > 20,
            $"at frame 124 the camera is drawn in {(Camera(moving.Sight) is { } f ? R(f) : "nowhere")}, the handle is {(moving.Handle is { } g ? R(g) : "hidden")}, and at rest the second scene has the camera in {(Camera(second.Sight) is { } h ? R(h) : "nowhere")}");

        // A drag of the handle, as the overlay makes it: the camera's top left corner to a place on the canvas of the video.
        Timeline.Mark("12: moving the camera of the second scene");
        var scenesBefore = ScenesOf(editor);
        var canvasSize = OnUi(() => (editor.Window.ViewModel.CanvasWidth, editor.Window.ViewModel.CanvasHeight));
        SetSlider(editor, "StudioPlayhead", MiddleOf(150));
        OnUi(() =>
        {
            var viewModel = editor.Window.ViewModel;
            viewModel.BeginGesture();
            viewModel.MoveBubbleTopLeft(500, 300);
            viewModel.MoveBubbleTopLeft(900, 500);
            viewModel.EndGesture();
        });
        var scenesAfter = Until(() => ScenesOf(editor), scenes => Describe(scenes[1]) != Describe(scenesBefore[1]), 1.5);
        editor.Expected = editor.Expected with { Scenes = scenesAfter };
        var moved = At(150);

        // The video is 1920 × 1080, so the bubble of 0.3 is 324 across, with its corner at (900, 500).
        var scale = moved.Sight is null ? double.NaN : moved.Sight.Canvas.Width / canvasSize.CanvasWidth;
        var wanted = moved.Sight is null ? default : new StudioFrameRect(moved.Sight.Canvas.X + (900 * scale), moved.Sight.Canvas.Y + (500 * scale), 324 * scale, 324 * scale);
        Invoke(editor, "StudioUndoButton");
        var undone = Until(() => Describe(ScenesOf(editor)), now => now == Describe(scenesBefore), 1.5);
        _report.Check(
            "moving the handle in the second scene moves that scene's camera, and no other scene's: the picture and the handle follow, the first and the third scene are as they were, and one Undo takes the whole move back",
            Describe(scenesAfter[0]) == Describe(scenesBefore[0]) && Describe(scenesAfter[2]) == Describe(scenesBefore[2]) && Describe(scenesAfter[1]) != Describe(scenesBefore[1])
                && moved.Sight is not null && JudgeLayout(moved.Sight.Reading) is null && Apart(Camera(moved.Sight), wanted) <= 1.5 && Apart(moved.Handle, Camera(moved.Sight)) <= 2 && undone == Describe(scenesBefore),
            $"the second scene went from {Describe(scenesBefore[1])} to {Describe(scenesAfter[1])}; the camera is drawn in {(Camera(moved.Sight) is { } i ? R(i) : "nowhere")}, wanted {R(wanted)}, and the handle is {(moved.Handle is { } j ? R(j) : "hidden")}; "
                + $"{(moved.Sight is null ? "no screenshot" : JudgeLayout(moved.Sight.Reading) ?? "the picture shows it")}; after one Undo the editor holds {undone}");
        CloseQuietly(editor);
    }
}
