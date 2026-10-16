using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

/// <summary>
/// Zooms and crops: what the shared fixtures do not pin down in the layout and the suggestions,
/// and the editor's operations on them.
/// </summary>
public sealed class StudioZoomTests
{
    private const int Precision = 9;

    // Cursor samples

    [Fact]
    public void PreparedCursorSamples_GiveThePlainSumOfTheSpec_ForAHundredThousandSamples()
    {
        // Stored newest first, so they also have to be put in order.
        var cursor = Enumerable.Range(0, 100_000)
            .Select(i => new StudioCursorSample
            {
                T = i / 60.0,
                X = (i % 97) / 96.0,
                Y = 1 - ((i % 89) / 88.0),
            })
            .Reverse()
            .ToArray();
        var prepared = StudioPreparedCursorSamples.For(new StudioEvents { Cursor = cursor });

        Assert.Equal(100_000, prepared.Count);
        foreach (var time in new[] { -10, 0, 0.125, 0.5, 3.25, 18.75, 123.456, 999.9, 1600.25, 1666.15, 1666.65, 2000 })
        {
            var expected = PlainCursorFocus(cursor, time);
            var actual = prepared.MeanInCenteredSecond(time);
            Assert.Equal(expected.X, actual.X, 12);
            Assert.Equal(expected.Y, actual.Y, 12);
        }
    }

    [Fact]
    public void PreparedCursorSamples_AreMadeOnceForACursorArray()
    {
        var events = new StudioEvents { Cursor = [new StudioCursorSample { T = 1, X = 0.2, Y = 0.3 }] };

        var first = StudioPreparedCursorSamples.For(events);

        Assert.Same(first, StudioPreparedCursorSamples.For(events));

        // Another events value with the same samples, as an edit of the clicks would make.
        Assert.Same(first, StudioPreparedCursorSamples.For(events with { Clicks = [new StudioClickEvent { T = 1 }] }));

        var other = events with { Cursor = [new StudioCursorSample { T = 1, X = 0.9, Y = 0.3 }] };
        Assert.NotSame(first, StudioPreparedCursorSamples.For(other));
        Assert.Equal(0.9, StudioPreparedCursorSamples.For(other).MeanInCenteredSecond(5).X, Precision);
    }

    [Fact]
    public void PreparedCursorSamples_WithoutSamples_AreEmpty()
    {
        Assert.Equal(0, StudioPreparedCursorSamples.For(null).Count);
        Assert.Equal(0, StudioPreparedCursorSamples.For(new StudioEvents()).Count);
        Assert.Equal(0, StudioPreparedCursorSamples.For(new StudioEvents { Cursor = [null!] }).Count);
    }

    [Fact]
    public void CursorSamplesWithTheSameTime_KeepTheOrderTheyAreStoredIn()
    {
        // Two samples at one instant: the pointer ends up where the later one says.
        var inOrder = new StudioEvents
        {
            Cursor =
            [
                new StudioCursorSample { T = 1, X = 0.2, Y = 0.2 },
                new StudioCursorSample { T = 1, X = 0.8, Y = 0.6 },
            ],
        };
        var outOfOrder = new StudioEvents
        {
            Cursor =
            [
                new StudioCursorSample { T = 9, X = 0.5, Y = 0.5 },
                new StudioCursorSample { T = 1, X = 0.2, Y = 0.2 },
                null!,
                new StudioCursorSample { T = 1, X = 0.8, Y = 0.6 },
            ],
        };

        foreach (var events in new[] { inOrder, outOfOrder })
        {
            var focus = StudioPreparedCursorSamples.For(events).MeanInCenteredSecond(4);
            Assert.Equal(0.8, focus.X, Precision);
            Assert.Equal(0.6, focus.Y, Precision);
        }
    }

    // Layout

    [Fact]
    public void Resolver_UsesTheEventsOnlyForAZoomThatFollowsThePointer()
    {
        var events = new StudioEvents
        {
            Cursor =
            [
                new StudioCursorSample { T = 1, X = 0.8, Y = 0.8 },
                new StudioCursorSample { T = 2, X = 0.8, Y = 0.8 },
            ],
        };
        var follows = MakeProject() with
        {
            Zooms = [Zoom(1, 5, mode: StudioZoomFocusMode.Cursor, x: 0.1, y: 0.1, easeIn: 0, easeOut: 0)],
        };
        var point = MakeProject() with { Zooms = [Zoom(1, 5, x: 0.1, y: 0.1, easeIn: 0, easeOut: 0)] };

        var withoutEvents = StudioLayoutResolver.Resolve(follows, 2, 1920, 1080).Screen!.Value.Source;
        var withEvents = StudioLayoutResolver.Resolve(follows, events, 2, 1920, 1080).Screen!.Value.Source;

        AssertRect(withoutEvents, 0, 0, 0.5, 0.5);
        AssertRect(withEvents, 0.5, 0.5, 0.5, 0.5);
        Assert.Equal(
            StudioLayoutResolver.Resolve(point, 2, 1920, 1080),
            StudioLayoutResolver.Resolve(point, events, 2, 1920, 1080));
        Assert.Equal(
            StudioLayoutResolver.Resolve(follows, 2, 1920, 1080),
            StudioLayoutResolver.Resolve(follows, null, 2, 1920, 1080));
    }

    [Fact]
    public void LayoutPlan_GivesTheFramesOfTheResolver()
    {
        var events = new StudioEvents
        {
            Cursor =
            [
                new StudioCursorSample { T = 0, X = 0.1, Y = 0.9 },
                new StudioCursorSample { T = 3, X = 0.7, Y = 0.2 },
            ],
        };
        var project = MakeProject(camera: true) with
        {
            Zooms = [Zoom(1, 4, mode: StudioZoomFocusMode.Cursor), Zoom(4, 6, scale: 3, x: 0.2, y: 0.2)],
        };
        var plan = StudioLayoutPlan.Create(project, events);

        foreach (var time in new[] { 0, 1, 1.25, 2.5, 3.9, 4, 4.3, 5.8, 6, 7 })
        {
            Assert.Equal(StudioLayoutResolver.Resolve(project, events, time, 1600, 900), plan.Resolve(time, 1600, 900));
        }
    }

    // Suggestions

    [Fact]
    public void Suggestions_NeedClicks()
    {
        var project = MakeProject();

        Assert.Empty(StudioZoomSuggestions.Suggest(project, null));
        Assert.Empty(StudioZoomSuggestions.Suggest(project, new StudioEvents()));
        Assert.Empty(StudioZoomSuggestions.Suggest(project, new StudioEvents { Clicks = [null!] }));
    }

    [Fact]
    public void SuggestionsForClicksThatMove_AreChainedByOneNumber()
    {
        var events = new StudioEvents
        {
            Clicks =
            [
                new StudioClickEvent { T = 3, X = 0.2, Y = 0.3 },
                new StudioClickEvent { T = 5.1, X = 0.8, Y = 0.7 },
            ],
        };
        var project = MakeProject();

        var zooms = StudioZoomSuggestions.Suggest(project, events);

        Assert.Equal(2, zooms.Length);

        // Not merely close: the layout chains two zooms only when the numbers are the same.
        Assert.True(zooms[0].End == zooms[1].Start);
        Assert.All(zooms, zoom => Assert.Equal(StudioZoomOrigin.Auto, zoom.Origin));

        var chained = project with { Zooms = zooms };
        var justBefore = StudioLayoutResolver.Resolve(chained, zooms[1].Start - 0.001, 1920, 1080).Screen!.Value.Source;
        AssertRect(justBefore, 0, 0.05, 0.5, 0.5);
    }

    // Adding and removing

    [Fact]
    public void AddZoom_GivesTheDefaults_AndLooksWhereThePointerIs()
    {
        var model = new StudioEditorModel(MakeProject(duration: 8));
        var events = new StudioEvents
        {
            Cursor =
            [
                new StudioCursorSample { T = 1.5, X = 0.2, Y = 0.4 },
                new StudioCursorSample { T = 2.5, X = 0.8, Y = 0.6 },
            ],
        };

        var result = model.AddZoom(2, events);

        Assert.Equal(new StudioZoomEditResult(true, 0), result);
        var zoom = Assert.Single(model.Project.Zooms);
        Assert.Equal(2, zoom.Start, Precision);
        Assert.Equal(5, zoom.End, Precision);
        Assert.Equal(2, zoom.Scale, Precision);
        Assert.Equal(0.5, zoom.EaseIn, Precision);
        Assert.Equal(0.5, zoom.EaseOut, Precision);
        Assert.Equal(StudioZoomFocusMode.Point, zoom.Focus.Mode);
        Assert.Equal(0.2, zoom.Focus.X, Precision);
        Assert.Equal(0.4, zoom.Focus.Y, Precision);
        Assert.Equal(StudioZoomOrigin.Manual, zoom.Origin);

        model.Undo();
        Assert.Empty(model.Project.Zooms);
        model.Redo();
        Assert.Single(model.Project.Zooms);
    }

    [Fact]
    public void AddZoom_WithoutCursorSamples_LooksAtTheCenter()
    {
        var model = new StudioEditorModel(MakeProject());

        model.AddZoom(1, null);
        model.AddZoom(5, new StudioEvents());

        Assert.All(model.Project.Zooms, zoom =>
        {
            Assert.Equal(0.5, zoom.Focus.X, Precision);
            Assert.Equal(0.5, zoom.Focus.Y, Precision);
        });
    }

    [Fact]
    public void AddZoom_WhereAZoomIs_ChangesNothing_AndSaysWhichZoomThatIs()
    {
        var model = new StudioEditorModel(MakeProject() with { Zooms = [Zoom(1, 3), Zoom(5, 7)] });

        Assert.Equal(new StudioZoomEditResult(false, 1), model.AddZoom(5, null));
        Assert.Equal(new StudioZoomEditResult(false, 1), model.AddZoom(6.9, null));
        Assert.Equal(new StudioZoomEditResult(false, 0), model.AddZoom(1, null));

        Assert.Equal(2, model.Project.Zooms.Length);
        Assert.False(model.CanUndo);
    }

    [Fact]
    public void AddZoom_StopsAtTheNextZoom_AndAtTheEndOfTheRecording()
    {
        var model = new StudioEditorModel(MakeProject(duration: 10) with { Zooms = [Zoom(4, 6)] });

        var before = model.AddZoom(2, null);
        var after = model.AddZoom(8.5, null);

        Assert.Equal(new StudioZoomEditResult(true, 0), before);
        Assert.Equal(new StudioZoomEditResult(true, 2), after);
        Assert.Equal(new[] { 2.0, 4.0, 8.5 }, model.Project.Zooms.Select(zoom => zoom.Start));
        Assert.True(model.Project.Zooms[0].End == model.Project.Zooms[1].Start);
        Assert.Equal(10, model.Project.Zooms[2].End, Precision);
    }

    [Fact]
    public void AddZoom_AtTheEndOfAZoom_IsChainedToIt()
    {
        var model = new StudioEditorModel(MakeProject() with { Zooms = [Zoom(1, 3)] });

        var result = model.AddZoom(3, null);

        Assert.Equal(new StudioZoomEditResult(true, 1), result);
        Assert.True(model.Project.Zooms[0].End == model.Project.Zooms[1].Start);
    }

    [Theory]
    [InlineData(3.8)]
    [InlineData(9.8)]
    [InlineData(10)]
    [InlineData(25)]
    public void AddZoom_WithoutRoomForTheShortestZoom_ChangesNothing(double time)
    {
        var model = new StudioEditorModel(MakeProject(duration: 10) with { Zooms = [Zoom(4, 6)] });

        Assert.Equal(new StudioZoomEditResult(false, null), model.AddZoom(time, null));

        Assert.Single(model.Project.Zooms);
        Assert.False(model.CanUndo);
    }

    [Fact]
    public void AddZoom_KeepsTheTimeInsideTheRecording_AndIgnoresWhatIsNotANumber()
    {
        var model = new StudioEditorModel(MakeProject(duration: 10));

        Assert.Equal(new StudioZoomEditResult(false, null), model.AddZoom(double.NaN, null));
        Assert.Equal(new StudioZoomEditResult(false, null), model.AddZoom(double.PositiveInfinity, null));
        Assert.Equal(new StudioZoomEditResult(true, 0), model.AddZoom(-4, null));

        Assert.Equal(0, Assert.Single(model.Project.Zooms).Start, Precision);
    }

    [Fact]
    public void RemoveZoom_TakesOneZoomOut()
    {
        var model = new StudioEditorModel(MakeProject() with { Zooms = [Zoom(1, 2), Zoom(3, 4), Zoom(5, 6)] });

        Assert.Equal(new StudioZoomEditResult(true, null), model.RemoveZoom(1));
        Assert.Equal(new[] { 1.0, 5.0 }, model.Project.Zooms.Select(zoom => zoom.Start));

        Assert.Equal(new StudioZoomEditResult(false, null), model.RemoveZoom(2));
        Assert.Equal(new StudioZoomEditResult(false, null), model.RemoveZoom(-1));

        model.Undo();
        Assert.Equal(new[] { 1.0, 3.0, 5.0 }, model.Project.Zooms.Select(zoom => zoom.Start));
    }

    // Moving the ends

    [Fact]
    public void ZoomEnds_StopAtTheNeighbours_OnExactlyTheirNumbers()
    {
        var model = new StudioEditorModel(MakeProject(duration: 12) with
        {
            Zooms = [Zoom(1, 3.1), Zoom(5, 7), Zoom(9.3, 11)],
        });

        Assert.Equal(new StudioZoomEditResult(true, 1), model.SetZoomStart(1, 2));
        Assert.Equal(new StudioZoomEditResult(true, 1), model.SetZoomEnd(1, 10));

        var zooms = model.Project.Zooms;
        Assert.True(zooms[1].Start == zooms[0].End);
        Assert.True(zooms[1].End == zooms[2].Start);
        Assert.Equal(new[] { 1.0, 3.1, 9.3 }, zooms.Select(zoom => zoom.Start));
    }

    [Fact]
    public void ZoomEnds_StopAtTheEdgesOfTheRecording()
    {
        var model = new StudioEditorModel(MakeProject(duration: 10) with { Zooms = [Zoom(4, 6)] });

        model.SetZoomStart(0, -3);
        model.SetZoomEnd(0, 40);

        Assert.Equal(0, model.Project.Zooms[0].Start, Precision);
        Assert.Equal(10, model.Project.Zooms[0].End, Precision);
    }

    [Fact]
    public void AZoom_IsNeverShorterThanTheShortestZoom()
    {
        var model = new StudioEditorModel(MakeProject(duration: 10) with { Zooms = [Zoom(4, 6)] });

        model.SetZoomStart(0, 5.9);
        Assert.Equal(6 - StudioEditorModel.MinimumZoomDuration, model.Project.Zooms[0].Start, Precision);

        model.SetZoomEnd(0, 1);
        Assert.Equal(model.Project.Zooms[0].Start + StudioEditorModel.MinimumZoomDuration, model.Project.Zooms[0].End, Precision);
    }

    [Fact]
    public void AZoomSqueezedBetweenItsNeighbours_DoesNotOverlapThem()
    {
        // Not something the editor makes: a project written by hand, with 0.2 seconds between two zooms.
        var model = new StudioEditorModel(MakeProject(duration: 10) with
        {
            Zooms = [Zoom(1, 3), Zoom(3, 3.2), Zoom(3.2, 6)],
        });

        model.SetZoomStart(1, 0);
        model.SetZoomEnd(1, 9);

        Assert.Equal(3, model.Project.Zooms[1].Start, Precision);
        Assert.Equal(3.2, model.Project.Zooms[1].End, Precision);
    }

    [Fact]
    public void ZoomEdits_ThatAreNotANumber_OrNameNoZoom_ChangeNothing()
    {
        var model = new StudioEditorModel(MakeProject() with { Zooms = [Zoom(1, 3)] });
        var before = model.Project;

        Assert.Equal(new StudioZoomEditResult(false, 0), model.SetZoomStart(0, double.NaN));
        Assert.Equal(new StudioZoomEditResult(false, 0), model.SetZoomEnd(0, double.NegativeInfinity));
        Assert.Equal(new StudioZoomEditResult(false, 0), model.SetZoomScale(0, double.NaN));
        Assert.Equal(new StudioZoomEditResult(false, 0), model.SetZoomFocusPoint(0, double.NaN, 0.2));
        Assert.Equal(new StudioZoomEditResult(false, 0), model.SetZoomFocusPoint(0, 0.2, double.PositiveInfinity));
        Assert.Equal(new StudioZoomEditResult(false, 0), model.SetZoomEaseIn(0, double.NaN));
        Assert.Equal(new StudioZoomEditResult(false, 0), model.SetZoomEaseOut(0, double.NaN));

        Assert.Equal(new StudioZoomEditResult(false, null), model.SetZoomStart(1, 2));
        Assert.Equal(new StudioZoomEditResult(false, null), model.SetZoomEnd(-1, 2));
        Assert.Equal(new StudioZoomEditResult(false, null), model.SetZoomScale(7, 2));
        Assert.Equal(new StudioZoomEditResult(false, null), model.SetZoomFocusMode(1, StudioZoomFocusMode.Cursor));
        Assert.Equal(new StudioZoomEditResult(false, null), model.SetZoomFocusPoint(1, 0.5, 0.5));
        Assert.Equal(new StudioZoomEditResult(false, null), model.SetZoomEaseIn(1, 1));
        Assert.Equal(new StudioZoomEditResult(false, null), model.SetZoomEaseOut(1, 1));

        Assert.Same(before, model.Project);
        Assert.False(model.CanUndo);
    }

    // The other values

    [Fact]
    public void ZoomValues_AreKeptInTheirRanges()
    {
        var model = new StudioEditorModel(MakeProject() with { Zooms = [Zoom(1, 5)] });

        model.SetZoomScale(0, 9);
        model.SetZoomFocusPoint(0, -1, 2);
        model.SetZoomEaseIn(0, 7);
        model.SetZoomEaseOut(0, -1);
        model.SetZoomFocusMode(0, StudioZoomFocusMode.Cursor);

        var zoom = model.Project.Zooms[0];
        Assert.Equal(5, zoom.Scale, Precision);
        Assert.Equal(0, zoom.Focus.X, Precision);
        Assert.Equal(1, zoom.Focus.Y, Precision);
        Assert.Equal(3, zoom.EaseIn, Precision);
        Assert.Equal(0, zoom.EaseOut, Precision);
        Assert.Equal(StudioZoomFocusMode.Cursor, zoom.Focus.Mode);

        model.SetZoomScale(0, 0.2);
        Assert.Equal(1, model.Project.Zooms[0].Scale, Precision);
    }

    [Fact]
    public void ChangingASuggestedZoom_MakesItTheUsersOwn_AndAnEditThatChangesNothingDoesNot()
    {
        var model = new StudioEditorModel(MakeProject() with
        {
            Zooms = [Zoom(1, 4, origin: StudioZoomOrigin.Auto), Zoom(5, 8, origin: StudioZoomOrigin.Auto)],
        });

        // The values the zoom already has, as the start of a drag sends them.
        Assert.Equal(new StudioZoomEditResult(false, 0), model.SetZoomScale(0, 2));
        Assert.Equal(new StudioZoomEditResult(false, 0), model.SetZoomStart(0, 1));
        Assert.Equal(new StudioZoomEditResult(false, 0), model.SetZoomEnd(0, 4));
        Assert.Equal(new StudioZoomEditResult(false, 0), model.SetZoomFocusMode(0, StudioZoomFocusMode.Point));
        Assert.Equal(new StudioZoomEditResult(false, 0), model.SetZoomFocusPoint(0, 0.5, 0.5));
        Assert.Equal(StudioZoomOrigin.Auto, model.Project.Zooms[0].Origin);
        Assert.False(model.CanUndo);

        Assert.Equal(new StudioZoomEditResult(true, 0), model.SetZoomScale(0, 3));

        Assert.Equal(StudioZoomOrigin.Manual, model.Project.Zooms[0].Origin);
        Assert.Equal(StudioZoomOrigin.Auto, model.Project.Zooms[1].Origin);

        model.Undo();
        Assert.Equal(StudioZoomOrigin.Auto, model.Project.Zooms[0].Origin);
        Assert.Equal(2, model.Project.Zooms[0].Scale, Precision);
    }

    [Fact]
    public void ZoomEdits_KeepWhatTheZoomHasThatThisVersionDoesNotKnow()
    {
        var project = StudioProjectJson.ReadProject("""
            {
              "id": "3f0013cf-ba10-4453-af91-792b7882dae6",
              "sources": { "screen": { "width": 1920, "height": 1080, "duration": 10 } },
              "zooms": [ { "start": 1, "end": 4, "tilt": 3, "focus": { "x": 0.2, "y": 0.3, "depth": 2 } } ]
            }
            """);
        var model = new StudioEditorModel(project);

        model.SetZoomScale(0, 3);
        model.SetZoomFocusPoint(0, 0.6, 0.7);
        model.SetZoomEnd(0, 5);

        var written = StudioProjectJson.WriteProject(model.Project);
        Assert.Contains("\"tilt\": 3", written);
        Assert.Contains("\"depth\": 2", written);
    }

    [Fact]
    public void ADragOfAZoom_IsOneUndoStep_AndCanBeCancelled()
    {
        var model = new StudioEditorModel(MakeProject() with { Zooms = [Zoom(1, 4)] });

        model.BeginEditingGroup();
        model.SetZoomEnd(0, 4.5);
        model.SetZoomEnd(0, 5);
        model.SetZoomEnd(0, 6);
        Assert.False(model.CanUndo);
        model.CommitEditingGroup();

        Assert.Equal(6, model.Project.Zooms[0].End, Precision);
        model.Undo();
        Assert.Equal(4, model.Project.Zooms[0].End, Precision);
        Assert.False(model.CanUndo);

        model.BeginEditingGroup();
        model.SetZoomFocusPoint(0, 0.1, 0.9);
        model.CancelEditingGroup();
        Assert.Equal(0.5, model.Project.Zooms[0].Focus.X, Precision);
        Assert.False(model.CanUndo);
    }

    [Fact]
    public void UndoingBackToWhatWasExported_IsNoLongerAChange_AndNeitherIsSettingTheValueBack()
    {
        var exported = MakeProject() with
        {
            Zooms = [Zoom(1, 4)],
            Exports = [new StudioExport { Path = @"C:\Videos\clip.mp4" }],
        };
        var model = new StudioEditorModel(exported);
        Assert.False(model.HasUnexportedChanges);

        model.SetZoomScale(0, 3);
        Assert.True(model.HasUnexportedChanges);

        model.SetZoomScale(0, 2);
        Assert.False(model.HasUnexportedChanges);

        model.AddZoom(6, null);
        Assert.True(model.HasUnexportedChanges);
        model.Undo();
        Assert.False(model.HasUnexportedChanges);
    }

    // The list

    [Fact]
    public void TheEditor_KeepsTheZoomsInTimeOrder_FromTheMomentItOpens()
    {
        var model = new StudioEditorModel(MakeProject() with
        {
            Zooms = [Zoom(5, 7), null!, Zoom(1, 3), Zoom(8, 9)],
        });

        Assert.Equal(new[] { 1.0, 5.0, 8.0 }, model.Project.Zooms.Select(zoom => zoom.Start));
        Assert.False(model.CanUndo);

        Assert.Equal(new StudioZoomEditResult(true, 1), model.AddZoom(3.5, null));
        Assert.Equal(new[] { 1.0, 3.5, 5.0, 8.0 }, model.Project.Zooms.Select(zoom => zoom.Start));
    }

    [Theory]
    [InlineData(0.99, null)]
    [InlineData(1, 0)]
    [InlineData(2.99, 0)]
    [InlineData(3, 1)]
    [InlineData(4.5, 1)]
    [InlineData(5, null)]
    [InlineData(6, 2)]
    [InlineData(double.NaN, null)]
    public void TheZoomAtATime_HasItsStartAndNotItsEnd(double time, int? expected)
    {
        var model = new StudioEditorModel(MakeProject() with { Zooms = [Zoom(1, 3), Zoom(3, 5), Zoom(6, 7)] });

        Assert.Equal(expected, model.GetZoomIndexAt(time));
    }

    // Suggestions in the editor

    [Fact]
    public void ApplyingSuggestions_ReplacesOnlyTheSuggestedZooms_InOneUndoStep()
    {
        var model = new StudioEditorModel(MakeProject() with
        {
            Zooms = [Zoom(1, 2), Zoom(4, 5, origin: StudioZoomOrigin.Auto), Zoom(9, 9.5)],
        });

        // Suggestions arrive marked as such, but the editor does not rely on it.
        Assert.True(model.ApplyZoomSuggestions([Zoom(6, 8), Zoom(3, 3.5, origin: StudioZoomOrigin.Auto), null!]));

        Assert.Equal(new[] { 1.0, 3.0, 6.0, 9.0 }, model.Project.Zooms.Select(zoom => zoom.Start));
        Assert.Equal(
            new[] { StudioZoomOrigin.Manual, StudioZoomOrigin.Auto, StudioZoomOrigin.Auto, StudioZoomOrigin.Manual },
            model.Project.Zooms.Select(zoom => zoom.Origin));

        model.Undo();
        Assert.Equal(new[] { 1.0, 4.0, 9.0 }, model.Project.Zooms.Select(zoom => zoom.Start));
        Assert.False(model.CanUndo);
    }

    [Fact]
    public void ApplyingTheSuggestionsThatAreThere_ChangesNothing()
    {
        var model = new StudioEditorModel(MakeProject());
        var events = new StudioEvents { Clicks = [new StudioClickEvent { T = 3, X = 0.2, Y = 0.3 }] };

        Assert.True(model.ApplyZoomSuggestions(StudioZoomSuggestions.Suggest(model.Project, events)));
        Assert.False(model.ApplyZoomSuggestions(StudioZoomSuggestions.Suggest(model.Project, events)));

        Assert.Single(model.Project.Zooms);
        model.Undo();
        Assert.Empty(model.Project.Zooms);
        Assert.False(model.CanUndo);
    }

    [Fact]
    public void ApplyingNoSuggestions_TakesTheSuggestedZoomsAway()
    {
        var model = new StudioEditorModel(MakeProject() with
        {
            Zooms = [Zoom(1, 2, origin: StudioZoomOrigin.Auto), Zoom(4, 5)],
        });

        Assert.True(model.ApplyZoomSuggestions([]));

        Assert.Equal(4, Assert.Single(model.Project.Zooms).Start, Precision);
        Assert.Throws<ArgumentNullException>(() => model.ApplyZoomSuggestions(null!));
    }

    // Crops

    [Fact]
    public void ACrop_IsMadeValid_SizeFirstAndThenPosition()
    {
        var model = new StudioEditorModel(MakeProject(camera: true));

        model.SetScreenCrop(new StudioRect(-1, 0.9, 2, 0.01));
        model.SetCameraCrop(new StudioRect(0.9, 0.9, 0.2, 0.2));

        AssertRect(model.Project.Screen.Crop, 0, 0.9, 1, 0.05);
        AssertRect(model.Project.Camera.Crop, 0.8, 0.8, 0.2, 0.2);
        Assert.NotNull(StudioCanvasMath.ValidCropOrNull(model.Project.Screen.Crop));
        Assert.NotNull(StudioCanvasMath.ValidCropOrNull(model.Project.Camera.Crop));
    }

    [Fact]
    public void AValidCrop_IsStoredAsItIs_AndChangesTheCanvas()
    {
        var model = new StudioEditorModel(MakeProject());

        model.SetScreenCrop(new StudioRect(0.25, 0.25, 0.5, 0.25));

        AssertRect(model.Project.Screen.Crop, 0.25, 0.25, 0.5, 0.25);
        Assert.Equal(new StudioSize(960, 270), StudioCanvasMath.NaturalSize(model.Project));
    }

    [Fact]
    public void Crops_CanBeClearedAndUndone_AndStayOutOfASavedLook()
    {
        var model = new StudioEditorModel(MakeProject(camera: true));
        model.SetScreenCrop(new StudioRect(0.1, 0.1, 0.5, 0.5));
        model.SetCameraCrop(new StudioRect(0.2, 0.2, 0.5, 0.5));

        Assert.Null(model.CurrentLook.Screen.Crop);
        Assert.Null(model.CurrentLook.Camera.Crop);

        model.ClearScreenCrop();
        model.ClearCameraCrop();
        Assert.Null(model.Project.Screen.Crop);
        Assert.Null(model.Project.Camera.Crop);

        // Clearing what is not there is not an edit.
        model.ClearScreenCrop();
        model.Undo();
        Assert.NotNull(model.Project.Camera.Crop);
        model.Undo();
        Assert.NotNull(model.Project.Screen.Crop);
    }

    [Fact]
    public void ACropThatIsNotANumber_IsIgnored()
    {
        var model = new StudioEditorModel(MakeProject(camera: true));
        model.SetScreenCrop(new StudioRect(0.1, 0.1, 0.5, 0.5));

        model.SetScreenCrop(new StudioRect(double.NaN, 0, 0.5, 0.5));
        model.SetScreenCrop(new StudioRect(0, 0, double.PositiveInfinity, 0.5));
        model.SetCameraCrop(new StudioRect(0, double.NaN, 0.5, 0.5));

        AssertRect(model.Project.Screen.Crop, 0.1, 0.1, 0.5, 0.5);
        Assert.Null(model.Project.Camera.Crop);
    }

    [Fact]
    public void AZoom_WorksInsideTheCrop()
    {
        var model = new StudioEditorModel(MakeProject());
        model.SetScreenCrop(new StudioRect(0.5, 0, 0.5, 1));
        model.AddZoom(1, null);
        model.SetZoomEaseIn(0, 0);

        // The focus is the middle of the whole screen, which is the crop's left edge.
        var source = StudioLayoutResolver.Resolve(model.Project, 2, 960, 1080).Screen!.Value.Source;

        AssertRect(source, 0.5, 0.25, 0.25, 0.5);
    }

    // Helpers

    private static (double X, double Y) PlainCursorFocus(IReadOnlyList<StudioCursorSample> raw, double time)
    {
        var samples = raw.OrderBy(sample => sample.T).ToArray();
        var start = time - 0.5;
        var end = time + 0.5;
        double x = 0;
        double y = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var from = i == 0 ? double.NegativeInfinity : samples[i].T;
            var until = i == samples.Length - 1 ? double.PositiveInfinity : samples[i + 1].T;
            var length = Math.Max(0, Math.Min(end, until) - Math.Max(start, from));
            x += Math.Min(1, Math.Max(0, samples[i].X)) * length;
            y += Math.Min(1, Math.Max(0, samples[i].Y)) * length;
        }

        return (x, y);
    }

    private static StudioZoom Zoom(
        double start,
        double end,
        double scale = 2,
        StudioZoomFocusMode mode = StudioZoomFocusMode.Point,
        double x = 0.5,
        double y = 0.5,
        double easeIn = 0.5,
        double easeOut = 0.5,
        StudioZoomOrigin origin = StudioZoomOrigin.Manual) => new()
        {
            Start = start,
            End = end,
            Scale = scale,
            Focus = new StudioZoomFocus { Mode = mode, X = x, Y = y },
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