using TinyClips.Core.Studio;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 8. Accessibility, as far as it can be read without a screen reader: what the window's UI
// Automation tree holds in each of its states.
internal sealed partial class WindowChecks
{
    // The controls a person operates. Each needs an automation id as well as a name.
    private static readonly int[] Operated =
    [
        ControlTypeNames.Button, ControlTypeNames.CheckBox, ControlTypeNames.ComboBox, ControlTypeNames.RadioButton, ControlTypeNames.Slider, ControlTypeNames.ListItem,
    ];

    /// <summary>
    /// The window's own content, as a screen reader walks it: everything inside the two panes in
    /// which the framework hosts XAML in a window. Those two are the framework's, and have no name
    /// in any WinUI window.
    /// </summary>
    private static List<(int Depth, UiaElement Element)> Content(Editor editor)
    {
        var island = editor.Root.Children().FirstOrDefault(child => child.ClassName == "Microsoft.UI.Content.DesktopChildSiteBridge");
        return island is null ? [] : [.. Tree(island).Where(entry => entry.Element.ClassName is not ("Microsoft.UI.Content.DesktopChildSiteBridge" or "InputSiteWindowClass"))];
    }

    /// <summary>What is wrong with a tree, one line for each element, and the panes without a name.</summary>
    private static (List<string> Problems, int Elements, int Sliders, int UnnamedPanes) Audit(List<(int Depth, UiaElement Element)> tree)
    {
        var problems = new List<string>();
        var sliders = 0;
        var panes = 0;
        foreach (var (_, element) in tree)
        {
            var type = element.ControlType;
            var name = element.Name.Trim();
            var what = $"{element.ControlTypeName} [{(element.Id.Length > 0 ? element.Id : "no id")}] of class {element.ClassName}";
            if (name.Length == 0)
            {
                // A pane that cannot take the keyboard focus is a container, and needs no name.
                if (type == ControlTypeNames.Pane && !element.IsKeyboardFocusable)
                {
                    panes++;
                }
                else
                {
                    problems.Add($"{what} has no name");
                }
            }

            if (Operated.Contains(type) && element.Id.Length == 0)
            {
                problems.Add($"{element.ControlTypeName} \"{name}\" has no automation id");
            }

            if (type == ControlTypeNames.Slider)
            {
                sliders++;
                if (element.Range is not { } range)
                {
                    problems.Add($"{what} reports no range");
                }
                else if (!(range.Minimum < range.Maximum) || range.Value < range.Minimum - 1e-6 || range.Value > range.Maximum + 1e-6 || !(range.SmallChange > 0))
                {
                    problems.Add($"{what} reports {F(range.Value)} in {F(range.Minimum)} to {F(range.Maximum)} with a step of {F(range.SmallChange, "0.####")}");
                }
                else if (range.IsReadOnly == element.IsEnabled)
                {
                    problems.Add($"{what} is {(element.IsEnabled ? "enabled" : "disabled")} and read-only: {range.IsReadOnly}");
                }

                if (string.IsNullOrWhiteSpace(element.ValueText))
                {
                    problems.Add($"{what} has no value as text");
                }
            }
        }

        return (problems, tree.Count, sliders, panes);
    }

    /// <summary>Audits the window as it is now, saves the tree next to the report, and records one check.</summary>
    private void AuditState(Editor editor, string state, string fileName, int slidersWanted, Func<List<(int Depth, UiaElement Element)>, string?>? also = null)
    {
        var tree = Content(editor);
        SaveTree(fileName, tree);
        var (problems, elements, sliders, panes) = Audit(tree);
        var extra = also?.Invoke(tree);
        if (extra is not null)
        {
            problems.Add(extra);
        }

        if (sliders != slidersWanted)
        {
            problems.Add($"{sliders} sliders, where {slidersWanted} were expected");
        }

        _report.Check(
            $"{state}: every control has a name, every control a person operates has an automation id, and every slider reports its value inside its range, its step and its value as text",
            elements > 0 && problems.Count == 0,
            problems.Count == 0 ? $"{elements} elements, {sliders} sliders; {panes} pane(s) without a name, none of which can take the focus; tree saved as {fileName}" : string.Join("; ", problems));
    }

    private void Accessibility()
    {
        Timeline.Mark("8: accessibility");

        // While the project opens: read at once, because it lasts less than a second.
        var editor = Open(NewCameraProject("Accessibility"), "accessibility");
        var opening = Content(editor);
        var ring = opening.Select(entry => entry.Element).FirstOrDefault(element => element.Id == "StudioLoadingRing");
        if (ring is null)
        {
            _report.Note("the project had opened before the window could be read while it was still opening");
        }
        else
        {
            SaveTree("tree-opening.txt", opening);
            var (wrong, count, _, _) = Audit(opening);
            _report.Check(
                "while the project opens: the progress ring says what is happening, and every element has a name",
                ring.Name.Contains("Opening the project", StringComparison.Ordinal) && wrong.Count == 0,
                wrong.Count == 0 ? $"{count} elements; the ring is called \"{ring.Name}\"; tree saved as tree-opening.txt" : string.Join("; ", wrong));
        }

        if (WaitLoaded(editor) != "ready")
        {
            _report.Check("accessibility: the editor becomes ready", false);
            return;
        }

        var sight = LookFor(editor, s => s.Shown == Both(editor, FrameOf(CameraOffset)), 5);

        // The editor with the bubble layout: padding, two sliders for the screen, three for the camera's place, two for its style, and the three of the trim bar.
        AuditState(editor, "the editor, bubble layout", "tree-editor.txt", 11);

        // The canvas: one image.
        var canvas = Find(editor, "StudioPreview");
        var inside = canvas?.Children().Count ?? -1;
        var (x, y, width, height) = canvas?.Bounds ?? default;
        var picture = sight?.Canvas;
        var apart = picture is { } box && sight is not null
            ? new[] { Math.Abs(x - sight.Shot.ScreenX - box.X), Math.Abs(y - sight.Shot.ScreenY - box.Y), Math.Abs(width - box.Width), Math.Abs(height - box.Height) }.Max()
            : int.MaxValue;
        _report.Check(
            "the canvas is one image called Preview, with nothing inside it for a screen reader, whose description states the layout and whose rectangle is the picture's",
            canvas is { ControlType: ControlTypeNames.Image, Name: "Preview", HelpText: "Screen with camera bubble, camera bottom right" } && inside == 0 && apart <= 2 && !canvas.IsKeyboardFocusable,
            $"{canvas}, described as \"{canvas?.HelpText}\", {inside} element(s) inside; its rectangle is {width}x{height}, no edge more than {apart} px from the picture's");

        // What the buttons say about their keys.
        string Keys(string id) => Find(editor, id, 0.5)?.AcceleratorKey ?? string.Empty;
        _report.Check(
            "Undo, Redo and Export tell a screen reader their keys",
            Keys("StudioUndoButton") == "Ctrl+Z" && Keys("StudioRedoButton") == "Ctrl+Y" && Keys("StudioExportButton") == "Ctrl+E",
            $"\"{Keys("StudioUndoButton")}\", \"{Keys("StudioRedoButton")}\", \"{Keys("StudioExportButton")}\"");

        // The other layouts bring other controls.
        Timeline.Mark("8: the other layouts");
        Find(editor, "StudioLayoutSideBySide")?.Select();
        Until(() => editor.Root.Find("StudioCameraShareSlider"), found => found is not null, 2);
        AuditState(editor, "the editor, side by side", "tree-side-by-side.txt", 9, _ => Find(editor, "StudioPreview", 0.5)?.HelpText == "Side by side" ? null : $"the canvas is described as \"{Find(editor, "StudioPreview", 0)?.HelpText}\"");
        Find(editor, "StudioLayoutScreen")?.Select();
        Until(() => editor.Root.Find("StudioCameraHiddenNote"), found => found is not null, 2);
        AuditState(editor, "the editor, screen only", "tree-screen.txt", 6, _ => Find(editor, "StudioPreview", 0.5)?.HelpText == "Screen only" ? null : $"the canvas is described as \"{Find(editor, "StudioPreview", 0)?.HelpText}\"");
        Find(editor, "StudioLayoutBubble")?.Select();
        Until(() => editor.Root.Find("StudioCameraSizeSlider"), found => found is not null, 2);

        // While exporting: the overlay is what there is to read, and everything under it is disabled.
        Timeline.Mark("8: the export overlay");
        Invoke(editor, "StudioExportButton");
        Find(editor, "StudioCancelExportButton", 3);
        AuditState(editor, "while exporting", "tree-exporting.txt", 11, tree =>
        {
            var enabled = tree.Where(e => Operated.Contains(e.Element.ControlType) && e.Element.IsEnabled && e.Element.Id != "StudioCancelExportButton").Select(e => e.Element.Id).ToArray();
            var bar = Find(editor, "StudioExportProgressBar", 0.5);
            return enabled.Length > 0 ? $"still enabled under the overlay: {string.Join(", ", enabled)}"
                : bar?.Range is not { Minimum: 0, Maximum: 100 } ? "the progress bar reports no range from 0 to 100"
                : null;
        });
        Invoke(editor, "StudioCancelExportButton");
        Gone(editor, "StudioCancelExportButton", 15);

        // With a message shown: an export that cannot be written.
        Timeline.Mark("8: the message bar");
        var inTheWay = BlockNextExport();
        Invoke(editor, "StudioExportButton");
        MessageBar(editor, 20);
        Gone(editor, "StudioCancelExportButton", 5);
        File.Delete(inTheWay);
        var (bar, texts, close) = MessageBar(editor, 2);
        AuditState(editor, "with a message shown", "tree-message.txt", 11, _ =>
            bar is not { Name: "Error" } ? $"the message bar is {(bar is null ? "not shown" : $"called \"{bar.Name}\"")}"
            : !texts.Any(text => text.StartsWith("Studio export failed", StringComparison.Ordinal)) ? $"the message is not a text of the bar: {string.Join(" | ", texts)}"
            : close is not { Name.Length: > 0 } ? "the bar has no close button with a name"
            : null);
        close?.Invoke();
        Gone(editor, "StudioErrorBar", 3);

        // The question on closing.
        Timeline.Mark("8: the question on closing");
        PressClose(editor);
        var question = Dialog(editor);
        Thread.Sleep(400);
        var dialogTree = question is null ? [] : Tree(question);
        SaveTree("tree-close-question.txt", dialogTree);
        var (problems, elements, _, panes) = Audit(dialogTree);
        var buttons = dialogTree.Where(e => e.Element.ControlType == ControlTypeNames.Button).Select(e => e.Element.Name).ToArray();
        _report.Check(
            "the question on closing: it is a window with the question as its name, and every button and text in it has a name",
            question is { ControlType: ControlTypeNames.Window, Name: "Export this recording before closing?" } && problems.Count == 0 && buttons.Length == 4,
            problems.Count == 0 ? $"{elements} elements; buttons: {string.Join(", ", buttons)}; {panes} pane(s) without a name; tree saved as tree-close-question.txt" : string.Join("; ", problems));
        question?.Find("CloseButton")?.Invoke();
        Until(() => !HasDialog(editor), gone => gone, 3);
        CloseQuietly(editor);

        // A recording without a camera, and a project that cannot be opened.
        Timeline.Mark("8: without a camera, and unavailable");
        if (OpenReady(NewScreenProject("Accessibility, screen"), "accessibility, screen only") is { } screenOnly)
        {
            AuditState(screenOnly, "a recording without a camera", "tree-no-camera.txt", 6);
            CloseQuietly(screenOnly);
        }

        var missing = Open(NewScreenProject("Accessibility, gone", writeScreen: false), "accessibility, unavailable");
        if (WaitLoaded(missing) == "unavailable")
        {
            AuditState(missing, "a project that cannot be opened", "tree-unavailable.txt", 0, tree =>
                tree.Any(e => e.Element is { Id: "StudioUnavailablePanel", IsKeyboardFocusable: true, ControlType: ControlTypeNames.Group }) ? null : "the message is not an element that can take the focus");
        }
        else
        {
            _report.Check("a project that cannot be opened says so", false);
        }

        CloseQuietly(missing);
    }
}
