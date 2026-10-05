using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Capture;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 13, continued. A cut while the preview is paused inside it and while it plays over it, and an
// export from the window of a project with cuts and scenes, decoded frame by frame.
internal sealed partial class WindowChecks
{
    /// <summary>The seconds a time readout such as "0:04.5 / 0:11.0" says the playhead is at, or NaN.</summary>
    private static double ReadoutSeconds(string text)
    {
        var at = text.Split('/')[0].Trim().Split(':');
        return at.Length == 2
            && int.TryParse(at[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            && double.TryParse(at[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            ? (minutes * 60) + seconds
            : double.NaN;
    }

    private void PlayingOverACut()
    {
        Timeline.Mark("13: paused inside a cut");

        // One cut, from 4.0 to 5.0 s: the frames 120 to 149 are left out.
        const int CutFirst = 120;
        const int CutLast = 149;
        const int From = 90;
        const int Last = 185;
        var folder = NewScreenProject("Playing over a cut", p => OnLemon(p) with { Edits = p.Edits with { Cuts = [Cut(4, 5)] } });
        if (OpenReady(folder, "playing over a cut") is not { } editor)
        {
            return;
        }

        var project = editor.Expected;

        // Paused, the playhead can be anywhere, also inside the cut: the picture is then what is
        // cut out, and the time, which counts the video, stands still from the cut's start to its end.
        (double At, string Wanted)[] places = [(3.5, "0:03.5"), (4.0, "0:04.0"), (4.5, "0:04.0"), (MiddleOf(CutLast), "0:04.0"), (5.0, "0:04.0"), (5.5, "0:04.5")];
        var readouts = new List<string>();
        foreach (var (at, _) in places)
        {
            SetSlider(editor, "StudioPlayhead", at);
            readouts.Add(Until(() => TimeText(editor), text => text.StartsWith(places[readouts.Count].Wanted, StringComparison.Ordinal), 1));
        }

        SetSlider(editor, "StudioPlayhead", 4.5);
        var inside = LookForLayout(editor, 135, 4);
        var canCut = Find(editor, "StudioAddCutButton", 0.5)?.IsEnabled;
        _report.Check(
            "paused, the playhead can be put inside a cut: the preview then shows the frame that is cut out, and the time, which counts the video, stands still from where the cut starts to where it ends; Cut is enabled there, because it has that cut to select",
            readouts.Select((text, index) => text == $"{places[index].Wanted} / 0:11.0").All(ok => ok) && inside is not null && JudgeLayout(inside.Reading) is null && canCut == true,
            $"{string.Join(", ", places.Select((place, index) => $"at {Seconds(place.At)} s \"{readouts[index]}\""))}; at 4.5 s the preview shows {(inside is null ? "nothing that was pictured" : JudgeLayout(inside.Reading) ?? "frame 135")}; Cut enabled {canCut}");

        // Playing over it.
        Timeline.Mark("13: playing over a cut");
        SetSlider(editor, "StudioPlayhead", MiddleOf(From));
        var rest = LookForLayout(editor, From, 5);
        var engine = EngineOf(editor);
        if (rest is null || JudgeLayout(rest.Reading) is not null || engine is null)
        {
            _report.Check("the preview rests on the frame the playing starts from", false, rest is null ? "no screenshot" : engine is null ? "the window's preview is not the preview engine" : JudgeLayout(rest.Reading));
            CloseQuietly(editor);
            return;
        }

        // Every picture the engine draws, and every place the window reports for the playhead with the time it shows for it.
        var scenes = new List<SceneReading>();
        var edgesOff = new Dictionary<int, LayoutReading>();
        var lastScene = From;
        using var recorder = new SceneRecorder((shot, at) => scenes.Add(ReadDrawnScene(shot, editor.Folder, project, at, ref lastScene, edgesOff, ahead: 75)));
        var reported = new List<(long At, double Playhead, string Time)>(1024);
        var viewModel = OnUi(() => editor.Window.ViewModel);

        // Where the playhead is, for this thread to follow without asking the window while it plays.
        double[] playhead = [MiddleOf(From)];
        PropertyChangedEventHandler onRaised = (_, e) =>
        {
            if (e.PropertyName == nameof(StudioViewModel.PlayheadText))
            {
                reported.Add((Stopwatch.GetTimestamp(), viewModel.Playhead, viewModel.TimeText));
                Volatile.Write(ref playhead[0], viewModel.Playhead);
            }
        };

        // The tool keeps out of the preview's way while it plays: see ZoomPlaying.cs.
        byte[]? shotBuffer = null;
        editor.Camera.Take(ref shotBuffer);
        recorder.Reserve(rest.Canvas.Width, rest.Canvas.Height, 8);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        OnUi(() => viewModel.PropertyChanged += onRaised);
        var watch = Stopwatch.StartNew();
        double played;
        engine.AfterRender = recorder.OnRender;
        try
        {
            Invoke(editor, "StudioPlayPauseButton");
            Until(() => FrameOf(Volatile.Read(ref playhead[0])), frame => frame >= Last, 8, 10);
            Invoke(editor, "StudioPlayPauseButton");
            played = watch.Elapsed.TotalSeconds;
            Until(() => NameOf(editor, "StudioPlayPauseButton", 0), name => name == "Play", 2);
            engine.WaitForIdle(TimeSpan.FromSeconds(5));
        }
        finally
        {
            engine.AfterRender = null;
            recorder.Finish();
            OnUi(() => viewModel.PropertyChanged -= onRaised);
        }

        // The picture: what was drawn from ten frames after the start to five before the pause.
        var counted = scenes.SkipWhile(scene => scene.Frame < From + 10).TakeWhile(scene => scene.Frame <= Last - 5).ToList();
        var unreadable = counted.Count(scene => scene.Frame == FrameCode.Unreadable);
        var readable = counted.Where(scene => scene.Frame != FrameCode.Unreadable).ToList();
        var backwards = readable.Zip(readable.Skip(1), (a, b) => b.Frame < a.Frame).Count(wentBack => wentBack);
        var ofTheCut = readable.Where(scene => scene.Frame is >= CutFirst and <= CutLast).ToList();
        var lastBefore = readable.LastOrDefault(scene => scene.Frame < CutFirst);
        var firstAfter = readable.FirstOrDefault(scene => scene.Frame > CutLast);

        // At rest, where the playing was paused: looked at now, before anything else moves the playhead.
        var head = Playhead(editor);
        var parked = LookForLayout(editor, FrameOf(head), 3);
        var time = TimeText(editor);
        var again = LookAgainPaused(editor, edgesOff);
        var wrong = counted.Where(scene => scene.Problem is not null).Select(scene => scene.Problem!).Concat(again.Wrong).ToList();
        var overTheCut = lastBefore is not null && firstAfter is not null ? Stopwatch.GetElapsedTime(lastBefore.At, firstAfter.At).TotalMilliseconds : double.NaN;
        var before = PaceOf(scenes, From + 10, CutFirst - 1);
        var after = PaceOf(scenes, CutLast + 6, Last - 5);
        var cutFrames = ofTheCut.Select(scene => scene.Frame).Distinct().ToArray();

        // The session sends the preview on when it hears that the playhead has come to the cut,
        // which is after the first frame of the cut has been drawn. How many more are drawn
        // until the preview is there is the engine's and the session's; a third of a second of
        // them, or a picture that stands still for half a second, would be seen as a fault.
        _report.Check(
            "playing over a cut, the picture goes on after it: every frame before the cut is followed by frames after it, never by an earlier one, and what is drawn of the cut on the way is a few frames at most",
            recorder.Failure is null && recorder.Unread == 0 && wrong.Count == 0 && unreadable == 0 && backwards == 0 && lastBefore is not null && firstAfter is not null
                && cutFrames.Length <= 9 && overTheCut < 500 && readable.Count(scene => scene.Frame > CutLast) >= 20,
            (wrong.Count == 0 ? string.Empty : $"{wrong.Count} pictures are wrong: {string.Join(" | ", wrong.Take(3))}. ")
                + string.Create(CultureInfo.InvariantCulture, $"the last frame before the cut that was drawn is {lastBefore?.Frame.ToString(CultureInfo.InvariantCulture) ?? "none"} and the first after it {firstAfter?.Frame.ToString(CultureInfo.InvariantCulture) ?? "none"}, {overTheCut:0} ms later; ")
                + $"between them {ofTheCut.Count} picture(s) of the cut were drawn{(cutFrames.Length == 0 ? string.Empty : ", of the frames " + string.Join(", ", cutFrames))}; "
                + string.Create(CultureInfo.InvariantCulture, $"before the cut the longest wait between two frames was {before.LongestMs:0} ms with {before.Dropped} never drawn{before.Which}, and after it {after.LongestMs:0} ms with {after.Dropped} never drawn{after.Which}; ")
                + $"{unreadable} pictures did not read, {backwards} went back; {scenes.Count} pictures were drawn in {F(played, "0.0")} s"
                + (again.Seen.Length == 0 ? string.Empty : "; " + again.Seen)
                + (recorder.Failure is null ? string.Empty : "; reading a picture back failed: " + recorder.Failure));

        // The playhead and the time the window showed for it.
        var inTheCut = reported.Where(entry => entry.Playhead > 4.0 + 1e-6 && entry.Playhead < 5.0 - 1e-6).ToArray();
        var headBack = reported.Zip(reported.Skip(1), (a, b) => b.Playhead < a.Playhead - 1e-9).Count(wentBack => wentBack);
        var times = reported.Select(entry => ReadoutSeconds(entry.Time)).ToArray();
        var steps = times.Zip(times.Skip(1), (a, b) => b - a).ToArray();
        var unread = times.Count(double.IsNaN);
        var jump = reported.Zip(reported.Skip(1), (a, b) => (From: a.Playhead, To: b.Playhead)).FirstOrDefault(step => step.From < 4.0 + 1e-6 && step.To >= 5.0 - 1e-6);
        _report.Check(
            "while it plays over the cut the playhead goes from before the cut to its end in one step and never stands inside it, and the time that is shown counts on without a jump: it never goes back, and never on by more than two tenths of a second from one report to the next",
            reported.Count >= 40 && inTheCut.Length == 0 && headBack == 0 && unread == 0 && steps.Length > 0 && steps.Min() >= 0 && steps.Max() <= 0.2 + 1e-9 && jump.To >= 5.0 - 1e-6 && jump.From > 3.8,
            string.Create(CultureInfo.InvariantCulture, $"{reported.Count} reports of the playhead; over the cut it went from {jump.From:0.###} s to {jump.To:0.###} s; {inTheCut.Length} report(s) inside the cut; it went back {headBack} time(s); ")
                + string.Create(CultureInfo.InvariantCulture, $"the time shown went from \"{(reported.Count > 0 ? reported[0].Time : string.Empty)}\" to \"{(reported.Count > 0 ? reported[^1].Time : string.Empty)}\", its largest step {(steps.Length > 0 ? steps.Max() : double.NaN):0.0} s and its smallest {(steps.Length > 0 ? steps.Min() : double.NaN):0.0} s"));

        // At rest again, after the cut.
        _report.Check(
            "paused after the cut, the preview rests on the playhead's frame, and the time is a second less than the playhead's place in the recording",
            parked is not null && JudgeLayout(parked.Reading) is null && FrameOf(head) > CutLast && Math.Abs(ReadoutSeconds(time) - (head - 1)) <= 0.1 + 1e-6 && time.EndsWith("/ 0:11.0", StringComparison.Ordinal),
            parked is null ? "no screenshot" : $"playhead {Seconds(head)} s (frame {FrameOf(head)}), time \"{time}\": {JudgeLayout(parked.Reading) ?? "the picture shows that frame"}");

        // Play, pressed with the playhead inside the cut, starts where the cut ends.
        Timeline.Mark("13: Play inside a cut");
        SetSlider(editor, "StudioPlayhead", 4.5);
        Until(() => Playhead(editor), value => value == 4.5, 1);
        reported.Clear();
        Volatile.Write(ref playhead[0], 4.5);
        OnUi(() => viewModel.PropertyChanged += onRaised);
        Invoke(editor, "StudioPlayPauseButton");
        Until(() => Volatile.Read(ref playhead[0]), value => value >= 5.3, 5, 10);
        Invoke(editor, "StudioPlayPauseButton");
        Until(() => NameOf(editor, "StudioPlayPauseButton", 0), name => name == "Play", 2);
        engine.WaitForIdle(TimeSpan.FromSeconds(5));
        OnUi(() => viewModel.PropertyChanged -= onRaised);
        var afterPlay = reported.Select(entry => entry.Playhead).ToArray();
        var stopped = Playhead(editor);
        var atRest = LookForLayout(editor, FrameOf(stopped), 3);
        _report.Check(
            "Play pressed with the playhead inside a cut starts where the cut ends: the playhead is never reported between where it stood and the end of the cut, and it plays on from there",
            afterPlay.Length > 0 && afterPlay.All(value => value >= 5.0 - 1e-6) && stopped >= 5.3 && atRest is not null && JudgeLayout(atRest.Reading) is null,
            $"{afterPlay.Length} reports of the playhead, the first at {(afterPlay.Length > 0 ? Seconds(afterPlay[0]) : "?")} s and the earliest at {(afterPlay.Length > 0 ? Seconds(afterPlay.Min()) : "?")} s; paused at {Seconds(stopped)} s, where the preview shows {(atRest is null ? "nothing that was pictured" : JudgeLayout(atRest.Reading) ?? "the playhead's frame")}");
        CloseQuietly(editor);
    }

    /// <summary>
    /// A recording with a camera, three scenes and two cuts, exported from the window. The file
    /// is decoded: it has to hold the frames the cuts leave and no others, each laid out as the
    /// scene it is in has it at that frame's instant.
    /// </summary>
    private void ExportWithCutsAndScenes()
    {
        Timeline.Mark("13: an export of a project with cuts and scenes");

        // The video runs from 3.0 to 7.5 s. The second scene starts at 4.0 s, side by side, and
        // is entered by moving for a second; the third starts at 6.0 s, the camera alone, and is
        // cut to. The first cut, 3.5 to 4.5 s, takes out the line between the first two scenes
        // and the first half of the move. The second, 5.5 to 6.5 s, takes out the line between
        // the last two. Every one of these times is a whole number of frames.
        var folder = NewCameraProject("Export, cuts and scenes", p =>
        {
            p = ForScenePictures(p);
            var first = p.Scenes[0];
            return p with
            {
                Scenes =
                [
                    first,
                    first with { Start = 4, Layout = StudioLayout.SideBySide, Transition = new StudioTransition { Kind = StudioTransitionKind.Morph, Duration = 1 } },
                    first with { Start = 6, Layout = StudioLayout.Camera, Transition = new StudioTransition { Kind = StudioTransitionKind.Cut } },
                ],
                Edits = p.Edits with { TrimStart = 3, TrimEnd = 7.5, Cuts = [Cut(3.5, 4.5), Cut(5.5, 6.5)] },
            };
        });
        if (OpenReady(folder, "export, cuts and scenes") is not { } editor)
        {
            return;
        }

        // What is kept: 3.0 to 3.5 s in the bubble layout, 4.5 to 5.5 s of which the first half
        // second is still moving, and 6.5 to 7.5 s with the camera alone.
        int[] kept = [.. Enumerable.Range(90, 15), .. Enumerable.Range(135, 30), .. Enumerable.Range(195, 30)];
        var project = editor.Expected;
        var size = StudioExportLimits.GetExportSize(project);
        var (width, height) = ((int)size.Width, (int)size.Height);
        var time = TimeText(editor);
        var cuts = LaneText(editor, CutLane);
        var scenesLane = LaneText(editor, SceneLane);

        // Before the export: the first frame after the first cut, in the preview. The move is half done there.
        SetSlider(editor, "StudioPlayhead", MiddleOf(135));
        var preview = LookForLayout(editor, 135, 5);
        if (!_report.Check(
            "before the export the window shows what will be exported: two cuts, three scenes, a video of 2.5 seconds, and in the preview the first frame after the first cut with its layers half way from the bubble layout to side by side",
            time.EndsWith("/ 0:02.5", StringComparison.Ordinal) && cuts == $"{CutName(3.5, 4.5)} | {CutName(5.5, 6.5)}" && LaneItems(editor, SceneLane).Count == 3 && preview is not null && JudgeLayout(preview.Reading) is null,
            $"time \"{time}\"; cuts: {cuts}; scenes: {scenesLane}; the preview at frame 135: {(preview is null ? "no screenshot" : JudgeLayout(preview.Reading) ?? preview.Reading.ToString())}"))
        {
            CloseQuietly(editor);
            return;
        }

        Timeline.Mark("13: Export");
        var filesBefore = ExportFiles();
        var watch = Stopwatch.StartNew();
        var pressed = Invoke(editor, "StudioExportButton");
        var overlay = Find(editor, "StudioCancelExportButton", 3) is not null;
        WatchProgress(editor, _ => false, 120);
        var finished = Until(() => _services.Exports.FirstOrDefault(e => e.ProjectId == editor.Id), e => e is not null, 30, 30);
        var took = watch.Elapsed.TotalSeconds;
        var gone = Gone(editor, "StudioCancelExportButton", 5);
        var message = ErrorOf(editor);
        var files = Until(() => ExportFiles().Except(filesBefore).ToArray(), left => left.Length == 1, 3, 50);
        if (!_report.Check(
            "the export of a project with cuts and scenes runs to its end, and the video is the one new file in the export folder",
            pressed && overlay && gone && finished is not null && message.Length == 0 && files.Length == 1,
            $"{F(took, "0.0")} s for 2.5 s of video; reported: {(finished is null ? "nothing" : Path.GetFileName(finished.Path))}; new files: {string.Join(", ", files.Select(Path.GetFileName))}{(message.Length > 0 ? $"; the window says \"{message}\"" : string.Empty)}") || finished is null)
        {
            CloseQuietly(editor);
            return;
        }

        CutFrames(editor, finished.Path, width, height, kept);
        CloseQuietly(editor);
    }

    // A frame of an exported video has been through an encoder twice, once as the recording and
    // once as the video, and each keeps the colours for every other pixel only. Where an edge is
    // read from its colours it is found half a pixel less surely than in the preview. A layer
    // that is laid out as the frame before or after it in a move is several pixels off.
    private const double EncodedAgain = 0.5;

    /// <summary>What the exported file is, by ffprobe, and what each of its frames shows, decoded by ffmpeg.</summary>
    private void CutFrames(Editor editor, string path, int width, int height, int[] kept)
    {
        Timeline.Mark("13: decoding the exported video");
        string stream;
        string colors;
        try
        {
            stream = RunTool("ffprobe", "-v", "error", "-select_streams", "v:0", "-count_packets", "-show_entries", "stream=codec_name,width,height,r_frame_rate,nb_read_packets", "-of", "csv=p=0", path).Trim();
            colors = RunTool("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=pix_fmt,color_range,color_space", "-of", "csv=p=0", path).Trim();
        }
        catch (Exception ex)
        {
            _report.Check("the exported video can be read by ffprobe", false, ex.Message);
            return;
        }

        var wanted = string.Create(CultureInfo.InvariantCulture, $"h264,{width},{height},{Fps}/1,{kept.Length}");
        _report.Check(
            "the file has one frame for each frame the cuts leave of the trimmed range: 75, where the range without cuts has 135",
            stream == wanted,
            $"ffprobe: {stream} (expected {wanted}); {colors}");

        // The sound is not listened to. What the file says about it is reported, not judged.
        try
        {
            var audio = RunTool("ffprobe", "-v", "error", "-select_streams", "a", "-show_entries", "stream=codec_name,channels,duration", "-of", "csv=p=0", path).Trim();
            _report.Note($"the file's audio, by ffprobe (codec, channels, seconds): {(audio.Length == 0 ? "none" : audio.ReplaceLineEndings("; "))}; the video is 2.5 s long. Whether the sound has the same stretches cut out was not listened to");
        }
        catch (Exception ex)
        {
            _report.Note($"the file's audio could not be read: {ex.Message}");
        }

        // Every frame against the layout the format gives for the frame of the recording it has to be.
        var project = editor.Expected;
        var canvas = new Box(0, 0, width, height);
        var wrong = new List<string>();
        var worst = new Dictionary<string, (int Frames, double Worst)>();
        var read = 0;
        var filter = colors.Contains("bt709", StringComparison.OrdinalIgnoreCase) ? "format=bgra" : "scale=in_color_matrix=bt709,format=bgra";
        try
        {
            foreach (var frame in DecodedFrames(path, width, height, filter))
            {
                if (read < kept.Length)
                {
                    var source = kept[read];
                    var shot = new Shot(frame, width, height, 0, 0, 0);
                    var reading = ReadLayout(shot, canvas, editor.Folder, project, source);
                    var problem = JudgeLayout(reading, EncodedAgain);
                    if (problem is not null)
                    {
                        if (wrong.Count < 5)
                        {
                            wrong.Add($"frame {read} of the video, which has to be frame {source} of the recording: {problem}");
                        }
                    }
                    else
                    {
                        // The four stretches: before the first cut, still moving after it, at rest, and after the second cut.
                        var part = source < 135 ? "the bubble layout" : source < 150 ? "moving" : source < 195 ? "side by side" : "the camera alone";
                        var (frames, furthest) = worst.GetValueOrDefault(part, (0, 0.0));
                        worst[part] = (frames + 1, Math.Max(furthest, reading.Worst));
                    }
                }

                read++;
            }
        }
        catch (Exception ex)
        {
            _report.Check("the exported video can be decoded by ffmpeg", false, ex.Message);
            return;
        }

        string Told(string name) => worst.TryGetValue(name, out var found) ? string.Create(CultureInfo.InvariantCulture, $"{found.Frames} frames, the edge furthest from its place {found.Worst:0.00} px from it") : "no frame read";
        _report.Check(
            "decoded, the video holds the frames the cuts leave and no others, each laid out as its scene has it: frames 90 to 104 of the recording in the bubble layout, then 135 to 149 with the layers on the second half of their way, 150 to 164 side by side, and 195 to 224 with the camera alone, each layer showing its frame where the format puts it, to a pixel and a quarter",
            read == kept.Length && wrong.Count == 0 && worst.Values.Sum(part => part.Frames) == kept.Length,
            wrong.Count > 0
                ? $"{read} frames decoded; {string.Join(" | ", wrong)}"
                : $"{read} frames decoded; in the bubble layout: {Told("the bubble layout")}; moving: {Told("moving")}; side by side: {Told("side by side")}; the camera alone: {Told("the camera alone")}");
    }
}
