using System.Collections.Concurrent;
using System.Diagnostics;
using Vortice.Direct3D11;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace StudioEngineSpike.Engine;

/// <summary>One <c>VideoFrameAvailable</c> callback, as the measurement code sees it.</summary>
/// <param name="Track">0 = screen, 1 = camera.</param>
/// <param name="Timestamp">Stopwatch timestamp when the callback started.</param>
/// <param name="ControllerTicks">Timeline controller position when the callback started.</param>
/// <param name="SessionTicks">The player's own <c>PlaybackSession.Position</c> at that moment.</param>
/// <param name="Frame">Frame number decoded from the copied pixels, or -1 when not identified.</param>
/// <param name="CopyMs">Time spent inside <c>CopyFrameToVideoSurface</c> (CPU side).</param>
/// <param name="ReadMs">Time for the strip readback, which also waits for the GPU to finish the copy.</param>
internal readonly record struct FrameEvent(int Track, long Timestamp, long ControllerTicks, long SessionTicks, int Frame, double CopyMs, double ReadMs);

/// <summary>One clip of the preview: a frame-server MediaPlayer and the texture it copies frames into.</summary>
internal sealed class PlayerTrack : IDisposable
{
    private long _frameCount;
    private long _callbacksStarted;
    private long _copyCount;
    private int _latestFrame = FrameCode.Unreadable;
    private long _latestTimestamp;
    private long _seekCompletedCount;
    private long _latestSeekCompleted;

    public PlayerTrack(int index, string name, ClipSpec clip, MediaPlayer player, ID3D11Texture2D texture, IDirect3DSurface surface, int textureWidth, int textureHeight)
    {
        Index = index;
        Name = name;
        Clip = clip;
        Player = player;
        Texture = texture;
        Surface = surface;
        TextureWidth = textureWidth;
        TextureHeight = textureHeight;
    }

    public int Index { get; }

    public string Name { get; }

    public ClipSpec Clip { get; }

    public MediaPlayer Player { get; }

    /// <summary>BGRA render target on the shared device; always holds the player's latest frame.</summary>
    public ID3D11Texture2D Texture { get; }

    public IDirect3DSurface Surface { get; }

    public int TextureWidth { get; }

    public int TextureHeight { get; }

    public SceneSource Source => new(Texture, 0, TextureWidth, TextureHeight);

    /// <summary>Maps clip pixels to this track's texture (the player scales when the texture is smaller).</summary>
    public ClipMap TextureMap => new(0, 0, (double)TextureWidth / Clip.Width, (double)TextureHeight / Clip.Height);

    public long FrameCount => Interlocked.Read(ref _frameCount);

    /// <summary>VideoFrameAvailable callbacks that have started (the frame may still be copying).</summary>
    public long CallbacksStarted => Interlocked.Read(ref _callbacksStarted);

    /// <summary>
    /// Copies completed into <see cref="Texture"/>. Incremented while the device gate is still held,
    /// so a reader that holds the gate knows exactly which copy the texture contains.
    /// </summary>
    public long CopyCount => Interlocked.Read(ref _copyCount);

    internal void RecordCallbackStarted() => Interlocked.Increment(ref _callbacksStarted);

    internal void RecordCopied() => Interlocked.Increment(ref _copyCount);

    /// <summary>Frame number of the latest copied frame (only maintained while frames are identified).</summary>
    public int LatestFrame => Volatile.Read(ref _latestFrame);

    public long LatestTimestamp => Interlocked.Read(ref _latestTimestamp);

    /// <summary><c>PlaybackSession.SeekCompleted</c> events raised so far, and when the last one was.</summary>
    public long SeekCompletedCount => Interlocked.Read(ref _seekCompletedCount);

    public long LatestSeekCompleted => Interlocked.Read(ref _latestSeekCompleted);

    public FrameCodeReader Reader { get; set; } = null!;

    internal void Record(int frame, long timestamp)
    {
        Volatile.Write(ref _latestFrame, frame);
        Interlocked.Exchange(ref _latestTimestamp, timestamp);
        Interlocked.Increment(ref _frameCount);
    }

    internal void RecordSeekCompleted()
    {
        Interlocked.Exchange(ref _latestSeekCompleted, Stopwatch.GetTimestamp());
        Interlocked.Increment(ref _seekCompletedCount);
    }

    public void Dispose()
    {
        Player.Dispose();
        (Surface as IDisposable)?.Dispose();
        Texture.Dispose();
    }
}

/// <summary>
/// The preview decode path under test: two <see cref="MediaPlayer"/>s in frame-server mode, one per
/// clip, slaved to one <see cref="MediaTimelineController"/>. Every <c>VideoFrameAvailable</c> copies
/// the frame into that track's texture on the shared D3D11 device; nothing here knows about windows.
/// </summary>
internal sealed class PreviewEngine : IDisposable
{
    private readonly GraphicsDevice _graphics;
    private readonly ManualResetEventSlim _screenOpened = new(false);
    private readonly ManualResetEventSlim _cameraOpened = new(false);
    private readonly long _created = Stopwatch.GetTimestamp();
    private string? _failure;

    public PreviewEngine(GraphicsDevice graphics, string screenPath, string cameraPath, double textureScale = 1.0)
    {
        _graphics = graphics;
        Controller = new MediaTimelineController();
        Controller.StateChanged += (sender, _) => StateChanges.Enqueue((Stopwatch.GetTimestamp(), sender.State));
        Screen = CreateTrack(0, "screen", TestMedia.Screen, screenPath, textureScale, _screenOpened);
        Camera = CreateTrack(1, "camera", TestMedia.Camera, cameraPath, textureScale, _cameraOpened);
    }

    public MediaTimelineController Controller { get; }

    public PlayerTrack Screen { get; }

    public PlayerTrack Camera { get; }

    public PlayerTrack[] Tracks => new[] { Screen, Camera };

    /// <summary>
    /// When set, each callback also reads the frame-number strip back from the copied texture.
    /// That costs a GPU sync per frame, so the playback tests are also run with it off.
    /// </summary>
    public bool IdentifyFrames { get; set; } = true;

    public ConcurrentQueue<FrameEvent> Events { get; } = new();

    public ConcurrentQueue<(long Timestamp, MediaTimelineControllerState State)> StateChanges { get; } = new();

    public ConcurrentQueue<string> Log { get; } = new();

    /// <summary>Raised on a MediaPlayer thread after a frame has been copied into a track's texture.</summary>
    public event Action<PlayerTrack>? FrameArrived;

    public string? Failure => _failure;

    /// <summary>Milliseconds from construction until each player raised <c>MediaOpened</c>.</summary>
    public double ScreenOpenedMs { get; private set; }

    public double CameraOpenedMs { get; private set; }

    public bool WaitForOpen(TimeSpan timeout) => _screenOpened.Wait(timeout) && _cameraOpened.Wait(timeout) && _failure is null;

    /// <summary>Sets the camera's offset on the shared timeline (see the sign experiment in preview mode).</summary>
    public void SetCameraOffset(TimeSpan offset) => Camera.Player.TimelineControllerPositionOffset = offset;

    private PlayerTrack CreateTrack(int index, string name, ClipSpec clip, string path, double textureScale, ManualResetEventSlim opened)
    {
        var width = Math.Max(2, (int)Math.Round(clip.Width * textureScale));
        var height = Math.Max(2, (int)Math.Round(clip.Height * textureScale));
        var texture = _graphics.CreateRenderTexture(width, height);
        var surface = _graphics.CreateSurface(texture);

        var player = new MediaPlayer
        {
            AutoPlay = false,
            IsMuted = true,
            IsVideoFrameServerEnabled = true,
        };

        // The app drives transport itself: no SMTC integration, and one clock for both players.
        player.CommandManager.IsEnabled = false;
        player.TimelineController = Controller;

        var track = new PlayerTrack(index, name, clip, player, texture, surface, width, height)
        {
            Reader = new FrameCodeReader(_graphics),
        };

        player.MediaOpened += (_, _) =>
        {
            var elapsed = Stopwatch.GetElapsedTime(_created).TotalMilliseconds;
            if (index == 0)
            {
                ScreenOpenedMs = elapsed;
            }
            else
            {
                CameraOpenedMs = elapsed;
            }

            opened.Set();
        };
        player.MediaFailed += (_, args) =>
        {
            _failure = $"{name}: {args.Error} 0x{args.ExtendedErrorCode?.HResult:X8} {args.ErrorMessage}";
            opened.Set();
        };
        player.MediaEnded += (_, _) => Log.Enqueue($"{Stopwatch.GetElapsedTime(_created).TotalSeconds:0.000}s {name}: MediaEnded");
        player.PlaybackSession.SeekCompleted += (_, _) => track.RecordSeekCompleted();
        player.VideoFrameAvailable += (sender, _) => OnVideoFrameAvailable(track, sender);
        player.Source = MediaSource.CreateFromUri(new Uri(path));
        return track;
    }

    private void OnVideoFrameAvailable(PlayerTrack track, MediaPlayer player)
    {
        var timestamp = Stopwatch.GetTimestamp();
        track.RecordCallbackStarted();
        var controllerTicks = Controller.Position.Ticks;
        var sessionTicks = player.PlaybackSession.Position.Ticks;

        double copyMs;
        lock (_graphics.Gate)
        {
            var start = Stopwatch.GetTimestamp();
            player.CopyFrameToVideoSurface(track.Surface);
            copyMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            track.RecordCopied();
        }

        var frame = FrameCode.Unreadable;
        var readMs = 0.0;
        if (IdentifyFrames)
        {
            var start = Stopwatch.GetTimestamp();
            frame = track.Reader.Read(track.Texture, 0, track.TextureWidth, track.TextureHeight, track.Clip, track.TextureMap);
            readMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        track.Record(frame, timestamp);
        Events.Enqueue(new FrameEvent(track.Index, timestamp, controllerTicks, sessionTicks, frame, copyMs, readMs));
        FrameArrived?.Invoke(track);
    }

    public void Dispose()
    {
        try
        {
            Controller.Pause();
        }
        catch
        {
            // Tearing down; the controller may already be in an error state.
        }

        Screen.Player.TimelineController = null;
        Camera.Player.TimelineController = null;
        Screen.Dispose();
        Camera.Dispose();
        _screenOpened.Dispose();
        _cameraOpened.Dispose();
    }
}
