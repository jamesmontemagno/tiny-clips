using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 1, continued. A project that cannot be shown although its screen recording is still in its
// folder: one whose project file this version cannot read. The window says why, and offers the
// one thing that is left: to save the screen recording as an ordinary video, where saved videos
// go. The project is left as it is. A window that is closed while the recording is still being
// copied closes at once, and the recording is saved and reported all the same.
internal sealed partial class WindowChecks
{
    private const string SaveRecordingButton = "StudioSaveScreenRecordingButton";
    private const string SaveRecordingStatus = "StudioScreenRecordingStatus";
    private const string SaveRecordingHelp = "Saves this project's screen recording to your videos folder as an ordinary video. The project is kept as it is.";
    private const string SaveRecordingTip = "Save the screen recording to your videos folder as an ordinary video";

    // What the window says of a project file that says it was written by a later version.
    private static readonly string UnreadableReason = new StudioUnsupportedSchemaVersionException(99).Message;

    /// <summary>
    /// Makes a project's file one that this version of the app cannot read: it then says that a
    /// later version wrote it. Nothing else in the file or in the folder is touched.
    /// </summary>
    private static bool MakeUnreadable(TestFolder folder)
    {
        var text = File.ReadAllText(folder.Paths.ProjectJsonPath);
        var changed = Regex.Replace(text, "\"schemaVersion\"\\s*:\\s*\\d+", "\"schemaVersion\": 99");
        if (changed == text)
        {
            return false;
        }

        File.WriteAllText(folder.Paths.ProjectJsonPath, changed);
        return true;
    }

    /// <summary>The first 16 hexadecimal digits of the SHA-256 of a file, or why it could not be read.</summary>
    private static string HashOf(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream))[..16];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"not read ({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// Every file of a project folder as one line, with its length, when it was last written and
    /// a hash of what is in it: two of these are the same when nothing in the folder changed.
    /// </summary>
    private static string FolderState(TestFolder folder)
    {
        var directory = folder.Paths.ProjectDirectory;
        return Directory.Exists(directory)
            ? string.Join("; ", Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => $"{Path.GetRelativePath(directory, path)} {new FileInfo(path).Length} bytes {File.GetLastWriteTimeUtc(path):HH:mm:ss.fffffff} {HashOf(path)}"))
            : "no folder";
    }

    /// <summary>Whether a file holds, byte for byte, what another holds.</summary>
    private static bool SameBytes(string path, string other) =>
        File.Exists(path) && File.Exists(other) && new FileInfo(path).Length == new FileInfo(other).Length && new FileInfo(path).Length > 0 && HashOf(path) == HashOf(other);

    /// <summary>
    /// What the status under the button says once a save is over, or what it says when the time
    /// is up. The status of the save before it stays until then, so it is told which that was.
    /// </summary>
    private static string SaveRecordingOutcome(Editor editor, string previous, double seconds = 20) => Until(
        () => NameOf(editor, SaveRecordingStatus, 0),
        text => text != previous && (text.StartsWith("Saved as ", StringComparison.Ordinal) || text.StartsWith("The screen recording could not be saved", StringComparison.Ordinal)),
        seconds);

    private void OpenWhatCannotBeShown()
    {
        Timeline.Mark("1: open a project whose file cannot be read, with its screen recording there");
        var folder = NewScreenProject("Cannot be shown");
        var made = MakeUnreadable(folder);
        var folderBefore = FolderState(folder);
        var editor = Open(folder, "cannot be shown");
        var state = WaitLoaded(editor);
        using var heard = UiaEvents.Listen(_uia, editor.Root);

        // What the window says, and what it offers.
        var panel = Find(editor, "StudioUnavailablePanel", 0.5);
        var heading = NameOf(editor, "StudioUnavailableHeading", 0.5);
        var message = NameOf(editor, "StudioUnavailableMessage", 0.5);
        var button = Find(editor, SaveRecordingButton, 1);
        var tip = OnUi(() => Descendant<Button>(editor.Window.Content, SaveRecordingButton) is { } element ? ToolTipService.GetToolTip(element) as string : null);
        var statusAtFirst = editor.Root.Find(SaveRecordingStatus);
        var order = TabStops(editor);
        File.WriteAllLines(Path.Combine(_output, "tab-order-cannot-be-shown.txt"), order);
        var (panelStop, buttonStop) = (order.IndexOf("StudioUnavailablePanel"), order.IndexOf(SaveRecordingButton));
        var editorParts = new[] { "StudioPlayPauseButton", "StudioExportButton", "StudioPreview" }.Where(id => editor.Root.Find(id) is not null).ToArray();
        _report.Check(
            "a project whose file cannot be read, with its screen recording still in its folder, opens a window that says why and offers to save the recording: a button that is enabled, says what it does, and is the next stop of the Tab key after the message; nothing of the editor is there",
            made && state == "unavailable" && heading == "This project can't be opened" && message == UnreadableReason && panel?.Name == $"{heading}. {message}"
                && button is { ControlType: ControlTypeNames.Button, Name: "Save the screen recording", IsEnabled: true, IsKeyboardFocusable: true, IsOffscreen: false } && button.HelpText == SaveRecordingHelp && tip == SaveRecordingTip
                && statusAtFirst is null && panelStop >= 0 && buttonStop == panelStop + 1 && editorParts.Length == 0,
            $"the project file was made unreadable: {made}; the window is {state}; heading \"{heading}\"; message \"{message}\"; the message as one element: \"{panel?.Name}\"; {button}, enabled {button?.IsEnabled}, described as \"{button?.HelpText}\", tooltip \"{tip}\"; "
                + $"a status before anything was saved: {(statusAtFirst is null ? "none" : $"\"{statusAtFirst.Name}\"")}; the stops of the Tab key: {string.Join(", ", order)}; of the editor there is {(editorParts.Length == 0 ? "nothing" : string.Join(", ", editorParts))}");
        if (state != "unavailable" || button is null)
        {
            CloseQuietly(editor);
            return;
        }

        AuditState(editor, "a project that cannot be shown, with its recording to save", "tree-cannot-be-shown.txt", 0, tree =>
            tree.Any(e => e.Element is { Id: SaveRecordingButton, ControlType: ControlTypeNames.Button }) ? null : "the button is not in what a screen reader walks");

        // Saved once.
        Timeline.Mark("1: Save the screen recording");
        var filesBefore = ExportFiles();
        var givenBefore = _services.Storage.Given().Length;
        var reportedBefore = _services.ScreenRecordingsSaved.Count;
        var exportsBefore = _services.Exports.Count;
        var mark = heard.Mark();
        var pressed = Invoke(editor, SaveRecordingButton);
        var status = SaveRecordingOutcome(editor, string.Empty);
        var given = _services.Storage.Given().Skip(givenBefore).ToArray();
        var wantedStatus = given.Length > 0 ? $"Saved as {Path.GetFileName(given[^1])}." : "Saved as";
        var said = Said(heard, mark, wantedStatus);
        var reported = Until(() => _services.ScreenRecordingsSaved.Skip(reportedBefore).ToArray(), now => now.Length >= 1, 2);
        var files = Until(() => ExportFiles().Except(filesBefore).ToArray(), now => now.Length == 1, 3, 50);
        var copied = given.Length == 1 && SameBytes(given[0], folder.Paths.ScreenPath);
        var folderAfter = FolderState(folder);
        var enabledAgain = Until(() => Find(editor, SaveRecordingButton, 0)?.IsEnabled, enabled => enabled == true, 2);
        _report.Check(
            "the button saves the screen recording where saved videos go, under the name the app gave it: the copy holds what the recording holds byte for byte, the status says its name and a screen reader is told so, the app is told once where it is, and the project folder is as it was",
            pressed && given.Length == 1 && Path.GetDirectoryName(given[0]) == _services.ExportDirectory && status == wantedStatus && said.Said && copied
                && files.Length == 1 && string.Equals(files[0], given[0], StringComparison.OrdinalIgnoreCase)
                && reported.Length == 1 && reported[0].ProjectId == editor.Id && string.Equals(reported[0].Path, given[0], StringComparison.OrdinalIgnoreCase)
                && _services.Exports.Count == exportsBefore && folderAfter == folderBefore && enabledAgain == true && Native.Exists(editor.Handle),
            $"asked for {given.Length} name(s): {string.Join(", ", given.Select(Path.GetFileName))}; the status: \"{status}\"; read out: {said.Heard}; the copy is the recording byte for byte: {copied} "
                + $"({(given.Length > 0 && File.Exists(given[0]) ? new FileInfo(given[0]).Length : 0)} bytes, the recording {new FileInfo(folder.Paths.ScreenPath).Length}); new files where videos go: {string.Join(", ", files.Select(Path.GetFileName))}; "
                + $"told to the app: {(reported.Length == 0 ? "nothing" : string.Join(", ", reported.Select(e => Path.GetFileName(e.Path))))}, and as an export {_services.Exports.Count - exportsBefore} time(s); "
                + $"the project folder {(folderAfter == folderBefore ? "is as it was" : $"was [{folderBefore}] and is [{folderAfter}]")}; the button is enabled again: {enabledAgain}");

        // A second request in the middle of a save. The name is asked for on the UI thread right
        // after the save has begun, and that is where the second request is made from.
        Timeline.Mark("1: a second request while the recording is being saved");
        filesBefore = ExportFiles();
        givenBefore = _services.Storage.Given().Length;
        reportedBefore = _services.ScreenRecordingsSaved.Count;
        (bool Reached, bool ButtonEnabled, bool? ElementEnabled, string Status, bool SecondWasOver, int NamesAsked)? during = null;
        _services.Storage.WhenNextAsked(() =>
        {
            var viewModel = editor.Window.ViewModel;
            var asked = _services.Storage.Given().Length;
            var second = viewModel.SaveScreenRecordingAsync();
            during = (true, viewModel.IsSaveScreenRecordingEnabled, Descendant<Button>(editor.Window.Content, SaveRecordingButton)?.IsEnabled, viewModel.ScreenRecordingStatus, second.IsCompleted, _services.Storage.Given().Length - asked);
        });
        var pressedAgain = Invoke(editor, SaveRecordingButton);
        var statusAgain = SaveRecordingOutcome(editor, status);
        _services.Storage.WhenNextAsked(null);
        Thread.Sleep(300);
        var givenAgain = _services.Storage.Given().Skip(givenBefore).ToArray();
        var filesAgain = Until(() => ExportFiles().Except(filesBefore).ToArray(), now => now.Length == 1, 3, 50);
        var reportedAgain = _services.ScreenRecordingsSaved.Skip(reportedBefore).ToArray();
        _report.Check(
            "while the recording is being saved the button is disabled and the status says that it is being saved; a second request made then does nothing more: no second name is asked for, no second file is written, and the app is told once",
            pressedAgain && during is { Reached: true, ButtonEnabled: false, ElementEnabled: false, Status: "Saving the screen recording\u2026", SecondWasOver: true, NamesAsked: 0 }
                && givenAgain.Length == 1 && statusAgain == $"Saved as {Path.GetFileName(givenAgain[0])}." && filesAgain.Length == 1 && SameBytes(givenAgain[0], folder.Paths.ScreenPath) && reportedAgain.Length == 1,
            during is { } then
                ? $"in the middle of the save the editor says the button is {(then.ButtonEnabled ? "enabled" : "disabled")}, the button itself is {(then.ElementEnabled == false ? "disabled" : "enabled")}, and the status reads \"{then.Status}\"; "
                    + $"the second request was over at once: {then.SecondWasOver}, and asked for {then.NamesAsked} name(s); in all {givenAgain.Length} name(s) were asked for, {filesAgain.Length} file(s) written and the app told {reportedAgain.Length} time(s); the status: \"{statusAgain}\""
                : $"the save never asked for a name; the status: \"{statusAgain}\"");

        // The name is taken by the time the copy is complete.
        Timeline.Mark("1: the recording's name is taken while it is being saved");
        var marker = Encoding.UTF8.GetBytes("Saved by something else while the recording was being copied.");
        filesBefore = ExportFiles();
        givenBefore = _services.Storage.Given().Length;
        reportedBefore = _services.ScreenRecordingsSaved.Count;
        _services.Storage.TakeNextName(marker);
        var pressedTaken = Invoke(editor, SaveRecordingButton);
        var statusTaken = SaveRecordingOutcome(editor, statusAgain);
        _services.Storage.TakeNextName(null);
        var givenTaken = _services.Storage.Given().Skip(givenBefore).ToArray();
        var filesTaken = Until(() => ExportFiles().Except(filesBefore).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(), now => now.Length == 2, 3, 50);
        var reportedTaken = _services.ScreenRecordingsSaved.Skip(reportedBefore).ToArray();
        var markerKept = givenTaken.Length > 0 && File.Exists(givenTaken[0]) && File.ReadAllBytes(givenTaken[0]).AsSpan().SequenceEqual(marker);
        _report.Check(
            "when another file has taken the name by the time the copy is complete, the recording gets the next name and nothing is replaced: the file that took the name is as it was, and the status and the app are given the name the recording got",
            pressedTaken && givenTaken.Length == 2 && markerKept && SameBytes(givenTaken[1], folder.Paths.ScreenPath) && statusTaken == $"Saved as {Path.GetFileName(givenTaken[1])}."
                && filesTaken.Length == 2 && filesTaken.SequenceEqual(givenTaken.OrderBy(path => path, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
                && reportedTaken.Length == 1 && string.Equals(reportedTaken[0].Path, givenTaken[1], StringComparison.OrdinalIgnoreCase) && FolderState(folder) == folderBefore,
            $"asked for {givenTaken.Length} name(s): {string.Join(", ", givenTaken.Select(Path.GetFileName))}; the file that had taken the first is as it was: {markerKept}; the status: \"{statusTaken}\"; "
                + $"new files where videos go: {string.Join(", ", filesTaken.Select(path => $"{Path.GetFileName(path)} ({new FileInfo(path).Length} bytes)"))}; told to the app: {(reportedTaken.Length == 0 ? "nothing" : string.Join(", ", reportedTaken.Select(e => Path.GetFileName(e.Path))))}");

        // A recording that cannot be written where it should go.
        Timeline.Mark("1: a recording that cannot be saved");
        filesBefore = ExportFiles();
        reportedBefore = _services.ScreenRecordingsSaved.Count;
        var errorsBefore = _services.Errors().Length;
        mark = heard.Mark();
        var inTheWay = BlockNextExport();
        var pressedBlocked = Invoke(editor, SaveRecordingButton);
        var statusBlocked = SaveRecordingOutcome(editor, statusTaken);
        var saidBlocked = Said(heard, mark, statusBlocked);
        File.Delete(inTheWay);
        var filesBlocked = ExportFiles().Except(filesBefore).ToArray();
        var enabledAfter = Until(() => Find(editor, SaveRecordingButton, 0)?.IsEnabled, enabled => enabled == true, 2);
        _report.Check(
            "a recording that cannot be written where it should go: the status says that it could not be saved, and why, and a screen reader is told the same; nothing is left behind, the app is told nothing, and the button works again",
            pressedBlocked && statusBlocked.StartsWith("The screen recording could not be saved: ", StringComparison.Ordinal) && statusBlocked.Length > "The screen recording could not be saved: ".Length && saidBlocked.Said
                && filesBlocked.Length == 0 && _services.ScreenRecordingsSaved.Count == reportedBefore && _services.Errors().Length == errorsBefore && enabledAfter == true && FolderState(folder) == folderBefore && Native.Exists(editor.Handle),
            $"the status: \"{statusBlocked}\"; read out: {saidBlocked.Heard}; files left where videos go: {(filesBlocked.Length == 0 ? "none" : string.Join(", ", filesBlocked.Select(Path.GetFileName)))}; "
                + $"told to the app: {_services.ScreenRecordingsSaved.Count - reportedBefore} saved, {_services.Errors().Length - errorsBefore} error(s); the button is enabled again: {enabledAfter}");

        // Closing: nothing to lose, so no question.
        var closePressed = PressClose(editor);
        var gone = WindowGone(editor);
        Release(editor);
        _report.Check(
            "its close button closes it without a question, and the project folder is still as it was before the window opened",
            closePressed && gone && FolderState(folder) == folderBefore,
            $"close pressed {closePressed}, window gone {gone}; the folder: [{FolderState(folder)}]");

        ClosedWhileItsRecordingIsSaved(byEscape: false);
        ClosedWhileItsRecordingIsSaved(byEscape: true);
        NothingToSave();
    }

    /// <summary>
    /// A window that is closed while its screen recording is still being copied, by its close
    /// button or by Esc. The window closes at once, the copy goes on, and the app is told of
    /// the video once it is complete, although the window is gone by then: the editor has not
    /// closed before the copy has reported, so until then the window service still listens to
    /// it, and the project still counts as open.
    /// </summary>
    /// <remarks>
    /// The copy is made to last. The store says, once, that the recording is at a pipe which
    /// hands out the first half of the recording's bytes and then waits
    /// (<see cref="SlowRecording"/>). The window is closed in that wait, the way a person
    /// closes it: by the Close button of its title bar, or by what the window does with Esc.
    /// Then the pipe hands out the rest.
    /// </remarks>
    private void ClosedWhileItsRecordingIsSaved(bool byEscape)
    {
        var how = byEscape ? "Esc" : "its close button";
        var label = $"cannot be shown, closed by {(byEscape ? "Esc" : "the close button")} while saving";
        Timeline.Mark($"1: the window is closed by {how} while its screen recording is being saved");
        var check = $"a window that is closed by {how} while its screen recording is being saved closes at once, and the recording is saved all the same: nothing is reported while the copy is under way, the project counts as open until it is complete, and then the app is told, once, where the video is";
        var folder = NewScreenProject($"Cannot be shown, closed by {(byEscape ? "Esc" : "the close button")} while saving");
        var made = MakeUnreadable(folder);
        var folderBefore = FolderState(folder);
        var editor = Open(folder, label);
        var state = WaitLoaded(editor);
        if (!made || state != "unavailable" || Find(editor, SaveRecordingButton, 1) is null)
        {
            _report.Check(check, false, $"the project file was made unreadable: {made}; the window is {state}; the button is {(editor.Root.Find(SaveRecordingButton) is null ? "not there" : "there")}");
            CloseQuietly(editor);
            return;
        }

        // The save, started by the button, and held up in the middle of its copy.
        using var slow = new SlowRecording(folder.Paths.ScreenPath);
        var filesBefore = ExportFiles();
        var givenBefore = _services.Storage.Given().Length;
        var reportedBefore = _services.ScreenRecordingsSaved.Count;
        var errorsBefore = _services.Errors().Length;
        _services.Store.SendNextRecordingFrom(editor.Id, slow.Path);
        var pressed = Invoke(editor, SaveRecordingButton);
        var copying = slow.WaitUntilHalfIsRead(10);
        _services.Store.SendNextRecordingFrom(null);
        var statusDuring = NameOf(editor, SaveRecordingStatus, 1);

        // Closed while the copy waits for the rest of the recording.
        var closed = byEscape ? Escape(editor) == StudioShortcutAction.RequestClose : PressClose(editor);
        var gone = WindowGone(editor);
        if (gone)
        {
            Release(editor);
        }

        Thread.Sleep(300);
        var reportedDuring = _services.ScreenRecordingsSaved.Count - reportedBefore;
        var openDuring = _services.Tracker.IsOpen(editor.Id);
        var given = _services.Storage.Given().Skip(givenBefore).ToArray();
        var namedDuring = given.Length > 0 && File.Exists(given[0]);

        // The rest of the recording, and its end.
        var taken = slow.Finish(10);
        Until(() => _services.ScreenRecordingsSaved.Count > reportedBefore, told => told, 10);
        var notOpen = Until(() => !_services.Tracker.IsOpen(editor.Id), ok => ok, 10, 50);
        Thread.Sleep(300);
        var reported = _services.ScreenRecordingsSaved.Skip(reportedBefore).ToArray();
        given = _services.Storage.Given().Skip(givenBefore).ToArray();
        var files = Until(() => ExportFiles().Except(filesBefore).ToArray(), now => now.Length == 1, 3, 50);
        var copied = given.Length == 1 && SameBytes(given[0], folder.Paths.ScreenPath);
        var folderAfter = FolderState(folder);
        var errors = _services.Errors().Length - errorsBefore;
        if (!gone)
        {
            CloseQuietly(editor);
        }

        _report.Check(
            check,
            pressed && copying && statusDuring == "Saving the screen recording\u2026" && closed && gone
                && reportedDuring == 0 && openDuring && !namedDuring
                && taken && reported.Length == 1 && reported[0].ProjectId == editor.Id && given.Length == 1 && string.Equals(reported[0].Path, given[0], StringComparison.OrdinalIgnoreCase)
                && copied && files.Length == 1 && string.Equals(files[0], given[0], StringComparison.OrdinalIgnoreCase)
                && notOpen && folderAfter == folderBefore && errors == 0,
            $"Save pressed {pressed}; the copy had opened the recording and taken its first half: {copying}; the status then: \"{statusDuring}\"; closed by {how}: {closed}, and the window gone: {gone}; "
                + $"while the copy waited for the rest, the app had been told {reportedDuring} time(s), the project counted as open: {openDuring}, and a file was under the video's name: {namedDuring}; "
                + $"the rest was taken: {taken}; after that the app was told {reported.Length} time(s){(reported.Length == 0 ? string.Empty : ": " + string.Join(", ", reported.Select(e => Path.GetFileName(e.Path))))}, of {given.Length} name(s) asked for{(given.Length == 0 ? string.Empty : ": " + string.Join(", ", given.Select(Path.GetFileName)))}; "
                + $"the copy is the recording byte for byte: {copied} ({(given.Length > 0 && File.Exists(given[0]) ? new FileInfo(given[0]).Length : 0)} bytes, the recording {slow.Length}); new files where videos go: {(files.Length == 0 ? "none" : string.Join(", ", files.Select(Path.GetFileName)))}; "
                + $"the project counts as open afterwards: {!notOpen}; the project folder {(folderAfter == folderBefore ? "is as it was" : $"was [{folderBefore}] and is [{folderAfter}]")}; errors told to the app: {errors}");
    }

    /// <summary>A project that cannot be read and has no recording in its folder has nothing to offer.</summary>
    private void NothingToSave()
    {
        Timeline.Mark("1: open a project whose file cannot be read and whose recording is gone");
        var folder = NewScreenProject("Cannot be shown, nothing left", writeScreen: false);
        var made = MakeUnreadable(folder);
        var editor = Open(folder, "cannot be shown, nothing left");
        var state = WaitLoaded(editor);
        var message = NameOf(editor, "StudioUnavailableMessage", 0.5);
        var button = editor.Root.Find(SaveRecordingButton);
        var offered = OnUi(() => editor.Window.ViewModel.CanSaveScreenRecording);
        var order = TabStops(editor);
        _report.Check(
            "without a screen recording in its folder, a project that cannot be read offers nothing to save: the window says why it cannot be opened, and the button is not there, neither for a screen reader nor for the Tab key",
            made && state == "unavailable" && message == UnreadableReason && button is null && !offered && !order.Contains(SaveRecordingButton) && order.Contains("StudioUnavailablePanel"),
            $"the window is {state}: \"{message}\"; the button: {(button is null ? "not there" : button.ToString())}; the editor offers to save: {offered}; the stops of the Tab key: {string.Join(", ", order)}");
        CloseQuietly(editor);
    }

    /// <summary>A picture of the window of a project that cannot be shown, with its button, for a person to look at.</summary>
    private void CannotBeShownPicture(string name, bool isLight, List<string> saved)
    {
        Timeline.Mark($"9: a project that cannot be shown, {name}");
        var check = $"the window of a project that cannot be shown, in the {name} theme: a picture shows the message and, whole and enabled, the button that saves the screen recording, on a {(isLight ? "light" : "dark")} surface";
        var folder = NewScreenProject($"Cannot be shown, {name}");
        var made = MakeUnreadable(folder);
        var editor = Open(folder, $"{name} theme, cannot be shown");
        var state = WaitLoaded(editor);
        Thread.Sleep(500);
        var panel = Find(editor, "StudioUnavailablePanel", 0.5);
        var button = Find(editor, SaveRecordingButton, 0.5);
        if (editor.Camera.Take() is not { } shot || panel is null || button is null)
        {
            _report.Check(check, false, $"the project file was made unreadable: {made}; the window is {state}; {(panel is null ? "there is no message" : button is null ? "there is no button" : "no screenshot")}");
            CloseQuietly(editor);
            return;
        }

        var path = Path.Combine(_output, $"cannot-be-shown-{name}.png");
        shot.Save(path);
        saved.Add(path);

        // The button whole in the picture, under the message; and the window's surface beside the button, where nothing is drawn.
        var (x, y, width, height) = button.Bounds;
        var (left, top) = (x - shot.ScreenX, y - shot.ScreenY);
        var whole = width > 0 && height > 0 && left >= 0 && top >= 0 && left + width <= shot.Width && top + height <= shot.Height;
        var under = top >= (Find(editor, "StudioUnavailableMessage", 0.5)?.Bounds is { Height: > 0 } text ? text.Y + text.Height - shot.ScreenY : int.MaxValue);
        var surface = shot.Color(left - (24 * editor.Scale), top + (height / 2.0), 2);
        _report.Check(
            check,
            made && state == "unavailable" && button is { Name: "Save the screen recording", IsEnabled: true, IsOffscreen: false } && whole && under && (isLight ? Luma(surface) > 170 : Luma(surface) < 90),
            $"the window is {state}; {button}, enabled {button.IsEnabled}, at {width}x{height} from ({left},{top}) in a picture of {shot.Width}x{shot.Height}, under the message: {under}; the surface beside it is {surface}; saved as {Path.GetFileName(path)}");
        CloseQuietly(editor);
    }
}
