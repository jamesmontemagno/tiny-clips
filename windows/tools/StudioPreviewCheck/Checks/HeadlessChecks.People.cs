using System.Diagnostics;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Preview;
using TinyClips.Tools.StudioPreviewCheck.Media;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

// A camera whose background is blurred or removed (camera.cutout, section 6.7 of the project
// format). The renderer looks for the people in the camera picture it is given, which takes a
// model some milliseconds, and keeps what it made of the picture for as long as it is given the
// same picture again. Which picture it is, the engine can say with a stamp on the frame
// (StudioGpuVideoFrame.Stamp): the same texture with the same stamp is the same picture, and 0
// says nothing. So a stamp that stays when the picture changes shows an old camera frame, and a
// stamp of 0 has the people looked for at every redraw of a paused preview, which is every move
// of a slider. The engine gives every frame with stamp 0 by itself, as it always has: the stamps
// are behind a switch (StudioPreviewOptions.StampPictures) that is off until these checks have
// run, because the app ships the model and a wrong stamp would show in it. The checks here, and
// --people, switch it on.
//
// The checks give the engine a stand-in for the model (StandInFinders) that counts how often it
// is asked and calls the whole frame a person, and they read, for every scene drawn, the camera
// frame in the scene and the camera frame in the texture that scene was drawn from. What the
// renderer draws for a camera with a cutout is a picture it made when it looked, so the two
// differ exactly when the renderer took a new picture for one it had seen.
//
//   --only people            the checks: only when named, until they have been run
//   --people <ms>[,<ms>]     another thing: every preview of the run gets a camera whose
//                            background is removed and a stand-in that takes that long for a
//                            look, and that long to be made (the model being loaded). With
//                            the other groups it says what a finder's time does to them
internal sealed partial class HeadlessChecks
{
    private static Func<StudioProject, StudioProject> WithCutout(StudioCameraCutout cutout) =>
        project => project.Sources.Camera is null ? project : project with { Camera = project.Camera with { Cutout = cutout } };

    /// <summary>
    /// Reads, for every scene the engine draws, which frame a clip's picture holds: the texture
    /// that scene was drawn from. It reads right after the scene's own pixels were read, on the
    /// render thread with the device lock held, so the two belong to the same draw. Only the
    /// strip that holds the frame's number is read, as of the scene.
    /// </summary>
    private sealed class PictureWatch : IDisposable
    {
        private readonly object _sync = new();
        private readonly RegionReader _reader = new();
        private readonly Session _session;
        private readonly Action<TinyClips.Core.Studio.Rendering.StudioGraphicsDevice, Vortice.Direct3D11.ID3D11Texture2D, int, int>? _inner;
        private List<(long At, int Frame)> _held = [];

        public PictureWatch(Session session, int clip)
        {
            _session = session;
            _inner = session.Engine.AfterRender;
            var spec = clip == 0 ? session.Folder.Screen : session.Folder.Camera!;
            session.Engine.AfterRender = (graphics, target, width, height) =>
            {
                _inner?.Invoke(graphics, target, width, height);
                var frame = FrameCode.Unreadable;
                if (session.Engine.PictureDrawnFrom(clip, out var pictureWidth, out var pictureHeight) is { } texture)
                {
                    var map = ClipMap.Scaled(spec, pictureWidth, pictureHeight);
                    Span<(int X, int Y, int Width, int Height)> regions = stackalloc (int, int, int, int)[1];
                    regions[0] = FrameCode.Bounds(spec, map, pictureWidth, pictureHeight);
                    var pixels = _reader.Read(graphics, texture, regions);
                    frame = FrameCode.Decode(pixels[0], regions[0].Width, regions[0].Height, spec, map.Offset(-regions[0].X, -regions[0].Y));
                }

                lock (_sync)
                {
                    _held.Add((Stopwatch.GetTimestamp(), frame));
                }
            };
        }

        /// <summary>One entry for every scene drawn since the last call, in the order they were drawn.</summary>
        public List<(long At, int Frame)> Drain()
        {
            lock (_sync)
            {
                var result = _held;
                _held = [];
                return result;
            }
        }

        /// <summary>Gives the engine its hook back. With the engine at rest, and before it is closed.</summary>
        public void Dispose()
        {
            _session.Engine.AfterRender = _inner;
            lock (_session.Engine.GraphicsDevice.Gate)
            {
                _reader.Dispose();
            }
        }
    }

    private void People()
    {
        PeopleWithoutStamps();
        var kinds = HoldUpKinds("none", "collector");
        for (var index = 0; index < kinds.Length; index++)
        {
            People(kinds[index], atRest: index == 0);
        }
    }

    /// <summary>
    /// The engine as it runs by itself, with the stamps off: every frame is given to the
    /// renderer with stamp 0, so the people are looked for at every draw. That is what the app
    /// does today, and it is the control for the checks that follow: with the stamps on, the
    /// same ten redraws must not look once.
    /// </summary>
    private void PeopleWithoutStamps()
    {
        _report.Section("A camera whose background is removed, with the stamps off, as the engine runs by itself: the people are looked for at every draw");
        var finders = new StandInFinders();
        var session = OpenSession(TestMedia.Camera, edit: WithCutout(StudioCameraCutout.Remove), options: Muted with { PersonFinderFactory = finders.Make, StampPictures = false });
        try
        {
            const int frame = 90;
            var landed = session.SeekTo(frame);
            var original = session.Project;
            var asked = finders.Calls;
            var scenesBefore = session.Engine.GetDiagnostics().FramesDrawn;
            var redrawn = 0;
            for (var index = 0; index < 10; index++)
            {
                var drawn = session.Engine.GetDiagnostics().FramesDrawn;
                session.Update(WithBackground(original, $"#{(index * 23) + 16:X2}{240 - (index * 19):X2}80"));
                session.WaitForIdle();
                redrawn += session.Engine.GetDiagnostics().FramesDrawn > drawn ? 1 : 0;
            }

            var again = finders.Calls - asked;
            var scenes = session.Engine.GetDiagnostics().FramesDrawn - scenesBefore;
            var shown = session.ReadShown();
            _report.Check(
                "with the stamps off, redrawn ten times while paused: the people are looked for at every one of the draws, and the picture is right",
                landed && asked >= 1 && redrawn == 10 && again == scenes && shown == new Shown(frame, session.ExpectedCamera(frame)),
                $"asked {again} times more for {scenes} scenes drawn ({redrawn} of the 10 changes were drawn); {shown}; {finders.Describe()}");
        }
        finally
        {
            Close(session);
        }
    }

    private void People(string kind, bool atRest)
    {
        using var holdUps = new HoldUps(kind, HoldUpMilliseconds);
        _report.Section($"A camera whose background is removed, with {holdUps.Name}: the people are looked for once in every camera picture, and the picture drawn is the one that was looked at");
        var finders = new StandInFinders();
        // The stamps are off in the engine until these checks have run: they are what is looked at here.
        var session = OpenSession(TestMedia.Camera, edit: WithCutout(StudioCameraCutout.Remove), options: holdUps.With(Muted) with { PersonFinderFactory = finders.Make, StampPictures = true });
        try
        {
            using var pictures = new PictureWatch(session, clip: 1);
            if (atRest)
            {
                PeopleAtRest(session, finders);
            }

            PeopleWhilePlaying(session, finders, pictures, holdUps, heldUp: kind != "none");
            if (atRest)
            {
                PeopleAfterALostDevice(session, finders);
            }

            _report.Check($"with {holdUps.Name}: no failure was reported", session.Events.FailedEvents == 0, string.Join("; ", session.Events.Failures()));
        }
        finally
        {
            Close(session);
        }

        _report.Check($"with {holdUps.Name}: every finder the engine's renderers were given is disposed when the preview is closed", finders.Made >= 1 && finders.Disposed == finders.Made, finders.Describe());
    }

    private void PeopleAtRest(Session session, StandInFinders finders)
    {
        // Before the camera's first frame it is no part of the picture.
        var early = session.SeekTo(2);
        var shown = session.ReadShown();
        _report.Check(
            "where the camera is no part of the picture, nobody is looked for",
            early && finders.Calls == 0 && shown == new Shown(2, FrameCode.Unreadable),
            $"{shown}; {finders.Describe()}");

        const int frame = 90;
        var landed = session.SeekTo(frame);
        shown = session.ReadShown();
        var asked = finders.Calls;
        _report.Check(
            "paused where the camera shows: the people were looked for, and the scene shows the camera frame that goes with the screen's",
            landed && asked >= 1 && shown == new Shown(frame, session.ExpectedCamera(frame)),
            $"{shown}; {finders.Describe()}");

        // Redrawn ten times, as when a slider is dragged over something that leaves the size of
        // the camera's picture alone.
        var original = session.Project;
        var scenesBefore = session.Engine.GetDiagnostics().FramesDrawn;
        var redrawn = 0;
        for (var index = 0; index < 10; index++)
        {
            var drawn = session.Engine.GetDiagnostics().FramesDrawn;
            session.Update(WithBackground(original, $"#{(index * 23) + 16:X2}{240 - (index * 19):X2}80"));
            session.WaitForIdle();
            redrawn += session.Engine.GetDiagnostics().FramesDrawn > drawn ? 1 : 0;
        }

        var again = finders.Calls - asked;
        shown = session.ReadShown();
        _report.Check(
            "redrawn ten times while paused, the people are looked for no more: the picture says that it is the same",
            again == 0 && redrawn == 10 && shown == new Shown(frame, session.ExpectedCamera(frame)),
            $"asked {again} times more for {session.Engine.GetDiagnostics().FramesDrawn - scenesBefore} scenes drawn ({redrawn} of the 10 changes were drawn); {shown}");
        session.Update(original);
        session.WaitForIdle();

        // A seek, two steps of one frame, a seek back and one far on: every new picture is looked at.
        var wrong = new List<string>();
        var looks = new List<long>();
        foreach (var target in (ReadOnlySpan<int>)[120, 121, 122, 60, 200])
        {
            var before = finders.Calls;
            landed = session.SeekTo(target);
            shown = session.ReadShown();
            var more = finders.Calls - before;
            looks.Add(more);
            if (!landed || more < 1 || shown != new Shown(target, session.ExpectedCamera(target)))
            {
                wrong.Add($"frame {target}: {shown}, asked {more} times more{(landed ? string.Empty : "; the picture did not get there")}");
            }
        }

        _report.Check(
            "after a seek and after a step of one frame, the new camera picture is looked at and is the one drawn",
            wrong.Count == 0,
            wrong.Count == 0 ? $"asked {string.Join(", ", looks)} times for the five" : string.Join(" | ", wrong));
    }

    private void PeopleWhilePlaying(Session session, StandInFinders finders, PictureWatch watch, HoldUps holdUps, bool heldUp)
    {
        var plays = _options.Number("count", _quick ? 2 : 4);
        var wrong = new List<string>();
        var restsWrong = new List<string>();
        var compared = 0;
        var furthest = 0;
        var scenesInAll = 0;
        var changesInAll = 0;
        long askedInAll = 0;

        // The players are their own masters and can be a frame apart; held up, a screen frame
        // can be without a number for a while, and the camera goes on without it.
        var mostApart = heldUp ? Fps / 2 : 1;
        holdUps.Begin();
        try
        {
            for (var play = 0; play < plays; play++)
            {
                session.SeekTo(30 + (play * 7));
                var previous = session.ReadClipFrame(1);
                session.Recorder.Drain();
                watch.Drain();
                var before = finders.Calls;
                session.Engine.Play();
                Thread.Sleep(2500);
                session.Engine.Pause();
                var idle = session.WaitForIdle();

                // At rest nothing is drawn, so the two lists have an entry for the same draws.
                var scenes = session.Recorder.Drain();
                var pictures = watch.Drain();
                var asked = finders.Calls - before;
                if (scenes.Count != pictures.Count)
                {
                    wrong.Add($"play {play + 1}: {scenes.Count} scenes were read and {pictures.Count} pictures");
                }

                var changes = 0;
                for (var index = 0; index < Math.Min(scenes.Count, pictures.Count); index++)
                {
                    var scene = scenes[index];
                    var picture = pictures[index].Frame;
                    changes += picture != previous ? 1 : 0;
                    previous = picture;
                    var wanted = scene.Screen == FrameCode.Unreadable ? FrameCode.Unreadable : session.ExpectedCamera(scene.Screen);
                    if (wanted == FrameCode.Unreadable)
                    {
                        continue;
                    }

                    compared++;
                    if (scene.Camera != picture)
                    {
                        wrong.Add($"play {play + 1}, screen frame {scene.Screen}: the scene shows camera {Named(scene.Camera)} and was drawn from a picture that holds {Named(picture)}");
                    }
                    else if (Math.Abs(scene.Camera - wanted) > mostApart)
                    {
                        wrong.Add($"play {play + 1}, screen frame {scene.Screen}: camera {Named(scene.Camera)}, where frame {wanted} goes with the screen's");
                    }
                    else
                    {
                        furthest = Math.Max(furthest, Math.Abs(scene.Camera - wanted));
                    }
                }

                // A look is made inside a draw, one at the most, and only for a picture the
                // renderer has not just seen: no fewer than the times the camera's frame
                // changed from one scene to the next, no more than there were scenes.
                if (asked < changes || asked > scenes.Count)
                {
                    wrong.Add($"play {play + 1}: the people were looked for {asked} times, in {scenes.Count} scenes between which the camera's picture changed {changes} times");
                }

                var position = session.PositionFrame;
                var shown = session.ReadShown();
                if (!idle || shown != new Shown(position, session.ExpectedCamera(position)))
                {
                    restsWrong.Add($"play {play + 1}: Position is frame {position} and the scene shows {shown}{(idle ? string.Empty : "; the engine did not come to rest")}");
                }

                scenesInAll += scenes.Count;
                changesInAll += changes;
                askedInAll += asked;
            }
        }
        finally
        {
            holdUps.Rest();
        }

        _report.Check(
            $"playing with {holdUps.Name}: every scene shows the camera frame that the picture it was drawn from holds, the people are looked for once for each camera picture drawn, and the camera is never more than {(mostApart == 1 ? "one frame" : mostApart + " frames")} from the frame that goes with the screen's ({plays} plays of 2.5 s)",
            wrong.Count == 0 && compared >= plays * 40,
            wrong.Count == 0
                ? $"{compared} scenes compared; the people were looked for {askedInAll} times in {scenesInAll} scenes between which the camera's picture changed {changesInAll} times; the camera at most {furthest} frame{(furthest == 1 ? string.Empty : "s")} from the screen's"
                : $"{wrong.Count} wrong of {compared} scenes compared; first: {string.Join(" | ", wrong.Take(3))}{Dump(session, "people")}");
        _report.Check(
            $"paused after playing with {holdUps.Name}: the scene shows the frame Position names, with the camera frame that goes with it",
            restsWrong.Count == 0,
            restsWrong.Count == 0 ? null : string.Join(" | ", restsWrong.Take(3)));
        if (holdUps.Kind != "none")
        {
            _report.Note($"people, with {holdUps.Name}: {holdUps.Describe()}");
        }
    }

    private void PeopleAfterALostDevice(Session session, StandInFinders finders)
    {
        const int frame = 150;
        session.SeekTo(frame);
        var made = finders.Made;
        var disposed = finders.Disposed;
        var asked = finders.Calls;
        var rebuilds = session.Engine.GetDiagnostics().DeviceRebuilds;
        session.Engine.SimulateDeviceLoss();
        var idle = session.WaitForIdle(10);
        var shown = session.ReadShown();
        _report.Check(
            "after a lost device the new renderer is given a finder of its own, the old one's is disposed, and the camera picture is looked at again",
            idle && session.Engine.GetDiagnostics().DeviceRebuilds == rebuilds + 1 && finders.Made == made + 1 && finders.Disposed == disposed + 1 && finders.Calls > asked && shown == new Shown(frame, session.ExpectedCamera(frame)),
            $"{shown}; rebuilds {session.Engine.GetDiagnostics().DeviceRebuilds - rebuilds}, finders made {finders.Made - made}, disposed {finders.Disposed - disposed}, asked {finders.Calls - asked} times more");
    }

    private static string Named(int frame) => frame == FrameCode.Unreadable ? "nothing that can be read" : $"frame {frame}";
}
