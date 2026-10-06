using System.Collections.Concurrent;
using System.Diagnostics;
using TinyClips.Core.Studio.Rendering;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace TinyClips.Core.Studio.Preview;

/// <summary>
/// The live preview of one Studio project: one frame-server <see cref="MediaPlayer"/> per clip on
/// a shared <see cref="MediaTimelineController"/>, each copying its frames into a texture, and a
/// render thread that draws the scene from those textures into an
/// <see cref="IStudioPreviewSurface"/>. Create it with <see cref="StudioPreviewFactory"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every member may be called from any thread. <see cref="Seek"/>, <see cref="Play"/>,
/// <see cref="UpdateProject"/> and <see cref="AttachSurface"/> only record what is wanted and
/// return; the render thread does the work. <see cref="Pause"/> stops the clock before it returns.
/// </para>
/// <para>
/// Events are raised one at a time on a thread of the engine's own, never on the caller's and
/// never while the engine holds a lock. A handler may call back into the engine.
/// </para>
/// <para>
/// The engine owns its Direct3D device. Everything that uses the device's immediate context (the
/// players' frame copies, the scene renderer, a surface's buffers) does so under the device's lock.
/// </para>
/// </remarks>
public sealed partial class StudioPreviewEngine : IStudioPreview, IStudioPreviewTransport
{
    private const int OpenTimeoutMilliseconds = 30_000;
    private const int FirstFrameTimeoutMilliseconds = 10_000;
    private const int ReadyTimeoutMilliseconds = 15_000;
    private const int ThreadJoinTimeoutMilliseconds = 5_000;
    private const int PauseDeliveryWaitMilliseconds = 50;
    private const int PausePassWaitMilliseconds = 100;
    private const int PauseRoundWaitMilliseconds = 500;
    private const int FilesClosedTimeoutMilliseconds = 5_000;

    // Proving players that came from another graphics adapter (ProvePlayers): how many rounds and
    // how long at most, and how long the players have to have said nothing before their pictures
    // are compared.
    private const int ProofRoundLimit = 8;
    private const int ProofTimeLimitMilliseconds = 8_000;
    private const int ProofQuietMilliseconds = 60;
    private const int ProofQuietLimitMilliseconds = 1_500;

    // How long a player is given to hand its first frame over by itself once the frames may be taken.
    private const int FirstFramePullMilliseconds = 100;

    // A preview that fails while it opens is opened once more when what went wrong may pass.
    private const int OpenAttemptLimit = 2;

    private enum PreviewEventKind
    {
        PositionChanged,
        IsPlayingChanged,
        Failed,
    }

    private readonly record struct PreviewEvent(PreviewEventKind Kind, StudioPreviewFailedEventArgs? Failure = null);

    private readonly StudioEvents _events;
    private readonly string _projectDirectory;
    private readonly StudioPreviewTimeline _timeline;
    private readonly StudioPreviewSeekPolicy _policy;
    private readonly MediaTimelineController _controller;
    private readonly StudioPreviewClip[] _clips;
    private readonly bool _forceMuted;
    private readonly bool _zeroVolume;
    private readonly bool _softwareDevice;
    private readonly Func<IStudioPersonFinder?>? _personFinderFactory;
    private readonly bool _stampPictures;
    private readonly StudioPreviewNamingSettings _naming;
    private readonly Func<TimeSpan>? _renderDelay;
    private readonly Func<TimeSpan>? _stopDelay;
    private readonly Func<TimeSpan>? _pauseDelay;
    private readonly bool _stopNotedLate;
    private readonly TypedEventHandler<MediaTimelineController, object> _endedHandler;
    private readonly TypedEventHandler<MediaTimelineController, MediaTimelineControllerFailedEventArgs> _controllerFailedHandler;

    private readonly Thread _engineThread;
    private readonly Thread _eventThread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly BlockingCollection<PreviewEvent> _eventQueue = [];
    private readonly ManualResetEventSlim _openedGate = new(false);
    private readonly ManualResetEventSlim _firstFramesGate = new(false);
    private readonly ManualResetEventSlim _readyGate = new(false);
    private readonly object _commandLock = new();
    private readonly object _transportLock = new();
    private readonly object _closeLock = new();

    // What the caller wants. _commandLock.
    private StudioProject _latestProject;
    private int _projectSerial;

    // The clock. _transportLock.
    private bool _wantPlaying;
    private bool _controllerRunning;

    private readonly StudioPreviewPosition _position = new();
    private readonly Action<string>? _trace;
    private readonly long _createdAt = Stopwatch.GetTimestamp();
    private long _pausedAt = StudioPreviewHandOverKinds.Never;
    private volatile bool _discardLateFrames;
    private long _lateFramesDiscarded;
    private Task? _closeTask;
    private volatile bool _opening = true;

    // On the software adapter the players may be on another adapter than the engine's device.
    // Their first frames are then held until each has one, and looked at (ReleaseFirstFrames).
    private readonly bool _trustsFirstFrames;
    private readonly bool _looksAtFirstFrames;
    private readonly bool _playersKeepFirstFrames;
    private volatile bool _firstFramesHeld;
    private long _firstFramesReleasedAt;
    private readonly int _openAttempt;
    private readonly string[] _frameTimesNotes;
    private readonly bool _failsAfterFirstFrames;
    private readonly bool _stopsDecodingBeforeFirstFrames;
    private volatile bool _aPlayerStoppedDecoding;
    private int _firstFramesLookedAt;
    private int _firstFramesEmpty;
    private int _firstFramesPulled;
    private int _proofRounds;
    private bool _proofHeld = true;
    private volatile bool _spendFirst;
    private volatile bool _closing;
    private volatile bool _stopRequested;
    private volatile bool _eventsClosed;
    private volatile bool _wakeDisposed;
    private int _eventsOutstanding;
    private int _failed;
    private Exception? _openFailure;
    private double _filesClosedAfterMilliseconds = -1;
    private bool _filesClosed = true;
    private int _filesWaitedFor;

    private StudioPreviewEngine(
        StudioProject project,
        StudioEvents events,
        string projectDirectory,
        string screenPath,
        string? cameraPath,
        double cameraFrameRate,
        StudioPreviewFrameTimes? screenTimes,
        StudioPreviewFrameTimes? cameraTimes,
        string[] frameTimesNotes,
        StudioPreviewOptions options,
        StudioGraphicsDevice graphics,
        StudioSceneRenderer renderer,
        int openAttempt)
    {
        _openAttempt = openAttempt;
        _frameTimesNotes = frameTimesNotes;
        _simulatedDeviceLosses = openAttempt <= options.DevicesLostWhileOpening ? 1 : 0;
        _failsAfterFirstFrames = openAttempt <= options.PlayersFailedWhileOpening;
        _stopsDecodingBeforeFirstFrames = openAttempt <= options.PlayersStopDecodingWhileOpening;
        _events = events;
        _projectDirectory = projectDirectory;
        _latestProject = project;
        _renderProject = project;
        _forceMuted = options.ForceMuted;
        _zeroVolume = options.ForceMuted || options.ZeroVolume;
        _softwareDevice = options.SoftwareDevice;
        _personFinderFactory = options.PersonFinderFactory;
        _stampPictures = options.StampPictures;
        _naming = options.Naming;
        _renderDelay = options.RenderDelay;
        _stopDelay = options.StopDelay;
        _pauseDelay = options.PauseDelay;
        _stopNotedLate = options.StopNotedLate;
        _trustsFirstFrames = options.TrustFirstFrames;
        _looksAtFirstFrames = graphics.IsSoftware && !options.TrustFirstFrames;
        _playersKeepFirstFrames = _looksAtFirstFrames && options.PlayersKeepFirstFrames;
        _firstFramesHeld = _looksAtFirstFrames;
        _trace = options.Trace;
        _graphics = graphics;
        _renderer = renderer;

        var screenSource = project.Sources.Screen;
        var frameRate = StudioPreviewTimeMath.NormalizeFrameRate(screenSource.FrameRate);
        // Where a file's frame times were read, its frames are counted as the file has them.
        // Otherwise each clip is taken to have a frame at the start of every slot of its rate.
        var tracks = new List<StudioPreviewClipTiming>
        {
            screenTimes is null
                ? new StudioPreviewClipTiming(frameRate, StudioPreviewTimeMath.FrameCount(screenSource.Duration, frameRate), 0)
                : StudioPreviewClipTiming.WithTimes(screenTimes, frameRate, 0),
        };
        if (project.Sources.Camera is { } cameraSource && cameraPath is not null)
        {
            if (cameraTimes is null)
            {
                var cameraRate = StudioPreviewTimeMath.NormalizeFrameRate(cameraFrameRate);
                tracks.Add(new StudioPreviewClipTiming(cameraRate, StudioPreviewTimeMath.FrameCount(cameraSource.Duration, cameraRate), cameraSource.StartOffset, cameraSource.Duration));
            }
            else
            {
                // The rate its frames usually come at, which is not the rate the probe reads
                // from the file: that one is frames by length, and a camera that stalled or
                // gave half its frames in low light has fewer than its length would hold.
                var usual = StudioPreviewTimeMath.TicksPerSecond / (double)cameraTimes.TypicalSpacing;
                tracks.Add(StudioPreviewClipTiming.WithTimes(cameraTimes, usual, cameraSource.StartOffset, cameraSource.Duration));
            }
        }

        _timeline = new StudioPreviewTimeline(screenSource.Duration, frameRate, tracks);
        _policy = new StudioPreviewSeekPolicy(this, _timeline, options.Seek);
        _consecutiveCopyFailures = new int[tracks.Count];
        _tookFrame = new bool[tracks.Count];
        _framesWithoutNumber = new long[tracks.Count];
        _framesShownWithoutNumber = new long[tracks.Count];
        _framesNumberedLate = new long[tracks.Count];
        _framesShownUnsure = new long[tracks.Count];
        _framesInferred = new long[tracks.Count];
        _framesPassedOver = new long[tracks.Count];
        _engineThread = NewThread(EngineLoop, "TinyClips.StudioPreview.Render");
        _eventThread = NewThread(EventLoop, "TinyClips.StudioPreview.Events");
        _endedHandler = (_, _) => Post(new Signal(SignalKind.Ended, 0, Stopwatch.GetTimestamp()));
        _controllerFailedHandler = (_, args) => Post(new Signal(SignalKind.MediaFailed, 0, Stopwatch.GetTimestamp(), 0, "The preview clock failed.", args.ExtendedError));

        // One clock for every player. Without a duration it would run on past the end of the recording.
        _controller = new MediaTimelineController { ClockRate = 1.0 };
        if (_timeline.Duration > 0)
        {
            _controller.Duration = TimeSpan.FromSeconds(_timeline.Duration);
        }

        _controller.Ended += _endedHandler;
        _controller.Failed += _controllerFailedHandler;

        var clips = new List<StudioPreviewClip>(tracks.Count);
        try
        {
            clips.Add(CreateClip(0, "screen", screenPath, screenSource.Width, screenSource.Height, tracks[0], project.Audio.Muted));
            if (tracks.Count > 1)
            {
                var camera = project.Sources.Camera!;
                clips.Add(CreateClip(1, "camera", cameraPath!, camera.Width, camera.Height, tracks[1], project.Audio.Muted));
            }
        }
        catch
        {
            foreach (var clip in clips)
            {
                ClosePlayer(clip);
            }

            UnhookController();
            throw;
        }

        _clips = [.. clips];
        _appliedProjectMuted = project.Audio.Muted;
    }

    /// <summary>
    /// The start time of the frame being shown. A position asked for with <see cref="Seek"/> is
    /// reported from the moment that call returns, before the picture has got there, and until it
    /// has, nothing else is: no frame that was shown before the call is ever reported after it.
    /// When playback stops by itself at the end of the recording it is the last frame.
    /// </summary>
    public double Position => _timeline.FrameStart(_position.Frame);

    /// <inheritdoc/>
    public bool IsPlaying
    {
        get
        {
            lock (_transportLock)
            {
                return _wantPlaying;
            }
        }
    }

    /// <summary>The length of the recording in seconds: the screen source's duration.</summary>
    public double Duration => _timeline.Duration;

    /// <summary>The frame rate of the recording's timeline. <see cref="Position"/> is always a whole number of frames.</summary>
    public double FrameRate => _timeline.FrameRate;

    /// <summary>
    /// Raised when <see cref="Position"/> changes: when <see cref="Seek"/> asks for another frame
    /// than the one reported, at the call; for every frame of playback; and when the players come
    /// to rest on another frame after the clock stopped. Raised once more when a seek has landed:
    /// the position is the same then, and the picture now shows it.
    /// </summary>
    public event EventHandler? PositionChanged;

    /// <inheritdoc/>
    public event EventHandler? IsPlayingChanged;

    /// <inheritdoc/>
    public event EventHandler<StudioPreviewFailedEventArgs>? Failed;

    private bool IsUnusable => _closing || Volatile.Read(ref _failed) != 0;

    /// <summary>
    /// Opens the recording and returns once both clips show their first frame, paused at the
    /// start. Blocks; the factory calls it on a worker thread.
    /// </summary>
    internal static StudioPreviewEngine Open(StudioProject project, StudioEvents events, StudioProjectPaths paths, StudioPreviewOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var screenPath = project.Sources.Screen.External ? project.Sources.Screen.File : paths.ScreenPath;
        if (string.IsNullOrWhiteSpace(screenPath) || !File.Exists(screenPath))
        {
            throw new FileNotFoundException($"The screen recording is missing: {screenPath}", screenPath);
        }

        string? cameraPath = null;
        var cameraFrameRate = 0.0;
        if (project.Sources.Camera is not null)
        {
            // A project that has a camera track cannot be shown or exported correctly without its
            // file, so opening fails instead of quietly showing the screen alone.
            cameraPath = paths.CameraPath;
            if (string.IsNullOrWhiteSpace(cameraPath) || !File.Exists(cameraPath))
            {
                throw new FileNotFoundException($"The camera recording is missing: {cameraPath ?? "camera.mp4"}", cameraPath ?? "camera.mp4");
            }

            // The project does not record the camera's frame rate; the file does.
            try
            {
                cameraFrameRate = MediaFileProbe.Probe(cameraPath, project.Sources.Screen.FrameRate).FrameRate;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException($"The camera recording could not be opened: {cameraPath}", ex);
            }
        }

        // The files' own frame times, where they are asked for and can be read. Read once: a
        // second attempt to open plays the same files.
        StudioPreviewFrameTimes? screenTimes = null;
        StudioPreviewFrameTimes? cameraTimes = null;
        var frameTimesNotes = new string[cameraPath is null ? 1 : 2];
        Array.Fill(frameTimesNotes, "the grid: the file's frame times were not asked for");
        if (options.FrameTimesFromFile)
        {
            screenTimes = ReadFrameTimes(screenPath, "screen", options, out frameTimesNotes[0]);
            if (cameraPath is not null)
            {
                cameraTimes = ReadFrameTimes(cameraPath, "camera", options, out frameTimesNotes[1]);
            }
        }

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StudioGraphicsDevice? graphics = null;
            StudioSceneRenderer? renderer = null;
            StudioPreviewEngine? engine = null;
            try
            {
                try
                {
                    graphics = CreateDevice(options.SoftwareDevice);
                    renderer = CreateRenderer(graphics, options.PersonFinderFactory);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("The preview needs a graphics device, and none could be created.", ex);
                }

                engine = new StudioPreviewEngine(project, events, paths.ProjectDirectory, screenPath, cameraPath, cameraFrameRate, screenTimes, cameraTimes, frameTimesNotes, options, graphics, renderer, attempt);
                engine.Start(cancellationToken);
                return engine;
            }
            catch (Exception ex)
            {
                if (engine is not null)
                {
                    engine.Close(joinEventThread: true);
                }
                else if (graphics is not null)
                {
                    if (renderer is not null)
                    {
                        lock (graphics.Gate)
                        {
                            renderer.Dispose();
                        }
                    }

                    graphics.Dispose();
                }

                // What went wrong may pass: see StudioPreviewOpenFailure. Then everything is opened
                // once more, on a new device and with new players.
                var deliveredAll = engine is { EveryPlayerDeliveredAFrame: true };
                var why = StudioPreviewOpenFailure.WorthAnotherAttempt(ex, deliveredAll, engine is { APlayerStoppedDecoding: true });
                if (attempt >= OpenAttemptLimit || why is null || cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                options.Trace?.Invoke($"          {why} while the preview opened (0x{(ex.InnerException ?? ex).HResult:X8}): opening once more in {options.SecondAttemptWait.TotalMilliseconds:0} ms");
                if (options.SecondAttemptWait > TimeSpan.Zero)
                {
                    // What made this attempt fail is given the time to pass. It has been seen to
                    // outlast a second attempt that was made at once.
                    cancellationToken.WaitHandle.WaitOne(options.SecondAttemptWait);
                }
            }
        }
    }

    /// <summary>
    /// A clip's frame times from its file's index, or null when they cannot be read from it:
    /// the clip is then played on the grid, as every clip is where the times are not asked for.
    /// </summary>
    private static StudioPreviewFrameTimes? ReadFrameTimes(string path, string name, StudioPreviewOptions options, out string note)
    {
        var times = StudioPreviewFrameTimes.TryRead(path, out var problem);
        note = times is null
            ? $"the grid: {problem ?? "the file's index was not read"}"
            : string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"the file's: {times.Count} frames, the first at {times.Start(0) / 10000.0:0.0###} ms, the last at {times.Start(times.Count - 1) / 10000.0:0.0###} ms, usually {times.TypicalSpacing / 10000.0:0.0###} ms apart");
        options.Trace?.Invoke($"          frame numbers of the {name}: {note}");
        return times;
    }

    /// <summary>Whether each player has handed over at least one frame, which shows that its file can be decoded.</summary>
    private bool EveryPlayerDeliveredAFrame
    {
        get
        {
            foreach (var clip in _clips)
            {
                if (!clip.HasDeliveredFrame)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Whether a player reported that it could not decode what it had opened, as against a source
    /// it does not support: the one kind of failure before the first frames that may pass.
    /// </summary>
    private bool APlayerStoppedDecoding => _aPlayerStoppedDecoding;

    /// <summary>The attempt that opened this preview: 1, or 2 when the first failed in a way that may pass.</summary>
    internal int OpenAttempt => _openAttempt;

    /// <inheritdoc/>
    public void UpdateProject(StudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (IsUnusable)
        {
            return;
        }

        lock (_commandLock)
        {
            _latestProject = project;
            _projectSerial++;
        }

        WakeEngine();
    }

    /// <inheritdoc/>
    public void Play()
    {
        if (IsUnusable)
        {
            return;
        }

        lock (_transportLock)
        {
            if (_wantPlaying)
            {
                return;
            }

            _wantPlaying = true;
        }

        RaiseLater(new PreviewEvent(PreviewEventKind.IsPlayingChanged));
        WakeEngine();
    }

    /// <summary>
    /// Stops playback. When it returns the clock is stopped, <see cref="Position"/> is the start
    /// of the frame the screen's picture shows, and neither moves on afterwards: a frame of this
    /// playback that has not reached the picture by then never does. A camera that stopped a
    /// frame apart is then brought onto the frame that belongs with the screen's. The call takes
    /// a few milliseconds as a rule, and as long as the render thread's round when that is held up.
    /// </summary>
    /// <remarks>
    /// The frame is the last one that had a number (<see cref="StudioPreviewFrameNamer"/>). On a
    /// PC that is held up, the scene may have shown a later frame than that a moment before:
    /// one of which it could not be told which frame it was. It is taken off the scene here.
    /// When the number rested on an inference about the player, the frame is fetched anew
    /// once the call has returned. The picture changes then only if the inference was wrong,
    /// and it changes to the frame <see cref="Position"/> names.
    /// </remarks>
    public void Pause()
    {
        if (_closing)
        {
            return;
        }

        bool stoppedClock;
        lock (_transportLock)
        {
            if (!_wantPlaying)
            {
                return;
            }

            _wantPlaying = false;
            stoppedClock = _controllerRunning;
            if (stoppedClock)
            {
                StopClock();
            }
        }

        if (stoppedClock)
        {
            // No frame is being copied any more, and one pass of the render thread later the last
            // one is on the picture and in the position.
            WaitForDeliveries(PauseDeliveryWaitMilliseconds);
            WaitForPass(PausePassWaitMilliseconds);
            if (_pauseDelay is { } delay && delay() is { Ticks: > 0 } wait)
            {
                // What the checks make of a thread that is kept from going on just here.
                Thread.Sleep(wait);
            }

            // That is so when nothing holds the process up. When something does, a frame can
            // still be on its way: from here on it is left out, so that what the caller reads
            // now stays true. The render thread is between two rounds while the lock is held.
            // Not for ever: a render thread that hangs must not take the caller with it.
            var entered = Monitor.TryEnter(_roundLock, PauseRoundWaitMilliseconds);

            // Not when Play() has come from another thread while this one waited: the render
            // thread may have started the clock again by now, and every frame of that playback
            // would be left out until the next pause. Resume() opens the picture under the
            // same lock.
            bool shut;
            lock (_transportLock)
            {
                shut = !_wantPlaying;
                if (shut)
                {
                    _playbackShut = true;
                }
            }

            if (entered)
            {
                // With the render thread kept out of its round, its pictures can be put right
                // from here. One pass later the scene shows them.
                var redraw = shut && ShowNumberedPictures();
                Monitor.Exit(_roundLock);
                if (redraw)
                {
                    WaitForPass(PausePassWaitMilliseconds);
                }
            }
        }

        RaiseLater(new PreviewEvent(PreviewEventKind.IsPlayingChanged));
        WakeEngine();
    }

    /// <summary>
    /// Shows the frame that contains <paramref name="sourceTime"/>, clamped to the recording, and
    /// does not change whether the preview is playing. When the call returns,
    /// <see cref="Position"/> is that frame's start; the picture follows. The newest request
    /// replaces any that has not been carried out yet. A preview that has failed or is being
    /// disposed ignores the call.
    /// </summary>
    public void Seek(double sourceTime)
    {
        if (IsUnusable)
        {
            return;
        }

        var frame = _timeline.FrameForSeek(sourceTime);
        var changed = _position.Request(frame);
        if (_trace is not null)
        {
            Note($"asked for frame {frame}");
        }

        if (changed)
        {
            RaiseLater(new PreviewEvent(PreviewEventKind.PositionChanged));
        }

        WakeEngine();
    }

    /// <summary>
    /// Stops the preview and releases everything. When the returned task completes, the players
    /// are closed, the media files are no longer open, an attached surface has released its
    /// buffers, and no event will be raised any more. Safe to call more than once and from an
    /// event handler.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_closeLock)
        {
            if (_closeTask is null)
            {
                _closing = true;

                // A handler that disposes the preview runs on the event thread, which therefore
                // cannot be waited for.
                var joinEventThread = Environment.CurrentManagedThreadId != _eventThread.ManagedThreadId;
                _closeTask = Task.Run(() => Close(joinEventThread));
            }

            return new ValueTask(_closeTask);
        }
    }

    private static Thread NewThread(ThreadStart start, string name)
    {
        var thread = new Thread(start) { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.MTA);
        return thread;
    }

    private StudioPreviewClip CreateClip(int index, string name, string path, int width, int height, StudioPreviewClipTiming timing, bool projectMuted)
    {
        var player = new MediaPlayer
        {
            AutoPlay = false,
            IsMuted = StudioPreviewAudio.IsMuted(isCamera: index != 0, projectMuted, _forceMuted),
            IsVideoFrameServerEnabled = true,
        };
        MediaSource? source = null;
        try
        {
            if (_zeroVolume)
            {
                player.Volume = 0;
            }

            // The engine drives the transport itself: no system media controls.
            player.CommandManager.IsEnabled = false;
            player.TimelineController = _controller;

            source = MediaSource.CreateFromUri(new Uri(path));
            var clip = new StudioPreviewClip(index, name, path, width, height, timing, player, source, _naming);
            clip.FrameHandler = (sender, _) => OnVideoFrameAvailable(clip, sender);
            clip.OpenedHandler = (_, _) => Post(new Signal(SignalKind.Opened, index, Stopwatch.GetTimestamp()));
            clip.EndedHandler = (_, _) =>
            {
                clip.EndReached();
                Post(new Signal(SignalKind.PlayerEnded, index, Stopwatch.GetTimestamp()));
            };
            clip.FailedHandler = (_, args) =>
            {
                // What a player says of a file that is no video is SourceNotSupported.
                if (args.Error is MediaPlayerError.DecodingError or MediaPlayerError.Unknown)
                {
                    _aPlayerStoppedDecoding = true;
                }

                Post(new Signal(
                    SignalKind.MediaFailed,
                    index,
                    Stopwatch.GetTimestamp(),
                    0,
                    $"The {name} recording could not be decoded ({args.Error}{(string.IsNullOrWhiteSpace(args.ErrorMessage) ? string.Empty : ": " + args.ErrorMessage.Trim())}).",
                    args.ExtendedErrorCode));
            };
            clip.SeekCompletedHandler = (_, _) =>
            {
                var at = Stopwatch.GetTimestamp();
                clip.SeekCompletedRaised(at);
                Post(new Signal(SignalKind.SeekCompleted, index, at));
            };
            player.VideoFrameAvailable += clip.FrameHandler;
            player.MediaOpened += clip.OpenedHandler;
            player.MediaEnded += clip.EndedHandler;
            player.MediaFailed += clip.FailedHandler;
            clip.Session.SeekCompleted += clip.SeekCompletedHandler;
            return clip;
        }
        catch
        {
            source?.Dispose();
            player.Dispose();
            throw;
        }
    }

    private void Start(CancellationToken cancellationToken)
    {
        // The players need somewhere to put their first frames before any surface is attached.
        var graphics = _graphics;
        lock (graphics.Gate)
        {
            var natural = StudioCanvasMath.NaturalSize(_renderProject);
            var fit = Math.Min(1, Math.Min(DefaultSurfaceWidth / natural.Width, DefaultSurfaceHeight / natural.Height));
            UpdateCopyTargets(graphics, _renderProject, natural.Width * fit, natural.Height * fit);
        }

        _eventThread.Start();
        _engineThread.Start();
        foreach (var clip in _clips)
        {
            clip.Player.Source = clip.Source;
        }

        if (_trace is not null)
        {
            Note("sources set");
        }

        if (_stopsDecodingBeforeFirstFrames)
        {
            // For the check of what opening does about a player that stops decoding at once.
            var failing = _clips[0];
            _aPlayerStoppedDecoding = true;
            Post(new Signal(SignalKind.MediaFailed, failing.Index, Stopwatch.GetTimestamp(), 0, $"The {failing.Name} recording could not be decoded (DecodingError, simulated).", new InvalidOperationException("Simulated failure of a player.")));
        }

        if (!_openedGate.Wait(OpenTimeoutMilliseconds, cancellationToken))
        {
            ThrowIfOpenFailed();
            throw new TimeoutException("The recording did not open in time.");
        }

        ThrowIfOpenFailed();

        // With the clock at zero and no offsets yet, every player hands over the first frame of
        // its stream by itself. That is the proof that each clip can be decoded. Where the frames
        // are held until every player has one, the player to blame is one that never had a frame.
        _firstFramesGate.Wait(FirstFrameTimeoutMilliseconds, cancellationToken);
        ThrowIfOpenFailed();
        foreach (var clip in _clips)
        {
            if (!clip.HasDeliveredFrame && !clip.HasAnnouncedFrame)
            {
                throw new InvalidDataException($"The {clip.Name} recording could not be decoded: it delivered no frame.");
            }
        }

        foreach (var clip in _clips)
        {
            if (!clip.HasDeliveredFrame)
            {
                throw new InvalidDataException($"The {clip.Name} recording could not be decoded: it delivered no frame.");
            }
        }

        if (_failsAfterFirstFrames)
        {
            // For the check of what opening does about a player that fails from here on.
            var failing = _clips[^1];
            Post(new Signal(SignalKind.MediaFailed, failing.Index, Stopwatch.GetTimestamp(), 0, $"The {failing.Name} recording could not be decoded (simulated).", new InvalidOperationException("Simulated failure of a player.")));
        }

        // Only now is each clip that does not start with the recording put at its place on the
        // clock. A player given its offset before it opens sometimes never answers the clock at
        // all (about one open in thirty). The offset is added to the clock, so a clip that starts
        // late gets a negative one.
        var offsets = false;
        foreach (var clip in _clips)
        {
            if (clip.Timing.StartOffset != 0)
            {
                clip.Player.TimelineControllerPositionOffset = TimeSpan.FromTicks(-StudioPreviewTimeMath.ToTicks(clip.Timing.StartOffset));
                offsets = true;
                if (_trace is not null)
                {
                    Note($"offset of clip {clip.Index} set");
                }
            }
        }

        // What a player makes of the first position it is given does not count, whether or not
        // its offset was changed: see RequestFirstSeek.
        _spendFirst = offsets || !_trustsFirstFrames;

        // A player whose first frame left nothing in its texture came from another graphics
        // adapter, and what it hands over is not believed until it has shown the same picture twice.
        var moved = false;
        foreach (var clip in _clips)
        {
            moved |= clip.FirstFrameWasEmpty;
        }

        if (moved)
        {
            ProvePlayers(cancellationToken);
        }

        // The first frame is asked for the way a caller asks for any other. It is frame 0, which
        // is what is reported already, so nobody is told.
        GoTo(0, cancellationToken);
        if (moved)
        {
            // The pass that landed has to be over before anybody may be told anything.
            WaitForQuiet(0, cancellationToken);
        }

        _opening = false;
    }

    /// <summary>Asks for a frame the way a caller does and waits until the players have landed on it. While opening only.</summary>
    private void GoTo(long frame, CancellationToken cancellationToken)
    {
        var landed = Volatile.Read(ref _landingCount);
        _position.Request(frame);
        WakeEngine();
        WaitForLanding(landed + 1, cancellationToken);
    }

    /// <summary>
    /// Sends the players back and forth between two frames until they have shown the same
    /// pictures twice in a row, or the limit is reached: see <see cref="StudioPreviewProof"/>.
    /// The preview opens either way; a picture that could not be proven is put right by the first
    /// seek that is.
    /// </summary>
    private void ProvePlayers(CancellationToken cancellationToken)
    {
        if (!_timeline.TryPickProofFrames(avoid: 0, out var first, out var second))
        {
            // A recording one frame long: there is nowhere to send the players.
            Volatile.Write(ref _proofHeld, false);
            return;
        }

        var proof = new StudioPreviewProof();
        var start = Stopwatch.GetTimestamp();
        var held = false;
        while (!held && proof.Rounds < ProofRoundLimit && Stopwatch.GetElapsedTime(start).TotalMilliseconds < ProofTimeLimitMilliseconds)
        {
            GoTo(second, cancellationToken);
            GoTo(first, cancellationToken);
            WaitForQuiet(ProofQuietMilliseconds, cancellationToken);
            var pictures = ReadPictures(first);
            held = proof.Offer(pictures);
            if (_trace is not null)
            {
                Note($"proof round {proof.Rounds} on frame {first}: {string.Join(' ', pictures.Select(picture => picture == StudioPreviewPixels.Nothing ? "nothing" : picture.ToString("X16")))}{(held ? ", the same as the round before" : string.Empty)}");
            }
        }

        Volatile.Write(ref _proofRounds, proof.Rounds);
        Volatile.Write(ref _proofHeld, held);
    }

    /// <summary>
    /// Waits until the render thread has nothing left to do and no player has announced a frame
    /// for <paramref name="quietMilliseconds"/>. Gives up, without saying so, after a second and a half.
    /// </summary>
    private void WaitForQuiet(int quietMilliseconds, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < ProofQuietLimitMilliseconds)
        {
            ThrowIfOpenFailed();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_position.HasNewRequest && _signals.IsEmpty && _engineIdle && IsQuietFor(quietMilliseconds))
            {
                return;
            }

            Thread.Sleep(2);
        }
    }

    private bool IsQuietFor(int milliseconds)
    {
        foreach (var clip in _clips)
        {
            if (clip.DeliveriesInFlight > 0 || Stopwatch.GetElapsedTime(clip.LastCallbackStartedAt).TotalMilliseconds < milliseconds)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Waits until the players have come to rest <paramref name="count"/> times since the engine started.</summary>
    private void WaitForLanding(int count, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            ThrowIfOpenFailed();
            if (Volatile.Read(ref _landingCount) >= count)
            {
                return;
            }

            var remaining = ReadyTimeoutMilliseconds - (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (remaining <= 0 || !_readyGate.Wait(remaining, cancellationToken))
            {
                ThrowIfOpenFailed();
                throw new TimeoutException($"The recording did not show its first frame in time ({DescribeOpening()}).");
            }

            // Set again by the render thread at the next landing. One that slips in before this
            // line is found by the count at the top of the loop.
            _readyGate.Reset();
        }
    }

    /// <summary>What the players have done so far, for the message of an open that timed out.</summary>
    private string DescribeOpening()
    {
        var parts = new List<string>();
        foreach (var clip in _clips)
        {
            parts.Add($"{clip.Name}: {clip.CallbacksStarted} frames, {clip.SeeksCompleted} seeks completed");
        }

        return $"{string.Join("; ", parts)}; clock moved {_policy.SeeksIssued} times";
    }

    private void ThrowIfOpenFailed()
    {
        if (Volatile.Read(ref _openFailure) is { } failure)
        {
            throw failure;
        }
    }

    // _transportLock held.
    private void StopClock()
    {
        if (_trace is not null)
        {
            Note("clock stopped");
        }

        if (_stopNotedLate)
        {
            // As it was before, for the check that shows what the order below is for.
            _controller.Pause();
            _controllerRunning = false;
            HoldAfterStop();
            Interlocked.Exchange(ref _pausedAt, Stopwatch.GetTimestamp());
            _discardLateFrames = true;
            return;
        }

        // A player can still hand over a frame after the clock has stopped: the one that comes
        // due in the moment the player goes on by itself, with the position the clock stopped
        // on, which is the position of the frame before it. That is no frame of this playback
        // (StudioPreviewHandOverKinds), and it is left out until the render thread asks the
        // players for something again.
        //
        // From when on that is so is said before the clock is told, and that frames are left out
        // is said before from when. The clock stops at once, and this thread can be kept from
        // saying anything afterwards for longer than a frame lasts: seen with every processor
        // busy, when a frame that set out 8 ms after the clock had stopped was taken for a frame
        // of playback, and by its position for the frame before it. A frame that sets out
        // between here and the clock's stopping is left out as well, which costs nothing: the
        // picture rests on the frame before it, and the players are brought there.
        _discardLateFrames = true;
        Interlocked.Exchange(ref _pausedAt, Stopwatch.GetTimestamp());
        _controller.Pause();
        _controllerRunning = false;
        HoldAfterStop();
    }

    // What the checks make of a thread that is kept from going on once the clock has stopped.
    private void HoldAfterStop()
    {
        if (_stopDelay is { } delay && delay() is { Ticks: > 0 } wait)
        {
            Thread.Sleep(wait);
        }
    }

    /// <summary>Waits, briefly, until no frame is being copied any more.</summary>
    private void WaitForDeliveries(int milliseconds)
    {
        var start = Stopwatch.GetTimestamp();
        var spinner = default(SpinWait);
        while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < milliseconds)
        {
            var busy = false;
            foreach (var clip in _clips)
            {
                busy |= clip.DeliveriesInFlight > 0;
            }

            if (!busy)
            {
                return;
            }

            // A copy takes a millisecond, and the sleep a spin wait falls back on takes a whole
            // timer tick. So it does not sleep at first.
            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 5)
            {
                spinner.SpinOnce(sleep1Threshold: -1);
            }
            else
            {
                spinner.SpinOnce();
            }
        }
    }

    /// <summary>
    /// Waits, briefly, until the render thread has made a pass that began after this call and drew
    /// what it had. Everything the players handed over before the call is on the picture and in
    /// the position then.
    /// </summary>
    private void WaitForPass(int milliseconds)
    {
        if (Environment.CurrentManagedThreadId == _engineThread.ManagedThreadId)
        {
            return;
        }

        // A pass that is under way began before this call and may have looked already.
        var wanted = Interlocked.Read(ref _passesBegun) + 1;
        WakeEngine();
        var start = Stopwatch.GetTimestamp();
        lock (_passLock)
        {
            while (_passesSettled < wanted && !_stopRequested)
            {
                var remaining = milliseconds - (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (remaining <= 0 || !Monitor.Wait(_passLock, remaining))
                {
                    return;
                }
            }
        }
    }

    private void WakeEngine()
    {
        if (_wakeDisposed)
        {
            return;
        }

        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RaiseLater(PreviewEvent item)
    {
        if (_eventsClosed)
        {
            return;
        }

        Interlocked.Increment(ref _eventsOutstanding);
        try
        {
            _eventQueue.Add(item);
        }
        catch (InvalidOperationException)
        {
            // Closed in the meantime.
            Interlocked.Decrement(ref _eventsOutstanding);
        }
    }

    private void EventLoop()
    {
        foreach (var item in _eventQueue.GetConsumingEnumerable())
        {
            if (_eventsClosed)
            {
                return;
            }

            try
            {
                switch (item.Kind)
                {
                    case PreviewEventKind.PositionChanged:
                        PositionChanged?.Invoke(this, EventArgs.Empty);
                        break;
                    case PreviewEventKind.IsPlayingChanged:
                        IsPlayingChanged?.Invoke(this, EventArgs.Empty);
                        break;
                    case PreviewEventKind.Failed:
                        Failed?.Invoke(this, item.Failure!);
                        break;
                }
            }
            catch (Exception ex)
            {
                // A subscriber's failure must not take the preview's event thread down with it.
                Debug.WriteLine($"Studio preview event handler failed: {ex}");
            }
            finally
            {
                Interlocked.Decrement(ref _eventsOutstanding);
            }
        }
    }

    private void Close(bool joinEventThread)
    {
        _closing = true;

        // The render thread first: after this nothing is drawn and nothing is asked of the players.
        _stopRequested = true;
        WakeEngine();
        lock (_passLock)
        {
            // A Pause() that waits for a pass will not get another.
            Monitor.PulseAll(_passLock);
        }

        if (_engineThread.IsAlive && Thread.CurrentThread != _engineThread)
        {
            _engineThread.Join(ThreadJoinTimeoutMilliseconds);
        }

        _policy.Stop();
        try
        {
            lock (_transportLock)
            {
                _wantPlaying = false;
                if (_controllerRunning)
                {
                    StopClock();
                }
            }
        }
        catch (Exception)
        {
            // Tearing down; the clock may already have failed.
        }

        foreach (var clip in _clips)
        {
            ClosePlayer(clip);
        }

        UnhookController();
        WaitForDeliveries(ThreadJoinTimeoutMilliseconds);
        ReleaseDevice();

        _eventsClosed = true;
        _eventQueue.CompleteAdding();
        if (joinEventThread && _eventThread.IsAlive && Thread.CurrentThread != _eventThread)
        {
            _eventThread.Join(ThreadJoinTimeoutMilliseconds);
        }

        _wakeDisposed = true;
        _wake.Dispose();
        _openedGate.Dispose();
        _firstFramesGate.Dispose();
        _readyGate.Dispose();
        if (!_eventThread.IsAlive)
        {
            _eventQueue.Dispose();
        }

        WaitForFilesClosed();
    }

    private void UnhookController()
    {
        try
        {
            _controller.Ended -= _endedHandler;
            _controller.Failed -= _controllerFailedHandler;
        }
        catch (Exception)
        {
            // Tearing down.
        }
    }

    private static void ClosePlayer(StudioPreviewClip clip)
    {
        var player = clip.Player;
        try
        {
            player.VideoFrameAvailable -= clip.FrameHandler;
            player.MediaOpened -= clip.OpenedHandler;
            player.MediaEnded -= clip.EndedHandler;
            player.MediaFailed -= clip.FailedHandler;
            clip.Session.SeekCompleted -= clip.SeekCompletedHandler;
        }
        catch (Exception)
        {
            // Tearing down; a player that failed may refuse.
        }

        try
        {
            player.TimelineController = null;
            player.Source = null;
        }
        catch (Exception)
        {
        }

        try
        {
            player.Dispose();
        }
        catch (Exception)
        {
        }

        try
        {
            clip.Source.Dispose();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// The caller may delete the project folder as soon as the preview is disposed, so wait until
    /// the players have really let go of the files in it.
    /// </summary>
    /// <remarks>
    /// The only way to find out is to open a file with no sharing, for an instant, again and
    /// again. That is done to the engine's own recordings only. A screen recording outside the
    /// project folder is the user's own file: nobody is about to delete it, another app may have
    /// it open for as long as it likes, and it must not be taken away from that app even for an
    /// instant. Its player is closed like the others and lets go of it in its own time; nobody
    /// waits for that.
    /// </remarks>
    private void WaitForFilesClosed()
    {
        var watched = new List<string>(_clips.Length);
        foreach (var clip in _clips)
        {
            if (StudioPreviewFiles.IsInFolder(_projectDirectory, clip.Path))
            {
                watched.Add(clip.Path);
            }
        }

        _filesWaitedFor = watched.Count;
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            var open = false;
            foreach (var path in watched)
            {
                open |= IsStillOpen(path);
            }

            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (!open || elapsed >= FilesClosedTimeoutMilliseconds)
            {
                _filesClosedAfterMilliseconds = elapsed;
                _filesClosed = !open;
                return;
            }

            Thread.Sleep(2);
        }
    }

    private static bool IsStillOpen(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Not ours to open exclusively; nothing more can be learned by waiting.
            return false;
        }
    }
}
