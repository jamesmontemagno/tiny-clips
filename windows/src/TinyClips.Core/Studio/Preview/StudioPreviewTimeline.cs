namespace TinyClips.Core.Studio.Preview;

/// <summary>Frame and time arithmetic of the preview. Pure: no media, no clock.</summary>
internal static class StudioPreviewTimeMath
{
    public const long TicksPerSecond = TimeSpan.TicksPerSecond;

    /// <summary>
    /// A time this fraction of a frame before a frame's start still counts as that frame. It
    /// absorbs rounding: a caller's <c>position + 1 / frameRate</c> can come out a hair before the
    /// next frame's start, and a file's sample time is rounded to 100 ns.
    /// </summary>
    public const double BoundaryTolerance = 1e-3;

    public static double NormalizeFrameRate(double frameRate) =>
        double.IsFinite(frameRate) && frameRate > 0 ? frameRate : 30;

    /// <summary>Whole frames in a clip, by the rule the exporter uses for its frame plan. At least 1.</summary>
    public static long FrameCount(double durationSeconds, double frameRate)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            return 1;
        }

        return Math.Max(1, (long)Math.Floor((durationSeconds * frameRate) + 1e-9));
    }

    /// <summary>The frame that contains <paramref name="seconds"/>. Not clamped; negative before the first frame.</summary>
    public static long FrameAt(double seconds, double frameRate) =>
        (long)Math.Floor((seconds * frameRate) + BoundaryTolerance);

    public static long ClampFrame(long frame, long frameCount) =>
        Math.Clamp(frame, 0, Math.Max(0, frameCount - 1));

    public static double FrameStart(long frame, double frameRate) => frame / frameRate;

    /// <summary>
    /// The middle of a frame. Every position given to a player is one of these: a position at a
    /// frame's start shows the previous frame for most frames and the right one for some.
    /// </summary>
    public static double FrameMiddle(long frame, double frameRate) => (frame + 0.5) / frameRate;

    public static long ToTicks(double seconds) =>
        (long)Math.Round(seconds * TicksPerSecond, MidpointRounding.AwayFromZero);

    public static double ToSeconds(long ticks) => ticks / (double)TicksPerSecond;
}

/// <summary>How one clip's frames sit on the recording's timeline.</summary>
/// <param name="FrameRate">The clip's own frame rate.</param>
/// <param name="FrameCount">Frames in the clip; at least 1.</param>
/// <param name="StartOffset">Timeline time of the clip's first frame, in seconds. May be negative.</param>
/// <param name="Duration">The clip's length in seconds as the project records it, which decides where it stops being shown.</param>
internal readonly record struct StudioPreviewClipTiming(double FrameRate, long FrameCount, double StartOffset, double Duration = double.PositiveInfinity)
{
    /// <summary>
    /// The frame the clip's player shows when the timeline clock is at
    /// <paramref name="timelineSeconds"/>. Outside the clip's range the player parks on its first
    /// or last frame, so the result is clamped.
    /// </summary>
    public long FrameAtTimeline(double timelineSeconds) =>
        StudioPreviewTimeMath.ClampFrame(StudioPreviewTimeMath.FrameAt(timelineSeconds - StartOffset, FrameRate), FrameCount);

    /// <summary>The frame that contains a position reported by the clip's own player.</summary>
    public long FrameAtPlayerTicks(long positionTicks) =>
        StudioPreviewTimeMath.ClampFrame(StudioPreviewTimeMath.FrameAt(StudioPreviewTimeMath.ToSeconds(positionTicks), FrameRate), FrameCount);

    /// <summary>
    /// The same, not clamped to the clip, and how far into that frame the position is. It is what
    /// the frames of playback are told apart by, where one frame more or less matters.
    /// </summary>
    public long NameAtPlayerTicks(long positionTicks, out double intoMilliseconds)
    {
        var frames = (StudioPreviewTimeMath.ToSeconds(positionTicks) * FrameRate) + StudioPreviewTimeMath.BoundaryTolerance;
        var frame = (long)Math.Floor(frames);
        intoMilliseconds = (frames - frame) * 1000.0 / FrameRate;
        return frame;
    }

    /// <summary>A frame number brought into the clip: its first frame at least, its last at most.</summary>
    public long ClampFrame(long frame) => StudioPreviewTimeMath.ClampFrame(frame, FrameCount);

    /// <summary>
    /// Whether the clip is part of the picture at a timeline time: the time lies inside the clip's
    /// own range. The same rule, with the same arithmetic, as the layout's for the camera
    /// (docs\studio-project-format.md, section 6.5), so the two never disagree about a frame.
    /// </summary>
    public bool IsShownAt(double timelineSeconds) =>
        timelineSeconds - StartOffset >= 0 && timelineSeconds - StartOffset <= Duration;
}

/// <summary>
/// The recording's frame grid, which is the screen clip's, and where each clip sits on it. Track 0
/// is the screen; track 1, when present, is the camera.
/// </summary>
internal sealed class StudioPreviewTimeline
{
    // How far apart the two frames of TryPickProofFrames are when the recording allows it, and
    // how far the second is looked for when the players do not all move over that distance.
    private const int ProofDistance = 8;
    private const int ProofSearchSpan = 90;

    private readonly StudioPreviewClipTiming[] _tracks;

    public StudioPreviewTimeline(double durationSeconds, double frameRate, IReadOnlyList<StudioPreviewClipTiming> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
        {
            throw new ArgumentException("A timeline needs at least the screen track.", nameof(tracks));
        }

        FrameRate = StudioPreviewTimeMath.NormalizeFrameRate(frameRate);
        Duration = double.IsFinite(durationSeconds) && durationSeconds > 0 ? durationSeconds : 0;
        FrameCount = StudioPreviewTimeMath.FrameCount(Duration, FrameRate);
        _tracks = [.. tracks];
    }

    public double Duration { get; }

    public double FrameRate { get; }

    public long FrameCount { get; }

    public long LastFrame => FrameCount - 1;

    public int TrackCount => _tracks.Length;

    public StudioPreviewClipTiming Track(int track) => _tracks[track];

    /// <summary>The frame a seek to <paramref name="sourceSeconds"/> shows: the one that contains it, clamped to the recording.</summary>
    public long FrameForSeek(double sourceSeconds)
    {
        if (double.IsNaN(sourceSeconds))
        {
            return 0;
        }

        if (double.IsPositiveInfinity(sourceSeconds))
        {
            return LastFrame;
        }

        if (double.IsNegativeInfinity(sourceSeconds))
        {
            return 0;
        }

        return StudioPreviewTimeMath.ClampFrame(StudioPreviewTimeMath.FrameAt(sourceSeconds, FrameRate), FrameCount);
    }

    /// <summary>The start time of a frame: what the preview reports as its position.</summary>
    public double FrameStart(long frame) => StudioPreviewTimeMath.FrameStart(frame, FrameRate);

    public double FrameMiddle(long frame) => StudioPreviewTimeMath.FrameMiddle(frame, FrameRate);

    /// <summary>The clock position that shows <paramref name="frame"/>: its middle, in 100 ns ticks.</summary>
    public long MiddleTicks(long frame) => StudioPreviewTimeMath.ToTicks(FrameMiddle(frame));

    /// <summary>
    /// The frame a track's player shows while the clock sits in the middle of
    /// <paramref name="timelineFrame"/>. Worked out in the players' own units, clock ticks plus the
    /// clip's offset in ticks, so it is exactly the frame the player's reported position names.
    /// </summary>
    public long PlayerFrame(int track, long timelineFrame) =>
        _tracks[track].FrameAtPlayerTicks(MiddleTicks(timelineFrame) - StudioPreviewTimeMath.ToTicks(_tracks[track].StartOffset));

    /// <summary>
    /// Whether a track is part of the picture of <paramref name="timelineFrame"/>. The screen always
    /// is. A camera outside its own time range is not drawn, whatever frame its player is parked on.
    /// </summary>
    public bool IsShown(int track, long timelineFrame) => track == 0 || _tracks[track].IsShownAt(FrameMiddle(timelineFrame));

    /// <summary>
    /// Whether a track's player, with the clock at <paramref name="timelineSeconds"/>, is somewhere
    /// inside its own stream rather than parked before its first frame or after its last. A parked
    /// player does not move when the clock moves between two times on the same side of its stream.
    /// </summary>
    public bool IsPlayerInside(int track, double timelineSeconds)
    {
        var timing = _tracks[track];
        var own = timelineSeconds - timing.StartOffset;
        return own >= 0 && own < timing.FrameCount / timing.FrameRate;
    }

    /// <summary>
    /// Whether a track's player, with the clock at <paramref name="timelineSeconds"/>, is on the
    /// last frame of its stream or parked after it. Either way it has read its stream to the end.
    /// </summary>
    public bool IsPlayerAtEnd(int track, double timelineSeconds)
    {
        var timing = _tracks[track];
        return timelineSeconds - timing.StartOffset >= (timing.FrameCount - 1) / timing.FrameRate;
    }

    /// <summary>
    /// Two frames to send the players back and forth between, to see whether each of them shows
    /// the same picture every time it comes back to <paramref name="first"/> (see
    /// <see cref="StudioPreviewProof"/>). <paramref name="first"/> is the earliest frame other than
    /// <paramref name="avoid"/> at which every track is part of the picture and no player is at
    /// the end of its stream; <paramref name="second"/> is a frame of the same kind a few frames
    /// away, on which every one of those players shows another frame. A recording that has no such
    /// pair gives the nearest thing: frames that show the screen at least. False when the
    /// recording has no two frames at all.
    /// </summary>
    /// <param name="avoid">The frame the players are wanted on afterwards, so that going there moves them.</param>
    public bool TryPickProofFrames(long avoid, out long first, out long second)
    {
        return TryPickProofFrames(avoid, everyTrack: true, insideStreams: true, out first, out second)
            || TryPickProofFrames(avoid, everyTrack: false, insideStreams: true, out first, out second)
            || TryPickProofFrames(avoid, everyTrack: false, insideStreams: false, out first, out second);
    }

    private bool TryPickProofFrames(long avoid, bool everyTrack, bool insideStreams, out long first, out long second)
    {
        first = -1;
        second = -1;
        for (long frame = 0; frame <= LastFrame; frame++)
        {
            if (frame != avoid && IsProofFrame(frame, everyTrack, insideStreams))
            {
                first = frame;
                break;
            }
        }

        if (first < 0)
        {
            return false;
        }

        // The usual distance first, then nearer, then farther.
        for (var step = 0; step < ProofDistance + ProofSearchSpan; step++)
        {
            var distance = step < ProofDistance ? ProofDistance - step : step + 1;
            foreach (var candidate in (ReadOnlySpan<long>)[first + distance, first - distance])
            {
                if (candidate >= 0 && candidate <= LastFrame && IsProofFrame(candidate, everyTrack, insideStreams) && MovesEveryPlayer(first, candidate))
                {
                    second = candidate;
                    return true;
                }
            }
        }

        first = -1;
        return false;
    }

    private bool IsProofFrame(long frame, bool everyTrack, bool insideStreams)
    {
        for (var track = 0; track < _tracks.Length; track++)
        {
            if (!IsShown(track, frame))
            {
                if (everyTrack)
                {
                    return false;
                }
            }
            else if (insideStreams && IsPlayerAtEnd(track, FrameMiddle(frame)))
            {
                return false;
            }
        }

        return true;
    }

    private bool MovesEveryPlayer(long from, long to)
    {
        for (var track = 0; track < _tracks.Length; track++)
        {
            if (IsShown(track, from) && PlayerFrame(track, from) == PlayerFrame(track, to))
            {
                return false;
            }
        }

        return true;
    }
}
