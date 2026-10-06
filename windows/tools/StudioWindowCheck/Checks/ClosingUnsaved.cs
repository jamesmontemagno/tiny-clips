using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 6, continued. A save that fails. While its window is open the window says so itself, in its
// message bar. A window that is closing saves its edits as its last act, and has no message bar
// left to say it in: what goes wrong then is handed to the app, which says it in the window's
// place. The store of the tool refuses the saves, as a disk that is full does.
internal sealed partial class WindowChecks
{
    private const string SaveRefusal = "The check's disk is full.";
    private const string SaveSentence = "Studio could not save this project: " + SaveRefusal;

    // What the window service hands on, as the tool writes it down: the kind of the error, then its sentence.
    private const string SaveReport = "Save: " + SaveSentence;

    private void ClosingWhenTheSaveFails()
    {
        ClosedWithAnEditThatCannotBeSaved();
        ClosedAfterItsBarSaidSo();
    }

    /// <summary>
    /// An edit and the close in one turn of the UI thread, as when the app exits right after an
    /// edit: the edit has not been saved when the window closes, and the save at the close is
    /// the first that is tried.
    /// </summary>
    private void ClosedWithAnEditThatCannotBeSaved()
    {
        Timeline.Mark("6: a window closed with an edit that cannot be saved");
        var folder = NewScreenProject("Closing, cannot be saved");
        if (OpenReady(folder, "closing, cannot be saved") is not { } editor)
        {
            return;
        }

        var savedBefore = Describe(_services.Store.Load(editor.Id));
        var errorsBefore = _services.Errors().Length;
        var refusedBefore = _services.Store.RefusedSaves;
        using var heard = UiaEvents.Listen(_uia, editor.Root);
        var mark = heard.Mark();
        bool ended;
        bool gone;
        bool question;
        string heldByEditor;
        string[] reported;
        _services.Store.RefuseSaves(editor.Id, SaveRefusal);
        try
        {
            var (teardown, message) = OnUi(() =>
            {
                var window = editor.Window;
                window.ViewModel.CanvasPadding = 0.2;
                var task = window.CloseForExit();
                return (task, window.ViewModel.ErrorMessage);
            });
            heldByEditor = message;
            ended = teardown.Wait(TimeSpan.FromSeconds(15));
            gone = WindowGone(editor, 5);
            question = !gone && HasDialog(editor);
            Release(editor);

            // One report, and no second one a moment later.
            Until(() => _services.Errors().Length, count => count > errorsBefore, 3);
            Thread.Sleep(300);
            reported = [.. _services.Errors().Skip(errorsBefore)];
        }
        finally
        {
            _services.Store.RefuseSaves(null);
        }

        var refused = _services.Store.RefusedSaves - refusedBefore;
        var notOpen = Until(() => !_services.Tracker.IsOpen(editor.Id), ok => ok, 10, 50);
        var savedAfter = Describe(_services.Store.Load(editor.Id));
        var said = heard.Since(mark, "notification");

        // What was brought about on purpose is taken out of what the run reports at its end.
        foreach (var error in reported.Where(error => error == SaveReport))
        {
            _services.Forget(error);
        }

        _report.Check(
            "a window that closes with an edit that cannot be saved closes all the same, without a question, and what it has no message bar left to say is reported to the app, once, in the words the bar would have had; the project is as it was last saved",
            ended && gone && !question && reported.Length == 1 && reported[0] == SaveReport && refused == 1 && notOpen && savedAfter == savedBefore,
            $"the window had let go of the project within 15 s: {ended}, and was gone: {gone}{(question ? ", after a question" : string.Empty)}; the store refused {refused} save(s); "
                + $"reported to the app: {(reported.Length == 0 ? "nothing" : string.Join(" | ", reported.Select(error => $"\"{error}\"")))}; the project is no longer counted as open: {notOpen}; "
                + $"{(savedAfter == savedBefore ? "the project file is as it was before the edit" : $"the project file holds {savedAfter}, and held {savedBefore}")}");
        _report.Note($"from the window that was closing, a screen reader was sent {(said.Length == 0 ? "nothing" : string.Join(", ", said.Select(e => $"\"{e.Text}\"")))}{(heard.Problem is null ? string.Empty : $" ({heard.Problem})")}; "
            + $"its editor held the message \"{heldByEditor}\" when the window closed. Nothing of that is judged: a window that is gone shows nothing");
    }

    /// <summary>
    /// The same in the order a person meets it: the edit cannot be saved while the window is
    /// open, which the window says itself; then the window is closed, and the save it tries as
    /// its last act is the app's to say.
    /// </summary>
    private void ClosedAfterItsBarSaidSo()
    {
        Timeline.Mark("6: an edit that cannot be saved, in a window that is open and is then closed");
        var folder = NewScreenProject("Closing, the bar said so");
        if (OpenReady(folder, "closing, the bar said so") is not { } editor)
        {
            return;
        }

        var savedBefore = Describe(_services.Store.Load(editor.Id));
        var errorsBefore = _services.Errors().Length;
        var refusedBefore = _services.Store.RefusedSaves;
        string[] reported = [];
        _services.Store.RefuseSaves(editor.Id, SaveRefusal);
        try
        {
            // The edit, and the save the editor makes by itself 0.6 s later.
            var edited = SetSlider(editor, "StudioPaddingSlider", 0.2);
            var (bar, texts, _) = MessageBar(editor, 5);
            Thread.Sleep(300);
            var whileOpen = _services.Errors().Length - errorsBefore;
            var refusedOpen = _services.Store.RefusedSaves - refusedBefore;
            _report.Check(
                "an edit that cannot be saved while its window is open is said by the window, in its message bar, and is not reported to the app as well",
                edited && bar is not null && texts.Contains(SaveSentence) && ErrorOf(editor) == SaveSentence && whileOpen == 0 && refusedOpen == 1 && Native.Exists(editor.Handle),
                $"{(bar is null ? "no message bar" : bar.ToString())}: {string.Join(" | ", texts.Select(text => $"\"{text}\""))}; the store refused {refusedOpen} save(s); reported to the app meanwhile: {whileOpen}");

            // Closed by its close button. The project was never exported, so the window asks, and is told to keep the draft.
            Timeline.Mark("6: that window is closed");
            var pressed = PressClose(editor);
            var kept = Dialog(editor)?.Find("SecondaryButton")?.Invoke() ?? false;
            var gone = WindowGone(editor);
            Release(editor);
            Until(() => _services.Errors().Length, count => count > errorsBefore, 3);
            Thread.Sleep(300);
            reported = [.. _services.Errors().Skip(errorsBefore)];
            var refusedAtClose = _services.Store.RefusedSaves - refusedBefore - refusedOpen;
            var notOpen = Until(() => !_services.Tracker.IsOpen(editor.Id), ok => ok, 10, 50);
            var savedAfter = Describe(_services.Store.Load(editor.Id));
            _report.Check(
                "closed after that and kept as a draft, the window closes, and the save it tries once more as its last act is reported to the app, once; the project is as it was last saved",
                pressed && kept && gone && reported.Length == 1 && reported[0] == SaveReport && refusedAtClose == 1 && notOpen && savedAfter == savedBefore,
                $"close pressed {pressed}, Keep as draft chosen {kept}, window gone {gone}; the store refused {refusedAtClose} more save(s); reported to the app: {(reported.Length == 0 ? "nothing" : string.Join(" | ", reported.Select(error => $"\"{error}\"")))}; "
                    + $"the project is no longer counted as open: {notOpen}; {(savedAfter == savedBefore ? "the project file is as it was before the edit" : $"the project file holds {savedAfter}, and held {savedBefore}")}");
        }
        finally
        {
            _services.Store.RefuseSaves(null);
            foreach (var error in reported.Where(error => error == SaveReport))
            {
                _services.Forget(error);
            }
        }
    }
}
