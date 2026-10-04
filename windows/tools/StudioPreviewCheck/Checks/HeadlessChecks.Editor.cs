using System.Collections.Concurrent;
using System.Diagnostics;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Core.Studio.Preview;
using TinyClips.Tools.StudioPreviewCheck.Media;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

// The editor's own session class, StudioEditorSession, driving the engine through IStudioPreview
// the way the editor window does: the two together have to do what a user expects.
internal sealed partial class HeadlessChecks
{
    private enum ClosedWhen
    {
        JustLoaded,
        Playing,
        Seeking,
        PausedAfterPlaying,
    }

    private void EditorSessionOnTheEngine()
    {
        const int trimStart = 60;
        const int trimEnd = 78;
        _report.Section("The editor session (StudioEditorSession) on the engine");
        _report.Line("The session class of TinyClips.Core, on a project store in the temp folder, opens its preview through the factory. Its thread is one of the tool's, busy with something else now and then as a UI thread is. The exporter is a stand-in that does nothing.");
        using (var host = EditorHost.Open(Muted, _media, TestMedia.Camera, Late))
        {
            if (host.Preview is not { } preview)
            {
                _report.Check("LoadAsync makes the session ready, with the engine as its preview", false, $"state {host.Get(e => e.State)}: {host.Get(e => e.UnavailableMessage)}");
                return;
            }

            var shown = preview.ReadShown();
            var (state, playhead, playing) = host.Get(e => (e.State, e.Playhead, e.IsPlaying));
            _report.Check(
                "LoadAsync makes the session ready with the engine as its preview: the first frame of both clips, paused, the playhead at the start",
                state == StudioEditorLoadState.Ready && shown == new Shown(0, preview.ExpectedCamera(0)) && playhead == 0 && !playing && !preview.Engine.IsPlaying,
                $"{shown}, playhead {F(playhead, "0.###")} s, loaded in {F(host.LoadMilliseconds, "0")} ms");

            // The kept range, set the way the trim handles set it.
            host.Do(e => e.SetTrimStart(TestFolder.TimeOf(trimStart)));
            preview.WaitForIdle();
            var atStart = preview.ReadShown();
            host.Do(e => e.SetTrimEnd(TestFolder.TimeOf(trimEnd)));
            preview.WaitForIdle();
            var atEnd = preview.ReadShown();
            var (keptFrom, keptTo, head) = host.Get(e => (e.Model!.TrimStart, e.Model!.TrimEnd, e.Playhead));
            _report.Check(
                "SetTrimStart and SetTrimEnd show the frame the video now starts on and the frame it ends at",
                atStart == new Shown(trimStart, preview.ExpectedCamera(trimStart)) && atEnd == new Shown(trimEnd, preview.ExpectedCamera(trimEnd)) && Math.Abs(head - keptTo) < 1e-9 && preview.PositionFrame == trimEnd,
                $"{atStart}; then {atEnd}; kept range {F(keptFrom, "0.###")} to {F(keptTo, "0.###")} s");

            EditorReplaysItsRange(host, preview, trimStart, trimEnd);
            EditorAtTheEndOfTheRecording(host, preview, trimStart);
            EditorPausesAndSteps(host, preview);
            EditorEdits(host, preview);
            EditorSavesAndCloses(host, preview);
        }

        EditorClosesAndDeletes();
    }

    // ---------------------------------------------------------------------------------------
    // Space at the end of the kept range
    // ---------------------------------------------------------------------------------------

    private void EditorReplaysItsRange(EditorHost host, Session preview, int trimStart, int trimEnd)
    {
        var repetitions = _quick ? 12 : 40;
        var gaps = new[] { 0, 2, 5, 10, 20, 35, 50, 70, 100, 150 };
        var before = new[] { 30, 22, 16, 12, 9, 6, 4, 2, 1, 0 };
        var rangeMilliseconds = (trimEnd - trimStart) * 1000.0 / Fps;
        var wrong = new List<string>();
        var played = new Samples();
        var firstFrame = new Samples();
        var parking = new Samples();
        preview.WaitForIdle();
        for (var repetition = 0; repetition < repetitions; repetition++)
        {
            // Space some time after the session parked the playhead at the end. The first three
            // times it rests there, which also shows how long the seek that parks it takes. After
            // that every other Space is timed to fall just before that seek lands, when the news
            // of the landing is about to be on its way to the session, and the others 0 to 150 ms
            // after the session parked.
            var gap = 0;
            if (repetition < 3)
            {
                preview.WaitForIdle();
                var landed = preview.Events.Positions().Where(p => p.At >= host.StoppedAt).Select(p => p.At).DefaultIfEmpty(0).Max();
                if (repetition > 0 && landed != 0)
                {
                    parking.Add(Stopwatch.GetElapsedTime(host.StoppedAt, landed).TotalMilliseconds);
                }
            }
            else
            {
                gap = repetition % 2 == 0 ? gaps[repetition / 2 % gaps.Length] : Math.Max(0, (int)Math.Round(parking.Mean) - before[repetition / 2 % before.Length]);
                var until = host.StoppedAt + (gap * Stopwatch.Frequency / 1000);
                while (Stopwatch.GetTimestamp() < until)
                {
                    Thread.SpinWait(40);
                }
            }

            preview.Recorder.Drain();
            var space = host.Space();
            var stopped = host.WaitForStop(TimeSpan.FromSeconds(6));
            var stoppedAt = host.StoppedAt;
            var scenes = preview.Recorder.Drain().Where(c => c.At >= space.At && c.At <= stoppedAt && c.Screen != FrameCode.Unreadable).ToList();
            var tookMilliseconds = Stopwatch.GetElapsedTime(space.At, stoppedAt).TotalMilliseconds;

            // Sent back at once is what a stale position does: the session reads the end frame it
            // has just left and pauses, and Space seems to do nothing.
            var drawnOfRange = scenes.Select(c => c.Screen).Where(f => f >= trimStart && f < trimEnd).Distinct().Count();
            var problem = !space.IsPlaying ? "the session was not playing when TogglePlayback had returned"
                : !stopped ? $"the session never reached the end of its range (engine Position frame {preview.PositionFrame})"
                : tookMilliseconds < 0.75 * rangeMilliseconds ? $"the session paused at the end {F(tookMilliseconds, "0")} ms after Space; the range takes {F(rangeMilliseconds, "0")} ms to play"
                : drawnOfRange < 0.6 * (trimEnd - trimStart) ? $"only {drawnOfRange} of the range's {trimEnd - trimStart} frames were drawn before the session paused"
                : PlayedFrom(scenes, trimStart, trimEnd);
            if (problem is not null)
            {
                wrong.Add($"repetition {repetition} (Space {(repetition < 3 ? "at rest" : gap + " ms after the session parked")}): {problem}{Dump(preview, "editor")}");
            }
            else
            {
                played.Add(tookMilliseconds);
                firstFrame.Add(Stopwatch.GetElapsedTime(space.At, scenes.First(c => c.Screen == trimStart).At).TotalMilliseconds);
            }
        }

        preview.WaitForIdle();
        var shown = preview.ReadShown();
        var (playhead, end) = host.Get(e => (e.Playhead, e.Model!.TrimEnd));
        _report.Check(
            $"TogglePlayback with the playhead parked at the end of the kept range plays the range again every time, and the session parks at the end again ({repetitions} times: at rest, 0 to 150 ms after it parked, and just before the seek that parks it lands)",
            wrong.Count == 0 && shown == new Shown(trimEnd, preview.ExpectedCamera(trimEnd)) && preview.PositionFrame == trimEnd && Math.Abs(playhead - end) < 1e-9,
            wrong.Count == 0 ? $"at the end the picture is on {shown}, the engine's Position on frame {preview.PositionFrame}, the playhead at {F(playhead, "0.###")} s" : $"{wrong.Count} wrong; first: {string.Join(" | ", wrong.Take(3))}");
        _report.Note($"TogglePlayback to the first frame of the range drawn: {firstFrame.Summary()}; to the session pausing at the end of the {F(rangeMilliseconds, "0")} ms range: {played.Summary()}; the session parking to the news that its seek has landed: {parking.Summary()}");
    }

    // ---------------------------------------------------------------------------------------
    // The kept range runs to the end of the recording
    // ---------------------------------------------------------------------------------------

    private void EditorAtTheEndOfTheRecording(EditorHost host, Session preview, int trimStart)
    {
        var loops = _quick ? 4 : 12;
        var gaps = new[] { 0, 5, 20, 60, 150, 400 };
        var last = preview.Folder.FrameCount - 1;
        var duration = (double)TestMedia.Screen.Seconds;
        var wrong = new List<string>();
        var firstFrame = new Samples();
        host.Do(e => e.SetTrimEnd(duration));
        preview.WaitForIdle();
        for (var loop = 0; loop < loops; loop++)
        {
            // A third of a second before the end, and into it. Nobody pauses: the preview stops by itself.
            host.Do(e => e.Scrub(TestFolder.TimeOf(last - 10, 0.5)));
            preview.WaitForIdle();
            host.Space();
            var ended = host.WaitForStop(TimeSpan.FromSeconds(6));
            var playhead = host.Get(e => e.Playhead);

            // Space again, some time after the session heard that playback had stopped.
            var gap = gaps[loop % gaps.Length];
            if (gap > 0)
            {
                Thread.Sleep(gap);
            }

            preview.Recorder.Drain();
            var space = host.Space();
            Thread.Sleep(450);
            var stillPlaying = host.Get(e => e.IsPlaying);
            var pause = host.Space();
            preview.WaitForIdle();
            var scenes = preview.Recorder.Drain().Where(c => c.At >= space.At && c.At <= pause.At && c.Screen != FrameCode.Unreadable).ToList();
            var problem = !ended ? "playback did not stop by itself at the end of the recording"
                : Math.Abs(playhead - duration) > 1e-9 ? $"the session's playhead was at {F(playhead, "0.####")} s when it had heard that playback stopped, and the kept range ends at {F(duration, "0.####")} s"
                : !space.IsPlaying || !stillPlaying ? "the session was no longer playing 450 ms after Space"
                : PlayedFrom(scenes, trimStart, last - 10);
            if (problem is not null)
            {
                wrong.Add($"loop {loop} (Space {gap} ms after the end): {problem}{Dump(preview, "editor")}");
            }
            else
            {
                firstFrame.Add(Stopwatch.GetElapsedTime(space.At, scenes.First(c => c.Screen == trimStart).At).TotalMilliseconds);
            }
        }

        _report.Check(
            $"with the kept range running to the end of the recording, playback stops by itself, the session puts its playhead at the end, and TogglePlayback then plays from the start of the range ({loops} times, 0 to 400 ms after the end)",
            wrong.Count == 0,
            wrong.Count == 0 ? null : $"{wrong.Count} wrong; first: {string.Join(" | ", wrong.Take(3))}");
        _report.Note($"after the end of the recording, TogglePlayback to the first frame of the range drawn: {firstFrame.Summary()}");
    }

    // ---------------------------------------------------------------------------------------
    // Pause, then a step
    // ---------------------------------------------------------------------------------------

    private void EditorPausesAndSteps(EditorHost host, Session preview)
    {
        var cycles = _quick ? 6 : 20;
        var wrong = new List<string>();
        var behind = new Samples();
        var notForward = 0;
        var readAtOnce = 0;
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            host.Do(e => e.Scrub(TestFolder.TimeOf(100 + (cycle % 5 * 20), 0.5)));
            preview.WaitForIdle();
            host.Space();
            Thread.Sleep(300 + _random.Next(300));
            var pause = host.Space();
            var idle = preview.WaitForIdle();

            // What the picture stopped on, and where the session thinks it is.
            var shown = preview.ReadShown();
            var position = preview.PositionFrame;
            var playheadFrame = FrameOf(host.Get(e => e.Playhead));
            behind.Add(shown.Screen - playheadFrame);
            readAtOnce += FrameOf(pause.Position) == shown.Screen ? 1 : 0;

            host.Do(e => e.StepFrames(1));
            idle &= preview.WaitForIdle();
            var stepped = preview.ReadShown();
            var asked = FrameOf(host.Get(e => e.Playhead));
            notForward += stepped.Screen <= shown.Screen ? 1 : 0;
            if (!idle
                || shown.Screen == FrameCode.Unreadable
                || shown.Screen != position
                || shown.Camera != preview.ExpectedCamera(shown.Screen)
                || stepped != new Shown(asked, preview.ExpectedCamera(asked))
                || preview.PositionFrame != asked)
            {
                wrong.Add($"cycle {cycle}: idle {idle}; after the pause the picture was on {shown} and the engine's Position on frame {position}; the step asked for frame {asked} and the picture was on {stepped}, Position on frame {preview.PositionFrame}{Dump(preview, "editor")}");
            }
        }

        _report.Check(
            $"the session pauses playback and steps a frame ({cycles} times): the engine's Position, read on the session's thread the moment its Pause has returned, is the frame the picture stays on; and after StepFrames(1) the picture is the frame the session asked for",
            wrong.Count == 0 && readAtOnce == cycles,
            wrong.Count == 0 ? $"Position was the frame the picture stayed on in {readAtOnce} of {cycles}" : $"{wrong.Count} wrong; first: {string.Join(" | ", wrong.Take(3))}");
        _report.Note($"the session's own playhead after its Pause, against the frame the picture had stopped on: behind by {behind.Summary("frames")}; its StepFrames(1) then showed the frame that was up already, or an earlier one, in {notForward} of {cycles}. The session takes its playhead from the last PositionChanged it handled while playing, not from Position after the pause");
    }

    /// <summary>The frame a time on the timeline lies in.</summary>
    private static int FrameOf(double seconds) => (int)Math.Floor((seconds * Fps) + 1e-6);

    // ---------------------------------------------------------------------------------------
    // An edit
    // ---------------------------------------------------------------------------------------

    private void EditorEdits(EditorHost host, Session preview)
    {
        const int frame = 120;
        host.Do(e => e.Scrub(TestFolder.TimeOf(frame, 0.5)));
        preview.WaitForIdle();
        var bubble = preview.View(1280, 720);
        var before = preview.Engine.GetDiagnostics();

        host.Do(e => e.SetLayout(StudioLayout.SideBySide));
        preview.Follow(host.Get(e => e.Project!));
        preview.WaitForIdle();
        var side = preview.ReadShown();
        var picture = preview.ReadPicture();
        var atOldPlace = picture is null ? 0 : FrameCode.Decode(picture.Bgra, picture.Width, picture.Height, TestMedia.Camera, bubble.CameraMap!.Value);

        host.Do(e => e.Undo());
        preview.Follow(host.Get(e => e.Project!));
        preview.WaitForIdle();
        var undone = preview.ReadShown();
        var after = preview.Engine.GetDiagnostics();
        var callbacks = (after.CallbacksStarted[0] - before.CallbacksStarted[0]) + (after.CallbacksStarted[1] - before.CallbacksStarted[1]);
        var want = new Shown(frame, preview.ExpectedCamera(frame));
        _report.Check(
            "SetLayout through the session shows in the picture, and Undo takes it back, with no frame asked of the players",
            side == want && atOldPlace == FrameCode.Unreadable && undone == want && callbacks == 0,
            $"side by side: {side}, where the bubble was: {(atOldPlace == FrameCode.Unreadable ? "nothing" : "frame " + atOldPlace)}; undone: {undone}; VideoFrameAvailable callbacks {callbacks}");
    }

    // ---------------------------------------------------------------------------------------
    // Closing
    // ---------------------------------------------------------------------------------------

    private void EditorSavesAndCloses(EditorHost host, Session preview)
    {
        var id = host.Folder.Paths.ProjectId;
        var keptFrom = host.Get(e => e.Model!.TrimStart);
        host.Space();
        Thread.Sleep(200);
        var ended = host.Close(deleteProject: false, out var milliseconds);
        var disposed = preview.Engine.DisposeAsync().IsCompleted;
        StudioProject? saved = null;
        string? error = null;
        try
        {
            saved = new StudioProjectStore(TestFolder.Root).Load(id);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {Shorten(ex.Message)}";
        }

        _report.Check(
            "CloseAsync while playing: the edits are in the project file, the preview is disposed, and nothing went wrong on the way",
            ended && disposed && saved is not null && Math.Abs(saved.Edits.TrimStart - keptFrom) < 1e-9 && host.Errors().Count == 0 && host.Ui.Failures().Count == 0 && preview.Events.FailedEvents == 0,
            error ?? $"closed in {F(milliseconds, "0")} ms; trim start saved as {F(saved!.Edits.TrimStart, "0.###")} s{Problems(host, preview)}");
        _report.Check("and the project folder can be deleted at once", TryDelete(host.Folder, out var deleteError), deleteError);
    }

    private void EditorClosesAndDeletes()
    {
        var rounds = _quick ? 4 : 12;
        var wrong = new List<string>();
        var closeTimes = new Samples();
        var attempts = 0;
        for (var round = 0; round < rounds; round++)
        {
            var when = (ClosedWhen)(round % 4);
            using var host = EditorHost.Open(Muted, _media, round % 3 == 2 ? null : TestMedia.Camera, Late);
            if (host.Preview is not { } preview)
            {
                wrong.Add($"round {round}: the session did not become ready: {host.Get(e => e.UnavailableMessage)}");
                continue;
            }

            switch (when)
            {
                case ClosedWhen.Playing:
                    host.Do(e => e.Scrub(TestFolder.TimeOf(30, 0.5)));
                    host.Space();
                    Thread.Sleep(250);
                    break;

                case ClosedWhen.Seeking:
                    // The last of these is still on its way when the session is closed.
                    for (var seek = 0; seek < 5; seek++)
                    {
                        var to = 40 + (seek * 61);
                        host.Do(e => e.Scrub(TestFolder.TimeOf(to, 0.5)));
                    }

                    break;

                case ClosedWhen.PausedAfterPlaying:
                    host.Do(e => e.Scrub(TestFolder.TimeOf(30, 0.5)));
                    host.Space();
                    Thread.Sleep(250);
                    host.Space();
                    break;
            }

            var ended = host.Close(deleteProject: true, out var milliseconds);
            closeTimes.Add(milliseconds);
            attempts += host.Store.Deletes;
            var gone = !Directory.Exists(host.Folder.Paths.ProjectDirectory);
            if (!ended || !gone || host.Store.Deletes != 1 || host.Store.FailedDeletes != 0 || host.Errors().Count > 0 || host.Ui.Failures().Count > 0 || preview.Events.FailedEvents > 0)
            {
                wrong.Add($"round {round} ({when}): the close ended {ended}, the folder is gone {gone}, Delete was tried {host.Store.Deletes} time(s) and failed {host.Store.FailedDeletes} time(s){Problems(host, preview)}");
            }
        }

        _report.Check(
            $"CloseAsync(deleteProject: true) right after loading, while playing, during a seek and after a pause ({rounds} sessions): the session's first attempt to delete the project folder succeeds every time",
            wrong.Count == 0,
            wrong.Count == 0 ? $"Delete was tried {attempts} times for {rounds} sessions" : $"{wrong.Count} wrong; first: {string.Join(" | ", wrong.Take(3))}");
        _report.Note($"CloseAsync(deleteProject: true), the call to the folder gone: {closeTimes.Summary()}");
    }

    /// <summary>What the session, its thread and the engine reported as having gone wrong, for the detail of a check.</summary>
    private static string Problems(EditorHost host, Session preview)
    {
        var problems = host.Errors().Select(e => "the session reported: " + e)
            .Concat(host.Ui.Failures().Select(f => "on the session's thread: " + Shorten(f)))
            .Concat(preview.Events.Failures().Select(f => "the engine failed: " + f))
            .ToList();
        return problems.Count == 0 ? string.Empty : "; " + string.Join("; ", problems);
    }

    /// <summary>
    /// An editor session on a project folder of its own, with the instruments of a
    /// <see cref="Session"/> around the preview it opened.
    /// </summary>
    private sealed class EditorHost : IDisposable
    {
        private readonly ManualResetEventSlim _stopped = new(false);
        private readonly List<string> _errors = [];
        private long _stoppedAt;
        private bool _wasPlaying;
        private bool _closed;

        private EditorHost(TestFolder folder, CountingStore store, SessionThread ui, StudioEditorSession editor)
        {
            Folder = folder;
            Store = store;
            Ui = ui;
            Editor = editor;
        }

        public TestFolder Folder { get; }

        public CountingStore Store { get; }

        /// <summary>The thread the session lives on. Everything asked of the session is asked there.</summary>
        public SessionThread Ui { get; }

        public StudioEditorSession Editor { get; }

        /// <summary>The session's preview, when it became ready: the engine, with a surface to draw into.</summary>
        public Session? Preview { get; private set; }

        public double LoadMilliseconds { get; private set; }

        /// <summary>When the session last went from playing to not playing.</summary>
        public long StoppedAt => Interlocked.Read(ref _stoppedAt);

        public static EditorHost Open(StudioPreviewOptions options, string mediaDirectory, ClipSpec? camera, double cameraOffset)
        {
            var folder = TestFolder.Create(mediaDirectory, camera, cameraOffset);
            var ui = new SessionThread();
            try
            {
                // The folder is a project of a store whose root is the folder the tool keeps its projects in.
                var store = new CountingStore(new StudioProjectStore(TestFolder.Root));
                var trace = new TraceLog(600);
                var editor = new StudioEditorSession(
                    folder.Paths.ProjectId,
                    store,
                    new StudioPreviewFactory(options with { Trace = trace.Add }),
                    new NoExporter(),
                    new CaptureSettings(new MemorySettings()),
                    ui.Post);
                var host = new EditorHost(folder, store, ui, editor);
                editor.Changed += host.OnChanged;
                editor.ErrorReported += host.OnErrorReported;
                var watch = Stopwatch.StartNew();
                ui.Run(editor.LoadAsync).GetAwaiter().GetResult();
                host.LoadMilliseconds = watch.Elapsed.TotalMilliseconds;
                var (preview, project) = ui.Run(() => (editor.Preview, editor.Project));
                if (preview is StudioPreviewEngine engine && project is not null)
                {
                    host.Preview = Session.Adopt(folder, engine, project, trace);
                }

                return host;
            }
            catch
            {
                ui.Dispose();
                folder.Dispose();
                throw;
            }
        }

        public void Do(Action<StudioEditorSession> action) => Ui.Run(() => action(Editor));

        public T Get<T>(Func<StudioEditorSession, T> read) => Ui.Run(() => read(Editor));

        /// <summary>
        /// The Space key. Returns when TogglePlayback had returned, whether the session was playing
        /// then, and what the preview's Position was at that moment, on the session's thread.
        /// </summary>
        public (long At, bool IsPlaying, double Position) Space()
        {
            _stopped.Reset();
            return Ui.Run(() =>
            {
                Editor.TogglePlayback();
                return (Stopwatch.GetTimestamp(), Editor.IsPlaying, Editor.Preview?.Position ?? double.NaN);
            });
        }

        /// <summary>Waits until the session has gone from playing to not playing since the last <see cref="Space"/>.</summary>
        public bool WaitForStop(TimeSpan timeout) => _stopped.Wait(timeout);

        public List<string> Errors()
        {
            lock (_errors)
            {
                return [.. _errors];
            }
        }

        /// <summary>Closes the session on its thread and waits for the task it returns. False when that did not end in time.</summary>
        public bool Close(bool deleteProject, out double milliseconds)
        {
            _closed = true;
            var watch = Stopwatch.StartNew();
            var ended = Ui.Run(() => Editor.CloseAsync(deleteProject)).Wait(TimeSpan.FromSeconds(15));
            milliseconds = watch.Elapsed.TotalMilliseconds;
            return ended;
        }

        public void Dispose()
        {
            if (!_closed)
            {
                try
                {
                    Close(deleteProject: true, out _);
                }
                catch (Exception)
                {
                    // The folder is swept up at the end of the run.
                }
            }

            Editor.Changed -= OnChanged;
            Editor.ErrorReported -= OnErrorReported;
            Preview?.Close();
            Ui.Dispose();
            _stopped.Dispose();
            Folder.Dispose();
        }

        // Raised on the session's thread.
        private void OnChanged(object? sender, StudioEditorChangedEventArgs e)
        {
            if (!e.Includes(StudioEditorChanges.Playback))
            {
                return;
            }

            var playing = Editor.IsPlaying;
            if (_wasPlaying && !playing)
            {
                Interlocked.Exchange(ref _stoppedAt, Stopwatch.GetTimestamp());
                _stopped.Set();
            }

            _wasPlaying = playing;
        }

        private void OnErrorReported(object? sender, StudioEditorErrorEventArgs e)
        {
            lock (_errors)
            {
                _errors.Add($"{e.Kind}: {e.Message}");
            }
        }
    }
}

/// <summary>
/// The thread an editor session lives on: what is posted to it runs there, one at a time and in
/// order, after a moment now and then in which the thread is busy with something else.
/// </summary>
internal sealed class SessionThread : IDisposable
{
    private static readonly int[] Lags = [0, 0, 1, 0, 3, 0, 0, 8, 0, 2];

    private readonly BlockingCollection<Action> _posted = [];
    private readonly List<string> _failures = [];
    private readonly Thread _thread;
    private int _lag;

    public SessionThread()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "StudioPreviewCheck.EditorSession" };
        _thread.Start();
    }

    /// <summary>What the session's own work on this thread threw. It must not throw.</summary>
    public List<string> Failures()
    {
        lock (_failures)
        {
            return [.. _failures];
        }
    }

    /// <summary>What a session is given as its way of running something on its thread, later.</summary>
    public void Post(Action action)
    {
        try
        {
            _posted.Add(action);
        }
        catch (InvalidOperationException)
        {
            // The thread has ended, and the session with it.
        }
    }

    /// <summary>Runs something on the thread, after what was posted before, and waits for it.</summary>
    public T Run<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);
        _posted.Add(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait();
        return failure is null ? result : throw new InvalidOperationException(failure.Message, failure);
    }

    public void Run(Action action) => Run(() =>
    {
        action();
        return true;
    });

    public void Dispose()
    {
        _posted.CompleteAdding();
        _thread.Join();
        _posted.Dispose();
    }

    private void Loop()
    {
        foreach (var action in _posted.GetConsumingEnumerable())
        {
            var lag = Lags[_lag++ % Lags.Length];
            if (lag > 0)
            {
                Thread.Sleep(lag);
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                lock (_failures)
                {
                    _failures.Add(ex.ToString());
                }
            }
        }
    }
}

/// <summary>A project store that passes everything on, and counts how often a delete was tried and how often it failed.</summary>
internal sealed class CountingStore(IStudioProjectStore inner) : IStudioProjectStore
{
    private int _deletes;
    private int _failedDeletes;

    public int Deletes => Volatile.Read(ref _deletes);

    public int FailedDeletes => Volatile.Read(ref _failedDeletes);

    public string RootDirectory => inner.RootDirectory;

    public StudioProjectPaths BeginRecording() => inner.BeginRecording();

    public StudioProject CompleteRecording(string projectId, StudioProjectCreationRequest request) => inner.CompleteRecording(projectId, request);

    public StudioProjectPaths GetPaths(string projectId) => inner.GetPaths(projectId);

    public StudioProjectPaths GetPaths(StudioProject project) => inner.GetPaths(project);

    public bool Exists(string projectId) => inner.Exists(projectId);

    public StudioProject Load(string projectId) => inner.Load(projectId);

    public StudioProject Save(StudioProject project) => inner.Save(project);

    public void Delete(string projectId)
    {
        Interlocked.Increment(ref _deletes);
        try
        {
            inner.Delete(projectId);
        }
        catch
        {
            Interlocked.Increment(ref _failedDeletes);
            throw;
        }
    }

    public StudioProject MarkOpened(string projectId) => inner.MarkOpened(projectId);

    public IReadOnlyList<StudioProjectSummary> ListSummaries() => inner.ListSummaries();

    public StudioStorageSummary GetStorageSummary() => inner.GetStorageSummary();

    public StudioProject RecordExport(string projectId, string exportedPath) => inner.RecordExport(projectId, exportedPath);

    public string? FindProjectIdByExportPath(string exportedPath) => inner.FindProjectIdByExportPath(exportedPath);

    public bool UpdateExportPath(string oldPath, string newPath) => inner.UpdateExportPath(oldPath, newPath);

    public bool RemoveExportPath(string exportedPath) => inner.RemoveExportPath(exportedPath);

    public StudioProject GetOrCreateFlatProject(string videoPath, StudioRecordingSourceInfo video, string appVersion) => inner.GetOrCreateFlatProject(videoPath, video, appVersion);

    public StudioEvents LoadEvents(string projectId) => inner.LoadEvents(projectId);

    public void SaveEvents(string projectId, StudioEvents events) => inner.SaveEvents(projectId, events);

    public StudioCleanupResult Cleanup(StudioCleanupOptions? options = null, IReadOnlyCollection<string>? inUseProjectIds = null) => inner.Cleanup(options, inUseProjectIds);
}

/// <summary>An exporter that renders nothing. The session writes a poster through it when it closes.</summary>
internal sealed class NoExporter : IStudioExportService
{
    public Task ExportAsync(StudioProject project, StudioEvents events, StudioProjectPaths paths, string outputPath, VideoCodec codec, IProgress<double>? progress, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task WritePosterAsync(StudioProject project, StudioEvents events, StudioProjectPaths paths, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Settings that live as long as the object does and touch nothing on the machine.</summary>
internal sealed class MemorySettings : ISettingsService
{
    private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

    public AppTheme Theme { get; set; }

    public string SaveDirectory { get; set; } = string.Empty;

    public T Get<T>(string key, T defaultValue) => _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;

    public void Set<T>(string key, T value) => _values[key] = value is null ? string.Empty : value;
}
