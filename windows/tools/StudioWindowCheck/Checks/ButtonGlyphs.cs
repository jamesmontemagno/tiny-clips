using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TinyClips.App.Controls.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioWindowCheck.Host;
using Windows.Foundation;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 9, continued. The glyphs before the words of the editor's buttons give way where there is no
// room for them, as at a large text size. The text size is a setting of Windows this tool does
// not change, so the room is taken away instead: the timeline, the two buttons side by side in
// the Zoom panel and one button of a panel are each given less width than their glyphs need.
// A note says, as a sum, how wide the timeline's row would be at a text size of 200%. And the
// buttons with a glyph that no other picture shows are pictured, for a person to look at.
internal sealed partial class WindowChecks
{
    private static readonly (string Id, string Name)[] RowButtons =
    [
        ("StudioSplitSceneButton", "Split scene"), ("StudioAddZoomButton", "Add zoom"), ("StudioAddCutButton", "Add cut"),
        ("StudioAddSpeedButton", "Add speed change"), ("StudioStartHereButton", "Start here"), ("StudioEndHereButton", "End here"),
    ];

    private bool _rowWidthsNoted;

    /// <summary>What a row of buttons measures: of each button its width and whether its glyph is drawn, and of the row what it has and what it needs.</summary>
    private sealed record GlyphRow(bool[] Shown, double[] Widths, double[] Glyphs, double Room, double Needed, double End, bool TimeWhole)
    {
        public bool AllShown => Shown.Length > 0 && Shown.All(shown => shown);

        public bool NoneShown => Shown.Length > 0 && !Shown.Any(shown => shown);
    }

    /// <summary>
    /// The row of the timeline is given less room than its six glyphs need, and then its room
    /// back; so are the two buttons side by side in the Zoom panel; and Add speed change, a
    /// button of a panel, is allowed less width than its glyph and its words need.
    /// </summary>
    private void GlyphsGiveWay(Editor editor, string name, List<string> saved)
    {
        Timeline.Mark($"9: rows without room for their glyphs, {name}");
        var ids = RowButtons.Select(button => button.Id).ToArray();
        string[] Names() => [.. ids.Select(id => Find(editor, id, 0.5)?.Name ?? string.Empty)];
        int Parts() => ids.Sum(id => Find(editor, id, 0.5)?.Children().Count ?? 0);
        bool Close(double a, double b) => Math.Abs(a - b) <= 1;

        // The timeline's row. Half of what the glyphs take is taken from what the row needs.
        var wide = OnUi(() => ReadRow(RowOf(editor, ids[0]), ids, editor));
        var (namesWide, partsWide) = (Names(), Parts());
        var less = wide.Needed - (wide.Glyphs.Sum() / 2);
        OnUi(() =>
        {
            if (First<StudioTimeline>(editor.Window.Content) is { } timeline)
            {
                timeline.MaxWidth = less + (timeline.ActualWidth - wide.Room);
            }
        });
        var narrow = Until(() => OnUi(() => ReadRow(RowOf(editor, ids[0]), ids, editor)), row => row.NoneShown, 2);
        Thread.Sleep(300);
        narrow = OnUi(() => ReadRow(RowOf(editor, ids[0]), ids, editor));
        var (namesNarrow, partsNarrow) = (Names(), Parts());
        var fileName = $"timeline-row-without-glyphs-{name}.png";
        if (editor.Camera.Take() is { } shot)
        {
            var path = Path.Combine(_output, fileName);
            shot.Save(path);
            saved.Add(path);
        }

        OnUi(() =>
        {
            if (First<StudioTimeline>(editor.Window.Content) is { } timeline)
            {
                timeline.MaxWidth = double.PositiveInfinity;
            }
        });
        var back = Until(() => OnUi(() => ReadRow(RowOf(editor, ids[0]), ids, editor)), row => row.AllShown, 2);

        var expected = RowButtons.Select(button => button.Name).ToArray();
        var narrower = narrow.Widths.Length == wide.Widths.Length && narrow.Widths.Select((width, index) => Close(width, wide.Widths[index] - wide.Glyphs[index])).All(same => same);
        var asBefore = back.Widths.Length == wide.Widths.Length && back.Widths.Select((width, index) => Close(width, wide.Widths[index])).All(same => same);
        _report.Check(
            $"the row of the timeline in the {name} theme, given less room than its six glyphs need: the glyphs give way and the words stay, each button is narrower by its glyph and the gap after it, the row fits the room it has with its time whole, the buttons say the names they said and hold nothing a screen reader walks, and with its room back the row shows its glyphs as before",
            wide.AllShown && wide.Needed <= wide.Room && narrow.NoneShown && narrower && narrow.Needed <= narrow.Room && narrow.End <= narrow.Room + 1 && narrow.TimeWhole
                && namesWide.SequenceEqual(expected) && namesNarrow.SequenceEqual(expected) && partsWide == 0 && partsNarrow == 0 && back.AllShown && asBefore,
            $"with {F(wide.Room, "0.#")} of room the row needs {F(wide.Needed, "0.#")}, its six buttons {Widths(wide.Widths)} wide, {wide.Shown.Count(shown => shown)} of 6 glyphs drawn, which take {F(wide.Glyphs.Sum(), "0.#")}; "
                + $"with {F(narrow.Room, "0.#")} of room it needs {F(narrow.Needed, "0.#")} and ends at {F(narrow.End, "0.#")}, its buttons {Widths(narrow.Widths)} wide, {narrow.Shown.Count(shown => shown)} of 6 glyphs drawn, the time {(narrow.TimeWhole ? "whole" : "cut")}; "
                + $"the names: {string.Join(", ", namesNarrow)}; elements inside the six buttons that a screen reader walks: {partsWide} and {partsNarrow}; "
                + $"with its room back, {F(back.Room, "0.#")}: {Widths(back.Widths)}, {back.Shown.Count(shown => shown)} of 6 glyphs drawn; saved as {fileName}");

        // The two buttons side by side in the Zoom panel, and one button of the Speed panel.
        string[] pairIds = ["StudioZoomSectionAddButton", "StudioSuggestZoomsButton"];
        const string Alone = "StudioSpeedSectionAddButton";
        var pairWide = OnUi(() => ReadRow(RowOf(editor, pairIds[0]), pairIds, editor));
        OnUi(() =>
        {
            if (RowOf(editor, pairIds[0]) is { } pair)
            {
                pair.MaxWidth = pairWide.Needed - (pairWide.Glyphs.Sum() / 2);
            }
        });
        var pairNarrow = Until(() => OnUi(() => ReadRow(RowOf(editor, pairIds[0]), pairIds, editor)), row => row.NoneShown, 2);
        var pairNames = pairIds.Select(id => Find(editor, id, 0.5)?.Name ?? string.Empty).ToArray();
        OnUi(() =>
        {
            if (RowOf(editor, pairIds[0]) is { } pair)
            {
                pair.MaxWidth = double.PositiveInfinity;
            }
        });
        var pairBack = Until(() => OnUi(() => ReadRow(RowOf(editor, pairIds[0]), pairIds, editor)), row => row.AllShown, 2);

        (bool Shown, double Width, double Glyph) ReadAlone() => OnUi(() =>
            Descendant<Button>(editor.Window.Content, Alone) is { Content: StudioButtonLabel label } button ? (label.IsGlyphShown, button.ActualWidth, label.GlyphWidth) : default);
        void AllowAlone(double width) => OnUi(() =>
        {
            if (Descendant<Button>(editor.Window.Content, Alone) is { } button)
            {
                button.MaxWidth = width;
            }
        });
        var aloneWide = ReadAlone();
        AllowAlone(aloneWide.Width - (aloneWide.Glyph / 2));
        var aloneNarrow = Until(ReadAlone, read => !read.Shown, 2);
        var aloneName = Find(editor, Alone, 0.5)?.Name ?? string.Empty;
        AllowAlone(double.PositiveInfinity);
        var aloneBack = Until(ReadAlone, read => read.Shown, 2);

        _report.Check(
            $"buttons of the inspector in the {name} theme without room for their glyphs: Add zoom and Suggest zooms, side by side, both give theirs up when the two do not fit and get them back; Add speed change, allowed less width than its glyph and its words need, gives its up, is as wide as its words need, and gets it back; each says the name it said",
            pairWide.AllShown && pairNarrow.NoneShown && pairNarrow.Needed <= pairNarrow.Room && pairBack.AllShown && pairNames.SequenceEqual(new[] { "Add zoom", "Suggest zooms" })
                && aloneWide.Shown && !aloneNarrow.Shown && Close(aloneNarrow.Width, aloneWide.Width - aloneWide.Glyph) && aloneName == "Add speed change" && aloneBack.Shown && Close(aloneBack.Width, aloneWide.Width),
            $"Add zoom and Suggest zooms: {Widths(pairWide.Widths)} wide in {F(pairWide.Room, "0.#")}, {pairWide.Shown.Count(shown => shown)} of 2 glyphs drawn; in {F(pairNarrow.Room, "0.#")}: {Widths(pairNarrow.Widths)}, {pairNarrow.Shown.Count(shown => shown)} of 2; with the room back: {Widths(pairBack.Widths)}, {pairBack.Shown.Count(shown => shown)} of 2; named {string.Join(", ", pairNames)}. "
                + $"Add speed change: {F(aloneWide.Width, "0.#")} wide with its glyph {(aloneWide.Shown ? "drawn" : "not drawn")}; allowed {F(aloneWide.Width - (aloneWide.Glyph / 2), "0.#")}: {F(aloneNarrow.Width, "0.#")} wide, its glyph {(aloneNarrow.Shown ? "drawn" : "not drawn")}, named \"{aloneName}\"; allowed any width again: {F(aloneBack.Width, "0.#")}, its glyph {(aloneBack.Shown ? "drawn" : "not drawn")}");

        static string Widths(double[] widths) => string.Join(" + ", widths.Select(width => F(width, "0.#")));
    }

    /// <summary>
    /// Pictures, for a person to look at, of the buttons with a glyph that no picture of a panel
    /// from its top shows: Delete scene and Delete speed change, at the end of their panels;
    /// Reset crop, in a group that is closed while nothing is cropped; and Show scene, which the
    /// Camera panel has only in a scene that hides the camera. The check says that each is in
    /// its picture: whole in the part of the panel that shows, with its glyph drawn.
    /// </summary>
    private void ButtonPictures(Editor editor, string name, List<string> saved)
    {
        Timeline.Mark($"9: the buttons the panels' pictures do not show, {name}");
        var (pictured, wrong) = (new List<string>(), new List<string>());
        void Picture(string id, string words, string file)
        {
            Until(() => editor.Root.FindAsItIs(id), found => found is not null, 2);
            OnUi(() => DescendantIn<Button>(editor.Window.Content, id)?.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 }));
            Thread.Sleep(450);
            var read = OnUi(() =>
            {
                if (DescendantIn<Button>(editor.Window.Content, id) is not { Content: StudioButtonLabel label } button)
                {
                    return (Found: false, Whole: false, Glyph: false, Words: string.Empty);
                }

                ScrollViewer? scroller = null;
                for (DependencyObject? at = button; at is not null && scroller is null; at = VisualTreeHelper.GetParent(at))
                {
                    scroller = at as ScrollViewer;
                }

                var place = scroller is null ? default : button.TransformToVisual(scroller).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                var whole = scroller is not null && place.Width > 0 && place.Left >= -0.5 && place.Top >= -0.5 && place.Right <= scroller.ActualWidth + 0.5 && place.Bottom <= scroller.ActualHeight + 0.5;
                return (Found: true, Whole: whole, Glyph: label.IsGlyphShown, Words: label.Text);
            });
            var said = editor.Root.FindAsItIs(id)?.Name ?? string.Empty;
            var fileName = $"button-{file}-{name}.png";
            if (editor.Camera.Take() is { } shot)
            {
                var path = Path.Combine(_output, fileName);
                shot.Save(path);
                saved.Add(path);
                pictured.Add(words);
            }
            else
            {
                wrong.Add($"{words}: no screenshot");
            }

            if (!read.Found || !read.Whole || !read.Glyph || read.Words != words || said != words)
            {
                wrong.Add($"{words} ({fileName}): {(read.Found ? "there" : "not there")}, {(read.Whole ? "whole in what the panel shows" : "not whole in what the panel shows")}, its glyph {(read.Glyph ? "drawn" : "not drawn")}, showing \"{read.Words}\", named \"{said}\"");
            }
        }

        // The playhead is in the second of three scenes, which shows the camera.
        ShowPanel(editor, StudioInspectorPanel.Scene);
        Picture("StudioDeleteSceneButton", "Delete scene", "delete-scene");

        // Selecting a speed change shows the Speed panel with its controls.
        var selected = LaneItems(editor, SpeedLane) is { Count: > 0 } speeds && speeds[0].Select();
        Picture("StudioDeleteSpeedButton", "Delete speed change", "delete-speed-change");

        ShowPanel(editor, StudioInspectorPanel.Screen);
        var screenOpen = editor.Root.FindAsItIs("StudioScreenCropGroup")?.Expand() ?? false;
        Thread.Sleep(450);
        Picture("StudioScreenCropResetButton", "Reset crop", "reset-screen-crop");

        ShowPanel(editor, StudioInspectorPanel.Camera);
        var cameraOpen = editor.Root.FindAsItIs("StudioCameraCropGroup")?.Expand() ?? false;
        Thread.Sleep(450);
        Picture("StudioCameraCropResetButton", "Reset crop", "reset-camera-crop");

        // In a scene that is the screen alone the Camera panel says so, and offers the Scene panel.
        var screenOnly = Find(editor, "StudioLayoutScreen")?.Select() ?? false;
        ShowPanel(editor, StudioInspectorPanel.Camera);
        Picture("StudioShowSceneButton", "Show scene", "show-scene");

        _report.Check(
            $"the buttons with a glyph that no picture of a panel from its top shows, in the {name} theme, are each pictured whole in its panel with its glyph drawn and its words as its name, for a person to look at: Delete scene, Delete speed change, Reset crop of the screen and of the camera, and Show scene",
            pictured.Count == 5 && wrong.Count == 0 && selected && screenOpen && cameraOpen && screenOnly,
            wrong.Count == 0
                ? $"{string.Join(", ", pictured)}; saved as button-<button>-{name}.png"
                : $"{string.Join("; ", wrong)}; a speed change was selected: {selected}; the crop groups were opened: {screenOpen}, {cameraOpen}; the scene was made the screen alone: {screenOnly}");
    }

    /// <summary>The row a button is in: what holds it. UI thread.</summary>
    private static Panel? RowOf(Editor editor, string buttonId) =>
        Descendant<Button>(editor.Window.Content, buttonId) is { } button ? VisualTreeHelper.GetParent(button) as Panel : null;

    /// <summary>
    /// What a row measures, added up here and not by the row: its items as wide as they are
    /// drawn, the time of the timeline's row as wide as its text, and the gaps between them. UI thread.
    /// </summary>
    private static GlyphRow ReadRow(Panel? row, string[] buttonIds, Editor editor)
    {
        if (row is null)
        {
            return new GlyphRow([], [], [], 0, 0, 0, false);
        }

        var buttons = buttonIds.Select(id => DescendantIn<Button>(row, id)).OfType<Button>().ToArray();
        var labels = buttons.Select(button => button.Content as StudioButtonLabel).OfType<StudioButtonLabel>().ToArray();
        var (needed, items, timeWhole) = (0.0, 0, true);
        foreach (var item in row.Children.OfType<FrameworkElement>().Where(item => item.Visibility == Visibility.Visible))
        {
            items++;
            if (item is StackPanel holder && DescendantIn<TextBlock>(holder, "StudioTimeText") is { } time)
            {
                needed += time.ActualWidth + time.Margin.Left + time.Margin.Right;
                timeWhole = holder.ActualWidth + 0.5 >= time.ActualWidth + time.Margin.Left + time.Margin.Right;
            }
            else
            {
                needed += item.ActualWidth + item.Margin.Left + item.Margin.Right;
            }
        }

        needed += row switch
        {
            Grid grid => grid.ColumnSpacing * (grid.ColumnDefinitions.Count - 1),
            StackPanel stack => stack.Spacing * Math.Max(0, items - 1),
            _ => 0,
        };
        var end = buttons.Length == 0 ? 0 : buttons[^1].TransformToVisual(row).TransformPoint(new Point(buttons[^1].ActualWidth, 0)).X;
        return new GlyphRow(
            [.. labels.Select(label => label.IsGlyphShown)],
            [.. buttons.Select(button => button.ActualWidth)],
            [.. labels.Select(label => label.GlyphWidth)],
            row.ActualWidth,
            needed,
            end,
            timeWhole);
    }

    /// <summary>The first element of a kind among what a window shows. UI thread.</summary>
    private static T? First<T>(DependencyObject? root)
        where T : class
    {
        if (root is null)
        {
            return null;
        }

        if (root is T match)
        {
            return match;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            if (First<T>(VisualTreeHelper.GetChild(root, index)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// How wide the items of the timeline's row are, and how wide they would be at a text size of
    /// 200%. It is a sum and nothing that was seen: every text and glyph of the row is measured
    /// again at the font size that text size gives it, and what a button has around its content
    /// is taken as it is.
    /// </summary>
    private string RowWidths(Editor editor, string[] row, double windowWidth) => OnUi(() =>
    {
        const double TextSize = 2;
        var now = new Windows.UI.ViewManagement.UISettings().TextScaleFactor;
        var (widths, larger, glyphs, glyphsNow, time, furthest) = (new List<double>(), new List<double>(), 0.0, 0.0, 0.0, 0.0);
        Grid? grid = null;
        var margins = 0.0;
        foreach (var id in row)
        {
            if (DescendantIn<FrameworkElement>(editor.Window.Content, id) is not { } item)
            {
                return $"the widths of the timeline's row could not be read: {id} is not in the window";
            }

            grid ??= VisualTreeHelper.GetParent(item) as Grid;
            margins += item.Margin.Left + item.Margin.Right;
            var grows = 0.0;
            foreach (var text in TextsOf(item))
            {
                // Measured as it is first, to see that measuring a copy gives what the window has.
                var same = MeasureText(text, 1);
                furthest = Math.Max(furthest, Math.Abs(same - text.ActualWidth));
                grows += MeasureText(text, TextSize / now) - same;
            }

            if (item is Button { Content: StudioButtonLabel label } && TextsOf(label).FirstOrDefault() is { } glyph)
            {
                glyphs += MeasureText(glyph, TextSize / now) + StudioButtonLabel.Gap;
                glyphsNow += label.GlyphWidth;
            }

            // The time has the room the buttons leave, so it counts as wide as its text.
            var wide = item is TextBlock own ? MeasureText(own, 1) : item.ActualWidth;
            widths.Add(wide);
            larger.Add(wide + grows);
            if (item is TextBlock)
            {
                time = wide + grows + item.Margin.Left + item.Margin.Right;
            }
        }

        if (grid is null)
        {
            return "the widths of the timeline's row could not be read: its items are not in a grid";
        }

        var gaps = (grid.ColumnSpacing * (grid.ColumnDefinitions.Count - 1)) + margins;
        var (room, around) = (grid.ActualWidth, windowWidth - grid.ActualWidth);
        var (sum, sumLarger) = (widths.Sum() + gaps, larger.Sum() + gaps);
        var without = sumLarger - glyphs;
        string N(double value) => F(value, "0.#");
        string Each(List<double> values) => string.Join(" + ", values.Select(N));
        string In(double needs) => needs <= room ? $"{N(room - needs)} less than the row has in this window" : $"{N(needs - room)} more than the row has in this window: it fits a window {N(needs + around)} wide";
        return $"the timeline's row by its sizes, in effective pixels, in a window {N(windowWidth)} wide, whose row is {N(room)} wide (the window has {N(around)} around it): "
            + $"at the text size of this PC, {N(now * 100)}%, its ten items are {Each(widths)}, and with {N(gaps)} between them {N(sum)}, which leaves {N(room - sum)}; {N(glyphsNow)} of it are the six glyphs before words, each with the gap after it. "
            + $"At a text size of {N(TextSize * 100)}%, with every text and glyph measured at that size (a copy measured at this size is within {F(furthest, "0.##")} of what the window has), "
            + $"they would be {Each(larger)}, and with the same {N(gaps)} between them {N(sumLarger)}, {In(sumLarger)}. "
            + $"The six glyphs with their gaps would be {N(glyphs)} of that. Where the row has no room for them they give way, and the row is {N(without)}, as wide as it was before its buttons had glyphs: {In(without)}. "
            + $"Narrower than that, the time, {N(time)} of it, is cut first, and the buttons with their gaps, {N(without - time)}, {(without - time <= room ? "are whole in this window" : "are cut in this window")}. "
            + "Nothing was seen at 200%: no tool may change the setting";
    });

    /// <summary>Every text an element shows, the glyph of an icon among them, in the order they are drawn. UI thread.</summary>
    private static IEnumerable<TextBlock> TextsOf(DependencyObject root)
    {
        if (root is TextBlock { Visibility: Visibility.Visible, ActualWidth: > 0 } text)
        {
            yield return text;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            foreach (var found in TextsOf(VisualTreeHelper.GetChild(root, index)))
            {
                yield return found;
            }
        }
    }

    /// <summary>How wide a text would be at a multiple of its font size, measured in a copy that is in no window. UI thread.</summary>
    private static double MeasureText(TextBlock text, double factor)
    {
        var copy = new TextBlock
        {
            Text = text.Text,
            FontFamily = text.FontFamily,
            FontSize = text.FontSize * factor,
            FontWeight = text.FontWeight,
            FontStyle = text.FontStyle,
            FontStretch = text.FontStretch,
            CharacterSpacing = text.CharacterSpacing,
            TextWrapping = TextWrapping.NoWrap,
        };
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(copy, Microsoft.UI.Xaml.Documents.Typography.GetNumeralAlignment(text));
        copy.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return copy.DesiredSize.Width;
    }
}
