using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

public sealed class StudioAnnotationTransferSessionTests : StudioEditorSessionTestBase
{
    [Fact]
    public async Task PasteIsOneSelectedUndoStep_AutosavesReopensAndPortableSavePreservesIt()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);
        session.Scrub(4.015);
        var intent = session.CaptureAnnotationPasteIntent()!;
        Assert.True(session.PasteAnnotation(intent, StudioAnnotationClipboardTests.Fixture()).Changed);
        var annotation = session.SelectedAnnotation!;
        Assert.Equal(4, annotation.Start, Precision);
        Assert.Equal(7, annotation.End, Precision);
        Assert.Equal(StudioInspectorPanel.Annotate, session.InspectorPanel);
        Assert.Equal(4.015, session.Playhead, Precision);
        session.Undo();
        Assert.Empty(session.Project!.Overlays.Annotations);
        session.Redo();
        Assert.Equal(annotation.Id, Assert.Single(session.Project!.Overlays.Annotations).Id);

        Time.Advance(StudioEditorSession.AutosaveDelay);
        Pump();
        Assert.Equal(annotation.Id, Assert.Single(Projects.Load(id).Overlays.Annotations).Id);
        var folder = Path.Combine(Path.GetTempPath(), "TinyClipsClipboard-" + Guid.NewGuid().ToString("N"));
        try
        {
            var save = session.SaveProjectFolderAsync(folder);
            await PumpUntilCompletedAsync(save);
            var result = await save;
            Assert.True(result.Outcome == StudioProjectFolderSaveOutcome.Saved, result.Failure?.ToString());
            var portable = Projects.OpenProjectFolder(result.ProjectFilePath!, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(annotation.Id, Assert.Single(portable.Overlays.Annotations).Id);
            Assert.True(Assert.Single(portable.Overlays.Annotations).ExtensionData["futureField"].GetProperty("keep").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        await FinishAsync(session.CloseAsync(false));
        var reopened = await OpenAsync(id);
        Assert.Equal(annotation.Id, Assert.Single(reopened.Project!.Overlays.Annotations).Id);
    }

    [Theory]
    [InlineData("playhead")]
    [InlineData("edit")]
    [InlineData("gesture")]
    [InlineData("closed")]
    public async Task AwaitedReadIntentCannotCommitAfterDestinationChanges_EvenIfItChangesBack(string change)
    {
        var session = await OpenAsync(CreateProject());
        var intent = session.CaptureAnnotationPasteIntent()!;
        switch (change)
        {
            case "playhead": session.Scrub(3); session.Scrub(0); break;
            case "edit": session.AddAnnotationAtPlayhead("arrow"); session.Undo(); break;
            case "gesture": session.BeginGesture(); session.EndGesture(); break;
            case "closed": await FinishAsync(session.CloseAsync(false)); break;
        }
        Assert.Throws<InvalidOperationException>(() => session.PasteAnnotation(intent, StudioAnnotationClipboardTests.Fixture()));
        Assert.Empty(session.Project!.Overlays.Annotations);
    }

    [Fact]
    public async Task OtherProjectAndOtherClipRejectStaleIntent_V2PasteOnlyChangesSelectedClip()
    {
        var id = CreateProject();
        Projects.Save(StudioTimelineEditing.ToTimelineProject(Projects.Load(id)));
        var session = await OpenAsync(id);
        session.Scrub(4);
        Assert.True(session.SplitClipAtPlayhead());
        var clips = session.Manifest!.Timeline!.Clips;
        var second = session.SelectedClipId!;
        var intent = session.CaptureAnnotationPasteIntent()!;
        Assert.True(session.SelectClip(clips[0].Id));
        Assert.Throws<InvalidOperationException>(() => session.PasteAnnotation(intent, StudioAnnotationClipboardTests.Fixture()));
        Assert.True(session.SelectClip(second));
        var current = session.CaptureAnnotationPasteIntent()!;
        var other = await OpenAsync(CreateProject());
        Assert.Throws<InvalidOperationException>(() => other.PasteAnnotation(current, StudioAnnotationClipboardTests.Fixture()));
        Assert.True(session.PasteAnnotation(current, StudioAnnotationClipboardTests.Fixture()).Changed);
        Assert.Empty(session.Manifest!.Timeline!.Clips[0].Overlays!.Annotations);
        Assert.Single(session.Manifest.Timeline.Clips[1].Overlays!.Annotations);
        session.Undo();
        Assert.Empty(session.Manifest.Timeline.Clips[1].Overlays!.Annotations);
        Assert.Empty(session.Manifest.Timeline.Clips[0].Overlays!.Annotations);
    }

    [Fact]
    public async Task CopyIsNonmutatingAndSurvivesSourceClosing_AndInvalidPasteDoesNotEdit()
    {
        var source = await OpenAsync(CreateProject());
        source.AddAnnotationAtPlayhead("arrow");
        var selection = source.SelectedAnnotationIndex;
        var project = source.Project;
        var bytes = source.CopySelectedAnnotation();
        Assert.Same(project, source.Project);
        Assert.Equal(selection, source.SelectedAnnotationIndex);
        await FinishAsync(source.CloseAsync(false));
        var destination = await OpenAsync(CreateProject());
        Assert.True(destination.PasteAnnotation(destination.CaptureAnnotationPasteIntent()!, StudioAnnotationClipboard.Decode(bytes)).Changed);
        var before = destination.Project;
        Assert.Throws<InvalidDataException>(() => destination.PasteAnnotation(destination.CaptureAnnotationPasteIntent()!,
            StudioAnnotationClipboardTests.Fixture() with { OutputDurationSeconds = double.PositiveInfinity }));
        Assert.Same(before, destination.Project);
    }
}
