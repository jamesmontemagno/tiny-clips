using TinyClips.Core.Capture;
using TinyClips.Core.Models;

namespace TinyClips.Core.Studio;

/// <summary>A pointer position in virtual-desktop pixels at a recording timeline time.</summary>
internal readonly record struct StudioPointSample(TimeSpan Time, int X, int Y);

internal static class StudioRecordingBuilder
{
    public static bool TryNormalizePoint(int x, int y, int originX, int originY, int width, int height, out (double X, double Y) normalized)
    {
        normalized = default;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var relativeX = x - originX;
        var relativeY = y - originY;
        if (relativeX < 0 || relativeY < 0 || relativeX >= width || relativeY >= height)
        {
            return false;
        }

        normalized = (relativeX / (double)width, relativeY / (double)height);
        return true;
    }

    public static StudioCursorSample[] BuildCursorSamples(
        IEnumerable<StudioPointSample> samples,
        int originX,
        int originY,
        int width,
        int height,
        int maxSamplesPerSecond = 60)
    {
        if (maxSamplesPerSecond <= 0)
        {
            return [];
        }

        var minGap = TimeSpan.FromSeconds(1.0 / maxSamplesPerSecond);
        var ordered = samples.OrderBy(sample => sample.Time).ToArray();
        var result = new List<StudioCursorSample>(ordered.Length);
        TimeSpan? lastTime = null;
        (double X, double Y)? lastPoint = null;

        foreach (var sample in ordered)
        {
            if (sample.Time < TimeSpan.Zero)
            {
                continue;
            }

            if (lastTime is { } t && sample.Time - t < minGap)
            {
                continue;
            }

            if (!TryNormalizePoint(sample.X, sample.Y, originX, originY, width, height, out var point))
            {
                point = ((sample.X - originX) / (double)width, (sample.Y - originY) / (double)height);
            }

            if (lastPoint is { } previous && previous.X == point.X && previous.Y == point.Y)
            {
                continue;
            }

            result.Add(new StudioCursorSample { T = sample.Time.TotalSeconds, X = point.X, Y = point.Y });
            lastTime = sample.Time;
            lastPoint = point;
        }

        return result.ToArray();
    }

    public static StudioClickEvent[] BuildClickEvents(
        IEnumerable<MouseClickSample> clicks,
        RecordingTimeline timeline,
        int originX,
        int originY,
        int width,
        int height)
    {
        return clicks
            .Select(click => TryBuildClick(click, timeline, originX, originY, width, height, out var studioClick) ? studioClick : null)
            .Where(static click => click is not null)
            .Select(static click => click!)
            .OrderBy(static click => click.T)
            .ToArray();
    }

    public static StudioCameraCornerEvent[] BuildCameraCornerEvents(IReadOnlyList<WebcamPlacementEvent> events)
    {
        var result = new List<StudioCameraCornerEvent>(events.Count);
        StudioAnchor? last = null;
        foreach (var item in events.OrderBy(static e => e.Time))
        {
            var anchor = ToStudioAnchor(item.Corner);
            if (last == anchor)
            {
                continue;
            }

            result.Add(new StudioCameraCornerEvent { T = Math.Max(0, item.Time.TotalSeconds), Corner = anchor });
            last = anchor;
        }

        return result.Count == 0
            ? [new StudioCameraCornerEvent { T = 0, Corner = StudioAnchor.BottomRight }]
            : result.ToArray();
    }

    /// <summary>
    /// The shortest scene that a change made while recording gets (section 9.1 of the project
    /// format). It is the shortest scene the editor makes.
    /// </summary>
    public const double ShortestRecordedScene = StudioEditorModel.MinimumSceneDuration;

    /// <summary>
    /// The scenes a new project gets from what was changed while recording (section 9.1 of the
    /// project format): a scene for each move of the camera to another corner and for each change
    /// of layout, entered by moving, after <paramref name="first"/>, which starts at 0.
    /// </summary>
    /// <param name="first">The scene the recording starts with.</param>
    /// <param name="corners">The corners the camera was in, with the time it got there.</param>
    /// <param name="markers">The layouts that were chosen, with the time they were chosen.</param>
    /// <param name="duration">How long the recording is, in seconds.</param>
    public static StudioScene[] BuildScenes(
        StudioScene first,
        IEnumerable<StudioCameraCornerEvent>? corners,
        IEnumerable<StudioLayoutMarker>? markers,
        double duration)
    {
        var changes = new List<RecordedChange>();
        var index = 0;
        foreach (var corner in corners ?? [])
        {
            if (double.IsFinite(corner.T))
            {
                changes.Add(new RecordedChange(corner.T, IsCorner: true, index, corner.Corner, default));
            }

            index++;
        }

        index = 0;
        foreach (var marker in markers ?? [])
        {
            if (double.IsFinite(marker.T))
            {
                changes.Add(new RecordedChange(marker.T, IsCorner: false, index, default, marker.Layout));
            }

            index++;
        }

        // By time. At the same time a corner comes before a marker, and two of a kind keep the
        // order of their list.
        changes.Sort(static (a, b) =>
            a.Time != b.Time ? a.Time.CompareTo(b.Time)
            : a.IsCorner != b.IsCorner ? (a.IsCorner ? -1 : 1)
            : a.Index.CompareTo(b.Index));

        var scenes = new List<StudioScene> { first with { Start = 0 } };
        foreach (var change in changes)
        {
            var last = scenes[^1];
            var changed = last;
            if (!change.IsCorner)
            {
                changed = last with { Layout = change.Layout };
            }
            else if (last.Bubble.Anchor != change.Corner)
            {
                // The offsets are from the corner the bubble was in, so they do not go with it.
                changed = last with { Bubble = last.Bubble with { Anchor = change.Corner, OffsetX = 0, OffsetY = 0 } };
            }

            if (ShowsTheSame(changed, last))
            {
                continue;
            }

            if (change.IsCorner && last.Layout != StudioLayout.Bubble)
            {
                // No bubble is showing. The corner counts from when one shows again.
                scenes[^1] = changed;
                continue;
            }

            if (change.Time < last.Start + ShortestRecordedScene)
            {
                // Too soon after the last scene started for that one to last: it takes the change.
                scenes[^1] = changed;
                if (scenes.Count > 1 && ShowsTheSame(changed, scenes[^2]))
                {
                    scenes.RemoveAt(scenes.Count - 1);
                }

                continue;
            }

            if (change.Time > duration - ShortestRecordedScene)
            {
                continue;
            }

            scenes.Add(changed with
            {
                Start = change.Time,
                Transition = new StudioTransition { Kind = StudioTransitionKind.Morph },
            });
        }

        return [.. scenes];
    }

    // Whether two scenes have the same layout and the bubble in the same place. A recording
    // changes nothing else about a scene.
    private static bool ShowsTheSame(StudioScene a, StudioScene b) =>
        a.Layout == b.Layout
        && a.Bubble.Anchor == b.Bubble.Anchor
        && a.Bubble.OffsetX == b.Bubble.OffsetX
        && a.Bubble.OffsetY == b.Bubble.OffsetY;

    private readonly record struct RecordedChange(double Time, bool IsCorner, int Index, StudioAnchor Corner, StudioLayout Layout);

    public static StudioProjectCreationRequest BuildCreationRequest(
        string name,
        StudioRecordingSourceInfo screen,
        StudioCameraSourceInfo? camera,
        WebcamCornerPosition initialCorner,
        MouseClickOverlayStyle clickStyle,
        bool clickVisualsEnabled,
        bool branding,
        string appVersion,
        StudioLook? look = null,
        IReadOnlyList<StudioCameraCornerEvent>? cameraCorners = null)
    {
        return new StudioProjectCreationRequest(
            name,
            screen,
            camera,
            ToStudioAnchor(initialCorner),
            new StudioClickOverlay
            {
                Enabled = clickVisualsEnabled,
                Color = clickStyle.ColorHex,
                Size = clickStyle.Size,
                StrokeWidth = clickStyle.StrokeWidth,
                Opacity = clickStyle.Opacity,
                Duration = clickStyle.DurationSeconds,
            },
            branding,
            appVersion,
            look,
            cameraCorners);
    }

    public static StudioCaptureKind ToCaptureKind(CaptureTarget target, PixelRect? region) =>
        target.IsWindow ? StudioCaptureKind.Window : region is null ? StudioCaptureKind.Display : StudioCaptureKind.Region;

    public static StudioAnchor ToStudioAnchor(WebcamCornerPosition corner) => corner switch
    {
        WebcamCornerPosition.TopLeft => StudioAnchor.TopLeft,
        WebcamCornerPosition.TopRight => StudioAnchor.TopRight,
        WebcamCornerPosition.BottomLeft => StudioAnchor.BottomLeft,
        _ => StudioAnchor.BottomRight,
    };

    private static bool TryBuildClick(
        MouseClickSample click,
        RecordingTimeline timeline,
        int originX,
        int originY,
        int width,
        int height,
        out StudioClickEvent? studioClick)
    {
        studioClick = null;
        if (!timeline.TryNormalizeActive(click.SourceTimestamp, out var t) ||
            !TryNormalizePoint(click.ScreenX, click.ScreenY, originX, originY, width, height, out var point))
        {
            return false;
        }

        studioClick = new StudioClickEvent
        {
            T = t.TotalSeconds,
            X = point.X,
            Y = point.Y,
            Button = click.Button switch
            {
                MouseClickButton.Right => StudioMouseButton.Right,
                MouseClickButton.Middle => StudioMouseButton.Middle,
                MouseClickButton.Other => StudioMouseButton.Other,
                _ => StudioMouseButton.Left,
            },
        };
        return true;
    }
}
