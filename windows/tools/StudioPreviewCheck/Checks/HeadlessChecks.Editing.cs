using System.Diagnostics;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Core.Studio.Preview;
using TinyClips.Tools.StudioPreviewCheck.Media;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

// The editor's rules for cuts, scenes and zooms, with StudioEditorSession driving the real
// engine. Everything is done the way the editor window does it: the cuts, scenes and zooms are
// made with the session's own edits, playback is started and stopped with TogglePlayback, and
// the picture is read back from what the engine drew.
internal sealed partial class HeadlessChecks
{
    // ---------------------------------------------------------------------------------------
    // Cuts
    // ---------------------------------------------------------------------------------------

    private void EditorPlaysOverCuts()
    {
        // A cut in the middle that leaves out the frames 150 to 194, and one from frame 300 to
        // the end of the recording, so that the video ends where that one starts.
        const int cutFirst = 150;
        const int cutEnd = 195;
        const int endFirst = 300;
        _report.Section("The editor session and cuts, on the engine");

        // Asked for with --stalls: the crossings once more while the process is held up. The
        // engine is opened with what it takes to hold its draws up, should that be asked for.
        var heldUpKinds = HoldUps.Parse(_options.Text("stalls", string.Empty)).Where(kind => kind != "none").ToArray();
        var heldUp = heldUpKinds.Select(kind => new HoldUps(kind, HoldUpMilliseconds)).ToArray();
        using var holdUpsOwner = new Disposables(heldUp);
        using var host = EditorHost.Open(HoldUps.With(Muted, heldUp), _media, TestMedia.Camera, Late);
        if (host.Preview is not { } preview)
        {
            _report.Check("LoadAsync makes the session ready, with the engine as its preview", false, $"state {host.Get(e => e.State)}: {host.Get(e => e.UnavailableMessage)}");
            return;
        }

        // Cut at the playhead, then the end dragged to where it belongs.
        var duration = (double)TestMedia.Screen.Seconds;
        host.Do(e =>
        {
            e.Scrub(TestFolder.TimeOf(cutFirst));
            if (e.AddCutAtPlayhead().Index is { } middle)
            {
                e.SetCutEnd(middle, TestFolder.TimeOf(cutEnd));
            }

            if (e.AddCut(TestFolder.TimeOf(endFirst)).Index is { } atEnd)
            {
                e.SetCutEnd(atEnd, duration);
            }
        });
        preview.WaitForIdle();
        var (cuts, playbackEnd, project) = host.Get(e => (e.Model!.Project.Edits.Cuts, e.Model!.PlaybackEnd, e.Project!));
        preview.Follow(project);
        var made = cuts.Length == 2
            && cuts[0].Start == TestFolder.TimeOf(cutFirst) && cuts[0].End == TestFolder.TimeOf(cutEnd)
            && cuts[1].Start == TestFolder.TimeOf(endFirst) && cuts[1].End == duration
            && playbackEnd == TestFolder.TimeOf(endFirst);
        _report.Check(
            "AddCutAtPlayhead, AddCut and SetCutEnd make a cut in the middle and one that runs to the end of the recording; the video then ends where the last cut starts",
            made,
            $"cuts {string.Join(", ", cuts.Select(c => $"{F(c.Start, "0.###")} to {F(c.End, "0.###")} s"))}; PlaybackEnd {F(playbackEnd, "0.###")} s");
        if (!made)
        {
            return;
        }

        bool InACut(double time) => cuts.Any(c => time >= c.Start && time < c.End) && Math.Abs(time - playbackEnd) > 1e-9;

        // Playing over the cut in the middle, from a second before it: with nothing in the way,
        // and then with each hold-up that was asked for.
        var crossings = _options.Number("count", _quick ? 4 : 12);
        foreach (var holdUps in new HoldUps?[] { null }.Concat(heldUp))
        {
            var when = holdUps is null ? string.Empty : $", with {holdUps.Name}";
            var wrong = new List<string>();
            var flashed = new Samples();
            var flashedFor = new Samples();
            var still = new Samples();
            var acrossIn = new Samples();
            var askedAfter = new Samples();
            var tookBack = new Samples();
            holdUps?.Begin();
            for (var crossing = 0; crossing < crossings; crossing++)
            {
                host.Do(e => e.Scrub(TestFolder.TimeOf(cutFirst - 30, 0.5)));
                preview.WaitForIdle();
                preview.Recorder.Drain();
                host.TakePlayheads();
                var space = host.Space();
                var watch = Stopwatch.StartNew();
                while (preview.PositionFrame < cutEnd + 20 && watch.Elapsed.TotalSeconds < 8)
                {
                    Thread.Sleep(5);
                }

                var pauseAsked = Stopwatch.GetTimestamp();
                var pause = host.Space();
                preview.WaitForIdle();
                var scenes = preview.Recorder.Drain().Where(c => c.At >= space.At && c.At <= pause.At && c.Screen != FrameCode.Unreadable).ToList();
                var playheads = host.TakePlayheads().Where(p => p.At >= space.At && p.At <= pause.At).ToList();

                // What was drawn while it played. The pause that ends the crossing can take the
                // picture back, to its last frame with a number, if it showed one without: that
                // is the pause's doing and no playing backwards. It is counted.
                var whilePlaying = scenes.Where(c => c.At < pauseAsked).ToList();
                var landing = scenes.FindIndex(c => c.Screen >= cutEnd);
                var firstOfCut = scenes.FindIndex(c => c.Screen >= cutFirst && c.Screen < cutEnd);
                var lastKept = scenes.FindLastIndex(c => c.Screen < cutFirst);
                var ofCut = scenes.Take(Math.Max(0, landing)).Where(c => c.Screen >= cutFirst).Select(c => c.Screen).Distinct().ToList();
                var afterLanding = landing < 0 ? new List<Composite>() : scenes.Skip(landing).ToList();
                var onward = afterLanding.Select(c => c.Screen).Where(f => f > cutEnd && f <= cutEnd + 20).Distinct().Count();
                var rested = playheads.Where(p => p.IsPlaying && InACut(p.Playhead)).Select(p => F(p.Playhead, "0.###")).ToList();
                var problem = !space.IsPlaying ? "the session was not playing when TogglePlayback had returned"
                    : landing < 0 ? $"no frame after the cut was drawn in {F(watch.Elapsed.TotalSeconds)} s; the last scenes showed [{string.Join(' ', scenes.TakeLast(6).Select(c => c.Screen))}]"
                    : scenes[landing].Screen != cutEnd ? $"the first frame drawn after the cut was {scenes[landing].Screen}, and the video goes on with frame {cutEnd}"
                    : afterLanding.Any(c => c.Screen < cutEnd) ? $"after frame {cutEnd} had been drawn, scenes showed [{string.Join(' ', afterLanding.Where(c => c.Screen < cutEnd).Select(c => c.Screen))}]"
                    : Backwards(whilePlaying) is { } back ? back
                    : onward < 16 ? $"only {onward} of the 20 frames after frame {cutEnd} were drawn"
                    : rested.Count > 0 ? $"while it played, the session's playhead was inside a cut: at [{string.Join(' ', rested)}] s"
                    : lastKept < 0 ? "no frame before the cut was drawn"
                    : null;
                if (problem is not null)
                {
                    wrong.Add($"crossing {crossing + 1}: {problem}{Dump(preview, "cuts")}");
                    continue;
                }

                flashed.Add(ofCut.Count);
                tookBack.Add(whilePlaying.Count == 0 ? 0 : Math.Max(0, whilePlaying.Max(c => c.Screen) - scenes[^1].Screen));
                still.Add(Stopwatch.GetElapsedTime(scenes[landing - 1].At, scenes[landing].At).TotalMilliseconds);
                acrossIn.Add(Stopwatch.GetElapsedTime(scenes[lastKept].At, scenes[landing].At).TotalMilliseconds);
                if (firstOfCut >= 0 && firstOfCut < landing)
                {
                    flashedFor.Add(Stopwatch.GetElapsedTime(scenes[firstOfCut].At, scenes[landing].At).TotalMilliseconds);
                    var jump = playheads.FindIndex(p => p.Playhead >= cuts[0].End);
                    if (jump >= 0)
                    {
                        askedAfter.Add(Stopwatch.GetElapsedTime(scenes[firstOfCut].At, playheads[jump].At).TotalMilliseconds);
                    }
                }
            }

            holdUps?.Rest();
            _report.Check(
                $"playing over a cut{when} ({crossings} times): the playhead never rests inside it, the first frame drawn after it is the one the video goes on with, no frame of the cut is drawn after that, and playback goes on",
                wrong.Count == 0,
                wrong.Count == 0 ? null : $"{wrong.Count} wrong; first: {string.Join(" | ", wrong.Take(3))}");
            _report.Note($"what is seen at a cut while playing{when}: frames of the cut drawn before the jump landed: {flashed.Summary("frames")}; from the first of them to the first frame after the cut: {flashedFor.Summary()}; the picture stood still before that frame for {still.Summary()}; from the last frame before the cut to the first after it: {acrossIn.Summary()} (one frame lasts {F(1000.0 / Fps)} ms)");
            _report.Note($"the session asked for the jump {askedAfter.Summary()} after the first frame of the cut had been drawn{when}: it learns of a cut when a position inside it arrives{(holdUps is null ? string.Empty : "; " + holdUps.Describe())}");
            _report.Note($"the pause that ended a crossing took the picture back to its last frame with a number in {tookBack.Count - tookBack.CountOf(0)} of {tookBack.Count} crossings{when}: by {tookBack.Summary("frames")}");
        }

        // Playing into the cut that ends the video, from a second before it.
        var ends = _quick ? 3 : 8;
        var endsWrong = new List<string>();
        var past = new Samples();
        var pastFor = new Samples();
        for (var end = 0; end < ends; end++)
        {
            host.Do(e => e.Scrub(TestFolder.TimeOf(endFirst - 30, 0.5)));
            preview.WaitForIdle();
            preview.Recorder.Drain();
            host.TakePlayheads();
            var space = host.Space();
            var stopped = host.WaitForStop(TimeSpan.FromSeconds(8));
            var idle = preview.WaitForIdle();
            var scenes = preview.Recorder.Drain().Where(c => c.At >= space.At && c.Screen != FrameCode.Unreadable).ToList();
            var playheads = host.TakePlayheads().Where(p => p.At >= space.At).ToList();
            var shown = preview.ReadShown();
            var (playhead, playing) = host.Get(e => (e.Playhead, e.IsPlaying));
            var beyond = scenes.Where(c => c.Screen > endFirst).ToList();
            var rested = playheads.Where(p => p.IsPlaying && InACut(p.Playhead)).Select(p => F(p.Playhead, "0.###")).ToList();
            var drawn = scenes.Select(c => c.Screen).Where(f => f >= endFirst - 30 && f < endFirst).Distinct().Count();
            var problem = !stopped ? $"the session did not stop at the end of the video (engine Position frame {preview.PositionFrame})"
                : !idle ? "the engine did not come to rest"
                : playing || preview.Engine.IsPlaying ? "something was still playing"
                : Math.Abs(playhead - playbackEnd) > 1e-9 ? $"the playhead came to rest at {F(playhead, "0.####")} s, and the video ends at {F(playbackEnd, "0.####")} s"
                : shown != new Shown(endFirst, preview.ExpectedCamera(endFirst)) || preview.PositionFrame != endFirst ? $"the picture came to rest on {shown}, Position on frame {preview.PositionFrame}; the playhead's frame is {endFirst}"
                : drawn < 26 ? $"only {drawn} of the last 30 frames of the video were drawn"
                : rested.Count > 0 ? $"while it played, the session's playhead was inside a cut: at [{string.Join(' ', rested)}] s"
                : null;
            if (problem is not null)
            {
                endsWrong.Add($"time {end + 1}: {problem}{Dump(preview, "cuts")}");
                continue;
            }

            past.Add(beyond.Select(c => c.Screen).Distinct().Count());
            if (beyond.Count > 0)
            {
                pastFor.Add(Stopwatch.GetElapsedTime(beyond[0].At, scenes[^1].At).TotalMilliseconds);
            }
        }

        _report.Check(
            $"playing into a cut that ends the video ({ends} times): the session stops by itself with its playhead at PlaybackEnd, and the picture rests on the frame there",
            endsWrong.Count == 0,
            endsWrong.Count == 0 ? null : $"{endsWrong.Count} wrong; first: {string.Join(" | ", endsWrong.Take(3))}");
        _report.Note($"what is seen where the video ends in a cut: frames after frame {endFirst} drawn before the picture came back to it: {past.Summary("frames")}{(pastFor.Count == 0 ? string.Empty : $"; from the first of them until it was back: {pastFor.Summary()}")}");

        // Paused: a frame at a time into the cut and out of it at the other end, and back.
        var steps = new List<string>();
        var stepCount = 0;
        void Walk(int from, int count, int direction)
        {
            host.Do(e => e.Scrub(TestFolder.TimeOf(from, 0.5)));
            preview.WaitForIdle();
            for (var step = 1; step <= count; step++)
            {
                var want = from + (step * direction);
                host.Do(e => e.StepFrames(direction));
                var idle = preview.WaitForIdle();
                var shown = preview.ReadShown();
                var head = FrameOf(host.Get(e => e.Playhead));
                stepCount++;
                if (!idle || shown != new Shown(want, preview.ExpectedCamera(want)) || head != want || preview.PositionFrame != want)
                {
                    steps.Add($"from frame {from}, step {step} of {direction:+0;-0}: wanted frame {want}; picture {shown}, playhead on frame {head}, Position on frame {preview.PositionFrame}{Dump(preview, "cuts")}");
                }
            }
        }

        Walk(cutFirst - 3, 6, 1);
        Walk(cutFirst + 3, 6, -1);
        Walk(cutEnd - 3, 6, 1);
        Walk(cutEnd + 3, 6, -1);
        _report.Check(
            $"paused, StepFrames goes a frame at a time into the cut, out of it at its end, and back ({stepCount} steps): the picture is the playhead's frame every time, also inside the cut",
            steps.Count == 0,
            steps.Count == 0 ? null : $"{steps.Count} wrong; first: {string.Join(" | ", steps.Take(3))}");

        // Paused: the playhead put into the cut, and Space from there.
        const int inside = 170;
        host.Do(e => e.Scrub(TestFolder.TimeOf(inside, 0.5)));
        var rest = preview.WaitForIdle();
        var inCut = preview.ReadShown();
        var (headInside, cutThere) = host.Get(e => (FrameOf(e.Playhead), e.GetCutIndexAt(e.Playhead)));
        _report.Check(
            "paused, Scrub into the cut shows the frame there, with the playhead inside the cut",
            rest && inCut == new Shown(inside, preview.ExpectedCamera(inside)) && headInside == inside && cutThere == 0 && preview.PositionFrame == inside,
            $"picture {inCut}, playhead on frame {headInside}, in cut {(cutThere is { } index ? index.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none")}");

        preview.Recorder.Drain();
        var from = host.Space();
        Thread.Sleep(500);
        var stopAsked = Stopwatch.GetTimestamp();
        host.Space();
        preview.WaitForIdle();
        var played = preview.Recorder.Drain().Where(c => c.At >= from.At && c.At < stopAsked && c.Screen != FrameCode.Unreadable).Select(c => c.Screen).ToList();
        _report.Check(
            "TogglePlayback with the playhead inside the cut plays from the frame the video goes on with, and draws no frame of the cut",
            from.IsPlaying && played.Count > 5 && played[0] == cutEnd && played.All(f => f >= cutEnd) && Backwards(played) is null,
            $"the scenes drawn showed [{string.Join(' ', played.Take(8))}{(played.Count > 8 ? " ..." : string.Empty)}]");
        _report.Check("the session and the engine reported nothing wrong", Problems(host, preview).Length == 0, Problems(host, preview));
    }

    /// <summary>Says so when scenes showed an earlier frame after a later one.</summary>
    private static string? Backwards(List<Composite> scenes) => Backwards(scenes.Select(c => c.Screen).ToList());

    private static string? Backwards(List<int> frames)
    {
        for (var index = 1; index < frames.Count; index++)
        {
            if (frames[index] < frames[index - 1])
            {
                return $"frame {frames[index]} was drawn after frame {frames[index - 1]}";
            }
        }

        return null;
    }

    // ---------------------------------------------------------------------------------------
    // A scene change
    // ---------------------------------------------------------------------------------------

    private void EditorChangesScene()
    {
        // Side by side from 4 s and the bubble again from 8 s, each entered by a move of 0.35 s:
        // the frames 120 to 129 and 240 to 249 have their middles inside a move.
        const int firstChange = 120;
        const int secondChange = 240;
        const int moveFrames = 10;
        _report.Section("The editor session and a scene change, on the engine");
        using var holdUps = new HoldUps("collector", HoldUpMilliseconds);
        using var host = EditorHost.Open(Muted, _media, TestMedia.Camera, Late);
        if (host.Preview is not { } preview)
        {
            _report.Check("LoadAsync makes the session ready, with the engine as its preview", false, $"state {host.Get(e => e.State)}: {host.Get(e => e.UnavailableMessage)}");
            return;
        }

        // Split at the playhead, then another layout for the scene that starts there.
        host.Do(e =>
        {
            e.Scrub(TestFolder.TimeOf(firstChange));
            e.SplitSceneAtPlayhead();
            e.SetLayout(StudioLayout.SideBySide);
            e.Scrub(TestFolder.TimeOf(secondChange));
            e.SplitSceneAtPlayhead();
            e.SetLayout(StudioLayout.Bubble);
        });
        preview.WaitForIdle();
        var project = host.Get(e => e.Project!);
        preview.Follow(project);
        var scenes = project.Scenes;
        var made = scenes.Length == 3
            && scenes[0] is { Start: 0, Layout: StudioLayout.Bubble }
            && scenes[1] is { Layout: StudioLayout.SideBySide, Transition: { Kind: StudioTransitionKind.Morph, Duration: 0.35 } } && scenes[1].Start == TestFolder.TimeOf(firstChange)
            && scenes[2] is { Layout: StudioLayout.Bubble, Transition: { Kind: StudioTransitionKind.Morph, Duration: 0.35 } } && scenes[2].Start == TestFolder.TimeOf(secondChange);
        _report.Check(
            "SplitSceneAtPlayhead and SetLayout make a scene side by side and one with the bubble again, each entered by a move of 0.35 s",
            made,
            string.Join("; ", scenes.Select(s => $"{s.Layout} from {F(s.Start, "0.###")} s, {s.Transition.Kind} {F(s.Transition.Duration, "0.##")} s")));
        if (!made)
        {
            return;
        }

        var layout = new MovingLayout(project, preview.Folder.Screen, preview.Folder.Camera, 1280, 720);
        _report.Line($"  the layout at rest in the first scene: {layout.Describe(firstChange - 1)}");
        _report.Line($"  half way into the second:              {layout.Describe(firstChange + (moveFrames / 2))}");
        _report.Line($"  at rest in the second:                 {layout.Describe(firstChange + moveFrames + 1)}");

        // Paused: every frame from before each move to after it, by Scrub and by StepFrames.
        var paused = new List<string>();
        var pausedFits = new Samples();
        var looked = 0;
        void Look(int frame, string how)
        {
            var idle = preview.WaitForIdle();
            looked++;
            if (preview.ReadPicture() is not { } picture)
            {
                paused.Add($"frame {frame} ({how}): no picture");
                return;
            }

            var read = layout.Read(picture, frame);
            var wanted = preview.Folder.ExpectedCamera(frame, project);
            if (!idle || read.Screen != frame || !read.ScreenFit.Within(MovingLayout.Tolerance) || read.Camera != wanted || !read.CameraFit.Within(MovingLayout.Tolerance) || preview.PositionFrame != frame)
            {
                paused.Add($"frame {frame} ({how}): screen {(read.Screen == FrameCode.Unreadable ? "not readable" : $"frame {read.Screen}, edges {read.ScreenFit} from their places{(read.FitsFrame is { } other ? $", fitting the layout of frame {other}" : string.Empty)}")}; camera {(read.Camera == FrameCode.Unreadable ? "not readable" : $"frame {read.Camera}, edges {read.CameraFit} from their places")}, wanted frame {wanted}; Position on frame {preview.PositionFrame}; the layout of that frame is {layout.Describe(frame)}{Dump(preview, "scenes")}");
            }
            else
            {
                pausedFits.Add(Math.Max(read.ScreenFit.Worst, read.CameraFit.Worst));
            }
        }

        foreach (var change in new[] { firstChange, secondChange })
        {
            for (var frame = change - 3; frame <= change + moveFrames + 3; frame++)
            {
                host.Do(e => e.Scrub(TestFolder.TimeOf(frame, 0.5)));
                Look(frame, "Scrub");
            }

            host.Do(e => e.Scrub(TestFolder.TimeOf(change - 2, 0.5)));
            preview.WaitForIdle();
            for (var frame = change - 1; frame <= change + moveFrames + 1; frame++)
            {
                host.Do(e => e.StepFrames(1));
                Look(frame, "StepFrames");
            }
        }

        _report.Check(
            $"paused before, inside and after each move, by Scrub and by StepFrames ({looked} frames): both clips are where the format puts them for the frame shown, to within {F(MovingLayout.Tolerance)} px",
            paused.Count == 0,
            paused.Count == 0 ? $"the edge furthest from its place: {pausedFits.Summary("px")}" : $"{paused.Count} wrong; first: {string.Join(" | ", paused.Take(3))}");

        // Playing through each move, and once more while the process is held up.
        (int First, int Last)[] moves = [(firstChange, firstChange + moveFrames - 1), (secondChange, secondChange + moveFrames - 1)];
        var plays = _quick ? 1 : 2;
        foreach (var heldUp in new[] { false, true })
        {
            var playedThrough = new List<PlayedScenes>();
            var numbers = NewTotals(holdUps);
            foreach (var move in moves)
            {
                for (var play = 0; play < plays; play++)
                {
                    playedThrough.Add(PlayAndRead(
                        preview,
                        layout,
                        move.First - 14,
                        move.Last + 14,
                        () => host.Do(e => e.Scrub(TestFolder.TimeOf(move.First - 14, 0.5))),
                        () => host.Space(),
                        () => host.Space(),
                        heldUp ? holdUps : null,
                        "scenes",
                        numbers));
                }
            }

            var label = heldUp ? $"playing through the moves with {holdUps.Name}" : "playing through the moves";
            JudgeMove(label, preview, playedThrough, moves, heldUp, numbers);
            if (heldUp)
            {
                _report.Note($"{label}: {holdUps.Describe()}");
            }
        }

        _report.Check("the session and the engine reported nothing wrong", Problems(host, preview).Length == 0, Problems(host, preview));
    }

    // ---------------------------------------------------------------------------------------
    // A zoom while playing
    // ---------------------------------------------------------------------------------------

    private void EditorPlaysThroughAZoom()
    {
        var plays = _options.Number("count", _quick ? 1 : 2);
        foreach (var kind in HoldUpKinds("none", "collector"))
        {
            using var holdUps = new HoldUps(kind, HoldUpMilliseconds, _options.Number("stall-gap", 0));
            _report.Section($"The editor session plays through a zoom that moves in, with {holdUps.Name}: played {plays} times");
            using var host = EditorHost.Open(holdUps.With(Muted), _media, TestMedia.Camera, Late);
            if (host.Preview is not { } preview)
            {
                _report.Check("LoadAsync makes the session ready, with the engine as its preview", false, $"state {host.Get(e => e.State)}: {host.Get(e => e.UnavailableMessage)}");
                continue;
            }

            // Zoom at the playhead, then its end, its place and the time it takes to move in.
            host.Do(e =>
            {
                e.Scrub(ZoomStart);
                if (e.AddZoomAtPlayhead().Index is { } zoom)
                {
                    e.SetZoomEnd(zoom, ZoomEnd);
                    e.SetZoomScale(zoom, 2);
                    e.SetZoomFocusPoint(zoom, ZoomFocusX, ZoomFocusY);
                    e.SetZoomEaseIn(zoom, ZoomEaseIn);
                    e.SetZoomEaseOut(zoom, 0.5);
                }
            });
            preview.WaitForIdle();
            var project = host.Get(e => e.Project!);
            preview.Follow(project);
            var made = project.Zooms is [{ Start: ZoomStart, End: ZoomEnd, Scale: 2, EaseIn: ZoomEaseIn, EaseOut: 0.5, Focus: { X: ZoomFocusX, Y: ZoomFocusY, Mode: StudioZoomFocusMode.Point } }];
            _report.Check(
                "AddZoomAtPlayhead and the zoom's edits make a zoom that takes three seconds to move in",
                made,
                string.Join("; ", project.Zooms.Select(z => $"{F(z.Start, "0.###")} to {F(z.End, "0.###")} s, x{F(z.Scale)}, on ({F(z.Focus.X, "0.##")}, {F(z.Focus.Y, "0.##")}), in over {F(z.EaseIn)} s")));
            if (!made)
            {
                continue;
            }

            var layout = new MovingLayout(project, preview.Folder.Screen, preview.Folder.Camera, 1280, 720);
            var played = new List<PlayedScenes>();
            var numbers = NewTotals(holdUps);
            for (var play = 0; play < plays; play++)
            {
                played.Add(PlayAndRead(
                    preview,
                    layout,
                    ZoomPlayedFrom,
                    ZoomPlayedTo,
                    () => host.Do(e => e.Scrub(TestFolder.TimeOf(ZoomPlayedFrom, 0.5))),
                    () => host.Space(),
                    () => host.Space(),
                    holdUps,
                    $"editor-zoom-{kind}",
                    numbers));
            }

            JudgeMove($"through the session, with {holdUps.Name}", preview, played, [(ZoomMovingFirst, ZoomMovingLast)], heldUp: kind != "none", numbers);
            _report.Note($"through the session, with {holdUps.Name}: {holdUps.Describe()}");
            _report.Check($"with {holdUps.Name}: the session and the engine reported nothing wrong", Problems(host, preview).Length == 0, Problems(host, preview));
        }
    }
}
