using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

/// <summary>Section 15.4 of the project format, up to where the pixels start.</summary>
public sealed class StudioCaptionDrawingTests
{
    private const int Precision = 9;

    private static StudioCaptionStyle Style(StudioCaptionAppearance appearance, StudioCaptionSize size = StudioCaptionSize.Medium, double width = 512, double height = 288) =>
        StudioCaptionStyle.For(new StudioCaptionOverlay { Appearance = appearance, Size = size }, width, height);

    [Theory]
    [InlineData(StudioCaptionSize.Small, 14)]
    [InlineData(StudioCaptionSize.Medium, 18)]
    [InlineData(StudioCaptionSize.Large, 24)]
    public void TheSizes_AreInUnitsOfTheShortSide(StudioCaptionSize size, double units)
    {
        // A unit is one pixel at 288, and these canvases have short sides of 288, 1080 and 1080.
        Assert.Equal(units, Style(StudioCaptionAppearance.ClassicOutline, size).TextHeight, Precision);
        Assert.Equal(units * 3.75, Style(StudioCaptionAppearance.ClassicOutline, size, 1920, 1080).TextHeight, Precision);
        Assert.Equal(units * 3.75, Style(StudioCaptionAppearance.ClassicOutline, size, 1080, 1920).TextHeight, Precision);
    }

    [Fact]
    public void ClassicOutline_IsWhiteOnABlackOutline()
    {
        var style = Style(StudioCaptionAppearance.ClassicOutline);

        Assert.False(style.IsBold);
        Assert.Equal(new StudioCaptionPaint(255, 255, 255, 1), style.Text);
        Assert.Equal(new StudioCaptionPaint(0, 0, 0, 1), style.Outline);
        Assert.Equal(1.2, style.OutlineWidth, Precision);
        Assert.Equal(0, style.ShadowOffset);
        Assert.Equal(0, style.PanelReach);
    }

    [Fact]
    public void ContrastPanel_IsWhiteOnAPanel_WithNoOutline()
    {
        var style = Style(StudioCaptionAppearance.ContrastPanel);

        Assert.Equal(new StudioCaptionPaint(255, 255, 255, 1), style.Text);
        Assert.Equal(new StudioCaptionPaint(0, 0, 0, 0.6), style.Panel);
        Assert.Equal(4, style.PanelReach, Precision);
        Assert.Equal(0, style.OutlineWidth);
        Assert.Equal(0, style.ShadowOffset);
    }

    [Fact]
    public void BoldImpact_IsBoldGold_WithAnOutlineAndAShadow()
    {
        var style = Style(StudioCaptionAppearance.BoldImpact);

        Assert.True(style.IsBold);
        Assert.Equal(new StudioCaptionPaint(0xFF, 0xD7, 0, 1), style.Text);
        Assert.Equal(new StudioCaptionPaint(0, 0, 0, 1), style.Outline);
        Assert.Equal(2.4, style.OutlineWidth, Precision);
        Assert.Equal(new StudioCaptionPaint(0, 0, 0, 1), style.Shadow);
        Assert.Equal(1.2, style.ShadowOffset, Precision);
    }

    [Fact]
    public void CleanShadow_IsOffWhite_WithAHalfBlackOutlineAndShadow()
    {
        var style = Style(StudioCaptionAppearance.CleanShadow);

        Assert.Equal(new StudioCaptionPaint(0xF5, 0xF5, 0xF5, 1), style.Text);
        Assert.Equal(new StudioCaptionPaint(0, 0, 0, 0.5), style.Outline);
        Assert.Equal(0.6, style.OutlineWidth, Precision);
        Assert.Equal(new StudioCaptionPaint(0, 0, 0, 0.5), style.Shadow);
        Assert.Equal(1.5, style.ShadowOffset, Precision);
    }

    [Fact]
    public void ALineWrapsAt90PercentOfTheCanvasWidth_EvenOnAPortraitCanvas()
    {
        Assert.Equal(1728, Style(StudioCaptionAppearance.ClassicOutline, width: 1920, height: 1080).LongestLine, Precision);
        Assert.Equal(972, Style(StudioCaptionAppearance.ClassicOutline, width: 1080, height: 1920).LongestLine, Precision);
    }

    [Fact]
    public void TextIsPlaced_12UnitsFromTheEdgeItIsNear()
    {
        var style = Style(StudioCaptionAppearance.ClassicOutline, width: 1920, height: 1080);

        Assert.Equal(45, style.Margin, Precision);
        Assert.Equal(45, style.TopOfText(StudioCaptionPosition.Top, 100, 1080), Precision);
        Assert.Equal(1080 - 45 - 100, style.TopOfText(StudioCaptionPosition.Bottom, 100, 1080), Precision);
    }

    [Fact]
    public void TextTooTallForTheCanvas_StartsAtItsFirstLine()
    {
        var style = Style(StudioCaptionAppearance.ClassicOutline, width: 1920, height: 1080);

        Assert.Equal(0, style.TopOfText(StudioCaptionPosition.Bottom, 5000, 1080));
    }

    [Theory]
    [InlineData(StudioCaptionAppearance.ClassicOutline, 1.2)]
    [InlineData(StudioCaptionAppearance.ContrastPanel, 4)]
    [InlineData(StudioCaptionAppearance.BoldImpact, 3.6)]
    [InlineData(StudioCaptionAppearance.CleanShadow, 2.1)]
    public void TheReach_CoversAllThatIsDrawnOutsideTheText(StudioCaptionAppearance appearance, double reach)
    {
        Assert.Equal(reach, Style(appearance).Reach, Precision);
    }
}

public sealed class StudioCaptionTrackTests
{
    private static StudioProject Project(bool enabled = true, StudioEdits? edits = null) => new()
    {
        Sources = new StudioSources { Screen = new StudioScreenSource { File = "screen.mp4", Width = 1920, Height = 1080, Duration = 20, FrameRate = 30 } },
        Edits = edits ?? new StudioEdits(),
        Overlays = new StudioOverlays { Captions = new StudioCaptionOverlay { Enabled = enabled } },
        Captions = new StudioCaptions
        {
            Cues =
            [
                new StudioCaptionCue { Id = "one", Start = 2, End = 4, Text = "One" },
                new StudioCaptionCue { Id = "two", Start = 6, End = 8, Text = "Two" },
            ],
        },
    };

    [Theory]
    [InlineData(1.99, null)]
    [InlineData(2, "One")]
    [InlineData(3.99, "One")]
    [InlineData(4, null)]
    [InlineData(7, "Two")]
    [InlineData(8.5, null)]
    public void WhatIsDrawn_IsTheCaptionShowingThen(double time, string? text)
    {
        Assert.Equal(text, StudioCaptionTrack.DrawnTextAt(Project(), time));
    }

    [Fact]
    public void NothingIsDrawn_WithBurnInSwitchedOff()
    {
        Assert.Null(StudioCaptionTrack.DrawnTextAt(Project(enabled: false), 3));
    }

    [Fact]
    public void ACaptionStaysOnItsWords_WhenACutMovesThem()
    {
        var project = Project(edits: new StudioEdits { Cuts = [new StudioTimeRange { Start = 0, End = 1 }] });

        Assert.Equal("One", StudioCaptionTrack.DrawnTextAt(project, 3));
        Assert.Null(StudioCaptionTrack.DrawnTextAt(project, 1.99));
    }

    [Fact]
    public void APartPlayedFaster_HasNoCaption()
    {
        var project = Project(edits: new StudioEdits { Speed = [new StudioSpeedRange { Start = 2, End = 4, Rate = 2 }] });

        Assert.Null(StudioCaptionTrack.DrawnTextAt(project, 3));
        Assert.Equal("Two", StudioCaptionTrack.DrawnTextAt(project, 7));
    }

    [Fact]
    public void AProjectKeepsItsTrack_AndAnEditedOneHasItsOwn()
    {
        var project = Project();

        Assert.Same(StudioCaptionTrack.For(project), StudioCaptionTrack.For(project));

        var edited = project with { Captions = new StudioCaptions { Cues = [new StudioCaptionCue { Id = "new", Start = 2, End = 4, Text = "New" }] } };
        Assert.Equal("New", StudioCaptionTrack.DrawnTextAt(edited, 3));
        Assert.Equal("One", StudioCaptionTrack.DrawnTextAt(project, 3));
    }

    [Fact]
    public void AProjectWithNoCaptions_DrawsNone()
    {
        Assert.Null(StudioCaptionTrack.DrawnTextAt(Project() with { Captions = new StudioCaptions() }, 3));
    }
}
