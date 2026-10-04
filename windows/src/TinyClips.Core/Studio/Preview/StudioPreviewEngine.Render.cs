using System.Diagnostics;
using TinyClips.Core.Capture;
using TinyClips.Core.Studio.Rendering;
using Vortice.Direct3D11;
using Windows.Graphics.DirectX.Direct3D11;

namespace TinyClips.Core.Studio.Preview;

// The surface, the textures the players copy into, drawing, and the device.
public sealed partial class StudioPreviewEngine
{
    // The size assumed for the first copy targets, until a surface says how large it is.
    private const double DefaultSurfaceWidth = 1280;
    private const double DefaultSurfaceHeight = 720;
    private const int MaxRememberedTargets = 4;
    private static readonly TimeSpan RebuildProbation = TimeSpan.FromSeconds(5);

    // Order of the engine's locks: _surfaceLock, then the device's lock. A player's frame callback
    // takes only the device's lock.
    private readonly object _surfaceLock = new();
    private readonly List<ID3D11Texture2D> _surfaceTargets = [];

    // The device and the renderer on it. Replaced only by the render thread, holding _surfaceLock
    // and the old device's lock.
    private StudioGraphicsDevice _graphics;
    private StudioSceneRenderer _renderer;
    private bool _rendererDisposed;

    private volatile IStudioPreviewSurface? _surface;
    private bool _surfaceConfigured;
    private volatile bool _redraw;
    private volatile bool _surfaceInvalidated;
    private int _simulatedDeviceLosses;

    // Render thread only.
    private long _lastRebuildAt;
    private bool _drawnSinceRebuild = true;
    private long _framesDrawn;
    private long _copyTargetChanges;
    private int _deviceRebuilds;

    /// <summary>
    /// Makes <paramref name="surface"/> the surface the preview is drawn into, in place of any
    /// other, and draws the current frame into it. The surface's buffers are created on the render
    /// thread shortly after; this call does not wait for that.
    /// </summary>
    public void AttachSurface(IStudioPreviewSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        lock (_surfaceLock)
        {
            if (_closing || ReferenceEquals(_surface, surface))
            {
                return;
            }

            ReleaseSurface();
            _surface = surface;
            _surfaceConfigured = false;
            surface.Invalidated += OnSurfaceInvalidated;
            _redraw = true;
        }

        WakeEngine();
    }

    /// <summary>
    /// Stops drawing into <paramref name="surface"/> if it is the attached one. When this returns
    /// the surface has released its buffers and the engine no longer refers to it. It may wait a
    /// few milliseconds for a frame that is being drawn.
    /// </summary>
    public void DetachSurface(IStudioPreviewSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        lock (_surfaceLock)
        {
            if (ReferenceEquals(_surface, surface))
            {
                ReleaseSurface();
            }
        }
    }

    private void OnSurfaceInvalidated(object? sender, EventArgs e)
    {
        _surfaceInvalidated = true;
        WakeEngine();
    }

    // _surfaceLock held.
    private void ReleaseSurface()
    {
        var surface = _surface;
        if (surface is null)
        {
            return;
        }

        _surface = null;
        surface.Invalidated -= OnSurfaceInvalidated;
        var graphics = _graphics;
        lock (graphics.Gate)
        {
            ReleaseSurfaceTargets();
            FlushDevice(graphics);
            surface.ReleaseDeviceResources();
        }
    }

    private bool HasSomethingToDraw()
    {
        if (_surface is null)
        {
            return false;
        }

        if (_redraw || _surfaceInvalidated)
        {
            return true;
        }

        foreach (var clip in _clips)
        {
            if (clip.CopyCount != clip.DrawnCopyCount)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Draws the scene when a texture holds a newer frame than was last drawn, or a redraw was
    /// asked for. It never waits for the other clip, and never presents without something new.
    /// Returns false when the draw has to wait: a texture holds a frame whose signal has not
    /// reached this thread yet, so the frame cannot be named. The signal is on its way.
    /// </summary>
    private bool Render()
    {
        if (Volatile.Read(ref _simulatedDeviceLosses) > 0)
        {
            Interlocked.Decrement(ref _simulatedDeviceLosses);
            throw new StudioDeviceLostException("Simulated loss of the rendering device.");
        }

        // During a repair's detour the textures show frames nobody asked for.
        if (!HasSomethingToDraw() || _policy.HoldPicture)
        {
            return true;
        }

        lock (_surfaceLock)
        {
            var surface = _surface;
            if (surface is null)
            {
                return true;
            }

            var graphics = _graphics;
            var drawn = false;
            lock (graphics.Gate)
            {
                // The scene's time, and with it whether the camera shows, comes from the frame the
                // screen's texture holds. Frames reach the textures before their signals reach
                // this thread, so make sure nothing is in a texture that has not been counted here.
                foreach (var clip in _clips)
                {
                    if (clip.DeliveredCount != clip.AcknowledgedCount)
                    {
                        return false;
                    }
                }

                // Cleared first, so that a request made while this frame is drawn gets its own.
                _redraw = false;
                _surfaceInvalidated = false;
                if (!_surfaceConfigured || surface.NeedsConfigure)
                {
                    // A swap chain cannot resize its buffers while anything refers to them: the
                    // renderer's wrapper for the back buffer, or what Direct2D left queued.
                    ReleaseSurfaceTargets();
                    FlushDevice(graphics);
                    surface.Configure(graphics.Device);
                    _surfaceConfigured = true;
                }

                var target = surface.AcquireTarget(out var width, out var height);
                if (target is not null && width > 0 && height > 0)
                {
                    target = Remember(target);
                    UpdateCopyTargets(graphics, _renderProject, width, height);
                    foreach (var clip in _clips)
                    {
                        PromoteCopyTarget(clip);
                        clip.DrawnCopyCount = clip.CopyCount;
                    }

                    DrawScene(_renderProject, target, width, height);
                    AfterRender?.Invoke(graphics, target, width, height);
                    drawn = true;
                    if (_trace is not null)
                    {
                        Note($"drew the scene of frame {_policy.ShownFrame(0)}");
                    }
                }
                else
                {
                    target?.Dispose();
                    foreach (var clip in _clips)
                    {
                        clip.DrawnCopyCount = clip.CopyCount;
                    }
                }
            }

            // Never across the device's lock: presenting can wait for the compositor.
            if (drawn)
            {
                surface.Present();
                _framesDrawn++;
                _drawnSinceRebuild = true;
            }
        }

        return true;
    }

    /// <summary>
    /// Keeps one reference per distinct target the renderer has seen, so each can be forgotten
    /// before the surface resizes or releases it. A swap chain hands out the same texture every time.
    /// </summary>
    private ID3D11Texture2D Remember(ID3D11Texture2D target)
    {
        foreach (var known in _surfaceTargets)
        {
            if (known.NativePointer == target.NativePointer)
            {
                target.Dispose();
                return known;
            }
        }

        if (_surfaceTargets.Count >= MaxRememberedTargets)
        {
            ReleaseSurfaceTargets();
        }

        _surfaceTargets.Add(target);
        return target;
    }

    private static void FlushDevice(StudioGraphicsDevice graphics)
    {
        graphics.Context.ClearState();
        graphics.Context.Flush();
    }

    // Device lock held.
    private void UpdateCopyTargets(StudioGraphicsDevice graphics, StudioProject project, double surfaceWidth, double surfaceHeight)
    {
        var time = _timeline.FrameMiddle(Math.Max(0, _policy.ShownFrame(0)));
        var layout = StudioLayoutResolver.Resolve(project, time, surfaceWidth, surfaceHeight);
        foreach (var clip in _clips)
        {
            var needed = 0.0;
            if (clip.Index == 0 && layout.Screen is { } screen)
            {
                needed = StudioPreviewCopyTargets.NeededScale(clip.SourceWidth, clip.SourceHeight, screen.Rect, screen.Source);
            }
            else if (clip.Index == 1 && layout.Camera is { } camera)
            {
                needed = StudioPreviewCopyTargets.NeededScale(clip.SourceWidth, clip.SourceHeight, camera.Rect, camera.Source);
            }

            var chosen = StudioPreviewCopyTargets.Choose(clip.SourceWidth, clip.SourceHeight, needed, clip.CopyTexture is null ? null : clip.CopySize);
            if (clip.CopyTexture is null || chosen != clip.CopySize)
            {
                _copyTargetChanges += clip.CopyTexture is null ? 0 : 1;
                CreateCopyTarget(graphics, clip, chosen);
            }
        }
    }

    // Device lock held.
    private static void CreateCopyTarget(StudioGraphicsDevice graphics, StudioPreviewClip clip, StudioPreviewCopyTargetSize size)
    {
        var texture = graphics.CreateRenderTexture(size.Width, size.Height);
        IDirect3DSurface surface;
        try
        {
            surface = WgcInterop.CreateDirect3DSurface(texture);
        }
        catch
        {
            texture.Dispose();
            throw;
        }

        // A target that never became the draw source has nothing else referring to it.
        if (clip.CopyTexture is not null && !ReferenceEquals(clip.CopyTexture, clip.DrawTexture))
        {
            (clip.CopySurface as IDisposable)?.Dispose();
            clip.CopyTexture.Dispose();
        }

        clip.CopyTexture = texture;
        clip.CopySurface = surface;
        clip.CopySize = size;
        clip.CopyHoldsFrame = false;
        if (!clip.HasDeliveredFrame)
        {
            return;
        }

        // The player still has its current frame. Take it again at the new size, so that a paused
        // picture does not stay on the old texture until the next seek.
        try
        {
            clip.Player.CopyFrameToVideoSurface(surface);
            clip.FrameCopied(delivered: false);
        }
        catch (Exception ex) when (!IsDeviceLost(ex))
        {
            // The old texture stays the draw source until the player's next frame arrives here.
        }
    }

    // Device lock held.
    private void PromoteCopyTarget(StudioPreviewClip clip)
    {
        if (!clip.CopyHoldsFrame || ReferenceEquals(clip.CopyTexture, clip.DrawTexture))
        {
            return;
        }

        if (clip.DrawTexture is not null)
        {
            ForgetRendererSources();
            (clip.DrawSurface as IDisposable)?.Dispose();
            clip.DrawTexture.Dispose();
        }

        clip.DrawTexture = clip.CopyTexture;
        clip.DrawSurface = clip.CopySurface;
        clip.DrawSize = clip.CopySize;
    }

    // Device lock held.
    private void DisposeClipTextures(StudioPreviewClip clip)
    {
        if (clip.CopyTexture is not null && !ReferenceEquals(clip.CopyTexture, clip.DrawTexture))
        {
            (clip.CopySurface as IDisposable)?.Dispose();
            clip.CopyTexture.Dispose();
        }

        (clip.DrawSurface as IDisposable)?.Dispose();
        clip.DrawTexture?.Dispose();
        clip.CopyTexture = null;
        clip.CopySurface = null;
        clip.DrawTexture = null;
        clip.DrawSurface = null;
        clip.CopyHoldsFrame = false;
    }

    // ----------------------------------------------------------------------------------------
    // Every use of the scene renderer is in this block, and every one of them holds the device's lock.
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Draws the canvas at the surface's pixel size. The renderer hides the camera outside its own
    /// time range; a player outside its range only parks on its first or last frame.
    /// </summary>
    private void DrawScene(StudioProject project, ID3D11Texture2D target, int width, int height)
    {
        // The middle of the frame the screen shows: the instant the exporter samples for that frame.
        var sourceTime = _timeline.FrameMiddle(Math.Max(0, _policy.ShownFrame(0)));
        var request = new StudioRenderRequest(
            project,
            _events,
            _projectDirectory,
            sourceTime,
            width,
            height,
            Source(_clips[0]),
            _clips.Length > 1 ? Source(_clips[1]) : null,
            target);
        _renderer.Render(in request);
    }

    private static StudioGpuVideoFrame? Source(StudioPreviewClip clip) =>
        clip.DrawTexture is { } texture ? new StudioGpuVideoFrame(texture, 0, clip.DrawSize.Width, clip.DrawSize.Height) : null;

    // The renderer keeps a wrapper per source texture, by address. Drop them before a texture goes.
    private void ForgetRendererSources() => _renderer.ForgetSources();

    private void ReleaseSurfaceTargets()
    {
        foreach (var target in _surfaceTargets)
        {
            if (!_rendererDisposed)
            {
                _renderer.ForgetTarget(target);
            }

            target.Dispose();
        }

        _surfaceTargets.Clear();
    }

    private void DisposeRenderer()
    {
        if (_rendererDisposed)
        {
            return;
        }

        _rendererDisposed = true;
        _renderer.Dispose();
    }

    // The renderer draws through the device's immediate context, so it is created, called and
    // disposed under the device's lock.
    private static StudioSceneRenderer CreateRenderer(StudioGraphicsDevice graphics)
    {
        lock (graphics.Gate)
        {
            return new StudioSceneRenderer(graphics.Device);
        }
    }

    /// <summary>The graphics hardware, or WARP on a PC that has none. A check can ask for WARP.</summary>
    private static StudioGraphicsDevice CreateDevice(bool software) =>
        software ? StudioGraphicsDevice.CreateWarp() : StudioGraphicsDevice.CreateHardware();

    // ----------------------------------------------------------------------------------------

    private bool IsDeviceRemoved()
    {
        try
        {
            return Volatile.Read(ref _graphics).Device.DeviceRemovedReason.Failure;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// The device was lost. Builds a new device, renderer and textures, lets the surface build its
    /// buffers again, and carries on where the players are. One attempt: a device that is lost
    /// again before anything was drawn fails the preview. Render thread only.
    /// </summary>
    private void RebuildDevice(Exception? cause)
    {
        const string message = "The graphics device was lost and the preview could not be restarted.";
        if (_opening || (!_drawnSinceRebuild && Stopwatch.GetElapsedTime(_lastRebuildAt) < RebuildProbation))
        {
            Fail(message, cause, asDataError: false);
            return;
        }

        var sizes = new StudioPreviewCopyTargetSize[_clips.Length];
        for (var index = 0; index < _clips.Length; index++)
        {
            sizes[index] = _clips[index].CopySize;
        }

        StudioGraphicsDevice old;
        StudioGraphicsDevice? created = null;
        Exception? failure = null;
        lock (_surfaceLock)
        {
            old = _graphics;
            lock (old.Gate)
            {
                // Everything on the old device goes. A lost device may refuse any call.
                try
                {
                    ReleaseSurfaceTargets();
                }
                catch (Exception)
                {
                    _surfaceTargets.Clear();
                }

                try
                {
                    _surface?.ReleaseDeviceResources();
                }
                catch (Exception)
                {
                }

                _surfaceConfigured = false;
                foreach (var clip in _clips)
                {
                    try
                    {
                        DisposeClipTextures(clip);
                    }
                    catch (Exception)
                    {
                    }
                }

                try
                {
                    DisposeRenderer();
                }
                catch (Exception)
                {
                }

                try
                {
                    created = CreateDevice(_softwareDevice);
                    _renderer = CreateRenderer(created);
                    _rendererDisposed = false;
                    Volatile.Write(ref _graphics, created);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }
        }

        if (failure is not null || created is null)
        {
            created?.Dispose();
            Fail(message, failure ?? cause, asDataError: false);
            return;
        }

        try
        {
            old.Dispose();
        }
        catch (Exception)
        {
        }

        _deviceRebuilds++;
        _lastRebuildAt = Stopwatch.GetTimestamp();
        _drawnSinceRebuild = false;
        var missing = false;
        lock (created.Gate)
        {
            for (var index = 0; index < _clips.Length; index++)
            {
                CreateCopyTarget(created, _clips[index], sizes[index]);
                missing |= !_clips[index].CopyHoldsFrame;
            }
        }

        _redraw = true;
        if (missing && !_policy.IsPlaying)
        {
            // A player would not hand its frame over again. A seek to where it is makes it.
            foreach (var clip in _clips)
            {
                if (!clip.CopyHoldsFrame)
                {
                    _policy.Forget(clip.Index);
                }
            }

            _policy.RequestSeek(_position.Frame);
        }
    }

    /// <summary>Releases the surface's buffers, the textures, the renderer and the device. The render thread has ended.</summary>
    private void ReleaseDevice()
    {
        lock (_surfaceLock)
        {
            var graphics = _graphics;
            lock (graphics.Gate)
            {
                var surface = _surface;
                _surface = null;
                try
                {
                    ReleaseSurfaceTargets();
                    FlushDevice(graphics);
                }
                catch (Exception)
                {
                    _surfaceTargets.Clear();
                }

                if (surface is not null)
                {
                    surface.Invalidated -= OnSurfaceInvalidated;
                    try
                    {
                        surface.ReleaseDeviceResources();
                    }
                    catch (Exception)
                    {
                    }
                }

                foreach (var clip in _clips)
                {
                    try
                    {
                        DisposeClipTextures(clip);
                    }
                    catch (Exception)
                    {
                    }
                }

                try
                {
                    DisposeRenderer();
                }
                catch (Exception)
                {
                }
            }

            try
            {
                graphics.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }

    // ----------------------------------------------------------------------------------------
    // For the check tool and the unit tests.
    // ----------------------------------------------------------------------------------------

    /// <summary>Called with the device lock held after every scene drawn, before it is presented.</summary>
    internal Action<StudioGraphicsDevice, ID3D11Texture2D, int, int>? AfterRender { get; set; }

    /// <summary>The device the engine draws with. Take its <c>Gate</c> around any use of its context.</summary>
    internal StudioGraphicsDevice GraphicsDevice => Volatile.Read(ref _graphics);

    /// <summary>
    /// Nothing is asked for, in flight, or waiting to be drawn, and every event raised so far has
    /// been handled.
    /// </summary>
    internal bool IsIdle
    {
        get
        {
            // What is asked for is read first and the loop's own state last: the loop marks itself
            // busy before it takes a request up, and idle only when the work is done.
            if (_position.HasNewRequest)
            {
                return false;
            }

            lock (_commandLock)
            {
                if (_projectSerial != _appliedProjectSerial)
                {
                    return false;
                }
            }

            lock (_transportLock)
            {
                if (_wantPlaying != _policyWantsPlaying)
                {
                    return false;
                }
            }

            if (!_signals.IsEmpty || Volatile.Read(ref _simulatedDeviceLosses) > 0 || HasSomethingToDraw())
            {
                return false;
            }

            if (!_engineIdle)
            {
                return false;
            }

            // The loop hands its news over before it marks itself idle, so what is still to be
            // heard is counted by now. A handler that asks is not kept waiting for itself.
            return Volatile.Read(ref _eventsOutstanding) == 0 || Environment.CurrentManagedThreadId == _eventThread.ManagedThreadId;
        }
    }

    internal bool WaitForIdle(TimeSpan timeout)
    {
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start) < timeout)
        {
            if (IsIdle)
            {
                return true;
            }

            Thread.Sleep(1);
        }

        return IsIdle;
    }

    /// <summary>
    /// Makes the render thread behave as if the device had just been lost, <paramref name="times"/>
    /// times in a row. Twice is a device that is lost again before the rebuilt one drew anything.
    /// </summary>
    internal void SimulateDeviceLoss(int times = 1)
    {
        Interlocked.Add(ref _simulatedDeviceLosses, Math.Max(1, times));
        WakeEngine();
    }

    /// <summary>
    /// The pixels of the texture that holds a clip's latest frame, as tightly packed BGRA, or null
    /// before its first frame.
    /// </summary>
    internal byte[]? ReadClipTexture(int clipIndex, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (_closing || clipIndex < 0 || clipIndex >= _clips.Length)
        {
            return null;
        }

        var clip = _clips[clipIndex];
        var graphics = Volatile.Read(ref _graphics);
        lock (graphics.Gate)
        {
            if (!ReferenceEquals(graphics, Volatile.Read(ref _graphics)))
            {
                return null;
            }

            var holdsLatest = clip.CopyHoldsFrame ? clip.CopyTexture : clip.DrawTexture;
            if (holdsLatest is null)
            {
                return null;
            }

            var size = clip.CopyHoldsFrame ? clip.CopySize : clip.DrawSize;
            width = size.Width;
            height = size.Height;
            return graphics.ReadTexture(holdsLatest);
        }
    }

    internal StudioPreviewDiagnostics GetDiagnostics()
    {
        var count = _clips.Length;
        var copied = new long[count];
        var started = new long[count];
        var mostAtOnce = new int[count];
        var lastStarted = new long[count];
        var failures = new long[count];
        var targets = new (int Width, int Height)[count];
        var muted = new bool[count];
        var volume = new double[count];
        var shown = new long[count];
        var playerSeconds = new double[count];
        var playerStates = new string[count];
        var seeksCompleted = new long[count];
        var lastSeekCompleted = new long[count];
        var endsReached = new long[count];
        var clockState = string.Empty;
        var clockSeconds = 0.0;
        if (!_closing)
        {
            try
            {
                clockState = _controller.State.ToString();
                clockSeconds = _controller.Position.TotalSeconds;
            }
            catch (Exception)
            {
                // Closed in the meantime.
            }
        }

        for (var index = 0; index < count; index++)
        {
            var clip = _clips[index];
            copied[index] = clip.CopyCount;
            started[index] = clip.CallbacksStarted;
            mostAtOnce[index] = clip.MostDeliveriesAtOnce;
            lastStarted[index] = clip.LastCallbackStartedAt;
            failures[index] = clip.CopyFailures;
            seeksCompleted[index] = clip.SeeksCompleted;
            lastSeekCompleted[index] = clip.LastSeekCompletedAt;
            endsReached[index] = clip.EndsReached;
            targets[index] = (clip.CopySize.Width, clip.CopySize.Height);
            shown[index] = _policy.ShownFrame(index);
            playerStates[index] = string.Empty;
            if (!_closing)
            {
                try
                {
                    muted[index] = clip.Player.IsMuted;
                    volume[index] = clip.Player.Volume;
                    playerSeconds[index] = clip.Session.Position.TotalSeconds;
                    playerStates[index] = clip.Session.PlaybackState.ToString();
                }
                catch (Exception)
                {
                    // The player was closed in the meantime.
                }
            }
        }

        return new StudioPreviewDiagnostics
        {
            FramesCopied = copied,
            CallbacksStarted = started,
            MostDeliveriesAtOnce = mostAtOnce,
            LastCallbackStartedAt = lastStarted,
            CopyFailures = failures,
            SeeksCompleted = seeksCompleted,
            LastSeekCompletedAt = lastSeekCompleted,
            EndsReached = endsReached,
            CopyTargets = targets,
            CopyTargetChanges = _copyTargetChanges,
            PlayerMuted = muted,
            PlayerVolume = volume,
            ProjectMuted = _appliedProjectMuted,
            SeeksIssued = _policy.SeeksIssued,
            StepsIssued = _policy.StepsIssued,
            StepFallbacks = _policy.StepFallbacks,
            Repairs = _policy.Repairs,
            LossesSeenEarly = _policy.LossesSeenEarly,
            RepairFailures = _policy.RepairFailures,
            EndRecoveries = _policy.EndRecoveries,
            StrayFrames = _policy.StrayFrames,
            AnswersGivenUp = _policy.AnswersGivenUp,
            DetoursUnanswered = _policy.DetoursUnanswered,
            SecondAnswers = _policy.SecondAnswers,
            StepsAvoided = _policy.StepsAvoided,
            FramesDrawn = _framesDrawn,
            FramesAfterPause = _framesAfterPause,
            LateFramesDiscarded = Interlocked.Read(ref _lateFramesDiscarded),
            FramesFromBeforeStart = _position.FramesFromBeforeStart,
            PositionPending = _position.IsPending,
            DeviceRebuilds = _deviceRebuilds,
            SettledFrame = _policy.SettledFrame,
            ShownFrames = shown,
            ClockState = clockState,
            ClockSeconds = clockSeconds,
            PlayerSeconds = playerSeconds,
            PlayerStates = playerStates,
            FilesClosedAfterMilliseconds = _filesClosedAfterMilliseconds,
            FilesClosed = _filesClosed,
        };
    }
}
