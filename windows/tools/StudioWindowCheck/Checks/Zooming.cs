using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyClips.App.Controls.Studio;
using TinyClips.App.Models.Studio;
using TinyClips.App.Views.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Capture;
using TinyClips.Tools.StudioWindowCheck.Host;
using Windows.System;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 10. Zooms: the lane above the trim bar, Add zoom, the keys Z and Delete, and what the preview
// shows for a zoom. What the Zoom section of the inspector does is in ZoomSection.cs, and a zoom
// that moves while the preview plays in ZoomPlaying.cs.
internal sealed partial class WindowChecks
{
    // Where the pointer rests in the recordings that have pointer positions: at the first place
    // until six seconds in, and at the second from then on.
    private const double PointerMovesAt = 6.0;
    private static readonly (double X, double Y) PointerFirst = (0.30, 0.28);
    private static readonly (double X, double Y) PointerSecond = (0.45, 0.60);

    // What a zoom at scale 2 holds when it looks at each of those places. Scale 2 shows half the
    // screen each way, so the part starts 0.25 before the point: at 0.30 − 0.25 = 0.05 across and
    // 0.28 − 0.25 = 0.03 down, and at 0.45 − 0.25 = 0.20 across and 0.60 − 0.25 = 0.35 down.
    private static readonly ScreenPart HeldAtFirst = new(0.05, 0.03, 0.5, 0.5);
    private static readonly ScreenPart HeldAtSecond = new(0.20, 0.35, 0.5, 0.5);

    // The pictures of a paused preview that were read for a zoom or a crop: how far the furthest edge was from its place.
    private readonly List<double> _partErrors = [];

    // The edge furthest from its place in any scene the preview drew while a zoom moved in.
    private double _worstMovingEdge = double.NaN;

    /// <summary>The length of the test recording, in seconds.</summary>
    private static double RecordingLength => TestMedia.Screen.Seconds;

    private void Zooming()
    {
        Timeline.Mark("10: zooms");
        _partErrors.Clear();
        _worstMovingEdge = double.NaN;
        _report.Check(
            "the window maps the Z key and the Delete key to the editor's keyboard model",
            StudioWindow.MapKey(VirtualKey.Z) == StudioShortcutKey.Z && StudioWindow.MapKey(VirtualKey.Delete) == StudioShortcutKey.Delete && StudioWindow.MapKey(VirtualKey.Back) == StudioShortcutKey.Other,
            $"Z is {StudioWindow.MapKey(VirtualKey.Z)}, Delete is {StudioWindow.MapKey(VirtualKey.Delete)}, Backspace is {StudioWindow.MapKey(VirtualKey.Back)}");

        AddingZooms();
        ZoomSection();
        DraggingOnTheLane();
        ZoomFollowsThePointer();
        ZoomMovesInWhilePlaying();
        ZoomSuggestions();
        if (_partErrors.Count > 0)
        {
            _report.Note($"over the {_partErrors.Count} pictures of a paused preview that were read for a zoom, the edge furthest from its place was {F(_partErrors.Max(), "0.00")} px from it, and on average the furthest edge of a picture {F(_partErrors.Average(), "0.00")} px; in the scenes drawn while a zoom moved in, {(double.IsNaN(_worstMovingEdge) ? "which were not read" : F(_worstMovingEdge, "0.00") + " px")}");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Projects
    // ---------------------------------------------------------------------------------------

    /// <summary>The pointer of the recordings that have one: two samples, which are steps.</summary>
    private static StudioCursorSample[] PointerSamples() =>
    [
        new StudioCursorSample { T = 0, X = PointerFirst.X, Y = PointerFirst.Y },
        new StudioCursorSample { T = PointerMovesAt, X = PointerSecond.X, Y = PointerSecond.Y },
    ];

    /// <summary>A project without a camera whose events file holds the given clicks and pointer positions.</summary>
    private TestFolder NewEventsProject(string name, StudioCursorSample[] cursor, StudioClickEvent[] clicks, Func<StudioProject, StudioProject>? edit = null, bool camera = false)
    {
        var folder = camera ? NewCameraProject(name, edit) : NewScreenProject(name, edit);

        // TestFolder writes an events file with the capture size only. This replaces it before the project is opened.
        File.WriteAllText(folder.Paths.EventsPath, StudioProjectJson.WriteEvents(folder.Events with { Cursor = cursor, Clicks = clicks }));
        return folder;
    }

    private static StudioZoom PointZoom(double start, double end, double x, double y, double scale = 2, double easeIn = 0.5, double easeOut = 0.5) => new()
    {
        Start = start,
        End = end,
        Scale = scale,
        Focus = new StudioZoomFocus { X = x, Y = y },
        EaseIn = easeIn,
        EaseOut = easeOut,
    };

    // ---------------------------------------------------------------------------------------
    // What the editor holds, and what the lane tells a screen reader
    // ---------------------------------------------------------------------------------------

    /// <summary>The zooms the editor holds now. Read on the UI thread.</summary>
    private StudioZoom[] ZoomsOf(Editor editor) => OnUi(() => editor.Window.ViewModel.Zooms.ToArray());

    private int? SelectedZoomOf(Editor editor) => OnUi(() => editor.Window.ViewModel.SelectedZoomIndex);

    /// <summary>The lane's items, as a screen reader finds them under the list.</summary>
    private static List<UiaElement> LaneItems(Editor editor) =>
        Find(editor, "StudioZoomLane", 0.5)?.Children().Where(child => child.ControlType == ControlTypeNames.ListItem).ToList() ?? [];

    /// <summary>The lane as one line: the name of every item in order, with a star before the selected one.</summary>
    private static string LaneText(Editor editor)
    {
        var items = LaneItems(editor);
        return items.Count == 0 ? "empty" : string.Join(" | ", items.Select(item => (item.IsSelected == true ? "*" : string.Empty) + item.Name));
    }

    private static string WaitForLane(Editor editor, string wanted, double seconds = 2) =>
        Until(() => LaneText(editor), text => text == wanted, seconds);

    /// <summary>The window's lane, for what is done to it in process. UI thread.</summary>
    private static StudioZoomLane? LaneOf(Editor editor) => Descendant<StudioZoomLane>(editor.Window.Content, "StudioZoomLane");

    /// <summary>What the lane does with a key while it has the focus. The key is not pressed.</summary>
    private bool LaneKey(Editor editor, VirtualKey key) => OnUi(() => LaneOf(editor)?.HandleKey(key) ?? false);

    /// <summary>A sentence the window sent to screen readers after a mark, waited for.</summary>
    private static (bool Said, string Heard) Said(UiaEvents heard, int mark, string sentence)
    {
        var arrived = heard.WaitFor(mark, "notification", e => e.Text == sentence);
        var text = arrived.Length == 0 ? "nothing" : string.Join(", ", arrived.Select(e => $"\"{e.Text}\""));
        return (arrived.Any(e => e.Text == sentence), heard.Problem is null ? text : $"{text} ({heard.Problem})");
    }

    private static bool Same(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance;

    private static string Seconds(double value) => F(value, "0.######");

    // ---------------------------------------------------------------------------------------
    // The picture
    // ---------------------------------------------------------------------------------------

    /// <summary>A screenshot, and what its canvas shows where the screen recording's card is.</summary>
    private sealed record PartSight(Shot Shot, Box Canvas, StudioFrameRect Card, PartReading Reading);

    /// <summary>
    /// The screen recording's card in a screenshot: where the layout puts it on the canvas, with
    /// its edges on whole pixels. A zoom never moves it, so it is resolved without the zooms.
    /// </summary>
    private static StudioFrameRect? ScreenCard(Box canvas, StudioProject project) =>
        StudioLayoutResolver.Resolve(project with { Zooms = [] }, 0, canvas.Width, canvas.Height).Screen is { } screen
            ? Shift(Aligned(screen.Rect), canvas)
            : null;

    /// <summary>How far inside the card an edge has to be: clear of its round corners.</summary>
    private static double CardInset(Box canvas, StudioProject project) =>
        (Math.Clamp(project.Screen.CornerRadius, 0, 0.2) * Math.Min(canvas.Width, canvas.Height)) + 2;

    private PartSight? LookAtPart(Editor editor, ScreenPart want, int frame)
    {
        var project = editor.Expected;
        if (editor.Camera.Take() is not { } shot
            || CanvasBox(editor, shot) is not { } canvas
            || canvas.Width < 8
            || canvas.Height < 8
            || ScreenCard(canvas, project) is not { } card)
        {
            return null;
        }

        return new PartSight(shot, canvas, card, ReadPart(shot, TestMedia.Screen, ScreenLandmarks(frame), card, want, mirror: false, CardInset(canvas, project)));
    }

    /// <summary>Looks until the card shows the wanted part of the wanted frame, or the time is up. The last look is returned either way.</summary>
    private PartSight? LookForPart(Editor editor, ScreenPart want, int frame, double seconds = 3) =>
        Until(() => LookAtPart(editor, want, frame), sight => sight is not null && Judge(sight.Reading, frame) is null, seconds, 30);

    /// <summary>
    /// One check that the preview shows a part of the screen recording at a frame: the frame
    /// number reads where the strip should be, and the edges of the test picture are where that
    /// part puts them.
    /// </summary>
    /// <param name="byHand">How the part was worked out, for the report.</param>
    private PartSight? ShowsPart(Editor editor, string name, ScreenPart want, int frame, string byHand)
    {
        var sight = LookForPart(editor, want, frame);
        var wrong = sight is null ? "no screenshot" : Judge(sight.Reading, frame);
        if (sight is not null && wrong is null)
        {
            _partErrors.Add(sight.Reading.Worst);
        }

        var fitted = sight is null ? null : Fitted(sight.Reading, TestMedia.Screen, mirror: false);
        _report.Check(
            name,
            wrong is null,
            wrong is null
                ? $"{byHand}; in a card of {R(sight!.Card)}: {sight.Reading}; the edges fit the part {fitted?.ToString() ?? "(too few to tell)"}"
                : $"{byHand}; {wrong}{(fitted is { } part ? $"; the edges that were found fit the part {part}" : string.Empty)}");
        return wrong is null ? sight : null;
    }

    // ---------------------------------------------------------------------------------------
    // Adding zooms, the lane, and the keys
    // ---------------------------------------------------------------------------------------

    private void AddingZooms()
    {
        Timeline.Mark("10: adding zooms");
        var folder = NewEventsProject("Zooms", PointerSamples(), clicks: []);
        if (OpenReady(folder, "zooms") is not { } editor)
        {
            return;
        }

        using var heard = UiaEvents.Listen(_uia, editor.Root);

        // What the editor asks to have read out, as its view model raises it, to hold against what the listener hears.
        var announced = new List<string>();
        OnUi(() => editor.Window.ViewModel.Announced += (_, e) =>
        {
            lock (announced)
            {
                announced.Add(e.Message);
            }
        });
        const int At = 60;
        SetSlider(editor, "StudioPlayhead", MiddleOf(At));
        var before = LookForPart(editor, ScreenPart.Whole, At);
        EmptyLane(editor, before, At);

        // Z, where the pointer rests at the first place.
        Timeline.Mark("10: Z adds a zoom at the playhead");
        var start = Playhead(editor);
        var mark = heard.Mark();
        var key = Key(editor, StudioShortcutKey.Z);
        const string FirstName = "Zoom 2×, 2.0 to 5.0 seconds";
        var lane = WaitForLane(editor, "*" + FirstName);
        var zooms = ZoomsOf(editor);
        var added = Said(heard, mark, StudioEditorText.ZoomAddedMessage);
        var isAsExpected = zooms.Length == 1
            && Same(zooms[0].Start, start) && Same(zooms[0].End, start + 3) && zooms[0].Scale == 2
            && zooms[0].Focus.Mode == StudioZoomFocusMode.Point && Same(zooms[0].Focus.X, PointerFirst.X) && Same(zooms[0].Focus.Y, PointerFirst.Y)
            && zooms[0].EaseIn == 0.5 && zooms[0].EaseOut == 0.5 && zooms[0].Origin == StudioZoomOrigin.Manual;
        var list = Find(editor, "StudioZoomLane");
        _report.Check(
            "what Z runs adds a zoom at the playhead: three seconds long, at scale 2, looking at where the pointer is; the lane gets one item, named as the editor words the zoom, and it is the selected one",
            key == StudioShortcutAction.AddZoom && FrameOf(start) == At && isAsExpected && lane == "*" + FirstName
                && FirstName == StudioEditorText.GetZoomDescription(zooms[0]) && list?.SelectedNames.SequenceEqual([FirstName]) == true
                && Find(editor, "StudioZoom_0") is { ControlType: ControlTypeNames.ListItem, IsEnabled: true } && Gone(editor, "StudioZoomLaneEmptyText", 1),
            $"the key ran {key} with the playhead at {Seconds(start)} s; the editor holds {(zooms.Length == 0 ? "no zoom" : string.Join(", ", zooms.Select(Describe)))}; the lane: {lane}; the list's selection: {string.Join(", ", list?.SelectedNames ?? [])}");
        _report.Check("a screen reader is told \"Zoom added.\"", added.Said, $"sent: {added.Heard}");

        // A zoom starts unzoomed, so the playhead goes on to where it has moved in: half a second later.
        var shown = FrameOf(start + 0.5);
        var head = Until(() => Playhead(editor), value => Same(value, start + 0.5), 2);
        var time = TimeText(editor);
        _report.Check(
            "the playhead moves to where the new zoom has moved in, half a second after its start, and the time follows",
            Same(head, start + 0.5) && shown == At + 15 && time == StudioEditorText.GetTimeText(head, RecordingLength),
            $"playhead {Seconds(head)} s (frame {FrameOf(head)}), time \"{time}\"");

        // The picture. It cannot tell the right picture from one that is stretched or squeezed down
        // the screen about the patches by less than about one part in 150: the level edges it
        // reads are the strip's, the patches' and nothing below them. A picture that is not
        // zoomed, zoomed about another point, or zoomed by another amount has its edges tens of
        // pixels from where they are looked for, and its strip elsewhere.
        var zoomed = ShowsPart(
            editor,
            "the preview is zoomed: it shows the half of the screen around the pointer, at the frame the playhead is on",
            HeldAtFirst,
            shown,
            "scale 2 around (0.30, 0.28) is the part from 0.05 across and 0.03 down, half the screen each way");
        if (zoomed is not null)
        {
            var path = Path.Combine(_output, "zoom-preview.png");
            zoomed.Shot.Save(path);
            _report.Line($"  saved {path}");
        }

        if (before is not null && zoomed is not null)
        {
            // Around the card: the canvas and the card's shadow, which a zoom leaves alone.
            var card = new Box((int)zoomed.Card.X, (int)zoomed.Card.Y, (int)zoomed.Card.Width, (int)zoomed.Card.Height);
            var canvas = zoomed.Canvas;
            Box[] around =
            [
                new Box(canvas.X, canvas.Y, canvas.Width, card.Y - canvas.Y),
                new Box(canvas.X, card.Bottom, canvas.Width, canvas.Bottom - card.Bottom),
                new Box(canvas.X, card.Y, card.X - canvas.X, card.Height),
                new Box(card.Right, card.Y, canvas.Right - card.Right, card.Height),
            ];
            var outside = around.Sum(box => DifferenceCount(before.Shot, zoomed.Shot, box, 12));
            var inside = DifferenceCount(before.Shot, zoomed.Shot, card, 12);
            _report.Check(
                "the zoom changes the picture inside the screen's card and nothing around it: the card, its shadow and the canvas stay where they were",
                before.Canvas == zoomed.Canvas && outside == 0 && inside > card.Width * card.Height / 4,
                $"{outside} of the {around.Sum(box => box.Width * box.Height)} pixels around the card differ from the picture before the zoom, and {inside} of the {card.Width * card.Height} inside it");
        }

        ZoomSectionShows(editor, "the Zoom section shows the new zoom", "Zoom 1 of 1", "2.0 to 5.0 seconds", 2, "2×", PointerFirst, "Start 2.0 seconds", "End 5.0 seconds", 0.5, 0.5);

        // Z again, where the zoom now is.
        Timeline.Mark("10: Z where a zoom is, and where none fits");
        mark = heard.Mark();
        var again = Key(editor, StudioShortcutKey.Z);
        var already = Said(heard, mark, StudioEditorText.ZoomAlreadyThereMessage);
        _report.Check(
            "what Z runs where a zoom already is adds nothing, keeps that zoom selected, and tells a screen reader that there is one",
            again == StudioShortcutAction.AddZoom && already.Said && ZoomsOf(editor).Length == 1 && LaneText(editor) == "*" + FirstName && Same(Playhead(editor), head),
            $"sent: {already.Heard}; the lane: {LaneText(editor)}; playhead {Seconds(Playhead(editor))} s");

        // A tenth of a second before the end of the recording there is no room for the shortest zoom.
        SetSlider(editor, "StudioPlayhead", RecordingLength - 0.1);
        var canAdd = Until(() => Find(editor, "StudioAddZoomButton", 0.5)?.IsEnabled, enabled => enabled == false, 2);
        var canAddInSection = Find(editor, "StudioZoomSectionAddButton", 0.5)?.IsEnabled;
        mark = heard.Mark();
        var noRoomKey = Key(editor, StudioShortcutKey.Z);
        var noRoom = Said(heard, mark, StudioEditorText.NoRoomForZoomMessage);
        _report.Check(
            "where less than the shortest zoom fits, both Add zoom buttons are disabled, and what Z runs adds nothing and tells a screen reader that there is no room",
            canAdd == false && canAddInSection == false && noRoomKey == StudioShortcutAction.AddZoom && noRoom.Said && ZoomsOf(editor).Length == 1 && LaneText(editor) == "*" + FirstName,
            $"with the playhead at {Seconds(Playhead(editor))} s: Add zoom enabled {canAdd}, in the Zoom section {canAddInSection}; sent: {noRoom.Heard}; the lane: {LaneText(editor)}");

        // The two buttons, where the pointer rests at the second place.
        Timeline.Mark("10: the two Add zoom buttons");
        const string SecondName = "Zoom 2×, 6.5 to 9.5 seconds";
        const string ThirdName = "Zoom 2×, 10.0 to 12.0 seconds";
        SetSlider(editor, "StudioPlayhead", MiddleOf(195));
        var secondStart = Playhead(editor);
        var enabledAgain = Until(() => Find(editor, "StudioAddZoomButton", 0.5)?.IsEnabled, enabled => enabled == true, 2);
        var pressed = Invoke(editor, "StudioAddZoomButton");
        var laneAfterSecond = WaitForLane(editor, $"{FirstName} | *{SecondName}");
        var secondHead = Until(() => Playhead(editor), value => Same(value, secondStart + 0.5), 2);
        SetSlider(editor, "StudioPlayhead", MiddleOf(300));
        var thirdStart = Playhead(editor);
        var pressedInSection = Invoke(editor, "StudioZoomSectionAddButton");
        var laneAfterThird = WaitForLane(editor, $"{FirstName} | {SecondName} | *{ThirdName}");
        zooms = ZoomsOf(editor);
        _report.Check(
            "Add zoom in the transport row and Add zoom in the Zoom section each add a zoom at the playhead and select it; one that reaches the end of the recording stops there",
            enabledAgain == true && pressed && pressedInSection && laneAfterSecond == $"{FirstName} | *{SecondName}" && laneAfterThird == $"{FirstName} | {SecondName} | *{ThirdName}"
                && zooms.Length == 3 && Same(zooms[1].Start, secondStart) && Same(zooms[1].End, secondStart + 3) && Same(zooms[1].Focus.X, PointerSecond.X) && Same(zooms[1].Focus.Y, PointerSecond.Y)
                && Same(zooms[2].Start, thirdStart) && zooms[2].End == RecordingLength && Same(secondHead, secondStart + 0.5) && Same(Playhead(editor), thirdStart + 0.5),
            $"the lane after the first button: {laneAfterSecond}; after the second: {laneAfterThird}; the editor holds {string.Join(", ", zooms.Select(Describe))}; playhead {Seconds(Playhead(editor))} s");
        var position = NameOf(editor, "StudioZoomPositionText");
        var range = NameOf(editor, "StudioZoomRangeText");
        _report.Check("the Zoom section says which zoom is selected and when it is", position == "Zoom 3 of 3" && range == "10.0 to 12.0 seconds", $"\"{position}\", \"{range}\"");

        LaneIsLinedUp(editor, zooms);
        var heardPerEvent = LaneKeys(editor, heard, zooms);
        LaneItemsCanBeSelected(editor, zooms);
        PreviousAndNext(editor, zooms);

        // Delete, with a zoom selected and with none.
        Timeline.Mark("10: Delete");
        Find(editor, "StudioZoom_1")?.Select();
        WaitForLane(editor, $"{FirstName} | *{SecondName} | {ThirdName}");
        var headBeforeDelete = Playhead(editor);
        mark = heard.Mark();
        var delete = Key(editor, StudioShortcutKey.Delete);
        var laneAfterDelete = WaitForLane(editor, $"{FirstName} | {ThirdName}");
        var deleted = Said(heard, mark, StudioEditorText.ZoomDeletedMessage);
        var deleteAgain = Key(editor, StudioShortcutKey.Delete);
        _report.Check(
            "what Delete runs removes the selected zoom, leaves none selected and the playhead where it was, and tells a screen reader; with no zoom selected the key is left alone",
            delete == StudioShortcutAction.RemoveSelectedZoom && laneAfterDelete == $"{FirstName} | {ThirdName}" && deleted.Said && SelectedZoomOf(editor) is null
                && Same(Playhead(editor), headBeforeDelete) && deleteAgain == StudioShortcutAction.None && ZoomsOf(editor).Length == 2,
            $"Delete ran {delete}, then {deleteAgain}; the lane: {laneAfterDelete}; sent: {deleted.Heard}; playhead {Seconds(Playhead(editor))} s, {Seconds(headBeforeDelete)} s before");
        var hint = NameOf(editor, "StudioZoomHint", 1);
        _report.Check(
            "with zooms and none selected the Zoom section says how many there are and how to select one, and shows no control of a zoom",
            NameOf(editor, "StudioZoomPositionText") == "2 zooms" && hint.StartsWith("Select a zoom", StringComparison.Ordinal) && Gone(editor, "StudioZoomScaleSlider", 1) && Gone(editor, "StudioDeleteZoomButton", 1) && Gone(editor, "StudioZoomRangeText", 1),
            $"\"{NameOf(editor, "StudioZoomPositionText")}\", \"{hint}\"");

        UndoAndRedoOfZooms(editor, FirstName, ThirdName);

        // Everything the editor announced in this window, against everything the listener heard.
        Thread.Sleep(400);
        string[] asked;
        lock (announced)
        {
            asked = [.. announced];
        }

        var arrived = heard.Since(0, "notification");
        var times = asked.Length > 0 && arrived.Length % asked.Length == 0 ? arrived.Length / asked.Length : 0;
        var inOrder = times > 0 && arrived.Select(e => e.Text).SequenceEqual(asked.SelectMany(text => Enumerable.Repeat(text, times)));
        _report.Check(
            "every sentence the editor announced reached a listener for screen reader notifications, in the order they were announced and no more often than an event of a control of the framework, and the editor announced each once",
            asked.Length >= 6 && inOrder && times == heardPerEvent,
            $"the editor announced {asked.Length} sentences: {string.Join(" ", asked.Select(text => $"\"{text}\""))}; the listener heard {arrived.Length} events{(inOrder ? $", each sentence {times} time(s)" : ": " + string.Join(" ", arrived.Select(e => $"\"{e.Text}\"")))}"
                + (arrived.Length >= 2 ? string.Create(CultureInfo.InvariantCulture, $"; the first two arrived {System.Diagnostics.Stopwatch.GetElapsedTime(arrived[0].At, arrived[1].At).TotalMilliseconds:0.0} ms apart, on threads {arrived[0].Thread} and {arrived[1].Thread}, from {arrived[0].From} and {arrived[1].From}, as \"{arrived[0].Id}\" and \"{arrived[1].Id}\"") : string.Empty));

        // What was saved, once the editor has saved by itself.
        var live = ZoomsOf(editor);
        var wanted = string.Join(", ", live.Select(Describe));
        var saved = Until(() => string.Join(", ", _services.Store.Load(editor.Id).Zooms.Select(Describe)), text => text == wanted, 3, 100);
        _report.Check("the editor saves the zooms by itself: the project file holds the zooms the editor holds", saved == wanted && live.Length > 0, saved == wanted ? saved : $"saved: {saved} | the editor holds: {wanted}");
        CloseQuietly(editor);
    }

    /// <summary>A recording without zooms: what the lane, the buttons and the Zoom section say, and the picture the zoom checks start from.</summary>
    private void EmptyLane(Editor editor, PartSight? before, int frame)
    {
        var list = Find(editor, "StudioZoomLane");
        var text = Find(editor, "StudioZoomLaneEmptyText", 1);
        const string HowToAdd = "No zooms. Press Z to add one at the playhead.";
        _report.Check(
            "a recording without zooms: the lane is a list called Zooms with no items, which can take the keyboard focus, and it says how to add a zoom, as a text and as the list's description",
            list is { ControlType: ControlTypeNames.List, Name: "Zooms", IsKeyboardFocusable: true, IsEnabled: true } && LaneItems(editor).Count == 0 && text?.Name == HowToAdd && list.HelpText == HowToAdd
                && list.Patterns.Contains("Selection", StringComparison.Ordinal) && list.SelectedNames.Length == 0,
            $"{list} with {LaneItems(editor).Count} item(s), described as \"{list?.HelpText}\"; the text: \"{text?.Name}\"; patterns({list?.Patterns})");

        var suggest = Find(editor, "StudioSuggestZoomsButton");
        var note = NameOf(editor, "StudioNoClicksNote", 1);
        _report.Check(
            "without zooms the Zoom section says so, has nothing to step to, and offers Add zoom; a recording without clicks has Suggest zooms disabled, with the reason next to it and as the button's description",
            NameOf(editor, "StudioZoomPositionText") == "No zooms yet"
                && Find(editor, "StudioPreviousZoomButton") is { IsEnabled: false, Name: "Previous zoom" } && Find(editor, "StudioNextZoomButton") is { IsEnabled: false, Name: "Next zoom" }
                && Find(editor, "StudioAddZoomButton") is { IsEnabled: true, Name: "Add zoom" } && Find(editor, "StudioZoomSectionAddButton") is { IsEnabled: true, Name: "Add zoom" }
                && suggest is { IsEnabled: false, Name: "Suggest zooms" } && suggest.HelpText == StudioEditorText.NoClicksExplanation && note == StudioEditorText.NoClicksExplanation
                && Gone(editor, "StudioZoomScaleSlider", 0.5) && Gone(editor, "StudioDeleteZoomButton", 0.5) && Gone(editor, "StudioRemoveSuggestionsButton", 0.5) && Gone(editor, "StudioZoomHint", 0.5),
            $"\"{NameOf(editor, "StudioZoomPositionText")}\"; Previous enabled {Find(editor, "StudioPreviousZoomButton")?.IsEnabled}, Next enabled {Find(editor, "StudioNextZoomButton")?.IsEnabled}; "
                + $"Suggest zooms enabled {suggest?.IsEnabled}, described as \"{suggest?.HelpText}\"; the note: \"{note}\"");

        var wrong = before is null ? "no screenshot" : Judge(before.Reading, frame);
        _report.Check(
            "before any zoom the preview shows the whole screen, with the edges of the test picture where the whole screen puts them",
            wrong is null,
            wrong ?? $"in a card of {R(before!.Card)}: {before.Reading}");
        if (before is not null && wrong is null)
        {
            _report.Note($"without a zoom the edge furthest from its place is {F(before.Reading.Worst, "0.00")} px from it: that is how closely this way of reading a picture places an edge");
        }
    }

    /// <summary>What the Zoom section shows for the selected zoom, read through UI Automation.</summary>
    private bool ZoomSectionShows(Editor editor, string name, string position, string range, double scale, string scaleText, (double X, double Y) focus, string start, string end, double easeIn, double easeOut)
    {
        string?[] problems =
        [
            NameOf(editor, "StudioZoomPositionText") == position ? null : $"the position reads \"{NameOf(editor, "StudioZoomPositionText")}\"",
            NameOf(editor, "StudioZoomRangeText") == range ? null : $"the times read \"{NameOf(editor, "StudioZoomRangeText")}\"",
            Slider(editor, "StudioZoomScaleSlider", scale, scaleText),
            Selected(editor, "StudioZoomFocusPoint"),
            Selected(editor, "StudioZoomFocusPointer", wanted: false),
            Slider(editor, "StudioZoomFocusXSlider", focus.X, StudioEditorText.GetPercentText(focus.X)),
            Slider(editor, "StudioZoomFocusYSlider", focus.Y, StudioEditorText.GetPercentText(focus.Y)),
            NameOf(editor, "StudioZoomStartText") == start ? null : $"Start reads \"{NameOf(editor, "StudioZoomStartText")}\"",
            NameOf(editor, "StudioZoomEndText") == end ? null : $"End reads \"{NameOf(editor, "StudioZoomEndText")}\"",
            Slider(editor, "StudioZoomEaseInSlider", easeIn, StudioEditorModel.GetSecondsText(easeIn)),
            Slider(editor, "StudioZoomEaseOutSlider", easeOut, StudioEditorModel.GetSecondsText(easeOut)),
            Find(editor, "StudioDeleteZoomButton", 0.5) is { IsEnabled: true, Name: "Delete zoom" } ? null : "there is no Delete zoom button",
        ];
        var detail = string.Join("; ", problems.Where(problem => problem is not null));
        return _report.Check(
            $"{name}: which zoom it is and its times, its scale, that it looks at a point and where, its start and end, its two eases, and Delete zoom",
            detail.Length == 0,
            detail.Length == 0 ? $"\"{position}\", \"{range}\", scale {scaleText}, looks at ({F(focus.X)}, {F(focus.Y)}), \"{start}\", \"{end}\", eases {F(easeIn)} s and {F(easeOut)} s" : detail);
    }

    /// <summary>
    /// The lane is on the trim bar's time scale: each block starts and ends where the playhead
    /// of the trim bar is when it is on those times, and the lane's own playhead line is there
    /// with it.
    /// </summary>
    private void LaneIsLinedUp(Editor editor, StudioZoom[] zooms)
    {
        Timeline.Mark("10: the lane is lined up with the trim bar");
        var lane = Find(editor, "StudioZoomLane")?.Bounds ?? default;
        var bar = Find(editor, "StudioTrimBar")?.Bounds ?? default;
        var wrong = new List<string>();
        var worst = 0.0;
        var wrongLine = new List<string>();
        var worstLine = 0.0;
        var lineSeen = new HashSet<int>();
        for (var index = 0; index < zooms.Length; index++)
        {
            var block = Find(editor, $"StudioZoom_{index}")?.Bounds ?? default;
            foreach (var (time, edge, which) in new[] { (zooms[index].Start, (double)block.X, "starts"), (zooms[index].End, block.X + block.Width, "ends") })
            {
                SetSlider(editor, "StudioPlayhead", time);
                var head = Until(() => Find(editor, "StudioPlayhead", 0.5), thumb => thumb?.Range is { } r && Math.Abs(r.Value - time) < 0.001, 2)?.Bounds ?? default;
                var middle = head.X + (head.Width / 2.0);
                worst = Math.Max(worst, Math.Abs(middle - edge));
                if (Math.Abs(middle - edge) > 1.5 || block.Width <= 0)
                {
                    wrong.Add($"zoom {index + 1} {which} at x {F(edge)} and the playhead at {Seconds(time)} s is at x {F(middle)}");
                }

                // The lane's line is not in the tree a screen reader gets, so the lane is asked,
                // in its own units from its left edge.
                var line = lane.X + (OnUi(() => LaneOf(editor)?.PlayheadLineX ?? double.NaN) * editor.Scale);
                lineSeen.Add((int)Math.Round(line));
                if (Math.Abs(middle - line) <= 1.5)
                {
                    worstLine = Math.Max(worstLine, Math.Abs(middle - line));
                }
                else
                {
                    wrongLine.Add($"at {Seconds(time)} s the line is at x {F(line)} and the trim bar's playhead at x {F(middle)}");
                }
            }
        }

        _report.Check(
            "the lane is as wide as the trim bar and on its time scale: every block starts and ends where the trim bar's playhead is at those times",
            lane.X == bar.X && lane.Width == bar.Width && lane.Y + lane.Height <= bar.Y && wrong.Count == 0 && zooms.Length == 3,
            wrong.Count == 0 ? $"lane {lane.Width}x{lane.Height} at ({lane.X},{lane.Y}), trim bar {bar.Width}x{bar.Height} at ({bar.X},{bar.Y}); no block edge more than {F(worst, "0.#")} px from the playhead at its time" : string.Join("; ", wrong));
        _report.Check(
            "the lane's playhead line follows the playhead: at each of those times it is over the playhead of the trim bar",
            wrongLine.Count == 0 && lineSeen.Count >= 4 && zooms.Length == 3,
            wrongLine.Count == 0 ? $"the line was at {lineSeen.Count} different places for the {zooms.Length * 2} times, never more than {F(worstLine, "0.#")} px from the trim bar's playhead" : string.Join("; ", wrongLine));
    }

    /// <summary>What the lane does with the arrow keys, Home and End while it has the focus.</summary>
    /// <returns>How often the listener hears one event of the window: the events of a control of the framework are counted.</returns>
    private int LaneKeys(Editor editor, UiaEvents heard, StudioZoom[] zooms)
    {
        Timeline.Mark("10: the lane's keys");
        var steps = new List<string>();
        var wrong = new List<string>();
        var mark = heard.Mark();
        void Press(VirtualKey key, int? wanted, bool movesPlayhead = true)
        {
            var headBefore = Playhead(editor);
            var handled = LaneKey(editor, key);
            var selected = Until(() => SelectedZoomOf(editor), index => index == wanted, 1);
            var head = wanted is { } index && movesPlayhead ? Until(() => Playhead(editor), value => Same(value, zooms[index].Start + 0.5), 1) : Playhead(editor);
            var marked = LaneItems(editor).Select((item, at) => item.IsSelected == true ? at : -1).Where(at => at >= 0).ToArray();
            steps.Add($"{key}: zoom {(selected is { } s ? (s + 1).ToString(CultureInfo.InvariantCulture) : "none")} at {Seconds(head)} s");
            var headWanted = wanted is { } w && movesPlayhead ? zooms[w].Start + 0.5 : headBefore;
            if (!handled || selected != wanted || !Same(head, headWanted) || !marked.SequenceEqual(wanted is { } only ? new[] { only } : []))
            {
                wrong.Add($"{key}: handled {handled}, selected {selected?.ToString(CultureInfo.InvariantCulture) ?? "none"} (the lane marks {string.Join(",", marked)}), playhead {Seconds(head)} s; wanted {wanted?.ToString(CultureInfo.InvariantCulture) ?? "none"} at {Seconds(headWanted)} s");
            }
        }

        // The third zoom is selected. Each key selects a zoom and moves the playhead to where it has moved in.
        Press(VirtualKey.Home, 0);
        Press(VirtualKey.Right, 1);
        Press(VirtualKey.Right, 2);
        Press(VirtualKey.Right, 2, movesPlayhead: false);
        Press(VirtualKey.Left, 1);
        Press(VirtualKey.End, 2);
        Press(VirtualKey.Home, 0);
        Press(VirtualKey.Left, 0, movesPlayhead: false);

        // With none selected, the arrows start from the playhead: between the first zoom and the second.
        Find(editor, "StudioZoom_0")?.RemoveFromSelection();
        Until(() => SelectedZoomOf(editor), index => index is null, 1);
        SetSlider(editor, "StudioPlayhead", 5.5);
        Press(VirtualKey.Right, 1);
        Find(editor, "StudioZoom_1")?.RemoveFromSelection();
        Until(() => SelectedZoomOf(editor), index => index is null, 1);
        SetSlider(editor, "StudioPlayhead", 5.5);
        Press(VirtualKey.Left, 0);

        var others = new[] { VirtualKey.Delete, VirtualKey.Space, VirtualKey.Z, VirtualKey.Up, VirtualKey.Down }.Where(key => LaneKey(editor, key)).ToArray();
        var selectedEvents = heard.WaitFor(mark, "selected", e => false, 0.3).Where(e => e.Id.StartsWith("StudioZoom_", StringComparison.Ordinal)).ToArray();

        // For comparison, a control of the framework that tells of its selection by itself: a background swatch.
        var swatchMark = heard.Mark();
        var lemon = StudioSwatch.Find("lemon");
        if (lemon is not null && Find(editor, "StudioSwatch_lemon")?.Select() == true)
        {
            Expect(editor, p => p with { Canvas = p.Canvas with { Background = new StudioBackground { Style = StudioBackgroundStyle.Solid, Preset = "lemon", Primary = lemon.PrimaryHex, Secondary = null } } });
        }

        var swatchEvents = heard.WaitFor(swatchMark, "selected", e => false, 0.5).Where(e => e.Id == "StudioSwatch_lemon").ToArray();
        static string Arrivals(UiaEvent[] events) => string.Join(", ", events.Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Id.Replace("Studio", string.Empty, StringComparison.Ordinal)} at {System.Diagnostics.Stopwatch.GetElapsedTime(events[0].At, e.At).TotalMilliseconds:0} ms on thread {e.Thread} from {e.From}")));
        _report.Check(
            "with the focus on the lane, Home and End select the first and the last zoom and Left and Right the one before and after, each shown where it has moved in; at either end, and for any other key, the lane does nothing",
            wrong.Count == 0 && others.Length == 0 && SelectedZoomOf(editor) == 0,
            wrong.Count == 0 ? $"{string.Join("; ", steps)}; keys the lane leaves to the window: Delete, Space, Z, Up, Down{(others.Length == 0 ? string.Empty : "; but it took " + string.Join(", ", others))}" : string.Join("; ", wrong));
        // The keys changed the selection eight times: to zoom 1, 2, 3, 2, 3, 1, and from none to 2 and to 1.
        string[] changes = ["StudioZoom_0", "StudioZoom_1", "StudioZoom_2", "StudioZoom_1", "StudioZoom_2", "StudioZoom_0", "StudioZoom_1", "StudioZoom_0"];
        var each = swatchEvents.Length;
        _report.Check(
            "a screen reader is told each time another zoom becomes the selected one, and no more often than it is told of a selected background, which is a control of the framework",
            each >= 1 && selectedEvents.Select(e => e.Id).SequenceEqual(changes.SelectMany(id => Enumerable.Repeat(id, each))) && selectedEvents.All(e => e.Text.StartsWith("Zoom 2×", StringComparison.Ordinal)),
            $"{selectedEvents.Length} events for the 8 changes of the selection the keys made: {string.Join(", ", selectedEvents.Select(e => e.Id.Replace("StudioZoom_", "zoom ", StringComparison.Ordinal)))}; {each} for one background selected: {Arrivals(swatchEvents)}; the first of the lane's: {Arrivals([.. selectedEvents.Take(4)])}{(heard.Problem is null ? string.Empty : $" ({heard.Problem})")}");
        if (each > 1)
        {
            _report.Note($"this tool's listener hears every event of the window {each} times, a few milliseconds apart on two threads: also the events of the framework's own controls. The tool listens from inside the process that shows the window. Whether a screen reader hears them once was not seen: none was run");
        }

        return each;
    }

    /// <summary>A block as a list item: it can be selected, taken out of the selection, and pressed.</summary>
    private void LaneItemsCanBeSelected(Editor editor, StudioZoom[] zooms)
    {
        Timeline.Mark("10: the lane's items, through UI Automation");
        var items = LaneItems(editor);
        var patterns = items.Select(item => item.Patterns).Distinct().ToArray();
        var ids = items.Select(item => item.Id).ToArray();

        SetSlider(editor, "StudioPlayhead", 5.5);
        var head = Playhead(editor);
        var selected = Find(editor, "StudioZoom_2")?.Select() ?? false;
        var afterSelect = (Until(() => SelectedZoomOf(editor), index => index == 2, 1), Playhead(editor));
        var invoked = Find(editor, "StudioZoom_1")?.Invoke() ?? false;
        var afterInvoke = (Until(() => SelectedZoomOf(editor), index => index == 1, 1), Until(() => Playhead(editor), value => Same(value, zooms[1].Start + 0.5), 1));
        var removed = Find(editor, "StudioZoom_1")?.RemoveFromSelection() ?? false;
        var afterRemove = (Until(() => SelectedZoomOf(editor), index => index is null, 1), Playhead(editor));
        var position = NameOf(editor, "StudioZoomPositionText");
        _report.Check(
            "each block is a list item with an automation id that can be selected and pressed: selecting it leaves the playhead where it is, pressing it also shows the zoom where it has moved in, and taking it out of the selection leaves none selected",
            items.Count == 3 && patterns.Length == 1 && patterns[0] == "Invoke,SelectionItem" && ids.SequenceEqual(["StudioZoom_0", "StudioZoom_1", "StudioZoom_2"]) && items.All(item => !item.IsKeyboardFocusable && item.Children().Count == 0)
                && selected && afterSelect.Item1 == 2 && Same(afterSelect.Item2, head)
                && invoked && afterInvoke.Item1 == 1 && Same(afterInvoke.Item2, zooms[1].Start + 0.5)
                && removed && afterRemove.Item1 is null && Same(afterRemove.Item2, afterInvoke.Item2) && position == "3 zooms",
            $"ids {string.Join(", ", ids)}, patterns({string.Join(" / ", patterns)}); selected: zoom {afterSelect.Item1 + 1} with the playhead at {Seconds(afterSelect.Item2)} s ({Seconds(head)} s before); "
                + $"pressed: zoom {afterInvoke.Item1 + 1} at {Seconds(afterInvoke.Item2)} s; taken out: {(afterRemove.Item1 is null ? "none selected" : "still selected")}, \"{position}\"");
    }

    /// <summary>Previous and Next in the Zoom section.</summary>
    private void PreviousAndNext(Editor editor, StudioZoom[] zooms)
    {
        Timeline.Mark("10: Previous and Next");
        var wrong = new List<string>();
        var steps = new List<string>();
        void Step(string button, int wanted, string position, bool canGoBack, bool canGoOn)
        {
            var pressed = Invoke(editor, button);
            var selected = Until(() => SelectedZoomOf(editor), index => index == wanted, 1);
            var head = Until(() => Playhead(editor), value => Same(value, zooms[wanted].Start + 0.5), 1);
            var text = Until(() => NameOf(editor, "StudioZoomPositionText", 0.5), name => name == position, 1);
            var previous = Until(() => Find(editor, "StudioPreviousZoomButton", 0.5)?.IsEnabled, enabled => enabled == canGoBack, 1);
            var next = Until(() => Find(editor, "StudioNextZoomButton", 0.5)?.IsEnabled, enabled => enabled == canGoOn, 1);
            steps.Add($"\"{text}\" at {Seconds(head)} s");
            if (!pressed || selected != wanted || !Same(head, zooms[wanted].Start + 0.5) || text != position || previous != canGoBack || next != canGoOn)
            {
                wrong.Add($"{button}: pressed {pressed}, zoom {selected + 1} at {Seconds(head)} s, \"{text}\", Previous enabled {previous}, Next enabled {next}");
            }
        }

        // None is selected and the playhead is in the second zoom: Next is that zoom, the one at the playhead.
        // The keyboard focus is on the button that is pressed, as when it is pressed with the keyboard.
        var both = Find(editor, "StudioPreviousZoomButton")?.IsEnabled == true && Find(editor, "StudioNextZoomButton")?.IsEnabled == true;
        var focus = new List<string> { FocusOn(editor, "StudioNextZoomButton") };
        Step("StudioNextZoomButton", 1, "Zoom 2 of 3", canGoBack: true, canGoOn: true);
        focus.Add(FocusedId(editor));
        Step("StudioNextZoomButton", 2, "Zoom 3 of 3", canGoBack: true, canGoOn: false);
        focus.Add(Until(() => FocusedId(editor), id => id == "StudioPreviousZoomButton", 1.5));
        Step("StudioPreviousZoomButton", 1, "Zoom 2 of 3", canGoBack: true, canGoOn: true);
        focus.Add(FocusedId(editor));
        Step("StudioPreviousZoomButton", 0, "Zoom 1 of 3", canGoBack: false, canGoOn: true);
        focus.Add(Until(() => FocusedId(editor), id => id == "StudioNextZoomButton", 1.5));
        _report.Check(
            "Previous and Next step through the zooms and show each where it has moved in; the text between them says which zoom it is, and each button is disabled where there is no zoom to step to",
            both && wrong.Count == 0,
            wrong.Count == 0 ? string.Join(", then ", steps) : string.Join("; ", wrong));
        string[] focusWanted = ["StudioNextZoomButton", "StudioNextZoomButton", "StudioPreviousZoomButton", "StudioPreviousZoomButton", "StudioNextZoomButton"];
        _report.Check(
            "the keyboard focus stays on Next and Previous while they have a zoom to step to, and goes to the other one when the last or the first zoom is reached and the pressed button is switched off",
            focus.SequenceEqual(focusWanted),
            $"the focus was on: {string.Join(", ", focus.Select(id => id.Replace("Studio", string.Empty, StringComparison.Ordinal)))}");
    }

    /// <summary>Undo and Redo across an add, a move and a delete: the lane and the selection follow.</summary>
    private void UndoAndRedoOfZooms(Editor editor, string firstName, string lastName)
    {
        Timeline.Mark("10: undo and redo of zooms");
        var wrong = new List<string>();
        var steps = new List<string>();
        void Expect(string what, string lane)
        {
            var text = WaitForLane(editor, lane);
            steps.Add($"{what}: {text}");
            if (text != lane)
            {
                wrong.Add($"{what}: the lane is {text} and should be {lane}");
            }
        }

        // Add: between the two zooms that are left, at 6.0 s. It is three seconds long, and selected.
        SetSlider(editor, "StudioPlayhead", 6.0);
        Key(editor, StudioShortcutKey.Z);
        Expect("add", $"{firstName} | *Zoom 2×, 6.0 to 9.0 seconds | {lastName}");

        // Move: half a second later, in two steps inside one gesture, which is what a drag of the block asks for.
        var moved = OnUi(() =>
        {
            var viewModel = editor.Window.ViewModel;
            viewModel.BeginGesture();
            var result = viewModel.MoveZoom(1, 6.25);
            result = viewModel.MoveZoom(result.Index ?? 1, 6.5);
            viewModel.EndGesture();
            return result;
        });
        Expect("move", $"{firstName} | *Zoom 2×, 6.5 to 9.5 seconds | {lastName}");

        // Delete.
        Key(editor, StudioShortcutKey.Delete);
        Expect("delete", $"{firstName} | {lastName}");

        // Back: the deleted zoom returns, and is not selected, because none was when it went.
        Invoke(editor, "StudioUndoButton");
        Expect("Undo of the delete", $"{firstName} | Zoom 2×, 6.5 to 9.5 seconds | {lastName}");
        Find(editor, "StudioZoom_1")?.Select();
        Expect("selected again", $"{firstName} | *Zoom 2×, 6.5 to 9.5 seconds | {lastName}");
        Invoke(editor, "StudioUndoButton");
        Expect("Undo of the move, which keeps the zoom selected", $"{firstName} | *Zoom 2×, 6.0 to 9.0 seconds | {lastName}");
        Invoke(editor, "StudioUndoButton");
        Expect("Undo of the add, which leaves none selected", $"{firstName} | {lastName}");

        // Forward again. Nothing is selected, so nothing becomes selected.
        Invoke(editor, "StudioRedoButton");
        Expect("Redo of the add", $"{firstName} | Zoom 2×, 6.0 to 9.0 seconds | {lastName}");
        Invoke(editor, "StudioRedoButton");
        Expect("Redo of the move", $"{firstName} | Zoom 2×, 6.5 to 9.5 seconds | {lastName}");
        Key(editor, StudioShortcutKey.Y, control: true);
        Expect("what Ctrl+Y runs: the delete again", $"{firstName} | {lastName}");
        Key(editor, StudioShortcutKey.Z, control: true);
        Expect("what Ctrl+Z runs: the delete undone, and no zoom added", $"{firstName} | Zoom 2×, 6.5 to 9.5 seconds | {lastName}");
        _report.Check(
            "Undo and Redo across an add, a move and a delete: the lane shows the zooms of each step, a zoom that is selected stays selected through the undo of its move, and Ctrl+Z still undoes while plain Z adds",
            wrong.Count == 0 && moved is { Changed: true, Index: 1 },
            wrong.Count == 0 ? string.Join("; ", steps) : string.Join("; ", wrong));
    }
}
