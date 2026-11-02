using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioWindowPointerTests
{
    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, true)]
    public void StudioCollectsWindowClicksEvenWithVisualsOff_OrdinaryOverlayPolicyIsUnchanged(
        bool isWindow, bool isStudio, bool showVisuals, bool expected)
    {
        var target = isWindow ? CaptureTarget.Window(123) : CaptureTarget.Monitor(456);
        Assert.Equal(expected, StudioRecordingBuilder.ShouldMonitorClicks(target, isStudio, showVisuals));
    }

    [Fact]
    public void WindowEventsUseEventTimeGeometry_NotInitialOrFinalOrigin()
    {
        var bounds = new PixelRect(-900, -200, 800, 600);
        var frame = Frame(800, 600);
        var pointer = new StudioWindowPointer(() => bounds, () => frame);
        var timeline = RecordingTimeline.FromOrigin(TimeSpan.FromSeconds(10));
        var firstClick = pointer.CaptureClick(Click(-700, -50, 11, MouseClickButton.Right));
        var firstCursor = pointer.CaptureCursor(TimeSpan.FromSeconds(1), -700, -50);
        Assert.NotNull(firstClick);

        // Same physical pointer, moved window. Its relative position must change even though
        // GetCursorPos has not changed. No monitor DPI scale belongs in this calculation.
        bounds = new PixelRect(-800, -100, 800, 600);
        var movedClick = pointer.CaptureClick(Click(-700, -50, 12, MouseClickButton.Middle));
        var movedCursor = pointer.CaptureCursor(TimeSpan.FromSeconds(2), -700, -50);
        Assert.NotNull(movedClick);
        Assert.NotEqual(firstCursor!.Value.CapturePoint, movedCursor!.Value.CapturePoint);

        // Resize to a square; pillarboxes occupy 100 pixels on either side of the encoder.
        bounds = new PixelRect(100, 50, 400, 400);
        frame = Frame(400, 400);
        var resizedClick = pointer.CaptureClick(Click(200, 150, 13));
        var resizedCursor = pointer.CaptureCursor(TimeSpan.FromSeconds(3), 200, 150);
        Assert.NotNull(resizedClick);
        // A later move must not retroactively alter saved samples.
        bounds = new PixelRect(3000, 1000, 400, 400);

        var clicks = StudioRecordingBuilder.BuildClickEvents(
            [firstClick.Value, movedClick.Value, resizedClick.Value], timeline, 999, 999, 800, 600);
        var cursors = StudioRecordingBuilder.BuildCursorSamples(
            [firstCursor.Value, movedCursor.Value, resizedCursor!.Value], 999, 999, 800, 600);
        Assert.Equal(3, clicks.Length);
        Assert.Equal(3, cursors.Length);
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, clicks.Select(c => c.T));
        AssertPoint(0.25, 0.25, (clicks[0].X, clicks[0].Y));
        AssertPoint(0.125, 1.0 / 12, (clicks[1].X, clicks[1].Y));
        AssertPoint(0.3125, 0.25, (clicks[2].X, clicks[2].Y));
        Assert.Equal(StudioMouseButton.Right, clicks[0].Button);
        Assert.Equal(StudioMouseButton.Middle, clicks[1].Button);
        for (var i = 0; i < clicks.Length; i++)
        {
            AssertPoint(clicks[i].X, clicks[i].Y, (cursors[i].X, cursors[i].Y));
        }

        Assert.Equal(StudioCaptureKind.Window, StudioRecordingBuilder.ToCaptureKind(CaptureTarget.Window(123), null));
    }

    [Theory]
    [InlineData(800, 400, true, 0, 100, 800, 400)]
    [InlineData(400, 600, true, 200, 0, 400, 600)]
    [InlineData(1600, 1200, true, 0, 0, 800, 600)]
    [InlineData(400, 300, true, 0, 0, 800, 600)]
    [InlineData(801, 601, true, 0, 0, 800, 600)]
    [InlineData(1600, 1200, false, 0, 0, 800, 600)]
    public void WindowFrameMatchesLetterboxing_EvenCrop_AndGpuBlitFallback(
        int width, int height, bool letterbox, double left, double top, double drawWidth, double drawHeight)
    {
        var frame = Frame(width, height, letterbox: letterbox);
        Assert.Equal(left, frame.Left);
        Assert.Equal(top, frame.Top);
        Assert.Equal(drawWidth, frame.DrawWidth);
        Assert.Equal(drawHeight, frame.DrawHeight);
        var bounds = new PixelRect(-2000, -500, width, height);
        Assert.True(frame.TryMap(bounds, -2000, -500, out var first));
        AssertPoint(left / 800, top / 600, first);
        // The odd trailing row/column and a failed GPU scaling blit are crops, not resizes.
        if (!frame.IsScaled && width > 800)
        {
            Assert.False(frame.TryMap(bounds, bounds.X + 800, bounds.Y, out _));
        }
        else
        {
            Assert.True(frame.TryMap(bounds, bounds.X + width / 2, bounds.Y + height / 2, out var middle));
            AssertPoint(0.5, 0.5, middle);
        }
    }

    [Theory]
    [InlineData(true, 0, 1, 8, 3)]
    [InlineData(false, 0, 1.4, 8, 3.2)]
    public void CpuIntegerAndGpuFractionalLetterboxOffsetsArePreserved(
        bool isCpu, double left, double top, double drawWidth, double drawHeight)
    {
        var frame = WindowCaptureFrame.Create(5, 2, 5, 2, 8, 6, letterbox: true, roundToPixels: isCpu);
        Assert.Equal(left, frame.Left, 5);
        Assert.Equal(top, frame.Top, 5);
        Assert.Equal(drawWidth, frame.DrawWidth, 5);
        Assert.Equal(drawHeight, frame.DrawHeight, 5);
        Assert.True(frame.TryMap(new PixelRect(10, 20, 5, 2), 10, 20, out var point));
        AssertPoint(left / 8, top / 6, point);
    }

    [Theory]
    [InlineData(4, 2)]
    [InlineData(2, 4)]
    [InlineData(12, 6)]
    [InlineData(6, 12)]
    [InlineData(5, 2)]
    public void CpuMappingDestinationMatchesActualReadbackPixels(int width, int height)
    {
        var source = new byte[width * height * 4];
        for (var i = 0; i < source.Length; i += 4)
        {
            source[i] = 100;
            source[i + 3] = 255;
        }

        var output = new CapturedFrame(new byte[8 * 6 * 4], 8, 6);
        CpuVideoFrameReadback.CopyTo(source, width * 4, width, height, width, height, output, null, letterbox: true);
        var frame = WindowCaptureFrame.Create(width, height, width, height, 8, 6, true, true);
        for (var y = 0; y < 6; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var inside = x >= frame.Left && x < frame.Left + frame.DrawWidth &&
                    y >= frame.Top && y < frame.Top + frame.DrawHeight;
                Assert.Equal(inside ? (byte)100 : (byte)0, output.BgraPixels[(y * 8 + x) * 4]);
            }
        }

        Assert.True(frame.TryMap(new PixelRect(-123, 45, width, height), -123, 45, out var point));
        AssertPoint(frame.Left / 8, frame.Top / 6, point);
    }

    [Fact]
    public void InitialUnavailableMinimizedDestroyedAndResizeMismatchDoNotInventPointerPositions()
    {
        PixelRect? bounds = new PixelRect(100, 50, 800, 600);
        WindowCaptureFrame? frame = null;
        var pointer = new StudioWindowPointer(() => bounds, () => frame);
        Assert.Null(pointer.CaptureClick(Click(200, 150, 11)));
        Assert.Null(pointer.CaptureCursor(TimeSpan.Zero, 200, 150));

        frame = Frame(800, 600);
        var visible = pointer.CaptureCursor(TimeSpan.FromSeconds(1), 200, 150);
        Assert.NotNull(pointer.CaptureClick(Click(200, 150, 11)));
        bounds = null; // Native reader returns null for minimized, destroyed, hidden or cloaked.
        var hidden = pointer.CaptureCursor(TimeSpan.FromSeconds(2), 200, 150);
        Assert.Null(pointer.CaptureClick(Click(200, 150, 12)));
        Assert.Null(hidden);
        bounds = new PixelRect(100, 50, 400, 400); // WGC has not caught up yet.
        Assert.Null(pointer.CaptureClick(Click(200, 150, 13)));
        Assert.Null(pointer.CaptureCursor(TimeSpan.FromSeconds(2.5), 200, 150));
        frame = Frame(400, 400);
        var restored = pointer.CaptureCursor(TimeSpan.FromSeconds(3), 200, 150);
        Assert.NotNull(pointer.CaptureClick(Click(200, 150, 13)));

        // There is no (-1,-1) sentinel: Studio clamps cursor samples into the frame for zoom
        // focus, so an unavailable sentinel would invent a top-left zoom target.
        var samples = StudioRecordingBuilder.BuildCursorSamples([visible!.Value, restored!.Value], 0, 0, 800, 600);
        Assert.Equal(2, samples.Length);
        Assert.All(samples, sample => Assert.True(sample.X > 0 && sample.Y > 0));
        var focus = StudioPreparedCursorSamples.For(new StudioEvents { Cursor = samples }).MeanInCenteredSecond(2);
        AssertPoint(samples[0].X, samples[0].Y, focus);
    }

    [Fact]
    public void VisibleFrameBoundsIncludeTitleBarButExcludeInvisibleBordersAndOutsidePointer()
    {
        var bounds = new PixelRect(-100, 50, 800, 600);
        var pointer = new StudioWindowPointer(() => bounds, () => Frame(800, 600));
        var titleBarClick = pointer.CaptureClick(Click(-100, 50, 11));
        Assert.NotNull(titleBarClick);
        AssertPoint(0, 0, titleBarClick.Value.CapturePoint!.Value);
        Assert.Null(pointer.CaptureClick(Click(-101, 100, 11)));
        Assert.Null(pointer.CaptureClick(Click(700, 100, 11)));
        Assert.Null(pointer.CaptureClick(Click(100, 650, 11)));
        Assert.Null(pointer.CaptureCursor(TimeSpan.Zero, -101, 100));
    }

    [Fact]
    public void GrowthOnOldPoolSurfaceMapsOnlyCapturedPixelsAndNotUncapturedArea()
    {
        var bounds = new PixelRect(-100, 50, 1600, 1200);
        var frame = WindowCaptureFrame.Create(1600, 1200, 800, 600, 800, 600, true, true);
        Assert.True(frame.TryMap(bounds, 300, 350, out var point));
        AssertPoint(0.5, 0.5, point);
        Assert.False(frame.TryMap(bounds, 700, 350, out _));
        Assert.False(frame.TryMap(bounds, 300, 650, out _));
    }

    [Fact]
    public void WindowClicksKeepSourceClock_ExcludePreparationAndPauses_AndSubtractOnlyEarlierPauses()
    {
        var timeline = RecordingTimeline.FromOrigin(TimeSpan.FromSeconds(10));
        timeline.Pause(TimeSpan.FromSeconds(12));
        timeline.Resume(TimeSpan.FromSeconds(15));
        timeline.Pause(TimeSpan.FromSeconds(18));
        timeline.Resume(TimeSpan.FromSeconds(19));
        var bounds = new PixelRect(-500, 100, 800, 600);
        var pointer = new StudioWindowPointer(() => bounds, () => Frame(800, 600));
        var clicks = new[] { 9.0, 11, 12, 14, 15, 16, 18.5, 20 }
            .Select(t => pointer.CaptureClick(Click(-300, 250, t))!.Value);
        var result = StudioRecordingBuilder.BuildClickEvents(clicks, timeline, 0, 0, 800, 600);
        Assert.Equal(new[] { 1.0, 2.0, 3.0, 6.0 }, result.Select(c => c.T));
        Assert.All(result, c => AssertPoint(0.25, 0.25, (c.X, c.Y)));
    }

    private static WindowCaptureFrame Frame(int width, int height, bool letterbox = true) =>
        WindowCaptureFrame.Create(width, height, width, height, 800, 600, letterbox, roundToPixels: true);

    private static MouseClickSample Click(int x, int y, double sourceSeconds, MouseClickButton button = MouseClickButton.Left) =>
        new(999, x, y, button, TimeSpan.FromSeconds(sourceSeconds));

    private static void AssertPoint(double x, double y, (double X, double Y) actual)
    {
        Assert.Equal(x, actual.X, 6);
        Assert.Equal(y, actual.Y, 6);
    }
}
