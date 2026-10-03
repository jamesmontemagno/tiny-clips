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

    public static StudioProjectCreationRequest BuildCreationRequest(
        string name,
        StudioRecordingSourceInfo screen,
        StudioCameraSourceInfo? camera,
        WebcamCornerPosition initialCorner,
        MouseClickOverlayStyle clickStyle,
        bool clickVisualsEnabled,
        bool branding,
        string appVersion)
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
            appVersion);
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
