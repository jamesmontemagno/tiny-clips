using TinyClips.Core.Studio;
using TinyClips.Tools.StudioWindowCheck.Capture;
using TinyClips.Tools.StudioWindowCheck.Host;
using Windows.Graphics;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 9, continued. The whole window with everything its timeline can hold: a recording with a
// camera that has three scenes, two zooms, two cuts and two speed changes, pictured at the size
// the window opens with and at the smallest size it can be given. The pictures are saved for a
// person to look at; the checks make sure that nothing of the transport row or the timeline is
// cut off or lies over something else, and that the preview is still large enough to work with.
// What the row does without room for the glyphs of its buttons, and how wide it would be at a
// text size of 200%, is in ButtonGlyphs.cs.
internal sealed partial class WindowChecks
{
    // The window's sizes, in effective pixels: what it opens with, and the smallest it can be given.
    private const int DefaultWindowWidth = 1180;
    private const int DefaultWindowHeight = 760;
    private const int SmallestWindowWidth = 980;
    private const int SmallestWindowHeight = 640;

    /// <summary>
    /// Three scenes, each with a layout of its own, the second entered by moving and the third by
    /// a cut; two zooms; two cuts, one of them over the line between two scenes; and two speed
    /// changes, a faster one in the first scene and a slower one in the last, each long enough
    /// for its mark and its rate to be drawn at the window's smallest size too.
    /// </summary>
    private static StudioProject WithScenesZoomsAndCuts(StudioProject project) => project with
    {
        Scenes =
        [
            new StudioScene { Layout = StudioLayout.Bubble, Bubble = new StudioBubble { Size = 0.4 } },
            new StudioScene { Start = 4, Layout = StudioLayout.SideBySide, Bubble = new StudioBubble { Size = 0.4 }, Transition = new StudioTransition { Kind = StudioTransitionKind.Morph } },
            new StudioScene { Start = 8.5, Layout = StudioLayout.Camera, Bubble = new StudioBubble { Size = 0.4 } },
        ],
        Zooms = [PointZoom(1, 3, 0.3, 0.3), PointZoom(5, 7.5, 0.5, 0.5, scale: 1.5)],
        Edits = project.Edits with
        {
            Cuts = [new StudioTimeRange { Start = 3.2, End = 3.8 }, new StudioTimeRange { Start = 8, End = 9.5 }],
            Speed = [Speed(1.5, 2.5, 4), Speed(10, 11.5, 0.5)],
        },
    };

    /// <summary>Asks for a size of the window in effective pixels, and returns the size it then has.</summary>
    private (double Width, double Height) Resize(Editor editor, int width, int height)
    {
        OnUi(() => editor.Window.AppWindow.Resize(new SizeInt32((int)Math.Round(width * editor.Scale), (int)Math.Round(height * editor.Scale))));
        Thread.Sleep(500);
        var size = OnUi(() => editor.Window.AppWindow.Size);
        return (size.Width / editor.Scale, size.Height / editor.Scale);
    }

    private void TimelinePictures(string name, bool isLight, List<string> saved)
    {
        Timeline.Mark($"9: scenes, zooms and cuts, {name}");
        if (OpenReady(NewCameraProject($"Scenes, zooms and cuts in the {name} theme", WithScenesZoomsAndCuts), $"{name} theme, scenes, zooms and cuts") is not { } editor)
        {
            return;
        }

        // In the second scene, with the second cut selected: the scene lane marks one block and
        // the cut lane another.
        const int Frame = 180;
        SetSlider(editor, "StudioPlayhead", MiddleOf(Frame));
        var cut = LaneItems(editor, CutLane) is { Count: 2 } cuts && cuts[1].Select();
        LookForLayout(editor, Frame, 5);

        PictureOfTimeline(editor, name, "the size it opens with", $"window-timeline-{name}.png", DefaultWindowWidth, DefaultWindowHeight, DefaultWindowWidth, DefaultWindowHeight, Frame, cut, saved);

        // Asked to be far smaller than it may be, the window takes its smallest size.
        Timeline.Mark($"9: the smallest window, {name}");
        PictureOfTimeline(editor, name, "its smallest size", $"window-timeline-{name}-smallest.png", 400, 300, SmallestWindowWidth, SmallestWindowHeight, Frame, cut, saved);
        GlyphsGiveWay(editor, name, saved);
        ButtonPictures(editor, name, saved);
        CloseQuietly(editor);
    }

    private void PictureOfTimeline(Editor editor, string name, string size, string fileName, int askedWidth, int askedHeight, int wantedWidth, int wantedHeight, int frame, bool cutSelected, List<string> saved)
    {
        var (width, height) = Resize(editor, askedWidth, askedHeight);
        var sight = LookForLayout(editor, frame, 5);
        Thread.Sleep(300);
        sight = LookAtLayout(editor, frame) ?? sight;
        if (sight is null)
        {
            _report.Check($"the window with scenes, zooms, cuts and speed changes in the {name} theme at {size} can be pictured", false, "no screenshot");
            return;
        }

        var path = Path.Combine(_output, fileName);
        sight.Shot.Save(path);
        saved.Add(path);

        // Everything of the transport row and the timeline, as UI Automation places it on the screen.
        var window = (X: (double)sight.Shot.ScreenX, Y: (double)sight.Shot.ScreenY, Right: (double)sight.Shot.ScreenX + sight.Shot.Width, Bottom: (double)sight.Shot.ScreenY + sight.Shot.Height);
        string[] row = ["StudioPlayPauseButton", "StudioPreviousFrameButton", "StudioNextFrameButton", "StudioTimeText", "StudioSplitSceneButton", "StudioAddZoomButton", "StudioAddCutButton", "StudioAddSpeedButton", "StudioStartHereButton", "StudioEndHereButton"];
        string[] lanes = [SceneLane, ZoomLane, CutLane, SpeedLane, "StudioTrimBar"];
        var wrong = new List<string>();
        (string Id, int X, int Y, int Width, int Height)[] Placed(string[] ids) => [.. ids.Select(id => (Id: id, Bounds: Find(editor, id, 0.5)?.Bounds ?? default)).Select(found => (found.Id, found.Bounds.X, found.Bounds.Y, found.Bounds.Width, found.Bounds.Height))];
        void Inside((string Id, int X, int Y, int Width, int Height) element)
        {
            if (element.Width <= 0 || element.Height <= 0 || element.X < window.X || element.Y < window.Y || element.X + element.Width > window.Right || element.Y + element.Height > window.Bottom)
            {
                wrong.Add($"{element.Id} is not whole inside the window: {element.Width}x{element.Height} at ({element.X},{element.Y})");
            }
        }

        var inRow = Placed(row);
        foreach (var element in inRow)
        {
            Inside(element);
        }

        for (var index = 1; index < inRow.Length; index++)
        {
            var (before, after) = (inRow[index - 1], inRow[index]);
            if (after.X < before.X + before.Width)
            {
                wrong.Add($"{after.Id} starts at x {after.X}, before {before.Id} ends at x {before.X + before.Width}");
            }

            // One row: every item's middle is level with the first one's.
            if (Math.Abs((after.Y + (after.Height / 2.0)) - (inRow[0].Y + (inRow[0].Height / 2.0))) > 3 * editor.Scale)
            {
                wrong.Add($"{after.Id} is not on the row of {inRow[0].Id}");
            }
        }

        var stacked = Placed(lanes);
        foreach (var element in stacked)
        {
            Inside(element);
            if (element.X != stacked[^1].X || element.Width != stacked[^1].Width)
            {
                wrong.Add($"{element.Id} is {element.Width} wide at x {element.X}, and the trim bar {stacked[^1].Width} at x {stacked[^1].X}");
            }
        }

        for (var index = 1; index < stacked.Length; index++)
        {
            if (stacked[index].Y < stacked[index - 1].Y + stacked[index - 1].Height)
            {
                wrong.Add($"{stacked[index].Id} starts at y {stacked[index].Y}, above where {stacked[index - 1].Id} ends");
            }
        }

        if (stacked[0].Y < inRow[0].Y + inRow[0].Height)
        {
            wrong.Add("the first lane starts above the end of the transport row");
        }

        // The header's controls, which have to fit beside the recording's name.
        foreach (var element in Placed(["StudioClipName", "StudioUndoButton", "StudioRedoButton", "StudioCanvasComboBox", "StudioExportButton"]))
        {
            Inside(element);
        }

        var canvas = (Width: sight.Canvas.Width / editor.Scale, Height: sight.Canvas.Height / editor.Scale);
        var layout = JudgeLayout(sight.Reading);
        var sceneLane = LaneText(editor, SceneLane);
        var zoomLane = LaneText(editor, ZoomLane);
        var cutLane = LaneText(editor, CutLane);
        var speedLane = LaneText(editor, SpeedLane);
        _report.Check(
            $"the window with three scenes, two zooms, two cuts and two speed changes in the {name} theme at {size}, {wantedWidth} × {wantedHeight}: the ten items of the transport row are on one row in their order, the four lanes and the trim bar are one above the other and equally wide, all of it and the header's controls whole inside the window, and the preview shows the playhead's frame as its scene has it, on a canvas at least 400 wide and 225 high",
            Math.Abs(width - wantedWidth) <= 1 && Math.Abs(height - wantedHeight) <= 1 && wrong.Count == 0 && layout is null && cutSelected && canvas.Width >= 400 && canvas.Height >= 225
                && LaneItems(editor, SceneLane).Count == 3 && LaneItems(editor, ZoomLane).Count == 2 && LaneItems(editor, CutLane).Count == 2 && LaneItems(editor, SpeedLane).Count == 2,
            $"the window is {F(width, "0")} × {F(height, "0")} effective pixels; the canvas {F(canvas.Width, "0")} × {F(canvas.Height, "0")}; "
                + (wrong.Count == 0 ? $"the row runs from x {inRow[0].X} to x {inRow[^1].X + inRow[^1].Width} and the lanes from y {stacked[0].Y} to y {stacked[^1].Y + stacked[^1].Height}, in a window from ({window.X:0},{window.Y:0}) to ({window.Right:0},{window.Bottom:0})" : string.Join("; ", wrong))
                + $"; {layout ?? "the preview shows " + sight.Reading}; scenes: {sceneLane}; zooms: {zoomLane}; cuts: {cutLane}; speed changes: {speedLane}; saved as {fileName} ({sight.Shot.Width}x{sight.Shot.Height})");

        // The sums are the same in both themes: once, at the smallest size.
        if (wantedWidth == SmallestWindowWidth && !_rowWidthsNoted)
        {
            _rowWidthsNoted = true;
            _report.Note(RowWidths(editor, row, width));
        }
    }
}
