using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>Annotations in the editor session: adding and selecting one, one selection, undo, and the panel.</summary>
public sealed class StudioEditorSessionAnnotationTests : StudioEditorSessionTestBase
{
    [Fact]
    public async Task Adding_SelectsIt_ShowsThePanel_AndLeavesThePlayhead()
    {
        var session = await OpenAsync(CreateProject());
        session.Scrub(4);
        Assert.True(session.CanAddAnnotation);

        Assert.Equal(new StudioAnnotationEditResult(true, 0), session.AddAnnotationAtPlayhead(StudioAnnotationKinds.Arrow));

        Assert.Equal(0, session.SelectedAnnotationIndex);
        Assert.Equal(4, session.SelectedAnnotation!.Start, Precision);
        Assert.Equal(StudioInspectorPanel.Annotate, session.InspectorPanel);
        Assert.Equal(4, session.Playhead, Precision);
        Assert.Single(Preview.LastProject!.Overlays.Annotations);
        Assert.True(session.HasUnsavedEdits);

        session.Undo();
        Assert.Null(session.SelectedAnnotationIndex);
        Assert.Empty(Preview.LastProject!.Overlays.Annotations);

        session.Redo();
        Assert.Single(Preview.LastProject!.Overlays.Annotations);
    }

    [Fact]
    public async Task OneThingIsSelected_AtATime()
    {
        var session = await OpenAsync(CreateProject());
        session.AddAnnotationAtPlayhead(StudioAnnotationKinds.Text);
        session.Scrub(6);
        session.AddSpeedAtPlayhead();
        Assert.Null(session.SelectedAnnotationIndex);
        Assert.NotNull(session.SelectedSpeedIndex);

        session.SelectAnnotation(0);
        Assert.Null(session.SelectedSpeedIndex);
        Assert.Equal(0, session.SelectedAnnotationIndex);
        Assert.Equal(StudioInspectorPanel.Annotate, session.InspectorPanel);

        session.SelectSpeed(0);
        Assert.Null(session.SelectedAnnotationIndex);

        session.SelectAnnotation(0);
        session.SelectNothing();
        Assert.Null(session.SelectedAnnotationIndex);
    }

    [Fact]
    public async Task TheLayerButtons_TakeTheSelectionWithThem()
    {
        var session = await OpenAsync(CreateProject());
        session.AddAnnotationAtPlayhead(StudioAnnotationKinds.Arrow);
        session.AddAnnotationAtPlayhead(StudioAnnotationKinds.Text);
        Assert.Equal(1, session.SelectedAnnotationIndex);
        var id = session.SelectedAnnotation!.Id;

        Assert.Equal(new StudioAnnotationEditResult(true, 0), session.SendAnnotationBack(1));
        Assert.Equal(0, session.SelectedAnnotationIndex);
        Assert.Equal(id, session.SelectedAnnotation!.Id);

        // Undo goes by the id, so the selection stays on the same annotation.
        session.Undo();
        Assert.Equal(1, session.SelectedAnnotationIndex);
        Assert.Equal(id, session.SelectedAnnotation!.Id);
    }

    [Fact]
    public async Task Deleting_LetsGo_AndLeavesThePanel()
    {
        var session = await OpenAsync(CreateProject());
        session.AddAnnotationAtPlayhead(StudioAnnotationKinds.Arrow);

        Assert.Equal(new StudioAnnotationEditResult(true, null), session.RemoveSelectedAnnotation());

        Assert.Null(session.SelectedAnnotationIndex);
        Assert.Equal(StudioInspectorPanel.Annotate, session.InspectorPanel);
        Assert.Equal(new StudioAnnotationEditResult(false, null), session.RemoveSelectedAnnotation());
    }

    [Fact]
    public async Task TheEnds_ShowThePictureThere_WhereTheAnnotationStillShows()
    {
        var session = await OpenAsync(CreateProject());
        session.Scrub(2);
        session.AddAnnotationAtPlayhead(StudioAnnotationKinds.Arrow);

        session.SetAnnotationStart(0, 3);
        Assert.Equal(3, session.Playhead, Precision);

        session.SetAnnotationEnd(0, 4);
        Assert.True(session.Playhead < 4);
        Assert.True(session.Playhead >= 3);
    }

    [Fact]
    public async Task Stepping_SelectsAndMovesThePlayhead()
    {
        var session = await OpenAsync(CreateProject());
        session.Scrub(1);
        session.AddAnnotationAtPlayhead(StudioAnnotationKinds.Arrow);
        session.Scrub(5);
        session.AddAnnotationAtPlayhead(StudioAnnotationKinds.Text);

        Assert.True(session.SelectPreviousAnnotation());
        Assert.Equal(0, session.SelectedAnnotationIndex);
        Assert.Equal(1, session.Playhead, Precision);
        Assert.False(session.SelectPreviousAnnotation());
        Assert.True(session.SelectNextAnnotation());
        Assert.Equal(1, session.SelectedAnnotationIndex);
        Assert.Equal(5, session.Playhead, Precision);
    }

    [Fact]
    public async Task AnEditThatChangesNothing_ShowsNoPanel()
    {
        var session = await OpenAsync(CreateProject());
        session.AddAnnotationAtPlayhead(StudioAnnotationKinds.Arrow);
        session.ShowInspectorPanel(StudioInspectorPanel.Audio);

        session.SetAnnotationStyle(0, StudioAnnotationStyle.Red);
        Assert.Equal(StudioInspectorPanel.Audio, session.InspectorPanel);

        session.SetAnnotationStyle(0, StudioAnnotationStyle.Blue);
        Assert.Equal(StudioInspectorPanel.Annotate, session.InspectorPanel);
    }

    [Theory]
    [InlineData(StudioShortcutKey.A, StudioShortcutAction.AddArrow)]
    [InlineData(StudioShortcutKey.C, StudioShortcutAction.AddCallout)]
    [InlineData(StudioShortcutKey.T, StudioShortcutAction.AddText)]
    public void TheToolKeys_AddOnce_AndLeaveTextAlone(StudioShortcutKey key, StudioShortcutAction action)
    {
        StudioShortcutInput Input(bool repeat = false, bool text = false, bool control = false) =>
            new(key, IsControlDown: control, IsShiftDown: false, IsAltDown: false, IsRepeat: repeat, IsTextInputFocused: text, IsReady: true, IsExporting: false);

        Assert.Equal(action, StudioShortcuts.Resolve(Input()));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Input(repeat: true)));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Input(text: true)));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Input(control: true)));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Input() with { IsDragging = true }));
    }

    [Fact]
    public void Delete_RemovesTheSelectedAnnotation()
    {
        var input = new StudioShortcutInput(StudioShortcutKey.Delete, false, false, false, false, false, true, false) { HasSelectedAnnotation = true };

        Assert.Equal(StudioShortcutAction.RemoveSelectedAnnotation, StudioShortcuts.Resolve(input));
    }

    [Fact]
    public void TheWords_NameAnAnnotation_InOneLine()
    {
        var callout = new StudioAnnotation { Kind = StudioAnnotationKinds.Callout, Text = "Billing lives\nhere", Start = 12, End = 16.5 };

        Assert.Equal("Arrow", StudioEditorText.GetAnnotationLabel(new StudioAnnotation()));
        Assert.Equal("Callout: Billing lives here", StudioEditorText.GetAnnotationLabel(callout));
        Assert.Equal("Text: a very long title that g…", StudioEditorText.GetAnnotationLabel(new StudioAnnotation { Kind = StudioAnnotationKinds.Text, Text = "a very long title that goes on and on" }));
        Assert.Equal(
            "Text: " + string.Concat(Enumerable.Repeat("\U0001F600", 24)) + "\u2026",
            StudioEditorText.GetAnnotationLabel(new StudioAnnotation { Kind = StudioAnnotationKinds.Text, Text = string.Concat(Enumerable.Repeat("\U0001F600", 30)) }));
        Assert.Equal("Annotation: later", StudioEditorText.GetAnnotationLabel(new StudioAnnotation { Kind = "highlight", Text = "later" }));
        Assert.Equal("Callout: Billing lives here, 12.0 to 16.5 seconds", StudioEditorText.GetAnnotationDescription(callout));
        Assert.Equal("Annotation 2 of 3, Callout: Billing lives here, 12.0 to 16.5 seconds", StudioEditorText.GetAnnotationStepText(1, 3, callout));
        Assert.Equal("Text added.", StudioEditorText.GetAnnotationAddedMessage(StudioAnnotationKinds.Text));
    }
}
