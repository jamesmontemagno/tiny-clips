using System.Diagnostics;
using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Preview;
using TinyClips.Tools.StudioPreviewCheck.Media;

namespace TinyClips.Tools.StudioPreviewCheck.Checks;

/// <summary>
/// The checks that need no window: the engine draws into an offscreen surface and every result is
/// read back from pixels. The players are forced mute for the whole run.
/// </summary>
internal sealed partial class HeadlessChecks
{
    private const int Fps = TestMedia.Fps;

    // The camera of most checks starts 0.2 s after the screen: screen frame N goes with camera frame N - 6.
    private const double Late = 0.2;

    private readonly Report _report;
    private readonly CheckOptions _options;
    private readonly string _media;
    private readonly string _output;
    private readonly string _failuresDirectory;
    private readonly StudioPreviewFactory _factory;
    private readonly StandInFinders? _peopleOfTheRun;
    private readonly Random _random = new(20261003);
    private readonly Samples _openTimes = new();
    private readonly Samples _disposeTimes = new();
    private readonly bool _quick;
    private int _dumps;

    public HeadlessChecks(Report report, CheckOptions options, string mediaDirectory, string outputDirectory)
    {
        _report = report;
        _options = options;
        _media = mediaDirectory;
        _output = outputDirectory;
        // A folder of its own for what the failed checks of this run leave behind, named like the
        // report, so that a run does not write over the evidence of the run before it.
        _failuresDirectory = Path.Combine(outputDirectory, "failures", report.Stamp);
        _quick = options.Flag("quick");
        Session.TraceLines = Math.Max(100, options.Number("trace-lines", 600));
        var muted = new StudioPreviewOptions
        {
            ForceMuted = true,
            TrustFirstFrames = options.Flag("trust-first-frames"),
            Naming = new StudioPreviewNamingSettings
            {
                BelievePositions = options.Flag("believe-positions"),
                FetchEveryRestingFrame = options.Flag("fetch-every-rest"),
                CollectorRuleNeedsIdleGap = options.Flag("guard-rule-3"),
            },
            Seek = new StudioPreviewSeekSettings { HoldFirstChangeAfterPlaying = !options.Flag("no-quiet-rule") },
            StopNotedLate = options.Flag("stop-noted-late"),
            FrameTimesFromFile = FrameTimes.FromFile(options),
        };
        _peopleOfTheRun = StandInFinders.FromOptions(options);
        if (_peopleOfTheRun is not null)
        {
            // Every project of the run has its camera's background removed, and every preview
            // a stand-in for the model that finds the people, which takes its time. The frames
            // say which picture they are, which the engine does not have them do by itself yet.
            muted = muted with { PersonFinderFactory = _peopleOfTheRun.Make, StampPictures = true };
            TestFolder.EditEvery = WithCutout(StudioCameraCutout.Remove);
        }

        Muted = muted;
        _factory = new StudioPreviewFactory(Muted);
    }

    /// <summary>
    /// What every preview of the run is opened with: every player muted. With
    /// <c>--trust-first-frames</c>, also opened the way the engine opened before it stopped
    /// believing what the players hand over first; with <c>--believe-positions</c>, taking every
    /// frame of playback for the one its position names, as the engine did before; with
    /// <c>--no-quiet-rule</c>, without holding the picture during the first position change after
    /// the clock ran; with <c>--stop-noted-late</c>, noting when the clock stopped only after it
    /// has, as the engine did before. Each is there to see which checks notice. With
    /// <c>--guard-rule-3</c>, the namer's rule 3 asks for an idle look, which the engine does
    /// not do by itself: that one is there to see what it would cost. With
    /// <c>--frame-times file</c>, each clip's frame times are read from its file and the engine
    /// counts in frames of the file, which it does not do by itself yet either: on the usual
    /// clips, whose frames are on the grid, every check has to come out as without it. With
    /// <c>--people</c>, every project has its camera's background removed and every preview
    /// a stand-in for the model that finds the people, which takes the time the option names,
    /// and the frames the renderer is given say which picture they are, which the engine does
    /// not have them do by itself yet either: that one is there to see what a finder's time
    /// does to everything else.
    /// </summary>
    private StudioPreviewOptions Muted { get; }

    /// <summary>Leaves pictures of a wrong result behind, for the first few of a run.</summary>
    private string Dump(Session session, string name) =>
        Interlocked.Increment(ref _dumps) <= 12 ? session.Dump(_failuresDirectory, $"{name}-{_dumps}") : string.Empty;

    /// <summary>
    /// Leaves the trace of a preview that did not open behind: what its players reported before
    /// the engine gave up. There are no pictures: the engine is gone.
    /// </summary>
    private string DumpFailedOpen(Exception exception, string name) =>
        Session.TraceOfFailedOpen(exception) is null || Interlocked.Increment(ref _dumps) > 12
            ? string.Empty
            : Session.DumpFailedOpen(exception, _failuresDirectory, $"{name}-{_dumps}");

    /// <summary>An exception with the error code underneath it: a player's failure carries the reason only there.</summary>
    private static string Describe(Exception exception)
    {
        var text = $"{exception.GetType().Name}: {Shorten(exception.Message)}";
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            text += $" <- {inner.GetType().Name} 0x{inner.HResult:X8}: {Shorten(inner.Message)}";
        }

        return text;
    }

    public void Run()
    {
        Group("open", Open);
        Group("seek", PausedSeeks);
        Group("step", Steps);
        Group("seekplay", SeekThenPlay);
        Group("position", PositionAfterSeek);
        Group("editor", EditorSessionOnTheEngine);
        Group("play", Playback);
        Group("pause", PauseCycles);
        Group("stalls", PausesHeldUp);
        Group("zoom", ZoomWhilePlaying);
        Group("cuts", EditorPlaysOverCuts);
        Group("scenes", EditorChangesScene);
        Group("end", EndOfRecording);
        Group("update", UpdateProject);
        Group("camera", CameraTiming);
        Group("mute", Mute);
        Group("surface", Surfaces);
        Group("dispose", Dispose);
        Group("device", DeviceLoss);
        Group("software", SoftwareDevice);
        if (_options.Names("only").Contains("recordings", StringComparer.OrdinalIgnoreCase))
        {
            // Only when it is asked for by name: these clips are written by the app's own
            // writers when they are missing, and the checks have not been run yet.
            Group("recordings", Recordings);
        }

        if (_options.Names("only").Contains("people", StringComparer.OrdinalIgnoreCase))
        {
            // Only when it is asked for by name, for the same reason: not run yet.
            Group("people", People);
        }

        if (_peopleOfTheRun is { } people)
        {
            _report.Section("The stand-ins for the model that finds the people (--people)");
            _report.Note($"over the whole run: {people.Describe()}; each look took {F(people.EachMilliseconds)} ms and each finder {F(people.FirstMilliseconds, "0")} ms to make");
        }

        _report.Section("Open and close times over the whole run");
        _report.Note($"open (factory call to engine returned): {_openTimes.Summary()}");
        _report.Note($"DisposeAsync: {_disposeTimes.Summary()}");
    }

    private void Group(string name, Action body)
    {
        if (!_options.Wants(name))
        {
            return;
        }

        try
        {
            body();
        }
        catch (Exception ex)
        {
            _report.Check($"{name}: the checks ran to the end", false, ex + DumpFailedOpen(ex, name));
        }
    }

    private Session OpenSession(ClipSpec? camera, double cameraOffset = Late, int width = 1280, int height = 720, bool attach = true, Func<StudioProject, StudioProject>? edit = null, StudioPreviewOptions? options = null, ClipSpec? screen = null)
    {
        var folder = TestFolder.Create(_media, camera, cameraOffset, edit, screen: screen);
        try
        {
            var session = Session.Open(options ?? Muted, folder, width, height, attach);
            _openTimes.Add(session.OpenMilliseconds);
            return session;
        }
        catch
        {
            folder.Dispose();
            throw;
        }
    }

    private void Close(Session session)
    {
        var watch = Stopwatch.StartNew();
        session.Close();
        _disposeTimes.Add(watch.Elapsed.TotalMilliseconds);
    }

    private static string F(double value, string format = "0.0") => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>The clock and the players as they describe themselves, for the detail of a check that did not hold.</summary>
    private static string Transport(Session session)
    {
        var state = session.Engine.GetDiagnostics();
        var players = new List<string>();
        for (var index = 0; index < state.PlayerStates.Length; index++)
        {
            players.Add($"{(index == 0 ? "screen" : "camera")} {state.PlayerStates[index]} at {F(state.PlayerSeconds[index], "0.0000")} s showing {state.ShownFrames[index]}");
        }

        return $"clock {state.ClockState} at {F(state.ClockSeconds, "0.0000")} s; {string.Join("; ", players)}";
    }

    // ---------------------------------------------------------------------------------------
    // Open
    // ---------------------------------------------------------------------------------------

    private void Open()
    {
        _report.Section("Open");

        // Screen and camera, both starting at 0: the first frame of both clips.
        var session = OpenSession(TestMedia.Camera, cameraOffset: 0);
        var shown = session.ReadShown();
        var first = shown is { Screen: 0, Camera: 0 };
        _report.Check("screen and camera: the picture shows the first frame of both clips", first, first ? shown.ToString() : shown + Dump(session, "open"));
        _report.Check("screen and camera: both players' textures hold their first frame", session.ReadClipFrame(0) == 0 && session.ReadClipFrame(1) == 0, $"screen {session.ReadClipFrame(0)}, camera {session.ReadClipFrame(1)}");
        _report.Check("screen and camera: paused at the start", !session.Engine.IsPlaying && session.Engine.Position == 0, $"IsPlaying {session.Engine.IsPlaying}, Position {F(session.Engine.Position, "0.####")}");
        _report.Check("screen and camera: no event was raised by opening", session.Events.PositionEvents == 0 && session.Events.PlayingEvents == 0 && session.Events.FailedEvents == 0);
        _report.Check("the engine reports the recording's length and frame rate", Math.Abs(session.Engine.Duration - TestMedia.Screen.Seconds) < 1e-9 && session.Engine.FrameRate == Fps, $"{F(session.Engine.Duration)} s at {F(session.Engine.FrameRate)} fps");
        var withCamera = session.Project;
        Close(session);

        // Screen only.
        session = OpenSession(camera: null);
        var picture = session.ReadPicture();
        shown = session.ReadShown();
        var bubble = SceneView.Resolve(withCamera, TestMedia.Screen, TestMedia.Camera, 1280, 720);
        var ghost = picture is null ? 0 : FrameCode.Decode(picture.Bgra, picture.Width, picture.Height, TestMedia.Camera, bubble.CameraMap!.Value);
        _report.Check("screen only: the picture shows the first screen frame and no camera", shown.Screen == 0 && ghost == FrameCode.Unreadable, $"{shown}; where a camera bubble would be: {(ghost == FrameCode.Unreadable ? "nothing" : "frame " + ghost)}");
        _report.Check("screen only: paused at the start", !session.Engine.IsPlaying && session.Engine.Position == 0);
        Close(session);

        // What cannot be opened.
        ExpectOpenFailure<FileNotFoundException>("a missing screen file", "screen recording is missing", TestFolder.Create(_media, TestMedia.Camera, Late, writeScreen: false));
        ExpectOpenFailure<InvalidDataException>("a screen file that is not a video", "screen recording", WithGarbage(TestFolder.Create(_media, TestMedia.Camera, Late, writeScreen: false), camera: false));
        ExpectOpenFailure<InvalidDataException>("an empty screen file", "screen recording", WithGarbage(TestFolder.Create(_media, TestMedia.Camera, Late, writeScreen: false), camera: false, bytes: 0));
        ExpectOpenFailure<FileNotFoundException>("a missing camera file", "camera recording is missing", TestFolder.Create(_media, TestMedia.Camera, Late, writeCamera: false));
        ExpectOpenFailure<InvalidDataException>("a camera file that is not a video", "camera recording", WithGarbage(TestFolder.Create(_media, TestMedia.Camera, Late, writeCamera: false), camera: true));

        // A cancelled open.
        using (var folder = TestFolder.Create(_media, TestMedia.Camera, Late))
        {
            Exception? thrown = null;
            try
            {
                _factory.OpenAsync(folder.Project, folder.Events, folder.Paths, new CancellationToken(canceled: true)).GetAwaiter().GetResult().DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            _report.Check("an open that is cancelled throws OperationCanceledException", thrown is OperationCanceledException, thrown?.GetType().Name ?? "nothing thrown");
            _report.Check("the folder of a cancelled open can be deleted at once", TryDelete(folder, out var error), error);
        }

        // What a player hands over when it has just opened is not always a picture: its first
        // frame, or the one for the first position it is given, came out blank in about one open
        // in six of a project without a camera offset, and was drawn for a moment. So every
        // scene drawn after an open is looked at, over many opens of each kind of project.
        var opens = _quick ? 6 : 24;
        var times = new Samples();
        var wrong = OpenMany(opens, Muted, "open", times, out var states);
        _report.Check(
            $"{opens} previews opened one after the other, with the camera late, with the camera from the start and without a camera: each shows the first frame, no scene drawn after it opened shows anything else, and no more than one needed a second attempt to open",
            wrong.Count == 0 && SecondAttemptCount(states) <= 1,
            wrong.Count == 0 ? SecondAttempts(states) : $"{wrong.Count} wrong; first: {string.Join(" | ", wrong.Take(3))}; {SecondAttempts(states)}");
        _report.Note($"those opens: {times.Summary()}");

        // A player that fails once every player has handed over a frame. The frames show that
        // the files can be decoded, so what went wrong is the player's and may pass.
        FirstAttemptFails<InvalidDataException>(
            "a player that fails while the preview opens, after every player has handed over a frame",
            Muted with { PlayersFailedWhileOpening = 1 },
            Muted with { PlayersFailedWhileOpening = 2 },
            "could not be decoded",
            "open");

        // A player that says it cannot decode what it has opened, before any frame. That is
        // not what a player says of a file that is no video, and it has been seen to pass.
        FirstAttemptFails<InvalidDataException>(
            "a player that stops decoding while the preview opens, before any player has handed over a frame",
            Muted with { PlayersStopDecodingWhileOpening = 1 },
            Muted with { PlayersStopDecodingWhileOpening = 2 },
            "could not be decoded",
            "open");
    }

    /// <summary>
    /// Opens a preview whose first attempt to open is made to fail in a way that may pass, which
    /// the engine answers by opening once more; and one whose second attempt fails as well.
    /// </summary>
    private void FirstAttemptFails<TException>(string what, StudioPreviewOptions once, StudioPreviewOptions twice, string messagePart, string name)
        where TException : Exception
    {
        var again = $"{what} (simulated): everything is opened once more, and the preview shows the first frame";
        var folder = TestFolder.Create(_media, TestMedia.Camera, Late);
        try
        {
            var session = Session.Open(once, folder);
            var attempts = session.Engine.GetDiagnostics().OpenAttempts;
            var first = session.ReadShown();
            var sought = session.SeekTo(100);
            var after = session.ReadShown();
            var right = attempts == 2 && first == new Shown(0, session.ExpectedCamera(0)) && sought && after == new Shown(100, session.ExpectedCamera(100)) && session.Events.FailedEvents == 0;
            _report.Check(again, right, $"opened at attempt {attempts} in {F(session.OpenMilliseconds, "0")} ms; picture {first}; after Seek to frame 100: {after}{(right ? string.Empty : Dump(session, name))}");
            session.Close();
        }
        catch (Exception ex)
        {
            folder.Dispose();
            _report.Check(again, false, Describe(ex) + DumpFailedOpen(ex, name + "-open"));
        }

        using var failing = TestFolder.Create(_media, TestMedia.Camera, Late);
        Exception? thrown = null;
        var watch = Stopwatch.StartNew();
        try
        {
            Session.Open(twice, failing).Close();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        var elapsed = watch.Elapsed.TotalMilliseconds;
        _report.Check(
            $"{what}, and again while it opens once more (simulated): OpenAsync gives up with a clear message, and the folder can be deleted at once",
            thrown is TException && thrown.Message.Contains(messagePart, StringComparison.OrdinalIgnoreCase) && AttemptsOf(thrown) == 2 && TryDelete(failing, out _),
            thrown is null ? "it opened" : $"{Describe(thrown)} after {F(elapsed, "0")} ms and {AttemptsOf(thrown)} attempts");
    }

    /// <summary>How many attempts an open that failed had made, by its trace.</summary>
    private static int AttemptsOf(Exception exception) =>
        1 + (Session.TraceOfFailedOpen(exception)?.Count(line => line.Contains("opening once more", StringComparison.Ordinal)) ?? 0);

    /// <summary>
    /// Opens previews one after the other, of each kind of project in turn: the camera 0.2 s
    /// late, the camera from the start, no camera. Returns what was wrong with each that did not
    /// open, did not show the first frame of its clips, or drew a scene that showed anything else
    /// in the tenth of a second after it opened.
    /// </summary>
    /// <param name="states">What each engine said about its own opening.</param>
    private List<string> OpenMany(int count, StudioPreviewOptions options, string name, Samples times, out List<StudioPreviewDiagnostics> states)
    {
        var wrong = new List<string>();
        states = [];
        for (var index = 0; index < count; index++)
        {
            var kind = (index % 3) switch { 0 => "camera late", 1 => "camera from the start", _ => "no camera" };
            var folder = index % 3 == 2 ? TestFolder.Create(_media, camera: null) : TestFolder.Create(_media, TestMedia.Camera, index % 3 == 0 ? Late : 0);
            Session session;
            try
            {
                session = Session.Open(options, folder);
            }
            catch (Exception ex)
            {
                folder.Dispose();
                wrong.Add($"open {index + 1} ({kind}): {Describe(ex)}{DumpFailedOpen(ex, name)}");
                continue;
            }

            var expected = new Shown(0, session.ExpectedCamera(0));
            var shown = session.ReadShown();
            var screen = session.ReadClipFrame(0);
            Thread.Sleep(100);
            session.WaitForIdle();
            var scenes = session.Recorder.Drain();
            var later = session.ReadShown();
            var others = scenes.Where(scene => new Shown(scene.Screen, scene.Camera) != expected).Select(scene => $"[{new Shown(scene.Screen, scene.Camera)}]").ToList();
            if (shown != expected || later != expected || screen != 0 || others.Count > 0 || session.Events.FailedEvents > 0)
            {
                wrong.Add($"open {index + 1} ({kind}): picture {shown}, a tenth of a second later {later}, the screen player's texture held frame {screen}; of {scenes.Count} scenes drawn, {others.Count} showed something else: {string.Join(" ", others.Take(4))}{string.Join("; ", session.Events.Failures())}{Dump(session, name)}");
            }

            times.Add(session.OpenMilliseconds);
            states.Add(session.Engine.GetDiagnostics());
            session.Close();
        }

        return wrong;
    }

    /// <summary>How many of a series of previews needed a second attempt to open, the first having failed in a way that may pass.</summary>
    private static int SecondAttemptCount(List<StudioPreviewDiagnostics> states) => states.Count(state => state.OpenAttempts > 1);

    private static string SecondAttempts(List<StudioPreviewDiagnostics> states) =>
        $"opened at the second attempt: {SecondAttemptCount(states)} of {states.Count}";

    private TestFolder WithGarbage(TestFolder folder, bool camera, int bytes = 256 * 1024)
    {
        var data = new byte[bytes];
        _random.NextBytes(data);
        File.WriteAllBytes(camera ? folder.Paths.CameraPath! : folder.Paths.ScreenPath, data);
        return folder;
    }

    private void ExpectOpenFailure<TException>(string what, string messagePart, TestFolder folder)
        where TException : Exception
    {
        using (folder)
        {
            Exception? thrown = null;
            var watch = Stopwatch.StartNew();
            try
            {
                Session.OpenEngine(Muted, folder, out _).DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            // Nothing says that a file like this would do better the second time.
            var elapsed = watch.Elapsed.TotalMilliseconds;
            var ok = thrown is TException && thrown.Message.Contains(messagePart, StringComparison.OrdinalIgnoreCase) && AttemptsOf(thrown) == 1;
            _report.Check(
                $"{what} fails OpenAsync with a clear message, at the first attempt",
                ok,
                thrown is null ? "it opened" : $"{thrown.GetType().Name} after {F(elapsed, "0")} ms{(AttemptsOf(thrown) == 1 ? string.Empty : $" and {AttemptsOf(thrown)} attempts")}: \"{Shorten(thrown.Message)}\"");
            if (thrown is InvalidDataException)
            {
                // What the player, or whatever read the file, gave as its reason: for a caller
                // that wants to log it or act on it without taking the sentence apart.
                var inner = thrown.InnerException;
                _report.Check(
                    $"{what}: the exception carries the error code of what failed underneath it, as InnerException.HResult",
                    inner is { HResult: < 0 },
                    inner is null ? "no inner exception" : $"0x{inner.HResult:X8} ({inner.GetType().Name})");
            }

            _report.Check($"{what}: the folder can be deleted at once afterwards", TryDelete(folder, out var error), error);
        }
    }

    private static string Shorten(string text)
    {
        var root = TestFolder.Root;
        text = text.Replace(root, "<temp>", StringComparison.OrdinalIgnoreCase).ReplaceLineEndings(" ");
        return text.Length > 200 ? text[..200] + "…" : text;
    }

    private static bool TryDelete(TestFolder folder, out string? error)
    {
        try
        {
            folder.Delete();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {Shorten(ex.Message)}";
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Paused seeks
    // ---------------------------------------------------------------------------------------

    private void PausedSeeks()
    {
        var count = _quick ? 60 : 300;
        _report.Section($"Paused seeks: {count} random positions, each read back from the picture");
        var session = OpenSession(TestMedia.Camera);
        var last = session.Folder.FrameCount - 1;
        var before = session.Engine.GetDiagnostics();

        // The edges, a keyframe and its neighbours, and the frames around the camera's first.
        var targets = new List<int> { last, 0, 1, last - 1, 60, 59, 61, 120, 119, 5, 6, 7, last, 0 };
        while (targets.Count < count)
        {
            targets.Add(_random.Next(0, last + 1));
        }

        var watch = new SeekWatch();
        var wrong = new List<string>();
        var current = 0;
        var eventsAtStart = session.Events.PositionEvents;
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            if (target == current)
            {
                target = target == last ? last - 2 : target + 1;
            }

            // Anywhere inside the frame must show that frame: its start, its middle, or wherever.
            var fraction = (index % 3) switch { 0 => 0.0, 1 => 0.5, _ => 0.05 + (0.9 * _random.NextDouble()) };
            if (SeekAndWatch(session, current, target, fraction, watch) is { } problem)
            {
                wrong.Add(problem);
            }

            current = target;
        }

        var after = session.Engine.GetDiagnostics();
        session.WaitForIdle();
        var eventsInAll = session.Events.PositionEvents - eventsAtStart;
        _report.Check(
            $"all {targets.Count} seeks show the requested frame on both clips, with Position on that frame; PositionChanged is raised twice for each, when Seek() is called and when the seek has landed, and both handlers read that frame",
            wrong.Count == 0 && eventsInAll == 2 * targets.Count,
            wrong.Count == 0 ? $"{eventsInAll} PositionChanged events for {targets.Count} seeks" : $"{wrong.Count} wrong; first: {string.Join(" | ", wrong.Take(4))}");
        _report.Note($"Seek() to the picture drawn: {watch.Latency.Summary()}");
        _report.Note($"PositionChanged for a seek: the first is handled {watch.NoticeAfterCall.Summary()} after Seek() was called; the second {watch.NoticeAfterPicture.Summary()} after the picture was drawn");
        _report.Note($"positions assigned {after.SeeksIssued - before.SeeksIssued}; seeks that needed the repair {after.Repairs - before.Repairs} ({after.LossesSeenEarly - before.LossesSeenEarly} recognised from SeekCompleted); never confirmed {after.RepairFailures - before.RepairFailures}; late answers to an earlier seek passed over {after.StrayFrames - before.StrayFrames}; seeks whose players did not all answer within the limit {after.AnswersGivenUp - before.AnswersGivenUp}");
        _report.Check("the picture changes once per seek: no scene drawn on the way shows one clip on the new frame and the other still on the old one", watch.Mixed == 0, $"{watch.Mixed} of {targets.Count} seeks, {watch.Scenes} scenes drawn");
        _report.Check("no scene drawn on the way had a blank or half-drawn screen picture", watch.Blank == 0, $"{watch.Blank} of {watch.Scenes} scenes");
        _report.Check("no seek was left without its frame", after.RepairFailures == before.RepairFailures);

        // Times outside the recording are clamped to it.
        foreach (var (time, frame, what) in new[] { (-1.0, 0, "a negative time"), (1e9, last, "a time far past the end"), ((double)TestMedia.Screen.Seconds, last, "exactly the duration"), (double.NaN, 0, "NaN") })
        {
            session.SeekTo(frame == 0 ? 100 : 50);
            var arrived = session.SeekToTime(time, frame, out _);
            var shown = session.ReadShown();
            _report.Check($"Seek({what}) shows frame {frame}", arrived && shown.Screen == frame && shown.Camera == session.ExpectedCamera(frame) && session.PositionFrame == frame, shown.ToString());
        }

        // A burst of 60 Seek calls in a row ends on the last one.
        var bursts = _quick ? 3 : 8;
        var burstWrong = new List<string>();
        var assigned = new Samples();
        var settle = new Samples();
        for (var burst = 0; burst < bursts; burst++)
        {
            var seeksBefore = session.Engine.GetDiagnostics().SeeksIssued;
            var target = 0;
            for (var call = 0; call < 60; call++)
            {
                target = _random.Next(0, last + 1);
                session.Engine.Seek(TestFolder.TimeOf(target, 0.5));
                if (burst % 2 == 1)
                {
                    // Every other burst is paced like a drag: a call each 60th of a second.
                    Thread.Sleep(16);
                }
            }

            var start = Stopwatch.GetTimestamp();
            var idle = session.WaitForIdle(8);
            settle.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            var shown = session.ReadShown();
            assigned.Add(session.Engine.GetDiagnostics().SeeksIssued - seeksBefore);
            if (!idle || shown.Screen != target || shown.Camera != session.ExpectedCamera(target) || session.PositionFrame != target)
            {
                burstWrong.Add($"last requested {target}: idle {idle}, picture {shown}, Position frame {session.PositionFrame}");
            }
        }

        _report.Check($"{bursts} bursts of 60 Seek calls each end on the last requested frame", burstWrong.Count == 0, burstWrong.Count == 0 ? null : string.Join(" | ", burstWrong.Take(3)));
        _report.Note($"positions assigned per burst of 60 calls: mean {F(assigned.Mean)} (max {F(assigned.Max, "0")}); last call to settled: {settle.Summary()}");
        _report.Check("no failure was reported", session.Events.FailedEvents == 0, string.Join("; ", session.Events.Failures()));
        Close(session);

        // For comparison, the other way of drawing: each clip's frame as it arrives, as during playback.
        var compared = _quick ? 30 : 100;
        session = OpenSession(TestMedia.Camera, options: Muted with { Seek = new StudioPreviewSeekSettings { CompositeDuringSeeks = true } });
        var arriving = new SeekWatch();
        var arrivingWrong = new List<string>();
        current = 0;
        for (var index = 0; index < compared; index++)
        {
            var target = _random.Next(10, last - 10);
            target += target == current ? 1 : 0;
            if (SeekAndWatch(session, current, target, 0.5, arriving) is { } problem)
            {
                arrivingWrong.Add(problem);
            }

            current = target;
        }

        _report.Check($"with each clip's frame drawn as it arrives, {compared} seeks end on the right picture too", arrivingWrong.Count == 0, arrivingWrong.Count == 0 ? null : string.Join(" | ", arrivingWrong.Take(3)));
        _report.Note($"drawn that way, one clip's new frame is shown over the other's old one in {arriving.Mixed} of {compared} seeks{(arriving.MixedMilliseconds.Count > 0 ? ", for " + arriving.MixedMilliseconds.Summary() : string.Empty)}; Seek() to the picture drawn with both clips on the frame: {arriving.Latency.Summary()}");
        Close(session);
    }

    /// <summary>What a series of seeks showed on the way.</summary>
    private sealed class SeekWatch
    {
        public Samples Latency { get; } = new();

        /// <summary>From the Seek() call to its first PositionChanged handled, and from the picture drawn to the second.</summary>
        public Samples NoticeAfterCall { get; } = new();

        public Samples NoticeAfterPicture { get; } = new();

        /// <summary>How long a scene with the clips on different positions stayed up.</summary>
        public Samples MixedMilliseconds { get; } = new();

        /// <summary>Seeks during which such a scene was drawn.</summary>
        public int Mixed { get; set; }

        public int Blank { get; set; }

        public int Scenes { get; set; }
    }

    /// <summary>
    /// Seeks to a frame and reads the picture back. Returns what was wrong, or null. Everything
    /// drawn on the way is looked at too: a scene with one clip on the new frame and the other on
    /// the old one, and a scene whose screen clip cannot be read.
    /// </summary>
    private string? SeekAndWatch(Session session, int current, int target, double fraction, SeekWatch watch)
    {
        session.Recorder.Drain();
        var eventsBefore = session.Events.PositionEvents;
        var arrived = session.SeekTo(target, out var milliseconds, fraction);
        var shown = session.ReadShown();
        var camera = session.ExpectedCamera(target);
        session.Events.WaitForPositionEvents(eventsBefore + 2);
        var notices = session.Events.Positions().Skip(eventsBefore).ToList();
        var noticeProblem = NoticeProblem(notices, target, session.LastDrawnAt);
        if (arrived)
        {
            watch.Latency.Add(milliseconds);
        }

        if (noticeProblem is null && session.LastDrawnAt != 0)
        {
            watch.NoticeAfterCall.Add(Stopwatch.GetElapsedTime(session.LastSoughtAt, notices[0].At).TotalMilliseconds);
            watch.NoticeAfterPicture.Add(Stopwatch.GetElapsedTime(session.LastDrawnAt, notices[1].At).TotalMilliseconds);
        }

        var problem = !arrived || shown.Screen != target || shown.Camera != camera || session.PositionFrame != target || noticeProblem is not null
            ? $"{current}->{target} (+{F(fraction, "0.00")} frame): arrived {arrived}, picture {shown}, wanted camera {camera}, Position frame {session.PositionFrame}{(noticeProblem is null ? string.Empty : ", " + noticeProblem)}{Dump(session, "seek")}"
            : null;

        var composites = session.Recorder.Drain();
        watch.Blank += composites.Count(c => c.Screen == FrameCode.Unreadable);
        watch.Scenes += composites.Count;
        for (var index = 0; index < composites.Count; index++)
        {
            var onTarget = composites[index].Screen == target;
            var cameraOnTarget = composites[index].Camera == camera;
            if (onTarget != cameraOnTarget && camera != FrameCode.Unreadable)
            {
                watch.Mixed++;
                if (index + 1 < composites.Count)
                {
                    watch.MixedMilliseconds.Add(Stopwatch.GetElapsedTime(composites[index].At, composites[index + 1].At).TotalMilliseconds);
                }

                break;
            }
        }

        return problem;
    }

    /// <summary>
    /// What is wrong with the PositionChanged events of one paused seek that moved the picture, or
    /// null. There are two: one when Seek() is called, because the position is the frame asked for
    /// from then on, and one when the seek has landed, which is after its picture was drawn. A
    /// handler of either reads the frame asked for.
    /// </summary>
    /// <param name="drawnAt">When the picture was drawn; 0 when that was not seen.</param>
    private static string? NoticeProblem(List<(long At, double Position)> notices, int target, long drawnAt)
    {
        if (notices.Count != 2)
        {
            return $"PositionChanged x{notices.Count}";
        }

        foreach (var notice in notices)
        {
            var frame = (int)Math.Round(notice.Position * Fps);
            if (frame != target)
            {
                return $"a PositionChanged handler read frame {frame}";
            }
        }

        return drawnAt != 0 && notices[1].At < drawnAt ? "the PositionChanged for the landing was raised before the picture was drawn" : null;
    }

    // ---------------------------------------------------------------------------------------
    // Frame steps
    // ---------------------------------------------------------------------------------------

    private void Steps()
    {
        var count = _quick ? 40 : 150;
        _report.Section($"Frame steps: {count} forward and {count} back, as Seek(Position ± one frame)");
        var session = OpenSession(TestMedia.Camera);
        const int origin = 100;
        session.SeekTo(origin);
        session.Recorder.Drain();
        var before = session.Engine.GetDiagnostics();

        // One after the other without a pause, as when the step key is held down.
        var forward = StepMany(session, count, +1, out var forwardLatency, waitForIdle: false);
        session.WaitForIdle();
        var afterForward = session.Engine.GetDiagnostics();
        var backward = StepMany(session, count, -1, out var backwardLatency, waitForIdle: false);
        session.WaitForIdle();
        var afterBackward = session.Engine.GetDiagnostics();

        _report.Check($"{count} steps forward each show exactly the next frame on both clips, with PositionChanged at the call and at the landing", forward.Count == 0, forward.Count == 0 ? null : $"{forward.Count} wrong; first: {string.Join(" | ", forward.Take(3))}");
        _report.Check($"{count} steps back each show exactly the previous frame on both clips, with PositionChanged at the call and at the landing", backward.Count == 0, backward.Count == 0 ? null : $"{backward.Count} wrong; first: {string.Join(" | ", backward.Take(3))}");
        _report.Note($"step forward, Seek() to the picture drawn: {forwardLatency.Summary()}; done by stepping the players {afterForward.StepsIssued - before.StepsIssued} times, fallen back to a seek {afterForward.StepFallbacks - before.StepFallbacks} times");
        _report.Note($"step back (a seek): {backwardLatency.Summary()}; repairs {afterBackward.Repairs - afterForward.Repairs}");
        _report.Check("a forward step of one frame is done by stepping the players", afterForward.StepsIssued - before.StepsIssued >= count - 2, $"{afterForward.StepsIssued - before.StepsIssued} of {count}");

        // One at a time, each left alone long enough for the clock to be brought along afterwards.
        // A stepped player draws its frame again when the clock is brought along, and sometimes
        // late. Until it has, the next frame is reached with a seek, because a step would take
        // that frame for its own.
        var lone = _quick ? 15 : 60;
        var loneWrong = StepMany(session, lone, +1, out var loneLatency, waitForIdle: true);
        var afterLone = session.Engine.GetDiagnostics();
        var loneStepped = afterLone.StepsIssued - afterBackward.StepsIssued;
        var loneAvoided = afterLone.StepsAvoided - afterBackward.StepsAvoided;
        _report.Check(
            $"{lone} single steps forward, the clock brought along after each, are exact too",
            loneWrong.Count == 0 && loneStepped + loneAvoided >= lone - 1,
            loneWrong.Count == 0 ? $"{loneStepped} stepped, {loneAvoided} reached with a seek because a player had not yet drawn its frame again for the clock's move" : string.Join(" | ", loneWrong.Take(3)));
        _report.Note($"single step forward, Seek() to the picture drawn: {loneLatency.Summary()}");
        var stepScenes = session.Recorder.Drain();
        _report.Check("no scene drawn during all these steps had a blank or half-drawn screen picture", stepScenes.Count(c => c.Screen == FrameCode.Unreadable) == 0, $"{stepScenes.Count(c => c.Screen == FrameCode.Unreadable)} of {stepScenes.Count} scenes");

        // Across the camera's first frame: it is parked, then appears, then moves.
        session.SeekTo(2);
        var edge = StepMany(session, 8, +1, out _, waitForIdle: false);
        _report.Check("steps across the moment the camera starts show it appear on its first frame", edge.Count == 0, string.Join(" | ", edge.Take(3)));

        // After steps the clock must be where the players are, or playback stalls until it has
        // caught up: a second for 30 steps, against the one seek it takes to bring the clock along.
        const int stepsBeforePlay = 30;
        session.SeekTo(origin);
        StepMany(session, stepsBeforePlay, +1, out _, waitForIdle: false);
        session.Recorder.Drain();
        var playStart = Stopwatch.GetTimestamp();
        session.Engine.Play();
        WaitForFrames(session, origin + stepsBeforePlay, 12);
        session.Engine.Pause();
        session.WaitForIdle();
        var played = session.Recorder.Drain().Where(c => c.At > playStart && c.Screen > origin + stepsBeforePlay).ToList();
        var firstNew = played.Count == 0 ? -1 : played[0].Screen;
        var firstAfter = played.Count == 0 ? double.NaN : Stopwatch.GetElapsedTime(playStart, played[0].At).TotalMilliseconds;
        _report.Check($"Play() straight after {stepsBeforePlay} steps carries on from the stepped frame without a stall", firstNew == origin + stepsBeforePlay + 1 && firstAfter < 700, $"first new frame {firstNew} after {F(firstAfter, "0")} ms");

        _report.Check("no failure was reported", session.Events.FailedEvents == 0, string.Join("; ", session.Events.Failures()));
        Close(session);

        // The same forward steps made as seeks, for the comparison.
        session = OpenSession(TestMedia.Camera, options: Muted with { Seek = new StudioPreviewSeekSettings { StepForward = false } });
        session.SeekTo(origin);
        var seekSteps = _quick ? 20 : 60;
        var asSeeks = StepMany(session, seekSteps, +1, out var seekLatency, waitForIdle: false);
        session.WaitForIdle();
        _report.Check($"{seekSteps} forward steps made as seeks are correct too", asSeeks.Count == 0, string.Join(" | ", asSeeks.Take(3)));
        _report.Note($"step forward made as a seek, for comparison: {seekLatency.Summary()}");
        Close(session);
    }

    /// <summary>Steps frame by frame the way the editor does, and returns a description of every step that was not exact.</summary>
    private List<string> StepMany(Session session, int count, int direction, out Samples latency, bool waitForIdle)
    {
        latency = new Samples();
        var wrong = new List<string>();
        for (var step = 0; step < count; step++)
        {
            var from = session.PositionFrame;
            var target = from + direction;
            var eventsBefore = session.Events.PositionEvents;
            var arrived = session.SeekToTime(session.Engine.Position + (direction / (double)Fps), target, out var milliseconds, waitForIdle: waitForIdle);

            // The call returns when the scene is drawn, so the picture can be read at once.
            var shown = session.ReadShown();
            if (arrived)
            {
                latency.Add(milliseconds);
            }

            var camera = session.ExpectedCamera(target);
            session.Events.WaitForPositionEvents(eventsBefore + 2);
            var noticeProblem = NoticeProblem(session.Events.Positions().Skip(eventsBefore).ToList(), target, session.LastDrawnAt);
            if (!arrived || shown.Screen != target || shown.Camera != camera || session.PositionFrame != target || noticeProblem is not null)
            {
                wrong.Add($"{from}->{target}: arrived {arrived}, picture {shown}, wanted camera {camera}, Position frame {session.PositionFrame}{(noticeProblem is null ? string.Empty : ", " + noticeProblem)}{Dump(session, "step")}");
            }
        }

        return wrong;
    }
}
