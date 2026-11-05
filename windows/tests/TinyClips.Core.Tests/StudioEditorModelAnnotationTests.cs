using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

/// <summary>What the editor model does to annotations. The same on the Mac (<c>StudioAnnotationTests.swift</c>).</summary>
public sealed class StudioEditorModelAnnotationTests
{
    private static StudioEditorModel Model(StudioAnnotation[]? annotations = null, StudioZoom[]? zooms = null) =>
        new(new StudioProject
        {
            Id = "p",
            Sources = new StudioSources { Screen = new StudioScreenSource { Width = 1920, Height = 1080, Duration = 60 } },
            Canvas = new StudioCanvas { Padding = 0 },
            Scenes = [new StudioScene { Layout = StudioLayout.Screen }],
            Zooms = zooms ?? [],
            Overlays = new StudioOverlays { Annotations = annotations ?? [] },
        });

    private static StudioAnnotation[] List(StudioEditorModel model) => model.Project.Overlays.Annotations;

    [Fact]
    public void Adding_PutsItOnTop_ForThreeSeconds_WithTheDefaultsOfItsKind()
    {
        var model = Model();

        Assert.Equal(new StudioAnnotationEditResult(true, 0), model.AddAnnotation(StudioAnnotationKinds.Arrow, 10, null));
        Assert.Equal(new StudioAnnotationEditResult(true, 1), model.AddAnnotation(StudioAnnotationKinds.Callout, 10, null));
        Assert.Equal(new StudioAnnotationEditResult(true, 2), model.AddAnnotation(StudioAnnotationKinds.Text, 10, null));

        var (arrow, callout, text) = (List(model)[0], List(model)[1], List(model)[2]);
        Assert.Equal((10, 13), (arrow.Start, arrow.End));
        Assert.Equal((StudioAnnotationAttach.Screen, StudioAnnotationStyle.Red, string.Empty), (arrow.Attach, arrow.Style, arrow.Text));
        Assert.Equal((StudioAnnotationAttach.Screen, StudioAnnotationStyle.Dark, StudioEditorModel.NewAnnotationText), (callout.Attach, callout.Style, callout.Text));
        Assert.Equal((StudioAnnotationAttach.Frame, StudioAnnotationStyle.White, StudioEditorModel.NewAnnotationText), (text.Attach, text.Style, text.Text));
        Assert.Equal(3, List(model).Select(annotation => annotation.Id).Distinct().Count());
        Assert.All(List(model), annotation => Assert.NotEmpty(annotation.Id));
    }

    [Fact]
    public void Adding_NearTheEnd_StaysInsideTheRecording()
    {
        var model = Model();

        model.AddAnnotation(StudioAnnotationKinds.Arrow, 59, null);
        model.AddAnnotation(StudioAnnotationKinds.Arrow, 60, null);

        Assert.Equal((59, 60), (List(model)[0].Start, List(model)[0].End));
        Assert.Equal((60 - StudioEditorModel.MinimumAnnotationDuration, 60), (List(model)[1].Start, List(model)[1].End));
    }

    [Fact]
    public void Adding_GoesInTheMiddleOfWhatIsInView()
    {
        var zoom = new StudioZoom { Start = 5, End = 15, Scale = 2, Focus = new StudioZoomFocus { X = 0.25, Y = 0.25 }, EaseIn = 0, EaseOut = 0 };
        var model = Model(zooms: [zoom]);

        model.AddAnnotation(StudioAnnotationKinds.Arrow, 1, null);
        model.AddAnnotation(StudioAnnotationKinds.Arrow, 10, null);
        model.AddAnnotation(StudioAnnotationKinds.Text, 10, null);

        Assert.Equal((0.5, 0.5), (List(model)[0].X, List(model)[0].Y));
        Assert.Equal((0.25, 0.25), (List(model)[1].X, List(model)[1].Y));

        // Text is fixed to the frame, so the zoom has nothing to do with it.
        Assert.Equal((0.5, 0.5), (List(model)[2].X, List(model)[2].Y));
    }

    [Fact]
    public void Adding_WithNoScreenInTheFrame_FixesItToTheFrame()
    {
        var model = new StudioEditorModel(new StudioProject
        {
            Id = "p",
            Sources = new StudioSources
            {
                Screen = new StudioScreenSource { Width = 1920, Height = 1080, Duration = 60 },
                Camera = new StudioCameraSource { Width = 1280, Height = 720, Duration = 60 },
            },
            Scenes = [new StudioScene { Layout = StudioLayout.Camera }],
        });

        model.AddAnnotation(StudioAnnotationKinds.Arrow, 1, null);

        Assert.Equal(StudioAnnotationAttach.Frame, List(model)[0].Attach);
    }

    [Fact]
    public void Timing_KeepsAnAnnotationInsideTheRecording_AndAtLeastTheShortest()
    {
        var model = Model([new StudioAnnotation { Start = 10, End = 20 }]);

        model.SetAnnotationStart(0, 30);
        Assert.Equal((20 - StudioEditorModel.MinimumAnnotationDuration, 20), (List(model)[0].Start, List(model)[0].End));
        model.SetAnnotationStart(0, -5);
        Assert.Equal((0, 20), (List(model)[0].Start, List(model)[0].End));

        model.SetAnnotationEnd(0, 500);
        Assert.Equal((0, 60), (List(model)[0].Start, List(model)[0].End));
        model.SetAnnotationEnd(0, -5);
        Assert.Equal((0, StudioEditorModel.MinimumAnnotationDuration), (List(model)[0].Start, List(model)[0].End));

        Assert.False(model.SetAnnotationStart(0, double.NaN).Changed);
        Assert.Equal(new StudioAnnotationEditResult(false, null), model.SetAnnotationStart(5, 1));
    }

    [Fact]
    public void Moving_KeepsItsLength_AndStopsAtBothEnds()
    {
        var model = Model([new StudioAnnotation { Start = 10, End = 14 }]);

        model.MoveAnnotation(0, 20);
        Assert.Equal((20, 24), (List(model)[0].Start, List(model)[0].End));
        model.MoveAnnotation(0, 100);
        Assert.Equal((56, 60), (List(model)[0].Start, List(model)[0].End));
        model.MoveAnnotation(0, -3);
        Assert.Equal((0, 4), (List(model)[0].Start, List(model)[0].End));
    }

    [Fact]
    public void AnnotationsMayOverlap_AndAnEditLeavesTheOthersAndTheOrderAlone()
    {
        var model = Model([new StudioAnnotation { Id = "a", Start = 1, End = 9 }, new StudioAnnotation { Id = "b", Start = 2, End = 8 }]);

        Assert.Equal(new StudioAnnotationEditResult(true, 1), model.MoveAnnotation(1, 0));

        Assert.Equal(["a", "b"], List(model).Select(annotation => annotation.Id));
        Assert.Equal((1, 9), (List(model)[0].Start, List(model)[0].End));
    }

    [Fact]
    public void TheLayer_MovesOnePlace_AndNotPastEitherEnd()
    {
        var model = Model([new StudioAnnotation { Id = "a", End = 1 }, new StudioAnnotation { Id = "b", End = 1 }]);

        Assert.Equal(new StudioAnnotationEditResult(true, 1), model.MoveAnnotationLayer(0, 1));
        Assert.Equal(["b", "a"], List(model).Select(annotation => annotation.Id));
        Assert.Equal(new StudioAnnotationEditResult(false, 1), model.MoveAnnotationLayer(1, 1));
        Assert.Equal(new StudioAnnotationEditResult(true, 0), model.MoveAnnotationLayer(1, -1));
        Assert.Equal(new StudioAnnotationEditResult(false, 0), model.MoveAnnotationLayer(0, -1));

        model.Undo();
        Assert.Equal(["b", "a"], List(model).Select(annotation => annotation.Id));
    }

    [Fact]
    public void Typing_IsOneUndoStep_UntilItStops()
    {
        var model = Model([new StudioAnnotation { Kind = StudioAnnotationKinds.Text, Text = "x", End = 1 }]);

        model.SetAnnotationText(0, "a");
        model.SetAnnotationText(0, "ab");
        model.SetAnnotationText(0, "abc");
        model.EndAnnotationTyping();
        model.SetAnnotationText(0, "abcd");

        model.Undo();
        Assert.Equal("abc", List(model)[0].Text);
        model.Undo();
        Assert.Equal("x", List(model)[0].Text);
        Assert.False(model.CanUndo);
        model.Redo();
        Assert.Equal("abc", List(model)[0].Text);
    }

    [Fact]
    public void AnotherEdit_EndsTheTypingToo()
    {
        var model = Model([new StudioAnnotation { Kind = StudioAnnotationKinds.Text, Text = "x", End = 1 }]);

        model.SetAnnotationText(0, "a");
        model.SetAnnotationStyle(0, StudioAnnotationStyle.Blue);
        model.SetAnnotationText(0, "ab");

        model.Undo();
        Assert.Equal(("a", StudioAnnotationStyle.Blue), (List(model)[0].Text, List(model)[0].Style));
    }

    [Fact]
    public void ThePointAndTheOffset_StayInRange()
    {
        var model = Model([new StudioAnnotation { End = 1 }]);

        model.SetAnnotationPoint(0, 2, -1);
        model.SetAnnotationOffset(0, 3, -3);

        Assert.Equal((1, 0, 1, -1), (List(model)[0].X, List(model)[0].Y, List(model)[0].OffsetX, List(model)[0].OffsetY));
    }

    [Fact]
    public void ChangingWhatItIsFixedTo_KeepsItWhereItIsInThePicture()
    {
        var zoom = new StudioZoom { Start = 5, End = 15, Scale = 2, Focus = new StudioZoomFocus { X = 0.5, Y = 0.5 }, EaseIn = 0, EaseOut = 0 };
        var model = Model([new StudioAnnotation { End = 20, X = 0.5, Y = 0.25 }], [zoom]);

        // At 10 s the zoom shows the middle half: a place a quarter of the way down the recording
        // is at the top of the picture.
        model.SetAnnotationAttach(0, StudioAnnotationAttach.Frame, 10, null);
        Assert.Equal((StudioAnnotationAttach.Frame, 0.5, 0), (List(model)[0].Attach, List(model)[0].X, List(model)[0].Y));

        model.SetAnnotationAttach(0, StudioAnnotationAttach.Screen, 10, null);
        Assert.Equal((StudioAnnotationAttach.Screen, 0.5, 0.25), (List(model)[0].Attach, List(model)[0].X, List(model)[0].Y));
    }

    [Fact]
    public void ChangingWhatItIsFixedTo_LeavesThePointOfOneOutOfView()
    {
        var zoom = new StudioZoom { Start = 5, End = 15, Scale = 2, Focus = new StudioZoomFocus { X = 0.5, Y = 0.5 }, EaseIn = 0, EaseOut = 0 };
        var model = Model([new StudioAnnotation { End = 20, X = 0.1, Y = 0.1 }], [zoom]);

        model.SetAnnotationAttach(0, StudioAnnotationAttach.Frame, 10, null);

        Assert.Equal((StudioAnnotationAttach.Frame, 0.1, 0.1), (List(model)[0].Attach, List(model)[0].X, List(model)[0].Y));
    }

    [Fact]
    public void Stepping_GoesByTime_AndThenByPlaceInTheList()
    {
        var model = Model(
        [
            new StudioAnnotation { Start = 20, End = 25 },
            new StudioAnnotation { Start = 5, End = 8 },
            new StudioAnnotation { Start = 5, End = 6 },
        ]);

        Assert.Equal(2, model.GetAnnotationIndexAfter(1, 0));
        Assert.Equal(0, model.GetAnnotationIndexAfter(2, 0));
        Assert.Null(model.GetAnnotationIndexAfter(0, 0));
        Assert.Equal(1, model.GetAnnotationIndexBefore(2, 0));
        Assert.Null(model.GetAnnotationIndexBefore(1, 0));

        // With nothing selected: the one at the playhead, or the nearest in that direction.
        Assert.Equal(1, model.GetAnnotationIndexAfter(null, 0));
        Assert.Equal(1, model.GetAnnotationIndexAfter(null, 7));
        Assert.Equal(0, model.GetAnnotationIndexAfter(null, 10));
        Assert.Null(model.GetAnnotationIndexAfter(null, 30));
        Assert.Equal(0, model.GetAnnotationIndexBefore(null, 30));
        Assert.Equal(2, model.GetAnnotationIndexBefore(null, 10));
        Assert.Null(model.GetAnnotationIndexBefore(null, 1));
    }

    [Fact]
    public void FollowingAnAnnotation_GoesByItsId_OrItsPlaceWithoutOne()
    {
        StudioAnnotation[] before = [new StudioAnnotation { Id = "a" }, new StudioAnnotation { Id = "b" }, new StudioAnnotation()];

        Assert.Equal(0, StudioEditorModel.FindAnnotationFollowing(before, 1, [new StudioAnnotation { Id = "b" }]));
        Assert.Null(StudioEditorModel.FindAnnotationFollowing(before, 1, [new StudioAnnotation { Id = "a" }]));
        Assert.Equal(2, StudioEditorModel.FindAnnotationFollowing(before, 2, [new StudioAnnotation(), new StudioAnnotation(), new StudioAnnotation()]));
        Assert.Null(StudioEditorModel.FindAnnotationFollowing(before, 2, [new StudioAnnotation()]));
        Assert.Null(StudioEditorModel.FindAnnotationFollowing(before, 9, before));
    }
}
