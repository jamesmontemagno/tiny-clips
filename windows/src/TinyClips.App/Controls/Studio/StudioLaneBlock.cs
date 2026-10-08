using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace TinyClips.App.Controls.Studio;

/// <summary>
/// One block on a lane of the Studio timeline: a scene, a zoom, a cut or a speed change. The
/// lane places it, tells it what it stands for, and handles the pointer; to screen readers the
/// block is an item of the lane's list. What a block looks like is in
/// <see cref="StudioSceneBlock"/>, <see cref="StudioZoomBlock"/>, <see cref="StudioCutBlock"/>
/// and <see cref="StudioSpeedBlock"/>, each of which has a visual state called Selected and one
/// called Unselected.
/// </summary>
public abstract partial class StudioLaneBlock : UserControl
{
    private readonly StudioLane _lane;
    private readonly string _automationIdPrefix;
    private string _description = string.Empty;
    private int _index = -1;
    private bool _isSelected;

    /// <param name="automationIdPrefix">What the block's automation id starts with: its place in the list follows.</param>
    protected StudioLaneBlock(StudioLane lane, string automationIdPrefix)
    {
        _lane = lane;
        _automationIdPrefix = automationIdPrefix;

        // A state gone to before the block is in the window is not always shown.
        Loaded += (_, _) => ShowSelection();
    }

    /// <summary>The lane the block is on.</summary>
    internal StudioLane Lane => _lane;

    /// <summary>The place in the lane's list of what the block stands for.</summary>
    internal int Index => _index;

    internal bool IsSelected => _isSelected;

    /// <summary>What the block stands for as a screen reader says it, such as "Zoom 2×, 12.0 to 16.5 seconds".</summary>
    internal string Description => _description;

    /// <summary>Shows the block as the marked one, or not, and tells screen readers when that changed.</summary>
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

    /// <summary>
    /// Says which item the block stands for, how a screen reader names it, and whether it is the
    /// marked one. Screen readers are told what changed.
    /// </summary>
    protected void Show(int index, string description, bool isSelected)
    {
        if (index != _index)
        {
            _index = index;
            AutomationProperties.SetAutomationId(this, string.Create(CultureInfo.InvariantCulture, $"{_automationIdPrefix}{index}"));
        }

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

    protected override AutomationPeer OnCreateAutomationPeer() => new StudioLaneBlockAutomationPeer(this);

    /// <summary>Called when the block has been shown as the marked one, or as not marked.</summary>
    private protected virtual void OnSelectionShown()
    {
    }

    private void ShowSelection()
    {
        VisualStateManager.GoToState(this, _isSelected ? "Selected" : "Unselected", false);
        OnSelectionShown();
    }
}

/// <summary>
/// Makes a <see cref="StudioLaneBlock"/> an item of a list to screen readers. It is named as the
/// editor words what it stands for, says whether it is the marked one, can be selected, and can
/// be pressed, which selects it and moves the playhead to where it shows.
/// </summary>
public sealed partial class StudioLaneBlockAutomationPeer(StudioLaneBlock owner)
    : FrameworkElementAutomationPeer(owner), ISelectionItemProvider, IInvokeProvider
{
    private StudioLaneBlock Block => (StudioLaneBlock)Owner;

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
            throw new InvalidOperationException("Only one item of this list can be selected at a time.");
        }

        Block.Lane.Select(Block.Index);
    }

    public void RemoveFromSelection()
    {
        ThrowIfDisabled();
        if (!Block.IsSelected)
        {
            return;
        }

        if (Block.Lane.IsSelectionRequired)
        {
            throw new InvalidOperationException("One item of this list is always selected.");
        }

        Block.Lane.Select(null);
    }

    public void Invoke()
    {
        ThrowIfDisabled();
        Block.Lane.SelectAndShow(Block.Index);
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

    // The block's own class: StudioSceneBlock, StudioZoomBlock, StudioCutBlock or StudioSpeedBlock.
    protected override string GetClassNameCore() => Owner.GetType().Name;

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
