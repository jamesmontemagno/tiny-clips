using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>
/// The editor's rules for cuts. The same cases, with the same numbers, are in the Mac's
/// <c>StudioEditorModelTests</c>.
/// </summary>
public sealed class StudioEditorModelCutTests
{
    private const int Precision = 9;

    [Fact]
    public void ACut_StartsAtATimeAndLastsOneSecond_OrUntilTheNextOne()
    {
        var model = new StudioEditorModel(MakeProject());
        Assert.Equal(new StudioCutEditResult(true, 0), model.AddCut(4));
        AssertCuts(model, (4, 5));
        Assert.Equal(9, model.OutputDuration, Precision);
        Assert.True(model.CanUndo);

        // Where a cut already is, that one is the answer and nothing changes.
        Assert.Equal(new StudioCutEditResult(false, 0), model.AddCut(4.5));
        AssertCuts(model, (4, 5));

        // The list stays in time order, and a new cut ends where the next one starts.
        Assert.Equal(new StudioCutEditResult(true, 0), model.AddCut(2));
        Assert.Equal(new StudioCutEditResult(true, 1), model.AddCut(3.5));
        AssertCuts(model, (2, 3), (3.5, 4), (4, 5));
        Assert.Equal(new StudioCutEditResult(false, 1), model.AddCut(3.95));

        // A cut contains its start and not its end, so one can start where another ends.
        Assert.Equal(new StudioCutEditResult(true, 1), model.AddCut(3));
        AssertCuts(model, (2, 3), (3, 3.5), (3.5, 4), (4, 5));

        // Less than 0.1 s before the end of the recording there is no room.
        Assert.Equal(new StudioCutEditResult(false, null), model.AddCut(9.95));
        Assert.False(model.CanAddCut(9.95));
        Assert.True(model.CanAddCut(4.2));
        Assert.True(model.CanAddCut(6));
        Assert.Equal(new StudioCutEditResult(false, null), model.AddCut(double.NaN));
        Assert.False(model.CanAddCut(double.NaN));

        // A time before the recording is its start.
        Assert.Equal(new StudioCutEditResult(true, 0), model.AddCut(-3));
        Assert.Equal(0, model.Project.Edits.Cuts[0].Start, Precision);
        Assert.Equal(1, model.Project.Edits.Cuts[0].End, Precision);
    }

    [Fact]
    public void ACutOrATrim_ThatWouldLeaveNoVideo_IsNotMade()
    {
        // A recording barely longer than a new cut.
        var brief = new StudioEditorModel(MakeProject(duration: 1.05));
        Assert.False(brief.CanAddCut(0));
        Assert.Equal(new StudioCutEditResult(false, null), brief.AddCut(0));
        Assert.Empty(brief.Project.Edits.Cuts);
        Assert.False(brief.CanUndo);

        // What counts is what the trim keeps: here the second from 4 to 5.
        var model = new StudioEditorModel(MakeProject());
        model.SetTrim(4, 5);
        Assert.False(model.CanAddCut(4));
        Assert.Equal(new StudioCutEditResult(false, null), model.AddCut(4));
        Assert.Equal(new StudioCutEditResult(true, 0), model.AddCut(4.5));
        Assert.Equal(0.5, model.OutputDuration, Precision);

        // The start of the cut can come down to 4.1, which leaves 0.1 s, and no further.
        Assert.Equal(new StudioCutEditResult(false, 0), model.SetCutStart(0, 4.05));
        Assert.Equal(4.5, model.Project.Edits.Cuts[0].Start, Precision);
        Assert.Equal(new StudioCutEditResult(true, 0), model.SetCutStart(0, 4.1));
        Assert.Equal(0.1, model.OutputDuration, Precision);

        // Nor can the trim take the rest away.
        model.SetTrimStart(4.05);
        Assert.Equal(4, model.TrimStart, Precision);
        model.SetTrimStart(3);
        Assert.Equal(3, model.TrimStart, Precision);
        Assert.Equal(1.1, model.OutputDuration, Precision);

        // Moving a cut onto all that is left is not made either.
        var moved = new StudioEditorModel(MakeProject(duration: 3) with
        {
            Edits = new StudioEdits { Cuts = [new StudioTimeRange { Start = 0, End = 2 }] },
        });
        Assert.Equal(new StudioCutEditResult(false, 0), moved.SetCutEnd(0, 2.95));
        Assert.Equal(new StudioCutEditResult(true, 0), moved.SetCutEnd(0, 2.9));
    }

    [Fact]
    public void TheEndsOfACut_StayClearOfItsNeighbors()
    {
        var model = new StudioEditorModel(ThreeCuts());

        Assert.Equal(new StudioCutEditResult(true, 1), model.SetCutStart(1, 4));
        AssertCuts(model, (2, 3), (4, 6), (8, 9));

        // The start stops at the end of the cut before, and 0.1 s before its own end.
        model.SetCutStart(1, 1);
        AssertCuts(model, (2, 3), (3, 6), (8, 9));
        model.SetCutStart(1, 5.95);
        AssertCuts(model, (2, 3), (5.9, 6), (8, 9));

        // The end stops at the start of the next cut, and 0.1 s after its own start.
        Assert.Equal(new StudioCutEditResult(true, 1), model.SetCutEnd(1, 7));
        model.SetCutEnd(1, 9.5);
        AssertCuts(model, (2, 3), (5.9, 8), (8, 9));
        model.SetCutEnd(1, 5);
        AssertCuts(model, (2, 3), (5.9, 6), (8, 9));

        // The first and the last stop at the ends of the recording.
        model.SetCutStart(0, -1);
        model.SetCutEnd(2, 12);
        AssertCuts(model, (0, 3), (5.9, 6), (8, 10));

        // Nothing to change, nothing that is a number, no such cut.
        var undoDepth = UndoDepth(model);
        Assert.Equal(new StudioCutEditResult(false, 1), model.SetCutStart(1, 5.9));
        Assert.Equal(new StudioCutEditResult(false, 1), model.SetCutEnd(1, double.NaN));
        Assert.Equal(new StudioCutEditResult(false, 1), model.SetCutStart(1, double.PositiveInfinity));
        Assert.Equal(new StudioCutEditResult(false, null), model.SetCutStart(3, 1));
        Assert.Equal(new StudioCutEditResult(false, null), model.SetCutEnd(-1, 1));
        Assert.Equal(undoDepth, UndoDepth(model));

        // A cut from a file that is shorter than 0.1 s and sits against the one before: the cut
        // before decides, so its start does not move into it.
        var tight = new StudioEditorModel(WithCuts((2, 3), (3, 3.05)));
        Assert.Equal(new StudioCutEditResult(false, 1), tight.SetCutStart(1, 2.5));
        AssertCuts(tight, (2, 3), (3, 3.05));
    }

    [Fact]
    public void MovingACut_KeepsItsLengthBetweenItsNeighbors()
    {
        var model = new StudioEditorModel(ThreeCuts());

        Assert.Equal(new StudioCutEditResult(true, 1), model.MoveCut(1, 6.5));
        AssertCuts(model, (2, 3), (6.5, 7.5), (8, 9));

        // Against the next cut, and against the one before.
        model.MoveCut(1, 7.8);
        AssertCuts(model, (2, 3), (7, 8), (8, 9));
        model.MoveCut(1, 0);
        AssertCuts(model, (2, 3), (3, 4), (8, 9));

        // Against the ends of the recording.
        model.MoveCut(0, -5);
        model.MoveCut(2, 20);
        AssertCuts(model, (0, 1), (3, 4), (9, 10));

        Assert.Equal(new StudioCutEditResult(false, 1), model.MoveCut(1, 3));
        Assert.Equal(new StudioCutEditResult(false, 1), model.MoveCut(1, double.NaN));
        Assert.Equal(new StudioCutEditResult(false, null), model.MoveCut(3, 1));
    }

    [Fact]
    public void DeletingACut_PutsItsStretchBack()
    {
        var model = new StudioEditorModel(ThreeCuts());
        Assert.Equal(7, model.OutputDuration, Precision);

        Assert.Equal(new StudioCutEditResult(true, null), model.RemoveCut(1));
        AssertCuts(model, (2, 3), (8, 9));
        Assert.Equal(8, model.OutputDuration, Precision);

        model.Undo();
        AssertCuts(model, (2, 3), (5, 6), (8, 9));
        model.Redo();
        AssertCuts(model, (2, 3), (8, 9));

        Assert.Equal(new StudioCutEditResult(false, null), model.RemoveCut(2));
        Assert.Equal(new StudioCutEditResult(false, null), model.RemoveCut(-1));
    }

    [Fact]
    public void Opening_PutsCutsInOrderAndJoinsThoseThatOverlap()
    {
        var model = new StudioEditorModel(MakeProject() with
        {
            Edits = new StudioEdits
            {
                Cuts =
                [
                    new StudioTimeRange { Start = 5, End = 6 },
                    new StudioTimeRange { Start = 2, End = 3 },
                    new StudioTimeRange { Start = 2.5, End = 4 },
                    new StudioTimeRange { Start = 9, End = 12 },
                    new StudioTimeRange { Start = 11, End = 11.5 },
                    new StudioTimeRange { Start = double.NaN, End = 3 },
                    new StudioTimeRange { Start = 7, End = 6.5 },
                    new StudioTimeRange { Start = 4, End = 4.5 },
                    new StudioTimeRange { Start = 5.2, End = 5.5 },
                    new StudioTimeRange { Start = double.NegativeInfinity, End = 1 },
                ],
            },
        });

        // Two that overlap are one, and so is one inside another. Two that touch stay two, and
        // play as one. One that reaches past the recording stops at its ends, and one that is not
        // a number is dropped.
        AssertCuts(model, (0, 1), (2, 4), (4, 4.5), (5, 6), (9, 10));
        Assert.False(model.CanUndo);
        Assert.Equal(4.5, model.OutputDuration, Precision);
    }

    [Fact]
    public void Playback_JumpsOverCuts_AndEndsWhereTheVideoDoes()
    {
        var model = new StudioEditorModel(WithCuts((2, 3), (9, 10)));

        // A cut that runs up to the end of the recording ends the video where it starts.
        Assert.Equal(0, model.PlaybackStart, Precision);
        Assert.Equal(9, model.PlaybackEnd, Precision);
        Assert.False(model.IsAtPlaybackEnd(8.99));
        Assert.True(model.IsAtPlaybackEnd(9));

        // Inside a cut the next thing to show is its end. A cut contains its start and not its end.
        Assert.Null(model.GetCutSkipTarget(1.99));
        Assert.Equal(3, Assert.NotNull(model.GetCutSkipTarget(2)), Precision);
        Assert.Equal(3, Assert.NotNull(model.GetCutSkipTarget(2.5)), Precision);
        Assert.Null(model.GetCutSkipTarget(3));
        Assert.Null(model.GetCutSkipTarget(9.5));

        // Play starts where the playhead is, after the cut it is in, or over again at the end.
        Assert.Equal(1, model.GetPlaybackStart(1), Precision);
        Assert.Equal(3, model.GetPlaybackStart(2.5), Precision);
        Assert.Equal(0, model.GetPlaybackStart(8.99), Precision);
        Assert.Equal(0, model.GetPlaybackStart(9.4), Precision);

        // A cut at the start of the recording starts the video at its end.
        var late = new StudioEditorModel(WithCuts((0, 1.5)));
        Assert.Equal(1.5, late.PlaybackStart, Precision);
        Assert.Equal(1.5, late.GetPlaybackStart(0.5), Precision);
        Assert.Null(late.GetCutSkipTarget(0.5));

        // Cuts count inside the trim only.
        var trimmed = new StudioEditorModel(WithCuts((0.5, 2), (7, 9)));
        trimmed.SetTrim(1, 8);
        Assert.Equal(2, trimmed.PlaybackStart, Precision);
        Assert.Equal(7, trimmed.PlaybackEnd, Precision);
        Assert.Equal(5, trimmed.OutputDuration, Precision);
        Assert.Equal(2, trimmed.GetOutputTime(4), Precision);
        Assert.Null(trimmed.GetCutSkipTarget(1.5));
        Assert.Null(trimmed.GetCutSkipTarget(7.5));

        // Two cuts that touch are jumped as one.
        var touching = new StudioEditorModel(WithCuts((2, 3), (3, 4)));
        Assert.Equal(4, Assert.NotNull(touching.GetCutSkipTarget(2.2)), Precision);

        // A file in which the cuts leave nothing: the video starts and ends in one place.
        var nothing = new StudioEditorModel(WithCuts((0, 10)));
        Assert.Equal(0, nothing.OutputDuration, Precision);
        Assert.Equal(0, nothing.PlaybackStart, Precision);
        Assert.Equal(0, nothing.PlaybackEnd, Precision);
        Assert.True(nothing.IsAtPlaybackEnd(0));
    }

    [Fact]
    public void WithoutCuts_PlaybackStartsAndEndsWithTheTrim()
    {
        var model = new StudioEditorModel(MakeProject());
        model.SetTrim(2, 8);
        Assert.Equal(2, model.PlaybackStart, Precision);
        Assert.Equal(8, model.PlaybackEnd, Precision);
        Assert.Equal(2, model.GetPlaybackStart(1), Precision);
        Assert.Equal(5, model.GetPlaybackStart(5), Precision);
        Assert.Equal(2, model.GetPlaybackStart(8), Precision);
        Assert.True(model.IsAtPlaybackEnd(8));
        Assert.False(model.IsAtPlaybackEnd(7.9));
        Assert.Null(model.GetCutSkipTarget(5));
        Assert.Null(model.GetCutIndexAt(5));
    }

    [Fact]
    public void SteppingThroughCuts_AndFollowingOneThroughAnEditThatDidNotSay()
    {
        var model = new StudioEditorModel(ThreeCuts());
        Assert.Equal(1, model.GetCutIndexAt(5));
        Assert.Equal(1, model.GetCutIndexAt(5.99));
        Assert.Null(model.GetCutIndexAt(6));
        Assert.Null(model.GetCutIndexAt(4.99));

        // With nothing selected: the cut at the playhead, or the nearest one on that side.
        Assert.Equal(0, model.GetCutIndexAfter(null, 0));
        Assert.Equal(0, model.GetCutIndexAfter(null, 2.5));
        Assert.Equal(1, model.GetCutIndexAfter(null, 3));
        Assert.Null(model.GetCutIndexAfter(null, 9));
        Assert.Equal(2, model.GetCutIndexBefore(null, 10));
        Assert.Equal(1, model.GetCutIndexBefore(null, 5.5));
        Assert.Null(model.GetCutIndexBefore(null, 1));

        // With one selected: its neighbors.
        Assert.Equal(1, model.GetCutIndexAfter(0, 9));
        Assert.Null(model.GetCutIndexAfter(2, 0));
        Assert.Equal(0, model.GetCutIndexBefore(1, 9));
        Assert.Null(model.GetCutIndexBefore(0, 9));

        StudioTimeRange[] before = [Cut(2, 3), Cut(5, 6)];

        // Only that cut differs: it is the same cut, changed.
        Assert.Equal(1, StudioEditorModel.FindCutFollowing(before, 1, [Cut(2, 3), Cut(5.5, 7)]));

        // Otherwise the one that shares the most time with it, or none.
        Assert.Equal(0, StudioEditorModel.FindCutFollowing(before, 1, [Cut(5, 6)]));
        Assert.Equal(2, StudioEditorModel.FindCutFollowing(before, 1, [Cut(1, 2), Cut(4.5, 5.2), Cut(5.2, 6.5)]));
        Assert.Equal(0, StudioEditorModel.FindCutFollowing(before, 1, [Cut(5.2, 6.5), Cut(8, 9)]));
        Assert.Null(StudioEditorModel.FindCutFollowing(before, 0, [Cut(5, 6)]));
        Assert.Null(StudioEditorModel.FindCutFollowing(before, 2, before));
        Assert.Null(StudioEditorModel.FindCutFollowing(before, -1, before));
    }

    [Fact]
    public void ACut_MovesNothingElse()
    {
        var project = MakeProject() with
        {
            Scenes =
            [
                new StudioScene { Start = 0, Layout = StudioLayout.Bubble },
                new StudioScene { Start = 4, Layout = StudioLayout.SideBySide },
            ],
            Zooms = [new StudioZoom { Start = 3, End = 6 }],
        };
        var model = new StudioEditorModel(project);
        var scenes = model.Project.Scenes;
        var zooms = model.Project.Zooms;

        Assert.True(model.AddCut(3.5).Changed);

        Assert.Same(scenes, model.Project.Scenes);
        Assert.Same(zooms, model.Project.Zooms);
        Assert.Equal(0, model.TrimStart, Precision);
        Assert.Equal(10, model.TrimEnd, Precision);

        // The video is a second shorter, and what came after the cut is a second earlier in it.
        Assert.Equal(9, model.OutputDuration, Precision);
        Assert.Equal(7, model.GetOutputTime(8), Precision);
        Assert.Equal(3.5, model.GetOutputTime(4), Precision);
    }

    [Fact]
    public void CutText_NamesTheTimes()
    {
        Assert.Equal("Cut, 12.0 to 16.5 seconds", StudioEditorText.GetCutDescription(Cut(12, 16.5)));
        Assert.Equal("12.0 to 16.5 seconds", StudioEditorText.GetCutRangeText(Cut(12, 16.5)));
        Assert.Equal("4.5 seconds long", StudioEditorText.GetCutLengthText(Cut(12, 16.5)));
        Assert.Equal("0.0 seconds long", StudioEditorText.GetCutLengthText(Cut(3, double.NaN)));
        Assert.Equal("Cut 2 of 3", StudioEditorText.GetCutPositionText(1, 3));
        Assert.Equal("Cut 2 of 3, 12.0 to 16.5 seconds", StudioEditorText.GetCutStepText(1, 3, Cut(12, 16.5)));
    }

    // Helpers

    private static void AssertCuts(StudioEditorModel model, params (double Start, double End)[] expected)
    {
        var cuts = model.Project.Edits.Cuts;
        Assert.Equal(expected.Length, cuts.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Start, cuts[i].Start, Precision);
            Assert.Equal(expected[i].End, cuts[i].End, Precision);
        }
    }

    // How many times Undo can be pressed. Everything undone is redone before it returns.
    private static int UndoDepth(StudioEditorModel model)
    {
        var depth = 0;
        while (model.CanUndo)
        {
            model.Undo();
            depth++;
        }

        for (var i = 0; i < depth; i++)
        {
            model.Redo();
        }

        return depth;
    }

    private static StudioTimeRange Cut(double start, double end) => new() { Start = start, End = end };

    /// <summary>Cuts from 2 to 3, 5 to 6 and 8 to 9 seconds, in a recording 10 s long.</summary>
    private static StudioProject ThreeCuts() => WithCuts((2, 3), (5, 6), (8, 9));

    private static StudioProject WithCuts(params (double Start, double End)[] cuts) => MakeProject() with
    {
        Edits = new StudioEdits { Cuts = [.. cuts.Select(cut => Cut(cut.Start, cut.End))] },
    };

    private static StudioProject MakeProject(bool camera = true, double duration = 10) => new()
    {
        Id = "3f0013cf-ba10-4453-af91-792b7882dae6",
        Name = "Test",
        Sources = new StudioSources
        {
            Screen = new StudioScreenSource { Width = 1920, Height = 1080, FrameRate = 30, Duration = duration },
            Camera = camera ? new StudioCameraSource { Width = 1280, Height = 720, Duration = duration } : null,
        },
        Scenes = [new StudioScene { Start = 0, Layout = camera ? StudioLayout.Bubble : StudioLayout.Screen }],
    };
}
