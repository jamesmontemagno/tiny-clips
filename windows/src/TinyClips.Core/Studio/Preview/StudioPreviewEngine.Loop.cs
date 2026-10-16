using System.Collections.Concurrent;
using System.Diagnostics;
using TinyClips.Core.Studio.Rendering;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Playback;

namespace TinyClips.Core.Studio.Preview;

// The render thread's loop: what the players report, what the caller wants, and the clock.
public sealed partial class StudioPreviewEngine
{
    private const int LongestIdleWaitMilliseconds = 250;
    private const int ConsecutiveCopyFailureLimit = 5;

    private enum SignalKind : byte
    {
        Opened,
        Announced,
        Frame,
        Doubtful,
        LeftOut,
        SeekCompleted,
        Ended,
        PlayerEnded,
        MediaFailed,
        CopyFailed,
    }

    /// <summary>Something a player or the clock reported, queued for the render thread.</summary>
    /// <param name="Frame">
    /// For a frame of playback: the clip's own frame number, as the clip's namer gave it. -1 for
    /// a frame handed over while the clock stood: that one is the frame its position names. For
    /// a frame of which it cannot be told which one it is: the latest it can be.
    /// </param>
    /// <param name="Serial">For a frame: which copy it is, so that it is only taken while it is still where it was put.</param>
    /// <param name="Kept">For a frame: it was kept aside, and is to be taken from there.</param>
    /// <param name="Unsure">For a frame of playback: its number is the one its position gave, which is not certain.</param>
    /// <param name="Inferred">
    /// For a frame of playback: its number rests on an inference about the player
    /// (<see cref="StudioPreviewFrameNamer.Inferred"/>). It is drawn and reported as any other;
    /// when the clock stops on it, the frame is fetched anew.
    /// </param>
    private readonly record struct Signal(
        SignalKind Kind,
        int Clip,
        long Timestamp,
        long PositionTicks = 0,
        string? Message = null,
        Exception? Error = null,
        long Frame = -1,
        long Serial = 0,
        bool Kept = false,
        bool Unsure = false,
        bool Inferred = false);

    private readonly ConcurrentQueue<Signal> _signals = new();
    private readonly List<StudioPreviewLanding> _landings = [];
    private readonly List<PreviewEvent> _pendingEvents = [];
    private readonly int[] _consecutiveCopyFailures;
    private readonly bool[] _tookFrame;
    private readonly long[] _framesWithoutNumber;
    private readonly long[] _framesShownWithoutNumber;
    private readonly long[] _framesNumberedLate;
    private readonly long[] _framesShownUnsure;
    private readonly long[] _framesInferred;
    private readonly long[] _framesPassedOver;

    // Render thread only, apart from the volatile ones, which other threads read.
    private StudioProject _renderProject;
    private bool _appliedProjectMuted;
    private double _appliedProjectVolume = 1;
    private int _landingCount;

    // The timeline frame the scene was last looked at for. Render thread only.
    private long _sceneFrame = -1;
    private int _landingsAnnounced;
    private long _framesAfterPause;
    private volatile int _appliedProjectSerial;
    private volatile bool _policyWantsPlaying;
    private volatile bool _engineIdle;

    // The passes of the render thread's loop: how many have begun, and the latest that drew what
    // it had (_passLock). Pause() waits for one.
    private readonly object _passLock = new();
    private long _passesBegun;
    private long _passesSettled;

    // Held by the render thread while it takes in what the players handed over, reports the
    // position and draws: one round of that is either over or has not begun when another thread
    // holds it. Pause() takes it to shut the picture to the frames of the playback it ended.
    private readonly object _roundLock = new();
    private volatile bool _playbackShut;

    // When the clock was last started, as a Stopwatch timestamp. Written with the device lock held.
    private long _clockRunningSince = StudioPreviewHandOverKinds.Never;

    /// <summary>
    /// A player has a new frame. Nothing happens here except the copy into the clip's texture and
    /// the question which frame it is: slow work in this callback makes the player drop frames.
    /// </summary>
    private void OnVideoFrameAvailable(StudioPreviewClip clip, MediaPlayer player)
    {
        var startedAt = Stopwatch.GetTimestamp();
        clip.DeliveryStarted(startedAt);
        try
        {
            if (_closing)
            {
                return;
            }

            // On the software adapter no frame is taken before every player has one: see
            // ReleaseFirstFrames. A player that still has a frame to hand over says so again
            // every hundredth of a second, and that is when it is taken.
            if (_firstFramesHeld || (_playersKeepFirstFrames && !clip.HasDeliveredFrame))
            {
                if (clip.FrameAnnounced())
                {
                    Post(new Signal(SignalKind.Announced, clip.Index, startedAt));
                }

                return;
            }

            // All a player says about the frame it hands over is where it is right now. Read
            // first thing: the frame was announced a moment ago, and the position moves on.
            var positionTicks = clip.Session.Position.Ticks;
            var collectorPause = GC.GetTotalPauseDuration().TotalMilliseconds;
            Signal earlier = default;
            Signal frame = default;
            long exited = 0;
            var graphics = Volatile.Read(ref _graphics);
            lock (graphics.Gate)
            {
                if (!ReferenceEquals(graphics, Volatile.Read(ref _graphics)) || _closing || clip.CopySurface is not { } surface)
                {
                    return;
                }

                bool playback;
                try
                {
                    playback = CopyFrame(graphics, clip, player, surface, startedAt, positionTicks, collectorPause, out earlier, out frame);
                }
                catch
                {
                    // What the copy target holds now, nobody knows.
                    clip.Namer.Forget();
                    clip.CopyWaits = false;
                    clip.CopyIsPicture = false;
                    throw;
                }

                // The frame before first: it is the earlier one, and is drawn first.
                if (earlier.Kind == SignalKind.Frame)
                {
                    PostFrame(earlier);
                }

                PostFrame(frame);
                if (playback)
                {
                    // Last thing, with nothing left to do but give the lock and the thread back:
                    // whatever held this hand-over up until now, the namer has to know of it
                    // before it can say that the player was not behind when the next frame came due.
                    clip.Namer.Exit(
                        Stopwatch.GetTimestamp(),
                        clip.Timing.NameAtPlayerTicks(clip.Session.Position.Ticks, out _),
                        GC.GetTotalPauseDuration().TotalMilliseconds);
                    exited = clip.CopySerial;
                }
            }

            if (exited != 0 && AfterExit is { } afterExit)
            {
                afterExit(clip.Index, exited);
            }
        }
        catch (Exception ex)
        {
            clip.CopyFailed();
            Post(new Signal(SignalKind.CopyFailed, clip.Index, startedAt, 0, null, ex));
        }
        finally
        {
            clip.DeliveryFinished();
        }
    }

    /// <summary>
    /// Copies the frame a player is handing over into the clip's copy target, and says what the
    /// render thread is to be told: about this frame, and about the one before it when this
    /// hand-over gave it its number. Device lock held.
    /// </summary>
    /// <remarks>
    /// A frame of playback is one whose hand-over began while the clock ran. Which frame it is,
    /// the clip's namer says; when that cannot be told, the render thread is told so, and shows
    /// it only where that does not matter. A frame handed over while the clock stood answers a
    /// position change and is the frame its position names; the seek policy sees to those. A
    /// frame handed over after Pause() stopped the clock is copied like any other, so that the
    /// copy target always holds what the player has, and left out of the picture.
    /// </remarks>
    /// <returns>True for a frame of playback, which the clip's namer was asked about.</returns>
    private bool CopyFrame(
        StudioGraphicsDevice graphics,
        StudioPreviewClip clip,
        MediaPlayer player,
        IDirect3DSurface surface,
        long startedAt,
        long positionTicks,
        double collectorPause,
        out Signal earlier,
        out Signal frame)
    {
        earlier = default;

        // In this order: see StopClock, which writes them the other way round.
        var stoppedAt = Interlocked.Read(ref _pausedAt);
        var kind = StudioPreviewHandOverKinds.Of(startedAt, Interlocked.Read(ref _clockRunningSince), stoppedAt, _discardLateFrames);
        var playback = kind == StudioPreviewHandOverKind.Playback;
        var late = kind == StudioPreviewHandOverKind.Late;
        var earlierIs = StudioPreviewEarlierFrame.Nothing;
        long earlierFrame = -1;
        var name = clip.Timing.NameAtPlayerTicks(positionTicks, out var intoMilliseconds);
        if (playback)
        {
            earlierIs = clip.Namer.Begin(startedAt, name, intoMilliseconds, collectorPause, out earlierFrame);
            if (!clip.CopyHoldsFrame)
            {
                // The copy target was replaced since, and the frame went with it.
                earlierIs = StudioPreviewEarlierFrame.Nothing;
            }
        }

        // The frame in the copy target is still wanted: it waits to be taken into the picture, or
        // has just got its number. It is put aside before the player writes over it.
        if (clip.CopyWaits || earlierIs != StudioPreviewEarlierFrame.Nothing)
        {
            KeepAside(graphics, clip);
            if (earlierIs == StudioPreviewEarlierFrame.Known)
            {
                clip.KeptWaits = true;
                earlier = new Signal(SignalKind.Frame, clip.Index, startedAt, 0, "numbered by the frame after it", null, clip.Timing.ClampFrame(earlierFrame), clip.KeptSerial, Kept: true, Inferred: clip.Namer.Inferred);
            }
        }

        var copyBegan = Stopwatch.GetTimestamp();
        player.CopyFrameToVideoSurface(surface);
        var copied = Stopwatch.GetTimestamp();
        var empty = _looksAtFirstFrames && !clip.HasDeliveredFrame && NoteFirstFrame(graphics, clip);
        clip.FrameCopied(delivered: true);
        var positionAfter = 0L;
        var collectorAfter = collectorPause;
        var named = new StudioPreviewNamedFrame(StudioPreviewFrameKnowledge.Known, name, false, string.Empty);
        if (playback)
        {
            positionAfter = clip.Session.Position.Ticks;
            collectorAfter = GC.GetTotalPauseDuration().TotalMilliseconds;
            var nameAfter = clip.Timing.NameAtPlayerTicks(positionAfter, out _);
            named = clip.Namer.End(copied, nameAfter, Stopwatch.GetElapsedTime(copyBegan, copied).TotalMilliseconds, collectorAfter);
            if (earlierIs == StudioPreviewEarlierFrame.KnownIfPrompt)
            {
                clip.KeptWaits = named.EarlierConfirmed;
                if (named.EarlierConfirmed)
                {
                    earlier = new Signal(SignalKind.Frame, clip.Index, startedAt, 0, "numbered by the frame after it", null, clip.Timing.ClampFrame(earlierFrame), clip.KeptSerial, Kept: true, Inferred: named.Inferred);
                }
            }

            // With or without a number it waits for the render thread, which knows what to do with it.
            clip.CopyWaits = true;
            frame = named.Knowledge == StudioPreviewFrameKnowledge.Unknown
                ? new Signal(SignalKind.Doubtful, clip.Index, startedAt, positionTicks, named.Rule, null, clip.Timing.ClampFrame(named.Frame), clip.CopySerial)
                : new Signal(SignalKind.Frame, clip.Index, startedAt, positionTicks, named.Rule, null, clip.Timing.ClampFrame(named.Frame), clip.CopySerial, Unsure: named.Knowledge == StudioPreviewFrameKnowledge.Unsure, Inferred: named.Inferred);
        }
        else if (late)
        {
            Interlocked.Increment(ref _lateFramesDiscarded);
            frame = new Signal(SignalKind.LeftOut, clip.Index, startedAt, positionTicks, "handed over after the clock had stopped");
        }
        else
        {
            clip.CopyWaits = true;
            frame = new Signal(SignalKind.Frame, clip.Index, startedAt, positionTicks, empty ? "nothing in it" : null, null, -1, clip.CopySerial);
        }

        if (AfterCopy is { } afterCopy && clip.CopyTexture is { } texture)
        {
            afterCopy(new StudioPreviewHandOver(
                clip.Index,
                clip.CopySerial,
                texture,
                clip.CopySize.Width,
                clip.CopySize.Height,
                kind,
                startedAt,
                positionTicks,
                intoMilliseconds,
                copyBegan,
                copied,
                positionAfter,
                collectorPause,
                collectorAfter,
                name,
                named.Knowledge,
                clip.Timing.ClampFrame(named.Frame),
                playback ? named.Rule : null,
                earlier.Kind == SignalKind.Frame ? earlier.Serial : 0,
                earlier.Kind == SignalKind.Frame ? earlier.Frame : -1,
                clip.Index == 0 ? _timeline.TimelineFrameOfScreen(clip.Timing.ClampFrame(named.Frame)) : -1,
                clip.Index == 0 && earlier.Kind == SignalKind.Frame ? _timeline.TimelineFrameOfScreen(earlier.Frame) : -1));
        }

        return playback;
    }

    /// <summary>
    /// Puts the frame in the copy target aside, with what is known of it, before the player
    /// writes the next one over it. A frame that was kept aside already and has not been taken
    /// is lost to this one; the render thread finds that out when it comes for it. Device lock held.
    /// </summary>
    private static void KeepAside(StudioGraphicsDevice graphics, StudioPreviewClip clip)
    {
        if (clip.CopyTexture is not { } copy)
        {
            return;
        }

        if (clip.KeptTexture is null || clip.KeptSize != clip.CopySize)
        {
            clip.KeptTexture?.Dispose();
            clip.KeptTexture = null;
            clip.KeptTexture = graphics.CreateRenderTexture(clip.CopySize.Width, clip.CopySize.Height);
            clip.KeptSize = clip.CopySize;
        }

        graphics.Context.CopyResource(clip.KeptTexture, copy);
        clip.KeptSerial = clip.CopySerial;
        clip.KeptWaits = clip.CopyWaits;
        clip.CopyWaits = false;
    }

    private void Post(Signal signal)
    {
        if (_closing)
        {
            return;
        }

        PostFrame(signal);
    }

    private void PostFrame(Signal signal)
    {
        _signals.Enqueue(signal);
        WakeEngine();
    }

    private void EngineLoop()
    {
        var deadline = long.MaxValue;
        while (true)
        {
            try
            {
                _wake.WaitOne(WaitMilliseconds(deadline));
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (_stopRequested)
            {
                return;
            }

            try
            {
                deadline = Iterate(Interlocked.Increment(ref _passesBegun));
            }
            catch (Exception ex)
            {
                deadline = OnLoopFailure(ex);
            }
        }
    }

    private static int WaitMilliseconds(long deadline)
    {
        if (deadline == long.MaxValue)
        {
            return LongestIdleWaitMilliseconds;
        }

        var remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining <= 0)
        {
            return 0;
        }

        var milliseconds = Math.Ceiling(remaining * 1000.0 / Stopwatch.Frequency);
        return (int)Math.Min(LongestIdleWaitMilliseconds, milliseconds);
    }

    private long Iterate(long pass)
    {
        _engineIdle = false;
        long deadline;
        long firstFramesBy;
        bool more;
        do
        {
            // One round: what the players handed over is taken in, the position is reported, and
            // the picture is drawn. A clip gives a round one frame at most, so that a frame which
            // is followed at once by the next one is drawn too. Pause() comes in between rounds.
            lock (_roundLock)
            {
                if (_playbackShut)
                {
                    // Pause() could not wait for this thread, and has left it this to do.
                    ShowNumberedPictures();
                }

                more = DrainSignals();
                if (Volatile.Read(ref _failed) != 0)
                {
                    FlushEvents();
                    UpdateOpenGates();
                    PassSettled(pass);
                    return long.MaxValue;
                }

                firstFramesBy = _looksAtFirstFrames && _opening ? ReleaseFirstFrames() : long.MaxValue;
                ApplyCommands();
                deadline = _policy.Pump();
                _policy.TakeLandings(_landings);
                foreach (var landing in _landings)
                {
                    if (_trace is not null)
                    {
                        Note($"landed on {landing.Frame} ({landing.Kind}, confirmed {landing.Confirmed}, repairs {landing.Repairs})");
                    }

                    Interlocked.Increment(ref _landingCount);
                }

                // What the players showed becomes the position, unless a frame that was asked for has not
                // been reached yet: then that frame is the position, and stays it. There is news when the
                // position changes and when a requested frame has become the picture. While opening, the
                // players are taken to a frame of the engine's own choosing and back, and nobody is told.
                if (_position.Update(_landings, _policy.IsSeeking) && !_opening)
                {
                    _pendingEvents.Add(new PreviewEvent(PreviewEventKind.PositionChanged));
                }

                _landings.Clear();

                // The scene is the one of the timeline frame the screen is shown under. Where
                // the screen counts in frames of its file, that frame can change without a new
                // picture: the players were brought to a frame of the timeline that shows the
                // frame the one before it shows. The scene is then drawn again, for its layout
                // may be another. On the grid a new frame always comes with a new picture.
                var sceneFrame = _policy.ShownTimelineFrame;
                if (sceneFrame != _sceneFrame)
                {
                    _sceneFrame = sceneFrame;
                    if (_timeline.ScreenHasTimes)
                    {
                        _redraw = true;
                    }
                }

                // Draw before telling anyone, so that a handler finds the picture its position names.
                Render();
                FlushEvents();
            }
        }
        while (more);

        PassSettled(pass);
        UpdateOpenGates();
        _engineIdle = _policy.IsIdle && !HasSomethingToDraw();
        return Math.Min(deadline, firstFramesBy);
    }

    private void PassSettled(long pass)
    {
        lock (_passLock)
        {
            _passesSettled = pass;
            Monitor.PulseAll(_passLock);
        }
    }

    /// <summary>
    /// Takes in what the players and the clock have reported. Stops before the second frame of a
    /// clip and returns true: the first is drawn before the second takes its place.
    /// </summary>
    private bool DrainSignals()
    {
        Array.Clear(_tookFrame);
        while (_signals.TryPeek(out var signal))
        {
            if (signal.Kind is SignalKind.Frame or SignalKind.Doubtful && _tookFrame[signal.Clip])
            {
                return true;
            }

            // This thread is the only one that takes signals out.
            _signals.TryDequeue(out signal);
            if (_trace is not null)
            {
                var frame = signal.Kind switch
                {
                    SignalKind.Frame when signal.Frame >= 0 => $" frame {signal.Frame}{(signal.Unsure ? " (unsure)" : signal.Inferred ? " (inferred)" : string.Empty)}{(signal.Kept ? ", kept aside" : string.Empty)}, position {signal.PositionTicks / 10000.0:0.0} ms",
                    SignalKind.Doubtful => $" frame {signal.Frame} at the latest, position {signal.PositionTicks / 10000.0:0.0} ms",
                    SignalKind.Frame or SignalKind.LeftOut => $" frame {_clips[signal.Clip].Timing.FrameAtPlayerTicks(signal.PositionTicks)} by its position, {signal.PositionTicks / 10000.0:0.0} ms",
                    _ => string.Empty,
                };
                Note($"{signal.Kind} clip {signal.Clip}{frame}, raised {Stopwatch.GetElapsedTime(signal.Timestamp).TotalMilliseconds:0.0} ms ago{(signal.Message is null ? string.Empty : ": " + signal.Message)}{(signal.Error is null ? string.Empty : $" (0x{signal.Error.HResult:X8})")}");
            }

            switch (signal.Kind)
            {
                case SignalKind.Opened:
                    _clips[signal.Clip].Opened = true;
                    break;

                case SignalKind.Announced:
                    // The pass that takes this signal looks whether every player has a frame now.
                    break;

                case SignalKind.Frame:
                    OnFrameSignal(signal);
                    break;

                case SignalKind.Doubtful:
                    OnDoubtfulSignal(signal);
                    break;

                case SignalKind.LeftOut:
                    break;

                case SignalKind.SeekCompleted:
                    _policy.OnSeekCompleted(signal.Clip, signal.Timestamp);
                    break;

                case SignalKind.Ended:
                    OnClockEnded();
                    break;

                case SignalKind.PlayerEnded:
                    _policy.OnPlayerEnded(signal.Clip);
                    break;

                case SignalKind.MediaFailed:
                    Fail(signal.Message ?? "The recording stopped decoding.", signal.Error, asDataError: true);
                    break;

                case SignalKind.CopyFailed:
                    OnCopyFailed(signal);
                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// A frame has its number: it is taken into its clip's picture, and the seek policy and the
    /// position are told, in that order and at one go, so that the picture and what is said of it
    /// never part.
    /// </summary>
    private void OnFrameSignal(in Signal signal)
    {
        var clip = _clips[signal.Clip];
        _consecutiveCopyFailures[signal.Clip] = 0;
        var ofPlayback = signal.Frame >= 0;
        if (ofPlayback && _playbackShut)
        {
            // Pause() has returned. Whoever called it has the position and the picture it was told about.
            StopWaiting(clip, signal.Serial);
            Interlocked.Increment(ref _lateFramesDiscarded);
            if (_trace is not null)
            {
                Note($"left out: Pause() had returned before frame {signal.Frame} of clip {signal.Clip} reached the picture");
            }

            return;
        }

        if (clip.ShowsLive && clip.LiveSerial == signal.Serial)
        {
            // It is on the scene already, shown without its number. Now it has one.
            NumberTheLivePicture(clip);
        }
        else if (!TakeIntoPicture(clip, signal.Serial))
        {
            Interlocked.Increment(ref _framesPassedOver[signal.Clip]);
            if (_trace is not null)
            {
                Note($"passed over: the frame of clip {signal.Clip} was written over before it was taken");
            }

            return;
        }

        _tookFrame[signal.Clip] = true;
        _framesNumberedLate[signal.Clip] += signal.Kept && ofPlayback ? 1 : 0;
        _framesShownUnsure[signal.Clip] += signal.Unsure ? 1 : 0;
        _framesInferred[signal.Clip] += signal.Inferred ? 1 : 0;
        var playback = _policy.AcceptsPlaybackFrames;
        if (playback && signal.Timestamp >= Interlocked.Read(ref _pausedAt))
        {
            // It set out after the clock had been stopped, and should have been left out.
            _framesAfterPause++;
        }

        // A number that is not certain, or that rests on an inference, is good for as long as the
        // clock runs. Should it stop on this frame, the policy fetches the frame anew.
        _policy.OnFrame(signal.Clip, ofPlayback ? signal.Frame : clip.Timing.FrameAtPlayerTicks(signal.PositionTicks), signal.Timestamp, signal.Unsure || signal.Inferred);
        if (signal.Clip == 0 && playback)
        {
            // The screen clip's frames are the timeline's, or each belongs to one of them.
            _position.Played(_policy.ShownTimelineFrame, signal.Timestamp);
        }
    }

    /// <summary>
    /// A frame of playback of which it cannot be told which frame it is. It is shown, in place of
    /// the clip's picture and without a word to anyone, when the scene comes out the same
    /// whichever frame it is: always for the camera, whose number decides nothing in the scene,
    /// and for the screen while the layout stands still. Otherwise it stays where it is, to be
    /// shown if the frame after it gives it its number.
    /// </summary>
    private void OnDoubtfulSignal(in Signal signal)
    {
        var clip = _clips[signal.Clip];
        _framesWithoutNumber[signal.Clip]++;

        // Only while the clock runs: once it has stopped, what is shown has to have a number.
        if (_playbackShut || !_policy.IsPlaying || (signal.Clip == 0 && !SceneIsTheSameUpTo(_timeline.TimelineFrameOfScreen(signal.Frame))))
        {
            if (_trace is not null)
            {
                Note($"not shown: which frame of clip {signal.Clip} it is cannot be told");
            }

            return;
        }

        if (TakeLivePicture(clip, signal.Serial))
        {
            _tookFrame[signal.Clip] = true;
            _framesShownWithoutNumber[signal.Clip]++;
        }
    }

    /// <summary>
    /// The clock is no longer running: every clip shows its picture with a number again. Render
    /// thread, or a thread that holds the round lock. True when a clip had to change.
    /// </summary>
    private bool ShowNumberedPictures()
    {
        var changed = false;
        foreach (var clip in _clips)
        {
            if (clip.ShowsLive)
            {
                clip.ShowsLive = false;
                clip.PictureCount++;
                changed = true;
            }
        }

        return changed;
    }

    private void ApplyCommands()
    {
        StudioProject project;
        int projectSerial;
        bool wantPlaying;
        bool clockRuns;
        lock (_commandLock)
        {
            project = _latestProject;
            projectSerial = _projectSerial;
        }

        lock (_transportLock)
        {
            wantPlaying = _wantPlaying;
            clockRuns = _controllerRunning;
        }

        if (projectSerial != _appliedProjectSerial)
        {
            _renderProject = project;
            ApplyAudio(project);
            _redraw = true;
            _appliedProjectSerial = projectSerial;
        }

        if (_position.TryTake(out var requested))
        {
            if (_spendFirst)
            {
                _spendFirst = false;
                _policy.RequestFirstSeek(requested);
            }
            else
            {
                _policy.RequestSeek(requested);
            }
        }

        if (!clockRuns && _policy.IsPlaying)
        {
            // Pause() has stopped the clock, and the policy has not heard of it. It hears now,
            // whatever is wanted by now: Pause() and then Play() between two looks of this
            // thread leave what is wanted as it was, and the policy would go on taking the
            // clock for running, with nothing moving until the next pause or seek. Only Pause()
            // stops the clock from another thread; this thread tells the policy itself.
            _policy.SetPlaying(false);
            _policyWantsPlaying = false;
        }

        if (wantPlaying != _policyWantsPlaying)
        {
            _policy.SetPlaying(wantPlaying);
            _policyWantsPlaying = wantPlaying;
        }
    }

    /// <summary>
    /// The screen clip plays its sound unless the project is muted, at the project's volume. The
    /// camera's player never does.
    /// </summary>
    private void ApplyAudio(StudioProject project)
    {
        var muted = project.Audio.Muted;
        var volume = StudioSound.Volume(project.Audio);
        var muteChanged = muted != _appliedProjectMuted;
        var volumeChanged = volume != _appliedProjectVolume;
        if (!muteChanged && !volumeChanged)
        {
            return;
        }

        _appliedProjectMuted = muted;
        _appliedProjectVolume = volume;
        foreach (var clip in _clips)
        {
            if (muteChanged)
            {
                clip.Player.IsMuted = StudioPreviewAudio.IsMuted(isCamera: clip.Index != 0, muted, _forceMuted);
            }

            if (volumeChanged)
            {
                clip.Player.Volume = StudioPreviewAudio.Volume(isCamera: clip.Index != 0, volume, _zeroVolume);
            }
        }
    }

    private void FlushEvents()
    {
        foreach (var item in _pendingEvents)
        {
            RaiseLater(item);
        }

        _pendingEvents.Clear();
    }

    /// <summary>The clock reached the end of the recording and stopped itself.</summary>
    private void OnClockEnded()
    {
        bool wasPlaying;
        lock (_transportLock)
        {
            if (!_controllerRunning)
            {
                return;
            }

            wasPlaying = _wantPlaying;
            _wantPlaying = false;
            _controllerRunning = false;

            // Nobody stopped it: what the players hand over from here on is not left out, and it
            // is not playback either. It answers the position the players are given next.
            Interlocked.Exchange(ref _pausedAt, StudioPreviewHandOverKinds.Never);
            Interlocked.Exchange(ref _clockRunningSince, StudioPreviewHandOverKinds.Never);
        }

        _policyWantsPlaying = false;
        if (_policy.IsPlaying)
        {
            // The clock stopped to go somewhere, as for a position asked for while it ran: the
            // players are about to be brought to the last frame. A clip that shows a frame
            // without a number keeps it until then, and has its frame fetched anew there. Going
            // back to its last frame with a number for the time that takes would show an
            // earlier frame after a later one.
            foreach (var clip in _clips)
            {
                if (clip.ShowsLive)
                {
                    _policy.OnPictureUncertain(clip.Index);
                }
            }
        }
        else
        {
            ShowNumberedPictures();
        }

        // The players are brought to rest on the last frame. That is the position from now on,
        // so that whoever hears that playback stopped finds it at the end, whether or not the
        // last frame got through before the clock stopped.
        if (_policy.OnEnded() && _position.Rest(_timeline.LastFrame))
        {
            _pendingEvents.Add(new PreviewEvent(PreviewEventKind.PositionChanged));
        }

        if (wasPlaying)
        {
            _pendingEvents.Add(new PreviewEvent(PreviewEventKind.IsPlayingChanged));
        }
    }

    private void OnCopyFailed(Signal signal)
    {
        if (IsDeviceLost(signal.Error) || IsDeviceRemoved())
        {
            RebuildDevice(signal.Error);
            return;
        }

        if (++_consecutiveCopyFailures[signal.Clip] >= ConsecutiveCopyFailureLimit)
        {
            Fail($"The {_clips[signal.Clip].Name} recording stopped decoding.", signal.Error, asDataError: true);
        }
    }

    private long OnLoopFailure(Exception ex)
    {
        if (_stopRequested)
        {
            return long.MaxValue;
        }

        try
        {
            if (IsDeviceLost(ex) || IsDeviceRemoved())
            {
                RebuildDevice(ex);

                // Go round again at once: the picture has to be drawn on the new device.
                return Stopwatch.GetTimestamp();
            }

            Fail("The preview ran into a problem and stopped.", ex, asDataError: false);
        }
        catch (Exception inner)
        {
            Fail("The preview ran into a problem and stopped.", inner, asDataError: false);
        }

        FlushEvents();
        UpdateOpenGates();
        return long.MaxValue;
    }

    /// <summary>
    /// The preview cannot go on. Raises <see cref="Failed"/> once; while opening, the failure is
    /// thrown to the caller of the factory instead. Render thread only.
    /// </summary>
    private void Fail(string message, Exception? exception, bool asDataError)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0)
        {
            return;
        }

        var wasPlaying = false;
        try
        {
            lock (_transportLock)
            {
                wasPlaying = _wantPlaying;
                _wantPlaying = false;
                if (_controllerRunning)
                {
                    StopClock();
                }
            }
        }
        catch (Exception)
        {
            // The clock may be what failed.
        }

        _policyWantsPlaying = false;
        _policy.Stop();
        if (_opening)
        {
            Volatile.Write(ref _openFailure, asDataError ? new InvalidDataException(message, exception) : new InvalidOperationException(message, exception));
            return;
        }

        if (wasPlaying)
        {
            _pendingEvents.Add(new PreviewEvent(PreviewEventKind.IsPlayingChanged));
        }

        _pendingEvents.Add(new PreviewEvent(PreviewEventKind.Failed, new StudioPreviewFailedEventArgs(message, exception)));
    }

    /// <summary>Lets <see cref="Open"/> carry on. Render thread only.</summary>
    private void UpdateOpenGates()
    {
        if (!_opening)
        {
            return;
        }

        if (Volatile.Read(ref _openFailure) is not null)
        {
            _openedGate.Set();
            _firstFramesGate.Set();
            _readyGate.Set();
            return;
        }

        var opened = true;
        var delivered = true;
        foreach (var clip in _clips)
        {
            opened &= clip.Opened;
            delivered &= clip.HasDeliveredFrame;
        }

        if (opened)
        {
            _openedGate.Set();
        }

        if (opened && delivered)
        {
            _firstFramesGate.Set();
        }

        var landings = Volatile.Read(ref _landingCount);
        if (landings != _landingsAnnounced)
        {
            _landingsAnnounced = landings;
            _readyGate.Set();
        }
    }

    void IStudioPreviewTransport.Seek(long timelineFrame)
    {
        if (_trace is not null)
        {
            Note($"clock to frame {timelineFrame}{(_policy.HoldPicture ? " (picture held)" : string.Empty)}");
        }

        _discardLateFrames = false;
        _controller.Position = TimeSpan.FromTicks(_timeline.MiddleTicks(timelineFrame));
    }

    void IStudioPreviewTransport.StepForward(int track)
    {
        if (_trace is not null)
        {
            Note($"step clip {track}");
        }

        _discardLateFrames = false;
        _clips[track].Player.StepForwardOneFrame();
    }

    bool IStudioPreviewTransport.Resume()
    {
        lock (_transportLock)
        {
            // Pause() may have come in since the render thread last looked.
            if (!_wantPlaying || _closing)
            {
                return false;
            }

            if (_trace is not null)
            {
                Note("clock started");
            }

            _discardLateFrames = false;
            _playbackShut = false;

            // Told before the clock moves: a frame of this playback sets out after it. The device
            // lock keeps a hand-over that is under way from being caught in between.
            var graphics = Volatile.Read(ref _graphics);
            lock (graphics.Gate)
            {
                var now = Stopwatch.GetTimestamp();
                var collectorPause = GC.GetTotalPauseDuration().TotalMilliseconds;
                foreach (var clip in _clips)
                {
                    clip.Namer.Start(RestingFrame(clip), now, collectorPause);
                }

                Interlocked.Exchange(ref _clockRunningSince, now);
                Interlocked.Exchange(ref _pausedAt, StudioPreviewHandOverKinds.Never);
                _position.ClockStarted(now);
            }

            _controller.Resume();
            _controllerRunning = true;
            return true;
        }
    }

    /// <summary>
    /// The frame of its own stream a clip's player rests on while the clock stands where the seek
    /// policy left it: -1 before the clip's first frame, -2 when it is not known. Render thread only.
    /// </summary>
    private long RestingFrame(StudioPreviewClip clip)
    {
        var shown = _policy.ShownFrame(clip.Index);
        var settled = _policy.SettledFrame;
        if (settled < 0 || clip.Index == 0)
        {
            return shown >= 0 ? shown : -2;
        }

        var own = _timeline.FrameMiddle(settled) - clip.Timing.StartOffset;
        if (own < 0)
        {
            // Parked before its stream: the next frame it hands over is its first, or a later one.
            return -1;
        }

        if (!_timeline.IsShown(clip.Index, settled))
        {
            // Parked after its stream. Nothing more comes from it.
            return clip.Timing.FrameCount - 1;
        }

        return shown >= 0 ? shown : -2;
    }

    void IStudioPreviewTransport.Pause()
    {
        bool toGoSomewhere;
        lock (_transportLock)
        {
            // Playing is still wanted when the policy stops the clock only to go to a position
            // that was asked for while it ran.
            toGoSomewhere = _wantPlaying;
            if (_controllerRunning)
            {
                StopClock();
            }
        }

        if (!toGoSomewhere || _playbackShut)
        {
            // Pause() was called: what is shown from here on has to have a number.
            ShowNumberedPictures();
            return;
        }

        // The pictures stay as they are until the position is reached, also one that shows a
        // frame without a number: going back to the last frame with a number for the time
        // the seek takes would show an earlier frame after a later one. The policy is told that
        // such a clip's frame has to come anew wherever the clock is put.
        foreach (var clip in _clips)
        {
            if (clip.ShowsLive)
            {
                _policy.OnPictureUncertain(clip.Index);
            }
        }
    }

    /// <summary>A line for the trace, with the time in milliseconds since the engine was created. Only called while there is one.</summary>
    private void Note(string text) => _trace?.Invoke($"{Stopwatch.GetElapsedTime(_createdAt).TotalMilliseconds,9:0.0} {text}");

    bool IStudioPreviewTransport.IsDelivering(int track) => _clips[track].DeliveriesInFlight > 0;

    private static bool IsDeviceLost(Exception? exception) => StudioPreviewOpenFailure.IsDeviceLost(exception);
}
