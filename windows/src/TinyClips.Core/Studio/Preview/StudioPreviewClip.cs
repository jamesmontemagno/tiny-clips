using Vortice.Direct3D11;
using Windows.Foundation;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace TinyClips.Core.Studio.Preview;

/// <summary>
/// One clip of the preview: a frame-server <see cref="MediaPlayer"/> and the texture its frames are
/// copied into.
/// </summary>
/// <remarks>
/// A clip has two texture slots. The copy target is where the player's next frame goes; the draw
/// source is the texture the scene is drawn from. They are the same texture except just after the
/// copy target was replaced by one of another size: the old one stays the draw source until a
/// frame has reached the new one, so the picture never shows an empty texture. Both slots, and
/// <see cref="CopyHoldsFrame"/>, are only touched with the device lock held.
/// </remarks>
internal sealed class StudioPreviewClip
{
    private long _copyCount;
    private long _deliveredCount;
    private long _callbacksStarted;
    private long _lastCallbackStartedAt;
    private long _copyFailures;
    private long _seeksCompleted;
    private long _lastSeekCompletedAt;
    private long _endsReached;
    private int _deliveriesInFlight;
    private int _mostDeliveriesAtOnce;
    private int _hasAnnouncedFrame;
    private volatile bool _hasDeliveredFrame;
    private volatile bool _firstFrameWasEmpty;

    public StudioPreviewClip(int index, string name, string path, int sourceWidth, int sourceHeight, StudioPreviewClipTiming timing, MediaPlayer player, MediaSource source)
    {
        Index = index;
        Name = name;
        Path = path;
        SourceWidth = Math.Max(1, sourceWidth);
        SourceHeight = Math.Max(1, sourceHeight);
        Timing = timing;
        Player = player;
        Session = player.PlaybackSession;
        Source = source;
    }

    /// <summary>0 for the screen, 1 for the camera. Also the track number of the seek policy.</summary>
    public int Index { get; }

    /// <summary>"screen" or "camera", for messages.</summary>
    public string Name { get; }

    public string Path { get; }

    public int SourceWidth { get; }

    public int SourceHeight { get; }

    public StudioPreviewClipTiming Timing { get; }

    public MediaPlayer Player { get; }

    public MediaPlaybackSession Session { get; }

    public MediaSource Source { get; }

    public ID3D11Texture2D? CopyTexture { get; set; }

    public IDirect3DSurface? CopySurface { get; set; }

    public StudioPreviewCopyTargetSize CopySize { get; set; }

    /// <summary>A frame has been copied into the current copy target.</summary>
    public bool CopyHoldsFrame { get; set; }

    public ID3D11Texture2D? DrawTexture { get; set; }

    public IDirect3DSurface? DrawSurface { get; set; }

    public StudioPreviewCopyTargetSize DrawSize { get; set; }

    /// <summary>The value of <see cref="CopyCount"/> when the scene was last drawn. Render thread only.</summary>
    public long DrawnCopyCount { get; set; } = -1;

    /// <summary>The source has opened. Render thread only.</summary>
    public bool Opened { get; set; }

    public TypedEventHandler<MediaPlayer, object>? FrameHandler { get; set; }

    public TypedEventHandler<MediaPlayer, object>? OpenedHandler { get; set; }

    public TypedEventHandler<MediaPlayer, object>? EndedHandler { get; set; }

    public TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>? FailedHandler { get; set; }

    public TypedEventHandler<MediaPlaybackSession, object>? SeekCompletedHandler { get; set; }

    /// <summary>
    /// Frames copied into a texture so far. It changes with the device lock held, so whoever holds
    /// the lock knows exactly which copy the textures contain.
    /// </summary>
    public long CopyCount => Interlocked.Read(ref _copyCount);

    /// <summary>Of those, the ones a <c>VideoFrameAvailable</c> callback made. Each is followed by a signal to the render thread.</summary>
    public long DeliveredCount => Interlocked.Read(ref _deliveredCount);

    /// <summary>
    /// Delivered frames the render thread has been told about. While it is behind
    /// <see cref="DeliveredCount"/>, the texture holds a frame the render thread cannot name yet.
    /// Render thread only.
    /// </summary>
    public long AcknowledgedCount { get; set; }

    /// <summary><c>VideoFrameAvailable</c> callbacks that have started.</summary>
    public long CallbacksStarted => Interlocked.Read(ref _callbacksStarted);

    public long LastCallbackStartedAt => Interlocked.Read(ref _lastCallbackStartedAt);

    public long CopyFailures => Interlocked.Read(ref _copyFailures);

    /// <summary><c>SeekCompleted</c> events the player's session has raised.</summary>
    public long SeeksCompleted => Interlocked.Read(ref _seeksCompleted);

    public long LastSeekCompletedAt => Interlocked.Read(ref _lastSeekCompletedAt);

    public void SeekCompletedRaised(long timestamp)
    {
        Interlocked.Increment(ref _seeksCompleted);
        Interlocked.Exchange(ref _lastSeekCompletedAt, timestamp);
    }

    /// <summary><c>MediaEnded</c> events the player has raised.</summary>
    public long EndsReached => Interlocked.Read(ref _endsReached);

    public void EndReached() => Interlocked.Increment(ref _endsReached);

    /// <summary>Frame deliveries that have started and not finished.</summary>
    public int DeliveriesInFlight => Volatile.Read(ref _deliveriesInFlight);

    /// <summary>
    /// The most frame deliveries that were ever under way at once. 1 means the player hands its
    /// frames over one after the other, so they reach the render thread in the order it made them.
    /// </summary>
    public int MostDeliveriesAtOnce => Volatile.Read(ref _mostDeliveriesAtOnce);

    /// <summary>The player has delivered at least one frame, so a frame can be pulled from it.</summary>
    public bool HasDeliveredFrame => _hasDeliveredFrame;

    /// <summary>The player has said that it has a frame to hand over.</summary>
    public bool HasAnnouncedFrame => Volatile.Read(ref _hasAnnouncedFrame) != 0;

    /// <summary>Notes that the player has a frame to hand over. True the first time.</summary>
    public bool FrameAnnounced() => Interlocked.Exchange(ref _hasAnnouncedFrame, 1) == 0;

    /// <summary>
    /// The first frame taken from the player left nothing in the texture: the player was on another
    /// graphics adapter than the texture and is moving over (see <see cref="StudioPreviewProof"/>).
    /// </summary>
    public bool FirstFrameWasEmpty
    {
        get => _firstFrameWasEmpty;
        set => _firstFrameWasEmpty = value;
    }

    public void DeliveryStarted(long timestamp)
    {
        var inFlight = Interlocked.Increment(ref _deliveriesInFlight);
        int most;
        while (inFlight > (most = Volatile.Read(ref _mostDeliveriesAtOnce)))
        {
            Interlocked.CompareExchange(ref _mostDeliveriesAtOnce, inFlight, most);
        }

        Interlocked.Increment(ref _callbacksStarted);
        Interlocked.Exchange(ref _lastCallbackStartedAt, timestamp);
    }

    public void DeliveryFinished() => Interlocked.Decrement(ref _deliveriesInFlight);

    /// <summary>
    /// A frame is in the copy target. Device lock held. <paramref name="delivered"/> is true for a
    /// frame the player handed over in its callback, false for one taken again from the player.
    /// </summary>
    public void FrameCopied(bool delivered)
    {
        CopyHoldsFrame = true;
        _hasDeliveredFrame = true;
        Interlocked.Increment(ref _copyCount);
        if (delivered)
        {
            Interlocked.Increment(ref _deliveredCount);
        }
    }

    public void CopyFailed() => Interlocked.Increment(ref _copyFailures);
}
