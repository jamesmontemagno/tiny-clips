using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// One zoom on the Studio zoom lane: a block that shows how much the zoom magnifies, a mark when
/// it follows the pointer and a mark when it is a suggestion that has not been changed. The lane
/// places it, tells it which zoom it stands for, and handles the pointer; to screen readers the
/// block is an item of the lane's list.
/// </summary>
public sealed partial class StudioZoomBlock : UserControl
{
    // Below these widths the text, and then the marks, would not fit inside the block.
    private const double ScaleTextMinimumWidth = 30;
    private const double MarkMinimumWidth = 56;

    private readonly StudioZoomLane _lane;
    private string _description = string.Empty;
    private int _index = -1;
    private bool _isSelected;

    internal StudioZoomBlock(StudioZoomLane lane)
    {
        _lane = lane;
        InitializeComponent();

        // A state gone to before the block is in the window is not always shown.
        Loaded += (_, _) => ShowSelection();
    }

    /// <summary>The lane the block is on.</summary>
    internal StudioZoomLane Lane => _lane;

    /// <summary>The zoom's place in the project's list of zooms.</summary>
    internal int Index => _index;

    internal bool IsSelected => _isSelected;

    /// <summary>The zoom as a screen reader says it, such as "Zoom 2×, 12.0 to 16.5 seconds".</summary>
    internal string Description => _description;

    /// <summary>Shows a zoom in a block of the given width.</summary>
    internal void Update(int index, StudioZoom zoom, double width, bool isSelected)
    {
        if (index != _index)
        {
            _index = index;
            AutomationProperties.SetAutomationId(this, string.Create(CultureInfo.InvariantCulture, $"StudioZoom_{index}"));
        }

        ScaleText.Text = StudioEditorText.GetZoomScaleText(zoom.Scale);
        ScaleText.Visibility = width >= ScaleTextMinimumWidth ? Visibility.Visible : Visibility.Collapsed;
        var hasRoomForMarks = width >= MarkMinimumWidth;
        PointerIcon.Visibility = hasRoomForMarks && zoom.Focus.Mode == StudioZoomFocusMode.Cursor ? Visibility.Visible : Visibility.Collapsed;
        SuggestedIcon.Visibility = hasRoomForMarks && zoom.Origin == StudioZoomOrigin.Auto ? Visibility.Visible : Visibility.Collapsed;

        var description = StudioEditorText.GetZoomDescription(zoom);
        if (description != _description)
        {
            var old = _description;
            _description = description;
            if (AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged)
                && FrameworkElementAutomationPeer.FromElement(this) is { } peer)
            {
                peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, old, description);
            }
        }

        SetSelected(isSelected);
    }

    /// <summary>Shows the block as the selected zoom, or not, and tells screen readers when that changed.</summary>
    internal void SetSelected(bool isSelected)
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

    protected override AutomationPeer OnCreateAutomationPeer() => new StudioZoomBlockAutomationPeer(this);

    private void ShowSelection() =>
        VisualStateManager.GoToState(this, _isSelected ? "Selected" : "Unselected", false);
}

/// <summary>
/// Makes a <see cref="StudioZoomBlock"/> an item of a list to screen readers. It is named as the
/// editor words the zoom, says whether it is the selected one, can be selected, and can be pressed,
/// which selects it and moves the playhead to where the zoom has moved in.
/// </summary>
public sealed partial class StudioZoomBlockAutomationPeer(StudioZoomBlock owner)
    : FrameworkElementAutomationPeer(owner), ISelectionItemProvider, IInvokeProvider
{
    private StudioZoomBlock Block => (StudioZoomBlock)Owner;

    public bool IsSelected => Block.IsSelected;

    public IRawElementProviderSimple SelectionContainer =>
        ProviderFromPeer(CreatePeerForElement(Block.Lane));

    public void Select()
    {
        ThrowIfDisabled();
        Block.Lane.Select(Block.Index);
    }

    public void AddToSelection()
    {
        ThrowIfDisabled();
        if (!Block.IsSelected && Block.Lane.HasSelection)
        {
            throw new InvalidOperationException("Only one zoom can be selected at a time.");
        }

        Block.Lane.Select(Block.Index);
    }

    public void RemoveFromSelection()
    {
        ThrowIfDisabled();
        if (Block.IsSelected)
        {
            Block.Lane.Select(null);
        }
    }

    public void Invoke()
    {
        ThrowIfDisabled();
        Block.Lane.SelectAndShow(Block.Index);
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

    protected override string GetClassNameCore() => nameof(StudioZoomBlock);

    protected override string GetNameCore() => Block.Description;

    // The text and the marks inside the block are all in its name.
    protected override IList<AutomationPeer> GetChildrenCore() => [];

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface is PatternInterface.SelectionItem or PatternInterface.Invoke
            ? this
            : base.GetPatternCore(patternInterface);

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    private void ThrowIfDisabled()
    {
        if (!Block.IsEnabled)
        {
            throw new ElementNotEnabledException();
        }
    }
}
