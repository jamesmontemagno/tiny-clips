namespace TinyClips.Core.Studio.Preview;

/// <summary>
/// The timeline frame a preview reports as its position, and when that is news.
/// <para>
/// Whoever asks for a position acts on what it reads next, and that can be before the players
/// have got there: a caller that seeks to the start and plays must not find the frame it left
/// reported once more and take it for where playback is. So the rules are:
/// </para>
/// <list type="number">
/// <item>A requested frame is reported from the moment it is requested.</item>
/// <item>Until it has been reached, nothing the players show is reported in its place: not the
/// frame an earlier request comes to rest on, not a frame of the playback the request interrupts,
/// not the frame the players are brought onto after the clock stopped.</item>
/// <item>It has been reached when the seek policy has been given the newest request and has no
/// position left to reach. From then on the position is the frame of the picture again: each
/// frame of playback, and each frame the players come to rest on.</item>
/// <item>A frame of playback is one the screen's player handed over after the clock was started.
/// A frame that set out while the clock stood still reports the position of that moment, however
/// late it arrives, and is not taken for one.</item>
/// <item>When playback runs into the end of the recording the players are brought to rest on the
/// last frame. That frame is reported at once, as a requested frame is: whoever hears that
/// playback has stopped finds the position at the end, whether or not the last frame got through
/// before the clock stopped.</item>
/// <item>There is news when the reported frame changes, and once more when the players have
/// moved onto a requested frame: the frame is the same then, but now it is the picture.</item>
/// </list>
/// <para>
/// <see cref="Request"/> and <see cref="Frame"/> may be used from any thread. The other members
/// are for the one thread that drives the seek policy.
/// </para>
/// </summary>
internal sealed class StudioPreviewPosition
{
    private readonly object _gate = new();
    private long _frame;
    private long _requested;
    private int _requests;
    private int _taken;
    private bool _pending;
    private bool _arrivalIsNews;

    // The driving thread only.
    private long _clockStartedAt = long.MinValue;
    private long _played = -1;
    private long _framesFromBeforeStart;

    /// <summary>The frame reported now.</summary>
    public long Frame => Interlocked.Read(ref _frame);

    /// <summary>A requested frame has not been taken for the seek policy yet.</summary>
    public bool HasNewRequest
    {
        get
        {
            lock (_gate)
            {
                return _taken != _requests;
            }
        }
    }

    /// <summary>The reported frame has not been reached yet: the players are on their way to it.</summary>
    public bool IsPending
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    /// <summary>Frames that arrived as playback but had set out before the clock was started.</summary>
    public long FramesFromBeforeStart => Interlocked.Read(ref _framesFromBeforeStart);

    /// <summary>
    /// Asks for a frame, which is reported from now on. Returns whether that changed the reported frame.
    /// </summary>
    public bool Request(long frame)
    {
        lock (_gate)
        {
            _requested = frame;
            _requests++;
            _pending = true;
            _arrivalIsNews = true;
            return Report(frame);
        }
    }

    /// <summary>Gives out the newest requested frame, once. False when there is none that was not given out before.</summary>
    public bool TryTake(out long frame)
    {
        lock (_gate)
        {
            frame = _requested;
            if (_taken == _requests)
            {
                return false;
            }

            _taken = _requests;
            return true;
        }
    }

    /// <summary>The clock is about to be started. To be called before it is, with the time of the call.</summary>
    public void ClockStarted(long at) => _clockStartedAt = at;

    /// <summary>
    /// The screen's player handed over a frame while the clock was running, or had only just stopped.
    /// </summary>
    /// <param name="frame">The timeline frame it shows.</param>
    /// <param name="setOutAt">When the player began to hand it over, on the clock of <see cref="ClockStarted"/>.</param>
    public void Played(long frame, long setOutAt)
    {
        if (setOutAt < _clockStartedAt)
        {
            Interlocked.Increment(ref _framesFromBeforeStart);
            return;
        }

        _played = frame;
    }

    /// <summary>
    /// Playback has run into the end, and the seek policy is bringing the players to rest on
    /// <paramref name="frame"/>. It is reported from now on, unless a requested frame is still
    /// to be reached: that one comes first. Returns whether the reported frame changed.
    /// </summary>
    public bool Rest(long frame)
    {
        lock (_gate)
        {
            if (_pending)
            {
                return false;
            }

            _pending = true;
            _arrivalIsNews = false;
            return Report(frame);
        }
    }

    /// <summary>
    /// Takes in what the players have shown since the last call. To be called after the seek
    /// policy was pumped, with what that left behind.
    /// </summary>
    /// <param name="landings">The frames the players came to rest on, oldest first.</param>
    /// <param name="seeking">The seek policy still has a position to reach.</param>
    /// <returns>Whether there is news: the reported frame changed, or a requested frame has become the picture.</returns>
    public bool Update(List<StudioPreviewLanding> landings, bool seeking)
    {
        var played = _played;
        _played = -1;
        lock (_gate)
        {
            if (_pending)
            {
                // All of this is from before the reported frame or from the way there. The frame
                // is reached when the policy got the newest request and has nothing left to reach,
                // and then the players rest on what is reported already.
                _pending = seeking || _taken != _requests;
                return !_pending && _arrivalIsNews && landings.Count > 0;
            }

            var before = _frame;
            if (played >= 0)
            {
                Report(played);
            }

            // The players come to rest after the frames they played.
            for (var index = 0; index < landings.Count; index++)
            {
                Report(landings[index].Frame);
            }

            return _frame != before;
        }
    }

    private bool Report(long frame) => Interlocked.Exchange(ref _frame, frame) != frame;
}
