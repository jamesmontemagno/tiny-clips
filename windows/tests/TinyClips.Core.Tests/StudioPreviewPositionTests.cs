using TinyClips.Core.Studio.Preview;

namespace TinyClips.Core.Tests;

/// <summary>
/// The position a preview reports. First the rule on its own; then the rule fed by the real seek
/// policy over a fake pair of players, with the calls a caller makes and the passes the engine's
/// render thread makes, in the order <c>StudioPreviewEngine</c> makes them. The clock counts
/// milliseconds.
/// </summary>
public sealed class StudioPreviewPositionTests
{
    private const int Screen = 0;
    private const int Camera = 1;

    // The camera started 0.2 s after the screen: timeline frame N shows camera frame N - 6.
    private const int Lag = 6;
    private const int Last = 899;

    // ---------------------------------------------------------------------------------------
    // The rule on its own
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void RequestedFrame_IsReportedAtOnce()
    {
        var position = new StudioPreviewPosition();
        Assert.Equal(0, position.Frame);
        Assert.False(position.IsPending);
        Assert.False(position.HasNewRequest);

        Assert.True(position.Request(100));

        Assert.Equal(100, position.Frame);
        Assert.True(position.IsPending);
        Assert.True(position.HasNewRequest);

        // Asking for the frame that is reported already changes nothing, and is a request all the same.
        Assert.False(position.Request(100));
        Assert.Equal(100, position.Frame);
    }

    [Fact]
    public void NewestRequest_IsGivenOutOnce()
    {
        var position = new StudioPreviewPosition();
        Assert.False(position.TryTake(out _));

        position.Request(100);
        position.Request(200);

        Assert.True(position.TryTake(out var frame));
        Assert.Equal(200, frame);
        Assert.False(position.HasNewRequest);
        Assert.False(position.TryTake(out _));
    }

    [Fact]
    public void UntilTheRequestedFrameIsReached_NothingThePlayersShowIsReported()
    {
        var position = new StudioPreviewPosition();
        position.Request(100);
        position.TryTake(out _);

        // The frame an earlier request comes to rest on, and a frame of the playback this one interrupts.
        position.Played(41, setOutAt: 5);
        Assert.False(position.Update([Landing(40)], seeking: true));
        Assert.Equal(100, position.Frame);
        Assert.True(position.IsPending);

        // A pass that has nothing new: the old picture is drawn again at most.
        Assert.False(position.Update([], seeking: true));
        Assert.Equal(100, position.Frame);

        // The frame itself. What is reported stays the same; that it is now the picture is news, once.
        Assert.True(position.Update([Landing(100)], seeking: false));
        Assert.Equal(100, position.Frame);
        Assert.False(position.IsPending);
        Assert.False(position.Update([], seeking: false));
    }

    [Fact]
    public void RequestThatThePolicyHasNotBeenGiven_StaysPending_WhateverThePolicySays()
    {
        var position = new StudioPreviewPosition();
        position.Request(100);

        // The policy has nothing left to reach, but it has not heard of this request yet.
        Assert.False(position.Update([Landing(40)], seeking: false));
        Assert.Equal(100, position.Frame);
        Assert.True(position.IsPending);

        // Given to the policy; before its landing is taken in, a newer request arrives.
        position.TryTake(out _);
        position.Request(200);
        Assert.False(position.Update([Landing(100)], seeking: false));
        Assert.Equal(200, position.Frame);
        Assert.True(position.IsPending);

        position.TryTake(out _);
        Assert.False(position.Update([], seeking: true));
        Assert.True(position.Update([Landing(200)], seeking: false));
        Assert.Equal(200, position.Frame);
        Assert.False(position.IsPending);
    }

    [Fact]
    public void RequestForTheFrameThePlayersRestOn_IsReachedWithoutALanding()
    {
        var position = new StudioPreviewPosition();
        position.Request(100);
        position.TryTake(out _);
        position.Update([Landing(100)], seeking: false);

        Assert.False(position.Request(100));
        Assert.True(position.IsPending);
        position.TryTake(out _);

        // The policy found the players on the frame already: nothing to reach, nothing landed.
        Assert.False(position.Update([], seeking: false));
        Assert.False(position.IsPending);
        Assert.Equal(100, position.Frame);
    }

    [Fact]
    public void OnceReached_ThePositionIsTheFrameOfThePicture()
    {
        var position = new StudioPreviewPosition();
        position.Request(100);
        position.TryTake(out _);
        position.Update([Landing(100)], seeking: false);

        position.ClockStarted(1000);
        position.Played(101, setOutAt: 1016);
        Assert.True(position.Update([], seeking: false));
        Assert.Equal(101, position.Frame);

        // Nothing new, and the same frame once more: nothing to tell.
        Assert.False(position.Update([], seeking: false));
        position.Played(101, setOutAt: 1020);
        Assert.False(position.Update([], seeking: false));

        // The clock stops. While the players are brought onto one frame the last frame played
        // stays; then it is the frame they rest on. Nobody asked for that frame, so it is not held back.
        position.Played(102, setOutAt: 1050);
        Assert.True(position.Update([], seeking: true));
        Assert.Equal(102, position.Frame);
        Assert.False(position.Update([], seeking: true));
        Assert.True(position.Update([Landing(103, StudioPreviewLandingKind.Snap)], seeking: false));
        Assert.Equal(103, position.Frame);
    }

    [Fact]
    public void FrameThatSetOutBeforeTheClockWasStarted_IsNotAFrameOfPlayback()
    {
        var position = new StudioPreviewPosition();
        position.Request(0);
        position.TryTake(out _);
        position.Update([Landing(0)], seeking: false);
        position.ClockStarted(1000);

        // Handed over late. It reports where its player was when it set out: the frame that was left.
        position.Played(Last, setOutAt: 999);
        Assert.False(position.Update([], seeking: false));
        Assert.Equal(0, position.Frame);
        Assert.Equal(1, position.FramesFromBeforeStart);

        position.Played(1, setOutAt: 1000);
        Assert.True(position.Update([], seeking: false));
        Assert.Equal(1, position.Frame);
        Assert.Equal(1, position.FramesFromBeforeStart);
    }

    [Fact]
    public void WhenPlaybackRunsIntoTheEnd_TheLastFrameIsReportedAtOnce_AndStaysUntilThePlayersRestOnIt()
    {
        var position = new StudioPreviewPosition();
        position.Request(880);
        position.TryTake(out _);
        position.Update([Landing(880)], seeking: false);
        position.ClockStarted(1000);
        position.Played(897, setOutAt: 1560);
        position.Update([], seeking: false);

        // The clock stopped at the end before the last frames got through.
        Assert.True(position.Rest(Last));
        Assert.Equal(Last, position.Frame);
        Assert.True(position.IsPending);

        // One of them arrives now, and the players are taken by another frame to the last.
        position.Played(898, setOutAt: 1590);
        Assert.False(position.Update([], seeking: true));
        Assert.False(position.Update([], seeking: true));
        Assert.Equal(Last, position.Frame);

        // They rest on it. Nobody asked for that frame by name, so its arrival is not news.
        Assert.False(position.Update([Landing(Last, StudioPreviewLandingKind.Snap)], seeking: false));
        Assert.Equal(Last, position.Frame);
        Assert.False(position.IsPending);
    }

    [Fact]
    public void LastFrameAtTheEnd_GivesWayToARequestedFrame()
    {
        var position = new StudioPreviewPosition();

        // Asked for just before playback ended: that frame comes first.
        position.Request(10);
        Assert.False(position.Rest(Last));
        Assert.Equal(10, position.Frame);

        position.TryTake(out _);
        Assert.True(position.Update([Landing(10)], seeking: false));

        // Asked for while the players are being brought to rest on the last frame.
        Assert.True(position.Rest(Last));
        Assert.True(position.Request(0));
        Assert.Equal(0, position.Frame);
        Assert.False(position.Update([Landing(Last, StudioPreviewLandingKind.Snap)], seeking: true));
        Assert.Equal(0, position.Frame);
        position.TryTake(out _);
        Assert.True(position.Update([Landing(0)], seeking: false));
        Assert.Equal(0, position.Frame);
    }

    [Fact]
    public void LastFrameAtTheEnd_ThatWasPlayed_IsNoNews()
    {
        var position = new StudioPreviewPosition();
        position.Request(880);
        position.TryTake(out _);
        position.Update([Landing(880)], seeking: false);
        position.ClockStarted(1000);
        position.Played(Last, setOutAt: 1630);
        Assert.True(position.Update([], seeking: false));

        Assert.False(position.Rest(Last));
        Assert.Equal(Last, position.Frame);
    }

    [Fact]
    public void Update_SaysWhetherTheReportedFrameChanged_NotWhetherSomethingArrived()
    {
        var position = new StudioPreviewPosition();
        position.Request(50);
        position.TryTake(out _);
        position.Update([Landing(50)], seeking: false);

        // Played on by one frame and brought back onto the first, within one pass.
        position.Played(51, setOutAt: 10);
        Assert.False(position.Update([Landing(50, StudioPreviewLandingKind.Snap)], seeking: false));
        Assert.Equal(50, position.Frame);
    }

    // ---------------------------------------------------------------------------------------
    // With the seek policy, as the engine drives it
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Seek_ReportsItsFrameAtOnce_AndKeepsItThroughARedrawAndTheLanding()
    {
        var p = new Preview();
        const double time = 3.35;
        var expected = p.Timeline.FrameStart(p.Timeline.FrameForSeek(time));
        Assert.Equal(100, p.Timeline.FrameForSeek(time));

        p.Seek(time);

        // Before the render thread has seen the request.
        Assert.Equal(expected, p.Seconds);
        Assert.Equal(1, p.Changes);

        p.Pass();
        Assert.Equal(["seek 100"], p.Calls);
        Assert.Equal(expected, p.Seconds);

        // The seek is on its way and the old picture is drawn again, twice.
        p.Advance(16);
        p.Pass();
        p.Advance(16);
        p.Pass();
        Assert.Equal(expected, p.Seconds);
        Assert.True(p.Position.IsPending);

        // One clip is on the frame: not landed.
        p.Advance(30);
        p.Frame(Screen, 100);
        p.Pass();
        Assert.Empty(p.Landings);
        Assert.Equal(expected, p.Seconds);

        // The landing.
        p.Frame(Camera, 100 - Lag);
        p.Completed(Screen);
        p.Completed(Camera);
        p.Pass();
        Assert.Equal(100, Assert.Single(p.Landings).Frame);
        Assert.Equal(expected, p.Seconds);
        Assert.False(p.Position.IsPending);

        // Told at the call, and once more now that the picture shows the frame.
        Assert.Equal(2, p.Changes);

        p.Advance(500);
        p.Pass();
        Assert.Equal(expected, p.Seconds);
        Assert.All(p.Reported, frame => Assert.Equal(100, frame));
        Assert.Equal(2, p.Changes);
    }

    [Theory]
    [InlineData(-5.0, 0)]
    [InlineData(0.0, 0)]
    [InlineData(3.3333333333333335, 100)]
    [InlineData(3.35, 100)]
    [InlineData(3.3666, 100)]
    [InlineData(29.99, 899)]
    [InlineData(30.0, 899)]
    [InlineData(1e9, 899)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 899)]
    public void Seek_ReportsTheStartOfTheFrameThatContainsTheTime_ClampedToTheRecording(double time, long frame)
    {
        var p = new Preview();
        p.Land(450);

        p.Seek(time);

        Assert.Equal(frame, p.PositionFrame);
        Assert.Equal(p.Timeline.FrameStart(p.Timeline.FrameForSeek(time)), p.Seconds);
        Assert.Equal(frame / 30.0, p.Seconds);
    }

    [Fact]
    public void Seek_AtAnotherFrameRate_ReportsTheStartOfItsFrame()
    {
        const double rate = 30000.0 / 1001;
        var p = new Preview(new StudioPreviewTimeline(20, rate, [new(rate, 599, 0)]));

        p.Seek(10);

        // 10 s is inside frame 299, which starts at 9.9766 s.
        Assert.Equal(299, p.PositionFrame);
        Assert.Equal(299 / rate, p.Seconds);
    }

    [Fact]
    public void Seek_WhileAnEarlierSeekIsInFlight_NeverReportsTheEarlierFrame()
    {
        var p = new Preview();
        p.Seek(Time(200));
        p.Pass();
        p.Advance(20);

        p.Seek(Time(50));
        Assert.Equal(50, p.PositionFrame);
        p.Pass();
        Assert.Equal(["seek 200"], p.Calls);

        // The earlier seek's frames come in. It lands, and that is the picture for a moment.
        p.Advance(30);
        p.Deliver(200);
        p.Pass();
        Assert.Equal(200, Assert.Single(p.Landings).Frame);
        Assert.Equal(50, p.PositionFrame);
        Assert.True(p.Position.IsPending);
        Assert.Equal(["seek 200", "seek 50"], p.Calls);

        p.Advance(60);
        p.Deliver(50);
        p.Pass();
        Assert.Equal([200, 50], p.Landings.Select(l => l.Frame));
        Assert.Equal(50, p.PositionFrame);
        Assert.False(p.Position.IsPending);

        Assert.DoesNotContain(200L, p.ReportedSince(Time(50)));

        // News at each call, and when the newer frame became the picture. Not when the earlier one did.
        Assert.Equal(3, p.Changes);
    }

    [Fact]
    public void Seek_ThatArrivesWhileALandingIsTakenIn_IsNotOverwrittenByIt()
    {
        var p = new Preview();
        p.Seek(Time(200));
        p.Pass();
        p.Advance(50);
        p.Deliver(200);

        // The render thread has pumped the policy and holds the landing on 200 when the caller asks for 50.
        p.Pass(beforeUpdate: () => p.Seek(Time(50)));

        Assert.Equal(200, Assert.Single(p.Landings).Frame);
        Assert.Equal(50, p.PositionFrame);
        Assert.True(p.Position.IsPending);

        p.Pass();
        p.Advance(60);
        p.Deliver(50);
        p.Pass();
        Assert.Equal(50, p.PositionFrame);
        Assert.False(p.Position.IsPending);
        Assert.DoesNotContain(200L, p.ReportedSince(Time(50)));
    }

    [Fact]
    public void BurstOfSeeks_AlwaysReportsTheNewest()
    {
        var p = new Preview();
        p.Land(10);
        var changes = p.Changes;

        for (var call = 1; call <= 60; call++)
        {
            p.Seek(Time(10 + (call * 7)));
            Assert.Equal(10 + (call * 7), p.PositionFrame);
            if (call % 4 == 0)
            {
                p.Advance(16);
                p.Pass();
                Assert.Equal(10 + (call * 7), p.PositionFrame);
            }
        }

        // Every call asked for another frame, so each one was news.
        Assert.Equal(changes + 60, p.Changes);

        // One more when the last of them is the picture; none for the one that landed on the way.
        p.Settle(430);
        Assert.Equal(430, p.PositionFrame);
        Assert.Equal([38, 430], p.Landings.Select(l => l.Frame));
        Assert.False(p.Position.IsPending);
        Assert.Equal(changes + 61, p.Changes);
    }

    [Fact]
    public void SeekToTheStartAndPlay_FromTheLastFrame_NeverReportsTheLastFrameAgain()
    {
        // The editor at the end of the kept range: parked there, landed, and then Space.
        var p = new Preview();
        p.Land(Last);
        Assert.Equal(Last, p.PositionFrame);

        p.Seek(0);
        p.Play();
        Assert.Equal(0, p.PositionFrame);

        // The screen's player rests on its last frame, so the way to frame 0 is by another frame.
        p.Pass();
        Assert.Equal(["seek 8"], p.Calls);
        p.Advance(30);
        p.Deliver(8);
        p.Pass();
        p.Advance(40);
        p.Pass();
        Assert.Equal(["seek 8", "seek 0"], p.Calls);
        Assert.Equal(0, p.PositionFrame);

        p.Advance(30);
        p.Deliver(0);
        var beforeStart = p.Now;
        p.Advance(1);
        p.Pass();
        Assert.Equal(["seek 8", "seek 0", "resume"], p.Calls);
        Assert.True(p.Policy.IsPlaying);
        Assert.Equal(0, p.PositionFrame);
        Assert.False(p.Position.IsPending);

        // A frame that set out before the clock was started and is handed over only now, still
        // reporting the frame that was left.
        p.Advance(3);
        p.Frame(Screen, Last, setOutAt: beforeStart);
        p.Pass();
        Assert.Equal(0, p.PositionFrame);
        Assert.Equal(1, p.Position.FramesFromBeforeStart);

        // Playback.
        for (var frame = 1; frame <= 5; frame++)
        {
            p.Advance(33);
            p.Frame(Screen, frame);
            p.Frame(Camera, 0);
            p.Pass();
            Assert.Equal(frame, p.PositionFrame);
        }

        var reported = p.ReportedSince(0);
        Assert.DoesNotContain((long)Last, reported);
        Assert.Equal(reported.Order(), reported);
    }

    [Fact]
    public void SeekWhilePlaying_ReportsItsFrame_NotTheFramesStillPlayed()
    {
        var p = new Preview();
        p.PlayFrom(100);
        p.Advance(500);
        p.Frame(Screen, 115);
        p.Frame(Camera, 109);
        p.Pass();
        Assert.Equal(115, p.PositionFrame);

        p.Seek(Time(10));
        Assert.Equal(10, p.PositionFrame);

        // The clock is still running: the render thread has not seen the request.
        p.Advance(5);
        p.Frame(Screen, 116);
        p.Frame(Camera, 110);
        p.Pass();
        Assert.Equal(["pause"], p.Calls);
        Assert.Equal(10, p.PositionFrame);

        // A frame that was on its way when the clock stopped.
        p.Advance(8);
        p.Frame(Screen, 117);
        p.Pass();
        Assert.Equal(10, p.PositionFrame);

        p.Advance(40);
        p.Pass();
        Assert.Equal(["pause", "seek 10"], p.Calls);
        p.Advance(60);
        p.Deliver(10);
        p.Pass();

        // The first change after the clock ran is reached when the players have gone quiet.
        Assert.Equal(["pause", "seek 10"], p.Calls);
        Assert.True(p.Position.IsPending);
        p.Advance(40);
        p.Pass();
        Assert.Equal(["pause", "seek 10", "resume"], p.Calls);
        Assert.Equal(10, p.PositionFrame);
        Assert.False(p.Position.IsPending);

        p.Advance(20);
        p.Frame(Screen, 11);
        p.Frame(Camera, 5);
        p.Pass();
        Assert.Equal(11, p.PositionFrame);

        var reported = p.ReportedSince(Time(10));
        Assert.All(reported, frame => Assert.InRange(frame, 10, 11));
        Assert.Equal(reported.Order(), reported);
    }

    [Fact]
    public void PauseThenSeek_ReportsTheSeek_NotTheFrameThePlayersStoppedOn()
    {
        // The editor when playback reaches the end of the kept range.
        var p = new Preview();
        p.PlayFrom(100);
        p.Advance(500);
        p.Frame(Screen, 115);
        p.Frame(Camera, 109);
        p.Pass();

        p.Pause();
        p.Seek(Time(114));
        Assert.Equal(114, p.PositionFrame);

        p.Advance(5);
        p.Frame(Screen, 116);
        p.Pass();
        Assert.Equal(["pause"], p.Calls);
        Assert.Equal(114, p.PositionFrame);

        p.Advance(40);
        p.Pass();
        Assert.Equal(["pause", "seek 114"], p.Calls);
        p.Advance(60);
        p.Deliver(114);
        p.Pass();
        Assert.True(p.Position.IsPending);
        p.Advance(40);
        p.Pass();

        Assert.Equal(StudioPreviewLandingKind.Seek, p.Landings[^1].Kind);
        Assert.Equal(114, p.PositionFrame);
        Assert.False(p.Position.IsPending);
        Assert.All(p.ReportedSince(Time(114)), frame => Assert.Equal(114, frame));
    }

    [Fact]
    public void Pause_OnItsOwn_ReportsTheFrameThePlayersComeToRestOn()
    {
        var p = new Preview();
        p.PlayFrom(100);
        p.Advance(1650);
        p.Frame(Screen, 150);
        p.Frame(Camera, 143);
        p.Pass();
        Assert.Equal(150, p.PositionFrame);

        p.Pause();
        p.Pass();

        // One more frame was on its way when the clock stopped.
        p.Advance(8);
        p.Frame(Screen, 151);
        p.Pass();
        Assert.Equal(151, p.PositionFrame);

        p.Advance(40);
        p.Pass();
        Assert.Equal(["pause", "seek 151"], p.Calls);
        p.Advance(30);
        p.Deliver(151);
        p.Pass();
        p.Advance(40);
        p.Pass();

        Assert.Equal(StudioPreviewLandingKind.Snap, p.Landings[^1].Kind);
        Assert.Equal(151, p.PositionFrame);
        Assert.False(p.Position.IsPending);
    }

    [Fact]
    public void EndOfTheRecording_ReportsTheLastFrameAtOnce_WhateverThePlayersGotTo()
    {
        var p = new Preview();
        p.PlayFrom(880);
        p.Advance(570);
        p.Frame(Screen, 897);
        p.Frame(Camera, 891);
        p.Pass();
        Assert.Equal(897, p.PositionFrame);
        var changes = p.Changes;

        // The clock stops at the end. Whoever hears that playback stopped reads the position now.
        p.Advance(30);
        p.Ended();
        Assert.Equal(Last, p.PositionFrame);
        Assert.Equal(changes + 1, p.Changes);

        // A frame that was on its way does not take the position back.
        p.Advance(5);
        p.Frame(Screen, 898);
        p.Pass();
        Assert.Equal(Last, p.PositionFrame);

        p.Settle(Last);
        Assert.Equal(StudioPreviewLandingKind.Snap, p.Landings[^1].Kind);
        Assert.Equal(Last, p.PositionFrame);
        Assert.False(p.Position.IsPending);
        Assert.Equal([897L, Last], p.Reported.Where(frame => frame >= 897).Distinct());
        Assert.Equal(changes + 1, p.Changes);
    }

    [Fact]
    public void SeekJustBeforePlaybackEnds_StaysThePosition()
    {
        var p = new Preview();
        p.PlayFrom(880);
        p.Advance(600);
        p.Frame(Screen, 898);
        p.Frame(Camera, 892);
        p.Pass();

        // Asked for, and the clock ends before the render thread has seen the request.
        p.Seek(Time(10));
        p.Ended();
        Assert.Equal(10, p.PositionFrame);

        p.Settle(10);
        Assert.Equal(10, p.PositionFrame);
        Assert.Equal(StudioPreviewLandingKind.Seek, p.Landings[^1].Kind);
        Assert.All(p.ReportedSince(Time(10)), frame => Assert.Equal(10, frame));
    }

    [Fact]
    public void SeekToTheFrameAlreadyShown_AsksNothingOfThePlayers_AndPlayingGoesOnFromIt()
    {
        var p = new Preview();
        p.Land(100);
        var changes = p.Changes;

        p.Seek(Time(100));
        p.Play();
        Assert.Equal(100, p.PositionFrame);
        Assert.True(p.Position.IsPending);

        p.Pass();
        Assert.Equal(["resume"], p.Calls);
        Assert.False(p.Position.IsPending);
        Assert.Equal(changes, p.Changes);

        p.Advance(20);
        p.Frame(Screen, 101);
        p.Frame(Camera, 95);
        p.Pass();
        Assert.Equal(101, p.PositionFrame);
        Assert.Equal(changes + 1, p.Changes);
    }

    [Fact]
    public void StepForward_IsReportedAtTheCall_AndIsNewsAgainWhenThePictureHasStepped()
    {
        var p = new Preview();
        p.Land(100);
        var changes = p.Changes;

        // The editor's step: Seek(Position + one frame).
        p.Seek(p.Seconds + (1 / 30.0));
        Assert.Equal(101, p.PositionFrame);

        p.Pass();
        Assert.Equal(["step 0", "step 1"], p.Calls);
        p.Advance(15);
        p.Frame(Screen, 101);
        p.Frame(Camera, 95);
        p.Pass();

        Assert.Equal(StudioPreviewLandingKind.Step, p.Landings[^1].Kind);
        Assert.Equal(101, p.PositionFrame);
        Assert.False(p.Position.IsPending);
        Assert.Equal(changes + 2, p.Changes);

        // The clock is brought along afterwards, which tells nobody anything.
        p.Advance(150);
        p.Pass();
        p.Advance(40);
        p.Deliver(101);
        p.Pass();
        Assert.Equal(101, p.PositionFrame);
        Assert.Equal(changes + 2, p.Changes);
    }

    [Fact]
    public void SeekWhoseFrameIsLost_KeepsItsFrameReported_ThroughTheRepair()
    {
        var p = new Preview();
        p.Land(50);

        p.Seek(Time(100));
        p.Pass();
        p.Advance(5);
        p.Completed(Screen);
        p.Completed(Camera);
        p.Frame(Camera, 94);

        // No frame from the screen within the grace period: by way of the neighbouring frame.
        p.Advance(45);
        p.Pass();
        Assert.Equal(["seek 100", "seek 99"], p.Calls);
        Assert.Equal(100, p.PositionFrame);
        Assert.True(p.Position.IsPending);

        p.Advance(30);
        p.Deliver(99);
        p.Pass();
        p.Advance(40);
        p.Pass();
        Assert.Equal(["seek 100", "seek 99", "seek 100"], p.Calls);
        Assert.Equal(100, p.PositionFrame);

        p.Advance(30);
        p.Deliver(100);
        p.Pass();
        Assert.Equal(100, p.Landings[^1].Frame);
        Assert.Equal(1, p.Landings[^1].Repairs);
        Assert.Equal(100, p.PositionFrame);
        Assert.False(p.Position.IsPending);
        Assert.All(p.ReportedSince(Time(100)), frame => Assert.Equal(100, frame));
    }

    private static StudioPreviewLanding Landing(long frame, StudioPreviewLandingKind kind = StudioPreviewLandingKind.Seek) =>
        new(frame, true, kind, 0, 0, 0);

    /// <summary>A time in the middle of a frame of the 30 fps timeline.</summary>
    private static double Time(long frame) => (frame + 0.5) / 30.0;

    private sealed class FakePlayers(Preview preview) : IStudioPreviewTransport
    {
        public List<string> Calls { get; } = [];

        public void Seek(long timelineFrame) => Calls.Add($"seek {timelineFrame}");

        public void StepForward(int track) => Calls.Add($"step {track}");

        public bool Resume()
        {
            // As the engine's transport does: told before the clock moves.
            preview.Position.ClockStarted(preview.Now);
            Calls.Add("resume");
            return true;
        }

        public void Pause() => Calls.Add("pause");

        public bool IsDelivering(int track) => false;
    }

    /// <summary>
    /// A preview without players: what a caller calls, what the render thread does in one pass of
    /// its loop, and the frames a test has the players hand over.
    /// </summary>
    private sealed class Preview
    {
        private readonly List<StudioPreviewLanding> _taken = [];
        private readonly List<(double Seconds, int Index)> _seeks = [];
        private bool _wantPlaying;
        private bool _policyWantsPlaying;

        public Preview(StudioPreviewTimeline? timeline = null)
        {
            Timeline = timeline ?? new StudioPreviewTimeline(30, 30, [new(30, 900, 0), new(30, 900, 0.2)]);
            Players = new FakePlayers(this);
            Policy = new StudioPreviewSeekPolicy(Players, Timeline, new StudioPreviewSeekSettings { TicksPerSecond = 1000 }, () => Now);

            // The players hand over their first frames when they open.
            for (var track = 0; track < Timeline.TrackCount; track++)
            {
                Policy.OnFrame(track, 0, Now);
            }
        }

        public StudioPreviewTimeline Timeline { get; }

        public StudioPreviewPosition Position { get; } = new();

        public StudioPreviewSeekPolicy Policy { get; }

        public FakePlayers Players { get; }

        public List<string> Calls => Players.Calls;

        public List<StudioPreviewLanding> Landings { get; } = [];

        public long Now { get; private set; } = 1000;

        /// <summary>The frame reported after every call and every pass, in order.</summary>
        public List<long> Reported { get; } = [];

        /// <summary>Times <c>PositionChanged</c> would have been raised.</summary>
        public int Changes { get; private set; }

        public long PositionFrame => Position.Frame;

        /// <summary>What <c>StudioPreviewEngine.Position</c> returns.</summary>
        public double Seconds => Timeline.FrameStart(Position.Frame);

        /// <summary>Everything reported from the moment the latest <c>Seek</c> to a time returned.</summary>
        public List<long> ReportedSince(double seconds) => Reported[_seeks.Last(s => s.Seconds.Equals(seconds)).Index..];

        public void Advance(long milliseconds) => Now += milliseconds;

        // What StudioPreviewEngine.Seek, Play and Pause do.

        public void Seek(double seconds)
        {
            Changes += Position.Request(Timeline.FrameForSeek(seconds)) ? 1 : 0;
            _seeks.Add((seconds, Reported.Count));
            Reported.Add(Position.Frame);
        }

        public void Play() => _wantPlaying = true;

        public void Pause() => _wantPlaying = false;

        /// <summary>One pass of the render thread: StudioPreviewEngine.Iterate, without the drawing.</summary>
        /// <param name="beforeUpdate">Something another thread does between the pump and the position's update.</param>
        public void Pass(Action? beforeUpdate = null)
        {
            if (Position.TryTake(out var requested))
            {
                Policy.RequestSeek(requested);
            }

            if (_wantPlaying != _policyWantsPlaying)
            {
                Policy.SetPlaying(_wantPlaying);
                _policyWantsPlaying = _wantPlaying;
            }

            Policy.Pump();
            Policy.TakeLandings(_taken);
            Landings.AddRange(_taken);
            beforeUpdate?.Invoke();
            Changes += Position.Update(_taken, Policy.IsSeeking) ? 1 : 0;
            _taken.Clear();
            Reported.Add(Position.Frame);
        }

        /// <summary>The clock reached the end of the recording: StudioPreviewEngine.OnClockEnded.</summary>
        public void Ended()
        {
            _wantPlaying = false;
            _policyWantsPlaying = false;
            Changes += Policy.OnEnded() && Position.Rest(Timeline.LastFrame) ? 1 : 0;
            Reported.Add(Position.Frame);
        }

        // What the players do. A frame reaches the policy and the position as in StudioPreviewEngine.DrainSignals.

        public void Frame(int track, long playerFrame, long? setOutAt = null)
        {
            var at = setOutAt ?? Now;
            var playback = Policy.AcceptsPlaybackFrames;
            Policy.OnFrame(track, playerFrame, at);
            if (track == Screen && playback)
            {
                Position.Played(Policy.ShownFrame(Screen), at);
            }
        }

        public void Completed(int track) => Policy.OnSeekCompleted(track, Now);

        /// <summary>Every player answers the clock's position the way a real one does: with a frame when it was moved, and with SeekCompleted.</summary>
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

        /// <summary>Lets the players answer whatever the policy asks until it is at rest on a frame.</summary>
        public void Settle(long timelineFrame)
        {
            for (var round = 0; round < 40 && !(Policy.IsIdle && Policy.SettledFrame == timelineFrame && !Position.HasNewRequest); round++)
            {
                Pass();
                Advance(50);
                var seek = Calls.LastOrDefault(c => c.StartsWith("seek ", StringComparison.Ordinal));
                if (seek is not null)
                {
                    Deliver(long.Parse(seek[5..]));
                }

                Pass();
                Advance(50);
            }

            Assert.Equal(timelineFrame, Policy.SettledFrame);
            Assert.True(Policy.IsIdle);
        }

        /// <summary>Seeks to a frame, lets it land, and forgets how it got there.</summary>
        public void Land(long timelineFrame)
        {
            Seek((timelineFrame + 0.5) / Timeline.FrameRate);
            Settle(timelineFrame);
            Advance(100);
            Calls.Clear();
            Landings.Clear();
        }

        public void PlayFrom(long timelineFrame)
        {
            Land(timelineFrame);
            Play();
            Pass();
            Assert.True(Policy.IsPlaying);
            Calls.Clear();
        }
    }
}
