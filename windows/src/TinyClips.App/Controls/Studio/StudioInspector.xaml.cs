using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TinyClips.App.Models.Studio;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using Windows.Foundation;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// The right-hand side of the Studio window: a rail of the panels a recording has, on the outer
/// edge, and next to it the one panel that is on show, under its name. Scene, Background, Screen
/// and Camera are how the picture looks; Zoom, Cut and Speed what is edited along the timeline;
/// Audio and Project the rest. The Scene and the Camera panel follow the scene the playhead is
/// in, and the Zoom, Cut and Speed panel the selected zoom, cut or speed change.
/// </summary>
/// <remarks>
/// Which panel is on show is the editor's to say (<see cref="StudioViewModel.InspectorPanel"/>):
/// the rail asks for one, and so does taking hold of something a panel edits. This control shows
/// that panel and marks it on the rail.
/// </remarks>
public sealed partial class StudioInspector : UserControl
{
    // Two groups of the rail are this far apart, and the line between them is in the middle of it.
    private const double RailGroupGap = 9;
    private const double RailLineInset = 12;

    private readonly List<StudioInspectorPanel> _railPanels = [];
    private readonly List<ListViewItem> _railItems = [];
    private readonly List<ListViewItem> _railGroupStarts = [];
    private bool? _railHasCamera;
    private StudioInspectorPanel? _shownPanel;
    private bool _isSyncingRail;
    private bool _isSyncingSwatches;

    public StudioInspector(StudioViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        StudioGlyphRow.Attach(ZoomAddRow);

        // The names come from the editor model, so the lists and what screen readers hear agree.
        foreach (var shape in Enum.GetValues<StudioCameraShape>())
        {
            ShapeChoice.Items.Add(StudioEditorModel.GetShapeName(shape));
        }

        foreach (var anchor in Enum.GetValues<StudioAnchor>())
        {
            PositionChoice.Items.Add(StudioEditorModel.GetAnchorName(anchor));
        }

        foreach (var cutout in Enum.GetValues<StudioCameraCutout>())
        {
            var choice = CreateChoice(StudioEditorModel.GetCutoutName(cutout), $"StudioCameraCutout_{cutout}");
            if (cutout == StudioCameraCutout.Remove)
            {
                // What the note under the choice says once this is chosen, said before it is.
                AutomationProperties.SetHelpText(choice, CutoutRemovedNote.Text);
            }

            CutoutChoice.Items.Add(choice);
        }

        // A rate shows as "2×" and is called "Twice the speed" by a screen reader.
        var rates = StudioEditorModel.SpeedRates;
        for (var index = 0; index < rates.Count; index++)
        {
            var choice = CreateChoice(
                StudioEditorText.GetSpeedRateText(rates[index]),
                string.Create(CultureInfo.InvariantCulture, $"StudioSpeedRate_{index}"));
            AutomationProperties.SetName(choice, StudioEditorText.GetSpeedRateName(rates[index]));
            SpeedRateChoice.Items.Add(choice);
        }

        AddSwatches(SolidSwatches, StudioSwatch.Solid);
        AddSwatches(GradientSwatches, StudioSwatch.Gradient);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public StudioViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SyncSwatches();
        FocusPad.Update(ViewModel.ZoomPad);
        SyncPanels();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(StudioViewModel.BackgroundPresetId))
        {
            SyncSwatches();
        }

        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(StudioViewModel.ZoomPad))
        {
            FocusPad.Update(ViewModel.ZoomPad);
        }

        if (string.IsNullOrEmpty(e.PropertyName))
        {
            SyncPanels();
        }
        else if (e.PropertyName == nameof(StudioViewModel.InspectorPanel))
        {
            ShowPanel(ViewModel.InspectorPanel);
        }
        else if (e.PropertyName is nameof(StudioViewModel.IsScreenCropOpen) or nameof(StudioViewModel.IsCameraCropOpen))
        {
            SyncCropGroups();
        }
    }

    private void OnFocusPadRequested(object? sender, StudioFocusPadPoint e) => ViewModel.SetZoomFocusOnPad(e.X, e.Y);

    // The rail and the panel on show

    /// <summary>Everything that follows the editor without a binding: the rail's items, the panel on show, and the two crop groups.</summary>
    private void SyncPanels()
    {
        // Which panels a recording has is known once it is open.
        if (ViewModel.IsReady)
        {
            BuildRail(ViewModel.HasCamera);
        }

        ShowPanel(ViewModel.InspectorPanel);
        SyncCropGroups();
    }

    /// <summary>
    /// Fills the rail with an item for each panel the recording has, in its three groups. The
    /// first item of the second and of the third group stands off from the one before it, and
    /// a line is drawn in the gap.
    /// </summary>
    private void BuildRail(bool hasCamera)
    {
        if (_railHasCamera == hasCamera)
        {
            return;
        }

        _railHasCamera = hasCamera;
        var itemStyle = (Style)Resources["StudioRailItemStyle"];
        var labelStyle = (Style)Resources["StudioRailLabelStyle"];
        _isSyncingRail = true;
        try
        {
            RailList.Items.Clear();
            _railPanels.Clear();
            _railItems.Clear();
            _railGroupStarts.Clear();
            var groups = StudioInspectorPanels.GetGroups(hasCamera);
            for (var group = 0; group < groups.Count; group++)
            {
                for (var place = 0; place < groups[group].Count; place++)
                {
                    var panel = groups[group][place];
                    var item = CreateRailItem(panel, itemStyle, labelStyle);
                    if (group > 0 && place == 0)
                    {
                        item.Margin = new Thickness(0, RailGroupGap, 0, 0);
                        _railGroupStarts.Add(item);
                    }

                    _railPanels.Add(panel);
                    _railItems.Add(item);
                    RailList.Items.Add(item);
                }
            }
        }
        finally
        {
            _isSyncingRail = false;
        }
    }

    /// <summary>
    /// One item of the rail: the panel's glyph over its name. A screen reader is given the name
    /// and, as help, what the panel holds, which is also the item's tooltip.
    /// </summary>
    private static ListViewItem CreateRailItem(StudioInspectorPanel panel, Style itemStyle, Style labelStyle)
    {
        var title = StudioInspectorPanels.GetTitle(panel);
        var summary = StudioInspectorPanels.GetSummary(panel);

        // The item says its name itself, so screen readers skip what it is drawn with.
        var glyph = new FontIcon { Glyph = StudioInspectorPanels.GetGlyph(panel), FontSize = 16 };
        AutomationProperties.SetAccessibilityView(glyph, AccessibilityView.Raw);
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(glyph);
        content.Children.Add(new TextBlock { Text = title, Style = labelStyle });

        var item = new ListViewItem { Style = itemStyle, Content = content };
        AutomationProperties.SetName(item, title);
        AutomationProperties.SetHelpText(item, summary);
        AutomationProperties.SetAutomationId(item, $"StudioInspectorRail_{panel}");
        ToolTipService.SetToolTip(item, summary);
        return item;
    }

    /// <summary>
    /// Shows a panel under its name, starting at its top, and marks it on the rail.
    /// </summary>
    /// <remarks>
    /// The keyboard focus stays where it is, with two exceptions. A control of the panel that
    /// goes away cannot keep it: left alone, the focus would go to whatever comes next in the
    /// window. And the rail has one stop for the Tab key, the chosen item, so a focus that is in
    /// the rail goes with the choice. Both times the focus is put on the rail's item for the new
    /// panel, before the old panel goes, and the next thing a screen reader says is that panel's name.
    /// </remarks>
    private void ShowPanel(StudioInspectorPanel panel)
    {
        SelectOnRail(panel);
        if (_shownPanel == panel)
        {
            return;
        }

        if (XamlRoot is { } root
            && FocusManager.GetFocusedElement(root) is DependencyObject focused
            && (IsInside(focused, PanelHost) || IsInside(focused, RailList)))
        {
            FocusOnRail(panel, focused is Control { FocusState: not FocusState.Unfocused } control ? control.FocusState : FocusState.Programmatic);
        }

        _shownPanel = panel;
        SetVisible(ScenePanel, panel == StudioInspectorPanel.Scene);
        SetVisible(BackgroundPanel, panel == StudioInspectorPanel.Background);
        SetVisible(ScreenPanel, panel == StudioInspectorPanel.Screen);
        SetVisible(CameraPanel, panel == StudioInspectorPanel.Camera);
        SetVisible(ZoomPanel, panel == StudioInspectorPanel.Zoom);
        SetVisible(CutPanel, panel == StudioInspectorPanel.Cut);
        SetVisible(SpeedPanel, panel == StudioInspectorPanel.Speed);
        SetVisible(AudioPanel, panel == StudioInspectorPanel.Audio);
        SetVisible(ProjectPanel, panel == StudioInspectorPanel.Project);
        PanelTitle.Text = StudioInspectorPanels.GetTitle(panel);
        PanelScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    private static void SetVisible(UIElement element, bool isVisible) =>
        element.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Makes the item of a panel the chosen one of the rail, and brings it into view when the rail is scrolled.</summary>
    private void SelectOnRail(StudioInspectorPanel panel)
    {
        var index = _railPanels.IndexOf(panel);
        if (RailList.SelectedIndex == index)
        {
            return;
        }

        _isSyncingRail = true;
        try
        {
            RailList.SelectedIndex = index;
        }
        finally
        {
            _isSyncingRail = false;
        }

        if (index >= 0 && VisualTreeHelper.GetParent(_railItems[index]) is not null)
        {
            _railItems[index].StartBringIntoView();
        }
    }

    private void FocusOnRail(StudioInspectorPanel panel, FocusState state)
    {
        var index = _railPanels.IndexOf(panel);
        if (index >= 0)
        {
            _railItems[index].Focus(state);
        }
    }

    // A press on an item, the arrow keys, Home and End, and a screen reader that selects an item
    // all come here: the list selects, and the editor is asked for that panel.
    private void OnRailSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingRail)
        {
            return;
        }

        var index = RailList.SelectedIndex;
        if (index >= 0 && index < _railPanels.Count)
        {
            ViewModel.ShowInspectorPanel(_railPanels[index]);
        }

        // A panel is always on show: taking the selection off an item chooses nothing.
        SelectOnRail(ViewModel.InspectorPanel);
    }

    /// <summary>
    /// The keyboard focus alone never changes the panel. A list remembers the item that had the
    /// focus last and selects the item the focus comes to, so a focus that came into the rail
    /// on another item than the chosen one, with the Tab key or put there by the window, would
    /// change the panel. It is sent to the chosen item. A press on an item is a choice, and the
    /// arrow keys move inside the rail: both are left alone.
    /// </summary>
    /// <remarks>
    /// A press is known by how the focus comes, with the pointer, and not by the device that
    /// was used last: that one is whatever the window saw last, also for a focus that the
    /// window or a screen reader moves by itself.
    /// </remarks>
    private void OnRailGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        if (args.FocusState == FocusState.Pointer
            || (args.OldFocusedElement is { } old && IsInside(old, RailList)))
        {
            return;
        }

        var index = _railPanels.IndexOf(ViewModel.InspectorPanel);
        if (index >= 0 && !ReferenceEquals(args.NewFocusedElement, _railItems[index]) && args.TrySetNewFocusedElement(_railItems[index]))
        {
            args.Handled = true;
        }
    }

    private void OnRailSizeChanged(object sender, SizeChangedEventArgs e) => PlaceRailLines();

    /// <summary>Draws the line between two groups of the rail in the gap over the item that starts the later one.</summary>
    private void PlaceRailLines()
    {
        Rectangle[] lines = [RailLineOne, RailLineTwo];
        var width = RailList.ActualWidth - (RailLineInset * 2);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (index >= _railGroupStarts.Count || !(width > 0) || VisualTreeHelper.GetParent(_railGroupStarts[index]) is null)
            {
                line.Visibility = Visibility.Collapsed;
                continue;
            }

            // The item's own top. Its margin, which is the gap, is over it.
            var top = _railGroupStarts[index].TransformToVisual(RailList).TransformPoint(default).Y;
            line.Width = width;
            Canvas.SetLeft(line, RailLineInset);
            Canvas.SetTop(line, Math.Round(top - ((RailGroupGap + 1) / 2)));
            line.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Whether an element is the rail's chosen item. The window asks when Space goes down: the
    /// list takes Space to select the item that has the focus, which the chosen item is
    /// already, so there the key would end with the list and do nothing. On another item of the
    /// rail, which the focus reaches with Ctrl and an arrow key, Space is the list's and chooses.
    /// </summary>
    internal bool IsChosenRailItem(object? focused)
    {
        var index = RailList.SelectedIndex;
        return index >= 0 && index < _railItems.Count && ReferenceEquals(focused, _railItems[index]);
    }

    // After a key

    /// <summary>
    /// What has the keyboard focus, when that is a control of the panel on show, and null
    /// otherwise. The window asks before it runs what a key means, and hands the answer to
    /// <see cref="KeepFocusAfterKey"/> when the key has done its work.
    /// </summary>
    internal DependencyObject? AsPanelControl(object? focused) =>
        focused is DependencyObject element && IsInside(element, PanelHost) ? element : null;

    /// <summary>
    /// What a key did may have taken away the control that had the keyboard focus: Delete takes
    /// the selected zoom's controls with the zoom, Undo and Redo may do the same, and a layout
    /// key hides what the camera has in another layout. The focus has then gone to whatever
    /// comes next in the window, which is past the inspector. It is put on the rail's item for
    /// the panel on show, where it also goes when a whole panel goes away.
    /// </summary>
    /// <remarks>
    /// A control that is still there has kept the focus, or the key gave the focus away on
    /// purpose: both are left alone. So is a focus that went on to another control of the
    /// inspector, which is the next one of the same panel.
    /// </remarks>
    internal void KeepFocusAfterKey(DependencyObject? held)
    {
        if (held is null || CanHaveFocus(held) || XamlRoot is not { } root)
        {
            return;
        }

        if (FocusManager.GetFocusedElement(root) is DependencyObject focused
            && !ReferenceEquals(focused, held)
            && IsInside(focused, this))
        {
            return;
        }

        FocusOnRail(ViewModel.InspectorPanel, FocusState.Keyboard);
    }

    /// <summary>Whether a control of a panel is still shown and switched on, with everything around it up to this control.</summary>
    private bool CanHaveFocus(DependencyObject element)
    {
        for (var at = element; at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is UIElement { Visibility: Visibility.Collapsed } or Control { IsEnabled: false })
            {
                return false;
            }

            if (ReferenceEquals(at, this))
            {
                return true;
            }
        }

        // No longer under this control at all.
        return false;
    }

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var at = element; at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (ReferenceEquals(at, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    // The crop groups

    /// <summary>Opens and closes the two crop groups as the editor says.</summary>
    private void SyncCropGroups()
    {
        SyncCropGroup(ScreenCropGroup, ScreenCropControls, ViewModel.IsScreenCropOpen);
        SyncCropGroup(CameraCropGroup, CameraCropControls, ViewModel.IsCameraCropOpen);
    }

    /// <summary>
    /// A group that still follows its crop closes when the crop goes, which can be with the
    /// keyboard focus on one of its sliders: a screen reader can press Reset crop without
    /// putting the focus on it. The focus then goes to the group's header, before the group
    /// closes. Where Reset crop had the focus itself it has lost it by then, because it was
    /// switched off first: its own handler puts the focus on the header.
    /// </summary>
    private void SyncCropGroup(Expander group, UIElement controls, bool isOpen)
    {
        if (group.IsExpanded == isOpen)
        {
            return;
        }

        if (!isOpen)
        {
            FocusHeaderFrom(controls, group);
        }

        group.IsExpanded = isOpen;
    }

    /// <summary>
    /// Sends a keyboard focus that is on one of a crop group's sliders, or on its Reset crop,
    /// to the group's header. A group that closes takes those away, a sixth of a second after
    /// it has said that it closes, and the focus would go with them to whatever comes next in
    /// the window.
    /// </summary>
    private void FocusHeaderFrom(UIElement controls, Expander group)
    {
        if (XamlRoot is { } root
            && FocusManager.GetFocusedElement(root) is DependencyObject focused
            && IsInside(focused, controls))
        {
            FocusHeader(group, focused is Control { FocusState: not FocusState.Unfocused } control ? control.FocusState : FocusState.Programmatic);
        }
    }

    // A press on a group's header. The group tells the same when this control opens or closes
    // it, which the editor knows already and takes as nothing new. A group can also be closed
    // from outside while the keyboard focus is inside it, as a screen reader does that
    // collapses it without going to its header: the focus is sent to the header then too.

    private void OnScreenCropExpanding(Expander sender, ExpanderExpandingEventArgs args) => ViewModel.SetScreenCropOpen(true);

    private void OnScreenCropCollapsed(Expander sender, ExpanderCollapsedEventArgs args)
    {
        FocusHeaderFrom(ScreenCropControls, ScreenCropGroup);
        ViewModel.SetScreenCropOpen(false);
    }

    private void OnCameraCropExpanding(Expander sender, ExpanderExpandingEventArgs args) => ViewModel.SetCameraCropOpen(true);

    private void OnCameraCropCollapsed(Expander sender, ExpanderCollapsedEventArgs args)
    {
        FocusHeaderFrom(CameraCropControls, CameraCropGroup);
        ViewModel.SetCameraCropOpen(false);
    }

    // A button that its own action switches off, or hides, would leave the keyboard focus to
    // whatever comes next in the window. Each of these moves it to where a person would go on
    // from instead. A button that was pressed without having the focus leaves the focus alone.

    private void OnPreviousSceneClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.StepToPreviousScene();
        if (!ViewModel.CanShowPreviousScene)
        {
            StudioFocus.Move(focus, NextSceneButton, SceneSectionSplitButton);
        }
    }

    private void OnNextSceneClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.StepToNextScene();
        if (!ViewModel.CanShowNextScene)
        {
            StudioFocus.Move(focus, PreviousSceneButton, SceneSectionSplitButton);
        }
    }

    // A paused playhead goes on to where the new scene has been entered, and no scene can be
    // split again that close to its end. The new scene is never the first, so Previous works.
    private void OnSplitSceneClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.SplitSceneAtPlayhead();
        if (!ViewModel.CanSplitSceneAtPlayhead)
        {
            StudioFocus.Move(focus, PreviousSceneButton, NextSceneButton, SelectedLayoutChoice);
        }
    }

    // Delete scene stays while there is another scene to delete. With one scene left it is gone,
    // and what there is to do next is to split that scene, or to choose its layout.
    private void OnDeleteSceneClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.RemoveCurrentScene();
        if (!ViewModel.IsDeleteSceneVisible)
        {
            StudioFocus.Move(focus, SceneSectionSplitButton, SelectedLayoutChoice);
        }
    }

    private void OnPreviousZoomClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.ShowPreviousZoom();
        if (!ViewModel.CanSelectPreviousZoom)
        {
            StudioFocus.Move(focus, NextZoomButton, ZoomSectionAddButton);
        }
    }

    private void OnNextZoomClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.ShowNextZoom();
        if (!ViewModel.CanSelectNextZoom)
        {
            StudioFocus.Move(focus, PreviousZoomButton, ZoomSectionAddButton);
        }
    }

    private void OnRemoveSuggestionsClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.RemoveZoomSuggestions();
        if (!ViewModel.HasSuggestedZooms)
        {
            StudioFocus.Move(focus, SuggestZoomsButton, ZoomSectionAddButton);
        }
    }

    private void OnDeleteZoomClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.RemoveSelectedZoom();
        if (!ViewModel.HasSelectedZoom)
        {
            StudioFocus.Move(focus, NextZoomButton, PreviousZoomButton, ZoomSectionAddButton);
        }
    }

    private void OnPreviousCutClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.ShowPreviousCut();
        if (!ViewModel.CanSelectPreviousCut)
        {
            StudioFocus.Move(focus, NextCutButton, CutSectionAddButton);
        }
    }

    private void OnNextCutClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.ShowNextCut();
        if (!ViewModel.CanSelectNextCut)
        {
            StudioFocus.Move(focus, PreviousCutButton, CutSectionAddButton);
        }
    }

    private void OnDeleteCutClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.RemoveSelectedCut();
        if (!ViewModel.HasSelectedCut)
        {
            StudioFocus.Move(focus, NextCutButton, PreviousCutButton, CutSectionAddButton);
        }
    }

    private void OnPreviousSpeedClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.ShowPreviousSpeed();
        if (!ViewModel.CanSelectPreviousSpeed)
        {
            StudioFocus.Move(focus, NextSpeedButton, SpeedSectionAddButton);
        }
    }

    private void OnNextSpeedClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.ShowNextSpeed();
        if (!ViewModel.CanSelectNextSpeed)
        {
            StudioFocus.Move(focus, PreviousSpeedButton, SpeedSectionAddButton);
        }
    }

    private void OnDeleteSpeedClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.RemoveSelectedSpeed();
        if (!ViewModel.HasSelectedSpeed)
        {
            StudioFocus.Move(focus, NextSpeedButton, PreviousSpeedButton, SpeedSectionAddButton);
        }
    }

    // Reset crop switches itself off. In a group that stays open, as one does whose header was
    // pressed or in which a slider was moved, the focus goes to the first of its sliders. A
    // group that was open only because of the crop closes with it, and the focus goes to its
    // header. The button is switched off before the group closes, so the focus has left it for
    // whatever comes next in the window by the time either is done here.

    private void OnResetScreenCropClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.ResetScreenCrop();
        if (!ViewModel.CanResetScreenCrop && focus != FocusState.Unfocused)
        {
            if (ViewModel.IsScreenCropOpen)
            {
                ScreenCropLeftRow.FocusSlider(focus);
            }
            else
            {
                FocusHeader(ScreenCropGroup, focus);
            }
        }
    }

    private void OnResetCameraCropClick(object sender, RoutedEventArgs e)
    {
        var focus = StudioFocus.StateOf(sender);
        ViewModel.ResetCameraCrop();
        if (!ViewModel.CanResetCameraCrop && focus != FocusState.Unfocused)
        {
            if (ViewModel.IsCameraCropOpen)
            {
                CameraCropLeftRow.FocusSlider(focus);
            }
            else
            {
                FocusHeader(CameraCropGroup, focus);
            }
        }
    }

    /// <summary>Puts the keyboard focus on the header of a crop group, which is the first thing in it that takes the focus.</summary>
    private static void FocusHeader(Expander group, FocusState state)
    {
        if (FocusManager.FindFirstFocusableElement(group) is Control header)
        {
            header.Focus(state);
        }
    }

    /// <summary>The one of the four layout choices that is chosen, which is where the Tab key stops among them.</summary>
    private RadioButton SelectedLayoutChoice => ViewModel.LayoutIndex switch
    {
        (int)StudioLayout.Screen => LayoutScreenChoice,
        (int)StudioLayout.SideBySide => LayoutSideBySideChoice,
        (int)StudioLayout.Camera => LayoutCameraChoice,
        _ => LayoutBubbleChoice,
    };

    /// <summary>
    /// One choice of a group that stands in columns. A choice is as wide as what it says, and
    /// not the width a radio button has otherwise, so that three of them fit next to each other.
    /// </summary>
    private static RadioButton CreateChoice(string text, string automationId)
    {
        var choice = new RadioButton { Content = text, MinWidth = 0 };
        AutomationProperties.SetAutomationId(choice, automationId);
        return choice;
    }

    private void AddSwatches(GridView grid, IReadOnlyList<StudioSwatch> swatches)
    {
        var itemStyle = (Style)Resources["StudioSwatchItemStyle"];
        var fillStyle = (Style)Resources["StudioSwatchFillStyle"];
        foreach (var swatch in swatches)
        {
            var item = new GridViewItem
            {
                Style = itemStyle,
                Tag = swatch,
                Content = new Border { Style = fillStyle, Background = CreateFill(swatch) },
            };
            AutomationProperties.SetName(item, swatch.AccessibleName);
            AutomationProperties.SetAutomationId(item, $"StudioSwatch_{swatch.Id}");
            ToolTipService.SetToolTip(item, swatch.Label);
            grid.Items.Add(item);
        }
    }

    private static Brush CreateFill(StudioSwatch swatch) =>
        swatch.Secondary is { } secondary
            ? new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = swatch.Primary, Offset = 0 },
                    new GradientStop { Color = secondary, Offset = 1 },
                },
            }
            : new SolidColorBrush(swatch.Primary);

    private void OnSwatchSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSwatches)
        {
            return;
        }

        if (e.AddedItems.Count > 0 && e.AddedItems[0] is GridViewItem { Tag: StudioSwatch swatch })
        {
            ViewModel.SelectSwatch(swatch);
        }

        // The two lists share one choice, and taking the selection off a swatch chooses nothing.
        SyncSwatches();
    }

    private void SyncSwatches()
    {
        _isSyncingSwatches = true;
        try
        {
            var presetId = ViewModel.BackgroundPresetId;
            Select(SolidSwatches, presetId);
            Select(GradientSwatches, presetId);
        }
        finally
        {
            _isSyncingSwatches = false;
        }
    }

    private static void Select(GridView grid, string? presetId)
    {
        GridViewItem? selected = null;
        foreach (var candidate in grid.Items)
        {
            if (candidate is GridViewItem { Tag: StudioSwatch swatch } item
                && string.Equals(swatch.Id, presetId, StringComparison.Ordinal))
            {
                selected = item;
                break;
            }
        }

        if (!ReferenceEquals(grid.SelectedItem, selected))
        {
            grid.SelectedItem = selected;
        }
    }
}
