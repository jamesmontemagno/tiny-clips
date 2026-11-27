using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

namespace TinyClips.App.Controls.Studio;

/// <summary>Where a video that is added to the timeline comes from.</summary>
internal enum StudioClipAddSource
{
    /// <summary>The window asks for video files.</summary>
    Files,

    /// <summary>The window offers the videos of the Clips Library.</summary>
    Library,

    /// <summary>The window records another segment.</summary>
    Recording,

    /// <summary>Files were dropped on the strip: <see cref="StudioClipAddRequest.Paths"/> has them.</summary>
    Dropped,
}

/// <summary>Something the strip asks the window to bring into the timeline, and where.</summary>
internal sealed record StudioClipAddRequest(
    StudioClipAddSource Source,
    StudioClipPlacement Placement,
    IReadOnlyList<string>? Paths = null);

/// <summary>
/// The clips of the timeline in a row, in project time, with a join between each two of them.
/// A tile is a clip. Clips are chosen, moved and removed here, in the keyboard's terms and in the
/// pointer's, and joins are set from their buttons. Files dropped on the row are asked of the
/// window. To screen readers the row is a list of clips, one of which is selected.
/// </summary>
/// <remarks>
/// Tiles and joins are kept from one update to the next and only given other clips, so the
/// keyboard's focus stays where it is when a clip is chosen, moved or removed.
/// </remarks>
public sealed partial class StudioClipTrack : UserControl
{
    // A tile is as wide as its clip is long, within limits that keep a very short clip pressable
    // and a very long one from taking the whole row.
    private const double PixelsPerSecond = 28;
    private const double MinTileWidth = 104;
    private const double MaxTileWidth = 420;

    private readonly StudioViewModel _viewModel;
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _row;
    private readonly List<StudioClipTile> _tiles = [];
    private readonly List<Button> _joins = [];
    private IReadOnlyList<StudioClipStripItem>? _shown;
    private bool _joinsAreEnabled = true;
    private string? _dragClipId;
    private int _dropBoundary = -1;

    internal StudioClipTrack(StudioViewModel viewModel)
    {
        _viewModel = viewModel;
        IsTabStop = false;
        AllowDrop = true;

        _row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            Padding = new Thickness(0, 2, 0, 6),
        };
        _scroll = new ScrollViewer
        {
            Content = _row,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            IsTabStop = false,
        };
        Content = _scroll;
        AutomationProperties.SetName(this, "Clips");
        AutomationProperties.SetAutomationId(this, "StudioClipTrack");
        AutomationProperties.SetHelpText(this, "The clips of the timeline, in order. Choose one to edit it.");

        DragOver += OnDragOver;
        DragLeave += (_, _) => ClearDropEdge();
        Drop += OnDrop;
    }

    /// <summary>What a request to add clips asks of the window.</summary>
    internal event EventHandler<StudioClipAddRequest>? AddRequested;

    internal StudioClipTile? SelectedTile => _tiles.FirstOrDefault(static tile => tile.IsSelected);

    /// <summary>The clip the inspectors edit, as the strip last showed it.</summary>
    internal StudioClipStripItem? SelectedItem => SelectedTile?.Item;

    /// <summary>Shows the clips of the editor: as many tiles as it has clips, and a join between each two.</summary>
    internal void Update()
    {
        var strip = _viewModel.ClipStrip;
        var canEdit = _viewModel.CanEditClips;
        if (ReferenceEquals(strip, _shown) && canEdit == _joinsAreEnabled)
        {
            return;
        }

        _shown = strip;
        _joinsAreEnabled = canEdit;
        while (_tiles.Count < strip.Count)
        {
            AddTile();
        }

        while (_tiles.Count > strip.Count)
        {
            RemoveLastTile();
        }

        for (var index = 0; index < strip.Count; index++)
        {
            var item = strip[index];
            _tiles[index].Update(item, GetTileWidth(item));
            if (index > 0)
            {
                UpdateJoin(_joins[index - 1], strip[index - 1], item, canEdit);
            }
        }

        if (SelectedTile is { } selected)
        {
            DispatcherQueue.TryEnqueue(() => selected.StartBringIntoView());
        }
    }

    /// <summary>Selects a clip, and keeps the keyboard's focus on it if it was on a clip.</summary>
    internal void SelectClip(string clipId)
    {
        var hadFocus = HasFocusOnClip();
        _viewModel.SelectClip(clipId);
        if (hadFocus)
        {
            FocusClip(clipId);
        }
    }

    internal bool FocusClip(string clipId) =>
        _tiles.FirstOrDefault(tile => tile.HasItem && tile.Item.Id == clipId)?.Focus(FocusState.Keyboard) ?? false;

    /// <summary>Puts the keyboard's focus on the selected clip, or the first.</summary>
    internal bool FocusSelected() =>
        (SelectedTile ?? _tiles.FirstOrDefault())?.Focus(FocusState.Keyboard) ?? false;

    private bool HasFocusOnClip() =>
        XamlRoot is { } root && FocusManager.GetFocusedElement(root) is StudioClipTile tile && tile.Track == this;

    /// <summary>The keys of a focused clip. False where the window is to have the key.</summary>
    internal bool HandleKey(StudioClipTile tile, VirtualKey key)
    {
        var item = tile.Item;
        var alt = IsKeyDown(VirtualKey.Menu);
        var shift = IsKeyDown(VirtualKey.Shift);
        var control = IsKeyDown(VirtualKey.Control);
        if (control)
        {
            return false;
        }

        switch (key)
        {
            case VirtualKey.Left or VirtualKey.Right when alt && shift:
            {
                var places = key == VirtualKey.Left ? -1 : 1;
                if (_viewModel.CanEditClips && _viewModel.MoveClipBy(item.Id, places))
                {
                    FocusClip(item.Id);
                }

                return true;
            }

            case VirtualKey.Left or VirtualKey.Right when !alt && !shift:
                MoveSelection(item.Index + (key == VirtualKey.Left ? -1 : 1));
                return true;

            case VirtualKey.Home when !alt && !shift:
                MoveSelection(0);
                return true;

            case VirtualKey.End when !alt && !shift:
                MoveSelection(item.Count - 1);
                return true;

            case VirtualKey.Delete when !alt && !shift:
                if (_viewModel.CanEditClips && item.Count > 1)
                {
                    RemoveClip(item);
                }

                return true;

            case VirtualKey.Enter when !alt && !shift:
                SelectClip(item.Id);
                return true;

            default:
                return false;
        }
    }

    private void MoveSelection(int index)
    {
        var strip = _viewModel.ClipStrip;
        var target = Math.Clamp(index, 0, strip.Count - 1);
        SelectClip(strip[target].Id);
    }

    private void RemoveClip(StudioClipStripItem item)
    {
        var hadFocus = HasFocusOnClip();
        if (!_viewModel.RemoveClip(item.Id) || !hadFocus)
        {
            return;
        }

        // What takes the removed clip's place in the row, or the one before it, is the selected one.
        FocusSelected();
    }

    /// <summary>Fills the menu of what is done to a clip: the same for a tile's menu and the strip's own.</summary>
    internal void FillClipMenu(MenuFlyout flyout, StudioClipStripItem item)
    {
        flyout.Items.Clear();
        var canEdit = _viewModel.CanEditClips;
        var split = new MenuFlyoutItem
        {
            Text = "Split clip at playhead",
            Icon = new FontIcon { Glyph = StudioGlyphs.SplitScene },
            IsEnabled = canEdit && _viewModel.CanSplitClip,
        };
        AutomationProperties.SetAutomationId(split, "StudioClipSplitItem");
        split.Click += (_, _) => _viewModel.SplitClipAtPlayhead();
        flyout.Items.Add(split);

        var earlier = new MenuFlyoutItem
        {
            Text = "Move earlier",
            KeyboardAcceleratorTextOverride = "Alt+Shift+Left",
            IsEnabled = canEdit && item.Index > 0,
        };
        AutomationProperties.SetAutomationId(earlier, "StudioClipMoveEarlierItem");
        earlier.Click += (_, _) => MoveBy(item, -1);
        flyout.Items.Add(earlier);

        var later = new MenuFlyoutItem
        {
            Text = "Move later",
            KeyboardAcceleratorTextOverride = "Alt+Shift+Right",
            IsEnabled = canEdit && item.Index < item.Count - 1,
        };
        AutomationProperties.SetAutomationId(later, "StudioClipMoveLaterItem");
        later.Click += (_, _) => MoveBy(item, 1);
        flyout.Items.Add(later);

        flyout.Items.Add(new MenuFlyoutSeparator());
        var remove = new MenuFlyoutItem
        {
            Text = "Remove from timeline",
            KeyboardAcceleratorTextOverride = "Delete",
            Icon = new FontIcon { Glyph = StudioGlyphs.Delete },
            IsEnabled = canEdit && item.Count > 1,
        };
        AutomationProperties.SetAutomationId(remove, "StudioClipRemoveItem");
        remove.Click += (_, _) => RemoveClip(item);
        flyout.Items.Add(remove);
    }

    private void MoveBy(StudioClipStripItem item, int places)
    {
        var hadFocus = HasFocusOnClip();
        if (_viewModel.MoveClipBy(item.Id, places) && hadFocus)
        {
            FocusClip(item.Id);
        }
    }

    internal StudioClipTile? FindTile(string clipId) =>
        _tiles.FirstOrDefault(tile => tile.HasItem && tile.Item.Id == clipId);

    // A tile's menu is for the tile it is on, which that menu makes the selected one.
    private void AddTile()
    {
        var tile = new StudioClipTile(this);
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            if (!tile.HasItem)
            {
                return;
            }

            if (!tile.IsSelected)
            {
                _viewModel.SelectClip(tile.Item.Id);
            }

            FillClipMenu(menu, tile.Item);
        };
        tile.ContextFlyout = menu;

        if (_tiles.Count > 0)
        {
            var join = CreateJoin();
            _joins.Add(join);
            _row.Children.Add(join);
        }

        _tiles.Add(tile);
        _row.Children.Add(tile);
    }

    private void RemoveLastTile()
    {
        _row.Children.RemoveAt(_row.Children.Count - 1);
        _tiles.RemoveAt(_tiles.Count - 1);
        if (_joins.Count > 0)
        {
            _row.Children.RemoveAt(_row.Children.Count - 1);
            _joins.RemoveAt(_joins.Count - 1);
        }
    }

    private static double GetTileWidth(StudioClipStripItem item) =>
        Math.Clamp(Math.Round(item.Duration * PixelsPerSecond), MinTileWidth, MaxTileWidth);

    // What comes in at a clip's start: a cut, a fade through a color, or a dissolve.
    private Button CreateJoin()
    {
        var join = new Button
        {
            Width = 32,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(2, 0, 2, 0),
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Content = new FontIcon
            {
                FontSize = 14,
                Glyph = StudioClipStrip.GetTransitionGlyph(StudioClipTransition.Cut),
            },
        };
        AutomationProperties.SetHelpText(join, "Choose how the next clip comes in: a cut, a fade, or a cross-dissolve.");
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        menu.Opening += (_, _) => FillJoinMenu(menu, join);
        join.Flyout = menu;
        return join;
    }

    private void UpdateJoin(Button join, StudioClipStripItem previous, StudioClipStripItem next, bool canEdit)
    {
        var transition = next.TransitionIn;
        join.Tag = next;
        join.IsEnabled = canEdit;
        ((FontIcon)join.Content).Glyph = StudioClipStrip.GetTransitionGlyph(transition);
        var name = StudioClipStrip.GetTransitionName(transition);
        AutomationProperties.SetName(
            join,
            $"{StudioClipStrip.GetJoinAccessibleName(previous.Name, next.Name)}, {name}");
        AutomationProperties.SetAutomationId(
            join,
            string.Create(CultureInfo.InvariantCulture, $"StudioClipJoin_{next.Index}"));
        ToolTipService.SetToolTip(join, name);
    }

    private void FillJoinMenu(MenuFlyout menu, Button join)
    {
        menu.Items.Clear();
        if (join.Tag is not StudioClipStripItem next)
        {
            return;
        }

        foreach (var (transition, text) in new (StudioClipTransition, string)[]
        {
            (StudioClipTransition.Cut, StudioClipStrip.GetTransitionName(StudioClipTransition.Cut)),
            (StudioClipTransition.FadeThroughBlack, StudioClipStrip.GetTransitionName(StudioClipTransition.FadeThroughBlack)),
            (StudioClipTransition.FadeThroughWhite, StudioClipStrip.GetTransitionName(StudioClipTransition.FadeThroughWhite)),
            (StudioClipTransition.CrossDissolve, StudioClipStrip.CrossDissolveHint),
        })
        {
            var choice = new RadioMenuFlyoutItem
            {
                Text = text,
                GroupName = "StudioClipTransition",
                IsChecked = transition == next.TransitionIn,
            };
            AutomationProperties.SetAutomationId(
                choice,
                string.Create(CultureInfo.InvariantCulture, $"StudioClipTransition_{transition}"));
            var chosen = transition;
            choice.Click += (_, _) => _viewModel.SetClipTransition(next.Id, chosen);
            menu.Items.Add(choice);
        }
    }

    // Dragging clips: a clip of the row is moved to a place between two clips, and files from the
    // file explorer are added at that place. The place is marked at the edge of the nearer clip.

    internal void BeginClipDrag(StudioClipTile tile, DragStartingEventArgs e)
    {
        if (!tile.HasItem || !_viewModel.CanEditClips || _viewModel.ClipStrip.Count < 2)
        {
            e.Cancel = true;
            return;
        }

        _dragClipId = tile.Item.Id;
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText(tile.Item.Name);
        SelectClip(tile.Item.Id);
    }

    internal void EndClipDrag()
    {
        _dragClipId = null;
        ClearDropEdge();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (_dragClipId is not null && _viewModel.CanEditClips)
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = "Move clip";
        }
        else if (_dragClipId is null && _viewModel.CanAddClips && !_viewModel.IsAddingClips
            && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to timeline";
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ClearDropEdge();
            return;
        }

        e.DragUIOverride.IsGlyphVisible = false;
        e.DragUIOverride.IsCaptionVisible = true;
        ShowDropEdge(GetBoundary(e.GetPosition(_row).X));
        e.Handled = true;
    }

    // Boundary b is before clip b, and the count is after the last: the one whose clips are
    // each side of the pointer by their middles.
    private int GetBoundary(double x)
    {
        var boundary = 0;
        foreach (var tile in _tiles)
        {
            var left = tile.TransformToVisual(_row).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
            if (x >= left + (tile.ActualWidth / 2))
            {
                boundary++;
            }
        }

        return boundary;
    }

    private void ShowDropEdge(int boundary)
    {
        if (boundary == _dropBoundary)
        {
            return;
        }

        ClearDropEdge();
        _dropBoundary = boundary;
        if (_tiles.Count == 0)
        {
            return;
        }

        if (boundary < _tiles.Count)
        {
            _tiles[boundary].ShowDropEdge(StudioClipDropEdge.Before);
        }
        else
        {
            _tiles[^1].ShowDropEdge(StudioClipDropEdge.After);
        }
    }

    private void ClearDropEdge()
    {
        _dropBoundary = -1;
        foreach (var tile in _tiles)
        {
            tile.ShowDropEdge(StudioClipDropEdge.None);
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var boundary = _dropBoundary >= 0 ? _dropBoundary : GetBoundary(e.GetPosition(_row).X);
        ClearDropEdge();
        if (_dragClipId is { } moving)
        {
            e.Handled = true;
            var from = _viewModel.ClipStrip.ToList().FindIndex(item => item.Id == moving);
            if (from >= 0 && _viewModel.CanEditClips)
            {
                // Taking the clip out of the row moves what is after it a place earlier.
                var to = boundary > from ? boundary - 1 : boundary;
                if (to != from && _viewModel.MoveClip(moving, to))
                {
                    FocusClip(moving);
                }
            }

            return;
        }

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items
                .OfType<StorageFile>()
                .Select(static file => file.Path)
                .Where(static path => path.Length > 0)
                .ToList();
            var videos = paths
                .Where(static path => ActivatedFile.VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (videos.Count == 0)
            {
                _viewModel.ShowError(paths.Count == 0
                    ? "Drop video files from File Explorer to add them to the timeline."
                    : "Studio adds MP4 and MOV videos to the timeline.");
                return;
            }

            AddRequested?.Invoke(this, new StudioClipAddRequest(
                StudioClipAddSource.Dropped,
                StudioClipPlacement.AtIndex(boundary),
                videos));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _viewModel.ShowError($"The dropped files could not be read: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    protected override AutomationPeer OnCreateAutomationPeer() => new StudioClipTrackAutomationPeer(this);
}

/// <summary>
/// Makes the row of clips a list to screen readers, of which one clip is selected. The joins
/// between the clips are its buttons.
/// </summary>
public sealed partial class StudioClipTrackAutomationPeer(StudioClipTrack owner)
    : FrameworkElementAutomationPeer(owner), ISelectionProvider
{
    private StudioClipTrack Track => (StudioClipTrack)Owner;

    public bool CanSelectMultiple => false;

    public bool IsSelectionRequired => true;

    public IRawElementProviderSimple[] GetSelection() =>
        Track.SelectedTile is { } tile ? [ProviderFromPeer(CreatePeerForElement(tile))] : [];

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

    protected override string GetClassNameCore() => nameof(StudioClipTrack);

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Selection ? this : base.GetPatternCore(patternInterface);

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;
}
