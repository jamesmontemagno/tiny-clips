using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Studio;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 3, continued. The camera's background: a choice in the Camera section to keep, blur or remove
// what is behind the people in the camera picture. The window offers it only where people can
// be found in a picture at all, which the app asks once for each editor it opens.
internal sealed partial class WindowChecks
{
    // What the note under the choice says while the background is removed.
    private const string CutoutRemovedText = "With the background removed, the camera has no border and no shadow.";

    // What the choice says of itself to a pointer that rests on it.
    private const string CutoutTip = "Keeps, blurs, or removes what is behind you in the camera picture. " + CutoutRemovedText;

    private static readonly string[] CutoutIds = ["StudioCameraCutout_None", "StudioCameraCutout_Blur", "StudioCameraCutout_Remove"];

    /// <summary>
    /// The Background choice of the Camera section. Whether people can be found is the tool's
    /// to say here: the app's answer is whether a model file is next to it, and none is next
    /// to the tool. Nothing finds people in the tool either way, so a camera's picture is the
    /// same whatever is chosen, and no picture is read: what a blurred or a removed background
    /// looks like is the renderer's, and is checked by StudioRenderCheck.
    /// </summary>
    private void CameraBackground()
    {
        Timeline.Mark("3: the camera's background");
        string[] all = ["StudioCameraCutoutChoice", .. CutoutIds, "StudioCameraCutoutRemovedNote"];

        // As in the app today: people cannot be found, and the choice is not offered.
        if (OpenReady(NewCameraProject("Camera background, without a finder"), "camera background, without a finder") is { } plain)
        {
            var mirror = Find(plain, "StudioCameraMirrorCheckBox");
            var there = all.Where(id => plain.Root.Find(id) is not null).ToArray();
            var can = OnUi(() => plain.Window.ViewModel.CanFindPeople);
            _report.Check(
                "where people cannot be found in a camera picture, as in the app today, the Camera section has no Background choice; Mirror, which the choice would come after, is there",
                mirror is { IsEnabled: true } && there.Length == 0 && !can,
                $"{mirror}; {(there.Length == 0 ? "none of the choice's elements is there" : "there: " + string.Join(", ", there))}; the editor was told that people can be found: {can}");
            CloseQuietly(plain);
        }

        // The tool says that people can be found, for the two windows opened here. The window
        // service asks while it opens a window, so the answer is taken back once both are open.
        Editor? opened;
        _services.PeopleCanBeFound = true;
        try
        {
            // A recording without a camera has no Camera section, and so no Background choice.
            if (OpenReady(NewScreenProject("Camera background, no camera"), "camera background, no camera") is { } bare)
            {
                var there = all.Where(id => bare.Root.Find(id) is not null).ToArray();
                var can = OnUi(() => bare.Window.ViewModel.CanFindPeople);
                _report.Check(
                    "a recording without a camera has no Background choice, although people can be found: it has no Camera section",
                    can && there.Length == 0 && bare.Root.Find("StudioNoCameraNote") is not null,
                    $"the editor was told that people can be found: {can}; {(there.Length == 0 ? "none of the choice's elements is there" : "there: " + string.Join(", ", there))}");
                CloseQuietly(bare);
            }

            opened = OpenReady(NewCameraProject("Camera background"), "camera background");
        }
        finally
        {
            _services.PeopleCanBeFound = false;
        }

        if (opened is not { } editor)
        {
            return;
        }

        int Held() => OnUi(() => editor.Window.ViewModel.CameraCutoutIndex);
        int[] Chosen() => [.. Enumerable.Range(0, CutoutIds.Length).Where(index => editor.Root.Find(CutoutIds[index])?.IsSelected == true)];
        string? Shows(StudioCameraCutout wanted)
        {
            int[] expected = [(int)wanted];
            var chosen = Until(Chosen, now => now.SequenceEqual(expected), 1.5);
            var held = Until(Held, index => index == (int)wanted, 1);
            var note = wanted == StudioCameraCutout.Remove
                ? Until(() => NameOf(editor, "StudioCameraCutoutRemovedNote", 0.3), text => text == CutoutRemovedText, 1) == CutoutRemovedText
                : Absent(editor, "StudioCameraCutoutRemovedNote", 1);
            return chosen.SequenceEqual(expected) && held == (int)wanted && note
                ? null
                : $"wanted {StudioEditorModel.GetCutoutName(wanted)}: chosen in the window: {(chosen.Length == 0 ? "none" : string.Join(", ", chosen.Select(index => StudioEditorModel.GetCutoutName((StudioCameraCutout)index))))}; "
                    + $"the editor holds {StudioEditorModel.GetCutoutName((StudioCameraCutout)held)}; the note about the border and the shadow is {(note ? "as it should be" : "not as it should be")}";
        }

        // The choice as a screen reader is given it, and where it is.
        var group = Find(editor, "StudioCameraCutoutChoice");
        var choices = CutoutIds.Select(id => Find(editor, id, 0.5)).ToArray();
        var tip = OnUi(() => Descendant<RadioButtons>(editor.Window.Content, "StudioCameraCutoutChoice") is { } buttons ? ToolTipService.GetToolTip(buttons) as string : null);
        var kept = Shows(StudioCameraCutout.None);
        var order = TabStops(editor);
        var at = order.IndexOf("StudioCameraCutoutChoice");
        var between = at > 0 && at + 1 < order.Count && order[at - 1] == "StudioCameraMirrorCheckBox" && order[at + 1] == "StudioCameraBorderSlider";
        _report.Check(
            "where people can be found, the Camera section has a choice called Background between Mirror and Border: Keep, Blur and Remove, each a radio button a screen reader reads by that name, one tab stop together; Keep is the chosen one for a new recording; Remove says before it is chosen that the camera then has no border and no shadow, and the choice's tooltip says what each does",
            group is { Name: "Background", IsEnabled: true } && choices.All(choice => choice is { ControlType: ControlTypeNames.RadioButton, IsEnabled: true } && choice.Patterns.Contains("SelectionItem", StringComparison.Ordinal))
                && choices.Select(choice => choice?.Name).SequenceEqual(["Keep", "Blur", "Remove"]) && Enum.GetValues<StudioCameraCutout>().Select(StudioEditorModel.GetCutoutName).SequenceEqual(["Keep", "Blur", "Remove"])
                && kept is null && choices[2]?.HelpText == CutoutRemovedText && choices[0]?.HelpText.Length == 0 && choices[1]?.HelpText.Length == 0 && tip == CutoutTip && between && OnUi(() => editor.Window.ViewModel.CanFindPeople),
            kept ?? $"{group}; choices: {string.Join(", ", choices.Select(choice => choice?.ToString() ?? "missing"))}; Remove is described as \"{choices[2]?.HelpText}\"; the tooltip: \"{tip}\"; "
                + $"the stops around it: {(at > 0 && at + 1 < order.Count ? $"{order[at - 1]}, {order[at]}, {order[at + 1]}" : "the choice is no stop")}");

        // Blur, then Remove: each sets the project, and is one undo step.
        Timeline.Mark("3: Blur and Remove, with undo and redo");
        var wrong = new List<string>();
        var steps = new List<string>();
        void Step(string what, Func<bool> act, StudioCameraCutout wanted)
        {
            var done = act();
            var problem = Shows(wanted);
            steps.Add($"{what}: {StudioEditorModel.GetCutoutName((StudioCameraCutout)Held())}");
            if (!done || problem is not null)
            {
                wrong.Add($"{what}: {(done ? string.Empty : "the control could not be used; ")}{problem}");
            }
        }

        var undoBefore = Find(editor, "StudioUndoButton", 0.5)?.IsEnabled;
        Step("Blur chosen", () => Find(editor, CutoutIds[1])?.Select() ?? false, StudioCameraCutout.Blur);
        Step("Remove chosen", () => Find(editor, CutoutIds[2])?.Select() ?? false, StudioCameraCutout.Remove);
        Step("Undo", () => Invoke(editor, "StudioUndoButton"), StudioCameraCutout.Blur);
        Step("Undo again", () => Invoke(editor, "StudioUndoButton"), StudioCameraCutout.None);
        var undoAfter = Until(() => Find(editor, "StudioUndoButton", 0.5)?.IsEnabled, enabled => enabled == false, 1);
        Step("Redo", () => Invoke(editor, "StudioRedoButton"), StudioCameraCutout.Blur);
        Step("Redo again", () => Invoke(editor, "StudioRedoButton"), StudioCameraCutout.Remove);
        _report.Check(
            "Blur and Remove each set the camera's background in the project, and each is one undo step: Undo goes back to Blur and then to Keep, after which nothing is left to undo, and Redo brings Blur and then Remove again; the choice in the window follows every step, and the note that the camera has no border and no shadow is shown while the background is removed and at no other time",
            wrong.Count == 0 && undoBefore == false && undoAfter == false,
            wrong.Count == 0 ? $"{string.Join("; ", steps)}; Undo was enabled before the first choice: {undoBefore}, and after the two Undo: {undoAfter}" : string.Join(" | ", wrong));

        // The choice belongs to the camera's styling: it goes when the layout hides the camera, comes back with it, is saved with the project, and is part of a saved look.
        Timeline.Mark("3: the camera's background, hidden, saved, and in a saved look");
        Find(editor, "StudioLayoutScreen")?.Select();
        var hidden = Until(() => all.Where(id => editor.Root.Find(id) is not null).ToArray(), there => there.Length == 0, 2);
        Find(editor, "StudioLayoutBubble")?.Select();
        var back = Shows(StudioCameraCutout.Remove);
        var saved = Until(() => _services.Store.Load(editor.Id).Camera.Cutout, cutout => cutout == StudioCameraCutout.Remove, 3, 100);
        var lookBefore = _services.Settings.StudioDefaultLook;
        var pressed = Invoke(editor, "StudioSaveDefaultLookButton");
        var look = Until(() => _services.Settings.StudioDefaultLook, now => now?.Camera.Cutout == StudioCameraCutout.Remove, 1.5);
        _services.Settings.StudioDefaultLook = lookBefore;
        _report.Check(
            "the Background choice is hidden with the camera's other styling in the layout that shows the screen only, and is back, as it was left, in a layout with the camera; the editor saves it with the project, and Save as default look takes it into the look",
            hidden.Length == 0 && back is null && saved == StudioCameraCutout.Remove && pressed && look?.Camera.Cutout == StudioCameraCutout.Remove,
            back ?? $"in the screen-only layout: {(hidden.Length == 0 ? "the choice is gone" : "still there: " + string.Join(", ", hidden))}; the project file holds {saved}; the saved look holds {look?.Camera.Cutout.ToString() ?? "no look"}");
        CloseQuietly(editor);
    }
}
