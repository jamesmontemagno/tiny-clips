using TinyClips.Core.Studio.Preview;

namespace TinyClips.Core.Tests;

/// <summary>
/// The preview's arithmetic and its seek policy where a clip's frame times are known
/// (<see cref="StudioPreviewClipTiming.Times"/>): a recording, whose frames sit some milliseconds
/// into their slots and leave a slot empty where the recorder missed a tick. A frame's number is
/// then its place in the file, and the timeline frame it belongs to is the one the export shows
/// it in: the first whose middle the frame has begun by.
/// </summary>
public sealed class StudioPreviewRecordingTimesTests
{
    private const double Ntsc = 30000.0 / 1001.0;

    // ----------------------------------------------------------------------------------------
    // Times on the grid change nothing
    // ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(30.0)]
    [InlineData(60.0)]
    [InlineData(Ntsc)]
    public void TimesOnTheGrid_NameEveryPositionAsTheGridDoes(double fps)
    {
        const int frames = 360;
        var grid = new StudioPreviewClipTiming(fps, frames, 0);
        var timed = StudioPreviewClipTiming.WithTimes(OnTheGrid(frames, fps), fps, 0);
        var random = new Random(3);
        var compared = 0;
        for (var round = 0; round < 20_000; round++)
        {
            // Anywhere from the start to three frames past the end. Not within a few 100 ns of
            // where one frame turns into the next: there the grid rounds a product and the table
            // a sum, and they may differ by one unit.
            var position = (long)(random.NextDouble() * (frames + 3) * 10_000_000 / fps);
            var inFrames = (position * fps / 10_000_000.0) + StudioPreviewTimeMath.BoundaryTolerance;
            var fraction = inFrames - Math.Floor(inFrames);
            if (fraction < 2e-5 || fraction > 1 - 2e-5)
            {
                continue;
            }

            compared++;
            Assert.Equal(grid.FrameAtPlayerTicks(position), timed.FrameAtPlayerTicks(position));
            var name = grid.NameAtPlayerTicks(position, out var into);
            Assert.Equal(name, timed.NameAtPlayerTicks(position, out var intoTimed));
            Assert.Equal(into, intoTimed, 0.001);
            Assert.Equal(grid.FrameAtTimeline(position / 10_000_000.0), timed.FrameAtTimeline(position / 10_000_000.0));
        }

        Assert.True(compared > 19_000);
    }

    [Theory]
    [InlineData(30.0)]
    [InlineData(60.0)]
    [InlineData(Ntsc)]
    public void TimesOnTheGrid_GiveTheTimelineTheSameAnswers(double fps)
    {
        const int frames = 360;
        var duration = frames / fps;
        var table = OnTheGrid(frames, fps);
        var grid = new StudioPreviewTimeline(duration, fps, [new StudioPreviewClipTiming(fps, frames, 0), new StudioPreviewClipTiming(fps, 300, 0.2, 300 / fps)]);
        var timed = new StudioPreviewTimeline(duration, fps, [StudioPreviewClipTiming.WithTimes(table, fps, 0), StudioPreviewClipTiming.WithTimes(OnTheGrid(300, fps), fps, 0.2, 300 / fps)]);

        Assert.False(grid.ScreenHasTimes);
        Assert.True(timed.ScreenHasTimes);
        Assert.Equal(grid.FrameCount, timed.FrameCount);
        for (long frame = 0; frame <= grid.LastFrame; frame++)
        {
            var middle = grid.FrameMiddle(frame);
            for (var track = 0; track < 2; track++)
            {
                Assert.Equal(grid.PlayerFrame(track, frame), timed.PlayerFrame(track, frame));
                Assert.Equal(grid.IsShown(track, frame), timed.IsShown(track, frame));
                Assert.Equal(grid.IsPlayerInside(track, middle), timed.IsPlayerInside(track, middle));
                Assert.Equal(grid.IsPlayerAtEnd(track, middle), timed.IsPlayerAtEnd(track, middle));
            }

            // Every frame of the file belongs to the timeline frame of its own number.
            Assert.Equal(frame, timed.TimelineFrameOfScreen(frame));
            Assert.Equal(frame, grid.TimelineFrameOfScreen(frame));
        }

        Assert.True(grid.TryPickProofFrames(0, out var first, out var second));
        Assert.True(timed.TryPickProofFrames(0, out var firstTimed, out var secondTimed));
        Assert.Equal((first, second), (firstTimed, secondTimed));
    }

    [Fact]
    public void WithoutTimes_AFrameOfTheScreenIsItsOwnTimelineFrame_WhateverItsNumber()
    {
        var timeline = new StudioPreviewTimeline(2, 30, [new StudioPreviewClipTiming(30, 60, 0)]);

        // Not clamped and not looked at: the grid's numbers are passed on as they are.
        Assert.Equal(-1, timeline.TimelineFrameOfScreen(-1));
        Assert.Equal(17, timeline.TimelineFrameOfScreen(17));
        Assert.Equal(75, timeline.TimelineFrameOfScreen(75));
    }

    // ----------------------------------------------------------------------------------------
    // Frames that are not on the grid
    // ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(40_000)]
    [InlineData(120_000)]
    [InlineData(160_000)]
    public void FramesBeforeTheMiddleOfTheirSlot_BelongToTheirOwnSlot(long into)
    {
        var timeline = Timeline(Frames(60, into), 60);

        for (long slot = 0; slot < 60; slot++)
        {
            Assert.Equal(slot, timeline.PlayerFrame(0, slot));
            Assert.Equal(slot, timeline.TimelineFrameOfScreen(slot));
        }
    }

    [Theory]
    [InlineData(200_000)]
    [InlineData(280_000)]
    [InlineData(320_000)]
    public void FramesPastTheMiddleOfTheirSlot_BelongToTheSlotAfter(long into)
    {
        var timeline = Timeline(Frames(60, into), 60);

        // Nothing has begun by the middle of slot 0: the first frame shows there all the same,
        // as in the export, and is that slot's.
        Assert.Equal(0, timeline.PlayerFrame(0, 0));
        Assert.Equal(0, timeline.TimelineFrameOfScreen(0));
        for (long slot = 1; slot < 60; slot++)
        {
            Assert.Equal(slot - 1, timeline.PlayerFrame(0, slot));
        }

        for (long frame = 1; frame < 59; frame++)
        {
            Assert.Equal(frame + 1, timeline.TimelineFrameOfScreen(frame));
        }

        // The last frame begins past the middle of the last slot: no slot is left for it, and it counts as the last.
        Assert.Equal(59, timeline.TimelineFrameOfScreen(59));
    }

    [Fact]
    public void ASlotWithoutAFrame_ShowsTheFrameBeforeIt_AndTheFrameAfterItIsTheNextOne()
    {
        // 4 ms into every slot but slot 20.
        var timeline = Timeline(Frames(60, 40_000, 30, 20), 60);

        Assert.Equal(59, timeline.Track(0).FrameCount);
        Assert.Equal(19, timeline.PlayerFrame(0, 19));
        Assert.Equal(19, timeline.PlayerFrame(0, 20));
        Assert.Equal(20, timeline.PlayerFrame(0, 21));
        Assert.Equal(58, timeline.PlayerFrame(0, 59));

        // The frame before the gap belongs to its own slot, the first of the two that show it.
        Assert.Equal(19, timeline.TimelineFrameOfScreen(19));
        Assert.Equal(21, timeline.TimelineFrameOfScreen(20));
        Assert.Equal(59, timeline.TimelineFrameOfScreen(58));
    }

    [Fact]
    public void ASlotWithoutAFrame_WhereTheFramesSitLate_IsOneSlotOn()
    {
        // 20 ms into every slot but slot 20: the middles of slots 20 and 21 both show the frame of slot 19.
        var timeline = Timeline(Frames(60, 200_000, 30, 20), 60);

        Assert.Equal(18, timeline.PlayerFrame(0, 19));
        Assert.Equal(19, timeline.PlayerFrame(0, 20));
        Assert.Equal(19, timeline.PlayerFrame(0, 21));
        Assert.Equal(20, timeline.PlayerFrame(0, 22));
        Assert.Equal(20, timeline.TimelineFrameOfScreen(19));
        Assert.Equal(22, timeline.TimelineFrameOfScreen(20));
    }

    [Fact]
    public void TwoFramesBeforeOneMiddle_BelongToTheSameTimelineFrame_WhichShowsTheSecond()
    {
        var timeline = Timeline(WithAFrameInNoSlot(), 60);

        Assert.Equal(9, timeline.PlayerFrame(0, 10));
        Assert.Equal(11, timeline.PlayerFrame(0, 11));
        Assert.Equal(9, timeline.TimelineFrameOfScreen(9));
        Assert.Equal(11, timeline.TimelineFrameOfScreen(10));
        Assert.Equal(11, timeline.TimelineFrameOfScreen(11));
        Assert.Equal(12, timeline.TimelineFrameOfScreen(12));
    }

    [Fact]
    public void AFirstFrameThatBeginsLate_IsShownFromTheStart()
    {
        // As a recording begins, if the file keeps the times: nothing in slot 0, the first frame 35 ms in.
        var timeline = Timeline(Frames(60, 20_000, 30, 0), 60);

        Assert.Equal(0, timeline.PlayerFrame(0, 0));
        Assert.Equal(0, timeline.PlayerFrame(0, 1));
        Assert.Equal(1, timeline.PlayerFrame(0, 2));
        Assert.Equal(0, timeline.TimelineFrameOfScreen(0));
        Assert.Equal(2, timeline.TimelineFrameOfScreen(1));

        // The player is inside its stream there, although no frame has begun.
        Assert.True(timeline.IsPlayerInside(0, timeline.FrameMiddle(0)));
        Assert.Equal(-1, timeline.Track(0).NameAtPlayerTicks(timeline.MiddleTicks(0), out _));
        Assert.Equal(0, timeline.Track(0).FrameAtPlayerTicks(timeline.MiddleTicks(0)));
    }

    [Fact]
    public void EveryFrameBelongsToTheFirstTimelineFrameThatShowsIt_WhateverTheTimes()
    {
        var random = new Random(5);
        foreach (var fps in new[] { 30.0, 60.0 })
        {
            for (var round = 0; round < 200; round++)
            {
                // Frames at random places: some close together, some with slots between them,
                // and a few after the timeline has ended.
                var starts = new List<long>();
                long at = random.Next(0, 400_000);
                while (at < 31_000_000)
                {
                    starts.Add(at);
                    at += random.Next(1, 3) == 1 ? random.Next(50_000, 400_000) : random.Next(300_000, 1_100_000);
                }

                var slots = (int)(3 * fps);
                var timeline = Timeline(Table([.. starts]), slots, fps);
                long before = 0;
                for (long frame = 0; frame < starts.Count; frame++)
                {
                    var slot = timeline.TimelineFrameOfScreen(frame);
                    Assert.InRange(slot, 0, timeline.LastFrame);
                    Assert.True(slot >= before, $"frame {frame} belongs to {slot}, the one before it to {before}");
                    before = slot;

                    // No earlier timeline frame shows it or a later one; this one does, unless the timeline ends first.
                    Assert.True(slot == 0 || timeline.PlayerFrame(0, slot - 1) < frame);
                    Assert.True(timeline.PlayerFrame(0, slot) >= frame || slot == timeline.LastFrame);
                }

                // And the other way round: what a timeline frame shows belongs to it or to an earlier one.
                for (long slot = 0; slot <= timeline.LastFrame; slot++)
                {
                    Assert.True(timeline.TimelineFrameOfScreen(timeline.PlayerFrame(0, slot)) <= slot);
                }
            }
        }
    }

    [Fact]
    public void NameAtPlayerTicks_WithTimes_IsTheLastFrameThatHasBegun_AndHowLongAgo()
    {
        var timing = StudioPreviewClipTiming.WithTimes(StudioPreviewFrameTimes.TryCreate([350_000, 683_333, 1_350_000], 1_683_333, out _)!, 30, 0);

        // Before the first frame: no frame's name, and the first frame all the same where one is wanted.
        Assert.Equal(-1, timing.NameAtPlayerTicks(100_000, out var beforeAny));
        Assert.Equal(0, beforeAny);
        Assert.Equal(0, timing.FrameAtPlayerTicks(100_000));

        Assert.Equal(0, timing.NameAtPlayerTicks(350_000, out var atTheStart));
        Assert.InRange(atTheStart, 0, 0.05);
        Assert.Equal(0, timing.NameAtPlayerTicks(350_000 + 123_000, out var into));
        Assert.InRange(into, 12.3, 12.35);

        // In a gap the frame before it is still the one, however long it has been.
        Assert.Equal(1, timing.NameAtPlayerTicks(1_300_000, out var inTheGap));
        Assert.InRange(inTheGap, 61.6, 61.75);

        // A position a hair before a frame's start counts as that frame, as on the grid.
        Assert.Equal(2, timing.NameAtPlayerTicks(1_350_000 - 200, out var aHairBefore));
        Assert.InRange(aHairBefore, 0, 0.05);
        Assert.Equal(1, timing.NameAtPlayerTicks(1_350_000 - 400, out _));

        // After the last frame's end the count goes on in frames of the usual length, so that
        // the last frame does not stand for what comes after it; clamped, it is the last frame.
        Assert.Equal(3, timing.NameAtPlayerTicks(1_683_333, out var atTheEnd));
        Assert.InRange(atTheEnd, 0, 0.05);
        Assert.Equal(4, timing.NameAtPlayerTicks(1_683_333 + 500_000, out var past));
        Assert.InRange(past, 16.6, 16.8);
        Assert.Equal(2, timing.FrameAtPlayerTicks(1_683_333 + 500_000));
        Assert.Equal(2, timing.ClampFrame(4));
    }

    [Fact]
    public void ACameraWithItsOwnTimes_ShowsItsLastFrameThroughAStall_AndItsFirstBeforeItBegins()
    {
        // Three frames a thirtieth apart, a stall of a third of a second, two more. The camera
        // began 137 ms into the recording, whose own frames are on the grid.
        var camera = StudioPreviewClipTiming.WithTimes(Table(0, 333_333, 666_667, 4_000_000, 4_333_333), 30, 0.137, 0.5);
        var timeline = new StudioPreviewTimeline(6, 30, [new StudioPreviewClipTiming(30, 180, 0), camera]);

        Assert.False(timeline.ScreenHasTimes);
        Assert.Equal(0, timeline.PlayerFrame(1, 0));
        Assert.Equal(0, timeline.PlayerFrame(1, 4));
        Assert.Equal(1, timeline.PlayerFrame(1, 5));
        Assert.Equal(2, timeline.PlayerFrame(1, 6));
        Assert.Equal(2, timeline.PlayerFrame(1, 10));
        Assert.Equal(3, timeline.PlayerFrame(1, 16));
        Assert.Equal(4, timeline.PlayerFrame(1, 17));
        Assert.Equal(4, timeline.PlayerFrame(1, 100));

        // Whether it is part of the picture is the project's to say, as before: from its offset for its length.
        Assert.False(timeline.IsShown(1, 3));
        Assert.True(timeline.IsShown(1, 4));
        Assert.True(timeline.IsShown(1, 18));
        Assert.False(timeline.IsShown(1, 19));
    }

    [Fact]
    public void WithTimes_InsideTheStreamAndAtItsEnd_GoByTheFramesTimes()
    {
        var table = StudioPreviewFrameTimes.TryCreate([350_000, 683_333, 1_016_667], 1_350_000, out _)!;
        var timeline = new StudioPreviewTimeline(2, 30, [StudioPreviewClipTiming.WithTimes(table, 30, 0)]);

        Assert.True(timeline.IsPlayerInside(0, 0.01));
        Assert.True(timeline.IsPlayerInside(0, 0.134));
        Assert.False(timeline.IsPlayerInside(0, 0.136));
        Assert.False(timeline.IsPlayerAtEnd(0, 0.1));
        Assert.True(timeline.IsPlayerAtEnd(0, 0.102));
        Assert.True(timeline.IsPlayerAtEnd(0, 1.5));
    }

    // ----------------------------------------------------------------------------------------
    // The seek policy
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void OnTheGrid_TheTimelineFrameIsTheFrameTheScreenDelivered()
    {
        var h = new Harness(new StudioPreviewTimeline(2, 30, [new StudioPreviewClipTiming(30, 60, 0)]));
        Assert.Equal(0, h.Policy.ShownTimelineFrame);

        h.SettleAt(19);
        Assert.Equal(19, h.Policy.ShownTimelineFrame);

        // A step: the player reports the position it had before, with the next frame.
        h.Policy.RequestSeek(20);
        h.Pump();
        Assert.Equal(["step 0"], h.Calls);
        h.Advance(20);
        h.Frame(0, 19);
        h.Pump();
        Assert.Equal(20, h.Policy.ShownFrame(0));
        Assert.Equal(20, h.Policy.ShownTimelineFrame);

        h.Policy.Forget(0);
        Assert.Equal(-1, h.Policy.ShownTimelineFrame);
    }

    [Fact]
    public void ASeekToASlotWithoutAFrameOfItsOwn_LandsAtOnce_OnThePictureThatIsThere()
    {
        var h = new Harness(Timeline(Frames(60, 40_000, 30, 20), 60));
        h.SettleAt(19);
        Assert.Equal(19, h.Policy.ShownFrame(0));
        Assert.Equal(19, h.Policy.ShownTimelineFrame);

        h.Policy.RequestSeek(20);
        h.Pump();

        // The clock is moved, and no picture is owed: the export shows frame 19 of the file in
        // slot 20 as well. Not a step either, for there is no next frame to step to.
        Assert.Equal(["seek 20"], h.Calls);
        var landing = Assert.Single(h.Landings);
        Assert.Equal(20, landing.Frame);
        Assert.Equal(StudioPreviewLandingKind.Seek, landing.Kind);
        Assert.True(landing.Confirmed);
        Assert.Equal(19, h.Policy.ShownFrame(0));
        Assert.Equal(20, h.Policy.ShownTimelineFrame);
        Assert.False(h.Policy.HoldPicture);

        // The player draws its frame again for the new position, as it does for every change
        // that moves the clock inside its stream. It is the answer, and no stray.
        h.Advance(40);
        h.Frame(0, 19);
        h.Completed(0);
        h.Pump();
        Assert.Equal(20, h.Policy.ShownTimelineFrame);
        Assert.Equal(20, h.Policy.SettledFrame);
        Assert.True(h.Policy.IsIdle);
        Assert.Equal(0, h.Policy.StrayFrames);
        Assert.Equal(0, h.Policy.Repairs);
    }

    [Fact]
    public void OneFrameOnFromASlotWithoutAFrame_StepsThePlayerToTheNextFrameOfTheFile()
    {
        var h = new Harness(Timeline(Frames(60, 40_000, 30, 20), 60));
        h.SettleAt(20);
        Assert.Equal(19, h.Policy.ShownFrame(0));
        Assert.Equal(20, h.Policy.ShownTimelineFrame);

        h.Policy.RequestSeek(21);
        h.Pump();
        Assert.Equal(["step 0"], h.Calls);

        // A stepped player reports the position it had: the frame the step started from.
        h.Advance(20);
        h.Frame(0, 19);
        h.Pump();
        var landing = Assert.Single(h.Landings);
        Assert.Equal(21, landing.Frame);
        Assert.Equal(StudioPreviewLandingKind.Step, landing.Kind);
        Assert.Equal(20, h.Policy.ShownFrame(0));
        Assert.Equal(21, h.Policy.ShownTimelineFrame);
    }

    [Fact]
    public void OneFrameOn_WhereTheNextSlotShowsTheFrameAfterTheNext_IsASeekAndNotAStep()
    {
        var h = new Harness(Timeline(WithAFrameInNoSlot(), 60));
        h.SettleAt(10);
        Assert.Equal(9, h.Policy.ShownFrame(0));

        h.Policy.RequestSeek(11);
        h.Pump();

        // A step would show frame 10 of the file, which the export shows in no slot.
        Assert.Equal(["seek 11"], h.Calls);
    }

    [Fact]
    public void AStepThatMovesOnlyTheCamera_MovesTheSceneToTheNextTimelineFrame()
    {
        // The screen has no frame in slot 20; the camera, on the grid, has one in every slot.
        var timeline = new StudioPreviewTimeline(2, 30, [StudioPreviewClipTiming.WithTimes(Frames(60, 40_000, 30, 20), 30, 0), new StudioPreviewClipTiming(30, 60, 0)]);
        var h = new Harness(timeline);
        h.SettleAt(19);

        h.Policy.RequestSeek(20);
        h.Pump();
        Assert.Equal(["step 1"], h.Calls);
        h.Advance(20);
        h.Frame(1, 19);
        h.Pump();

        Assert.Equal(20, h.Landings[^1].Frame);
        Assert.Equal(StudioPreviewLandingKind.Step, h.Landings[^1].Kind);
        Assert.Equal(19, h.Policy.ShownFrame(0));
        Assert.Equal(20, h.Policy.ShownFrame(1));
        Assert.Equal(20, h.Policy.ShownTimelineFrame);
    }

    [Fact]
    public void AFrameOfPlayback_IsShownUnderTheTimelineFrameItBelongsTo_AndThePlayersRestThereWithoutFetchingIt()
    {
        var h = new Harness(Timeline(Frames(60, 40_000, 30, 20), 60));
        h.Play(10);

        // The frames of the file one after the other. The 20th is the one after the empty slot.
        h.Advance(300);
        h.Frame(0, 19);
        Assert.Equal(19, h.Policy.ShownTimelineFrame);
        h.Advance(66);
        h.Frame(0, 20);
        Assert.Equal(20, h.Policy.ShownFrame(0));
        Assert.Equal(21, h.Policy.ShownTimelineFrame);

        h.Policy.SetPlaying(false);
        h.Advance(40);
        h.Pump();
        Assert.Equal(["pause", "seek 21"], h.Calls);
        h.Advance(30);
        h.Deliver(21);
        h.Pump();
        h.Quiet();

        var landing = Assert.Single(h.Landings);
        Assert.Equal(21, landing.Frame);
        Assert.Equal(StudioPreviewLandingKind.Snap, landing.Kind);
        Assert.Equal(20, h.Policy.ShownFrame(0));
        Assert.Equal(21, h.Policy.ShownTimelineFrame);
        Assert.Equal(0, h.Policy.Repairs);
        Assert.Equal(0, h.Policy.RestsFetchedAnew);
    }

    [Fact]
    public void FramesThatSitPastTheMiddleOfTheirSlot_RestUnderTheNumberTheyPlayedUnder()
    {
        // 20 ms into every slot: the export shows each a slot later, and so do playing and a pause.
        var h = new Harness(Timeline(Frames(60, 200_000), 60));
        h.Play(10);
        h.Advance(200);
        h.Frame(0, 15);
        Assert.Equal(16, h.Policy.ShownTimelineFrame);

        h.Policy.SetPlaying(false);
        h.Advance(40);
        h.Pump();
        Assert.Equal(["pause", "seek 16"], h.Calls);
        h.Advance(30);
        h.Deliver(16);
        h.Pump();
        h.Quiet();

        // The picture stays: the frame the players rest on is the frame that was playing.
        Assert.Equal(16, Assert.Single(h.Landings).Frame);
        Assert.Equal(15, h.Policy.ShownFrame(0));
        Assert.Equal(16, h.Policy.ShownTimelineFrame);
        Assert.Equal(0, h.Policy.Repairs);
    }

    [Fact]
    public void AFrameTheExportShowsInNoSlot_PlaysUnderTheNextFramesNumber_AndThePlayersRestOnThatOne()
    {
        var h = new Harness(Timeline(WithAFrameInNoSlot(), 60));
        h.Play(5);
        h.Advance(180);
        h.Frame(0, 10);
        Assert.Equal(11, h.Policy.ShownTimelineFrame);

        h.Policy.SetPlaying(false);
        h.Advance(40);
        h.Pump();
        Assert.Equal(["pause", "seek 11"], h.Calls);

        // The frame the export shows there is the one after: it is owed, and it comes.
        h.Advance(30);
        h.Deliver(11);
        h.Pump();
        h.Quiet();
        Assert.Equal(11, Assert.Single(h.Landings).Frame);
        Assert.Equal(11, h.Policy.ShownFrame(0));
        Assert.Equal(11, h.Policy.ShownTimelineFrame);
    }

    [Fact]
    public void ADetour_GoesToATimelineFrameThatShowsAnotherFrameOfTheFile()
    {
        // Slots 20 to 24 have no frame: they all show the frame of slot 19. A lost frame at
        // slot 21 is fetched by way of a slot that shows something else, not by the slot next
        // to it, where the player would not move.
        var h = new Harness(Timeline(Frames(60, 40_000, 30, 20, 21, 22, 23, 24), 60));
        h.SettleAt(40);

        h.Policy.RequestSeek(21);
        h.Pump();
        Assert.Equal(["seek 21"], h.Calls);
        h.Advance(2000);
        h.Pump();

        Assert.Equal(2, h.Calls.Count);
        var detour = long.Parse(h.Calls[1][5..]);
        Assert.NotEqual(h.Timeline.PlayerFrame(0, 21), h.Timeline.PlayerFrame(0, detour));
        Assert.True(detour is < 19 or > 24, $"the detour went to slot {detour}");
    }

    // ----------------------------------------------------------------------------------------

    private static long SlotStart(long slot, double fps = 30) => (long)Math.Round(slot * 10_000_000.0 / fps, MidpointRounding.AwayFromZero);

    private static StudioPreviewFrameTimes Table(params long[] starts) => StudioPreviewFrameTimes.TryCreate(starts, 0, out _)!;

    /// <summary>A frame exactly at the start of every slot, to the last 100 ns.</summary>
    private static StudioPreviewFrameTimes OnTheGrid(int frames, double fps) =>
        StudioPreviewFrameTimes.TryCreate([.. Enumerable.Range(0, frames).Select(frame => StudioPreviewTimeMath.ToTicks(frame / fps))], StudioPreviewTimeMath.ToTicks(frames / fps), out _)!;

    /// <summary>A frame so far into every slot, and none in the slots named.</summary>
    private static StudioPreviewFrameTimes Frames(int slots, long into, double fps = 30, params int[] empty) =>
        Table([.. Enumerable.Range(0, slots).Where(slot => !empty.Contains(slot)).Select(slot => SlotStart(slot, fps) + into)]);

    /// <summary>
    /// 4 ms into every slot, but 22 ms into slot 10: that frame and the next both begin between
    /// the middles of slots 10 and 11, so the export shows the first of the two in no slot.
    /// </summary>
    private static StudioPreviewFrameTimes WithAFrameInNoSlot() =>
        Table([.. Enumerable.Range(0, 60).Select(slot => SlotStart(slot) + (slot == 10 ? 220_000 : 40_000))]);

    private static StudioPreviewTimeline Timeline(StudioPreviewFrameTimes screen, int slots, double fps = 30) =>
        new(slots / fps, fps, [StudioPreviewClipTiming.WithTimes(screen, fps, 0)]);

    private sealed class Players : IStudioPreviewTransport
    {
        public List<string> Calls { get; } = [];

        public void Seek(long timelineFrame) => Calls.Add($"seek {timelineFrame}");

        public void StepForward(int track) => Calls.Add($"step {track}");

        public bool Resume()
        {
            Calls.Add("resume");
            return true;
        }

        public void Pause() => Calls.Add("pause");

        public bool IsDelivering(int track) => false;
    }

    /// <summary>The seek policy over players that do as they are told, with a clock that counts milliseconds.</summary>
    private sealed class Harness
    {
        private readonly Players _players = new();

        public Harness(StudioPreviewTimeline timeline)
        {
            Timeline = timeline;
            Policy = new StudioPreviewSeekPolicy(_players, timeline, new StudioPreviewSeekSettings { TicksPerSecond = 1000 }, () => Now);

            // A player hands over its first frame when it opens.
            for (var track = 0; track < timeline.TrackCount; track++)
            {
                Policy.OnFrame(track, 0, Now);
            }
        }

        public StudioPreviewTimeline Timeline { get; }

        public StudioPreviewSeekPolicy Policy { get; }

        public List<StudioPreviewLanding> Landings { get; } = [];

        public List<string> Calls => _players.Calls;

        public long Now { get; private set; } = 1000;

        public void Advance(long milliseconds) => Now += milliseconds;

        public long Pump()
        {
            var deadline = Policy.Pump();
            Policy.TakeLandings(Landings);
            return deadline;
        }

        public void Frame(int track, long playerFrame) => Policy.OnFrame(track, playerFrame, Now);

        public void Completed(int track) => Policy.OnSeekCompleted(track, Now);

        /// <summary>
        /// Every player answers a position change the way a real one does: with the frame it
        /// shows there, when the change moves it or it is inside its stream, and with SeekCompleted.
        /// </summary>
        public void Deliver(long timelineFrame)
        {
            for (var track = 0; track < Timeline.TrackCount; track++)
            {
                var wanted = Timeline.PlayerFrame(track, timelineFrame);
                if (Timeline.IsPlayerInside(track, Timeline.FrameMiddle(timelineFrame)) || Policy.ShownFrame(track) != wanted)
                {
                    Frame(track, wanted);
                }

                Completed(track);
            }
        }

        /// <summary>The players say nothing for the grace period.</summary>
        public void Quiet()
        {
            Advance(40);
            Pump();
        }

        /// <summary>Brings the players to rest on a timeline frame and forgets how they got there.</summary>
        public void SettleAt(long timelineFrame)
        {
            Policy.RequestSeek(timelineFrame);
            Pump();
            Advance(50);
            Deliver(timelineFrame);
            Pump();
            Assert.Equal(timelineFrame, Policy.SettledFrame);
            Advance(100);
            Calls.Clear();
            Landings.Clear();
        }

        public void Play(long from)
        {
            SettleAt(from);
            Policy.SetPlaying(true);
            Pump();
            Assert.True(Policy.IsPlaying);
            Calls.Clear();
        }
    }
}
