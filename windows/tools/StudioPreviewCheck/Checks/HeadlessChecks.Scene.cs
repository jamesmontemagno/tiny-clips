using System.Diagnostics;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Preview;
using TinyClips.Tools.StudioPreviewCheck.Media;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

// What the picture is made of: the project, the camera's timing, sound, and the surface.
internal sealed partial class HeadlessChecks
{
    private static StudioProject WithBubble(StudioProject project, Func<StudioBubble, StudioBubble> change) =>
        project with { Scenes = [project.Scenes[0] with { Layout = StudioLayout.Bubble, Bubble = change(project.Scenes[0].Bubble) }] };

    private static StudioProject WithLayout(StudioProject project, StudioLayout layout) =>
        project with { Scenes = [project.Scenes[0] with { Layout = layout }] };

    private static StudioProject WithBackground(StudioProject project, string color) =>
        project with { Canvas = project.Canvas with { Background = new StudioBackground { Style = StudioBackgroundStyle.Solid, Preset = null, Primary = color, Secondary = null } } };

    private static Rgb Color(string hex) => new(Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16));

    // ---------------------------------------------------------------------------------------
    // UpdateProject
    // ---------------------------------------------------------------------------------------

    private void UpdateProject()
    {
        _report.Section("UpdateProject while paused: the change shows in the pixels and no player delivers a frame");
        var session = OpenSession(TestMedia.Camera);
        const int frame = 90;
        session.SeekTo(frame);
        var camera = session.ExpectedCamera(frame);
        var original = session.Project;
        var originalView = session.View(1280, 720);

        void Expect(string what, StudioProject project, Func<Picture, string?> extra)
        {
            var before = session.Engine.GetDiagnostics();
            session.Update(project);
            var idle = session.WaitForIdle();
            var after = session.Engine.GetDiagnostics();
            var picture = session.ReadPicture();
            var shown = session.ReadShown();
            var wantScreen = project.Scenes[0].Layout == StudioLayout.Camera ? FrameCode.Unreadable : frame;
            var wantCamera = session.ExpectedCamera(frame);
            var problem = picture is null ? "no picture" : extra(picture);
            var callbacks = (after.CallbacksStarted[0] - before.CallbacksStarted[0]) + (after.CallbacksStarted[1] - before.CallbacksStarted[1]);
            var ok = idle && shown.Screen == wantScreen && shown.Camera == wantCamera && problem is null && callbacks == 0 && after.FramesDrawn > before.FramesDrawn && session.PositionFrame == frame;
            _report.Check(what, ok, $"{shown}{(problem is null ? string.Empty : "; " + problem)}; VideoFrameAvailable callbacks {callbacks}, scenes drawn {after.FramesDrawn - before.FramesDrawn}");
        }

        // The bubble moved to another corner: the camera is read where the new layout puts it, and is gone from the old place.
        Expect("bubble moved to the bottom left", WithBubble(original, b => b with { Anchor = StudioAnchor.BottomLeft }), picture =>
            FrameCode.Decode(picture.Bgra, picture.Width, picture.Height, TestMedia.Camera, originalView.CameraMap!.Value) == FrameCode.Unreadable ? null : "the camera is still at its old place");
        Expect("bubble moved by an offset", WithBubble(original, b => b with { OffsetX = -0.2, OffsetY = -0.1 }), _ => null);
        Expect("bubble made smaller", WithBubble(original, b => b with { Size = 0.3 }), _ => null);
        Expect("layout switched to side by side", WithLayout(original, StudioLayout.SideBySide), _ => null);
        Expect("layout switched to screen only: the camera is not drawn", WithLayout(original, StudioLayout.Screen), picture =>
            FrameCode.Decode(picture.Bgra, picture.Width, picture.Height, TestMedia.Camera, originalView.CameraMap!.Value) == FrameCode.Unreadable ? null : "the camera is still drawn");
        Expect("layout switched to camera only: the screen is not drawn", WithLayout(original, StudioLayout.Camera), picture =>
            FrameCode.Decode(picture.Bgra, picture.Width, picture.Height, TestMedia.Screen, originalView.ScreenMap!.Value) == FrameCode.Unreadable ? null : "the screen is still drawn");
        Expect("padding changed", original with { Canvas = original.Canvas with { Padding = 0.15 } }, _ => null);
        Expect("camera no longer mirrored", original with { Camera = original.Camera with { Mirror = false } }, _ => null);
        foreach (var color in new[] { "#FF0000", "#00C040", "#2040FF" })
        {
            Expect($"background colour {color}", WithBackground(original, color), picture =>
            {
                var corner = picture.ColorAt(2, 2);
                return corner.Distance(Color(color)) <= 6 ? null : $"the corner is {corner}";
            });
        }

        Expect("back to the first project", original, _ => null);

        // 200 updates in a row, as fast as a caller can make them.
        var colors = new string[200];
        for (var index = 0; index < colors.Length; index++)
        {
            colors[index] = $"#{(index * 37) % 256:X2}{(index * 91) % 256:X2}{255 - index:X2}";
        }

        var drawnBefore = session.Engine.GetDiagnostics();
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < colors.Length; index++)
        {
            session.Update(WithBubble(WithBackground(original, colors[index]), b => b with { OffsetX = -0.001 * index }));
        }

        var callsMilliseconds = watch.Elapsed.TotalMilliseconds;
        session.WaitForIdle();
        var settledMilliseconds = watch.Elapsed.TotalMilliseconds;
        var drawnAfter = session.Engine.GetDiagnostics();
        var last = session.ReadPicture();
        var lastShown = session.ReadShown();
        var redraws = drawnAfter.FramesDrawn - drawnBefore.FramesDrawn;
        var lastCorner = last?.ColorAt(2, 2) ?? default;
        _report.Check("200 updates in a row cost no more than a few redraws", redraws is >= 1 and <= 12, $"{redraws} scenes drawn; the 200 calls took {F(callsMilliseconds, "0.00")} ms in all ({F(callsMilliseconds * 1000 / 200, "0.0")} µs each), settled after {F(settledMilliseconds)} ms");
        _report.Check("the picture after them is the last project's", lastShown.Screen == frame && lastShown.Camera == camera && lastCorner.Distance(Color(colors[^1])) <= 6, $"{lastShown}, corner {lastCorner}, wanted {Color(colors[^1])}");
        _report.Check("no player delivered a frame for them", drawnAfter.CallbacksStarted[0] == drawnBefore.CallbacksStarted[0] && drawnAfter.CallbacksStarted[1] == drawnBefore.CallbacksStarted[1]);

        // The same paced like a slider drag: an update every 60th of a second, each one drawn.
        session.Recorder.Drain();
        var calls = new List<(long At, Rgb Color)>();
        drawnBefore = session.Engine.GetDiagnostics();
        var pace = Stopwatch.StartNew();
        const int paced = 120;
        for (var index = 0; index < paced; index++)
        {
            var color = $"#{(index * 2) + 8:X2}{255 - (index * 2):X2}{(index * 53) % 256:X2}";
            calls.Add((Stopwatch.GetTimestamp(), Color(color)));
            session.Update(WithBackground(original, color));
            var next = (index + 1) * 1000.0 / 60;
            while (pace.Elapsed.TotalMilliseconds < next)
            {
                Thread.Sleep(1);
            }
        }

        session.WaitForIdle();
        drawnAfter = session.Engine.GetDiagnostics();
        var composites = session.Recorder.Drain();
        var latency = new Samples();
        var seen = 0;
        foreach (var (at, color) in calls)
        {
            var first = composites.FirstOrDefault(c => c.At >= at && c.Corner.Distance(color) <= 3);
            if (first.At != 0)
            {
                seen++;
                latency.Add(Stopwatch.GetElapsedTime(at, first.At).TotalMilliseconds);
            }
        }

        _report.Check($"{paced} updates at 60 per second are each drawn, with nothing drawn twice", seen >= paced - 6 && drawnAfter.FramesDrawn - drawnBefore.FramesDrawn <= paced + 2, $"{seen} of {paced} seen, {drawnAfter.FramesDrawn - drawnBefore.FramesDrawn} scenes drawn");
        _report.Note($"UpdateProject() to the scene drawn (the redraw cost seen by a caller): {latency.Summary()}");

        // While playing it takes effect within a frame.
        session.Update(original);
        session.SeekTo(30);
        var wrong = new List<string>();
        var playingLatency = new Samples();
        session.Engine.Play();
        Thread.Sleep(500);
        foreach (var color in new[] { "#FF2020", "#20FF20", "#2020FF", "#F0F020", "#20F0F0", "#F020F0" })
        {
            session.Recorder.Drain();
            var at = Stopwatch.GetTimestamp();
            session.Update(WithBackground(original, color));
            Thread.Sleep(300);
            var drawn = session.Recorder.Drain().Where(c => c.At >= at).ToList();
            var wanted = Color(color);
            var firstNew = drawn.FindIndex(c => c.Corner.Distance(wanted) <= 6);

            // A scene that was being drawn when the call was made may still be the old one; the next must be the new.
            if (firstNew < 0 || firstNew > 1)
            {
                wrong.Add($"{color}: the new background first showed in scene {firstNew} after the call (of {drawn.Count})");
            }
            else
            {
                playingLatency.Add(Stopwatch.GetElapsedTime(at, drawn[firstNew].At).TotalMilliseconds);
            }
        }

        var stillPlaying = session.Engine.IsPlaying;
        session.Engine.Pause();
        session.WaitForIdle();
        _report.Check("while playing, an update shows in the next scene drawn", wrong.Count == 0 && stillPlaying, wrong.Count == 0 ? null : string.Join(" | ", wrong));
        _report.Note($"UpdateProject() while playing to the first scene drawn with it: {playingLatency.Summary()}");

        // A layout change while playing, read after the pause.
        session.Engine.Play();
        Thread.Sleep(300);
        session.Update(WithLayout(original, StudioLayout.SideBySide));
        Thread.Sleep(300);
        session.Engine.Pause();
        session.WaitForIdle();
        var end = session.ReadShown();
        _report.Check("a layout switched while playing is the layout of the picture afterwards", end.Screen == session.PositionFrame && end.Camera == session.ExpectedCamera(end.Screen) && end.Screen > 30, end.ToString());
        _report.Check("no failure was reported", session.Events.FailedEvents == 0, string.Join("; ", session.Events.Failures()));
        Close(session);
    }

    // ---------------------------------------------------------------------------------------
    // Camera timing
    // ---------------------------------------------------------------------------------------

    private void CameraTiming()
    {
        _report.Section("Camera timing: start offsets and a camera shorter than the screen");
        CameraCase("camera starts 0.2 s late", TestMedia.Camera, 0.2, [0, 3, 5, 6, 7, 100, 359, 5, 6, 0], playAcross: 2);
        CameraCase("camera starts 0.1 s early", TestMedia.Camera, -0.1, [0, 1, 200, 356, 357, 359, 356, 0], playAcross: 350);
        CameraCase("6 s camera starting at 2 s", TestMedia.ShortCamera, 2.0, [0, 59, 60, 61, 150, 239, 240, 300, 359, 239, 60, 59, 0], playAcross: 55, playAcrossEnd: 233);

        // Less than a frame late, as two recorders started together are: only the very first frame is without it.
        CameraCase("camera starts 20 ms late", TestMedia.Camera, 0.02, [0, 1, 2, 100, 359, 1, 0], playAcross: 0);
    }

    private void CameraCase(string name, ClipSpec camera, double offset, int[] frames, int playAcross, int playAcrossEnd = -1)
    {
        var session = OpenSession(camera, offset);
        var before = session.Engine.GetDiagnostics();
        var wrong = new List<string>();
        var hidden = 0;
        var visible = 0;
        var latency = new Samples();
        var first = session.ReadShown();
        if (first.Screen != 0 || first.Camera != session.ExpectedCamera(0))
        {
            wrong.Add($"after open: {first}, wanted camera {session.ExpectedCamera(0)}");
        }

        foreach (var frame in frames)
        {
            var arrived = session.SeekTo(frame, out var milliseconds);
            if (milliseconds > 0)
            {
                latency.Add(milliseconds);
            }

            var shown = session.ReadShown();
            var expected = session.ExpectedCamera(frame);
            hidden += expected == FrameCode.Unreadable ? 1 : 0;
            visible += expected == FrameCode.Unreadable ? 0 : 1;
            if (!arrived || shown.Screen != frame || shown.Camera != expected)
            {
                wrong.Add($"frame {frame}: arrived {arrived}, {shown}, wanted camera {(expected == FrameCode.Unreadable ? "hidden" : expected.ToString())}");
            }
        }

        var after = session.Engine.GetDiagnostics();
        _report.Check($"{name}: the camera is hidden outside its range and shows the right frame inside it ({visible} positions inside, {hidden} outside)", wrong.Count == 0 && hidden > 0 && visible > 0, wrong.Count == 0 ? null : string.Join(" | ", wrong.Take(4)));
        _report.Note($"{name}: seek latency {latency.Summary()}; repairs {after.Repairs - before.Repairs}, never confirmed {after.RepairFailures - before.RepairFailures}");

        // Where the camera is hidden the picture is the one a project without the bubble gives.
        var hiddenFrame = frames.First(f => session.ExpectedCamera(f) == FrameCode.Unreadable);
        session.SeekTo(hiddenFrame);
        var withBubble = session.ReadPicture();
        var project = session.Project;
        session.Update(WithLayout(project, StudioLayout.Screen));
        session.WaitForIdle();
        var withoutBubble = session.ReadPicture();
        session.Update(project);
        session.WaitForIdle();
        var difference = withBubble is null || withoutBubble is null ? 255 : LargestDifference(withBubble, withoutBubble);
        _report.Check($"{name}: at frame {hiddenFrame} the picture is exactly the one without a camera", difference <= 1, $"largest difference {difference} of 255");

        // Playing across the moment the camera appears, and where it has one, the moment it ends.
        foreach (var start in new[] { playAcross, playAcrossEnd })
        {
            if (start < 0)
            {
                continue;
            }

            session.SeekTo(start);
            session.Recorder.Drain();

            // The frame it starts from is drawn once more, so that it is the first scene on record.
            session.Update(session.Project);
            session.WaitForIdle();
            session.Engine.Play();
            Thread.Sleep(700);
            session.Engine.Pause();
            session.WaitForIdle();
            var composites = session.Recorder.Drain().Where(c => c.Screen != FrameCode.Unreadable).ToList();
            var offBy = 0;
            var changes = 0;
            var late = 0;
            for (var index = 0; index < composites.Count; index++)
            {
                var expected = session.ExpectedCamera(composites[index].Screen);
                var got = composites[index].Camera;
                if (index > 0 && (session.ExpectedCamera(composites[index - 1].Screen) == FrameCode.Unreadable) != (expected == FrameCode.Unreadable))
                {
                    changes++;
                }

                if (expected == FrameCode.Unreadable || got == FrameCode.Unreadable)
                {
                    // Shown a frame early or hidden a frame late: the frames either side of the edge.
                    if (expected != got)
                    {
                        late++;
                        var neighbour = session.ExpectedCamera(composites[index].Screen + 1) == got || session.ExpectedCamera(composites[index].Screen - 1) == got;
                        offBy = Math.Max(offBy, neighbour ? 1 : 2);
                    }
                }
                else
                {
                    offBy = Math.Max(offBy, Math.Abs(got - expected));
                }
            }

            var final = session.ReadShown();
            var good = composites.Count > 10 && changes >= 1 && offBy <= 1 && final.Camera == session.ExpectedCamera(final.Screen);
            _report.Check(
                $"{name}: playing from frame {start} across the edge of the camera's range shows and hides it on time",
                good,
                $"{composites.Count} scenes, frames {(composites.Count == 0 ? -1 : composites[0].Screen)}..{(composites.Count == 0 ? -1 : composites[^1].Screen)}, worst {offBy} frame(s) apart, {late} scene(s) a frame early or late at the edge; after the pause {final}{(good ? string.Empty : "; drawn (screen:camera): " + string.Join(' ', composites.Select(c => $"{c.Screen}:{(c.Camera == FrameCode.Unreadable ? "-" : c.Camera.ToString())}")))}");
        }

        // The players still answer after running past the camera's end.
        var beforeBack = Transport(session);
        var back = session.SeekTo(100);
        var afterEnd = session.ReadShown();
        Thread.Sleep(300);
        var afterEndLater = session.ReadShown();
        var backRight = back && afterEnd.Screen == 100 && afterEnd.Camera == session.ExpectedCamera(100) && afterEndLater == afterEnd;
        _report.Check($"{name}: a seek back into the range afterwards shows the right frames, and they stay", backRight, $"{afterEnd}; 300 ms later {afterEndLater}{(backRight ? string.Empty : $"; before the seek: {beforeBack}; after: {Transport(session)}{Dump(session, "camera")}")}");
        _report.Check($"{name}: no failure was reported", session.Events.FailedEvents == 0, string.Join("; ", session.Events.Failures()));
        Close(session);
    }

    private static int LargestDifference(Picture a, Picture b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return 255;
        }

        var largest = 0;
        for (var index = 0; index < a.Bgra.Length; index++)
        {
            if (index % 4 != 3)
            {
                largest = Math.Max(largest, Math.Abs(a.Bgra[index] - b.Bgra[index]));
            }
        }

        return largest;
    }

    // ---------------------------------------------------------------------------------------
    // Mute
    // ---------------------------------------------------------------------------------------

    private void Mute()
    {
        _report.Section("Mute");

        // The switch every other check runs with: both players muted and at volume zero, whatever the project says.
        var session = OpenSession(TestMedia.Camera);
        var forced = new List<string>();
        foreach (var (muted, volume) in new[] { (false, 1.0), (true, 0.5), (false, 0.5), (false, 0.0), (true, 1.0), (false, 0.25) })
        {
            session.Update(session.Project with { Audio = new StudioAudio { Muted = muted, Volume = volume } });
            session.WaitForIdle();
            var state = session.Engine.GetDiagnostics();
            if (!state.PlayerMuted[0] || !state.PlayerMuted[1] || state.PlayerVolume[0] != 0 || state.PlayerVolume[1] != 0 || state.ProjectMuted != muted || state.ProjectVolume != volume)
            {
                forced.Add($"project muted {muted}, volume {F(volume)}: IsMuted screen {state.PlayerMuted[0]}, camera {state.PlayerMuted[1]}; volume {F(state.PlayerVolume[0])}/{F(state.PlayerVolume[1])}; engine saw muted {state.ProjectMuted}, volume {F(state.ProjectVolume)}");
            }
        }

        // The volume changed while it plays, as a slider that is dragged does.
        session.Engine.Play();
        Thread.Sleep(150);
        session.Update(session.Project with { Audio = new StudioAudio { Volume = 0.8 } });
        Thread.Sleep(150);
        var playing = session.Engine.GetDiagnostics();
        session.Engine.Pause();
        session.WaitForIdle();
        _report.Check(
            "with the checks' force-mute both players are muted and at volume zero whatever the project says of mute and of its volume, also while playing and while the volume changes; the engine is told each volume",
            forced.Count == 0 && playing.PlayerMuted[0] && playing.PlayerMuted[1] && playing.PlayerVolume[0] == 0 && playing.PlayerVolume[1] == 0 && playing.ProjectVolume == 0.8,
            forced.Count == 0
                ? $"six projects, then while playing: volume {F(playing.PlayerVolume[0])}/{F(playing.PlayerVolume[1])}, muted {playing.PlayerMuted[0]}/{playing.PlayerMuted[1]}, the engine saw volume {F(playing.ProjectVolume)}"
                : string.Join(" | ", forced));
        Close(session);

        // The project's flag reaching the screen player can only be seen on a player that is not
        // forced mute. This one plays a screen file that has no audio track, at volume zero, so
        // nothing can be heard whatever its IsMuted says.
        var silentScreen = TestMedia.Camera;
        if (silentScreen.HasAudio)
        {
            _report.Check("the screen clip of the mute check has no audio track", false);
            return;
        }

        var unforced = new StudioPreviewOptions { ZeroVolume = true };
        foreach (var startMuted in new[] { false, true })
        {
            session = OpenSession(TestMedia.Camera, options: unforced, screen: silentScreen, edit: p => p with { Audio = new StudioAudio { Muted = startMuted } });
            var wrong = new List<string>();
            var open = session.Engine.GetDiagnostics();
            if (open.PlayerMuted[0] != startMuted || !open.PlayerMuted[1] || open.PlayerVolume[0] != 0)
            {
                wrong.Add($"after open with muted {startMuted}: screen IsMuted {open.PlayerMuted[0]}, camera IsMuted {open.PlayerMuted[1]}, volume {F(open.PlayerVolume[0])}");
            }

            // The project's volume changes with it. The players stay at zero: that is what
            // this option is for, and with it the volume is the engine's to know, not theirs.
            foreach (var (muted, volume) in new[] { (!startMuted, 0.5), (startMuted, 1.0), (!startMuted, 0.25) })
            {
                session.Update(session.Project with { Audio = new StudioAudio { Muted = muted, Volume = volume } });
                session.WaitForIdle();
                var state = session.Engine.GetDiagnostics();
                if (state.PlayerMuted[0] != muted || !state.PlayerMuted[1] || state.PlayerVolume[0] != 0 || state.PlayerVolume[1] != 0 || state.ProjectVolume != volume)
                {
                    wrong.Add($"project muted {muted}, volume {F(volume)}: screen IsMuted {state.PlayerMuted[0]}, camera IsMuted {state.PlayerMuted[1]}, volume {F(state.PlayerVolume[0])}/{F(state.PlayerVolume[1])}, the engine saw volume {F(state.ProjectVolume)}");
                }
            }

            var shown = session.ReadShown();
            _report.Check(
                $"project.Audio.Muted reaches the screen player's IsMuted and the camera player stays muted, and both players stay at volume zero while the project's volume changes, which the engine is told (opened with muted = {startMuted}; screen file without an audio track, volume zero)",
                wrong.Count == 0 && shown.Screen == 0,
                wrong.Count == 0 ? null : string.Join(" | ", wrong));
            Close(session);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Surfaces
    // ---------------------------------------------------------------------------------------

    private void Surfaces()
    {
        _report.Section("Surface: none, attach, detach, replace, resize");
        var session = OpenSession(TestMedia.Camera, attach: false);

        // No surface: nothing is drawn, the players still decode and the position still moves.
        var arrived = session.SeekTo(50);
        var none = session.Engine.GetDiagnostics();
        _report.Check("with no surface a seek still lands: both players hold the frame and Position follows", arrived && session.ReadClipFrame(0) == 50 && session.ReadClipFrame(1) == session.ExpectedCamera(50) && session.PositionFrame == 50, $"screen texture {session.ReadClipFrame(0)}, camera texture {session.ReadClipFrame(1)}, Position frame {session.PositionFrame}");
        _report.Check("with no surface nothing is drawn", none.FramesDrawn == 0, $"{none.FramesDrawn} scenes");

        // Attach: the current frame is drawn without anything being asked of the players.
        var first = session.Attach(1280, 720);
        session.WaitForIdle();
        var attached = session.Engine.GetDiagnostics();
        var shown = session.ReadShown();
        _report.Check("attaching a surface draws the current frame into it, with no new frame from the players", shown.Screen == 50 && shown.Camera == session.ExpectedCamera(50) && first.Presents >= 1 && attached.CallbacksStarted[0] == none.CallbacksStarted[0], $"{shown}, presented {first.Presents} time(s)");

        // Detach: the surface has released its texture when the call returns.
        session.Detach();
        var presentsAtDetach = first.Presents;
        _report.Check("detaching releases the surface's buffers before it returns", first.Releases >= 1 && first.BufferSize == (0, 0), $"release calls {first.Releases}, buffer {first.BufferSize}");
        session.SeekTo(70);
        Thread.Sleep(100);
        _report.Check("a detached surface is not drawn into again", first.Presents == presentsAtDetach && first.BufferSize == (0, 0));

        // Attach another one.
        var second = session.Attach(960, 540);
        session.WaitForIdle();
        shown = session.ReadShown();
        _report.Check("attaching another surface shows the frame the players are on now", shown.Screen == 70 && shown.Camera == session.ExpectedCamera(70) && second.BufferSize == (960, 540), $"{shown}, buffer {second.BufferSize}");

        // Replace it without detaching.
        var third = session.Attach(640, 360);
        session.WaitForIdle();
        shown = session.ReadShown();
        _report.Check("attaching a third in its place releases the second and draws into the third", second.Releases >= 1 && second.BufferSize == (0, 0) && shown.Screen == 70 && shown.Camera == session.ExpectedCamera(70) && third.BufferSize == (640, 360), $"{shown}; second surface release calls {second.Releases}, buffer {second.BufferSize}");

        // The first one again: a surface can come back.
        session.Detach();
        session.Engine.AttachSurface(first);
        session.WaitForIdle();
        var again = first.Read(session.Engine)?.Shown(session.View(1280, 720), session.Folder.Screen, session.Folder.Camera) ?? default;
        _report.Check("a surface that was detached can be attached again", again.Screen == 70 && again.Camera == session.ExpectedCamera(70) && first.BufferSize == (1280, 720), again.ToString());
        session.Engine.DetachSurface(first);
        var surface = session.Attach(1280, 720);
        session.WaitForIdle();

        // Sizes while paused.
        var sizes = new (int Width, int Height)[] { (640, 360), (1920, 1080), (801, 451), (333, 187), (1279, 719), (2560, 1440), (700, 700), (402, 714), (1280, 720) };
        var wrong = new List<string>();
        var targets = new List<string>();
        var resizeLatency = new Samples();
        foreach (var (width, height) in sizes)
        {
            var presentsBefore = surface.Presents;
            var start = Stopwatch.GetTimestamp();
            surface.Resize(width, height);
            var idle = session.WaitForIdle();
            var problem = PictureProblem(session, 70, width, height);
            var targetProblem = CopyTargetProblem(session, width, height, out var description);
            targets.Add($"{width}x{height}: {description}");
            if (surface.Presents > presentsBefore)
            {
                resizeLatency.Add(Stopwatch.GetElapsedTime(start, surface.LastPresentAt).TotalMilliseconds);
            }

            if (!idle || problem is not null || targetProblem is not null)
            {
                wrong.Add($"{width}x{height}: {(idle ? string.Empty : "not idle; ")}{problem}{(problem is not null && targetProblem is not null ? "; " : string.Empty)}{targetProblem}");
            }
        }

        _report.Check($"paused: after each of {sizes.Length} size changes the buffer has the new size, the picture shows the same frames with the right colours, and the copy targets fit", wrong.Count == 0, wrong.Count == 0 ? null : string.Join(" | ", wrong.Take(4)));
        _report.Note($"copy targets (screen, camera) by surface size: {string.Join("; ", targets)}");
        _report.Note($"size change to the picture presented at the new size, paused: {resizeLatency.Summary()}");

        // A burst of size changes, as a window drag makes them: only the last one counts.
        var configuresBefore = surface.Configures;
        for (var step = 0; step < 60; step++)
        {
            surface.Resize(700 + (step * 7), 400 + (step * 4));
        }

        session.WaitForIdle();
        var burst = PictureProblem(session, 70, 700 + (59 * 7), 400 + (59 * 4));
        _report.Check("a burst of 60 size changes ends at the last size with the right picture", burst is null, $"{burst}; the surface was configured {surface.Configures - configuresBefore} time(s)");

        // No size at all, as a minimised window has: nothing is drawn, and it comes back.
        var presentsAtZero = surface.Presents;
        surface.Resize(0, 0);
        session.WaitForIdle();
        session.SeekTo(120);
        var drawnAtZero = surface.Presents - presentsAtZero;
        surface.Resize(1280, 720);
        session.WaitForIdle();
        var afterZero = PictureProblem(session, 120, 1280, 720);
        _report.Check("a surface of size zero is not drawn into, and the picture is right again when it has a size", drawnAtZero == 0 && afterZero is null && session.PositionFrame == 120, $"{afterZero}; presents at size zero {drawnAtZero}");

        // Sizes while playing.
        session.SeekTo(20);
        session.Recorder.Drain();
        session.Engine.Play();
        var playingSizes = new (int Width, int Height)[] { (640, 360), (1921, 1081), (480, 270), (1280, 720), (999, 563), (1600, 900) };
        var playingWrong = new List<string>();
        foreach (var (width, height) in playingSizes)
        {
            surface.Resize(width, height);
            Thread.Sleep(450);
            var composites = session.Recorder.Drain();
            var atSize = composites.Where(c => c.Width == width && c.Height == height && c.Screen != FrameCode.Unreadable).ToList();
            var backwards = 0;
            var apart = 0;
            for (var index = 0; index < atSize.Count; index++)
            {
                backwards += index > 0 && atSize[index].Screen < atSize[index - 1].Screen ? 1 : 0;
                var expected = session.ExpectedCamera(atSize[index].Screen);
                apart = Math.Max(apart, expected == FrameCode.Unreadable || atSize[index].Camera == FrameCode.Unreadable ? (expected == atSize[index].Camera ? 0 : 1) : Math.Abs(atSize[index].Camera - expected));
            }

            var unreadable = composites.Count(c => c.Width == width && c.Height == height && c.Screen == FrameCode.Unreadable);
            if (atSize.Count < 6 || backwards > 0 || apart > 1 || unreadable > 0 || surface.BufferSize != (width, height))
            {
                playingWrong.Add($"{width}x{height}: {atSize.Count} scenes at that size, {unreadable} unreadable, {backwards} went back, clips {apart} apart, buffer {surface.BufferSize}");
            }
        }

        session.Engine.Pause();
        session.WaitForIdle();
        var paused = session.PositionFrame;
        var finalProblem = PictureProblem(session, paused, 1600, 900);
        var finalTargets = CopyTargetProblem(session, 1600, 900, out var finalDescription);
        _report.Check($"playing: at each of {playingSizes.Length} sizes the frames keep coming in order with both clips right", playingWrong.Count == 0, playingWrong.Count == 0 ? null : string.Join(" | ", playingWrong.Take(3)));
        _report.Check("playing: after the pause the picture and the copy targets are right at the last size", finalProblem is null && finalTargets is null, $"{finalProblem} {finalTargets} ({finalDescription})");

        // Detach and attach another while playing.
        session.SeekTo(40);
        session.Engine.Play();
        Thread.Sleep(250);
        session.Detach();
        Thread.Sleep(150);
        var replacement = session.Attach(1024, 576);
        session.Recorder.Drain();
        Thread.Sleep(450);
        var onReplacement = session.Recorder.Drain().Where(c => c.Width == 1024 && c.Screen != FrameCode.Unreadable).ToList();
        var third2 = session.Attach(800, 450);
        session.Recorder.Drain();
        Thread.Sleep(450);
        var onThird = session.Recorder.Drain().Where(c => c.Width == 800 && c.Screen != FrameCode.Unreadable).ToList();
        session.Engine.Pause();
        session.WaitForIdle();
        var replacedProblem = PictureProblem(session, session.PositionFrame, 800, 450);
        _report.Check(
            "playing: detaching, attaching another and replacing it keep the picture going on the surface that is attached",
            onReplacement.Count > 6 && onThird.Count > 6 && IsOrdered(onReplacement) && IsOrdered(onThird) && replacedProblem is null && replacement.BufferSize == (0, 0) && third2.BufferSize == (800, 450),
            $"{onReplacement.Count} scenes on the second, {onThird.Count} on the third; {replacedProblem}");
        _report.Check("no failure was reported", session.Events.FailedEvents == 0, string.Join("; ", session.Events.Failures()));
        var changes = session.Engine.GetDiagnostics().CopyTargetChanges;
        _report.Note($"copy targets were recreated {changes} time(s) in this group");
        Close(session);
        _report.Check("disposing the engine releases the attached surface's buffers", third2.BufferSize == (0, 0) && third2.Releases >= 1);
    }

    private static bool IsOrdered(List<Composite> composites)
    {
        for (var index = 1; index < composites.Count; index++)
        {
            if (composites[index].Screen < composites[index - 1].Screen)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>What is wrong with the surface's picture, or null: its size, the frames it shows, and the colours of the screen's patches.</summary>
    private static string? PictureProblem(Session session, int frame, int width, int height)
    {
        var picture = session.ReadPicture();
        if (picture is null)
        {
            return "no picture";
        }

        if (picture.Width != width || picture.Height != height)
        {
            return $"the buffer is {picture.Width}x{picture.Height}";
        }

        var view = session.View(width, height);
        var shown = picture.Shown(view, session.Folder.Screen, session.Folder.Camera);
        var camera = session.ExpectedCamera(frame);
        if (shown.Screen != frame || shown.Camera != camera)
        {
            return $"shows {shown}, wanted screen {frame}, camera {camera}";
        }

        // The patches: a stale or half-written texture would not have them in place.
        var patches = FrameCode.Patches(picture.Bgra, picture.Width, picture.Height, session.Folder.Screen, view.ScreenMap!.Value);
        for (var index = 0; index < patches.Length; index++)
        {
            var nominal = new Rgb(TestMedia.PatchColors[index].R, TestMedia.PatchColors[index].G, TestMedia.PatchColors[index].B);
            if (patches[index].Distance(nominal) > 40)
            {
                return $"the {TestMedia.PatchColors[index].Name} patch is {patches[index]}";
            }
        }

        return null;
    }

    /// <summary>
    /// Checks the textures the players copy into against how large each clip appears on a surface
    /// of this size: not smaller than it appears, not larger than the source, and not much larger
    /// than needed.
    /// </summary>
    private static string? CopyTargetProblem(Session session, int width, int height, out string description)
    {
        var view = session.View(width, height);
        var targets = session.Engine.GetDiagnostics().CopyTargets;
        var clips = new[] { session.Folder.Screen, session.Folder.Camera! };
        var maps = new[] { view.ScreenMap, view.CameraMap };
        var parts = new List<string>();
        string? problem = null;
        for (var index = 0; index < clips.Length; index++)
        {
            var (targetWidth, targetHeight) = targets[index];
            parts.Add($"{targetWidth}x{targetHeight}");
            if (maps[index] is not { } map)
            {
                continue;
            }

            // Pixels on the surface per source pixel.
            var appears = Math.Max(Math.Abs(map.ScaleX), Math.Abs(map.ScaleY));
            var scale = Math.Min((double)targetWidth / clips[index].Width, (double)targetHeight / clips[index].Height);
            var name = index == 0 ? "screen" : "camera";
            if (targetWidth > clips[index].Width || targetHeight > clips[index].Height)
            {
                problem ??= $"the {name} copy target {targetWidth}x{targetHeight} is larger than the source";
            }
            else if (scale < Math.Min(1, appears) - 0.02)
            {
                problem ??= $"the {name} copy target {targetWidth}x{targetHeight} is smaller than the clip appears ({F(appears * clips[index].Width, "0")} px wide)";
            }
            else if (scale > Math.Max(2.1 * appears, 0.1) && scale > 64.0 / Math.Max(clips[index].Width, clips[index].Height) + 0.02)
            {
                problem ??= $"the {name} copy target {targetWidth}x{targetHeight} is more than twice what is needed ({F(appears * clips[index].Width, "0")} px wide)";
            }
        }

        description = string.Join(", ", parts);
        return problem;
    }
}
