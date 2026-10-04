using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>The text the Studio editor shows, the keys it listens for, and where its canvas sits.</summary>
public sealed class StudioEditorSessionTextTests : StudioEditorSessionTestBase
{
    // Session text

    [Fact]
    public async Task TimeText_IsThePlayheadAndTheLengthInOutputTime()
    {
        var session = await OpenAsync(CreateProject());
        Assert.Equal("0:00.0 / 0:10.0", session.TimeText);

        session.Scrub(2.5);
        Assert.Equal("0:02.5 / 0:10.0", session.TimeText);

        session.SetTrimStart(2);
        session.SetTrimEnd(8);
        session.Scrub(4.5);
        Assert.Equal("0:02.5 / 0:06.0", session.TimeText);

        // Before the trim start is the first kept frame, and after the trim end is the last.
        session.Scrub(1);
        Assert.Equal("0:00.0 / 0:06.0", session.TimeText);
        session.Scrub(9);
        Assert.Equal("0:06.0 / 0:06.0", session.TimeText);
    }

    [Fact]
    public void TimeText_BeforeAProjectIsOpen_IsZero()
    {
        var session = CreateSession(CreateProject());

        Assert.Equal("0:00.0 / 0:00.0", session.TimeText);
        Assert.Equal("Exports at 1920 × 1080", session.ExportSizeText);
        Assert.Equal("Untitled recording", session.ClipName);
    }

    [Fact]
    public async Task ExportSize_FollowsTheCanvasShape_AndIsLimitedOnItsLongSide()
    {
        var session = await OpenAsync(CreateProject());
        Assert.Equal("Exports at 1920 × 1080", session.ExportSizeText);
        Assert.Equal(new StudioSize(1920, 1080), session.ExportSize);

        session.SetCanvasAspect(StudioCanvasAspect.Square);
        Assert.Equal("Exports at 1920 × 1920", session.ExportSizeText);

        session.SetCanvasAspect(StudioCanvasAspect.Portrait9X16);
        Assert.Equal(new StudioSize(1920, 3414), session.ExportSize);
        Assert.Equal(StudioExportLimits.GetExportSize(session.Project!), session.ExportSize);
    }

    [Theory]
    [InlineData("Quarterly demo", "Quarterly demo")]
    [InlineData("  Padded name \t", "Padded name")]
    [InlineData("", "Untitled recording")]
    [InlineData("   ", "Untitled recording")]
    [InlineData(null, "Untitled recording")]
    public void ClipName_FallsBackWhenThereIsNoName(string? name, string expected)
    {
        Assert.Equal(expected, StudioEditorText.GetClipName(name));
    }

    [Fact]
    public async Task ClipName_ComesFromTheProject()
    {
        var session = await OpenAsync(CreateProject(name: "  Standup recording  "));

        Assert.Equal("Standup recording", session.ClipName);
    }

    // Numbers

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(0.06, "6%")]
    [InlineData(0.4, "40%")]
    [InlineData(1, "100%")]
    [InlineData(0.125, "13%")]
    [InlineData(0.004, "0%")]
    [InlineData(-0.004, "0%")]
    [InlineData(-0.125, "-13%")]
    [InlineData(double.NaN, "0%")]
    public void PercentText_RoundsHalvesAwayFromZero(double fraction, string expected)
    {
        Assert.Equal(expected, StudioEditorText.GetPercentText(fraction));
    }

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(0.03, "+3%")]
    [InlineData(-0.03, "-3%")]
    [InlineData(0.004, "0%")]
    [InlineData(1, "+100%")]
    public void SignedPercentText_MarksValuesAboveZero(double fraction, string expected)
    {
        Assert.Equal(expected, StudioEditorText.GetSignedPercentText(fraction));
    }

    [Fact]
    public void SliderValueText_MatchesTheMacInspector()
    {
        // The default look, with each slider's scale: radius times 5, camera radius times 2, border times 50.
        Assert.Equal("6%", StudioEditorText.GetPercentText(0.06));
        Assert.Equal("10%", StudioEditorText.GetPercentText(0.02 * 5));
        Assert.Equal("50%", StudioEditorText.GetPercentText(0.5));
        Assert.Equal("24%", StudioEditorText.GetPercentText(0.12 * 2));
        Assert.Equal("24%", StudioEditorText.GetPercentText(0.24));
        Assert.Equal("30%", StudioEditorText.GetPercentText(0.3));
        Assert.Equal("0%", StudioEditorText.GetPercentText(0 * 50));
        Assert.Equal("100%", StudioEditorText.GetPercentText(0.02 * 50));
        Assert.Equal("35%", StudioEditorText.GetPercentText(0.35));
    }

    [Theory]
    [InlineData(0.214, 0.01, 0, 0.4, 0.21)]
    [InlineData(0.215, 0.01, 0, 0.4, 0.22)]
    [InlineData(0.9, 0.01, 0, 0.4, 0.4)]
    [InlineData(-0.2, 0.01, 0, 0.4, 0)]
    [InlineData(0.0123, 0.005, -1, 1, 0.01)]
    [InlineData(-0.0126, 0.005, -1, 1, -0.015)]
    [InlineData(0.33, 0, 0, 1, 0.33)]
    [InlineData(double.NaN, 0.01, 0.08, 0.6, 0.08)]
    public void SnapToStep_GivesTheNearestStepInsideTheRange(double value, double step, double minimum, double maximum, double expected)
    {
        Assert.Equal(expected, StudioEditorText.SnapToStep(value, step, minimum, maximum), 12);
    }

    [Theory]
    [InlineData(1.0 / 30, 10, 0.1)]
    [InlineData(1.0 / 30, 2, 1.0 / 30)]
    [InlineData(1.0 / 30, 600, 1)]
    [InlineData(1.0 / 60, 92.4, 0.924)]
    public void TrimStep_IsAHundredthOfTheRecording_BetweenOneFrameAndOneSecond(double frame, double duration, double expected)
    {
        Assert.Equal(expected, StudioEditorText.GetTrimStep(frame, duration), Precision);
    }

    [Fact]
    public void AutomationValue_AWholeNumberOfStepsAway_IsExactlyWhatTheArrowKeysGive()
    {
        // What a screen reader sends for "one step more" arrives in single precision.
        const double Step = 0.1;
        var current = 3.2;
        Assert.Equal(current + Step, StudioEditorText.ResolveAutomationValue((float)(current + Step), current, Step));
        Assert.Equal(current - Step, StudioEditorText.ResolveAutomationValue((float)(current - Step), current, Step));
        Assert.Equal(current + 10 * Step, StudioEditorText.ResolveAutomationValue((float)(current + 10 * Step), current, Step));

        // The same when the screen reader also read the value and the step in single precision.
        var asRead = (float)current + (float)Step;
        Assert.Equal(current + Step, StudioEditorText.ResolveAutomationValue((float)asRead, current, Step));
    }

    [Fact]
    public void AutomationValue_OneFrameOn_LandsOnTheFrame_NotJustBeforeIt()
    {
        const double Frame = 1.0 / 30;
        var playhead = 0.0;
        for (var frame = 1; frame <= 300; frame++)
        {
            var next = StudioEditorText.ResolveAutomationValue((float)(playhead + Frame), playhead, Frame);

            // Exactly what stepping one frame gives, so the frame shown is the next one.
            Assert.Equal(playhead + Frame, next);
            Assert.Equal(frame, (int)Math.Floor(next / Frame + 1e-9));
            playhead = next;
        }
    }

    [Fact]
    public void AutomationValue_FarIntoALongRecording_StillFindsTheStep()
    {
        const double Step = 1.0 / 60;
        var current = 5400.25;
        Assert.Equal(current + Step, StudioEditorText.ResolveAutomationValue((float)(current + Step), current, Step));
        Assert.Equal(current - 3 * Step, StudioEditorText.ResolveAutomationValue((float)(current - 3 * Step), current, Step));
    }

    [Theory]
    [InlineData(3.27, 3.0, 0.1, 3.27)]
    [InlineData(12.345678, 0.0, 1.0, 12.346)]
    [InlineData(10.0, 3.33, 0.1, 10.0)]
    [InlineData(0.0, 3.33, 0.1, 0.0)]
    [InlineData(4.5, 4.5, 0.1, 4.5)]
    public void AutomationValue_ThatIsNotOnAStep_IsKeptToAThousandth(double asked, double current, double step, double expected)
    {
        Assert.Equal(expected, StudioEditorText.ResolveAutomationValue((float)asked, current, step), 12);
    }

    [Fact]
    public void AutomationValue_WithoutAUsableStepOrNumber_DoesNotFail()
    {
        Assert.Equal(3.2, StudioEditorText.ResolveAutomationValue((float)3.2, 1, 0), 12);
        Assert.Equal(3.2, StudioEditorText.ResolveAutomationValue((float)3.2, double.NaN, 0.1), 12);
        Assert.Equal(1.5, StudioEditorText.ResolveAutomationValue(double.NaN, 1.5, 0.1));
        Assert.Equal(1.5, StudioEditorText.ResolveAutomationValue(double.PositiveInfinity, 1.5, 0.1));
    }

    [Fact]
    public void ExportSizeText_UsesWholeNumbers()
    {
        Assert.Equal("Exports at 3840 × 1608", StudioEditorText.GetExportSizeText(new StudioSize(3840, 1608)));
        Assert.Equal("0:02.5 / 0:10.0", StudioEditorText.GetTimeText(2.59, 10));
    }

    [Fact]
    public void ZoomDescription_SaysTheScaleTheTimes_AndWhatElseApplies()
    {
        Assert.Equal(
            "Zoom 2×, 12.0 to 16.5 seconds",
            StudioEditorText.GetZoomDescription(new StudioZoom { Start = 12, End = 16.5 }));

        Assert.Equal(
            "Zoom 2.5×, 1.0 to 3.0 seconds, follows the pointer, suggested",
            StudioEditorText.GetZoomDescription(new StudioZoom
            {
                Start = 1,
                End = 3,
                Scale = 2.5,
                Focus = new StudioZoomFocus { Mode = StudioZoomFocusMode.Cursor },
                Origin = StudioZoomOrigin.Auto,
            }));

        Assert.Equal(
            "Zoom 1.25×, 0.0 to 0.3 seconds, suggested",
            StudioEditorText.GetZoomDescription(new StudioZoom { Start = 0, End = 0.3, Scale = 1.25, Origin = StudioZoomOrigin.Auto }));
    }

    [Theory]
    [InlineData(9, "Zoom 5×, 1.0 to 2.0 seconds")]
    [InlineData(0.2, "Zoom 1×, 1.0 to 2.0 seconds")]
    [InlineData(double.NaN, "Zoom 1×, 1.0 to 2.0 seconds")]
    public void ZoomDescription_SaysTheScaleThatIsDrawn(double stored, string expected)
    {
        Assert.Equal(expected, StudioEditorText.GetZoomDescription(new StudioZoom { Start = 1, End = 2, Scale = stored }));
    }

    [Theory]
    [InlineData(2, "2×")]
    [InlineData(2.5, "2.5×")]
    [InlineData(1.25, "1.25×")]
    [InlineData(3.14159, "3.14×")]
    [InlineData(9, "5×")]
    [InlineData(0.2, "1×")]
    [InlineData(double.NaN, "1×")]
    public void ZoomScaleText_IsTheScaleThatIsDrawn_WithUpToTwoDecimals(double stored, string expected)
    {
        Assert.Equal(expected, StudioEditorText.GetZoomScaleText(stored));
    }

    [Fact]
    public void ZoomRangeText_SaysTheTimesInSourceTime()
    {
        Assert.Equal("12.0 to 16.5 seconds", StudioEditorText.GetZoomRangeText(new StudioZoom { Start = 12, End = 16.5 }));
        Assert.Equal("0.0 to 0.3 seconds", StudioEditorText.GetZoomRangeText(new StudioZoom { Start = 0, End = 0.3 }));
        Assert.Equal(
            "0.0 to 0.0 seconds",
            StudioEditorText.GetZoomRangeText(new StudioZoom { Start = double.NaN, End = double.PositiveInfinity }));
    }

    [Fact]
    public void ZoomPositionText_CountsFromOne()
    {
        Assert.Equal("Zoom 1 of 1", StudioEditorText.GetZoomPositionText(0, 1));
        Assert.Equal("Zoom 2 of 5", StudioEditorText.GetZoomPositionText(1, 5));
        Assert.Equal("Zoom 12 of 1000", StudioEditorText.GetZoomPositionText(11, 1000));
    }

    [Theory]
    [InlineData(0, "No zooms to suggest for this recording.")]
    [InlineData(-1, "No zooms to suggest for this recording.")]
    [InlineData(1, "1 zoom suggested.")]
    [InlineData(2, "2 zooms suggested.")]
    [InlineData(1200, "1200 zooms suggested.")]
    public void ZoomSuggestionsText_SaysHowManyThereAre(int count, string expected)
    {
        Assert.Equal(expected, StudioEditorText.GetZoomSuggestionsText(count));
    }

    [Fact]
    public async Task PreviewDescription_NamesTheLayout_AndTheCornerOfTheBubble()
    {
        var session = await OpenAsync(CreateProject(camera: true));
        Assert.Equal("Screen with camera bubble, camera bottom right", StudioEditorText.GetPreviewDescription(session.Model!));

        session.SetCameraAnchor(StudioAnchor.TopLeft);
        Assert.Equal("Screen with camera bubble, camera top left", StudioEditorText.GetPreviewDescription(session.Model!));

        session.SetLayout(StudioLayout.SideBySide);
        Assert.Equal("Side by side", StudioEditorText.GetPreviewDescription(session.Model!));

        var withoutCamera = await OpenAsync(CreateProject());
        Assert.Equal("Screen only", StudioEditorText.GetPreviewDescription(withoutCamera.Model!));
    }

    // Canvas placement

    [Theory]
    [InlineData(1000, 600, 16.0 / 9, 1)]
    [InlineData(1000, 600, 16.0 / 9, 1.25)]
    [InlineData(1000, 600, 16.0 / 9, 1.5)]
    [InlineData(827.2, 513.6, 16.0 / 9, 1.75)]
    [InlineData(660, 520, 9.0 / 16, 2.25)]
    [InlineData(628, 444, 1, 1.5)]
    [InlineData(628.6667, 444.6667, 3440.0 / 1440, 1.5)]
    public void FitCanvas_IsTheLargestCenteredRectangle_OnWholePixels(double viewWidth, double viewHeight, double aspect, double scale)
    {
        var rect = StudioPreviewLayout.FitCanvas(viewWidth, viewHeight, aspect, scale);

        foreach (var edge in new[] { rect.X, rect.Y, rect.Width, rect.Height })
        {
            var pixels = edge * scale;
            Assert.Equal(Math.Round(pixels), pixels, 6);
        }

        Assert.True(rect.X >= 0 && rect.Y >= 0);
        Assert.True(rect.X + rect.Width <= viewWidth + 1e-9);
        Assert.True(rect.Y + rect.Height <= viewHeight + 1e-9);

        // It touches the view on one axis, to within the pixel lost to rounding, and is centered on the other.
        var pixel = 1 / scale;
        var fillsWidth = viewWidth - rect.Width < pixel + 1e-9;
        var fillsHeight = viewHeight - rect.Height < pixel + 1e-9;
        Assert.True(fillsWidth || fillsHeight);
        Assert.InRange(viewWidth - rect.Width - 2 * rect.X, -1e-9, 2 * pixel);
        Assert.InRange(viewHeight - rect.Height - 2 * rect.Y, -1e-9, 2 * pixel);

        // The shape is the canvas shape, to within a pixel.
        Assert.InRange(rect.Width * scale - rect.Height * scale * aspect, -Math.Max(1, aspect), Math.Max(1, aspect));
    }

    [Theory]
    [InlineData(0, 600, 1.5, 1)]
    [InlineData(1000, 0, 1.5, 1)]
    [InlineData(1000, 600, 0, 1)]
    [InlineData(1000, 600, double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 600, 1.5, 1)]
    [InlineData(0.2, 0.2, 1.5, 1)]
    public void FitCanvas_WithoutRoomOrShape_IsEmpty(double viewWidth, double viewHeight, double aspect, double scale)
    {
        Assert.Equal(default, StudioPreviewLayout.FitCanvas(viewWidth, viewHeight, aspect, scale));
    }

    [Fact]
    public void FitCanvas_GivesExactPixelsForACommonCase()
    {
        // 1500 by 900 physical pixels at 150%: a 16:9 canvas is 1500 by 843, 28 pixels down.
        Assert.Equal(
            new StudioFrameRect(0, 28 / 1.5, 1000, 843 / 1.5),
            StudioPreviewLayout.FitCanvas(1000, 600, 16.0 / 9, 1.5));

        // A scale that is not a number falls back to one.
        Assert.Equal(
            new StudioFrameRect(100, 0, 800, 600),
            StudioPreviewLayout.FitCanvas(1000, 600, 4.0 / 3, double.NaN));
    }
}
