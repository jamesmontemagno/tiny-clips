using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.App.Views.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Host;
using Windows.System;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 6, continued. Esc asks the window to close, as its close button does, behind the setting that
// guards the other editors against a stray Esc. Where closing asks a question of its own, that
// question is asked, once, whatever the setting says. Where a project is open and closing asks
// nothing, which is a project that was exported, Esc asks whether it was meant while the
// setting is on, and closes at once while it is off. A window that cannot show its project
// closes at once either way. And Esc that is not the window's to act on does nothing: with a
// modifier, held, in a text box, while the list of a drop-down is open and in a drag. While an
// export runs a press stops the export and that is all, and a held key does not even do that.
// It is the Mac's rule.
//
// The key is not pressed. Each check hands the window what its key handler hands it once it
// has mapped a key (StudioWindow.RunShortcut), with the modifiers and as a first press or a
// held key, and reads what the window then shows.
internal sealed partial class WindowChecks
{
    // What main's confirmation is to a screen reader, with Studio's words (TinyClips.Core/Editing/EditorEscape.cs).
    private const string EscapeQuestionId = "EditorCloseConfirmationDialog";
    private const string EscapeQuestionTitle = "Close Studio?";
    private const string EscapeQuestionWords = "Your edits are saved with the project, and you can reopen it from the Clips Library. You can turn off this confirmation in General settings.";
    private const string DraftQuestionTitle = "Export this recording before closing?";
    private const string CanvasDropDown = "StudioCanvasComboBox";

    private void ClosingByEscape()
    {
        // Every check below hands the window the key as its handler would, past this mapping.
        _report.Check(
            "the window maps the Esc key to the editor's keyboard model",
            StudioWindow.MapKey(VirtualKey.Escape) == StudioShortcutKey.Escape,
            $"Esc is {StudioWindow.MapKey(VirtualKey.Escape)}");

        var asksBefore = OnUi(() => _services.Settings.ConfirmEditorEscape);
        try
        {
            EscapeOnAnExportedProject();
            EscapeOnADraft();
            EscapeWhileAnExportRuns();
            EscapeThatIsNotTheWindows();
            EscapeWhereTheProjectCannotBeShown();
        }
        finally
        {
            ConfirmEscape(asksBefore);
        }
    }

    /// <summary>Switches the setting that Settings shows as "Confirm before closing editors with Esc".</summary>
    private void ConfirmEscape(bool isOn) => OnUi(() => { _services.Settings.ConfirmEditorEscape = isOn; });

    /// <summary>The questions the window is asking. There must never be more than one.</summary>
    private static List<UiaElement> Questions(Editor editor)
    {
        try
        {
            return [.. editor.Root.FindAll(ControlTypeNames.Window).Where(window => window.Find("PrimaryButton") is not null)];
        }
        catch (Exception)
        {
            // A window that is going answers nothing.
            return [];
        }
    }

    private static string[] WordsOf(UiaElement? question) =>
        question?.FindAll(ControlTypeNames.Text).Select(text => text.Name).Where(text => text.Length > 0).ToArray() ?? [];

    /// <summary>
    /// Whether a question is the one Esc asks, by what a screen reader is given of it: its
    /// automation id, its title, Studio's sentence, Close Studio and Cancel, and no third answer.
    /// </summary>
    private static bool IsEscapeQuestion(UiaElement? question) =>
        question is { Id: EscapeQuestionId, Name: EscapeQuestionTitle }
        && question.Find("PrimaryButton") is { Name: "Close Studio", IsEnabled: true }
        && question.Find("CloseButton") is { Name: "Cancel" }
        && question.Find("SecondaryButton") is not { Name.Length: > 0 }
        && WordsOf(question).Contains(EscapeQuestionWords);

    /// <summary>Whether a question is the one a recording that was never exported asks, with its four answers.</summary>
    private static bool IsDraftQuestion(UiaElement? question) =>
        question is { Name: DraftQuestionTitle }
        && question.Id != EscapeQuestionId
        && question.Find("PrimaryButton") is { Name: "Export" }
        && question.Find("SecondaryButton") is { Name: "Keep as draft" }
        && question.Find("CloseButton") is { Name: "Cancel" }
        && question.Find("StudioClosePromptDeleteButton") is { Name: "Delete recording" };

    private static string QuestionAsked(UiaElement? question) => question is null
        ? "no question"
        : $"question \"{question.Name}\" [{question.Id}] with {question.Find("PrimaryButton")}, {question.Find("SecondaryButton")?.ToString() ?? "no second button"} and {question.Find("CloseButton")}: {string.Join(" | ", WordsOf(question).Select(text => $"\"{text}\""))}";

    /// <summary>A project that lists an export, without an export having to run: closing it asks nothing.</summary>
    private TestFolder NewExportedProject(string name) => NewScreenProject(name, p => p with
    {
        Exports = [new StudioExport { Path = Path.Combine(_services.ExportDirectory, name + ".mp4"), ExportedAt = DateTimeOffset.UtcNow }],
    });

    /// <summary>
    /// Answers a question with Cancel and gives it the time to finish closing: until then the
    /// window passes over a request to close, and the question still holds on to the window.
    /// </summary>
    private static void Dismiss(Editor editor, UiaElement? question)
    {
        if (question is null)
        {
            return;
        }

        question.Find("CloseButton")?.Invoke();
        Until(() => !HasDialog(editor), gone => gone, 3);
        Thread.Sleep(QuestionSettles);
    }

    /// <summary>Starts the video, so that a question can be seen to stop it.</summary>
    private static bool StartPlaying(Editor editor)
    {
        Invoke(editor, "StudioPlayPauseButton");
        return Until(() => NameOf(editor, "StudioPlayPauseButton", 0), name => name == "Pause", 2) == "Pause";
    }

    private static bool HasStoppedPlaying(Editor editor) =>
        Until(() => NameOf(editor, "StudioPlayPauseButton", 0), name => name == "Play", 2) == "Play";

    /// <summary>
    /// A project that was exported. Closing it asks nothing, so Esc asks whether it was meant:
    /// the question, the question while it is open, Cancel, Close Studio, and the setting off.
    /// </summary>
    private void EscapeOnAnExportedProject()
    {
        Timeline.Mark("6: Esc on a project that was exported");
        var folder = NewExportedProject("Esc, exported");
        if (OpenReady(folder, "Esc, exported") is not { } editor)
        {
            return;
        }

        ConfirmEscape(true);
        var deletesBefore = _services.Store.Deletes;
        var errorsBefore = _services.Errors().Length;
        var playing = StartPlaying(editor);
        var fileBefore = File.ReadAllBytes(folder.Paths.ProjectJsonPath);
        var ran = Escape(editor);
        var question = Dialog(editor);
        var stopped = HasStoppedPlaying(editor);
        var focus = question is null ? string.Empty : Until(() => FocusedId(editor), id => id == "PrimaryButton", 1.5);
        _report.Check(
            "Esc on a project that was exported asks whether it was meant, while the setting is on: the question the other editors ask, with Studio's words, Close Studio and Cancel, and the focus on Close Studio; the window stays, and the video stops playing",
            playing && ran == StudioShortcutAction.RequestClose && IsEscapeQuestion(question) && stopped && focus == "PrimaryButton" && Native.Exists(editor.Handle),
            $"Esc ran {ran}; {QuestionAsked(question)}; focus on \"{focus}\"; the video was playing {playing} and stopped {stopped}; window open {Native.Exists(editor.Handle)}");
        if (question is null)
        {
            CloseQuietly(editor);
            return;
        }

        // While the question is open the key is the question's, and the close button waits.
        var pressedAgain = Escape(editor);
        var held = Escape(editor, repeat: true);
        var closePressed = PressClose(editor);
        Thread.Sleep(300);
        var questions = Questions(editor);
        _report.Check(
            "while that question is open, Esc belongs to it and the close button waits for the answer: what the window runs for the key is nothing, pressed or held, there is still the one question, and the window is still there",
            pressedAgain == StudioShortcutAction.None && held == StudioShortcutAction.None && closePressed && questions.Count == 1 && IsEscapeQuestion(questions[0]) && Native.Exists(editor.Handle),
            $"Esc ran {pressedAgain}, and held {held}; the close button was pressed: {closePressed}; {questions.Count} question(s): {QuestionAsked(questions.FirstOrDefault())}; window open {Native.Exists(editor.Handle)}");

        // Cancel.
        Timeline.Mark("6: Esc, then Cancel");
        var cancelled = question.Find("CloseButton")?.Invoke() ?? false;
        var questionGone = Until(() => !HasDialog(editor), gone => gone, 3);
        var fileAfter = File.ReadAllBytes(folder.Paths.ProjectJsonPath);
        var restedOn = FrameOf(Playhead(editor));
        var stepped = Invoke(editor, "StudioNextFrameButton");
        var after = LookFor(editor, s => s.Shown == Both(editor, restedOn + 1), 3);
        _report.Check(
            "Cancel: the question goes, the window stays, the project file is as it was, and the editor goes on working: Next frame steps one frame on",
            cancelled && questionGone && Native.Exists(editor.Handle) && fileAfter.AsSpan().SequenceEqual(fileBefore) && stepped && after?.Shown == Both(editor, restedOn + 1)
                && _services.Store.Deletes == deletesBefore && _services.Errors().Length == errorsBefore,
            $"question gone {questionGone}; window open {Native.Exists(editor.Handle)}; the project file is {(fileAfter.AsSpan().SequenceEqual(fileBefore) ? "as it was" : $"another: {fileBefore.Length} bytes before, {fileAfter.Length} now")}; the video had stopped on frame {restedOn}, and the picture now shows {after?.Shown}");
        Thread.Sleep(QuestionSettles);

        // Close Studio.
        Timeline.Mark("6: Esc, then Close Studio");
        var ranAgain = Escape(editor);
        var again = Dialog(editor);
        var sameQuestion = IsEscapeQuestion(again);
        var confirmed = again?.Find("PrimaryButton")?.Invoke() ?? false;
        var windowGone = WindowGone(editor);
        if (windowGone)
        {
            Release(editor);
        }
        else
        {
            CloseQuietly(editor);
        }

        var notOpen = Until(() => !_services.Tracker.IsOpen(editor.Id), ok => ok, 10, 50);
        var project = _services.Store.Load(editor.Id);
        _report.Check(
            "Esc again, and Close Studio: the window closes, and the project stays, with its export listed and nothing deleted",
            ranAgain == StudioShortcutAction.RequestClose && sameQuestion && confirmed && windowGone && notOpen && project.Exports.Length == 1 && File.Exists(folder.Paths.ScreenPath)
                && _services.Store.Deletes == deletesBefore && _services.Errors().Length == errorsBefore,
            $"Esc ran {ranAgain}; the same question {sameQuestion}; Close Studio pressed {confirmed}; window gone {windowGone}; exports listed {project.Exports.Length}; the recording is there: {File.Exists(folder.Paths.ScreenPath)}; deletes {_services.Store.Deletes - deletesBefore}");

        // The setting off.
        Timeline.Mark("6: Esc on a project that was exported, with the confirmation switched off");
        ConfirmEscape(false);
        if (OpenReady(folder, "Esc, exported, not asked") is not { } quiet)
        {
            ConfirmEscape(true);
            return;
        }

        var ranQuiet = Escape(quiet);
        var askedAtOnce = Questions(quiet).Count;
        var goneAtOnce = WindowGone(quiet, 5);
        var askedLater = goneAtOnce ? 0 : Questions(quiet).Count;
        if (goneAtOnce)
        {
            Release(quiet);
        }
        else
        {
            CloseQuietly(quiet);
        }

        ConfirmEscape(true);
        var left = _services.Store.Load(quiet.Id);
        _report.Check(
            "with the confirmation switched off, Esc on a project that was exported closes the window at once, without a question, and the project stays",
            ranQuiet == StudioShortcutAction.RequestClose && askedAtOnce == 0 && goneAtOnce && left.Exports.Length == 1 && File.Exists(folder.Paths.ScreenPath)
                && _services.Store.Deletes == deletesBefore && _services.Errors().Length == errorsBefore,
            $"Esc ran {ranQuiet}; window gone {goneAtOnce}; questions asked: {askedAtOnce + askedLater}; exports listed {left.Exports.Length}");
    }

    /// <summary>
    /// A recording that was never exported. Closing it asks what to do with it, and that is the
    /// confirmation: Esc asks it whatever the setting says, once, and nothing after it.
    /// </summary>
    private void EscapeOnADraft()
    {
        Timeline.Mark("6: Esc on a recording that was never exported");
        var folder = NewScreenProject("Esc, a draft");
        if (OpenReady(folder, "Esc, a draft") is not { } editor)
        {
            return;
        }

        // With the confirmation switched off: the question about the recording is not the setting's to take away.
        ConfirmEscape(false);
        var deletesBefore = _services.Store.Deletes;
        var playing = StartPlaying(editor);
        var ran = Escape(editor);
        var question = Dialog(editor);
        var stopped = HasStoppedPlaying(editor);
        var asked = Questions(editor).Count;
        _report.Check(
            "Esc on a recording that was never exported asks what its close button asks, also with the confirmation switched off: Export, Keep as draft, Cancel and Delete, in one question; the window stays, and the video stops playing",
            playing && ran == StudioShortcutAction.RequestClose && IsDraftQuestion(question) && asked == 1 && stopped && Native.Exists(editor.Handle),
            $"Esc ran {ran}; {asked} question(s): {QuestionAsked(question)}, Delete: {question?.Find("StudioClosePromptDeleteButton")?.ToString() ?? "none"}; the video was playing {playing} and stopped {stopped}; window open {Native.Exists(editor.Handle)}");
        ConfirmEscape(true);
        if (question is null)
        {
            CloseQuietly(editor);
            return;
        }

        Dismiss(editor, question);

        // With the confirmation on: that question again, and after its answer no other.
        Timeline.Mark("6: Esc, then Keep as draft");
        var ranAgain = Escape(editor);
        var again = Dialog(editor);
        var sameQuestion = IsDraftQuestion(again);
        var kept = again?.Find("SecondaryButton")?.Invoke() ?? false;
        var second = string.Empty;
        var watch = Stopwatch.StartNew();
        while (Native.Exists(editor.Handle) && watch.Elapsed.TotalSeconds < 5)
        {
            if (Questions(editor).FirstOrDefault(candidate => candidate.Id == EscapeQuestionId || candidate.Name == EscapeQuestionTitle) is { } extra)
            {
                second = QuestionAsked(extra);
                break;
            }

            Thread.Sleep(20);
        }

        var gone = second.Length == 0 && WindowGone(editor, 1);
        if (gone)
        {
            Release(editor);
        }
        else
        {
            CloseQuietly(editor);
        }

        var notOpen = Until(() => !_services.Tracker.IsOpen(editor.Id), ok => ok, 10, 50);
        var project = _services.Store.Load(editor.Id);
        _report.Check(
            "with the confirmation on it is that question again and no other: after Keep as draft the window closes without a second question, and the recording stays as a draft",
            ranAgain == StudioShortcutAction.RequestClose && sameQuestion && kept && second.Length == 0 && gone && notOpen && project.Exports.Length == 0 && File.Exists(folder.Paths.ScreenPath) && _services.Store.Deletes == deletesBefore,
            $"Esc ran {ranAgain}; {QuestionAsked(again)}; Keep as draft pressed {kept}; {(second.Length == 0 ? "no second question" : "a second question: " + second)}; window gone {gone}; exports listed {project.Exports.Length}; the recording is there: {File.Exists(folder.Paths.ScreenPath)}");
    }

    /// <summary>
    /// While an export runs a press of Esc stops it, and that is all. A key that is held does
    /// nothing at all: it does not stop an export it comes to from somewhere else, and once
    /// the export it stopped is gone it does not go on to close the window. A new press asks.
    /// </summary>
    private void EscapeWhileAnExportRuns()
    {
        Timeline.Mark("6: Esc while an export runs, held, and pressed again");
        var folder = NewCameraProject("Esc, exporting");
        if (OpenReady(folder, "Esc, exporting") is not { } editor)
        {
            return;
        }

        ConfirmEscape(true);
        var filesBefore = ExportFiles();
        Invoke(editor, "StudioExportButton");
        var overlay = Find(editor, "StudioCancelExportButton", 3);
        WatchProgress(editor, value => value >= 3, 20);

        // A key that is still held when it comes to this window: the Esc that answered Keep
        // exporting in the question about this export and was held a little too long, or the
        // one that closed a window in front of this one. It was not meant for the export.
        var heldBefore = Escape(editor, repeat: true);

        // An export that is stopped was gone within 0.4 s in every run so far.
        Thread.Sleep(600);
        var goesOn = IsExporting(editor) && editor.Root.Find("StudioCancelExportButton") is not null;
        var endedByItself = _services.Exports.Any(e => e.ProjectId == editor.Id);
        _report.Check(
            "Esc that is being held when it comes to a window whose export runs stops nothing: what the window runs for it is nothing, and the export goes on",
            overlay is not null && heldBefore == StudioShortcutAction.None && goesOn,
            $"held, Esc ran {heldBefore}; 0.6 s later the export was still running: {goesOn}{(endedByItself ? "; the export had finished by itself by then, so the tool could not see it go on" : string.Empty)}");

        var underWay = IsExporting(editor);
        var ran = Escape(editor);

        // The key is still down: while the export stops, and once it is gone.
        var heldWhileItStops = Escape(editor, repeat: true);
        var overlayGone = Gone(editor, "StudioCancelExportButton", 15);
        var working = Until(() => Find(editor, "StudioPlayPauseButton", 0)?.IsEnabled == true, ok => ok, 3);
        var heldAfter = Escape(editor, repeat: true);
        Thread.Sleep(300);
        var asked = Questions(editor).Count;
        var files = Until(() => ExportFiles().Except(filesBefore).ToArray(), left => left.Length == 0, 3, 50);
        var listed = _services.Store.Load(editor.Id).Exports.Length;
        _report.Check(
            "a press of Esc while an export runs stops the export, and that is all: the window stays, nothing is asked and nothing is written; and the key, still held while the export stops and when it is gone, runs nothing",
            overlay is not null && underWay && ran == StudioShortcutAction.CancelExport && heldWhileItStops == StudioShortcutAction.None
                && overlayGone && working && heldAfter == StudioShortcutAction.None && asked == 0 && Native.Exists(editor.Handle) && files.Length == 0 && listed == 0,
            $"an export was under way: {underWay}; Esc ran {ran}; held while it stopped, {heldWhileItStops}; the overlay went: {overlayGone}, and the editor works again: {working}; held after that, {heldAfter}; questions asked: {asked}; window open {Native.Exists(editor.Handle)}; files left: {(files.Length == 0 ? "none" : string.Join(", ", files.Select(Path.GetFileName)))}; exports listed {listed}");

        // A new press, with the export gone.
        var pressedAgain = Escape(editor);
        var question = Dialog(editor);
        _report.Check(
            "pressed again with the export gone, Esc asks the window to close: the recording was never exported, so it asks what to do with it",
            pressedAgain == StudioShortcutAction.RequestClose && IsDraftQuestion(question) && Questions(editor).Count == 1 && Native.Exists(editor.Handle),
            $"Esc ran {pressedAgain}; {QuestionAsked(question)}");
        Dismiss(editor, question);
        if (Native.Exists(editor.Handle))
        {
            EscapeTwiceBeforeTheFocusIsPlaced(editor);
        }

        CloseQuietly(editor);
    }

    /// <summary>
    /// Esc pressed twice in quick succession while an export runs. The first press stops the
    /// export, and the end of an export asks for the keyboard focus to go back to Export, which
    /// the window does after everything else it has to do. The second press comes before that,
    /// and opens the question about the recording. The focus has to stay with that question:
    /// on Export under it, it would take Esc away from the question, and Enter or Space would
    /// start an export under it. Once the question has been answered, the focus that waited is
    /// put on Export.
    /// </summary>
    /// <remarks>
    /// The second press is made on the UI thread, in the window's own turn in which the export
    /// is gone: after the window has asked for the focus, and before it has put it anywhere. A
    /// key that is in the queue when the export ends is handled at the same point.
    /// </remarks>
    private void EscapeTwiceBeforeTheFocusIsPlaced(Editor editor)
    {
        Timeline.Mark("6: Esc twice, the second before the focus that the stopped export asked for has been placed");
        Invoke(editor, "StudioExportButton");
        var overlay = Find(editor, "StudioCancelExportButton", 3);
        WatchProgress(editor, value => value >= 3, 20);
        var underWay = IsExporting(editor);

        var viewModel = OnUi(() => editor.Window.ViewModel);
        var window = editor.Window;
        var second = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = 1;
        void PressAgain(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Added after the window's own handler, so it runs after that one has asked for the focus.
            if (viewModel.IsExporting || Interlocked.Exchange(ref armed, 0) == 0)
            {
                return;
            }

            try
            {
                second.SetResult(window.RunShortcut(StudioShortcutKey.Escape, isControlDown: false, isShiftDown: false, isAltDown: false, isRepeat: false).ToString());
            }
            catch (Exception ex)
            {
                second.SetResult($"an exception: {ex.Message}");
            }
        }

        var first = StudioShortcutAction.None;
        var secondRan = "nothing, the export did not end";
        UiaElement? question = null;
        var (givenFocus, stopWatching) = WatchFocusUnderAQuestion(editor);
        var focusAtFirst = string.Empty;
        var focus = string.Empty;
        try
        {
            OnUi(() => { viewModel.PropertyChanged += PressAgain; });
            try
            {
                first = underWay ? Escape(editor) : StudioShortcutAction.None;
                if (second.Task.Wait(TimeSpan.FromSeconds(15)))
                {
                    secondRan = second.Task.Result;
                }
            }
            finally
            {
                OnUi(() => { viewModel.PropertyChanged -= PressAgain; });
            }

            question = Dialog(editor);
            focusAtFirst = question is null ? string.Empty : Until(() => FocusedId(editor), IsOnTheQuestion, 1.5);
            WhenTheUiThreadHasNothingWaiting();
            focus = FocusedId(editor);
        }
        finally
        {
            stopWatching();
        }

        var asked = Questions(editor).Count;
        var given = givenFocus.Given().Where(id => !IsOnTheQuestion(id)).ToArray();
        _report.Check(
            "Esc pressed twice, the second press before the window has put the focus back on Export for the export the first one stopped: the question about the recording is asked, once, and the keyboard stays with it, on one of its own answers",
            overlay is not null && underWay && first == StudioShortcutAction.CancelExport && secondRan == nameof(StudioShortcutAction.RequestClose)
                && IsDraftQuestion(question) && asked == 1 && IsOnTheQuestion(focusAtFirst) && IsOnTheQuestion(focus) && Native.Exists(editor.Handle),
            $"an export was under way: {underWay}; the first press ran {first}, and the second, made as the export went, {secondRan}; {asked} question(s): {QuestionAsked(question)}; "
                + $"the focus was on \"{focusAtFirst}\" when the question had opened, and on \"{focus}\" once the window had done what it had waiting; "
                + $"given the focus in the window itself from the first press on: {(given.Length == 0 ? "nothing" : string.Join(", ", given))}, which the end of an export may account for without the question being open yet");
        if (question is null)
        {
            return;
        }

        // Cancel. The focus that waited for the answer is put where the end of the export wanted it.
        var cancelled = question.Find("CloseButton")?.Invoke() ?? false;
        var questionGone = Until(() => !HasDialog(editor), gone => gone, 3);
        var focusAfter = Until(() => FocusedId(editor), id => id == "StudioExportButton", 2);
        _report.Check(
            "answered with Cancel, the question goes, the window stays, and the focus that waited for the answer is on Export",
            cancelled && questionGone && Native.Exists(editor.Handle) && focusAfter == "StudioExportButton" && !IsExporting(editor),
            $"Cancel pressed {cancelled}; question gone {questionGone}; window open {Native.Exists(editor.Handle)}; the focus is on \"{focusAfter}\"; an export is running: {IsExporting(editor)}");
        Thread.Sleep(QuestionSettles);
    }

    /// <summary>
    /// Esc that is not the window's to act on: held, with a modifier, while the list of a
    /// drop-down is open, in a text box, and in the middle of a drag. And a drop-down that only
    /// has the focus, where Esc is the window's like anywhere else. On a project that was
    /// exported, with the confirmation on, so that a key the window acts on shows as a question.
    /// </summary>
    private void EscapeThatIsNotTheWindows()
    {
        Timeline.Mark("6: Esc that is not the window's to act on");
        var folder = NewExportedProject("Esc, left alone");
        if (OpenReady(folder, "Esc, left alone") is not { } editor)
        {
            return;
        }

        ConfirmEscape(true);
        bool Untouched()
        {
            Thread.Sleep(250);
            return Questions(editor).Count == 0 && Native.Exists(editor.Handle);
        }

        // Held, and with Ctrl, Shift or Alt.
        (string Name, StudioShortcutAction Ran)[] modified =
        [
            ("held", Escape(editor, repeat: true)),
            ("Ctrl+Esc", Escape(editor, control: true)),
            ("Shift+Esc", Escape(editor, shift: true)),
            ("Alt+Esc", Escape(editor, alt: true)),
            ("Ctrl+Shift+Esc", Escape(editor, control: true, shift: true)),
        ];
        var modifiedLeft = Untouched();
        _report.Check(
            "Esc that is held, and Esc with Ctrl, Shift or Alt, is not the window's: it runs nothing, asks nothing, and the window stays",
            modified.All(press => press.Ran == StudioShortcutAction.None) && modifiedLeft,
            $"{string.Join(", ", modified.Select(press => $"{press.Name} ran {press.Ran}"))}; no question and the window still there: {modifiedLeft}");

        // A drop-down whose list is closed only has the focus, as it does after a choice was
        // made from it with the mouse. Esc is the window's there: it asks.
        Timeline.Mark("6: Esc with the focus on a drop-down that is closed");
        var onList = FocusOn(editor, CanvasDropDown);
        var closedRan = Escape(editor);
        var closedQuestion = Dialog(editor);
        _report.Check(
            "Esc with the focus on a drop-down whose list is closed asks the window to close, as it does anywhere else: on this project, the question whether it was meant",
            onList == CanvasDropDown && closedRan == StudioShortcutAction.RequestClose && IsEscapeQuestion(closedQuestion) && Native.Exists(editor.Handle),
            $"with the focus on \"{onList}\" and its list closed, Esc ran {closedRan}: {QuestionAsked(closedQuestion)}; window open {Native.Exists(editor.Handle)}");
        Dismiss(editor, closedQuestion);
        if (!Native.Exists(editor.Handle))
        {
            // The key closed the window. What follows has no window to be made in.
            Release(editor);
            return;
        }

        EscapeWhileAListIsOpen(editor);
        if (!Native.Exists(editor.Handle))
        {
            Release(editor);
            return;
        }

        // A text box. The editor has none, so one is put into its window for this.
        Timeline.Mark("6: Esc in a text box");
        var box = OnUi<TextBox?>(() =>
        {
            if (editor.Window.Content is not Grid root)
            {
                return null;
            }

            // In the row of the preview, which takes what room there is: nothing else moves.
            var added = new TextBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetRow(added, 1);
            root.Children.Add(added);
            return added;
        });
        var inBox = false;
        var boxRan = StudioShortcutAction.None;
        if (box is not null)
        {
            try
            {
                // A control takes the focus once it is in the tree, which is a moment after it is added.
                inBox = Until(
                    () => OnUi(() => box.Focus(FocusState.Programmatic) && editor.Window.Content?.XamlRoot is { } root && ReferenceEquals(FocusManager.GetFocusedElement(root), box)),
                    focused => focused,
                    2,
                    50);
                boxRan = Escape(editor);
            }
            finally
            {
                OnUi(() =>
                {
                    if (box.Parent is Panel parent)
                    {
                        parent.Children.Remove(box);
                    }
                });
            }
        }

        var boxLeft = Untouched();
        var onPlay = FocusOn(editor, "StudioPlayPauseButton");
        _report.Check(
            "Esc in a text box belongs to the text box: the window runs nothing, asks nothing and stays. The editor has no text box, so the tool puts one into the window for this",
            inBox && boxRan == StudioShortcutAction.None && boxLeft,
            $"with the focus in a text box ({inBox}) Esc ran {boxRan}; no question and the window still there: {boxLeft}");

        // In the middle of a drag, and after it.
        Timeline.Mark("6: Esc in the middle of a drag");
        var viewModel = OnUi(() => editor.Window.ViewModel);
        OnUi(viewModel.BeginGesture);
        var dragging = OnUi(() => viewModel.IsInGesture);
        var dragRan = Escape(editor);
        var dragLeft = Untouched();
        OnUi(viewModel.EndGesture);
        var over = !OnUi(() => viewModel.IsInGesture);
        var afterRan = Escape(editor);
        var question = Dialog(editor);
        _report.Check(
            "Esc in the middle of a drag does nothing: no question, and the window stays; once the drag is over, the same key asks",
            onPlay == "StudioPlayPauseButton" && dragging && dragRan == StudioShortcutAction.None && dragLeft && over && afterRan == StudioShortcutAction.RequestClose && IsEscapeQuestion(question),
            $"a drag was open: {dragging}; Esc ran {dragRan}, no question and the window still there: {dragLeft}; with the drag over ({over}) Esc ran {afterRan}: {QuestionAsked(question)}");
        Dismiss(editor, question);

        // This window had the keyboard focus put on Play. A window that is closed while the
        // framework waits to show the tooltip of such a button stays in memory, which is the
        // framework's doing (see ToolTipWait), so the tooltip is waited for before it closes.
        KeyboardFocusWhereTheFocusIs(editor, "StudioPlayPauseButton");
        ToolTipShown(editor, 5);
        CloseQuietly(editor);
    }

    /// <summary>
    /// While the list of a drop-down is open, Esc is the list's. The list is opened the way a
    /// screen reader opens it, for as short a time as it takes to hand the key over, and closed
    /// again the same way. It is the one list the tool ever opens, so what the open list was to
    /// the system is read as well, and judged in a check of its own.
    /// </summary>
    private void EscapeWhileAListIsOpen(Editor editor)
    {
        Timeline.Mark("6: Esc while the list of a drop-down is open");
        bool IsOpen() => OnUi(() => Descendant<ComboBox>(editor.Window.Content, CanvasDropDown)?.IsDropDownOpen == true);
        int Chosen() => OnUi(() => Descendant<ComboBox>(editor.Window.Content, CanvasDropDown)?.SelectedIndex ?? -1);

        var onList = FocusOn(editor, CanvasDropDown);

        // The drop-down has a tooltip, which is a window of its own and comes 0.8 s after the
        // keyboard focus. It is waited for, so that it is not taken for the list's window.
        ToolTipShown(editor, 1.2);
        var chosenBefore = Chosen();
        var element = Find(editor, CanvasDropDown, 1);
        var windowsBefore = Native.VisibleWindowsOfThisProcess();
        var watch = Stopwatch.StartNew();
        var expanded = element?.Expand() ?? false;
        var opened = Until(() => IsOpen(), open => open, 2, 10);

        // A list that is a window of its own is one within a moment of being open.
        if (opened)
        {
            Until(() => Native.VisibleWindowsOfThisProcess().Except(windowsBefore).Any(), any => any, 0.4, 20);
        }

        var (seen, inFront) = opened ? OpenListAsTheSystemHasIt(windowsBefore) : ("the list did not open", false);
        var focusIn = OnUi(() => editor.Window.Content?.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is { } focused ? focused.GetType().Name : "nothing");
        var ran = Escape(editor);
        var stillOpen = IsOpen();
        var collapsed = element?.Collapse() ?? false;
        var closedAgain = Until(() => !IsOpen(), closed => closed, 2, 10);
        var openFor = watch.Elapsed.TotalMilliseconds;
        if (!closedAgain)
        {
            // Not left open, whatever went wrong.
            OnUi(() =>
            {
                if (Descendant<ComboBox>(editor.Window.Content, CanvasDropDown) is { } list)
                {
                    list.IsDropDownOpen = false;
                }
            });
        }

        Thread.Sleep(250);
        var asked = Questions(editor);
        var chosenAfter = Chosen();
        _report.Check(
            "while the list of a drop-down is open, Esc is the list's: what the window runs for the key is nothing, there is no question and the window stays, and the list and what is chosen in it are as they were",
            onList == CanvasDropDown && expanded && opened && ran == StudioShortcutAction.None && stillOpen && asked.Count == 0 && Native.Exists(editor.Handle) && chosenAfter == chosenBefore && collapsed && closedAgain,
            $"the list was opened through UI Automation: {expanded}, and was open: {opened}, with the focus on a {focusIn}; Esc ran {ran}; the list was still open after it: {stillOpen}; "
                + $"{(asked.Count == 0 ? "no question" : QuestionAsked(asked[0]))}; closed again through UI Automation: {collapsed && closedAgain}, {F(openFor, "0")} ms after it was opened; item {chosenBefore} was chosen before and item {chosenAfter} after");
        if (opened)
        {
            _report.Check(
                "the list the tool opened for that was not in front of anything: it was drawn in the editor's own window, or was a window behind the one in front",
                !inFront,
                seen);
        }

        Dismiss(editor, asked.FirstOrDefault());
    }

    /// <summary>
    /// What an open list is to the system: no window of its own, or one, and then whether the
    /// system keeps that window in front and where it is among the windows on the desktop. In
    /// front means kept in front by the system, or ahead of the window that is in front.
    /// </summary>
    private static (string Seen, bool InFront) OpenListAsTheSystemHasIt(List<nint> windowsBefore)
    {
        var added = Native.VisibleWindowsOfThisProcess().Except(windowsBefore).ToArray();
        if (added.Length == 0)
        {
            return ("the open list was drawn in the editor's own window: the tool had no window more while it was open", false);
        }

        var all = Native.VisibleWindowsFromTheFront();
        var front = Native.Foreground();
        var frontPlace = all.IndexOf(front) + 1;
        var inFront = false;
        var parts = new List<string>();
        foreach (var window in added)
        {
            var place = all.IndexOf(window) + 1;
            var keptInFront = Native.IsTopmost(window);
            inFront |= keptInFront || (frontPlace > 0 && place > 0 && place < frontPlace);
            parts.Add($"class {Native.ClassOf(window)}, {(keptInFront ? "one the system keeps in front" : "not one the system keeps in front")}, in place {place} of {all.Count} from the front");
        }

        return ($"while the list was open the tool had {added.Length} window(s) more: {string.Join("; ", parts)}; the window in front, \"{Native.TitleOf(front)}\", was in place {frontPlace}", inFront);
    }

    /// <summary>
    /// A window that cannot show its project. The question Esc asks says that the edits are
    /// saved, which is no sentence for a window that opened nothing. So Esc closes such a window
    /// as its close button does: at once and without a question, whatever the setting says.
    /// </summary>
    private void EscapeWhereTheProjectCannotBeShown()
    {
        Timeline.Mark("6: Esc in a window that cannot show its project");
        var folder = NewScreenProject("Esc, cannot be shown", writeScreen: false);
        var errorsBefore = _services.Errors().Length;
        var whenOn = EscapeWhereNothingIsShown(folder, "Esc, cannot be shown", isConfirmationOn: true);
        _report.Check(
            "in a window that cannot show its project, Esc closes the window at once, as its close button does, although the setting is on: nothing is asked, and the project folder stays",
            whenOn.State == "unavailable" && whenOn.Ran == StudioShortcutAction.RequestClose && whenOn.Asked.Length == 0 && whenOn.Gone
                && Directory.Exists(folder.Paths.ProjectDirectory) && _services.Errors().Length == errorsBefore,
            $"the window is {whenOn.State}; Esc ran {whenOn.Ran}; {(whenOn.Asked.Length == 0 ? "nothing was asked" : "it asked: " + whenOn.Asked)}; window gone {whenOn.Gone}; the project folder is there: {Directory.Exists(folder.Paths.ProjectDirectory)}");

        Timeline.Mark("6: Esc in a window that cannot show its project, with the confirmation switched off");
        var whenOff = EscapeWhereNothingIsShown(folder, "Esc, cannot be shown, not asked", isConfirmationOn: false);
        _report.Check(
            "and with the confirmation switched off it is the same: the window closes at once, and nothing is asked",
            whenOff.State == "unavailable" && whenOff.Ran == StudioShortcutAction.RequestClose && whenOff.Asked.Length == 0 && whenOff.Gone
                && Directory.Exists(folder.Paths.ProjectDirectory) && _services.Errors().Length == errorsBefore,
            $"the window is {whenOff.State}; Esc ran {whenOff.Ran}; {(whenOff.Asked.Length == 0 ? "nothing was asked" : "it asked: " + whenOff.Asked)}; window gone {whenOff.Gone}");
    }

    /// <summary>
    /// Opens a window on a project that cannot be shown, hands it Esc, and says what came of
    /// it: what the key ran, the question if one was asked, and whether the window went.
    /// </summary>
    private (string State, StudioShortcutAction Ran, string Asked, bool Gone) EscapeWhereNothingIsShown(TestFolder folder, string label, bool isConfirmationOn)
    {
        ConfirmEscape(isConfirmationOn);
        var editor = Open(folder, label);
        var state = WaitLoaded(editor);
        var ran = Escape(editor);

        // A question is in the tree within a moment of the key. A window that closes is gone before that.
        var asked = string.Empty;
        var watch = Stopwatch.StartNew();
        while (Native.Exists(editor.Handle) && watch.Elapsed.TotalSeconds < 3)
        {
            if (Questions(editor).FirstOrDefault() is { } question)
            {
                asked = QuestionAsked(question);
                break;
            }

            Thread.Sleep(20);
        }

        var gone = asked.Length == 0 && WindowGone(editor, 2);
        if (gone)
        {
            Release(editor);
        }
        else
        {
            CloseQuietly(editor);
        }

        // The next window on this project opens once this one has let go of it.
        Until(() => !_services.Tracker.IsOpen(editor.Id), ok => ok, 10, 50);
        ConfirmEscape(true);
        return (state, ran, asked, gone);
    }
}
