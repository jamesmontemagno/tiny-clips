using TinyClips.Core.Models;

namespace TinyClips.Core.Capture;

/// <summary>
/// Stores webcam corner changes on the pause-adjusted recording timeline.
/// </summary>
public sealed class WebcamPlacementTimeline
{
    private readonly object _gate = new();
    private readonly List<WebcamPlacementEvent> _events;

    public WebcamPlacementTimeline(WebcamCornerPosition initialCorner)
    {
        _events = [new(TimeSpan.Zero, initialCorner)];
    }

    public void Add(TimeSpan time, WebcamCornerPosition corner)
    {
        lock (_gate)
        {
            time = time < TimeSpan.Zero ? TimeSpan.Zero : time;
            var last = _events[^1];
            if (last.Corner == corner)
            {
                return;
            }

            time = time < last.Time ? last.Time : time;
            if (time == last.Time)
            {
                _events[^1] = new WebcamPlacementEvent(time, corner);
            }
            else
            {
                _events.Add(new WebcamPlacementEvent(time, corner));
            }
        }
    }

    public WebcamCornerPosition CornerAt(TimeSpan time)
    {
        lock (_gate)
        {
            for (var i = _events.Count - 1; i >= 0; i--)
            {
                if (_events[i].Time <= time)
                {
                    return _events[i].Corner;
                }
            }

            return _events[0].Corner;
        }
    }

    internal IReadOnlyList<WebcamPlacementEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }
}

internal readonly record struct WebcamPlacementEvent(TimeSpan Time, WebcamCornerPosition Corner);
