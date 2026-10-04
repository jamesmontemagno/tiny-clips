using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

/// <summary>
/// What the zoom lane and the inspector ask of the editor model beyond single values: moving a
/// whole zoom, stepping through the zooms, keeping a selection on its zoom, the focus pad, and a
/// crop as what it cuts off each edge.
/// </summary>
public sealed class StudioZoomLaneTests
{
    private const int Precision = 9;

    // Moving a zoom

    [Fact]
    public void MoveZoom_KeepsTheLength_AndGoesWhereItIsPut()
    {
        var model = new StudioEditorModel(MakeProject(duration: 20) with
        {
            Zooms = [Zoom(1, 3), Zoom(8, 10.5), Zoom(15, 17)],
        });

        Assert.Equal(new StudioZoomEditResult(true, 1), model.MoveZoom(1, 5.25));

        Assert.Equal(5.25, model.Project.Zooms[1].Start, Precision);
        Assert.Equal(7.75, model.Project.Zooms[1].End, Precision);
        Assert.True(model.CanUndo);

        model.Undo();
        Assert.Equal(8, model.Project.Zooms[1].Start, Precision);
        Assert.Equal(10.5, model.Project.Zooms[1].End, Precision);
    }

    [Fact]
    public void MoveZoom_StopsAtTheNeighbours_OnExactlyTheirNumbers()
    {
        // Numbers that are not exact in binary: 11.1 - 2.3 + 2.3 is not 11.1.
        var model = new StudioEditorModel(MakeProject(duration: 20) with
        {
            Zooms = [Zoom(1, 3.1), Zoom(4, 6.3), Zoom(11.1, 13)],
        });

        Assert.Equal(new StudioZoomEditResult(true, 1), model.MoveZoom(1, 19));

        var zooms = model.Project.Zooms;
        Assert.True(zooms[1].End == zooms[2].Start);
        Assert.Equal(2.3, zooms[1].End - zooms[1].Start, Precision);

        Assert.Equal(new StudioZoomEditResult(true, 1), model.MoveZoom(1, 0));

        zooms = model.Project.Zooms;
        Assert.True(zooms[1].Start == zooms[0].End);
        Assert.Equal(2.3, zooms[1].End - zooms[1].Start, Precision);
        Assert.Equal(1, zooms[0].Start, Precision);
        Assert.Equal(11.1, zooms[2].Start, Precision);
    }

    [Fact]
    public void MoveZoom_StopsAtTheEndsOfTheRecording()
    {
        var model = new StudioEditorModel(MakeProject(duration: 11.1) with { Zooms = [Zoom(4, 6.3)] });

        model.MoveZoom(0, -5);
        Assert.Equal(0, model.Project.Zooms[0].Start, Precision);
        Assert.Equal(2.3, model.Project.Zooms[0].End, Precision);

        // Exactly the end of the recording, not a rounding error past it.
        model.MoveZoom(0, 50);
        Assert.Equal(8.8, model.Project.Zooms[0].Start, Precision);
        Assert.True(model.Project.Zooms[0].End == 11.1);
    }

    [Fact]
    public void MovingASuggestion_MakesItTheUsersOwn_AndMovingItNowhereDoesNot()
    {
        var model = new StudioEditorModel(MakeProject() with
        {
            Zooms = [Zoom(4, 6, origin: StudioZoomOrigin.Auto)],
        });

        Assert.Equal(new StudioZoomEditResult(false, 0), model.MoveZoom(0, 4));
        Assert.Equal(StudioZoomOrigin.Auto, model.Project.Zooms[0].Origin);
        Assert.False(model.CanUndo);

        Assert.Equal(new StudioZoomEditResult(true, 0), model.MoveZoom(0, 5));
        Assert.Equal(StudioZoomOrigin.Manual, model.Project.Zooms[0].Origin);
    }

    [Fact]
    public void MoveZoom_ThatIsNotANumber_OrNamesNoZoom_ChangesNothing()
    {
        var model = new StudioEditorModel(MakeProject() with { Zooms = [Zoom(4, 6)] });
        var before = model.Project;

        Assert.Equal(new StudioZoomEditResult(false, 0), model.MoveZoom(0, double.NaN));
        Assert.Equal(new StudioZoomEditResult(false, 0), model.MoveZoom(0, double.PositiveInfinity));
        Assert.Equal(new StudioZoomEditResult(false, null), model.MoveZoom(1, 2));
        Assert.Equal(new StudioZoomEditResult(false, null), model.MoveZoom(-1, 2));

        Assert.Same(before, model.Project);
        Assert.False(model.CanUndo);
    }

    [Fact]
    public void AZoomLongerThanTheRoomBetweenItsNeighbours_IsMovedIntoTheRoom()
    {
        // Not something the editor makes: a project written by hand, where a zoom of 3.5 seconds
        // overlaps the next one and has 3 seconds of room.
        var model = new StudioEditorModel(MakeProject(duration: 10) with
        {
            Zooms = [Zoom(1, 3), Zoom(4, 7.5), Zoom(6, 9)],
        });

        Assert.Equal(new StudioZoomEditResult(true, 1), model.MoveZoom(1, 0));
        Assert.Equal(3, model.Project.Zooms[1].Start, Precision);
        Assert.Equal(6, model.Project.Zooms[1].End, Precision);

        // And with no room at all between them it stays where it is.
        var crowded = new StudioEditorModel(MakeProject(duration: 10) with
        {
            Zooms = [Zoom(1, 5), Zoom(4, 4.5), Zoom(4.2, 9)],
        });

        Assert.Equal(new StudioZoomEditResult(false, 1), crowded.MoveZoom(1, 0));
        Assert.Equal(4, crowded.Project.Zooms[1].Start, Precision);
    }

    // Whether a zoom can be added

    [Fact]
    public void AZoomCanBeAdded_ExactlyWhereAddingOneAnswersWithAZoom()
    {
        var project = MakeProject(duration: 10) with { Zooms = [Zoom(1, 3), Zoom(3.2, 7), Zoom(9.8, 10)] };

        for (var step = -4; step <= 208; step++)
        {
            var time = step * 0.05;
            var model = new StudioEditorModel(project);
            var canAdd = model.CanAddZoom(time);
            var result = model.AddZoom(time, null);

            Assert.True(canAdd == result.Index is not null, $"at {time}: can add {canAdd}, adding gave {result}");
        }

        var untouched = new StudioEditorModel(project);

        // A second before the first zoom, and inside a zoom, which adding selects.
        Assert.True(untouched.CanAddZoom(0));
        Assert.True(untouched.CanAddZoom(2));

        // 0.15 seconds before the next zoom, and 0.2 before the last.
        Assert.False(untouched.CanAddZoom(3.05));
        Assert.False(untouched.CanAddZoom(9.6));

        // At the end of a zoom, chained to it.
        Assert.True(untouched.CanAddZoom(7));

        // Times outside the recording count as its ends.
        Assert.True(untouched.CanAddZoom(-3));
        Assert.False(untouched.CanAddZoom(40));
        Assert.False(untouched.CanAddZoom(double.NaN));
        Assert.False(untouched.CanAddZoom(double.PositiveInfinity));

        // Asking changes nothing.
        Assert.Equal(3, untouched.Project.Zooms.Length);
        Assert.False(untouched.CanUndo);
    }

    // Stepping through the zooms

    [Theory]
    [InlineData(null, 0.0, 0)]
    [InlineData(null, 2.0, 0)]
    [InlineData(null, 3.0, 1)]
    [InlineData(null, 6.0, 1)]
    [InlineData(null, 12.0, null)]
    [InlineData(0, 12.0, 1)]
    [InlineData(1, 0.0, 2)]
    [InlineData(2, 0.0, null)]
    [InlineData(9, 6.0, 1)]
    public void TheNextZoom_FollowsTheSelectedOne_OrIsTheOneAtOrAfterThePlayhead(int? selected, double playhead, int? expected)
    {
        var model = new StudioEditorModel(MakeProject(duration: 12) with
        {
            Zooms = [Zoom(1, 3), Zoom(5, 7), Zoom(9, 11)],
        });

        Assert.Equal(expected, model.GetZoomIndexAfter(selected, playhead));
    }

    [Theory]
    [InlineData(null, 0.0, null)]
    [InlineData(null, 1.0, 0)]
    [InlineData(null, 4.0, 0)]
    [InlineData(null, 6.0, 1)]
    [InlineData(null, 12.0, 2)]
    [InlineData(2, 0.0, 1)]
    [InlineData(0, 12.0, null)]
    [InlineData(-1, 6.0, 1)]
    public void ThePreviousZoom_ComesBeforeTheSelectedOne_OrIsTheOneAtOrBeforeThePlayhead(int? selected, double playhead, int? expected)
    {
        var model = new StudioEditorModel(MakeProject(duration: 12) with
        {
            Zooms = [Zoom(1, 3), Zoom(5, 7), Zoom(9, 11)],
        });

        Assert.Equal(expected, model.GetZoomIndexBefore(selected, playhead));
    }

    [Fact]
    public void WithoutZooms_ThereIsNoNextOrPreviousOne()
    {
        var model = new StudioEditorModel(MakeProject());

        Assert.Null(model.GetZoomIndexAfter(null, 0));
        Assert.Null(model.GetZoomIndexBefore(null, 5));
        Assert.Null(model.GetZoomIndexAfter(0, 0));
    }

    // Keeping a selection on its zoom

    [Fact]
    public void WhenOnlyOneZoomHasChanged_ItIsFollowedToTheSamePlace_HoweverFarItMoved()
    {
        var first = Zoom(1, 2);
        var middle = Zoom(4, 5);
        var last = Zoom(8, 9);
        StudioZoom[] before = [first, middle, last];

        // The middle zoom, moved to where it shares no time with what it was.
        Assert.Equal(1, StudioEditorModel.FindZoomFollowing(before, 1, [first, Zoom(6, 7, scale: 3), last]));

        // Nothing changed at all, as when an edit was to something other than the zooms.
        Assert.Equal(2, StudioEditorModel.FindZoomFollowing(before, 2, [first, middle, last]));
        Assert.Equal(0, StudioEditorModel.FindZoomFollowing(before, 0, before));
    }

    [Fact]
    public void WhenMoreHasChanged_AZoomIsFollowedToTheOneThatSharesTheMostTimeWithIt()
    {
        StudioZoom[] after = [Zoom(0, 2), Zoom(2, 5), Zoom(6, 9)];

        // Itself, one place on, behind a zoom that was added.
        Assert.Equal(1, StudioEditorModel.FindZoomFollowing([Zoom(2, 5), Zoom(6, 9)], 0, after));

        // Half a second with the first, two seconds with the second.
        Assert.Equal(1, StudioEditorModel.FindZoomFollowing([Zoom(1.5, 4)], 0, after));

        // Half a second with the second, two seconds with the third.
        Assert.Equal(2, StudioEditorModel.FindZoomFollowing([Zoom(4.5, 8)], 0, after));

        // Touching two zooms is not sharing time with either.
        Assert.Null(StudioEditorModel.FindZoomFollowing([Zoom(5, 6)], 0, after));
        Assert.Null(StudioEditorModel.FindZoomFollowing([Zoom(10, 12)], 0, after));
        Assert.Null(StudioEditorModel.FindZoomFollowing([Zoom(1, 2)], 0, []));
    }

    [Fact]
    public void AmongZoomsThatShareTheSameTime_TheOneNearestToWhereItWasIsFollowed()
    {
        // Written by hand: the editor does not make zooms that overlap.
        StudioZoom[] after = [Zoom(0, 10), Zoom(2, 5), Zoom(2, 5, scale: 3)];
        StudioZoom[] before = [Zoom(2, 5), Zoom(2, 5), Zoom(2, 5), Zoom(2, 5)];

        Assert.Equal(0, StudioEditorModel.FindZoomFollowing(before, 0, after));
        Assert.Equal(1, StudioEditorModel.FindZoomFollowing(before, 1, after));
        Assert.Equal(2, StudioEditorModel.FindZoomFollowing(before, 2, after));
        Assert.Equal(2, StudioEditorModel.FindZoomFollowing(before, 3, after));
    }

    [Fact]
    public void AZoomThatWasNotThere_IsNotFollowedAnywhere()
    {
        StudioZoom[] zooms = [Zoom(1, 2)];

        Assert.Null(StudioEditorModel.FindZoomFollowing(zooms, 1, zooms));
        Assert.Null(StudioEditorModel.FindZoomFollowing(zooms, -1, zooms));
        Assert.Null(StudioEditorModel.FindZoomFollowing([], 0, zooms));
    }

    // Showing a zoom

    [Fact]
    public void AZoomIsShown_AtTheMomentItHasMovedIn()
    {
        var model = new StudioEditorModel(MakeProject(duration: 20) with
        {
            Zooms =
            [
                Zoom(1, 4),
                Zoom(5, 8, easeIn: 1.25),
                Zoom(10, 12, easeIn: 0),

                // Eases longer than the zoom are shortened in proportion: 3 and 1 in one second
                // are 0.75 and 0.25.
                Zoom(14, 15, easeIn: 3, easeOut: 1),
            ],
        });

        Assert.Equal(1.5, Assert.NotNull(model.GetZoomLookTime(0)), Precision);
        Assert.Equal(6.25, Assert.NotNull(model.GetZoomLookTime(1)), Precision);
        Assert.Equal(10, Assert.NotNull(model.GetZoomLookTime(2)), Precision);
        Assert.Equal(14.75, Assert.NotNull(model.GetZoomLookTime(3)), Precision);
        Assert.Null(model.GetZoomLookTime(4));
        Assert.Null(model.GetZoomLookTime(-1));

        // At each of those times the layout shows the whole zoom: twice the size, in the middle.
        for (var index = 0; index < 4; index++)
        {
            var time = Assert.NotNull(model.GetZoomLookTime(index));
            var source = StudioLayoutResolver.Resolve(model.Project, time, 1920, 1080).Screen!.Value.Source;
            AssertRect(source, 0.25, 0.25, 0.5, 0.5);
        }
    }

    [Fact]
    public void AZoomThatNeverMovesAllTheWayIn_IsShownOnItsLastFrame()
    {
        // The next zoom is chained, so this one has no ease out and its ease in takes the whole
        // second. A zoom does not contain its end, so the time stays one frame inside it.
        var model = new StudioEditorModel(MakeProject(duration: 20) with
        {
            Zooms = [Zoom(14, 15, easeIn: 3, easeOut: 1), Zoom(15, 18)],
        });

        var time = Assert.NotNull(model.GetZoomLookTime(0));

        Assert.Equal(15 - (1.0 / 30), time, Precision);
        Assert.Equal(0, model.GetZoomIndexAt(time));
    }

    [Fact]
    public void AZoomShorterThanAFrame_IsShownAtItsStart()
    {
        var model = new StudioEditorModel(MakeProject(duration: 20) with { Zooms = [Zoom(3, 3.01, easeIn: 0)] });

        Assert.Equal(3, Assert.NotNull(model.GetZoomLookTime(0)), Precision);
    }

    // The focus pad

    [Fact]
    public void ThePad_ShowsTheWindowAndThePoint_AcrossTheWholeScreen()
    {
        var model = new StudioEditorModel(MakeProject() with
        {
            Zooms = [Zoom(1, 3, scale: 4, x: 0.5, y: 0.25), Zoom(5, 7, scale: 2, x: 0.9, y: 0.05)],
        });

        var centered = Assert.NotNull(model.GetZoomPad(0));
        Assert.Equal(16.0 / 9, centered.AspectRatio, Precision);
        AssertRect(centered.Window, 0.375, 0.125, 0.25, 0.25);
        Assert.Equal(0.5, centered.FocusX, Precision);
        Assert.Equal(0.25, centered.FocusY, Precision);

        // Near a corner the window stops at the edges, and the point stays where it was put.
        var corner = Assert.NotNull(model.GetZoomPad(1));
        AssertRect(corner.Window, 0.5, 0, 0.5, 0.5);
        Assert.Equal(0.9, corner.FocusX, Precision);
        Assert.Equal(0.05, corner.FocusY, Precision);

        Assert.Null(model.GetZoomPad(2));
        Assert.Null(model.GetZoomPad(-1));
    }

    [Fact]
    public void ThePad_StandsForTheCrop_WhenTheScreenIsCropped()
    {
        var model = new StudioEditorModel(MakeProject() with { Zooms = [Zoom(1, 3, scale: 2, x: 0.75, y: 0.5)] });
        model.SetScreenCrop(new StudioRect(0.5, 0, 0.5, 1));

        // The right half of a 16:9 screen, and the zoom looks at the middle of it.
        var pad = Assert.NotNull(model.GetZoomPad(0));
        Assert.Equal(8.0 / 9, pad.AspectRatio, Precision);
        Assert.Equal(0.5, pad.FocusX, Precision);
        Assert.Equal(0.5, pad.FocusY, Precision);
        AssertRect(pad.Window, 0.25, 0.25, 0.5, 0.5);

        // A point the crop has cut off is shown on the pad's edge, where the window stops too.
        model.SetZoomFocusPoint(0, 0.1, 0.5);
        var outside = Assert.NotNull(model.GetZoomPad(0));
        Assert.Equal(0, outside.FocusX, Precision);
        AssertRect(outside.Window, 0, 0.25, 0.5, 0.5);
    }

    [Fact]
    public void ThePadsWindow_IsTheOneTheLayoutDraws()
    {
        var model = new StudioEditorModel(MakeProject() with
        {
            Zooms = [Zoom(1, 5, scale: 3, x: 0.2, y: 0.9, easeIn: 0)],
        });
        model.SetScreenCrop(new StudioRect(0.1, 0.2, 0.6, 0.5));

        var pad = Assert.NotNull(model.GetZoomPad(0));
        var source = StudioLayoutResolver.Resolve(model.Project, 2, 1920, 1080).Screen!.Value.Source;

        AssertRect(
            source,
            0.1 + (pad.Window.X * 0.6),
            0.2 + (pad.Window.Y * 0.5),
            pad.Window.Width * 0.6,
            pad.Window.Height * 0.5);
        Assert.Equal(1.0 / 3, pad.Window.Width, Precision);
        Assert.Equal(1.0 / 3, pad.Window.Height, Precision);
    }

    [Fact]
    public void PointingOnThePad_SetsTheFocusInTheScreenFrame()
    {
        var model = new StudioEditorModel(MakeProject() with { Zooms = [Zoom(1, 3)] });
        model.SetScreenCrop(new StudioRect(0.5, 0.2, 0.4, 0.6));

        // A quarter of the way across the crop, and below the pad, which counts as its bottom edge.
        Assert.Equal(new StudioZoomEditResult(true, 0), model.SetZoomFocusOnPad(0, 0.25, 1.5));

        Assert.Equal(0.6, model.Project.Zooms[0].Focus.X, Precision);
        Assert.Equal(0.8, model.Project.Zooms[0].Focus.Y, Precision);
        var pad = Assert.NotNull(model.GetZoomPad(0));
        Assert.Equal(0.25, pad.FocusX, Precision);
        Assert.Equal(1, pad.FocusY, Precision);
    }

    [Fact]
    public void PointingOnThePad_AtWhatIsNotANumber_OrForNoZoom_ChangesNothing()
    {
        var model = new StudioEditorModel(MakeProject() with { Zooms = [Zoom(1, 3)] });
        var before = model.Project;

        Assert.Equal(new StudioZoomEditResult(false, 0), model.SetZoomFocusOnPad(0, double.NaN, 0.5));
        Assert.Equal(new StudioZoomEditResult(false, 0), model.SetZoomFocusOnPad(0, 0.5, double.NegativeInfinity));
        Assert.Equal(new StudioZoomEditResult(false, null), model.SetZoomFocusOnPad(3, 0.5, 0.5));
        Assert.Equal(new StudioZoomEditResult(false, null), model.SetZoomFocusOnPad(3, double.NaN, 0.5));

        // Where the zoom already looks.
        Assert.Equal(new StudioZoomEditResult(false, 0), model.SetZoomFocusOnPad(0, 0.5, 0.5));

        Assert.Same(before, model.Project);
    }

    // Counting suggestions

    [Fact]
    public void SuggestedZooms_AreCounted_UntilTheyAreChanged()
    {
        var model = new StudioEditorModel(MakeProject() with
        {
            Zooms = [Zoom(1, 2, origin: StudioZoomOrigin.Auto), Zoom(3, 4), Zoom(5, 6, origin: StudioZoomOrigin.Auto)],
        });

        Assert.Equal(2, model.SuggestedZoomCount);

        model.SetZoomScale(0, 3);

        Assert.Equal(1, model.SuggestedZoomCount);
    }

    // Crops as insets

    [Fact]
    public void CropInsets_AreWhatACropCutsOffEachEdge()
    {
        Assert.Equal(default, StudioCropInsets.From(null));
        Assert.True(StudioCropInsets.From(null).IsEmpty);

        var insets = StudioCropInsets.From(new StudioRect(0.1, 0.2, 0.6, 0.4));

        // Plain numbers, without what binary leaves behind in 1 - 0.1 - 0.6.
        Assert.Equal(new StudioCropInsets(0.1, 0.2, 0.3, 0.4), insets);
        Assert.False(insets.IsEmpty);
        Assert.Equal(0.1, insets[StudioCropEdge.Left]);
        Assert.Equal(0.2, insets[StudioCropEdge.Top]);
        Assert.Equal(0.3, insets[StudioCropEdge.Right]);
        Assert.Equal(0.4, insets[StudioCropEdge.Bottom]);

        // A crop that is not valid is not applied, so it cuts nothing off.
        Assert.Equal(default, StudioCropInsets.From(new StudioRect(0.5, 0.5, 0.01, 0.5)));
    }

    [Fact]
    public void ACropEdge_GoesWhereItIsPut_AndTheCropIsStoredInPlainNumbers()
    {
        var model = new StudioEditorModel(MakeProject(camera: true));

        model.SetScreenCropInset(StudioCropEdge.Left, 0.07);
        AssertRect(model.Project.Screen.Crop, 0.07, 0, 0.93, 1);

        model.SetScreenCropInset(StudioCropEdge.Right, 0.2);
        model.SetScreenCropInset(StudioCropEdge.Top, 0.1);
        model.SetScreenCropInset(StudioCropEdge.Bottom, 0.25);

        var crop = Assert.IsType<StudioRect>(model.Project.Screen.Crop);
        Assert.True(crop is { X: 0.07, Y: 0.1, Width: 0.73, Height: 0.65 }, $"{crop.X} {crop.Y} {crop.Width} {crop.Height}");
        Assert.Equal(new StudioCropInsets(0.07, 0.1, 0.2, 0.25), model.ScreenCropInsets);
        Assert.NotNull(StudioCanvasMath.ValidCropOrNull(crop));

        Assert.Null(model.Project.Camera.Crop);
        model.SetCameraCropInset(StudioCropEdge.Top, 0.3);
        AssertRect(model.Project.Camera.Crop, 0, 0.3, 1, 0.7);
        Assert.Equal(new StudioCropInsets(0, 0.3, 0, 0), model.CameraCropInsets);
        Assert.Equal(new StudioCropInsets(0.07, 0.1, 0.2, 0.25), model.ScreenCropInsets);
    }

    [Fact]
    public void WithNothingCutOffAnyEdge_TheCropIsRemoved()
    {
        var model = new StudioEditorModel(MakeProject());
        model.SetScreenCrop(new StudioRect(0.1, 0.2, 0.6, 0.4));

        model.SetScreenCropInset(StudioCropEdge.Left, 0);
        model.SetScreenCropInset(StudioCropEdge.Top, 0);
        model.SetScreenCropInset(StudioCropEdge.Right, 0);
        AssertRect(model.Project.Screen.Crop, 0, 0, 1, 0.6);

        model.SetScreenCropInset(StudioCropEdge.Bottom, 0);
        Assert.Null(model.Project.Screen.Crop);
        Assert.True(model.ScreenCropInsets.IsEmpty);
    }

    [Fact]
    public void ACropEdge_StopsWhereAPieceOfTheFrameIsStillLeft()
    {
        var model = new StudioEditorModel(MakeProject());

        model.SetScreenCropInset(StudioCropEdge.Left, 0.5);
        model.SetScreenCropInset(StudioCropEdge.Right, 0.9);
        AssertRect(model.Project.Screen.Crop, 0.5, 0, 0.05, 1);

        // Already as far as it goes.
        var before = model.Project;
        model.SetScreenCropInset(StudioCropEdge.Left, 2);
        Assert.Same(before, model.Project);

        model.SetScreenCropInset(StudioCropEdge.Bottom, 1);
        model.SetScreenCropInset(StudioCropEdge.Top, -3);
        AssertRect(model.Project.Screen.Crop, 0.5, 0, 0.05, 0.05);
        Assert.NotNull(StudioCanvasMath.ValidCropOrNull(model.Project.Screen.Crop));
    }

    [Fact]
    public void ACropEdge_ThatIsNotANumber_OrIsPutWhereItIs_ChangesNothing()
    {
        var model = new StudioEditorModel(MakeProject());
        model.SetScreenCropInset(StudioCropEdge.Left, 0.1);
        var before = model.Project;

        model.SetScreenCropInset(StudioCropEdge.Left, double.NaN);
        model.SetScreenCropInset(StudioCropEdge.Right, double.PositiveInfinity);
        model.SetScreenCropInset(StudioCropEdge.Left, 0.1);
        model.SetScreenCropInset(StudioCropEdge.Top, 0);

        Assert.Same(before, model.Project);
        model.Undo();
        Assert.Null(model.Project.Screen.Crop);
        Assert.False(model.CanUndo);
    }

    [Fact]
    public void MovingACropEdge_KeepsWhatTheCropHasThatThisVersionDoesNotKnow()
    {
        var project = StudioProjectJson.ReadProject("""
            {
              "id": "3f0013cf-ba10-4453-af91-792b7882dae6",
              "sources": { "screen": { "width": 1920, "height": 1080, "duration": 10 } },
              "screen": { "crop": { "x": 0.1, "y": 0.1, "width": 0.8, "height": 0.8, "feather": 4 } }
            }
            """);
        var model = new StudioEditorModel(project);

        model.SetScreenCropInset(StudioCropEdge.Left, 0.3);

        AssertRect(model.Project.Screen.Crop, 0.3, 0.1, 0.6, 0.8);
        Assert.Contains("\"feather\": 4", StudioProjectJson.WriteProject(model.Project));
    }

    // Helpers

    private static StudioZoom Zoom(
        double start,
        double end,
        double scale = 2,
        double x = 0.5,
        double y = 0.5,
        double easeIn = 0.5,
        double easeOut = 0.5,
        StudioZoomOrigin origin = StudioZoomOrigin.Manual) => new()
        {
            Start = start,
            End = end,
            Scale = scale,
            Focus = new StudioZoomFocus { X = x, Y = y },
            EaseIn = easeIn,
            EaseOut = easeOut,
            Origin = origin,
        };

    private static void AssertRect(StudioRect? actual, double x, double y, double width, double height)
    {
        Assert.NotNull(actual);
        Assert.Equal(x, actual.X, Precision);
        Assert.Equal(y, actual.Y, Precision);
        Assert.Equal(width, actual.Width, Precision);
        Assert.Equal(height, actual.Height, Precision);
    }

    private static void AssertRect(StudioFrameRect actual, double x, double y, double width, double height)
    {
        Assert.Equal(x, actual.X, Precision);
        Assert.Equal(y, actual.Y, Precision);
        Assert.Equal(width, actual.Width, Precision);
        Assert.Equal(height, actual.Height, Precision);
    }

    private static StudioProject MakeProject(bool camera = false, double duration = 10) => new()
    {
        Id = "3f0013cf-ba10-4453-af91-792b7882dae6",
        Sources = new StudioSources
        {
            Screen = new StudioScreenSource { Width = 1920, Height = 1080, FrameRate = 30, Duration = duration },
            Camera = camera ? new StudioCameraSource { Width = 1280, Height = 720, Duration = duration } : null,
        },
        Scenes = [new StudioScene { Start = 0, Layout = camera ? StudioLayout.Bubble : StudioLayout.Screen }],
    };
}
