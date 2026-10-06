using Vortice.Direct3D11;

namespace TinyClips.Core.Studio.Preview;

/// <summary>When a player handed a frame over, as far as the clock is concerned.</summary>
internal enum StudioPreviewHandOverKind : byte
{
    /// <summary>While the clock ran. Which frame it is, the clip's namer says.</summary>
    Playback,

    /// <summary>While the clock stood: the answer to a position the player was given. It is the frame its position names.</summary>
    Answer,

    /// <summary>
    /// After the clock was stopped, by <c>Pause</c> or by the engine to go somewhere, and before
    /// the players were asked for anything. Left out of the picture.
    /// </summary>
    Late,
}

/// <summary>Tells the three kinds of hand-over apart, from when it set out and what the clock was doing.</summary>
internal static class StudioPreviewHandOverKinds
{
    /// <summary>What stands for a moment that has not come: the clock has not been started, or has not been stopped since.</summary>
    public const long Never = long.MaxValue;

    /// <summary>
    /// A hand-over is one of playback when it set out while the clock ran: at or after the moment
    /// the clock was started, and before the moment it was stopped.
    /// <para>
    /// Before, not after, the moment the engine decided to stop it. The clock the players share
    /// stops at once, and its position is what a player reports. A player can go on by itself
    /// for a moment longer, and hands over the frame that comes due in that moment as if nothing
    /// had happened: the frame after the one the stopped position names. Nothing of what the
    /// frame namer goes by holds for such a frame, so it is no frame of playback.
    /// </para>
    /// <para>
    /// Measured: with nothing in the way a player handed a frame over after the stop in 2 of 80
    /// pauses, within 0.6 ms, and it was the frame its position named. With every processor
    /// busy, in 28 of 300 pauses, up to 15 ms after the stop, and 6 of those 32 frames were the
    /// frame after the one the stopped position named.
    /// </para>
    /// </summary>
    /// <param name="startedAt">When the hand-over set out.</param>
    /// <param name="clockRunningSince">When the clock was last started, or <see cref="Never"/>.</param>
    /// <param name="clockStoppedAt">
    /// When it was decided to stop the clock, or <see cref="Never"/> while it runs. To be read
    /// before <paramref name="stopLeavesOut"/>.
    /// </param>
    /// <param name="stopLeavesOut">
    /// What sets out after the stop is left out: true from just before the clock is stopped
    /// until the players are asked for something. To be read after <paramref name="clockStoppedAt"/>.
    /// </param>
    public static StudioPreviewHandOverKind Of(long startedAt, long clockRunningSince, long clockStoppedAt, bool stopLeavesOut)
    {
        if (stopLeavesOut && clockStoppedAt == Never)
        {
            // The clock is being stopped right now: it has been said that frames are left out,
            // and not yet from when. A hand-over that set out since the clock started may have
            // set out before that moment or after it, and counts as after. Leaving a frame of
            // playback out here costs nothing; taking a late one in gives it a wrong number.
            return startedAt >= clockRunningSince ? StudioPreviewHandOverKind.Late : StudioPreviewHandOverKind.Answer;
        }

        if (startedAt >= clockRunningSince && startedAt < clockStoppedAt)
        {
            return StudioPreviewHandOverKind.Playback;
        }

        return stopLeavesOut && startedAt >= clockStoppedAt ? StudioPreviewHandOverKind.Late : StudioPreviewHandOverKind.Answer;
    }
}

/// <summary>
/// One frame a player handed over and what the engine made of it, for the checks that read from
/// the pixels which frame a copy really holds. Given to <c>StudioPreviewEngine.AfterCopy</c> with
/// the device lock held, right after the copy.
/// </summary>
/// <param name="Clip">0 for the screen, 1 for the camera.</param>
/// <param name="Serial">Which copy of the clip it is.</param>
/// <param name="Texture">The clip's copy target, which holds the frame now.</param>
/// <param name="StartedAt">Stopwatch timestamp of the beginning of the hand-over.</param>
/// <param name="PositionTicks">The player's position then, in its own time.</param>
/// <param name="IntoMilliseconds">How far into the frame it names that position was.</param>
/// <param name="CopyBeganAt">Stopwatch timestamp just before the player's copy.</param>
/// <param name="CopiedAt">Stopwatch timestamp just after it.</param>
/// <param name="PositionAfterTicks">The player's position after the copy; for a frame of playback only.</param>
/// <param name="CollectorBefore">How long the garbage collector had held the process up in all, in milliseconds, read after the position.</param>
/// <param name="CollectorAfter">The same after the copy; for a frame of playback only.</param>
/// <param name="NameByPosition">The frame of the clip that contains <paramref name="PositionTicks"/>, not clamped.</param>
/// <param name="Knowledge">What the namer made of a frame of playback. <see cref="StudioPreviewFrameKnowledge.Known"/> for an answer, which is the frame its position names.</param>
/// <param name="Frame">The number the frame was given, clamped to the clip; for a frame that got none, the latest it can be.</param>
/// <param name="Rule">Which rule gave the number, or why there is none.</param>
/// <param name="EarlierSerial">The copy before this one, when this hand-over gave it its number; otherwise 0.</param>
/// <param name="EarlierFrame">The number that copy got.</param>
/// <param name="TimelineFrame">
/// For the screen, the timeline frame a frame of that number plays under: the number itself on
/// the grid, and the frame of the timeline it belongs to where the clip counts in frames of
/// its file. For an answer it says where such a frame would play, not where the clock was put.
/// -1 for the camera.
/// </param>
/// <param name="EarlierTimelineFrame">The same for <paramref name="EarlierFrame"/>.</param>
internal readonly record struct StudioPreviewHandOver(
    int Clip,
    long Serial,
    ID3D11Texture2D Texture,
    int Width,
    int Height,
    StudioPreviewHandOverKind Kind,
    long StartedAt,
    long PositionTicks,
    double IntoMilliseconds,
    long CopyBeganAt,
    long CopiedAt,
    long PositionAfterTicks,
    double CollectorBefore,
    double CollectorAfter,
    long NameByPosition,
    StudioPreviewFrameKnowledge Knowledge,
    long Frame,
    string? Rule,
    long EarlierSerial,
    long EarlierFrame,
    long TimelineFrame,
    long EarlierTimelineFrame);

/// <summary>
/// Whether a picture can be drawn without knowing which frame it is: it can when the scene comes
/// out the same for every frame it could be.
/// </summary>
internal static class StudioPreviewStillness
{
    /// <summary>The longest stretch of frames looked at. A picture that could be any of more frames than this is not drawn.</summary>
    public const int MostFrames = 12;

    // What counts as the same place: a hundredth of a pixel, and as much of a clip. A zoom that
    // follows a pointer at rest works its place out anew for every frame, and comes out a hair
    // beside itself.
    private const double PixelTolerance = 0.01;
    private const double SourceTolerance = 0.00001;
    private const double ShareTolerance = 0.0001;

    /// <summary>
    /// True when the layout of every frame from <paramref name="first"/> to <paramref name="last"/>
    /// is that of the first: the rectangles of screen and camera, the parts of them that are
    /// shown, their shapes, shadows and opacities; and nothing is drawn over the screen that
    /// changes from frame to frame. A zoom that moves, a scene that is being entered, a zoom
    /// that follows the pointer while the pointer moves, the frame at which the camera appears
    /// or goes, and the ring of a click while it shows are not.
    /// </summary>
    /// <param name="frameRate">Frames a second of the timeline the frame numbers count in.</param>
    public static bool SameLayout(StudioProject project, StudioEvents? events, double frameRate, long first, long last, double canvasWidth, double canvasHeight)
    {
        if (first < 0 || last < first || last - first > MostFrames)
        {
            return false;
        }

        // The ring of a click grows and fades from one frame to the next for as long as it shows.
        if (project.Overlays.Clicks is { Enabled: true, Duration: > 0, Opacity: > 0 } rings && events?.Clicks is { Length: > 0 } clicks)
        {
            var from = StudioPreviewTimeMath.FrameMiddle(first, frameRate);
            var to = StudioPreviewTimeMath.FrameMiddle(last, frameRate);
            foreach (var click in clicks)
            {
                if (click.T <= to && click.T + rings.Duration >= from)
                {
                    return false;
                }
            }
        }

        var plan = StudioLayoutPlan.Create(project, events);
        var reference = Layout(first);
        for (var frame = first + 1; frame <= last; frame++)
        {
            if (!Same(reference, Layout(frame)))
            {
                return false;
            }
        }

        return true;

        StudioResolvedFrame Layout(long frame) =>
            plan.Resolve(StudioPreviewTimeMath.FrameMiddle(frame, frameRate), canvasWidth, canvasHeight);
    }

    private static bool Same(in StudioResolvedFrame a, in StudioResolvedFrame b)
    {
        if (a.SceneIndex != b.SceneIndex || a.Layout != b.Layout || a.Screen.HasValue != b.Screen.HasValue || a.Camera.HasValue != b.Camera.HasValue)
        {
            return false;
        }

        if (a.Screen is { } screen && b.Screen is { } otherScreen
            && !(Same(screen.Rect, otherScreen.Rect, PixelTolerance)
                && Same(screen.Source, otherScreen.Source, SourceTolerance)
                && Near(screen.CornerRadius, otherScreen.CornerRadius, PixelTolerance)
                && Same(screen.Shadow, otherScreen.Shadow)
                && Near(screen.Opacity, otherScreen.Opacity, ShareTolerance)))
        {
            return false;
        }

        // Which camera frame goes with a frame changes from one to the next, and is no part of
        // how the scene is laid out.
        return a.Camera is not { } camera || b.Camera is not { } otherCamera
            || (camera.Shape == otherCamera.Shape
                && camera.Mirror == otherCamera.Mirror
                && camera.Visible == otherCamera.Visible
                && Same(camera.Rect, otherCamera.Rect, PixelTolerance)
                && Same(camera.Source, otherCamera.Source, SourceTolerance)
                && Near(camera.CornerRadius, otherCamera.CornerRadius, PixelTolerance)
                && Near(camera.BorderWidth, otherCamera.BorderWidth, PixelTolerance)
                && Same(camera.Shadow, otherCamera.Shadow)
                && Near(camera.Opacity, otherCamera.Opacity, ShareTolerance));
    }

    private static bool Same(StudioFrameRect a, StudioFrameRect b, double tolerance) =>
        Near(a.X, b.X, tolerance) && Near(a.Y, b.Y, tolerance) && Near(a.Width, b.Width, tolerance) && Near(a.Height, b.Height, tolerance);

    private static bool Same(StudioResolvedShadow a, StudioResolvedShadow b) =>
        Near(a.Blur, b.Blur, PixelTolerance) && Near(a.OffsetY, b.OffsetY, PixelTolerance) && Near(a.Opacity, b.Opacity, ShareTolerance);

    // Not a number is the same as nothing else, and neither is anything further away than allowed.
    private static bool Near(double a, double b, double tolerance) => Math.Abs(a - b) <= tolerance;
}
