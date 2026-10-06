using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using TinyClips.App;

namespace TinyClips.Core.Tests;

[CollectionDefinition("Window open tracing", DisableParallelization = true)]
public sealed class WindowOpenTraceCollection;

[Collection("Window open tracing")]
public sealed class WindowOpenTraceTests
{
    [Fact]
    public void DisabledProvider_DoesNotCreateTrace()
    {
        Assert.Null(WindowOpenTrace.Start(WindowOpenKind.Settings));
    }

    [Fact]
    public void Phases_RecordInclusiveMonotonicTimeAndFixedNumericSchema()
    {
        using var listener = new TimingListener();
        var time = new TimestampProvider();
        var trace = Assert.IsType<WindowOpenTrace>(WindowOpenTrace.Start(WindowOpenKind.Settings, time));
        time.Advance(TimeSpan.FromMilliseconds(4));
        trace.Mark(WindowOpenMilestone.ConstructorEntered);
        var phase = trace.Measure(WindowOpenPhase.Xaml);
        time.Advance(TimeSpan.FromMilliseconds(7));
        phase.Dispose();
        phase.Dispose();
        trace.Mark(WindowOpenMilestone.RootLoaded);

        var events = listener.Events.ToArray();
        Assert.Equal(5, events.Length);
        Assert.All(events, e =>
        {
            Assert.Equal(1, e.EventId);
            Assert.Equal(6, e.Payload!.Count);
            Assert.All(e.Payload, value => Assert.True(value is long or int or double));
        });
        var stop = Assert.Single(events, e => (int)e.Payload![2]! == 2);
        Assert.Equal((int)WindowOpenKind.Settings, stop.Payload![1]);
        Assert.Equal((int)WindowOpenPhase.Xaml, stop.Payload[3]);
        Assert.Equal(11d, stop.Payload[4]);
        Assert.Equal(7d, stop.Payload[5]);
    }

    [Fact]
    public void OverlappingWindows_HaveIndependentIdsAndClocks()
    {
        using var listener = new TimingListener();
        var time = new TimestampProvider();
        var first = Assert.IsType<WindowOpenTrace>(WindowOpenTrace.Start(WindowOpenKind.Library, time));
        time.Advance(TimeSpan.FromMilliseconds(3));
        var second = Assert.IsType<WindowOpenTrace>(WindowOpenTrace.Start(WindowOpenKind.ScreenshotEditor, time));
        time.Advance(TimeSpan.FromMilliseconds(5));
        first.Mark(WindowOpenMilestone.ConstructorCompleted);
        second.Mark(WindowOpenMilestone.ConstructorCompleted);

        var completed = listener.Events
            .Where(e => (int)e.Payload![3]! == (int)WindowOpenMilestone.ConstructorCompleted).ToArray();
        Assert.Equal(2, completed.Length);
        Assert.NotEqual(completed[0].Payload![0], completed[1].Payload![0]);
        Assert.Equal(8d, completed[0].Payload![4]);
        Assert.Equal(5d, completed[1].Payload![4]);
    }

    [Fact]
    public void Close_SuppressesLateAsyncAndQueuedMilestones()
    {
        using var listener = new TimingListener();
        var trace = Assert.IsType<WindowOpenTrace>(WindowOpenTrace.Start(WindowOpenKind.ScreenshotEditor));
        var phase = trace.Measure(WindowOpenPhase.ContentLoad);
        trace.Mark(WindowOpenMilestone.Closed);
        trace.Mark(WindowOpenMilestone.ContentReady);
        trace.Mark(WindowOpenMilestone.LoadedDispatcherTurn);
        phase.Dispose();

        Assert.Equal(3, listener.Events.Count);
        Assert.Equal((int)WindowOpenMilestone.Closed, listener.Events.Last().Payload![3]);
    }

    private sealed class TimestampProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }

    private sealed class TimingListener : EventListener
    {
        public ConcurrentQueue<EventWrittenEventArgs> Events { get; } = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "TinyClips-WindowOpen")
            {
                EnableEvents(eventSource, EventLevel.Informational);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData) => Events.Enqueue(eventData);
    }
}
