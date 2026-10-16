using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TinyClips.App.Controls.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 3, continued. The Volume slider of the Audio panel. The recordings of this tool have no
// sound, and its previews are opened with every player muted and at volume zero whatever the
// project says (ToolServices), which the second check here reads back from the players. So
// what is checked is everything but the sound: the slider as a screen reader is given it,
// audio.volume in the project file, that the preview's engine was told, Undo and Redo, and Mute.
internal sealed partial class WindowChecks
{
    private const string VolumeSliderId = "StudioVolumeSlider";

    /// <summary>The project is not muted when this starts, and not when it ends. It leaves the volume at 35%.</summary>
    private void VolumeSlider(Editor editor)
    {
        Timeline.Mark("3: the volume");
        const string Mute = "StudioMuteCheckBox";

        // What it is.
        var mute = Find(editor, Mute);
        var slider = Find(editor, VolumeSliderId);
        var (tip, title, value) = OnUi(() => RowOf(editor) is { } row
            ? (ToolTipService.GetToolTip(row) as string, (row.FindName("TitleLabel") as TextBlock)?.Text, (row.FindName("ValueLabel") as TextBlock)?.Text)
            : default);
        var inkOn = OnUi(() => InkOf(editor));
        var stops = TabStopsAsItIs(editor);
        var inFile = _services.Store.Load(editor.Id).Audio.Volume;
        var range = slider?.Range;
        _report.Check(
            "the Audio panel has a slider called Volume under Mute: it is at 100% for a project that says nothing else, goes from 0 to 1 in steps of 0.05, which is what an arrow key moves it by, says its value as a percent, to a screen reader and beside its name, is the stop of the Tab key after Mute, and its tooltip says what it sets",
            slider is { ControlType: ControlTypeNames.Slider, Name: "Volume", IsEnabled: true, IsKeyboardFocusable: true, ValueText: "100%" }
                && range is { IsReadOnly: false } && Same(range.Value.Value, 1) && Same(range.Value.Minimum, 0) && Same(range.Value.Maximum, 1) && Same(range.Value.SmallChange, 0.05) && Same(range.Value.LargeChange, 0.5)
                && title == "Volume" && value == "100%" && tip == "How loud the video's sound is"
                && mute is { IsToggledOn: false } && mute.Bounds.Y + mute.Bounds.Height <= slider.Bounds.Y + 2
                && stops.IndexOf(Mute) is >= 0 and var muteStop && stops.IndexOf(VolumeSliderId) == muteStop + 1
                && Same(inFile, 1),
            $"{slider}, enabled {slider?.IsEnabled}, can take the focus {slider?.IsKeyboardFocusable}, says \"{slider?.ValueText}\"; range {Words(range)}; "
                + $"on show: \"{title}\" and \"{value}\"; the tooltip: \"{tip}\"; Mute is {OnOff(mute?.IsToggledOn)}, its bottom edge at {mute?.Bounds.Y + mute?.Bounds.Height} and the slider's top at {slider?.Bounds.Y}; "
                + $"the stops with the Audio panel on show: {string.Join(", ", stops.Select(id => id.Replace("Studio", string.Empty, StringComparison.Ordinal)))}; the file has volume {F(inFile)}");

        // Set as a screen reader sets it.
        var set = slider?.SetRange(0.35) ?? false;
        var shown = Slider(editor, VolumeSliderId, 0.35, "35%");
        var saved = Until(() => _services.Store.Load(editor.Id).Audio.Volume, volume => Same(volume, 0.35), 3, 50);
        var written = Regex.Matches(File.ReadAllText(_services.Store.GetPaths(editor.Id).ProjectJsonPath), "\"volume\":\\s*([0-9.eE+-]+)").Select(match => match.Groups[1].Value).ToArray();
        var engine = EngineOf(editor);
        var told = Until(() => engine?.GetDiagnostics(), state => state is not null && Same(state.ProjectVolume, 0.35), 2);
        var silent = told is not null && told.PlayerVolume.All(volume => volume == 0) && told.PlayerMuted.All(muted => muted);
        var undoPressed = Invoke(editor, "StudioUndoButton");
        var undone = Slider(editor, VolumeSliderId, 1, "100%");
        var savedUndone = Until(() => _services.Store.Load(editor.Id).Audio.Volume, volume => Same(volume, 1), 3, 50);
        var toldUndone = Until(() => engine?.GetDiagnostics().ProjectVolume ?? double.NaN, volume => Same(volume, 1), 2);
        var redoPressed = Invoke(editor, "StudioRedoButton");
        var redone = Slider(editor, VolumeSliderId, 0.35, "35%");
        var savedRedone = Until(() => _services.Store.Load(editor.Id).Audio.Volume, volume => Same(volume, 0.35), 3, 50);
        Expect(editor, p => p with { Audio = p.Audio with { Volume = 0.35 } });
        _report.Check(
            "set to 0.35 through UI Automation, the slider says 35%, the project file has audio.volume 0.35, written once, and the preview's engine was told 0.35, while its players stay muted and at volume zero as this tool opens them; Undo gives 100% back, in the slider, the file and the engine, and Redo 35%",
            set && shown is null && Same(saved, 0.35) && written is ["0.35"]
                && told is not null && Same(told.ProjectVolume, 0.35) && silent
                && undoPressed && undone is null && Same(savedUndone, 1) && Same(toldUndone, 1)
                && redoPressed && redone is null && Same(savedRedone, 0.35),
            $"set {set}{(shown is null ? string.Empty : $", but {shown}")}; the file then had {F(saved)}, written as {(written.Length == 0 ? "nothing" : string.Join(" and ", written))}; "
                + $"the engine {(told is null ? "was not there" : $"was told {F(told.ProjectVolume)}, its players' volume {string.Join("/", told.PlayerVolume.Select(volume => F(volume)))}, muted {string.Join("/", told.PlayerMuted)}")}; "
                + $"after Undo {(undone is null ? "100%" : undone)}, the file {F(savedUndone)}, the engine {F(toldUndone)}; after Redo {(redone is null ? "35%" : redone)}, the file {F(savedRedone)}");

        // Mute switches it off and leaves its value.
        var toggled = Find(editor, Mute)?.Toggle() ?? false;
        var off = Until(() => Find(editor, VolumeSliderId, 0.5), found => found is { IsEnabled: false }, 2);
        // Read now: the element is the live slider, and Mute is switched off again further down.
        var (offEnabled, offFocusable, offText) = (off?.IsEnabled, off?.IsKeyboardFocusable, off?.ValueText);
        var offRange = off?.Range;
        var refused = !(off?.SetRange(0.6) ?? false);
        var mutedInFile = Until(() => _services.Store.Load(editor.Id).Audio, audio => audio.Muted, 3, 50);
        var stopsMuted = TabStopsAsItIs(editor);
        var rowOff = OnUi(() => RowOf(editor) is { } row ? (row.IsEnabled, (row.FindName("ValueLabel") as TextBlock)?.Text) : default);
        var inkOff = OnUi(() => InkOf(editor));
        var partsOff = OnUi(() => Descendant<StudioSlider>(editor.Window.Content, VolumeSliderId) is { } part
            ? $"the slider itself enabled {part.IsEnabled}, a tab stop {part.IsTabStop}, its peer enabled {Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(part)?.IsEnabled()}"
            : "the slider itself was not found");
        var untoggled = Find(editor, Mute)?.Toggle() ?? false;
        var on = Until(() => Find(editor, VolumeSliderId, 0.5), found => found is { IsEnabled: true }, 2);
        var unmutedInFile = Until(() => _services.Store.Load(editor.Id).Audio, audio => !audio.Muted, 3, 50);
        _report.Check(
            "with Mute on the slider is switched off and keeps its value: it still says 35%, cannot be set, and is no stop of the Tab key, its name and its value are drawn in another colour than before, and the file is muted with its volume still 0.35; with Mute off again it is back as it was",
            toggled && offEnabled == false && offFocusable == false && offText == "35%" && offRange is { IsReadOnly: true } && Same(offRange.Value.Value, 0.35) && refused
                && mutedInFile.Muted && Same(mutedInFile.Volume, 0.35) && stopsMuted.Contains(Mute) && !stopsMuted.Contains(VolumeSliderId) && rowOff is (false, "35%") && inkOn.Length > 0 && inkOff.Length > 0 && inkOn != inkOff
                && untoggled && on is { IsEnabled: true, IsKeyboardFocusable: true, ValueText: "35%" } && on.Range is { IsReadOnly: false } back && Same(back.Value, 0.35)
                && !unmutedInFile.Muted && Same(unmutedInFile.Volume, 0.35),
            $"Mute toggled {toggled}: the slider enabled {offEnabled}, can take the focus {offFocusable}, says \"{offText}\", range {Words(offRange)}, a new value was {(refused ? "refused" : "taken")}; "
                + $"the file: muted {mutedInFile.Muted}, volume {F(mutedInFile.Volume)}; a stop of the Tab key: {stopsMuted.Contains(VolumeSliderId)}; the row enabled {rowOff.Item1}, showing \"{rowOff.Item2}\", its name and value drawn in {inkOff}, which were {inkOn}; {partsOff}; "
                + $"Mute toggled back {untoggled}: enabled {on?.IsEnabled}, says \"{on?.ValueText}\", range {Words(on?.Range)}; the file: muted {unmutedInFile.Muted}, volume {F(unmutedInFile.Volume)}");
    }

    /// <summary>The row that holds the Volume slider, which is what the tooltip and the two texts are on. UI thread.</summary>
    private static StudioSliderRow? RowOf(Editor editor)
    {
        DependencyObject? at = Descendant<StudioSlider>(editor.Window.Content, VolumeSliderId);
        while (at is not null and not StudioSliderRow)
        {
            at = VisualTreeHelper.GetParent(at);
        }

        return at as StudioSliderRow;
    }

    /// <summary>The colours the row's name and its value are drawn in. Empty when one of them is not a plain colour. UI thread.</summary>
    private static string InkOf(Editor editor) =>
        RowOf(editor) is { } row
        && (row.FindName("TitleLabel") as TextBlock)?.Foreground is SolidColorBrush name
        && (row.FindName("ValueLabel") as TextBlock)?.Foreground is SolidColorBrush value
            ? $"{name.Color} and {value.Color}"
            : string.Empty;

    private static string Words(UiaRange? range) =>
        range is { } r ? $"{F(r.Minimum)} to {F(r.Maximum)} at {F(r.Value)}, small step {F(r.SmallChange)}, large {F(r.LargeChange)}, read-only {r.IsReadOnly}" : "none";
}
