using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Preview;

namespace TinyClips.Core.Tests;

/// <summary>
/// When a picture of which it is not known which frame it is may be drawn all the same: when the
/// scene is laid out the same way for every frame it could be.
/// </summary>
public sealed class StudioPreviewStillnessTests
{
    private const double Fps = 30;

    [Fact]
    public void APlainProject_IsLaidOutTheSameWayThroughout()
    {
        var project = Project();

        Assert.True(Same(project, 0, 5));
        Assert.True(Same(project, 100, 112));
        Assert.True(Same(project, 200, 200));
    }

    [Fact]
    public void AZoomThatMoves_IsNot_AndOneThatIsHeldIs()
    {
        // Moves in from 4 s to 7 s, is held until 9.5 s, and moves out until 10 s.
        var project = Project() with
        {
            Zooms = [new StudioZoom { Start = 4, End = 10, Scale = 2, Focus = new StudioZoomFocus { X = 0.3, Y = 0.28 }, EaseIn = 3, EaseOut = 0.5 }],
        };

        Assert.True(Same(project, 108, 119));
        Assert.False(Same(project, 150, 151));
        Assert.False(Same(project, 150, 155));
        Assert.False(Same(project, 208, 210));
        Assert.True(Same(project, 211, 220));
        Assert.False(Same(project, 284, 286));
        Assert.True(Same(project, 301, 310));
    }

    [Fact]
    public void ASceneBeingEntered_IsNot_WhetherByAMoveOrAtOnce()
    {
        var project = Project() with
        {
            Scenes =
            [
                new StudioScene { Layout = StudioLayout.Bubble },
                new StudioScene { Start = 4, Layout = StudioLayout.SideBySide, Transition = new StudioTransition { Kind = StudioTransitionKind.Morph, Duration = 0.35 } },
                new StudioScene { Start = 8, Layout = StudioLayout.Bubble, Transition = new StudioTransition { Kind = StudioTransitionKind.Cut } },
            ],
        };

        // The frames 120 to 129 have their middles inside the move.
        Assert.True(Same(project, 110, 119));
        Assert.False(Same(project, 119, 121));
        Assert.False(Same(project, 124, 125));
        Assert.True(Same(project, 131, 140));

        // Cut to at 8 s: frame 239 is the last of the one scene, frame 240 the first of the other.
        Assert.True(Same(project, 230, 239));
        Assert.False(Same(project, 239, 240));
        Assert.True(Same(project, 240, 250));
    }

    [Fact]
    public void TheFrameAtWhichTheCameraAppears_IsNot()
    {
        // The camera starts 0.2 s after the screen: frame 5 is the last without it.
        var project = Project(cameraOffset: 0.2);

        Assert.True(Same(project, 0, 5));
        Assert.False(Same(project, 5, 6));
        Assert.True(Same(project, 6, 18));
    }

    [Fact]
    public void AZoomThatFollowsThePointer_IsStill_OnlyWhileThePointerIs()
    {
        var project = Project() with
        {
            Zooms = [new StudioZoom { Start = 1, End = 10, Scale = 2, Focus = new StudioZoomFocus { Mode = StudioZoomFocusMode.Cursor }, EaseIn = 0.5, EaseOut = 0.5 }],
        };

        // The pointer rests until 5 s and then crosses the screen in a second.
        var samples = new List<StudioCursorSample>();
        for (var index = 0; index <= 360; index++)
        {
            var time = index / 30.0;
            var moved = Math.Clamp(time - 5, 0, 1);
            samples.Add(new StudioCursorSample { T = time, X = 0.3 + (0.4 * moved), Y = 0.3 + (0.3 * moved) });
        }

        var events = new StudioEvents { Capture = new StudioCaptureInfo { Width = 1920, Height = 1080 }, Cursor = [.. samples] };

        Assert.True(Same(project, 90, 100, events));
        Assert.False(Same(project, 165, 166, events));
        Assert.True(Same(project, 240, 250, events));

        // Without the pointer's samples there is nothing to follow.
        Assert.True(Same(project, 165, 166));
    }

    [Fact]
    public void AMoveOfLessThanAHundredthOfAPixel_IsNoMove_AndItIsMeasuredFromTheFirstFrame()
    {
        // Three seconds to move in by a hair: the part shown goes from the whole screen to
        // 0.9998 of it, a quarter of a pixel over the whole move.
        var project = Project() with
        {
            Zooms = [new StudioZoom { Start = 4, End = 10, Scale = 1.0002, EaseIn = 3, EaseOut = 0.5 }],
        };

        // No frame is as much as a hundredth of a pixel from the next, at the start of the move
        // or in the middle of it.
        Assert.True(Same(project, 120, 122));
        Assert.True(Same(project, 165, 166));

        // Twelve frames in the middle of it are, from the first to the last.
        Assert.False(Same(project, 160, 172));
    }

    [Fact]
    public void TheRingOfAClick_ChangesTheSceneForAsLongAsItShows()
    {
        // A click at 5 s; its ring shows for 0.45 s: in the frames 150 to 162, whose middles
        // are inside that time.
        var project = Project();
        var events = new StudioEvents { Clicks = [new StudioClickEvent { T = 5, X = 0.5, Y = 0.5 }] };

        Assert.True(Same(project, 140, 149, events));
        Assert.False(Same(project, 148, 150, events));
        Assert.False(Same(project, 155, 156, events));
        Assert.False(Same(project, 162, 165, events));
        Assert.True(Same(project, 164, 170, events));

        // With the rings switched off nothing is drawn for it.
        var without = project with { Overlays = new StudioOverlays { Clicks = new StudioClickOverlay { Enabled = false } } };
        Assert.True(Same(without, 148, 156, events));
    }

    [Fact]
    public void MoreFramesThanAPictureCanBeInDoubtOver_AreNotLookedAt()
    {
        var project = Project();

        Assert.True(Same(project, 100, 100 + StudioPreviewStillness.MostFrames));
        Assert.False(Same(project, 100, 101 + StudioPreviewStillness.MostFrames));
    }

    [Fact]
    public void AStretchThatIsNoStretch_IsNot()
    {
        var project = Project();

        Assert.False(Same(project, 101, 100));
        Assert.False(Same(project, -1, 3));
    }

    [Fact]
    public void TheSizeOfTheCanvasDoesNotMatter()
    {
        var project = Project() with
        {
            Zooms = [new StudioZoom { Start = 4, End = 10, Scale = 2, EaseIn = 3, EaseOut = 0.5 }],
        };

        foreach (var (width, height) in new[] { (1280.0, 720.0), (1707.0, 960.0), (333.0, 187.0) })
        {
            Assert.True(StudioPreviewStillness.SameLayout(project, null, Fps, 100, 112, width, height));
            Assert.False(StudioPreviewStillness.SameLayout(project, null, Fps, 150, 151, width, height));
        }
    }

    private static bool Same(StudioProject project, long first, long last, StudioEvents? events = null) =>
        StudioPreviewStillness.SameLayout(project, events, Fps, first, last, 1280, 720);

    private static StudioProject Project(double cameraOffset = 0) => new()
    {
        Sources = new StudioSources
        {
            Screen = new StudioScreenSource { Width = 1920, Height = 1080, FrameRate = Fps, Duration = 12 },
            Camera = new StudioCameraSource { Width = 1280, Height = 720, Duration = 12, StartOffset = cameraOffset },
        },
    };
}
