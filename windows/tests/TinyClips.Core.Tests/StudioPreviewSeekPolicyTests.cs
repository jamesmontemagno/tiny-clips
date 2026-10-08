using TinyClips.Core.Studio.Preview;

namespace TinyClips.Core.Tests;

/// <summary>
/// The seek policy as a state machine: a fake pair of players records what is asked of it, and the
/// test decides which frames arrive and when. The clock counts milliseconds.
/// </summary>
public sealed class StudioPreviewSeekPolicyTests
{
    private const int Screen = 0;
    private const int Camera = 1;

    // The camera started 0.2 s after the screen: timeline frame N shows camera frame N - 6.
    private const int Lag = 6;

    [Fact]
    public void Seek_AssignsThePosition_AndLandsWhenEveryPlayerHasDelivered()
    {
        var h = new Harness();

        h.Policy.RequestSeek(100);
        h.Pump();
        Assert.Equal(["seek 100"], h.Calls);
        Assert.Empty(h.Landings);

        h.Advance(50);
        h.Frame(Screen, 100);
        h.Completed(Screen);
        h.Pump();
        Assert.Empty(h.Landings);

        h.Advance(10);
        h.Frame(Camera, 100 - Lag);
        h.Completed(Camera);
        h.Pump();

        var landing = Assert.Single(h.Landings);
        Assert.Equal(new StudioPreviewLanding(100, true, StudioPreviewLandingKind.Seek, 1000, 1060, 0), landing);
        Assert.Equal(100, h.Policy.SettledFrame);
        Assert.True(h.Policy.IsIdle);
        Assert.Equal(["seek 100"], h.Calls);
    }

    [Fact]
    public void Seek_LandsWhenTheFramesAreIn_AndStaysInFlightUntilThePlayersHaveAnswered()
    {
        var h = new Harness();

        h.Policy.RequestSeek(100);
        h.Pump();
        h.Advance(30);
        h.Frame(Screen, 100);
        h.Frame(Camera, 94);
        var deadline = h.Pump();

        // The picture is complete, so the position is reported at once.
        Assert.Equal(100, Assert.Single(h.Landings).Frame);
        Assert.Equal(1030, h.Landings[0].LandedAt);

        // SeekCompleted is still to come. Until it has, or 400 ms after the clock was moved, the
        // next position change waits, or this one's SeekCompleted would be taken for its own.
        Assert.False(h.Policy.IsIdle);
        Assert.Equal(1400, deadline);
        h.Policy.RequestSeek(200);
        h.Pump();
        Assert.Equal(["seek 100"], h.Calls);

        h.Advance(4);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Pump();
        Assert.Equal(["seek 100", "seek 200"], h.Calls);
    }

    [Fact]
    public void IsSeeking_FromTheRequestUntilThePositionIsReported_NotUntilThePlayersHaveAnswered()
    {
        var h = new Harness();
        Assert.False(h.Policy.IsSeeking);

        // Asked for and not started on.
        h.Policy.RequestSeek(100);
        Assert.True(h.Policy.IsSeeking);

        // On the way.
        h.Pump();
        Assert.True(h.Policy.IsSeeking);
        h.Advance(30);
        h.Frame(Screen, 100);
        h.Pump();
        Assert.True(h.Policy.IsSeeking);

        // Reported. The players' SeekCompleted is still waited for.
        h.Frame(Camera, 94);
        h.Pump();
        Assert.Equal(100, Assert.Single(h.Landings).Frame);
        Assert.False(h.Policy.IsSeeking);
        Assert.False(h.Policy.IsIdle);
    }

    [Fact]
    public void IsSeeking_NoLonger_WhenThePlayersAreFoundOnTheRequestedFrameAlready()
    {
        var h = new Harness();
        h.SettleAt(100);

        h.Policy.RequestSeek(100);
        Assert.True(h.Policy.IsSeeking);
        h.Pump();

        Assert.False(h.Policy.IsSeeking);
        Assert.Empty(h.Calls);
        Assert.Empty(h.Landings);
    }

    [Fact]
    public void IsSeeking_ThroughARepair_AndThroughTheSnapAfterAPause_ButNotWhileTheClockIsBroughtAlong()
    {
        var h = new Harness();
        h.SettleAt(50);

        // A lost frame: the detour and the way back are part of reaching the position.
        h.Policy.RequestSeek(100);
        h.Pump();
        h.Advance(2000);
        h.Pump();
        Assert.Equal(["seek 100", "seek 99"], h.Calls);
        Assert.True(h.Policy.IsSeeking);
        h.Advance(30);
        h.DeliverDetour(99);
        Assert.Equal(["seek 100", "seek 99", "seek 100"], h.Calls);
        Assert.True(h.Policy.IsSeeking);
        h.Advance(30);
        h.Deliver(100);
        h.Pump();
        Assert.False(h.Policy.IsSeeking);

        // A step lands when the frames are in; bringing the clock along afterwards reaches nothing new.
        h.Advance(100);
        h.Step(101);
        Assert.False(h.Policy.IsSeeking);
        h.Advance(150);
        h.Pump();
        Assert.Equal("seek 101", h.Calls[^1]);
        Assert.False(h.Policy.IsSeeking);
        h.Advance(40);
        h.Deliver(101);
        h.Pump();

        // After a pause the players are brought onto one frame: that is a position to reach.
        h.Policy.SetPlaying(true);
        h.Pump();
        Assert.True(h.Policy.IsPlaying);
        Assert.False(h.Policy.IsSeeking);
        h.Advance(500);
        h.Frame(Screen, 116);
        h.Frame(Camera, 109);
        h.Policy.SetPlaying(false);
        h.Advance(40);
        h.Pump();
        Assert.Equal("seek 116", h.Calls[^1]);
        Assert.True(h.Policy.IsSeeking);
        h.Advance(30);
        h.Deliver(116);
        h.Pump();
        Assert.True(h.Policy.IsSeeking);
        h.Quiet();
        Assert.Equal(StudioPreviewLandingKind.Snap, h.Landings[^1].Kind);
        Assert.False(h.Policy.IsSeeking);
    }

    [Fact]
    public void IsSeeking_NoLonger_AfterStop()
    {
        var h = new Harness();
        h.Policy.RequestSeek(100);
        h.Pump();
        Assert.True(h.Policy.IsSeeking);

        h.Policy.Stop();

        Assert.False(h.Policy.IsSeeking);
        Assert.Empty(h.Landings);
    }

    [Fact]
    public void Seek_ThatIsNeverCompleted_StopsBlockingTheNextOne()
    {
        var h = new Harness();

        h.Policy.RequestSeek(100);
        h.Pump();
        h.Advance(30);
        h.Frame(Screen, 100);
        h.Frame(Camera, 94);
        h.Pump();
        h.Policy.RequestSeek(200);

        h.Advance(369);
        h.Pump();
        Assert.Equal(["seek 100"], h.Calls);
        Assert.Equal(0, h.Policy.AnswersGivenUp);

        h.Advance(1);
        h.Pump();
        Assert.Equal(["seek 100", "seek 200"], h.Calls);
        Assert.Equal(1, h.Policy.AnswersGivenUp);
    }

    [Fact]
    public void FrameThatReportsAnotherPosition_IsALateAnswerToAnEarlierChange_AndDoesNotCount()
    {
        // Measured on a busy machine: a seek took over half a second, and its frame arrived after
        // the next position change had been made. The player reports the position the frame is for.
        var h = new Harness();
        h.SettleAt(120);

        h.Policy.RequestSeek(119);
        h.Pump();
        h.Advance(20);
        h.Frame(Screen, 118);
        h.Completed(Screen);
        h.Frame(Camera, 113);
        h.Completed(Camera);
        h.Pump();

        Assert.Empty(h.Landings);
        Assert.Equal(118, h.Policy.ShownFrame(Screen));
        Assert.Equal(1, h.Policy.StrayFrames);
        Assert.True(h.Policy.HoldPicture);

        h.Advance(30);
        h.Frame(Screen, 119);
        h.Pump();
        Assert.Equal(119, Assert.Single(h.Landings).Frame);
        Assert.Equal(0, h.Policy.Repairs);
    }

    [Fact]
    public void PlayerThatOwesNoNewPicture_IsStillWaitedFor_BecauseItAnswersAnyway()
    {
        // A 15 fps camera: timeline frames 100 and 101 both show its frame 50. Its player is moved
        // all the same and draws that frame again, some tens of milliseconds later.
        var h = new Harness(new StudioPreviewTimeline(30, 30, [new(30, 900, 0), new(15, 450, 0)]));
        h.SettleAt(101);

        h.Policy.RequestSeek(100);
        h.Pump();
        h.Advance(20);
        h.Frame(Screen, 100);
        h.Completed(Screen);
        h.Pump();
        Assert.Equal(100, Assert.Single(h.Landings).Frame);

        // If the next change were made now, the camera would be flushed while it hands that frame over.
        h.Policy.RequestSeek(300);
        h.Pump();
        Assert.Equal(["seek 100"], h.Calls);

        h.Advance(30);
        h.Frame(Camera, 50);
        h.Completed(Camera);
        h.Pump();
        Assert.Equal(["seek 100", "seek 300"], h.Calls);
    }

    [Fact]
    public void PlayerThatCompletesWithoutAFrame_IsGivenTheGracePeriod_ThenTheNextChangeGoesAhead()
    {
        var h = new Harness(new StudioPreviewTimeline(30, 30, [new(30, 900, 0), new(15, 450, 0)]));
        h.SettleAt(101);

        h.Policy.RequestSeek(100);
        h.Pump();
        h.Advance(20);
        h.Frame(Screen, 100);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Policy.RequestSeek(300);
        var deadline = h.Pump();
        Assert.Equal(h.Now + 40, deadline);
        Assert.Equal(["seek 100"], h.Calls);

        h.Advance(40);
        h.Pump();
        Assert.Equal(["seek 100", "seek 300"], h.Calls);
        Assert.Equal(0, h.Policy.Repairs);
    }

    [Fact]
    public void PlayerParkedOutsideItsStream_IsNotWaitedFor()
    {
        // Timeline frames 2 and 4 both lie before the camera starts. Its player is not moved by
        // the change and says nothing, not even SeekCompleted.
        var h = new Harness();
        h.SettleAt(2);

        h.Policy.RequestSeek(4);
        h.Pump();
        Assert.Equal(["seek 4"], h.Calls);
        h.Advance(20);
        h.Frame(Screen, 4);
        h.Completed(Screen);
        h.Pump();

        Assert.Equal(4, Assert.Single(h.Landings).Frame);
        Assert.True(h.Policy.IsIdle);
    }

    [Fact]
    public void PausedSeek_HoldsThePicture_UntilEveryClipIsOnTheNewFrame()
    {
        var h = new Harness();
        h.SettleAt(100);
        Assert.False(h.Policy.HoldPicture);

        h.Policy.RequestSeek(200);
        Assert.False(h.Policy.HoldPicture);
        h.Pump();
        Assert.True(h.Policy.HoldPicture);

        // The camera's frame is in, the screen's is not: drawn now, the picture would be the
        // camera at 194 over the screen at 100.
        h.Advance(30);
        h.Frame(Camera, 194);
        h.Completed(Camera);
        h.Pump();
        Assert.True(h.Policy.HoldPicture);
        Assert.Empty(h.Landings);

        h.Advance(15);
        h.Frame(Screen, 200);
        h.Pump();
        Assert.False(h.Policy.HoldPicture);
        Assert.Equal(200, Assert.Single(h.Landings).Frame);
    }

    [Fact]
    public void Step_HoldsThePicture_UntilEveryClipHasStepped()
    {
        var h = new Harness();
        h.SettleAt(100);

        h.Policy.RequestSeek(101);
        h.Pump();
        Assert.Equal(["step 0", "step 1"], h.Calls);
        Assert.True(h.Policy.HoldPicture);

        h.Advance(8);
        h.Frame(Screen, 101);
        h.Pump();
        Assert.True(h.Policy.HoldPicture);

        h.Advance(4);
        h.Frame(Camera, 95);
        h.Pump();
        Assert.False(h.Policy.HoldPicture);
        Assert.Equal(101, Assert.Single(h.Landings).Frame);
    }

    [Fact]
    public void SeekThatIsReplacedBeforeItsFramesAreIn_KeepsThePictureHeld_UntilTheNextOneLands()
    {
        var h = new Harness();
        h.SettleAt(100);

        h.Policy.RequestSeek(200);
        h.Pump();
        h.Advance(5);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Frame(Camera, 194);
        h.Policy.RequestSeek(300);
        h.Advance(45);
        h.Pump();

        // The screen's frame for 200 was lost and nobody wants 200 any more.
        Assert.Equal(["seek 200", "seek 300"], h.Calls);
        Assert.True(h.Policy.HoldPicture);
        Assert.Empty(h.Landings);

        h.Advance(50);
        h.Deliver(300);
        h.Pump();
        Assert.False(h.Policy.HoldPicture);
        Assert.Equal(300, Assert.Single(h.Landings).Frame);
    }

    [Fact]
    public void CompositeDuringSeeks_LeavesThePictureFree_ExceptOnADetour()
    {
        var players = new FakePlayers();
        var now = 1000L;
        var timeline = new StudioPreviewTimeline(30, 30, [new(30, 900, 0), new(30, 900, 0.2)]);
        var policy = new StudioPreviewSeekPolicy(players, timeline, new StudioPreviewSeekSettings { TicksPerSecond = 1000, CompositeDuringSeeks = true }, () => now);
        policy.OnFrame(Screen, 0, now);
        policy.OnFrame(Camera, 0, now);

        policy.RequestSeek(200);
        policy.Pump();
        Assert.False(policy.HoldPicture);

        // The screen's frame is lost: the detour that fetches it is never drawn.
        now += 5;
        policy.OnSeekCompleted(Screen, now);
        policy.OnSeekCompleted(Camera, now);
        policy.OnFrame(Camera, 194, now);
        now += 40;
        policy.Pump();
        Assert.Equal(["seek 200", "seek 199"], players.Calls);
        Assert.True(policy.HoldPicture);
    }

    [Fact]
    public void OneSeekInFlight_AndTheNewestRequestWins()
    {
        var h = new Harness();

        h.Policy.RequestSeek(100);
        h.Pump();
        h.Policy.RequestSeek(200);
        h.Pump();
        h.Policy.RequestSeek(300);
        h.Policy.RequestSeek(400);
        h.Pump();
        Assert.Equal(["seek 100"], h.Calls);

        h.Advance(70);
        h.Deliver(100);
        h.Pump();

        Assert.Equal(["seek 100", "seek 400"], h.Calls);
        Assert.Equal(100, Assert.Single(h.Landings).Frame);

        h.Advance(70);
        h.Deliver(400);
        h.Pump();

        Assert.Equal([100, 400], h.Landings.Select(l => l.Frame));
        Assert.Equal(2, h.Policy.SeeksIssued);
        Assert.True(h.Policy.IsIdle);
    }

    [Fact]
    public void BurstOfRequests_EndsOnTheLastOne()
    {
        var h = new Harness();

        for (var i = 1; i <= 60; i++)
        {
            h.Policy.RequestSeek(i * 10);
            h.Pump();
            h.Advance(1);
        }

        for (var round = 0; round < 4 && !h.Policy.IsIdle; round++)
        {
            h.Advance(70);
            h.Deliver(h.LastSeek);
            h.Pump();
        }

        Assert.Equal(600, h.Landings[^1].Frame);
        Assert.Equal(600, h.Policy.SettledFrame);
        Assert.True(h.Policy.SeeksIssued <= 3);
    }

    [Fact]
    public void SeekToTheFrameAlreadyShown_DoesNothing()
    {
        var h = new Harness();
        h.SettleAt(100);

        h.Policy.RequestSeek(100);
        h.Pump();

        Assert.Empty(h.Calls);
        Assert.Empty(h.Landings);
        Assert.True(h.Policy.IsIdle);
    }

    [Fact]
    public void Seek_IsClampedToTheRecording()
    {
        var h = new Harness();

        h.Policy.RequestSeek(5000);
        h.Pump();
        Assert.Equal(["seek 899"], h.Calls);

        var other = new Harness();
        other.SettleAt(100);
        other.Policy.RequestSeek(-3);
        other.Pump();
        Assert.Equal(["seek 0"], other.Calls);
    }

    [Fact]
    public void CameraParkedBeforeItsFirstFrame_HasNothingToDeliver()
    {
        var h = new Harness();

        // Timeline frames 0 to 6 all show camera frame 0, which the camera already shows.
        h.Policy.RequestSeek(3);
        h.Pump();
        h.Advance(20);
        h.Frame(Screen, 3);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Pump();

        Assert.Equal(3, Assert.Single(h.Landings).Frame);
        Assert.True(h.Landings[0].Confirmed);
        Assert.Equal(0, h.Policy.Repairs);
    }

    [Fact]
    public void CameraOutsideItsRange_IsNotWaitedFor()
    {
        var h = new Harness();
        h.SettleAt(100);

        // At timeline frame 2 the camera has not started and is not drawn. Its player parks on its
        // first frame and may deliver nothing at all.
        h.Policy.RequestSeek(2);
        h.Pump();
        h.Advance(30);
        h.Frame(Screen, 2);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Pump();

        var landing = Assert.Single(h.Landings);
        Assert.Equal(2, landing.Frame);
        Assert.True(landing.Confirmed);
        Assert.Equal(0, h.Policy.Repairs);
        Assert.Equal(94, h.Policy.ShownFrame(Camera));

        // Back inside its range it has to deliver again: its texture still holds frame 94.
        h.Advance(40);
        h.Pump();
        h.Policy.RequestSeek(10);
        h.Pump();
        h.Advance(30);
        h.Frame(Screen, 10);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Pump();
        Assert.Single(h.Landings);

        h.Frame(Camera, 4);
        h.Pump();
        Assert.Equal([2, 10], h.Landings.Select(l => l.Frame));
    }

    [Fact]
    public void PlayerThatHasNeverDelivered_IsNotNeededWhileItsClipIsOutOfThePicture()
    {
        // A camera that starts after the recording does hands over no first frame when it opens.
        var h = new Harness(cameraDelivered: false);

        h.Policy.RequestSeek(0);
        h.Pump();
        Assert.Equal(["seek 0"], h.Calls);
        h.Advance(5);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Pump();

        var landing = Assert.Single(h.Landings);
        Assert.Equal(0, landing.Frame);
        Assert.True(landing.Confirmed);
        Assert.Equal(0, h.Policy.Repairs);

        // No stepping onto a frame that needs a player whose frame is not known.
        h.Advance(200);
        h.Policy.RequestSeek(5);
        h.Pump();
        h.Advance(50);
        h.Frame(Screen, 5);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Pump();
        Assert.Equal(5, h.Policy.SettledFrame);
        Assert.Equal(-1, h.Policy.ShownFrame(Camera));

        h.Advance(200);
        h.Policy.RequestSeek(6);
        h.Pump();
        Assert.Equal(["seek 0", "seek 5", "seek 6"], h.Calls);
    }

    [Fact]
    public void FirstPositionAfterAnOffsetChange_IsSpentOnAnotherFrame()
    {
        // The players have opened and each has handed over its first frame. Then the camera was
        // given its offset on the clock, and the first position after that is lost on it.
        var h = new Harness();

        h.Policy.RequestFirstSeek(0);
        h.Pump();
        Assert.Equal(["seek 8"], h.Calls);
        Assert.True(h.Policy.HoldPicture);

        // The screen answers. The camera says nothing, and nothing is expected of it.
        h.Advance(40);
        h.Frame(Screen, 8);
        h.Completed(Screen);
        h.Pump();
        h.Advance(39);
        h.Pump();
        Assert.Equal(["seek 8"], h.Calls);

        h.Advance(1);
        h.Pump();
        Assert.Equal(["seek 8", "seek 0"], h.Calls);
        Assert.Equal(0, h.Policy.Repairs);

        h.Advance(40);
        h.Deliver(0);
        h.Pump();

        var landing = Assert.Single(h.Landings);
        Assert.Equal(0, landing.Frame);
        Assert.True(landing.Confirmed);
        Assert.False(h.Policy.HoldPicture);

        // The camera left its stream on the way back and may still answer; then all is quiet.
        h.Advance(40);
        h.Pump();
        Assert.True(h.Policy.IsIdle);
        Assert.Equal(["seek 8", "seek 0"], h.Calls);
    }

    [Fact]
    public void FirstPositionAfterAnOffsetChange_IsStillOwedByThePlayersWhoseOffsetDidNotChange()
    {
        var h = new Harness();

        h.Policy.RequestFirstSeek(0);
        h.Pump();

        // The screen's seek completes without a frame: that one is a lost seek like any other,
        // and the way on waits for it, or its late frame would be taken for frame 0.
        h.Advance(20);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Advance(39);
        h.Pump();
        Assert.Equal(["seek 8"], h.Calls);

        h.Advance(1);
        h.Pump();
        Assert.Equal(["seek 8", "seek 0"], h.Calls);
    }

    [Fact]
    public void FirstPositionAfterOpening_IsSpentOnAnotherFrame_AlsoWhenNoOffsetChanged()
    {
        // Screen and camera start together, so nothing was changed after the players opened.
        // What a player hands over for its first position still does not count: measured on the
        // graphics hardware, the screen picture came out blank in about one open in seven.
        var h = new Harness(new StudioPreviewTimeline(30, 30, [new(30, 900, 0), new(30, 900, 0)]));

        h.Policy.RequestFirstSeek(0);
        h.Pump();
        Assert.Equal(["seek 8"], h.Calls);
        Assert.True(h.Policy.HoldPicture);

        // Neither player is expected to lose the position, so both are waited for.
        h.Advance(30);
        h.Frame(Screen, 8);
        h.Completed(Screen);
        h.Pump();
        h.Advance(100);
        h.Pump();
        Assert.Equal(["seek 8"], h.Calls);

        h.Frame(Camera, 8);
        h.Completed(Camera);
        h.Pump();
        h.Advance(39);
        h.Pump();
        Assert.Equal(["seek 8"], h.Calls);

        h.Advance(1);
        h.Pump();
        Assert.Equal(["seek 8", "seek 0"], h.Calls);
        Assert.Empty(h.Landings);
        Assert.True(h.Policy.HoldPicture);

        // The picture is let go on a frame the players were sent to and came back with.
        h.Advance(30);
        h.Deliver(0);
        h.Pump();
        var landing = Assert.Single(h.Landings);
        Assert.Equal(0, landing.Frame);
        Assert.True(landing.Confirmed);
        Assert.False(h.Policy.HoldPicture);
        Assert.Equal(0, h.Policy.Repairs);
        Assert.Equal(0, h.Policy.DetoursUnanswered);
    }

    [Fact]
    public void FirstPositionAfterOpening_ASecondFrameForIt_KeepsTheWayBackWaiting()
    {
        // The screen alone. Its answer to the first position is a blank frame, and the right one
        // follows a moment later: the way back starts only when the player has gone quiet.
        var h = new Harness(new StudioPreviewTimeline(30, 30, [new(30, 900, 0)]));

        h.Policy.RequestFirstSeek(0);
        h.Pump();
        Assert.Equal(["seek 8"], h.Calls);

        h.Advance(20);
        h.Frame(Screen, 8);
        h.Completed(Screen);
        h.Pump();
        h.Advance(30);
        h.Frame(Screen, 8);
        h.Pump();
        h.Advance(39);
        h.Pump();
        Assert.Equal(["seek 8"], h.Calls);

        h.Advance(1);
        h.Pump();
        Assert.Equal(["seek 8", "seek 0"], h.Calls);

        h.Advance(30);
        h.Deliver(0);
        h.Pump();
        Assert.Equal(0, Assert.Single(h.Landings).Frame);
    }

    [Fact]
    public void FirstPositionAfterOpening_InARecordingOfAFewFrames_IsSpentOnTheNextFrame()
    {
        // Five frames: there is no frame eight away to go to.
        var h = new Harness(new StudioPreviewTimeline(5 / 30.0, 30, [new(30, 5, 0)]));

        h.Policy.RequestFirstSeek(0);
        h.Pump();
        Assert.Equal(["seek 1"], h.Calls);

        h.Advance(20);
        h.DeliverDetour(1);
        Assert.Equal(["seek 1", "seek 0"], h.Calls);

        h.Advance(20);
        h.Deliver(0);
        h.Pump();
        Assert.Equal(0, Assert.Single(h.Landings).Frame);
    }

    [Fact]
    public void FirstPositionAfterOpening_IsNotSpentOnTheEndOfAStream_IfItCanBeHelped()
    {
        // Nine frames: the frame eight away is the last, and a player that has read its stream to
        // the end does not do its next seek properly. The next frame will do as well.
        var h = new Harness(new StudioPreviewTimeline(9 / 30.0, 30, [new(30, 9, 0)]));

        h.Policy.RequestFirstSeek(0);
        h.Pump();

        Assert.Equal(["seek 1"], h.Calls);
        Assert.Equal(0, h.Policy.EndRecoveries);
    }

    [Fact]
    public void FirstPositionAfterOpening_OfTwoFrames_IsSpentOnTheLastFrame_ThereBeingNoOther()
    {
        var h = new Harness(new StudioPreviewTimeline(2 / 30.0, 30, [new(30, 2, 0)]));

        h.Policy.RequestFirstSeek(0);
        h.Pump();

        Assert.Equal(["seek 1"], h.Calls);
    }

    [Fact]
    public void FirstPositionAfterOpening_InARecordingOfOneFrame_IsTheFrameItself()
    {
        var h = new Harness(new StudioPreviewTimeline(1 / 30.0, 30, [new(30, 1, 0)]));

        h.Policy.RequestFirstSeek(0);
        h.Pump();

        // Nowhere else to go, and the player shows the frame already: the clock is put on it.
        Assert.Equal(["seek 0"], h.Calls);
        Assert.Equal(0, Assert.Single(h.Landings).Frame);
    }

    [Fact]
    public void LostSeek_IsRecognisedFromSeekCompleted_AndRepairedByADetour()
    {
        var h = new Harness();

        h.Policy.RequestSeek(100);
        h.Pump();
        h.Advance(5);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Frame(Camera, 94);
        var deadline = h.Pump();

        // SeekCompleted without a frame: the screen gets the grace period, no longer.
        Assert.Equal(h.Now + 40, deadline);
        h.Advance(39);
        h.Pump();
        Assert.Equal(["seek 100"], h.Calls);

        h.Advance(1);
        h.Pump();
        Assert.Equal(["seek 100", "seek 99"], h.Calls);
        Assert.True(h.Policy.HoldPicture);
        Assert.Empty(h.Landings);

        // The way back waits until the players have said nothing for the grace period.
        h.Advance(60);
        h.Deliver(99);
        h.Pump();
        h.Advance(39);
        h.Pump();
        Assert.Equal(["seek 100", "seek 99"], h.Calls);

        h.Advance(1);
        h.Pump();
        Assert.Equal(["seek 100", "seek 99", "seek 100"], h.Calls);
        Assert.True(h.Policy.HoldPicture);

        h.Advance(60);
        h.Deliver(100);
        h.Pump();

        var landing = Assert.Single(h.Landings);
        Assert.Equal(100, landing.Frame);
        Assert.True(landing.Confirmed);
        Assert.Equal(1, landing.Repairs);
        Assert.Equal(1000, landing.RequestedAt);
        Assert.Equal(1, h.Policy.Repairs);
        Assert.Equal(1, h.Policy.LossesSeenEarly);
        Assert.Equal(0, h.Policy.RepairFailures);
        Assert.False(h.Policy.HoldPicture);
    }

    [Fact]
    public void FrameThatIsBeingHandedOver_IsNotCalledLost()
    {
        // Measured on a busy machine: SeekCompleted came, and the frame's handover, which had
        // started before, took another 80 ms.
        var h = new Harness();
        h.SettleAt(100);

        h.Policy.RequestSeek(200);
        h.Pump();
        h.Advance(5);
        h.Frame(Screen, 200);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Players.Delivering[Camera] = true;
        h.Advance(40);
        h.Pump();
        h.Advance(45);
        h.Pump();
        Assert.Equal(["seek 200"], h.Calls);

        h.Frame(Camera, 194);
        h.Players.Delivering[Camera] = false;
        h.Pump();
        Assert.Equal(200, Assert.Single(h.Landings).Frame);
        Assert.Equal(0, h.Policy.Repairs);
    }

    [Fact]
    public void LateFrameOfAnEarlierChange_ThatTakesAClipsPicture_MakesTheClipOweItsFrameAgain()
    {
        // As traced on a busy machine. The screen has frame 128, the camera's is lost, and the
        // repair goes by way of 127. The screen's frame for 127 is slow and arrives on the way
        // back, when the screen was thought to show 128 still.
        var h = new Harness();
        h.SettleAt(100);

        h.Policy.RequestSeek(128);
        h.Pump();
        h.Advance(10);
        h.Frame(Screen, 128);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Advance(40);
        h.Pump();
        Assert.Equal(["seek 128", "seek 127"], h.Calls);

        h.Advance(50);
        h.Frame(Camera, 121);
        h.Completed(Camera);
        h.Completed(Screen);
        h.Pump();
        h.Advance(40);
        h.Pump();
        Assert.Equal(["seek 128", "seek 127", "seek 128"], h.Calls);

        h.Advance(20);
        h.Frame(Screen, 127);
        h.Advance(20);
        h.Frame(Camera, 122);
        h.Completed(Camera);
        h.Pump();
        Assert.Empty(h.Landings);
        Assert.True(h.Policy.HoldPicture);

        h.Advance(50);
        h.Frame(Screen, 128);
        h.Completed(Screen);
        h.Pump();
        var landing = Assert.Single(h.Landings);
        Assert.Equal(128, landing.Frame);
        Assert.Equal(128, h.Policy.ShownFrame(Screen));
        Assert.False(h.Policy.HoldPicture);
    }

    [Fact]
    public void LateFrameOfAnEarlierChange_AfterThePositionWasReported_IsNotDrawn_AndIsPutRight()
    {
        var h = new Harness();
        h.SettleAt(100);

        h.Policy.RequestSeek(200);
        h.Pump();
        h.Advance(30);
        h.Frame(Screen, 200);
        h.Frame(Camera, 194);
        h.Pump();
        Assert.Equal(200, Assert.Single(h.Landings).Frame);
        Assert.False(h.Policy.HoldPicture);

        // The screen's texture now holds frame 100 again.
        h.Advance(5);
        h.Frame(Screen, 100);
        Assert.True(h.Policy.HoldPicture);
        Assert.Equal(1, h.Policy.StrayFrames);

        // The clock is on 200 already, so the way back to it is by another frame.
        h.Completed(Screen);
        h.Completed(Camera);
        h.Pump();
        Assert.Equal(["seek 200", "seek 199"], h.Calls);

        h.Advance(30);
        h.DeliverDetour(199);
        h.Advance(30);
        h.Deliver(200);
        h.Pump();
        Assert.Equal([200, 200], h.Landings.Select(l => l.Frame));
        Assert.Equal(200, h.Policy.ShownFrame(Screen));
        Assert.False(h.Policy.HoldPicture);
    }

    [Fact]
    public void LateFrameOfAnEarlierChange_WhileAtRest_IsNotDrawn_AndIsPutRight()
    {
        var h = new Harness();
        h.SettleAt(100);
        Assert.True(h.Policy.IsIdle);

        h.Frame(Screen, 40);
        Assert.True(h.Policy.HoldPicture);
        Assert.False(h.Policy.IsIdle);

        h.Pump();
        Assert.Equal(["seek 99"], h.Calls);
        h.Advance(30);
        h.DeliverDetour(99);
        h.Advance(30);
        h.Deliver(100);
        h.Pump();
        Assert.Equal(100, Assert.Single(h.Landings).Frame);
        Assert.Equal(100, h.Policy.ShownFrame(Screen));
        Assert.False(h.Policy.HoldPicture);
    }

    [Fact]
    public void LateFrameOfAnEarlierChange_ForAClipThatIsNotInThePicture_ChangesNothing()
    {
        var h = new Harness();
        h.SettleAt(2);

        // At timeline frame 2 the camera has not started and is not drawn.
        h.Frame(Camera, 50);
        h.Pump();
        Assert.False(h.Policy.HoldPicture);
        Assert.Empty(h.Calls);
        Assert.True(h.Policy.IsIdle);
    }

    [Fact]
    public void Step_TakesOnlyAFrameNextToTheOneItSteppedFrom()
    {
        var h = new Harness();
        h.SettleAt(100);

        h.Policy.RequestSeek(101);
        h.Pump();
        h.Advance(5);

        // The camera's stepped frame reports the position the player had. The screen's is a late
        // frame of an earlier change.
        h.Frame(Camera, 94);
        h.Frame(Screen, 300);
        h.Pump();
        Assert.Empty(h.Landings);
        Assert.Equal(95, h.Policy.ShownFrame(Camera));
        Assert.Equal(300, h.Policy.ShownFrame(Screen));

        h.Advance(245);
        h.Pump();
        Assert.Equal(["step 0", "step 1", "seek 100"], h.Calls);
    }

    [Fact]
    public void LostSeek_WithoutSeekCompleted_IsRecognisedByTheTimeout()
    {
        var h = new Harness();

        h.Policy.RequestSeek(100);
        var deadline = h.Pump();
        Assert.Equal(h.Now + 2000, deadline);

        h.Advance(1999);
        h.Pump();
        Assert.Equal(["seek 100"], h.Calls);

        h.Advance(1);
        h.Pump();

        Assert.Equal(["seek 100", "seek 99"], h.Calls);
        Assert.Equal(1, h.Policy.Repairs);
        Assert.Equal(0, h.Policy.LossesSeenEarly);
    }

    [Fact]
    public void LostSeek_WaitsForTheOtherPlayer_BeforeTheDetour()
    {
        var h = new Harness();

        h.Policy.RequestSeek(100);
        h.Pump();
        h.Advance(5);
        h.Completed(Screen);
        h.Advance(45);
        h.Pump();

        // The screen is lost, but the camera is still decoding: no position change yet.
        Assert.Equal(["seek 100"], h.Calls);

        h.Advance(30);
        h.Frame(Camera, 94);
        h.Completed(Camera);
        h.Pump();
        Assert.Equal(["seek 100", "seek 99"], h.Calls);
    }

    [Fact]
    public void RepairThatFails_IsRetriedWithLongerDetours_ThenReported()
    {
        var h = new Harness();

        h.Policy.RequestSeek(100);
        h.Pump();

        // The camera always delivers; the screen never does.
        for (var leg = 0; leg < 7; leg++)
        {
            h.Advance(20);
            h.Frame(Camera, Math.Max(0, h.LastSeek - Lag));
            h.Completed(Camera);
            h.Completed(Screen);
            h.Advance(40);
            h.Pump();
        }

        Assert.Equal(["seek 100", "seek 99", "seek 100", "seek 92", "seek 100", "seek 70", "seek 100"], h.Calls);
        var landing = Assert.Single(h.Landings);
        Assert.Equal(100, landing.Frame);
        Assert.False(landing.Confirmed);
        Assert.Equal(3, landing.Repairs);
        Assert.Equal(1, h.Policy.Repairs);
        Assert.Equal(1, h.Policy.RepairFailures);
        Assert.False(h.Policy.HoldPicture);
        Assert.True(h.Policy.IsIdle);

        // Asking again is not treated as already shown. The clock is on that position already and
        // giving it the same one again would move nothing, so it goes by way of another frame.
        h.Policy.RequestSeek(100);
        h.Pump();
        Assert.Equal("seek 99", h.Calls[^1]);
        Assert.Equal(8, h.Calls.Count);
        Assert.True(h.Policy.HoldPicture);
    }

    [Fact]
    public void ForgottenFrame_IsFetchedAgain_ByWayOfAnotherFrame()
    {
        var h = new Harness();
        h.SettleAt(100);

        // The camera's texture was recreated and could not be refilled.
        h.Policy.Forget(Camera);
        h.Policy.RequestSeek(100);
        h.Pump();
        Assert.Equal(["seek 99"], h.Calls);
        Assert.True(h.Policy.HoldPicture);

        h.Advance(50);
        h.DeliverDetour(99);
        Assert.Equal(["seek 99", "seek 100"], h.Calls);

        h.Advance(50);
        h.Deliver(100);
        h.Pump();
        Assert.Equal(100, Assert.Single(h.Landings).Frame);
        Assert.Equal(94, h.Policy.ShownFrame(Camera));
        Assert.False(h.Policy.HoldPicture);
        Assert.Equal(0, h.Policy.Repairs);
    }

    [Fact]
    public void ForgettingTheScreensPicture_LeavesNoFrameForTheScene_UntilTheScreenDeliversAgain()
    {
        var h = new Harness();
        h.SettleAt(100);
        Assert.Equal(100, h.Policy.ShownTimelineFrame);

        // The screen's texture was recreated and could not be refilled: there is no picture to
        // draw a scene for, and the scene's frame goes with it.
        h.Policy.Forget(Screen);
        Assert.Equal(-1, h.Policy.ShownFrame(Screen));
        Assert.Equal(-1, h.Policy.ShownTimelineFrame);

        h.Policy.RequestSeek(100);
        h.Pump();
        Assert.Equal(["seek 99"], h.Calls);

        h.Advance(50);
        h.DeliverDetour(99);
        Assert.Equal(["seek 99", "seek 100"], h.Calls);

        h.Advance(50);
        h.Deliver(100);
        h.Pump();
        Assert.Equal(100, Assert.Single(h.Landings).Frame);
        Assert.Equal(100, h.Policy.ShownFrame(Screen));
        Assert.Equal(100, h.Policy.ShownTimelineFrame);
    }

    [Fact]
    public void OnTheGrid_AScreenClipThatEndsBeforeTheRecording_RestsOnItsLastFrame_AndTheSceneIsThatFrame()
    {
        // The file is a frame short of what the project says: 899 frames in a recording of 900.
        var h = new Harness(new StudioPreviewTimeline(30, 30, [new(30, 899, 0)]));

        h.SettleAt(899);

        // The players were brought to frame 899 of the timeline, and the screen shows the last
        // frame it has. On the grid the scene is drawn for the frame the screen delivered, as it
        // always was, and not for the frame the players were brought to: that is what a screen
        // goes by that counts in frames of its file, and only such a screen.
        Assert.Equal(898, h.Policy.ShownFrame(Screen));
        Assert.Equal(898, h.Policy.ShownTimelineFrame);
    }

    [Fact]
    public void RepairOfAPositionNobodyWantsAnyMore_IsDropped()
    {
        var h = new Harness();

        h.Policy.RequestSeek(100);
        h.Pump();
        h.Advance(5);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Frame(Camera, 94);
        h.Policy.RequestSeek(200);
        h.Advance(45);
        h.Pump();

        Assert.Equal(["seek 100", "seek 200"], h.Calls);
        Assert.Empty(h.Landings);
        Assert.Equal(0, h.Policy.Repairs);

        h.Advance(60);
        h.Deliver(200);
        h.Pump();
        Assert.Equal(200, Assert.Single(h.Landings).Frame);
    }

    [Fact]
    public void Detour_GoesWhereTheLostPlayerShowsAnotherFrame()
    {
        var h = new Harness();
        h.SettleAt(30);

        // Timeline frames 5 and 6 both show camera frame 0, so going to 5 and back would not make
        // the camera deliver frame 0 again. Frame 7 shows camera frame 1.
        h.Policy.RequestSeek(6);
        h.Pump();
        h.Advance(10);
        h.Frame(Screen, 6);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Advance(40);
        h.Pump();

        Assert.Equal(["seek 6", "seek 7"], h.Calls);
    }

    [Fact]
    public void Play_WhileASeekIsPending_StartsAtThePendingTarget()
    {
        var h = new Harness();

        h.Policy.RequestSeek(100);
        h.Policy.SetPlaying(true);
        h.Pump();
        Assert.Equal(["seek 100"], h.Calls);
        Assert.False(h.Policy.IsPlaying);
        Assert.False(h.Policy.IsIdle);

        h.Advance(80);
        h.Deliver(100);
        h.Pump();

        Assert.Equal(["seek 100", "resume"], h.Calls);
        Assert.Equal(100, Assert.Single(h.Landings).Frame);
        Assert.True(h.Policy.IsPlaying);
        Assert.Equal(-1, h.Policy.SettledFrame);
    }

    [Fact]
    public void Play_WhileASeekIsInFlight_WaitsForTheNewestPosition()
    {
        var h = new Harness();

        h.Policy.RequestSeek(100);
        h.Pump();
        h.Policy.SetPlaying(true);
        h.Policy.RequestSeek(250);
        h.Pump();
        Assert.Equal(["seek 100"], h.Calls);

        h.Advance(80);
        h.Deliver(100);
        h.Pump();
        Assert.Equal(["seek 100", "seek 250"], h.Calls);

        h.Advance(80);
        h.Deliver(250);
        h.Pump();
        Assert.Equal(["seek 100", "seek 250", "resume"], h.Calls);
    }

    [Fact]
    public void Play_WhenNothingIsPending_StartsAtOnce()
    {
        var h = new Harness();
        h.SettleAt(100);

        h.Policy.SetPlaying(true);
        h.Pump();

        Assert.Equal(["resume"], h.Calls);
        Assert.True(h.Policy.IsPlaying);
        Assert.True(h.Policy.AcceptsPlaybackFrames);
    }

    [Fact]
    public void Play_ThatIsRefused_LeavesTheClockStopped()
    {
        var h = new Harness();
        h.SettleAt(100);
        h.Players.RefuseResume = true;

        h.Policy.SetPlaying(true);
        h.Pump();
        h.Pump();

        Assert.Equal(["resume"], h.Calls);
        Assert.False(h.Policy.IsPlaying);
        Assert.True(h.Policy.IsIdle);
    }

    [Fact]
    public void Pause_BringsThePlayersOntoTheFrameTheScreenShows()
    {
        var h = new Harness();
        h.Play(from: 100);

        // The clips come to rest a frame apart.
        h.Advance(1650);
        h.Frame(Screen, 150);
        h.Frame(Camera, 143);
        h.Policy.SetPlaying(false);
        Assert.Equal(["pause"], h.Calls);
        Assert.False(h.Policy.IsPlaying);
        Assert.True(h.Policy.AcceptsPlaybackFrames);

        var deadline = h.Pump();
        Assert.Equal(h.Now + 40, deadline);
        Assert.Equal(["pause"], h.Calls);

        h.Advance(40);
        h.Pump();
        Assert.Equal(["pause", "seek 150"], h.Calls);
        Assert.False(h.Policy.AcceptsPlaybackFrames);

        h.Advance(30);
        h.Completed(Screen);
        h.Frame(Camera, 144);
        h.Completed(Camera);
        h.Pump();

        // The first change after the clock ran: reported once the players have gone quiet.
        Assert.Empty(h.Landings);
        Assert.True(h.Policy.HoldPicture);
        h.Quiet();

        var landing = Assert.Single(h.Landings);
        Assert.Equal(150, landing.Frame);
        Assert.Equal(StudioPreviewLandingKind.Snap, landing.Kind);
        Assert.Equal(150, h.Policy.SettledFrame);
        Assert.Equal(144, h.Policy.ShownFrame(Camera));
        Assert.False(h.Policy.HoldPicture);
    }

    [Fact]
    public void StoppedAndWantedAgainBeforeTheNextLook_BringsThePlayersToTheFrameShown_ThenStartsTheClock()
    {
        var h = new Harness();
        h.Play(from: 100);
        h.Advance(500);
        h.Frame(Screen, 115);
        h.Frame(Camera, 109);

        // The owner's Pause() stopped the clock and its Play() came before the policy was told of
        // either. It is told of the stop first and of what is wanted after: told nothing, it
        // would go on taking the clock for running.
        h.Policy.SetPlaying(false);
        h.Policy.SetPlaying(true);
        Assert.Equal(["pause"], h.Calls);
        Assert.False(h.Policy.IsPlaying);
        Assert.False(h.Policy.IsIdle);

        // As after any stop, the players are first brought onto the frame the screen shows.
        h.Advance(40);
        h.Pump();
        Assert.Equal(["pause", "seek 115"], h.Calls);
        Assert.False(h.Policy.IsPlaying);

        h.Advance(30);
        h.Deliver(115);
        h.Pump();
        h.Quiet();
        Assert.Equal(115, Assert.Single(h.Landings).Frame);

        // Then the clock runs again.
        h.Pump();
        Assert.Equal(["pause", "seek 115", "resume"], h.Calls);
        Assert.True(h.Policy.IsPlaying);
    }

    [Fact]
    public void FirstChangeAfterTheClockRan_IsReportedOnlyWhenThePlayersHaveGoneQuiet()
    {
        var h = new Harness();
        h.Play(from: 100);
        h.Advance(500);
        h.Frame(Screen, 115);
        h.Frame(Camera, 109);
        h.Policy.SetPlaying(false);
        h.Advance(40);
        h.Pump();
        Assert.Equal(["pause", "seek 115"], h.Calls);

        // The screen's player had frame 116 ready when the clock stopped. It hands that over as
        // its first answer, with the position it was just given on it: to the policy it is frame
        // 115, which the player was believed to show and did not owe.
        h.Advance(3);
        h.Frame(Screen, 115);
        h.Pump();
        Assert.Empty(h.Landings);
        Assert.True(h.Policy.HoldPicture);

        h.Advance(20);
        h.Frame(Camera, 109);
        h.Completed(Camera);
        h.Pump();
        Assert.Empty(h.Landings);

        // The frame that belongs to the position comes with SeekCompleted.
        h.Advance(30);
        h.Frame(Screen, 115);
        h.Completed(Screen);
        h.Pump();
        Assert.Empty(h.Landings);
        Assert.True(h.Policy.HoldPicture);
        Assert.True(h.Policy.IsSeeking);

        h.Advance(39);
        h.Pump();
        Assert.Empty(h.Landings);

        h.Advance(1);
        h.Pump();
        var landing = Assert.Single(h.Landings);
        Assert.Equal(115, landing.Frame);
        Assert.Equal(StudioPreviewLandingKind.Snap, landing.Kind);
        Assert.False(h.Policy.HoldPicture);
        Assert.True(h.Policy.IsIdle);

        // The screen's player handed over two frames for one position.
        Assert.Equal(1, h.Policy.SecondAnswers);
    }

    [Fact]
    public void FirstChangeAfterTheClockRan_WaitsForAFrameThatIsBeingHandedOver()
    {
        var h = new Harness();
        h.Play(from: 100);
        h.Advance(500);
        h.Frame(Screen, 115);
        h.Frame(Camera, 109);
        h.Policy.SetPlaying(false);
        h.Advance(40);
        h.Pump();
        h.Advance(1);
        h.Completed(Screen);
        h.Completed(Camera);

        // Quiet for the grace period, but a player is in the middle of handing a frame over.
        h.Players.Delivering[Screen] = true;
        h.Advance(40);
        h.Pump();
        Assert.Empty(h.Landings);
        Assert.True(h.Policy.HoldPicture);

        // It has, and nothing follows it.
        h.Frame(Screen, 115);
        h.Players.Delivering[Screen] = false;
        h.Pump();
        Assert.Empty(h.Landings);
        h.Quiet();
        Assert.Equal(115, Assert.Single(h.Landings).Frame);
        Assert.False(h.Policy.HoldPicture);
    }

    [Fact]
    public void SecondChangeAfterTheClockRan_IsReportedWhenItsFramesAreIn()
    {
        var h = new Harness();
        h.Play(from: 100);
        h.Advance(500);
        h.Frame(Screen, 115);
        h.Frame(Camera, 109);
        h.Policy.SetPlaying(false);
        h.Advance(40);
        h.Pump();
        h.Advance(1);
        h.Deliver(115);
        h.Pump();
        h.Quiet();
        Assert.Equal(StudioPreviewLandingKind.Snap, Assert.Single(h.Landings).Kind);
        h.Landings.Clear();

        // The players have answered a change since the clock ran: what they hand over now is
        // what they are asked for.
        h.Advance(100);
        h.Policy.RequestSeek(300);
        h.Pump();
        Assert.True(h.Policy.HoldPicture);
        h.Advance(60);
        h.Frame(Screen, 300);
        h.Frame(Camera, 294);
        h.Pump();
        Assert.Equal(300, Assert.Single(h.Landings).Frame);
        Assert.False(h.Policy.HoldPicture);
    }

    [Fact]
    public void FirstChangeAfterTheClockRan_ThatIsReplaced_IsNotReported()
    {
        var h = new Harness();
        h.Play(from: 100);
        h.Advance(500);
        h.Frame(Screen, 115);
        h.Frame(Camera, 109);
        h.Policy.SetPlaying(false);
        h.Advance(40);
        h.Pump();
        Assert.Equal(["pause", "seek 115"], h.Calls);

        // Asked for while the players are being brought onto the frame the screen shows.
        h.Advance(5);
        h.Policy.RequestSeek(300);
        h.Advance(1);
        h.Deliver(115);
        h.Pump();
        Assert.Equal(["pause", "seek 115"], h.Calls);
        Assert.True(h.Policy.HoldPicture);

        h.Quiet();
        Assert.Empty(h.Landings);
        Assert.Equal(["pause", "seek 115", "seek 300"], h.Calls);
        Assert.True(h.Policy.HoldPicture);
        Assert.True(h.Policy.IsSeeking);

        h.Advance(60);
        h.Deliver(300);
        h.Pump();
        var landing = Assert.Single(h.Landings);
        Assert.Equal(300, landing.Frame);
        Assert.Equal(StudioPreviewLandingKind.Seek, landing.Kind);
        Assert.False(h.Policy.HoldPicture);
    }

    [Fact]
    public void StepAskedForWhileThePlayersAreBroughtToRestAfterAPause_IsStillAStep()
    {
        var h = new Harness();
        h.Play(from: 100);
        h.Advance(500);
        h.Frame(Screen, 115);
        h.Frame(Camera, 109);
        h.Policy.SetPlaying(false);
        h.Advance(40);
        h.Pump();
        Assert.Equal(["pause", "seek 115"], h.Calls);

        // The next frame is asked for while the players are being brought onto frame 115.
        h.Advance(5);
        h.Policy.RequestSeek(116);
        h.Advance(1);
        h.Deliver(115);
        h.Pump();
        h.Quiet();

        // Nobody is told about frame 115, but the players rest on it, and the step starts from there.
        Assert.Empty(h.Landings);
        Assert.Equal(["pause", "seek 115", "step 0", "step 1"], h.Calls);
        Assert.True(h.Policy.HoldPicture);

        h.Advance(15);
        h.Frame(Screen, 116);
        h.Frame(Camera, 110);
        h.Pump();
        var landing = Assert.Single(h.Landings);
        Assert.Equal(116, landing.Frame);
        Assert.Equal(StudioPreviewLandingKind.Step, landing.Kind);
        Assert.False(h.Policy.HoldPicture);
    }

    [Fact]
    public void Pause_WaitsForAFrameThatIsBeingHandedOver()
    {
        var h = new Harness();
        h.Play(from: 100);
        h.Advance(500);
        h.Frame(Screen, 114);
        h.Frame(Camera, 108);
        h.Policy.SetPlaying(false);

        // The time for a frame on its way is up, but a player is in the middle of handing one
        // over: it set out before the clock stopped, and the players are to come to rest on it.
        h.Players.Delivering[Screen] = true;
        h.Advance(40);
        h.Pump();
        h.Advance(30);
        h.Pump();
        Assert.Equal(["pause"], h.Calls);

        h.Frame(Screen, 115);
        h.Players.Delivering[Screen] = false;
        h.Pump();
        Assert.Equal(["pause", "seek 115"], h.Calls);
    }

    [Fact]
    public void Pause_DoesNotWaitForeverForAFrameThatIsBeingHandedOver()
    {
        var h = new Harness();
        h.Play(from: 100);
        h.Advance(500);
        h.Frame(Screen, 114);
        h.Frame(Camera, 108);
        h.Policy.SetPlaying(false);
        h.Players.Delivering[Screen] = true;

        h.Advance(499);
        h.Pump();
        Assert.Equal(["pause"], h.Calls);

        h.Advance(1);
        h.Pump();
        Assert.Equal(["pause", "seek 114"], h.Calls);
    }

    [Fact]
    public void Pause_UsesAFrameThatWasStillOnItsWay()
    {
        var h = new Harness();
        h.Play(from: 100);

        h.Advance(500);
        h.Frame(Screen, 115);
        h.Frame(Camera, 109);
        h.Policy.SetPlaying(false);
        h.Advance(8);
        h.Frame(Screen, 116);
        h.Advance(40);
        h.Pump();

        Assert.Equal(["pause", "seek 116"], h.Calls);
    }

    [Fact]
    public void Pause_FollowedByASeek_GoesStraightThere()
    {
        var h = new Harness();
        h.Play(from: 100);

        h.Advance(500);
        h.Frame(Screen, 115);
        h.Frame(Camera, 109);
        h.Policy.SetPlaying(false);
        h.Policy.RequestSeek(300);
        h.Pump();
        Assert.Equal(["pause"], h.Calls);

        h.Advance(40);
        h.Pump();
        Assert.Equal(["pause", "seek 300"], h.Calls);

        h.Advance(70);
        h.Deliver(300);
        h.Pump();

        // It is the first change after the clock ran.
        Assert.Empty(h.Landings);
        Assert.True(h.Policy.HoldPicture);
        h.Quiet();
        Assert.Equal(StudioPreviewLandingKind.Seek, Assert.Single(h.Landings).Kind);
        Assert.False(h.Policy.HoldPicture);
    }

    [Fact]
    public void WithoutHoldingTheFirstChangeAfterPlaying_ItIsReportedAsSoonAsItsFramesAreThere()
    {
        var h = new Harness(settings: new StudioPreviewSeekSettings { TicksPerSecond = 1000, HoldFirstChangeAfterPlaying = false });
        h.Play(from: 100);

        h.Advance(500);
        h.Frame(Screen, 115);
        h.Frame(Camera, 109);
        h.Policy.SetPlaying(false);
        h.Policy.RequestSeek(300);
        h.Advance(40);
        h.Pump();
        Assert.Equal(["pause", "seek 300"], h.Calls);

        h.Advance(70);
        h.Frame(Screen, 300);
        h.Frame(Camera, 294);
        h.Pump();

        // Not held until the players have gone quiet: this is what the rule is measured against.
        Assert.Equal(StudioPreviewLandingKind.Seek, Assert.Single(h.Landings).Kind);
        Assert.False(h.Policy.HoldPicture);

        // A second frame for the same change is still counted: the first of the two is the one
        // that was drawn, and may have been a frame nobody asked for.
        h.Advance(8);
        h.Frame(Screen, 300);
        Assert.Equal(1, h.Policy.SecondAnswers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pause_AfterAFrameThatWasShownUnsure_HasThatClipsFrameBroughtAnew(bool unsure)
    {
        var h = new Harness();
        h.Play(from: 100);

        // The clips come to rest on frames that go together. Of the screen's it is not certain
        // that it is the frame it was shown as.
        h.Advance(1650);
        h.Policy.OnFrame(Screen, 150, h.Now, unsure);
        h.Frame(Camera, 144);
        h.Policy.SetPlaying(false);
        h.Advance(40);
        h.Pump();
        Assert.Equal(["pause", "seek 150"], h.Calls);

        // The players are where the clock was put, as far as they know, and hand nothing over.
        h.Advance(30);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Pump();
        h.Quiet();

        if (!unsure)
        {
            var landing = Assert.Single(h.Landings);
            Assert.Equal(150, landing.Frame);
            Assert.Equal(StudioPreviewLandingKind.Snap, landing.Kind);
            Assert.Equal(["pause", "seek 150"], h.Calls);
            return;
        }

        // Not taken for reached. The screen's frame is fetched by way of another frame.
        Assert.Empty(h.Landings);
        Assert.True(h.Policy.HoldPicture);
        Assert.Equal(3, h.Calls.Count);
        var detour = h.LastSeek;
        Assert.NotEqual(150, detour);

        h.Advance(50);
        h.DeliverDetour(detour);
        Assert.Equal("seek 150", h.Calls[^1]);
        h.Advance(50);
        h.Deliver(150);
        h.Pump();
        h.Quiet();

        var back = Assert.Single(h.Landings);
        Assert.Equal(150, back.Frame);
        Assert.True(back.Confirmed);
        Assert.False(h.Policy.HoldPicture);

        // The frame that came for it is certain, and the next pause finds nothing to fetch.
        Assert.Equal(150, h.Policy.ShownFrame(Screen));
    }

    [Fact]
    public void ASeekWhilePlaying_ToTheFrameAClipWasTakenToShow_BringsItAnew_WhenItsPictureIsUncertain()
    {
        var h = new Harness();
        h.Play(from: 100);
        h.Advance(1650);
        h.Frame(Screen, 150);
        h.Frame(Camera, 144);

        // The owner has put a later frame on the screen's picture without knowing which one it
        // is. Then the very frame the screen delivered last is asked for.
        h.Policy.OnPictureUncertain(Screen);
        h.Policy.RequestSeek(150);
        h.Pump();
        h.Advance(40);
        h.Pump();
        Assert.Equal(["pause", "seek 150"], h.Calls);

        // The players are where the clock was put, as far as they know, and hand nothing over.
        h.Advance(30);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Pump();
        h.Quiet();

        // Not taken for reached: the screen's frame is fetched by way of another frame.
        Assert.Empty(h.Landings);
        Assert.True(h.Policy.HoldPicture);
        Assert.Equal(3, h.Calls.Count);
        var detour = h.LastSeek;
        Assert.NotEqual(150, detour);

        h.Advance(50);
        h.DeliverDetour(detour);
        Assert.Equal("seek 150", h.Calls[^1]);
        h.Advance(50);
        h.Deliver(150);
        h.Pump();
        h.Quiet();

        var landing = Assert.Single(h.Landings);
        Assert.Equal(150, landing.Frame);
        Assert.True(landing.Confirmed);
    }

    [Fact]
    public void AFrameDeliveredAfterThePictureWasUncertain_TakesThatBack()
    {
        var h = new Harness();
        h.Play(from: 100);
        h.Advance(1650);
        h.Frame(Screen, 149);
        h.Frame(Camera, 144);
        h.Policy.OnPictureUncertain(Screen);

        // The frame after it comes with its number, and is the picture now.
        h.Frame(Screen, 150);
        h.Policy.SetPlaying(false);
        h.Advance(40);
        h.Pump();
        h.Advance(30);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Pump();
        h.Quiet();

        var landing = Assert.Single(h.Landings);
        Assert.Equal(150, landing.Frame);
        Assert.Equal(["pause", "seek 150"], h.Calls);
    }

    [Fact]
    public void Pause_WhenAlreadyPaused_DoesNothing()
    {
        var h = new Harness();
        h.SettleAt(100);

        h.Policy.SetPlaying(false);
        h.Pump();

        Assert.Empty(h.Calls);
        Assert.True(h.Policy.IsIdle);
    }

    [Fact]
    public void SeekWhilePlaying_StopsTheClock_GoesThereThePausedWay_AndPlaysOn()
    {
        var h = new Harness();
        h.Play(from: 100);
        h.Advance(500);
        h.Frame(Screen, 115);
        h.Frame(Camera, 109);

        h.Policy.RequestSeek(400);
        var deadline = h.Pump();
        Assert.Equal(["pause"], h.Calls);
        Assert.False(h.Policy.IsPlaying);
        Assert.False(h.Policy.IsIdle);

        // Frames that were on their way when the clock stopped are let through first.
        Assert.Equal(h.Now + 40, deadline);
        h.Policy.RequestSeek(500);
        h.Advance(40);
        h.Pump();
        Assert.Equal(["pause", "seek 500"], h.Calls);

        h.Advance(60);
        h.Deliver(500);
        h.Pump();

        // The first change after the clock ran: the players are given the grace period to hand
        // over whatever else they have, and only then is the clock started.
        Assert.Empty(h.Landings);
        Assert.Equal(["pause", "seek 500"], h.Calls);
        h.Quiet();

        var landing = Assert.Single(h.Landings);
        Assert.Equal(500, landing.Frame);
        Assert.Equal(StudioPreviewLandingKind.Seek, landing.Kind);
        Assert.Equal(["pause", "seek 500", "resume"], h.Calls);
        Assert.True(h.Policy.IsPlaying);
    }

    [Fact]
    public void Pause_DuringASeekMadeWhilePlaying_LandsThereAndStaysPaused()
    {
        var h = new Harness();
        h.Play(from: 100);
        h.Advance(500);
        h.Frame(Screen, 115);
        h.Frame(Camera, 109);

        h.Policy.RequestSeek(400);
        h.Pump();
        h.Advance(10);
        h.Policy.SetPlaying(false);
        h.Advance(30);
        h.Pump();
        Assert.Equal(["pause", "seek 400"], h.Calls);

        h.Advance(60);
        h.Deliver(400);
        h.Pump();
        h.Quiet();

        Assert.Equal(400, Assert.Single(h.Landings).Frame);
        Assert.Equal(["pause", "seek 400"], h.Calls);
        Assert.False(h.Policy.IsPlaying);
        Assert.True(h.Policy.IsIdle);
    }

    [Fact]
    public void EndOfTheRecording_RestsOnTheLastFrame()
    {
        var h = new Harness();
        h.Play(from: 880);

        h.Advance(650);
        h.Frame(Screen, 898);
        h.Frame(Camera, 892);
        h.Policy.OnEnded();
        Assert.False(h.Policy.IsPlaying);
        Assert.Empty(h.Calls);

        // A player that has run into the end of its stream lands its next seek one frame late.
        // So the first seek goes somewhere else, with the picture held, and the second to the last frame.
        h.Advance(40);
        h.Pump();
        Assert.Equal(["seek 891"], h.Calls);
        Assert.True(h.Policy.HoldPicture);

        h.Advance(30);
        h.DeliverDetour(891);
        Assert.Equal(["seek 891", "seek 899"], h.Calls);
        Assert.True(h.Policy.HoldPicture);

        h.Advance(30);
        h.Deliver(899);
        h.Pump();
        var landing = Assert.Single(h.Landings);
        Assert.Equal(899, landing.Frame);
        Assert.Equal(StudioPreviewLandingKind.Snap, landing.Kind);
        Assert.Equal(0, landing.Repairs);
        Assert.Equal(1, h.Policy.EndRecoveries);
        Assert.Equal(0, h.Policy.Repairs);
        Assert.False(h.Policy.HoldPicture);
        Assert.True(h.Policy.IsIdle);

        // Seek(0) and Play() work afterwards. The screen rests on its last frame, so its stream
        // is read to the end again, and the way to frame 0 is once more by another frame.
        h.Advance(100);
        h.Landings.Clear();
        h.Policy.RequestSeek(0);
        h.Policy.SetPlaying(true);
        h.Pump();
        Assert.Equal(["seek 891", "seek 899", "seek 8"], h.Calls);
        h.Advance(30);
        h.DeliverDetour(8);
        h.Advance(30);
        h.Deliver(0);
        h.Pump();
        Assert.Equal(["seek 891", "seek 899", "seek 8", "seek 0", "resume"], h.Calls);
        Assert.Equal(0, Assert.Single(h.Landings).Frame);
        Assert.True(h.Policy.IsPlaying);
    }

    [Fact]
    public void PlayerPutOnItsLastFrame_GetsASeekElsewhereFirst()
    {
        // Measured: after a paused seek to a player's last frame, playing on from the next seek
        // stops two frames later unless another seek came in between.
        var h = new Harness();
        h.SettleAt(899);

        h.Policy.RequestSeek(890);
        h.Pump();
        Assert.Equal(["seek 882"], h.Calls);
        Assert.True(h.Policy.HoldPicture);

        h.Advance(30);
        h.DeliverDetour(882);
        Assert.Empty(h.Landings);
        Assert.Equal(["seek 882", "seek 890"], h.Calls);

        h.Advance(30);
        h.Deliver(890);
        h.Pump();
        Assert.Equal(890, Assert.Single(h.Landings).Frame);
        Assert.Equal(1, h.Policy.EndRecoveries);
        Assert.Equal(0, h.Policy.Repairs);

        // Once only.
        h.Advance(100);
        h.Policy.RequestSeek(880);
        h.Pump();
        Assert.Equal("seek 880", h.Calls[^1]);
        Assert.Equal(3, h.Calls.Count);
    }

    [Fact]
    public void PlayerSteppedOntoItsLastFrame_GetsASeekElsewhereFirst()
    {
        var h = new Harness(new StudioPreviewTimeline(30, 30, [new(30, 900, 0)]));
        h.SettleAt(898);
        h.Step(899);
        h.Calls.Clear();

        h.Policy.RequestSeek(890);
        h.Pump();
        Assert.Equal(["seek 882"], h.Calls);
    }

    [Fact]
    public void CameraParkedAfterItsEnd_GetsASeekElsewhereFirst_OnceItIsBackInThePicture()
    {
        // A camera six seconds long that starts two seconds in: it ends at timeline frame 240.
        var h = new Harness(new StudioPreviewTimeline(30, 30, [new(30, 900, 0), new(30, 180, 2.0, 6)]));
        h.SettleAt(300);

        // Where the camera is not in the picture nothing special happens.
        h.Policy.RequestSeek(600);
        h.Pump();
        Assert.Equal(["seek 600"], h.Calls);
        h.Advance(30);
        h.Deliver(600);
        h.Pump();
        Assert.Equal(0, h.Policy.EndRecoveries);

        h.Advance(100);
        h.Policy.RequestSeek(230);
        h.Pump();
        Assert.Equal(["seek 600", "seek 222"], h.Calls);
        Assert.True(h.Policy.HoldPicture);

        h.Advance(30);
        h.DeliverDetour(222);
        h.Advance(30);
        h.Deliver(230);
        h.Pump();
        Assert.Equal(["seek 600", "seek 222", "seek 230"], h.Calls);
        Assert.Equal(230, h.Policy.SettledFrame);
        Assert.Equal(1, h.Policy.EndRecoveries);
    }

    [Fact]
    public void PlayerThatReachedTheEndOfItsStream_GetsASeekElsewhereFirst()
    {
        // A camera six seconds long that starts two seconds in: it ends at timeline frame 240.
        var h = new Harness(new StudioPreviewTimeline(30, 30, [new(30, 900, 0), new(30, 180, 2.0, 6)]));
        h.SettleAt(230);
        h.Policy.SetPlaying(true);
        h.Pump();
        h.Advance(1000);
        h.Frame(Screen, 260);
        h.Policy.OnPlayerEnded(Camera);
        h.Policy.SetPlaying(false);
        h.Advance(40);
        h.Pump();
        h.Calls.Clear();

        // Where the camera is not in the picture nothing special happens.
        h.Advance(30);
        h.Deliver(260);
        h.Pump();
        h.Quiet();
        h.Policy.RequestSeek(600);
        h.Pump();
        Assert.Equal(["seek 600"], h.Calls);
        h.Advance(30);
        h.Deliver(600);
        h.Pump();
        Assert.Equal(0, h.Policy.EndRecoveries);

        // Back where it is, its first seek is one that does not count.
        h.Landings.Clear();
        h.Policy.RequestSeek(100);
        h.Pump();
        Assert.Equal(["seek 600", "seek 92"], h.Calls);
        Assert.True(h.Policy.HoldPicture);

        h.Advance(30);
        h.DeliverDetour(92);
        Assert.Equal("seek 100", h.Calls[^1]);

        h.Advance(30);
        h.Deliver(100);
        h.Pump();
        Assert.Equal(100, Assert.Single(h.Landings).Frame);
        Assert.Equal(40, h.Policy.ShownFrame(Camera));
        Assert.Equal(1, h.Policy.EndRecoveries);

        // Once only.
        h.Policy.RequestSeek(150);
        h.Pump();
        Assert.Equal("seek 150", h.Calls[^1]);
    }

    [Fact]
    public void PlayerThatPlayedIntoItsEnd_IsWaitedFor_ThoughItsSeekCompletedCameAtOnce()
    {
        var h = new Harness();
        h.Play(from: 880);
        h.Advance(650);
        h.Frame(Screen, 899);
        h.Frame(Camera, 893);
        h.Policy.OnEnded();
        h.Advance(40);
        h.Pump();
        Assert.Equal(["seek 891"], h.Calls);

        // Measured: at the end of its stream a player raises SeekCompleted for the next seek
        // within a millisecond, and hands the frame over a tenth of a second later.
        h.Advance(1);
        h.Completed(Screen);
        h.Advance(60);
        h.Frame(Camera, 885);
        h.Completed(Camera);
        h.Pump();
        h.Advance(40);
        h.Pump();
        Assert.Equal(["seek 891"], h.Calls);

        h.Advance(10);
        h.Frame(Screen, 891);
        h.Pump();
        h.Advance(40);
        h.Pump();
        Assert.Equal(["seek 891", "seek 899"], h.Calls);
    }

    [Fact]
    public void PlayerAtItsEnd_ThatHandsOverNothingForItsNextSeek_IsWaitedForAsLongAsAnyAnswer_NotAsLongAsALostFrame()
    {
        var h = new Harness();
        h.SettleAt(899);

        h.Policy.RequestSeek(150);
        h.Pump();
        Assert.Equal(["seek 142"], h.Calls);

        // Measured: about one seek in fifty away from the last frame, the screen's player raises
        // SeekCompleted a few milliseconds after the clock was moved and never hands over a frame.
        h.Advance(6);
        h.Completed(Screen);
        h.Advance(15);
        h.Frame(Camera, 136);
        h.Completed(Camera);
        var deadline = h.Pump();
        Assert.Equal(h.Now + 379, deadline);

        h.Advance(378);
        h.Pump();
        Assert.Equal(["seek 142"], h.Calls);
        Assert.True(h.Policy.HoldPicture);
        Assert.Equal(0, h.Policy.DetoursUnanswered);

        // 400 ms after the clock was moved. That position was only there to be spent, so the way
        // on is taken: no repair, and not the two seconds a frame is given that is owed.
        h.Advance(1);
        h.Pump();
        Assert.Equal(["seek 142", "seek 150"], h.Calls);
        Assert.Equal(1, h.Policy.DetoursUnanswered);
        Assert.True(h.Policy.HoldPicture);

        h.Advance(40);
        h.Deliver(150);
        h.Pump();
        var landing = Assert.Single(h.Landings);
        Assert.Equal(150, landing.Frame);
        Assert.True(landing.Confirmed);
        Assert.Equal(0, landing.Repairs);
        Assert.Equal(0, h.Policy.Repairs);
        Assert.Equal(1, h.Policy.EndRecoveries);
        Assert.False(h.Policy.HoldPicture);
    }

    [Fact]
    public void PlayerAtItsEnd_ThatIsHandingItsFrameOverAtTheLimit_IsWaitedFor()
    {
        var h = new Harness();
        h.SettleAt(899);
        h.Policy.RequestSeek(150);
        h.Pump();
        h.Advance(6);
        h.Completed(Screen);
        h.Frame(Camera, 136);
        h.Completed(Camera);

        h.Players.Delivering[Screen] = true;
        h.Advance(394);
        h.Pump();
        h.Advance(60);
        h.Pump();
        Assert.Equal(["seek 142"], h.Calls);

        h.Frame(Screen, 142);
        h.Players.Delivering[Screen] = false;
        h.Pump();
        h.Advance(40);
        h.Pump();
        Assert.Equal(["seek 142", "seek 150"], h.Calls);
        Assert.Equal(0, h.Policy.DetoursUnanswered);
    }

    [Fact]
    public void SecondFrameAfterADetour_IsNotTakenForTheAnswerToTheWayBack()
    {
        var h = new Harness();
        h.Play(from: 880);
        h.Advance(650);
        h.Frame(Screen, 899);
        h.Frame(Camera, 893);
        h.Policy.OnEnded();
        h.Advance(40);
        h.Pump();
        h.Advance(100);
        h.Deliver(891);
        h.Pump();

        // Measured: a few milliseconds after the frame and SeekCompleted, the screen's player
        // sometimes hands over one more frame. It restarts the quiet time.
        h.Advance(8);
        h.Frame(Screen, 891);
        h.Pump();
        h.Advance(39);
        h.Pump();
        Assert.Equal(["seek 891"], h.Calls);
        Assert.True(h.Policy.HoldPicture);

        h.Advance(1);
        h.Pump();
        Assert.Equal(["seek 891", "seek 899"], h.Calls);
        Assert.Empty(h.Landings);
    }

    [Fact]
    public void EndOfTheRecording_SaysWhetherThePlayersAreBroughtToRestOnTheLastFrame()
    {
        var h = new Harness();
        h.Play(from: 880);
        h.Advance(600);
        h.Frame(Screen, 898);
        h.Frame(Camera, 892);
        Assert.True(h.Policy.OnEnded());
        Assert.True(h.Policy.IsSeeking);

        // A position that was asked for before the clock stopped is gone to instead, by way of
        // another frame as always for a player at the end of its stream.
        var other = new Harness();
        other.Play(from: 880);
        other.Advance(600);
        other.Frame(Screen, 898);
        other.Policy.RequestSeek(10);
        Assert.False(other.Policy.OnEnded());
        other.Advance(40);
        other.Pump();
        Assert.Equal("seek 2", other.Calls[^1]);

        // And nothing happens at all when the clock was not running.
        var paused = new Harness();
        paused.SettleAt(100);
        Assert.False(paused.Policy.OnEnded());
        Assert.False(paused.Policy.IsSeeking);
    }

    [Fact]
    public void EndOfTheRecording_IsIgnoredWhenTheClockIsNotRunning()
    {
        var h = new Harness();
        h.SettleAt(100);

        h.Policy.OnEnded();
        h.Pump();

        Assert.Empty(h.Calls);
        Assert.True(h.Policy.IsIdle);
    }

    [Fact]
    public void StepForward_StepsThePlayers_AndLandsWhenTheirFramesAreIn()
    {
        var h = new Harness();
        h.SettleAt(100);

        h.Policy.RequestSeek(101);
        h.Pump();
        Assert.Equal(["step 0", "step 1"], h.Calls);

        // A step's position sits on a frame boundary; whichever side it is read on, the step moved one frame.
        h.Advance(20);
        h.Frame(Screen, 100);
        h.Frame(Camera, 95);
        var deadline = h.Pump();

        var landing = Assert.Single(h.Landings);
        Assert.Equal(101, landing.Frame);
        Assert.True(landing.Confirmed);
        Assert.Equal(StudioPreviewLandingKind.Step, landing.Kind);
        Assert.Equal(101, h.Policy.ShownFrame(Screen));
        Assert.Equal(95, h.Policy.ShownFrame(Camera));
        Assert.Equal(1, h.Policy.StepsIssued);

        // The clock has not moved yet, and is not moved for a moment in case another step follows.
        Assert.Equal(["step 0", "step 1"], h.Calls);
        Assert.Equal(h.Now + 150, deadline);
        Assert.False(h.Policy.IsIdle);
    }

    [Fact]
    public void StepForward_BringsTheClockAlong_OnceNoFurtherStepFollows()
    {
        var h = new Harness();
        h.SettleAt(100);
        h.Step(101);

        h.Advance(149);
        h.Pump();
        Assert.Equal(["step 0", "step 1"], h.Calls);

        h.Advance(1);
        h.Pump();
        Assert.Equal(["step 0", "step 1", "seek 101"], h.Calls);
        Assert.False(h.Policy.IsIdle);

        // Both players draw their frame again when the clock moves under them. Nothing else may be
        // asked of them before they have: SeekCompleted alone is not the end of it.
        h.Advance(5);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Policy.RequestSeek(102);
        h.Pump();
        Assert.Equal(["step 0", "step 1", "seek 101"], h.Calls);

        h.Advance(30);
        h.Frame(Screen, 101);
        h.Frame(Camera, 95);
        h.Pump();
        Assert.Equal(["step 0", "step 1", "seek 101", "step 0", "step 1"], h.Calls);

        // Moving the clock is not another landing: the position was reported when the step arrived.
        Assert.Equal(101, Assert.Single(h.Landings).Frame);
    }

    [Fact]
    public void ClockBroughtAlong_WithoutAnAnswerFromAPlayer_IsDoneAfterTheGracePeriod()
    {
        var h = new Harness();
        h.SettleAt(100);
        h.Step(101);
        h.Advance(150);
        h.Pump();

        h.Advance(5);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Advance(39);
        h.Pump();
        Assert.False(h.Policy.IsIdle);

        h.Advance(1);
        h.Pump();
        Assert.True(h.Policy.IsIdle);
        Assert.Equal(101, h.Policy.SettledFrame);
        Assert.Equal(0, h.Policy.Repairs);
    }

    [Fact]
    public void ClockBroughtAlong_ThatAPlayerHasNotAnsweredWithItsFrame_MakesTheNextFrameASeek()
    {
        var h = new Harness();
        h.SettleAt(100);
        h.Step(101);
        h.Advance(150);
        h.Pump();
        Assert.Equal(["step 0", "step 1", "seek 101"], h.Calls);

        // SeekCompleted at once, and no frame within the grace period.
        h.Advance(5);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Advance(40);
        h.Pump();
        Assert.True(h.Policy.IsIdle);

        // The frames the players draw again for that move of the clock are still to come. A step
        // made now would take them for its own, with the picture of frame 101 in them.
        h.Advance(20);
        h.Policy.RequestSeek(102);
        h.Pump();
        Assert.Equal(["step 0", "step 1", "seek 101", "seek 102"], h.Calls);
        Assert.Equal(1, h.Policy.StepsAvoided);
        Assert.True(h.Policy.HoldPicture);

        // They come now, with the position they were made for, and are not an answer to this one.
        h.Advance(30);
        h.Frame(Screen, 101);
        h.Frame(Camera, 95);
        h.Pump();
        Assert.Equal(101, Assert.Single(h.Landings).Frame);
        Assert.True(h.Policy.HoldPicture);

        h.Advance(40);
        h.Deliver(102);
        h.Pump();
        Assert.Equal([101, 102], h.Landings.Select(l => l.Frame));
        Assert.Equal(StudioPreviewLandingKind.Seek, h.Landings[^1].Kind);
        Assert.False(h.Policy.HoldPicture);

        // The players have answered since: the next frame is a step again.
        h.Advance(200);
        h.Policy.RequestSeek(103);
        h.Pump();
        Assert.Equal(["step 0", "step 1"], h.Calls.TakeLast(2));
    }

    [Fact]
    public void ClockBroughtAlong_AnsweredLateWhileAtRest_LeavesTheNextFrameAStep()
    {
        var h = new Harness();
        h.SettleAt(100);
        h.Step(101);
        h.Advance(150);
        h.Pump();
        h.Advance(5);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Advance(40);
        h.Pump();
        Assert.True(h.Policy.IsIdle);

        // A tenth of a second after SeekCompleted the players draw their frame again after all.
        h.Advance(60);
        h.Frame(Screen, 101);
        h.Frame(Camera, 95);
        h.Pump();
        Assert.False(h.Policy.HoldPicture);

        h.Advance(100);
        h.Policy.RequestSeek(102);
        h.Pump();
        Assert.Equal(["step 0", "step 1", "seek 101", "step 0", "step 1"], h.Calls);
        Assert.Equal(0, h.Policy.StepsAvoided);
    }

    [Fact]
    public void ClockBroughtAlong_ThatAPlayerAnswersWithAnotherFrame_IsPutRightByASeek()
    {
        var h = new Harness();
        h.SettleAt(100);
        h.Step(101);
        h.Advance(150);
        h.Pump();
        Assert.Equal("seek 101", h.Calls[^1]);

        // The screen's player answers the clock's move with the frame before.
        h.Advance(30);
        h.Frame(Screen, 100);
        h.Frame(Camera, 95);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Pump();
        h.Advance(40);
        h.Pump();

        // The clock is on 101 already, so the way there is over another frame.
        Assert.Equal(["step 0", "step 1", "seek 101", "seek 100"], h.Calls);
        Assert.True(h.Policy.HoldPicture);
        Assert.False(h.Policy.IsIdle);

        h.Advance(40);
        h.Frame(Screen, 100);
        h.Frame(Camera, 94);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Pump();
        h.Advance(40);
        h.Pump();
        Assert.Equal("seek 101", h.Calls[^1]);

        h.Advance(40);
        h.Deliver(101);
        h.Pump();

        Assert.Equal([101, 101], h.Landings.Select(l => l.Frame));
        Assert.Equal(101, h.Policy.ShownFrame(Screen));
        Assert.False(h.Policy.HoldPicture);
        Assert.True(h.Policy.IsIdle);
    }

    [Fact]
    public void StepForward_LeavesAParkedCameraAlone()
    {
        var h = new Harness();
        h.SettleAt(3);

        h.Policy.RequestSeek(4);
        h.Pump();

        Assert.Equal(["step 0"], h.Calls);
    }

    [Fact]
    public void StepForward_IsNotUsed_WhenAPlayerWouldMoveMoreThanOneFrame()
    {
        var timeline = new StudioPreviewTimeline(30, 30, [new(30, 900, 0), new(60, 1800, 0)]);
        var h = new Harness(timeline);
        h.SettleAt(100);

        h.Policy.RequestSeek(101);
        h.Pump();

        Assert.Equal(["seek 101"], h.Calls);
    }

    [Fact]
    public void StepBackward_AndLongerJumps_AreSeeks()
    {
        var h = new Harness();
        h.SettleAt(100);

        h.Policy.RequestSeek(99);
        h.Pump();
        h.Deliver(99);
        h.Pump();
        h.Policy.RequestSeek(101);
        h.Pump();

        Assert.Equal(["seek 99", "seek 101"], h.Calls);
    }

    [Fact]
    public void StepForward_ThatDeliversNothing_IsDoneAgainAsASeek()
    {
        var h = new Harness();
        h.SettleAt(100);

        h.Policy.RequestSeek(101);
        var deadline = h.Pump();
        Assert.Equal(h.Now + 250, deadline);

        h.Advance(20);
        h.Frame(Camera, 95);
        h.Advance(230);
        h.Pump();

        // The screen may still hand its stepped frame over, and a seek to 101 would flush it in
        // the middle of that. So the way is by another frame, with the picture held.
        Assert.Equal(["step 0", "step 1", "seek 100"], h.Calls);
        Assert.Equal(1, h.Policy.StepFallbacks);
        Assert.True(h.Policy.HoldPicture);

        h.Advance(60);
        h.DeliverDetour(100);
        Assert.Equal(["step 0", "step 1", "seek 100", "seek 101"], h.Calls);

        h.Advance(60);
        h.Deliver(101);
        h.Pump();
        var landing = Assert.Single(h.Landings);
        Assert.Equal(101, landing.Frame);
        Assert.Equal(StudioPreviewLandingKind.Seek, landing.Kind);
        Assert.False(h.Policy.HoldPicture);
    }

    [Fact]
    public void StepForward_IsNotUsed_ForAPlayerTheClockStillHoldsBeforeItsStream()
    {
        // The camera starts at timeline frame 6. Steps from frame 2 move only the screen and
        // leave the clock on frame 2, where the camera's player is parked before its first frame.
        var h = new Harness();
        h.SettleAt(2);
        h.Step(3);
        h.Step(4);
        h.Step(5);
        h.Step(6);
        Assert.DoesNotContain(h.Calls, c => c == "step 1");
        h.Calls.Clear();

        // Frame 7 needs the camera's second frame. Its player would not step from where the
        // clock holds it, so this one is a seek, which takes the clock along.
        h.Policy.RequestSeek(7);
        h.Pump();
        Assert.Equal(["seek 7"], h.Calls);
    }

    [Fact]
    public void StepsInARow_LeaveTheClockAlone_UntilTheLastOne()
    {
        var h = new Harness();
        h.SettleAt(100);

        // The second step is asked for while the first is still on its way.
        h.Policy.RequestSeek(101);
        h.Pump();
        h.Policy.RequestSeek(102);
        h.Advance(20);
        h.Frame(Screen, 101);
        h.Frame(Camera, 95);
        h.Pump();

        // Frame 101 is shown and reported, and the next step starts without a seek in between.
        Assert.Equal(["step 0", "step 1", "step 0", "step 1"], h.Calls);
        Assert.Equal(101, Assert.Single(h.Landings).Frame);

        h.Advance(20);
        h.Frame(Screen, 102);
        h.Frame(Camera, 96);
        h.Pump();
        Assert.Equal([101, 102], h.Landings.Select(l => l.Frame));

        // The third comes a little later, but before the clock was moved.
        h.Advance(100);
        h.Policy.RequestSeek(103);
        h.Pump();
        Assert.Equal(6, h.Calls.Count);
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("seek", StringComparison.Ordinal));

        h.Advance(20);
        h.Frame(Screen, 103);
        h.Frame(Camera, 97);
        h.Pump();
        h.Advance(150);
        h.Pump();
        Assert.Equal("seek 103", h.Calls[^1]);

        h.Advance(40);
        h.Deliver(103);
        h.Pump();
        Assert.Equal([101, 102, 103], h.Landings.Select(l => l.Frame));
        Assert.Equal(1, h.Policy.SeeksIssued - h.SeeksBefore);
        Assert.True(h.Policy.IsIdle);
    }

    [Fact]
    public void SeekAfterAStep_GoesStraightThere_AndTakesTheClockWithIt()
    {
        var h = new Harness();
        h.SettleAt(100);
        h.Step(101);

        h.Advance(30);
        h.Policy.RequestSeek(200);
        h.Pump();
        Assert.Equal("seek 200", h.Calls[^1]);

        h.Advance(50);
        h.Deliver(200);
        h.Pump();
        h.Advance(500);
        h.Pump();

        Assert.Equal(["step 0", "step 1", "seek 200"], h.Calls);
        Assert.Equal([101, 200], h.Landings.Select(l => l.Frame));
        Assert.True(h.Policy.IsIdle);
    }

    [Fact]
    public void Play_DuringAStep_BringsTheClockAlongFirst()
    {
        var h = new Harness();
        h.SettleAt(100);

        h.Policy.RequestSeek(101);
        h.Pump();
        h.Policy.SetPlaying(true);
        h.Advance(20);
        h.Frame(Screen, 101);
        h.Frame(Camera, 95);
        h.Pump();

        // No waiting for a further step: playback would stall on a clock that is behind.
        Assert.Equal(["step 0", "step 1", "seek 101"], h.Calls);

        // The clock starts once both players have answered its move.
        h.Advance(10);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Pump();
        Assert.Equal(["step 0", "step 1", "seek 101"], h.Calls);

        h.Advance(30);
        h.Frame(Screen, 101);
        h.Frame(Camera, 95);
        h.Pump();

        Assert.Equal(["step 0", "step 1", "seek 101", "resume"], h.Calls);
        Assert.True(h.Policy.IsPlaying);
    }

    [Fact]
    public void Play_AfterAStep_BringsTheClockAlongFirst()
    {
        var h = new Harness();
        h.SettleAt(100);
        h.Step(101);

        h.Advance(10);
        h.Policy.SetPlaying(true);
        h.Pump();
        Assert.Equal(["step 0", "step 1", "seek 101"], h.Calls);

        h.Advance(45);
        h.Deliver(101);
        h.Pump();

        Assert.Equal(["step 0", "step 1", "seek 101", "resume"], h.Calls);
    }

    [Fact]
    public void StepThenARequestForTheSameFrame_AsksNothingMore_AndTheClockStillFollows()
    {
        var h = new Harness();
        h.SettleAt(100);

        h.Policy.RequestSeek(101);
        h.Pump();
        h.Policy.RequestSeek(102);
        h.Advance(20);
        h.Frame(Screen, 101);
        h.Frame(Camera, 95);
        h.Policy.RequestSeek(101);
        h.Pump();

        Assert.Equal(["step 0", "step 1"], h.Calls);
        Assert.Equal(101, Assert.Single(h.Landings).Frame);

        h.Advance(150);
        h.Pump();
        Assert.Equal(["step 0", "step 1", "seek 101"], h.Calls);
    }

    [Fact]
    public void Stop_DuringASeek_AsksNothingMoreOfThePlayers()
    {
        var h = new Harness();

        h.Policy.RequestSeek(100);
        h.Policy.SetPlaying(true);
        h.Pump();
        h.Policy.Stop();

        h.Advance(60);
        h.Deliver(100);
        h.Policy.RequestSeek(200);
        h.Policy.SetPlaying(false);
        h.Policy.OnEnded();
        var deadline = h.Pump();
        h.Advance(1000);
        h.Pump();

        Assert.Equal(["seek 100"], h.Calls);
        Assert.Empty(h.Landings);
        Assert.Equal(long.MaxValue, deadline);
        Assert.False(h.Policy.HoldPicture);
    }

    [Fact]
    public void Stop_DuringARepair_ReleasesThePicture()
    {
        var h = new Harness();

        h.Policy.RequestSeek(100);
        h.Pump();
        h.Advance(2000);
        h.Pump();
        Assert.True(h.Policy.HoldPicture);

        h.Policy.Stop();

        Assert.False(h.Policy.HoldPicture);
        Assert.Equal(["seek 100", "seek 99"], h.Calls);
    }

    [Fact]
    public void SeekCompletedAndFrames_FromBeforeTheAssignment_DoNotCount()
    {
        var h = new Harness();
        h.SettleAt(50);
        var before = h.Now - 1;

        h.Policy.RequestSeek(100);
        h.Advance(1);
        h.Pump();
        h.Policy.OnSeekCompleted(Screen, before);
        h.Policy.OnSeekCompleted(Camera, before);
        h.Policy.OnFrame(Screen, 100, before);
        h.Policy.OnFrame(Camera, 94, before);
        h.Advance(100);
        h.Pump();

        Assert.Empty(h.Landings);
        Assert.Single(h.Calls);
    }

    [Fact]
    public void WhichFrameAPlayerShows_ComesFromWhatItDelivered()
    {
        var h = new Harness();

        // The seek to 100 is lost on the screen, then superseded. The next request is for the
        // frame the screen still shows, so the screen has nothing to deliver.
        h.Policy.RequestSeek(100);
        h.Pump();
        h.Advance(5);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Frame(Camera, 94);
        h.Policy.RequestSeek(0);
        h.Advance(45);
        h.Pump();
        Assert.Equal(["seek 100", "seek 0"], h.Calls);
        Assert.Equal(0, h.Policy.ShownFrame(Screen));

        h.Advance(30);
        h.Frame(Camera, 0);
        h.Completed(Screen);
        h.Completed(Camera);
        h.Pump();

        var landing = Assert.Single(h.Landings);
        Assert.Equal(0, landing.Frame);
        Assert.True(landing.Confirmed);
    }

    [Fact]
    public void ScreenOnlyRecording_Works()
    {
        var timeline = new StudioPreviewTimeline(30, 30, [new(30, 900, 0)]);
        var h = new Harness(timeline);

        h.Policy.RequestSeek(100);
        h.Pump();
        h.Advance(50);
        h.Frame(Screen, 100);
        h.Completed(Screen);
        h.Pump();
        h.Policy.RequestSeek(101);
        h.Pump();

        Assert.Equal(["seek 100", "step 0"], h.Calls);
        Assert.Equal(100, Assert.Single(h.Landings).Frame);
    }

    private sealed class FakePlayers : IStudioPreviewTransport
    {
        public List<string> Calls { get; } = [];

        public bool RefuseResume { get; set; }

        public void Seek(long timelineFrame) => Calls.Add($"seek {timelineFrame}");

        public void StepForward(int track) => Calls.Add($"step {track}");

        public bool Resume()
        {
            Calls.Add("resume");
            return !RefuseResume;
        }

        public void Pause() => Calls.Add("pause");

        /// <summary>Tracks whose player is in the middle of handing a frame over.</summary>
        public bool[] Delivering { get; } = new bool[2];

        public bool IsDelivering(int track) => Delivering[track];
    }

    private sealed class Harness
    {
        private readonly StudioPreviewTimeline _timeline;

        public Harness(StudioPreviewTimeline? timeline = null, bool cameraDelivered = true, StudioPreviewSeekSettings? settings = null)
        {
            _timeline = timeline ?? new StudioPreviewTimeline(30, 30, [new(30, 900, 0), new(30, 900, 0.2)]);
            Policy = new StudioPreviewSeekPolicy(Players, _timeline, settings ?? new StudioPreviewSeekSettings { TicksPerSecond = 1000 }, () => Now);

            // A player hands over its first frame when it opens, unless its clip has not started yet.
            for (var track = 0; track < _timeline.TrackCount; track++)
            {
                if (track == Screen || cameraDelivered)
                {
                    Policy.OnFrame(track, 0, Now);
                }
            }
        }

        public FakePlayers Players { get; } = new();

        public StudioPreviewSeekPolicy Policy { get; }

        public List<StudioPreviewLanding> Landings { get; } = [];

        public List<string> Calls => Players.Calls;

        public long Now { get; private set; } = 1000;

        public long SeeksBefore { get; private set; }

        public long LastSeek => long.Parse(Players.Calls.Last(c => c.StartsWith("seek ", StringComparison.Ordinal))[5..]);

        public void Advance(long milliseconds) => Now += milliseconds;

        public long Pump()
        {
            SceneIsTheScreensFrame();
            var deadline = Policy.Pump();
            Policy.TakeLandings(Landings);
            SceneIsTheScreensFrame();
            return deadline;
        }

        public void Frame(int track, long playerFrame)
        {
            SceneIsTheScreensFrame();
            Policy.OnFrame(track, playerFrame, Now);
            SceneIsTheScreensFrame();
        }

        public void Completed(int track)
        {
            SceneIsTheScreensFrame();
            Policy.OnSeekCompleted(track, Now);
            SceneIsTheScreensFrame();
        }

        /// <summary>
        /// On the grid, which every timeline of this suite is, the timeline frame the scene is
        /// drawn for is the frame the screen's player delivered: at every moment, whatever was
        /// asked of the policy since. It is what leaves the engine doing what it did before a
        /// screen could count in frames of its file. Held before and after every call the
        /// harness makes, so that what a test asks of the policy directly is held too.
        /// </summary>
        private void SceneIsTheScreensFrame()
        {
            Assert.False(_timeline.ScreenHasTimes);
            Assert.Equal(Policy.ShownFrame(Screen), Policy.ShownTimelineFrame);
        }

        /// <summary>
        /// Every player answers a position change the way a real one does: with a frame when the
        /// change moves it, which is always inside its stream and, outside it, when it parks on
        /// another frame than it showed; and with SeekCompleted.
        /// </summary>
        public void Deliver(long timelineFrame)
        {
            for (var track = 0; track < _timeline.TrackCount; track++)
            {
                var wanted = _timeline.PlayerFrame(track, timelineFrame);
                if (_timeline.IsPlayerInside(track, _timeline.FrameMiddle(timelineFrame)) || Policy.ShownFrame(track) != wanted)
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

        /// <summary>The players answer a detour, and then say nothing more for as long as the policy listens before the way back.</summary>
        public void DeliverDetour(long timelineFrame)
        {
            Deliver(timelineFrame);
            Pump();
            Advance(40);
            Pump();
        }

        /// <summary>Brings the players to rest on a frame and forgets how they got there.</summary>
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
            SeeksBefore = Policy.SeeksIssued;
        }

        /// <summary>Steps one frame forward from rest: both players deliver, and the step lands.</summary>
        public void Step(long timelineFrame)
        {
            Policy.RequestSeek(timelineFrame);
            Pump();
            Advance(20);
            for (var track = 0; track < _timeline.TrackCount; track++)
            {
                var wanted = _timeline.PlayerFrame(track, timelineFrame);
                if (Policy.ShownFrame(track) != wanted)
                {
                    Frame(track, wanted);
                }
            }

            Pump();
            Assert.Equal(timelineFrame, Landings[^1].Frame);
            Assert.Equal(StudioPreviewLandingKind.Step, Landings[^1].Kind);
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
