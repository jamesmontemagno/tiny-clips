using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Preview;

namespace TinyClips.Core.Tests;

public sealed class StudioPreviewTimelineTests
{
    private const double Ntsc = 30000.0 / 1001.0;

    [Theory]
    [InlineData(30)]
    [InlineData(Ntsc)]
    [InlineData(60)]
    public void FrameAt_FindsTheFrameThatContainsATime(double fps)
    {
        foreach (var frame in new long[] { 0, 1, 2, 29, 30, 599, 1000, 107_999 })
        {
            var start = StudioPreviewTimeMath.FrameStart(frame, fps);
            var middle = StudioPreviewTimeMath.FrameMiddle(frame, fps);
            var nearEnd = start + (0.98 / fps);

            Assert.Equal(frame, StudioPreviewTimeMath.FrameAt(start, fps));
            Assert.Equal(frame, StudioPreviewTimeMath.FrameAt(middle, fps));
            Assert.Equal(frame, StudioPreviewTimeMath.FrameAt(nearEnd, fps));
            Assert.Equal(frame - 1, StudioPreviewTimeMath.FrameAt(start - (0.02 / fps), fps));
        }
    }

    [Theory]
    [InlineData(30)]
    [InlineData(29.97)]
    [InlineData(Ntsc)]
    [InlineData(60)]
    public void FrameForSeek_StepsExactlyOneFrameFromAReportedPosition(double fps)
    {
        var timeline = Timeline(duration: 600, fps);
        var frameDuration = 1 / fps;

        for (long frame = 1; frame < timeline.LastFrame; frame += 7)
        {
            var position = timeline.FrameStart(frame);

            Assert.Equal(frame, timeline.FrameForSeek(position));
            Assert.Equal(frame + 1, timeline.FrameForSeek(position + frameDuration));
            Assert.Equal(frame - 1, timeline.FrameForSeek(position - frameDuration));
        }
    }

    [Theory]
    [InlineData(30)]
    [InlineData(Ntsc)]
    [InlineData(60)]
    public void NameAtPlayerTicks_IsTheFrameOfAPosition_AndHowFarIntoIt(double fps)
    {
        var clip = new StudioPreviewClipTiming(fps, 360, 0);
        foreach (var frame in new long[] { 0, 1, 29, 150, 359 })
        {
            // A position as a player reports it: in ticks of a ten-millionth of a second.
            var start = StudioPreviewTimeMath.ToTicks(StudioPreviewTimeMath.FrameStart(frame, fps));
            var late = start + 123_000;

            // A position on a frame's first instant counts as inside it, by a thousandth of a frame.
            Assert.Equal(frame, clip.NameAtPlayerTicks(start, out var atTheStart));
            Assert.InRange(atTheStart, 0, 0.05);
            Assert.Equal(frame, clip.NameAtPlayerTicks(late, out var into));
            Assert.InRange(into, 12.3, 12.35);
            Assert.Equal(frame, clip.NameAtPlayerTicks(StudioPreviewTimeMath.ToTicks(StudioPreviewTimeMath.FrameMiddle(frame, fps)), out var half));
            Assert.InRange(half, 500 / fps, (500 / fps) + 0.05);
            Assert.Equal(frame, clip.FrameAtPlayerTicks(late));
        }
    }

    [Fact]
    public void NameAtPlayerTicks_IsNotClampedToTheClip_AndClampFrameIs()
    {
        var clip = new StudioPreviewClipTiming(30, 360, 0);
        var afterTheEnd = StudioPreviewTimeMath.ToTicks(StudioPreviewTimeMath.FrameMiddle(363, 30));

        // Where one frame more or less matters, the last frame is not to stand for those after it.
        Assert.Equal(363, clip.NameAtPlayerTicks(afterTheEnd, out _));
        Assert.Equal(359, clip.FrameAtPlayerTicks(afterTheEnd));
        Assert.Equal(359, clip.ClampFrame(363));
        Assert.Equal(0, clip.ClampFrame(-4));
        Assert.Equal(150, clip.ClampFrame(150));
    }

    [Fact]
    public void FrameForSeek_ClampsAtBothEnds()
    {
        var timeline = Timeline(duration: 12, fps: 30);

        Assert.Equal(360, timeline.FrameCount);
        Assert.Equal(0, timeline.FrameForSeek(-5));
        Assert.Equal(0, timeline.FrameForSeek(0));
        Assert.Equal(359, timeline.FrameForSeek(12));
        Assert.Equal(359, timeline.FrameForSeek(1e9));
        Assert.Equal(359, timeline.FrameForSeek(double.PositiveInfinity));
        Assert.Equal(0, timeline.FrameForSeek(double.NegativeInfinity));
        Assert.Equal(0, timeline.FrameForSeek(double.NaN));
    }

    [Theory]
    [InlineData(12.0, 30, 360)]
    [InlineData(12.016, 30, 360)]
    [InlineData(11.99, 30, 359)]
    [InlineData(10.0, Ntsc, 299)]
    [InlineData(10.0, 60, 600)]
    [InlineData(0.0, 30, 1)]
    [InlineData(0.01, 30, 1)]
    [InlineData(double.NaN, 30, 1)]
    public void FrameCount_FollowsTheExportersRule(double duration, double fps, long expected)
    {
        Assert.Equal(expected, StudioPreviewTimeMath.FrameCount(duration, fps));
    }

    [Fact]
    public void MiddleTicks_IsTheMiddleOfTheFrameInHundredNanoseconds()
    {
        var thirty = Timeline(30, 30);
        var ntsc = Timeline(30, Ntsc);
        var sixty = Timeline(30, 60);

        Assert.Equal(166_667, thirty.MiddleTicks(0));
        Assert.Equal(1_500_000, thirty.MiddleTicks(4));
        Assert.Equal(166_833, ntsc.MiddleTicks(0));
        Assert.Equal(83_333, sixty.MiddleTicks(0));
        Assert.Equal(0.5 / 30, thirty.FrameMiddle(0), 12);
        Assert.Equal(100 / Ntsc, ntsc.FrameStart(100), 12);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(Ntsc)]
    [InlineData(60)]
    public void PlayerPosition_InTheMiddleOfAFrame_NamesThatFrame(double fps)
    {
        var timeline = Timeline(120, fps);
        var screen = timeline.Track(0);

        for (long frame = 0; frame <= timeline.LastFrame; frame += 13)
        {
            Assert.Equal(frame, screen.FrameAtPlayerTicks(timeline.MiddleTicks(frame)));
        }
    }

    [Fact]
    public void PlayerPosition_OnAFrameBoundary_NamesTheFrameThatStartsThere()
    {
        var screen = new StudioPreviewClipTiming(30, 900, 0);

        // Frame 1 starts at 333333.33 ticks; a file stores 333333 and a step reports that.
        Assert.Equal(1, screen.FrameAtPlayerTicks(333_333));
        Assert.Equal(500, screen.FrameAtPlayerTicks(166_666_666));
        Assert.Equal(0, screen.FrameAtPlayerTicks(0));
        Assert.Equal(899, screen.FrameAtPlayerTicks(TimeSpan.FromSeconds(30).Ticks));
        Assert.Equal(899, screen.FrameAtPlayerTicks(TimeSpan.FromSeconds(45).Ticks));
    }

    [Fact]
    public void CameraThatStartedLate_IsParkedOnItsFirstFrameUntilItStarts()
    {
        var timeline = Timeline(30, 30, new StudioPreviewClipTiming(30, 900, 0.2));

        for (long frame = 0; frame <= 6; frame++)
        {
            Assert.Equal(0, timeline.PlayerFrame(1, frame));
        }

        Assert.Equal(1, timeline.PlayerFrame(1, 7));
        Assert.Equal(144, timeline.PlayerFrame(1, 150));
        Assert.Equal(150, timeline.PlayerFrame(0, 150));
    }

    [Fact]
    public void CameraThatStartedEarly_IsAheadOfTheScreen()
    {
        var timeline = Timeline(12, 30, new StudioPreviewClipTiming(30, 360, -0.1));

        Assert.Equal(3, timeline.PlayerFrame(1, 0));
        Assert.Equal(103, timeline.PlayerFrame(1, 100));
        Assert.Equal(359, timeline.PlayerFrame(1, 356));
        Assert.Equal(359, timeline.PlayerFrame(1, 359));
    }

    [Fact]
    public void CameraShorterThanTheScreen_IsParkedOnItsLastFrameAfterItEnds()
    {
        var timeline = Timeline(12, 30, new StudioPreviewClipTiming(30, 180, 2.0));

        Assert.Equal(0, timeline.PlayerFrame(1, 0));
        Assert.Equal(0, timeline.PlayerFrame(1, 60));
        Assert.Equal(1, timeline.PlayerFrame(1, 61));
        Assert.Equal(179, timeline.PlayerFrame(1, 239));
        Assert.Equal(179, timeline.PlayerFrame(1, 240));
        Assert.Equal(179, timeline.PlayerFrame(1, 359));
    }

    [Fact]
    public void CameraWithAnotherFrameRate_UsesItsOwnGrid()
    {
        var timeline = Timeline(12, 30, new StudioPreviewClipTiming(15, 180, 0));

        Assert.Equal(0, timeline.PlayerFrame(1, 0));
        Assert.Equal(0, timeline.PlayerFrame(1, 1));
        Assert.Equal(1, timeline.PlayerFrame(1, 2));
        Assert.Equal(50, timeline.PlayerFrame(1, 100));
    }

    [Fact]
    public void Timeline_FallsBackToThirtyFramesPerSecond_WhenTheRateIsUnusable()
    {
        Assert.Equal(30, Timeline(10, 0).FrameRate);
        Assert.Equal(30, Timeline(10, double.NaN).FrameRate);
        Assert.Equal(30, Timeline(10, -24).FrameRate);
        Assert.Equal(1, Timeline(double.NaN, 30).FrameCount);
    }

    [Fact]
    public void IsShown_TheScreenIsInEveryFrame()
    {
        var timeline = Timeline(12, 30, new StudioPreviewClipTiming(30, 180, 2.0, 6));

        Assert.True(timeline.IsShown(0, 0));
        Assert.True(timeline.IsShown(0, 359));
    }

    [Fact]
    public void IsShown_ACameraIsInTheFramesWhoseMiddleLiesInsideItsRange()
    {
        // Started 0.2 s late: the middle of frame 5 is 0.183 s, of frame 6 0.217 s.
        var late = Timeline(12, 30, new StudioPreviewClipTiming(30, 360, 0.2, 12));
        Assert.False(late.IsShown(1, 0));
        Assert.False(late.IsShown(1, 5));
        Assert.True(late.IsShown(1, 6));
        Assert.True(late.IsShown(1, 359));

        // Started 0.1 s early and as long as the screen: it runs out 0.1 s before the end.
        var early = Timeline(12, 30, new StudioPreviewClipTiming(30, 360, -0.1, 12));
        Assert.True(early.IsShown(1, 0));
        Assert.True(early.IsShown(1, 356));
        Assert.False(early.IsShown(1, 357));
        Assert.False(early.IsShown(1, 359));

        // Six seconds long, from 2 s to 8 s.
        var middle = Timeline(12, 30, new StudioPreviewClipTiming(30, 180, 2.0, 6));
        Assert.False(middle.IsShown(1, 59));
        Assert.True(middle.IsShown(1, 60));
        Assert.True(middle.IsShown(1, 239));
        Assert.False(middle.IsShown(1, 240));
    }

    [Theory]
    [InlineData(0.2, 12, 30)]
    [InlineData(-0.1, 12, 30)]
    [InlineData(2.0, 6, 30)]
    [InlineData(0.02, 11.97, 29.97)]
    [InlineData(-0.013, 3.5, 60)]
    [InlineData(1.0 / 3.0, 7.77, 60)]
    public void IsShown_AgreesWithTheLayout_OnEveryFrame(double startOffset, double cameraDuration, double fps)
    {
        // The seek policy does not wait for a camera the picture does not contain. If it and the
        // layout ever disagreed about a frame, that frame would be drawn with a stale camera picture.
        var project = new StudioProject
        {
            Sources = new StudioSources
            {
                Screen = new StudioScreenSource { Width = 1920, Height = 1080, FrameRate = fps, Duration = 12 },
                Camera = new StudioCameraSource { Width = 1280, Height = 720, Duration = cameraDuration, StartOffset = startOffset },
            },
        };
        var timeline = Timeline(12, fps, new StudioPreviewClipTiming(30, StudioPreviewTimeMath.FrameCount(cameraDuration, 30), startOffset, cameraDuration));
        var plan = StudioLayoutPlan.Create(project);

        for (long frame = 0; frame < timeline.FrameCount; frame++)
        {
            var resolved = plan.Resolve(timeline.FrameMiddle(frame), 1280, 720);
            Assert.True(resolved.Camera.HasValue);
            Assert.True(resolved.Camera.Value.Visible == timeline.IsShown(1, frame), $"frame {frame}");
        }
    }

    [Fact]
    public void IsPlayerInside_IsFalse_WhereThePlayerIsParkedBeforeOrAfterItsStream()
    {
        // Six seconds long, from 2 s to 8 s.
        var timeline = Timeline(12, 30, new StudioPreviewClipTiming(30, 180, 2.0, 6));

        Assert.True(timeline.IsPlayerInside(0, 0));
        Assert.True(timeline.IsPlayerInside(0, 11.99));
        Assert.False(timeline.IsPlayerInside(1, 0));
        Assert.False(timeline.IsPlayerInside(1, 1.999));
        Assert.True(timeline.IsPlayerInside(1, 2.0));
        Assert.True(timeline.IsPlayerInside(1, 7.999));
        Assert.False(timeline.IsPlayerInside(1, 8.0));
        Assert.False(timeline.IsPlayerInside(1, 11));
    }

    [Fact]
    public void IsPlayerAtEnd_IsTrue_OnTheLastFrameOfTheStreamAndAfterIt()
    {
        // Six seconds long, from 2 s to 8 s: its last frame, 179, starts at 7.9667 s.
        var timeline = Timeline(12, 30, new StudioPreviewClipTiming(30, 180, 2.0, 6));

        Assert.False(timeline.IsPlayerAtEnd(1, 0));
        Assert.False(timeline.IsPlayerAtEnd(1, timeline.FrameMiddle(238)));
        Assert.True(timeline.IsPlayerAtEnd(1, timeline.FrameMiddle(239)));
        Assert.True(timeline.IsPlayerAtEnd(1, timeline.FrameMiddle(240)));
        Assert.True(timeline.IsPlayerAtEnd(1, 11));

        Assert.False(timeline.IsPlayerAtEnd(0, timeline.FrameMiddle(358)));
        Assert.True(timeline.IsPlayerAtEnd(0, timeline.FrameMiddle(359)));
    }

    [Fact]
    public void ProofFrames_AreTheFirstFrameThatShowsEveryClip_AndOneEightFramesOn()
    {
        // The camera starts 0.2 s late: it is in the picture from frame 6, on its own frame 0.
        var late = Timeline(12, 30, new StudioPreviewClipTiming(30, 360, 0.2, 12));
        Assert.True(late.TryPickProofFrames(avoid: 0, out var first, out var second));
        Assert.Equal((6, 14), (first, second));
        Assert.True(late.IsShown(1, first) && late.IsShown(1, second));
        Assert.Equal((0, 8), (late.PlayerFrame(1, first), late.PlayerFrame(1, second)));

        // Both clips from the start, and the screen alone: any frame but the one to avoid.
        Assert.True(Timeline(12, 30, new StudioPreviewClipTiming(30, 360, 0, 12)).TryPickProofFrames(0, out first, out second));
        Assert.Equal((1, 9), (first, second));
        Assert.True(Timeline(12, 30).TryPickProofFrames(0, out first, out second));
        Assert.Equal((1, 9), (first, second));
    }

    [Fact]
    public void ProofFrames_LeaveOutTheFrameThePlayersAreWantedOnAfterwards()
    {
        var late = Timeline(12, 30, new StudioPreviewClipTiming(30, 360, 0.2, 12));

        Assert.True(late.TryPickProofFrames(avoid: 6, out var first, out var second));
        Assert.Equal((7, 15), (first, second));

        // It may be the second of the two: the players come back from there to the first.
        Assert.True(Timeline(12, 30).TryPickProofFrames(avoid: 8, out first, out second));
        Assert.Equal((0, 8), (first, second));
    }

    [Fact]
    public void ProofFrames_MoveEveryPlayer_AlsoOneWithFewFramesASecond()
    {
        // Two camera frames a second: fifteen screen frames show the same one.
        var slow = Timeline(12, 30, new StudioPreviewClipTiming(2, 24, 0, 12));

        Assert.True(slow.TryPickProofFrames(avoid: 0, out var first, out var second));

        Assert.Equal((1, 15), (first, second));
        Assert.NotEqual(slow.PlayerFrame(1, first), slow.PlayerFrame(1, second));
    }

    [Fact]
    public void ProofFrames_KeepOffTheEndOfAStream_WhereAPlayerDoesNotSeekProperly()
    {
        // Three camera frames, from 0.2 s: in the picture on frames 6, 7 and 8, at its end on 8.
        var brief = Timeline(12, 30, new StudioPreviewClipTiming(30, 3, 0.2, 0.1));

        Assert.True(brief.TryPickProofFrames(avoid: 0, out var first, out var second));

        Assert.Equal((6, 7), (first, second));
    }

    [Fact]
    public void ProofFrames_FallBackToTheScreen_WhenTheCameraIsNeverInThePicture()
    {
        var never = Timeline(12, 30, new StudioPreviewClipTiming(30, 360, 20, 12));

        Assert.True(never.TryPickProofFrames(avoid: 0, out var first, out var second));

        Assert.Equal((1, 9), (first, second));
    }

    [Fact]
    public void ProofFrames_OfARecordingOfTwoFrames_AreThoseTwo_AndOneFrameHasNone()
    {
        Assert.True(Timeline(2 / 30.0, 30).TryPickProofFrames(avoid: 0, out var first, out var second));
        Assert.Equal((1, 0), (first, second));

        Assert.False(Timeline(1 / 30.0, 30).TryPickProofFrames(avoid: 0, out first, out second));
        Assert.Equal((-1, -1), (first, second));
        Assert.False(Timeline(1 / 30.0, 30).TryPickProofFrames(avoid: -1, out first, out second));
        Assert.Equal((-1, -1), (first, second));
    }

    private static StudioPreviewTimeline Timeline(double duration, double fps, StudioPreviewClipTiming? camera = null)
    {
        var rate = StudioPreviewTimeMath.NormalizeFrameRate(fps);
        var tracks = new List<StudioPreviewClipTiming> { new(rate, StudioPreviewTimeMath.FrameCount(duration, rate), 0) };
        if (camera is { } timing)
        {
            tracks.Add(timing);
        }

        return new StudioPreviewTimeline(duration, fps, tracks);
    }
}

public sealed class StudioPreviewCopyTargetsTests
{
    private static readonly StudioFrameRect Whole = new(0, 0, 1, 1);

    [Fact]
    public void NeededScale_IsShownPixelsPerSourcePixel()
    {
        Assert.Equal(0.5, StudioPreviewCopyTargets.NeededScale(1920, 1080, new StudioFrameRect(10, 10, 960, 540), Whole), 9);
        Assert.Equal(0.25, StudioPreviewCopyTargets.NeededScale(2560, 1440, new StudioFrameRect(0, 0, 640, 360), Whole), 9);
    }

    [Fact]
    public void NeededScale_CountsOnlyThePartOfTheSourceThatIsShown()
    {
        // A camera cropped to its middle square and shown in a 240 pixel circle.
        var crop = new StudioFrameRect(0.21875, 0, 0.5625, 1);

        Assert.Equal(240.0 / 720, StudioPreviewCopyTargets.NeededScale(1280, 720, new StudioFrameRect(0, 0, 240, 240), crop), 9);
    }

    [Fact]
    public void NeededScale_IsNeverAboveOne_AndZeroWhenNothingIsShown()
    {
        Assert.Equal(1, StudioPreviewCopyTargets.NeededScale(1280, 720, new StudioFrameRect(0, 0, 3840, 2160), Whole));
        Assert.Equal(0, StudioPreviewCopyTargets.NeededScale(1280, 720, default, Whole));
        Assert.Equal(0, StudioPreviewCopyTargets.NeededScale(0, 0, new StudioFrameRect(0, 0, 100, 100), Whole));
        Assert.Equal(0, StudioPreviewCopyTargets.NeededScale(1280, 720, new StudioFrameRect(0, 0, 100, 100), default));
    }

    [Theory]
    [InlineData(1.0, 1920, 1080)]
    [InlineData(0.9, 1920, 1080)]
    [InlineData(0.7, 1358, 764)]
    [InlineData(0.5, 960, 540)]
    [InlineData(0.36, 960, 540)]
    [InlineData(0.35, 680, 382)]
    [InlineData(0.25, 480, 270)]
    public void Choose_PicksTheSmallestLadderStepThatIsLargeEnough(double needed, int width, int height)
    {
        var size = StudioPreviewCopyTargets.Choose(1920, 1080, needed, current: null);

        Assert.Equal(width, size.Width);
        Assert.Equal(height, size.Height);
        Assert.True(size.Scale >= needed);
        Assert.True(size.Scale < needed * 1.4143);
    }

    [Fact]
    public void Choose_KeepsATargetThatIsLargeEnoughAndLessThanTwiceTooLarge()
    {
        var current = StudioPreviewCopyTargets.Choose(1920, 1080, 0.5, null);

        Assert.Equal(current, StudioPreviewCopyTargets.Choose(1920, 1080, 0.5, current));
        Assert.Equal(current, StudioPreviewCopyTargets.Choose(1920, 1080, 0.45, current));
        Assert.Equal(current, StudioPreviewCopyTargets.Choose(1920, 1080, 0.26, current));
    }

    [Fact]
    public void Choose_GrowsAsSoonAsTheTargetIsTooSmall_AndShrinksWhenItIsTwiceTooLarge()
    {
        var current = StudioPreviewCopyTargets.Choose(1920, 1080, 0.5, null);

        var grown = StudioPreviewCopyTargets.Choose(1920, 1080, 0.51, current);
        var shrunk = StudioPreviewCopyTargets.Choose(1920, 1080, 0.24, current);

        Assert.Equal(1358, grown.Width);
        Assert.Equal(480, shrunk.Width);
    }

    [Fact]
    public void Choose_NeverExceedsTheSource_AndNeverGoesBelowTheMinimum()
    {
        var odd = StudioPreviewCopyTargets.Choose(1279, 719, 1, null);
        var hidden = StudioPreviewCopyTargets.Choose(1920, 1080, 0, null);
        var tiny = StudioPreviewCopyTargets.Choose(40, 30, 0.1, null);
        var garbage = StudioPreviewCopyTargets.Choose(1920, 1080, double.NaN, null);

        Assert.Equal((1279, 719), (odd.Width, odd.Height));
        Assert.InRange(hidden.Width, StudioPreviewCopyTargets.MinimumLongSide, 2 * StudioPreviewCopyTargets.MinimumLongSide);
        Assert.True(hidden.Height >= 2);
        Assert.Equal((40, 30), (tiny.Width, tiny.Height));
        Assert.Equal((1920, 1080), (garbage.Width, garbage.Height));
    }

    [Fact]
    public void Choose_GivesEvenSizes()
    {
        for (var needed = 0.05; needed <= 1.0; needed += 0.013)
        {
            var size = StudioPreviewCopyTargets.Choose(1920, 1080, needed, null);

            Assert.Equal(0, size.Width % 2);
            Assert.Equal(0, size.Height % 2);
            Assert.True(size.Width <= 1920 && size.Height <= 1080);
            Assert.True(size.Width >= 1920 * Math.Min(needed, 1) - 1);
        }
    }

    [Fact]
    public void Choose_HiddenClipShrinksToTheMinimum()
    {
        var full = StudioPreviewCopyTargets.Choose(1280, 720, 1, null);

        var hidden = StudioPreviewCopyTargets.Choose(1280, 720, 0, full);

        Assert.True(hidden.Width < 200);
        Assert.Equal(hidden, StudioPreviewCopyTargets.Choose(1280, 720, 0, hidden));
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.99, 1.0)]
    [InlineData(0.7072, 1.0)]
    [InlineData(0.7071, 0.70710678)]
    [InlineData(0.70, 0.70710678)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.3, 0.35355339)]
    [InlineData(0.125, 0.125)]
    public void LadderScale_StepsByTheSquareRootOfTwo(double needed, double expected)
    {
        Assert.Equal(expected, StudioPreviewCopyTargets.LadderScale(needed), 6);
    }
}

public sealed class StudioPreviewAudioTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, true)]
    public void OnlyTheScreenPlayerOfAProjectThatIsNotMuted_PlaysSound(bool isCamera, bool projectMuted, bool forceMuted, bool expected)
    {
        Assert.Equal(expected, StudioPreviewAudio.IsMuted(isCamera, projectMuted, forceMuted));
    }
}
