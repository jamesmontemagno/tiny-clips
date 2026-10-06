using Vortice.Direct3D11;
using Windows.Foundation;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace TinyClips.Core.Studio.Preview;

/// <summary>
/// One clip of the preview: a frame-server <see cref="MediaPlayer"/> and the textures its frames
/// go through.
/// </summary>
/// <remarks>
/// <para>
/// A clip has four textures. The copy target is where the player puts its next frame, whatever
/// that frame is. The picture is what the scene is drawn from: the render thread takes a frame
/// from the copy target into it once the frame has a number, and never before, so the picture
/// and the number it is drawn under always belong together. The third holds a frame that waits
/// to be taken when the player is about to write over it: that is how a frame that only got its
/// number from the frame after it is still shown, and how two frames that come a hundredth of
/// a second apart are both drawn. The fourth is a picture without a number: a frame of which it
/// cannot be told which one it is, shown in place of the picture for as long as the clock runs
/// and the scene is laid out the same way whichever frame it is. Nothing is said about it to
/// anyone, and when the clock stops the picture with a number is the picture again.
/// </para>
/// <para>
/// The copy target and the kept frame, and what is said about them here, are only touched with
/// the device lock held. The two pictures are touched by the render thread alone, with the lock
/// held, or by a thread that keeps the render thread out of its round.
/// </para>
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

    public StudioPreviewClip(int index, string name, string path, int sourceWidth, int sourceHeight, StudioPreviewClipTiming timing, MediaPlayer player, MediaSource source, StudioPreviewNamingSettings? naming = null)
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
        Namer = new StudioPreviewFrameNamer(timing.FrameRate, naming);
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

    /// <summary>Tells which frame the player hands over while the clock runs. Device lock.</summary>
    public StudioPreviewFrameNamer Namer { get; }

    public ID3D11Texture2D? CopyTexture { get; set; }

    public IDirect3DSurface? CopySurface { get; set; }

    public StudioPreviewCopyTargetSize CopySize { get; set; }

    /// <summary>A frame has been copied into the current copy target.</summary>
    public bool CopyHoldsFrame { get; set; }

    /// <summary>Counts what was put into a copy target, so that a frame in it can be told from the one that came after.</summary>
    public long CopySerial { get; set; }

    /// <summary>The frame in the copy target has its number and waits for the render thread to take it into the picture.</summary>
    public bool CopyWaits { get; set; }

    /// <summary>The frame in the copy target is the one the picture shows: the player's own current frame is on the picture.</summary>
    public bool CopyIsPicture { get; set; }

    /// <summary>Holds a frame that still waits to be taken when the player writes the next one over it. Made when first needed.</summary>
    public ID3D11Texture2D? KeptTexture { get; set; }

    public StudioPreviewCopyTargetSize KeptSize { get; set; }

    /// <summary>The <see cref="CopySerial"/> the kept frame had.</summary>
    public long KeptSerial { get; set; }

    public bool KeptWaits { get; set; }

    /// <summary>The picture: what the scene is drawn from.</summary>
    public ID3D11Texture2D? DrawTexture { get; set; }

    public StudioPreviewCopyTargetSize DrawSize { get; set; }

    /// <summary>A later frame than the picture's, of which it is not known which frame it is.</summary>
    public ID3D11Texture2D? LiveTexture { get; set; }

    public StudioPreviewCopyTargetSize LiveSize { get; set; }

    /// <summary>The <see cref="CopySerial"/> the frame in <see cref="LiveTexture"/> had.</summary>
    public long LiveSerial { get; set; }

    /// <summary>The scene is drawn from <see cref="LiveTexture"/> in place of the picture.</summary>
    public bool ShowsLive { get; set; }

    /// <summary>How often what the scene is drawn from has changed. Render thread only.</summary>
    public long PictureCount { get; set; }

    /// <summary>
    /// Names the frame in <see cref="DrawTexture"/> for the renderer
    /// (<see cref="Rendering.StudioGpuVideoFrame.Stamp"/>): the value <see cref="PictureCount"/>
    /// had when the frame was put there. Every frame put into either picture gets a count of
    /// its own, so a texture never has two frames under one stamp, and no stamp of a texture
    /// that holds a frame is 0. It is counted always and given to the renderer only when the
    /// engine is asked to (<see cref="StudioPreviewOptions.StampPictures"/>). As the pictures
    /// are: the render thread, or a thread that keeps it out of its round.
    /// </summary>
    public long DrawStamp { get; set; }

    /// <summary>The same for the frame in <see cref="LiveTexture"/>.</summary>
    public long LiveStamp { get; set; }

    /// <summary>The value of <see cref="PictureCount"/> when the scene was last drawn. Render thread only.</summary>
    public long DrawnPictureCount { get; set; } = -1;

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

    /// <summary>Of those, the ones a <c>VideoFrameAvailable</c> callback made.</summary>
    public long DeliveredCount => Interlocked.Read(ref _deliveredCount);

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
        CopySerial++;
        CopyWaits = false;
        CopyIsPicture = false;
        _hasDeliveredFrame = true;
        Interlocked.Increment(ref _copyCount);
        if (delivered)
        {
            Interlocked.Increment(ref _deliveredCount);
        }
    }

    public void CopyFailed() => Interlocked.Increment(ref _copyFailures);
}
