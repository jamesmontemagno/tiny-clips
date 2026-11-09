using System.Diagnostics;
using TinyClips.App.Models.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 6. Closing: the question a project without a saved project folder asks, what each answer
// does, a project that was exported without being saved as a folder, a project saved as a
// folder with and without changes, and an export that ends behind the question about it: the
// close button then, and where the keyboard focus is. What Esc does is in ClosingByEscape.cs, and a
// window that closes while its edits cannot be saved in ClosingUnsaved.cs.
internal sealed partial class WindowChecks
{
    // The window asks one question at a time, and passes over its close button until the last
    // question has finished closing. A person is not faster than this either.
    private const int QuestionSettles = 700;

    private void Closing()
    {
        DraftQuestion();
        ExportedAndSavedProjects();
        CloseButtonAfterTheExportEndedBehindItsQuestion();
        ClosingByEscape();
        ClosingWhenTheSaveFails();
    }

    /// <summary>Cancel, Keep as draft, the draft opened again, and Delete.</summary>
    private void DraftQuestion()
    {
        Timeline.Mark("6: a project without a saved project folder");
        var folder = NewCameraProject("Closing, a draft");
        if (OpenReady(folder, "closing, a draft") is not { } editor)
        {
            return;
        }

        // Edits to find again when the draft is opened later.
        var lemon = StudioSwatch.Find("lemon");
        var edited = (Find(editor, "StudioSwatch_lemon")?.Select() ?? false)
            & SetSlider(editor, "StudioPaddingSlider", 0.12)
            & (Find(editor, "StudioLayoutSideBySide")?.Select() ?? false)
            & SetSlider(editor, "StudioTrimStart", 1.0);
        Expect(editor, p => WithScene(
            p with
            {
                Canvas = p.Canvas with { Padding = 0.12, Background = new StudioBackground { Style = StudioBackgroundStyle.Solid, Preset = "lemon", Primary = lemon?.PrimaryHex ?? string.Empty, Secondary = null } },
                Edits = p.Edits with { TrimStart = 1.0 },
            },
            s => s with { Layout = StudioLayout.SideBySide }));
        const int Start = 30;
        var shown = LookFor(editor, s => s.Shown == Both(editor, Start), 5);

        // The close button, while the video plays.
        Invoke(editor, "StudioPlayPauseButton");
        Until(() => NameOf(editor, "StudioPlayPauseButton", 0), name => name == "Pause", 2);
        var deletesBefore = _services.Store.Deletes;
        var pressed = PressClose(editor);
        var question = Dialog(editor);
        var keep = question?.Find("PrimaryButton");
        var second = question?.Find("SecondaryButton");
        var cancel = question?.Find("CloseButton");
        var delete = question?.Find("StudioClosePromptDeleteButton");
        var words = question?.FindAll(ControlTypeNames.Text).Select(text => text.Name).FirstOrDefault(text => text.StartsWith("It has not been saved as a project", StringComparison.Ordinal)) ?? string.Empty;
        var paused = Until(() => NameOf(editor, "StudioPlayPauseButton", 0), name => name == "Play", 2) == "Play";
        var focus = Until(() => FocusedId(editor), id => id == "PrimaryButton", 1.5);
        _report.Check(
            "the close button on a project that has no saved project folder asks first, with three answers: Keep as draft, Cancel and Delete; the window stays, and the video stops playing",
            edited && shown?.Shown == Both(editor, Start) && pressed && question is { Name: DraftQuestionTitle }
                && keep is { Name: "Keep as draft", IsEnabled: true } && second is not { Name.Length: > 0 } && cancel is { Name: "Cancel" } && delete is { Name: "Delete recording", ControlType: ControlTypeNames.Button }
                && words == DraftQuestionWords && paused && Native.Exists(editor.Handle),
            $"question \"{question?.Name}\": \"{words}\"; answers {keep}, {second?.ToString() ?? "no second button"}, {cancel}, {delete}; playing stopped: {paused}");
        _report.Check(
            "when the question opens, the keyboard focus is on Keep as draft, its default answer, and not on Delete",
            focus == "PrimaryButton",
            $"focus is on \"{focus}\"");
        if (question is null || cancel is null)
        {
            CloseQuietly(editor);
            return;
        }

        // Cancel.
        Timeline.Mark("6: Cancel");
        var cancelled = cancel.Invoke();
        var questionGone = Until(() => !HasDialog(editor), gone => gone, 3);
        var restedOn = FrameOf(Playhead(editor));
        var stepped = Invoke(editor, "StudioNextFrameButton");
        var after = LookFor(editor, s => s.Shown == Both(editor, restedOn + 1), 3);
        _report.Check(
            "Cancel: the question goes, the window stays open with its project, and the editor goes on working: Next frame steps one frame on",
            cancelled && questionGone && Native.Exists(editor.Handle) && stepped && after?.Shown == Both(editor, restedOn + 1) && Directory.Exists(folder.Paths.ProjectDirectory) && _services.Store.Deletes == deletesBefore,
            $"question gone {questionGone}; window open {Native.Exists(editor.Handle)}; the video had stopped on frame {restedOn}, and the picture now shows {after?.Shown}; project folder there: {Directory.Exists(folder.Paths.ProjectDirectory)}");
        Thread.Sleep(QuestionSettles);

        // Keep as draft.
        Timeline.Mark("6: Keep as draft");
        var keptPressed = PressClose(editor);
        var kept = Dialog(editor)?.Find("PrimaryButton")?.Invoke() ?? false;
        var gone = WindowGone(editor);
        Release(editor);
        var wanted = Describe(editor.Expected);
        var saved = Until(() => Describe(_services.Store.Load(editor.Id)), text => text == wanted, 3, 50);
        var closed = Until(() => !_services.Tracker.IsOpen(editor.Id), ok => ok, 10, 50);
        _report.Check(
            "Keep as draft: the window closes and the project stays, with its edits in the project file and without an export",
            keptPressed && kept && gone && saved == wanted && closed && File.Exists(folder.Paths.ScreenPath) && _services.Store.Load(editor.Id).Exports.Length == 0 && _services.Store.Deletes == deletesBefore,
            saved == wanted ? $"window gone {gone}; saved: {saved}" : $"window gone {gone}; saved: {saved} | expected: {wanted}");

        // The draft, opened again.
        Timeline.Mark("6: the draft opened again");
        if (OpenReady(folder, "closing, the draft again") is not { } again)
        {
            return;
        }

        again.Expected = editor.Expected;
        var sight = LookFor(again, s => s.Shown == Both(again, Start) && Corners(s).All(c => Near(c, Lemon)), 5);
        string?[] controls =
        [
            Selected(again, "StudioLayoutSideBySide"),
            Selected(again, "StudioSwatch_lemon"),
            Slider(again, "StudioPaddingSlider", 0.12, "12%"),
            Math.Abs(SliderValue(again, "StudioTrimStart") - 1.0) < 1e-6 ? null : $"Start is at {F(SliderValue(again, "StudioTrimStart"))} s",
            Find(again, "StudioPreview", 0.5)?.HelpText == "Side by side" ? null : $"the preview is described as \"{Find(again, "StudioPreview", 0)?.HelpText}\"",
        ];
        var differing = string.Join("; ", controls.Where(control => control is not null));
        _report.Check(
            "the draft opened again shows the saved edits: the layout, the background and the padding in the picture and in the inspector, and the trim on the trim bar, with the video at its new start",
            sight is not null && sight.Shown == Both(again, Start) && Corners(sight).All(c => Near(c, Lemon)) && differing.Length == 0,
            $"{sight?.Shown} (expected {Both(again, Start)}); corners {(sight is null ? "?" : CornerText(sight))}{(differing.Length == 0 ? string.Empty : "; " + differing)}");

        // Delete.
        Timeline.Mark("6: Delete");
        var deletePressed = PressClose(again);
        var deleteButton = Dialog(again)?.Find("StudioClosePromptDeleteButton");
        var watch = Stopwatch.StartNew();
        var deleted = deleteButton?.Invoke() ?? false;
        var folderGone = Until(() => !Directory.Exists(folder.Paths.ProjectDirectory), isGone => isGone, 10, 5);
        var took = watch.Elapsed.TotalMilliseconds;
        var windowGone = WindowGone(again);
        Release(again);
        var notOpen = Until(() => !_services.Tracker.IsOpen(again.Id), ok => ok, 5, 50);
        _report.Check(
            "Delete: the window closes and the project folder is gone at once, deleted at the first attempt",
            deletePressed && deleted && folderGone && took < 1500 && windowGone && notOpen && !_services.Store.Exists(again.Id)
                && _services.Store.Deletes == deletesBefore + 1 && _services.Store.FailedDeletes == 0 && _services.Errors().Length == 0,
            $"the folder was gone {F(took, "0")} ms after the answer: {folderGone}; window gone {windowGone}; delete attempts {_services.Store.Deletes - deletesBefore}, failed {_services.Store.FailedDeletes}{(_services.Errors() is { Length: > 0 } errors ? "; reported: " + errors[0] : string.Empty)}");
    }

    /// <summary>
    /// What closing asks is decided by the saved project folder, not by an export: a project
    /// that was exported but has no folder asks the draft question; one saved as a folder closes
    /// without a question while it has no changes, and with a change asks whether to save it.
    /// Cancel, Don't save and Save.
    /// </summary>
    private void ExportedAndSavedProjects()
    {
        // Exported, without a saved project folder: the draft question, and Keep as draft.
        Timeline.Mark("6: a project that was exported, without a saved project folder");
        var exported = NewExportedProject("Closing, exported");
        if (OpenReady(exported, "closing, exported") is { } editor)
        {
            var pressed = PressClose(editor);
            var question = Dialog(editor);
            var isDraft = IsDraftQuestion(question);
            var kept = isDraft && (question!.Find("PrimaryButton")?.Invoke() ?? false);
            var gone = WindowGone(editor);
            var asked = QuestionAsked(question);
            CloseQuietly(editor);
            _report.Check(
                "a project that was exported, but never saved as a project folder, asks the same question as any draft when it is closed: Keep as draft, Cancel and Delete, with no Export among them; Keep as draft closes the window, and the project stays with its export listed",
                pressed && isDraft && kept && gone && File.Exists(exported.Paths.ScreenPath) && _services.Store.Load(editor.Id).Exports.Length == 1,
                $"close pressed {pressed}; {asked}; Keep as draft pressed {kept}; window gone {gone}; exports listed {_services.Store.Load(editor.Id).Exports.Length}");
        }

        // Saved as a folder, with no change since it was opened: closes without a question.
        Timeline.Mark("6: a project saved as a folder, without changes");
        const string SavedName = "Closing, saved";
        var (saved, projectFile) = NewSavedProject(SavedName);
        var target = Path.GetDirectoryName(projectFile)!;
        var folderBefore = Fingerprint(target);
        if (OpenReady(saved, "closing, saved") is not { } quiet)
        {
            return;
        }

        var closePressed = PressClose(quiet);
        var closedAtOnce = WindowGone(quiet, 5);
        var questionAsked = !closedAtOnce && HasDialog(quiet);
        CloseQuietly(quiet);
        _report.Check(
            "a project saved as a project folder, with no change since it was opened, closes without a question, and stays; its folder is as it was",
            closePressed && closedAtOnce && !questionAsked && File.Exists(saved.Paths.ScreenPath) && Fingerprint(target).SequenceEqual(folderBefore),
            $"window gone {closedAtOnce}{(questionAsked ? ", a question was asked" : string.Empty)}; the project is there: {File.Exists(saved.Paths.ScreenPath)}; the saved folder is {(Fingerprint(target).SequenceEqual(folderBefore) ? "as it was" : "changed")}");

        // A change: Save changes, then Cancel and Don't save.
        Timeline.Mark("6: a saved project with a change, Cancel and Don't save");
        if (OpenReady(saved, "closing, saved, Don't save") is not { } changed)
        {
            return;
        }

        var paddingBefore = _services.Store.Load(changed.Id).Canvas.Padding;
        var edited = SetSlider(changed, "StudioPaddingSlider", 0.12);
        var asks = PressClose(changed);
        var unsaved = Dialog(changed);
        var focus = unsaved is null ? string.Empty : Until(() => FocusedId(changed), id => id == "PrimaryButton", 1.5);
        _report.Check(
            "a project saved as a project folder that has changed since asks first, with its name, whether to save the changes: Save, Don't save and Cancel, with the keyboard focus on Save, and no Delete; the window stays",
            edited && asks && IsUnsavedChangesQuestion(unsaved, SavedName) && focus == "PrimaryButton" && Native.Exists(changed.Handle),
            $"close pressed {asks}; {QuestionAsked(unsaved)}; focus on \"{focus}\"");
        if (unsaved is null)
        {
            CloseQuietly(changed);
            return;
        }

        Dismiss(changed, unsaved);
        var staysAfterCancel = Native.Exists(changed.Handle) && Same(SliderValue(changed, "StudioPaddingSlider"), 0.12);
        var revealedBefore = _services.Revealed.Count;
        var askedAgain = PressClose(changed);
        var dontSave = Dialog(changed)?.Find("SecondaryButton")?.Invoke() ?? false;
        var discardedGone = WindowGone(changed);
        CloseQuietly(changed);
        var restored = Until(() => _services.Store.Load(changed.Id).Canvas.Padding, padding => Same(padding, paddingBefore), 3, 50);
        _report.Check(
            "Cancel keeps the window and the change; Don't save closes the window and puts the project back as it was when it was opened, and the saved folder is not written",
            staysAfterCancel && askedAgain && dontSave && discardedGone && Same(restored, paddingBefore) && Fingerprint(target).SequenceEqual(folderBefore) && _services.Revealed.Count == revealedBefore,
            $"after Cancel the window was open with the change: {staysAfterCancel}; Don't save pressed {dontSave}; window gone {discardedGone}; the padding in the project is {F(restored)} (it was {F(paddingBefore)}); the saved folder is {(Fingerprint(target).SequenceEqual(folderBefore) ? "as it was" : "changed")}");

        // A change: Save.
        Timeline.Mark("6: a saved project with a change, and Save");
        if (OpenReady(saved, "closing, saved, Save") is not { } toSave)
        {
            return;
        }

        var editedAgain = SetSlider(toSave, "StudioPaddingSlider", 0.2);
        revealedBefore = _services.Revealed.Count;
        var savesBefore = _services.Store.ProjectSavesBegun;
        var savePressed = PressClose(toSave);
        var saveQuestion = Dialog(toSave);
        var isSaveQuestion = IsUnsavedChangesQuestion(saveQuestion, SavedName);
        var chose = saveQuestion?.Find("PrimaryButton")?.Invoke() ?? false;
        var savedGone = WindowGone(toSave, 15);
        CloseQuietly(toSave);
        var inFolder = Until(() => PaddingOf(projectFile), padding => Same(padding, 0.2), 3, 50);
        var inStore = _services.Store.Load(toSave.Id).Canvas.Padding;
        _report.Check(
            "Save writes the change into the saved project folder, without showing the folder, and then the window closes; the project keeps the change",
            editedAgain && savePressed && isSaveQuestion && chose && savedGone && Same(inFolder, 0.2) && Same(inStore, 0.2)
                && _services.Store.ProjectSavesBegun == savesBefore + 1 && _services.Revealed.Count == revealedBefore && _services.Errors().Length == 0,
            $"{QuestionAsked(saveQuestion)}; Save pressed {chose}; window gone {savedGone}; the padding in the saved folder is {F(inFolder)} and in the project {F(inStore)}; saves begun {_services.Store.ProjectSavesBegun - savesBefore}; shown in its folder: {_services.Revealed.Count - revealedBefore}{(_services.Errors() is { Length: > 0 } errors ? "; reported: " + errors[0] : string.Empty)}");
    }

    /// <summary>
    /// No window closes from under a question, and nothing under a question is given the
    /// keyboard. What closing asks can change while its question is open in one place: the
    /// question about a running export, with the export ending behind it. The end of an export
    /// asks for the keyboard focus to go back to Export: that waits until the question has been
    /// answered, because the focus on Export would take Esc away from the question, and Enter
    /// or Space would start a second export under it. And the close button, pressed again then,
    /// used to close the window from under that question: it waits for the answer now, like
    /// every other press of the close button while a question is open.
    /// </summary>
    private void CloseButtonAfterTheExportEndedBehindItsQuestion()
    {
        Timeline.Mark("6: the close button after an export has ended behind the question about it");
        const string ExportQuestion = "An export is still running.";
        var folder = NewScreenProject("Closing, the export ended", p => p with { Edits = new StudioEdits { TrimStart = 2.0, TrimEnd = 5.0 } });
        if (OpenReady(folder, "closing, the export ended") is not { } editor)
        {
            return;
        }

        // The question about the export, asked while it runs.
        Invoke(editor, "StudioExportButton");
        var overlay = Find(editor, "StudioCancelExportButton", 3) is not null;
        var pressed = PressClose(editor);
        var question = Dialog(editor);
        if (question is not { Name: ExportQuestion })
        {
            _report.Check(
                "the close button, pressed again once the export has ended behind the question about it, waits for the answer",
                false,
                $"the question about a running export could not be asked first: an export started {overlay}, close pressed {pressed}, and what was asked is \"{question?.Name}\"; window open {Native.Exists(editor.Handle)}");
            CloseQuietly(editor);
            return;
        }

        // The question has the keyboard, and keeps it. The start of an export asks for the
        // focus to go to Cancel, and its end for the focus to go back to Export; the window
        // does either after everything else it has to do, which here is after the question has
        // opened. Both have to wait for the answer.
        var (givenFocus, stopWatching) = WatchFocusUnderAQuestion(editor);
        bool ended;
        StudioExportedEventArgs? finished;
        string focusAtFirst;
        string focus;
        try
        {
            WhenTheUiThreadHasNothingWaiting();
            focusAtFirst = Until(() => FocusedId(editor), IsOnTheQuestion, 1.5);

            // The export ends behind the question. From here on closing asks the draft question:
            // the project has been exported, but it has no saved project folder.
            ended = Until(() => !IsExporting(editor), done => done, 120, 50);
            finished = Until(() => _services.Exports.FirstOrDefault(e => e.ProjectId == editor.Id), e => e is not null, 5, 30);
            Thread.Sleep(300);
            WhenTheUiThreadHasNothingWaiting();
            focus = FocusedId(editor);
        }
        finally
        {
            stopWatching();
        }

        var under = givenFocus.Given().Where(id => !IsOnTheQuestion(id)).ToArray();
        _report.Check(
            "while the question about a running export is open, the keyboard stays with it: the focus is on one of the question's own answers once the export has started, and still when the export has ended behind the question, and nothing under the question was given it, although the start of an export puts the focus on Cancel and its end on Export",
            ended && IsOnTheQuestion(focusAtFirst) && IsOnTheQuestion(focus) && under.Length == 0,
            $"with the question open and the window having done what the start of the export had it waiting to do, the focus was on \"{focusAtFirst}\"; once the export had ended behind the question ({ended}) and the window had again done what it had waiting, on \"{focus}\"; given the focus under the question in that time: {(under.Length == 0 ? "nothing" : string.Join(", ", under))}");

        var pressedAgain = PressClose(editor);
        Thread.Sleep(300);
        var questions = Questions(editor);
        var stays = Native.Exists(editor.Handle);
        _report.Check(
            "the close button, pressed again once the export has ended behind the question about it, waits for the answer like every other press while a question is open: the window and the one question stay",
            overlay && pressed && ended && finished is not null && pressedAgain && stays && questions.Count == 1 && questions[0].Name == ExportQuestion,
            $"the export ended behind the question: {ended}, with {(finished is null ? "no video" : Path.GetFileName(finished.Path))}; the close button was pressed again: {pressedAgain}; window open {stays}; {questions.Count} question(s){(questions.Count > 0 ? $", \"{questions[0].Name}\"" : string.Empty)}");
        if (!stays)
        {
            Release(editor);
            return;
        }

        // Keep exporting is the answer that leaves the window. The close button then asks the draft question.
        var kept = Dialog(editor, 1)?.Find("CloseButton")?.Invoke() ?? false;
        var questionGone = Until(() => !HasDialog(editor), gone => gone, 3);
        var staysAfter = Native.Exists(editor.Handle);

        // The focus that the end of the export asked for has waited for this.
        var focusAfterAnswer = staysAfter ? Until(() => FocusedId(editor), id => id == "StudioExportButton", 2) : string.Empty;
        _report.Check(
            "the keyboard focus that the end of the export asked for has waited for the answer: once the question is gone, it is on Export",
            kept && questionGone && staysAfter && focusAfterAnswer == "StudioExportButton",
            $"Keep exporting pressed {kept}; question gone {questionGone}; window open {staysAfter}; the focus is on \"{focusAfterAnswer}\"");
        Thread.Sleep(QuestionSettles);
        var closePressed = staysAfter && PressClose(editor);
        var draftQuestion = closePressed ? Dialog(editor) : null;
        var isDraft = IsDraftQuestion(draftQuestion);
        var keptAsDraft = isDraft && (draftQuestion!.Find("PrimaryButton")?.Invoke() ?? false);
        var goneAtLast = WindowGone(editor, 5);
        if (goneAtLast)
        {
            Release(editor);
        }
        else
        {
            CloseQuietly(editor);
        }

        var listed = _services.Store.Load(editor.Id).Exports.Length;
        _report.Check(
            "answered with Keep exporting, the question goes and the window stays; the close button then asks the draft question, because the recording has been exported but not saved as a project folder, and Keep as draft closes the window",
            kept && questionGone && staysAfter && closePressed && isDraft && keptAsDraft && goneAtLast && listed == 1 && File.Exists(folder.Paths.ScreenPath),
            $"Keep exporting pressed {kept}; question gone {questionGone}; window open after it {staysAfter}; the close button pressed {closePressed}, and it asked: {QuestionAsked(draftQuestion)}; Keep as draft pressed {keptAsDraft}; the window gone {goneAtLast}; exports listed {listed}");
    }
}
