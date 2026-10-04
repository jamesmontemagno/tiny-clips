using System.Globalization;
using System.Runtime.InteropServices;
using UIA = Interop.UIAutomationClient;

namespace TinyClips.Tools.StudioWindowCheck.Automation;

/// <summary>
/// The UI Automation client a screen reader uses, pointed at the tool's own windows. Patterns act
/// on a window that is not in front and send no input. Use it from a thread that is not the UI
/// thread: the window answers on its UI thread, which has to be free to do so.
/// </summary>
internal sealed class Uia
{
    private readonly UIA.IUIAutomation _automation = new UIA.CUIAutomation8();

    internal UIA.IUIAutomation Automation => _automation;

    /// <summary>The element of a top-level window. Everything in the window is found from it.</summary>
    public UiaElement FromWindow(nint window) => new(this, _automation.ElementFromHandle(window));
}

/// <summary>What a slider reports through the range pattern.</summary>
internal readonly record struct UiaRange(double Value, double Minimum, double Maximum, bool IsReadOnly, double SmallChange, double LargeChange);

/// <summary>One element of the tree. A member returns its default when the element is gone or lacks the pattern.</summary>
internal sealed class UiaElement
{
    private readonly Uia _uia;
    private readonly UIA.IUIAutomationElement _element;

    public UiaElement(Uia uia, UIA.IUIAutomationElement element)
    {
        _uia = uia;
        _element = element;
    }

    /// <summary>The element itself, for what this class does not wrap.</summary>
    internal UIA.IUIAutomationElement Raw => _element;

    public string Name => Read(() => _element.CurrentName) ?? string.Empty;

    public string Id => Read(() => _element.CurrentAutomationId) ?? string.Empty;

    public string ClassName => Read(() => _element.CurrentClassName) ?? string.Empty;

    public string HelpText => Read(() => _element.CurrentHelpText) ?? string.Empty;

    public string AcceleratorKey => Read(() => _element.CurrentAcceleratorKey) ?? string.Empty;

    public int ControlType => Read(() => _element.CurrentControlType);

    /// <summary>The control type as the constant's name without its prefix, such as "Button".</summary>
    public string ControlTypeName => ControlTypeNames.Of(ControlType);

    public bool IsEnabled => Read(() => _element.CurrentIsEnabled) != 0;

    public bool IsOffscreen => Read(() => _element.CurrentIsOffscreen) != 0;

    public bool IsKeyboardFocusable => Read(() => _element.CurrentIsKeyboardFocusable) != 0;

    public bool HasKeyboardFocus => Read(() => _element.CurrentHasKeyboardFocus) != 0;

    /// <summary>False once the element has left the tree.</summary>
    public bool IsAlive
    {
        get
        {
            try
            {
                _ = _element.CurrentControlType;
                return true;
            }
            catch (COMException)
            {
                return false;
            }
        }
    }

    /// <summary>The bounding rectangle in screen pixels.</summary>
    public (int X, int Y, int Width, int Height) Bounds
    {
        get
        {
            try
            {
                var rect = _element.CurrentBoundingRectangle;
                return (rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
            }
            catch (COMException)
            {
                return default;
            }
        }
    }

    /// <summary>The first descendant in the control view with this automation id, or null.</summary>
    public UiaElement? Find(string automationId)
    {
        try
        {
            var condition = _uia.Automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_AutomationIdPropertyId, automationId);
            var found = _element.FindFirst(UIA.TreeScope.TreeScope_Descendants, condition);
            return found is null ? null : new UiaElement(_uia, found);
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>Every descendant in the control view with this control type.</summary>
    public List<UiaElement> FindAll(int controlType)
    {
        var result = new List<UiaElement>();
        try
        {
            var condition = _uia.Automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_ControlTypePropertyId, controlType);
            var found = _element.FindAll(UIA.TreeScope.TreeScope_Descendants, condition);
            for (var index = 0; found is not null && index < found.Length; index++)
            {
                result.Add(new UiaElement(_uia, found.GetElement(index)));
            }
        }
        catch (COMException)
        {
            // The window is closing: what was found so far is all there is.
        }

        return result;
    }

    /// <summary>The children in the control view, which is what a screen reader walks; or in the raw view, which leaves nothing out.</summary>
    public List<UiaElement> Children(bool raw = false)
    {
        var result = new List<UiaElement>();
        try
        {
            var walker = raw ? _uia.Automation.RawViewWalker : _uia.Automation.ControlViewWalker;
            for (var child = walker.GetFirstChildElement(_element); child is not null; child = walker.GetNextSiblingElement(child))
            {
                result.Add(new UiaElement(_uia, child));
            }
        }
        catch (COMException)
        {
            // As above.
        }

        return result;
    }

    /// <summary>The first descendant in the raw view with this automation id, for elements kept out of the control view.</summary>
    public UiaElement? FindRaw(string automationId, int depth = 40)
    {
        foreach (var child in Children(raw: true))
        {
            if (child.Id == automationId)
            {
                return child;
            }

            if (depth > 0 && child.FindRaw(automationId, depth - 1) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // Patterns

    public bool Invoke() => Act<UIA.IUIAutomationInvokePattern>(UIA.UIA_PatternIds.UIA_InvokePatternId, static pattern => pattern.Invoke());

    public bool Toggle() => Act<UIA.IUIAutomationTogglePattern>(UIA.UIA_PatternIds.UIA_TogglePatternId, static pattern => pattern.Toggle());

    /// <summary>True for on, false for off, null when the element has no toggle pattern or is in between.</summary>
    public bool? IsToggledOn => Get<UIA.IUIAutomationTogglePattern, bool?>(UIA.UIA_PatternIds.UIA_TogglePatternId, static pattern => pattern.CurrentToggleState switch
    {
        UIA.ToggleState.ToggleState_On => true,
        UIA.ToggleState.ToggleState_Off => false,
        _ => null,
    });

    public bool Select() => Act<UIA.IUIAutomationSelectionItemPattern>(UIA.UIA_PatternIds.UIA_SelectionItemPatternId, static pattern => pattern.Select());

    public bool? IsSelected => Get<UIA.IUIAutomationSelectionItemPattern, bool?>(UIA.UIA_PatternIds.UIA_SelectionItemPatternId, static pattern => pattern.CurrentIsSelected != 0);

    /// <summary>Takes the element out of its container's selection.</summary>
    public bool RemoveFromSelection() => Act<UIA.IUIAutomationSelectionItemPattern>(UIA.UIA_PatternIds.UIA_SelectionItemPatternId, static pattern => pattern.RemoveFromSelection());

    public bool SetRange(double value) => Act<UIA.IUIAutomationRangeValuePattern>(UIA.UIA_PatternIds.UIA_RangeValuePatternId, pattern => pattern.SetValue(value));

    public UiaRange? Range => Get<UIA.IUIAutomationRangeValuePattern, UiaRange?>(UIA.UIA_PatternIds.UIA_RangeValuePatternId, static pattern =>
        new UiaRange(pattern.CurrentValue, pattern.CurrentMinimum, pattern.CurrentMaximum, pattern.CurrentIsReadOnly != 0, pattern.CurrentSmallChange, pattern.CurrentLargeChange));

    /// <summary>The value pattern's text, or null when the element has no value pattern.</summary>
    public string? ValueText => Get<UIA.IUIAutomationValuePattern, string?>(UIA.UIA_PatternIds.UIA_ValuePatternId, static pattern => pattern.CurrentValue ?? string.Empty);

    /// <summary>True for expanded, false for collapsed, null when the element cannot be expanded.</summary>
    public bool? IsExpanded => Get<UIA.IUIAutomationExpandCollapsePattern, bool?>(UIA.UIA_PatternIds.UIA_ExpandCollapsePatternId, static pattern => pattern.CurrentExpandCollapseState switch
    {
        UIA.ExpandCollapseState.ExpandCollapseState_Collapsed => false,
        UIA.ExpandCollapseState.ExpandCollapseState_LeafNode => null,
        _ => true,
    });

    /// <summary>The names of the selected items of a list or a combo box.</summary>
    public string[] SelectedNames => Get<UIA.IUIAutomationSelectionPattern, string[]>(UIA.UIA_PatternIds.UIA_SelectionPatternId, static pattern =>
    {
        var selection = pattern.GetCurrentSelection();
        var names = new string[selection?.Length ?? 0];
        for (var index = 0; index < names.Length; index++)
        {
            names[index] = selection!.GetElement(index).CurrentName ?? string.Empty;
        }

        return names;
    }) ?? [];

    /// <summary>Asks a top-level window to close, as its close button does.</summary>
    public bool CloseWindow() => Act<UIA.IUIAutomationWindowPattern>(UIA.UIA_PatternIds.UIA_WindowPatternId, static pattern => pattern.Close());

    /// <summary>The names of the patterns the element offers, of the ones the checks care about.</summary>
    public string Patterns
    {
        get
        {
            var names = new List<string>();
            Add(UIA.UIA_PatternIds.UIA_InvokePatternId, "Invoke");
            Add(UIA.UIA_PatternIds.UIA_TogglePatternId, "Toggle");
            Add(UIA.UIA_PatternIds.UIA_SelectionItemPatternId, "SelectionItem");
            Add(UIA.UIA_PatternIds.UIA_SelectionPatternId, "Selection");
            Add(UIA.UIA_PatternIds.UIA_RangeValuePatternId, "RangeValue");
            Add(UIA.UIA_PatternIds.UIA_ValuePatternId, "Value");
            Add(UIA.UIA_PatternIds.UIA_ExpandCollapsePatternId, "ExpandCollapse");
            Add(UIA.UIA_PatternIds.UIA_WindowPatternId, "Window");
            return string.Join(',', names);

            void Add(int id, string name)
            {
                try
                {
                    if (_element.GetCurrentPattern(id) is not null)
                    {
                        names.Add(name);
                    }
                }
                catch (COMException)
                {
                    // Gone.
                }
            }
        }
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{ControlTypeName} \"{Name}\" [{Id}]");

    private static T? Read<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (COMException)
        {
            return default;
        }
    }

    private bool Act<TPattern>(int patternId, Action<TPattern> act)
        where TPattern : class
    {
        try
        {
            if (_element.GetCurrentPattern(patternId) is not TPattern pattern)
            {
                return false;
            }

            act(pattern);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException or ArgumentException)
        {
            return false;
        }
    }

    private TResult? Get<TPattern, TResult>(int patternId, Func<TPattern, TResult> read)
        where TPattern : class
    {
        try
        {
            return _element.GetCurrentPattern(patternId) is TPattern pattern ? read(pattern) : default;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException or ArgumentException)
        {
            return default;
        }
    }
}

/// <summary>The control types the Studio window uses, by number, and what they are called.</summary>
internal static class ControlTypeNames
{
    public const int Button = UIA.UIA_ControlTypeIds.UIA_ButtonControlTypeId;
    public const int CheckBox = UIA.UIA_ControlTypeIds.UIA_CheckBoxControlTypeId;
    public const int ComboBox = UIA.UIA_ControlTypeIds.UIA_ComboBoxControlTypeId;
    public const int Image = UIA.UIA_ControlTypeIds.UIA_ImageControlTypeId;
    public const int ListItem = UIA.UIA_ControlTypeIds.UIA_ListItemControlTypeId;
    public const int List = UIA.UIA_ControlTypeIds.UIA_ListControlTypeId;
    public const int ProgressBar = UIA.UIA_ControlTypeIds.UIA_ProgressBarControlTypeId;
    public const int RadioButton = UIA.UIA_ControlTypeIds.UIA_RadioButtonControlTypeId;
    public const int Slider = UIA.UIA_ControlTypeIds.UIA_SliderControlTypeId;
    public const int Text = UIA.UIA_ControlTypeIds.UIA_TextControlTypeId;
    public const int Group = UIA.UIA_ControlTypeIds.UIA_GroupControlTypeId;
    public const int Window = UIA.UIA_ControlTypeIds.UIA_WindowControlTypeId;
    public const int Pane = UIA.UIA_ControlTypeIds.UIA_PaneControlTypeId;
    public const int TitleBar = UIA.UIA_ControlTypeIds.UIA_TitleBarControlTypeId;
    public const int Custom = UIA.UIA_ControlTypeIds.UIA_CustomControlTypeId;

    private static readonly Dictionary<int, string> Names = new()
    {
        [50000] = "Button", [50001] = "Calendar", [50002] = "CheckBox", [50003] = "ComboBox", [50004] = "Edit",
        [50005] = "Hyperlink", [50006] = "Image", [50007] = "ListItem", [50008] = "List", [50009] = "Menu",
        [50010] = "MenuBar", [50011] = "MenuItem", [50012] = "ProgressBar", [50013] = "RadioButton", [50014] = "ScrollBar",
        [50015] = "Slider", [50016] = "Spinner", [50017] = "StatusBar", [50018] = "Tab", [50019] = "TabItem",
        [50020] = "Text", [50021] = "ToolBar", [50022] = "ToolTip", [50023] = "Tree", [50024] = "TreeItem",
        [50025] = "Custom", [50026] = "Group", [50027] = "Thumb", [50028] = "DataGrid", [50029] = "DataItem",
        [50030] = "Document", [50031] = "SplitButton", [50032] = "Window", [50033] = "Pane", [50034] = "Header",
        [50035] = "HeaderItem", [50036] = "Table", [50037] = "TitleBar", [50038] = "Separator", [50039] = "SemanticZoom",
        [50040] = "AppBar",
    };

    public static string Of(int controlType) =>
        Names.TryGetValue(controlType, out var name) ? name : controlType.ToString(CultureInfo.InvariantCulture);
}
