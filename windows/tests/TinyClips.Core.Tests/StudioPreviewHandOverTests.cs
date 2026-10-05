using TinyClips.Core.Studio.Preview;

namespace TinyClips.Core.Tests;

/// <summary>
/// Which hand-overs are frames of playback: those that set out while the clock ran, which ends
/// at the moment the engine decides to stop it and not at some moment after the clock has.
/// </summary>
public sealed class StudioPreviewHandOverTests
{
    private const long Never = StudioPreviewHandOverKinds.Never;

    // The clock was started at 100 and, where it was stopped, that was decided at 200.
    private const long Started = 100;
    private const long Stopped = 200;

    [Fact]
    public void AHandOverThatSetsOutWhileTheClockRuns_IsOneOfPlayback()
    {
        Assert.Equal(StudioPreviewHandOverKind.Playback, StudioPreviewHandOverKinds.Of(150, Started, Never, stopLeavesOut: false));

        // At the very moment the clock was started.
        Assert.Equal(StudioPreviewHandOverKind.Playback, StudioPreviewHandOverKinds.Of(Started, Started, Never, stopLeavesOut: false));

        // It set out before the stop and was kept waiting until after it.
        Assert.Equal(StudioPreviewHandOverKind.Playback, StudioPreviewHandOverKinds.Of(199, Started, Stopped, stopLeavesOut: true));
    }

    [Fact]
    public void OneThatSetOutBeforeTheClockWasStarted_AnswersAPosition()
    {
        Assert.Equal(StudioPreviewHandOverKind.Answer, StudioPreviewHandOverKinds.Of(90, Started, Never, stopLeavesOut: false));
        Assert.Equal(StudioPreviewHandOverKind.Answer, StudioPreviewHandOverKinds.Of(90, Started, Stopped, stopLeavesOut: true));
    }

    [Fact]
    public void OneThatSetsOutOnceTheClockHasBeenStopped_IsLeftOut()
    {
        Assert.Equal(StudioPreviewHandOverKind.Late, StudioPreviewHandOverKinds.Of(Stopped, Started, Stopped, stopLeavesOut: true));
        Assert.Equal(StudioPreviewHandOverKind.Late, StudioPreviewHandOverKinds.Of(208, Started, Stopped, stopLeavesOut: true));
    }

    [Fact]
    public void OnceThePlayersHaveBeenAskedForSomething_ItAnswersThat()
    {
        Assert.Equal(StudioPreviewHandOverKind.Answer, StudioPreviewHandOverKinds.Of(250, Started, Stopped, stopLeavesOut: false));
    }

    [Fact]
    public void WhileNoClockRuns_EverythingAnswersAPosition()
    {
        // Before the clock was ever started, and after it ran out by itself: nobody stopped it,
        // so nothing is left out.
        Assert.Equal(StudioPreviewHandOverKind.Answer, StudioPreviewHandOverKinds.Of(5, Never, Never, stopLeavesOut: false));
        Assert.Equal(StudioPreviewHandOverKind.Answer, StudioPreviewHandOverKinds.Of(500, Never, Never, stopLeavesOut: false));
    }

    [Fact]
    public void WhileTheClockIsBeingStopped_AHandOverCountsAsSetOutAfterIt()
    {
        // It has been said that frames are left out, and not yet from when.
        Assert.Equal(StudioPreviewHandOverKind.Late, StudioPreviewHandOverKinds.Of(150, Started, Never, stopLeavesOut: true));
        Assert.Equal(StudioPreviewHandOverKind.Late, StudioPreviewHandOverKinds.Of(Started, Started, Never, stopLeavesOut: true));

        // One from before the clock was started answers a position, as it would a moment later.
        Assert.Equal(StudioPreviewHandOverKind.Answer, StudioPreviewHandOverKinds.Of(90, Started, Never, stopLeavesOut: true));
    }

    [Fact]
    public void NoHandOverThatSetsOutAfterTheStop_IsOneOfPlayback_WheneverItLooks()
    {
        // The stop says that frames are left out, then from when, and then tells the clock. A
        // hand-over reads from when first and whether second, each at any point of that.
        (long At, bool LeavesOut)[] states = [(Never, false), (Never, true), (Stopped, true)];
        long[] setOuts = [150, 199, 200, 201, 208, 260];
        for (var timeRead = 0; timeRead < states.Length; timeRead++)
        {
            for (var switchRead = timeRead; switchRead < states.Length; switchRead++)
            {
                foreach (var setOut in setOuts)
                {
                    if (timeRead == 0 && switchRead == 0 && setOut >= Stopped)
                    {
                        // A hand-over that finds nothing of the stop looked before the stop
                        // began, and so set out before it.
                        continue;
                    }

                    var kind = StudioPreviewHandOverKinds.Of(setOut, Started, states[timeRead].At, states[switchRead].LeavesOut);
                    if (setOut >= Stopped)
                    {
                        Assert.Equal(StudioPreviewHandOverKind.Late, kind);
                    }
                    else if (timeRead == switchRead && timeRead != 1)
                    {
                        // Before the stop began and after it was all said, one that set out in time is in.
                        Assert.Equal(StudioPreviewHandOverKind.Playback, kind);
                    }
                    else
                    {
                        // In between it may be left out, and is never anything else.
                        Assert.Equal(StudioPreviewHandOverKind.Late, kind);
                    }
                }
            }
        }
    }

    [Fact]
    public void WithTheStopNotedOnlyAfterTheClockHasStopped_AFrameFromInBetweenPassesForPlayback()
    {
        // What the engine did before: the clock stopped at 200, a player handed over the frame
        // it had coming at 208, and the stop was noted at 212.
        Assert.Equal(StudioPreviewHandOverKind.Playback, StudioPreviewHandOverKinds.Of(208, Started, clockStoppedAt: 212, stopLeavesOut: true));

        // Noted before the clock is told, it is left out.
        Assert.Equal(StudioPreviewHandOverKind.Late, StudioPreviewHandOverKinds.Of(208, Started, clockStoppedAt: Stopped, stopLeavesOut: true));
    }
}
