using System.Collections.Concurrent;
using System.Diagnostics;
using TinyClips.Core.Studio.Rendering;
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
        Frame,
        SeekCompleted,
        Ended,
        PlayerEnded,
        MediaFailed,
        CopyFailed,
    }

    /// <summary>Something a player or the clock reported, queued for the render thread.</summary>
    private readonly record struct Signal(SignalKind Kind, int Clip, long Timestamp, long PositionTicks = 0, string? Message = null, Exception? Error = null);

    private readonly ConcurrentQueue<Signal> _signals = new();
    private readonly List<StudioPreviewLanding> _landings = [];
    private readonly List<PreviewEvent> _pendingEvents = [];
    private readonly int[] _consecutiveCopyFailures;

    // Render thread only, apart from the volatile ones, which other threads read.
    private StudioProject _renderProject;
    private bool _appliedProjectMuted;
    private int _landingCount;
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

    /// <summary>
    /// A player has a new frame. Nothing happens here except the copy into the clip's texture:
    /// slow work in this callback makes the player drop frames.
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

            // Stopped by Pause(), and this frame only set out afterwards.
            if (_discardLateFrames && startedAt > Interlocked.Read(ref _pausedAt))
            {
                Interlocked.Increment(ref _lateFramesDiscarded);
                return;
            }

            // Which frame this is: the frame that contains the player's position right now.
            var positionTicks = clip.Session.Position.Ticks;
            var copied = false;
            var graphics = Volatile.Read(ref _graphics);
            lock (graphics.Gate)
            {
                if (ReferenceEquals(graphics, Volatile.Read(ref _graphics)) && !_closing && clip.CopySurface is { } surface)
                {
                    player.CopyFrameToVideoSurface(surface);
                    clip.FrameCopied(delivered: true);
                    copied = true;
                }
            }

            // Always posted when the frame was counted: the render thread waits for it before it draws.
            if (copied)
            {
                PostFrame(new Signal(SignalKind.Frame, clip.Index, startedAt, positionTicks));
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
        DrainSignals();
        if (Volatile.Read(ref _failed) != 0)
        {
            FlushEvents();
            UpdateOpenGates();
            PassSettled(pass);
            return long.MaxValue;
        }

        ApplyCommands();
        var deadline = _policy.Pump();
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

        // Draw before telling anyone, so that a handler finds the picture its position names. When
        // the draw has to wait for a frame's signal, so does the news.
        if (Render())
        {
            FlushEvents();
            PassSettled(pass);
        }

        UpdateOpenGates();
        _engineIdle = _policy.IsIdle && !HasSomethingToDraw();
        return deadline;
    }

    private void PassSettled(long pass)
    {
        lock (_passLock)
        {
            _passesSettled = pass;
            Monitor.PulseAll(_passLock);
        }
    }

    private void DrainSignals()
    {
        while (_signals.TryDequeue(out var signal))
        {
            if (_trace is not null)
            {
                var frame = signal.Kind == SignalKind.Frame ? $" frame {_clips[signal.Clip].Timing.FrameAtPlayerTicks(signal.PositionTicks)} at {signal.PositionTicks / 10000.0:0.0} ms" : string.Empty;
                Note($"{signal.Kind} clip {signal.Clip}{frame}, raised {Stopwatch.GetElapsedTime(signal.Timestamp).TotalMilliseconds:0.0} ms ago{(signal.Message is null ? string.Empty : ": " + signal.Message)}{(signal.Error is null ? string.Empty : $" (0x{signal.Error.HResult:X8})")}");
            }

            switch (signal.Kind)
            {
                case SignalKind.Opened:
                    _clips[signal.Clip].Opened = true;
                    break;

                case SignalKind.Frame:
                {
                    _consecutiveCopyFailures[signal.Clip] = 0;
                    _clips[signal.Clip].AcknowledgedCount++;
                    var playback = _policy.AcceptsPlaybackFrames;
                    if (playback && signal.Timestamp > Interlocked.Read(ref _pausedAt))
                    {
                        // Its callback started after Pause() had stopped the clock.
                        _framesAfterPause++;
                    }

                    _policy.OnFrame(signal.Clip, _clips[signal.Clip].Timing.FrameAtPlayerTicks(signal.PositionTicks), signal.Timestamp);
                    if (signal.Clip == 0 && playback)
                    {
                        // The screen clip's frames are the timeline's.
                        _position.Played(_policy.ShownFrame(0), signal.Timestamp);
                    }

                    break;
                }

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
    }

    private void ApplyCommands()
    {
        StudioProject project;
        int projectSerial;
        bool wantPlaying;
        lock (_commandLock)
        {
            project = _latestProject;
            projectSerial = _projectSerial;
        }

        lock (_transportLock)
        {
            wantPlaying = _wantPlaying;
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
            if (_offsetsChanged)
            {
                _offsetsChanged = false;
                _policy.RequestSeekAfterOffsetChange(requested);
            }
            else
            {
                _policy.RequestSeek(requested);
            }
        }

        if (wantPlaying != _policyWantsPlaying)
        {
            _policy.SetPlaying(wantPlaying);
            _policyWantsPlaying = wantPlaying;
        }
    }

    /// <summary>The screen clip plays its sound unless the project is muted. The camera's player never does.</summary>
    private void ApplyAudio(StudioProject project)
    {
        var muted = project.Audio.Muted;
        if (muted == _appliedProjectMuted)
        {
            return;
        }

        _appliedProjectMuted = muted;
        foreach (var clip in _clips)
        {
            clip.Player.IsMuted = StudioPreviewAudio.IsMuted(isCamera: clip.Index != 0, muted, _forceMuted);
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
            Interlocked.Exchange(ref _pausedAt, long.MaxValue);
        }

        _policyWantsPlaying = false;

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

            // Told before the clock moves: a frame of this playback sets out after it.
            _position.ClockStarted(Stopwatch.GetTimestamp());
            _controller.Resume();
            _controllerRunning = true;
            Interlocked.Exchange(ref _pausedAt, long.MaxValue);
            return true;
        }
    }

    void IStudioPreviewTransport.Pause()
    {
        lock (_transportLock)
        {
            if (_controllerRunning)
            {
                StopClock();
            }
        }
    }

    /// <summary>A line for the trace, with the time in milliseconds since the engine was created. Only called while there is one.</summary>
    private void Note(string text) => _trace?.Invoke($"{Stopwatch.GetElapsedTime(_createdAt).TotalMilliseconds,9:0.0} {text}");

    bool IStudioPreviewTransport.IsDelivering(int track) => _clips[track].DeliveriesInFlight > 0;

    private static bool IsDeviceLost(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is StudioDeviceLostException)
            {
                return true;
            }

            switch (unchecked((uint)current.HResult))
            {
                case 0x887A0005: // DXGI_ERROR_DEVICE_REMOVED
                case 0x887A0006: // DXGI_ERROR_DEVICE_HUNG
                case 0x887A0007: // DXGI_ERROR_DEVICE_RESET
                case 0x887A0020: // DXGI_ERROR_DRIVER_INTERNAL_ERROR
                case 0x8899000C: // D2DERR_RECREATE_TARGET
                    return true;
            }
        }

        return false;
    }
}
