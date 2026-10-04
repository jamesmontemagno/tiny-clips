using TinyClips.Core.Models;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Capture;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 9. Light and dark: a picture of the whole window in each theme, with a project open, and one
// of the question on closing. The pictures are saved for a person to look at; the checks only
// make sure each is what its name says.
internal sealed partial class WindowChecks
{
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
        if (OpenReady(NewCameraProject($"Studio in the {name} theme"), $"{name} theme") is not { } editor)
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

        // The window's own surfaces: the header left of Undo, the inspector's edge, and the timeline between the time and Start here.
        var surfaces = Surfaces(editor, sight.Shot);
        var text = TextContrast(editor, sight.Shot, "StudioClipName");
        var isLight = theme == AppTheme.Light;
        _report.Check(
            $"the window in the {name} theme: its root element asks for that theme, its surfaces are {(isLight ? "light" : "dark")} and its text {(isLight ? "dark" : "light")} on them, and the preview shows the project",
            requested == theme.ToString() && surfaces.Count >= 3 && surfaces.All(color => isLight ? Luma(color) > 170 : Luma(color) < 90) && (isLight ? text < -80 : text > 80) && sight.Shown == Both(editor, Frame),
            $"requested theme {requested}; surfaces {string.Join(" ", surfaces)}; the recording's name is {F(Math.Abs(text), "0")} levels {(text < 0 ? "darker" : "lighter")} than what is behind it; {sight.Shown}; saved as {Path.GetFileName(windowPath)} ({sight.Shot.Width}x{sight.Shot.Height})");

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

    /// <summary>The colour of three of the window's own surfaces where nothing is drawn on them.</summary>
    private static List<Rgb> Surfaces(Editor editor, Shot shot)
    {
        var colors = new List<Rgb>();
        if (Find(editor, "StudioUndoButton", 0.5) is { } undo)
        {
            // In the header, left of Undo.
            colors.Add(shot.Color(undo.Bounds.X - shot.ScreenX - (40 * editor.Scale), undo.Bounds.Y - shot.ScreenY + (undo.Bounds.Height / 2.0), 2));
        }

        if (Find(editor, "StudioPaddingSlider", 0.5) is { } slider && Find(editor, "StudioPreview", 0.5) is { } preview)
        {
            // In the inspector, in the margin left of its controls, level with the top of the preview.
            colors.Add(shot.Color(slider.Bounds.X - shot.ScreenX - (8 * editor.Scale), preview.Bounds.Y - shot.ScreenY + 4, 2));
        }

        if (Find(editor, "StudioStartHereButton", 0.5) is { } startHere)
        {
            // In the timeline, left of Start here.
            colors.Add(shot.Color(startHere.Bounds.X - shot.ScreenX - (60 * editor.Scale), startHere.Bounds.Y - shot.ScreenY + (startHere.Bounds.Height / 2.0), 2));
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
