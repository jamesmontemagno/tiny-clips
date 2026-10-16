using System.Diagnostics;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 2. Transport: Play and Pause, the frame steps, the playhead, and the end of the kept range.
internal sealed partial class WindowChecks
{
    /// <summary>The transport read-out without the word a screen reader gets in front of it.</summary>
    private static string TimeText(Editor editor)
    {
        var name = NameOf(editor, "StudioTimeText");
        return name.StartsWith("Time ", StringComparison.Ordinal) ? name[5..] : name;
    }

    private static double Playhead(Editor editor) => SliderValue(editor, "StudioPlayhead");

    private void Transport()
    {
        Timeline.Mark("2: transport");
        if (OpenReady(NewCameraProject("Transport"), "transport") is not { } editor)
        {
            return;
        }

        var trimStart = CameraOffset;
        double trimEnd = TestMedia.Screen.Seconds;
        string TimeFor(double playhead) => StudioEditorText.GetTimeText(playhead - trimStart, trimEnd - trimStart);
        var start = FrameOf(trimStart);
        LookFor(editor, s => s.Shown == Both(editor, start), 5);

        // At rest.
        var head = Playhead(editor);
        var time = TimeText(editor);
        var play = NameOf(editor, "StudioPlayPauseButton");
        _report.Check(
            "at rest the button offers Play, the time reads the start of the video and its length, and the playhead is on the frame the picture shows",
            play == "Play" && time == TimeFor(trimStart) && FrameOf(head) == start,
            $"button \"{play}\", time \"{time}\", playhead {F(head)} s (frame {FrameOf(head)})");

        // The two step buttons, through UI Automation.
        var stepped = true;
        for (var step = 0; step < 3; step++)
        {
            stepped &= Invoke(editor, "StudioNextFrameButton");
        }

        Stepped("Next frame, three times: the picture, the playhead and the time are three frames on", stepped, start + 3);
        Stepped("Previous frame: one frame back", Invoke(editor, "StudioPreviousFrameButton"), start + 2);

        // What the arrow keys and Space do. The keys are not pressed: see Key().
        var right = Key(editor, StudioShortcutKey.Right);
        Stepped("what the Right arrow key runs steps one frame on", right == StudioShortcutAction.NextFrame, start + 3);
        var left = Key(editor, StudioShortcutKey.Left);
        Stepped("what the Left arrow key runs steps one frame back", left == StudioShortcutAction.PreviousFrame, start + 2);

        // The playhead as a slider.
        const int Far = 120;
        Stepped("the playhead set as a slider: the picture and the time follow", SetSlider(editor, "StudioPlayhead", MiddleOf(Far)), Far);

        Playing(editor, TimeFor);

        // Into the end of the kept range.
        const int From = 150;
        const double End = 5.6;
        var endSet = SetSlider(editor, "StudioTrimEnd", End);
        trimEnd = SliderValue(editor, "StudioTrimEnd");
        Expect(editor, p => p with { Edits = p.Edits with { TrimEnd = trimEnd } });
        var endFrame = FrameOf(trimEnd);
        var atEnd = LookFor(editor, s => s.Shown == Both(editor, endFrame), 3);
        _report.Check(
            "the End handle set as a slider: the picture shows the frame the video now ends on, and the time reads the new length",
            endSet && Math.Abs(trimEnd - End) < 1e-6 && atEnd?.Shown == Both(editor, endFrame) && TimeText(editor) == TimeFor(trimEnd),
            $"End {F(trimEnd)} s; {atEnd?.Shown}; time \"{TimeText(editor)}\"");

        SetSlider(editor, "StudioPlayhead", MiddleOf(From));
        LookFor(editor, s => s.Shown.Screen == From, 3);
        var watch = Stopwatch.StartNew();
        Invoke(editor, "StudioPlayPauseButton");
        var seen = new List<int>();
        var stopped = Until(
            () =>
            {
                if (Look(editor) is { } sight && sight.Shown.Screen != FrameCode.Unreadable)
                {
                    seen.Add(sight.Shown.Screen);
                }

                return watch.ElapsedMilliseconds > 250 && NameOf(editor, "StudioPlayPauseButton", 0) == "Play";
            },
            done => done,
            6,
            20);
        var took = watch.Elapsed.TotalMilliseconds;
        var parked = LookFor(editor, s => s.Shown == Both(editor, endFrame), 3);
        head = Playhead(editor);
        time = TimeText(editor);
        _report.Check(
            "playing into the end of the kept range stops there by itself: Play is offered again, the playhead is on the end, the picture on its frame, and the time reads the full length",
            stopped && Math.Abs(head - trimEnd) < 1e-6 && parked?.Shown == Both(editor, endFrame) && time == TimeFor(trimEnd) && seen.Count > 0 && seen.Max() <= endFrame,
            $"stopped {stopped} after {F(took, "0")} ms for {F((endFrame - From) * 1000.0 / Fps, "0")} ms of video; playhead {F(head)} s; {parked?.Shown}; time \"{time}\"; frames seen on the way {(seen.Count == 0 ? "none" : $"{seen.Min()} to {seen.Max()}")}");

        // Play again: from the start of the kept range, not from the end.
        Invoke(editor, "StudioPlayPauseButton");
        watch.Restart();
        seen.Clear();
        Until(
            () =>
            {
                if (Look(editor) is { } sight && sight.Shown.Screen != FrameCode.Unreadable && sight.Shown.Screen != endFrame)
                {
                    seen.Add(sight.Shown.Screen);
                }

                return seen.Count >= 4 || watch.ElapsedMilliseconds > 1500;
            },
            done => done,
            2,
            20);
        var playing = NameOf(editor, "StudioPlayPauseButton", 0) == "Pause";
        Invoke(editor, "StudioPlayPauseButton");
        _report.Check(
            "Play at the end of the kept range plays it again from its start",
            playing && seen.Count >= 2 && seen[0] >= start && seen[0] < start + 20 && seen.SequenceEqual(seen.Order()),
            $"playing {playing}; the first frames seen after Play: {string.Join(", ", seen)} (the range starts on frame {start} and ends on {endFrame})");
        CloseQuietly(editor);

        void Stepped(string name, bool done, int frame)
        {
            var sight = LookFor(editor, s => s.Shown == Both(editor, frame), 3);
            var position = Playhead(editor);
            var text = TimeText(editor);
            _report.Check(
                name,
                done && sight?.Shown == Both(editor, frame) && FrameOf(position) == frame && text == TimeFor(position),
                $"{sight?.Shown} (expected {Both(editor, frame)}); playhead {F(position)} s (frame {FrameOf(position)}); time \"{text}\"");
        }
    }

    private void Playing(Editor editor, Func<double, string> timeFor)
    {
        Timeline.Mark("2: play and pause");
        var before = Look(editor)?.Shown.Screen ?? FrameCode.Unreadable;
        var invoked = Invoke(editor, "StudioPlayPauseButton");
        var offersPause = Until(() => NameOf(editor, "StudioPlayPauseButton", 0), name => name == "Pause", 2) == "Pause";

        // Three looks while it plays: the picture, then at once the playhead and the time.
        // "At once" is the tool's doing, and on a PC that is busy with something else a look
        // can take a second: the playhead is then read that much later than the picture was
        // taken, and is that many frames ahead of it. In a run of 7 October 2026 the three
        // looks were 16 and 43 frames apart where they are 8 or 9, and the playhead was 18
        // frames ahead of the picture. So looks that took too long are made again, twice at
        // most, while the preview plays on, and the note says so.
        const double SlowLook = 0.45;
        var looks = new List<(int Picture, int Playhead, string Time)>();
        var slowest = 0.0;
        var heldUp = new List<string>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            looks.Clear();
            slowest = 0;
            for (var look = 0; look < 3; look++)
            {
                var began = Stopwatch.GetTimestamp();
                Thread.Sleep(160);
                var picture = Look(editor)?.Shown.Screen ?? FrameCode.Unreadable;
                looks.Add((picture, FrameOf(Playhead(editor)), TimeText(editor)));
                slowest = Math.Max(slowest, Stopwatch.GetElapsedTime(began).TotalSeconds);
            }

            if (slowest <= SlowLook)
            {
                break;
            }

            heldUp.Add($"{string.Join("; ", looks.Select(l => $"{l.Picture} / {l.Playhead}"))}, the slowest look {F(slowest * 1000, "0")} ms");
            before = looks[^1].Picture;
        }

        var advancing = looks.Select(l => l.Picture).Prepend(before).SequenceEqual(looks.Select(l => l.Picture).Prepend(before).Order())
            && looks[^1].Picture > before + 5
            && looks.Zip(looks.Skip(1), (a, b) => b.Playhead > a.Playhead).All(ok => ok)
            && looks.Select(l => l.Time).Distinct().Count() > 1;
        var apart = looks.Max(l => Math.Abs(l.Picture - l.Playhead));
        _report.Check(
            "Play: the button offers Pause, and the picture, the playhead and the time all move on together",
            invoked && offersPause && advancing && apart <= 6,
            $"picture / playhead frame / time, about 160 ms apart: {string.Join("; ", looks.Select(l => $"{l.Picture} / {l.Playhead} / \"{l.Time}\""))}");
        _report.Note(
            $"while playing, the playhead the window reported was at most {apart} frame(s) from the frame in the screenshot taken just before it; the slowest of the three looks took {F(slowest * 1000, "0")} ms"
            + (heldUp.Count == 0 ? string.Empty : $"; looked again because the tool was held up, {heldUp.Count} time(s): {string.Join(" | ", heldUp)}"));

        var paused = Invoke(editor, "StudioPlayPauseButton");
        var offersPlay = Until(() => NameOf(editor, "StudioPlayPauseButton", 0), name => name == "Play", 2) == "Play";
        EngineOf(editor)?.WaitForIdle(TimeSpan.FromSeconds(5));
        var head = Playhead(editor);
        var frame = FrameOf(head);
        var sight = LookFor(editor, s => s.Shown == Both(editor, frame), 3);
        var time = TimeText(editor);
        Thread.Sleep(300);
        var later = Look(editor);
        _report.Check(
            "Pause: the button offers Play, the picture stays on the frame the playhead names, with the camera frame that belongs to it, and the time reads that frame",
            paused && offersPlay && sight?.Shown == Both(editor, frame) && time == timeFor(head) && later?.Shown == sight.Shown && Math.Abs(Playhead(editor) - head) < 1e-9,
            $"playhead {F(head)} s (frame {frame}); picture {sight?.Shown}, and {later?.Shown} 300 ms later; time \"{time}\"");

        // What Space runs.
        var space = Key(editor, StudioShortcutKey.Space);
        var playsOnSpace = Until(() => NameOf(editor, "StudioPlayPauseButton", 0), name => name == "Pause", 2) == "Pause";
        Thread.Sleep(200);
        Key(editor, StudioShortcutKey.Space);
        var pausesOnSpace = Until(() => NameOf(editor, "StudioPlayPauseButton", 0), name => name == "Play", 2) == "Play";
        var after = FrameOf(Playhead(editor));
        var rests = LookFor(editor, s => s.Shown == Both(editor, FrameOf(Playhead(editor))), 3);
        _report.Check(
            "what Space runs starts playback, and stops it again",
            space == StudioShortcutAction.TogglePlayback && playsOnSpace && pausesOnSpace && after > frame && rests?.Shown.Screen == FrameOf(Playhead(editor)),
            $"played from frame {frame} to frame {after}; picture {rests?.Shown}");
    }
}
