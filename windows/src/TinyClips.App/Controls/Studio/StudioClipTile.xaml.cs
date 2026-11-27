using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// One clip on the primary clip strip: its name and how long it plays on the timeline. The
/// strip places it and handles what is done to it; to screen readers it is an item of the
/// strip's list, which can be selected and pressed. A selected clip is the one the inspectors
/// edit. It has a visual state called Selected and one called Unselected.
/// </summary>
public sealed partial class StudioClipTile : UserControl
{
    private readonly StudioClipTrack _track;
    private StudioClipStripItem? _item;
    private bool _isSelected;

    internal StudioClipTile(StudioClipTrack track)
    {
        _track = track;
        InitializeComponent();
        CanDrag = true;

        // A state gone to before the tile is in the window is not always shown.
        Loaded += (_, _) => ShowSelection();
        Tapped += OnTapped;
        KeyDown += OnKeyDown;
        DragStarting += (_, e) => _track.BeginClipDrag(this, e);
        DropCompleted += (_, _) => _track.EndClipDrag();
    }

    internal StudioClipTrack Track => _track;

    /// <summary>What the tile shows: its place in the order is in it.</summary>
    internal StudioClipStripItem Item => _item ?? throw new InvalidOperationException("The tile shows no clip yet.");

    internal bool HasItem => _item is not null;

    internal bool IsSelected => _isSelected;

    /// <summary>What a screen reader says for the clip, such as "Clip 2 of 3: Intro, 4.5 seconds".</summary>
    internal string Description => _item?.AccessibleName ?? string.Empty;

    /// <summary>Shows a clip in a tile of the given width, and tells screen readers what changed.</summary>
    internal void Update(StudioClipStripItem item, double width)
    {
        var old = Description;
        var indexChanged = _item is null || _item.Index != item.Index;
        _item = item;
        Width = width;
        NameText.Text = item.Name;
        DurationText.Text = item.DurationText;
        ToolTipService.SetToolTip(this, item.Name);

        if (indexChanged)
        {
            AutomationProperties.SetAutomationId(this, string.Create(CultureInfo.InvariantCulture, $"StudioClip_{item.Index}"));
        }

        // Only the selected clip is a stop of the Tab key: the arrow keys go from clip to clip.
        IsTabStop = item.IsSelected;
        SetSelected(item.IsSelected);

        var description = Description;
        if (description != old
            && AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged)
            && FrameworkElementAutomationPeer.FromElement(this) is { } peer)
        {
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, old, description);
        }
    }

    /// <summary>Marks the place a clip that is being dragged would go, before this clip, after it, or nowhere.</summary>
    internal void ShowDropEdge(StudioClipDropEdge edge)
    {
        DropBefore.Visibility = edge == StudioClipDropEdge.Before ? Visibility.Visible : Visibility.Collapsed;
        DropAfter.Visibility = edge == StudioClipDropEdge.After ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetSelected(bool isSelected)
    {
        if (isSelected == _isSelected)
        {
            return;
        }

        _isSelected = isSelected;
        ShowSelection();
        if (FrameworkElementAutomationPeer.FromElement(this) is not { } peer)
        {
            return;
        }

        if (AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged))
        {
            peer.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, !isSelected, isSelected);
        }

        if (isSelected && AutomationPeer.ListenerExists(AutomationEvents.SelectionItemPatternOnElementSelected))
        {
            peer.RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
        }
    }

    private void ShowSelection() =>
        VisualStateManager.GoToState(this, _isSelected ? "Selected" : "Unselected", false);

    private void OnTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        _track.SelectClip(Item.Id);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_item is not null && _track.HandleKey(this, e.Key))
        {
            e.Handled = true;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StudioClipTileAutomationPeer(this);
}

/// <summary>Where a clip that is dragged would go, as one clip shows it.</summary>
internal enum StudioClipDropEdge
{
    None,
    Before,
    After,
}

/// <summary>
/// Makes a <see cref="StudioClipTile"/> an item of the clip strip's list to screen readers. It is
/// named as the editor words the clip, says whether it is the selected one, can be selected, and
/// can be pressed, which selects it and puts the playhead on its first picture.
/// </summary>
public sealed partial class StudioClipTileAutomationPeer(StudioClipTile owner)
    : FrameworkElementAutomationPeer(owner), ISelectionItemProvider, IInvokeProvider
{
    private StudioClipTile Tile => (StudioClipTile)Owner;

    public bool IsSelected => Tile.IsSelected;

    public IRawElementProviderSimple SelectionContainer =>
        ProviderFromPeer(CreatePeerForElement(Tile.Track));

    public void Select()
    {
        ThrowIfDisabled();
        Tile.Track.SelectClip(Tile.Item.Id);
    }

    public void AddToSelection()
    {
        ThrowIfDisabled();
        if (!Tile.IsSelected)
        {
            throw new InvalidOperationException("Only one clip can be selected at a time.");
        }
    }

    public void RemoveFromSelection()
    {
        ThrowIfDisabled();
        if (Tile.IsSelected)
        {
            throw new InvalidOperationException("One clip is always selected.");
        }
    }

    public void Invoke()
    {
        ThrowIfDisabled();
        Tile.Track.SelectClip(Tile.Item.Id);
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

    protected override string GetClassNameCore() => nameof(StudioClipTile);

    protected override string GetNameCore() => Tile.Description;

    // The text inside the tile is all in its name.
    protected override IList<AutomationPeer> GetChildrenCore() => [];

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface is PatternInterface.SelectionItem or PatternInterface.Invoke
            ? this
            : base.GetPatternCore(patternInterface);

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    private void ThrowIfDisabled()
    {
        if (!Tile.IsEnabled)
        {
            throw new ElementNotEnabledException();
        }
    }
}
