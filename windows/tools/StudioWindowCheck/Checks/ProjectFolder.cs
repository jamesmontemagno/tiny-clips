using System.Security.Cryptography;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TinyClips.App.Views.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 15. The project as a whole (#429): the Project menu of the header and the two buttons of the
// inspector's Project panel; a project saved as a folder, with what the editor does while the
// recordings are copied; the three cases of a place that is taken; a saved project opened
// again; Open recent; and Delete project.
//
// The system's pickers, the Explorer window and the notification are windows of their own, in
// front of other windows, so the tool shows none of them: a check says what a picker answers,
// and reads what the window asked to have shown and said (Host\ToolServices.cs). The Project
// menu is not opened either: an open menu is a window in front. Its items are read from the
// window's own elements, after the window has brought them up to date as opening does.
internal sealed partial class WindowChecks
{
    private const string ProjectMenuId = "StudioProjectMenuButton";
    private const string SaveProjectId = "StudioSaveProjectButton";
    private const string DeleteProjectId = "StudioDeleteProjectButton";
    private const string CancelSaveId = "StudioCancelSaveProjectButton";
    private const string SaveNameBoxId = "StudioSaveProjectNameBox";
    private const string SavePlaceId = "StudioSaveProjectPlaceText";
    private const string SaveProblemId = "StudioSaveProjectProblemText";
    private const string SaveDialogTitle = "Save project";
    private const string ProjectHelp = "Open another project, save this one as a folder, or delete it.";
    private const string DeleteWords = "The recording and every edit are removed from Tiny Clips Studio. Videos you exported and folders you saved this project to are not deleted. This cannot be undone.";

    /// <summary>One line of the Project menu, as the window has it.</summary>
    private readonly record struct MenuLine(string Kind, string Text, string Id, string Keys, string AcceleratorKey, string Glyph, bool IsEnabled, string[] Inner)
    {
        public override string ToString() => Kind == "separator"
            ? "a line"
            : $"{Kind} \"{Text}\" [{Id}]{(Keys.Length > 0 ? $" showing {Keys}" : string.Empty)}{(AcceleratorKey.Length > 0 ? $", key {AcceleratorKey}" : string.Empty)}, picture {(Glyph.Length > 0 ? $"U+{(int)Glyph[0]:X4}" : "none")}, {(IsEnabled ? "enabled" : "greyed")}{(Kind == "menu" ? $" with {Inner.Length} line(s): {string.Join(" | ", Inner)}" : string.Empty)}";
    }

    /// <summary>The Project menu, brought up to date as its opening does, and read without being opened. UI thread inside.</summary>
    private MenuLine[] ProjectMenu(Editor editor) => OnUi(() =>
    {
        editor.Window.PrepareProjectMenu();
        if (Descendant<DropDownButton>(editor.Window.Content, ProjectMenuId)?.Flyout is not MenuFlyout menu)
        {
            return [];
        }

        static string Glyph(IconElement? icon) => (icon as FontIcon)?.Glyph ?? string.Empty;
        return menu.Items.Select(item => item switch
        {
            MenuFlyoutSubItem inner => new MenuLine(
                "menu", inner.Text, AutomationProperties.GetAutomationId(inner), string.Empty, string.Empty, Glyph(inner.Icon), inner.IsEnabled,
                [.. inner.Items.OfType<MenuFlyoutItem>().Select(line => $"{line.Text}{(line.IsEnabled ? string.Empty : " (greyed)")}")]),
            MenuFlyoutItem line => new MenuLine(
                "item", line.Text, AutomationProperties.GetAutomationId(line), line.KeyboardAcceleratorTextOverride, AutomationProperties.GetAcceleratorKey(line), Glyph(line.Icon), line.IsEnabled, []),
            _ => new MenuLine("separator", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, true, []),
        }).ToArray();
    });

    /// <summary>The ids of the projects Open recent lists, in its order, once the window has read them again.</summary>
    private string[] OpenRecentIds(Editor editor, Func<string[], bool> wanted, double seconds = 5) => Until(
        () =>
        {
            ProjectMenu(editor);
            return OnUi(() => editor.Window.ViewModel.RecentProjects.Select(project => project.Id).ToArray());
        },
        wanted,
        seconds,
        100);

    /// <summary>The question Save project asks, once it is there: the one with a name box.</summary>
    private static UiaElement? SaveQuestion(Editor editor, double seconds = 3) => Until(
        () => DialogElement(editor) is { } question && question.Find(SaveNameBoxId) is not null ? question : null,
        found => found is not null,
        seconds);

    /// <summary>A question by the start of its title, once it is there.</summary>
    private static UiaElement? QuestionTitled(Editor editor, string start, double seconds = 3) => Until(
        () => DialogElement(editor) is { } question && question.Name.StartsWith(start, StringComparison.Ordinal) ? question : null,
        found => found is not null,
        seconds);

    private static string ProblemOf(UiaElement? question, double seconds = 1.5) =>
        Until(() => question?.Find(SaveProblemId)?.Name ?? string.Empty, text => text.Length > 0, seconds);

    /// <summary>Every file of a folder by name, with its length and the first digits of its SHA-256: what "unchanged" is read from.</summary>
    private static string[] Fingerprint(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return ["(no folder)"];
        }

        return [.. Directory.EnumerateFileSystemEntries(folder).Order(StringComparer.OrdinalIgnoreCase).Select(entry =>
        {
            if (Directory.Exists(entry))
            {
                return $"{Path.GetFileName(entry)}/";
            }

            using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return $"{Path.GetFileName(entry)} {stream.Length} {Convert.ToHexString(SHA256.HashData(stream))[..12]}";
        })];
    }

    private static string[] NamesIn(string folder) =>
        Directory.Exists(folder) ? [.. Directory.EnumerateFileSystemEntries(folder).Select(entry => Path.GetFileName(entry)!).Order(StringComparer.OrdinalIgnoreCase)] : [];

    private bool IsSavingProject(Editor editor) => OnUi(() => editor.Window.ViewModel.IsSavingProject);

    private void ProjectAsAWhole()
    {
        // Where the checks save projects to: a folder of this run in the temp folder, beside
        // the store and not in it, gone when the group is over.
        var saveRoot = Path.Combine(Path.GetTempPath(), $"TinyClipsStudioWindowCheck-saved-{Environment.ProcessId}");
        Directory.CreateDirectory(saveRoot);
        var placeBefore = _services.Settings.StudioProjectSaveFolder;
        _services.Settings.StudioProjectSaveFolder = saveRoot;
        try
        {
            ProjectCommands(saveRoot);
            DeletingAProjectThatCannotBeShown();
        }
        finally
        {
            _services.Store.HoldProjectSaves(null);
            _services.Settings.StudioPreviewEnabled = true;
            _services.Settings.StudioProjectSaveFolder = placeBefore;
            try
            {
                Directory.Delete(saveRoot, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _report.Note($"the folder the checks saved projects to could not be removed: {saveRoot} ({ex.Message})");
            }
        }
    }

    private void ProjectCommands(string saveRoot)
    {
        Timeline.Mark("15: the Project menu and the Project panel");
        const string Name = "Saved: as a folder";
        var folderName = StudioProjectFolder.FolderName(Name);
        var target = Path.Combine(saveRoot, folderName);
        var projectFile = Path.Combine(target, folderName + ".tinyclips");

        // One that lists an export, so that closing it asks nothing, and whose video is there
        // to be found again after the project is deleted.
        var folder = NewExportedProject(Name);
        var exported = Path.Combine(_services.ExportDirectory, Name.Replace(':', '-') + ".mp4");
        File.WriteAllText(exported, "an exported video");
        _services.Store.Save(_services.Store.Load(folder.Paths.ProjectId) with { Exports = [new StudioExport { Path = exported, ExportedAt = DateTimeOffset.UtcNow }] });
        var other = NewExportedProject("Another project");
        if (OpenReady(folder, "the project as a whole") is not { } editor)
        {
            return;
        }

        // The button of the header.
        var button = Find(editor, ProjectMenuId);
        var inside = button?.Children().Select(child => child.ToString()).ToArray() ?? [];
        var stops = TabStops(editor);
        File.WriteAllLines(Path.Combine(_output, "tab-order-project-folder.txt"), stops);

        // Undo is no stop while there is nothing to undo, as now: Canvas is then the next one.
        var (menuStop, undoStop, canvasStop) = (stops.IndexOf(ProjectMenuId), stops.IndexOf("StudioUndoButton"), stops.IndexOf("StudioCanvasComboBox"));
        var lines = ProjectMenu(editor);
        string[] wanted = ["menu|Open recent|StudioOpenRecentItem||\uE81C", "item|Open project\u2026|StudioOpenProjectItem|Ctrl+O|\uE8E5", "separator||||", "item|Save project\u2026|StudioSaveProjectItem|Ctrl+Shift+S|\uE74E", "separator||||", "item|Delete project\u2026|StudioDeleteProjectItem||\uE74D"];
        var read = lines.Select(line => $"{line.Kind}|{line.Text}|{line.Id}|{line.Keys}|{line.Glyph}").ToArray();
        _report.Check(
            "the header has one Project button, the first stop of the Tab key in the editor, before Undo and Canvas: a button with a menu, called Project, that says what it is for, and whose picture and word are nothing of their own to a screen reader; its menu has Open recent, Open project\u2026 with Ctrl+O, Save project\u2026 with Ctrl+Shift+S and Delete project\u2026, each with its picture, and all of them can be chosen",
            button is { ControlType: ControlTypeNames.Button, Name: "Project", IsEnabled: true, IsKeyboardFocusable: true } && button.HelpText == ProjectHelp && button.IsExpanded == false
                && inside.Length == 0 && menuStop >= 0 && canvasStop == menuStop + (undoStop < 0 ? 1 : 3) && (undoStop < 0 || undoStop == menuStop + 1)
                && read.SequenceEqual(wanted) && lines.All(line => line.IsEnabled) && lines.Where(line => line.Kind == "item").All(line => line.AcceleratorKey == line.Keys),
            $"{button}, described as \"{button?.HelpText}\", patterns {button?.Patterns}; inside it for a screen reader: {(inside.Length == 0 ? "nothing" : string.Join(", ", inside))}; of the {stops.Count} stops of the Tab key it is number {menuStop}, Undo {undoStop} and Canvas {canvasStop} (saved as tab-order-project-folder.txt); the menu: {string.Join("; ", lines.Select(line => line.ToString()))}");

        // The two buttons of the Project panel.
        ShowPanel(editor, StudioInspectorPanel.Project);
        var save = Find(editor, SaveProjectId);
        var delete = Find(editor, DeleteProjectId);
        var walked = Content(editor).Select(entry => entry.Element).ToList();
        var (look, heading, saveAt, deleteAt) = (
            walked.FindIndex(e => e.Id == "StudioSaveDefaultLookButton"),
            walked.FindIndex(e => e.ControlType == ControlTypeNames.Text && e.Name == "Project folder"),
            walked.FindIndex(e => e.Id == SaveProjectId),
            walked.FindIndex(e => e.Id == DeleteProjectId));
        var (lookStop, saveStop, deleteStop) = (stops.IndexOf("StudioSaveDefaultLookButton"), stops.IndexOf(SaveProjectId), stops.IndexOf(DeleteProjectId));
        var insideButtons = (save?.Children().Count ?? -1) + (delete?.Children().Count ?? -1);
        _report.Check(
            "the Project panel has Save project\u2026 under a heading Project folder, and Delete project\u2026 after it, each a button called by its words alone, with a note under it; Save project\u2026 names its keys; they come after Save as default look for a screen reader and for the Tab key, and are the panel's last two stops",
            save is { ControlType: ControlTypeNames.Button, Name: "Save project\u2026", IsEnabled: true, IsKeyboardFocusable: true, AcceleratorKey: "Ctrl+Shift+S" }
                && delete is { ControlType: ControlTypeNames.Button, Name: "Delete project\u2026", IsEnabled: true, IsKeyboardFocusable: true } && delete.HelpText == "Deletes the recording and every edit. Asks first."
                && insideButtons == 0 && look >= 0 && look < heading && heading < saveAt && saveAt < deleteAt
                && lookStop >= 0 && saveStop == lookStop + 1 && deleteStop == saveStop + 1
                && NameOf(editor, "StudioSaveProjectNote") == "Saves a copy of the recordings with a .tinyclips file that opens them in Studio again, on this PC or another."
                && NameOf(editor, "StudioDeleteProjectNote") == "Removes the recording and every edit from Studio. Exported videos and saved folders stay.",
            $"{save}, key \"{save?.AcceleratorKey}\"; {delete}, described as \"{delete?.HelpText}\"; elements inside the two for a screen reader: {insideButtons}; walked: Save as default look {look}, the heading {heading}, Save {saveAt}, Delete {deleteAt}; "
                + $"Tab: Save as default look {lookStop}, Save {saveStop}, Delete {deleteStop}; notes \"{NameOf(editor, "StudioSaveProjectNote", 0)}\" and \"{NameOf(editor, "StudioDeleteProjectNote", 0)}\"");

        ProjectPicture(editor, "panel");

        // The keys.
        Timeline.Mark("15: Ctrl+O and Ctrl+Shift+S");
        var pickerBefore = _services.ProjectFilePickerAsked;
        var openKey = Key(editor, StudioShortcutKey.O, control: true);
        var pickerAsked = Until(() => _services.ProjectFilePickerAsked - pickerBefore, asked => asked == 1, 2);
        var saveKey = Key(editor, StudioShortcutKey.S, control: true, shift: true);
        var byKey = SaveQuestion(editor);
        var plainSave = Key(editor, StudioShortcutKey.S, control: true);
        Dismiss(editor, byKey);
        _report.Check(
            "Ctrl+O asks for a project file with the open picker, and when none is chosen nothing happens; Ctrl+Shift+S asks where to save the project; Ctrl+S alone is no key of the window",
            openKey == StudioShortcutAction.OpenProject && pickerAsked == 1 && saveKey == StudioShortcutAction.SaveProject && byKey is { Name: SaveDialogTitle } && plainSave == StudioShortcutAction.None
                && ErrorOf(editor).Length == 0 && _services.Tracker.OpenProjectIds.Count == 1,
            $"Ctrl+O ran {openKey}, and the picker was asked {pickerAsked} time(s); Ctrl+Shift+S ran {saveKey}, and the window asked \"{byKey?.Name}\"; Ctrl+S ran {plainSave}; {_services.Tracker.OpenProjectIds.Count} project(s) open{(ErrorOf(editor) is { Length: > 0 } error ? $"; the window says \"{error}\"" : string.Empty)}");

        // Save project: what it asks.
        Timeline.Mark("15: Save project asks for a name and a place");
        var edited = SetSlider(editor, "StudioPaddingSlider", 0.12);
        Expect(editor, p => p with { Canvas = p.Canvas with { Padding = 0.12 } });
        var focusedFirst = FocusOn(editor, SaveProjectId);
        var pressed = Invoke(editor, SaveProjectId);
        var question = SaveQuestion(editor);
        var nameBox = question?.Find(SaveNameBoxId);
        var place = question?.Find(SavePlaceId);
        var choose = question?.Find("StudioSaveProjectChooseFolderButton");
        var focus = Until(() => FocusedId(editor), id => id == SaveNameBoxId, 1.5);
        ProjectPicture(editor, "save-question");
        _report.Check(
            "Save project\u2026 asks in a dialog: a box for the folder's name, which holds the project's name as a folder can have it and has the keyboard focus; the folder it will be made in, which is where a project was saved last; Choose folder\u2026, Save and Cancel; and nothing is saved by then",
            edited && pressed && question is { Name: SaveDialogTitle } && nameBox is { Name: "Folder name", IsKeyboardFocusable: true } && nameBox.ValueText == folderName && folderName == "Saved- as a folder"
                && place?.Name == $"In {saveRoot}" && choose is { Name: "Choose folder\u2026", ControlType: ControlTypeNames.Button }
                && question.Find("PrimaryButton") is { Name: "Save" } && question.Find("CloseButton") is { Name: "Cancel" } && question.Find(SaveProblemId) is null
                && focus == SaveNameBoxId && NamesIn(saveRoot).Length == 0 && !IsSavingProject(editor),
            $"{QuestionAsked(question)}; the box {nameBox} holds \"{nameBox?.ValueText}\" for a project called \"{Name}\"; the place reads \"{place?.Name}\"; {choose}; the focus is on \"{focus}\"; in the place: {NamesIn(saveRoot).Length} entries");

        // Choose folder: the picker's answer becomes the place, and Cancel saves nothing.
        var elsewhere = Path.Combine(saveRoot, "Elsewhere");
        Directory.CreateDirectory(elsewhere);
        _services.AnswerSaveFolderPicker(elsewhere);
        var folderPickerBefore = _services.SaveFolderPickerAsked;
        var chose = choose?.Invoke() ?? false;
        var placeAfter = Until(() => question?.Find(SavePlaceId)?.Name, text => text == $"In {elsewhere}", 2);
        var cancelledPick = (choose?.Invoke() ?? false) && Until(() => _services.SaveFolderPickerAsked - folderPickerBefore, asked => asked == 2, 2) == 2;
        var placeKept = question?.Find(SavePlaceId)?.Name;
        Dismiss(editor, question);
        _report.Check(
            "Choose folder\u2026 asks the folder picker, and the folder that was chosen is the place; a picker that is closed without a choice leaves the place as it was; Cancel saves nothing and remembers nothing",
            chose && placeAfter == $"In {elsewhere}" && cancelledPick && placeKept == placeAfter && !HasDialog(editor)
                && NamesIn(elsewhere).Length == 0 && _services.Store.ProjectSavesBegun == 0 && _services.Settings.StudioProjectSaveFolder == saveRoot,
            $"after the picker answered {elsewhere} the place read \"{placeAfter}\", and after it answered nothing \"{placeKept}\"; saves begun {_services.Store.ProjectSavesBegun}; the place a save starts in is {_services.Settings.StudioProjectSaveFolder}");

        // The save itself, held before anything is copied so that the editor can be looked at.
        Timeline.Mark("15: while the project is saved");
        using var heard = UiaEvents.Listen(_uia, editor.Root);
        using var gate = new ManualResetEventSlim(false);
        _services.Store.HoldProjectSaves(gate);
        var mark = heard.Mark();
        var revealedBefore = _services.Revealed.Count;
        FocusOn(editor, SaveProjectId);
        Invoke(editor, SaveProjectId);
        var asked = SaveQuestion(editor);
        var saveStarted = asked?.Find("PrimaryButton")?.Invoke() ?? false;
        var cancelSave = Find(editor, CancelSaveId, 5);
        var words = NameOf(editor, "StudioSavingProjectText", 1);
        var bar = Find(editor, "StudioSaveProjectProgressBar", 1);
        var range = bar?.Range;
        var underOverlay = new[] { "StudioPlayPauseButton", ProjectMenuId, "StudioExportButton", "StudioUndoButton" }.Select(id => (Id: id, Enabled: Find(editor, id, 0.5)?.IsEnabled)).ToArray();
        var inFile = _services.Store.Load(editor.Id).Canvas.Padding;
        var saidSaving = heard.WaitFor(mark, "notification", e => e.Text == "Saving project\u2026", 2);
        var focusWhile = Until(() => FocusedId(editor), id => id == CancelSaveId, 2);
        ProjectPicture(editor, "saving");
        _report.Check(
            "Save puts an overlay over the editor: Saving project\u2026, a progress bar from 0 to 100 and Cancel, which has the keyboard focus; the editor under it is disabled; a screen reader is told that the project is being saved; and the edit made just before is in the project file before anything is copied",
            saveStarted && cancelSave is { IsEnabled: true, Name: "Cancel save" } && words == "Saving project\u2026" && bar is { Name: "Save progress" } && range is { Minimum: 0, Maximum: 100 }
                && underOverlay.All(control => control.Enabled == false) && Same(inFile, 0.12) && saidSaving.Any(e => e.Text == "Saving project\u2026") && focusWhile == CancelSaveId && IsSavingProject(editor) && !Directory.Exists(target),
            $"\"{words}\", {bar} {F(range?.Minimum ?? double.NaN)} to {F(range?.Maximum ?? double.NaN)}, {cancelSave}; under it: {string.Join(", ", underOverlay.Select(control => $"{control.Id} enabled {control.Enabled}"))}; "
                + $"the padding in the project file is {F(inFile)}; read out: {Heard(saidSaving)}{(heard.Problem is null ? string.Empty : $" ({heard.Problem})")}; the focus is on \"{focusWhile}\"; the folder is there already: {Directory.Exists(target)}");

        // No edit is taken meanwhile: not through what a control is bound to, and not by a key.
        var before = Describe(OnUi(() => _services.Store.Load(editor.Id)));
        var (padding, muted, undo) = OnUi(() =>
        {
            var viewModel = editor.Window.ViewModel;
            viewModel.CanvasPadding = 0.3;
            viewModel.IsMuted = true;
            viewModel.Undo();
            return (viewModel.CanvasPadding, viewModel.IsMuted, viewModel.CanUndo);
        });
        StudioShortcutAction[] keys =
        [
            Key(editor, StudioShortcutKey.Space), Key(editor, StudioShortcutKey.Z), Key(editor, StudioShortcutKey.Z, control: true), Key(editor, StudioShortcutKey.E, control: true),
            Key(editor, StudioShortcutKey.O, control: true), Key(editor, StudioShortcutKey.S, control: true, shift: true), Key(editor, StudioShortcutKey.Delete),
        ];
        var savesBegun = _services.Store.ProjectSavesBegun;
        var after = Describe(OnUi(() => _services.Store.Load(editor.Id)));
        _report.Check(
            "while the project is saved the editor takes no edit: a value written to what the controls are bound to changes nothing, Undo undoes nothing, and no key of the window runs anything, a second save among them",
            Same(padding, 0.12) && !muted && !undo && keys.All(key => key == StudioShortcutAction.None) && savesBegun == 1 && before == after && IsSavingProject(editor) && !IsExporting(editor),
            $"the padding is {F(padding)} after 0.3 was written, mute is {(muted ? "on" : "off")} after it was switched on, Undo is {(undo ? "offered" : "not offered")}; the keys ran {string.Join(", ", keys)}; saves begun {savesBegun}; the project file {(before == after ? "is as it was" : "changed")}");

        // The close button asks, and Keep saving keeps the window.
        Timeline.Mark("15: the close button while the project is saved");
        var closePressed = PressClose(editor);
        var closing = QuestionTitled(editor, "The project is still being saved.");
        var closingWords = WordsOf(closing);
        var closingAsked = QuestionAsked(closing);
        var closingAnswers = closing?.Find("PrimaryButton") is { Name: "Stop and close" } && closing.Find("CloseButton") is { Name: "Keep saving" };
        var keptSaving = closing?.Find("CloseButton")?.Invoke() ?? false;
        Until(() => !HasDialog(editor), gone => gone, 3);
        Thread.Sleep(QuestionSettles);
        _report.Check(
            "the close button, while the project is saved, asks first: Stop and close, or Keep saving; with Keep saving the window stays and the save goes on",
            closePressed && closing is not null && closingAnswers
                && closingWords.Contains("Closing the window stops the save, and nothing is left of the folder that was being written. Your edits are kept.")
                && keptSaving && Native.Exists(editor.Handle) && IsSavingProject(editor),
            $"{closingAsked}; after Keep saving the window is {(Native.Exists(editor.Handle) ? "open" : "gone")} and the editor is {(IsSavingProject(editor) ? "still saving" : "not saving")}");

        // Let it finish.
        Timeline.Mark("15: the project is saved");
        mark = heard.Mark();
        gate.Set();
        var overlayGone = Gone(editor, CancelSaveId, 15);
        var working = Until(() => Find(editor, "StudioPlayPauseButton", 0)?.IsEnabled == true, ok => ok, 3);
        var files = NamesIn(target);
        var revealed = Until(() => _services.Revealed.Skip(revealedBefore).ToArray(), shown => shown.Length > 0, 3);
        var saidSaved = heard.WaitFor(mark, "notification", e => e.Text == $"Project saved to the folder {folderName}.", 2);
        var focusAfter = Until(() => FocusedId(editor), id => id == SaveProjectId, 2);
        string opened;
        string? reopenedId = null;
        try
        {
            var copy = _services.Store.OpenProjectFolder(projectFile);
            reopenedId = copy.Id;
            opened = copy.Id != editor.Id && copy.Name == Name && Same(copy.Canvas.Padding, 0.12) && copy.Exports.Length == 0
                && File.ReadAllBytes(_services.Store.GetPaths(copy).ScreenPath).AsSpan().SequenceEqual(File.ReadAllBytes(folder.Paths.ScreenPath))
                    ? "ok"
                    : $"a project called \"{copy.Name}\" with the padding {F(copy.Canvas.Padding)} and {copy.Exports.Length} export(s)";
        }
        catch (Exception ex)
        {
            opened = ex.Message;
        }

        _report.Check(
            "when the copy is done the overlay goes and the editor works again, with the focus back on the button the save was asked from; the folder holds the recording and a .tinyclips file of the folder's name; the store opens that file again as a project of its own, with the edit, the same recording and no exports; the file is shown in its folder; a screen reader is told where the project was saved; and the next save starts in the same place",
            overlayGone && working && files.SequenceEqual(new[] { "events.json", folderName + ".tinyclips", "screen.mp4" }, StringComparer.OrdinalIgnoreCase) && opened == "ok"
                && revealed.SequenceEqual([projectFile]) && saidSaved.Any(e => e.Text == $"Project saved to the folder {folderName}.") && focusedFirst == SaveProjectId && focusAfter == SaveProjectId
                && ErrorOf(editor).Length == 0 && _services.Settings.StudioProjectSaveFolder == saveRoot && _services.Notices.IsEmpty,
            $"the overlay went: {overlayGone}; the editor works: {working}; the folder holds {string.Join(", ", files)}; opened again by the store: {opened}; shown in its folder: {(revealed.Length == 0 ? "nothing" : string.Join(", ", revealed))}; "
                + $"read out: {Heard(saidSaved)}; the focus is on \"{focusAfter}\"; the next save starts in {_services.Settings.StudioProjectSaveFolder}{(ErrorOf(editor) is { Length: > 0 } saveError ? $"; the window says \"{saveError}\"" : string.Empty)}");
        if (reopenedId is not null)
        {
            _services.Store.Delete(reopenedId);
        }

        SavingOverWhatIsThere(editor, saveRoot, folderName, target, projectFile);
        ASaveThatIsStopped(editor, saveRoot, heard);
        OpeningASavedProject(editor, other, target, projectFile, saveRoot);
        DeletingTheProject(editor, folder, target, exported);
    }

    /// <summary>The three cases of a place that is taken, and a name a folder cannot have.</summary>
    private void SavingOverWhatIsThere(Editor editor, string saveRoot, string folderName, string target, string projectFile)
    {
        Timeline.Mark("15: saving over a saved project");
        _services.Store.HoldProjectSaves(null);
        var savesBefore = _services.Store.ProjectSavesBegun;
        SetSlider(editor, "StudioPaddingSlider", 0.2);
        Expect(editor, p => p with { Canvas = p.Canvas with { Padding = 0.2 } });

        // A saved project with nothing else in it: asked about. No is no.
        Invoke(editor, SaveProjectId);
        var first = SaveQuestion(editor);
        first?.Find("PrimaryButton")?.Invoke();
        var replace = QuestionTitled(editor, "Replace the saved project");
        var replaceWords = WordsOf(replace);
        var wantedWords = $"The folder \u201C{folderName}\u201D in {saveRoot} already holds a saved Tiny Clips project. Replacing it deletes the recordings and the project file that are in it now. This cannot be undone.";
        var replaceFocus = Until(() => FocusedId(editor), id => id == "CloseButton", 1.5);
        var replaceAsked = QuestionAsked(replace);
        var replaceTitle = replace?.Name;
        ProjectPicture(editor, "replace-question");
        var replaceAnswers = replace?.Find("PrimaryButton") is { Name: "Replace" } && replace.Find("CloseButton") is { Name: "Cancel" };
        var unchanged = Fingerprint(target);
        var refused = replace?.Find("CloseButton")?.Invoke() ?? false;
        var again = SaveQuestion(editor, 4);
        var nameAgain = again?.Find(SaveNameBoxId)?.ValueText;
        var stillUnchanged = Fingerprint(target).SequenceEqual(unchanged) && _services.Store.ProjectSavesBegun == savesBefore;
        _report.Check(
            "saving to a name where a saved project is, with nothing else in its folder, asks whether to replace it, naming the folder and where it is, with Cancel as the default answer; Cancel replaces nothing and asks for the name again, with the name as it was",
            replaceTitle == $"Replace the saved project \u201C{folderName}\u201D?" && replaceWords.Contains(wantedWords)
                && replaceAnswers && replaceFocus == "CloseButton"
                && refused && again is not null && nameAgain == folderName && stillUnchanged,
            $"{replaceAsked}; the focus was on \"{replaceFocus}\"; after Cancel the window asks \"{again?.Name}\" with the name \"{nameAgain}\"; the folder is {(stillUnchanged ? "as it was" : "changed")}; saves begun {_services.Store.ProjectSavesBegun - savesBefore}");

        // Yes replaces it.
        again?.Find("PrimaryButton")?.Invoke();
        var replaceAgain = QuestionTitled(editor, "Replace the saved project");
        var agreed = replaceAgain?.Find("PrimaryButton")?.Invoke() ?? false;
        var done = Until(() => _services.Store.ProjectSavesBegun - savesBefore == 1 && !IsSavingProject(editor), ok => ok, 15);
        Thread.Sleep(QuestionSettles);
        var replaced = PaddingOf(projectFile);
        _report.Check(
            "Replace replaces the saved project: the folder then holds the project as it is now",
            agreed && done && Same(replaced, 0.2) && NamesIn(target).Length == 3 && ErrorOf(editor).Length == 0,
            $"the project file in the folder has the padding {F(replaced)}, and the editor {F(SliderValue(editor, "StudioPaddingSlider"))}; the folder holds {string.Join(", ", NamesIn(target))}{(ErrorOf(editor) is { Length: > 0 } error ? $"; the window says \"{error}\"" : string.Empty)}");

        // A saved project that holds something else: said, not asked, and left as it is.
        Timeline.Mark("15: a saved project that holds something else");
        File.WriteAllText(Path.Combine(target, "notes.txt"), "not part of the project");
        var withNotes = Fingerprint(target);
        savesBefore = _services.Store.ProjectSavesBegun;
        Invoke(editor, SaveProjectId);
        var holding = SaveQuestion(editor);
        holding?.Find("PrimaryButton")?.Invoke();
        var holdsSentence = ProblemOf(holding);
        Thread.Sleep(400);
        var stillTheSame = SaveQuestion(editor, 0.5);
        var noReplaceQuestion = DialogElement(editor) is { } shown && !shown.Name.StartsWith("Replace", StringComparison.Ordinal);
        ProjectPicture(editor, "save-question-refused");
        var focusOnName = FocusedId(editor);
        _report.Check(
            "saving to a name where a saved project holds a file that is not part of it asks nothing: the dialog stays, says under the name which file the folder holds and that it was not replaced, and has the focus in the name box; the folder is as it was",
            holdsSentence == "This folder holds notes.txt, which is not part of the project, so it was not replaced. Choose another name."
                && stillTheSame is { Name: SaveDialogTitle } && noReplaceQuestion && focusOnName == SaveNameBoxId
                && Fingerprint(target).SequenceEqual(withNotes) && _services.Store.ProjectSavesBegun == savesBefore,
            $"the dialog says \"{holdsSentence}\"; the question on show is \"{DialogElement(editor)?.Name}\"; the focus is on \"{focusOnName}\"; the folder is {(Fingerprint(target).SequenceEqual(withNotes) ? "as it was" : "changed")}; saves begun {_services.Store.ProjectSavesBegun - savesBefore}");

        // Another name, in the same dialog: something that is not a saved project.
        Timeline.Mark("15: a name that is taken, and one a folder cannot have");
        var holiday = Path.Combine(saveRoot, "Holiday");
        Directory.CreateDirectory(holiday);
        File.WriteAllText(Path.Combine(holiday, "beach.jpg"), "a picture");
        var typed = stillTheSame?.Find(SaveNameBoxId)?.SetValue("Holiday") ?? false;
        var clearedByTyping = Until(() => stillTheSame?.Find(SaveProblemId) is null, cleared => cleared, 1.5);
        stillTheSame?.Find("PrimaryButton")?.Invoke();
        var takenSentence = ProblemOf(stillTheSame);
        var typedBad = stillTheSame?.Find(SaveNameBoxId)?.SetValue("a/b") ?? false;
        Until(() => stillTheSame?.Find(SaveProblemId) is null, cleared => cleared, 1.5);
        stillTheSame?.Find("PrimaryButton")?.Invoke();
        var badSentence = ProblemOf(stillTheSame);
        Dismiss(editor, stillTheSame);
        _report.Check(
            "typing another name takes the sentence away; a name where something else is, is said to be taken; a name a folder cannot have is refused with one that it can; neither asks anything, and nothing is saved or changed",
            typed && clearedByTyping && takenSentence == "There is already something with that name, and it is not a saved Tiny Clips project. Choose another name."
                && typedBad && badSentence == "A folder cannot be called that. Try \u201Ca-b\u201D."
                && NamesIn(holiday).SequenceEqual(["beach.jpg"]) && _services.Store.ProjectSavesBegun == savesBefore && !Directory.Exists(Path.Combine(saveRoot, "a-b")),
            $"for \"Holiday\" the dialog says \"{takenSentence}\", and for \"a/b\" \"{badSentence}\"; typing took the first sentence away: {clearedByTyping}; the folder Holiday holds {string.Join(", ", NamesIn(holiday))}; saves begun {_services.Store.ProjectSavesBegun - savesBefore}");
        File.Delete(Path.Combine(target, "notes.txt"));
    }

    /// <summary>Cancel on the overlay, what Esc runs, and Stop and close.</summary>
    private void ASaveThatIsStopped(Editor editor, string saveRoot, UiaEvents heard)
    {
        Timeline.Mark("15: a save that is stopped");
        var stoppedTarget = Path.Combine(saveRoot, "Stopped");
        string[] rootBefore = NamesIn(saveRoot);

        bool Begin(ManualResetEventSlim gate)
        {
            _services.Store.HoldProjectSaves(gate);
            Invoke(editor, SaveProjectId);
            var question = SaveQuestion(editor);
            var typed = question?.Find(SaveNameBoxId)?.SetValue("Stopped") ?? false;
            return typed && (question?.Find("PrimaryButton")?.Invoke() ?? false) && Find(editor, CancelSaveId, 5) is not null;
        }

        // By the button.
        var mark = heard.Mark();
        bool begun, cancelled, idle;
        using (var gate = new ManualResetEventSlim(false))
        {
            begun = Begin(gate);
            cancelled = Invoke(editor, CancelSaveId);
            gate.Set();
            idle = Gone(editor, CancelSaveId, 15);
        }

        var said = heard.WaitFor(mark, "notification", e => e.Text == "Saving the project was cancelled. Nothing was saved.", 2);
        var working = Until(() => Find(editor, "StudioPlayPauseButton", 0)?.IsEnabled == true, ok => ok, 3);
        Thread.Sleep(QuestionSettles);

        // By what Esc runs.
        StudioShortcutAction escape;
        bool begunAgain, idleAgain;
        using (var gate = new ManualResetEventSlim(false))
        {
            begunAgain = Begin(gate);
            escape = Key(editor, StudioShortcutKey.Escape);
            gate.Set();
            idleAgain = Gone(editor, CancelSaveId, 15);
        }

        _services.Store.HoldProjectSaves(null);
        var nothingLeft = NamesIn(saveRoot).SequenceEqual(rootBefore) && !Directory.Exists(stoppedTarget);
        _report.Check(
            "Cancel on the overlay stops the save, and so does what Esc runs then, which does not go on to close the window: the overlay goes, the editor works again, nothing is left where the folder would have been, a screen reader is told that nothing was saved, and no error is shown",
            begun && cancelled && idle && said.Any(e => e.Text == "Saving the project was cancelled. Nothing was saved.") && working && begunAgain && escape == StudioShortcutAction.CancelProjectSave && idleAgain
                && nothingLeft && Native.Exists(editor.Handle) && !HasDialog(editor) && ErrorOf(editor).Length == 0,
            $"by the button: begun {begun}, cancelled {cancelled}, the overlay went {idle}; read out: {Heard(said)}; Esc ran {escape}, and the overlay went {idleAgain}; "
                + $"the place holds {string.Join(", ", NamesIn(saveRoot))}; the window is {(Native.Exists(editor.Handle) ? "open" : "gone")}{(ErrorOf(editor) is { Length: > 0 } error ? $"; the window says \"{error}\"" : string.Empty)}");
        Thread.Sleep(QuestionSettles);
    }

    /// <summary>Open project from a saved folder, a file that is no project, Studio switched off, and Open recent.</summary>
    private void OpeningASavedProject(Editor editor, TestFolder other, string target, string projectFile, string saveRoot)
    {
        Timeline.Mark("15: Open project");
        var folderBefore = Fingerprint(target);
        var openBefore = _services.Tracker.OpenProjectIds.ToHashSet(StringComparer.Ordinal);
        var projectsBefore = _services.Store.ListSummaries().Select(summary => summary.Id).ToHashSet(StringComparer.Ordinal);
        _services.AnswerProjectFilePicker(projectFile);
        var key = Key(editor, StudioShortcutKey.O, control: true);
        var newId = Until(() => _services.Tracker.OpenProjectIds.FirstOrDefault(id => !openBefore.Contains(id)), id => id is not null, 20, 50);
        StudioWindow? copyWindow = null;
        var ready = false;
        var copyName = string.Empty;
        var copyExports = -1;
        if (newId is not null)
        {
            copyWindow = OnUi(() => _services.Windows.Open(newId) as StudioWindow);
            var handle = copyWindow is null ? 0 : OnUi(() => WinRT.Interop.WindowNative.GetWindowHandle(copyWindow));
            var root = handle == 0 ? null : _uia.FromWindow(handle);
            ready = Until(() => root?.Find("StudioPlayPauseButton") is { IsEnabled: true }, ok => ok, 30, 50);
            var copy = _services.Store.Load(newId);
            (copyName, copyExports) = (copy.Name, copy.Exports.Length);
        }

        var isNew = newId is not null && newId != editor.Id && !projectsBefore.Contains(newId);
        _report.Check(
            "Open project\u2026, with a saved project's .tinyclips file chosen, copies the project into the store as a new one with no exports and opens an editor on the copy; the folder is only read, the editor that asked stays as it is, and nothing is said meanwhile for a copy this short",
            key == StudioShortcutAction.OpenProject && isNew && ready && copyName == "Saved: as a folder" && copyExports == 0
                && Fingerprint(target).SequenceEqual(folderBefore) && Native.Exists(editor.Handle) && ErrorOf(editor).Length == 0 && _services.Notices.IsEmpty,
            $"Ctrl+O ran {key}; a project {(isNew ? "of its own" : "that is not new")} was opened: {newId ?? "none"}, called \"{copyName}\", with {copyExports} export(s); its editor became ready: {ready}; the folder is {(Fingerprint(target).SequenceEqual(folderBefore) ? "as it was" : "changed")}"
                + $"{(ErrorOf(editor) is { Length: > 0 } error ? $"; the window says \"{error}\"" : string.Empty)}{(_services.Notices.IsEmpty ? string.Empty : $"; said: {string.Join(" | ", _services.Notices)}")}");

        // Open recent, with the copy open and Another project in the store.
        Timeline.Mark("15: Open recent");
        var recent = OpenRecentIds(editor, ids => newId is not null && ids.Contains(newId) && ids.Contains(other.Paths.ProjectId));
        var menu = ProjectMenu(editor).FirstOrDefault(line => line.Kind == "menu");
        var others = _services.Store.ListSummaries();
        var expected = StudioProjectFolderText.GetRecentProjects(others, editor.Id).Select(summary => summary.Id).ToArray();
        var titles = StudioProjectFolderText.GetRecentProjects(others, editor.Id).Select(summary => StudioProjectFolderText.GetRecentTitle(summary)).ToArray();
        var activationsBefore = copyWindow is null ? 0 : _services.ActivationsOf(OnUi(() => WinRT.Interop.WindowNative.GetWindowHandle(copyWindow)));
        if (newId is not null)
        {
            OnUi(() => editor.Window.OpenRecentProject(newId));
        }

        var activations = copyWindow is null ? 0 : _services.ActivationsOf(OnUi(() => WinRT.Interop.WindowNative.GetWindowHandle(copyWindow)));
        _report.Check(
            "Open recent lists the other projects of the store that can be opened, the one opened last first, eight at most, each by its name and when it was recorded, and never the project the menu belongs to; choosing one that is open already brings its window forward",
            recent.SequenceEqual(expected) && recent.Length is > 0 and <= 8 && !recent.Contains(editor.Id) && recent[0] == newId
                && (menu.Inner ?? []).SequenceEqual(titles) && titles.All(title => title.Contains(", ", StringComparison.Ordinal)) && activations == activationsBefore + 1,
            $"the store has {others.Count} project(s); Open recent lists {recent.Length}: {string.Join(" | ", menu.Inner ?? [])}; the first is the copy that was just opened: {recent.FirstOrDefault() == newId}; the window of the copy was asked forward {activations - activationsBefore} time(s)");

        // The copy's window is closed, and the copy stays in the store as a draft.
        if (copyWindow is not null)
        {
            OnUi(() => copyWindow.CloseForExit()).Wait(TimeSpan.FromSeconds(15));
            Until(() => !_services.Tracker.IsOpen(newId!), closed => closed, 15, 50);
            copyWindow = null;
        }

        // A file that is not a project, and Studio switched off.
        Timeline.Mark("15: a file that cannot be opened");
        var broken = Path.Combine(saveRoot, "Broken.tinyclips");
        File.WriteAllText(broken, "{ this is not a project");
        var countBefore = _services.Store.ListSummaries().Count;
        _services.AnswerProjectFilePicker(broken);
        Key(editor, StudioShortcutKey.O, control: true);
        var (bar, texts, close) = MessageBar(editor, 10);
        var brokenMessage = ErrorOf(editor);
        var dismissed = close?.Invoke() ?? false;
        Gone(editor, "StudioErrorBar", 3);

        _services.Settings.StudioPreviewEnabled = false;
        string offMessage;
        try
        {
            _services.AnswerProjectFilePicker(projectFile);
            Key(editor, StudioShortcutKey.O, control: true);
            MessageBar(editor, 10);
            offMessage = ErrorOf(editor);
        }
        finally
        {
            _services.Settings.StudioPreviewEnabled = true;
        }

        MessageBar(editor, 1).Close?.Invoke();
        Gone(editor, "StudioErrorBar", 3);
        var countAfter = _services.Store.ListSummaries().Count;
        _report.Check(
            "a file that is not a project is not opened, and the window that asked says why in its message bar, in the store's sentence; with Studio switched off a project file is not opened either, the window says that Studio is switched off, and nothing is copied",
            bar is not null && brokenMessage == "Studio could not open this project: This file could not be read as a Tiny Clips project. It may be damaged." && texts.Contains(brokenMessage) && dismissed
                && offMessage == "Tiny Clips Studio is switched off. Switch it on in Settings, under Studio, to open this project."
                && countAfter == countBefore && _services.Tracker.OpenProjectIds.Count == 1,
            $"for the file that is no project the window says \"{brokenMessage}\"; with Studio switched off it says \"{offMessage}\"; the store had {countBefore} project(s) before and has {countAfter}; {_services.Tracker.OpenProjectIds.Count} project(s) open");
    }

    /// <summary>Delete project: what it asks, Cancel, and Delete.</summary>
    private void DeletingTheProject(Editor editor, TestFolder folder, string target, string exported)
    {
        Timeline.Mark("15: Delete project");
        var savedBefore = Fingerprint(target);
        var deletesBefore = _services.Store.Deletes;
        Invoke(editor, "StudioPlayPauseButton");
        Until(() => NameOf(editor, "StudioPlayPauseButton", 0), name => name == "Pause", 2);
        var pressed = Invoke(editor, DeleteProjectId);
        var question = QuestionTitled(editor, "Delete");
        var words = WordsOf(question);
        var focus = Until(() => FocusedId(editor), id => id == "CloseButton", 1.5);
        var paused = Until(() => NameOf(editor, "StudioPlayPauseButton", 0), name => name == "Play", 2) == "Play";
        ProjectPicture(editor, "delete-question");
        _report.Check(
            "Delete project\u2026 asks first, with the project's name: it says that exported videos and saved folders are not deleted and that it cannot be undone; the answers are Delete project and Cancel, with the keyboard focus on Cancel; the video stops playing, and nothing is deleted by then",
            pressed && question is { Name: "Delete \u201CSaved: as a folder\u201D?" } && words.Contains(DeleteWords)
                && question.Find("PrimaryButton") is { Name: "Delete project" } && question.Find("CloseButton") is { Name: "Cancel" } && question.Find("SecondaryButton") is not { Name.Length: > 0 }
                && focus == "CloseButton" && paused && _services.Store.Deletes == deletesBefore && _services.Store.Exists(editor.Id),
            $"{QuestionAsked(question)}; the focus is on \"{focus}\"; playing stopped: {paused}; delete attempts {_services.Store.Deletes - deletesBefore}");

        // Cancel.
        Dismiss(editor, question);
        var kept = Native.Exists(editor.Handle) && _services.Store.Exists(editor.Id) && Find(editor, "StudioPlayPauseButton", 0.5)?.IsEnabled == true;

        // Delete, this time from the menu's own command.
        var lines = ProjectMenu(editor);
        OnUi(() => editor.Window.ViewModel.RequestDeleteProject());
        var second = QuestionTitled(editor, "Delete");
        var agreed = second?.Find("PrimaryButton")?.Invoke() ?? false;
        var folderGone = Until(() => !Directory.Exists(folder.Paths.ProjectDirectory), gone => gone, 10, 5);
        var windowGone = WindowGone(editor);
        Release(editor);
        var notOpen = Until(() => !_services.Tracker.IsOpen(editor.Id), ok => ok, 5, 50);
        _report.Check(
            "Cancel keeps the project and the window; Delete project closes the window and deletes the project in the store at the first attempt; the folder it was saved to and the video exported from it are still there, as they were",
            kept && lines.Any(line => line.Id == "StudioDeleteProjectItem" && line.IsEnabled) && agreed && folderGone && windowGone && notOpen && !_services.Store.Exists(editor.Id)
                && _services.Store.Deletes == deletesBefore + 1 && _services.Store.FailedDeletes == 0
                && Fingerprint(target).SequenceEqual(savedBefore) && File.Exists(exported) && File.ReadAllText(exported) == "an exported video" && _services.Errors().Length == 0,
            $"after Cancel the project and its window were there: {kept}; after Delete project the project's folder in the store is {(folderGone ? "gone" : "there")}, the window {(windowGone ? "gone" : "there")}; delete attempts {_services.Store.Deletes - deletesBefore}, failed {_services.Store.FailedDeletes}; "
                + $"the saved folder is {(Fingerprint(target).SequenceEqual(savedBefore) ? "as it was" : "changed")}; the exported video is {(File.Exists(exported) ? "there" : "gone")}{(_services.Errors() is { Length: > 0 } errors ? "; reported: " + errors[0] : string.Empty)}");
    }

    /// <summary>A project that cannot be shown has Delete project under its message.</summary>
    private void DeletingAProjectThatCannotBeShown()
    {
        Timeline.Mark("15: Delete project, of a project that cannot be shown");
        var folder = NewScreenProject("Lost its recording", writeScreen: false);
        var editor = Open(folder, "delete, cannot be shown");
        var state = WaitLoaded(editor);
        var button = Find(editor, "StudioDeleteUnavailableProjectButton");
        var deletesBefore = _services.Store.Deletes;
        var pressed = button?.Invoke() ?? false;
        var question = QuestionTitled(editor, "Delete");
        var words = WordsOf(question);
        var asked = QuestionAsked(question);
        var title = question?.Name;
        ProjectPicture(editor, "delete-question-cannot-be-shown");
        var buttonIs = button is { ControlType: ControlTypeNames.Button, Name: "Delete project\u2026", IsEnabled: true, IsKeyboardFocusable: true } && button.Children().Count == 0;
        var buttonSays = $"{button}, described as \"{button?.HelpText}\"";
        var agreed = question?.Find("PrimaryButton")?.Invoke() ?? false;
        var folderGone = Until(() => !Directory.Exists(folder.Paths.ProjectDirectory), gone => gone, 10, 5);
        var windowGone = WindowGone(editor);
        Release(editor);
        Until(() => !_services.Tracker.IsOpen(editor.Id), ok => ok, 5, 50);
        _report.Check(
            "a project that cannot be shown has Delete project\u2026 under its message, a button called by its words alone; it asks the same question, with the project's name, and Delete project closes the window and deletes what is left of the project",
            state == "unavailable" && buttonIs
                && pressed && title == "Delete \u201CLost its recording\u201D?" && words.Contains(DeleteWords)
                && agreed && folderGone && windowGone && _services.Store.Deletes == deletesBefore + 1 && _services.Errors().Length == 0,
            $"the window is {state}; {buttonSays}; {asked}; the project's folder is {(folderGone ? "gone" : "there")}, the window {(windowGone ? "gone" : "there")}; delete attempts {_services.Store.Deletes - deletesBefore}");
    }

    /// <summary>Keeps a picture of the window as it is now, for a person to look at. Nothing is read from it.</summary>
    private void ProjectPicture(Editor editor, string name)
    {
        Thread.Sleep(350);
        if (editor.Camera.Take() is { } shot)
        {
            shot.Save(Path.Combine(_output, $"project-{name}.png"));
        }
    }

    private static string Heard(UiaEvent[] events) => events.Length == 0 ? "nothing" : string.Join(", ", events.Select(e => $"\"{e.Text}\""));

    /// <summary>The padding a saved project's file says, or NaN when it cannot be read.</summary>
    private static double PaddingOf(string projectFile)
    {
        try
        {
            return StudioProjectJson.ReadProject(File.ReadAllText(projectFile)).Canvas.Padding;
        }
        catch (Exception)
        {
            return double.NaN;
        }
    }
}
