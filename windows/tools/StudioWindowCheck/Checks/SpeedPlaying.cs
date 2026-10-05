using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Capture;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 14, continued. Speed changes while the preview is paused inside one and while it plays through
// them: what the time shows, what the editor asks the preview to play at and when, and how fast
// the picture and the playhead then move. And an export from the window of a project with speed
// changes, decoded frame by frame, with its sound.
internal sealed partial class WindowChecks
{
    /// <summary>
    /// Whether the preview plays a stretch at the rate it is asked for. The preview engine does
    /// not yet: it takes the request and plays on at the recording's own speed. Until it does,
    /// how fast the picture and the playhead move inside a speed change is measured and written
    /// into the report as a note, and no check depends on it. Once the engine plays the rate,
    /// have this return true: the same measurements are then a check, held against the rate.
    /// </summary>
    private static bool PreviewPlaysTheRate => false;

    // How many frames after the first frame of a stretch the request for its rate may come. The
    // editor hears of every frame a playing preview shows on its own thread, a moment after the
    // preview has shown it, and asks then.
    private const int RateRequestFrames = 6;

    /// <summary>
    /// How fast the pictures went on between two frames of the recording, in seconds of the
    /// recording for each second that passed, from the first time each frame was drawn. Not a
    /// number when fewer than two frames of the range were drawn.
    /// </summary>
    private static double PicturePace(IEnumerable<SceneReading> scenes, int first, int last)
    {
        var drawn = new SortedDictionary<int, long>();
        foreach (var scene in scenes.Where(scene => scene.Frame >= first && scene.Frame <= last))
        {
            drawn.TryAdd(scene.Frame, scene.At);
        }

        if (drawn.Count < 2)
        {
            return double.NaN;
        }

        var (from, to) = (drawn.First(), drawn.Last());
        var seconds = Stopwatch.GetElapsedTime(from.Value, to.Value).TotalSeconds;
        return seconds > 0 ? (to.Key - from.Key) / (double)Fps / seconds : double.NaN;
    }

    /// <summary>How fast the playhead the window reported went on between two places in the recording, in seconds of the recording for each second that passed.</summary>
    private static double PlayheadPace(IReadOnlyList<(long At, double Playhead, string Time)> reported, double from, double to)
    {
        var inside = reported.Where(entry => entry.Playhead >= from && entry.Playhead <= to).ToArray();
        if (inside.Length < 2)
        {
            return double.NaN;
        }

        var seconds = Stopwatch.GetElapsedTime(inside[0].At, inside[^1].At).TotalSeconds;
        return seconds > 0 ? (inside[^1].Playhead - inside[0].Playhead) / seconds : double.NaN;
    }

    private static string RatesAsked(PlaybackRateRequest[] requests) =>
        requests.Length == 0
            ? "no request"
            : string.Join(", ", requests.Select(request => string.Create(CultureInfo.InvariantCulture, $"{request.Rate:0.##}× with the preview at {request.Position:0.###} s, {(request.WasPlaying ? "playing" : "paused")}")));

    private void PlayingOverSpeedChanges()
    {
        Timeline.Mark("14: paused inside a speed change");

        // Twice as fast from 4.0 to 6.0 s, the frames 120 to 179; a cut from 7.0 to 8.0 s; and
        // half as fast from 8.0 to 9.0 s, the frames 240 to 269, right where the cut ends.
        const int FasterFirst = 120;
        const int FasterLast = 179;
        const int From = 105;
        const int Last = 195;
        var folder = NewScreenProject("Playing over speed changes", p => OnLemon(p) with { Edits = p.Edits with { Cuts = [Cut(7, 8)], Speed = [Speed(4, 6), Speed(8, 9, 0.5)] } });

        // What this window's editor asks its preview to play at is written down on its way there.
        var rates = _services.PlaybackRates;
        rates.Watch(folder.Paths.ProjectId);
        if (OpenReady(folder, "playing over speed changes") is not { } editor)
        {
            return;
        }

        var project = editor.Expected;
        var id = editor.Id;

        // The video's time at a place in the recording, worked out by hand: the two seconds from
        // 4.0 s take one, the second from 7.0 s is cut out, and the second from 8.0 s takes two.
        static double VideoTime(double source) =>
            source < 4 ? source
            : source < 6 ? 4 + ((source - 4) / 2)
            : source < 7 ? 5 + (source - 6)
            : source < 8 ? 6
            : source < 9 ? 6 + ((source - 8) * 2)
            : 8 + (source - 9);

        // Paused, the playhead can be anywhere. The time counts the video: half as fast as the
        // recording inside the faster stretch, not at all inside the cut, and twice as fast inside
        // the slower stretch.
        (double At, string Wanted)[] places =
        [
            (3.5, "0:03.5"), (4.0, "0:04.0"), (5.0, "0:04.5"), (5.5, "0:04.7"), (6.0, "0:05.0"), (6.5, "0:05.5"),
            (7.5, "0:06.0"), (8.25, "0:06.5"), (8.5, "0:07.0"), (9.0, "0:08.0"), (10.5, "0:09.5"),
        ];
        var readouts = new List<string>();
        foreach (var (at, _) in places)
        {
            SetSlider(editor, "StudioPlayhead", at);
            readouts.Add(Until(() => TimeText(editor), text => text.StartsWith(places[readouts.Count].Wanted, StringComparison.Ordinal), 1));
        }

        SetSlider(editor, "StudioPlayhead", MiddleOf(150));
        var inside = LookForLayout(editor, 150, 4);
        var whilePaused = rates.Since(id);
        var isWatched = OnUi(() => editor.Window.ViewModel.Preview is WatchedPreview);
        var byHand = places.All(place => StudioEditorModel.FormatTime(VideoTime(place.At)) == place.Wanted);
        _report.Check(
            "paused, the time counts the video's time through the speed changes: it goes half as fast as the playhead inside the stretch at twice the speed, stands still inside the cut, and goes twice as fast inside the stretch at half speed; the preview shows the playhead's frame inside a stretch, and is asked for no rate while it is paused",
            byHand && readouts.Select((text, index) => text == $"{places[index].Wanted} / 0:11.0").All(ok => ok) && inside is not null && JudgeLayout(inside.Reading) is null && whilePaused.Length == 0 && isWatched,
            $"{string.Join(", ", places.Select((place, index) => $"at {Seconds(place.At)} s \"{readouts[index]}\""))}; at 5.0 s the preview shows {(inside is null ? "nothing that was pictured" : JudgeLayout(inside.Reading) ?? "frame 150")}; asked while paused: {RatesAsked(whilePaused)}{(isWatched ? string.Empty : "; the tool does not stand in front of this window's preview, so it would not have seen a request")}");

        // Playing into the faster stretch and out of it.
        Timeline.Mark("14: playing through a speed change");
        SetSlider(editor, "StudioPlayhead", MiddleOf(From));
        var rest = LookForLayout(editor, From, 5);
        var engine = EngineOf(editor);
        if (rest is null || JudgeLayout(rest.Reading) is not null || engine is null)
        {
            _report.Check("the preview rests on the frame the playing starts from", false, rest is null ? "no screenshot" : engine is null ? "the window's preview is not the preview engine, or one the tool watches" : JudgeLayout(rest.Reading));
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
        var mark = rates.Count(id);
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

        var asked = rates.Since(id, mark);

        // What the editor asked for: twice the speed once the preview had come to the stretch, and the recording's own once it had left it.
        bool Came(PlaybackRateRequest request, double rate, double start) =>
            request.Rate == rate && request.WasPlaying && request.Position >= start - 1e-6 && FrameOf(request.Position) - FrameOf(start) <= RateRequestFrames;
        var firstOfTheStretch = scenes.FirstOrDefault(scene => scene.Frame >= FasterFirst);
        var afterTheFrame = asked.Length > 0 && firstOfTheStretch is not null ? Stopwatch.GetElapsedTime(firstOfTheStretch.At, asked[0].At).TotalMilliseconds : double.NaN;
        _report.Check(
            "playing into a stretch at twice the speed and out of it, the editor asks the preview for twice the speed when the playhead has come to the stretch, and for the recording's own speed when it has left it: each once, with the preview no more than six frames past the edge, and nothing before or between",
            asked.Length == 2 && Came(asked[0], 2, 4.0) && Came(asked[1], 1, 6.0),
            $"asked: {RatesAsked(asked)}"
                + (asked.Length > 0 ? string.Create(CultureInfo.InvariantCulture, $"; the first came {FrameOf(asked[0].Position) - FasterFirst} frame(s) into the stretch, {afterTheFrame:0} ms after the first picture of the stretch was drawn") : string.Empty)
                + (asked.Length > 1 ? string.Create(CultureInfo.InvariantCulture, $"; the second {FrameOf(asked[1].Position) - (FasterLast + 1)} frame(s) after the stretch") : string.Empty));

        // The picture: what was drawn from ten frames after the start to five before the pause.
        var counted = scenes.SkipWhile(scene => scene.Frame < From + 10).TakeWhile(scene => scene.Frame <= Last - 5).ToList();
        var unreadable = counted.Count(scene => scene.Frame == FrameCode.Unreadable);
        var readable = counted.Where(scene => scene.Frame != FrameCode.Unreadable).ToList();
        var backwards = readable.Zip(readable.Skip(1), (a, b) => b.Frame < a.Frame).Count(wentBack => wentBack);
        var ofTheStretch = readable.Where(scene => scene.Frame is >= FasterFirst and <= FasterLast).Select(scene => scene.Frame).Distinct().Count();
        var afterTheStretch = readable.Where(scene => scene.Frame > FasterLast).Select(scene => scene.Frame).Distinct().Count();

        // At rest, where the playing was paused: looked at now, before anything else moves the playhead.
        var head = Playhead(editor);
        var parked = LookForLayout(editor, FrameOf(head), 3);
        var time = TimeText(editor);
        var again = LookAgainPaused(editor, edgesOff);
        var wrong = counted.Where(scene => scene.Problem is not null).Select(scene => scene.Problem!).Concat(again.Wrong).ToList();
        _report.Check(
            "playing through the stretch, every picture the preview draws is a frame of the recording, laid out as it should be, and never an earlier one than the picture before: the picture goes on through the stretch and after it",
            recorder.Failure is null && recorder.Unread == 0 && wrong.Count == 0 && unreadable == 0 && backwards == 0 && ofTheStretch >= 20 && afterTheStretch >= 8,
            (wrong.Count == 0 ? string.Empty : $"{wrong.Count} pictures are wrong: {string.Join(" | ", wrong.Take(3))}. ")
                + $"{ofTheStretch} of the 60 frames of the stretch were drawn and {afterTheStretch} of the 11 after it that count; {unreadable} pictures did not read, {backwards} went back; {scenes.Count} pictures were drawn in {F(played, "0.0")} s"
                + (again.Seen.Length == 0 ? string.Empty : "; " + again.Seen)
                + (recorder.Failure is null ? string.Empty : "; reading a picture back failed: " + recorder.Failure));

        // The playhead and the time the window showed for it.
        var inTheStretch = reported.Count(entry => entry.Playhead > 4.0 + 1e-6 && entry.Playhead < 6.0 - 1e-6);
        var headBack = reported.Zip(reported.Skip(1), (a, b) => b.Playhead < a.Playhead - 1e-9).Count(wentBack => wentBack);
        var times = reported.Select(entry => ReadoutSeconds(entry.Time)).ToArray();
        var steps = times.Zip(times.Skip(1), (a, b) => b - a).ToArray();
        var unread = times.Count(double.IsNaN);
        var offTheVideo = reported.Count(entry => Math.Abs(ReadoutSeconds(entry.Time) - VideoTime(entry.Playhead)) > 0.1 + 1e-6);
        _report.Check(
            "while it plays through the stretch the playhead goes through it and never back, and the time that is shown is the video's time at the playhead: it never goes back, never on by more than two tenths of a second from one report to the next, and after the pause it is the video's time of the frame the preview rests on",
            reported.Count >= 40 && inTheStretch >= 10 && headBack == 0 && unread == 0 && offTheVideo == 0 && steps.Length > 0 && steps.Min() >= 0 && steps.Max() <= 0.2 + 1e-9
                && parked is not null && JudgeLayout(parked.Reading) is null && FrameOf(head) > FasterLast && Math.Abs(ReadoutSeconds(time) - VideoTime(head)) <= 0.1 + 1e-6 && time.EndsWith("/ 0:11.0", StringComparison.Ordinal),
            string.Create(CultureInfo.InvariantCulture, $"{reported.Count} reports of the playhead, {inTheStretch} of them inside the stretch; it went back {headBack} time(s); {offTheVideo} report(s) showed another time than the video's at the playhead; ")
                + string.Create(CultureInfo.InvariantCulture, $"the time shown went from \"{(reported.Count > 0 ? reported[0].Time : string.Empty)}\" to \"{(reported.Count > 0 ? reported[^1].Time : string.Empty)}\", its largest step {(steps.Length > 0 ? steps.Max() : double.NaN):0.0} s and its smallest {(steps.Length > 0 ? steps.Min() : double.NaN):0.0} s; ")
                + (parked is null ? "paused: no screenshot" : $"paused at {Seconds(head)} s (frame {FrameOf(head)}), time \"{time}\": {JudgeLayout(parked.Reading) ?? "the picture shows that frame"}"));

        // How fast it went: inside the stretch, clear of its edges, and after it.
        var pace = (
            PictureInside: PicturePace(scenes, FasterFirst + RateRequestFrames, FasterLast - RateRequestFrames),
            PictureAfter: PicturePace(scenes, FasterLast + 1 + RateRequestFrames, Last - 2),
            PlayheadInside: PlayheadPace(reported, MiddleOf(FasterFirst + RateRequestFrames), MiddleOf(FasterLast - RateRequestFrames)),
            PlayheadAfter: PlayheadPace(reported, MiddleOf(FasterLast + 1 + RateRequestFrames), MiddleOf(Last - 2)));
        var measured = string.Create(
            CultureInfo.InvariantCulture,
            $"inside the stretch at twice the speed the picture went on {pace.PictureInside:0.00} seconds of the recording a second and the playhead {pace.PlayheadInside:0.00}; after it the picture {pace.PictureAfter:0.00} and the playhead {pace.PlayheadAfter:0.00}; the {Last - From} frames from 3.5 s to 6.5 s, which are 2.0 s of video, took {played:0.0} s to play");
        if (PreviewPlaysTheRate)
        {
            static bool Within(double value, double wanted) => Math.Abs((value / wanted) - 1) <= 0.2;
            _report.Check(
                "inside the stretch the picture and the playhead go on twice as fast as the recording, and after it at the recording's own speed, each within a fifth",
                Within(pace.PictureInside, 2) && Within(pace.PlayheadInside, 2) && Within(pace.PictureAfter, 1) && Within(pace.PlayheadAfter, 1),
                measured);
        }
        else
        {
            _report.Note($"not judged, because the preview engine does not play a rate yet (PreviewPlaysTheRate in SpeedPlaying.cs): {measured}");
        }

        // A short stretch of playing, without the pictures: from a place, until the playhead has come to another.
        (PlaybackRateRequest[] Asked, (long At, double Playhead, string Time)[] Reported, double Stopped) Play(double from, double until)
        {
            SetSlider(editor, "StudioPlayhead", from);
            Until(() => Playhead(editor), value => Same(value, from), 1);
            reported.Clear();
            Volatile.Write(ref playhead[0], from);
            var before = rates.Count(id);
            OnUi(() => viewModel.PropertyChanged += onRaised);
            try
            {
                Invoke(editor, "StudioPlayPauseButton");
                Until(() => Volatile.Read(ref playhead[0]), value => value >= until, 8, 10);
                Invoke(editor, "StudioPlayPauseButton");
                Until(() => NameOf(editor, "StudioPlayPauseButton", 0), name => name == "Play", 2);
                engine.WaitForIdle(TimeSpan.FromSeconds(5));
            }
            finally
            {
                OnUi(() => viewModel.PropertyChanged -= onRaised);
            }

            return (rates.Since(id, before), [.. reported], Playhead(editor));
        }

        // Over the cut into the stretch at half speed, which starts where the cut ends.
        Timeline.Mark("14: playing over a cut into a speed change");
        var overCut = Play(MiddleOf(198), 9.3);
        var inTheCut = overCut.Reported.Count(entry => entry.Playhead > 7.0 + 1e-6 && entry.Playhead < 8.0 - 1e-6);
        var cutTimes = overCut.Reported.Select(entry => ReadoutSeconds(entry.Time)).ToArray();
        var cutSteps = cutTimes.Zip(cutTimes.Skip(1), (a, b) => b - a).ToArray();
        var slowerInside = PlayheadPace(overCut.Reported, 8.0 + (RateRequestFrames / (double)Fps), 9.0 - (2.0 / Fps));
        _report.Check(
            "playing over a cut into a stretch at half speed that starts where the cut ends, the editor asks the preview for half speed as it sends it on over the cut, with the preview at the cut's end, and for the recording's own speed once the stretch is left; the playhead is never reported inside the cut, and the time shown never goes back or on by more than three tenths of a second, which four frames at half speed come to",
            overCut.Asked.Length == 2 && overCut.Asked[0].Rate == 0.5 && overCut.Asked[0].WasPlaying && Same(overCut.Asked[0].Position, 8.0, 1.0 / Fps) && Came(overCut.Asked[1], 1, 9.0)
                && overCut.Stopped >= 9.3 && inTheCut == 0 && cutSteps.Length > 0 && cutSteps.Min() >= 0 && cutSteps.Max() <= 0.3 + 1e-9,
            $"asked: {RatesAsked(overCut.Asked)}; {overCut.Reported.Length} reports of the playhead, {inTheCut} inside the cut; "
                + string.Create(CultureInfo.InvariantCulture, $"the time's largest step {(cutSteps.Length > 0 ? cutSteps.Max() : double.NaN):0.0} s and its smallest {(cutSteps.Length > 0 ? cutSteps.Min() : double.NaN):0.0} s; paused at {overCut.Stopped:0.###} s"));
        if (PreviewPlaysTheRate)
        {
            _report.Check(
                "inside the stretch at half speed the playhead goes on half as fast as the recording, within a fifth",
                Math.Abs((slowerInside / 0.5) - 1) <= 0.2,
                string.Create(CultureInfo.InvariantCulture, $"the playhead went on {slowerInside:0.00} seconds of the recording a second"));
        }
        else
        {
            _report.Note(string.Create(CultureInfo.InvariantCulture, $"not judged, because the preview engine does not play a rate yet: inside the stretch at half speed the playhead went on {slowerInside:0.00} seconds of the recording a second"));
        }

        // Play, pressed inside a stretch: the preview is told the rate before it plays.
        Timeline.Mark("14: Play inside a speed change");
        var insideSlower = Play(8.5, 9.3);
        _report.Check(
            "Play pressed with the playhead inside the stretch at half speed asks the preview for half speed before it plays, and for the recording's own speed once the stretch is left",
            insideSlower.Asked.Length == 2 && insideSlower.Asked[0].Rate == 0.5 && !insideSlower.Asked[0].WasPlaying && Same(insideSlower.Asked[0].Position, 8.5, 1.0 / Fps) && Came(insideSlower.Asked[1], 1, 9.0) && insideSlower.Stopped >= 9.3,
            $"asked: {RatesAsked(insideSlower.Asked)}; paused at {Seconds(insideSlower.Stopped)} s");

        // Paused inside the faster stretch, the preview keeps the rate it was told. Started again outside it, it is told the recording's own first.
        var insideFaster = Play(4.5, 4.7);
        var outside = Play(2.0, 2.3);
        _report.Check(
            "Play pressed inside the stretch at twice the speed asks for twice the speed before the preview plays, and nothing more while it stays inside; paused there and started again outside the stretch, the preview is asked for the recording's own speed before it plays",
            insideFaster.Asked.Length == 1 && insideFaster.Asked[0].Rate == 2 && !insideFaster.Asked[0].WasPlaying && Same(insideFaster.Asked[0].Position, 4.5, 1.0 / Fps) && insideFaster.Stopped is >= 4.7 and < 6.0
                && outside.Asked.Length == 1 && outside.Asked[0].Rate == 1 && !outside.Asked[0].WasPlaying && Same(outside.Asked[0].Position, 2.0, 1.0 / Fps) && outside.Stopped is >= 2.3 and < 4.0,
            $"from 4.5 s: {RatesAsked(insideFaster.Asked)}, paused at {Seconds(insideFaster.Stopped)} s; from 2.0 s: {RatesAsked(outside.Asked)}, paused at {Seconds(outside.Stopped)} s");
        CloseQuietly(editor);
    }

    /// <summary>
    /// A recording with a camera, a trim, a cut and two speed changes, exported from the window.
    /// The file is decoded: it has to hold as many frames as the speed changes leave, each
    /// showing the frame of the recording the format gives for it, and its sound has to be
    /// silent where the video plays at another speed.
    /// </summary>
    private void ExportWithSpeedChanges()
    {
        Timeline.Mark("14: an export of a project with speed changes");

        // Every time below is in 120ths of a second, a quarter of a frame, so that the place in
        // the recording an output frame shows is never on the line between two frames. The video
        // runs from 120 to 1080 (1.0 to 9.0 s). It is twice as fast from 239 to 479, cut from
        // 600 to 720 (5.0 to 6.0 s), and half as fast from 719 to 839, of which the cut leaves
        // 720 to 839: the video goes on at half speed right after the cut.
        var folder = NewCameraProject("Export, speed changes", p => ForScenePictures(p) with
        {
            Edits = new StudioEdits { TrimStart = 1, TrimEnd = 9, Cuts = [Cut(5, 6)], Speed = [Speed(239 / 120.0, 479 / 120.0), Speed(719 / 120.0, 839 / 120.0, 0.5)] },
        });
        if (OpenReady(folder, "export, speed changes") is not { } editor)
        {
            return;
        }

        // The pieces, and where each begins in the video:
        //   120 to 239 at 1×, 119 long          0
        //   239 to 479 at 2×, 120 long          119
        //   479 to 600 at 1×, 121 long          239
        //   720 to 839 at 0.5×, 238 long        360
        //   839 to 1080 at 1×, 241 long         598, ending at 839: 6.99 s, which is 209 whole frames.
        // The middle of output frame i is 4i + 2, and a frame of the recording is 4 long:
        //   i = 0 to 29      (4i + 2 < 119): the recording at 120 + 4i + 2, frame i + 30.5, so i + 30
        //   i = 30 to 59     (to 239): at 239 + 2 × (4i + 2 − 119) = 8i + 5, frame 2i + 1.25, so 2i + 1: every other frame
        //   i = 60 to 89     (to 360): at 479 + (4i + 2 − 239) = 4i + 242, frame i + 60.5, so i + 60
        //   i = 90 to 148    (to 598): at 720 + (4i + 2 − 360) / 2 = 2i + 541, frame i / 2 + 135.25, so 135 + i / 2 rounded down: every frame twice
        //   i = 149 to 208:  at 839 + (4i + 2 − 598) = 4i + 243, frame i + 60.75, so i + 60
        int[] kept =
        [
            .. Enumerable.Range(0, 30).Select(i => i + 30),
            .. Enumerable.Range(30, 30).Select(i => (2 * i) + 1),
            .. Enumerable.Range(60, 30).Select(i => i + 60),
            .. Enumerable.Range(90, 59).Select(i => 135 + (i / 2)),
            .. Enumerable.Range(149, 60).Select(i => i + 60),
        ];
        var project = editor.Expected;
        var size = StudioExportLimits.GetExportSize(project);
        var (width, height) = ((int)size.Width, (int)size.Height);
        var time = TimeText(editor);
        var speeds = LaneText(editor, SpeedLane);
        var cuts = LaneText(editor, CutLane);

        // Before the export: a frame between the faster stretch and the cut, in the preview.
        SetSlider(editor, "StudioPlayhead", MiddleOf(135));
        var preview = LookForLayout(editor, 135, 5);
        if (!_report.Check(
            "before the export the window shows what will be exported: two speed changes, a cut, a video of 6.9 seconds where the trimmed range is 8.0, and in the preview the playhead's frame",
            kept.Length == 209 && time.EndsWith("/ 0:06.9", StringComparison.Ordinal) && speeds == "Speed 2×, 2.0 to 4.0 seconds | Speed 0.5×, 6.0 to 7.0 seconds" && cuts == CutName(5, 6) && preview is not null && JudgeLayout(preview.Reading) is null,
            $"time \"{time}\"; speed changes: {speeds}; cuts: {cuts}; the preview at frame 135: {(preview is null ? "no screenshot" : JudgeLayout(preview.Reading) ?? preview.Reading.ToString())}"))
        {
            CloseQuietly(editor);
            return;
        }

        Timeline.Mark("14: Export");
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
            "the export of a project with speed changes runs to its end, and the video is the one new file in the export folder",
            pressed && overlay && gone && finished is not null && message.Length == 0 && files.Length == 1,
            $"{F(took, "0.0")} s for 7.0 s of video; reported: {(finished is null ? "nothing" : Path.GetFileName(finished.Path))}; new files: {string.Join(", ", files.Select(Path.GetFileName))}{(message.Length > 0 ? $"; the window says \"{message}\"" : string.Empty)}") || finished is null)
        {
            CloseQuietly(editor);
            return;
        }

        SpeedFrames(editor, finished.Path, width, height, kept);
        SpeedSound(finished.Path, kept.Length);
        CloseQuietly(editor);
    }

    /// <summary>What the exported file is, by ffprobe, and what each of its frames shows, decoded by ffmpeg.</summary>
    private void SpeedFrames(Editor editor, string path, int width, int height, int[] kept)
    {
        Timeline.Mark("14: decoding the exported video");
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
            "the file has as many frames as the speed changes leave: 209, where the trimmed range has 240 frames of the recording, and 210 once the cut is out",
            stream == wanted,
            $"ffprobe: {stream} (expected {wanted}); {colors}");

        // Every frame against the layout of the frame of the recording it has to be. The project
        // has one scene and no zoom, so the layout is the same for every frame, and a frame of
        // the video is right when both layers show the frame they must.
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
                            wrong.Add($"frame {read} of the video, which has to be frame {source} of the recording{(reading.Screen is { } screen ? $" and whose strip reads {screen.Strip}" : string.Empty)}: {problem}");
                        }
                    }
                    else
                    {
                        // The five stretches of the video.
                        var part = read < 30 ? "before" : read < 60 ? "twice as fast" : read < 90 ? "between" : read < 149 ? "half as fast" : "after";
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

        string Seen(string name) => worst.TryGetValue(name, out var found) ? string.Create(CultureInfo.InvariantCulture, $"{found.Frames} frames, the edge furthest from its place {found.Worst:0.00} px from it") : "no frame read";
        _report.Check(
            "decoded, each frame of the video shows the frame of the recording the format gives for it: frames 30 to 59 one by one, then every other frame from 61 to 119 where the video is twice as fast, 120 to 149 one by one, after the cut every frame from 180 to 208 twice and 209 once where it is half as fast, and 209 to 268 one by one, with the camera's frame that belongs to each",
            read == kept.Length && wrong.Count == 0 && worst.Values.Sum(part => part.Frames) == kept.Length,
            wrong.Count > 0
                ? $"{read} frames decoded; {string.Join(" | ", wrong)}"
                : $"{read} frames decoded; before: {Seen("before")}; twice as fast: {Seen("twice as fast")}; between: {Seen("between")}; half as fast: {Seen("half as fast")}; after: {Seen("after")}");
    }

    /// <summary>The sound of a video as one channel of 16-bit samples at 48 kHz, decoded by ffmpeg.</summary>
    private static short[] DecodedSound(string path)
    {
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-v", "error", "-nostdin", "-i", path, "-map", "0:a:0", "-ac", "1", "-ar", "48000", "-f", "s16le", "-" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg could not be started.");
        var errors = process.StandardError.ReadToEndAsync();
        using var bytes = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(bytes);
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("ffmpeg did not finish decoding the sound.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg did not decode the sound: {errors.Result.Trim()}");
        }

        var raw = bytes.ToArray();
        var samples = new short[raw.Length / 2];
        Buffer.BlockCopy(raw, 0, samples, 0, samples.Length * 2);
        return samples;
    }

    /// <summary>
    /// The sound of the exported video. The recording has a tone of a twentieth of a second at
    /// the start of every second and silence between them. The video has to have the tones of
    /// the seconds it plays at the recording's own speed, where it plays them, and none where it
    /// plays at another speed: a stretch at another speed is silent.
    /// </summary>
    /// <remarks>
    /// A stretch whose sound was kept, at whatever speed, would have a tone in it: that of
    /// second 3 in the faster stretch, and that of second 6 at the start of the slower one. A
    /// tone of that length raises the level of its stretch to about 2,000 of 32,767, where
    /// silence may be 200. The sound is not listened to: its samples are read.
    /// </remarks>
    private void SpeedSound(string path, int frames)
    {
        Timeline.Mark("14: the sound of the exported video");
        short[] sound;
        try
        {
            sound = DecodedSound(path);
        }
        catch (Exception ex)
        {
            _report.Check("the exported video's sound can be decoded by ffmpeg", false, ex.Message);
            return;
        }

        const int Rate = 48000;
        double Peak(double from, double to)
        {
            var (first, last) = (Math.Max(0, (int)(from * Rate)), Math.Min(sound.Length, (int)(to * Rate)));
            var peak = 0;
            for (var index = first; index < last; index++)
            {
                peak = Math.Max(peak, Math.Abs((int)sound[index]));
            }

            return peak;
        }

        double Level(double from, double to)
        {
            var (first, last) = (Math.Max(0, (int)(from * Rate)), Math.Min(sound.Length, (int)(to * Rate)));
            if (last <= first)
            {
                return double.NaN;
            }

            double sum = 0;
            for (var index = first; index < last; index++)
            {
                sum += (double)sound[index] * sound[index];
            }

            return Math.Sqrt(sum / (last - first));
        }

        // The tones the video keeps, by the second of the recording each starts, and where the
        // video plays it, in 120ths of a second: second 1 at 0; second 4, which is 480, at
        // 239 + (480 − 479) = 240; seconds 7 and 8, which are 840 and 960, at 598 + (840 − 839) =
        // 599 and at 719. Seconds 2 and 3 are in the faster stretch, second 5 is cut out, and
        // second 6 is in the slower stretch.
        (int Second, double At)[] tones = [(1, 0), (4, 240 / 120.0), (7, 599 / 120.0), (8, 719 / 120.0)];
        var peaks = tones.Select(tone => Peak(tone.At - 0.03, tone.At + 0.08)).ToArray();

        // The two stretches at another speed, as the video has them: 119 to 239, and 360 to 598.
        // Three hundredths of a second are left out at each end, where a tone next to the stretch rings on in the encoded sound.
        (string What, double From, double To)[] silent = [("twice as fast", 119 / 120.0, 239 / 120.0), ("half as fast", 360 / 120.0, 598 / 120.0)];
        var levels = silent.Select(stretch => Level(stretch.From + 0.03, stretch.To - 0.03)).ToArray();
        var loudest = silent.Select(stretch => Peak(stretch.From + 0.03, stretch.To - 0.03)).ToArray();
        var seconds = sound.Length / (double)Rate;
        var picture = frames / (double)Fps;
        _report.Check(
            "the video's sound has the tones of the seconds that play at the recording's own speed where the video plays them, and is silent where the video plays at another speed, in the faster stretch and in the slower one; it lasts as long as the picture",
            peaks.All(peak => peak >= 6000) && levels.All(level => level <= 200) && Math.Abs(seconds - picture) <= 0.1,
            string.Create(CultureInfo.InvariantCulture, $"{seconds:0.000} s of sound for {picture:0.000} s of picture; ")
                + string.Join(", ", tones.Select((tone, index) => string.Create(CultureInfo.InvariantCulture, $"the tone of second {tone.Second} at {tone.At:0.000} s: {peaks[index]:0} of 32767")))
                + "; "
                + string.Join(", ", silent.Select((stretch, index) => string.Create(CultureInfo.InvariantCulture, $"where it is {stretch.What}, from {stretch.From:0.000} to {stretch.To:0.000} s: level {levels[index]:0.0}, loudest sample {loudest[index]:0}")))
                + "; a tone is at least 6000 and silence a level of 200 at most");
    }
}
