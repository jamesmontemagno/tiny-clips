using System.Text.Json;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>
/// The editor's rules for speed changes. The same cases, with the same numbers, are in the Mac's
/// <c>StudioEditorModelTests</c>.
/// </summary>
public sealed class StudioEditorModelSpeedTests
{
    private const int Precision = 9;

    [Fact]
    public void ASpeedChange_StartsAtATime_PlaysTwiceAsFast_AndCoversTwoSeconds_OrUntilTheNextOne()
    {
        var model = new StudioEditorModel(MakeProject());
        Assert.Equal(new StudioSpeedEditResult(true, 0), model.AddSpeed(4));
        AssertSpeed(model, (4, 6, 2));
        Assert.Equal(9, model.OutputDuration, Precision);
        Assert.True(model.CanUndo);

        // Where a speed change already is, that one is the answer and nothing changes.
        Assert.Equal(new StudioSpeedEditResult(false, 0), model.AddSpeed(5));
        AssertSpeed(model, (4, 6, 2));

        // The list stays in time order, and a new one ends where the next one starts.
        Assert.Equal(new StudioSpeedEditResult(true, 0), model.AddSpeed(1));
        Assert.Equal(new StudioSpeedEditResult(true, 1), model.AddSpeed(3.5));
        AssertSpeed(model, (1, 3, 2), (3.5, 4, 2), (4, 6, 2));
        Assert.Equal(new StudioSpeedEditResult(false, 1), model.AddSpeed(3.95));

        // A speed change contains its start and not its end, so one can start where another ends.
        Assert.Equal(new StudioSpeedEditResult(true, 1), model.AddSpeed(3));
        AssertSpeed(model, (1, 3, 2), (3, 3.5, 2), (3.5, 4, 2), (4, 6, 2));

        // Less than 0.1 s before the end of the recording there is no room.
        Assert.Equal(new StudioSpeedEditResult(false, null), model.AddSpeed(9.95));
        Assert.False(model.CanAddSpeed(9.95));
        Assert.True(model.CanAddSpeed(4.2));
        Assert.True(model.CanAddSpeed(7));
        Assert.Equal(new StudioSpeedEditResult(false, null), model.AddSpeed(double.NaN));
        Assert.False(model.CanAddSpeed(double.NaN));

        // A time before the recording is its start.
        Assert.Equal(new StudioSpeedEditResult(true, 0), model.AddSpeed(-3));
        Assert.Equal(0, model.Project.Edits.Speed[0].Start, Precision);
        Assert.Equal(1, model.Project.Edits.Speed[0].End, Precision);
    }

    [Fact]
    public void ASpeedChange_ChangesHowLongTheVideoIs_AndMovesNothingElse()
    {
        var project = MakeProject() with
        {
            Scenes =
            [
                new StudioScene { Start = 0, Layout = StudioLayout.Bubble },
                new StudioScene { Start = 4, Layout = StudioLayout.SideBySide },
            ],
            Zooms = [new StudioZoom { Start = 3, End = 6 }],
            Edits = new StudioEdits { Cuts = [new StudioTimeRange { Start = 7, End = 7.5 }] },
        };
        var model = new StudioEditorModel(project);
        var scenes = model.Project.Scenes;
        var zooms = model.Project.Zooms;
        var cuts = model.Project.Edits.Cuts;

        Assert.True(model.AddSpeed(3.5).Changed);

        Assert.Same(scenes, model.Project.Scenes);
        Assert.Same(zooms, model.Project.Zooms);
        Assert.Same(cuts, model.Project.Edits.Cuts);
        Assert.Equal(0, model.TrimStart, Precision);
        Assert.Equal(10, model.TrimEnd, Precision);

        // The two seconds from 3.5 take one, so the video is a second shorter, and what came
        // after is a second earlier in it. Inside, the video's time passes half as fast.
        Assert.Equal(8.5, model.OutputDuration, Precision);
        Assert.Equal(5, model.GetOutputTime(6), Precision);
        Assert.Equal(4, model.GetOutputTime(4.5), Precision);
        Assert.Equal(4.5, model.GetSourceTime(4), Precision);
    }

    [Fact]
    public void Playback_GoesAtTheRateOfTheStretchItIsIn()
    {
        var model = new StudioEditorModel(ThreeSpeedChanges());
        Assert.Equal(1, model.GetPlaybackRate(1), Precision);
        Assert.Equal(2, model.GetPlaybackRate(2), Precision);
        Assert.Equal(2, model.GetPlaybackRate(2.99), Precision);
        Assert.Equal(1, model.GetPlaybackRate(3), Precision);
        Assert.Equal(4, model.GetPlaybackRate(5.5), Precision);
        Assert.Equal(0.5, model.GetPlaybackRate(8), Precision);
        Assert.Equal(1, model.GetPlaybackRate(9), Precision);
        Assert.Equal(1, model.GetPlaybackRate(double.NaN), Precision);

        // What the video leaves out has no rate: playback does not stay there.
        model.SetTrim(2.5, 8.5);
        model.AddCut(5.2);
        Assert.Equal(1, model.GetPlaybackRate(2.2), Precision);
        Assert.Equal(2, model.GetPlaybackRate(2.5), Precision);
        Assert.Equal(4, model.GetPlaybackRate(5.1), Precision);
        Assert.Equal(1, model.GetPlaybackRate(5.5), Precision);
        Assert.Equal(0.5, model.GetPlaybackRate(8.4), Precision);
        Assert.Equal(1, model.GetPlaybackRate(8.5), Precision);
    }

    [Fact]
    public void TheRateOfASpeedChange_IsKeptWithinItsLimits_AndOneIsNoRate()
    {
        var model = new StudioEditorModel(WithSpeed((2, 6, 2)));
        Assert.Equal(8, model.OutputDuration, Precision);

        Assert.Equal(new StudioSpeedEditResult(true, 0), model.SetSpeedRate(0, 4));
        AssertSpeed(model, (2, 6, 4));
        Assert.Equal(7, model.OutputDuration, Precision);

        // The rate it has already changes nothing. A rate of 1 is no speed change, and neither is
        // one that is no rate at all. None of them leaves anything to undo.
        var undoDepth = UndoDepth(model);
        Assert.Equal(new StudioSpeedEditResult(false, 0), model.SetSpeedRate(0, 4));
        Assert.Equal(new StudioSpeedEditResult(false, 0), model.SetSpeedRate(0, 1));
        Assert.Equal(new StudioSpeedEditResult(false, 0), model.SetSpeedRate(0, 0));
        Assert.Equal(new StudioSpeedEditResult(false, 0), model.SetSpeedRate(0, -2));
        Assert.Equal(new StudioSpeedEditResult(false, 0), model.SetSpeedRate(0, double.NaN));
        Assert.Equal(new StudioSpeedEditResult(false, 0), model.SetSpeedRate(0, double.PositiveInfinity));
        AssertSpeed(model, (2, 6, 4));
        Assert.Equal(undoDepth, UndoDepth(model));

        // A rate past a limit is the limit.
        Assert.Equal(new StudioSpeedEditResult(true, 0), model.SetSpeedRate(0, 100));
        AssertSpeed(model, (2, 6, 8));
        Assert.Equal(new StudioSpeedEditResult(false, 0), model.SetSpeedRate(0, 16));
        Assert.Equal(new StudioSpeedEditResult(true, 0), model.SetSpeedRate(0, 0.01));
        AssertSpeed(model, (2, 6, 0.25));
        Assert.Equal(22, model.OutputDuration, Precision);

        Assert.Equal(new StudioSpeedEditResult(false, null), model.SetSpeedRate(1, 2));
        Assert.Equal(new StudioSpeedEditResult(false, null), model.SetSpeedRate(-1, 2));

        // Every rate the editor offers can be chosen, a new speed change has one of them, and
        // none of them is 1.
        Assert.Equal(new[] { 0.25, 0.5, 1.5, 2, 4, 8 }, StudioEditorModel.SpeedRates);
        Assert.Contains(StudioEditorModel.NewSpeedRate, StudioEditorModel.SpeedRates);
        foreach (var rate in StudioEditorModel.SpeedRates.Reverse())
        {
            Assert.Equal(new StudioSpeedEditResult(true, 0), model.SetSpeedRate(0, rate));
            Assert.Equal(rate, model.Project.Edits.Speed[0].Rate, Precision);
        }
    }

    [Fact]
    public void ASpeedChangeOrATrim_ThatWouldLeaveTooLittleVideo_IsNotMade()
    {
        // Twice as fast, a recording of 0.15 s would last 0.075 s.
        var brief = new StudioEditorModel(MakeProject(duration: 0.15));
        Assert.False(brief.CanAddSpeed(0));
        Assert.Equal(new StudioSpeedEditResult(false, null), brief.AddSpeed(0));
        Assert.Empty(brief.Project.Edits.Speed);
        Assert.False(brief.CanUndo);

        // What counts is how long the video is: here half a second of the recording.
        var model = new StudioEditorModel(WithSpeed((4, 5, 2)));
        model.SetTrim(4, 4.5);
        Assert.Equal(0.25, model.OutputDuration, Precision);
        Assert.Equal(new StudioSpeedEditResult(false, 0), model.SetSpeedRate(0, 8));
        AssertSpeed(model, (4, 5, 2));
        Assert.Equal(new StudioSpeedEditResult(true, 0), model.SetSpeedRate(0, 4));
        Assert.Equal(0.125, model.OutputDuration, Precision);

        // Nor can the trim take more of it away: four times as fast, 0.3 s would last 0.075 s.
        model.SetTrimEnd(4.3);
        Assert.Equal(4.5, model.TrimEnd, Precision);
        model.SetTrimStart(4.2);
        Assert.Equal(4, model.TrimStart, Precision);
        model.SetTrimEnd(4.4);
        Assert.Equal(4.4, model.TrimEnd, Precision);
        Assert.Equal(0.1, model.OutputDuration, Precision);

        // A faster stretch moved onto all that is kept is not made either.
        var moved = new StudioEditorModel(WithSpeed((6, 8, 8)));
        moved.SetTrim(4, 4.5);
        Assert.Equal(0.5, moved.OutputDuration, Precision);
        Assert.Equal(new StudioSpeedEditResult(false, 0), moved.MoveSpeed(0, 4));
        AssertSpeed(moved, (6, 8, 8));
        Assert.Equal(new StudioSpeedEditResult(false, 0), moved.SetSpeedStart(0, 4));
        Assert.Equal(new StudioSpeedEditResult(true, 0), moved.MoveSpeed(0, 4.3));
        Assert.Equal(0.325, moved.OutputDuration, Precision);

        // Taking a slower stretch away makes the video shorter. Here a cut leaves 0.05 s of the
        // recording, which lasts 0.2 s at a quarter of the speed.
        var slow = new StudioEditorModel(MakeProject() with
        {
            Edits = new StudioEdits
            {
                TrimStart = 4,
                TrimEnd = 5,
                Cuts = [new StudioTimeRange { Start = 4.05, End = 5 }],
                Speed = [Speed(4, 4.05, 0.25)],
            },
        });
        Assert.Equal(0.2, slow.OutputDuration, Precision);
        Assert.Equal(new StudioSpeedEditResult(false, 0), slow.RemoveSpeed(0));
        Assert.Single(slow.Project.Edits.Speed);
        Assert.Equal(new StudioSpeedEditResult(false, 0), slow.SetSpeedRate(0, 2));
        Assert.Equal(new StudioSpeedEditResult(true, 0), slow.SetSpeedRate(0, 0.5));
        Assert.Equal(0.1, slow.OutputDuration, Precision);
        Assert.False(slow.CanRedo);
    }

    [Fact]
    public void TheEndsOfASpeedChange_StayClearOfItsNeighbors()
    {
        var model = new StudioEditorModel(ThreeSpeedChanges());

        Assert.Equal(new StudioSpeedEditResult(true, 1), model.SetSpeedStart(1, 4));
        AssertSpeed(model, (2, 3, 2), (4, 6, 4), (8, 9, 0.5));

        // The start stops at the end of the one before, and 0.1 s before its own end.
        model.SetSpeedStart(1, 1);
        AssertSpeed(model, (2, 3, 2), (3, 6, 4), (8, 9, 0.5));
        model.SetSpeedStart(1, 5.95);
        AssertSpeed(model, (2, 3, 2), (5.9, 6, 4), (8, 9, 0.5));

        // The end stops at the start of the next one, and 0.1 s after its own start.
        Assert.Equal(new StudioSpeedEditResult(true, 1), model.SetSpeedEnd(1, 7));
        model.SetSpeedEnd(1, 9.5);
        AssertSpeed(model, (2, 3, 2), (5.9, 8, 4), (8, 9, 0.5));
        model.SetSpeedEnd(1, 5);
        AssertSpeed(model, (2, 3, 2), (5.9, 6, 4), (8, 9, 0.5));

        // The first and the last stop at the ends of the recording.
        model.SetSpeedStart(0, -1);
        model.SetSpeedEnd(2, 12);
        AssertSpeed(model, (0, 3, 2), (5.9, 6, 4), (8, 10, 0.5));

        // Nothing to change, nothing that is a number, no such speed change.
        var undoDepth = UndoDepth(model);
        Assert.Equal(new StudioSpeedEditResult(false, 1), model.SetSpeedStart(1, 5.9));
        Assert.Equal(new StudioSpeedEditResult(false, 1), model.SetSpeedEnd(1, double.NaN));
        Assert.Equal(new StudioSpeedEditResult(false, 1), model.SetSpeedStart(1, double.PositiveInfinity));
        Assert.Equal(new StudioSpeedEditResult(false, null), model.SetSpeedStart(3, 1));
        Assert.Equal(new StudioSpeedEditResult(false, null), model.SetSpeedEnd(-1, 1));
        Assert.Equal(undoDepth, UndoDepth(model));

        // One from a file that is shorter than 0.1 s and sits against the one before: the one
        // before decides, so its start does not move into it.
        var tight = new StudioEditorModel(WithSpeed((2, 3, 2), (3, 3.05, 4)));
        Assert.Equal(new StudioSpeedEditResult(false, 1), tight.SetSpeedStart(1, 2.5));
        AssertSpeed(tight, (2, 3, 2), (3, 3.05, 4));
    }

    [Fact]
    public void MovingASpeedChange_KeepsItsLengthAndItsRate_BetweenItsNeighbors()
    {
        var model = new StudioEditorModel(ThreeSpeedChanges());

        Assert.Equal(new StudioSpeedEditResult(true, 1), model.MoveSpeed(1, 6.5));
        AssertSpeed(model, (2, 3, 2), (6.5, 7.5, 4), (8, 9, 0.5));

        // Against the next one, and against the one before.
        model.MoveSpeed(1, 7.8);
        AssertSpeed(model, (2, 3, 2), (7, 8, 4), (8, 9, 0.5));
        model.MoveSpeed(1, 0);
        AssertSpeed(model, (2, 3, 2), (3, 4, 4), (8, 9, 0.5));

        // Against the ends of the recording.
        model.MoveSpeed(0, -5);
        model.MoveSpeed(2, 20);
        AssertSpeed(model, (0, 1, 2), (3, 4, 4), (9, 10, 0.5));

        Assert.Equal(new StudioSpeedEditResult(false, 1), model.MoveSpeed(1, 3));
        Assert.Equal(new StudioSpeedEditResult(false, 1), model.MoveSpeed(1, double.NaN));
        Assert.Equal(new StudioSpeedEditResult(false, null), model.MoveSpeed(3, 1));
    }

    [Fact]
    public void DeletingASpeedChange_PutsItsStretchBackAtTheRecordingsOwnSpeed()
    {
        var model = new StudioEditorModel(ThreeSpeedChanges());
        Assert.Equal(9.75, model.OutputDuration, Precision);

        Assert.Equal(new StudioSpeedEditResult(true, null), model.RemoveSpeed(1));
        AssertSpeed(model, (2, 3, 2), (8, 9, 0.5));
        Assert.Equal(10.5, model.OutputDuration, Precision);

        model.Undo();
        AssertSpeed(model, (2, 3, 2), (5, 6, 4), (8, 9, 0.5));
        model.Redo();
        AssertSpeed(model, (2, 3, 2), (8, 9, 0.5));

        Assert.Equal(new StudioSpeedEditResult(false, null), model.RemoveSpeed(2));
        Assert.Equal(new StudioSpeedEditResult(false, null), model.RemoveSpeed(-1));
    }

    [Fact]
    public void Opening_PutsSpeedChangesInOrderAndClearOfEachOther_AndPlaysAsBefore()
    {
        var edits = new StudioEdits
        {
            Speed =
            [
                Speed(8, 12, 2),
                Speed(4, 9, 4),
                Speed(5, 7, 8),
                Speed(1, 2, 16) with { ExtensionData = new() { ["label"] = JsonDocument.Parse("\"intro\"").RootElement.Clone() } },
                Speed(2.5, 3, 1),
                Speed(3, 3.5, 0),
                Speed(double.NaN, 3, 2),
                Speed(7, 6.5, 2),
                Speed(-2, 0.5, 0.5),
                Speed(0.25, 0.75, 3),
                Speed(11, 12, 2),
                Speed(13, 14, 2),
            ],
        };
        var model = new StudioEditorModel(MakeProject() with { Edits = edits });

        // Where two overlap, the one that starts first in the recording stays as it is and the
        // other starts where it ends, or is dropped when nothing of it is left. A rate past a
        // limit is the limit, and an entry with a rate of 1 or of 0, with a time that is not a
        // number, or that ends before it starts is none. What reaches past the recording stops at
        // its ends, and what lies outside it is dropped.
        AssertSpeed(model, (0, 0.5, 0.5), (0.5, 0.75, 3), (1, 2, 8), (4, 9, 4), (9, 10, 2));
        Assert.False(model.CanUndo);
        Assert.True(model.Project.Edits.Speed[2].ExtensionData.ContainsKey("label"));

        // The video plays as the file said, with any trim and any cut.
        Assert.Equal(new StudioTimeMap(10, edits).Pieces, model.TimeMap.Pieces);
        var trimmed = edits with { TrimStart = 0.3, TrimEnd = 9.5, Cuts = [new StudioTimeRange { Start = 1.5, End = 4.5 }] };
        Assert.Equal(
            new StudioTimeMap(10, trimmed).Pieces,
            new StudioEditorModel(MakeProject() with { Edits = trimmed }).TimeMap.Pieces);
        Assert.Equal(125.0 / 24, model.OutputDuration, Precision);
    }

    [Fact]
    public void SteppingThroughSpeedChanges_AndFollowingOneThroughAnEditThatDidNotSay()
    {
        var model = new StudioEditorModel(ThreeSpeedChanges());
        Assert.Equal(1, model.GetSpeedIndexAt(5));
        Assert.Equal(1, model.GetSpeedIndexAt(5.99));
        Assert.Null(model.GetSpeedIndexAt(6));
        Assert.Null(model.GetSpeedIndexAt(4.99));

        // With nothing selected: the one at the playhead, or the nearest one on that side.
        Assert.Equal(0, model.GetSpeedIndexAfter(null, 0));
        Assert.Equal(0, model.GetSpeedIndexAfter(null, 2.5));
        Assert.Equal(1, model.GetSpeedIndexAfter(null, 3));
        Assert.Null(model.GetSpeedIndexAfter(null, 9));
        Assert.Equal(2, model.GetSpeedIndexBefore(null, 10));
        Assert.Equal(1, model.GetSpeedIndexBefore(null, 5.5));
        Assert.Null(model.GetSpeedIndexBefore(null, 1));

        // With one selected: its neighbors.
        Assert.Equal(1, model.GetSpeedIndexAfter(0, 9));
        Assert.Null(model.GetSpeedIndexAfter(2, 0));
        Assert.Equal(0, model.GetSpeedIndexBefore(1, 9));
        Assert.Null(model.GetSpeedIndexBefore(0, 9));

        StudioSpeedRange[] before = [Speed(2, 3, 2), Speed(5, 6, 2)];

        // Only that one differs, in its times or in its rate: it is the same one, changed.
        Assert.Equal(1, StudioEditorModel.FindSpeedFollowing(before, 1, [Speed(2, 3, 2), Speed(5.5, 7, 2)]));
        Assert.Equal(1, StudioEditorModel.FindSpeedFollowing(before, 1, [Speed(2, 3, 2), Speed(5, 6, 4)]));
        Assert.Equal(0, StudioEditorModel.FindSpeedFollowing(before, 0, [Speed(7, 8, 2), Speed(5, 6, 2)]));

        // Otherwise the one that shares the most time with it, or none. Another one whose rate
        // changed is another change.
        Assert.Null(StudioEditorModel.FindSpeedFollowing(before, 0, [Speed(7, 8, 2), Speed(5, 6, 4)]));
        Assert.Equal(0, StudioEditorModel.FindSpeedFollowing(before, 1, [Speed(5, 6, 2)]));
        Assert.Equal(2, StudioEditorModel.FindSpeedFollowing(before, 1, [Speed(1, 2, 2), Speed(4.5, 5.2, 2), Speed(5.2, 6.5, 2)]));
        Assert.Equal(0, StudioEditorModel.FindSpeedFollowing(before, 1, [Speed(5.2, 6.5, 2), Speed(8, 9, 2)]));
        Assert.Null(StudioEditorModel.FindSpeedFollowing(before, 0, [Speed(5, 6, 2)]));
        Assert.Null(StudioEditorModel.FindSpeedFollowing(before, 2, before));
        Assert.Null(StudioEditorModel.FindSpeedFollowing(before, -1, before));
    }

    [Fact]
    public void SpeedText_NamesTheRateAndTheTimes()
    {
        Assert.Equal("Speed 2×, 12.0 to 16.5 seconds", StudioEditorText.GetSpeedDescription(Speed(12, 16.5, 2)));
        Assert.Equal("Speed 0.25×, 0.0 to 3.0 seconds", StudioEditorText.GetSpeedDescription(Speed(0, 3, 0.25)));
        Assert.Equal("12.0 to 16.5 seconds", StudioEditorText.GetSpeedRangeText(Speed(12, 16.5, 2)));
        Assert.Equal("Speed change 2 of 3", StudioEditorText.GetSpeedPositionText(1, 3));
        Assert.Equal("Speed change 2 of 3, 2×, 12.0 to 16.5 seconds", StudioEditorText.GetSpeedStepText(1, 3, Speed(12, 16.5, 2)));

        // The rate is the one that plays: inside the limits, with two decimals at most.
        Assert.Equal("2×", StudioEditorText.GetSpeedRateText(2));
        Assert.Equal("0.5×", StudioEditorText.GetSpeedRateText(0.5));
        Assert.Equal("1.5×", StudioEditorText.GetSpeedRateText(1.5));
        Assert.Equal("1.33×", StudioEditorText.GetSpeedRateText(4.0 / 3));
        Assert.Equal("8×", StudioEditorText.GetSpeedRateText(16));
        Assert.Equal("0.25×", StudioEditorText.GetSpeedRateText(0.01));
        Assert.Equal("1×", StudioEditorText.GetSpeedRateText(double.NaN));
        Assert.Equal("1×", StudioEditorText.GetSpeedRateText(0));

        // How much of the recording, and how long that takes in the video.
        Assert.Equal("4.5 seconds, plays in 9.0 seconds", StudioEditorText.GetSpeedLengthText(Speed(12, 16.5, 0.5)));
        Assert.Equal("4.5 seconds, plays in 1.1 seconds", StudioEditorText.GetSpeedLengthText(Speed(12, 16.5, 4)));
        Assert.Equal("4.0 seconds, plays in 0.5 seconds", StudioEditorText.GetSpeedLengthText(Speed(2, 6, 100)));
        Assert.Equal("0.0 seconds, plays in 0.0 seconds", StudioEditorText.GetSpeedLengthText(Speed(3, double.NaN, 2)));
        Assert.Equal("1.0 seconds, plays in 1.0 seconds", StudioEditorText.GetSpeedLengthText(Speed(3, 4, double.NaN)));

        // The names of the buttons that choose a rate.
        Assert.Equal(
            new[] { "Quarter speed", "Half speed", "One and a half times the speed", "Twice the speed", "4 times the speed", "8 times the speed" },
            StudioEditorModel.SpeedRates.Select(StudioEditorText.GetSpeedRateName));
        Assert.Equal("0.75 times the speed", StudioEditorText.GetSpeedRateName(0.75));
        Assert.Equal("8 times the speed", StudioEditorText.GetSpeedRateName(20));
        Assert.Equal("The recording's own speed", StudioEditorText.GetSpeedRateName(1));
        Assert.Equal("The recording's own speed", StudioEditorText.GetSpeedRateName(double.NaN));
    }

    // Helpers

    private static void AssertSpeed(StudioEditorModel model, params (double Start, double End, double Rate)[] expected)
    {
        var speed = model.Project.Edits.Speed;
        Assert.Equal(expected.Length, speed.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Start, speed[i].Start, Precision);
            Assert.Equal(expected[i].End, speed[i].End, Precision);
            Assert.Equal(expected[i].Rate, speed[i].Rate, Precision);
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

    private static StudioSpeedRange Speed(double start, double end, double rate) => new() { Start = start, End = end, Rate = rate };

    /// <summary>
    /// Twice as fast from 2 to 3, four times as fast from 5 to 6, and half as fast from 8 to 9
    /// seconds, in a recording 10 s long.
    /// </summary>
    private static StudioProject ThreeSpeedChanges() => WithSpeed((2, 3, 2), (5, 6, 4), (8, 9, 0.5));

    private static StudioProject WithSpeed(params (double Start, double End, double Rate)[] speed) => MakeProject() with
    {
        Edits = new StudioEdits { Speed = [.. speed.Select(entry => Speed(entry.Start, entry.End, entry.Rate))] },
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
