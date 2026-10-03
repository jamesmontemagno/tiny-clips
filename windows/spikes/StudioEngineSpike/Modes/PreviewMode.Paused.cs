using System.Diagnostics;
using StudioEngineSpike.Engine;

namespace StudioEngineSpike.Modes;

internal sealed partial class PreviewSession
{
    // ---------------------------------------------------------------------------------------
    // Re-render on demand while paused
    // ---------------------------------------------------------------------------------------

    private void DragWhilePaused()
    {
        _report.Section("Re-render on demand while paused (dragging the camera bubble; players are not touched)");
        const int frame = 520;
        SeekAndWait(MidFrame(frame), frame, frame - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(3));
        var callbacksBefore = _engine.Screen.FrameCount + _engine.Camera.FrameCount;
        _renderPaused = true;
        try
        {
            foreach (var mode in new[] { ShadowMode.Cached, ShadowMode.Live })
            {
                var total = new Samples();
                var wrong = 0;
                var rebuildsBefore = _renderer.ShadowRebuilds;
                const int steps = 120;
                for (var step = 0; step < steps; step++)
                {
                    var t = step / (double)(steps - 1);
                    var dragged = ResolveLayout(_settings with { BubbleOffsetX = -0.62 * t, BubbleOffsetY = -0.12 * Math.Sin(t * Math.PI) }, _canvasWidth, _canvasHeight);
                    int screenFrame;
                    int cameraFrame;
                    lock (_compositeGate)
                    {
                        lock (_graphics.Gate)
                        {
                            var start = Stopwatch.GetTimestamp();
                            _renderer.Render(_canvas, dragged, _engine.Screen.Source, _engine.Camera.Source, new RenderOptions(mode));
                            _graphics.WaitForGpu();
                            total.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                        }

                        screenFrame = _reader.Read(_canvas, 0, _canvasWidth, _canvasHeight, TestMedia.Screen, dragged.ScreenMap(TestMedia.Screen));
                        cameraFrame = _reader.Read(_canvas, 0, _canvasWidth, _canvasHeight, TestMedia.Camera, dragged.CameraMap(TestMedia.Camera));
                    }

                    if (screenFrame != frame || cameraFrame != frame - TestMedia.CameraFrameLag)
                    {
                        wrong++;
                    }

                    if (mode == ShadowMode.Cached && (step == 0 || step == steps / 2 || step == steps - 1))
                    {
                        SavePng($"drag-{(step == 0 ? "start" : step == steps - 1 ? "end" : "mid")}.png");
                    }
                }

                _report.Line($"shadows {mode}: {steps} composites at new bubble positions, {steps - wrong} show screen {frame} and camera {frame - TestMedia.CameraFrameLag} at the moved position; {total.Summary()}; cached shadow rebuilds {_renderer.ShadowRebuilds - rebuildsBefore}");
                if (wrong > 0)
                {
                    _failures++;
                }
            }
        }
        finally
        {
            _renderPaused = false;
        }

        _report.Line($"VideoFrameAvailable callbacks during the drag: {_engine.Screen.FrameCount + _engine.Camera.FrameCount - callbacksBefore} (the textures simply keep the last frame)");
    }

    /// <summary>Can a frame be pulled again outside the callback, e.g. into a new texture after a resize?</summary>
    private void OnDemandCopy()
    {
        _report.Section("CopyFrameToVideoSurface outside the VideoFrameAvailable callback (paused)");
        const int frame = 610;
        SeekAndWait(MidFrame(frame), frame, frame - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(3));
        foreach (var track in _engine.Tracks)
        {
            var expected = track.Index == 0 ? frame : frame - TestMedia.CameraFrameLag;
            foreach (var scale in new[] { 1.0, 0.5 })
            {
                var width = (int)(track.Clip.Width * scale);
                var height = (int)(track.Clip.Height * scale);
                using var texture = _graphics.CreateRenderTexture(width, height);
                var surface = _graphics.CreateSurface(texture);
                try
                {
                    var times = new Samples();
                    string outcome;
                    try
                    {
                        for (var repeat = 0; repeat < 20; repeat++)
                        {
                            lock (_graphics.Gate)
                            {
                                var start = Stopwatch.GetTimestamp();
                                track.Player.CopyFrameToVideoSurface(surface);
                                _graphics.WaitForGpu();
                                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                            }
                        }

                        var read = _reader.Read(texture, 0, width, height, track.Clip, new ClipMap(0, 0, scale, scale));
                        outcome = $"frame {read} (expected {expected}) {Verdict(read == expected)}; {times.Summary()} incl. GPU";
                        if (read != expected)
                        {
                            _failures++;
                        }
                    }
                    catch (Exception ex)
                    {
                        outcome = $"threw {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message.Trim()}";
                        _failures++;
                    }

                    _report.Line($"{track.Name} into a fresh {width}x{height} texture: {outcome}");
                }
                finally
                {
                    (surface as IDisposable)?.Dispose();
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Edges of the timeline
    // ---------------------------------------------------------------------------------------

    private void Edges()
    {
        _report.Section("Edges: before the camera's first frame, and the end of the screen clip");
        foreach (var frame in new[] { 3, 5, 6, 7 })
        {
            var result = SeekAndWait(MidFrame(frame), frame, expectedCamera: null, TimeSpan.FromSeconds(2), acceptAnyFrame: true);
            var cameraPosition = _engine.Camera.Player.PlaybackSession.Position;
            _report.Line($"controller in screen frame {frame} (camera time {((frame + 0.5) / Fps - TestMedia.CameraStartOffsetSeconds).F("+0.000;-0.000")} s): screen {result.ScreenFrame}, camera texture shows {result.CameraFrame} ({result.CameraEvents} callbacks), camera session position {cameraPosition.TotalSeconds.F("0.000")} s");
        }

        // Play from zero: when does the camera start delivering?
        SeekAndWait(TimeSpan.Zero, 0, expectedCamera: null, TimeSpan.FromSeconds(2), acceptAnyFrame: true);
        DrainFrameEvents();
        _engine.Controller.Resume();
        Thread.Sleep(1200);
        _engine.Controller.Pause();
        Thread.Sleep(400);
        var events = DrainFrameEvents();
        var firstCamera = events.Where(e => e.Track == 1 && e.Frame >= 0).OrderBy(e => e.Timestamp).Cast<FrameEvent?>().FirstOrDefault();
        var firstScreen = events.Where(e => e.Track == 0 && e.Frame >= 0).OrderBy(e => e.Timestamp).Cast<FrameEvent?>().FirstOrDefault();
        static string First(FrameEvent? e) => e is { } value ? $"frame {value.Frame} at controller {(value.ControllerTicks / 1e7).F("0.000")} s" : "none";
        _report.Line($"play from 0 for 1.2 s: first screen callback {First(firstScreen)}; first camera callback {First(firstCamera)} (camera frame 0 belongs at {TestMedia.CameraStartOffsetSeconds.F("0.000")} s)");

        // Run off the end of the screen clip.
        SeekAndWait(MidFrame(870), 870, 870 - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(3));
        DrainFrameEvents();
        while (_engine.StateChanges.TryDequeue(out _))
        {
        }

        _engine.Controller.Resume();
        Thread.Sleep(2500);
        var stateWhileRunning = _engine.Controller.State;
        var positionWhileRunning = _engine.Controller.Position;
        _engine.Controller.Pause();
        Thread.Sleep(300);
        events = DrainFrameEvents();
        var lastScreen = events.Where(e => e.Track == 0 && e.Frame >= 0).Select(e => e.Frame).DefaultIfEmpty(-1).Max();
        var lastCamera = events.Where(e => e.Track == 1 && e.Frame >= 0).Select(e => e.Frame).DefaultIfEmpty(-1).Max();
        _report.Line($"play from frame 870 for 2.5 s (the screen clip ends at 30.000 s): controller reached {positionWhileRunning.TotalSeconds.F("0.000")} s in state {stateWhileRunning}; last screen frame {lastScreen}, last camera frame {lastCamera}; state changes: {string.Join(", ", _engine.StateChanges.Select(c => c.State))}");
        _report.Line($"Controller.Duration = {(_engine.Controller.Duration is { } duration ? duration.TotalSeconds.F("0.000") + " s" : "null")}");

        // And back from the end: does a seek still work after a player reported MediaEnded?
        var back = SeekAndWait(MidFrame(400), 400, 400 - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(3));
        _report.Line($"seek back to frame 400 after running off the end: screen {back.ScreenFrame}, camera {back.CameraFrame} {Verdict(back.Correct)}");
        if (!back.Correct)
        {
            _failures++;
        }

        // Controller.Duration is how the app would stop at a trim-out point. Set it to 15 s, play from 14 s.
        var ended = 0;
        long endedTimestamp = 0;
        void OnEnded(Windows.Media.MediaTimelineController sender, object args)
        {
            Interlocked.Increment(ref ended);
            Interlocked.CompareExchange(ref endedTimestamp, Stopwatch.GetTimestamp(), 0);
        }

        _engine.Controller.Ended += OnEnded;
        try
        {
            _engine.Controller.Duration = TimeSpan.FromSeconds(15);
            SeekAndWait(MidFrame(420), 420, 420 - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(3));
            DrainFrameEvents();
            while (_engine.StateChanges.TryDequeue(out _))
            {
            }

            var start = Stopwatch.GetTimestamp();
            _engine.Controller.Resume();
            Thread.Sleep(2500);
            var state = _engine.Controller.State;
            var position = _engine.Controller.Position;
            if (state == Windows.Media.MediaTimelineControllerState.Running)
            {
                _engine.Controller.Pause();
            }

            Thread.Sleep(300);
            var afterEnd = DrainFrameEvents();
            var lastScreenFrame = afterEnd.Where(e => e.Track == 0 && e.Frame >= 0).Select(e => e.Frame).DefaultIfEmpty(-1).Max();
            var lastCameraFrame = afterEnd.Where(e => e.Track == 1 && e.Frame >= 0).Select(e => e.Frame).DefaultIfEmpty(-1).Max();
            var endedAfter = ended > 0 ? Stopwatch.GetElapsedTime(start, Interlocked.Read(ref endedTimestamp)).TotalSeconds.F("0.000") + " s after Resume()" : "never";
            _report.Line($"Controller.Duration = 15 s, play from 14.017 s for 2.5 s: Ended raised {ended}× ({endedAfter}); state {state}, position {position.TotalSeconds.F("0.000")} s; last screen frame {lastScreenFrame} (frame 449 is the last one before 15 s), last camera frame {lastCameraFrame}; states: {string.Join(", ", _engine.StateChanges.Select(c => c.State))}");
            var afterDuration = SeekAndWait(MidFrame(600), 600, 600 - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(2), acceptAnyFrame: true);
            _report.Line($"with Duration = 15 s, a paused seek to 20.017 s (frame 600) shows screen {afterDuration.ScreenFrame}, camera {afterDuration.CameraFrame}; controller position {_engine.Controller.Position.TotalSeconds.F("0.000")} s");
        }
        catch (Exception ex)
        {
            _report.Line($"Controller.Duration test threw {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message.Trim()}");
        }
        finally
        {
            _engine.Controller.Ended -= OnEnded;
            try
            {
                _engine.Controller.Duration = null;
            }
            catch (Exception ex)
            {
                _report.Line($"clearing Controller.Duration threw 0x{ex.HResult:X8}");
            }
        }

        var restored = SeekAndWait(MidFrame(500), 500, 500 - TestMedia.CameraFrameLag, TimeSpan.FromSeconds(3));
        _report.Line($"Duration cleared, seek to frame 500: screen {restored.ScreenFrame}, camera {restored.CameraFrame} {Verdict(restored.Correct)}");
    }

}
