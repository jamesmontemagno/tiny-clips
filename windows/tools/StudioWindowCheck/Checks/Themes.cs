using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TinyClips.Core.Models;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Capture;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 9. Light and dark: pictures of the whole window in each theme, with a project open: as it
// opens, with a zoom selected on the lane, with the Zoom panel of the inspector in view, and
// with the question on closing. The pictures are saved for a person to look at; the checks only
// make sure each is what its name says.
internal sealed partial class WindowChecks
{
    /// <summary>
    /// Three zooms for the pictures, none of them at the frame the pictures are taken at: one that
    /// looks at a point, one that follows the pointer, and one that was suggested, so that both
    /// marks a block can carry are in the picture.
    /// </summary>
    private static StudioZoom[] ZoomsForPictures() =>
    [
        PointZoom(0.5, 2.5, 0.3, 0.3),
        PointZoom(5, 7.5, 0.5, 0.5, scale: 1.5) with { Focus = new StudioZoomFocus { Mode = StudioZoomFocusMode.Cursor, X = 0.5, Y = 0.5 } },
        PointZoom(9, 11, 0.7, 0.6, scale: 3) with { Origin = StudioZoomOrigin.Auto },
    ];

    private void Themes()
    {
        var saved = new List<string>();
        try
        {
            Themed(AppTheme.Light, "light", saved);
            Themed(AppTheme.Dark, "dark", saved);
        }
        finally
        {
            _services.Settings.Theme = AppTheme.Default;
        }

        foreach (var path in saved)
        {
            _report.Line($"  saved {path}");
        }
    }

    private void Themed(AppTheme theme, string name, List<string> saved)
    {
        Timeline.Mark($"9: the {name} theme");

        // The theme is the app's setting, which the window applies to its own root element when it opens.
        _services.Settings.Theme = theme;
        if (OpenReady(NewCameraProject($"Studio in the {name} theme", p => p with { Zooms = ZoomsForPictures() }), $"{name} theme") is not { } editor)
        {
            return;
        }

        const int Frame = 90;
        SetSlider(editor, "StudioPlayhead", MiddleOf(Frame));
        var sight = LookFor(editor, s => s.Shown == Both(editor, Frame), 5);
        Thread.Sleep(400);
        sight = sight is null ? null : Look(editor) ?? sight;
        var requested = OnUi(() => (editor.Window.Content as Microsoft.UI.Xaml.FrameworkElement)?.RequestedTheme.ToString() ?? string.Empty);
        if (sight is null)
        {
            _report.Check($"the window in the {name} theme can be pictured", false, "no screenshot");
            CloseQuietly(editor);
            return;
        }

        var windowPath = Path.Combine(_output, $"window-{name}.png");
        sight.Shot.Save(windowPath);
        saved.Add(windowPath);

        // The window's own surfaces: the header left of Undo, the inspector's edge, and the timeline between the time and Split.
        var surfaces = Surfaces(editor, sight.Shot);
        var text = TextContrast(editor, sight.Shot, "StudioClipName");
        var isLight = theme == AppTheme.Light;
        _report.Check(
            $"the window in the {name} theme: its root element asks for that theme, its surfaces are {(isLight ? "light" : "dark")} and its text {(isLight ? "dark" : "light")} on them, and the preview shows the project",
            requested == theme.ToString() && surfaces.Count >= 3 && surfaces.All(color => isLight ? Luma(color) > 170 : Luma(color) < 90) && (isLight ? text < -80 : text > 80) && sight.Shown == Both(editor, Frame),
            $"requested theme {requested}; surfaces {string.Join(" ", surfaces)}; the recording's name is {F(Math.Abs(text), "0")} levels {(text < 0 ? "darker" : "lighter")} than what is behind it; {sight.Shown}; saved as {Path.GetFileName(windowPath)} ({sight.Shot.Width}x{sight.Shot.Height})");

        ZoomPictures(editor, name, isLight, Frame, saved);
        ProjectSectionPicture(editor, name, isLight, saved);
        PanelPictures(editor, name, saved);
        TimelinePictures(name, isLight, saved);
        CannotBeShownPicture(name, isLight, saved);

        // The question on closing, once it has finished opening.
        Timeline.Mark($"9: the question on closing, {name}");
        PressClose(editor);
        var question = Dialog(editor);
        Thread.Sleep(600);

        // The question as an element is as large as the window. Its card is where its text scrolls.
        var settled = question?.Find("ContentScrollViewer")?.Bounds;
        var shot = editor.Camera.Take();
        if (question is null || shot is null || settled is not { } bounds)
        {
            _report.Check($"the question on closing in the {name} theme can be pictured", false, question is null ? "no question appeared" : "no screenshot");
        }
        else
        {
            var dialogPath = Path.Combine(_output, $"close-dialog-{name}.png");
            shot.Save(dialogPath);
            saved.Add(dialogPath);

            // Inside the card, near its top right corner, where nothing is written; and the window behind it, dimmed.
            var panel = shot.Color(bounds.X + bounds.Width - shot.ScreenX - (8 * editor.Scale), bounds.Y - shot.ScreenY + (8 * editor.Scale), 2);
            var behind = Surfaces(editor, shot);
            var dimmed = behind.Count > 0 && surfaces.Count > 0 && Luma(behind[0]) < Luma(surfaces[0]) - (isLight ? 30 : 3);
            _report.Check(
                $"the question on closing in the {name} theme is {(isLight ? "light" : "dark")} like its window, which is dimmed behind it",
                (isLight ? Luma(panel) > 170 : Luma(panel) < 90) && dimmed,
                $"the question's surface {panel}; the window's header behind it {(behind.Count > 0 ? behind[0].ToString() : "?")}, {(surfaces.Count > 0 ? surfaces[0].ToString() : "?")} before; saved as {Path.GetFileName(dialogPath)}");
        }

        question?.Find("CloseButton")?.Invoke();
        Until(() => !HasDialog(editor), gone => gone, 3);
        CloseQuietly(editor);
    }

    /// <summary>
    /// A picture of the window with each panel of the inspector on show in turn, from its top,
    /// for a person to look at: every panel is laid out for the first time when it is first
    /// shown. The check only says that each picture is of the panel its name says: the panel's
    /// name is over the inspector, and a control the panel always has is in the window. What a
    /// panel looks like is not judged.
    /// </summary>
    private void PanelPictures(Editor editor, string name, List<string> saved)
    {
        Timeline.Mark($"9: each panel of the inspector, {name}");
        var was = OnUi(() => editor.Window.ViewModel.InspectorPanel);
        var panels = OnUi(() => StudioInspectorPanels.GetAvailable(editor.Window.ViewModel.HasCamera).ToArray());
        var (pictured, wrong) = (new List<string>(), new List<string>());
        foreach (var panel in panels)
        {
            var title = StudioInspectorPanels.GetTitle(panel);
            ShowPanel(editor, panel);

            // A crop group that has just come on show is still opening.
            Thread.Sleep(450);
            var shown = PanelShown(editor);
            var anchor = editor.Root.FindAsItIs(PanelAnchors.First(entry => entry.Panel == panel).Id);
            if (editor.Camera.Take() is not { } shot)
            {
                wrong.Add($"{title}: no screenshot");
                continue;
            }

            var path = Path.Combine(_output, $"panel-{panel.ToString().ToLowerInvariant()}-{name}.png");
            shot.Save(path);
            saved.Add(path);
            pictured.Add(title);
            if (!IsShown(shown, title) || anchor is not { IsOffscreen: false })
            {
                wrong.Add($"{title}: {PanelWords(shown)}; a control it always has is {(anchor is null ? "not there" : anchor.IsOffscreen ? "out of view" : "there")}");
            }
        }

        ShowPanel(editor, was);
        _report.Check(
            $"each panel of the inspector in the {name} theme is pictured with its own name over it and a control it always has in the window, for a person to look at",
            pictured.Count == panels.Length && wrong.Count == 0,
            wrong.Count == 0 ? $"{string.Join(", ", pictured)}; saved as panel-<panel>-{name}.png" : string.Join("; ", wrong));
    }

    /// <summary>
    /// More pictures of a window whose project has three zooms: the whole window with the
    /// middle zoom selected on the lane, and the inspector's Zoom panel.
    /// </summary>
    private void ZoomPictures(Editor editor, string name, bool isLight, int frame, List<string> saved)
    {
        Timeline.Mark($"9: the lane with a zoom selected, {name}");
        var zooms = ZoomsForPictures();
        var names = zooms.Select(StudioEditorText.GetZoomDescription).ToArray();
        var wanted = $"{names[0]} | *{names[1]} | {names[2]}";

        // Selecting through the list item selects without moving the playhead, so the preview stays as it was.
        var selected = LaneItems(editor) is { Count: 3 } items && items[1].Select();
        var lane = WaitForLane(editor, wanted);
        Thread.Sleep(400);
        var sight = Look(editor);
        if (sight is null)
        {
            _report.Check($"the window with zooms in the {name} theme can be pictured", false, "no screenshot");
            return;
        }

        var path = Path.Combine(_output, $"window-zooms-{name}.png");
        sight.Shot.Save(path);
        saved.Add(path);

        // Each block: where it is, the colour of its fill left of its text, and whether something is written in its middle.
        var laneBox = Find(editor, "StudioZoomLane")?.Bounds ?? default;
        var blocks = LaneItems(editor).Select(item =>
        {
            var (x, y, width, height) = item.Bounds;
            var (left, top) = (x - sight.Shot.ScreenX, y - sight.Shot.ScreenY);
            var fill = sight.Shot.Color(left + (8 * editor.Scale), top + (height / 2.0), 1);
            var written = 0;
            for (var row = top + 4; row < top + height - 4; row++)
            {
                for (var column = left + (width / 2) - (int)(30 * editor.Scale); column < left + (width / 2) + (int)(30 * editor.Scale); column++)
                {
                    if (sight.Shot.Color(column, row, 0) is { R: >= 0 } color && color.Distance(fill) > 80)
                    {
                        written++;
                    }
                }
            }

            return (Left: x, Right: x + width, Top: y, Bottom: y + height, Fill: fill, Written: written);
        }).ToArray();
        var inLane = blocks.Length == 3
            && blocks.All(block => block.Left >= laneBox.X && block.Right <= laneBox.X + laneBox.Width && block.Top >= laneBox.Y && block.Bottom <= laneBox.Y + laneBox.Height)
            && blocks[0].Right <= blocks[1].Left
            && blocks[1].Right <= blocks[2].Left;
        var told = blocks.Length == 3
            && blocks[0].Fill.Distance(blocks[2].Fill) < 12
            && blocks[1].Fill.Distance(blocks[0].Fill) > 40
            && blocks.All(block => block.Written >= 12);
        _report.Check(
            $"the window in the {name} theme with three zooms: the lane shows three blocks in the order of their times, each with something written on it, the selected one filled differently from the other two, and the preview is as it was",
            selected && lane == wanted && inLane && told && sight.Shown == Both(editor, frame),
            $"the lane: {lane}; blocks at {string.Join(", ", blocks.Select(block => $"{block.Left} to {block.Right}"))} in a lane from {laneBox.X} to {laneBox.X + laneBox.Width}; fills {string.Join(" ", blocks.Select(block => block.Fill))}; pixels of writing {string.Join(" ", blocks.Select(block => block.Written))}; {sight.Shown}; saved as {Path.GetFileName(path)}");

        // The Zoom panel, with the first zoom selected: it looks at a point, so the focus pad
        // and its two sliders are there. The panel is higher than the inspector, so it is
        // pictured three times: from its top down, from the focus pad, and from its end up.
        Timeline.Mark($"9: the Zoom panel, {name}");
        var first = LaneItems(editor) is { Count: 3 } again && again[0].Select();
        var laneThen = WaitForLane(editor, $"*{names[0]} | {names[1]} | {names[2]}");
        var fromHeading = PictureOfZoomSection(editor, section => section.Top - 8, "StudioZoomScaleSlider", Path.Combine(_output, $"zoom-section-{name}.png"), saved);
        var fromPad = fromHeading is null ? null : PictureOfZoomSection(editor, section => section.Pad - 8, "StudioZoomFocusXSlider", Path.Combine(_output, $"zoom-section-{name}-2.png"), saved);
        var fromEnd = fromPad is null ? null : PictureOfZoomSection(editor, section => section.Bottom - section.Viewport + 16, "StudioDeleteZoomButton", Path.Combine(_output, $"zoom-section-{name}-3.png"), saved);
        if (fromHeading is not { } top || fromPad is not { } middle || fromEnd is not { } end)
        {
            _report.Check($"the Zoom panel in the {name} theme can be pictured", false, "the inspector's scroll viewer, its Zoom panel or the focus pad was not found, or there was no screenshot");
        }
        else
        {
            static string Short(string[] ids) => ids.Length == 0 ? "none" : string.Join(", ", ids.Select(id => id.Replace("Studio", string.Empty, StringComparison.Ordinal)));
            var inNone = top.Outside.Intersect(middle.Outside).Intersect(end.Outside).ToArray();
            _report.Check(
                $"the Zoom panel in the {name} theme with a zoom selected: three pictures show the inspector, which is {(isLight ? "light" : "dark")}, from the panel's top, from the focus pad and from the panel's end, and every control of the panel, the focus pad among them, is whole in one of them",
                first && top.Position == "Zoom 1 of 3" && inNone.Length == 0 && top.HeadingAtTop && !middle.Outside.Contains("StudioZoomFocusPad")
                    && new[] { top.Surface, middle.Surface, end.Surface }.All(surface => isLight ? Luma(surface) > 170 : Luma(surface) < 90),
                $"the lane: {laneThen}; \"{top.Position}\"; the inspector shows {R(top.Viewport)} of the window, its surface is {top.Surface}, {middle.Surface} and {end.Surface}; "
                    + $"{(inNone.Length == 0 ? "every control is whole in one of the three pictures" : "whole in none of the pictures: " + string.Join(", ", inNone))}; "
                    + $"not in the first: {Short(top.Outside)}; not in the second: {Short(middle.Outside)}; not in the third: {Short(end.Outside)}; "
                    + $"saved as zoom-section-{name}.png, zoom-section-{name}-2.png and zoom-section-{name}-3.png");
        }

        ScrollInspector(editor, _ => 0);
    }

    /// <summary>What a picture of the inspector's Zoom panel shows.</summary>
    /// <param name="Outside">The controls of the panel that are not whole inside the inspector's viewport.</param>
    /// <param name="Surface">The colour of the inspector's own surface, in the margin left of a control.</param>
    private sealed record SectionPicture(string[] Outside, Rgb Surface, string Position, StudioFrameRect Viewport, bool HeadingAtTop);

    /// <summary>
    /// Scrolls the inspector to a place in its Zoom panel and saves a picture of the window.
    /// Null without a picture.
    /// </summary>
    /// <param name="surfaceBeside">A control of the panel that is in the picture: the inspector's surface is read in the margin left of it.</param>
    private SectionPicture? PictureOfZoomSection(Editor editor, Func<(double Top, double Bottom, double Viewport, double Pad), double> offset, string surfaceBeside, string path, List<string> saved)
    {
        string[] all =
        [
            "StudioPreviousZoomButton", "StudioZoomPositionText", "StudioZoomRangeText", "StudioNextZoomButton", "StudioZoomSectionAddButton", "StudioSuggestZoomsButton",
            "StudioZoomScaleSlider", "StudioZoomFocusPoint", "StudioZoomFocusPointer", "StudioZoomFocusPad", "StudioZoomFocusXSlider", "StudioZoomFocusYSlider",
            "StudioZoomStartText", "StudioZoomStartEarlierButton", "StudioZoomStartLaterButton", "StudioZoomStartAtPlayheadButton",
            "StudioZoomEndText", "StudioZoomEndEarlierButton", "StudioZoomEndLaterButton", "StudioZoomEndAtPlayheadButton",
            "StudioZoomEaseInSlider", "StudioZoomEaseOutSlider", "StudioDeleteZoomButton",
        ];
        var viewport = ScrollInspector(editor, offset);
        Thread.Sleep(500);
        if (viewport is not { } view || editor.Camera.Take() is not { } shot)
        {
            return null;
        }

        shot.Save(path);
        saved.Add(path);
        var box = InShot(editor, shot, view);
        bool Within(double left, double upper, double width, double height) =>
            width > 0 && height > 0 && left >= box.X - 1 && upper >= box.Y - 1 && left + width <= box.X + box.Width + 1 && upper + height <= box.Y + box.Height + 1;
        bool Inside(string id)
        {
            if (id == "StudioZoomFocusPad")
            {
                // The pad is not an element for UI Automation. Where it is comes from the window's own elements.
                var pad = OnUi<Windows.Foundation.Rect?>(() => Descendant<TinyClips.App.Controls.Studio.StudioFocusPad>(editor.Window.Content, id) is { ActualHeight: > 0 } element
                    ? element.TransformToVisual(editor.Window.Content).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight))
                    : null);
                return pad is { } rect && PadShows(editor) && InShot(editor, shot, rect) is var at && Within(at.X, at.Y, at.Width, at.Height);
            }

            if (editor.Root.Find(id) is not { IsOffscreen: false } found)
            {
                return false;
            }

            var (x, y, width, height) = found.Bounds;
            return Within(x - shot.ScreenX, y - shot.ScreenY, width, height);
        }

        // The inspector's own surface, in the margin left of the panel's controls.
        var surface = editor.Root.Find(surfaceBeside) is { } anchor
            ? shot.Color(anchor.Bounds.X - shot.ScreenX - (8 * editor.Scale), anchor.Bounds.Y - shot.ScreenY + (anchor.Bounds.Height / 2.0), 2)
            : new Rgb(-1, -1, -1);

        // The panel's first row is a few pixels under the top of the inspector's viewport when the panel was scrolled to its top.
        var headingAtTop = editor.Root.Find("StudioPreviousZoomButton") is { } stepper && stepper.Bounds.Y - shot.ScreenY - box.Y is > 0 and < 110 * 1.5;
        return new SectionPicture([.. all.Where(id => !Inside(id))], surface, NameOf(editor, "StudioZoomPositionText", 0.5), box, headingAtTop);
    }
    /// <summary>
    /// Shows the inspector's Zoom panel and scrolls it, without an animation, to an offset
    /// worked out from where the panel and its focus pad are. Returns the scroll viewer's
    /// rectangle in the window's content, or null when either was not found.
    /// </summary>
    private Windows.Foundation.Rect? ScrollInspector(Editor editor, Func<(double Top, double Bottom, double Viewport, double Pad), double> offset) => OnUi<Windows.Foundation.Rect?>(() =>
    {
        if (Descendant<Button>(editor.Window.Content, "StudioPreviousZoomButton") is not { } button)
        {
            return null;
        }

        FrameworkElement? section = null;
        ScrollViewer? scroller = null;
        for (DependencyObject? at = button; at is not null && scroller is null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is FrameworkElement { Name: "ZoomPanel" } found)
            {
                section = found;
            }

            scroller = at as ScrollViewer;
        }

        if (section is null || scroller?.Content is not UIElement content)
        {
            return null;
        }

        var top = section.TransformToVisual(content).TransformPoint(default).Y;

        // Where the focus pad starts, when it is shown: the panel's own start otherwise.
        var pad = Descendant<TinyClips.App.Controls.Studio.StudioFocusPad>(section, "StudioZoomFocusPad") is { ActualHeight: > 0 } shown
            ? shown.TransformToVisual(content).TransformPoint(default).Y
            : top;
        var wanted = offset((top, top + section.ActualHeight, scroller.ViewportHeight, pad));
        scroller.ChangeView(null, Math.Clamp(wanted, 0, scroller.ScrollableHeight), null, disableAnimation: true);
        scroller.UpdateLayout();
        return scroller.TransformToVisual(editor.Window.Content).TransformBounds(new Windows.Foundation.Rect(0, 0, scroller.ActualWidth, scroller.ActualHeight));
    });

    /// <summary>The colour of three of the window's own surfaces where nothing is drawn on them.</summary>
    private static List<Rgb> Surfaces(Editor editor, Shot shot)
    {
        var colors = new List<Rgb>();
        if (Find(editor, "StudioUndoButton", 0.5) is { } undo)
        {
            // In the header, left of Undo.
            colors.Add(shot.Color(undo.Bounds.X - shot.ScreenX - (40 * editor.Scale), undo.Bounds.Y - shot.ScreenY + (undo.Bounds.Height / 2.0), 2));
        }

        if ((Find(editor, "StudioPreviousSceneButton", 0.5) ?? Find(editor, "StudioNoCameraNote", 0.5)) is { } first && Find(editor, "StudioPreview", 0.5) is { } preview)
        {
            // In the inspector, in the margin left of its first control, level with the top of the preview.
            colors.Add(shot.Color(first.Bounds.X - shot.ScreenX - (8 * editor.Scale), preview.Bounds.Y - shot.ScreenY + 4, 2));
        }

        if ((Find(editor, "StudioSplitSceneButton", 0.2) ?? Find(editor, "StudioAddZoomButton", 0.5)) is { } button)
        {
            // In the timeline, left of the first of the buttons on its right: Split, or Add zoom for a recording without a camera.
            colors.Add(shot.Color(button.Bounds.X - shot.ScreenX - (60 * editor.Scale), button.Bounds.Y - shot.ScreenY + (button.Bounds.Height / 2.0), 2));
        }

        return colors;
    }

    /// <summary>
    /// How much the darkest or lightest pixel of a text differs from what is behind it, in levels
    /// of brightness: negative for dark text on light, positive for light text on dark.
    /// </summary>
    private static double TextContrast(Editor editor, Shot shot, string automationId)
    {
        if (Find(editor, automationId, 0.5) is not { } text)
        {
            return 0;
        }

        var (x, y, width, height) = text.Bounds;
        var (left, top) = (x - shot.ScreenX, y - shot.ScreenY);
        var background = Luma(shot.Color(left + Math.Min(width, 400) + 6, top + (height / 2.0), 1));
        double darkest = 255, lightest = 0;
        for (var row = top + 2; row < top + height - 2; row++)
        {
            for (var column = left; column < left + Math.Min(width, 200); column++)
            {
                var value = Luma(shot.Color(column, row, 0));
                if (value >= 0)
                {
                    darkest = Math.Min(darkest, value);
                    lightest = Math.Max(lightest, value);
                }
            }
        }

        return background - darkest > lightest - background ? darkest - background : lightest - background;
    }
}
