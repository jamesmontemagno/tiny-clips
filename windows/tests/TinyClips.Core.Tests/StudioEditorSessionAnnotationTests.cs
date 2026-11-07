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
    [InlineData(StudioShortcutKey.H, StudioShortcutAction.AddRectangle)]
    [InlineData(StudioShortcutKey.E, StudioShortcutAction.AddEllipse)]
    [InlineData(StudioShortcutKey.B, StudioShortcutAction.AddRedaction)]
    [InlineData(StudioShortcutKey.M, StudioShortcutAction.AddEmoji)]
    public void TheToolKeys_AddOnce_AndLeaveTextAlone(StudioShortcutKey key, StudioShortcutAction action)
    {
        StudioShortcutInput Input(bool repeat = false, bool text = false, bool control = false) =>
            new(key, IsControlDown: control, IsShiftDown: false, IsAltDown: false, IsRepeat: repeat, IsTextInputFocused: text, IsReady: true, IsExporting: false);

        Assert.Equal(action, StudioShortcuts.Resolve(Input()));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Input(repeat: true)));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Input(text: true)));
        // With Control, E is still Export.
        Assert.Equal(key == StudioShortcutKey.E ? StudioShortcutAction.Export : StudioShortcutAction.None, StudioShortcuts.Resolve(Input(control: true)));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(Input() with { IsDragging = true }));
    }

    [Fact]
    public void ControlE_StillExports()
    {
        var input = new StudioShortcutInput(StudioShortcutKey.E, IsControlDown: true, IsShiftDown: false, IsAltDown: false, IsRepeat: false, IsTextInputFocused: false, IsReady: true, IsExporting: false);

        Assert.Equal(StudioShortcutAction.Export, StudioShortcuts.Resolve(input));
    }

    [Fact]
    public void ControlD_DuplicatesOnlyASelectedAnnotation_AndOnlyWhenFree()
    {
        var input = new StudioShortcutInput(StudioShortcutKey.D, IsControlDown: true, IsShiftDown: false, IsAltDown: false, IsRepeat: false, IsTextInputFocused: false, IsReady: true, IsExporting: false) { HasSelectedAnnotation = true };

        Assert.Equal(StudioShortcutAction.DuplicateSelectedAnnotation, StudioShortcuts.Resolve(input));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(input with { HasSelectedAnnotation = false }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(input with { IsDragging = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(input with { IsTextInputFocused = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(input with { IsExporting = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(input with { IsTypeToSearchFocused = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(input with { IsRepeat = true }));
        Assert.Equal(StudioShortcutAction.None, StudioShortcuts.Resolve(input with { IsControlDown = false }));
    }

    [Fact]
    public async Task Duplicating_CopiesAboveTheOriginal_NudgedAndSelected_AsOneUndoStep()
    {
        var session = await OpenAsync(CreateProject());
        session.Scrub(2);
        session.AddAnnotationAtPlayhead(StudioAnnotationKinds.Emoji);
        session.AddAnnotationAtPlayhead(StudioAnnotationKinds.Arrow);
        session.SelectAnnotation(0);
        var original = session.SelectedAnnotation!;

        Assert.Equal(new StudioAnnotationEditResult(true, 1), session.DuplicateSelectedAnnotation());

        var annotations = Preview.LastProject!.Overlays.Annotations;
        Assert.Equal(3, annotations.Length);
        Assert.Equal(StudioAnnotationKinds.Arrow, annotations[2].Kind);
        var copy = annotations[1];
        Assert.Equal(1, session.SelectedAnnotationIndex);
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal(original.Text, copy.Text);
        Assert.Equal(original.Start, copy.Start, Precision);
        Assert.Equal(original.End, copy.End, Precision);
        Assert.Equal(Math.Clamp(original.X + 0.02, 0, 1), copy.X, Precision);
        Assert.Equal(Math.Clamp(original.Y + 0.02, 0, 1), copy.Y, Precision);

        session.Undo();
        Assert.Equal(2, Preview.LastProject!.Overlays.Annotations.Length);

        session.SelectNothing();
        Assert.Equal(new StudioAnnotationEditResult(false, null), session.DuplicateSelectedAnnotation());
    }

    [Fact]
    public async Task ChoosingAnEmoji_IsOneUndoStep_AndABlankOneIsIgnored()
    {
        var session = await OpenAsync(CreateProject());
        session.AddAnnotationAtPlayhead(StudioAnnotationKinds.Emoji);
        Assert.Equal("\U0001F44D", session.SelectedAnnotation!.Text);
        Assert.False(session.SelectedAnnotation.Shadow);

        session.SetAnnotationEmoji(0, "\U0001F680");
        session.SetAnnotationShadow(0, true);
        session.SetAnnotationSize(0, StudioAnnotationSize.Large);
        Assert.Equal(new StudioAnnotationEditResult(false, 0), session.SetAnnotationEmoji(0, " "));

        var emoji = session.SelectedAnnotation!;
        Assert.Equal("\U0001F680", emoji.Text);
        Assert.True(emoji.Shadow);
        Assert.Equal(StudioAnnotationSize.Large, emoji.Size);

        session.Undo();
        session.Undo();
        Assert.Equal("\U0001F680", session.SelectedAnnotation!.Text);
        session.Undo();
        Assert.Equal("\U0001F44D", session.SelectedAnnotation!.Text);
    }

    [Fact]
    public async Task AnArea_ShowsItsRect_AndADragOfACornerIsOneUndoStep()
    {
        var session = await OpenAsync(CreateProject());
        session.AddAnnotationAtPlayhead(StudioAnnotationKinds.Rectangle);
        var handle = Assert.Single(session.GetAnnotationHandles(1920, 1080));
        Assert.Equal(StudioAnnotationKinds.Rectangle, handle.Kind);
        var box = handle.Box!.Value;
        Assert.True(box.Width > 0 && box.Height > 0);

        var start = session.SelectedAnnotation!;
        session.BeginGesture();
        session.DragAnnotation(0, start, StudioAnnotationPart.BottomRight, 50, 0, null, 1920, 1080);
        session.DragAnnotation(0, start, StudioAnnotationPart.BottomRight, 100, 40, null, 1920, 1080);
        session.EndGesture();
        Assert.True(session.SelectedAnnotation!.Width > start.Width);

        session.Undo();
        Assert.Equal(start.Width, session.SelectedAnnotation!.Width, Precision);
        Assert.Equal(start.X, session.SelectedAnnotation!.X, Precision);
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
