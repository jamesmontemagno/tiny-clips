using Microsoft.UI.Xaml.Controls;
using TinyClips.App.Views.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Host;
using Windows.System;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// The annotations: the tools that add them, the lane that comes with the first and goes with the
// last, and the Annotate panel. Everything is read through UI Automation and from the project, so
// this group takes no pictures. What an annotation looks like is checked by StudioRenderCheck.
internal sealed partial class WindowChecks
{
    private const string AnnotationLane = "StudioAnnotationLane";
    private const string AnnotationList = "StudioAnnotationLayerList";
    private const string AnnotationBox = "StudioAnnotationTextBox";

    private StudioAnnotation[] AnnotationsOf(Editor editor) => OnUi(() => editor.Window.ViewModel.Annotations.ToArray());

    private int? SelectedAnnotationOf(Editor editor) => OnUi(() => editor.Window.ViewModel.SelectedAnnotationIndex);

    /// <summary>The rows of the list of annotations, top first, with a star before the selected one.</summary>
    private string AnnotationRows(Editor editor) => OnUi(() =>
    {
        Descendant<ListView>(editor.Window.Content, AnnotationList)?.UpdateLayout();
        var list = Descendant<ListView>(editor.Window.Content, AnnotationList);
        return list is null
            ? "no list"
            : string.Join(" | ", list.Items.OfType<ListViewItem>().Select(item => (ReferenceEquals(list.SelectedItem, item) ? "*" : string.Empty) + item.Content));
    });

    private void Annotations()
    {
        Timeline.Mark("16: annotations");
        _report.Check(
            "the window maps the A, C and T keys to the editor's keyboard model",
            StudioWindow.MapKey(VirtualKey.A) == StudioShortcutKey.A && StudioWindow.MapKey(VirtualKey.C) == StudioShortcutKey.C && StudioWindow.MapKey(VirtualKey.T) == StudioShortcutKey.T,
            $"A is {StudioWindow.MapKey(VirtualKey.A)}, C is {StudioWindow.MapKey(VirtualKey.C)}, T is {StudioWindow.MapKey(VirtualKey.T)}");
        AddingAnnotations();
        AnnotationsAProjectAlreadyHolds();
    }

    private void AddingAnnotations()
    {
        if (OpenReady(NewScreenProject("Annotations"), "annotations") is not { } editor)
        {
            return;
        }

        using var heard = UiaEvents.Listen(_uia, editor.Root);
        SetSlider(editor, "StudioPlayhead", 2.0);

        _report.Check(
            "a recording without annotations has no annotation lane, the header offers the three tools, and the Annotate panel says how to add one",
            Absent(editor, AnnotationLane, 0.5) && Find(editor, "StudioAddArrowButton") is { IsEnabled: true, Name: "Add arrow" }
                && Find(editor, "StudioAddCalloutButton") is { IsEnabled: true, Name: "Add callout" } && Find(editor, "StudioAddTextButton") is { IsEnabled: true, Name: "Add text" }
                && NameOf(editor, "StudioAnnotationEmptyHint", 1) == StudioEditorText.AnnotationEmptyHint && Absent(editor, "StudioDeleteAnnotationButton", 0.3),
            $"the lane: {LaneText(editor, AnnotationLane)}; the hint: \"{NameOf(editor, "StudioAnnotationEmptyHint", 0.5)}\"");

        // A brings the lane, with the arrow selected and its panel on show.
        Timeline.Mark("16: A adds an arrow at the playhead");
        var mark = heard.Mark();
        var key = Key(editor, StudioShortcutKey.A);
        var said = Said(heard, mark, StudioEditorText.GetAnnotationAddedMessage(StudioAnnotationKinds.Arrow));
        var lane = Until(() => LaneText(editor, AnnotationLane), text => text != "empty", 2);
        var one = AnnotationsOf(editor);
        var panel = PanelShown(editor, "Annotate", 1);
        _report.Check(
            "what A runs adds an arrow at the playhead for three seconds, which brings the lane with it, selected, with the Annotate panel on show",
            key == StudioShortcutAction.AddArrow && one.Length == 1 && one[0].Kind == StudioAnnotationKinds.Arrow && Same(one[0].Start, 2) && Same(one[0].End, 5)
                && one[0].Id.Length > 0 && lane == "*" + StudioEditorText.GetAnnotationDescription(one[0]) && SelectedAnnotationOf(editor) == 0 && IsShown(panel, "Annotate"),
            $"the key ran {key}; the lane: {lane}; the panel: {PanelWords(panel)}");
        _report.Check("a screen reader is told \"Arrow added.\"", said.Said, $"sent: {said.Heard}");

        // A tool and a key add the others on top, and the list says so.
        Timeline.Mark("16: a callout by its tool, and text by T");
        var pressed = Invoke(editor, "StudioAddCalloutButton");
        var two = Until(() => AnnotationsOf(editor).Length, count => count == 2, 1.5);
        var keyForText = Key(editor, StudioShortcutKey.T);
        var three = Until(() => AnnotationsOf(editor).Length, count => count == 3, 1.5);
        var all = AnnotationsOf(editor);
        var rows = AnnotationRows(editor);
        _report.Check(
            "the Callout tool and what T runs add on top: the last one added is selected, the lane has all three, and the list has them top first",
            pressed && two == 2 && three == 3 && keyForText == StudioShortcutAction.AddText
                && all.Select(item => item.Kind).SequenceEqual([StudioAnnotationKinds.Arrow, StudioAnnotationKinds.Callout, StudioAnnotationKinds.Text])
                && all.Select(item => item.Id).Distinct().Count() == 3 && SelectedAnnotationOf(editor) == 2 && LaneItems(editor, AnnotationLane).Count == 3
                && rows == "*Text: Your text | Callout: Your text | Arrow",
            $"pressed: {pressed}; T ran {keyForText}; {all.Length} annotations, selected {SelectedAnnotationOf(editor)}; rows: {rows}");

        EditingTheSelectedAnnotation(editor);
        DeletingAnnotations(editor, heard);
        CloseQuietly(editor);
    }

    private void EditingTheSelectedAnnotation(Editor editor)
    {
        // Typing goes through the real box, whose change notice comes later, and is one undo step.
        Timeline.Mark("16: typing");
        var box = Find(editor, AnnotationBox);
        var typed = box?.SetValue("Hello") == true && Until(() => AnnotationsOf(editor)[2].Text, text => text == "Hello", 2) == "Hello";
        typed = typed && box?.SetValue("Hello again") == true && Until(() => AnnotationsOf(editor)[2].Text, text => text == "Hello again", 2) == "Hello again";
        var undone = Invoke(editor, "StudioUndoButton") && Until(() => AnnotationsOf(editor)[2].Text, text => text == "Your text", 2) == "Your text";
        var boxAfterUndo = Until(() => Find(editor, AnnotationBox)?.ValueText, text => text == "Your text", 2);
        var redone = Invoke(editor, "StudioRedoButton") && Until(() => AnnotationsOf(editor)[2].Text, text => text == "Hello again", 2) == "Hello again";
        _report.Check(
            "what is typed in the box reaches the selected annotation, one Undo takes back all of it and the box follows, and Redo brings it back",
            typed && undone && boxAfterUndo == "Your text" && redone && SelectedAnnotationOf(editor) == 2,
            $"typed: {typed}; undone: {undone}, the box reads \"{boxAfterUndo}\"; redone: {redone}; selected {SelectedAnnotationOf(editor)}");

        // The real controls, which hand back what they are told to show.
        Timeline.Mark("16: style and place");
        var chose = Find(editor, "StudioAnnotationStyle_Blue")?.Toggle() == true;
        var style = Until(() => AnnotationsOf(editor)[2].Style, value => value == StudioAnnotationStyle.Blue, 1.5);
        var chipOn = OnUi(() => Descendant<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>(editor.Window.Content, "StudioAnnotationStyle_Blue")?.IsChecked);
        var scaled = SetSlider(editor, "StudioAnnotationScaleSlider", 2);
        var scale = Until(() => AnnotationsOf(editor)[2].Scale, value => Same(value, 2, 1e-3), 1.5);
        _report.Check(
            "a style chip chooses the look and shows it is in use, and the Size slider changes the selected annotation",
            chose && style == StudioAnnotationStyle.Blue && chipOn == true && scaled && Same(scale, 2, 1e-3),
            $"chose {style}, the chip is on: {chipOn}; the slider was set: {scaled}; scale {scale}");

        // Colors chosen by hand are one undo step, and a style takes them back.
        OnUi(() =>
        {
            editor.Window.ViewModel.SetAnnotationFillColor(Windows.UI.Color.FromArgb(255, 1, 2, 3));
            editor.Window.ViewModel.SetAnnotationFillColor(Windows.UI.Color.FromArgb(255, 18, 171, 159));
        });
        var fill = Until(() => AnnotationsOf(editor)[2].Fill, value => value == "#12AB9F", 1.5);
        var chipOff = OnUi(() => Descendant<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>(editor.Window.Content, "StudioAnnotationStyle_Blue")?.IsChecked);
        var note = NameOf(editor, "StudioAnnotationCustomNote", 1);
        Invoke(editor, "StudioUndoButton");
        var noFill = Until(() => AnnotationsOf(editor)[2].Fill, value => value.Length == 0, 1.5);
        Invoke(editor, "StudioRedoButton");
        Until(() => AnnotationsOf(editor)[2].Fill, value => value == "#12AB9F", 1.5);
        Find(editor, "StudioAnnotationStyle_Blue")?.Toggle();
        var backToStyle = Until(() => AnnotationsOf(editor)[2].Fill, value => value.Length == 0, 1.5);
        _report.Check(
            "a color chosen by hand turns the chips off and says Custom; choosing colors is one undo step; a chip takes them back",
            fill == "#12AB9F" && chipOff == false && note == "Custom" && noFill.Length == 0 && backToStyle.Length == 0,
            $"fill {fill}; the chip is on: {chipOff}; the note: \"{note}\"; after Undo \"{noFill}\"; after the chip \"{backToStyle}\"");

        // Advanced is closed until it is asked for, and holds the rest.
        var closed = Absent(editor, "StudioAnnotationXSlider", 0.3);
        OnUi(() => Descendant<Expander>(editor.Window.Content, "StudioAnnotationAdvancedGroup")!.IsExpanded = true);
        var opened = Until(() => Find(editor, "StudioAnnotationXSlider", 0.3) is not null, found => found, 1.5);
        _report.Check("Advanced starts closed, and opening it shows the position's sliders", closed && opened, $"closed: {closed}; opened: {opened}");
        var moved = SetSlider(editor, "StudioAnnotationXSlider", 0.25);
        var x = Until(() => AnnotationsOf(editor)[2].X, value => Same(value, 0.25, 1e-3), 1.5);
        _report.Check(
            "the Style choice and the Horizontal slider change the selected annotation",
            (int)style == 1 && moved && Same(x, 0.25, 1e-3) && Same(SliderValue(editor, "StudioAnnotationXSlider"), 0.25, 1e-3),
            $"style {style}; the slider was set: {moved}; x {x}");

        // Layers: the list's order is the drawing order, and the selection stays on the same annotation.
        Timeline.Mark("16: layers");
        var ids = AnnotationsOf(editor).Select(item => item.Id).ToArray();
        var topIsOff = Find(editor, "StudioBringAnnotationForwardButton") is { IsEnabled: false };
        var back = Invoke(editor, "StudioSendAnnotationBackButton");
        var backIds = Until(() => AnnotationsOf(editor).Select(item => item.Id).ToArray(), now => now.SequenceEqual([ids[0], ids[2], ids[1]]), 1.5);
        var rowsBack = AnnotationRows(editor);
        var undoneBack = Invoke(editor, "StudioUndoButton");
        var restored = Until(() => AnnotationsOf(editor).Select(item => item.Id).ToArray(), now => now.SequenceEqual(ids), 1.5);
        _report.Check(
            "Bring forward is off for the top one; Send back moves it under the next, the selection and the list follow it, and Undo puts it back selected",
            topIsOff && back && backIds.SequenceEqual([ids[0], ids[2], ids[1]]) && rowsBack == "Callout: Your text | *Text: Hello again | Arrow"
                && undoneBack && restored.SequenceEqual(ids) && SelectedAnnotationOf(editor) == 2,
            $"top is off: {topIsOff}; rows after Send back: {rowsBack}; after Undo selected {SelectedAnnotationOf(editor)}");

        // A drag on the preview, by what the overlay's pointer handlers call: one undo step.
        Timeline.Mark("16: dragging");
        var head0 = Playhead(editor);
        var handles = OnUi(() => editor.Window.ViewModel.AnnotationHandles.ToArray());
        var before = AnnotationsOf(editor);
        var dragged = OnUi(() =>
        {
            var viewModel = editor.Window.ViewModel;
            var start = viewModel.BeginAnnotationDrag(0)!;
            viewModel.BeginGesture();
            viewModel.DragAnnotation(0, start, TinyClips.Core.Studio.StudioAnnotationPart.Body, 100, 0);
            var move = viewModel.DragAnnotation(0, start, TinyClips.Core.Studio.StudioAnnotationPart.Body, 200, 0);
            viewModel.EndGesture();
            return move;
        });
        var after = AnnotationsOf(editor);
        var selectedThen = SelectedAnnotationOf(editor);
        Invoke(editor, "StudioUndoButton");
        var undoneDrag = Until(() => AnnotationsOf(editor)[0].X, value => Same(value, before[0].X), 1.5);
        _report.Check(
            "at the playhead the preview can take hold of all three; a drag by the shaft moves an arrow by what it was dragged, selects it, and is one undo step",
            handles.Length == 3 && handles[0].Tail is not null && handles[1].Box is not null && handles[2].Box is not null && dragged is not null
                && after[0].X > before[0].X && Same(after[0].OffsetX, before[0].OffsetX) && selectedThen == 0 && Same(undoneDrag, before[0].X) && Same(Playhead(editor), head0),
            $"{handles.Length} handles; x {before[0].X} to {after[0].X}; selected {selectedThen}; after Undo {undoneDrag}");
        OnUi(() => editor.Window.ViewModel.SelectAnnotation(2));
        Until(() => SelectedAnnotationOf(editor), index => index == 2, 1);

        // The list: reaching it changes nothing, and choosing a row selects that annotation.
        Timeline.Mark("16: the list");
        OnUi(() => editor.Window.ViewModel.SelectAnnotation(null));
        Until(() => SelectedAnnotationOf(editor), index => index is null, 1);
        var head = Playhead(editor);
        var focused = FocusOn(editor, AnnotationList);
        Thread.Sleep(200);
        var followsFocus = OnUi(() => Descendant<ListView>(editor.Window.Content, AnnotationList)?.SingleSelectionFollowsFocus);
        var stillNone = SelectedAnnotationOf(editor) is null && Same(Playhead(editor), head);
        var choseRow = Find(editor, "StudioAnnotationLayer_2")?.Select() == true;
        var chosen = Until(() => SelectedAnnotationOf(editor), index => index == 0, 1.5);
        _report.Check(
            "the keyboard focus reaching the list selects nothing and leaves the playhead; choosing its bottom row selects the bottom annotation",
            followsFocus == false && stillNone && choseRow && chosen == 0,
            $"the focus went to \"{focused}\"; selection follows the focus: {followsFocus}; nothing selected and the playhead still: {stillNone}; chose: {choseRow}, selected {chosen}");
    }

    private void DeletingAnnotations(Editor editor, UiaEvents heard)
    {
        Timeline.Mark("16: deleting");
        FocusOn(editor, "StudioPlayPauseButton");
        var mark = heard.Mark();
        var key = Key(editor, StudioShortcutKey.Delete);
        var said = Said(heard, mark, StudioEditorText.AnnotationDeletedMessage);
        var left = Until(() => AnnotationsOf(editor).Length, count => count == 2, 1.5);
        var undone = Invoke(editor, "StudioUndoButton");
        var back = Until(() => AnnotationsOf(editor).Length, count => count == 3, 1.5);
        _report.Check(
            "what Delete runs takes away the selected annotation and says so; Undo brings it back and selects nothing, as with a cut",
            key == StudioShortcutAction.RemoveSelectedAnnotation && said.Said && left == 2 && undone && back == 3 && SelectedAnnotationOf(editor) is null,
            $"Delete ran {key}, {left} left; sent: {said.Heard}; after Undo {back}, selected {SelectedAnnotationOf(editor)}");

        // With the Delete annotation button, one after the other. The last takes the lane and the list with it.
        var focusAfter = new List<string>();
        for (var count = 3; count > 0; count--)
        {
            OnUi(() => editor.Window.ViewModel.SelectAnnotation(0));
            Until(() => SelectedAnnotationOf(editor), index => index == 0, 1);
            FocusOn(editor, "StudioDeleteAnnotationButton");
            Invoke(editor, "StudioDeleteAnnotationButton");
            Until(() => AnnotationsOf(editor).Length, now => now == count - 1, 1.5);
            focusAfter.Add(Until(() => FocusedId(editor), id => id != "StudioDeleteAnnotationButton", 1));
        }

        var inPanel = focusAfter.Take(2).All(id => id is "StudioNextAnnotationButton" or "StudioPreviousAnnotationButton");
        var onRail = focusAfter[2].StartsWith(InspectorRailId, StringComparison.Ordinal);
        var laneGone = Absent(editor, AnnotationLane, 1) && LaneText(editor, AnnotationLane) == "empty";
        var hint = NameOf(editor, "StudioAnnotationEmptyHint", 1);
        _report.Check(
            "Delete annotation keeps the focus in the inspector: on a button that steps while some are left, then on the rail; the last one takes the lane with it and the panel says how to add one",
            inPanel && onRail && laneGone && hint == StudioEditorText.AnnotationEmptyHint,
            $"the focus went to: {string.Join(", ", focusAfter)}; the lane is gone: {laneGone}; the hint: \"{hint}\"");

        var lanes = Invoke(editor, "StudioUndoButton") && Until(() => LaneText(editor, AnnotationLane), text => text != "empty", 2) != "empty";
        _report.Check("Undo after the last one is deleted brings the lane back", lanes, $"the lane: {LaneText(editor, AnnotationLane)}");
    }

    /// <summary>
    /// A project that opens with annotations in it, one of which holds what its sliders cannot show
    /// and the other a line break written another way. All of that is allowed, and showing it is no edit.
    /// </summary>
    private void AnnotationsAProjectAlreadyHolds()
    {
        Timeline.Mark("16: a project that holds annotations");
        var folder = NewScreenProject("Annotations held", project => project with
        {
            Overlays = project.Overlays with
            {
                Annotations =
                [
                    new StudioAnnotation { Id = "a", Kind = StudioAnnotationKinds.Arrow, Start = 1, End = 3, X = -1, Y = 1.5, OffsetX = 5, OffsetY = -5 },
                    new StudioAnnotation { Id = "b", Kind = StudioAnnotationKinds.Text, Start = 2, End = 4, X = 0.5, Y = 0.5, Text = "one\r\ntwo" },
                ],
            },
        });
        if (OpenReady(folder, "annotations held") is not { } editor)
        {
            return;
        }

        var rows = Until(() => { Find(editor, AnnotationList); return AnnotationRows(editor); }, text => text == "Text: one two | Arrow", 2);
        _report.Check(
            "a project that opens with annotations has their lane and fills the list, with nothing selected",
            LaneItems(editor, AnnotationLane).Count == 2 && rows == "Text: one two | Arrow" && SelectedAnnotationOf(editor) is null,
            $"the lane: {LaneText(editor, AnnotationLane)}; rows: {rows}; selected {SelectedAnnotationOf(editor)}");

        foreach (var index in new[] { 0, 1, 0 })
        {
            OnUi(() => editor.Window.ViewModel.SelectAnnotation(index));
            Until(() => SelectedAnnotationOf(editor), selected => selected == index, 1);

            // What the text box says it has changed to comes a moment after it is given a text.
            Thread.Sleep(300);
        }

        var held = AnnotationsOf(editor);
        var canUndo = Find(editor, "StudioUndoButton", 0.5)?.IsEnabled;
        var arrow = SliderValue(editor, "StudioAnnotationXSlider");
        _report.Check(
            "selecting them edits nothing: what no slider can show stays in the project, as does the line break, and there is nothing to undo",
            canUndo == false && Same(held[0].X, -1) && Same(held[0].Y, 1.5) && Same(held[0].OffsetX, 5) && Same(held[0].OffsetY, -5) && held[1].Text == "one\r\ntwo" && Same(arrow, 0) && Same(OnUi(() => editor.Window.ViewModel.AnnotationScale), 1),
            $"Undo is on: {canUndo}; the arrow: {held[0].X}, {held[0].Y}, {held[0].OffsetX}, {held[0].OffsetY}; its slider shows {arrow}; the text has \\r\\n: {held[1].Text.Contains("\r\n", StringComparison.Ordinal)}");

        // Moving the slider is the edit that clamps what was held.
        var moved = SetSlider(editor, "StudioAnnotationXSlider", 0.25);
        var x = Until(() => AnnotationsOf(editor)[0].X, value => Same(value, 0.25, 1e-3), 1.5);
        _report.Check(
            "moving a slider is an edit, and writes what it was moved to",
            moved && Same(x, 0.25, 1e-3) && Find(editor, "StudioUndoButton", 1) is { IsEnabled: true },
            $"the slider was set: {moved}; x {x}");
        CloseQuietly(editor);
    }
}
