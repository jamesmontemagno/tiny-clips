using System.Diagnostics;
using TinyClips.Core.Capture;
using TinyClips.Core.Studio.Preview;
using TinyClips.Core.Studio.Rendering;
using TinyClips.Tools.StudioPreviewCheck.Media;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

// Closing the preview, and losing the graphics device.
internal sealed partial class HeadlessChecks
{
    private enum CloseMoment
    {
        WhilePlaying,
        DuringSeek,
        RightAfterOpen,
        DuringSeekThenPlay,
        Paused,
    }

    // ---------------------------------------------------------------------------------------
    // Dispose
    // ---------------------------------------------------------------------------------------

    private void Dispose()
    {
        _report.Section("Dispose: nothing afterwards, and the folder can be deleted at once");
        var wrong = new List<string>();
        var disposeTimes = new Dictionary<CloseMoment, Samples>();
        var filesClosed = new Samples();
        var moments = new[] { CloseMoment.WhilePlaying, CloseMoment.DuringSeek, CloseMoment.RightAfterOpen, CloseMoment.DuringSeekThenPlay };
        const int rounds = 20;
        for (var round = 0; round < rounds; round++)
        {
            var moment = moments[round % moments.Length];
            var problem = OpenAndClose(moment, out var milliseconds, out var closedAfter);
            if (!disposeTimes.TryGetValue(moment, out var samples))
            {
                disposeTimes[moment] = samples = new Samples();
            }

            samples.Add(milliseconds);
            filesClosed.Add(closedAfter);
            if (problem is not null)
            {
                wrong.Add($"round {round} ({moment}): {problem}");
            }
        }

        _report.Check($"{rounds} times in a row (while playing, during a seek, right after open, during a seek followed by Play): no event after DisposeAsync returned, the surface released, and the project folder deleted straight away", wrong.Count == 0, wrong.Count == 0 ? null : string.Join(" | ", wrong.Take(4)));
        foreach (var (moment, samples) in disposeTimes)
        {
            _report.Note($"DisposeAsync {moment}: {samples.Summary()}");
        }

        _report.Note($"of that, waiting for the media files to be released after the players were closed: {filesClosed.Summary()}");

        // Disposing twice, from two threads at once, and using the preview afterwards.
        {
            var folder = TestFolder.Create(_media, TestMedia.Camera, Late);
            var engine = Session.OpenEngine(Muted, folder, out _);
            var surface = new OffscreenSurface(640, 360);
            engine.AttachSurface(surface);
            engine.WaitForIdle(TimeSpan.FromSeconds(5));
            Exception? thrown = null;
            try
            {
                var first = Task.Run(() => engine.DisposeAsync().AsTask());
                var second = Task.Run(() => engine.DisposeAsync().AsTask());
                Task.WaitAll(first, second);
                engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
                engine.Seek(1);
                engine.Play();
                engine.Pause();
                engine.UpdateProject(folder.Project);
                engine.AttachSurface(new OffscreenSurface(64, 64));
                engine.DetachSurface(surface);
                _ = engine.Position;
                _ = engine.IsPlaying;
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            _report.Check("disposing twice at once, and calling every member afterwards, does nothing and throws nothing", thrown is null && !engine.IsPlaying && engine.Position == 0, thrown?.ToString() ?? $"IsPlaying {engine.IsPlaying}, Position {F(engine.Position, "0.###")} s after Seek(1) on the disposed preview");
            _report.Check("the folder can be deleted after that too", TryDelete(folder, out var error), error);
            folder.Dispose();
        }

        // Disposing from inside an event handler, which runs on the engine's own event thread.
        {
            var folder = TestFolder.Create(_media, TestMedia.Camera, Late);
            var engine = Session.OpenEngine(Muted, folder, out _);
            using var disposed = new ManualResetEventSlim(false);
            Task? closing = null;
            var eventsAfter = 0;
            var returned = false;
            engine.PositionChanged += (_, _) =>
            {
                if (Volatile.Read(ref returned))
                {
                    Interlocked.Increment(ref eventsAfter);
                    return;
                }

                if (closing is null)
                {
                    closing = engine.DisposeAsync().AsTask();
                    disposed.Set();
                }
            };
            engine.Play();
            var signalled = disposed.Wait(TimeSpan.FromSeconds(5));
            var finished = signalled && closing!.Wait(TimeSpan.FromSeconds(10));
            Volatile.Write(ref returned, true);
            var deleted = TryDelete(folder, out var error);
            Thread.Sleep(200);
            _report.Check("DisposeAsync called from a PositionChanged handler completes, and the folder can be deleted", finished && deleted && eventsAfter == 0, $"handler ran {signalled}, dispose finished {finished}, events afterwards {eventsAfter}; {error}");
            folder.Dispose();
        }

        FilesAtClose();

        // 20 open/close cycles: handles, threads, memory and GPU memory.
        _report.Section("20 open/close cycles: handles and memory");

        // Windows leaves kernel handles behind for every MediaPlayer and MediaTimelineController
        // that was ever created, whatever is done with them afterwards. That is measured first:
        // the same players, used the way the engine uses them, with nothing of the engine
        // involved. The engine is then held to it.
        const int platformCycles = 12;
        var platform = PlatformResidue(platformCycles, out var platformByType);
        _report.Note($"left behind without the engine, by two MediaPlayers on a MediaTimelineController that were opened, sought, played and closed {platformCycles} times: {F(platform)} lasting handles per cycle ({platformByType})");
        using var gpu = GpuMemory.TryCreate();
        using var process = Process.GetCurrentProcess();
        var readings = new List<(int Handles, int Threads, double PrivateMb, double GpuMb)>();
        const int cycles = 20;
        const int settled = 5;
        var censusSettled = new Dictionary<string, int>();
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var problem = OpenAndClose(cycle % 2 == 0 ? CloseMoment.WhilePlaying : CloseMoment.Paused, out _, out _);
            if (problem is not null)
            {
                wrong.Add($"cycle {cycle}: {problem}");
            }

            CollectGarbage();
            process.Refresh();
            readings.Add((process.HandleCount, process.Threads.Count, process.PrivateMemorySize64 / 1048576.0, gpu?.CurrentUsageMb() ?? -1));
            if (cycle == settled - 1)
            {
                censusSettled = HandleCensus.Take();
            }
        }

        var censusEnd = HandleCensus.Take();
        _report.Check($"{cycles} more open/close cycles each closed cleanly", wrong.Count == 0, wrong.Count == 0 ? null : string.Join(" | ", wrong.Take(3)));
        static double Mean(IEnumerable<double> values) => values.Average();
        var early = readings.Skip(settled).Take(5).ToList();
        var late = readings.Skip(cycles - 5).ToList();
        var lastingPerCycle = (HandleCensus.Lasting(censusEnd) - HandleCensus.Lasting(censusSettled)) / (double)(cycles - settled);
        var threadGrowth = Mean(late.Select(r => (double)r.Threads)) - Mean(early.Select(r => (double)r.Threads));
        var privateGrowth = Mean(late.Select(r => r.PrivateMb)) - Mean(early.Select(r => r.PrivateMb));
        var gpuGrowth = gpu is null ? 0 : Mean(late.Select(r => r.GpuMb)) - Mean(early.Select(r => r.GpuMb));
        _report.Note($"handles after each cycle: {string.Join(' ', readings.Select(r => r.Handles))}");
        _report.Note($"threads after each cycle: {string.Join(' ', readings.Select(r => r.Threads))}");
        _report.Note($"private memory after each cycle (MB): {string.Join(' ', readings.Select(r => F(r.PrivateMb, "0")))}");
        _report.Note(gpu is null
            ? "GPU memory: the adapter does not report per-process usage"
            : $"GPU memory of this process after each cycle (MB): {string.Join(' ', readings.Select(r => F(r.GpuMb, "0")))}");

        // The handle count does not reach a plateau, because of what Windows leaves behind. What
        // can be held to is that the engine adds nothing of its own. Handles of the thread pool
        // and of I/O in progress are left out of the comparison: they come and go by a dozen.
        const double margin = 3;
        _report.Check(
            $"an open/close cycle of the preview leaves no more lasting handles behind than the same players do without the engine (within {F(margin, "0")})",
            lastingPerCycle <= platform + margin,
            $"{F(lastingPerCycle)} per cycle over cycles {settled + 1}-{cycles} ({HandleCensus.Difference(censusSettled, censusEnd)}); the bare players {F(platform)} per cycle");
        _report.Check("the thread count stays on a plateau", threadGrowth <= 6, $"cycles 6-10 mean {F(Mean(early.Select(r => (double)r.Threads)))}, cycles 16-20 mean {F(Mean(late.Select(r => (double)r.Threads)))}");
        _report.Check("GPU memory stays on a plateau", gpuGrowth <= 24, gpu is null ? "not measured" : $"cycles 6-10 mean {F(Mean(early.Select(r => r.GpuMb)))} MB, cycles 16-20 mean {F(Mean(late.Select(r => r.GpuMb)))} MB ({(gpuGrowth >= 0 ? "+" : string.Empty)}{F(gpuGrowth)} MB)");
        _report.Note($"private memory: cycles 6-10 mean {F(Mean(early.Select(r => r.PrivateMb)))} MB, cycles 16-20 mean {F(Mean(late.Select(r => r.PrivateMb)))} MB ({(privateGrowth >= 0 ? "+" : string.Empty)}{F(privateGrowth)} MB)");
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Thread.Sleep(150);
    }

    /// <summary>
    /// What closing does about a media file that another program has open. A recording in the
    /// project folder is waited for, because the caller may be about to delete the folder. A
    /// screen recording outside it is the user's own video: nobody is about to delete it, so it is
    /// not waited for, and not opened without sharing to find out whether a player still has it.
    /// </summary>
    private void FilesAtClose()
    {
        // The control: this is what waiting for a file looks like, and how long it takes.
        var inFolder = TestFolder.Create(_media, camera: null);
        var held = CloseWhileHeld(inFolder, inFolder.Paths.ScreenPath);
        _report.Check(
            "the control: a recording in the project folder that another program has open is waited for when the preview closes, for 5 s, and then the engine says that it gave up",
            held.Played && held.State.FilesWaitedFor == 1 && !held.State.FilesClosed && held.DisposeMilliseconds is >= 4500 and <= 8000 && held.OtherStillReads,
            $"DisposeAsync took {F(held.DisposeMilliseconds, "0")} ms; waited for {held.State.FilesWaitedFor} file(s), released: {held.State.FilesClosed}");

        Directory.CreateDirectory(TestFolder.Root);
        var video = Path.Combine(TestFolder.Root, $"the-users-own-video-{Guid.NewGuid():N}.mp4");
        File.Copy(TestMedia.PathOf(_media, TestMedia.Screen), video);
        var outside = TestFolder.Create(
            _media,
            camera: null,
            writeScreen: false,
            edit: project => project with { Sources = project.Sources with { Screen = project.Sources.Screen with { File = video, External = true } } });
        var left = CloseWhileHeld(outside, video);
        _report.Check(
            "a screen recording outside the project folder (the user's own video) that another program has open: the preview plays it, closing does not wait for it, and the other program keeps it",
            left.Played && left.State.FilesWaitedFor == 0 && left.State.FilesClosed && left.DisposeMilliseconds < 1000 && left.OtherStillReads,
            $"DisposeAsync took {F(left.DisposeMilliseconds, "0")} ms; waited for {left.State.FilesWaitedFor} file(s); the other program could still read it: {left.OtherStillReads}");

        // The player lets go of that video in its own time. Nobody waits for it; it is reported.
        var letGo = Stopwatch.StartNew();
        var deleted = false;
        while (!deleted && letGo.Elapsed.TotalSeconds < 5)
        {
            try
            {
                File.Delete(video);
                deleted = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(5);
            }
        }

        _report.Note(deleted
            ? $"the player had let go of that video {F(letGo.Elapsed.TotalMilliseconds, "0")} ms after DisposeAsync returned and the other program closed it"
            : "the player had NOT let go of that video 5 s after DisposeAsync returned and the other program closed it");
    }

    /// <summary>
    /// Opens a preview of <paramref name="folder"/> while this tool holds <paramref name="heldPath"/>
    /// open for reading, as another program that plays the video does, and disposes the preview.
    /// </summary>
    private (bool Played, double DisposeMilliseconds, StudioPreviewDiagnostics State, bool OtherStillReads) CloseWhileHeld(TestFolder folder, string heldPath)
    {
        var other = new FileStream(heldPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Session? session = null;
        try
        {
            session = Session.Open(Muted, folder);
            var played = session.ReadShown().Screen == 0 && session.SeekTo(100) && session.ReadShown().Screen == 100;
            var watch = Stopwatch.StartNew();
            session.Engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
            var milliseconds = watch.Elapsed.TotalMilliseconds;
            var state = session.Engine.GetDiagnostics();
            var reads = false;
            try
            {
                other.Position = 0;
                reads = other.ReadByte() >= 0;
            }
            catch (IOException)
            {
            }

            return (played, milliseconds, state, reads);
        }
        finally
        {
            // The other program first, so that the session can delete its folder.
            other.Dispose();
            if (session is null)
            {
                folder.Dispose();
            }
            else
            {
                session.Close();
            }
        }
    }

    /// <summary>
    /// Opens two bare <see cref="MediaPlayer"/>s on one <see cref="MediaTimelineController"/>,
    /// muted, and does with them what the engine does: frames copied into textures on a device of
    /// their own, an offset, a position change, a little playback, and closed the engine's way.
    /// Returns the lasting kernel handles each such cycle leaves behind.
    /// </summary>
    private double PlatformResidue(int cycles, out string byType)
    {
        Uri[] files = [new(TestMedia.PathOf(_media, TestMedia.Screen)), new(TestMedia.PathOf(_media, TestMedia.Camera))];

        void Cycle()
        {
            var graphics = StudioGraphicsDevice.CreateHardware();
            var controller = new MediaTimelineController { Duration = TimeSpan.FromSeconds(TestMedia.Screen.Seconds) };
            var players = new List<(MediaPlayer Player, MediaSource Source, ID3D11Texture2D Texture, IDirect3DSurface Surface, TypedEventHandler<MediaPlayer, object> Frame)>();
            foreach (var file in files)
            {
                using var opened = new ManualResetEventSlim(false);
                var player = new MediaPlayer { AutoPlay = false, IsMuted = true, Volume = 0, IsVideoFrameServerEnabled = true };
                player.CommandManager.IsEnabled = false;
                player.TimelineController = controller;
                var texture = graphics.CreateRenderTexture(1280, 720);
                var surface = WgcInterop.CreateDirect3DSurface(texture);
                TypedEventHandler<MediaPlayer, object> frame = (sender, _) =>
                {
                    lock (graphics.Gate)
                    {
                        try
                        {
                            sender.CopyFrameToVideoSurface(surface);
                        }
                        catch (Exception)
                        {
                            // Closed in the meantime.
                        }
                    }
                };
                TypedEventHandler<MediaPlayer, object> open = (_, _) => opened.Set();
                player.VideoFrameAvailable += frame;
                player.MediaOpened += open;
                var source = MediaSource.CreateFromUri(file);
                player.Source = source;
                opened.Wait(TimeSpan.FromSeconds(5));
                player.MediaOpened -= open;
                players.Add((player, source, texture, surface, frame));
            }

            players[1].Player.TimelineControllerPositionOffset = TimeSpan.FromSeconds(-Late);
            controller.Position = TimeSpan.FromSeconds(TestFolder.TimeOf(100, 0.5));
            Thread.Sleep(200);
            controller.Resume();
            Thread.Sleep(300);
            controller.Pause();
            Thread.Sleep(100);
            foreach (var (player, source, _, _, frame) in players)
            {
                player.VideoFrameAvailable -= frame;
                player.TimelineController = null;
                player.Source = null;
                player.Dispose();
                source.Dispose();
            }

            lock (graphics.Gate)
            {
                foreach (var (_, _, texture, surface, _) in players)
                {
                    (surface as IDisposable)?.Dispose();
                    texture.Dispose();
                }
            }

            graphics.Dispose();
        }

        for (var warm = 0; warm < 3; warm++)
        {
            Cycle();
        }

        CollectGarbage();
        var before = HandleCensus.Take();
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            Cycle();
            CollectGarbage();
        }

        var after = HandleCensus.Take();
        byType = HandleCensus.Difference(before, after);
        return (HandleCensus.Lasting(after) - HandleCensus.Lasting(before)) / (double)cycles;
    }

    /// <summary>
    /// Opens a preview with a surface, disposes it at the given moment, and deletes the project
    /// folder the instant DisposeAsync has returned. Returns what went wrong, or null.
    /// </summary>
    private string? OpenAndClose(CloseMoment moment, out double disposeMilliseconds, out double filesClosedAfter, StudioPreviewOptions? options = null)
    {
        var folder = TestFolder.Create(_media, TestMedia.Camera, Late);
        var watch = Stopwatch.StartNew();
        StudioPreviewEngine engine;
        try
        {
            engine = Session.OpenEngine(options ?? Muted, folder, out _);
        }
        catch
        {
            folder.Dispose();
            throw;
        }

        _openTimes.Add(watch.Elapsed.TotalMilliseconds);
        var surface = new OffscreenSurface(1280, 720);
        engine.AttachSurface(surface);
        var log = new EventLog(engine);
        switch (moment)
        {
            case CloseMoment.WhilePlaying:
                engine.Seek(TestFolder.TimeOf(_random.Next(20, 200), 0.5));
                engine.Play();
                Thread.Sleep(250 + _random.Next(100));
                break;
            case CloseMoment.DuringSeek:
                engine.WaitForIdle(TimeSpan.FromSeconds(5));
                engine.Seek(TestFolder.TimeOf(_random.Next(20, 340), 0.5));
                Thread.Sleep(_random.Next(0, 30));
                break;
            case CloseMoment.DuringSeekThenPlay:
                engine.WaitForIdle(TimeSpan.FromSeconds(5));
                engine.Seek(TestFolder.TimeOf(_random.Next(20, 300), 0.5));
                engine.Play();
                Thread.Sleep(_random.Next(0, 60));
                break;
            case CloseMoment.Paused:
                engine.WaitForIdle(TimeSpan.FromSeconds(5));
                engine.Seek(TestFolder.TimeOf(_random.Next(20, 340), 0.5));
                engine.WaitForIdle(TimeSpan.FromSeconds(5));
                break;
            case CloseMoment.RightAfterOpen:
                break;
        }

        watch.Restart();
        engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var returnedAt = Stopwatch.GetTimestamp();
        disposeMilliseconds = watch.Elapsed.TotalMilliseconds;
        _disposeTimes.Add(disposeMilliseconds);

        // Straight away, and once only: this is what the editor does when a project is deleted.
        string? problem = null;
        try
        {
            folder.Delete();
        }
        catch (Exception ex)
        {
            problem = $"the folder could not be deleted: {ex.GetType().Name}: {Shorten(ex.Message)}";
        }

        var presentsAtReturn = surface.Presents;
        Thread.Sleep(120);
        var diagnostics = engine.GetDiagnostics();
        filesClosedAfter = diagnostics.FilesClosedAfterMilliseconds;
        if (log.LastEventAt > returnedAt)
        {
            problem ??= $"an event was raised {F(Stopwatch.GetElapsedTime(returnedAt, log.LastEventAt).TotalMilliseconds)} ms after DisposeAsync returned";
        }

        if (surface.BufferSize != (0, 0) || surface.Presents != presentsAtReturn)
        {
            problem ??= $"the surface was not released (buffer {surface.BufferSize}, {surface.Presents - presentsAtReturn} presents afterwards)";
        }

        if (!diagnostics.FilesClosed)
        {
            problem ??= "the engine gave up waiting for the media files to be released";
        }

        if (log.FailedEvents > 0)
        {
            problem ??= $"Failed was raised: {string.Join("; ", log.Failures())}";
        }

        folder.Dispose();
        return problem;
    }

    // ---------------------------------------------------------------------------------------
    // A lost device (simulated)
    // ---------------------------------------------------------------------------------------

    private void DeviceLoss()
    {
        _report.Section("A lost graphics device (simulated: the render thread is made to see one)");
        var session = OpenSession(TestMedia.Camera);
        session.SeekTo(120);
        var deviceBefore = session.Engine.GraphicsDevice;
        var before = session.Engine.GetDiagnostics();
        var watch = Stopwatch.StartNew();
        session.Engine.SimulateDeviceLoss();
        var idle = session.WaitForIdle(10);
        var rebuildMilliseconds = watch.Elapsed.TotalMilliseconds;
        var after = session.Engine.GetDiagnostics();
        var problem = PictureProblem(session, 120, 1280, 720);
        _report.Check(
            "paused: the device, renderer, textures and surface buffer are rebuilt and the same frame is shown again",
            idle && after.DeviceRebuilds == before.DeviceRebuilds + 1 && !ReferenceEquals(deviceBefore, session.Engine.GraphicsDevice) && problem is null && session.PositionFrame == 120 && session.Events.FailedEvents == 0,
            $"{problem}; rebuilds {after.DeviceRebuilds - before.DeviceRebuilds}, back to idle after {F(rebuildMilliseconds, "0")} ms, surface configured {session.Surface!.Configures} times");

        var seekAfter = session.SeekTo(200);
        var stepAfter = session.SeekTo(201);
        var shown = session.ReadShown();
        _report.Check("paused: seeks and steps work on the new device", seekAfter && stepAfter && shown.Screen == 201 && shown.Camera == session.ExpectedCamera(201), shown.ToString());

        // While playing.
        session.SeekTo(30);
        session.Recorder.Drain();
        session.Engine.Play();
        Thread.Sleep(500);
        var lostAt = Stopwatch.GetTimestamp();
        session.Engine.SimulateDeviceLoss();
        Thread.Sleep(900);
        var stillPlaying = session.Engine.IsPlaying;
        session.Engine.Pause();
        session.WaitForIdle();
        var composites = session.Recorder.Drain().Where(c => c.Screen != FrameCode.Unreadable).ToList();
        var afterLoss = composites.Where(c => c.At > lostAt).ToList();
        var gap = afterLoss.Count == 0 ? double.NaN : Stopwatch.GetElapsedTime(lostAt, afterLoss[0].At).TotalMilliseconds;
        var apart = 0;
        foreach (var composite in afterLoss.Skip(2))
        {
            var expected = session.ExpectedCamera(composite.Screen);
            apart = Math.Max(apart, composite.Camera == FrameCode.Unreadable || expected == FrameCode.Unreadable ? (composite.Camera == expected ? 0 : 1) : Math.Abs(composite.Camera - expected));
        }

        var final = PictureProblem(session, session.PositionFrame, 1280, 720);
        var rebuilds = session.Engine.GetDiagnostics().DeviceRebuilds;
        _report.Check(
            "playing: playback carries on across the rebuild, in order, with both clips right",
            stillPlaying && rebuilds == after.DeviceRebuilds + 1 && afterLoss.Count > 10 && IsOrdered(composites) && apart <= 1 && final is null && session.Events.FailedEvents == 0,
            $"{afterLoss.Count} scenes after the loss, the first {F(gap, "0")} ms after it; clips at most {apart} apart; after the pause: {final ?? "right"}");
        _report.Check("no failure was reported for a device that could be rebuilt", session.Events.FailedEvents == 0, string.Join("; ", session.Events.Failures()));

        // A device that is lost again before the rebuilt one has drawn anything: the preview gives up, once.
        session.SeekTo(60);
        session.Engine.Play();
        Thread.Sleep(200);
        session.Events.Clear();
        session.Engine.SimulateDeviceLoss(times: 2);
        var failedWatch = Stopwatch.StartNew();
        while (session.Events.FailedEvents == 0 && failedWatch.Elapsed.TotalSeconds < 5)
        {
            Thread.Sleep(5);
        }

        Thread.Sleep(300);
        var failures = session.Events.Failures();
        var playingEvents = session.Events.Playing();
        _report.Check("a device lost again straight after the rebuild raises Failed exactly once", failures.Count == 1, failures.Count == 0 ? "not raised" : string.Join(" | ", failures));
        _report.Check("the failed preview has stopped playing and said so once", !session.Engine.IsPlaying && playingEvents.Count == 1 && !playingEvents[0].IsPlaying, $"IsPlaying {session.Engine.IsPlaying}, IsPlayingChanged x{playingEvents.Count}");
        var positionsBefore = session.Events.PositionEvents;
        var positionBefore = session.Engine.Position;
        session.Engine.Seek(1);
        session.Engine.Play();
        Thread.Sleep(300);
        _report.Check("a failed preview ignores Seek and Play: it does not play, and its Position stays", !session.Engine.IsPlaying && session.Events.PositionEvents == positionsBefore && session.Engine.Position == positionBefore && session.Events.FailedEvents == 1, $"Position {F(positionBefore, "0.###")} s before, {F(session.Engine.Position, "0.###")} s after Seek(1)");
        var folder = session.Folder;
        var closeWatch = Stopwatch.StartNew();
        session.Engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var deleted = TryDelete(folder, out var error);
        _report.Check("a failed preview can be disposed, and the folder deleted at once", deleted, $"{error}; DisposeAsync took {F(closeWatch.Elapsed.TotalMilliseconds, "0")} ms");
        Close(session);

        // A device lost while the preview opens: everything is opened once more.
        FirstAttemptFails<InvalidOperationException>(
            "a device lost while the preview opens",
            Muted with { DevicesLostWhileOpening = 1 },
            Muted with { DevicesLostWhileOpening = 2 },
            "graphics device was lost",
            "device");

        DeviceLossWhereTheCameraIsNotShown();
    }

    /// <summary>
    /// A device lost where the camera is no part of the picture. Its player is parked outside its
    /// stream and hands nothing over, so the camera's picture does not come back by itself; the
    /// scene has no use for it there and must be drawn without waiting for it.
    /// </summary>
    private void DeviceLossWhereTheCameraIsNotShown()
    {
        // The camera is shown from frame 60 to frame 239 of 360.
        var session = OpenSession(TestMedia.ShortCamera, cameraOffset: 2.0);

        // Paused before the camera's first frame.
        session.SeekTo(30);
        var before = session.Engine.GetDiagnostics();
        session.Engine.SimulateDeviceLoss();
        var idle = session.WaitForIdle(10);
        var after = session.Engine.GetDiagnostics();
        var problem = PictureProblem(session, 30, 1280, 720);
        _report.Check(
            "paused before the camera's first frame: the same frame is shown again after the rebuild",
            idle && after.DeviceRebuilds == before.DeviceRebuilds + 1 && after.FramesDrawn > before.FramesDrawn && problem is null && session.PositionFrame == 30 && session.Events.FailedEvents == 0,
            $"{problem ?? "right"}; rebuilds {after.DeviceRebuilds - before.DeviceRebuilds}, scenes drawn since {after.FramesDrawn - before.FramesDrawn}");

        // Playing after the camera's last frame.
        session.SeekTo(260);
        session.Recorder.Drain();
        session.Engine.Play();
        Thread.Sleep(500);
        var lostAt = Stopwatch.GetTimestamp();
        var rebuilds = session.Engine.GetDiagnostics().DeviceRebuilds;
        session.Engine.SimulateDeviceLoss();
        Thread.Sleep(900);
        var stillPlaying = session.Engine.IsPlaying;
        session.Engine.Pause();
        session.WaitForIdle();
        var composites = session.Recorder.Drain().Where(c => c.Screen != FrameCode.Unreadable).ToList();
        var afterLoss = composites.Where(c => c.At > lostAt).ToList();
        var gap = afterLoss.Count == 0 ? double.NaN : Stopwatch.GetElapsedTime(lostAt, afterLoss[0].At).TotalMilliseconds;
        var final = PictureProblem(session, session.PositionFrame, 1280, 720);
        _report.Check(
            "playing after the camera's last frame: playback carries on across the rebuild, in order",
            stillPlaying && session.Engine.GetDiagnostics().DeviceRebuilds == rebuilds + 1 && afterLoss.Count > 10 && IsOrdered(composites) && afterLoss.All(c => c.Camera == FrameCode.Unreadable) && final is null && session.Events.FailedEvents == 0,
            $"{afterLoss.Count} scenes after the loss, the first {F(gap, "0")} ms after it; after the pause: {final ?? "right"}");

        // The camera's picture went with the device, and its player is parked on its last frame
        // still. Asked for on that very frame, it has to hand it over again.
        var arrived = session.SeekTo(239);
        var shown = session.ReadShown();
        var wanted = session.ExpectedCamera(239);
        var last = PictureProblem(session, 239, 1280, 720);
        _report.Check(
            "after that, the last frame that shows the camera shows it: the frame its player was parked on all along",
            arrived && wanted != FrameCode.Unreadable && shown.Screen == 239 && shown.Camera == wanted && last is null,
            $"{shown}, wanted camera {wanted}; {last ?? "the picture is right"}");
        _report.Check("no failure was reported for either rebuild", session.Events.FailedEvents == 0, string.Join("; ", session.Events.Failures()));
        Close(session);
    }

    /// <summary>The GPU memory this process uses, as the default adapter reports it.</summary>
    private sealed class GpuMemory : IDisposable
    {
        private readonly IDXGIFactory1 _factory;
        private readonly IDXGIAdapter3 _adapter;

        private GpuMemory(IDXGIFactory1 factory, IDXGIAdapter3 adapter)
        {
            _factory = factory;
            _adapter = adapter;
        }

        public static GpuMemory? TryCreate()
        {
            try
            {
                // Adapter 0 is the one a device created without naming an adapter is made on.
                var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                if (factory.EnumAdapters1(0, out var adapter).Failure || adapter is null)
                {
                    factory.Dispose();
                    return null;
                }

                using (adapter)
                {
                    var adapter3 = adapter.QueryInterfaceOrNull<IDXGIAdapter3>();
                    if (adapter3 is null)
                    {
                        factory.Dispose();
                        return null;
                    }

                    return new GpuMemory(factory, adapter3);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public double CurrentUsageMb()
        {
            var local = _adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).CurrentUsage;
            var shared = _adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.NonLocal).CurrentUsage;
            return (local + shared) / 1048576.0;
        }

        public void Dispose()
        {
            _adapter.Dispose();
            _factory.Dispose();
        }
    }
}
