using TinyClips.Core.Studio.Preview;

namespace TinyClips.Core.Tests;

/// <summary>
/// The rules that tell which frame a player hands over while the clock runs. Times are in
/// milliseconds on a clock of the test's own; a player looks for a frame to hand over every ten.
/// </summary>
public sealed class StudioPreviewFrameNamerTests
{
    private static readonly StudioPreviewNamingSettings Settings = new() { TicksPerSecond = 1000 };

    [Fact]
    public void SteadyPlay_NumbersEveryFrameAsTheOneAfterTheLast()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);

        // A frame every thirtieth of a second, handed over at the player's next look.
        long[] at = [20, 50, 90, 120, 150, 190, 220, 250, 290];
        for (var index = 0; index < at.Length; index++)
        {
            var handOver = player.HandOver(at[index], name: 100 + index);

            Assert.Equal(StudioPreviewEarlierFrame.Nothing, handOver.Earlier);
            Assert.Equal(StudioPreviewFrameKnowledge.Known, handOver.Named.Knowledge);
            Assert.Equal(100 + index, handOver.Named.Frame);
            Assert.Equal("next", handOver.Named.Rule);
            Assert.False(handOver.Named.Inferred);
        }

        Assert.False(player.Namer.ShowsUnsure);
        Assert.False(player.Namer.Inferred);
    }

    [Fact]
    public void TheFirstFrameOfAClipThatHasNotBegun_IsItsFirst()
    {
        var player = new Player();
        player.Start(restingFrame: -1, at: 0);

        var handOver = player.HandOver(at: 210, name: 0);

        Assert.Equal(StudioPreviewFrameKnowledge.Known, handOver.Named.Knowledge);
        Assert.Equal(0, handOver.Named.Frame);
    }

    [Fact]
    public void AFrameWhosePositionNamesALaterFrameThanTheNext_HasNoNumber()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);

        // Held up: the position has moved on to frame 102. It is frame 101 or frame 102.
        var late = player.HandOver(at: 90, name: 102, into: 20);

        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, late.Named.Knowledge);
        Assert.Equal(102, late.Named.Frame);
        Assert.Equal("no number", late.Named.Rule);
    }

    [Fact]
    public void AnIdleLookOfThePlayer_GivesTheFrameBeforeItsNumber()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);
        player.HandOver(at: 90, name: 102, into: 4);

        // The next one begins three looks later, on time: at the look in between the player had
        // nothing due, so the frame before was not late, and was the one its position named.
        var next = player.HandOver(at: 120, name: 103);

        Assert.Equal(StudioPreviewEarlierFrame.Known, next.Earlier);
        Assert.Equal(102, next.EarlierFrame);
        Assert.Equal(StudioPreviewFrameKnowledge.Known, next.Named.Knowledge);
        Assert.Equal(103, next.Named.Frame);
        Assert.Equal("next", next.Named.Rule);
    }

    [Theory]
    [InlineData(100, 2.0, "the next look of the player: it may have been catching up")]
    [InlineData(125, 2.0, "not a whole number of looks after the one before")]
    [InlineData(120, 20.0, "late in its own frame")]
    public void WithoutAnIdleLook_TheFrameBeforeStaysWithoutANumber(long at, double into, string because)
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);
        player.HandOver(at: 90, name: 102, into: 4);

        var next = player.HandOver(at, name: 103, into);

        Assert.True(next.Earlier == StudioPreviewEarlierFrame.Nothing, because);
        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, next.Named.Knowledge);
        Assert.Equal(103, next.Named.Frame);
    }

    [Fact]
    public void AnIdleLook_SaysNothing_WhenTheFrameAfterIsNotTheVeryNext()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);
        player.HandOver(at: 90, name: 102, into: 4);

        // As it was seen with every processor busy: nothing for more than a second, while frame
        // after frame came due. A player that does that is not waiting for its next frame. It
        // cannot keep up, and what it handed over last can be far behind its position.
        var next = player.HandOver(at: 1350, name: 140);

        Assert.Equal(StudioPreviewEarlierFrame.Nothing, next.Earlier);

        // And this one is no more certain. After so long without a number it is shown under
        // the number of its position, and called unsure.
        Assert.Equal(StudioPreviewFrameKnowledge.Unsure, next.Named.Knowledge);
        Assert.Equal(140, next.Named.Frame);
    }

    [Fact]
    public void AnIdleLook_SaysNothing_WhenTheCollectorHeldTheProcessInBetween()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);
        player.HandOver(at: 90, name: 102, into: 4);
        player.Collect(15);

        var next = player.HandOver(at: 120, name: 103);

        Assert.Equal(StudioPreviewEarlierFrame.Nothing, next.Earlier);
        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, next.Named.Knowledge);
    }

    [Fact]
    public void AnIdleLook_SaysNothing_WhenThePositionMovedToTheNextFrameDuringTheCopyBefore()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);
        player.HandOver(at: 90, name: 102, into: 32, nameAfter: 103);

        // Two looks later, with everything else as an idle look has it.
        var next = player.HandOver(at: 110, name: 103, into: 12);

        Assert.Equal(StudioPreviewEarlierFrame.Nothing, next.Earlier);
        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, next.Named.Knowledge);
    }

    [Fact]
    public void AFrameKeptWaitingByTheCollector_IsTheOneThatWasNext()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);

        // The collector stops every thread of the engine for 60 ms. The player announced frame
        // 101 when it was due; its hand-over begins when the collector is done, with a position
        // that has moved on.
        player.Collect(60);
        var kept = player.HandOver(at: 110, name: 102, into: 20);

        Assert.Equal(StudioPreviewFrameKnowledge.Known, kept.Named.Knowledge);
        Assert.Equal(101, kept.Named.Frame);
        Assert.Equal("kept by the collector", kept.Named.Rule);

        // And the chain goes on from there.
        var next = player.HandOver(at: 120, name: 102, into: 30);
        Assert.Equal(StudioPreviewFrameKnowledge.Known, next.Named.Knowledge);
        Assert.Equal(102, next.Named.Frame);
    }

    [Fact]
    public void AFrameKeptAtTheDoorByTheCollector_HasNoNumber_WhenTheHandOverBeforeLeftThePlayerBehind()
    {
        var player = new Player();
        player.Start(restingFrame: 166, at: 0);

        // As it was seen from the window: the collector kept the hand-over of frame 167 for
        // 153 ms after its copy, and four frames came due before the player had its thread back.
        var first = player.HandOver(at: 20, name: 167, into: 1, copy: 157, nameAfter: 171, collectorMeanwhile: 153);
        Assert.Equal(StudioPreviewFrameKnowledge.Known, first.Named.Knowledge);
        Assert.Equal(167, first.Named.Frame);

        // The collector once more, for 6 ms, at the door of the next hand-over. The player was
        // behind when it announced that one and had left two frames out: it was frame 170.
        player.Collect(6);
        var next = player.HandOver(at: 185, name: 171, into: 32, nameAfter: 172);

        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, next.Named.Knowledge);
        Assert.Equal(171, next.Named.Frame);
    }

    [Fact]
    public void WhatKeepsAHandOverAfterItsCopy_IsNotTakenForWhatKeptTheNextAtTheDoor()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);

        // The copy of frame 100 was prompt. The collector stopped the thread for 60 ms on its
        // way out, and two frames came due before the player had it back.
        var held = player.HandOver(at: 20, name: 100, tail: 62, nameAtExit: 102, collectorInTail: 60);
        Assert.Equal(StudioPreviewFrameKnowledge.Known, held.Named.Knowledge);
        Assert.Equal(100, held.Named.Frame);

        // The next hand-over begins at the player's next look, and nothing kept it at the door.
        var next = player.HandOver(at: 90, name: 102, into: 8);

        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, next.Named.Knowledge);
        Assert.Equal(102, next.Named.Frame);
    }

    [Fact]
    public void AFrameKeptAtTheDoorByTheCollector_IsTheNext_WhenTheHandOverBeforeWasKeptOnlyWhileItsOwnFrameWasDue()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);

        // Kept for 8 ms on its way out, with frame 100 still the one due: the player is not behind.
        player.HandOver(at: 20, name: 100, tail: 10, nameAtExit: 100, collectorInTail: 8);
        player.Collect(60);

        var kept = player.HandOver(at: 115, name: 102, into: 20);

        Assert.Equal(StudioPreviewFrameKnowledge.Known, kept.Named.Knowledge);
        Assert.Equal(101, kept.Named.Frame);
        Assert.Equal("kept by the collector", kept.Named.Rule);
    }

    [Fact]
    public void AnIdleLook_IsCountedFromWhenThePlayerHadItsThreadBack()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);

        // In doubt, and kept for 15 ms after its copy by something that was not the collector.
        player.HandOver(at: 90, name: 102, into: 4, tail: 15);

        // 17 ms after that copy, and 2 ms after the player had its thread back: it has not
        // looked for a frame in between.
        var next = player.HandOver(at: 110, name: 103, into: 1);

        Assert.Equal(StudioPreviewEarlierFrame.Nothing, next.Earlier);
        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, next.Named.Knowledge);
    }

    [Fact]
    public void AFrameThatComesLongerAfterTheCollectorThanTheCollectorAccountsFor_HasNoNumber()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);
        player.Collect(60);

        // 105 ms after the frame before: a frame time, the collector's 60 ms and more than the margin.
        var late = player.HandOver(at: 128, name: 103, into: 5);

        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, late.Named.Knowledge);
        Assert.Equal(103, late.Named.Frame);
    }

    [Fact]
    public void ACopyThatTookLongWhileAFrameCameDue_HasNoNumber_AndMayHoldThatFrame()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);

        var slow = player.HandOver(at: 50, name: 101, copy: 40, nameAfter: 102);

        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, slow.Named.Knowledge);
        Assert.Equal(102, slow.Named.Frame);
        Assert.Equal("copy held up", slow.Named.Rule);
    }

    [Fact]
    public void ACopyThatTookLongWhileNoFrameCameDue_IsTheFrameThatWasAnnounced()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);

        // Slow, as on a graphics adapter that is busy, and still inside the frame's own time.
        var slow = player.HandOver(at: 50, name: 101, copy: 20);

        Assert.Equal(StudioPreviewFrameKnowledge.Known, slow.Named.Knowledge);
        Assert.Equal(101, slow.Named.Frame);
        Assert.Equal("next", slow.Named.Rule);
    }

    [Fact]
    public void ACopyKeptWaitingByTheCollectorOnItsWayBack_IsTheFrameThatWasAnnounced()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);

        // The collector stopped the engine's thread after the player had made the copy: 85 ms
        // of the 88 are the collector's, and two frames came due meanwhile.
        var held = player.HandOver(at: 50, name: 101, copy: 88, nameAfter: 103, collectorMeanwhile: 85);

        Assert.Equal(StudioPreviewFrameKnowledge.Known, held.Named.Knowledge);
        Assert.Equal(101, held.Named.Frame);

        // The collector's time counts as its own from here on, and not once more.
        var after = player.HandOver(at: 140, name: 104, into: 4);
        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, after.Named.Knowledge);
    }

    [Fact]
    public void ACopyThatTookLongerThanTheCollectorAccountsFor_HasNoNumber()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);

        var held = player.HandOver(at: 50, name: 101, copy: 88, nameAfter: 103, collectorMeanwhile: 60);

        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, held.Named.Knowledge);
        Assert.Equal(103, held.Named.Frame);
    }

    [Fact]
    public void TheOnlyNumberLeft_NumbersAFrameAndTheOneBeforeIt()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);

        // Frame 101 at the least, and nothing more is known of it.
        player.HandOver(at: 50, name: 101, copy: 40, nameAfter: 102);

        // The next one's position names frame 102. It is later than the one before and no later
        // than 102: so it is 102, and the one before was 101.
        var next = player.HandOver(at: 100, name: 102, into: 12);

        Assert.Equal(StudioPreviewEarlierFrame.KnownIfPrompt, next.Earlier);
        Assert.Equal(101, next.EarlierFrame);
        Assert.Equal(StudioPreviewFrameKnowledge.Known, next.Named.Knowledge);
        Assert.Equal(102, next.Named.Frame);
        Assert.Equal("only one left", next.Named.Rule);
        Assert.True(next.Named.EarlierConfirmed);

        // That follows from the order of the frames alone, whatever the player does.
        Assert.False(next.Named.Inferred);
    }

    [Fact]
    public void ANumberFromTheCollectorRule_IsInferred_AndSoAreTheOnesAfterIt_UntilThirtyHaveComeOnTime()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        Assert.False(player.HandOver(at: 20, name: 100).Named.Inferred);

        player.Collect(60);
        var kept = player.HandOver(at: 110, name: 102, into: 20);
        Assert.Equal("kept by the collector", kept.Named.Rule);
        Assert.True(kept.Named.Inferred);

        // The next in line, and late: it does not count towards the thirty.
        var late = player.HandOver(at: 120, name: 102, into: 30);
        Assert.Equal(102, late.Named.Frame);
        Assert.True(late.Named.Inferred);

        long at = 120;
        for (var index = 0; index < 29; index++)
        {
            at += index % 3 == 0 ? 40 : 30;
            var onTime = player.HandOver(at, name: 103 + index);
            Assert.Equal("next", onTime.Named.Rule);
            Assert.True(onTime.Named.Inferred, $"frame {103 + index}");
        }

        var thirtieth = player.HandOver(at + 30, name: 132);
        Assert.Equal(132, thirtieth.Named.Frame);
        Assert.False(thirtieth.Named.Inferred);
        Assert.False(player.Namer.Inferred);
    }

    [Fact]
    public void ANumberFromAnIdleLook_IsInferred_AndTheNextFramesWithIt()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);
        player.HandOver(at: 90, name: 102, into: 4);

        var next = player.HandOver(at: 120, name: 103);

        Assert.Equal(StudioPreviewEarlierFrame.Known, next.Earlier);
        Assert.True(next.Named.Inferred);
        Assert.True(player.HandOver(at: 150, name: 104).Named.Inferred);
    }

    [Fact]
    public void ACopyWhoseTimeIsLaidToTheCollector_IsInferred_AndASlowOneWhileNoFrameCameDueIsNot()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);

        Assert.False(player.HandOver(at: 50, name: 101, copy: 20).Named.Inferred);

        var held = player.HandOver(at: 90, name: 102, copy: 88, nameAfter: 104, collectorMeanwhile: 85);
        Assert.Equal(StudioPreviewFrameKnowledge.Known, held.Named.Knowledge);
        Assert.Equal(102, held.Named.Frame);
        Assert.True(held.Named.Inferred);
    }

    [Fact]
    public void WhenTheClockStartsAgain_NothingIsInferred()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);
        player.Collect(60);
        Assert.True(player.HandOver(at: 110, name: 102, into: 20).Named.Inferred);

        // The clock stopped, the frame was fetched anew, and the clock starts from it.
        player.Start(restingFrame: 101, at: 1000);

        Assert.False(player.Namer.Inferred);
        Assert.False(player.HandOver(at: 1020, name: 102).Named.Inferred);
    }

    [Fact]
    public void AFrameWithoutANumber_IsNotCalledInferred()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);
        player.Collect(60);
        player.HandOver(at: 110, name: 102, into: 20);

        var unknown = player.HandOver(at: 150, name: 105);

        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, unknown.Named.Knowledge);
        Assert.False(unknown.Named.Inferred);
    }

    [Fact]
    public void TheOnlyNumberLeft_IsNotBelieved_WhenTheCopyTookLong()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);
        player.HandOver(at: 50, name: 101, copy: 40, nameAfter: 102);

        var next = player.HandOver(at: 100, name: 102, into: 28, copy: 9, nameAfter: 103);

        Assert.Equal(StudioPreviewEarlierFrame.KnownIfPrompt, next.Earlier);
        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, next.Named.Knowledge);
        Assert.False(next.Named.EarlierConfirmed);
    }

    [Fact]
    public void TheSameFrameHandedOverAgain_KeepsItsNumber()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);

        var again = player.HandOver(at: 40, name: 100, into: 22);

        Assert.Equal(StudioPreviewFrameKnowledge.Known, again.Named.Knowledge);
        Assert.Equal(100, again.Named.Frame);
        Assert.Equal("again", again.Named.Rule);

        // A player does not do that as a rule: should the clock stop here, the frame is fetched anew.
        Assert.True(again.Named.Inferred);
        Assert.Equal(101, player.HandOver(at: 50, name: 101).Named.Frame);
    }

    [Fact]
    public void APositionBeforeTheFrameThatIsDue_MakesEverythingUnknown_UntilAnIdleLook()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);

        var back = player.HandOver(at: 50, name: 97);
        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, back.Named.Knowledge);
        Assert.Equal("out of order", back.Named.Rule);

        // Even a frame that names the next one is not believed now.
        var after = player.HandOver(at: 60, name: 98);
        Assert.Equal(StudioPreviewEarlierFrame.Nothing, after.Earlier);
        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, after.Named.Knowledge);

        // An idle look says that the frame before was the one its position named.
        var idle = player.HandOver(at: 90, name: 99);
        Assert.Equal(StudioPreviewEarlierFrame.Known, idle.Earlier);
        Assert.Equal(98, idle.EarlierFrame);
        Assert.Equal(StudioPreviewFrameKnowledge.Known, idle.Named.Knowledge);
        Assert.Equal(99, idle.Named.Frame);
    }

    [Fact]
    public void AfterForget_NothingIsKnown_UntilAnIdleLook()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 20, name: 100);
        player.Namer.Forget();

        var first = player.HandOver(at: 50, name: 101);
        Assert.Equal(StudioPreviewEarlierFrame.Nothing, first.Earlier);
        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, first.Named.Knowledge);

        var second = player.HandOver(at: 90, name: 102);
        Assert.Equal(StudioPreviewEarlierFrame.Known, second.Earlier);
        Assert.Equal(101, second.EarlierFrame);
        Assert.Equal(102, second.Named.Frame);
    }

    [Fact]
    public void WhenTheRestingFrameIsNotKnown_TheFirstFrameHasNoNumber()
    {
        var player = new Player();
        player.Start(restingFrame: -2, at: 0);

        var first = player.HandOver(at: 20, name: 100);
        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, first.Named.Knowledge);

        var second = player.HandOver(at: 50, name: 101);
        Assert.Equal(StudioPreviewEarlierFrame.Known, second.Earlier);
        Assert.Equal(100, second.EarlierFrame);
        Assert.Equal(StudioPreviewFrameKnowledge.Known, second.Named.Knowledge);
    }

    [Fact]
    public void TheFirstFrameAfterTheStart_IsNotJudgedByTheTimeSinceTheStart()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);

        // Long after the start, on time and with nothing in the way: but the start was no hand-over.
        var first = player.HandOver(at: 200, name: 102);

        Assert.Equal(StudioPreviewEarlierFrame.Nothing, first.Earlier);
        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, first.Named.Knowledge);
    }

    [Fact]
    public void FramesWithoutANumberForHalfASecond_AreShownUnsure_UntilThirtyInARowHaveOne()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);

        // A PC that cannot keep up: every frame late in its own time, and two frames on from the last.
        long at = 25;
        long name = 101;
        var unsureFrom = -1L;
        for (; at < 700; at += 33, name += 2)
        {
            var handOver = player.HandOver(at, name, into: 25);
            if (handOver.Named.Knowledge == StudioPreviewFrameKnowledge.Unsure)
            {
                unsureFrom = unsureFrom < 0 ? at : unsureFrom;
                Assert.Equal(name, handOver.Named.Frame);
                Assert.Equal("unsure, by position", handOver.Named.Rule);
            }
            else
            {
                Assert.Equal(StudioPreviewFrameKnowledge.Unknown, handOver.Named.Knowledge);
                Assert.True(unsureFrom < 0, "a frame without a number after frames were shown unsure");
            }
        }

        // The first hand-over that ends half a second after the first frame without a number.
        Assert.InRange(unsureFrom, 500, 560);
        Assert.True(player.Namer.ShowsUnsure);

        // It keeps up again: an idle look, and then one frame after the other.
        at = 800;
        Assert.Equal(StudioPreviewFrameKnowledge.Unsure, player.HandOver(at, name).Named.Knowledge);
        for (var count = 1; count <= 30; count++)
        {
            at += count % 3 == 0 ? 40 : 30;
            name++;
            Assert.True(player.Namer.ShowsUnsure);
            Assert.Equal(StudioPreviewFrameKnowledge.Known, player.HandOver(at, name).Named.Knowledge);
        }

        Assert.False(player.Namer.ShowsUnsure);

        // The next doubt is waited out again.
        var doubt = player.HandOver(at + 60, name + 3, into: 25);
        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, doubt.Named.Knowledge);
    }

    [Fact]
    public void ShownUnsure_IsNotForgottenWhenTheClockStartsAgain()
    {
        var player = new Player();
        player.Start(restingFrame: 99, at: 0);
        long name = 101;
        for (long at = 25; at < 700; at += 33, name += 2)
        {
            player.HandOver(at, name, into: 25);
        }

        Assert.True(player.Namer.ShowsUnsure);
        player.Start(restingFrame: 200, at: 5000);

        Assert.True(player.Namer.ShowsUnsure);
        Assert.Equal(StudioPreviewFrameKnowledge.Known, player.HandOver(at: 5020, name: 201).Named.Knowledge);

        // No half second without a picture this time.
        var late = player.HandOver(at: 5090, name: 204, into: 25);
        Assert.Equal(StudioPreviewFrameKnowledge.Unsure, late.Named.Knowledge);
        Assert.Equal(204, late.Named.Frame);
    }

    [Fact]
    public void BelievePositions_TakesEveryFrameForTheOneItsPositionNames()
    {
        var player = new Player(Settings with { BelievePositions = true });
        player.Start(restingFrame: 99, at: 0);

        var late = player.HandOver(at: 90, name: 102, into: 20);
        Assert.Equal(StudioPreviewEarlierFrame.Nothing, late.Earlier);
        Assert.Equal(StudioPreviewFrameKnowledge.Known, late.Named.Knowledge);
        Assert.Equal(102, late.Named.Frame);

        var slow = player.HandOver(at: 120, name: 103, copy: 40);
        Assert.Equal(StudioPreviewFrameKnowledge.Known, slow.Named.Knowledge);
        Assert.Equal(103, slow.Named.Frame);
    }

    [Fact]
    public void AtSixtyFramesASecond_TheCollectorRuleAllowsForAShorterFrame()
    {
        var player = new Player(frameRate: 60);
        player.Start(restingFrame: 99, at: 0);
        player.HandOver(at: 10, name: 100);
        player.Collect(60);

        // 77 ms after the frame before: a sixtieth of a second, 60 ms and the margin are 81.7 ms.
        Assert.Equal(101, player.HandOver(at: 90, name: 104, into: 5).Named.Frame);

        var other = new Player(frameRate: 60);
        other.Start(restingFrame: 99, at: 0);
        other.HandOver(at: 10, name: 100);
        other.Collect(60);

        // 87 ms after: more than that.
        Assert.Equal(StudioPreviewFrameKnowledge.Unknown, other.HandOver(at: 100, name: 105, into: 5).Named.Knowledge);
    }

    private readonly record struct HandedOver(StudioPreviewEarlierFrame Earlier, long EarlierFrame, StudioPreviewNamedFrame Named);

    /// <summary>A player as the namer sees it: when a hand-over begins, what its position names, how long the copy takes.</summary>
    private sealed class Player(StudioPreviewNamingSettings? settings = null, double frameRate = 30)
    {
        private double _collector;

        public StudioPreviewFrameNamer Namer { get; } = new(frameRate, settings ?? Settings);

        public void Start(long restingFrame, long at) => Namer.Start(restingFrame, at, _collector);

        /// <summary>The garbage collector holds the process up for so long, before the next hand-over begins.</summary>
        public void Collect(double milliseconds) => _collector += milliseconds;

        /// <param name="at">When the hand-over begins.</param>
        /// <param name="name">The frame that contains the player's position then.</param>
        /// <param name="into">How far into that frame the position is, in milliseconds.</param>
        /// <param name="copy">How long the copy takes.</param>
        /// <param name="nameAfter">The frame that contains the position after the copy; the same frame when null.</param>
        /// <param name="collectorMeanwhile">For how long of the copy's time the garbage collector held the process up.</param>
        /// <param name="tail">How long after the copy the player gets its thread back.</param>
        /// <param name="nameAtExit">The frame that contains the position then; the one after the copy when null.</param>
        /// <param name="collectorInTail">For how long of that time the garbage collector held the process up.</param>
        public HandedOver HandOver(
            long at,
            long name,
            double into = 2,
            long copy = 3,
            long? nameAfter = null,
            double collectorMeanwhile = 0,
            long tail = 0,
            long? nameAtExit = null,
            double collectorInTail = 0)
        {
            var earlier = Namer.Begin(at, name, into, _collector, out var earlierFrame);
            _collector += collectorMeanwhile;
            var named = Namer.End(at + copy, nameAfter ?? name, copy, _collector);
            _collector += collectorInTail;
            Namer.Exit(at + copy + tail, nameAtExit ?? nameAfter ?? name, _collector);
            return new HandedOver(earlier, earlierFrame, named);
        }
    }
}
