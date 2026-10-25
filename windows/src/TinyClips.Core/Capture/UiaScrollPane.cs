using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace TinyClips.Core.Capture;

/// <summary>
/// Read-only UI Automation access to the scroll pane under a point: its vertical scroll
/// percentage predicts each step's shift and lets the original position be restored. Only
/// properties are read; invoking UIA scroll methods makes many apps come to the front.
/// Uses source-generated COM interop like <see cref="WgcInterop"/>. Create and use it on an
/// MTA thread.
/// </summary>
internal sealed partial class UiaScrollPane : IDisposable
{
    private static readonly Guid ClsidCUIAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    private static readonly Guid IidIUIAutomationScrollPattern = new("88F4D42A-E881-459D-A77C-73BBBB7E02DC");
    private const int UiaScrollPatternId = 10004;
    private const int MaxAncestors = 64;

    private IUIAutomationScrollPattern? _pattern;

    private UiaScrollPane(IUIAutomationScrollPattern pattern)
    {
        _pattern = pattern;
    }

    /// <summary>Vertical scroll percent (0-100), or -1 when unavailable.</summary>
    public double VerticalPercent
        => _pattern is { } pattern && pattern.GetCurrentVerticalScrollPercent(out var value) >= 0 ? value : -1;

    /// <summary>
    /// Walks up from the element at the point to the nearest vertically scrollable pane. Returns
    /// null when the element at the point belongs to another process (the target is covered
    /// there), so another app's scroll position is never trusted.
    /// </summary>
    public static unsafe UiaScrollPane? TryFind(int screenX, int screenY, nint window)
    {
        nint automationPointer = 0;
        try
        {
            var iid = typeof(IUIAutomation).GUID;
            if (AutoScrollNative.CoCreateInstance(in ClsidCUIAutomation, 0, AutoScrollNative.ClsCtxInprocServer, in iid, out automationPointer) < 0
                || automationPointer == 0)
            {
                return null;
            }

            // ConvertToManaged does not take ownership; the CoCreateInstance reference is released below.
            var automation = ComInterfaceMarshaller<IUIAutomation>.ConvertToManaged((void*)automationPointer)!;
            AutoScrollNative.GetWindowThreadProcessId(window, out var targetProcessId);
            if (automation.ElementFromPoint(new AutoScrollNative.POINT(screenX, screenY), out var element) < 0
                || element is null
                || element.GetCurrentProcessId(out var processId) < 0
                || (uint)processId != targetProcessId
                || automation.GetControlViewWalker(out var walker) < 0
                || walker is null)
            {
                return null;
            }

            for (var i = 0; i < MaxAncestors && element is not null; i++)
            {
                if (ScrollPatternOf(element) is { } pattern)
                {
                    return new UiaScrollPane(pattern);
                }

                element = walker.GetParentElement(element, out var parent) >= 0 ? parent : null;
            }

            return null;
        }
        catch (Exception)
        {
            // UI Automation is best-effort: without it the capture still works from pixels alone.
            return null;
        }
        finally
        {
            if (automationPointer != 0)
            {
                ComInterfaceMarshaller<IUIAutomation>.Free((void*)automationPointer);
            }
        }
    }

    public void Dispose()
    {
        if ((object?)_pattern is ComObject comObject)
        {
            comObject.FinalRelease();
        }

        _pattern = null;
    }

    /// <summary>The element's scroll pattern when it scrolls vertically, otherwise null.</summary>
    private static unsafe IUIAutomationScrollPattern? ScrollPatternOf(IUIAutomationElement element)
    {
        if (element.GetCurrentPatternAs(UiaScrollPatternId, in IidIUIAutomationScrollPattern, out var patternPointer) < 0
            || patternPointer == 0)
        {
            return null;
        }

        try
        {
            var pattern = ComInterfaceMarshaller<IUIAutomationScrollPattern>.ConvertToManaged((void*)patternPointer)!;
            return pattern.GetCurrentVerticallyScrollable(out var scrollable) >= 0 && scrollable != 0 ? pattern : null;
        }
        finally
        {
            ComInterfaceMarshaller<IUIAutomationScrollPattern>.Free((void*)patternPointer);
        }
    }

    // UIAutomationClient.h interfaces. Methods must be declared in vtable order, so members before
    // the ones used are declared too (with untyped pointers, since they are never called).

    [GeneratedComInterface]
    [System.Runtime.InteropServices.Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
    internal partial interface IUIAutomation
    {
        [PreserveSig]
        int CompareElements(nint element1, nint element2, out int areSame);

        [PreserveSig]
        int CompareRuntimeIds(nint runtimeId1, nint runtimeId2, out int areSame);

        [PreserveSig]
        int GetRootElement(out nint root);

        [PreserveSig]
        int ElementFromHandle(nint hwnd, out nint element);

        [PreserveSig]
        int ElementFromPoint(AutoScrollNative.POINT point, out IUIAutomationElement? element);

        [PreserveSig]
        int GetFocusedElement(out nint element);

        [PreserveSig]
        int GetRootElementBuildCache(nint cacheRequest, out nint root);

        [PreserveSig]
        int ElementFromHandleBuildCache(nint hwnd, nint cacheRequest, out nint element);

        [PreserveSig]
        int ElementFromPointBuildCache(AutoScrollNative.POINT point, nint cacheRequest, out nint element);

        [PreserveSig]
        int GetFocusedElementBuildCache(nint cacheRequest, out nint element);

        [PreserveSig]
        int CreateTreeWalker(nint condition, out nint walker);

        /// <summary>get_ControlViewWalker.</summary>
        [PreserveSig]
        int GetControlViewWalker(out IUIAutomationTreeWalker? walker);
    }

    [GeneratedComInterface]
    [System.Runtime.InteropServices.Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
    internal partial interface IUIAutomationElement
    {
        [PreserveSig]
        int SetFocus();

        [PreserveSig]
        int GetRuntimeId(out nint runtimeId);

        [PreserveSig]
        int FindFirst(int scope, nint condition, out nint found);

        [PreserveSig]
        int FindAll(int scope, nint condition, out nint found);

        [PreserveSig]
        int FindFirstBuildCache(int scope, nint condition, nint cacheRequest, out nint found);

        [PreserveSig]
        int FindAllBuildCache(int scope, nint condition, nint cacheRequest, out nint found);

        [PreserveSig]
        int BuildUpdatedCache(nint cacheRequest, out nint updatedElement);

        [PreserveSig]
        int GetCurrentPropertyValue(int propertyId, nint value);

        [PreserveSig]
        int GetCurrentPropertyValueEx(int propertyId, int ignoreDefaultValue, nint value);

        [PreserveSig]
        int GetCachedPropertyValue(int propertyId, nint value);

        [PreserveSig]
        int GetCachedPropertyValueEx(int propertyId, int ignoreDefaultValue, nint value);

        [PreserveSig]
        int GetCurrentPatternAs(int patternId, in Guid iid, out nint patternObject);

        [PreserveSig]
        int GetCachedPatternAs(int patternId, in Guid iid, out nint patternObject);

        [PreserveSig]
        int GetCurrentPattern(int patternId, out nint patternObject);

        [PreserveSig]
        int GetCachedPattern(int patternId, out nint patternObject);

        [PreserveSig]
        int GetCachedParent(out nint parent);

        [PreserveSig]
        int GetCachedChildren(out nint children);

        /// <summary>get_CurrentProcessId.</summary>
        [PreserveSig]
        int GetCurrentProcessId(out int processId);
    }

    [GeneratedComInterface]
    [System.Runtime.InteropServices.Guid("4042C624-389C-4AFC-A630-9DF854A541FC")]
    internal partial interface IUIAutomationTreeWalker
    {
        [PreserveSig]
        int GetParentElement(IUIAutomationElement element, out IUIAutomationElement? parent);
    }

    [GeneratedComInterface]
    [System.Runtime.InteropServices.Guid("88F4D42A-E881-459D-A77C-73BBBB7E02DC")]
    internal partial interface IUIAutomationScrollPattern
    {
        [PreserveSig]
        int Scroll(int horizontalAmount, int verticalAmount);

        [PreserveSig]
        int SetScrollPercent(double horizontalPercent, double verticalPercent);

        [PreserveSig]
        int GetCurrentHorizontalScrollPercent(out double value);

        [PreserveSig]
        int GetCurrentVerticalScrollPercent(out double value);

        [PreserveSig]
        int GetCurrentHorizontalViewSize(out double value);

        [PreserveSig]
        int GetCurrentVerticalViewSize(out double value);

        [PreserveSig]
        int GetCurrentHorizontallyScrollable(out int value);

        [PreserveSig]
        int GetCurrentVerticallyScrollable(out int value);
    }
}
