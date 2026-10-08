using TinyClips.Core.Studio.Rendering;

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
    /// check that has to see the project's mute flag arrive at a player. The project's volume
    /// does not reach a player then either.
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
    /// Makes this many attempts to open see a player stop decoding before any player has handed
    /// over a frame, starting with the first attempt. 1 is a player that does so while the
    /// preview opens; 2 is one that does so again while it opens once more.
    /// </summary>
    public int PlayersStopDecodingWhileOpening { get; init; }

    /// <summary>
    /// How long opening waits before its second attempt (<see cref="StudioPreviewOpenFailure"/>).
    /// What makes an open fail and then passes has been seen to last between a third of a second
    /// and something over half a second: a second attempt made at once failed as the first had,
    /// and the preview opened after it was fine.
    /// </summary>
    public TimeSpan SecondAttemptWait { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Makes the engine deaf to a player that offers its first frame again once the frames may be
    /// taken, so that the engine has to take every first frame itself. Only where the first frames
    /// are held back, on the software adapter. For the check of that way in: where it was
    /// measured, no player has ever made it necessary.
    /// </summary>
    public bool PlayersKeepFirstFrames { get; init; }

    public StudioPreviewSeekSettings Seek { get; init; } = new();

    /// <summary>
    /// How the frames of playback are told apart. Its <c>BelievePositions</c> brings back what the
    /// engine did before: every frame taken for the one its player's position names. For the
    /// checks that show what that does when the process is held up.
    /// </summary>
    public StudioPreviewNamingSettings Naming { get; init; } = new();

    /// <summary>
    /// Reads when each frame of each clip begins from its file's index while the preview opens
    /// (<see cref="StudioPreviewFrameTimes"/>), and counts in frames of the file: a frame handed
    /// over is the one whose time the position has reached, the next frame is the next one of
    /// the file, and the timeline frame it is shown under is the one the export shows it in.
    /// For a recording, whose frames sit some milliseconds into their slots and leave a slot
    /// empty where the recorder missed a tick. A file whose index cannot be read that way is
    /// played as before. Off, every clip is taken to have one frame at the start of every slot
    /// of its frame rate, which is so for the check tool's clips and not for a recording.
    /// <para>
    /// Off for now: it has been unit tested and no player has played with it yet. The checks
    /// that will say whether it is to be the way the app runs are listed in
    /// windows\docs\studio-preview.md.
    /// </para>
    /// </summary>
    public bool FrameTimesFromFile { get; init; }

    /// <summary>
    /// Tells the renderer which picture each frame it is given is
    /// (<see cref="StudioGpuVideoFrame.Stamp"/>): the count of pictures that have been put into
    /// the clip's textures, which changes exactly when the picture does and is never 0. The
    /// renderer then finds the people in a camera picture whose background is blurred or
    /// removed once for each picture, however often the scene is drawn again. Off, every frame
    /// is given with stamp 0, which says nothing: the renderer looks at the camera picture
    /// afresh at every draw, as it always has, also at every redraw of a paused preview.
    /// <para>
    /// Off for now. The app ships the model that finds people, so this decides what the
    /// preview of such a camera shows, and nothing has run with it yet: a stamp that stayed
    /// while the picture changed would show the camera picture the renderer made when it last
    /// looked, which is an old frame. <c>StudioPreviewCheck --only people</c> switches it on
    /// and decides, with the faults made against it; <c>--people</c> switches it on for the
    /// other groups. Where it stands is in windows\docs\studio-preview.md.
    /// </para>
    /// </summary>
    public bool StampPictures { get; init; }

    /// <summary>
    /// Makes what finds the people in a camera picture for the engine's renderer, in place of
    /// the finder that runs the app's model (<see cref="StudioPersonFinders.CreateDefault"/>).
    /// For the checks of a camera whose background is blurred or removed: a stand-in that
    /// counts how often it is asked and takes the time it is told to. The renderer asks for it
    /// when it first has such a camera picture to draw; the engine makes a renderer when it
    /// opens and another after a lost device.
    /// </summary>
    public Func<IStudioPersonFinder?>? PersonFinderFactory { get; init; }

    /// <summary>
    /// Called by the render thread before it draws a scene, with the device lock held; the thread
    /// then sleeps for the time returned, still holding the lock. It stands for a draw that takes
    /// long, which keeps the players waiting with their frames. For the checks of that.
    /// </summary>
    public Func<TimeSpan>? RenderDelay { get; init; }

    /// <summary>
    /// Called by the thread that stops the clock, right after it has told the clock to stop; the
    /// thread then sleeps for the time returned before it goes on. It stands for a thread that is
    /// kept from going on just then, as on a PC whose processors are all busy, while a player
    /// still hands over the frame it had coming. For the checks of that.
    /// </summary>
    public Func<TimeSpan>? StopDelay { get; init; }

    /// <summary>
    /// Called by a thread inside <c>Pause()</c>, once it has stopped the clock and waited for the
    /// render thread, and before it shuts the picture to the frames of the playback it ended;
    /// the thread then sleeps for the time returned. It stands for a thread that is kept from
    /// going on just there, while <c>Play()</c> comes from another thread and the render thread
    /// starts the clock again. For the check of that.
    /// </summary>
    public Func<TimeSpan>? PauseDelay { get; init; }

    /// <summary>
    /// Notes when the clock stopped only once the clock has been told and the thread has got on,
    /// as the engine did before. A frame a player hands over in between is then taken for a frame
    /// of playback, with the position the clock stopped on, which is the position of the frame
    /// before it. For the check that shows what that does; it takes <see cref="StopDelay"/>, or
    /// a PC that is busy, for the time in between to be long enough to matter.
    /// </summary>
    public bool StopNotedLate { get; init; }

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

    /// <summary>
    /// A player's volume. The screen clip plays at the project's volume
    /// (<see cref="StudioSound.Volume(double)"/>), which is what the export multiplies its
    /// samples by. A check that must not be heard keeps every player at zero
    /// (<see cref="StudioPreviewOptions.ForceMuted"/>, <see cref="StudioPreviewOptions.ZeroVolume"/>),
    /// whatever the project says, and the camera's player, which is never heard, is at zero too.
    /// </summary>
    /// <param name="zeroVolume">Either of the two options is set.</param>
    public static double Volume(bool isCamera, double projectVolume, bool zeroVolume) =>
        isCamera || zeroVolume ? 0 : StudioSound.Volume(projectVolume);
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

    /// <summary>
    /// The project's volume the last time it was applied, between 0 and 1. A player's own
    /// <see cref="PlayerVolume"/> is this for the screen clip, and 0 where a check keeps the
    /// players at zero.
    /// </summary>
    public double ProjectVolume { get; init; } = 1;

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

    /// <summary>
    /// Times the clock stopped on a frame whose number was not certain or rested on an inference
    /// (<see cref="FramesShownUnsure"/>, <see cref="FramesInferred"/>), and the frame was fetched
    /// anew. None while nothing holds the process up.
    /// </summary>
    public long RestsFetchedAnew { get; init; }

    /// <summary>Scenes drawn into a surface.</summary>
    public long FramesDrawn { get; init; }

    /// <summary>Frames that set out after the clock had been stopped and were drawn all the same. Stays 0: such a frame is left out.</summary>
    public long FramesAfterPause { get; init; }

    /// <summary>
    /// Frames of playback that were left out of the picture because of <c>Pause</c>: a player
    /// handed them over after the clock had stopped, or they had not reached the picture when
    /// <c>Pause</c> returned.
    /// </summary>
    public long LateFramesDiscarded { get; init; }

    /// <summary>
    /// Frames of playback of which it could not be told which frame they were when they were
    /// handed over, per clip. None while nothing holds the process up.
    /// </summary>
    public long[] FramesWithoutNumber { get; init; } = [];

    /// <summary>
    /// Of those, the ones that were shown all the same, without a number, because the scene came
    /// out the same whichever frame they were. The others were not shown, unless the frame after
    /// them gave them their number (<see cref="FramesNumberedLate"/>).
    /// </summary>
    public long[] FramesShownWithoutNumber { get; init; } = [];

    /// <summary>Of the frames that got their number, the ones that got it from the frame after them and were shown a moment late, per clip.</summary>
    public long[] FramesNumberedLate { get; init; } = [];

    /// <summary>
    /// Frames shown under the number their position gave although that was not certain, because
    /// the frames had been without a number for too long, per clip. Should stay 0 on a PC that
    /// keeps up with the recording.
    /// </summary>
    public long[] FramesShownUnsure { get; init; } = [];

    /// <summary>
    /// Frames whose number rested on an inference about the player, per clip: drawn and reported
    /// as any other, and fetched anew when the clock stopped on one. None while nothing holds
    /// the process up.
    /// </summary>
    public long[] FramesInferred { get; init; } = [];

    /// <summary>Frames that had their number and were written over by a later frame before the render thread took them, per clip.</summary>
    public long[] FramesPassedOver { get; init; } = [];

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

    /// <summary>The timeline frame the screen's picture is shown under, or -1 while it has none: the frame the scene is drawn for.</summary>
    public long ShownTimelineFrame { get; init; } = -1;

    /// <summary>
    /// What each clip's frame numbers count, per clip: "the grid" with the reason, or what the
    /// file's index said about its frames (<see cref="StudioPreviewOptions.FrameTimesFromFile"/>).
    /// </summary>
    public string[] FrameTimes { get; init; } = [];

    /// <summary>Whether each clip's frame numbers count frames of its file. False on the grid.</summary>
    public bool[] CountsFileFrames { get; init; } = [];

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
