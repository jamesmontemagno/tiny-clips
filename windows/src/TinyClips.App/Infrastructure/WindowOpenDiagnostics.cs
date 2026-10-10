using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace TinyClips.App;

internal sealed class WindowOpenDiagnostics
{
    private static readonly ConditionalWeakTable<Window, WindowOpenTrace> Traces = new();
    private readonly Window _window;
    private readonly FrameworkElement _root;
    private readonly WindowOpenTrace _trace;
    private bool _closed;

    private WindowOpenDiagnostics(Window window, FrameworkElement root, WindowOpenTrace trace)
    {
        _window = window;
        _root = root;
        _trace = trace;
        Traces.Add(window, trace);
        root.Loaded += OnLoaded;
        window.Activated += OnActivated;
        window.Closed += OnClosed;
    }

    public static void Observe(Window window, FrameworkElement root, WindowOpenTrace? trace)
    {
        if (trace is not null)
        {
            _ = new WindowOpenDiagnostics(window, root, trace);
        }
    }

    public static IDisposable? Measure(Window window, WindowOpenPhase phase) =>
        Traces.TryGetValue(window, out var trace) ? trace.Measure(phase) : null;

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _root.Loaded -= OnLoaded;
        _trace.Mark(WindowOpenMilestone.RootLoaded);
        QueueTurn(WindowOpenMilestone.LoadedDispatcherTurn);
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        _window.Activated -= OnActivated;
        _trace.Mark(WindowOpenMilestone.FirstActivated);
        QueueTurn(WindowOpenMilestone.ActivatedDispatcherTurn);
    }

    private void QueueTurn(WindowOpenMilestone milestone)
    {
        // A low-priority queue turn is a responsiveness proxy, not input/presentation completion.
        if (!_window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!_closed)
            {
                _trace.Mark(milestone);
            }
        }))
        {
            _trace.Mark(WindowOpenMilestone.DispatcherUnavailable);
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _root.Loaded -= OnLoaded;
        _window.Activated -= OnActivated;
        _window.Closed -= OnClosed;
        Traces.Remove(_window);
        _trace.Mark(WindowOpenMilestone.Closed);
    }
}
