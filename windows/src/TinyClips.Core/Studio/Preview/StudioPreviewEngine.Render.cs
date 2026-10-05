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
    private double _drawnWidth = DefaultSurfaceWidth;
    private double _drawnHeight = DefaultSurfaceHeight;

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
            if (clip.PictureCount != clip.DrawnPictureCount)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Draws the scene when a clip's picture has changed since it was last drawn, or a redraw was
    /// asked for. It never waits for the other clip, and never presents without something new.
    /// The pictures only change on this thread, each together with the number it is drawn under.
    /// </summary>
    private void Render()
    {
        if (Volatile.Read(ref _simulatedDeviceLosses) > 0)
        {
            Interlocked.Decrement(ref _simulatedDeviceLosses);
            throw new StudioDeviceLostException("Simulated loss of the rendering device.");
        }

        // During a repair's detour the pictures show frames nobody asked for.
        if (!HasSomethingToDraw() || _policy.HoldPicture)
        {
            return;
        }

        lock (_surfaceLock)
        {
            var surface = _surface;
            if (surface is null)
            {
                return;
            }

            var graphics = _graphics;
            var drawn = false;
            lock (graphics.Gate)
            {
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
                if (target is not null && width > 0 && height > 0 && APictureIsMissing())
                {
                    // Asked for again by the frame that brings the picture back.
                    target.Dispose();
                    _redraw = true;
                }
                else if (target is not null && width > 0 && height > 0)
                {
                    target = Remember(target);
                    _drawnWidth = width;
                    _drawnHeight = height;
                    UpdateCopyTargets(graphics, _renderProject, width, height);
                    foreach (var clip in _clips)
                    {
                        clip.DrawnPictureCount = clip.PictureCount;
                    }

                    if (_renderDelay is { } delay && delay() is { Ticks: > 0 } wait)
                    {
                        // A draw that takes long: the players wait for the device with their frames.
                        Thread.Sleep(wait);
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
                        clip.DrawnPictureCount = clip.PictureCount;
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

                // While the clock runs the player's next frame is there within a thirtieth of a
                // second, and the frame it has now may not be the one the picture shows.
                CreateCopyTarget(graphics, clip, chosen, refill: !_policy.IsPlaying);
            }
        }
    }

    /// <summary>
    /// Gives a clip a copy target of another size. A frame in the old one that has its number
    /// and has not reached the picture yet is put aside first, at the size it has, and is taken
    /// from there. Device lock held; render thread, or the thread that opens the engine before
    /// the render thread runs.
    /// </summary>
    /// <param name="refill">
    /// Take the player's current frame again at the new size, into the picture, so that a paused
    /// picture does not stay at the old size until the next seek. Only done when that frame is the
    /// one the picture shows.
    /// </param>
    private void CreateCopyTarget(StudioGraphicsDevice graphics, StudioPreviewClip clip, StudioPreviewCopyTargetSize size, bool refill)
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

        // Nothing but this engine refers to a copy target: the scene is drawn from the picture.
        var pictureIsCurrent = clip.CopyHoldsFrame && clip.CopyIsPicture;
        if (clip.CopyWaits)
        {
            KeepAside(graphics, clip);
        }
        else if (!clip.KeptWaits)
        {
            clip.KeptTexture?.Dispose();
            clip.KeptTexture = null;
        }

        (clip.CopySurface as IDisposable)?.Dispose();
        clip.CopyTexture?.Dispose();
        clip.CopyTexture = texture;
        clip.CopySurface = surface;
        clip.CopySize = size;
        clip.CopyHoldsFrame = false;
        clip.CopyWaits = false;
        clip.CopyIsPicture = false;
        clip.CopySerial++;
        if (!refill || !clip.HasDeliveredFrame || !pictureIsCurrent || clip.DeliveriesInFlight > 0)
        {
            return;
        }

        try
        {
            clip.Player.CopyFrameToVideoSurface(surface);
            clip.FrameCopied(delivered: false);
            clip.CopyWaits = true;
            TakeIntoPicture(clip, clip.CopySerial);
        }
        catch (Exception ex) when (!IsDeviceLost(ex))
        {
            // The picture stays as it is until the player's next frame arrives.
        }
    }

    /// <summary>
    /// Takes a frame that waits to be taken into its clip's picture: from the copy target, or
    /// from where it was put aside when the player's next frame came before this thread did.
    /// False when it is in neither any more: a later frame was written over it. Render thread
    /// only; takes the device lock.
    /// </summary>
    /// <param name="serial">Which copy it is: <see cref="StudioPreviewClip.CopySerial"/> as it was when the frame was copied.</param>
    private bool TakeIntoPicture(StudioPreviewClip clip, long serial)
    {
        var graphics = _graphics;
        lock (graphics.Gate)
        {
            var kept = !(clip.CopyWaits && clip.CopySerial == serial);
            var source = kept ? clip.KeptTexture : clip.CopyTexture;
            if (source is null || (kept && !(clip.KeptWaits && clip.KeptSerial == serial)))
            {
                return false;
            }

            var size = kept ? clip.KeptSize : clip.CopySize;
            if (clip.DrawTexture is null || clip.DrawSize != size)
            {
                if (clip.DrawTexture is not null)
                {
                    ForgetRendererSources();
                    clip.DrawTexture.Dispose();
                    clip.DrawTexture = null;
                }

                clip.DrawTexture = graphics.CreateRenderTexture(size.Width, size.Height);
                clip.DrawSize = size;
            }

            graphics.Context.CopyResource(clip.DrawTexture, source);
            if (kept)
            {
                clip.KeptWaits = false;
            }
            else
            {
                clip.CopyWaits = false;
                clip.CopyIsPicture = true;
            }

            // A frame with a number is later than any that was shown without one.
            clip.ShowsLive = false;
            clip.PictureCount++;
            return true;
        }
    }

    /// <summary>
    /// Shows a frame that has no number in place of its clip's picture: the same as
    /// <see cref="TakeIntoPicture"/>, into the clip's other picture. The picture with a number
    /// stays as it is, for when the clock stops. Render thread only; takes the device lock.
    /// </summary>
    private bool TakeLivePicture(StudioPreviewClip clip, long serial)
    {
        var graphics = _graphics;
        lock (graphics.Gate)
        {
            var kept = !(clip.CopyWaits && clip.CopySerial == serial);
            var source = kept ? clip.KeptTexture : clip.CopyTexture;
            if (source is null || (kept && !(clip.KeptWaits && clip.KeptSerial == serial)))
            {
                return false;
            }

            var size = kept ? clip.KeptSize : clip.CopySize;
            if (clip.LiveTexture is null || clip.LiveSize != size)
            {
                if (clip.LiveTexture is not null)
                {
                    ForgetRendererSources();
                    clip.LiveTexture.Dispose();
                    clip.LiveTexture = null;
                }

                clip.LiveTexture = graphics.CreateRenderTexture(size.Width, size.Height);
                clip.LiveSize = size;
            }

            graphics.Context.CopyResource(clip.LiveTexture, source);
            if (kept)
            {
                clip.KeptWaits = false;
            }
            else
            {
                clip.CopyWaits = false;
            }

            clip.LiveSerial = serial;
            clip.ShowsLive = true;
            clip.PictureCount++;
            return true;
        }
    }

    /// <summary>
    /// The frame that is shown without a number has got its number: it becomes the clip's picture
    /// as it is. Render thread only; takes the device lock.
    /// </summary>
    private void NumberTheLivePicture(StudioPreviewClip clip)
    {
        var graphics = _graphics;
        lock (graphics.Gate)
        {
            (clip.DrawTexture, clip.LiveTexture) = (clip.LiveTexture, clip.DrawTexture);
            (clip.DrawSize, clip.LiveSize) = (clip.LiveSize, clip.DrawSize);
            clip.ShowsLive = false;

            // The copy that was put aside for this is not needed.
            if (clip.KeptSerial == clip.LiveSerial)
            {
                clip.KeptWaits = false;
            }

            if (clip.CopySerial == clip.LiveSerial)
            {
                clip.CopyWaits = false;
                clip.CopyIsPicture = true;
            }
        }
    }

    /// <summary>
    /// Whether the scene is laid out the same way for every frame from the one the screen's
    /// picture shows to <paramref name="latest"/>: then a screen frame that is one of those,
    /// nobody knows which, can be drawn. Render thread only.
    /// </summary>
    private bool SceneIsTheSameUpTo(long latest) =>
        StudioPreviewStillness.SameLayout(_renderProject, _events, _timeline.FrameRate, _policy.ShownFrame(0), latest, _drawnWidth, _drawnHeight);

    /// <summary>A frame that waits to be taken into the picture is not going to be. Render thread only; takes the device lock.</summary>
    private void StopWaiting(StudioPreviewClip clip, long serial)
    {
        var graphics = _graphics;
        lock (graphics.Gate)
        {
            if (clip.CopySerial == serial)
            {
                clip.CopyWaits = false;
            }
            else if (clip.KeptSerial == serial)
            {
                clip.KeptWaits = false;
            }
        }
    }

    /// <summary>
    /// Lets the players' first frames be taken, once each of them has one to hand over, and takes
    /// the frame of a player that does not hand it over by itself. Render thread only. Returns
    /// when it wants to be called again, as a clock reading.
    /// </summary>
    /// <remarks>
    /// A player decodes on a Direct3D device of its own, and the first frame it copies into a
    /// texture tells it which device its frames are wanted on. When that device is on another
    /// graphics adapter the player moves its work there. Measured with the players on the graphics
    /// hardware and the engine on the software adapter: while one player moves, another that is
    /// still opening fails (<c>MF_E_INVALIDMEDIATYPE</c>, reported as <c>SourceNotSupported</c>),
    /// and one that has opened and not yet announced its first frame never announces any. Two
    /// players that both have their first frame ready come through each other's move. So on the
    /// software adapter, the one place where the engine can know that the players may be
    /// elsewhere, no frame is taken before every player has one. What a player that moved hands
    /// over at first is another matter: see <see cref="StudioPreviewProof"/>.
    /// </remarks>
    private long ReleaseFirstFrames()
    {
        var now = Stopwatch.GetTimestamp();
        if (_firstFramesHeld)
        {
            foreach (var clip in _clips)
            {
                if (!clip.HasAnnouncedFrame)
                {
                    return long.MaxValue;
                }
            }

            // From here on a frame is copied by the callback that announces it.
            _firstFramesReleasedAt = now;
            _firstFramesHeld = false;
            if (_trace is not null)
            {
                Note("every player has a first frame: they are taken now");
            }
        }

        // A player announces a frame nobody took again and again (measured: every 10 ms). That is
        // not written down anywhere, so a player that stays silent has its frame taken from here.
        var pullAt = _firstFramesReleasedAt + (FirstFramePullMilliseconds * Stopwatch.Frequency / 1000);
        var waiting = false;
        foreach (var clip in _clips)
        {
            if (clip.HasDeliveredFrame)
            {
                continue;
            }

            if (now < pullAt || clip.DeliveriesInFlight > 0)
            {
                waiting = true;
                continue;
            }

            var graphics = _graphics;
            lock (graphics.Gate)
            {
                if (clip.HasDeliveredFrame || clip.CopySurface is not { } surface)
                {
                    continue;
                }

                long positionTicks;
                try
                {
                    positionTicks = clip.Session.Position.Ticks;
                    clip.Player.CopyFrameToVideoSurface(surface);
                }
                catch (Exception ex)
                {
                    // Dealt with as a copy that failed in the player's own callback is, by the
                    // next pass; and tried again until that gives the player up.
                    clip.CopyFailed();
                    Post(new Signal(SignalKind.CopyFailed, clip.Index, now, 0, null, ex));
                    waiting = true;
                    continue;
                }

                var empty = NoteFirstFrame(graphics, clip);
                clip.FrameCopied(delivered: false);
                clip.CopyWaits = true;
                TakeIntoPicture(clip, clip.CopySerial);
                Interlocked.Increment(ref _firstFramesPulled);
                if (_trace is not null)
                {
                    Note($"first frame of clip {clip.Index} taken from the player, which did not hand it over{(empty ? ": nothing in it" : string.Empty)}");
                }

                _policy.OnFrame(clip.Index, clip.Timing.FrameAtPlayerTicks(positionTicks), now);
            }
        }

        return waiting ? Math.Max(pullAt, now + (Stopwatch.Frequency / 100)) : long.MaxValue;
    }

    /// <summary>
    /// Looks at the first frame taken from a player, and notes when it left nothing in the
    /// texture. Device lock held. Returns whether it did.
    /// </summary>
    private bool NoteFirstFrame(StudioGraphicsDevice graphics, StudioPreviewClip clip)
    {
        if (clip.CopyTexture is not { } texture)
        {
            return false;
        }

        Interlocked.Increment(ref _firstFramesLookedAt);
        if (!StudioPreviewPixels.IsEmpty(graphics, texture))
        {
            return false;
        }

        clip.FirstFrameWasEmpty = true;
        Interlocked.Increment(ref _firstFramesEmpty);
        return true;
    }

    /// <summary>
    /// The fingerprint of the picture each player's texture holds, for the clips that are part of
    /// the picture of <paramref name="timelineFrame"/>. Any thread.
    /// </summary>
    private ulong[] ReadPictures(long timelineFrame)
    {
        var pictures = new List<ulong>(_clips.Length);
        var graphics = Volatile.Read(ref _graphics);
        lock (graphics.Gate)
        {
            foreach (var clip in _clips)
            {
                if (_timeline.IsShown(clip.Index, timelineFrame) && clip.CopyTexture is { } texture)
                {
                    pictures.Add(StudioPreviewPixels.Fingerprint(graphics, texture));
                }
            }
        }

        return [.. pictures];
    }

    // Device lock held.
    private static void DisposeClipTextures(StudioPreviewClip clip)
    {
        (clip.CopySurface as IDisposable)?.Dispose();
        clip.CopyTexture?.Dispose();
        clip.KeptTexture?.Dispose();
        clip.DrawTexture?.Dispose();
        clip.LiveTexture?.Dispose();
        clip.CopyTexture = null;
        clip.CopySurface = null;
        clip.KeptTexture = null;
        clip.DrawTexture = null;
        clip.LiveTexture = null;
        clip.ShowsLive = false;
        clip.CopyHoldsFrame = false;
        clip.CopyWaits = false;
        clip.CopyIsPicture = false;
        clip.KeptWaits = false;
        clip.CopySerial++;
        clip.Namer.Forget();
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

    /// <summary>What a clip is drawn from, for the renderer: its picture, or the later frame that is shown without a number.</summary>
    private static StudioGpuVideoFrame? Source(StudioPreviewClip clip) =>
        clip.ShowsLive && clip.LiveTexture is { } live ? new StudioGpuVideoFrame(live, 0, clip.LiveSize.Width, clip.LiveSize.Height)
        : clip.DrawTexture is { } texture ? new StudioGpuVideoFrame(texture, 0, clip.DrawSize.Width, clip.DrawSize.Height)
        : null;

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
            // Whoever opens the preview has to be able to tell that it was the device, whatever
            // the error that brought it to light said: a device lost while opening is answered
            // by opening once more.
            Fail(message, _opening && !IsDeviceLost(cause) ? new StudioDeviceLostException("The graphics device was removed.", cause) : cause, asDataError: false);
            return;
        }

        var sizes = new StudioPreviewCopyTargetSize[_clips.Length];
        var current = new bool[_clips.Length];
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
                    // Whether the frame the player has is the one that was on the picture: then
                    // it can be taken from the player again, under the same number.
                    current[clip.Index] = clip.CopyHoldsFrame && clip.CopyIsPicture;
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
                var clip = _clips[index];
                clip.CopyIsPicture = current[index];
                clip.CopyHoldsFrame = current[index];
                CreateCopyTarget(created, clip, sizes[index], refill: !_policy.IsPlaying);
                missing |= clip.DrawTexture is null;
            }
        }

        _redraw = true;
        if (missing && !_policy.IsPlaying)
        {
            // A player would not hand its frame over again. A seek to where it is makes it.
            foreach (var clip in _clips)
            {
                if (clip.DrawTexture is null)
                {
                    _policy.Forget(clip.Index);
                }
            }

            _policy.RequestSeek(_position.Frame);
        }
    }

    /// <summary>
    /// A clip that had a picture has none: the device was rebuilt, and the frame could not be
    /// taken from the player again. The scene is not drawn without it; the player's next frame
    /// brings it back.
    /// </summary>
    private bool APictureIsMissing()
    {
        foreach (var clip in _clips)
        {
            if (clip.HasDeliveredFrame && clip.DrawTexture is null)
            {
                return true;
            }
        }

        return false;
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

    /// <summary>
    /// Called by a player's own thread, with the device lock held, after every frame it has
    /// copied: which copy it is and what the engine took it for. For the checks that read from
    /// the pixels which frame a copy holds. It has to be quick: the player waits.
    /// </summary>
    internal Action<StudioPreviewHandOver>? AfterCopy { get; set; }

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
    /// The pixels of a clip's picture, the texture the scene is drawn from, as tightly packed
    /// BGRA, or null before its first frame.
    /// </summary>
    internal byte[]? ReadClipTexture(int clipIndex, out int width, out int height) => ReadClipTexture(clipIndex, copyTarget: false, out width, out height);

    /// <summary>
    /// The same of a clip's copy target when <paramref name="copyTarget"/> is set: the frame the
    /// player handed over last, which is not on the picture while it has no number.
    /// </summary>
    internal byte[]? ReadClipTexture(int clipIndex, bool copyTarget, out int width, out int height)
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

            // Whether the scene is drawn from the picture without a number changes on the render
            // thread, without this lock. Read while the clock runs, this may be the picture of a
            // moment ago; read after Pause() has returned, it is the picture.
            var live = !copyTarget && clip.ShowsLive && clip.LiveTexture is not null;
            var texture = copyTarget ? (clip.CopyHoldsFrame ? clip.CopyTexture : null) : live ? clip.LiveTexture : clip.DrawTexture;
            if (texture is null)
            {
                return null;
            }

            var size = copyTarget ? clip.CopySize : live ? clip.LiveSize : clip.DrawSize;
            width = size.Width;
            height = size.Height;
            return graphics.ReadTexture(texture);
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
            RestsFetchedAnew = _policy.RestsFetchedAnew,
            StepsAvoided = _policy.StepsAvoided,
            FramesDrawn = _framesDrawn,
            FramesAfterPause = _framesAfterPause,
            LateFramesDiscarded = Interlocked.Read(ref _lateFramesDiscarded),
            FramesWithoutNumber = [.. _framesWithoutNumber],
            FramesShownWithoutNumber = [.. _framesShownWithoutNumber],
            FramesNumberedLate = [.. _framesNumberedLate],
            FramesShownUnsure = [.. _framesShownUnsure],
            FramesInferred = [.. _framesInferred],
            FramesPassedOver = [.. _framesPassedOver.Select((_, index) => Interlocked.Read(ref _framesPassedOver[index]))],
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
            FilesWaitedFor = _filesWaitedFor,
            OpenAttempts = _openAttempt,
            FirstFramesLookedAt = Volatile.Read(ref _firstFramesLookedAt),
            FirstFramesEmpty = Volatile.Read(ref _firstFramesEmpty),
            FirstFramesPulled = Volatile.Read(ref _firstFramesPulled),
            ProofRounds = Volatile.Read(ref _proofRounds),
            ProofHeld = Volatile.Read(ref _proofHeld),
        };
    }
}
