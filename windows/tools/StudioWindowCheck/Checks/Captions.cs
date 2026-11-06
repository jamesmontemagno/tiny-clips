using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using TinyClips.App.Models.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

internal sealed partial class WindowChecks
{
    private const string WarningGlyph = "\uE7BA";

    private void Captions()
    {
        var folder = NewScreenProject("Caption warnings", project => project with
        {
            Captions = new StudioCaptions
            {
                Cues =
                [
                    new StudioCaptionCue { Id = "fine", Start = 3, End = 5, Text = "A caption with nothing wrong" },
                    new StudioCaptionCue { Id = "short", Start = 1, End = 1.2, Text = "  Hi  " },
                ],
            },
        });
        if (OpenReady(folder, "caption warnings") is not { } editor)
        {
            return;
        }

        OnUi(() => editor.Window.ViewModel.ShowInspectorPanel(StudioInspectorPanel.Captions));
        WhenTheUiThreadHasNothingWaiting();

        var marks = OnUi(() =>
        {
            var list = (ListView)InspectorIn(editor.Window.Content)!.FindName("CaptionList");
            list.UpdateLayout();
            return Enumerable.Range(0, list.Items.Count)
                .Select(index => list.ContainerFromIndex(index) is DependencyObject row && HasVisibleWarning(row))
                .ToArray();
        });
        _report.Check(
            "saved captions are listed, with a warning mark only on the one with problems",
            marks.SequenceEqual([true, false]),
            string.Join(", ", marks));

        var texts = OnUi(() => TextsOf(InspectorIn(editor.Window.Content)!).Select(static text => text.Text).ToArray());
        _report.Check("there is no Checks section", !texts.Contains("Checks"));

        var header = OnUi(() =>
        {
            var content = editor.Window.Content;
            var next = Descendant<Button>(content, "StudioCaptionsNextProblemButton");
            var fix = Descendant<Button>(content, "StudioCaptionsFixButton");
            var count = Descendant<TextBlock>(content, "StudioCaptionsProblemCount");
            return (Next: next?.Visibility == Visibility.Visible && next.IsEnabled,
                Fix: fix?.Visibility == Visibility.Visible && fix.IsEnabled,
                Count: count?.Text);
        });
        _report.Check(
            "the list's header has Next problem with the count, and Auto fix",
            header is { Next: true, Fix: true, Count: "1" },
            header.ToString());

        var layout = OnUi(() =>
        {
            var inspector = InspectorIn(editor.Window.Content)!;
            var list = (FrameworkElement)inspector.FindName("CaptionList");
            var expander = DescendantIn<Expander>(editor.Window.Content, "StudioCaptionsInVideoExpander");
            if (expander is null)
            {
                return (Below: false, Collapsed: false, Opens: false);
            }

            var listTop = list.TransformToVisual(inspector).TransformPoint(default).Y;
            var expanderTop = expander.TransformToVisual(inspector).TransformPoint(default).Y;
            var collapsed = !expander.IsExpanded;
            expander.IsExpanded = true;
            expander.UpdateLayout();
            var drawn = DescendantIn<CheckBox>(expander, "StudioCaptionsDrawnCheckBox");
            return (Below: expanderTop > listTop, Collapsed: collapsed, Opens: drawn is { IsEnabled: true });
        });
        _report.Check(
            "In the video is a collapsed expander under the list, and opening it shows Draw captions on the video",
            layout is { Below: true, Collapsed: true, Opens: true },
            layout.ToString());

        OnUi(() => editor.Window.ViewModel.AreCaptionsDrawn = true);
        WhenTheUiThreadHasNothingWaiting();
        var looks = OnUi(() =>
        {
            var inspector = InspectorIn(editor.Window.Content)!;
            var grid = (GridView)inspector.FindName("CaptionLooks");
            grid.UpdateLayout();
            var tops = grid.Items.OfType<GridViewItem>()
                .Select(item => Math.Round(item.TransformToVisual(grid).TransformPoint(default).Y))
                .ToArray();
            var size = DescendantIn<FrameworkElement>(inspector, "StudioCaptionsSizeChoice")!;
            var clipped = AllOf<TextBlock>(size)
                .Where(static text => text.Text.Length > 0)
                .Where(text =>
                {
                    var item = AncestorOf<SelectorItem>(text) ?? (FrameworkElement)size;
                    var left = text.TransformToVisual(item).TransformPoint(default).X;
                    return left < -0.5 || left + text.ActualWidth > item.ActualWidth + 0.5;
                })
                .Select(static text => text.Text)
                .ToArray();
            return (Tops: string.Join("/", tops), Clipped: string.Join(",", clipped));
        });
        _report.Check(
            "the four caption looks sit two to a row",
            looks.Tops.Split('/') is [var a, var b, var c, var d] && a == b && c == d && a != c,
            looks.Tops);
        _report.Check("the Size choices are shown in full", looks.Clipped.Length == 0, looks.Clipped);

        OnUi(() => editor.Window.ViewModel.SelectNextCaptionProblem());
        var selected = OnUi(() => editor.Window.ViewModel.SelectedCaptionText);
        _report.Check("Next problem opens the caption with the warning", selected == "  Hi  ", selected);

        OnUi(() => editor.Window.ViewModel.FixCaptions());
        WhenTheUiThreadHasNothingWaiting();
        var after = OnUi(() =>
        {
            var list = (ListView)InspectorIn(editor.Window.Content)!.FindName("CaptionList");
            list.UpdateLayout();
            var anyMark = Enumerable.Range(0, list.Items.Count)
                .Any(index => list.ContainerFromIndex(index) is DependencyObject row && HasVisibleWarning(row));
            var next = Descendant<Button>(editor.Window.Content, "StudioCaptionsNextProblemButton");
            return (anyMark, NextShown: next?.Visibility == Visibility.Visible);
        });
        _report.Check("after Auto fix no row is marked and the header actions go away", after is { anyMark: false, NextShown: false }, after.ToString());
        CloseQuietly(editor);
    }

    private static IEnumerable<T> AllOf<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T found)
            {
                yield return found;
            }

            foreach (var inner in AllOf<T>(child))
            {
                yield return inner;
            }
        }
    }

    private static T? AncestorOf<T>(DependencyObject element)
        where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is T found)
            {
                return found;
            }
        }

        return null;
    }

    // Whether a warning glyph is on show inside an element, skipping what is collapsed.
    private static bool HasVisibleWarning(DependencyObject root)
    {
        if (root is UIElement { Visibility: Visibility.Collapsed })
        {
            return false;
        }

        if (root is FontIcon { Glyph: WarningGlyph })
        {
            return true;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            if (HasVisibleWarning(VisualTreeHelper.GetChild(root, index)))
            {
                return true;
            }
        }

        return false;
    }
}
