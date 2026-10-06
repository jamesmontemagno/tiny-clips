using System.Diagnostics;
using TinyClips.App.Models.Studio;
using TinyClips.Core.Studio;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 6. Closing: the question a project that was never exported asks, what each answer does, and a
// project that was exported. A window that closes while its edits cannot be saved is in
// ClosingUnsaved.cs.
internal sealed partial class WindowChecks
{
    // The window asks one question at a time, and passes over its close button until the last
    // question has finished closing. A person is not faster than this either.
    private const int QuestionSettles = 700;

    private void Closing()
    {
        DraftQuestion();
        ExportAnswer();
        ClosingWhenTheSaveFails();
    }

    /// <summary>Cancel, Keep as draft, the draft opened again, and Delete.</summary>
    private void DraftQuestion()
    {
        Timeline.Mark("6: a project that was never exported");
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
        var export = question?.Find("PrimaryButton");
        var keep = question?.Find("SecondaryButton");
        var cancel = question?.Find("CloseButton");
        var delete = question?.Find("StudioClosePromptDeleteButton");
        var words = question?.FindAll(ControlTypeNames.Text).Select(text => text.Name).FirstOrDefault(text => text.StartsWith("It has not been exported", StringComparison.Ordinal)) ?? string.Empty;
        var paused = Until(() => NameOf(editor, "StudioPlayPauseButton", 0), name => name == "Play", 2) == "Play";
        var focus = Until(() => FocusedId(editor), id => id == "PrimaryButton", 1.5);
        _report.Check(
            "the close button on a project that was never exported asks first, with four answers: Export, Keep as draft, Cancel and Delete; the window stays, and the video stops playing",
            edited && shown?.Shown == Both(editor, Start) && pressed && question is { Name: "Export this recording before closing?" }
                && export is { Name: "Export", IsEnabled: true } && keep is { Name: "Keep as draft" } && cancel is { Name: "Cancel" } && delete is { Name: "Delete recording", ControlType: ControlTypeNames.Button }
                && words.Length > 0 && paused && Native.Exists(editor.Handle),
            $"question \"{question?.Name}\": \"{words}\"; answers {export}, {keep}, {cancel}, {delete}; playing stopped: {paused}");
        _report.Check(
            "when the question opens, the keyboard focus is on Export, its default answer, and not on Delete",
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
        var kept = Dialog(editor)?.Find("SecondaryButton")?.Invoke() ?? false;
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

    /// <summary>Export as the answer: cancelled, the window stays; finished, it closes by itself. And the exported project, opened again.</summary>
    private void ExportAnswer()
    {
        Timeline.Mark("6: the answer Export");
        const int First = 60;
        const int Last = 104;
        var folder = NewScreenProject("Closing, export", p => p with { Edits = new StudioEdits { TrimStart = 2.0, TrimEnd = 3.5 } });
        if (OpenReady(folder, "closing, export") is not { } editor)
        {
            return;
        }

        LookFor(editor, s => s.Shown == Both(editor, First), 5);
        var filesBefore = ExportFiles();

        // Export, stopped again: the window stays.
        var asked = PressClose(editor);
        var chosen = Dialog(editor)?.Find("PrimaryButton")?.Invoke() ?? false;
        var overlay = Find(editor, "StudioCancelExportButton", 3);
        var stopped = overlay?.Invoke() ?? false;
        var idle = Gone(editor, "StudioCancelExportButton", 15);
        Thread.Sleep(300);
        var files = Until(() => ExportFiles().Except(filesBefore).ToArray(), left => left.Length == 0, 3, 50);
        _report.Check(
            "Export in the question starts the export, and when that export is cancelled the window stays open, with nothing written",
            asked && chosen && overlay is not null && stopped && idle && Native.Exists(editor.Handle) && !HasDialog(editor) && files.Length == 0 && _services.Store.Load(editor.Id).Exports.Length == 0,
            $"export started {overlay is not null}, cancelled {stopped}; window open {Native.Exists(editor.Handle)}; files left: {(files.Length == 0 ? "none" : string.Join(", ", files.Select(Path.GetFileName)))}");
        Thread.Sleep(QuestionSettles);

        // Export, to the end: the window closes by itself.
        Timeline.Mark("6: Export, to the end");
        var askedAgain = PressClose(editor);
        var watch = Stopwatch.StartNew();
        var chosenAgain = Dialog(editor)?.Find("PrimaryButton")?.Invoke() ?? false;
        var gone = WindowGone(editor, 60);
        var took = watch.Elapsed.TotalSeconds;
        Release(editor);
        var finished = Until(() => _services.Exports.FirstOrDefault(e => e.ProjectId == editor.Id), e => e is not null, 5, 30);
        var project = _services.Store.Load(editor.Id);
        var frames = new List<Shown>();
        var decodeFailure = string.Empty;
        if (finished is not null)
        {
            try
            {
                var size = StudioExportLimits.GetExportSize(editor.Expected);
                var (width, height) = ((int)size.Width, (int)size.Height);
                var view = SceneView.Resolve(editor.Expected, editor.Folder.Screen, editor.Folder.Camera, width, height);
                foreach (var frame in DecodedFrames(finished.Path, width, height, "format=bgra"))
                {
                    frames.Add(Shown.Read(frame, width, height, view, editor.Folder.Screen, editor.Folder.Camera));
                }
            }
            catch (Exception ex)
            {
                decodeFailure = ex.Message;
            }
        }

        var range = Enumerable.Range(First, Last - First + 1).Select(frame => Both(editor, frame)).ToArray();
        _report.Check(
            "Export in the question, left to finish: the video of the trimmed range is written, the project lists it, and then the window closes by itself",
            askedAgain && chosenAgain && gone && finished is not null && File.Exists(finished.Path) && project.Exports.Length == 1 && frames.SequenceEqual(range) && File.Exists(folder.Paths.ScreenPath),
            $"the window was gone {F(took, "0.0")} s after the answer: {gone}; video: {(finished is null ? "none" : Path.GetFileName(finished.Path))}, {frames.Count} frames{(frames.Count > 0 ? $", screen {frames[0].Screen} to {frames[^1].Screen}" : string.Empty)} (expected {range.Length}, {First} to {Last}); exports listed {project.Exports.Length}{(decodeFailure.Length > 0 ? "; " + decodeFailure : string.Empty)}");

        // Exported: opened again, it closes without a question.
        Timeline.Mark("6: a project that was exported");
        if (OpenReady(folder, "closing, exported") is not { } again)
        {
            return;
        }

        LookFor(again, s => s.Shown == Both(again, First), 5);
        var closePressed = PressClose(again);
        var closedAtOnce = WindowGone(again, 5);
        var question = !closedAtOnce && HasDialog(again);
        CloseQuietly(again);

        _report.Check(
            "a project that was exported closes without a question, and stays",
            closePressed && closedAtOnce && !question && File.Exists(folder.Paths.ScreenPath) && _services.Store.Load(again.Id).Exports.Length == 1,
            $"window gone {closedAtOnce}{(question ? ", a question was asked" : string.Empty)}; the project is there: {File.Exists(folder.Paths.ScreenPath)}");
    }
}
