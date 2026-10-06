using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TinyClips.Core.Studio;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Capture;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 3, continued. Keep this project: the check box of the inspector's Project section. A kept
// project is never removed by storage cleanup. It is not part of the video and not an edit: it
// is written into the project file the moment it is switched, Undo leaves it alone, and it does
// not have to wait for an export to end.
internal sealed partial class WindowChecks
{
    private const string KeepBox = "StudioKeepProjectCheckBox";
    private const string KeepHelp = "Storage cleanup never removes a kept project, so its video stays editable. Otherwise a project goes by the rules in Settings once its video has been exported.";
    private const string KeepTip = "Storage cleanup never removes a kept project, so its video stays editable";

    /// <summary>
    /// Whether the project file says that the project is kept. Read from the file itself, and
    /// not asked of the store or of the editor. Null when the file cannot be read just now.
    /// </summary>
    private static bool? KeptInFile(TestFolder folder)
    {
        try
        {
            using var stream = new FileStream(folder.Paths.ProjectJsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("keepSources", out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string KeptText(bool? kept) => kept switch { true => "kept", false => "not kept", _ => "not to be read" };

    private static string OnOff(bool? on) => on switch { true => "on", false => "off", _ => "not to be read" };

    private void KeepingTheProject()
    {
        Timeline.Mark("3: Keep this project");

        // Two seconds of video, so that an export of it lasts long enough to switch the box in.
        var folder = NewScreenProject("Kept or not", p => p with { Edits = new StudioEdits { TrimStart = 2.0, TrimEnd = 4.0 } });
        if (OpenReady(folder, "keep this project") is not { } editor)
        {
            return;
        }

        // As the project comes: not kept.
        var box = Find(editor, KeepBox);
        var inFile = KeptInFile(folder);
        var tip = OnUi(() => Descendant<CheckBox>(editor.Window.Content, KeepBox) is { } element ? ToolTipService.GetToolTip(element) as string : null);
        var walked = Content(editor).Select(entry => entry.Element).ToList();
        var (mute, keep, look) = (walked.FindIndex(e => e.Id == "StudioMuteCheckBox"), walked.FindIndex(e => e.Id == KeepBox), walked.FindIndex(e => e.Id == "StudioSaveDefaultLookButton"));
        var heading = mute < 0 ? -1 : walked.FindIndex(mute, e => e.ControlType == ControlTypeNames.Text && e.Name == "Project");
        var order = TabStops(editor);
        File.WriteAllLines(Path.Combine(_output, "tab-order-project.txt"), order);
        var (muteStop, keepStop, lookStop) = (order.IndexOf("StudioMuteCheckBox"), order.IndexOf(KeepBox), order.IndexOf("StudioSaveDefaultLookButton"));
        _report.Check(
            "Keep this project is a check box under a heading Project, after the extras and before Save as default look, both in what a screen reader walks and in the order of the Tab key; it is off for a project whose file says it is not kept, and it says what keeping means",
            box is { ControlType: ControlTypeNames.CheckBox, Name: "Keep this project", IsEnabled: true, IsKeyboardFocusable: true, IsToggledOn: false } && box.HelpText == KeepHelp && tip == KeepTip
                && inFile == false && mute >= 0 && mute < heading && heading < keep && keep < look
                && muteStop >= 0 && keepStop == muteStop + 1 && lookStop == keepStop + 1,
            $"{box}, {OnOff(box?.IsToggledOn)}, enabled {box?.IsEnabled}, can take the focus {box?.IsKeyboardFocusable}; described as \"{box?.HelpText}\"; its tooltip: \"{tip}\"; the file says {KeptText(inFile)}; "
                + $"of the {walked.Count} elements a screen reader walks, Mute audio is number {mute}, the heading Project {heading}, the check box {keep} and Save as default look {look}; "
                + $"of the {order.Count} stops of the Tab key, Mute audio is number {muteStop}, the check box {keepStop} and Save as default look {lookStop} (saved as tab-order-project.txt)");

        // An edit first, and its save, so that there is something to undo and nothing waiting to be written.
        Timeline.Mark("3: Keep this project, switched on");
        var paddingBefore = SliderValue(editor, "StudioPaddingSlider");
        var edited = SetSlider(editor, "StudioPaddingSlider", 0.12);
        var editSaved = Until(() => _services.Store.Load(editor.Id).Canvas.Padding, padding => Same(padding, 0.12), 3, 50);
        var undoBefore = Find(editor, "StudioUndoButton", 0.5)?.IsEnabled;
        var redoBefore = Find(editor, "StudioRedoButton", 0.5)?.IsEnabled;

        var watch = Stopwatch.StartNew();
        var toggled = Find(editor, KeepBox)?.Toggle() ?? false;
        var onReturn = KeptInFile(folder);
        var written = Until(() => KeptInFile(folder), kept => kept == true, 2, 5);
        var took = watch.Elapsed.TotalMilliseconds;
        var shownOn = Until(() => Find(editor, KeepBox, 0)?.IsToggledOn, on => on == true, 2);
        var heldOn = OnUi(() => editor.Window.ViewModel.KeepsProject);
        var undoAfter = Find(editor, "StudioUndoButton", 0.5)?.IsEnabled;
        var redoAfter = Find(editor, "StudioRedoButton", 0.5)?.IsEnabled;
        var paddingKept = SliderValue(editor, "StudioPaddingSlider");

        // The editor saves an edit 0.6 s after it. What is in the file sooner than that was not written by a save of edits.
        _report.Check(
            "switching Keep this project on writes it into the project file at once, and is not an edit: Undo and Redo are as they were, and the edit made before it stays",
            edited && Same(editSaved, 0.12) && toggled && written == true && took < 400 && shownOn == true && heldOn
                && undoBefore == true && redoBefore == false && undoAfter == undoBefore && redoAfter == redoBefore && Same(paddingKept, 0.12),
            $"switched through the check box: {toggled}; when that call came back the file said {KeptText(onReturn)}, and {F(took, "0")} ms after the call began it said {KeptText(written)}; the check box is {OnOff(shownOn)} and the editor holds {(heldOn ? "kept" : "not kept")}; "
                + $"Undo enabled {undoBefore} before and {undoAfter} after, Redo {redoBefore} and {redoAfter}; the padding is {F(paddingKept)}, and {F(editSaved)} in the file");

        // One Undo takes the padding back, and its save leaves what the file says about keeping.
        Invoke(editor, "StudioUndoButton");
        var undone = Until(() => SliderValue(editor, "StudioPaddingSlider"), value => Same(value, paddingBefore), 2);
        var undoSaved = Until(() => _services.Store.Load(editor.Id).Canvas.Padding, padding => Same(padding, paddingBefore), 3, 50);
        var afterUndo = (Shown: Find(editor, KeepBox, 0.5)?.IsToggledOn, InFile: KeptInFile(folder));
        Invoke(editor, "StudioRedoButton");
        var redone = Until(() => SliderValue(editor, "StudioPaddingSlider"), value => Same(value, 0.12), 2);
        var redoSaved = Until(() => _services.Store.Load(editor.Id).Canvas.Padding, padding => Same(padding, 0.12), 3, 50);
        var afterRedo = (Shown: Find(editor, KeepBox, 0.5)?.IsToggledOn, InFile: KeptInFile(folder));
        Expect(editor, p => p with { Canvas = p.Canvas with { Padding = 0.12 } });
        _report.Check(
            "one Undo after it takes back the edit made before it and leaves Keep this project alone, and so does the Redo: the check box stays on, and the file still says kept once each of the two has been saved",
            Same(undone, paddingBefore) && Same(undoSaved, paddingBefore) && afterUndo is { Shown: true, InFile: true } && Same(redone, 0.12) && Same(redoSaved, 0.12) && afterRedo is { Shown: true, InFile: true },
            $"after Undo the padding is {F(undone)}, and {F(undoSaved)} in the file, with the check box {OnOff(afterUndo.Shown)} and the file saying {KeptText(afterUndo.InFile)}; "
                + $"after Redo the padding is {F(redone)}, and {F(redoSaved)} in the file, with the check box {OnOff(afterRedo.Shown)} and the file saying {KeptText(afterRedo.InFile)}");

        // While an export runs. The editor under the export's overlay is disabled as a whole.
        // Where that leaves the check box enabled it is switched itself, and otherwise what it is bound to.
        Timeline.Mark("3: Keep this project, switched while an export runs");
        var pressed = Invoke(editor, "StudioExportButton");
        var overlay = Find(editor, "StudioCancelExportButton", 3) is not null;
        var under = Find(editor, KeepBox, 0.5);
        var enabledUnder = under?.IsEnabled == true;
        var exportingBefore = IsExporting(editor);
        var throughBox = enabledUnder && (under?.Toggle() ?? false);
        if (!throughBox)
        {
            OnUi(() => editor.Window.ViewModel.KeepsProject = false);
        }

        var offInFile = Until(() => KeptInFile(folder), kept => kept == false, 2, 5);
        var exportingAfter = IsExporting(editor);
        var finished = Until(() => _services.Exports.FirstOrDefault(e => e.ProjectId == editor.Id), e => e is not null, 120, 30);
        var idle = Gone(editor, "StudioCancelExportButton", 10);
        var afterExport = (Shown: Until(() => Find(editor, KeepBox, 0)?.IsToggledOn, on => on == false, 2), InFile: KeptInFile(folder));
        var listed = _services.Store.Load(editor.Id).Exports.Length;
        var message = ErrorOf(editor);
        _report.Check(
            "Keep this project can be switched while an export runs: it is written into the project file at once, the export goes on to its end, and afterwards the project lists its video and is still as it was switched",
            pressed && overlay && exportingBefore && offInFile == false && exportingAfter && finished is not null && idle && afterExport is { Shown: false, InFile: false } && listed == 1 && message.Length == 0,
            $"an export was running when it was switched: {exportingBefore}, and still right after: {exportingAfter}; switched {(throughBox ? "through the check box" : "through what the check box is bound to, because the check box is " + (under is null ? "not there" : "disabled") + " while the export's overlay covers the editor")}; "
                + $"the file then said {KeptText(offInFile)}; the export ended with {(finished is null ? "no video" : Path.GetFileName(finished.Path))}; afterwards the check box is {OnOff(afterExport.Shown)}, the file says {KeptText(afterExport.InFile)} and the project lists {listed} export(s)"
                + $"{(message.Length > 0 ? $"; the window says \"{message}\"" : string.Empty)}");
        if (!enabledUnder)
        {
            _report.Note("while an export runs the whole editor is disabled under the overlay, and Keep this project with it: the editor would take the change then, and the window gives no way to make it. The check above made it through the property the check box is bound to");
        }

        // Left on, closed, and opened again.
        Timeline.Mark("3: Keep this project, opened again");
        var onAgain = (Find(editor, KeepBox)?.Toggle() ?? false) && Until(() => KeptInFile(folder), kept => kept == true, 2, 5) == true;
        CloseQuietly(editor);
        var closedKept = KeptInFile(folder);
        if (OpenReady(folder, "keep this project, opened again") is not { } again)
        {
            return;
        }

        var shownAgain = Find(again, KeepBox)?.IsToggledOn;
        var heldAgain = OnUi(() => again.Window.ViewModel.KeepsProject);
        var openedKept = KeptInFile(folder);
        var reopened = _services.Store.Load(again.Id);
        _report.Check(
            "closed and opened again, the project is kept as it was left: the check box is on, the file says so, and closing and opening lost neither the edit nor the export",
            onAgain && closedKept == true && shownAgain == true && heldAgain && openedKept == true && Same(reopened.Canvas.Padding, 0.12) && reopened.Exports.Length == 1,
            $"switched on again before closing: {onAgain}; the file said {KeptText(closedKept)} once the window was closed and says {KeptText(openedKept)} now that it is open again; the check box is {OnOff(shownAgain)} and the editor holds {(heldAgain ? "kept" : "not kept")}; "
                + $"the padding in the file is {F(reopened.Canvas.Padding)}, and it lists {reopened.Exports.Length} export(s)");

        // The store refuses, once, as a disk that is full does.
        Timeline.Mark("3: Keep this project, refused by the store");
        const string Refusal = "The check's disk is full.";
        var sentence = $"Studio could not change whether this project is kept: {Refusal}";
        var refusedBefore = _services.Store.RefusedKeeps;
        var errorsBefore = _services.Errors().Length;
        using var heard = UiaEvents.Listen(_uia, again.Root);
        var mark = heard.Mark();
        _services.Store.RefuseNextKeep(Refusal);
        bool asked;
        (UiaElement? Bar, string[] Texts, UiaElement? Close) shown;
        try
        {
            asked = Find(again, KeepBox)?.Toggle() ?? false;
            shown = MessageBar(again, 5);
        }
        finally
        {
            _services.Store.RefuseNextKeep(null);
        }

        var wentBack = Until(() => Find(again, KeepBox, 0)?.IsToggledOn, on => on == true, 2);
        var heldAfter = OnUi(() => again.Window.ViewModel.KeepsProject);
        var fileAfter = KeptInFile(folder);
        var refused = _services.Store.RefusedKeeps - refusedBefore;
        var forwarded = _services.Errors().Length - errorsBefore;
        var messageShown = ErrorOf(again);
        var read = heard.WaitFor(mark, "notification", e => e.Text.Contains(Refusal, StringComparison.Ordinal), 1.5);

        // The message is taken away, and the next switch works.
        var dismissed = shown.Close?.Invoke() ?? false;
        var barGone = Gone(again, "StudioErrorBar", 3);
        var switched = Find(again, KeepBox)?.Toggle() ?? false;
        var offShown = Until(() => Find(again, KeepBox, 0)?.IsToggledOn, on => on == false, 2);
        var offFile = Until(() => KeptInFile(folder), kept => kept == false, 2, 5);
        _report.Check(
            "when the store refuses to write it, the check box goes back to what the file says and the message bar says why; nothing is reported to the app, because the window is there to say it; and the next switch works again",
            asked && refused == 1 && shown.Bar is not null && shown.Texts.Contains(sentence) && messageShown == sentence && wentBack == true && heldAfter && fileAfter == true && forwarded == 0
                && dismissed && barGone && switched && offShown == false && offFile == false,
            $"the store refused {refused} time(s); {(shown.Bar is null ? "no message bar" : shown.Bar.ToString())}: {string.Join(" | ", shown.Texts.Select(text => $"\"{text}\""))}; the check box went back to {OnOff(wentBack)}, the editor holds {(heldAfter ? "kept" : "not kept")} and the file says {KeptText(fileAfter)}; "
                + $"reported to the app: {forwarded}; read out: {(read.Length == 0 ? "nothing" : string.Join(", ", read.Select(e => $"\"{e.Text}\"")))}{(heard.Problem is null ? string.Empty : $" ({heard.Problem})")}; "
                + $"the message was taken away: {dismissed && barGone}; switched once more, the check box is {OnOff(offShown)} and the file says {KeptText(offFile)}");
        CloseQuietly(again);
    }

    /// <summary>
    /// Scrolls the inspector to its end, without an animation. Returns the scroll viewer's
    /// rectangle in the window's content, or null when it was not found.
    /// </summary>
    private Windows.Foundation.Rect? ScrollInspectorToEnd(Editor editor) => OnUi<Windows.Foundation.Rect?>(() =>
    {
        ScrollViewer? scroller = null;
        for (DependencyObject? at = Descendant<CheckBox>(editor.Window.Content, KeepBox); at is not null && scroller is null; at = VisualTreeHelper.GetParent(at))
        {
            scroller = at as ScrollViewer;
        }

        if (scroller is null)
        {
            return null;
        }

        scroller.ChangeView(null, scroller.ScrollableHeight, null, disableAnimation: true);
        scroller.UpdateLayout();
        return scroller.TransformToVisual(editor.Window.Content).TransformBounds(new Windows.Foundation.Rect(0, 0, scroller.ActualWidth, scroller.ActualHeight));
    });

    /// <summary>
    /// A picture of the end of the inspector, for a person to look at: the extras, the Project
    /// section with Keep this project, and Save as default look.
    /// </summary>
    private void ProjectSectionPicture(Editor editor, string name, bool isLight, List<string> saved)
    {
        Timeline.Mark($"9: the Project section, {name}");
        var check = $"the Project section in the {name} theme: a picture shows the end of the inspector, which is {(isLight ? "light" : "dark")}, with Keep this project whole in it between Mute audio and Save as default look";
        var viewport = ScrollInspectorToEnd(editor);
        Thread.Sleep(500);
        if (viewport is not { } view || editor.Camera.Take() is not { } shot)
        {
            _report.Check(check, false, viewport is null ? "the inspector's scroll viewer was not found" : "no screenshot");
            return;
        }

        var path = Path.Combine(_output, $"project-section-{name}.png");
        shot.Save(path);
        saved.Add(path);
        var box = InShot(editor, shot, view);
        string[] ids = ["StudioMuteCheckBox", KeepBox, "StudioSaveDefaultLookButton"];
        var places = ids.Select(id => editor.Root.Find(id) is { IsOffscreen: false } found ? found.Bounds : default).ToArray();
        var outside = ids.Where((_, index) => places[index] is not { Width: > 0, Height: > 0 } at
            || at.X - shot.ScreenX < box.X - 1 || at.Y - shot.ScreenY < box.Y - 1
            || at.X - shot.ScreenX + at.Width > box.X + box.Width + 1 || at.Y - shot.ScreenY + at.Height > box.Y + box.Height + 1).ToArray();
        var inOrder = places[0].Y < places[1].Y && places[1].Y < places[2].Y;

        // The inspector's own surface, in the margin left of the check box.
        var keep = editor.Root.Find(KeepBox);
        var surface = keep is null ? new Rgb(-1, -1, -1) : shot.Color(keep.Bounds.X - shot.ScreenX - (8 * editor.Scale), keep.Bounds.Y - shot.ScreenY + (keep.Bounds.Height / 2.0), 2);
        _report.Check(
            check,
            keep is { Name: "Keep this project" } && outside.Length == 0 && inOrder && (isLight ? Luma(surface) > 170 : Luma(surface) < 90),
            $"the inspector shows {R(box)} of the window, and its surface is {surface}; {(outside.Length == 0 ? "Mute audio, Keep this project and Save as default look are whole in it" : "not whole in it: " + string.Join(", ", outside))}, "
                + $"from top to bottom: {inOrder}; saved as {Path.GetFileName(path)}");
        ScrollInspector(editor, _ => 0);
    }
}
