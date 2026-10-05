using TinyClips.Core.Studio.Preview;

namespace TinyClips.Core.Tests;

/// <summary>
/// What the frame namer makes of a recording as the app writes one, played by a player that keeps
/// up with nothing holding the process up.
/// <para>
/// The recorder stamps a screen frame with the wall clock a moment after its pacer's tick
/// (<c>GpuCaptureSession</c>). So the frames of a recording do not sit at the start of their
/// thirtieth of a second, as the frames of the check tools' clips do: they are so many
/// milliseconds into it, by an amount that differs from one recording to the next and a little
/// from frame to frame, and a tick the recorder misses leaves its slot empty.
/// </para>
/// <para>
/// This is a model and not a measurement. The player is the one the namer's own comment
/// describes: it looks for a frame every hundredth of a second, hands over one at a time and
/// never before its time, and the position it reports is the clock's. No such file has been
/// played by the engine yet. The tests that are skipped say what the namer does not do for such
/// a file, by this model; they are the ones to make pass, or to prove wrong with a real player.
/// </para>
/// </summary>
public sealed class StudioPreviewFrameNamerRecordingTests
{
    // Millionths of a second.
    private const long TicksPerSecond = 1_000_000;
    private const double LookMilliseconds = 10;
    private const double LatencyMilliseconds = 0.5;
    private const double CopyMilliseconds = 3;
    private const double TailMilliseconds = 0.3;
    private const double JitterMilliseconds = 2;
    private const int Slots = 600;
    private const int EmptySlot = 60;

    private const string AfterAMissingFrame =
        "Known, by this model: the frame after an empty slot has no number, and the rule that gives the numbers back asks for a frame "
        + "within 13 ms of the start of its slot. Where the frames sit later than that, none has a number for half a second (15 frames "
        + "at 30 a second, 30 at 60), and the rest of the play is shown by position and called unsure. See \"Where each platform "
        + "stands\" in plans/video-studio-plan.md.";

    private const string LateInTheSlot =
        "Known, by this model: where the frames sit within a look of the end of their slot, a hand-over begins now in the frame's own "
        + "slot and now in the next, the numbers skip, and up to a third of the frames get theirs one hand-over late, which is a frame "
        + "not drawn on time while the layout moves. See \"Where each platform stands\" in plans/video-studio-plan.md.";

    private const string NotTheFrameAtRest =
        "Known, and older than the namer: a frame that sits past the middle of its slot plays under that slot's number, while a pause, "
        + "a seek and the export show the frame before it there, because they look at the middle of the slot. See \"Where each "
        + "platform stands\" in plans/video-studio-plan.md.";

    private static readonly StudioPreviewNamingSettings Settings = new() { TicksPerSecond = TicksPerSecond };

    [Theory]
    [InlineData(30, 0)]
    [InlineData(30, 4)]
    [InlineData(30, 8)]
    [InlineData(30, 12)]
    [InlineData(30, 16)]
    [InlineData(30, 20)]
    [InlineData(60, 0)]
    [InlineData(60, 2)]
    [InlineData(60, 4)]
    public void FramesEarlyInTheirSlot_EachHaveTheirNumberAtOnce(double frameRate, double intoSlot)
    {
        foreach (var play in EveryLookPhase(frameRate, intoSlot, emptySlot: -1))
        {
            Assert.True(play.AtOnce == play.Frames, $"{play}");
            Assert.False(play.UnsureAtEnd);
        }
    }

    [Theory(Skip = LateInTheSlot)]
    [InlineData(30, 24)]
    [InlineData(30, 26)]
    [InlineData(30, 28)]
    [InlineData(30, 30)]
    [InlineData(60, 6)]
    [InlineData(60, 8)]
    [InlineData(60, 10)]
    [InlineData(60, 12)]
    [InlineData(60, 14)]
    public void FramesLateInTheirSlot_EachHaveTheirNumberAtOnce(double frameRate, double intoSlot)
    {
        foreach (var play in EveryLookPhase(frameRate, intoSlot, emptySlot: -1))
        {
            Assert.True(play.AtOnce == play.Frames, $"{play}");
        }
    }

    [Theory]
    [InlineData(30, 0)]
    [InlineData(30, 4)]
    [InlineData(30, 8)]
    [InlineData(60, 0)]
    [InlineData(60, 2)]
    [InlineData(60, 4)]
    public void AMissingFrame_WhereTheFramesSitEarlyInTheirSlot_CostsItsNeighboursTheirNumberAndNoMore(double frameRate, double intoSlot)
    {
        foreach (var play in EveryLookPhase(frameRate, intoSlot, EmptySlot))
        {
            Assert.True(play.LongestWithout <= 2 && play.Late + play.Never <= 4, $"{play}");
            Assert.Equal(0, play.Unsure);
            Assert.False(play.UnsureAtEnd);
        }
    }

    [Theory(Skip = AfterAMissingFrame)]
    [InlineData(30, 12)]
    [InlineData(30, 16)]
    [InlineData(30, 20)]
    [InlineData(30, 24)]
    [InlineData(60, 7)]
    [InlineData(60, 8)]
    [InlineData(60, 9)]
    [InlineData(60, 10)]
    public void AMissingFrame_WhereverTheFramesSitInTheirSlot_CostsItsNeighboursTheirNumberAndNoMore(double frameRate, double intoSlot)
    {
        foreach (var play in EveryLookPhase(frameRate, intoSlot, EmptySlot))
        {
            Assert.True(play.LongestWithout <= 2 && play.Late + play.Never <= 4, $"{play}");
            Assert.Equal(0, play.Unsure);
            Assert.False(play.UnsureAtEnd);
        }
    }

    [Theory(Skip = NotTheFrameAtRest)]
    [InlineData(30, 20)]
    [InlineData(60, 10)]
    public void TheNumberAFramePlaysUnder_IsTheOneAPauseShowsItUnder(double frameRate, double intoSlot)
    {
        foreach (var play in EveryLookPhase(frameRate, intoSlot, emptySlot: -1))
        {
            Assert.True(play.NotAsAtRest == 0, $"{play}");
        }
    }

    /// <summary>The same recording played ten times, the player's looks a millisecond later each time.</summary>
    private static IEnumerable<Outcome> EveryLookPhase(double frameRate, double intoSlot, int emptySlot)
    {
        for (var look = 0; look < 10; look++)
        {
            yield return Play(frameRate, Stamps(frameRate, intoSlot, emptySlot, seed: 7 + look), look);
        }
    }

    /// <summary>
    /// When each frame of a file is stamped, in milliseconds: so far into its slot and up to
    /// <see cref="JitterMilliseconds"/> later, and no frame in the slot left empty.
    /// </summary>
    private static List<double> Stamps(double frameRate, double intoSlot, int emptySlot, int seed)
    {
        var random = new Random(seed);
        var slot = 1000.0 / frameRate;
        var stamps = new List<double>(Slots);
        for (var index = 0; index < Slots; index++)
        {
            var late = JitterMilliseconds * random.NextDouble();
            if (index != emptySlot)
            {
                stamps.Add((index * slot) + intoSlot + late);
            }
        }

        return stamps;
    }

    private static Outcome Play(double frameRate, List<double> stamps, double lookPhase)
    {
        var slot = 1000.0 / frameRate;
        var timing = new StudioPreviewClipTiming(frameRate, 100_000, 0);
        var namer = new StudioPreviewFrameNamer(frameRate, Settings);

        // The clock rests in the middle of a frame, on the frame its position names.
        const int restingFrame = 2;
        var clockAtStart = (restingFrame + 0.5) * slot;
        namer.Start(restingFrame, 0, 0);

        var next = 0;
        while (next < stamps.Count && stamps[next] <= clockAtStart)
        {
            next++;
        }

        var handedOver = new List<(StudioPreviewFrameKnowledge Knowledge, long Frame, int File)>();
        var late = 0;
        var free = 0.0;
        for (var look = lookPhase; next < stamps.Count; look += LookMilliseconds)
        {
            // One at a time, and never before its time.
            if (look < free || stamps[next] > clockAtStart + look)
            {
                continue;
            }

            var began = look + LatencyMilliseconds;
            var name = Name(clockAtStart + began, out var into);
            var earlier = namer.Begin(Ticks(began), name, into, 0, out var earlierFrame);
            var named = namer.End(Ticks(began + CopyMilliseconds), Name(clockAtStart + began + CopyMilliseconds, out _), CopyMilliseconds, 0);
            free = began + CopyMilliseconds + TailMilliseconds;
            namer.Exit(Ticks(free), Name(clockAtStart + free, out _), 0);

            if (handedOver.Count > 0
                && handedOver[^1].Knowledge == StudioPreviewFrameKnowledge.Unknown
                && (earlier == StudioPreviewEarlierFrame.Known || (earlier == StudioPreviewEarlierFrame.KnownIfPrompt && named.EarlierConfirmed)))
            {
                // The frame before gets its number from this one, a hand-over late.
                handedOver[^1] = (StudioPreviewFrameKnowledge.Known, earlierFrame, handedOver[^1].File);
                late++;
            }

            handedOver.Add((named.Knowledge, named.Frame, next));
            next++;
        }

        var outcome = new Outcome { FrameRate = frameRate, LookPhase = lookPhase, Late = late, UnsureAtEnd = namer.ShowsUnsure };
        var without = 0;
        foreach (var (knowledge, frame, file) in handedOver)
        {
            outcome.Frames++;
            if (knowledge == StudioPreviewFrameKnowledge.Unknown)
            {
                outcome.Never++;
                outcome.LongestWithout = Math.Max(outcome.LongestWithout, ++without);
                continue;
            }

            without = 0;
            outcome.Unsure += knowledge == StudioPreviewFrameKnowledge.Unsure ? 1 : 0;

            // Whether the player shows this frame with the clock in the middle of the slot it was
            // called: that is what a pause and a seek put there, and what the export draws.
            var middle = (frame + 0.5) * slot;
            var atRest = stamps[file] <= middle && (file + 1 >= stamps.Count || stamps[file + 1] > middle);
            outcome.NotAsAtRest += atRest ? 0 : 1;
        }

        outcome.AtOnce = outcome.Frames - outcome.Never - outcome.Unsure - late;
        return outcome;

        long Name(double clockMilliseconds, out double intoMilliseconds) =>
            timing.NameAtPlayerTicks((long)Math.Round(clockMilliseconds * 10_000), out intoMilliseconds);
    }

    private static long Ticks(double milliseconds) => (long)Math.Round(milliseconds * TicksPerSecond / 1000);

    private record struct Outcome
    {
        public double FrameRate { get; set; }

        public double LookPhase { get; set; }

        /// <summary>Frames handed over.</summary>
        public int Frames { get; set; }

        /// <summary>Given their number when they were handed over.</summary>
        public int AtOnce { get; set; }

        /// <summary>Given their number by the hand-over after them.</summary>
        public int Late { get; set; }

        /// <summary>Never given one.</summary>
        public int Never { get; set; }

        /// <summary>Shown under the number of their position, after half a second without one.</summary>
        public int Unsure { get; set; }

        /// <summary>The most frames in a row that never got a number.</summary>
        public int LongestWithout { get; set; }

        /// <summary>Numbered, and not the frame the player shows with the clock in the middle of that slot.</summary>
        public int NotAsAtRest { get; set; }

        public bool UnsureAtEnd { get; set; }

        public override readonly string ToString() =>
            $"{FrameRate} frames a second, looks {LookPhase} ms after the start: {Frames} frames, {AtOnce} numbered at once, {Late} a hand-over late, "
            + $"{Never} never ({LongestWithout} in a row at most), {Unsure} unsure, {NotAsAtRest} not the frame a pause shows there; unsure at the end: {UnsureAtEnd}";
    }
}
