using System.Text.Json;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioAnnotationTransferModelTests
{
    private static StudioEditorModel Model(StudioEdits? edits = null, StudioAnnotation[]? annotations = null) => new(new StudioProject
    {
        Id = "destination",
        Sources = new StudioSources { Screen = new StudioScreenSource { Width = 1920, Height = 1080, Duration = 20 } },
        Edits = edits ?? new(),
        Overlays = new StudioOverlays { Annotations = annotations ?? [] },
    });

    [Fact]
    public void CopyUsesEditedPlaybackDurationAndDoesNotMutate_OrShareExtensions()
    {
        var annotation = StudioAnnotationClipboardTests.Fixture().Annotation;
        var model = Model(new StudioEdits
        {
            TrimStart = 2, TrimEnd = 10,
            Cuts = [new() { Start = 3, End = 5 }],
            Speed = [new() { Start = 0, End = 20, Rate = 2 }],
        }, [annotation]);
        var project = model.Project;
        var transfer = model.CopyAnnotation(0);
        Assert.Equal(2.5, transfer.OutputDurationSeconds, 9);
        Assert.Same(project, model.Project);
        Assert.False(model.CanUndo);
        Assert.NotSame(annotation.ExtensionData, transfer.Annotation.ExtensionData);
        transfer.Annotation.ExtensionData.Clear();
        Assert.Single(annotation.ExtensionData);
    }

    [Fact]
    public void FullyRemovedAnnotationCannotBeCopied()
    {
        var annotation = StudioAnnotationClipboardTests.Fixture().Annotation;
        var model = Model(new StudioEdits { Cuts = [new() { Start = 0, End = 10 }] }, [annotation]);
        Assert.Throws<InvalidDataException>(() => model.CopyAnnotation(0));
        Assert.False(model.CanUndo);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(2)]
    [InlineData(8)]
    public void CopyDurationUsesSourceEditedSpeed(double rate)
    {
        var annotation = StudioAnnotationClipboardTests.Fixture().Annotation;
        var model = Model(new StudioEdits
        {
            Cuts = [new() { Start = 3, End = 5 }],
            Speed = [new() { Start = 0, End = 20, Rate = rate }],
        }, [annotation]);
        Assert.Equal(6 / rate, model.CopyAnnotation(0).OutputDurationSeconds, 9);
        Assert.False(model.CanUndo);
    }

    [Fact]
    public void RejectedTimelineMutationCannotClaimTheExistingLastIdAsAPaste()
    {
        var transfer = StudioAnnotationClipboardTests.Fixture();
        var timeline = StudioTimelineEditing.ToTimelineProject(Model(annotations: [transfer.Annotation]).Manifest);
        var first = timeline.Timeline!.Clips[0];
        timeline = timeline with { Timeline = timeline.Timeline with { Clips = [first, first with { Id = "second" }] } };
        var model = new StudioEditorModel(timeline);
        // Simulate an externally invalidated, unselected clip. The editor's existing mutation
        // validator refuses the candidate; the selected clip still has room for the annotation.
        model.Manifest.Timeline!.Clips[1] = first with { Id = "second", Edits = new StudioEdits { TrimEnd = 0 } };
        var existing = model.Project.Overlays.Annotations.ToArray();
        Assert.Throws<InvalidOperationException>(() => model.PasteAnnotation(transfer, 10));
        Assert.Equal(existing, model.Project.Overlays.Annotations);
        Assert.False(model.CanUndo);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(2)]
    [InlineData(8)]
    public void PastePreservesOutputDurationAcrossSpeeds(double rate)
    {
        var transfer = StudioAnnotationClipboardTests.Fixture() with { OutputDurationSeconds = 1 };
        var model = Model(new StudioEdits
        {
            TrimStart = 2, TrimEnd = 18,
            Speed = [new() { Start = 0, End = 20, Rate = rate }],
        });
        Assert.True(model.PasteAnnotation(transfer, 3).Changed);
        var pasted = Assert.Single(model.Project.Overlays.Annotations);
        Assert.Equal(3, pasted.Start, 9);
        Assert.Equal(3 + rate, pasted.End, 9);
        Assert.Equal(1, model.GetOutputTime(pasted.End) - model.GetOutputTime(pasted.Start), 9);
    }

    [Theory]
    [InlineData(3, 3, 3.75)]
    [InlineData(5, 5, 12)]
    [InlineData(7, 8, 16.5)]
    [InlineData(17, 17, 18)]
    public void PasteUsesKeptDisplayFrameAndVariableSpeed_ClampingAtEditedEnd(double playhead, double start, double end)
    {
        var model = Model(new StudioEdits
        {
            TrimStart = 2, TrimEnd = 18,
            Cuts = [new() { Start = 6, End = 8 }],
            Speed = [new() { Start = 2, End = 4, Rate = 0.25 }, new() { Start = 8, End = 12, Rate = 2 }, new() { Start = 12, End = 16, Rate = 8 }],
        });
        Assert.True(model.PasteAnnotation(StudioAnnotationClipboardTests.Fixture(), playhead).Changed);
        var pasted = Assert.Single(model.Project.Overlays.Annotations);
        Assert.Equal(start, pasted.Start, 9);
        Assert.Equal(end, pasted.End, 9);
        Assert.Equal(Math.Min(3, model.OutputDuration - model.GetOutputTime(start)),
            model.GetOutputTime(end) - model.GetOutputTime(start), 9);
    }

    [Fact]
    public void EndAtCutBoundaryUsesPrecedingKeptEdge()
    {
        var model = Model(new StudioEdits { Cuts = [new() { Start = 6, End = 8 }] });
        Assert.True(model.PasteAnnotation(StudioAnnotationClipboardTests.Fixture() with { OutputDurationSeconds = 1 }, 5).Changed);
        Assert.Equal(6, Assert.Single(model.Project.Overlays.Annotations).End, 9);
    }

    [Theory]
    [InlineData(20, 3)]
    [InlineData(19.95, 3)]
    [InlineData(5, 0.09)]
    public void NoRoomNeverMovesStartEarlierOrEdits(double start, double duration)
    {
        var model = Model();
        Assert.False(model.PasteAnnotation(StudioAnnotationClipboardTests.Fixture() with { OutputDurationSeconds = duration }, start).Changed);
        Assert.Empty(model.Project.Overlays.Annotations);
        Assert.False(model.CanUndo);
    }

    [Fact]
    public void RepeatedPasteAppendsFreshIdsWithIdenticalGeometryAndAttachment_DuplicateUnchanged()
    {
        var transfer = StudioAnnotationClipboardTests.Fixture();
        var model = Model(annotations: [transfer.Annotation]);
        Assert.Equal(1, model.PasteAnnotation(transfer, 10).Index);
        Assert.Equal(2, model.PasteAnnotation(transfer, 10).Index);
        var first = model.Project.Overlays.Annotations[1];
        var second = model.Project.Overlays.Annotations[2];
        Assert.Equal(3, model.Project.Overlays.Annotations.Select(item => item.Id).Distinct().Count());
        foreach (var pasted in new[] { first, second })
        {
            var restored = pasted with { Id = transfer.Annotation.Id, Start = transfer.Annotation.Start, End = transfer.Annotation.End };
            using var expected = JsonDocument.Parse(StudioAnnotationClipboard.Encode(transfer));
            using var actual = JsonDocument.Parse(StudioAnnotationClipboard.Encode(transfer with { Annotation = restored }));
            Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
        }
        model.Undo();
        Assert.Equal(2, model.Project.Overlays.Annotations.Length);
        model.Redo();
        Assert.Equal(second.Id, model.Project.Overlays.Annotations[2].Id);
        model.DuplicateAnnotation(0);
        var duplicate = model.Project.Overlays.Annotations[1];
        Assert.Equal(transfer.Annotation.Start, duplicate.Start);
        Assert.Equal(transfer.Annotation.X + StudioEditorModel.DuplicateNudge, duplicate.X, 9);
    }
}
