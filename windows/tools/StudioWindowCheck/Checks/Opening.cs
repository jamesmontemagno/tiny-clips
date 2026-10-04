using System.Diagnostics;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 1. Opening: a project with screen and camera, a project with a screen only, and a project
// whose recording is gone.
internal sealed partial class WindowChecks
{
    private void Opening()
    {
        OpenWithCamera();
        OpenScreenOnly();
        OpenWithoutItsRecording();
    }

    private void OpenWithCamera()
    {
        Timeline.Mark("1: open a project with a screen and a camera recording");
        var watch = Stopwatch.StartNew();
        var editor = Open(NewCameraProject("Camera and screen"), "screen and camera");
        var state = WaitLoaded(editor);
        _report.Check(
            "a project with a screen and a camera recording opens in the editor",
            state == "ready",
            state == "ready" ? $"ready {watch.ElapsedMilliseconds} ms after the window service was asked for it" : $"the window is {state}: \"{NameOf(editor, "StudioUnavailableMessage", 0.2)}\"");
        if (state != "ready")
        {
            return;
        }

        // The video starts where the camera starts: frame 6 of the screen, frame 0 of the camera.
        var first = FrameOf(CameraOffset);
        var want = Both(editor, first);
        var sight = LookFor(editor, s => s.Shown == want, 5);
        _report.Check(
            "the preview shows the frame the video starts on, of both clips, where the layout puts them inside the canvas rectangle",
            sight?.Shown == want,
            sight is null ? "no screenshot" : $"{sight.Shown} (expected {want}); canvas {sight.Canvas} in a screenshot of {sight.Shot.Width}x{sight.Shot.Height}");
        if (sight is null)
        {
            return;
        }

        var bounds = Native.FrameBounds(editor.Handle);
        _report.Note($"a screenshot is the window as the system draws it: {sight.Shot.Width}x{sight.Shot.Height}, and the window's frame is {bounds.Width}x{bounds.Height}; display scale {F(editor.Scale * 100, "0")} %");

        var name = NameOf(editor, "StudioClipName");
        var sizeText = NameOf(editor, "StudioExportSizeText");
        _report.Check(
            "the header names the recording and says what size it exports at",
            name == "Camera and screen" && sizeText == StudioEditorText.GetExportSizeText(StudioExportLimits.GetExportSize(editor.Expected)),
            $"\"{name}\", \"{sizeText}\"");

        CanvasIsWhereItShouldBe(editor, sight);
        HandleIsOnTheCamera(editor, sight, want.Camera);

        // This window was told it is the active one, as a window the app has just opened is.
        var focused = Until(() => FocusedId(editor), id => id == "StudioPlayPauseButton", 2);
        _report.Check("in the active window the keyboard focus is on Play once the project has opened", focused == "StudioPlayPauseButton", $"focus is on \"{focused}\"");

        CameraIsUnderTheHandle(editor, first);
        CloseQuietly(editor);
    }

    /// <summary>The picture ends at the canvas rectangle, and its corners are the two colours of the default background.</summary>
    private void CanvasIsWhereItShouldBe(Editor editor, Sight sight)
    {
        var (shot, canvas) = (sight.Shot, sight.Canvas);
        var background = editor.Expected.Canvas.Background;
        var from = Hex(background.Primary);
        var to = Hex(background.Secondary ?? background.Primary);

        var topLeft = shot.Color(canvas.X + 3, canvas.Y + 3);
        var bottomRight = shot.Color(canvas.Right - 4, canvas.Bottom - 4);
        _report.Check(
            "the canvas shows the default background, ocean: its first colour in the top left corner, its second in the bottom right",
            Near(topLeft, GradientAt(from, to, canvas, canvas.X + 3, canvas.Y + 3)) && Near(bottomRight, GradientAt(from, to, canvas, canvas.Right - 4, canvas.Bottom - 4)),
            $"top left {topLeft} (asked for {from}), bottom right {bottomRight} (asked for {to})");

        // At the middle of each edge: just inside is the background, just outside is the window.
        (string Name, double InX, double InY, double OutX, double OutY)[] edges =
        [
            ("left", canvas.X + 2, canvas.Y + (canvas.Height / 2.0), canvas.X - 3, canvas.Y + (canvas.Height / 2.0)),
            ("right", canvas.Right - 3, canvas.Y + (canvas.Height / 2.0), canvas.Right + 2, canvas.Y + (canvas.Height / 2.0)),
            ("top", canvas.X + (canvas.Width / 2.0), canvas.Y + 2, canvas.X + (canvas.Width / 2.0), canvas.Y - 3),
            ("bottom", canvas.X + (canvas.Width / 2.0), canvas.Bottom - 3, canvas.X + (canvas.Width / 2.0), canvas.Bottom + 2),
        ];
        var wrong = new List<string>();
        foreach (var (edge, inX, inY, outX, outY) in edges)
        {
            var inside = shot.Color(inX, inY);
            var outside = shot.Color(outX, outY);
            var expected = GradientAt(from, to, canvas, inX, inY);
            if (!Near(inside, expected) || outside.R < 0 || outside.Distance(expected) < 30)
            {
                wrong.Add($"{edge}: inside {inside} (expected {expected}), outside {outside}");
            }
        }

        _report.Check(
            "the picture fills the canvas rectangle and ends at its edges: at each edge the background is just inside, and the window just outside",
            wrong.Count == 0,
            wrong.Count == 0 ? $"canvas {canvas}" : string.Join("; ", wrong));
    }

    /// <summary>The handle that moves the camera is where the layout puts the camera, and the camera's own frame number reads through it.</summary>
    private void HandleIsOnTheCamera(Editor editor, Sight sight, int cameraFrame)
    {
        var handle = HandleRect(editor, sight.Shot);
        var (_, camera) = Layers(sight.Canvas, editor.Expected);
        if (handle is not { } rect || camera is not { } layer)
        {
            _report.Check("the overlay's camera handle is on the camera", false, handle is null ? "the handle is hidden" : "the layout has no camera");
            return;
        }

        var apart = EdgeDistance(rect, layer.Rect);
        var through = FrameCode.Decode(sight.Shot.Bgra, sight.Shot.Width, sight.Shot.Height, editor.Folder.Camera!, ClipMap.ForLayer(editor.Folder.Camera!, rect, layer.Source, layer.Mirror));
        _report.Check(
            "the overlay's camera handle is where the layout puts the camera, and the camera's frame number reads from the pixels inside the handle's rectangle",
            apart <= 1.5 && through == cameraFrame,
            $"handle {R(rect)}, the layout's camera {R(layer.Rect)}, no edge more than {F(apart, "0.##")} px apart; camera frame read inside the handle: {through}{(apart <= 1.5 ? string.Empty : "; in effective pixels: " + HandleNumbers(editor))}");
    }

    /// <summary>
    /// Where the camera really is in the picture, from pixels alone: the camera is made a plain
    /// rectangle without a shadow and then hidden, and what differs between the two screenshots is
    /// the camera. The handle has to be that rectangle.
    /// </summary>
    private void CameraIsUnderTheHandle(Editor editor, int frame)
    {
        Timeline.Mark("1: the camera's place in the picture, from what changes when it is hidden");
        var set = SetSlider(editor, "StudioCameraShadowSlider", 0) & SetCombo(editor, "StudioCameraShapeComboBox", (int)StudioCameraShape.Rectangle);
        Expect(editor, p => p with { Camera = p.Camera with { Shadow = 0, Shape = StudioCameraShape.Rectangle } });
        var shownWith = Both(editor, frame);
        var with = LookFor(editor, s => s.Shown == shownWith, 4);
        var handle = with is null ? null : HandleRect(editor, with.Shot);

        var hidden = Find(editor, "StudioLayoutScreen")?.Select() ?? false;
        Expect(editor, p => WithScene(p, s => s with { Layout = StudioLayout.Screen }));

        // Gone when the camera's frame number no longer reads where the bubble was.
        var bubbleCamera = with?.View.CameraMap;
        var without = LookFor(
            editor,
            s => s.Shown.Screen == frame
                && (bubbleCamera is not { } map || FrameCode.Decode(s.Shot.Bgra, s.Shot.Width, s.Shot.Height, editor.Folder.Camera!, map) != shownWith.Camera),
            4);
        if (!set || !hidden || with is null || without is null || handle is not { } rect || with.Shown != shownWith)
        {
            _report.Check("the camera in the picture is exactly under the overlay's handle", false, $"camera made a rectangle without shadow: {set} ({with?.Shown}); hidden: {hidden}; handle {(handle is null ? "hidden" : "found")}");
            return;
        }

        var changed = DifferenceBox(with.Shot, without.Shot, with.Canvas);
        var apart = changed is { } box ? EdgeDistance(rect, new StudioFrameRect(box.X, box.Y, box.Width, box.Height)) : double.NaN;
        _report.Check(
            "the camera in the picture is exactly under the overlay's handle: what changes in the picture when the camera is hidden is the handle's rectangle",
            apart <= 2,
            $"handle {R(rect)}; the pixels that changed: {changed?.ToString() ?? "none"}; no edge more than {F(apart, "0.##")} px apart");
    }

    private void OpenScreenOnly()
    {
        // This one is told, as soon as it is open, that the user has gone on to another window.
        Timeline.Mark("1: open a project with a screen recording only, in a window that is not the active one");
        _services.OpensInactive = true;
        Editor editor;
        try
        {
            editor = Open(NewScreenProject("Screen only"), "screen only");
        }
        finally
        {
            _services.OpensInactive = false;
        }

        var began = ForegroundGuard.Elapsed;
        var state = WaitLoaded(editor);
        var want = new Shown(0, FrameCode.Unreadable);
        var sight = state == "ready" ? LookFor(editor, s => s.Shown == want, 5) : null;
        _report.Check(
            "a project with a screen recording only opens, and the preview shows its first frame",
            state == "ready" && sight?.Shown == want,
            state == "ready" ? $"{sight?.Shown}" : $"the window is {state}: \"{NameOf(editor, "StudioUnavailableMessage", 0.2)}\"");
        if (state != "ready" || sight is null)
        {
            return;
        }

        var note = NameOf(editor, "StudioNoCameraNote");
        var help = Find(editor, "StudioPreview")?.HelpText;
        var handle = HandleRect(editor, sight.Shot);
        _report.Check(
            "without a camera the inspector offers no layouts and no camera controls and says why, the preview is described as screen only, and the overlay has no handle",
            note.Length > 0 && editor.Root.Find("StudioLayoutChoice") is null && editor.Root.Find("StudioCameraShapeComboBox") is null && help == StudioEditorModel.GetLayoutName(StudioLayout.Screen) && handle is null,
            $"note \"{note}\"; preview described as \"{help}\"; handle {(handle is null ? "hidden" : "shown")}");
        // Moving the focus asks Windows for the keyboard focus, which brings a window to the front.
        Thread.Sleep(300);
        var requests = ForegroundGuard.Events().Where(e => e.At >= began).ToArray();
        var before = FocusedId(editor);
        Timeline.Mark("1: the user comes back to that window");
        TellActive(editor, isActive: true);
        var after = Until(() => FocusedId(editor), id => id == "StudioPlayPauseButton", 2);
        _report.Check(
            "a window that is not the active one when its project has opened does not ask Windows for the keyboard focus; the focus is put on Play when the user comes back to it",
            requests.Length == 0 && before != "StudioPlayPauseButton" && after == "StudioPlayPauseButton",
            $"{requests.Length} request(s) while it was not active{(requests.Length == 0 ? string.Empty : ": " + string.Join(", ", requests.Select(e => $"{e.What} at {F(e.At, "0.00")} s")))}; focus then on \"{before}\", and on \"{after}\" once the window was active again");
        CloseQuietly(editor);
    }

    private void OpenWithoutItsRecording()
    {
        Timeline.Mark("1: open a project whose screen recording is missing");
        var folder = NewScreenProject("Recording gone", writeScreen: false);
        var editor = Open(folder, "missing recording");
        var state = WaitLoaded(editor);
        var panel = Find(editor, "StudioUnavailablePanel", 0.5);
        var heading = NameOf(editor, "StudioUnavailableHeading", 0.5);
        var message = NameOf(editor, "StudioUnavailableMessage", 0.5);
        _report.Check(
            "a project whose screen recording is missing opens a window that says it cannot be opened, and why",
            state == "unavailable" && heading == "This project can't be opened" && message == StudioEditorSession.MissingRecordingMessage && panel?.Name == $"{heading}. {message}",
            $"the window is {state}; heading \"{heading}\"; message \"{message}\"; the message as one element: \"{panel?.Name}\"");
        _report.Check(
            "and it offers nothing of the editor: no Play, no Export, no preview",
            editor.Root.Find("StudioPlayPauseButton") is null && editor.Root.Find("StudioExportButton") is null && editor.Root.Find("StudioPreview") is null);
        var focused = Until(() => FocusedId(editor), id => id == "StudioUnavailablePanel", 2);
        _report.Check("the message has the keyboard focus, so a screen reader reads it", focused == "StudioUnavailablePanel", $"focus is on \"{focused}\"");

        // Nothing to lose, so the window's close button closes it at once.
        var pressed = PressClose(editor);
        var gone = WindowGone(editor);
        var asked = !gone && editor.Root.Find("StudioClosePromptDeleteButton") is not null;
        Release(editor);
        _report.Check(
            "its close button closes it without a question, and the project folder stays",
            pressed && gone && Directory.Exists(folder.Paths.ProjectDirectory),
            $"close pressed {pressed}, window gone {gone}{(asked ? ", a question was asked" : string.Empty)}, folder exists {Directory.Exists(folder.Paths.ProjectDirectory)}");
    }
}
