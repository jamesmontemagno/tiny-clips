using System.Diagnostics;
using TinyClips.Tools.StudioPreviewCheck.Media;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

// The engine on the software adapter, which is what StudioGraphicsDevice.CreateHardware() gives a
// PC without graphics hardware.
internal sealed partial class HeadlessChecks
{
    private void SoftwareDevice()
    {
        _report.Section("The software adapter (WARP): the device of a PC without graphics hardware");
        _report.Line("The engine is made to create the software device. The players are the same as in every other check: on this PC they still decode with the graphics hardware, so this shows that the engine and the renderer work on that device, not how fast such a PC would be.");
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
            _report.Check("the preview opens on the software adapter", false, $"{ex.GetType().Name}: {Shorten(ex.Message)}");
            return;
        }

        var graphics = session.Engine.GraphicsDevice;
        _report.Check("the preview opens on the software adapter", graphics.IsSoftware, $"{graphics.AdapterName}, opened in {F(session.OpenMilliseconds, "0")} ms");
        var shown = session.ReadShown();
        _report.Check("the picture shows the first frame of both clips", shown == new Shown(0, session.ExpectedCamera(0)), shown.ToString());

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

        // Playback: the order of what is drawn is judged; how much of it gets drawn is reported.
        const int start = 30;
        var seconds = _quick ? 2 : 4;
        session.SeekTo(start);
        session.Recorder.Drain();
        var played = Stopwatch.StartNew();
        session.Engine.Play();
        Thread.Sleep(seconds * 1000);
        session.Engine.Pause();
        var wall = played.Elapsed.TotalSeconds;
        var idle = session.WaitForIdle();
        var scenes = session.Recorder.Drain().Where(c => c.Screen != FrameCode.Unreadable).ToList();
        var backwards = 0;
        var worstApart = 0;
        for (var index = 0; index < scenes.Count; index++)
        {
            backwards += index > 0 && scenes[index].Screen < scenes[index - 1].Screen ? 1 : 0;
            var camera = session.ExpectedCamera(scenes[index].Screen);
            if (camera != FrameCode.Unreadable && scenes[index].Camera != FrameCode.Unreadable)
            {
                worstApart = Math.Max(worstApart, Math.Abs(scenes[index].Camera - camera));
            }
        }

        var distinct = scenes.Select(c => c.Screen).Where(f => f > start).Distinct().Count();
        var reached = scenes.Count == 0 ? start : scenes[^1].Screen;
        shown = session.ReadShown();
        _report.Check(
            $"{seconds} s of playback: frames are drawn in order, the clock runs at 1x, and after Pause() both clips are on matching frames with Position on the frame shown",
            idle && scenes.Count > 10 && backwards == 0 && Math.Abs((reached - start) - (wall * Fps)) <= 0.1 * wall * Fps && shown.Screen == session.PositionFrame && shown.Camera == session.ExpectedCamera(shown.Screen),
            $"{scenes.Count} scenes, {backwards} went back, reached frame {reached} from {start} in {F(wall, "0.00")} s; at rest {shown}, Position frame {session.PositionFrame}");
        _report.Note($"on the software adapter, playback drew {distinct} of the {Math.Max(1, reached - start)} frames it passed ({F(100.0 * distinct / Math.Max(1, reached - start), "0")} %); the clips were at most {worstApart} frame(s) apart");
        _report.Check("no failure was reported", session.Events.FailedEvents == 0, string.Join("; ", session.Events.Failures()));
        session.Close();
    }
}
