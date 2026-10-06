using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TinyClips.App.Controls.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioWindowCheck.Automation;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// The inspector shows one panel at a time, chosen on the rail down its edge, and a control of
// another panel is not in the window: not for the Tab key and not for a screen reader. The
// checks were written for an inspector that showed everything in one scroll. What they need of
// the new one is in this file, so that they go on asking for a control by its automation id:
// the one table of which panel holds which control, the step that shows that panel before a
// control is looked for, and the two readings that are of the whole inspector, which go through
// the panels one after the other: every control with its name, and the stops of the Tab key.
internal sealed partial class WindowChecks
{
    private const string InspectorRailId = "StudioInspectorRail";
    private const string InspectorTitleId = "StudioInspectorPanelTitle";

    // What the Zoom, Cut and Speed panels say while the recording has none of theirs.
    private const string ZoomEmptyHint = "Press Z or Add zoom to zoom in at the playhead.";
    private const string CutEmptyHint = "Press X or Add cut to cut a second out of the video at the playhead.";
    private const string SpeedEmptyHint = "Press R or Add speed change to play two seconds twice as fast from the playhead.";

    /// <summary>The Crop group of the Screen panel or of the Camera panel, which a crop slider is inside of.</summary>
    private enum CropGroup
    {
        None,
        Screen,
        Camera,
    }

    // Which panel holds which control, by automation id, as StudioInspector.xaml has them. The
    // Cropped texts are in the headers of the two crop groups, not inside them.
    private static readonly Dictionary<string, (StudioInspectorPanel Panel, CropGroup Group)> Places = BuildPlaces();

    // Controls the inspector makes in code: the swatches, the camera's cutouts, and the speeds.
    private static readonly (string Prefix, StudioInspectorPanel Panel)[] PlacePrefixes =
    [
        ("StudioSwatch_", StudioInspectorPanel.Background),
        ("StudioCameraCutout_", StudioInspectorPanel.Camera),
        ("StudioSpeedRate_", StudioInspectorPanel.Speed),
    ];

    private static Dictionary<string, (StudioInspectorPanel Panel, CropGroup Group)> BuildPlaces()
    {
        var places = new Dictionary<string, (StudioInspectorPanel Panel, CropGroup Group)>(StringComparer.Ordinal);
        void Add(StudioInspectorPanel panel, CropGroup group, params string[] ids)
        {
            foreach (var id in ids)
            {
                places.Add(id, (panel, group));
            }
        }

        Add(StudioInspectorPanel.Scene, CropGroup.None,
            "StudioPreviousSceneButton", "StudioScenePositionText", "StudioSceneRangeText", "StudioNextSceneButton", "StudioLayoutChoice",
            "StudioLayoutScreen", "StudioLayoutBubble", "StudioLayoutSideBySide", "StudioLayoutCamera", "StudioSceneSectionSplitButton",
            "StudioSplitSceneNote", "StudioOneSceneNote", "StudioFirstSceneNote", "StudioSceneEntryChoice", "StudioSceneEntryCut",
            "StudioSceneEntryMove", "StudioSceneMoveSlider", "StudioSceneMoveLimitedNote", "StudioSceneStartText", "StudioSceneStartEarlierButton",
            "StudioSceneStartLaterButton", "StudioSceneStartAtPlayheadButton", "StudioDeleteSceneButton");
        Add(StudioInspectorPanel.Background, CropGroup.None,
            "StudioShowBackgroundCheckBox", "StudioSolidSwatches", "StudioGradientSwatches", "StudioPaddingSlider");
        Add(StudioInspectorPanel.Screen, CropGroup.None,
            "StudioNoCameraNote", "StudioScreenCornerRadiusSlider", "StudioScreenShadowSlider", "StudioClickRingsCheckBox", "StudioScreenCropGroup",
            "StudioScreenCroppedText");
        Add(StudioInspectorPanel.Screen, CropGroup.Screen,
            "StudioScreenCropLeftSlider", "StudioScreenCropTopSlider", "StudioScreenCropRightSlider", "StudioScreenCropBottomSlider",
            "StudioScreenCropResetButton");
        Add(StudioInspectorPanel.Camera, CropGroup.None,
            "StudioCameraHiddenNote", "StudioShowSceneButton", "StudioCameraSizeSlider", "StudioCameraPositionComboBox", "StudioCameraOffsetXSlider",
            "StudioCameraOffsetYSlider", "StudioCameraSideComboBox", "StudioCameraShareSlider", "StudioCameraPlacementNote",
            "StudioCameraShapeComboBox", "StudioCameraCornerRadiusSlider", "StudioCameraMirrorCheckBox", "StudioCameraCutoutChoice",
            "StudioCameraCutoutRemovedNote", "StudioCameraBorderSlider", "StudioCameraShadowSlider", "StudioCameraCropGroup",
            "StudioCameraCroppedText");
        Add(StudioInspectorPanel.Camera, CropGroup.Camera,
            "StudioCameraCropLeftSlider", "StudioCameraCropTopSlider", "StudioCameraCropRightSlider", "StudioCameraCropBottomSlider",
            "StudioCameraCropResetButton");
        Add(StudioInspectorPanel.Zoom, CropGroup.None,
            "StudioPreviousZoomButton", "StudioZoomPositionText", "StudioZoomRangeText", "StudioNextZoomButton", "StudioZoomSectionAddButton",
            "StudioSuggestZoomsButton", "StudioNoClicksNote", "StudioRemoveSuggestionsButton", "StudioZoomEmptyHint", "StudioZoomHint",
            "StudioZoomScaleSlider", "StudioZoomFocusChoice", "StudioZoomFocusPoint", "StudioZoomFocusPointer", "StudioNoPointerNote",
            "StudioZoomFocusPad", "StudioZoomFocusXSlider", "StudioZoomFocusYSlider", "StudioZoomStartText", "StudioZoomStartEarlierButton",
            "StudioZoomStartLaterButton", "StudioZoomStartAtPlayheadButton", "StudioZoomEndText", "StudioZoomEndEarlierButton",
            "StudioZoomEndLaterButton", "StudioZoomEndAtPlayheadButton", "StudioZoomEaseInSlider", "StudioZoomEaseOutSlider", "StudioDeleteZoomButton");
        Add(StudioInspectorPanel.Cut, CropGroup.None,
            "StudioPreviousCutButton", "StudioCutPositionText", "StudioCutRangeText", "StudioNextCutButton", "StudioCutSectionAddButton",
            "StudioCutEmptyHint", "StudioCutHint", "StudioCutStartText", "StudioCutStartEarlierButton", "StudioCutStartLaterButton",
            "StudioCutStartAtPlayheadButton", "StudioCutEndText", "StudioCutEndEarlierButton", "StudioCutEndLaterButton",
            "StudioCutEndAtPlayheadButton", "StudioCutLengthText", "StudioDeleteCutButton");
        Add(StudioInspectorPanel.Speed, CropGroup.None,
            "StudioPreviousSpeedButton", "StudioSpeedPositionText", "StudioSpeedRangeText", "StudioNextSpeedButton", "StudioSpeedSectionAddButton",
            "StudioSpeedEmptyHint", "StudioSpeedHint", "StudioSpeedRateChoice", "StudioSpeedStartText", "StudioSpeedStartEarlierButton",
            "StudioSpeedStartLaterButton", "StudioSpeedStartAtPlayheadButton", "StudioSpeedEndText", "StudioSpeedEndEarlierButton",
            "StudioSpeedEndLaterButton", "StudioSpeedEndAtPlayheadButton", "StudioSpeedLengthText", "StudioSpeedSilentNote", "StudioDeleteSpeedButton");
        Add(StudioInspectorPanel.Audio, CropGroup.None,
            "StudioMuteCheckBox");
        Add(StudioInspectorPanel.Project, CropGroup.None,
            "StudioBrandingCheckBox", "StudioKeepProjectCheckBox", "StudioKeepProjectNote", "StudioSaveDefaultLookButton", "StudioDefaultLookNote",
            "StudioDefaultLookStatus");
        return places;
    }

    /// <summary>The panel that holds a control of the inspector, and the crop group it is in. Null for everything else in the window.</summary>
    private static (StudioInspectorPanel Panel, CropGroup Group)? PlaceOf(string automationId)
    {
        if (Places.TryGetValue(automationId, out var place))
        {
            return place;
        }

        foreach (var (prefix, panel) in PlacePrefixes)
        {
            if (automationId.StartsWith(prefix, StringComparison.Ordinal))
            {
                return (panel, CropGroup.None);
            }
        }

        return null;
    }

    /// <summary>The inspector among what a window shows. UI thread.</summary>
    private static StudioInspector? InspectorIn(DependencyObject? content) =>
        ((content as FrameworkElement)?.FindName("InspectorHost") as Border)?.Child as StudioInspector;

    // ---------------------------------------------------------------------------------------
    // The one step: show the panel that holds a control, then look
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Shows the panel that holds a control of the inspector, and opens the crop group it is in,
    /// the way the rail and a group's header do: through the editor. True when something had to
    /// change. Does nothing for a control that is not the inspector's, for a panel the recording
    /// does not have, and before the project is open. UI thread.
    /// </summary>
    /// <param name="layoutNow">
    /// Whether the window lays itself out before this returns, which the caller needs when it
    /// goes on to the control in the same turn of the UI thread.
    /// </param>
    private static bool ShowWhatHolds(DependencyObject? content, string automationId, bool layoutNow)
    {
        if (PlaceOf(automationId) is not { } place
            || InspectorIn(content) is not { ViewModel: { IsReady: true } viewModel } inspector
            || StudioInspectorPanels.Resolve(place.Panel, viewModel.HasCamera) != place.Panel)
        {
            return false;
        }

        var changed = false;
        if (viewModel.InspectorPanel != place.Panel)
        {
            viewModel.ShowInspectorPanel(place.Panel);
            changed = true;
        }

        if (place.Group == CropGroup.Screen && !viewModel.IsScreenCropOpen)
        {
            viewModel.SetScreenCropOpen(true);
            changed = true;
        }
        else if (place.Group == CropGroup.Camera && !viewModel.IsCameraCropOpen)
        {
            viewModel.SetCameraCropOpen(true);
            changed = true;
        }

        if (changed && layoutNow)
        {
            inspector.UpdateLayout();
        }

        return changed;
    }

    /// <summary>
    /// What runs before a control is looked for in a window's UI Automation tree: the panel that
    /// holds it is shown, and after a change the tree is given a moment to have the control. A
    /// control that is not there after that is not shown by its panel either.
    /// </summary>
    private void ShowWhatHolds(Editor editor, string automationId)
    {
        if (PlaceOf(automationId) is null || editor.IsReleased)
        {
            return;
        }

        bool Show()
        {
            DependencyObject? content;
            try
            {
                content = editor.IsReleased ? null : editor.Window.Content;
            }
            catch (Exception)
            {
                // A window that has closed has nothing to show.
                content = null;
            }

            return ShowWhatHolds(content, automationId, layoutNow: false);
        }

        var changed = _dispatcher.HasThreadAccess ? Show() : OnUi(Show);
        if (changed && !_dispatcher.HasThreadAccess)
        {
            // A crop group that opens shows what is inside it a moment later.
            Until(() => editor.Root.FindAsItIs(automationId) is not null, found => found, 0.8);
        }
    }

    /// <summary>
    /// Shows a panel of the inspector the way the rail does, without looking for anything. For
    /// a check that is about what the panel shows.
    /// </summary>
    private void ShowPanel(Editor editor, StudioInspectorPanel panel)
    {
        OnUi(() => editor.Window.ViewModel.ShowInspectorPanel(panel));
        PanelShown(editor, StudioInspectorPanels.GetTitle(StudioInspectorPanels.Resolve(panel, OnUi(() => editor.Window.ViewModel.HasCamera))), 1.5);
    }

    /// <summary>
    /// The panel on show as a screen reader finds it: the name over the panel, and the names of
    /// the rail's items that say they are selected. Nothing is shown first. Waits for the name.
    /// </summary>
    private static (string Title, string[] Selected) PanelShown(Editor editor, string? wanted = null, double seconds = 1.5)
    {
        (string Title, string[] Selected) Read()
        {
            var title = editor.Root.FindAsItIs(InspectorTitleId)?.Name ?? string.Empty;
            var selected = editor.Root.FindAsItIs(InspectorRailId)?.Children().Where(item => item.IsSelected == true).Select(item => item.Name).ToArray() ?? [];
            return (title, selected);
        }

        return wanted is null ? Read() : Until(Read, read => read.Title == wanted && read.Selected.SequenceEqual([wanted]), seconds);
    }

    /// <summary>Whether the panel on show is this one and no other: by the name over it, and by the one selected item of the rail.</summary>
    private static bool IsShown((string Title, string[] Selected) shown, string title) =>
        shown.Title == title && shown.Selected.SequenceEqual([title]);

    private static string PanelWords((string Title, string[] Selected) shown) =>
        $"the panel is called \"{shown.Title}\" and the rail has {(shown.Selected.Length == 0 ? "nothing" : string.Join(" and ", shown.Selected.Select(name => $"\"{name}\"")))} selected";

    // ---------------------------------------------------------------------------------------
    // Every panel in turn: what a screen reader is given
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the window as a screen reader walks it with each panel of the inspector on show in
    /// turn, both crop groups open, so that every control a person can bring up is read. Each
    /// reading is saved next to the report. What is returned is the first reading, whole, and
    /// from each later one the inspector's controls that have an automation id; what is wrong
    /// with any of the readings, each thing once; and the sliders, each once. A window that
    /// shows no inspector, as one whose project cannot be opened, is read as it is.
    /// </summary>
    /// <remarks>
    /// The panel that was on show is shown again afterwards, and the keyboard focus is put back
    /// if a panel that went away took it along. The crop groups are left as they were found,
    /// which from then on is as if their headers had been pressed.
    /// </remarks>
    private (List<(int Depth, UiaElement Element)> Tree, List<string> Problems, int Elements, int Sliders, int Panes, string Saved) ReadEveryPanel(Editor editor, string fileName)
    {
        var start = OnUi(() =>
        {
            if (InspectorIn(editor.Window.Content) is not { ViewModel: { IsReady: true } viewModel } inspector)
            {
                return ((StudioInspectorPanel Panel, bool Screen, bool Camera, StudioInspectorPanel[] All, UIElement? Focused, FocusState State)?)null;
            }

            var focused = inspector.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as UIElement : null;
            var before = (viewModel.InspectorPanel, viewModel.IsScreenCropOpen, viewModel.IsCameraCropOpen, StudioInspectorPanels.GetAvailable(viewModel.HasCamera).ToArray(), focused, (focused as Control)?.FocusState ?? FocusState.Programmatic);
            viewModel.SetScreenCropOpen(true);
            viewModel.SetCameraCropOpen(true);
            return before;
        });
        if (start is not { } was)
        {
            var asItIs = Content(editor);
            SaveTree(fileName, asItIs);
            var (wrong, count, sliderCount, paneCount) = Audit(asItIs);
            return (asItIs, wrong, count, sliderCount, paneCount, fileName);
        }

        var joined = new List<(int Depth, UiaElement Element)>();
        var problems = new List<string>();
        var (sliders, panes) = (0, 0);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        try
        {
            foreach (var panel in was.All)
            {
                var title = StudioInspectorPanels.GetTitle(panel);
                OnUi(() => editor.Window.ViewModel.ShowInspectorPanel(panel));
                var shown = PanelShown(editor, title, 2);

                // A crop group that has just come on show is still opening.
                Thread.Sleep(panel is StudioInspectorPanel.Screen or StudioInspectorPanel.Camera ? 450 : 60);
                var tree = Content(editor);
                SaveTree($"{stem}-{panel.ToString().ToLowerInvariant()}.txt", tree);
                var (wrong, _, _, unnamed) = Audit(tree);
                if (!IsShown(shown, title))
                {
                    wrong.Add($"asked for the {title} panel, {PanelWords(shown)}");
                }

                problems.AddRange(wrong.Where(problem => !problems.Contains(problem)));
                panes = Math.Max(panes, unnamed);
                var first = joined.Count == 0;
                var taken = first ? tree : [.. tree.Where(entry => PlaceOf(entry.Element.Id) is not null)];
                joined.AddRange(taken);
                sliders += taken.Count(entry => entry.Element.ControlType == ControlTypeNames.Slider);
            }
        }
        finally
        {
            OnUi(() =>
            {
                var viewModel = editor.Window.ViewModel;
                viewModel.ShowInspectorPanel(was.Panel);
                viewModel.SetScreenCropOpen(was.Screen);
                viewModel.SetCameraCropOpen(was.Camera);
                if (was.Focused is { XamlRoot: { } root } focused && !ReferenceEquals(FocusManager.GetFocusedElement(root), focused))
                {
                    editor.Window.Content?.UpdateLayout();
                    focused.Focus(was.State);
                }
            });
            PanelShown(editor, StudioInspectorPanels.GetTitle(was.Panel), 2);
        }

        return (joined, problems, joined.Count, sliders, panes, $"{stem}-<panel>.txt, one for each of the {was.All.Length} panels");
    }

    // ---------------------------------------------------------------------------------------
    // The stops of the Tab key
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The stops of the Tab key in the window as it is, one round from the first, read by asking
    /// the window to move the keyboard focus to the next stop and noting where it lands. The
    /// focus is put back afterwards. UI thread.
    /// </summary>
    /// <remarks>
    /// A stop is listed by its automation id. A group of radio buttons is listed by the group's
    /// id, the rail by its own and not by the item the focus is on, and the header of a crop
    /// group by the group's: the header is a part of the framework's template, with the same id
    /// in every group.
    /// </remarks>
    private static List<string> StopsAsItIs(FrameworkElement content, XamlRoot root, bool putBack = true)
    {
        var stops = new List<string>();
        var seen = new List<DependencyObject>();
        var before = FocusManager.GetFocusedElement(root) as UIElement;
        var options = new FindNextElementOptions { SearchRoot = content };
        (FocusManager.FindFirstFocusableElement(content) as UIElement)?.Focus(FocusState.Keyboard);
        for (var step = 0; step < 200; step++)
        {
            // Round once: the first stop comes again after the last.
            if (FocusManager.GetFocusedElement(root) is not DependencyObject focused || seen.Exists(element => ReferenceEquals(element, focused)))
            {
                break;
            }

            seen.Add(focused);
            stops.Add(StopOf(focused));
            if (!FocusManager.TryMoveFocus(FocusNavigationDirection.Next, options))
            {
                break;
            }
        }

        if (putBack)
        {
            before?.Focus(FocusState.Programmatic);
        }

        return stops;
    }

    /// <summary>The name a stop of the Tab key is listed under. See <see cref="StopsAsItIs"/>.</summary>
    private static string StopOf(DependencyObject focused)
    {
        var named = focused;
        var isCropHeader = focused is ToggleButton { Name: "ExpanderHeader" };
        if (focused is RadioButton or ListViewItem || isCropHeader)
        {
            for (var parent = VisualTreeHelper.GetParent(focused); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if ((focused is RadioButton && parent is RadioButtons)
                    || (focused is ListViewItem && parent is ListView && AutomationProperties.GetAutomationId(parent) == InspectorRailId)
                    || (isCropHeader && parent is Expander))
                {
                    named = parent;
                    break;
                }
            }
        }

        var id = AutomationProperties.GetAutomationId(named);
        return id.Length > 0 ? id : (focused as FrameworkElement)?.Name is { Length: > 0 } own ? own : focused.GetType().Name;
    }

    /// <summary>The stop the keyboard focus is on, named as <see cref="StopsAsItIs"/> names it. Empty when nothing in the window has it.</summary>
    private string FocusedStop(Editor editor) => OnUi(() =>
        editor.Window.Content?.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused ? StopOf(focused) : string.Empty);

    /// <summary>The stops of the Tab key in the window as it is, with the panel that is on show. See <see cref="StopsAsItIs"/>.</summary>
    private List<string> TabStopsAsItIs(Editor editor) => OnUi(() =>
        editor.Window.Content is FrameworkElement { XamlRoot: { } root } content ? StopsAsItIs(content, root) : []);

    /// <summary>
    /// The stops of the Tab key with each panel of the inspector on show in turn, put together
    /// as one list: the stops up to the rail, the rail, each panel's stops in the order of the
    /// rail, and the stops after the inspector. A window that shows no inspector is walked as
    /// it is. The panel that was on show and the keyboard focus are put back. UI thread.
    /// </summary>
    /// <remarks>
    /// Every walk is one round of the whole window, so the walks begin alike, up to the rail,
    /// and end alike, after the panel. What they have in common at the end is what comes after
    /// the inspector. A walk in which the rail is no stop cannot be cut that way: it is given
    /// whole after a line that says so, so that the check that reads the list fails on it.
    /// </remarks>
    private static List<string> StopsWithEachPanel(Editor editor)
    {
        if (editor.Window.Content is not FrameworkElement { XamlRoot: { } root } content)
        {
            return [];
        }

        if (InspectorIn(content) is not { ViewModel: { IsReady: true } viewModel })
        {
            return StopsAsItIs(content, root);
        }

        var before = FocusManager.GetFocusedElement(root) as UIElement;
        var shown = viewModel.InspectorPanel;
        var panels = StudioInspectorPanels.GetAvailable(viewModel.HasCamera);
        var walks = new List<List<string>>();
        foreach (var panel in panels)
        {
            viewModel.ShowInspectorPanel(panel);
            content.UpdateLayout();
            walks.Add(StopsAsItIs(content, root, putBack: false));
        }

        viewModel.ShowInspectorPanel(shown);
        content.UpdateLayout();
        before?.Focus(FocusState.Programmatic);

        var stops = new List<string>();
        var rails = walks.Select(walk => walk.IndexOf(InspectorRailId)).ToArray();
        if (rails.Any(rail => rail < 0))
        {
            for (var index = 0; index < walks.Count; index++)
            {
                stops.Add($"(with the {StudioInspectorPanels.GetTitle(panels[index])} panel on show the rail {(rails[index] < 0 ? "is no stop" : "is a stop")})");
                stops.AddRange(walks[index]);
            }

            return stops;
        }

        // How many stops every walk ends with alike, after its rail.
        var after = 0;
        while (walks.Select((walk, index) => (walk, index)).All(item => item.walk.Count - after - 1 > rails[item.index])
            && walks.All(walk => walk[^(after + 1)] == walks[0][^(after + 1)]))
        {
            after++;
        }

        stops.AddRange(walks[0].Take(rails[0] + 1));
        for (var index = 0; index < walks.Count; index++)
        {
            stops.AddRange(walks[index].Skip(rails[index] + 1).Take(walks[index].Count - after - rails[index] - 1));
        }

        stops.AddRange(walks[0].Skip(walks[0].Count - after));
        return stops;
    }
}
