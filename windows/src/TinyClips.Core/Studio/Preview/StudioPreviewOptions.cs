namespace TinyClips.Core.Studio.Preview;

/// <summary>Switches for the unit tests and the check tool. The app runs with the defaults.</summary>
internal sealed record StudioPreviewOptions
{
    /// <summary>
    /// Keeps every player muted, and at volume zero, whatever the project says. For automated
    /// checks on a machine where sound is not acceptable.
    /// </summary>
    public bool ForceMuted { get; init; }

    /// <summary>
    /// Keeps every player at volume zero and lets <c>IsMuted</c> follow the project. For the one
    /// check that has to see the project's mute flag arrive at a player.
    /// </summary>
    public bool ZeroVolume { get; init; }

    /// <summary>
    /// Draws on WARP, Direct3D's software device, which is where the engine draws on a PC without
    /// usable graphics hardware. For the check that the preview works there. On a PC that has
    /// graphics hardware this is not the same as having none: the players start on the hardware
    /// and move to the software adapter when their first frames are taken.
    /// </summary>
    public bool SoftwareDevice { get; init; }

    /// <summary>
    /// Believes what the players hand over first: takes each player's first frame the moment it
    /// has one, and shows what the players hold after the first position they are given. For the
    /// comparison that shows what that costs. On the graphics hardware: a blank screen picture
    /// drawn for a moment after about one open in six of a project whose camera does not start
    /// late (92 of 520). On the software adapter of a PC that has graphics hardware: previews that
    /// fail to open, and first pictures that are blank or a frame off.
    /// </summary>
    public bool TrustFirstFrames { get; init; }

    /// <summary>
    /// Makes this many attempts to open see a lost graphics device, starting with the first. 1 is
    /// a device lost while the preview opens; 2 is one lost again while it opens once more.
    /// </summary>
    public int DevicesLostWhileOpening { get; init; }

    /// <summary>
    /// Makes this many attempts to open see a player fail once every player has handed over its
    /// first frame, starting with the first attempt. 1 is a player that fails while the preview
    /// opens; 2 is one that fails again while it opens once more.
    /// </summary>
    public int PlayersFailedWhileOpening { get; init; }

    /// <summary>
    /// Makes the engine deaf to a player that offers its first frame again once the frames may be
    /// taken, so that the engine has to take every first frame itself. Only where the first frames
    /// are held back, on the software adapter. For the check of that way in: where it was
    /// measured, no player has ever made it necessary.
    /// </summary>
    public bool PlayersKeepFirstFrames { get; init; }

    public StudioPreviewSeekSettings Seek { get; init; } = new();

    /// <summary>
    /// Receives a line for everything the players report to the render thread and everything the
    /// engine asks of them, from the moment the engine is created. It explains a check that did
    /// not hold. Called from the render thread and from callers of <c>Seek</c> and <c>Pause</c>,
    /// so it has to be thread-safe and quick. Nothing is formatted while it is null.
    /// </summary>
    public Action<string>? Trace { get; init; }
}

internal static class StudioPreviewAudio
{
    /// <summary>
    /// Whether a player is muted. The camera clip has no sound of its own and stays muted; the
    /// screen clip follows the project.
    /// </summary>
    public static bool IsMuted(bool isCamera, bool projectMuted, bool forceMuted) =>
        isCamera || forceMuted || projectMuted;
}

/// <summary>Counters and states of a preview engine, for the check tool and for logging.</summary>
internal sealed record StudioPreviewDiagnostics
{
    /// <summary>Frames each player has delivered into its texture: screen, then camera.</summary>
    public long[] FramesCopied { get; init; } = [];

    /// <summary><c>VideoFrameAvailable</c> callbacks that have started, per clip.</summary>
    public long[] CallbacksStarted { get; init; } = [];

    /// <summary>
    /// The most <c>VideoFrameAvailable</c> callbacks of one player that were ever under way at the
    /// same time, per clip. 1 means a player's frames reach the render thread in the order the
    /// player made them.
    /// </summary>
    public int[] MostDeliveriesAtOnce { get; init; } = [];

    /// <summary>Stopwatch timestamp of each clip's latest callback start.</summary>
    public long[] LastCallbackStartedAt { get; init; } = [];

    public long[] CopyFailures { get; init; } = [];

    /// <summary><c>SeekCompleted</c> events each player has raised, and the Stopwatch timestamp of the latest.</summary>
    public long[] SeeksCompleted { get; init; } = [];

    public long[] LastSeekCompletedAt { get; init; } = [];

    /// <summary><c>MediaEnded</c> events each player has raised.</summary>
    public long[] EndsReached { get; init; } = [];

    /// <summary>Size of the texture each player copies into.</summary>
    public (int Width, int Height)[] CopyTargets { get; init; } = [];

    /// <summary>How often a copy target was replaced by one of another size.</summary>
    public long CopyTargetChanges { get; init; }

    /// <summary><c>IsMuted</c> of each player, read from the player.</summary>
    public bool[] PlayerMuted { get; init; } = [];

    /// <summary><c>Volume</c> of each player, read from the player.</summary>
    public double[] PlayerVolume { get; init; } = [];

    /// <summary>What the project asked for the last time it was applied.</summary>
    public bool ProjectMuted { get; init; }

    public long SeeksIssued { get; init; }

    public long StepsIssued { get; init; }

    public long StepFallbacks { get; init; }

    /// <summary>
    /// Frames one ahead that were reached with a seek, because a stepped player had not yet drawn
    /// its frame again after the clock was brought along.
    /// </summary>
    public long StepsAvoided { get; init; }

    public long Repairs { get; init; }

    public long LossesSeenEarly { get; init; }

    public long RepairFailures { get; init; }

    /// <summary>Detours made because a player had read its stream to the end.</summary>
    public long EndRecoveries { get; init; }

    /// <summary>Frames that arrived during a position change and reported another position: late answers to an earlier change.</summary>
    public long StrayFrames { get; init; }

    /// <summary>Position changes that were given up waiting for a player's answer, at the limit.</summary>
    public long AnswersGivenUp { get; init; }

    /// <summary>Positions that do not count (after the end of a stream, after an offset change) for which a player handed over no frame.</summary>
    public long DetoursUnanswered { get; init; }

    /// <summary>
    /// Times a player handed over a second frame for the first position change after the clock
    /// ran. The first of two can be the frame it had ready for playback when the clock stopped,
    /// with the new position on it.
    /// </summary>
    public long SecondAnswers { get; init; }

    /// <summary>Scenes drawn into a surface.</summary>
    public long FramesDrawn { get; init; }

    /// <summary>Frames that set out after <c>Pause</c> had stopped the clock and were drawn all the same. Should stay 0.</summary>
    public long FramesAfterPause { get; init; }

    /// <summary>Frames a player handed over after <c>Pause</c> had stopped the clock, which were left out of the picture.</summary>
    public long LateFramesDiscarded { get; init; }

    /// <summary>
    /// Frames of the screen clip that arrived while playing but had set out before the clock was
    /// started. They report a position from before playback began and are not taken for it.
    /// </summary>
    public long FramesFromBeforeStart { get; init; }

    /// <summary>A position that was asked for has not been reached yet; it is what <c>Position</c> reports.</summary>
    public bool PositionPending { get; init; }

    public int DeviceRebuilds { get; init; }

    /// <summary>Timeline frame the players rest on, or -1.</summary>
    public long SettledFrame { get; init; }

    /// <summary>The frame each player delivered last.</summary>
    public long[] ShownFrames { get; init; } = [];

    /// <summary>The shared clock's state and position, read from the controller.</summary>
    public string ClockState { get; init; } = string.Empty;

    public double ClockSeconds { get; init; }

    /// <summary>Each player's own position and playback state, read from its session.</summary>
    public double[] PlayerSeconds { get; init; } = [];

    public string[] PlayerStates { get; init; } = [];

    /// <summary>Milliseconds the last close waited for the media files to be released, or -1 before a close.</summary>
    public double FilesClosedAfterMilliseconds { get; init; } = -1;

    /// <summary>False when the last close gave up waiting for a file.</summary>
    public bool FilesClosed { get; init; } = true;

    /// <summary>
    /// The media files the last close waited for: the ones in the project folder. A screen
    /// recording outside it is the user's own file and is not waited for.
    /// </summary>
    public int FilesWaitedFor { get; init; }

    /// <summary>
    /// The attempt that opened this preview: 1, or 2 when the first failed in a way that may
    /// pass (a graphics device lost, or a player that failed after every player had handed over
    /// a frame).
    /// </summary>
    public int OpenAttempts { get; init; } = 1;

    /// <summary>
    /// Players whose first frame the engine looked at: all of them on the software adapter, none
    /// on the graphics hardware, where the players are taken to be on the engine's adapter.
    /// </summary>
    public int FirstFramesLookedAt { get; init; }

    /// <summary>
    /// Of those, the players whose first frame left nothing in its texture: they started on
    /// another graphics adapter than the engine's device and moved over. 0 where both are on the
    /// same adapter.
    /// </summary>
    public int FirstFramesEmpty { get; init; }

    /// <summary>
    /// First frames the engine took from a player because the player did not hand it over by
    /// itself once the frames could be taken. Should stay 0: a player announces a frame nobody
    /// took again and again.
    /// </summary>
    public int FirstFramesPulled { get; init; }

    /// <summary>
    /// Rounds it took, while opening, until the players showed the same pictures twice in a row
    /// (see <see cref="StudioPreviewProof"/>). 0 when no player had moved, so there was nothing to prove.
    /// </summary>
    public int ProofRounds { get; init; }

    /// <summary>False when the players had moved and did not show the same pictures twice in a row within the limit.</summary>
    public bool ProofHeld { get; init; } = true;
}
