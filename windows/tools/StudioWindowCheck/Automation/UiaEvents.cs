using System.Diagnostics;
using System.Runtime.InteropServices;
using UIA = Interop.UIAutomationClient;

namespace TinyClips.Tools.StudioWindowCheck.Automation;

/// <summary>One thing a window told its UI Automation clients.</summary>
/// <param name="Kind">"notification" for a sentence to be read out, "selected" for an element that became the selected one.</param>
/// <param name="Text">The sentence, or the selected element's name.</param>
/// <param name="Id">The notification's activity id, or the selected element's automation id.</param>
/// <param name="From">The element the event came from, as UI Automation numbers it.</param>
/// <param name="At">A Stopwatch timestamp of the event's arrival.</param>
/// <param name="Thread">The managed thread it arrived on.</param>
internal readonly record struct UiaEvent(string Kind, string Text, string Id, string From = "", long At = 0, int Thread = 0);

/// <summary>
/// Listens to one window the way a screen reader does: for the sentences the window asks to have
/// read out, and for which element of a list became the selected one. Make and dispose it on a
/// thread that is not the UI thread. The events arrive on a thread of UI Automation's own.
/// </summary>
internal sealed class UiaEvents : UIA.IUIAutomationNotificationEventHandler, UIA.IUIAutomationEventHandler, IDisposable
{
    private readonly object _gate = new();
    private readonly List<UiaEvent> _events = [];
    private readonly UIA.IUIAutomation _automation;
    private readonly UIA.IUIAutomationElement _window;
    private bool _hearsNotifications;
    private bool _hearsSelection;

    private UiaEvents(UIA.IUIAutomation automation, UIA.IUIAutomationElement window)
    {
        _automation = automation;
        _window = window;
    }

    /// <summary>Why nothing is heard, or null when both kinds of event are listened for.</summary>
    public string? Problem { get; private set; }

    public static UiaEvents Listen(Uia uia, UiaElement window)
    {
        var events = new UiaEvents(uia.Automation, window.Raw);
        try
        {
            // The name and the id come with the event, so the handler never has to ask the window for them.
            var cache = uia.Automation.CreateCacheRequest();
            cache.AddProperty(UIA.UIA_PropertyIds.UIA_NamePropertyId);
            cache.AddProperty(UIA.UIA_PropertyIds.UIA_AutomationIdPropertyId);
            uia.Automation.AddAutomationEventHandler(UIA.UIA_EventIds.UIA_SelectionItem_ElementSelectedEventId, window.Raw, UIA.TreeScope.TreeScope_Subtree, cache, events);
            events._hearsSelection = true;
            if (uia.Automation is UIA.IUIAutomation5 newer)
            {
                newer.AddNotificationEventHandler(window.Raw, UIA.TreeScope.TreeScope_Subtree, null, events);
                events._hearsNotifications = true;
            }
            else
            {
                events.Problem = "this version of UI Automation has no notification events";
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            events.Problem = ex.Message;
        }

        return events;
    }

    /// <summary>How many events have arrived. Events after this moment are asked for with the number.</summary>
    public int Mark()
    {
        lock (_gate)
        {
            return _events.Count;
        }
    }

    /// <summary>The events of one kind that arrived after a mark.</summary>
    public UiaEvent[] Since(int mark, string kind)
    {
        lock (_gate)
        {
            return [.. _events.Skip(mark).Where(e => e.Kind == kind)];
        }
    }

    /// <summary>Waits for an event after a mark, and returns every event of its kind that arrived after the mark.</summary>
    public UiaEvent[] WaitFor(int mark, string kind, Func<UiaEvent, bool> wanted, double seconds = 2)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var arrived = Since(mark, kind);
            if (arrived.Any(wanted) || watch.Elapsed.TotalSeconds >= seconds)
            {
                return arrived;
            }

            Thread.Sleep(20);
        }
    }

    public void HandleNotificationEvent(UIA.IUIAutomationElement sender, UIA.NotificationKind notificationKind, UIA.NotificationProcessing notificationProcessing, string displayString, string activityId)
    {
        var from = RuntimeId(sender);
        lock (_gate)
        {
            _events.Add(new UiaEvent("notification", displayString ?? string.Empty, activityId ?? string.Empty, from, Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId));
        }
    }

    private static string RuntimeId(UIA.IUIAutomationElement? element)
    {
        try
        {
            return element?.GetRuntimeId() is int[] id ? string.Join(".", id) : string.Empty;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return string.Empty;
        }
    }

    public void HandleAutomationEvent(UIA.IUIAutomationElement sender, int eventId)
    {
        if (eventId != UIA.UIA_EventIds.UIA_SelectionItem_ElementSelectedEventId)
        {
            return;
        }

        string name;
        string id;
        try
        {
            name = sender.CachedName ?? string.Empty;
            id = sender.CachedAutomationId ?? string.Empty;
        }
        catch (COMException)
        {
            return;
        }

        var from = RuntimeId(sender);
        lock (_gate)
        {
            _events.Add(new UiaEvent("selected", name, id, from, Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId));
        }
    }

    public void Dispose()
    {
        try
        {
            if (_hearsSelection)
            {
                _automation.RemoveAutomationEventHandler(UIA.UIA_EventIds.UIA_SelectionItem_ElementSelectedEventId, _window, this);
            }

            if (_hearsNotifications && _automation is UIA.IUIAutomation5 newer)
            {
                newer.RemoveNotificationEventHandler(_window, this);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // The window is gone, and its handlers with it.
        }
    }
}
