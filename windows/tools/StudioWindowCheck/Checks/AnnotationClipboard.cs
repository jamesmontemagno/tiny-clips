using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// Native clipboard and UI Automation checks, deliberately without screenshot capture.
internal sealed partial class WindowChecks
{
    private void AnnotationClipboardActions()
    {
        _report.Check("the window maps V to the annotation Paste keyboard model",
            TinyClips.App.Views.Studio.StudioWindow.MapKey(VirtualKey.V) == StudioShortcutKey.V, "Ctrl+V");
        var original = new StudioAnnotation
        {
            Id = "clipboard-source", Kind = StudioAnnotationKinds.Text, Text = "Transfer 👋",
            Attach = StudioAnnotationAttach.Frame, Start = 0, End = 2, X = 0.25, Y = 0.75,
        };
        var sourceFolder = NewScreenProject("Annotation clipboard source", project => project with
        {
            Overlays = project.Overlays with { Annotations = [original] },
        });
        if (OpenReady(sourceFolder, "annotation clipboard source") is not { } source)
        {
            return;
        }
        OnUi(() => source.Window.ViewModel.SelectAnnotation(0));
        var copied = Invoke(source, "StudioCopyAnnotationButton")
            && Until(() => OnUi(() => source.Window.ViewModel.AnnotationClipboardStatus), value => value == "Annotation copied.", 5) == "Annotation copied.";
        _report.Check("Copy publishes the native custom format without a plain-text JSON fallback",
            copied && OnUi(() =>
            {
                var content = Clipboard.GetContent();
                return content.Contains(StudioAnnotationClipboard.Format) && !content.Contains(StandardDataFormats.Text);
            }), "the source editor reported a successful clipboard write");
        var sourceClosed = CloseQuietly(source);
        _report.Check("the source clipboard editor closes before Paste", sourceClosed, "the source window has closed");
        if (!copied || OpenReady(NewScreenProject("Annotation clipboard destination"), "annotation clipboard destination") is not { } target)
        {
            return;
        }
        OnUi(() => target.Window.ViewModel.ShowInspectorPanel(StudioInspectorPanel.Annotate));
        var pasteReady = Until(() => OnUi(() => target.Window.ViewModel.CanPasteAnnotation), value => value, 3);
        _report.Check("Paste is reachable with no annotations or selection after the source editor closes",
            pasteReady && Find(target, "StudioPasteAnnotationButton") is { IsEnabled: true, Name: "Paste annotation" }
            && Find(target, "StudioCopyAnnotationButton") is { IsEnabled: false, Name: "Copy annotation" },
            "compact Copy and Paste controls in the empty Annotate panel");
        SetSlider(target, "StudioPlayhead", 1);
        FocusOn(target, "StudioPasteAnnotationButton");
        var firstAction = Key(target, StudioShortcutKey.V, control: true);
        var pasted = Until(() => AnnotationsOf(target), values => values.Length == 1, 5);
        var first = pasted.FirstOrDefault();
        _report.Check("Ctrl+V appends and selects an independent annotation with unchanged geometry and edited duration",
            firstAction == StudioShortcutAction.PasteAnnotation && first is not null && first.Id != original.Id
            && Same(first.X, original.X) && Same(first.Y, original.Y) && first.Attach == original.Attach
            && Same(first.Start, 1) && Same(first.End, 3) && SelectedAnnotationOf(target) == 0,
            $"action {firstAction}; pasted {first?.Id}; selected {SelectedAnnotationOf(target)}");
        if (first is not null)
        {
            FocusOn(target, AnnotationBox);
            _report.Check("focused annotation text keeps native Ctrl+C and Ctrl+V",
                Key(target, StudioShortcutKey.C, control: true) == StudioShortcutAction.None
                && Key(target, StudioShortcutKey.V, control: true) == StudioShortcutAction.None,
                "neither key is consumed as an annotation clipboard action");
            FocusOn(target, "StudioPasteAnnotationButton");
            Until(() => OnUi(() => target.Window.ViewModel.CanPasteAnnotation), value => value, 3);
            var repeatedAction = Key(target, StudioShortcutKey.V, control: true);
            var repeated = Until(() => AnnotationsOf(target), values => values.Length == 2, 5);
            _report.Check("repeated Paste generates a fresh top-layer ID without a spatial nudge",
                repeatedAction == StudioShortcutAction.PasteAnnotation && repeated.Length == 2
                && repeated[1].Id != first.Id && Same(repeated[1].X, first.X) && Same(repeated[1].Y, first.Y),
                $"{repeated.Length} annotations after the second paste");
            Invoke(target, "StudioUndoButton");
            var undone = Until(() => AnnotationsOf(target), values => values.Length == 1, 3);
            Invoke(target, "StudioRedoButton");
            var redone = Until(() => AnnotationsOf(target), values => values.Length == 2, 3);
            _report.Check("Undo removes only the paste and Redo restores the same ID",
                undone.Length == 1 && redone.Length == 2 && repeated.Length == 2 && redone[1].Id == repeated[1].Id,
                "one paste is one undo transaction");
        }
        OnUi(() =>
        {
            var text = new DataPackage();
            text.SetText("Ordinary native text");
            Clipboard.SetContent(text);
            Clipboard.Flush();
        });
        var unrelated = Until(() => OnUi(() => target.Window.ViewModel.CanPasteAnnotation), value => !value, 3);
        _report.Check("ordinary text is not interpreted as an annotation",
            !unrelated && Key(target, StudioShortcutKey.V, control: true) == StudioShortcutAction.None,
            "annotation Paste disables after the native clipboard content-change notification");
        CloseQuietly(target);
    }
}
